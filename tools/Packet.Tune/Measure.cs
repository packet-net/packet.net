using System.Globalization;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;

namespace Packet.Tune;

/// <summary>
/// <c>measure</c>: one-shot level survey. From the NinoTNC: GETVER and a
/// GETRSSI baseline (n=5 — the TNC's RX-audio RMS level in dB). With a
/// CCDI port: the attached Tait radio's averaged + raw RSSI, which on an
/// idle channel is the RF noise floor.
/// </summary>
internal static class Measure
{
    private const int BaselineSamples = 5;

    public static async Task<int> Run(string tncPort, string? ccdiPort)
    {
        Console.WriteLine($"measure — TNC {tncPort}" + (ccdiPort is null ? string.Empty : $", radio CCDI {ccdiPort}"));

        await using var tnc = NinoTncSerialPort.Open(tncPort);

        string version = await tnc.GetVersionAsync();
        Console.WriteLine($"  GETVER: firmware {version}");

        var levels = new List<float>();
        for (int i = 0; i < BaselineSamples; i++)
        {
            float level = await tnc.GetRssiAsync();
            levels.Add(level);
            Console.WriteLine($"  GETRSSI #{i + 1}: {Fmt(level)} dB");
            await Task.Delay(200);
        }
        Console.WriteLine($"  RX-audio baseline (n={BaselineSamples}): median {Fmt(Median(levels))} dB " +
                          $"(min {Fmt(levels.Min())} / max {Fmt(levels.Max())})");

        if (ccdiPort is not null)
        {
            Console.WriteLine();
            await using var radio = TaitCcdiRadio.Open(ccdiPort);
            var identity = await radio.QueryIdentityAsync();
            Console.WriteLine($"  radio: {identity.ProductName} s/n {identity.SerialNumber} (CCDI {identity.CcdiVersion})");

            float averaged = await radio.ReadAveragedRssiDbmAsync();
            var raw = new List<float>();
            for (int i = 0; i < BaselineSamples; i++)
            {
                raw.Add(await radio.ReadRssiDbmAsync());
                await Task.Delay(100);
            }
            Console.WriteLine($"  RSSI averaged: {Fmt(averaged)} dBm");
            Console.WriteLine($"  RSSI raw (n={BaselineSamples}): median {Fmt(Median(raw))} dBm " +
                              $"(min {Fmt(raw.Min())} / max {Fmt(raw.Max())})");
            Console.WriteLine("  (on an idle channel the raw median is the squelched noise floor)");
        }

        return 0;
    }

    private static float Median(List<float> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    private static string Fmt(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
