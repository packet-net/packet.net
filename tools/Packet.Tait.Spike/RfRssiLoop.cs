using System.Globalization;
using System.Text;
using Packet.Ax25;
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio;
using Packet.Radio.Tait;

namespace Packet.Tait.Spike;

/// <summary>
/// The headline PoC: AX.25 UI frames from one NinoTNC through the Tait RF link to the other,
/// with every received frame stamped with RSSI/SNR from the receiving radio's CCDI channel via
/// <see cref="RssiTaggingTransport"/>, and hardware-DCD lead time measured against each frame.
/// </summary>
internal static class RfRssiLoop
{
    public static async Task<int> Run(string txTncPort, string rxTncPort, string rxRadioPort, int frameCount, byte mode)
    {
        Console.WriteLine($"rf-rssi — TX TNC {txTncPort} → RF → RX TNC {rxTncPort}, RX radio CCDI {rxRadioPort}, mode {mode}, {frameCount} frames");

        await using var radio = TaitCcdiRadio.Open(rxRadioPort);
        await using var txTnc = NinoTncSerialPort.Open(txTncPort);
        await using var rxTnc = NinoTncSerialPort.Open(rxTncPort);

        var dcdEdges = new List<CarrierSenseChange>();
        radio.CarrierSenseChanged += (_, e) =>
        {
            lock (dcdEdges)
            {
                dcdEdges.Add(e);
            }
            Console.WriteLine($"    dcd {(e.Busy ? "BUSY" : "idle ")} at {e.At:HH:mm:ss.fff}");
        };
        await radio.SetProgressMessagesAsync(true);

        Console.WriteLine($"radio: {(await radio.QueryIdentityAsync()).ProductName}, idle rssi {await radio.ReadRssiDbmAsync():0.0} dBm");

        await txTnc.SetModeAsync(mode);
        await rxTnc.SetModeAsync(mode);
        await txTnc.SetTxDelayAsync(30); // 300 ms — enough preamble for the link, short enough to time
        await Task.Delay(500);

        var options = new RssiTaggingOptions { BitRateHzProvider = () => rxTnc.CurrentBitRateHz };
        await using var tagged = new RssiTaggingTransport(rxTnc, radio, options);

        var received = new List<(Ax25Frame Frame, DateTimeOffset At, Packet.Ax25.Transport.RadioMetadata? Radio)>();
        using var rxCts = new CancellationTokenSource();
        var rxTask = Task.Run(async () =>
        {
            await foreach (var inbound in tagged.ReceiveAsync(rxCts.Token))
            {
                if (!Ax25Frame.TryParse(inbound.Ax25.Span, out var parsed))
                {
                    Console.WriteLine($"  rx UNPARSEABLE {inbound.Ax25.Length}B rssi={Fmt(inbound.Radio?.RssiDbm)}");
                    continue;
                }
                lock (received)
                {
                    received.Add((parsed, inbound.ReceivedAt, inbound.Radio));
                }
                var m = inbound.Radio;
                Console.WriteLine(
                    $"  rx #{received.Count} {inbound.Ax25.Length,3}B" +
                    $"  rssi={Fmt(m?.RssiDbm)} (min {Fmt(m?.RssiMinDbm)}/max {Fmt(m?.RssiMaxDbm)}, n={m?.RssiSampleCount})" +
                    $"  snr={Fmt(m?.SnrDb)}  floor={Fmt(m?.NoiseFloorDbm)}" +
                    $"  burst#{m?.BurstIndex}  air={FmtMs(m?.EstimatedAirtime)}  preData={FmtMs(m?.PreDataCarrier)}");
            }
        });

        // Let the sampler establish a noise floor before the first frame flies.
        await Task.Delay(1500);

        for (int i = 1; i <= frameCount; i++)
        {
            string text = $"RSSI PoC frame {i} " + new string('x', 20 * (i % 4));
            var ui = Ax25Frame.Ui(
                destination: new Callsign("TEST", 1),
                source: new Callsign("M0LTE", 15),
                info: Encoding.ASCII.GetBytes(text));
            Console.WriteLine($"  tx #{i} ({ui.ToBytes().Length}B)");
            await txTnc.SendFrameAsync(ui.ToBytes());
            await Task.Delay(2500);
        }

        Console.WriteLine("  tx train: 3 frames back-to-back (one carrier window expected)");
        for (int i = 0; i < 3; i++)
        {
            var ui = Ax25Frame.Ui(
                destination: new Callsign("TEST", 1),
                source: new Callsign("M0LTE", 15),
                info: Encoding.ASCII.GetBytes($"train frame {i}"));
            await txTnc.SendFrameAsync(ui.ToBytes());
        }
        await Task.Delay(5000);

        await Task.Delay(2000);
        await rxCts.CancelAsync();
        try
        {
            await rxTask;
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine();
        Console.WriteLine($"received {received.Count}/{frameCount} frames");

        // DCD lead: how long before each frame's arrival did the radio raise carrier-sense?
        // That lead is the whole value of hardware DCD for CSMA — the modem only knows about a
        // frame after the full preamble + payload has been demodulated.
        List<double> leads = [];
        lock (dcdEdges)
        {
            foreach (var (_, at, _) in received)
            {
                var rise = dcdEdges.LastOrDefault(e => e.Busy && e.At <= at);
                if (rise != default)
                {
                    leads.Add((at - rise.At).TotalMilliseconds);
                }
            }
        }
        if (leads.Count > 0)
        {
            Console.WriteLine(
                $"hardware DCD led frame delivery by min {leads.Min():0} ms / " +
                $"avg {leads.Average():0} ms / max {leads.Max():0} ms across {leads.Count} frames");
        }

        var withRssi = received.Where(r => r.Radio?.RssiDbm is not null).ToList();
        Console.WriteLine($"frames with RSSI attributed: {withRssi.Count}/{received.Count}");
        if (withRssi.Count > 0)
        {
            Console.WriteLine(
                $"attributed RSSI range {withRssi.Min(r => r.Radio!.Value.RssiDbm):0.0} … " +
                $"{withRssi.Max(r => r.Radio!.Value.RssiDbm):0.0} dBm, " +
                $"SNR up to {withRssi.Max(r => r.Radio!.Value.SnrDb):0.0} dB");
        }

        return received.Count > 0 && withRssi.Count == received.Count ? 0 : 1;
    }

    private static string Fmt(float? value) =>
        value?.ToString("0.0", CultureInfo.InvariantCulture) ?? "n/a";

    private static string FmtMs(TimeSpan? value) =>
        value is { } t ? t.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms" : "n/a";
}
