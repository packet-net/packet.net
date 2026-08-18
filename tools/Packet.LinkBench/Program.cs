using System.Globalization;
using System.Text;
using System.Text.Json;
using Packet.LinkBench;

// ── Packet.LinkBench - AX.25 connected-mode link bench (docs/link-bench-plan.md) ──
//
// Two AX.25 engines in one process, joined by a pluggable channel; a bulk
// connected-mode transfer A→B; a metrics table. Multi-valued flags sweep.

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(Cli.HelpText);
    return 0;
}

List<RunConfig> runs;
string? jsonPath;
string? tracePath;
bool detail;
try
{
    (runs, jsonPath, tracePath, detail) = Cli.Parse(args);
}
catch (Exception ex) when (ex is FormatException or ArgumentException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine("run with --help for usage.");
    return 2;
}

Console.WriteLine($"Packet.LinkBench — {runs.Count} run(s)");
if (runs.Any(r => r.ZeroAirtimeTimersAutoScaled))
{
    Console.WriteLine(
        $"note: baud=0 (no airtime) — default T1V auto-scaled to {Cli.ZeroAirtimeDefaultT1Ms:F0}ms / T2 {Cli.ZeroAirtimeDefaultT2Ms:F0}ms so loss recovery isn't dominated by the 6 s spec default (which is sized for real airtime). Pass --t1/--t2 to override, or --baud N / --time-scale for a modelled channel.");
}

Console.WriteLine();

var results = new List<BenchResult>();
using var ctrlC = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; ctrlC.Cancel(); };

foreach (var (cfg, i) in runs.Select((c, i) => (c, i)))
{
    if (ctrlC.IsCancellationRequested)
    {
        break;
    }

    Console.Write($"[{i + 1}/{runs.Count}] {cfg.Describe()} … ");
    var result = await BenchRunner.RunAsync(cfg, ctrlC.Token);
    results.Add(result);
    Console.WriteLine(result.Completed
        ? $"{result.TransferTime.TotalSeconds:F2}s, {result.ThroughputBytesPerSec:F0} B/s{(result.IntegrityOk ? "" : " INTEGRITY-FAIL")}"
        : $"FAILED ({result.Failure})");
}

Console.WriteLine();
Console.WriteLine(ResultTable.Render(results));

if (detail || results.Count <= 2)
{
    foreach (var r in results)
    {
        Console.WriteLine();
        Console.WriteLine(ResultTable.RenderDetail(r));
    }
}

if (jsonPath is not null)
{
    await File.WriteAllLinesAsync(jsonPath, results.Select(ResultTable.ToJsonLine));
    Console.WriteLine();
    Console.WriteLine($"wrote {results.Count} result(s) to {jsonPath}");
}

if (tracePath is not null)
{
    await File.WriteAllTextAsync(tracePath, ResultTable.RenderTraces(results));
    Console.WriteLine($"wrote frame traces to {tracePath}");
}

return results.All(r => r.Completed && r.IntegrityOk) ? 0 : 1;

internal static class Cli
{
    public const string HelpText = """
        Packet.LinkBench — AX.25 connected-mode link bench (see docs/link-bench-plan.md)

        usage: dotnet run --project tools/Packet.LinkBench -- [options]

        Comma-separated values on swept flags expand to a cartesian product of runs.

        transfer
          --channel C          inproc | axudp | netsim          (default inproc)
          --payload N          bulk payload size; k/m suffixes ok (default 64k)
          --bidi               both ends send at once (exercises turn/RNR logic)

        engine knobs (swept)
          --k LIST             send-window size 1..7              (default engine: 4)
          --t1 LIST            T1V in ms          (default engine: 6000; auto-
                               scaled to 500 at --baud 0 — see --baud)
          --t2 LIST            T2 ack-delay in ms; 0=ack-per-frame (default engine: 3000)
          --paclen LIST        bytes per I-frame, ≤ N1=256        (default 256)
          --ackmode LIST       on | off — pace TX on the 0x0C echo (default on)
          --t1-tx-complete LIST  on | off — re-arm T1 from the frame's TX-complete
                               echo instead of enqueue (default off)
          --srej LIST          on | off — force SREJ (selective reject) on both
                               ends; only bites under loss (default off)

        inproc channel model (rung 1b; ignored on axudp/netsim)
          --baud N             modeled bit/s; 0 = no airtime      (default 0 — rung 1)
                               at 0, the default T1V/T2 auto-scale down (the 6 s
                               spec default assumes airtime; under loss, recovery
                               latency = losses × T1V and dwarfs the zero RTT).
                               An explicit --t1 is always respected.
          --half-duplex        one transmitter at a time
          --txdelay-ms N       keyup delay                        (default 250)
          --txtail-ms N        tail hang                          (default 20)
          --turnaround-ms N    half-duplex turnaround             (default 100)
          --loss LIST          frame loss probability 0..1        (default 0)
          --seed N             loss-roll / payload seed           (default 42)
          --time-scale F       run the modeled channel F× faster than real time;
                               scales engine T1/T2/T3 defaults to match (default 1)

        other channels
          --axudp-ports A,B    UDP loopback ports                 (default 27401,27402)
          --netsim A,B         host:port,host:port of two net-sim KISS-TCP ports

        measurement / output
          --dup-window-ms N    max gap between identical supervisory frames to
                               count as a duplicate burst (#79)   (default 1000)
          --run-timeout-s N    per-run budget                     (default 600)
          --json PATH          also write one JSON object per run (JSONL)
          --trace PATH         dump every traced frame (both endpoints, monitor-style)
          --detail             per-run frame breakdown even on big sweeps

        examples
          # rung 1: lossless, no channel — the #79 engine-intrinsic question
          dotnet run --project tools/Packet.LinkBench -- --payload 64k --t2 0

          # rung 1: same over real UDP loopback (ackmode off by nature)
          dotnet run --project tools/Packet.LinkBench -- --channel axudp --payload 64k --t2 0

          # k × ackmode sweep at modeled 1200 baud, 50× faster than real time
          dotnet run --project tools/Packet.LinkBench -- --payload 16k --baud 1200 \
              --half-duplex --time-scale 50 --k 1,2,4,7 --ackmode on,off
        """;

    public static (List<RunConfig> Runs, string? JsonPath, string? TracePath, bool Detail) Parse(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var switches = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"unexpected argument '{a}'");
            }
            if (a is "--bidi" or "--half-duplex" or "--detail")
            {
                switches.Add(a);
            }
            else
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{a} needs a value");
                }

                flags[a] = args[++i];
            }
        }

        string Get(string name, string fallback) => flags.TryGetValue(name, out var v) ? v : fallback;

        var channel = Get("--channel", "inproc");
        if (channel is not ("inproc" or "axudp" or "netsim"))
        {
            throw new ArgumentException($"--channel must be inproc|axudp|netsim, got '{channel}'");
        }

        var payload = ParseSize(Get("--payload", "64k"));
        if (payload < 1)
        {
            throw new ArgumentException("--payload must be ≥ 1");
        }

        // Swept dimensions. null entries mean "engine default".
        var ks = ParseIntList(flags.GetValueOrDefault("--k"));
        foreach (var k in ks.OfType<int>())
        {
            if (k is < 1 or > 7)
            {
                throw new ArgumentException($"--k must be 1..7 (mod-8), got {k}");
            }
        }
        var t1s = ParseMsList(flags.GetValueOrDefault("--t1"));
        var t2s = ParseMsList(flags.GetValueOrDefault("--t2"));
        var paclens = ParseIntList(flags.GetValueOrDefault("--paclen")).Select(p => p ?? 256).Distinct().ToList();
        foreach (var p in paclens)
        {
            if (p is < 1 or > 256)
            {
                throw new ArgumentException($"--paclen must be 1..256 (N1), got {p}");
            }
        }
        var t1txs = (flags.GetValueOrDefault("--t1-tx-complete") ?? "off").Split(',')
            .Select(s => s.Trim().ToLowerInvariant() switch
            {
                "on" => true,
                "off" => false,
                var other => throw new ArgumentException($"--t1-tx-complete values are on|off, got '{other}'"),
            }).Distinct().ToList();
        var srejs = (flags.GetValueOrDefault("--srej") ?? "off").Split(',')
            .Select(s => s.Trim().ToLowerInvariant() switch
            {
                "on" => true,
                "off" => false,
                var other => throw new ArgumentException($"--srej values are on|off, got '{other}'"),
            }).Distinct().ToList();
        var ackmodes = (flags.GetValueOrDefault("--ackmode") ?? "on").Split(',')
            .Select(s => s.Trim().ToLowerInvariant() switch
            {
                "on" => true,
                "off" => false,
                var other => throw new ArgumentException($"--ackmode values are on|off, got '{other}'"),
            }).Distinct().ToList();
        var losses = (flags.GetValueOrDefault("--loss") ?? "0").Split(',')
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).Distinct().ToList();
        foreach (var l in losses)
        {
            if (l is < 0 or >= 1)
            {
                throw new ArgumentException($"--loss must be in [0,1), got {l}");
            }
        }

        if (channel == "axudp" && ackmodes.Contains(true))
        {
            Console.Error.WriteLine("note: AXUDP is a tunnel with no TNC — no 0x0C echo; forcing ackmode off.");
            ackmodes = [false];
        }

        ((string, int), (string, int))? netsim = null;
        if (channel == "netsim")
        {
            var spec = flags.GetValueOrDefault("--netsim")
                ?? throw new ArgumentException("--channel netsim requires --netsim host:port,host:port");
            var parts = spec.Split(',');
            if (parts.Length != 2)
            {
                throw new ArgumentException("--netsim wants exactly two host:port endpoints");
            }

            netsim = (ParseHostPort(parts[0]), ParseHostPort(parts[1]));
        }

        var axudpPorts = (27401, 27402);
        if (flags.TryGetValue("--axudp-ports", out var ap))
        {
            var parts = ap.Split(',');
            if (parts.Length != 2)
            {
                throw new ArgumentException("--axudp-ports wants two ports: A,B");
            }

            axudpPorts = (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        var baseCfg = new RunConfig
        {
            Channel = channel,
            PayloadBytes = payload,
            Bidirectional = switches.Contains("--bidi"),
            Baud = int.Parse(Get("--baud", "0"), CultureInfo.InvariantCulture),
            HalfDuplex = switches.Contains("--half-duplex"),
            TxDelay = TimeSpan.FromMilliseconds(double.Parse(Get("--txdelay-ms", "250"), CultureInfo.InvariantCulture)),
            TxTail = TimeSpan.FromMilliseconds(double.Parse(Get("--txtail-ms", "20"), CultureInfo.InvariantCulture)),
            Turnaround = TimeSpan.FromMilliseconds(double.Parse(Get("--turnaround-ms", "100"), CultureInfo.InvariantCulture)),
            Seed = int.Parse(Get("--seed", "42"), CultureInfo.InvariantCulture),
            TimeScale = double.Parse(Get("--time-scale", "1"), CultureInfo.InvariantCulture),
            DupWindow = TimeSpan.FromMilliseconds(double.Parse(Get("--dup-window-ms", "1000"), CultureInfo.InvariantCulture)),
            RunTimeout = TimeSpan.FromSeconds(double.Parse(Get("--run-timeout-s", "600"), CultureInfo.InvariantCulture)),
            AxudpPorts = axudpPorts,
            NetSim = netsim,
        };
        if (baseCfg.TimeScale < 1)
        {
            throw new ArgumentException("--time-scale must be ≥ 1");
        }

        var runs = (
            from k in ks
            from t1 in t1s
            from t2 in t2s
            from paclen in paclens
            from ack in ackmodes
            from t1tx in t1txs
            from srej in srejs
            from loss in losses
            select baseCfg with { K = k, T1 = t1, T2 = t2, Paclen = paclen, AckMode = ack, T1FromTxComplete = t1tx, Srej = srej, Loss = loss }
        ).Select(ApplyZeroAirtimeTimerDefaults).ToList();

        return (runs, flags.GetValueOrDefault("--json"), flags.GetValueOrDefault("--trace"), switches.Contains("--detail"));
    }

    // A zero-airtime (--baud 0) inproc channel delivers instantly, so the 6 s
    // spec-default T1V - sized for real ~1200-baud airtime + turnaround - dwarfs
    // the (zero) round-trip. Under loss, recovery latency is losses × T1V, so a
    // loss-heavy run burns a full 6 s per dropped frame/ack and times out (the
    // engine is fine: pipelining and recovery work; the timer is just mismatched
    // to the channel). Scale the *default* T1/T2 down to match the zero airtime so
    // the bench measures the engine, not the T1:airtime ratio. An explicit --t1
    // means the user is tuning deliberately and is always respected (no scaling,
    // no warning); modelled channels (--baud N / --time-scale) keep the real
    // defaults. See docs/link-bench-plan.md.
    internal const double ZeroAirtimeDefaultT1Ms = 500;
    internal const double ZeroAirtimeDefaultT2Ms = 250;

    private static RunConfig ApplyZeroAirtimeTimerDefaults(RunConfig cfg)
    {
        if (cfg.Channel != "inproc" || cfg.Baud != 0 || cfg.TimeScale != 1.0 || cfg.T1 is not null)
        {
            return cfg;
        }

        return cfg with
        {
            T1 = TimeSpan.FromMilliseconds(ZeroAirtimeDefaultT1Ms),
            T2 = cfg.T2 ?? TimeSpan.FromMilliseconds(ZeroAirtimeDefaultT2Ms),
            ZeroAirtimeTimersAutoScaled = true,
        };
    }

    private static int ParseSize(string text)
    {
        text = text.Trim().ToLowerInvariant();
        var factor = 1;
        if (text.EndsWith('k')) { factor = 1024; text = text[..^1]; }
        else if (text.EndsWith('m')) { factor = 1024 * 1024; text = text[..^1]; }
        return checked(int.Parse(text, CultureInfo.InvariantCulture) * factor);
    }

    private static List<int?> ParseIntList(string? text) =>
        text is null
            ? [null]
            : text.Split(',').Select(s => (int?)int.Parse(s.Trim(), CultureInfo.InvariantCulture)).Distinct().ToList();

    private static List<TimeSpan?> ParseMsList(string? text) =>
        text is null
            ? [null]
            : text.Split(',')
                .Select(s => (TimeSpan?)TimeSpan.FromMilliseconds(double.Parse(s.Trim(), CultureInfo.InvariantCulture)))
                .Distinct().ToList();

    private static (string Host, int Port) ParseHostPort(string text)
    {
        var idx = text.LastIndexOf(':');
        if (idx < 1)
        {
            throw new ArgumentException($"'{text}' is not host:port");
        }

        return (text[..idx], int.Parse(text[(idx + 1)..], CultureInfo.InvariantCulture));
    }
}

internal static class ResultTable
{
    public static string Render(IReadOnlyList<BenchResult> results)
    {
        string[] headers =
        [
            "ch", "k", "T1ms", "T2ms", "pac", "ack", "rej", "loss", "wall_s", "B/s",
            "I_tx", "retx", "RR", "REJ", "SREJ", "dupS", "burst", "stall_s", "ackRTT_ms", "ok",
        ];
        var rows = results.Select(r =>
        {
            var c = r.Config;
            var a = r.StatsA;
            var b = r.StatsB;
            return new[]
            {
                c.Channel,
                c.EffectiveK.ToString(CultureInfo.InvariantCulture),
                c.T1?.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) ?? "def",
                c.T2?.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) ?? "def",
                c.Paclen.ToString(CultureInfo.InvariantCulture),
                c.AckMode ? (c.T1FromTxComplete ? "on+t1" : "on") : (c.T1FromTxComplete ? "t1" : "off"),
                c.Srej ? "srej" : "gbn",
                c.Loss.ToString("0.###", CultureInfo.InvariantCulture),
                r.Completed ? r.TransferTime.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) : "—",
                r.Completed ? r.ThroughputBytesPerSec.ToString("F0", CultureInfo.InvariantCulture) : "—",
                (a.TxI + b.TxI).ToString(CultureInfo.InvariantCulture),
                (a.Retransmits + b.Retransmits).ToString(CultureInfo.InvariantCulture),
                (a.TxRr + b.TxRr).ToString(CultureInfo.InvariantCulture),
                (a.TxRej + b.TxRej).ToString(CultureInfo.InvariantCulture),
                (a.TxSrej + b.TxSrej).ToString(CultureInfo.InvariantCulture),
                (a.DupSupervisory + b.DupSupervisory).ToString(CultureInfo.InvariantCulture),
                Math.Max(a.MaxSupervisoryBurst, b.MaxSupervisoryBurst).ToString(CultureInfo.InvariantCulture),
                (a.WindowStall + b.WindowStall).TotalSeconds.ToString("F2", CultureInfo.InvariantCulture),
                r.AckRttMean?.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) ?? "—",
                r.Completed ? (r.IntegrityOk ? (r.CleanDisconnect ? "✓" : "✓*") : "BAD") : "FAIL",
            };
        }).ToList();

        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join("  ", headers.Select((h, i) => h.PadLeft(widths[i]))));
        sb.AppendLine(string.Join("  ", widths.Select(w => new string('─', w))));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join("  ", row.Select((v, i) => v.PadLeft(widths[i]))));
        }
        sb.Append("ok: ✓ = intact payload + clean DISC, ✓* = intact but DISC unconfirmed; rej = loss-recovery mode (srej selective / gbn go-back-N); SREJ column = SREJ frames actually emitted; dupS/burst = #79 duplicate-supervisory count / longest identical run.");
        return sb.ToString();
    }

    public static string RenderDetail(BenchResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── {r.Config.Describe()} ──");
        if (!r.Completed)
        {
            sb.AppendLine($"   FAILED: {r.Failure}");
        }

        sb.AppendLine($"   connect {r.ConnectTime.TotalMilliseconds:F0} ms · transfer {r.TransferTime.TotalSeconds:F2} s · " +
                      $"{r.ThroughputBytesPerSec:F0} B/s · integrity {(r.IntegrityOk ? "OK" : "FAILED")} · " +
                      (r.IntegrityDetail is { } d ? $"[{d}] " : "") +
                      $"DISC {(r.CleanDisconnect ? "clean" : "unconfirmed")}");
        foreach (var (name, s) in new[] { ("A", r.StatsA), ("B", r.StatsB) })
        {
            sb.AppendLine($"   {name}: TX {s.TxTotal} (I {s.TxI}, RR {s.TxRr}, RNR {s.TxRnr}, REJ {s.TxRej}, SREJ {s.TxSrej}, U {s.TxU}) " +
                          $"RX {s.RxTotal} · retx {s.Retransmits} · dupS {s.DupSupervisory} (burst {s.MaxSupervisoryBurst}) · " +
                          $"stall {s.WindowStall.TotalSeconds:F2} s");
        }
        if (r.AckReceipts > 0)
        {
            sb.AppendLine($"   ackmode echoes: {r.AckReceipts} · RTT min/mean/max " +
                          $"{r.AckRttMin!.Value.TotalMilliseconds:F1}/{r.AckRttMean!.Value.TotalMilliseconds:F1}/{r.AckRttMax!.Value.TotalMilliseconds:F1} ms");
        }
        return sb.ToString().TrimEnd();
    }

    public static string ToJsonLine(BenchResult r) => JsonSerializer.Serialize(new
    {
        channel = r.Config.Channel,
        payloadBytes = r.Config.PayloadBytes,
        k = r.Config.EffectiveK,
        t1Ms = r.Config.T1?.TotalMilliseconds,
        t2Ms = r.Config.T2?.TotalMilliseconds,
        paclen = r.Config.Paclen,
        ackmode = r.Config.AckMode,
        t1FromTxComplete = r.Config.T1FromTxComplete,
        srej = r.Config.Srej,
        bidi = r.Config.Bidirectional,
        baud = r.Config.Baud,
        halfDuplex = r.Config.HalfDuplex,
        loss = r.Config.Loss,
        seed = r.Config.Seed,
        timeScale = r.Config.TimeScale,
        completed = r.Completed,
        failure = r.Failure,
        integrityOk = r.IntegrityOk,
        integrityDetail = r.IntegrityDetail,
        cleanDisconnect = r.CleanDisconnect,
        connectMs = r.ConnectTime.TotalMilliseconds,
        transferS = r.TransferTime.TotalSeconds,
        throughputBps = r.ThroughputBytesPerSec,
        a = Stats(r.StatsA),
        b = Stats(r.StatsB),
        ackReceipts = r.AckReceipts,
        ackRttMsMin = r.AckRttMin?.TotalMilliseconds,
        ackRttMsMean = r.AckRttMean?.TotalMilliseconds,
        ackRttMsMax = r.AckRttMax?.TotalMilliseconds,
    });

    public static string RenderTraces(IReadOnlyList<BenchResult> results)
    {
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"==== {r.Config.Describe()} :: {(r.Completed ? "completed" : $"FAILED: {r.Failure}")} ====");
            var merged = r.TraceA.Select(t => (Ep: "A", t.At, Line: $"{(t.Direction == Packet.Ax25.Session.FrameDirection.Transmitted ? "TX" : "RX")} {DescribeFrame(t.Frame)}"))
                .Concat(r.TraceB.Select(t => (Ep: "B", t.At, Line: $"{(t.Direction == Packet.Ax25.Session.FrameDirection.Transmitted ? "TX" : "RX")} {DescribeFrame(t.Frame)}")))
                .Concat(r.Events.Select(e => (Ep: e.Endpoint, e.At, Line: $"·· {e.What}")))
                .OrderBy(x => x.At)
                .ToList();
            var t0 = merged.Count > 0 ? merged[0].At : default;
            foreach (var (ep, at, line) in merged)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{(at - t0).TotalSeconds,9:F3} {ep} {line}");
            }
        }
        return sb.ToString();
    }

    private static string DescribeFrame(Packet.Ax25.Ax25Frame f)
    {
        var kind = Packet.Ax25.Session.Ax25FrameClassifier.Classify(f) switch
        {
            Packet.Ax25.Session.IFrameReceived => $"I ns={f.Ns} nr={f.Nr} len={f.Info.Length}",
            Packet.Ax25.Session.RrReceived => $"RR nr={f.Nr}",
            Packet.Ax25.Session.RnrReceived => $"RNR nr={f.Nr}",
            Packet.Ax25.Session.RejReceived => $"REJ nr={f.Nr}",
            Packet.Ax25.Session.SrejReceived => $"SREJ nr={f.Nr}",
            Packet.Ax25.Session.SabmReceived => "SABM",
            Packet.Ax25.Session.SabmeReceived => "SABME",
            Packet.Ax25.Session.DiscReceived => "DISC",
            Packet.Ax25.Session.UaReceived => "UA",
            Packet.Ax25.Session.DmReceived => "DM",
            Packet.Ax25.Session.FrmrReceived => "FRMR",
            Packet.Ax25.Session.UiReceived => $"UI len={f.Info.Length}",
            Packet.Ax25.Session.XidReceived => "XID",
            Packet.Ax25.Session.TestReceived => "TEST",
            _ => $"ctrl=0x{f.Control:X2}",
        };
        return $"{f.Source.Callsign}>{f.Destination.Callsign} {kind} {(f.IsCommand ? "cmd" : "res")}{(f.PollFinal ? " pf=1" : "")}";
    }

    private static object Stats(Packet.LinkBench.Metrics.FrameStats s) => new
    {
        txTotal = s.TxTotal,
        txI = s.TxI,
        txRr = s.TxRr,
        txRnr = s.TxRnr,
        txRej = s.TxRej,
        txSrej = s.TxSrej,
        txU = s.TxU,
        rxTotal = s.RxTotal,
        retransmits = s.Retransmits,
        dupSupervisory = s.DupSupervisory,
        maxSupervisoryBurst = s.MaxSupervisoryBurst,
        windowStallS = s.WindowStall.TotalSeconds,
    };
}
