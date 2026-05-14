using Packet.Ax25;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Maps a raw <see cref="KissFrame"/> to its corresponding typed
/// <see cref="NinoTncInboundEvent"/>. Pulled out as a static helper so
/// tests can exercise the classification rules without spinning up a
/// real serial port, and so other consumers of the typed model (e.g.
/// log replay tools) can reuse the same dispatch.
/// </summary>
public static class NinoTncFrameClassifier
{
    /// <summary>
    /// Classify <paramref name="frame"/> into one of the
    /// <see cref="NinoTncInboundEvent"/> subtypes. Never returns null —
    /// frames the rules don't recognise become an
    /// <see cref="UnknownInboundEvent"/>.
    /// </summary>
    /// <remarks>
    /// ACKMODE TX-completion echoes (KISS command 0x0C with exactly a
    /// 2-byte payload) are *not* classified here. They are correlated
    /// inside <see cref="NinoTncSerialPort"/> by their sequence tag and
    /// surface via the return value of
    /// <see cref="NinoTncSerialPort.SendFrameWithAckAsync"/>, not as a
    /// typed event. Pass an echo through this method and it'll come back
    /// as <see cref="UnknownInboundEvent"/>.
    /// </remarks>
    public static NinoTncInboundEvent Classify(KissFrame frame)
    {
        // ACKMODE-Data: command 0x0C with 2-byte seq tag + AX.25 payload.
        if (KissAckMode.TryParseDataFrame(frame, out var tag, out var ax25Payload))
        {
            return new AckModeDataReceivedEvent(frame, tag, ax25Payload);
        }

        // KISS Data — could be an AX.25 frame, could be the NinoTNC's
        // synthetic TX-Test diagnostic. The TX-Test parser keys on the
        // "=FirmwareVr:" ASCII marker; if it matches, prefer that shape
        // (the surrounding bytes look like a malformed AX.25 header but
        // are firmware-generated and don't decode cleanly).
        if (frame.Command == KissCommand.Data)
        {
            if (NinoTncTxTestFrame.TryParse(frame, out var diag) && diag is not null)
            {
                return new TxTestFrameReceivedEvent(frame, diag);
            }
            if (Ax25Frame.TryParse(frame.Payload, out var ax25))
            {
                return new Ax25FrameReceivedEvent(frame, ax25);
            }
        }

        return new UnknownInboundEvent(frame);
    }
}
