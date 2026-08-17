using Packet.Core;

namespace Packet.NetRom.Routing;

/// <summary>
/// The identity of a NET/ROM neighbour: the <b>(port, callsign)</b> pair, not the callsign
/// alone. One station audible on two ports is two neighbours - two adjacencies, each with its
/// own path quality, its own routes, its own interlink and its own liveness.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the port is in the key.</b> A neighbour is an <em>adjacency</em>, and an adjacency
/// belongs to a link. The de-facto references agree: LinBPQ's <c>struct ROUTE</c> carries
/// <c>NEIGHBOUR_CALL</c> <em>and</em> <c>NEIGHBOUR_PORT</c> with its own quality, its own L2
/// session and its own round-trip state, and <c>FindNeighbour(call, port)</c> compares both;
/// the Linux kernel's <c>struct nr_neigh</c> carries <c>callsign</c> and <c>dev</c>, and
/// <c>/proc/net/nr_neigh</c> prints one row per pair. Keying by callsign alone means the
/// per-port <c>QUALITY</c> of whichever port heard the last broadcast wins, one dead port drops
/// routes another port could still carry, and a dual-homed backbone peer gets no port diversity.
/// See <c>docs/netrom-multiport-neighbours.md</c>.
/// </para>
/// <para>
/// <b>Not a wire concept.</b> No port appears in a NODES broadcast entry or an INP3 RIP - the
/// key is local routing state only, so a mixed fleet interoperates unchanged.
/// </para>
/// <para>
/// <b>Comparison.</b> <see cref="PortId"/> compares <see cref="StringComparison.Ordinal"/>
/// (port ids are opaque configuration identifiers, never case-folded), so the key is a stable
/// dictionary key and a deterministic sort key across the C#/TS/Rust ports.
/// </para>
/// </remarks>
/// <param name="PortId">The node-host port id the neighbour is adjacent on.</param>
/// <param name="Callsign">The neighbour node's callsign.</param>
public readonly record struct NeighbourKey(string PortId, Callsign Callsign)
{
    /// <summary>Ordinal port-id equality plus callsign equality.</summary>
    public bool Equals(NeighbourKey other)
        => string.Equals(PortId, other.PortId, StringComparison.Ordinal) && Callsign.Equals(other.Callsign);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(PortId is null ? 0 : StringComparer.Ordinal.GetHashCode(PortId), Callsign);

    /// <summary>"port:CALLSIGN" - the shape the console and the logs print.</summary>
    public override string ToString() => $"{PortId}:{Callsign}";
}
