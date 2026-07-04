using System.Globalization;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>hail</c>: query — or answer queries for — a peer station's modulation/modem + capabilities
/// over the radios' SDM side channel. Because the side channel rides the radio's own FFSK modem,
/// a hail reaches (and reports the status of) a station you <b>cannot</b> talk to on the packet
/// path <em>because</em> of a mode mismatch — the reply reveals the mismatch. A pure
/// network-diagnostic that nothing else in the stack offers.
/// <list type="bullet">
///   <item><c>hail --tnc &lt;port&gt; --radio &lt;ccdi&gt; --peer &lt;8charId&gt;</c> — send a hail and
///     print the peer's returned status (mode/bitrate/channel/capabilities).</item>
///   <item><c>hail --respond --tnc &lt;port&gt; --radio &lt;ccdi&gt; --peer &lt;8charId&gt;</c> — arm this
///     station as a hail responder (opt-in): auto-reply to hails from the peer with this
///     station's live status until Ctrl-C.</item>
/// </list>
/// </summary>
internal static class HailCommand
{
    public static async Task<int> Run(string[] args)
    {
        var p = Parse(args);
        if (p is null)
        {
            Console.WriteLine("hail needs --radio <ccdi> --peer <8charId> and, to send, --tnc <port>");
            Console.WriteLine("  send:    hail --tnc <port> --radio <ccdi> --peer <8charId> [--callsign X] [--verbose]");
            Console.WriteLine("  respond: hail --respond --tnc <port> --radio <ccdi> --peer <8charId> [--callsign X] [--verbose]");
            return 2;
        }

        Console.WriteLine($"hail — {(p.Respond ? "responder" : "hailer")}, radio {p.Radio}, peer {p.Peer}" +
                          (p.Tnc is null ? string.Empty : $", TNC {p.Tnc}"));

        using var radio = TaitCcdiRadio.Open(p.Radio);
        await radio.SetProgressMessagesAsync(true); // DCD, SDM arrivals + delivery receipts

        NinoTncSerialPort? tnc = null;
        if (p.Tnc is not null)
        {
            tnc = NinoTncSerialPort.Open(p.Tnc);
            Console.WriteLine($"  TNC firmware {await tnc.GetVersionAsync()}");
            await tnc.SetBeaconIntervalAsync(0); // a status beacon keying mid-exchange pollutes the channel
        }

        try
        {
            using var cancelOnCtrlC = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cancelOnCtrlC.Cancel();
            };

            // The richer STAT reply needs the 128-character extended SDM budget.
            await using var link = SdmTuningLink.Create(radio, p.Peer, extendedSdm: true);
            if (p.Verbose)
            {
                link.Log = line => Console.WriteLine("  " + line);
            }

            return p.Respond
                ? await RunResponder(link, tnc, radio, p, cancelOnCtrlC.Token)
                : await RunHailer(link, p, cancelOnCtrlC.Token);
        }
        finally
        {
            if (tnc is not null)
            {
                await tnc.DisposeAsync();
            }
        }
    }

    private static async Task<int> RunHailer(SdmTuningLink link, Args p, CancellationToken cancellationToken)
    {
        await using var hailer = new StationHailer(link, p.CallsignText)
        {
            Log = line => Console.WriteLine("  " + line),
        };

        Console.WriteLine();
        Console.WriteLine($"  hailing {p.Peer} over the SDM side channel...");
        StationHailResult result;
        try
        {
            result = await hailer.HailAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("  cancelled");
            return 1;
        }

        Console.WriteLine();
        if (!result.Success || result.Status is null)
        {
            Console.WriteLine($"  NO STATUS — {result.Outcome} ({result.Detail})");
            return 1;
        }

        PrintStatus(result.Status);
        return 0;
    }

    private static async Task<int> RunResponder(
        SdmTuningLink link, NinoTncSerialPort? tnc, TaitCcdiRadio radio, Args p, CancellationToken cancellationToken)
    {
        var provider = new NinoTncStationStatusSource(tnc, radio, p.CallsignText)
        {
            Log = line => Console.WriteLine("  " + line),
        };
        var responder = new StationHailResponder(link, provider)
        {
            Log = line => Console.WriteLine("  " + line),
        };

        Console.WriteLine();
        Console.WriteLine("  armed as a hail responder — Ctrl-C to stop");
        await responder.RunAsync(cancellationToken);
        return 0;
    }

    private static void PrintStatus(StationStatus status)
    {
        Console.WriteLine($"  ── station status: {status.Callsign} ──");
        Console.WriteLine($"    mode      : {(status.Mode is { } m ? $"{m} ({status.ModeName})" : "unknown")}");
        Console.WriteLine($"    bitrate   : {(status.BitRateHz is { } b ? string.Create(CultureInfo.InvariantCulture, $"{b} bit/s") : "unknown")}");
        Console.WriteLine($"    channel   : {status.Channel ?? "unknown"}");
        if (status.SupportedModes.Count > 0)
        {
            Console.WriteLine($"    modes     : {string.Join(", ", status.SupportedModes)}");
        }
        if (status.Capabilities.Count > 0)
        {
            Console.WriteLine($"    caps      : {string.Join(", ", status.Capabilities)}");
        }
        if (status.RssiOfHailDbm is { } rssi)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    rssi(hail): {rssi:0.0} dBm (as heard at the peer)"));
        }
    }

    private sealed record Args(bool Respond, string? Tnc, string Radio, string Peer, string CallsignText, bool Verbose);

    private static Args? Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        bool respond = false, verbose = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--respond":
                    respond = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                    {
                        flags[args[i]] = args[++i];
                    }
                    break;
            }
        }

        string? radio = flags.GetValueOrDefault("--radio");
        string? peer = flags.GetValueOrDefault("--peer");
        string? tnc = flags.GetValueOrDefault("--tnc");
        if (radio is null || peer is null || peer.Length != TaitSdmSideChannel.IdentityLength)
        {
            return null;
        }
        // The responder must be able to read its own mode → a TNC is required to answer.
        if (respond && tnc is null)
        {
            return null;
        }

        return new Args(respond, tnc, radio, peer, flags.GetValueOrDefault("--callsign") ?? "N0CALL", verbose);
    }
}
