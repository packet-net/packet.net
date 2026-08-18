using Packet.Core;

namespace Packet.NetRom.Routing;

/// <summary>
/// The INP3 metric for a learned route - the <em>second</em> metric space a route
/// can carry, alongside the vanilla NODES quality. Where <see cref="NetRomRoute.Quality"/>
/// is the multiplicative per-hop quality learned from NODES broadcasts (best =
/// highest), the INP3 metric is a <em>measured target time</em> learned from a RIF:
/// the summed transport time along the path (lowest = best) plus the hop count.
/// Immutable; present on a <see cref="NetRomRoute"/> only when that route was learned
/// (or refreshed) from INP3 RIF ingestion.
/// </summary>
/// <remarks>
/// <para>
/// <b>Units &amp; range.</b> <see cref="TargetTimeMs"/> is milliseconds, in
/// <c>[0, <see cref="Wire.Inp3Rip.HorizonMs"/>)</c> - i.e. strictly below the 600 s
/// routing horizon. A route at or over the horizon is <em>unreachable</em> and is
/// withdrawn rather than held (plan §5.3), so a stored <see cref="Inp3RouteMetric"/>
/// is always a live, finite-time route. The on-wire 10 ms granularity is a codec
/// concern (<see cref="Wire.Inp3Rip"/>); locally we carry plain milliseconds because
/// the per-hop increment and the neighbour SNTT need not be multiples of 10.
/// </para>
/// <para>
/// <b>Ordering.</b> Best INP3 route = lowest <see cref="TargetTimeMs"/> (ties broken
/// by lowest <see cref="HopCount"/>, then by neighbour callsign for determinism - the
/// time-space analogue of the quality-space "highest quality, then callsign"
/// ordering).
/// </para>
/// </remarks>
/// <param name="TargetTimeMs">The measured target time to the destination via this
/// route, in milliseconds (lower is better; always below the 600 s horizon).</param>
/// <param name="HopCount">The hop count to the destination along this route.</param>
public sealed record Inp3RouteMetric(int TargetTimeMs, byte HopCount);

/// <summary>
/// One learned route to a NET/ROM destination: the next-hop neighbour to forward
/// through, the quality we derived for it, and its obsolescence count. Immutable
/// - a member of a <see cref="NetRomDestination"/> in a
/// <see cref="NetRomRoutingSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Dual-metric routes (INP3).</b> A route can carry an <em>optional</em>
/// <see cref="Inp3"/> metric (target time + hop count) learned from an INP3 RIF, in
/// addition to its NODES-learned <see cref="Quality"/>. The two metric spaces are
/// independent: a destination can simultaneously hold quality-routes (from NODES) and
/// time-routes (from RIF), and the selection policy (plan §5.2) decides which space
/// wins per the <c>preferInp3Routes</c> knob. <see cref="Inp3"/> is <c>null</c> on a
/// route that was only ever learned from NODES - which is every route until INP3 is
/// enabled, so the default keeps today's behaviour exactly.
/// </para>
/// <para>
/// <b>Persistence.</b> <see cref="Inp3"/> is deliberately <em>not</em> persisted by
/// the SQLite routing store: INP3 routes re-learn from RIF (and the underlying link
/// SNTTs re-measure) within an L3RTT/RIF interval of restart, so persisting a stale
/// measured time would be worse than re-learning it. The store round-trips only the
/// vanilla <c>(Neighbour, Quality, Obsolescence)</c> triple; a restored route's
/// <see cref="Inp3"/> is therefore <c>null</c> until the next RIF refreshes it.
/// </para>
/// </remarks>
/// <param name="Neighbour">The neighbour we forward through for this route.</param>
/// <param name="PortId">The port that neighbour is adjacent on - the second half of the
/// route's next-hop identity (<see cref="NeighbourKey"/>). One destination can hold two
/// routes via the <em>same</em> callsign on different ports, each with its own quality and
/// obsolescence, exactly as BPQ's <c>NRROUTE[]</c> can point at two <c>ROUTE</c>s for one
/// neighbour callsign.</param>
/// <param name="Quality">Our derived quality for this route (0..255), best first within a destination.</param>
/// <param name="Obsolescence">Obsolescence count; decremented each sweep, purged at 0.</param>
/// <param name="Inp3">The optional INP3 metric (target time + hop count) for this
/// route, or <c>null</c> if the route was not learned from an INP3 RIF. Present iff
/// the route is INP3-learned; never persisted (re-learnt from RIF).</param>
public sealed record NetRomRoute(Callsign Neighbour, string PortId, byte Quality, int Obsolescence, Inp3RouteMetric? Inp3 = null)
{
    /// <summary>This route's next hop as the composite <see cref="NeighbourKey"/> - what the
    /// host looks the interlink up by.</summary>
    public NeighbourKey Key => new(PortId, Neighbour);
}

/// <summary>
/// A destination known to the table - its callsign + alias and its kept routes
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
/// A directly-heard NET/ROM neighbour - a node whose NODES broadcast we received
/// firsthand, with the path quality we assume to it and the port we heard it on.
/// Mirrors the canonical neighbour list (the <c>ROUTES</c> command), restricted
/// to what read-only ingest can know (we don't probe links, so quality is the
/// assumed default-port quality, and there are no digipeaters or lock state).
/// </summary>
/// <remarks>
/// One row per <see cref="NeighbourKey"/>, i.e. per <b>(port, callsign)</b> - so a station
/// audible on two ports appears twice, each row carrying that port's own
/// <see cref="PathQuality"/>, exactly as BPQ's <c>ROUTES</c> prints one line per
/// <c>(port, call)</c>. <see cref="Neighbour"/> + <see cref="PortId"/> together are the key.
/// </remarks>
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
/// An immutable, point-in-time view of the learned NET/ROM routing table -
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
    /// Resolve a connect target - an <em>alias</em> (e.g. <c>SOT</c>) or a
    /// <em>callsign</em> (e.g. <c>GB7SOT</c>, with or without SSID) - to the known
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

    /// <summary>The directly-heard neighbour entry for the exact adjacency
    /// <paramref name="key"/> - <b>(port, callsign)</b> - or <c>null</c> if we hold no such
    /// row. This is the lookup an interlink uses: the port is chosen by the route being
    /// followed, never guessed from the callsign.</summary>
    public NetRomNeighbour? NeighbourFor(NeighbourKey key)
        => Neighbours.FirstOrDefault(n =>
            n.Neighbour.Equals(key.Callsign) && string.Equals(n.PortId, key.PortId, StringComparison.Ordinal));

    /// <summary>
    /// <b>Every</b> adjacency we hold to <paramref name="neighbour"/> - one per port it is
    /// audible on - best first (highest path quality, ties by port id ordinal). Empty when it
    /// is not a known neighbour. The multi-port replacement for the old first-match-by-callsign
    /// scan: a caller that genuinely wants "any link to that node" iterates this in order.
    /// </summary>
    public IReadOnlyList<NetRomNeighbour> NeighboursOf(Callsign neighbour)
        => Neighbours
            .Where(n => n.Neighbour.Equals(neighbour))
            .OrderByDescending(n => n.PathQuality)
            .ThenBy(n => n.PortId, StringComparer.Ordinal)
            .ToList();

    /// <summary>The best adjacency to <paramref name="neighbour"/> (highest path quality, ties
    /// by port id ordinal), or <c>null</c> if it is not a known neighbour. The single-answer
    /// form of <see cref="NeighboursOf"/>.</summary>
    public NetRomNeighbour? BestNeighbourFor(Callsign neighbour)
    {
        var all = NeighboursOf(neighbour);
        return all.Count > 0 ? all[0] : null;
    }
}
