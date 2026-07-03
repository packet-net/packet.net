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
/// it. <b>Run this with the TNC in a known-good mode</b>: a fresh firmware
/// flash boots mode 0 (9600 GFSK — dead on narrow channels), where the
/// timing measurement produces a false "pot override" verdict; callers
/// should SETHW a sane mode (6 is the default in the CLI) before invoking.</para>
/// </remarks>
public static class TxDelayControlCheck
{
    /// <summary>Commanded TXDELAY test points, in KISS 10 ms units.</summary>
    private static readonly byte[] TestPointsTenMs = [20, 50];

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
            var completion = await tnc
                .SendFrameWithAckAsync(ProbeFrame(source, 2), TimeSpan.FromSeconds(20), null, cancellationToken)
                .ConfigureAwait(false);
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            var after = await tnc.GetAllAsync(null, cancellationToken).ConfigureAwait(false);

            var delta = NinoTncStatusDelta.Between(before, after);
            points.Add(new TestPoint(commandedMs, delta.PreambleSeconds(bitRateHz), delta.PreambleWordCount, completion.Elapsed));
            log?.Invoke(string.Create(
                CultureInfo.InvariantCulture,
                $"  frame 2: reg-0B delta {delta.PreambleWordCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} words " +
                $"({FmtS(delta.PreambleSeconds(bitRateHz))}), ACKMODE echo after {completion.Elapsed.TotalMilliseconds:0} ms"));
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
