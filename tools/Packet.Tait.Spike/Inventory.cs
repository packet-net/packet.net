using System.Text.Json;
using Packet.Radio.Tait;

namespace Packet.Tait.Spike;

internal static class Inventory
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> Run(string[] ports)
    {
        Directory.CreateDirectory("artifacts/radio-capabilities");
        foreach (string port in ports)
        {
            Console.WriteLine($"== {port} ==");
            await using var radio = TaitCcdiRadio.Open(port);

            var identity = await radio.QueryIdentityAsync();
            float rssi = await radio.ReadRssiDbmAsync();
            float rssiAvg = await radio.ReadAveragedRssiDbmAsync();
            var paTemp = await radio.ReadPaTemperatureAsync();
            int? fwd = await radio.ReadForwardPowerAsync();
            int? rev = await radio.ReadReversePowerAsync();

            var report = new
            {
                Port = port,
                identity.SerialNumber,
                identity.ProductName,
                identity.RuType,
                identity.RuModel,
                identity.RuTier,
                identity.CcdiVersion,
                identity.Versions,
                Telemetry = new
                {
                    RssiDbm = rssi,
                    AveragedRssiDbm = rssiAvg,
                    PaTemperatureCelsius = paTemp.Celsius,
                    PaTemperatureAdcMillivolts = paTemp.AdcMillivolts,
                    ForwardPowerMillivolts = fwd,
                    ReversePowerMillivolts = rev,
                },
                radio.Capabilities,
                CapabilityNames = radio.Capabilities.ToString(),
            };

            string json = JsonSerializer.Serialize(report, JsonOptions);
            Console.WriteLine(json);
            string path = $"artifacts/radio-capabilities/{identity.SerialNumber}.json";
            await File.WriteAllTextAsync(path, json);
            Console.WriteLine($"wrote {path}");
        }
        return 0;
    }
}
