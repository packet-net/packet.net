using System.Diagnostics;
using System.Text;
using Packet.Ax25;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Adaptive;
using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Long-running test campaign against the back-to-back NinoTNC pair.
/// Driven by sub-commands; each writes a markdown section to the report
/// file under <c>artifacts/nino-tnc-soak/&lt;timestamp&gt;/</c>.
/// </summary>
internal static class Soak
{
    private const byte DefaultModeForSweeps = 6;       // 1200 AFSK AX.25 — robust on audio
    private const byte DefaultTxDelayUnits = 50;       // 500 ms — KISS spec default

    public static async Task<int> Run(string command, string portA, string portB)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var outDir = Path.Combine("artifacts", "nino-tnc-soak", stamp);
        Directory.CreateDirectory(outDir);
        var report = Path.Combine(outDir, "results.md");

        await using var fileStream = new FileStream(report, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(fileStream) { AutoFlush = true };
        var sink = new ReportSink(writer);

        await sink.WriteLineAsync($"# NinoTNC soak campaign — {stamp}");
        await sink.WriteLineAsync($"- ports: A={portA}, B={portB}");
        await sink.WriteLineAsync($"- host: {Environment.MachineName} on {Environment.OSVersion}");
        await sink.WriteLineAsync($"- driver: Packet.Kiss.NinoTnc.NinoTncSerialPort");
        await sink.WriteLineAsync();

        try
        {
            switch (command)
            {
                case "mode-sweep":
                    await ModeSweep(portA, portB, sink);
                    break;
                case "txdelay-sweep":
                    await TxDelaySweep(portA, portB, sink);
                    break;
                case "payload-sweep":
                    await PayloadSweep(portA, portB, sink);
                    break;
                case "throughput":
                    await Throughput(portA, portB, sink);
                    break;
                case "ackmode":
                    await AckModeConcurrent(portA, portB, sink);
                    break;
                case "bidirectional":
                    await Bidirectional(portA, portB, sink);
                    break;
                case "idle":
                    await IdleWatch(portA, portB, sink);
                    break;
                case "estimator-live":
                    await EstimatorLive(portA, portB, sink);
                    break;
                case "stress":
                    await StressRun(portA, portB, sink);
                    break;
                case "per-mode-txdelay":
                    await PerModeTxDelaySweep(portA, portB, sink);
                    break;
                case "binary-payload":
                    await BinaryPayloadStress(portA, portB, sink);
                    break;
                case "all":
                    await ModeSweep(portA, portB, sink);
                    await TxDelaySweep(portA, portB, sink);
                    await PayloadSweep(portA, portB, sink);
                    await Throughput(portA, portB, sink);
                    await AckModeConcurrent(portA, portB, sink);
                    await Bidirectional(portA, portB, sink);
                    await EstimatorLive(portA, portB, sink);
                    await IdleWatch(portA, portB, sink);
                    break;
                case "marathon":
                    // The "I have hours" run. Adds the stress + binary +
                    // per-mode-TXDELAY soaks on top of the standard set.
                    await ModeSweep(portA, portB, sink);
                    await TxDelaySweep(portA, portB, sink);
                    await PerModeTxDelaySweep(portA, portB, sink);
                    await PayloadSweep(portA, portB, sink);
                    await BinaryPayloadStress(portA, portB, sink);
                    await Throughput(portA, portB, sink);
                    await AckModeConcurrent(portA, portB, sink);
                    await Bidirectional(portA, portB, sink);
                    await EstimatorLive(portA, portB, sink);
                    await StressRun(portA, portB, sink);
                    await IdleWatch(portA, portB, sink);
                    break;
                default:
                    Console.WriteLine($"unknown soak sub-command '{command}'");
                    return 2;
            }
        }
        catch (Exception ex)
        {
            await sink.WriteLineAsync();
            await sink.WriteLineAsync($"## FAULT");
            await sink.WriteLineAsync($"```\n{ex}\n```");
            Console.WriteLine(ex);
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"Report written to {report}");
        return 0;
    }

    // -------------------- 1. Mode compatibility sweep --------------------

    private static async Task ModeSweep(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Mode compatibility A↔B");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Each mode is set on both modems via SETHW (+16 non-persist). Five round-trips per direction with default TXDELAY=50, payload \"SOAK MODE n DIR\". Success = received frame matches sent within 8 s.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| Mode | Name | bps | A→B succ | A→B mean ms | B→A succ | B→A mean ms |");
        await sink.WriteLineAsync("|---:|---|---:|---:|---:|---:|---:|");

        // Audio-coupled candidates first; the modes that need clean audio
        // (9600 GFSK, 4FSK, 19200 4FSK) may or may not pass through.
        byte[] candidates = { 6, 7, 12, 0, 2, 3, 1 };
        foreach (var mode in candidates)
        {
            var info = NinoTncCatalog.ByMode[mode];
            Console.WriteLine($"\n--- mode {mode} ({info.Name}) ---");
            await using var a = NinoTncSerialPort.Open(portA);
            await using var b = NinoTncSerialPort.Open(portB);
            await a.SetModeAsync(mode);
            await b.SetModeAsync(mode);
            await Task.Delay(700);

            var ab = await RoundTrips(a, b, 5, $"SOAK M{mode} AB", TimeSpan.FromSeconds(12));
            var ba = await RoundTrips(b, a, 5, $"SOAK M{mode} BA", TimeSpan.FromSeconds(12));

            await sink.WriteLineAsync($"| {mode} | {info.Name} | {info.BitRateHz} | {ab.Successes}/{ab.Total} | {Format(ab.MeanMs)} | {ba.Successes}/{ba.Total} | {Format(ba.MeanMs)} |");
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 2. TXDELAY sweep --------------------

    private static async Task TxDelaySweep(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## TXDELAY sweep at mode 6 (1200 AFSK AX.25)");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("TXDELAY is applied as a KISS parameter on both modems before each batch. 10 frames each direction per row. Goal: find the lowest reliable value.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| TXDELAY units | TXDELAY ms | A→B succ | A→B mean ms | B→A succ | B→A mean ms |");
        await sink.WriteLineAsync("|---:|---:|---:|---:|---:|---:|");

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await Task.Delay(500);

        byte[] sweep = { 50, 40, 30, 20, 15, 12, 10, 8, 6, 5, 4, 3, 2, 1 };
        foreach (var txd in sweep)
        {
            Console.WriteLine($"\n--- TXDELAY = {txd} ({txd * 10} ms) ---");
            await a.SetTxDelayAsync(txd);
            await b.SetTxDelayAsync(txd);
            await Task.Delay(100);

            var ab = await RoundTrips(a, b, 10, $"TXD{txd}-AB", TimeSpan.FromSeconds(8));
            var ba = await RoundTrips(b, a, 10, $"TXD{txd}-BA", TimeSpan.FromSeconds(8));

            await sink.WriteLineAsync($"| {txd} | {txd * 10} | {ab.Successes}/{ab.Total} | {Format(ab.MeanMs)} | {ba.Successes}/{ba.Total} | {Format(ba.MeanMs)} |");

            // Once both directions hit zero, stepping further is pointless.
            if (ab.Successes == 0 && ba.Successes == 0)
            {
                Console.WriteLine("both directions zero — ending sweep");
                break;
            }
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 3. Payload size sweep --------------------

    private static async Task PayloadSweep(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Payload size sweep at mode 6");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("AX.25 INFO field varied. 5 attempts per row, both directions. Frame size = INFO + ~17 bytes of AX.25 header.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| INFO bytes | A→B succ | A→B mean ms | B→A succ | B→A mean ms |");
        await sink.WriteLineAsync("|---:|---:|---:|---:|---:|");

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await Task.Delay(500);

        int[] sizes = { 1, 16, 64, 128, 200, 230 };  // AX.25 N1=256; reserve ~17B for header
        foreach (var size in sizes)
        {
            Console.WriteLine($"\n--- payload {size} bytes ---");
            var ab = await RoundTripsWithPayloadLen(a, b, 5, size, TimeSpan.FromSeconds(12));
            var ba = await RoundTripsWithPayloadLen(b, a, 5, size, TimeSpan.FromSeconds(12));
            await sink.WriteLineAsync($"| {size} | {ab.Successes}/{ab.Total} | {Format(ab.MeanMs)} | {ba.Successes}/{ba.Total} | {Format(ba.MeanMs)} |");
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 4. Throughput --------------------

    private static async Task Throughput(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Sustained throughput (ACK-paced)");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("20 frames of 200-byte AX.25 INFO sent A→B in ACKMODE: each `SendFrameWithAckAsync` awaits the TX-completion echo before the next is queued. Effective bytes/sec is total INFO bytes ÷ wall-clock elapsed. Compare to theoretical (bps ÷ 8).");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Note: an earlier non-paced version of this test bursted Data frames at the TNC; on three of four modes that delivered ~0 % of the frames because the TNC silently dropped under pressure. ACK-paced sends one at a time and is a true measure of sustained airtime throughput.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| Mode | Name | bps | Theoretical B/s | Frames | All ACKed? | Elapsed s | Effective B/s | Efficiency |");
        await sink.WriteLineAsync("|---:|---|---:|---:|---:|---:|---:|---:|---:|");

        byte[] modes = { 6, 7, 12, 2 };
        foreach (var mode in modes)
        {
            var info = NinoTncCatalog.ByMode[mode];
            Console.WriteLine($"\n--- throughput mode {mode} ({info.Name}) ---");
            await using var a = NinoTncSerialPort.Open(portA);
            await using var b = NinoTncSerialPort.Open(portB);
            await a.SetModeAsync(mode);
            await b.SetModeAsync(mode);
            await a.SetTxDelayAsync(5);    // Use the measured floor — every mode supports it
            await Task.Delay(700);

            const int N = 20;
            const int sz = 200;

            var sw = Stopwatch.StartNew();
            int acked = 0;
            for (int i = 0; i < N; i++)
            {
                byte[] payload = RandomPayload(sz, i, $"TPUT M{mode} {i:000}");
                var ax25 = Ax25Frame.Ui(
                    destination: new Callsign("TEST", 2),
                    source: new Callsign("M0LTE", 1),
                    info: payload);
                try
                {
                    await a.SendFrameWithAckAsync(ax25.ToBytes(), TimeSpan.FromSeconds(30));
                    acked++;
                }
                catch (TimeoutException)
                {
                    Console.WriteLine($"   frame {i + 1}/{N}: ACK timeout — bailing");
                    break;
                }
            }
            sw.Stop();

            int totalBytes = acked * sz;
            double effective = sw.Elapsed.TotalSeconds > 0 ? totalBytes / sw.Elapsed.TotalSeconds : 0;
            double theoretical = info.BitRateHz / 8.0;
            double efficiency = theoretical > 0 ? effective / theoretical : 0;
            string allAcked = acked == N ? "yes" : $"no — {acked}/{N}";
            await sink.WriteLineAsync($"| {mode} | {info.Name} | {info.BitRateHz} | {theoretical:F0} | {acked} | {allAcked} | {sw.Elapsed.TotalSeconds:F2} | {effective:F0} | {efficiency:P0} |");
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 5. ACKMODE concurrency --------------------

    private static async Task AckModeConcurrent(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## ACKMODE concurrency");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Fires N ACKMODE frames concurrently from A and awaits all echoes. Mode 6. Reports total elapsed and the distribution of per-tag round trips.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| N concurrent | All echoed? | Total elapsed s | Min ms | Mean ms | Max ms |");
        await sink.WriteLineAsync("|---:|---:|---:|---:|---:|---:|");

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await Task.Delay(500);

        int[] batchSizes = { 1, 2, 4, 8 };
        foreach (var n in batchSizes)
        {
            Console.WriteLine($"\n--- ACKMODE batch of {n} ---");
            var ax25 = Ax25Frame.Ui(
                destination: new Callsign("TEST", 2),
                source: new Callsign("M0LTE", 1),
                info: Encoding.ASCII.GetBytes($"ACK BATCH N{n}"));
            byte[] bytes = ax25.ToBytes();

            var sw = Stopwatch.StartNew();
            var tasks = new List<Task<TxCompletion>>(n);
            for (int i = 0; i < n; i++)
            {
                tasks.Add(a.SendFrameWithAckAsync(bytes, TimeSpan.FromSeconds(30)));
            }
            TxCompletion[] receipts;
            try
            {
                receipts = await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                await sink.WriteLineAsync($"| {n} | no — {ex.GetType().Name} | {sw.Elapsed.TotalSeconds:F2} | — | — | — |");
                continue;
            }
            sw.Stop();
            var ms = receipts.Select(r => r.Elapsed.TotalMilliseconds).ToArray();
            await sink.WriteLineAsync($"| {n} | yes | {sw.Elapsed.TotalSeconds:F2} | {ms.Min():F0} | {ms.Average():F0} | {ms.Max():F0} |");
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 6. Bidirectional simultaneous --------------------

    private static async Task Bidirectional(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Bidirectional simultaneous send");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Both modems send the same instant. Half-duplex audio link → expect CSMA contention. 10 rounds. A success is *both* peers receiving the other's frame within the window.");
        await sink.WriteLineAsync();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await a.SetTxDelayAsync(20);
        await b.SetTxDelayAsync(20);
        await Task.Delay(500);

        int aGotB = 0, bGotA = 0, both = 0;
        const int rounds = 10;
        for (int i = 0; i < rounds; i++)
        {
            Console.WriteLine($"--- bidir round {i + 1} ---");
            var aSent = Ax25Frame.Ui(new Callsign("BB", 2), new Callsign("AA", 1), Encoding.ASCII.GetBytes($"AB-{i}"));
            var bSent = Ax25Frame.Ui(new Callsign("AA", 1), new Callsign("BB", 2), Encoding.ASCII.GetBytes($"BA-{i}"));

            var aGotBTcs = new TaskCompletionSource();
            var bGotATcs = new TaskCompletionSource();
            EventHandler<KissFrame> onA = (_, f) =>
            {
                if (f.Command == KissCommand.Data && Ax25Frame.TryParse(f.Payload, out var p) &&
                    p.Info.Span.SequenceEqual(bSent.Info.Span))
                {
                    aGotBTcs.TrySetResult();
                }
            };
            EventHandler<KissFrame> onB = (_, f) =>
            {
                if (f.Command == KissCommand.Data && Ax25Frame.TryParse(f.Payload, out var p) &&
                    p.Info.Span.SequenceEqual(aSent.Info.Span))
                {
                    bGotATcs.TrySetResult();
                }
            };
            a.FrameReceived += onA;
            b.FrameReceived += onB;
            try
            {
                var tA = a.SendFrameAsync(aSent.ToBytes());
                var tB = b.SendFrameAsync(bSent.ToBytes());
                await Task.WhenAll(tA, tB);

                var aOk = await aGotBTcs.Task.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => aGotBTcs.Task.IsCompletedSuccessfully);
                var bOk = await bGotATcs.Task.WaitAsync(TimeSpan.FromSeconds(15)).ContinueWith(_ => bGotATcs.Task.IsCompletedSuccessfully);
                if (aOk) aGotB++;
                if (bOk) bGotA++;
                if (aOk && bOk) both++;
            }
            finally
            {
                a.FrameReceived -= onA;
                b.FrameReceived -= onB;
            }
        }
        await sink.WriteLineAsync($"- rounds: {rounds}");
        await sink.WriteLineAsync($"- A received B's frame: {aGotB}/{rounds}");
        await sink.WriteLineAsync($"- B received A's frame: {bGotA}/{rounds}");
        await sink.WriteLineAsync($"- both sides successful: {both}/{rounds}");
        await sink.WriteLineAsync();
    }

    // -------------------- 7. Idle stability --------------------

    private static async Task IdleWatch(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Long-idle stability");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Open both modems, sit on the ports with no traffic for 2 minutes, count any inbound frames or pump errors.");
        await sink.WriteLineAsync();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await Task.Delay(500);

        int aFrames = 0, bFrames = 0;
        a.FrameReceived += (_, _) => Interlocked.Increment(ref aFrames);
        b.FrameReceived += (_, _) => Interlocked.Increment(ref bFrames);

        var watchSpan = TimeSpan.FromMinutes(2);
        Console.WriteLine($"idle-watch for {watchSpan} ...");
        await Task.Delay(watchSpan);
        await sink.WriteLineAsync($"- watch duration: {watchSpan}");
        await sink.WriteLineAsync($"- inbound frames on A: {aFrames}");
        await sink.WriteLineAsync($"- inbound frames on B: {bFrames}");
        await sink.WriteLineAsync();
    }

    // -------------------- 8. Estimator live --------------------

    private static async Task EstimatorLive(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Live `TxDelayHillClimbEstimator`");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Starts the estimator at TXDELAY=50 and feeds it real ACKMODE outcomes from 40 frames. Logs the recommended TXDELAY at each step. Aggressive tuning (`SuccessesPerStepDown=3`, `StepUnits=2`, `MinTxDelay=2`).");
        await sink.WriteLineAsync();

        var estimator = new TxDelayHillClimbEstimator(initialTxDelay: 50)
        {
            SuccessesPerStepDown = 3,
            StepUnits = 2,
            MinTxDelay = 2,
            LossPenaltyUnits = 10,
            MaxTxDelay = 100,
        };

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await Task.Delay(500);

        const string peer = "BB-2";
        const int total = 40;
        await sink.WriteLineAsync("| Step | Sent TXDELAY | Outcome | Elapsed ms | Estimator → next |");
        await sink.WriteLineAsync("|---:|---:|---|---:|---:|");
        for (int i = 0; i < total; i++)
        {
            byte tx = estimator.Recommend(peer).TxDelayTenMsUnits ?? DefaultTxDelayUnits;
            await a.SetTxDelayAsync(tx);
            await Task.Delay(50);

            var ax25 = Ax25Frame.Ui(
                destination: new Callsign("BB", 2),
                source: new Callsign("AA", 1),
                info: Encoding.ASCII.GetBytes($"EST{i:000}"));
            var sample = new FrameOutcomeSample(peer, FrameOutcome.AcknowledgedFirstTry,
                ax25.Info.Length, KissParameters.SpecDefaults with { TxDelayTenMsUnits = tx },
                null, DateTimeOffset.UtcNow);

            try
            {
                var receipt = await a.SendFrameWithAckAsync(ax25.ToBytes(), TimeSpan.FromSeconds(15));
                sample = sample with { AckElapsed = receipt.Elapsed };
                estimator.Observe(sample);
                await sink.WriteLineAsync($"| {i + 1} | {tx} | ack | {receipt.Elapsed.TotalMilliseconds:F0} | {estimator.CurrentTxDelayFor(peer)} |");
            }
            catch (TimeoutException)
            {
                estimator.Observe(sample with { Outcome = FrameOutcome.AckModeTimedOut });
                await sink.WriteLineAsync($"| {i + 1} | {tx} | timeout | — | {estimator.CurrentTxDelayFor(peer)} |");
            }
        }
        await sink.WriteLineAsync();
        await sink.WriteLineAsync($"- final recommendation: TXDELAY={estimator.CurrentTxDelayFor(peer)} units ({(estimator.CurrentTxDelayFor(peer) ?? 0) * 10} ms)");
        await sink.WriteLineAsync();
    }

    // -------------------- 9. High-volume stress --------------------

    private static async Task StressRun(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## High-volume stress (mode 6, TXDELAY=5)");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("200 AX.25 UI frames A→B sequentially at the lowest-known-good TXDELAY (5 = 50 ms). Reports success rate, throughput, distribution of round-trip times.");
        await sink.WriteLineAsync();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await a.SetTxDelayAsync(5);
        await b.SetTxDelayAsync(5);
        await Task.Delay(500);

        const int N = 200;
        const int sz = 100;
        int success = 0;
        var roundTrips = new List<double>(N);
        for (int i = 0; i < N; i++)
        {
            byte[] info = RandomPayload(sz, i, $"STRESS-{i:0000}");
            var ax25 = Ax25Frame.Ui(new Callsign("TEST", 2), new Callsign("M0LTE", 1), info);
            var ms = await OneRoundTrip(a, b, ax25, TimeSpan.FromSeconds(6));
            if (ms >= 0)
            {
                success++;
                roundTrips.Add(ms);
            }
            if (i % 20 == 0)
            {
                Console.WriteLine($"  stress {i + 1}/{N}: success so far {success}");
            }
        }
        await sink.WriteLineAsync($"- frames: {N}");
        await sink.WriteLineAsync($"- success: {success}/{N} ({100.0 * success / N:F1}%)");
        if (roundTrips.Count > 0)
        {
            var sorted = roundTrips.OrderBy(x => x).ToList();
            double Pct(double p) => sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
            await sink.WriteLineAsync($"- round-trip ms: min {sorted[0]:F0}, p50 {Pct(0.5):F0}, p95 {Pct(0.95):F0}, p99 {Pct(0.99):F0}, max {sorted[^1]:F0}");
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 10. Per-mode TXDELAY scan --------------------

    private static async Task PerModeTxDelaySweep(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Per-mode TXDELAY minimum scan");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Walks TXDELAY down a fixed ladder until either direction drops below 10/10. Records the lowest TXDELAY that still scored 10/10 — the audio-link 'floor' for that mode on this hardware.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| Mode | Name | bps | Min TXDELAY 10/10 | Min ms | Notes |");
        await sink.WriteLineAsync("|---:|---|---:|---:|---:|---|");

        byte[] modes = { 6, 7, 12, 0, 2, 3, 1 };
        foreach (var mode in modes)
        {
            var info = NinoTncCatalog.ByMode[mode];
            Console.WriteLine($"\n--- TXDELAY scan mode {mode} ({info.Name}) ---");
            await using var a = NinoTncSerialPort.Open(portA);
            await using var b = NinoTncSerialPort.Open(portB);
            await a.SetModeAsync(mode);
            await b.SetModeAsync(mode);
            await Task.Delay(700);

            byte[] ladder = { 50, 30, 20, 15, 10, 8, 5, 3, 2, 1 };
            byte? lowestOk = null;
            string notes = "";
            foreach (var txd in ladder)
            {
                await a.SetTxDelayAsync(txd);
                await b.SetTxDelayAsync(txd);
                await Task.Delay(100);
                var ab = await RoundTrips(a, b, 10, $"M{mode}-TXD{txd}-AB", TimeSpan.FromSeconds(10));
                var ba = await RoundTrips(b, a, 10, $"M{mode}-TXD{txd}-BA", TimeSpan.FromSeconds(10));
                Console.WriteLine($"   txd={txd}: ab {ab.Successes}/{ab.Total}, ba {ba.Successes}/{ba.Total}");
                if (ab.Successes >= 10 && ba.Successes >= 10)
                {
                    lowestOk = txd;
                }
                else
                {
                    notes = $"breaks at TXDELAY={txd} ({ab.Successes}/{ab.Total} | {ba.Successes}/{ba.Total})";
                    break;
                }
            }
            string lowestStr = lowestOk?.ToString() ?? "—";
            string msStr = lowestOk is null ? "—" : $"{lowestOk.Value * 10}";
            await sink.WriteLineAsync($"| {mode} | {info.Name} | {info.BitRateHz} | {lowestStr} | {msStr} | {notes} |");
        }
        await sink.WriteLineAsync();
    }

    // -------------------- 11. Binary payload stress (KISS escape coverage) --------------------

    private static async Task BinaryPayloadStress(string portA, string portB, ReportSink sink)
    {
        await sink.WriteLineAsync("## Binary payload stress (KISS escape coverage)");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Random AX.25 INFO bytes biased toward 0xC0 (FEND) and 0xDB (FESC) to exercise the KISS escape path through the actual driver and modem. 30 frames at mode 6 with TXDELAY=5.");
        await sink.WriteLineAsync();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(DefaultModeForSweeps);
        await b.SetModeAsync(DefaultModeForSweeps);
        await a.SetTxDelayAsync(5);
        await b.SetTxDelayAsync(5);
        await Task.Delay(500);

        const int N = 30;
        int success = 0;
        var rng = new Random(0xC0DB);
        for (int i = 0; i < N; i++)
        {
            int len = rng.Next(20, 200);
            var payload = new byte[len];
            for (int j = 0; j < len; j++)
            {
                int dice = rng.Next(100);
                if (dice < 15) payload[j] = 0xC0;
                else if (dice < 30) payload[j] = 0xDB;
                else if (dice < 45) payload[j] = 0xDC;
                else if (dice < 60) payload[j] = 0xDD;
                else payload[j] = (byte)rng.Next(256);
            }
            var ax25 = Ax25Frame.Ui(new Callsign("TEST", 2), new Callsign("M0LTE", 1), payload);
            var ms = await OneRoundTrip(a, b, ax25, TimeSpan.FromSeconds(6));
            Console.WriteLine($"  binary {i + 1}/{N} len={len} → {(ms >= 0 ? "ok" : "FAIL")}");
            if (ms >= 0) success++;
        }
        await sink.WriteLineAsync($"- frames: {N}");
        await sink.WriteLineAsync($"- escape-heavy success: {success}/{N} ({100.0 * success / N:F1}%)");
        await sink.WriteLineAsync();
    }

    // -------------------- helpers --------------------

    private static byte[] RandomPayload(int len, int seed, string prefix)
    {
        var rng = new Random(seed);
        var bytes = new byte[len];
        var prefixBytes = Encoding.ASCII.GetBytes(prefix);
        Array.Copy(prefixBytes, bytes, Math.Min(prefixBytes.Length, len));
        for (int i = prefixBytes.Length; i < len; i++)
        {
            bytes[i] = (byte)('A' + rng.Next(26));
        }
        return bytes;
    }

    private record RoundTripResult(int Successes, int Total, double MeanMs);

    private static async Task<RoundTripResult> RoundTrips(NinoTncSerialPort tx, NinoTncSerialPort rx, int attempts, string label, TimeSpan timeout)
    {
        int success = 0;
        var elapsedMs = new List<double>(attempts);
        for (int i = 0; i < attempts; i++)
        {
            var ax25 = Ax25Frame.Ui(
                destination: new Callsign("TEST", 2),
                source: new Callsign("M0LTE", 1),
                info: Encoding.ASCII.GetBytes($"{label} #{i}"));
            var ms = await OneRoundTrip(tx, rx, ax25, timeout);
            if (ms >= 0)
            {
                success++;
                elapsedMs.Add(ms);
            }
            // Settle between frames so half-duplex CSMA doesn't trip itself.
            await Task.Delay(100);
        }
        double mean = elapsedMs.Count > 0 ? elapsedMs.Average() : double.NaN;
        return new RoundTripResult(success, attempts, mean);
    }

    private static async Task<RoundTripResult> RoundTripsWithPayloadLen(NinoTncSerialPort tx, NinoTncSerialPort rx, int attempts, int payloadLen, TimeSpan timeout)
    {
        int success = 0;
        var elapsedMs = new List<double>(attempts);
        for (int i = 0; i < attempts; i++)
        {
            byte[] info = RandomPayload(payloadLen, payloadLen * 1000 + i, $"SZ{payloadLen}-{i:00}");
            var ax25 = Ax25Frame.Ui(
                destination: new Callsign("TEST", 2),
                source: new Callsign("M0LTE", 1),
                info: info);
            var ms = await OneRoundTrip(tx, rx, ax25, timeout);
            if (ms >= 0)
            {
                success++;
                elapsedMs.Add(ms);
            }
            await Task.Delay(200);
        }
        double mean = elapsedMs.Count > 0 ? elapsedMs.Average() : double.NaN;
        return new RoundTripResult(success, attempts, mean);
    }

    /// <summary>Returns the elapsed ms, or -1 on timeout / mismatch.</summary>
    private static async Task<double> OneRoundTrip(NinoTncSerialPort tx, NinoTncSerialPort rx, Ax25Frame ax25, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<double>();
        var sw = Stopwatch.StartNew();
        EventHandler<KissFrame> handler = (_, frame) =>
        {
            if (frame.Command != KissCommand.Data) return;
            if (!Ax25Frame.TryParse(frame.Payload, out var parsed)) return;
            if (parsed.Source.Callsign == ax25.Source.Callsign &&
                parsed.Destination.Callsign == ax25.Destination.Callsign &&
                parsed.Info.Span.SequenceEqual(ax25.Info.Span))
            {
                tcs.TrySetResult(sw.Elapsed.TotalMilliseconds);
            }
        };
        rx.FrameReceived += handler;
        try
        {
            await tx.SendFrameAsync(ax25.ToBytes());
            try
            {
                return await tcs.Task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
                return -1;
            }
        }
        finally
        {
            rx.FrameReceived -= handler;
        }
    }

    private static string Format(double ms) => double.IsNaN(ms) ? "—" : $"{ms:F0}";

    private sealed class ReportSink(StreamWriter writer)
    {
        public async Task WriteLineAsync(string text = "")
        {
            Console.WriteLine(text);
            await writer.WriteLineAsync(text);
        }
    }
}
