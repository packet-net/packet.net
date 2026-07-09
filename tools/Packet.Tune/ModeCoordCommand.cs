using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>mode-coord</c>: renegotiate the TNC mode (and optionally the radio channel) of a
/// working RF link over the radios' own SDM side channel — which is mode/channel-agnostic,
/// so the coordination keeps working while the link under it is being changed. The
/// coordinator proposes each switch, both ends commit, the new link is verified with
/// probe frames both ways, and any failure reverts both ends to the session's home
/// mode/channel (responder watchdog as the backstop). Ends with a Markdown result table
/// on the coordinator.
/// </summary>
internal static class ModeCoordCommand
{
    public static async Task<int> Run(string[] args)
    {
        var parsed = Parse(args);
        if (parsed is null)
        {
            Console.WriteLine("mode-coord needs --role coordinator|responder --tnc <port> --radio <ccdi> --peer <8charId>");
            Console.WriteLine("  coordinator also needs a plan: --sequence m[@ch][,m[@ch]…]  or  --sweep");
            Console.WriteLine("  options: --home-mode N=6 --home-channel N=0 --probes N=5 --callsign X");
            Console.WriteLine("           --strict-bandwidth (--sweep only: skip 25 kHz modes on a narrow channel)");
            Console.WriteLine("           --channel-width narrow|wide=narrow (what --strict-bandwidth judges against)");
            return 2;
        }
        var p = parsed;

        Console.WriteLine($"mode-coord — role {p.Role}, TNC {p.Tnc}, radio {p.Radio}, peer {p.Peer}");
        Console.WriteLine($"  home: mode {p.Options.HomeMode} / channel {p.Options.HomeChannel}; probes/direction: {p.Options.ProbeFrames}");

        var source = Callsign.Parse(p.CallsignText);
        await using var tnc = NinoTncSerialPort.Open(p.Tnc);
        using var radio = TaitCcdiRadio.Open(p.Radio);
        await radio.SetProgressMessagesAsync(true); // DCD, SDM arrivals + delivery receipts
        Console.WriteLine($"  TNC firmware {await tnc.GetVersionAsync()}");
        await tnc.SetBeaconIntervalAsync(0); // a 60 s status beacon keying mid-probe pollutes everything

        var station = new NinoTncModeCoordStation(tnc, radio, source, p.Options.HomeMode)
        {
            Log = line => Console.WriteLine("  " + line),
        };

        // Start from a known place: pin the home mode and verify the home channel.
        Console.WriteLine($"  pinning home mode {p.Options.HomeMode} + verifying channel {p.Options.HomeChannel}...");
        await station.ApplyModeAsync(p.Options.HomeMode);
        await station.ApplyChannelAsync(p.Options.HomeChannel);

        await using var link = SdmTuningLink.Create(radio, p.Peer);
        if (p.Verbose)
        {
            link.Log = line => Console.WriteLine("  " + line);
        }

        using var cancelOnCtrlC = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancelOnCtrlC.Cancel();
        };

        return p.Role == "coordinator"
            ? await RunCoordinator(link, station, p, cancelOnCtrlC.Token)
            : await RunResponder(link, station, p, cancelOnCtrlC.Token);
    }

    private static async Task<int> RunCoordinator(
        SdmTuningLink link, NinoTncModeCoordStation station, Args p, CancellationToken cancellationToken)
    {
        await using var coordinator = new ModeCoordinator(link, station, p.Options)
        {
            Log = line => Console.WriteLine("  " + line),
        };

        Console.WriteLine();
        Console.WriteLine("  hello — checking the responder is alive over the side channel...");
        if (!await coordinator.HelloAsync(cancellationToken))
        {
            Console.WriteLine("  no responder answered — is the peer process running with the right --peer id?");
            return 1;
        }
        Console.WriteLine("  responder present");

        var attempts = new List<ModeCoordAttempt>();
        try
        {
            foreach (var (mode, channel) in p.Plan)
            {
                Console.WriteLine();
                string name = NinoTncCatalog.TryGetByMode(mode)?.Name ?? $"mode {mode}";
                Console.WriteLine($"  ── coordinate: mode {mode} ({name})" +
                                  (channel is { } ch ? $" @ channel {ch}" : string.Empty) + " ──");
                var attempt = await coordinator.CoordinateWithRetryAsync(mode, channel, cancellationToken);
                attempts.Add(attempt);
                Console.WriteLine($"  → {ModeCoordReport.DescribeOutcome(attempt)}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("  cancelled — returning the rig to home");
        }
        finally
        {
            // Whatever happened, finish at home — coordinated when the link is
            // alive, locally if not.
            try
            {
                if (coordinator.CurrentMode != p.Options.HomeMode || coordinator.CurrentChannel != p.Options.HomeChannel)
                {
                    Console.WriteLine();
                    Console.WriteLine("  ── coordinated return to home ──");
                    using var homeCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var home = await coordinator.CoordinateAsync(
                        p.Options.HomeMode, p.Options.HomeChannel, homeCts.Token);
                    attempts.Add(home);
                    Console.WriteLine($"  → {ModeCoordReport.DescribeOutcome(home)}");
                    if (!home.Success && !home.Reverted)
                    {
                        Console.WriteLine("  home coordination failed — restoring this end locally");
                        await station.ApplyModeAsync(p.Options.HomeMode, homeCts.Token);
                        await station.ApplyChannelAsync(p.Options.HomeChannel, homeCts.Token);
                    }
                }
                await coordinator.EndAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ModeCoordException or TuningLinkException)
            {
                Console.WriteLine($"  RESTORE WARNING: {ex.Message} — check the rig manually");
            }
        }

        Console.WriteLine();
        Console.WriteLine(ModeCoordReport.RenderMarkdown(attempts));
        return attempts.All(a => a.Success || a.HomeLinkAlive != false) ? 0 : 1;
    }

    private static async Task<int> RunResponder(
        SdmTuningLink link, NinoTncModeCoordStation station, Args p, CancellationToken cancellationToken)
    {
        var responder = new ModeResponder(link, station, p.Options)
        {
            Log = line => Console.WriteLine("  " + line),
        };
        Console.WriteLine();
        return await responder.RunAsync(cancellationToken);
    }

    private sealed record Args(
        string Role, string Tnc, string Radio, string Peer, string CallsignText, bool Verbose,
        IReadOnlyList<(byte Mode, int? Channel)> Plan, ModeCoordOptions Options);

    private static Args? Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        bool verbose = false, sweep = false, strictBandwidth = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verbose":
                    verbose = true;
                    break;
                case "--sweep":
                    sweep = true;
                    break;
                case "--strict-bandwidth":
                    strictBandwidth = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        flags[args[i]] = args[++i];
                    }
                    break;
            }
        }

        string? role = flags.GetValueOrDefault("--role");
        string? tnc = flags.GetValueOrDefault("--tnc");
        string? radioPort = flags.GetValueOrDefault("--radio");
        string? peer = flags.GetValueOrDefault("--peer");
        if (role is not ("coordinator" or "responder") || tnc is null || radioPort is null || peer is null)
        {
            return null;
        }

        byte homeMode = ParseByte(flags.GetValueOrDefault("--home-mode")) ?? 6;
        int homeChannel = ParseInt(flags.GetValueOrDefault("--home-channel")) ?? 0;
        int probes = ParseInt(flags.GetValueOrDefault("--probes")) is int pr and >= 1 and <= 20 ? pr : 5;
        var options = new ModeCoordOptions { HomeMode = homeMode, HomeChannel = homeChannel, ProbeFrames = probes };

        var plan = new List<(byte Mode, int? Channel)>();
        if (role == "coordinator")
        {
            string? sequence = flags.GetValueOrDefault("--sequence");
            if (sweep == (sequence is not null))
            {
                return null; // exactly one of --sweep / --sequence
            }
            if (sweep)
            {
                bool wide = flags.GetValueOrDefault("--channel-width") == "wide";
                foreach (var mode in ModeCoordReport.SelectSweepModes(strictBandwidth, wide))
                {
                    plan.Add((mode.Mode, null));
                }
            }
            else
            {
                foreach (string entry in sequence!.Split(','))
                {
                    string[] halves = entry.Split('@');
                    if (halves.Length is < 1 or > 2 || ParseByte(halves[0]) is not { } mode)
                    {
                        return null;
                    }
                    int? channel = null;
                    if (halves.Length == 2)
                    {
                        if (ParseInt(halves[1]) is not { } ch)
                        {
                            return null;
                        }
                        channel = ch;
                    }
                    plan.Add((mode, channel));
                }
            }
        }

        return new Args(role, tnc, radioPort, peer,
            flags.GetValueOrDefault("--callsign") ?? "N0CALL", verbose, plan, options);
    }

    private static byte? ParseByte(string? text) =>
        byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out byte value) ? value : null;

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
}
