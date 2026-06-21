using Packet.Ax25;

namespace Packet.Kiss;

/// <summary>
/// Typed inbound event from a KISS modem (e.g. <c>KissSerialPort</c>,
/// <c>NinoTncSerialPort</c>, a TCP-based proxy). The base type lets
/// callers subscribe to "anything inbound" through one event; the
/// concrete subclasses let them filter by shape without re-parsing.
/// </summary>
/// <remarks>
/// Concrete drivers may emit *additional* subclasses for modem-specific
/// frames — e.g. <c>NinoTncTxTestFrameReceivedEvent</c> in
/// <c>Packet.Kiss.NinoTnc</c>. Subscribers should always cover the
/// generic shapes here and treat anything else as "modem-specific,
/// handle if recognised, else ignore".
/// </remarks>
public abstract record KissInboundEvent(KissFrame Raw)
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
/// payload did not parse as AX.25 the driver falls back to firing
/// <see cref="UnknownInboundEvent"/>.
/// </summary>
public sealed record Ax25FrameReceivedEvent(KissFrame Raw, Ax25Frame Ax25) : KissInboundEvent(Raw);

/// <summary>
/// An inbound ACKMODE-Data frame: KISS command 0x0C with a 2-byte sequence
/// tag prefix followed by an AX.25 frame body. Not the same as our own
/// outbound ACKMODE's TX-completion echo — that gets correlated by tag
/// inside the driver and surfaces as the returned
/// <see cref="Packet.Ax25.Transport.TxCompletion"/>, not as a typed event.
/// </summary>
public sealed record AckModeDataReceivedEvent(KissFrame Raw, ushort SequenceTag, ReadOnlyMemory<byte> Ax25Payload) : KissInboundEvent(Raw);

/// <summary>
/// A KISS frame the driver did not recognise as any of the typed shapes
/// above. The raw bytes are exposed for callers that want to debug, log,
/// or extend the dispatch. Modem-specific overlays (e.g. NinoTNC TX-Test)
/// run *after* the generic classifier and can upgrade an <see cref="UnknownInboundEvent"/>
/// into a richer typed event before it reaches subscribers.
/// </summary>
public sealed record UnknownInboundEvent(KissFrame Raw) : KissInboundEvent(Raw);
