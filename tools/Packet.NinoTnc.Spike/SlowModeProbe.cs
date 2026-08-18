using System.Diagnostics;
using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Followup to <see cref="Mode12Probe"/> - does the "TXDELAY=100 breaks
/// B→A" failure mode also show up on the other 300-baud modes?
///
///   Mode 12: 300 AFSK AX.25       - plain AFSK demod, AX.25 framing
///   Mode 13: 300 AFSKPLL IL2P     - PLL AFSK demod, IL2P framing
///   Mode 14: 300 AFSKPLL IL2P+CRC - PLL AFSK demod, IL2P+CRC framing
///
/// Probes both directions at TXDELAY=50 and TXDELAY=100 with N=50 each
/// so we can tell whether the failure is rooted in:
///   - the AFSK-without-PLL demod (mode 12 only)
///   - 300 baud air time (all three modes)
///   - AX.25 framing without IL2P FEC (mode 12 only)
/// </summary>
internal static class SlowModeProbe
{
    private const int N = 50;
    private const int PayloadBytes = 50;

    public static async Task<int> Run(string portA, string portB)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var outDir = Path.Combine("artifacts", "nino-tnc-slow-modes", stamp);
        Directory.CreateDirectory(outDir);
        var reportPath = Path.Combine(outDir, "report.md");
        await using var fs = new FileStream(reportPath, FileMode.Create);
        await using var writer = new StreamWriter(fs) { AutoFlush = true };
        var sink = new Sink(writer);

        await sink.WriteLineAsync($"# 300-baud modes (12, 13, 14) — TXDELAY=50 vs 100 — {stamp}");
        await sink.WriteLineAsync($"- ports: A={portA}, B={portB}");
        await sink.WriteLineAsync($"- N: {N} per direction per row");
        await sink.WriteLineAsync($"- payload: {PayloadBytes} B");
        await sink.WriteLineAsync();
        await sink.WriteLineAsync("| Mode | Name | TXDELAY ms | A→B succ | A→B failure-gap pattern | B→A succ | B→A failure-gap pattern |");
        await sink.WriteLineAsync("|---:|---|---:|---:|---|---:|---|");

        byte[] modes = { 12, 13, 14 };
        byte[] txDelays = { 50, 100 };

        foreach (var mode in modes)
        {
            var info = NinoTncCatalog.ByMode[mode];
            foreach (var txd in txDelays)
            {
                Console.WriteLine($"\n=== mode {mode} ({info.Name}), TXDELAY={txd} ===");
                await using var a = NinoTncSerialPort.Open(portA);
                await using var b = NinoTncSerialPort.Open(portB);
                await a.SetModeAsync(mode);
                await b.SetModeAsync(mode);
                await a.SetTxDelayAsync(txd);
                await b.SetTxDelayAsync(txd);
                await Task.Delay(800);

                var (abOk, abFails) = await Direction(a, b, "A→B", mode);
                var (baOk, baFails) = await Direction(b, a, "B→A", mode);

                await sink.WriteLineAsync(
                    $"| {mode} | {info.Name} | {txd * 10} | {abOk}/{N} | {Pattern(abFails)} | {baOk}/{N} | {Pattern(baFails)} |");
            }
        }

        await sink.WriteLineAsync();
        await sink.WriteLineAsync($"_Report: `{reportPath}`._");
        Console.WriteLine();
        Console.WriteLine($"Report: {reportPath}");
        return 0;
    }

    private static async Task<(int ok, List<int> failureIndexes)> Direction(
        NinoTncSerialPort tx, NinoTncSerialPort rx, string label, byte mode)
    {
        int ok = 0;
        var failures = new List<int>();
        for (int i = 0; i < N; i++)
        {
            var info = new byte[PayloadBytes];
            var prefix = Encoding.ASCII.GetBytes($"M{mode}-{label}-{i:00}");
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
            }
            else
            {
                failures.Add(i);
            }

            if ((i + 1) % 10 == 0)
            {
                Console.WriteLine($"  {label}: {i + 1}/{N}, ok {ok}, fails {failures.Count}");
            }
        }
        return (ok, failures);
    }

    private static async Task<double> OneRoundTrip(
        NinoTncSerialPort tx, NinoTncSerialPort rx, Ax25Frame ax25, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<double>();
        var sw = Stopwatch.StartNew();
        EventHandler<KissFrame> handler = (_, frame) =>
        {
            if (frame.Command != KissCommand.Data)
            {
                return;
            }

            if (!Ax25Frame.TryParse(frame.Payload, out var parsed))
            {
                return;
            }

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
            try { return await tcs.Task.WaitAsync(timeout); }
            catch (TimeoutException) { return -1; }
        }
        finally { rx.FrameReceived -= handler; }
    }

    /// <summary>
    /// Compact pattern description: "-" for none, "first" if only index 0,
    /// "0 + run from N" if frame 0 plus a contiguous tail, "scattered (n)"
    /// otherwise.
    /// </summary>
    private static string Pattern(List<int> failures)
    {
        if (failures.Count == 0)
        {
            return "—";
        }

        if (failures.Count == 1 && failures[0] == 0)
        {
            return "first only";
        }

        if (failures.Count == 1)
        {
            return $"index {failures[0]}";
        }

        // Try to recognise "frame 0 dropped, then contiguous run from some N".
        bool first = failures[0] == 0;
        int runStart = first ? failures[1] : failures[0];
        int expected = runStart;
        bool isRun = true;
        for (int i = first ? 1 : 0; i < failures.Count; i++)
        {
            if (failures[i] != expected) { isRun = false; break; }
            expected++;
        }
        if (isRun)
        {
            return first
                ? $"0 + contiguous {runStart}..{failures[^1]} ({failures.Count - 1} frames)"
                : $"contiguous {runStart}..{failures[^1]} ({failures.Count} frames)";
        }
        return $"scattered ({failures.Count}): [{string.Join(", ", failures)}]";
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
