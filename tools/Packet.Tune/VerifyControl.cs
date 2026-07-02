using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss.NinoTnc;

namespace Packet.Tune;

/// <summary>
/// <c>verify-control</c>: prove a NinoTNC is under software control.
/// Reads the DIP positions + config mode from GETALL, then measures the
/// effective TXDELAY at two commanded values (200 ms and 500 ms). If the
/// measured value tracks the commanded one, the TXDELAY pot is at minimum
/// and KISS TXDELAY is in charge; if it doesn't, the pot is overriding.
/// </summary>
/// <remarks>
/// Two measurement paths, in preference order:
/// <list type="number">
///   <item>the GETALL delta of register 0B (preamble word count; seconds =
///         words × 16 ÷ baud) across the measured frame — but firmware 3.41
///         never increments this register for host traffic (bench-verified
///         2026-07-02: flat at TXDELAY 200/500/1000 ms, ACKMODE and plain
///         sends, modes 6 and 7, both TX and RX side);</item>
///   <item>fallback: ACKMODE TX-completion timing — the queue→echo elapsed
///         is TXDELAY + airtime + a constant, so the difference between the
///         two test points isolates the TXDELAY change.</item>
/// </list>
/// Either way the NinoTNC applies a changed TXDELAY on the SECOND frame
/// after the change (bench-confirmed), so each test point transmits a
/// settling frame first and discards it.
/// </remarks>
internal static class VerifyControl
{
    /// <summary>Commanded TXDELAY test points, in KISS 10 ms units.</summary>
    private static readonly byte[] TestPointsTenMs = [20, 50];

    private sealed record TestPoint(int CommandedMs, double? PreambleS, long? Words, TimeSpan EchoElapsed);

    public static async Task<int> Run(string tncPort, string callsign)
    {
        Console.WriteLine($"verify-control — TNC {tncPort}, test frames from {callsign}");
        var source = Callsign.Parse(callsign);

        await using var tnc = NinoTncSerialPort.Open(tncPort);

        var status = await tnc.GetAllAsync();
        Console.WriteLine($"  firmware: {status.FirmwareVersionRaw ?? "?"}");
        Console.WriteLine($"  DIP positions (reg 04): {status.DipSwitchesBinary ?? "?"}" +
                          (status.IsSoftwareControlMode switch
                          {
                              true => "  → software control (1111)",
                              false => "  → HARDWARE mode pinned by DIPs",
                              null => "  (register missing)",
                          }));
        Console.WriteLine($"  config mode (reg 06): 0x{status.FirmwareModeByte?.ToString("X2", CultureInfo.InvariantCulture) ?? "??"}" +
                          $" → {status.RunningMode?.Name ?? "unknown to catalog"}");

        int bitRate = status.RunningMode is { BitRateHz: > 0 } mode ? mode.BitRateHz : 1200;
        if (status.RunningMode is not { BitRateHz: > 0 })
        {
            Console.WriteLine($"  ! mode bit rate unknown — assuming {bitRate} bit/s for the preamble arithmetic");
        }
        Console.WriteLine();

        var points = new List<TestPoint>();
        foreach (byte tenMs in TestPointsTenMs)
        {
            int commandedMs = tenMs * 10;
            Console.WriteLine($"  set TXDELAY {tenMs} ({commandedMs} ms)");
            await tnc.SetTxDelayAsync(tenMs);

            // The NinoTNC applies a changed TXDELAY on the SECOND frame after
            // the change — transmit and discard a settling frame first.
            // ACKMODE sends resolve when the TNC finishes keying, so the
            // GETALL snapshots cannot land mid-transmission.
            await tnc.SendFrameWithAckAsync(ProbeFrame(source, 1), TimeSpan.FromSeconds(20));

            var before = await tnc.GetAllAsync();
            var completion = await tnc.SendFrameWithAckAsync(ProbeFrame(source, 2), TimeSpan.FromSeconds(20));
            await Task.Delay(250);
            var after = await tnc.GetAllAsync();

            var delta = NinoTncStatusDelta.Between(before, after);
            points.Add(new TestPoint(commandedMs, delta.PreambleSeconds(bitRate), delta.PreambleWordCount, completion.Elapsed));
            Console.WriteLine(
                $"    frame 2: reg-0B delta {delta.PreambleWordCount?.ToString(CultureInfo.InvariantCulture) ?? "?"} words" +
                $" ({FmtS(delta.PreambleSeconds(bitRate))}), ACKMODE echo after {completion.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms");
        }

        Console.WriteLine();
        return Verdict(points);
    }

    private static int Verdict(List<TestPoint> points)
    {
        double commandedDiff = (points[1].CommandedMs - points[0].CommandedMs) / 1000.0;

        // Preferred path: register 0B moved — read the preamble directly.
        if (points.All(p => p.Words is > 0))
        {
            double m1 = points[0].PreambleS!.Value;
            double m2 = points[1].PreambleS!.Value;
            bool tracks = Math.Abs((m2 - m1) - commandedDiff) <= commandedDiff * 0.4 &&
                          Math.Abs(m1 - (points[0].CommandedMs / 1000.0)) <= 0.15;
            Console.WriteLine(tracks
                ? "verdict: TXDELAY under software control (pot at minimum) — measured preamble " +
                  $"{FmtS(m1)} / {FmtS(m2)} tracks commanded 0.200 s / 0.500 s"
                : "verdict: POT OVERRIDE suspected — measured preamble " +
                  $"{FmtS(m1)} / {FmtS(m2)} does not track commanded 0.200 s / 0.500 s " +
                  "(turn the TXDELAY pot fully anticlockwise for software control)");
            return tracks ? 0 : 1;
        }

        // Fallback path: firmware (3.41 at least) never increments reg 0B —
        // use the ACKMODE echo timing. The elapsed is TXDELAY + airtime +
        // constant, so the two points' difference isolates the TXDELAY step.
        Console.WriteLine("  (register 0B did not move — firmware 3.41 behaviour; using ACKMODE echo timing)");
        double e1 = points[0].EchoElapsed.TotalSeconds;
        double e2 = points[1].EchoElapsed.TotalSeconds;
        double measuredDiff = e2 - e1;
        bool timingTracks = Math.Abs(measuredDiff - commandedDiff) <= 0.15;
        Console.WriteLine(timingTracks
            ? "verdict: TXDELAY under software control (pot at minimum) — echo timing moved " +
              $"{FmtS(measuredDiff)} for a {FmtS(commandedDiff)} commanded step ({FmtS(e1)} → {FmtS(e2)})"
            : "verdict: POT OVERRIDE suspected — echo timing moved " +
              $"{FmtS(measuredDiff)} for a {FmtS(commandedDiff)} commanded step ({FmtS(e1)} → {FmtS(e2)}) " +
              "(turn the TXDELAY pot fully anticlockwise for software control)");
        return timingTracks ? 0 : 1;
    }

    /// <summary>A ~60-byte UI probe frame (16 header + 44 info bytes).</summary>
    private static byte[] ProbeFrame(Callsign source, int n)
    {
        string text = $"Packet.Tune TXDELAY probe {n} ";
        var ui = Ax25Frame.Ui(
            destination: new Callsign("TEST"),
            source: source,
            info: Encoding.ASCII.GetBytes(text.PadRight(44, '.')));
        return ui.ToBytes();
    }

    private static string FmtS(double? seconds) =>
        seconds is { } s ? s.ToString("0.000", CultureInfo.InvariantCulture) + " s" : "n/a";
}
