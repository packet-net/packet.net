using Packet.Ax25;

namespace Packet.Kiss;

/// <summary>
/// Maps a raw <see cref="KissFrame"/> to its corresponding typed
/// <see cref="KissInboundEvent"/>. Generic across KISS modems -
/// recognises the shapes defined by KISS itself (Data → AX.25,
/// ACKMODE-Data, otherwise Unknown) without any modem-specific
/// knowledge.
/// </summary>
/// <remarks>
/// Modem-specific drivers can layer their own classifier on top: take
/// this output, and if it's <see cref="UnknownInboundEvent"/> (or even
/// an <see cref="Ax25FrameReceivedEvent"/> that happens to match a
/// firmware-specific pattern), upgrade it to a richer subclass before
/// firing the event. <c>NinoTncFrameClassifier</c> is the example.
/// </remarks>
public static class KissFrameClassifier
{
    /// <summary>
    /// Classify <paramref name="frame"/>. Never returns null - frames
    /// the rules don't recognise become an
    /// <see cref="UnknownInboundEvent"/>.
    /// </summary>
    /// <remarks>
    /// ACKMODE TX-completion echoes (KISS command 0x0C with exactly a
    /// 2-byte payload) are *not* classified here. They are correlated
    /// inside the driver by their sequence tag and surface via the
    /// return value of <c>SendFrameWithAckAsync</c>, not as a typed
    /// event. Pass an echo through this method and it'll come back as
    /// <see cref="UnknownInboundEvent"/>.
    /// </remarks>
    public static KissInboundEvent Classify(KissFrame frame)
    {
        // ACKMODE-Data: command 0x0C with 2-byte seq tag + AX.25 payload.
        if (KissAckMode.TryParseDataFrame(frame, out var tag, out var ax25Payload))
        {
            return new AckModeDataReceivedEvent(frame, tag, ax25Payload);
        }

        // KISS Data with an AX.25-shaped body.
        if (frame.Command == KissCommand.Data &&
            Ax25Frame.TryParse(frame.Payload, out var ax25))
        {
            return new Ax25FrameReceivedEvent(frame, ax25);
        }

        return new UnknownInboundEvent(frame);
    }
}
