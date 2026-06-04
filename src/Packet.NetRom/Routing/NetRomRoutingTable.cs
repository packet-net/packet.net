using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Routing;

/// <summary>
/// The learned NET/ROM routing table: ingests NODES broadcasts heard
/// promiscuously, derives route qualities via the multiplicative per-hop
/// formula, keeps the best routes per destination with obsolescence decay, and
/// hands out immutable <see cref="NetRomRoutingSnapshot"/>s for surfacing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read-only by construction.</b> The table is a pure consumer of heard
/// broadcasts — it transmits nothing, originates no NODES, opens no circuits.
/// It implements the canonical processing heuristics from the NET/ROM appendix:
/// </para>
/// <list type="number">
///   <item>A heard broadcast's originator becomes a directly-heard
///   <em>neighbour</em>, created with the configured default-port path quality
///   if not already known (heuristic 3 + 4).</item>
///   <item>A <b>direct route to the originator</b> is assumed at the neighbour's
///   path quality (heuristic 4).</item>
///   <item>For each advertised destination, the route quality <em>via that
///   neighbour</em> is the advertised quality combined with the path quality
///   (<see cref="NetRomQuality.Combine"/>, heuristic 5).</item>
///   <item><b>Trivial-loop guard</b>: if the advertised best-neighbour is our own
///   callsign, the route is quality 0 — a last resort that is never kept
///   (heuristic 6).</item>
///   <item>Only the <see cref="NetRomRoutingOptions.MaxRoutesPerDestination"/>
///   best routes per destination are kept (heuristic 7).</item>
///   <item>Routes at or below quality 0, or below
///   <see cref="NetRomRoutingOptions.MinQuality"/>, are dropped (heuristic 8).</item>
///   <item>Destinations stop being added once
///   <see cref="NetRomRoutingOptions.MaxDestinations"/> is reached (heuristic 9).</item>
/// </list>
/// <para>
/// <b>Obsolescence.</b> A route's count is (re)set to
/// <see cref="NetRomRoutingOptions.ObsoleteInitial"/> whenever a broadcast
/// adds/refreshes it. <see cref="Sweep"/> (called at the broadcast interval)
/// decrements every count and purges routes that reach 0; a destination with no
/// remaining routes is removed.
/// </para>
/// <para>
/// <b>Thread-safety.</b> All mutation and snapshotting is under a single lock, so
/// the table can be fed from every port's frame tap concurrently and read by the
/// console / MCP at any time without tearing. <see cref="TimeProvider"/> is
/// injected (no wall-clock — §2.7) for last-heard stamps.
/// </para>
/// </remarks>
public sealed class NetRomRoutingTable
{
    private readonly NetRomRoutingOptions options;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    // destination callsign -> its entry (alias + per-neighbour routes).
    private readonly Dictionary<Callsign, DestinationState> destinations = new();
    // neighbour callsign -> directly-heard neighbour state.
    private readonly Dictionary<Callsign, NeighbourState> neighbours = new();

    /// <summary>Construct a table with the canonical default options and the system clock.</summary>
    public NetRomRoutingTable()
        : this(NetRomRoutingOptions.Default, TimeProvider.System)
    {
    }

    /// <summary>Construct a table with explicit options and time provider.</summary>
    public NetRomRoutingTable(NetRomRoutingOptions options, TimeProvider timeProvider)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Ingest a NODES broadcast heard from <paramref name="originator"/> on
    /// <paramref name="portId"/>, with this node's own callsign
    /// <paramref name="myCall"/> (for the trivial-loop guard). Pure table
    /// maintenance — never transmits.
    /// </summary>
    /// <param name="originator">The AX.25 source callsign of the UI frame (the broadcasting neighbour).</param>
    /// <param name="myCall">Our own node callsign — an advertised best-neighbour matching this is loop-guarded to quality 0.</param>
    /// <param name="portId">The node-host port id the broadcast was heard on.</param>
    /// <param name="broadcast">The parsed broadcast content.</param>
    public void Ingest(Callsign originator, Callsign myCall, string portId, NodesBroadcast broadcast)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        ArgumentNullException.ThrowIfNull(portId);

        var now = timeProvider.GetUtcNow();
        byte pathQuality = (byte)Math.Clamp(options.DefaultNeighbourQuality, NetRomQuality.Min, NetRomQuality.Max);

        lock (gate)
        {
            // Heuristic 3: ensure a neighbour-list entry for the originator,
            // created with the default-port path quality. Refresh its alias +
            // last-heard each time.
            if (!neighbours.TryGetValue(originator, out var nbr))
            {
                nbr = new NeighbourState { PathQuality = pathQuality };
                neighbours[originator] = nbr;
            }
            nbr.Alias = broadcast.SenderAlias;
            nbr.PortId = portId;
            nbr.LastHeard = now;
            byte originatorPathQuality = nbr.PathQuality;

            // Heuristic 4: assume a direct route to the originator at the
            // neighbour path quality. (The originator may also appear as a
            // destination in its own list with a different quality — that is
            // merged as a normal indirect route below; the direct route via
            // itself usually wins.)
            UpsertRoute(
                destination: originator,
                alias: broadcast.SenderAlias,
                viaNeighbour: originator,
                quality: originatorPathQuality);

            // Heuristic 5/6/7/8: each advertised destination becomes a route via
            // this neighbour at the combined quality, loop-guarded against us.
            foreach (var entry in broadcast.Entries)
            {
                byte quality = entry.BestNeighbour.Equals(myCall)
                    ? (byte)NetRomQuality.Min                    // trivial-loop guard
                    : NetRomQuality.Combine(entry.BestQuality, originatorPathQuality);

                UpsertRoute(
                    destination: entry.Destination,
                    alias: entry.DestinationAlias,
                    viaNeighbour: originator,
                    quality: quality);
            }
        }
    }

    /// <summary>
    /// Decrement the obsolescence count of every route, purging routes that reach
    /// 0 and destinations that lose all their routes. Call this at the NODES
    /// broadcast interval. Neighbours with no surviving route are also dropped.
    /// </summary>
    /// <returns>The number of routes purged.</returns>
    public int Sweep()
    {
        lock (gate)
        {
            int purged = 0;
            var emptyDestinations = new List<Callsign>();

            foreach (var (destCall, dest) in destinations)
            {
                var survivors = new Dictionary<Callsign, RouteState>(dest.Routes.Count);
                foreach (var (via, route) in dest.Routes)
                {
                    int next = route.Obsolescence - 1;
                    if (next <= 0)
                    {
                        purged++;
                        continue;
                    }
                    survivors[via] = route with { Obsolescence = next };
                }
                dest.Routes = survivors;
                if (survivors.Count == 0)
                {
                    emptyDestinations.Add(destCall);
                }
            }

            foreach (var dc in emptyDestinations)
            {
                destinations.Remove(dc);
            }

            PruneOrphanNeighbours();
            return purged;
        }
    }

    /// <summary>
    /// Take an immutable snapshot of the current table — destinations with their
    /// best-first routes, and the directly-heard neighbours. Ordering is stable
    /// (alias-or-callsign for destinations, callsign for neighbours) so the
    /// surfaced output is deterministic.
    /// </summary>
    public NetRomRoutingSnapshot Snapshot()
    {
        lock (gate)
        {
            var dests = new List<NetRomDestination>(destinations.Count);
            foreach (var (destCall, dest) in destinations)
            {
                var routes = dest.Routes.Values
                    .OrderByDescending(r => r.Quality)
                    .ThenBy(r => r.Neighbour.ToString(), StringComparer.Ordinal)
                    .Take(options.MaxRoutesPerDestination)
                    .Select(r => new NetRomRoute(r.Neighbour, r.Quality, r.Obsolescence))
                    .ToList();

                dests.Add(new NetRomDestination(destCall, dest.Alias, routes));
            }

            dests = dests
                .OrderBy(d => string.IsNullOrEmpty(d.Alias) ? d.Destination.ToString() : d.Alias, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.Destination.ToString(), StringComparer.Ordinal)
                .ToList();

            var nbrs = neighbours
                .Select(kvp => new NetRomNeighbour(
                    kvp.Key, kvp.Value.Alias, kvp.Value.PortId, kvp.Value.PathQuality, kvp.Value.LastHeard))
                .OrderBy(n => n.Neighbour.ToString(), StringComparer.Ordinal)
                .ToList();

            return new NetRomRoutingSnapshot(dests, nbrs, timeProvider.GetUtcNow());
        }
    }

    /// <summary>
    /// Build the destination entries to advertise in our own NODES broadcast — the
    /// L3-origination view of the table. For each known destination we advertise its
    /// best route's quality via its best next-hop neighbour, gated by
    /// <paramref name="obsoleteMinimum"/> (OBSMIN: a route whose obsolescence has
    /// decayed below this is still kept but no longer advertised — it ages out of
    /// broadcasts before it is purged). Quality-0 routes are never advertised.
    /// </summary>
    /// <param name="obsoleteMinimum">The OBSMIN advertise-gate (routes with
    /// obsolescence &lt; this are not advertised). 0 advertises everything kept.</param>
    /// <returns>The advertisable entries, best quality first.</returns>
    public IReadOnlyList<Wire.NodesBroadcastBuilder.Entry> BuildAdvertisement(int obsoleteMinimum)
    {
        lock (gate)
        {
            var entries = new List<(Wire.NodesBroadcastBuilder.Entry Entry, byte Quality)>();
            foreach (var (destCall, dest) in destinations)
            {
                var best = dest.Routes.Values
                    .OrderByDescending(r => r.Quality)
                    .ThenByDescending(r => r.Obsolescence)
                    .FirstOrDefault();
                if (best is null)
                {
                    continue;
                }
                if (best.Quality <= NetRomQuality.Min)
                {
                    continue;   // never advertise a quality-0 / loop-guarded route
                }
                if (best.Obsolescence < obsoleteMinimum)
                {
                    continue;   // OBSMIN: decayed below the advertise threshold
                }

                entries.Add((
                    new Wire.NodesBroadcastBuilder.Entry(destCall, dest.Alias, best.Neighbour, best.Quality),
                    best.Quality));
            }

            return entries
                .OrderByDescending(e => e.Quality)
                .ThenBy(e => e.Entry.Destination.ToString(), StringComparer.Ordinal)
                .Select(e => e.Entry)
                .ToList();
        }
    }

    // ─── Internals ────────────────────────────────────────────────────

    // Add or refresh a route to `destination` via `viaNeighbour`. Applies the
    // quality-0 / MINQUAL floor (heuristic 8), resets obsolescence to OBSINIT,
    // enforces the per-destination route cap (heuristic 7) and the destination
    // cap (heuristic 9). Caller holds the lock.
    private void UpsertRoute(Callsign destination, string alias, Callsign viaNeighbour, byte quality)
    {
        // A quality-0 route is never usable / kept; likewise anything under the
        // configured floor. If such a route already existed (from a prior, better
        // advertisement), a now-too-low re-advertisement removes it.
        bool acceptable = quality > NetRomQuality.Min && quality >= options.MinQuality;

        if (!destinations.TryGetValue(destination, out var dest))
        {
            if (!acceptable)
            {
                return;   // nothing to add, nothing to update
            }
            if (destinations.Count >= options.MaxDestinations)
            {
                return;   // heuristic 9: destination list full — ignore new destinations
            }
            dest = new DestinationState { Alias = alias };
            destinations[destination] = dest;
        }
        else if (!string.IsNullOrEmpty(alias))
        {
            // Refresh a known destination's alias when the advertisement carries one.
            dest.Alias = alias;
        }

        if (!acceptable)
        {
            // Drop a route that has decayed below the floor.
            dest.Routes.Remove(viaNeighbour);
            if (dest.Routes.Count == 0)
            {
                destinations.Remove(destination);
            }
            return;
        }

        dest.Routes[viaNeighbour] = new RouteState
        {
            Neighbour = viaNeighbour,
            Quality = quality,
            Obsolescence = options.ObsoleteInitial,
        };

        // Heuristic 7: keep only the N best routes. If we now exceed the cap,
        // evict the lowest-quality route(s).
        if (dest.Routes.Count > options.MaxRoutesPerDestination)
        {
            var keep = dest.Routes.Values
                .OrderByDescending(r => r.Quality)
                .ThenBy(r => r.Neighbour.ToString(), StringComparer.Ordinal)
                .Take(options.MaxRoutesPerDestination)
                .ToDictionary(r => r.Neighbour);
            dest.Routes = keep;
        }
    }

    // Drop neighbours that are no longer the next hop for any kept route. Caller
    // holds the lock. (A neighbour we heard directly always has its own direct
    // route, so it survives until that route ages out — at which point it is a
    // genuine orphan.)
    private void PruneOrphanNeighbours()
    {
        if (neighbours.Count == 0)
        {
            return;
        }

        var inUse = new HashSet<Callsign>();
        foreach (var dest in destinations.Values)
        {
            foreach (var via in dest.Routes.Keys)
            {
                inUse.Add(via);
            }
        }

        var orphans = neighbours.Keys.Where(n => !inUse.Contains(n)).ToList();
        foreach (var o in orphans)
        {
            neighbours.Remove(o);
        }
    }

    private sealed class DestinationState
    {
        public string Alias { get; set; } = string.Empty;
        public Dictionary<Callsign, RouteState> Routes { get; set; } = new();
    }

    private sealed record RouteState
    {
        public required Callsign Neighbour { get; init; }
        public required byte Quality { get; init; }
        public required int Obsolescence { get; init; }
    }

    private sealed class NeighbourState
    {
        public string Alias { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
        public byte PathQuality { get; set; }
        public DateTimeOffset LastHeard { get; set; }
    }
}
