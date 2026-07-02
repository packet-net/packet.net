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
/// Optional per-frame radio signal metadata. Every field is optional and populated only when a
/// real source (a radio control channel such as Tait CCDI, via <c>Packet.Radio</c>'s
/// <c>RssiTaggingTransport</c>) actually measured it. Grows additively (new optional fields) as
/// radio-control channels surface more, without forcing a change to
/// <see cref="Ax25InboundFrame"/>.
/// </summary>
/// <param name="RssiDbm">
/// Received signal strength attributed to this frame, in dBm — the median of the RSSI samples
/// taken while the frame's carrier was up.
/// </param>
/// <param name="SnrDb">
/// <see cref="RssiDbm"/> minus <see cref="NoiseFloorDbm"/>, in dB.
/// </param>
/// <param name="NoiseFloorDbm">
/// The channel-idle noise floor the source was tracking when this frame arrived, in dBm
/// (an EMA over samples taken while carrier-sense reported the channel quiet).
/// </param>
/// <param name="RssiMinDbm">
/// Weakest RSSI sample attributed to this frame, in dBm. A wide min–max spread across one
/// frame indicates fading/flutter that the median alone hides.
/// </param>
/// <param name="RssiMaxDbm">Strongest RSSI sample attributed to this frame, in dBm.</param>
/// <param name="RssiSampleCount">
/// How many RSSI samples produced the statistics above — a confidence signal (short frames
/// between polls may carry only one or two samples).
/// </param>
/// <param name="CarrierRiseAt">
/// When the radio's hardware carrier-sense (DCD) rose for the transmission window carrying
/// this frame. For a multi-frame train this is the rise of the shared window, identical for
/// every frame in the burst. Precision is the control channel's reporting latency — measured
/// ~27 ms constant (±2 ms) on Tait CCDI at 28800 baud.
/// </param>
/// <param name="BurstIndex">
/// Zero-based position of this frame within its carrier window. AX.25 permits several frames
/// in one continuous transmission (one preamble, no re-keying); index 0 is the frame that paid
/// the TXDELAY. <c>null</c> when the source has no carrier-sense channel.
/// </param>
/// <param name="EstimatedAirtime">
/// Approximate on-air duration of this frame: wire bytes (+FCS +flag) × 8 ÷ the modem bit
/// rate, ignoring bit-stuffing (real airtime runs 0–8 % longer). Only present when the source
/// knows the modem's current bit rate. Note <see cref="Ax25InboundFrame.ReceivedAt"/> is
/// *delivery* time — end-of-RF plus the modem's decode+serial latency — so the frame's on-air
/// start is approximately <c>ReceivedAt − EstimatedAirtime</c>.
/// </param>
/// <param name="PreDataCarrier">
/// Measured carrier time before this frame's data began: (<c>ReceivedAt − EstimatedAirtime</c>)
/// − <see cref="CarrierRiseAt"/>. Only populated for the first frame of a burst
/// (<see cref="BurstIndex"/> 0) — later frames in a train paid no preamble. This is the input
/// to an <b>excess-TXDELAY detector</b>: it tracks the transmitting station's actual TXDELAY
/// (+ a small rig-constant overhead, measured ~40–75 ms at 1200 Bd), so a station burning
/// 500 ms of preamble where 150 ms decodes fine is directly visible per frame.
/// </param>
public readonly record struct RadioMetadata(
    float? RssiDbm = null,
    float? SnrDb = null,
    float? NoiseFloorDbm = null,
    float? RssiMinDbm = null,
    float? RssiMaxDbm = null,
    int? RssiSampleCount = null,
    DateTimeOffset? CarrierRiseAt = null,
    int? BurstIndex = null,
    TimeSpan? EstimatedAirtime = null,
    TimeSpan? PreDataCarrier = null);
