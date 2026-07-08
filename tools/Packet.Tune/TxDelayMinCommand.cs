using System.Globalization;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Tune.Core;

namespace Packet.Tune;

/// <summary>
/// <c>txdelay-min</c>: find — and optionally apply — the minimum reliable TXDELAY of a
/// working RF link, coordinated over the radios' own SDM side channel (which does not
/// depend on the TXDELAY under test). The coordinator steps its own KISS TXDELAY DOWN
/// from the configured value, keying K SEPARATE probe transmissions per step (never one
/// multi-frame train — that shares a single preamble); the meter counts decodes per step
/// and, with DCD available, measures each probe's carrier-rise→data lead — the as-heard
/// effective TXDELAY that makes the sweep table self-evidencing. Ends with a Markdown
/// sweep table + recommendation (knee + margin) on the coordinator. The sweep always
/// restores the original TXDELAY + polite channel access; <c>--apply</c> is the separate
/// explicit verify-and-keep step. See docs/research/txdelay-optimisation.md.
/// </summary>
internal static class TxDelayMinCommand
{
    public static async Task<int> Run(string[] args)
    {
        var parsed = Parse(args);
        if (parsed is null)
        {
            Console.WriteLine("txdelay-min needs --role coordinator|meter --tnc <port> --radio <ccdi> --peer <8charId>");
            Console.WriteLine("  options: --start ms=500 --step ms=40 --min ms=20 --probes N=5 --callsign X --verbose");
            Console.WriteLine("           --apply           (after the sweep: verify + keep the recommendation)");
            Console.WriteLine("           --apply-at ms     (verify + keep an explicit value instead)");
            return 2;
        }
        var p = parsed;

        Console.WriteLine($"txdelay-min — role {p.Role}, TNC {p.Tnc}, radio {p.Radio}, peer {p.Peer}");
        if (p.Role == "coordinator")
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  sweep: {p.Options.StartTxDelayMs} ms down in {p.Options.StepMs} ms steps " +
                $"(floor {p.Options.MinTxDelayMs} ms), {p.Options.ProbesPerStep} separate keyings/step"));
        }

        var source = Callsign.Parse(p.CallsignText);
        await using var tnc = NinoTncSerialPort.Open(p.Tnc);
        using var radio = TaitCcdiRadio.Open(p.Radio);
        await radio.SetProgressMessagesAsync(true); // DCD (the meter's pre-data measurement), SDM receipts
        Console.WriteLine($"  TNC firmware {await tnc.GetVersionAsync()}");
        await tnc.SetBeaconIntervalAsync(0); // a 60 s status beacon keying mid-probe pollutes everything

        var station = new NinoTncTxDelayMinStation(tnc, source, radio)
        {
            Log = line => Console.WriteLine("  " + line),
        };

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
            : await RunMeter(link, station, p, cancelOnCtrlC.Token);
    }

    private static async Task<int> RunCoordinator(
        SdmTuningLink link, NinoTncTxDelayMinStation station, Args p, CancellationToken cancellationToken)
    {
        await using var minimizer = new TxDelayMinimizer(link, station, p.Options)
        {
            Log = line => Console.WriteLine("  " + line),
        };

        TxDelaySweepResult result;
        int? applied = null;
        try
        {
            Console.WriteLine();
            result = await minimizer.RunSweepAsync(cancellationToken);
            Console.WriteLine();
            Console.WriteLine(TxDelayMinReport.RenderMarkdown(result));

            int? applyAt = p.ApplyAtMs ?? (p.Apply ? result.RecommendedMs : null);
            if (applyAt is { } ms)
            {
                if (!result.Success && p.ApplyAtMs is null)
                {
                    Console.WriteLine("  no recommendation to apply — skipping --apply");
                }
                else
                {
                    Console.WriteLine($"  ── APPLY: verifying {ms} ms ──");
                    var apply = await minimizer.ApplyAsync(ms, cancellationToken);
                    Console.WriteLine(apply.Verified
                        ? string.Create(CultureInfo.InvariantCulture,
                            $"  applied: TXDELAY {apply.TxDelayMs} ms verified {apply.Decoded}/{apply.Probes}" +
                            $"{(apply.MedianPreDataCarrierMs is { } pre ? $", heard pre-data ~{pre:0} ms" : string.Empty)}")
                        : $"  APPLY FAILED: {apply.Detail}");
                    if (apply.Verified)
                    {
                        applied = apply.TxDelayMs;
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"  NOTE: the value lives in TNC RAM only — persist it in your node config " +
                            $"(ports[].kiss.txDelay: {(apply.TxDelayMs + 9) / 10}) or it dies with the TNC's power"));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("  cancelled — the sweep's abort path restored TXDELAY + channel access");
            await minimizer.EndAsync(null, CancellationToken.None);
            return 1;
        }

        await minimizer.EndAsync(applied ?? result.RecommendedMs, CancellationToken.None);
        return result.Success ? 0 : 1;
    }

    private static async Task<int> RunMeter(
        SdmTuningLink link, NinoTncTxDelayMinStation station, Args p, CancellationToken cancellationToken)
    {
        var responder = new TxDelayMinResponder(link, station, p.Options)
        {
            Log = line => Console.WriteLine("  " + line),
        };
        Console.WriteLine();
        return await responder.RunAsync(cancellationToken);
    }

    private sealed record Args(
        string Role, string Tnc, string Radio, string Peer, string CallsignText,
        bool Verbose, bool Apply, int? ApplyAtMs, TxDelayMinOptions Options);

    private static Args? Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        bool verbose = false, apply = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--verbose":
                    verbose = true;
                    break;
                case "--apply":
                    apply = true;
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
        if (role is not ("coordinator" or "meter") || tnc is null || radioPort is null || peer is null)
        {
            return null;
        }

        int start = ParseInt(flags.GetValueOrDefault("--start")) ?? 500;
        int step = ParseInt(flags.GetValueOrDefault("--step")) ?? 40;
        int min = ParseInt(flags.GetValueOrDefault("--min")) ?? 20;
        int probes = ParseInt(flags.GetValueOrDefault("--probes")) is int pr and >= 1 and <= 20 ? pr : 5;
        int? applyAt = ParseInt(flags.GetValueOrDefault("--apply-at"));
        if (step <= 0 || min < 0 || start < min || start > 2550 || applyAt is <= 0 or > 2550)
        {
            return null;
        }

        var options = new TxDelayMinOptions
        {
            StartTxDelayMs = start,
            StepMs = step,
            MinTxDelayMs = min,
            ProbesPerStep = probes,
        };
        return new Args(role, tnc, radioPort, peer,
            flags.GetValueOrDefault("--callsign") ?? "N0CALL", verbose, apply, applyAt, options);
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : null;
}
