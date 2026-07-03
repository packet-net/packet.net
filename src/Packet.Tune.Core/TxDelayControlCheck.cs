using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune.Core;

/// <summary>The result of a commanded-vs-measured TXDELAY check.</summary>
/// <param name="UnderSoftwareControl">True = the measured TXDELAY tracked the
/// commanded values (pot at minimum, KISS in charge); false = pot override
/// suspected.</param>
/// <param name="UsedRegisterPath">True = register 0B (preamble words) moved
/// and was used; false = the ACKMODE echo-timing fallback was used (firmware
/// 3.41/3.44 never increment 0B for host traffic).</param>
/// <param name="Summary">One-line human verdict.</param>
public sealed record TxDelayControlResult(bool UnderSoftwareControl, bool UsedRegisterPath, string Summary);

/// <summary>
/// Prove a NinoTNC's TXDELAY is under software control by measuring the
/// effective TXDELAY at two commanded values (200 ms and 500 ms) and checking
/// the measurement tracks the command. Shared by <c>packet-tune
/// verify-control</c> and the capability doctor.
/// </summary>
/// <remarks>
/// <para>Two measurement paths, in preference order:</para>
/// <list type="number">
///   <item>the GETALL delta of register 0B (preamble words; seconds = words ×
///     16 ÷ baud) across the measured frame — but firmware 3.41 and 3.44
///     never increment this register for host traffic (bench-verified
///     2026-07-02), so on today's firmware this path never engages;</item>
///   <item>fallback: ACKMODE TX-completion timing — queue→echo elapsed is
///     TXDELAY + airtime + a constant, so the difference between the two
///     test points isolates the TXDELAY change.</item>
/// </list>
/// <para>The NinoTNC applies a changed TXDELAY on the SECOND frame after the
/// change, so each test point transmits a settling frame first and discards
/// it. The echo-timing path measures three frames per test point and keeps
/// the MINIMUM elapsed — the TNC's CSMA adds a random per-transmission
/// deferral that a single sample can't distinguish from a pot offset
/// (bench-observed: up to ~0.5 s of jitter on a quiet channel).</para>
/// <para><b>Run this with the TNC in a known-good mode</b>: a fresh firmware
/// flash boots mode 0 (9600 GFSK — dead on narrow channels), where the
/// timing measurement produces a false "pot override" verdict; callers
/// should SETHW a sane mode (6 is the default in the CLI) before invoking.</para>
/// </remarks>
public static class TxDelayControlCheck
{
    /// <summary>Commanded TXDELAY test points, in KISS 10 ms units.</summary>
    private static readonly byte[] TestPointsTenMs = [20, 50];

    /// <summary>Measured frames per test point — the minimum elapsed wins
    /// (strips the TNC's random CSMA deferral).</summary>
    private const int SamplesPerPoint = 3;

    private sealed record TestPoint(int CommandedMs, double? PreambleS, long? Words, TimeSpan EchoElapsed);

    /// <summary>
    /// Run the check. The TNC transmits four short UI frames (two per test
    /// point) from <paramref name="source"/>.
    /// </summary>
    /// <param name="tnc">An open TNC connection.</param>
    /// <param name="source">Source callsign for the probe frames.</param>
    /// <param name="bitRateHz">The running mode's bit rate (for the register-path arithmetic).</param>
    /// <param name="log">Optional progress sink (one line per measurement).</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<TxDelayControlResult> RunAsync(
        NinoTncSerialPort tnc,
        Callsign source,
        int bitRateHz,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tnc);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitRateHz);

        // Pin the CSMA parameters for the measurement: with the default
        // p-persistence the TNC defers each transmission a random number of
        // ~100 ms slots, which swamps the 300 ms TXDELAY step under test
        // (bench-observed as false "pot override" verdicts). p=255 +
        // slottime 0 keys deterministically on a quiet channel.
        await tnc.SetPersistenceAsync(255, cancellationToken).ConfigureAwait(false);
        await tnc.SetSlotTimeAsync(0, cancellationToken).ConfigureAwait(false);
        try
        {
            return await MeasureAsync(tnc, source, bitRateHz, log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Restore polite channel-access defaults (KISS has no read-back,
            // so "restore" means the conventional p=63 / slottime 10).
            try
            {
                await tnc.SetPersistenceAsync(63, CancellationToken.None).ConfigureAwait(false);
                await tnc.SetSlotTimeAsync(10, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private static async Task<TxDelayControlResult> MeasureAsync(
        NinoTncSerialPort tnc,
        Callsign source,
        int bitRateHz,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var points = new List<TestPoint>();
        foreach (byte tenMs in TestPointsTenMs)
        {
            int commandedMs = tenMs * 10;
            log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"set TXDELAY {tenMs} ({commandedMs} ms)"));
            await tnc.SetTxDelayAsync(tenMs, cancellationToken).ConfigureAwait(false);

            // The NinoTNC applies a changed TXDELAY on the SECOND frame after
            // the change — transmit and discard a settling frame first.
            // ACKMODE sends resolve when the TNC finishes keying, so the
            // GETALL snapshots cannot land mid-transmission.
            await tnc.SendFrameWithAckAsync(ProbeFrame(source, 1), TimeSpan.FromSeconds(20), null, cancellationToken)
                .ConfigureAwait(false);

            var before = await tnc.GetAllAsync(null, cancellationToken).ConfigureAwait(false);
            TimeSpan best = TimeSpan.MaxValue;
            for (int sample = 0; sample < SamplesPerPoint; sample++)
            {
                // Let the transmitter unkey between samples: back-to-back
                // sends chain into ONE keying train with a single preamble
                // (bench-observed: chained frames echo in a constant ~430 ms
                // regardless of TXDELAY), which would measure nothing.
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);

                var completion = await tnc
                    .SendFrameWithAckAsync(ProbeFrame(source, 2 + sample), TimeSpan.FromSeconds(20), null, cancellationToken)
                    .ConfigureAwait(false);
                if (completion.Elapsed < best)
                {
                    best = completion.Elapsed;
                }
                log?.Invoke(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  sample {sample + 1}/{SamplesPerPoint}: ACKMODE echo after {completion.Elapsed.TotalMilliseconds:0} ms"));
            }
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var after = await tnc.GetAllAsync(null, cancellationToken).ConfigureAwait(false);

            var delta = NinoTncStatusDelta.Between(before, after);
            long? wordsPerFrame = delta.PreambleWordCount is { } w ? w / SamplesPerPoint : null;
            double? preambleS = wordsPerFrame is { } wpf ? wpf * 16.0 / bitRateHz : null;
            points.Add(new TestPoint(commandedMs, preambleS, wordsPerFrame, best));
            log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"  reg-0B delta {delta.PreambleWordCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} words over {SamplesPerPoint} frames " +
                $"({FmtS(preambleS)}/frame), best echo {best.TotalMilliseconds:0} ms"));
        }

        return Verdict(points);
    }

    private static TxDelayControlResult Verdict(List<TestPoint> points)
    {
        double commandedDiff = (points[1].CommandedMs - points[0].CommandedMs) / 1000.0;

        // Preferred path: register 0B moved — read the preamble directly.
        if (points.All(p => p.Words is > 0))
        {
            double m1 = points[0].PreambleS!.Value;
            double m2 = points[1].PreambleS!.Value;
            bool tracks = Math.Abs((m2 - m1) - commandedDiff) <= commandedDiff * 0.4 &&
                          Math.Abs(m1 - (points[0].CommandedMs / 1000.0)) <= 0.15;
            string summary = tracks
                ? "TXDELAY under software control (pot at minimum) — measured preamble " +
                  $"{FmtS(m1)} / {FmtS(m2)} tracks commanded 0.200 s / 0.500 s"
                : "POT OVERRIDE suspected — measured preamble " +
                  $"{FmtS(m1)} / {FmtS(m2)} does not track commanded 0.200 s / 0.500 s " +
                  "(turn the TXDELAY pot fully anticlockwise for software control)";
            return new TxDelayControlResult(tracks, UsedRegisterPath: true, summary);
        }

        // Fallback path: firmware 3.41/3.44 never increment reg 0B — use the
        // ACKMODE echo timing. The elapsed is TXDELAY + airtime + constant,
        // so the two points' difference isolates the TXDELAY step.
        double e1 = points[0].EchoElapsed.TotalSeconds;
        double e2 = points[1].EchoElapsed.TotalSeconds;
        double measuredDiff = e2 - e1;
        bool timingTracks = Math.Abs(measuredDiff - commandedDiff) <= 0.15;
        string timingSummary = timingTracks
            ? "TXDELAY under software control (pot at minimum) — echo timing moved " +
              $"{FmtS(measuredDiff)} for a {FmtS(commandedDiff)} commanded step ({FmtS(e1)} → {FmtS(e2)})"
            : "POT OVERRIDE suspected — echo timing moved " +
              $"{FmtS(measuredDiff)} for a {FmtS(commandedDiff)} commanded step ({FmtS(e1)} → {FmtS(e2)}) " +
              "(turn the TXDELAY pot fully anticlockwise for software control)";
        return new TxDelayControlResult(timingTracks, UsedRegisterPath: false, timingSummary);
    }

    /// <summary>A ~60-byte UI probe frame (16 header + 44 info bytes).</summary>
    private static byte[] ProbeFrame(Callsign source, int n)
    {
        string text = string.Create(CultureInfo.InvariantCulture, $"Packet.Tune TXDELAY probe {n} ");
        var ui = Ax25Frame.Ui(
            destination: new Callsign("TEST"),
            source: source,
            info: Encoding.ASCII.GetBytes(text.PadRight(44, '.')));
        return ui.ToBytes();
    }

    private static string FmtS(double? seconds) =>
        seconds is { } s ? s.ToString("0.000", CultureInfo.InvariantCulture) + " s" : "n/a";
}
