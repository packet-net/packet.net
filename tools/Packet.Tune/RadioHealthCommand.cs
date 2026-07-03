using System.Globalization;
using Packet.Radio.Tait;

namespace Packet.Tune;

/// <summary>
/// <c>radio-health</c>: run a <see cref="TaitRadioHealthMonitor"/> against one Tait radio's
/// CCDI port for a while and print the samples as a live table — averaged RSSI (CCTM 063),
/// PA temperature (CCTM 047), and the raw + idle-offset-corrected forward/reverse power
/// detectors (CCTM 318/319, transmit samples only) — followed by the rolling min/median/max
/// summary. The fwd/rev figures are trends, never VSWR (see the monitor's docs).
/// <c>--key-once [s]</c> keys the transmitter ONCE via CCDI FUNCTION 9 (at the channel's
/// programmed power) for at most 3 s to capture a TX sample — mind your dummy load/attenuator
/// ratings before using it.
/// </summary>
internal static class RadioHealthCommand
{
    private const double MaxKeySeconds = 3;

    public static async Task<int> Run(string ccdiPort, string[] rest)
    {
        double intervalSeconds = 10;
        double durationSeconds = 60;
        double? keyOnceSeconds = null;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--interval" when i + 1 < rest.Length &&
                        double.TryParse(rest[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double iv):
                    intervalSeconds = iv;
                    i++;
                    break;
                case "--duration" when i + 1 < rest.Length &&
                        double.TryParse(rest[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double du):
                    durationSeconds = du;
                    i++;
                    break;
                case "--key-once":
                    keyOnceSeconds = 2;
                    if (i + 1 < rest.Length &&
                        double.TryParse(rest[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ks))
                    {
                        keyOnceSeconds = ks;
                        i++;
                    }
                    break;
                default:
                    Console.WriteLine($"radio-health: unrecognised argument '{rest[i]}'");
                    return 2;
            }
        }
        if (intervalSeconds <= 0 || durationSeconds <= 0)
        {
            Console.WriteLine("radio-health: --interval and --duration must be positive");
            return 2;
        }
        if (keyOnceSeconds is { } k && (k <= 0 || k > MaxKeySeconds))
        {
            Console.WriteLine($"radio-health: --key-once must be 0–{MaxKeySeconds} s (one brief keying, nothing more)");
            return 2;
        }

        await using var radio = TaitCcdiRadio.Open(ccdiPort);
        var identity = await radio.QueryIdentityAsync();
        Console.WriteLine($"radio-health — {identity.ProductName} s/n {identity.SerialNumber} on {ccdiPort}");
        Console.WriteLine($"  interval {intervalSeconds:0.#} s, duration {durationSeconds:0.#} s" +
                          (keyOnceSeconds is { } ko ? $", one {ko:0.#} s keyed sample" : string.Empty));
        Console.WriteLine("  fwd/rev are raw detector mV (√P-scaled, uncalibrated) — trend them, never read as VSWR");
        await radio.SetProgressMessagesAsync(true); // PTT edges gate the TX sampling

        await using var monitor = new TaitRadioHealthMonitor(radio, new TaitRadioHealthMonitorOptions
        {
            SampleInterval = TimeSpan.FromSeconds(intervalSeconds),
        });
        Console.WriteLine();
        Console.WriteLine("  time      tx   rssi dBm   pa °C   pa mV   fwd mV   rev mV   fwd-idle   rev-idle   rev/fwd");
        monitor.SampleTaken += (_, s) => Console.WriteLine(
            $"  {s.At.ToLocalTime():HH:mm:ss}  {(s.Transmitting ? "TX" : "--"),2}  " +
            $"{Cell(s.RssiDbm, "0.0"),9}  {Cell(s.PaTemperatureCelsius),6}  {Cell(s.PaDetectorMillivolts),6}  " +
            $"{Cell(s.ForwardPowerMillivolts),7}  {Cell(s.ReversePowerMillivolts),7}  " +
            $"{Cell(s.TxForwardOverIdleMillivolts),9}  {Cell(s.TxReverseOverIdleMillivolts),9}  " +
            $"{Cell(s.TxReverseForwardRatio, "0.000"),8}");

        var run = Task.Delay(TimeSpan.FromSeconds(durationSeconds));
        if (keyOnceSeconds is { } keySeconds)
        {
            // Let at least one idle sample land first so the keyed sample has offsets to subtract.
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(intervalSeconds, durationSeconds) / 2));
            Console.WriteLine($"  [keying transmitter for {keySeconds:0.#} s — channel-programmed power]");
            await radio.SetTransmitterAsync(true);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(keySeconds));
            }
            finally
            {
                await radio.SetTransmitterAsync(false);
                Console.WriteLine("  [transmitter unkeyed]");
            }
        }
        await run;

        var summary = monitor.Summarize();
        Console.WriteLine();
        Console.WriteLine($"  summary — {summary.SampleCount} samples " +
                          $"({summary.TransmitSampleCount} keyed), {summary.From:HH:mm:ss}–{summary.To:HH:mm:ss}");
        Console.WriteLine("  metric               min      median     max      n");
        PrintStat("rssi dBm", summary.RssiDbm, "0.0");
        PrintStat("pa °C", summary.PaTemperatureCelsius, "0");
        PrintStat("tx fwd-idle mV", summary.TxForwardOverIdleMillivolts, "0");
        PrintStat("tx rev-idle mV", summary.TxReverseOverIdleMillivolts, "0");
        PrintStat("tx rev/fwd (trend)", summary.TxReverseForwardRatio, "0.000");
        return 0;
    }

    private static string Cell(double? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : "·";

    private static string Cell(float? value, string format) => Cell((double?)value, format);

    private static string Cell(int? value) => Cell(value, "0");

    private static void PrintStat(string label, TaitRadioHealthStat? stat, string format)
    {
        string row = stat is { } s
            ? $"{s.Min.ToString(format, CultureInfo.InvariantCulture),8}  " +
              $"{s.Median.ToString(format, CultureInfo.InvariantCulture),8}  " +
              $"{s.Max.ToString(format, CultureInfo.InvariantCulture),8}  {s.Count,4}"
            : "       —         —         —     0";
        Console.WriteLine($"  {label,-18} {row}");
    }
}
