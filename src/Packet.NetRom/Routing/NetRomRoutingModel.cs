using Packet.Core;

namespace Packet.NetRom.Routing;

/// <summary>
/// One learned route to a NET/ROM destination: the next-hop neighbour to forward
/// through, the quality we derived for it, and its obsolescence count. Immutable
/// — a member of a <see cref="NetRomDestination"/> in a
/// <see cref="NetRomRoutingSnapshot"/>.
/// </summary>
/// <param name="Neighbour">The neighbour we forward through for this route.</param>
/// <param name="Quality">Our derived quality for this route (0..255), best first within a destination.</param>
/// <param name="Obsolescence">Obsolescence count; decremented each sweep, purged at 0.</param>
public sealed record NetRomRoute(Callsign Neighbour, byte Quality, int Obsolescence);

/// <summary>
/// A destination known to the table — its callsign + alias and its kept routes
/// (≤ <see cref="NetRomRoutingOptions.MaxRoutesPerDestination"/>, sorted by
/// quality, best first). The active route is <see cref="BestRoute"/>.
/// </summary>
/// <param name="Destination">The destination node's callsign.</param>
/// <param name="Alias">The destination node's alias / mnemonic (may be empty).</param>
/// <param name="Routes">The kept routes, best quality first.</param>
public sealed record NetRomDestination(Callsign Destination, string Alias, IReadOnlyList<NetRomRoute> Routes)
{
    /// <summary>The highest-quality route to this destination, or <c>null</c> if it somehow has none.</summary>
    public NetRomRoute? BestRoute => Routes.Count > 0 ? Routes[0] : null;
}

/// <summary>
/// A directly-heard NET/ROM neighbour — a node whose NODES broadcast we received
/// firsthand, with the path quality we assume to it and the port we heard it on.
/// Mirrors the canonical neighbour list (the <c>ROUTES</c> command), restricted
/// to what read-only ingest can know (we don't probe links, so quality is the
/// assumed default-port quality, and there are no digipeaters or lock state).
/// </summary>
/// <param name="Neighbour">The neighbour's callsign.</param>
/// <param name="Alias">The neighbour's alias / mnemonic, as it announced (may be empty).</param>
/// <param name="PortId">The node-host port id we heard it on.</param>
/// <param name="PathQuality">The path quality we assume to this neighbour (0..255).</param>
/// <param name="LastHeard">When we last heard a broadcast from it.</param>
public sealed record NetRomNeighbour(
    Callsign Neighbour,
    string Alias,
    string PortId,
    byte PathQuality,
    DateTimeOffset LastHeard);

/// <summary>
/// An immutable, point-in-time view of the learned NET/ROM routing table —
/// destinations with their routes, and the directly-heard neighbours. This is
/// the read-only model the <c>Nodes</c> console command, a future MCP
/// <c>network_topology</c> tool, and the web monitor all consume; the live table
/// hands one out via a single lock so consumers never see a torn state.
/// </summary>
/// <param name="Destinations">Known destinations (ordering: alias/callsign, ascending).</param>
/// <param name="Neighbours">Directly-heard neighbours (ordering: callsign, ascending).</param>
/// <param name="GeneratedAt">When this snapshot was taken.</param>
public sealed record NetRomRoutingSnapshot(
    IReadOnlyList<NetRomDestination> Destinations,
    IReadOnlyList<NetRomNeighbour> Neighbours,
    DateTimeOffset GeneratedAt)
{
    /// <summary>An empty snapshot (nothing learned yet).</summary>
    public static NetRomRoutingSnapshot Empty { get; } =
        new([], [], DateTimeOffset.MinValue);

    /// <summary>Total destinations known.</summary>
    public int DestinationCount => Destinations.Count;

    /// <summary>Total directly-heard neighbours.</summary>
    public int NeighbourCount => Neighbours.Count;

    /// <summary>
    /// Resolve a connect target — an <em>alias</em> (e.g. <c>SOT</c>) or a
    /// <em>callsign</em> (e.g. <c>GB7SOT</c>, with or without SSID) — to the known
    /// destination, or <c>null</c> if the table has no route to it. Alias match is
    /// case-insensitive; callsign match is exact. This is what <c>connect &lt;alias&gt;</c>
    /// consults to find the best next hop across the network.
    /// </summary>
    public NetRomDestination? ResolveDestination(string aliasOrCallsign)
    {
        if (string.IsNullOrWhiteSpace(aliasOrCallsign))
        {
            return null;
        }
        var needle = aliasOrCallsign.Trim();

        // Prefer an exact alias match (the human-friendly name a user types).
        var byAlias = Destinations.FirstOrDefault(d =>
            !string.IsNullOrEmpty(d.Alias) && string.Equals(d.Alias, needle, StringComparison.OrdinalIgnoreCase));
        if (byAlias is not null)
        {
            return byAlias;
        }

        // Else a callsign match (exact text, e.g. GB7SOT or GB7SOT-2).
        return Destinations.FirstOrDefault(d =>
            string.Equals(d.Destination.ToString(), needle, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The directly-heard neighbour entry for <paramref name="neighbour"/>,
    /// or <c>null</c> if it is not a known neighbour. Used to find the port an
    /// interlink to that neighbour should run on.</summary>
    public NetRomNeighbour? NeighbourFor(Callsign neighbour)
        => Neighbours.FirstOrDefault(n => n.Neighbour.Equals(neighbour));
}
