namespace Packet.Node.Core.Capabilities;

/// <summary>
/// What we have learned about one neighbour on one port: whether it supports the
/// AX.25 v2.2 extended (SABME / mod-128) link setup, and whether it answers a
/// pre-session XID (which is how we discover SREJ support before committing a
/// connect). Capability is <b>per-link</b> - the same callsign reachable on two
/// ports can answer differently - so the key is the (<see cref="PortId"/>,
/// <see cref="Peer"/>) pair.
/// </summary>
/// <param name="PortId">The node port the peer was probed on.</param>
/// <param name="Peer">The neighbour callsign (as the dial path names it).</param>
/// <param name="SupportsExtended">Whether the peer accepts SABME (v2.2 extended).
/// <c>null</c> = never probed this dimension (no SABME dial has returned yet); a
/// mod-8 dial proves nothing here and leaves it <c>null</c>.</param>
/// <param name="SupportsSrejViaXid">Whether the peer answers a pre-session XID with
/// SREJ enabled. <c>null</c> = never probed (no pre-connect XID dial has returned).</param>
/// <param name="LastProbed">When this record was last updated by a returned dial -
/// drives the ~30-day staleness re-probe.</param>
/// <param name="LastRefused">When the peer last <i>refused/degraded</i> an extended
/// dial (we offered SABME and it came back mod-8). <c>null</c> = never observed a
/// refusal. Diagnostic only; the dial decision keys off the bools + freshness.</param>
public sealed record PeerCapabilityRecord(
    string PortId,
    string Peer,
    bool? SupportsExtended,
    bool? SupportsSrejViaXid,
    DateTimeOffset LastProbed,
    DateTimeOffset? LastRefused);
