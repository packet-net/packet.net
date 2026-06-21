namespace Packet.Ax25.Transport;

/// <summary>
/// One inbound AX.25 frame as delivered by an <see cref="IAx25Transport"/> — the bare AX.25
/// frame body plus the small amount of metadata the consumer/monitor actually wants. Replaces
/// the KISS-shaped <c>KissFrame</c> at the transport seam: there is no command byte (the
/// transport has already filtered to AX.25 data) and no KISS framing.
/// </summary>
/// <param name="Ax25">
/// The bare AX.25 frame body, FCS-stripped — ready for <c>Ax25Frame.TryParse</c>.
/// </param>
/// <param name="PortId">
/// Multi-drop / multi-port channel id (0–15), preserved for multi-port serial / AGW. 0 for
/// single-channel transports (AXUDP, in-memory).
/// </param>
/// <param name="ReceivedAt">
/// Capture time, stamped by the transport from an injected <see cref="TimeProvider"/> at the
/// moment the frame arrived — NOT reconstructed later, so the adaptive estimator and the
/// monitor see true receive latency.
/// </param>
/// <param name="Radio">
/// Optional per-frame radio signal metadata; <c>null</c> for every transport with no radio
/// control channel (AXUDP, KISS-TCP, in-memory). A future RSSI/SNR source populates it
/// without changing this contract.
/// </param>
public readonly record struct Ax25InboundFrame(
    ReadOnlyMemory<byte> Ax25,
    byte PortId,
    DateTimeOffset ReceivedAt,
    RadioMetadata? Radio = null);

/// <summary>
/// Optional per-frame radio signal metadata. Intentionally minimal — only fields a real
/// source populates today. Grows additively (new optional fields) as radio-control channels
/// surface more, without forcing a change to <see cref="Ax25InboundFrame"/>.
/// </summary>
public readonly record struct RadioMetadata(
    float? RssiDbm = null,
    float? SnrDb = null);
