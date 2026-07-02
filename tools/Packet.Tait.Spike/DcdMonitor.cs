using Packet.Radio.Tait;

namespace Packet.Tait.Spike;

internal static class DcdMonitor
{
    public static async Task<int> Run(string port)
    {
        await using var radio = TaitCcdiRadio.Open(port);
        var started = DateTimeOffset.UtcNow;

        radio.CarrierSenseChanged += (_, e) =>
            Console.WriteLine($"{(e.At - started).TotalSeconds,8:0.000}s  DCD {(e.Busy ? "BUSY" : "idle")}");
        radio.TransmitterStateChanged += (_, e) =>
            Console.WriteLine($"{(e.At - started).TotalSeconds,8:0.000}s  PTT {(e.Transmitting ? "keyed" : "unkeyed")}");
        radio.ProgressReceived += (_, e) =>
            Console.WriteLine($"{(DateTimeOffset.UtcNow - started).TotalSeconds,8:0.000}s  progress {e.Type} {e.Para}");

        await radio.SetProgressMessagesAsync(true);
        Console.WriteLine($"monitoring {port} — ctrl-c to stop");

        while (true)
        {
            float rssi = await radio.ReadRssiDbmAsync();
            Console.WriteLine(
                $"{(DateTimeOffset.UtcNow - started).TotalSeconds,8:0.000}s  rssi {rssi,7:0.0} dBm  busy={radio.ChannelBusy?.ToString() ?? "?"}");
            await Task.Delay(1000);
        }
    }
}
