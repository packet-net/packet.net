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
public sealed record HeardEntry(
    string PortId,
    string Callsign,
    DateTimeOffset FirstHeard,
    DateTimeOffset LastHeard,
    long Count);
