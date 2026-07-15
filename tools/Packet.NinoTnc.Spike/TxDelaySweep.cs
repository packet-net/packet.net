using System.Diagnostics;
using System.Text;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Empirically determines the minimum workable TXDELAY for every NinoTNC mode, using two
/// TNCs wired audio-to-audio. No radios, no soundmodem — this measures the TNC itself, so
/// the answer is a property of the hardware rather than of anything we built.
/// </summary>
/// <remarks>
/// <para>Three things the firmware does that this has to work around, all learned the hard way:</para>
/// <list type="bullet">
///   <item><b>SETHW is fire-and-forget and silently fails.</b> Every mode change is read
///         back from GETALL's <c>BrdSwchMod</c> and retried; a mode that did not take looks
///         exactly like a mode that does not work (zero either way).</item>
///   <item><b>The frame after a TXDELAY change goes at the OLD setting.</b> Every probe
///         throws its first frame away. Measured directly: moving 300 → 50 ms, burst #0
///         still occupied 571 ms of air and #1 onward 330 ms.</item>
///   <item><b>The applied preamble is quantised to 16-bit words</b>, so the requested
///         TXDELAY and the delivered one differ — at 300 baud one word is already 53 ms.
///         GETALL register 0B reports what actually landed, and that is what gets recorded.</item>
/// </list>
/// </remarks>
internal static class TxDelaySweep
{
    private const int SettleAfterModeChangeMs = 1500;
    private const string Source = "Q0AAA";
    private const string Dest = "TEST";
    private const int PayloadBytes = 40;

    /// <summary>
    /// Result of one attempt: whether the TNC confirmed it finished keying, how long that
    /// took, and whether the far end decoded it. Keeping these apart is the whole point —
    /// a frame that never went out is not a decode failure.
    /// </summary>
    private readonly record struct Attempt(bool Transmitted, double KeyedMs, bool Decoded);

    internal static async Task<int> Run(string portA, string portB, string[] rest)
    {
        int searchFrames = Arg(rest, "--frames", 5);
        int confirmFrames = Arg(rest, "--confirm-frames", 10);
        byte ceiling = (byte)Arg(rest, "--ceiling", 30);   // 300 ms — assumed good
        byte[] modes = rest.FirstOrDefault(a => a.StartsWith("--modes=", StringComparison.Ordinal)) is { } m
            ? m["--modes=".Length..].Split(',').Select(byte.Parse).ToArray()
            : Enumerable.Range(0, 15).Select(i => (byte)i).ToArray();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);

        foreach ((string label, NinoTncSerialPort tnc) in new[] { ("A", a), ("B", b) })
        {
            NinoTncStatusFrame s = await a.GetAllAsync(TimeSpan.FromSeconds(3));
            _ = s;
            NinoTncStatusFrame status = await tnc.GetAllAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine(
                $"TNC {label} {tnc.PortName}: firmware {status.FirmwareVersionRaw}, " +
                $"DIP {status.DipSwitchesBinary} ({(status.IsSoftwareControlMode == true ? "software control" : "NOT software control")})");
            if (status.FirmwareVersion?.ToString() is not "3.44" and not "4.44")
            {
                Console.Error.WriteLine($"  !! expected v44 firmware, found {status.FirmwareVersionRaw}");
                return 2;
            }

            if (status.IsSoftwareControlMode != true)
            {
                Console.Error.WriteLine("  !! DIPs are not 1111; mode and TXDELAY are not under software control");
                return 2;
            }
        }

        // p-persistence exists so stations do not collide; with exactly two TNCs that never
        // transmit at once, its dice roll only adds random latency and irregular keying.
        // Pin both to transmit-as-soon-as-clear so timing is deterministic. Rig-only — this
        // must never become a shipped default.
        foreach (NinoTncSerialPort tnc in new[] { a, b })
        {
            await tnc.SetPersistenceAsync(255);
            await tnc.SetSlotTimeAsync(0);
            await Task.Delay(150);
        }

        Console.WriteLine(
            $"\n{Source}>{Dest}, {PayloadBytes}-byte payloads, PERSIST 255 / SLOTTIME 0.\n" +
            $"Every frame is sent with ACKMODE, so \"transmitted\" and \"decoded\" are measured\n" +
            $"separately; each distinct applied preamble is walked with {confirmFrames} frames.\n");

        var results = new List<string>();
        foreach (byte mode in modes)
        {
            NinoTncMode? catalogued = NinoTncCatalog.TryGetByMode(mode);
            if (mode == 15 || catalogued is null)
            {
                continue;   // 15 is the "set from KISS" escape, not an operating mode
            }

            Console.WriteLine($"── mode {mode} — {catalogued.Value.Name} ({catalogued.Value.BitRateHz} bps)");
            if (!await SetModeVerified(a, mode) || !await SetModeVerified(b, mode))
            {
                Console.WriteLine("   SKIP: could not get both TNCs into this mode\n");
                results.Add($"| {mode} | {catalogued.Value.Name} | — | — | mode would not set |");
                continue;
            }

            var perDirection = new List<string>();
            foreach ((string dir, NinoTncSerialPort tx, NinoTncSerialPort rx) in
                     new[] { ("A→B", a, b), ("B→A", b, a) })
            {
                string outcome = await SweepDirection(
                    dir, tx, rx, mode, catalogued.Value.BitRateHz, ceiling, searchFrames, confirmFrames);
                perDirection.Add(outcome);
            }

            results.Add($"| {mode} | {catalogued.Value.Name} | {perDirection[0]} | {perDirection[1]} | |");
            Console.WriteLine();
        }

        Console.WriteLine("\n## Summary\n");
        Console.WriteLine("| mode | name | A→B min | B→A min | note |");
        Console.WriteLine("|---|---|---|---|---|");
        foreach (string r in results)
        {
            Console.WriteLine(r);
        }

        return 0;
    }

    /// <summary>
    /// Walks every distinct <em>applied</em> preamble the firmware will produce and reports
    /// the whole curve, rather than binary-searching for an edge.
    /// </summary>
    /// <remarks>
    /// Two reasons the obvious approach was wrong, both found by trying it:
    /// <list type="bullet">
    ///   <item>Requested TXDELAY is not the parameter — the firmware quantises to 16-bit
    ///         words, so at 300 baud every request from 0 to 50 ms delivers the same single
    ///         word. Scanning in 10 ms steps re-measures identical signals and invents
    ///         boundaries between them.</item>
    ///   <item>A binary search assumes monotonicity that a real link does not have. The
    ///         first run's search was repeatedly fooled: it would settle on 160 ms while the
    ///         150 ms below it scored better. Walking every point costs more time and
    ///         assumes nothing.</item>
    /// </list>
    /// The floor is judged against the link's own ceiling, not against perfection: a
    /// direction that tops out at 8/10 because of level is still entitled to a minimum
    /// preamble, and demanding 10/10 there would report "no answer" for a link that works.
    /// </remarks>
    private static async Task<string> SweepDirection(
        string dir, NinoTncSerialPort tx, NinoTncSerialPort rx, byte mode, int bitRate,
        byte ceiling, int searchFrames, int confirmFrames)
    {
        // Map requested TXDELAY -> applied preamble words, and keep the lowest request
        // that produces each distinct word count.
        var distinct = new List<(byte Units, long Words)>();
        for (byte units = 0; units <= ceiling; units++)
        {
            long words = await AppliedWords(tx, rx, units, bitRate);
            if (words >= 0 && (distinct.Count == 0 || distinct[^1].Words != words))
            {
                distinct.Add((units, words));
            }
        }

        if (distinct.Count == 0)
        {
            return "no preamble readback";
        }

        Console.WriteLine(
            $"   {dir}: {distinct.Count} distinct preambles across TXDELAY 0..{ceiling * 10} ms " +
            $"({string.Join(", ", distinct.Select(d => $"{d.Words}w"))})");

        // Ceiling first: what can this direction do with the most preamble available?
        var (topOk, topDead, topKeyed, _) = await Probe(tx, rx, distinct[^1].Units, confirmFrames, bitRate);
        if (topDead > 0)
        {
            Console.WriteLine($"        !! {topDead}/{confirmFrames} never transmitted (no ACKMODE echo) — CSMA/DCD, not decode");
        }

        if (topOk == 0)
        {
            Console.WriteLine($"        NO LINK at {distinct[^1].Words} words — nothing to measure");
            return "no link";
        }

        Console.WriteLine(
            $"        ceiling: {distinct[^1].Words,3} words ({PreambleMs(distinct[^1].Words, bitRate),4:F0} ms) → {topOk}/{confirmFrames}"
            + $", keyed {topKeyed:F0} ms");

        var curve = new List<(long Words, double Ms, int Ok)>();
        foreach ((byte units, long words) in distinct)
        {
            var (ok, dead, meanKeyed, _) = await Probe(tx, rx, units, confirmFrames, bitRate);
            curve.Add((words, PreambleMs(words, bitRate), ok));
            Console.WriteLine(
                $"        {words,3} words ({PreambleMs(words, bitRate),4:F0} ms, TXDELAY {units * 10,3} ms) → {ok}/{confirmFrames}" +
                $", keyed {meanKeyed,6:F0} ms{(dead > 0 ? $", {dead} NOT TX" : "")}{(ok >= topOk ? "  ✓" : "")}");
        }

        // Lowest preamble matching the ceiling, and everything above it must hold too —
        // an isolated pass under a run of failures is a fluke, not a floor.
        (long Words, double Ms, int Ok)? floor = null;
        for (int i = 0; i < curve.Count; i++)
        {
            if (curve[i].Ok >= topOk && curve.Skip(i).All(c => c.Ok >= topOk))
            {
                floor = curve[i];
                break;
            }
        }

        if (floor is null)
        {
            return $"unstable (ceiling {topOk}/{confirmFrames}, no consistent floor)";
        }

        string ceilingNote = topOk < confirmFrames ? $", link ceiling {topOk}/{confirmFrames}" : "";
        if (floor.Value.Words == curve[0].Words)
        {
            return $"**{floor.Value.Ms:F0} ms** ({floor.Value.Words}w — firmware floor, lowest available{ceilingNote})";
        }

        var below = curve[curve.FindIndex(c => c.Words == floor.Value.Words) - 1];
        return $"**{floor.Value.Ms:F0} ms** ({floor.Value.Words}w; {below.Ok}/{confirmFrames} at {below.Ms:F0} ms{ceilingNote})";
    }

    /// <summary>
    /// Sets a TXDELAY and reports what preamble the firmware actually applied.
    /// </summary>
    /// <remarks>
    /// A frame must go out before reading: register 0B does not update on the SETHW-style
    /// command, only once a transmission has used the new value — the same reason the frame
    /// after a TXDELAY change goes at the old setting. Reading it straight after the command
    /// returns the *previous* value, which silently reported "every TXDELAY from 0 to 300 ms
    /// applies 5 words" and would have produced a table of confident nonsense.
    /// </remarks>
    private static async Task<long> AppliedWords(
        NinoTncSerialPort tx, NinoTncSerialPort rx, byte units, int bitRate)
    {
        await tx.SetTxDelayAsync(units);
        await Task.Delay(250);
        _ = await SendAndWait(tx, rx, units, 99, bitRate);   // makes the new value take
        try
        {
            NinoTncStatusFrame status = await tx.GetAllAsync(TimeSpan.FromSeconds(3));
            return status.PreambleWordCount ?? -1;
        }
        catch (TimeoutException)
        {
            return -1;
        }
    }

    /// <summary>Sets TXDELAY, throws the next frame away (it goes at the old setting), then
    /// counts how many of <paramref name="frames"/> the far end reports.</summary>
    /// <summary>Sets TXDELAY, throws the next frame away (it goes at the old setting), then
    /// counts how many of <paramref name="frames"/> the far end decodes. Frames the TNC
    /// never transmitted are reported separately — they are not decode failures.</summary>
    private static async Task<(int Received, int NotTransmitted, double MeanKeyedMs, long PreambleWords)> Probe(
        NinoTncSerialPort tx, NinoTncSerialPort rx, byte units, int frames, int bitRate)
    {
        await tx.SetTxDelayAsync(units);
        await Task.Delay(250);

        _ = await SendAndWait(tx, rx, units, 0, bitRate);       // throwaway — old TXDELAY

        long words = -1;
        try
        {
            NinoTncStatusFrame status = await tx.GetAllAsync(TimeSpan.FromSeconds(3));
            words = status.PreambleWordCount ?? -1;
        }
        catch (TimeoutException)
        {
        }

        int ok = 0, dead = 0;
        var keyed = new List<double>();
        for (int i = 1; i <= frames; i++)
        {
            Attempt a = await SendAndWait(tx, rx, units, i, bitRate);
            if (!a.Transmitted)
            {
                dead++;
                continue;
            }

            keyed.Add(a.KeyedMs);
            if (a.Decoded)
            {
                ok++;
            }
        }

        return (ok, dead, keyed.Count > 0 ? keyed.Average() : 0, words);
    }

    /// <summary>
    /// Sends one frame with ACKMODE and reports transmission and reception separately.
    /// </summary>
    /// <remarks>
    /// This has to use <see cref="NinoTncSerialPort.SendFrameWithAckAsync"/>, not the
    /// fire-and-forget send: that one returns when the bytes reach the serial stream, which
    /// says nothing about the air. Waiting on the far end alone conflates "the TNC has not
    /// transmitted yet" (CSMA backoff, DCD hold) with "the TNC transmitted and the far end
    /// could not decode it" — and only the second is a TXDELAY measurement. The ACKMODE echo
    /// separates them, and times the keying as a bonus.
    /// </remarks>
    private static async Task<Attempt> SendAndWait(
        NinoTncSerialPort tx, NinoTncSerialPort rx, byte units, int seq, int bitRate)
    {
        string tag = $"TXD{units:D2}-{seq:D2}";
        var seen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrame(object? _, KissFrame f)
        {
            if (f.Command == KissCommand.Data && Encoding.ASCII.GetString(f.Payload).Contains(tag, StringComparison.Ordinal))
            {
                seen.TrySetResult(true);
            }
        }

        rx.FrameReceived += OnFrame;
        try
        {
            double keyed;
            var clock = Stopwatch.StartNew();
            try
            {
                // Generous: this covers the TNC's own CSMA as well as the keying itself.
                _ = await tx.SendFrameWithAckAsync(BuildFrame(tag), TimeSpan.FromSeconds(20));
                keyed = clock.Elapsed.TotalMilliseconds;
            }
            catch (TimeoutException)
            {
                return new Attempt(Transmitted: false, KeyedMs: 0, Decoded: false);
            }

            // The frame is provably on the air now, so the far end has either decoded it or
            // not — a short window, and no ambiguity about what a timeout means.
            using var cts = new CancellationTokenSource(1500);
            await using (cts.Token.Register(() => seen.TrySetResult(false)))
            {
                return new Attempt(true, keyed, await seen.Task);
            }
        }
        finally
        {
            rx.FrameReceived -= OnFrame;
            await Task.Delay(120);
        }
    }

    private static byte[] BuildFrame(string tag)
    {
        var payload = new byte[PayloadBytes];
        byte[] t = Encoding.ASCII.GetBytes($"PDN {tag} ");
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = i < t.Length ? t[i] : (byte)('A' + ((i - t.Length) % 26));
        }

        static byte[] Addr(string call, bool last, int c)
        {
            var b = new byte[7];
            for (int i = 0; i < 6; i++)
            {
                b[i] = (byte)((i < call.Length ? call[i] : ' ') << 1);
            }

            b[6] = (byte)((c << 7) | 0x60 | (last ? 1 : 0));
            return b;
        }

        return [.. Addr(Dest, false, 1), .. Addr(Source, true, 0), 0x03, 0xF0, .. payload];
    }

    /// <summary>SETHW, settle, then read the running mode back. Retries — SETHW is
    /// unacknowledged and does silently fail.</summary>
    private static async Task<bool> SetModeVerified(NinoTncSerialPort tnc, byte mode)
    {
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            await tnc.SetModeAsync(mode, persistToFlash: false);
            await Task.Delay(SettleAfterModeChangeMs);
            try
            {
                NinoTncStatusFrame status = await tnc.GetAllAsync(TimeSpan.FromSeconds(3));
                if (status.RunningMode?.Mode == mode)
                {
                    return true;
                }

                Console.WriteLine(
                    $"   SETHW attempt {attempt}: asked for {mode}, running " +
                    $"{(status.RunningMode?.Mode.ToString() ?? $"unknown (byte 0x{status.FirmwareModeByte:X2})")} — retrying");
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"   SETHW attempt {attempt}: no GETALL reply — retrying");
            }
        }

        return false;
    }

    private static double PreambleMs(long words, int bitRate) =>
        words < 0 ? 0 : words * 16.0 * 1000 / bitRate;

    private static int Arg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : fallback;
    }
}
