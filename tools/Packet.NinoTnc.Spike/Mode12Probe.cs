using System.Diagnostics;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Deep-dive on mode 12 (300 AFSK AX.25) flakiness. The standard soak
/// uses 5-frame samples — far too small to tell signal from noise.
/// This probe runs 100-frame samples and breaks down where the
/// failures fall: time-of-arrival, TXDELAY sensitivity, payload-size
/// sensitivity, and one-direction-vs-the-other.
/// </summary>
internal static class Mode12Probe
{
    private const byte Mode = 12;
    private const int LargeSample = 100;

    public static async Task<int> Run(string portA, string portB)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var outDir = Path.Combine("artifacts", "nino-tnc-mode12", stamp);
        Directory.CreateDirectory(outDir);
        var reportPath = Path.Combine(outDir, "report.md");
        await using var fs = new FileStream(reportPath, FileMode.Create);
        await using var writer = new StreamWriter(fs) { AutoFlush = true };

        var sink = new Sink(writer);

        await sink.WriteLineAsync($"# Mode 12 (300 AFSK AX.25) characterisation — {stamp}");
        await sink.WriteLineAsync($"- ports: A={portA}, B={portB}");
        await sink.WriteLineAsync();

        // 1) Big sample at default TXDELAY=50, default payload "PROBE-NNN".
        await BigSample(portA, portB, txDelay: 50, payloadBytes: 50, sink);

        // 2) Same again with high TXDELAY=100 — does more preamble help?
        await BigSample(portA, portB, txDelay: 100, payloadBytes: 50, sink);

        // 3) Same with low TXDELAY=20.
        await BigSample(portA, portB, txDelay: 20, payloadBytes: 50, sink);

        // 4) Payload-size sweep at TXDELAY=50 — does failure correlate with frame length?
        await PayloadSweep(portA, portB, sink);

        await sink.WriteLineAsync();
        await sink.WriteLineAsync($"_Report written to `{reportPath}`._");

        Console.WriteLine();
        Console.WriteLine($"Report: {reportPath}");
        return 0;
    }

    private static async Task BigSample(string portA, string portB, byte txDelay, int payloadBytes, Sink sink)
    {
        await sink.WriteLineAsync($"## {LargeSample}-frame sample, TXDELAY={txDelay} ({txDelay * 10} ms), payload {payloadBytes} B");
        await sink.WriteLineAsync();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(Mode);
        await b.SetModeAsync(Mode);
        await a.SetTxDelayAsync(txDelay);
        await b.SetTxDelayAsync(txDelay);
        await Task.Delay(700);

        var (abOk, abMs, abFailures) = await Direction(a, b, "A→B", txDelay, payloadBytes);
        var (baOk, baMs, baFailures) = await Direction(b, a, "B→A", txDelay, payloadBytes);

        await sink.WriteLineAsync($"- A→B: **{abOk}/{LargeSample}** success ({100.0 * abOk / LargeSample:F1}%)");
        if (abOk > 0)
        {
            await sink.WriteLineAsync($"  - RTT ms: min {abMs.Min():F0}, p50 {Pct(abMs, 0.5):F0}, p95 {Pct(abMs, 0.95):F0}, p99 {Pct(abMs, 0.99):F0}, max {abMs.Max():F0}");
        }
        if (abFailures.Count > 0)
        {
            await sink.WriteLineAsync($"  - failure indexes: `{string.Join(", ", abFailures)}`");
        }
        await sink.WriteLineAsync($"- B→A: **{baOk}/{LargeSample}** success ({100.0 * baOk / LargeSample:F1}%)");
        if (baOk > 0)
        {
            await sink.WriteLineAsync($"  - RTT ms: min {baMs.Min():F0}, p50 {Pct(baMs, 0.5):F0}, p95 {Pct(baMs, 0.95):F0}, p99 {Pct(baMs, 0.99):F0}, max {baMs.Max():F0}");
        }
        if (baFailures.Count > 0)
        {
            await sink.WriteLineAsync($"  - failure indexes: `{string.Join(", ", baFailures)}`");
        }

        if (abFailures.Count > 0 || baFailures.Count > 0)
        {
            await sink.WriteLineAsync();
            await sink.WriteLineAsync($"  - **Clustering check** — failure-to-failure gaps (A→B): " +
                                     $"`{string.Join(", ", Gaps(abFailures))}`");
            await sink.WriteLineAsync($"  - failure-to-failure gaps (B→A): " +
                                     $"`{string.Join(", ", Gaps(baFailures))}`");
        }
        await sink.WriteLineAsync();
    }

    private static async Task<(int ok, List<double> ms, List<int> failureIndexes)> Direction(
        NinoTncSerialPort tx,
        NinoTncSerialPort rx,
        string label,
        byte txDelay,
        int payloadBytes)
    {
        Console.WriteLine($"\n--- {label}, TXDELAY={txDelay}, payload={payloadBytes}B, N={LargeSample} ---");
        int ok = 0;
        var elapsed = new List<double>(LargeSample);
        var failures = new List<int>();

        for (int i = 0; i < LargeSample; i++)
        {
            var info = new byte[payloadBytes];
            var prefix = Encoding.ASCII.GetBytes($"M12-{label}-{i:000}");
            Array.Copy(prefix, info, Math.Min(prefix.Length, info.Length));
            for (int j = prefix.Length; j < info.Length; j++)
            {
                info[j] = (byte)('A' + (j % 26));
            }

            var ax25 = Ax25Frame.Ui(new Callsign("BB", 2), new Callsign("AA", 1), info);
            var ms = await OneRoundTrip(tx, rx, ax25, TimeSpan.FromSeconds(15));
            if (ms >= 0)
            {
                ok++;
                elapsed.Add(ms);
            }
            else
            {
                failures.Add(i);
            }
            if ((i + 1) % 20 == 0)
            {
                Console.WriteLine($"  {label}: {i + 1}/{LargeSample}, success so far {ok}, failures {failures.Count}");
            }
        }
        return (ok, elapsed, failures);
    }

    private static async Task PayloadSweep(string portA, string portB, Sink sink)
    {
        await sink.WriteLineAsync($"## Payload-size sensitivity at mode 12, TXDELAY=50, N=50 per row");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("Does mode-12 flakiness scale with frame length? Small frames = less air time = less chance of bit error. Large frames = more chance.");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| Payload bytes | A→B succ/50 | B→A succ/50 |");
        await sink.WriteLineAsync("|---:|---:|---:|");

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(Mode);
        await b.SetModeAsync(Mode);
        await a.SetTxDelayAsync(50);
        await b.SetTxDelayAsync(50);
        await Task.Delay(700);

        int[] sizes = { 5, 20, 50, 100, 200 };
        const int N = 50;
        foreach (var sz in sizes)
        {
            Console.WriteLine($"\n--- payload {sz}B ---");
            int abOk = 0, baOk = 0;
            for (int i = 0; i < N; i++)
            {
                var info = new byte[sz];
                var prefix = Encoding.ASCII.GetBytes($"SZ{sz}-AB-{i:00}");
                Array.Copy(prefix, info, Math.Min(prefix.Length, info.Length));
                for (int j = prefix.Length; j < info.Length; j++) info[j] = (byte)('A' + (j % 26));

                var ax25 = Ax25Frame.Ui(new Callsign("BB", 2), new Callsign("AA", 1), info);
                if (await OneRoundTrip(a, b, ax25, TimeSpan.FromSeconds(15)) >= 0) abOk++;
            }
            for (int i = 0; i < N; i++)
            {
                var info = new byte[sz];
                var prefix = Encoding.ASCII.GetBytes($"SZ{sz}-BA-{i:00}");
                Array.Copy(prefix, info, Math.Min(prefix.Length, info.Length));
                for (int j = prefix.Length; j < info.Length; j++) info[j] = (byte)('A' + (j % 26));

                var ax25 = Ax25Frame.Ui(new Callsign("AA", 1), new Callsign("BB", 2), info);
                if (await OneRoundTrip(b, a, ax25, TimeSpan.FromSeconds(15)) >= 0) baOk++;
            }
            await sink.WriteLineAsync($"| {sz} | {abOk}/{N} | {baOk}/{N} |");
        }
        await sink.WriteLineAsync();
    }

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

    private static double Pct(List<double> xs, double p)
    {
        if (xs.Count == 0) return double.NaN;
        var sorted = xs.OrderBy(x => x).ToList();
        int idx = Math.Min(sorted.Count - 1, (int)(sorted.Count * p));
        return sorted[idx];
    }

    private static List<int> Gaps(List<int> indexes)
    {
        if (indexes.Count < 2) return new();
        var gaps = new List<int>(indexes.Count - 1);
        for (int i = 1; i < indexes.Count; i++)
        {
            gaps.Add(indexes[i] - indexes[i - 1]);
        }
        return gaps;
    }

    private sealed class Sink(StreamWriter writer)
    {
        public async Task WriteLineAsync(string text = "")
        {
            Console.WriteLine(text);
            await writer.WriteLineAsync(text);
        }
    }
}
