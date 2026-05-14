using Packet.Kiss.NinoTnc;

namespace Packet.NinoTnc.Spike;

/// <summary>
/// Sits on a port and prints every typed inbound event. Intended for the
/// "press the TX-Test button now" demo: we expect to see one
/// <see cref="TxTestFrameReceivedEvent"/> immediately after the press,
/// alongside (or instead of) any AX.25 frame the modem also keys onto
/// the air.
/// </summary>
internal static class TxTestListener
{
    public static async Task<int> Run(string port)
    {
        Console.WriteLine($"TxTestListener — listening on {port}");
        Console.WriteLine("Press the TX-Test button on the modem now.");
        Console.WriteLine("Ctrl-C to exit.");
        Console.WriteLine();

        await using var tnc = NinoTncSerialPort.Open(port);
        // No SetModeAsync here — we want whatever DIP / runtime state the
        // operator already has. The TX-Test frame doesn't depend on mode
        // (the demod test signal does, but that lands on the air, not on
        // the host stream).

        tnc.InboundEvent += (_, evt) =>
        {
            var stamp = DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
            switch (evt)
            {
                case TxTestFrameReceivedEvent t:
                    var d = t.Diagnostic;
                    Console.WriteLine($"[{stamp}] TX-Test pressed");
                    Console.WriteLine($"          firmware:        {d.FirmwareVersion}");
                    Console.WriteLine($"          serial:          {d.SerialNumber ?? "(not set)"}");
                    Console.WriteLine($"          uptime:          {d.Uptime}");
                    Console.WriteLine($"          board revision:  0x{d.BoardRevision:X2}");
                    Console.WriteLine($"          DIP position:    {d.DipSwitchPosition}");
                    Console.WriteLine($"          firmware mode:   0x{d.FirmwareModeByte:X2}");
                    Console.WriteLine($"          running mode:    {d.RunningMode?.Name ?? "(unknown — firmware byte not in catalog)"}");
                    Console.WriteLine($"          AX25 rx pkts:    {d.Ax25RxPackets}");
                    Console.WriteLine($"          IL2P rx pkts:    {d.Il2pRxPackets}");
                    Console.WriteLine($"          TX pkts:         {d.TxPacketCount}");
                    break;
                case Ax25FrameReceivedEvent ax:
                    Console.WriteLine($"[{stamp}] AX.25 frame   src={ax.Ax25.Source.Callsign} dst={ax.Ax25.Destination.Callsign} info={ax.Ax25.Info.Length}B");
                    break;
                case AckModeDataReceivedEvent ack:
                    Console.WriteLine($"[{stamp}] ACKMODE data  tag=0x{ack.SequenceTag:X4} payload={ack.Ax25Payload.Length}B");
                    break;
                case UnknownInboundEvent u:
                    Console.WriteLine($"[{stamp}] Unknown       cmd={u.Raw.Command} payload={u.Raw.Payload.Length}B");
                    break;
            }
        };

        // Park until Ctrl-C.
        var done = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
        await done.Task;
        return 0;
    }
}
