using Packet.Kiss;
using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Simultaneous listener on two NinoTNCs while one is pressed. Captures:
///
///   1) the synthetic <see cref="NinoTncTxTestFrameReceivedEvent"/> that the
///      pressed modem sends *up to its own host*, and
///   2) whatever the *other* modem hears on the air at the same moment -
///      typed (AX.25 / TX-Test / ACKMODE-Data / Unknown) plus the raw
///      KISS payload hex so we can eyeball the bytes when nothing
///      decodes cleanly.
///
/// Both modems are SETHW'd into the same mode so the receive side has
/// a chance of decoding whatever the transmit side puts out.
/// </summary>
internal static class TxTestDualListener
{
    public static async Task<int> Run(string portA, string portB, byte mode)
    {
        Console.WriteLine($"TxTestDualListener — A={portA}, B={portB}, mode={mode}");
        Console.WriteLine("Press the TX-Test button on either modem now.");
        Console.WriteLine("Ctrl-C to exit.");
        Console.WriteLine();

        await using var a = NinoTncSerialPort.Open(portA);
        await using var b = NinoTncSerialPort.Open(portB);
        await a.SetModeAsync(mode);
        await b.SetModeAsync(mode);
        await Task.Delay(500);

        a.InboundEvent += (_, evt) => Report("A", portA, evt);
        b.InboundEvent += (_, evt) => Report("B", portB, evt);

        // Catch the raw bytes too for the "didn't decode" case - the
        // typed dispatcher classifies non-decodable payloads as
        // UnknownInboundEvent but it's nice to have a single combined
        // log line for both sides.
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        await done.Task;
        return 0;
    }

    private static void Report(string side, string port, KissInboundEvent evt)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        var prefix = $"[{stamp}] {side} ({port})";
        switch (evt)
        {
            case NinoTncTxTestFrameReceivedEvent t:
                var d = t.Diagnostic;
                Console.WriteLine($"{prefix} TX-Test frame (synthetic)");
                Console.WriteLine($"           firmware={d.FirmwareVersion} serial={d.SerialNumber ?? "(not set)"} uptime={d.Uptime}");
                Console.WriteLine($"           DIP={d.DipSwitchPosition} running={d.RunningMode?.Name}");
                Console.WriteLine($"           rx: AX25={d.Ax25RxPackets} IL2P={d.Il2pRxPackets} IL2Punc={d.Il2pRxUncorrectable} preamble={d.PreambleCount}");
                Console.WriteLine($"           tx: pkts={d.TxPacketCount}");
                break;
            case Ax25FrameReceivedEvent ax:
                var info = ax.Ax25.Info.Length > 0
                    ? $" info[{ax.Ax25.Info.Length}B]={Convert.ToHexString(ax.Ax25.Info.Span)}"
                    : "";
                Console.WriteLine($"{prefix} AX.25 src={ax.Ax25.Source.Callsign} dst={ax.Ax25.Destination.Callsign} control=0x{ax.Ax25.Control:X2}{info}");
                break;
            case AckModeDataReceivedEvent ack:
                Console.WriteLine($"{prefix} ACKMODE-Data tag=0x{ack.SequenceTag:X4} payload[{ack.Ax25Payload.Length}B]={Convert.ToHexString(ack.Ax25Payload.Span)}");
                break;
            case UnknownInboundEvent u:
                Console.WriteLine($"{prefix} Unknown cmd={u.Raw.Command} port={u.Raw.Port} payload[{u.Raw.Payload.Length}B]={Convert.ToHexString(u.Raw.Payload)}");
                break;
        }
    }
}
