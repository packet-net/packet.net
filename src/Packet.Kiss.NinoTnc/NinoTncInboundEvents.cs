using Packet.Ax25;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// Typed inbound event from a <see cref="NinoTncSerialPort"/>. The base
/// type lets callers subscribe to "anything inbound" through one event;
/// the concrete subclasses let them filter by shape without re-parsing.
/// </summary>
/// <remarks>
/// The raw <see cref="NinoTncSerialPort.FrameReceived"/> still fires
/// alongside these. Subscribe to whichever surface matches your needs:
/// the typed events when you want strong types and pre-parsing; the raw
/// event when you want every byte the TNC sent and full control of
/// dispatch.
/// </remarks>
public abstract record NinoTncInboundEvent(KissFrame Raw)
{
    /// <summary>
    /// When the event was constructed by the read pump (UTC). Useful for
    /// adaptive estimators and latency measurements.
    /// </summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A normal AX.25 frame received over the air, decoded from a KISS Data
/// frame. <see cref="Ax25"/> is non-null and successfully parsed; if the
/// payload did not parse as AX.25 the driver falls back to firing only
/// the raw <see cref="NinoTncSerialPort.FrameReceived"/> event.
/// </summary>
public sealed record Ax25FrameReceivedEvent(KissFrame Raw, Ax25Frame Ax25) : NinoTncInboundEvent(Raw);

/// <summary>
/// The on-demand TX-Test diagnostic frame the NinoTNC emits when the
/// operator presses the front-panel TX-Test button. The over-air frame
/// (the modem-generated test signal) is *not* this; this is the synthetic
/// KISS-Data frame the firmware delivers to its USB host at the same moment.
/// </summary>
public sealed record TxTestFrameReceivedEvent(KissFrame Raw, NinoTncTxTestFrame Diagnostic) : NinoTncInboundEvent(Raw);

/// <summary>
/// An inbound ACKMODE-Data frame: KISS command 0x0C with a 2-byte sequence
/// tag prefix followed by an AX.25 frame body. This is *not* the more
/// common case of our own outbound ACKMODE's TX-completion echo — that
/// gets correlated internally by <see cref="NinoTncSerialPort.SendFrameWithAckAsync"/>
/// and surfaces as the returned <see cref="AckModeReceipt"/>, not as
/// a typed event.
/// </summary>
public sealed record AckModeDataReceivedEvent(KissFrame Raw, ushort SequenceTag, ReadOnlyMemory<byte> Ax25Payload) : NinoTncInboundEvent(Raw);

/// <summary>
/// A KISS frame the driver did not recognise as any of the typed shapes
/// above. The raw bytes are exposed for callers that want to debug, log,
/// or extend the dispatch. Most real systems will never see one — it is
/// useful mainly for firmware changes or future KISS extensions.
/// </summary>
public sealed record UnknownInboundEvent(KissFrame Raw) : NinoTncInboundEvent(Raw);
