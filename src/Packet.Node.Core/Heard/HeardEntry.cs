namespace Packet.Node.Core.Heard;

/// <summary>
/// One station heard on one port — a row of the MHeard log (the <c>MH</c> console verb,
/// #454). "Heard" means a frame was <i>received</i> from this callsign on this port; the
/// heard log is per-link, so the same callsign heard on two ports keeps two rows (the key
/// is the (<see cref="PortId"/>, <see cref="Callsign"/>) pair), and a node-wide view
/// merges them.
/// </summary>
/// <param name="PortId">The node port the station was heard on.</param>
/// <param name="Callsign">The transmitting station's callsign (the AX.25 source address),
/// in canonical form.</param>
/// <param name="FirstHeard">When this station was first heard on this port (set once).</param>
/// <param name="LastHeard">When this station was most recently heard on this port.</param>
/// <param name="Count">How many frames have been heard from this station on this port.</param>
/// <param name="LastRssiDbm">The received signal strength (dBm) attributed to the most recent frame
/// heard from this station on this port, when a radio control channel measured it — <c>null</c> when
/// this port has no radio attached, or the newest frame carried no attributed RSSI. Additive
/// (trailing optional): a heard row without it round-trips exactly as before.</param>
/// <param name="LastSnrDb">The signal-to-noise ratio (dB) attributed to the most recent frame heard
/// from this station on this port (RSSI over the tracked channel-idle noise floor), when a radio
/// control channel measured it — <c>null</c> when this port has no radio attached, or the newest
/// frame carried no attributed SNR. Additive (trailing optional), mirroring <see cref="LastRssiDbm"/>:
/// a heard row without it round-trips exactly as before. This is the per-partner SNR the observability
/// exporter surfaces, bounded to configured neighbours / active links (see docs/observability.md).</param>
public sealed record HeardEntry(
    string PortId,
    string Callsign,
    DateTimeOffset FirstHeard,
    DateTimeOffset LastHeard,
    long Count,
    float? LastRssiDbm = null,
    float? LastSnrDb = null);
