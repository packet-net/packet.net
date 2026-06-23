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
    /// <summary>
    /// The fixed INP3 per-hop target-time increment, in milliseconds (design §2.2,
    /// plan §6.3). Added to every learned time-route so target time is strictly
    /// increasing per hop even across a ~0 ms link (a loopback or same-host fleet) —
    /// the loop-safety invariant "target time monotonic-nondecreasing per hop".
    /// </summary>
    public const int PerHopIncrementMs = 10;

    /// <summary>
    /// The default INP3 hop horizon (plan §8 <c>hopLimit</c>, canonical 30): a RIP
    /// whose learned hop count would exceed this is not learned. The hop-count analogue
    /// of the 600 s time horizon. Used when <see cref="IngestRif"/> is called without an
    /// explicit limit (the host passes its configured <c>NetRomInp3Options.HopLimit</c>).
    /// </summary>
    public const int DefaultHopLimit = 30;

    private readonly NetRomRoutingOptions options;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();

    // destination callsign -> its entry (alias + per-neighbour routes).
    private readonly Dictionary<Callsign, DestinationState> destinations = new();
    // neighbour callsign -> directly-heard neighbour state.
    private readonly Dictionary<Callsign, NeighbourState> neighbours = new();

    // INP3 invariant (W): destinations that have lost their LAST Inp3-bearing route
    // (withdrawn at horizon, dropped by MarkNeighbourDown, or aged out by Sweep) since
    // the host last DRAINED this set. The host DrainRecentlyWithdrawn()s it ONCE at the
    // start of each fan-out round (atomic snapshot+clear under `gate`) and hands the
    // snapshot to every neighbour's BuildRif, so the one-shot horizon RIP reaches each
    // neighbour exactly once. Draining atomically (rather than read-then-clear-after-round)
    // closes the host-thread race: a concurrent IngestRif / MarkNeighbourDown / Sweep that
    // withdraws a destination mid-round lands in the live set AFTER the snapshot was taken,
    // so it is captured by the NEXT round's drain instead of being cleared unadvertised.
    // Populated under `gate` only when an Inp3-bearing route fully leaves — so a vanilla
    // (quality-only) MarkNeighbourDown / Sweep, the INP3-off path, never touches it (the
    // default-off guarantee, design §7.1). RecentlyWithdrawn() is a read-only peek (tests /
    // monitoring); the host never reads it directly — only DrainRecentlyWithdrawn().
    private readonly HashSet<Callsign> recentlyWithdrawn = new();

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
    /// <param name="neighbourQuality">
    /// The path quality to assume for the directly-heard neighbour on this port (the BPQ
    /// per-port <c>QUALITY</c>). <c>null</c> (the default) ⇒ the table-wide
    /// <see cref="NetRomRoutingOptions.DefaultNeighbourQuality"/> — byte-for-byte the prior
    /// behaviour. When supplied, it overrides that default for routes learned on this port,
    /// so a mixed-grade node advertises an accurate per-port quality. Clamped to 0..255.
    /// The cached neighbour path quality is refreshed to this value on every ingest, so a
    /// per-port quality change (or a neighbour moving ports) takes effect on the next broadcast.
    /// </param>
    /// <param name="minQuality">
    /// The worst quality a learned route may have and still be kept on this port (the BPQ
    /// per-port <c>MINQUAL</c>). <c>null</c> (the default) ⇒ the table-wide
    /// <see cref="NetRomRoutingOptions.MinQuality"/> — byte-for-byte the prior behaviour. When
    /// supplied, it overrides that floor for routes <em>learned via this ingest</em> (a NODES
    /// broadcast heard on this port), so a node can keep only high-grade routes off a busy port
    /// (e.g. <c>MINQUAL 100</c> on RF) while keeping everything off another. Clamped to 0..255.
    /// A re-advertisement that derives below the effective floor removes an existing route, exactly
    /// as the table-wide floor does. Note this gates the <em>direct</em> route to the originator
    /// too — a neighbour whose own assumed path quality is below the floor is not kept.
    /// </param>
    public void Ingest(Callsign originator, Callsign myCall, string portId, NodesBroadcast broadcast, int? neighbourQuality = null, int? minQuality = null)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        ArgumentNullException.ThrowIfNull(portId);

        var now = timeProvider.GetUtcNow();
        byte pathQuality = (byte)Math.Clamp(neighbourQuality ?? options.DefaultNeighbourQuality, NetRomQuality.Min, NetRomQuality.Max);
        // The effective MINQUAL floor for this ingest: the per-port override if supplied,
        // else the table-wide default. Clamped defensively to 0..255 (validation already
        // rejects out-of-range, but a clamp keeps the floor comparison total).
        int floor = Math.Clamp(minQuality ?? options.MinQuality, NetRomQuality.Min, NetRomQuality.Max);

        lock (gate)
        {
            // Heuristic 3: ensure a neighbour-list entry for the originator, created with
            // the (per-port or default) path quality. Refresh its alias + last-heard each
            // time — and refresh its path quality too, so a per-port QUALITY change (or a
            // neighbour newly heard on a different-grade port) reflects on the next broadcast.
            if (!neighbours.TryGetValue(originator, out var nbr))
            {
                nbr = new NeighbourState { PathQuality = pathQuality };
                neighbours[originator] = nbr;
            }
            nbr.Alias = broadcast.SenderAlias;
            nbr.PortId = portId;
            nbr.PathQuality = pathQuality;
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
                quality: originatorPathQuality,
                minQuality: floor);

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
                    quality: quality,
                    minQuality: floor);
            }
        }
    }

    /// <summary>
    /// Ingest an INP3 <see cref="Inp3Rif"/> heard on a connected interlink from
    /// <paramref name="receivedFromNeighbour"/>, learning a measured <em>target-time</em>
    /// route (the second metric space) per RIP. This is the time-space analogue of
    /// <see cref="Ingest(Callsign, Callsign, string, NodesBroadcast, int?, int?)"/> for the quality
    /// space: it mirrors <see cref="UpsertRoute"/>'s discipline (per-destination route
    /// cap, best-first ordering, the trivial-loop guard) and is pure table maintenance —
    /// it never transmits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Host-free.</b> The table never reaches for the INP3 engine; the caller (the
    /// node host) supplies the smoothed neighbour transport time
    /// (<paramref name="neighbourSnttMs"/>) it read from <c>Inp3Engine</c>, exactly as
    /// <see cref="Ingest(Callsign, Callsign, string, NodesBroadcast, int?, int?)"/> takes
    /// <c>myCall</c>/<c>portId</c> rather than reaching for them.
    /// </para>
    /// <para>
    /// <b>Per-RIP math</b> (design §2.2 / §5.2). For each RIP, the local INP3 metric for
    /// its destination <em>via <paramref name="receivedFromNeighbour"/></em> is
    /// <c>localTargetTimeMs = rip.TargetTimeMs + neighbourSnttMs + 10</c> (the peer's
    /// advertised target time, plus the measured cost of this link, plus a fixed
    /// <see cref="PerHopIncrementMs"/> per-hop increment that keeps target time strictly
    /// increasing per hop even across a ~0 ms link) and <c>localHopCount = rip.HopCount + 1</c>.
    /// </para>
    /// <para>
    /// <b>Horizon = withdrawal</b> (design §2.3). If the RIP is at/over the 600 s horizon
    /// (<see cref="Inp3Rip.IsHorizon"/>), or the computed <c>localTargetTimeMs</c> reaches
    /// the horizon, the INP3 metric for <c>(destination via receivedFromNeighbour)</c> is
    /// <em>withdrawn</em> — its <see cref="Inp3RouteMetric"/> is cleared, leaving any
    /// coexisting quality route intact; a route then left with neither a usable quality
    /// nor an INP3 metric is removed, and a destination left with no route is removed.
    /// Withdrawal feeds the same next-decision failover as <see cref="MarkNeighbourDown"/>.
    /// </para>
    /// <para>
    /// <b>Skips</b> (no learn, no withdraw). A RIP is skipped when the link cost is not yet
    /// measured (<paramref name="neighbourSnttMs"/> == <see cref="Inp3Sntt.Unset"/> — an
    /// un-probed link must never <em>remove</em> a time-route it never learned), when
    /// <c>localHopCount</c> exceeds <paramref name="hopLimit"/> (the hop horizon), or when
    /// the destination is <paramref name="myCall"/> (the receive-side trivial-loop guard,
    /// mirroring <see cref="Ingest(Callsign, Callsign, string, NodesBroadcast, int?, int?)"/>).
    /// </para>
    /// <para>
    /// <b>Coexistence (does not disturb quality ingestion).</b> An INP3 upsert only sets
    /// the <see cref="Inp3RouteMetric"/> on the <c>(dest via neighbour)</c> route, creating
    /// the route as a pure time-route (quality 0) if none existed, or attaching the metric
    /// to an existing quality route without touching its quality/obsolescence. The
    /// per-destination cap evicts by quality (an INP3-only route counts as quality 0 for
    /// eviction ordering only — design AMBIGUITY-I3-2), so a node that never prefers INP3
    /// routes evicts byte-identically to today. Best INP3 route per destination = lowest
    /// <see cref="Inp3RouteMetric.TargetTimeMs"/>, then lowest hop count, then neighbour
    /// callsign (projected by <see cref="Snapshot"/>).
    /// </para>
    /// </remarks>
    /// <param name="receivedFromNeighbour">The interlink neighbour the RIF arrived on — the
    /// next-hop (via) for every route this RIF teaches.</param>
    /// <param name="myCall">Our own node callsign — a RIP whose destination is us is skipped
    /// (the trivial-loop guard).</param>
    /// <param name="neighbourSnttMs">The smoothed transport time to
    /// <paramref name="receivedFromNeighbour"/> in milliseconds (<c>Inp3Sntt.Ms</c>);
    /// <see cref="Inp3Sntt.Unset"/> (<c>uint.MaxValue</c>) means "no measurement yet" — every
    /// RIP is then skipped (no time-route learned, none withdrawn).</param>
    /// <param name="rif">The parsed RIF (the I-1 wire type).</param>
    /// <param name="hopLimit">The maximum learned hop count (the hop horizon, plan §8
    /// <c>hopLimit</c>, canonical 30): a RIP whose <c>localHopCount</c> exceeds this is not
    /// learned. Values &lt; 1 are treated as 1.</param>
    public void IngestRif(
        Callsign receivedFromNeighbour,
        Callsign myCall,
        uint neighbourSnttMs,
        Inp3Rif rif,
        int hopLimit = DefaultHopLimit)
    {
        ArgumentNullException.ThrowIfNull(rif);
        int effectiveHopLimit = Math.Max(1, hopLimit);

        lock (gate)
        {
            // An un-probed link has no measured cost — learn no time-route, and (crucially)
            // withdraw none either: an Unset SNTT must never remove a route it never taught.
            bool linkMeasured = neighbourSnttMs != Inp3Sntt.Unset;

            foreach (var rip in rif.Rips)
            {
                // localTargetTimeMs = peer target + this link's measured cost + per-hop floor;
                // computed in long to keep the horizon comparison overflow-free even if the
                // peer advertised right up against the horizon (SNTT is clamped to ≤ 600_000).
                long localTargetTime = (long)rip.TargetTimeMs + neighbourSnttMs + PerHopIncrementMs;

                // Horizon = withdrawal (clears the INP3 metric only), independent of the SNTT
                // measurement — a peer advertising the horizon withdraws regardless. The
                // computed-over-horizon case only applies once the link is measured (an Unset
                // SNTT would trivially overflow the horizon, which we must NOT treat as a
                // withdrawal — hence the linkMeasured guard on the second clause).
                if (rip.IsHorizon || (linkMeasured && localTargetTime >= Inp3Rip.HorizonMs))
                {
                    WithdrawInp3(rip.Destination, receivedFromNeighbour);
                    continue;
                }

                if (!linkMeasured)
                {
                    continue;   // link cost unknown — learn no time-route (and withdrew none)
                }

                int localHopCount = rip.HopCount + 1;
                if (localHopCount > effectiveHopLimit)
                {
                    continue;   // hop horizon — path too long to learn
                }

                if (rip.Destination.Equals(myCall))
                {
                    continue;   // trivial-loop guard: a route to ourselves is never learned
                }

                UpsertInp3Route(
                    destination: rip.Destination,
                    alias: rip.Alias ?? string.Empty,
                    viaNeighbour: receivedFromNeighbour,
                    metric: new Inp3RouteMetric((int)localTargetTime, (byte)Math.Min(localHopCount, byte.MaxValue)));
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
                // Did this destination hold an Inp3-bearing route before the sweep? (Cheaper
                // to test before than to diff after, and the predicate is pre-mutation.)
                bool hadInp3Before = false;
                foreach (var route in dest.Routes.Values)
                {
                    if (route.Inp3 is not null)
                    {
                        hadInp3Before = true;
                        break;
                    }
                }

                var survivors = new Dictionary<Callsign, RouteState>(dest.Routes.Count);
                bool hasInp3After = false;
                foreach (var (via, route) in dest.Routes)
                {
                    int next = route.Obsolescence - 1;
                    if (next <= 0)
                    {
                        purged++;
                        continue;
                    }
                    survivors[via] = route with { Obsolescence = next };
                    if (route.Inp3 is not null)
                    {
                        hasInp3After = true;
                    }
                }
                dest.Routes = survivors;
                if (survivors.Count == 0)
                {
                    emptyDestinations.Add(destCall);
                }

                // Invariant (W): an Inp3-bearing destination whose last time-route aged out
                // this sweep leaves the INP3 space → record the one-shot horizon withdrawal.
                // Guarded on "had an Inp3 route before," so a quality-only sweep (INP3 off)
                // never touches the set (the default-off guard, design §7.1).
                if (hadInp3Before && !hasInp3After)
                {
                    recentlyWithdrawn.Add(destCall);
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
    /// React to a neighbour going down — its interlink could not be raised (it did
    /// not answer the connect) or its quality collapsed — by immediately dropping
    /// every route that forwards through it, and the neighbour entry itself. This is
    /// the explicit link-down failover signal: instead of waiting for the
    /// obsolescence <see cref="Sweep"/> to age the now-dead routes out over the
    /// broadcast interval (during which forwarding / connect-routing would keep
    /// choosing a route that can't carry traffic), the dead routes leave the table at
    /// once, so the very next forward or connect decision fails over to an alternate
    /// next hop. A destination that loses all its routes is removed; it and the
    /// neighbour re-learn naturally from the next NODES broadcast if the neighbour
    /// returns. Idempotent — marking an unknown / already-removed neighbour down is a
    /// no-op returning 0.
    /// </summary>
    /// <param name="neighbour">The neighbour whose routes to drop.</param>
    /// <returns>The number of routes dropped (across all destinations).</returns>
    public int MarkNeighbourDown(Callsign neighbour)
    {
        lock (gate)
        {
            int dropped = 0;
            var emptyDestinations = new List<Callsign>();

            foreach (var (destCall, dest) in destinations)
            {
                // Note whether the route we are about to drop carried an INP3 metric —
                // only then can dropping it cost the destination its last time-route.
                bool removedRouteHadInp3 =
                    dest.Routes.TryGetValue(neighbour, out var removed) && removed.Inp3 is not null;

                if (dest.Routes.Remove(neighbour))
                {
                    dropped++;
                }
                if (dest.Routes.Count == 0)
                {
                    emptyDestinations.Add(destCall);
                }

                // Invariant (W): a destination that just lost its LAST Inp3-bearing route
                // leaves the INP3 time-space → record it for the one-shot horizon RIP. Guarded
                // on "the removed route carried an Inp3 metric," so a vanilla (quality-only)
                // MarkNeighbourDown — the L4 dial-failure path that runs with INP3 off — never
                // populates the set (the load-bearing default-off guard, design §7.1).
                if (removedRouteHadInp3 && !HasAnyInp3Route(destCall))
                {
                    recentlyWithdrawn.Add(destCall);
                }
            }

            foreach (var dc in emptyDestinations)
            {
                destinations.Remove(dc);
            }

            neighbours.Remove(neighbour);
            PruneOrphanNeighbours();
            return dropped;
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
                    .Select(r => new NetRomRoute(r.Neighbour, r.Quality, r.Obsolescence, r.Inp3))
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
    /// Replace the table's contents with a persisted <see cref="NetRomRoutingSnapshot"/>
    /// — the hydrate path a node host uses at startup to restore the routing table it
    /// had learned before a restart, so the node is not blind until the next NODES
    /// broadcast. Pure table maintenance (no I/O). Each restored route's obsolescence
    /// is reduced by <paramref name="obsolescenceDecay"/> (the number of broadcast
    /// intervals that elapsed while the node was down), so a route last refreshed long
    /// ago is not resurrected at full strength; a route decaying to ≤ 0 is dropped, a
    /// destination left with no routes is not restored, and a neighbour left with no
    /// route is pruned — matching what the elapsed <see cref="Sweep"/>s would have
    /// produced. Intended to run once on a fresh table before any ingest.
    /// </summary>
    /// <param name="snapshot">The persisted snapshot to load.</param>
    /// <param name="obsolescenceDecay">Obsolescence to subtract from every route
    /// (elapsed-downtime aging); 0 restores the snapshot verbatim.</param>
    public void Restore(NetRomRoutingSnapshot snapshot, int obsolescenceDecay = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var decay = Math.Max(0, obsolescenceDecay);

        lock (gate)
        {
            destinations.Clear();
            neighbours.Clear();

            foreach (var n in snapshot.Neighbours)
            {
                neighbours[n.Neighbour] = new NeighbourState
                {
                    Alias = n.Alias,
                    PortId = n.PortId,
                    PathQuality = n.PathQuality,
                    LastHeard = n.LastHeard,
                };
            }

            foreach (var d in snapshot.Destinations)
            {
                var routes = new Dictionary<Callsign, RouteState>(d.Routes.Count);
                foreach (var r in d.Routes)
                {
                    int obs = r.Obsolescence - decay;
                    if (obs <= 0)
                    {
                        continue;   // aged out during the downtime
                    }
                    routes[r.Neighbour] = new RouteState
                    {
                        Neighbour = r.Neighbour,
                        Quality = r.Quality,
                        Obsolescence = obs,
                    };
                }
                if (routes.Count == 0)
                {
                    continue;       // every route to this destination aged out
                }
                destinations[d.Destination] = new DestinationState
                {
                    Alias = d.Alias,
                    Routes = routes,
                };
            }

            // A neighbour whose only routes aged out is now an orphan — drop it, so the
            // restored table matches what the elapsed live Sweeps would have produced.
            PruneOrphanNeighbours();
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

    /// <summary>
    /// Build the per-neighbour, poison-reversed INP3 RIF to advertise <em>toward</em>
    /// <paramref name="toTargetNeighbour"/> — the INP3 (measured target-time) analogue of
    /// <see cref="BuildAdvertisement(int)"/> (the quality/NODES view). A pure read under the
    /// table lock; the host calls <see cref="Inp3Rif.ToBytes"/> on the result and wraps it in
    /// a PID-0xCF I-frame on the neighbour's interlink session. Host-free: it takes
    /// <paramref name="myCall"/> as a parameter (the table never reaches for identity — the
    /// same discipline as <see cref="IngestRif"/>). Advertisement is independent of the local
    /// <c>preferInp3Routes</c> forwarding knob: a destination is advertised iff we hold an INP3
    /// time-route for it, regardless of whether this node forwards by time or by quality. Locked
    /// design: <c>docs/netrom-inp3-i4-design.md</c> §1/§2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The RIF emits, in order (AMBIGUITY-I4-4 — deterministic + cross-stack byte-identical):
    /// </para>
    /// <list type="number">
    ///   <item><description><b>Our own node</b> — exactly one RIP for <paramref name="myCall"/>
    ///   at <b>target-time 0 ms, hop 0</b>, no TLVs. We are the source of ourselves (the cost
    ///   to reach us from us is zero, in zero hops); a neighbour ingesting this learns the
    ///   direct cost to us. The own-node RIP is <b>always</b> present and is <b>never poisoned</b>
    ///   — it is the source identity, not a learned route (design §1.1 rule 1, §2.2 exemption,
    ///   invariant (Source)).</description></item>
    ///   <item><description><b>Every destination D (≠ <paramref name="myCall"/>) holding a
    ///   <em>selected</em> INP3 time-route</b> — D's best (lowest-target-time) held route whose
    ///   <see cref="NetRomRoute.Inp3"/> is non-null, chosen independently of the local
    ///   <c>preferInp3Routes</c> forwarding knob (advertisement is not gated by the forwarding
    ///   preference) —
    ///   ordered by ascending local target time then destination callsign (ordinal). One RIP
    ///   each: hop = the selected route's <see cref="Inp3RouteMetric.HopCount"/>; target time =
    ///   <b>POISON-REVERSE</b>: if the selected route's next-hop neighbour <em>is</em>
    ///   <paramref name="toTargetNeighbour"/> the RIP is advertised at the
    ///   <see cref="Inp3Rip.HorizonMs"/> (unreachable — breaks the would-be two-hop loop, design
    ///   §2, invariant (P)); otherwise at the route's real local target time, quantised to the
    ///   10 ms wire granule. No TLVs (alias emission gated off, AMBIGUITY-I4-1).</description></item>
    /// </list>
    /// <para>
    /// Quality-only destinations (no INP3 route) are <b>not</b> in the RIF — they are carried
    /// by NODES. A destination's "selected" route mirrors
    /// <see cref="BuildAdvertisement(int)"/> advertising each destination's <em>best</em> route
    /// (not every kept route), so poison-reverse is a clean per-destination decision (one
    /// selected next-hop to compare against N). Construction stays strict: every emitted target
    /// time is in <c>[0, HorizonMs]</c> (a poisoned RIP is exactly <see cref="Inp3Rip.HorizonMs"/>,
    /// the own node is 0/0, and every learned finite metric is <c>&lt; HorizonMs</c> by the I-3
    /// storage invariant) so <see cref="Inp3Rip.Write"/> never throws on emitter output.
    /// </para>
    /// </remarks>
    /// <param name="myCall">Our own node callsign — emitted as the source RIP (target-time 0 /
    /// hop 0, never poisoned) and used as the loop-guard identity (a route whose destination is
    /// us is never in the RIF).</param>
    /// <param name="toTargetNeighbour">N — the neighbour this RIF is built <em>for</em>: any
    /// destination whose selected route is via N is poison-reversed (advertised at the horizon)
    /// in this RIF (design §1.4).</param>
    /// <param name="recentlyWithdrawn">The recently-withdrawn snapshot the host
    /// <see cref="DrainRecentlyWithdrawn"/>ed once at the start of this fan-out round and passes to
    /// every neighbour's RIF — one explicit horizon RIP is appended per entry (minus any that were
    /// re-learned finite this round, and our own node). <c>null</c> (or empty) appends no
    /// withdrawal RIPs (the default for callers that don't drive the withdrawn set, e.g. unit
    /// tests of pure poison-reverse). Passing the host-drained snapshot — not reading the live set
    /// — is what makes the fan-out race-free (design §6.4).</param>
    /// <returns>The poison-reversed INP3 RIF to advertise toward
    /// <paramref name="toTargetNeighbour"/>.</returns>
    public Inp3Rif BuildRif(Callsign myCall, Callsign toTargetNeighbour, IReadOnlyCollection<Callsign>? recentlyWithdrawn = null)
    {
        lock (gate)
        {
            // The destination RIPs, ordered by ascending local target time then callsign
            // (AMBIGUITY-I4-4). We sort by the *real* local target time (stable across the
            // neighbour the RIF is built for), not the poison-overridden value, so the RIP
            // order is identical in every neighbour's RIF given identical state.
            var destRips = new List<(Inp3Rip Rip, int LocalTargetTimeMs)>();

            foreach (var (destCall, dest) in destinations)
            {
                if (destCall.Equals(myCall))
                {
                    continue;   // our own node is emitted as the 0/0 source RIP below, never as a learned route.
                }

                // We ADVERTISE a destination iff we HOLD an INP3 time-route for it (design §1) —
                // independent of preferInp3Routes, which is a local *forwarding* preference, not
                // an advertisement gate. (A node that forwards by quality should still tell its
                // neighbours the time it can reach D in, so they can route to us by time.) Pick
                // our best (lowest-target-time) INP3 route as the advertised metric, and note
                // whether the neighbour we are building toward is ANY of D's kept next hops.
                Inp3RouteMetric? bestInp3 = null;
                bool poison = false;
                foreach (var r in dest.Routes.Values)
                {
                    if (r.Neighbour.Equals(toTargetNeighbour))
                    {
                        poison = true;
                    }
                    if (r.Inp3 is { } m && (bestInp3 is null || m.TargetTimeMs < bestInp3.TargetTimeMs))
                    {
                        bestInp3 = m;
                    }
                }

                if (bestInp3 is not { } inp3)
                {
                    continue;   // no INP3 route held → carried by NODES (quality), not the RIF.
                }

                // POISON-REVERSE (design §2, loop-safety): advertise D back at the horizon
                // (unreachable) if the neighbour we are building this RIF for is ANY of D's kept
                // forwarding next hops — not merely D's *best* INP3 next hop. The shipped
                // multi-route load-balancer (NetRomForwarding.SelectWeighted, PerFlow default)
                // spreads D's traffic across every kept route, so advertising D back at a finite
                // metric to any neighbour we'd forward D through seeds a two-hop loop (that
                // neighbour installs D-via-us, then bounces D's share back). Split-horizon over
                // the full kept-route set is the safe rule; over-poisoning at worst costs a
                // backup-path advertisement (which alternate-reverse would recover — deferred).
                int advertisedTargetTimeMs = poison
                    ? Inp3Rip.HorizonMs
                    : Quantise10(inp3.TargetTimeMs);

                destRips.Add((
                    new Inp3Rip
                    {
                        Destination = destCall,
                        HopCount = inp3.HopCount,
                        TargetTimeMs = advertisedTargetTimeMs,
                        Tlvs = [],   // alias TLV emission gated OFF (AMBIGUITY-I4-1)
                    },
                    inp3.TargetTimeMs));
            }

            var ordered = destRips
                .OrderBy(x => x.LocalTargetTimeMs)
                .ThenBy(x => x.Rip.Destination.ToString(), StringComparer.Ordinal)
                .Select(x => x.Rip)
                .ToList();

            // Own-node RIP first (the source seed: 0/0, no TLVs, never poisoned), then the
            // ordered destination RIPs.
            int withdrawnCount = recentlyWithdrawn?.Count ?? 0;
            var rips = new List<Inp3Rip>(ordered.Count + withdrawnCount + 1)
            {
                new()
                {
                    Destination = myCall,
                    HopCount = 0,
                    TargetTimeMs = 0,
                    Tlvs = [],
                },
            };
            rips.AddRange(ordered);

            // Invariant (W): append one explicit horizon RIP per recently-withdrawn destination so
            // the peer withdraws it immediately (rather than waiting for its obsolescence sweep).
            // The set is the snapshot the host DRAINED once at the start of this fan-out round and
            // passes to every neighbour's BuildRif, so every neighbour's RIF carries the withdrawal
            // exactly once (the host-thread race fix — design §6.4). A destination that was
            // withdrawn-then-relearned in the same round is carried by its FINITE RIP above (it's in
            // `emitted`), not poisoned; and our own node is never withdrawn (the Source invariant).
            if (withdrawnCount > 0)
            {
                var emitted = new HashSet<Callsign>(ordered.Select(r => r.Destination));
                foreach (var wd in recentlyWithdrawn!.OrderBy(c => c.ToString(), StringComparer.Ordinal))
                {
                    if (wd.Equals(myCall) || emitted.Contains(wd))
                    {
                        continue;
                    }
                    rips.Add(new Inp3Rip
                    {
                        Destination = wd,
                        HopCount = 0,
                        TargetTimeMs = Inp3Rip.HorizonMs,
                        Tlvs = [],
                    });
                }
            }

            return new Inp3Rif { Rips = rips };
        }
    }

    /// <summary>
    /// A read-only <b>peek</b> at the recently-withdrawn destinations (INP3 invariant W) —
    /// destinations that have lost their last <see cref="Inp3RouteMetric"/>-bearing route since the
    /// host last <see cref="DrainRecentlyWithdrawn"/>ed the set. Does <b>not</b> clear — for tests
    /// and monitoring only. The host never reads this on the fan-out path; it
    /// <see cref="DrainRecentlyWithdrawn"/>s once at the start of a round and hands the snapshot to
    /// each neighbour's <see cref="BuildRif"/> (so reading and clearing are a single atomic step,
    /// closing the host-thread race). Stable ordinal ordering for deterministic, cross-stack
    /// comparison.
    /// </summary>
    public IReadOnlyList<Callsign> RecentlyWithdrawn()
    {
        lock (gate)
        {
            return recentlyWithdrawn
                .OrderBy(c => c.ToString(), StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>
    /// Atomically snapshot <b>and clear</b> the recently-withdrawn set (INP3 invariant W) under a
    /// single <c>gate</c> hold. The host calls this <b>once</b> at the start of a fan-out round and
    /// hands the returned snapshot to every neighbour's <see cref="BuildRif"/> — so the one-shot
    /// horizon RIP reaches each neighbour exactly once. Draining as one atomic step (rather than the
    /// old read-then-clear-after-round) closes the host-thread race: a concurrent
    /// <see cref="IngestRif"/> / <see cref="MarkNeighbourDown"/> / <see cref="Sweep"/> that withdraws
    /// a destination mid-round lands in the live set AFTER this snapshot was taken, so it is captured
    /// by the NEXT round's drain instead of being cleared unadvertised. Stable ordinal ordering for
    /// deterministic, cross-stack-comparable RIFs; an empty list when nothing is pending.
    /// </summary>
    public IReadOnlyList<Callsign> DrainRecentlyWithdrawn()
    {
        lock (gate)
        {
            if (recentlyWithdrawn.Count == 0)
            {
                return Array.Empty<Callsign>();
            }
            var snapshot = recentlyWithdrawn
                .OrderBy(c => c.ToString(), StringComparer.Ordinal)
                .ToList();
            recentlyWithdrawn.Clear();
            return snapshot;
        }
    }

    // Quantise a full-ms local target time down to the 10 ms wire granule the RIP codec
    // carries (the stored metric is full-ms — the granule is an emission-only concern,
    // design I-3 AMBIGUITY-I3-3). Floor, so the emitted finite time never exceeds the
    // stored one; clamped to one granule below the horizon so a near-horizon finite metric
    // can never round up to read as a withdrawal.
    private static int Quantise10(int targetTimeMs)
    {
        int quantised = (targetTimeMs / 10) * 10;
        return Math.Min(quantised, Inp3Rip.HorizonMs - 10);
    }

    // ─── Internals ────────────────────────────────────────────────────

    // Add or refresh a route to `destination` via `viaNeighbour`. Applies the
    // quality-0 / MINQUAL floor (heuristic 8) — using the caller-supplied effective
    // floor, which is the per-port MINQUAL when this ingest carried one, else the
    // table-wide options.MinQuality — resets obsolescence to OBSINIT, enforces the
    // per-destination route cap (heuristic 7) and the destination cap (heuristic 9).
    // Caller holds the lock.
    private void UpsertRoute(Callsign destination, string alias, Callsign viaNeighbour, byte quality, int minQuality)
    {
        // A quality-0 route is never usable / kept; likewise anything under the
        // effective floor. If such a route already existed (from a prior, better
        // advertisement), a now-too-low re-advertisement removes it.
        bool acceptable = quality > NetRomQuality.Min && quality >= minQuality;

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

        // Preserve any INP3 metric already learned for this (dest via neighbour)
        // route — a NODES quality refresh must not wipe a coexisting time-route
        // (the two metric spaces are independent; see IngestRif).
        dest.Routes.TryGetValue(viaNeighbour, out var existing);
        dest.Routes[viaNeighbour] = new RouteState
        {
            Neighbour = viaNeighbour,
            Quality = quality,
            Obsolescence = options.ObsoleteInitial,
            Inp3 = existing?.Inp3,
        };

        EnforceRouteCap(dest);
    }

    // Heuristic 7 (and its INP3 analogue): keep only the N best routes per
    // destination. When the cap is exceeded, evict by the SAME key the quality
    // selection orders by — lowest-quality-first, ties by neighbour callsign — so a
    // node that never prefers INP3 routes evicts byte-identically to today; an
    // INP3-only route (quality 0) sorts as a quality-0 route for eviction ordering
    // only (design AMBIGUITY-I3-2). Caller holds the lock.
    private void EnforceRouteCap(DestinationState dest)
    {
        if (dest.Routes.Count <= options.MaxRoutesPerDestination)
        {
            return;
        }

        var keep = dest.Routes.Values
            .OrderByDescending(r => r.Quality)
            .ThenBy(r => r.Neighbour.ToString(), StringComparer.Ordinal)
            .Take(options.MaxRoutesPerDestination)
            .ToDictionary(r => r.Neighbour);
        dest.Routes = keep;
    }

    // Attach (or refresh) an INP3 time-route metric on the (destination via
    // viaNeighbour) route — the time-space analogue of UpsertRoute. If the route
    // already exists (as a quality route, or a prior time-route) the metric is set in
    // place, preserving its quality + obsolescence; if it does not exist the route is
    // created as a pure time-route (quality 0, obsolescence OBSINIT). The per-dest cap
    // is then enforced by the same quality-first eviction key as the quality path
    // (AMBIGUITY-I3-2). Honours the destination cap exactly as UpsertRoute does. Caller
    // holds the lock. (Floor/horizon/hop/loop gating is done by IngestRif before here,
    // so this only ever stores a live, finite, in-horizon metric.)
    private void UpsertInp3Route(Callsign destination, string alias, Callsign viaNeighbour, Inp3RouteMetric metric)
    {
        if (!destinations.TryGetValue(destination, out var dest))
        {
            if (destinations.Count >= options.MaxDestinations)
            {
                return;   // heuristic 9: destination list full — ignore new destinations
            }
            dest = new DestinationState { Alias = alias };
            destinations[destination] = dest;
        }
        else if (!string.IsNullOrEmpty(alias))
        {
            dest.Alias = alias;
        }

        if (dest.Routes.TryGetValue(viaNeighbour, out var existing))
        {
            // Refresh the time-route in place: keep the route's quality (its other
            // metric space) and reset obsolescence so the time-route ages like a
            // quality route refreshed by a NODES broadcast.
            dest.Routes[viaNeighbour] = existing with
            {
                Obsolescence = options.ObsoleteInitial,
                Inp3 = metric,
            };
        }
        else
        {
            // A brand-new route known only via INP3: quality 0 (no NODES quality), the
            // time metric carrying its reachability. Quality 0 means it is invisible to
            // the quality path / never advertised, exactly as intended.
            dest.Routes[viaNeighbour] = new RouteState
            {
                Neighbour = viaNeighbour,
                Quality = (byte)NetRomQuality.Min,
                Obsolescence = options.ObsoleteInitial,
                Inp3 = metric,
            };
        }

        EnforceRouteCap(dest);
    }

    // Withdraw the INP3 metric of the (destination via viaNeighbour) route (a horizon
    // withdrawal). Clears Inp3 only — a coexisting quality route stays. A route left
    // with neither a usable quality (≤ MINQUAL / 0) nor an INP3 metric is removed; a
    // destination left with no route is removed. A no-op if the route / destination is
    // unknown or the route had no INP3 metric. Caller holds the lock.
    private void WithdrawInp3(Callsign destination, Callsign viaNeighbour)
    {
        if (!destinations.TryGetValue(destination, out var dest))
        {
            return;
        }
        if (!dest.Routes.TryGetValue(viaNeighbour, out var route) || route.Inp3 is null)
        {
            return;   // nothing INP3 to withdraw on this route
        }

        // A route whose only reason to exist was its (now-withdrawn) time metric — i.e.
        // it carries no usable quality — is removed outright; otherwise it survives as a
        // pure quality route with Inp3 cleared.
        bool hasUsableQuality = route.Quality > NetRomQuality.Min && route.Quality >= options.MinQuality;
        if (hasUsableQuality)
        {
            dest.Routes[viaNeighbour] = route with { Inp3 = null };
        }
        else
        {
            dest.Routes.Remove(viaNeighbour);
            if (dest.Routes.Count == 0)
            {
                destinations.Remove(destination);
            }
        }

        // Invariant (W): if the destination now holds NO Inp3-bearing route at all, it has
        // left the INP3 time-space — record it so the next RIF to every neighbour carries a
        // one-shot horizon withdrawal. (We had an Inp3 metric on this route a moment ago, so
        // this add is only ever reached on a genuine INP3 withdrawal.)
        if (!HasAnyInp3Route(destination))
        {
            recentlyWithdrawn.Add(destination);
        }
    }

    // True iff some kept route to `destination` still carries an Inp3 metric. A destination
    // that is gone from the table holds no route, so it has no Inp3 route either. The
    // "lost its LAST INP3 route" predicate for invariant (W) (design §6.3). Caller holds the lock.
    private bool HasAnyInp3Route(Callsign destination)
    {
        if (!destinations.TryGetValue(destination, out var dest))
        {
            return false;
        }
        foreach (var route in dest.Routes.Values)
        {
            if (route.Inp3 is not null)
            {
                return true;
            }
        }
        return false;
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

        /// <summary>
        /// The optional INP3 metric (measured target time + hop count) learned from a
        /// RIF, or <c>null</c> if this route was only ever learned from NODES. The
        /// second metric space (lowest-time-best), independent of <see cref="Quality"/>:
        /// one route can carry both. Cleared (set <c>null</c>) on a horizon withdrawal
        /// without disturbing the quality metric.
        /// </summary>
        public Inp3RouteMetric? Inp3 { get; init; }
    }

    private sealed class NeighbourState
    {
        public string Alias { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
        public byte PathQuality { get; set; }
        public DateTimeOffset LastHeard { get; set; }
    }
}
