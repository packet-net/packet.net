# NET/ROM neighbours and interlinks keyed per (port, callsign)

Issue [#725](https://github.com/packet-net/packet.net/issues/725), umbrella [#726](https://github.com/packet-net/packet.net/issues/726). Evidence: the 2026-08-17 multi-port architecture assessment (`portmap-evidence.md`, identity-routing section). Shipped as port-concept PC4; the amendment-log entry is [`plan.md`](plan.md) §17.

## 1. Problem

A NET/ROM neighbour used to be identified by callsign alone, node-wide, in four places:

| Where | Key |
|---|---|
| routing table neighbours | `Dictionary<Callsign, NeighbourState>` |
| a destination's routes | `Dictionary<Callsign, RouteState>` |
| live interlinks | `ConcurrentDictionary<Callsign, Interlink>` |
| persisted neighbours | `callsign TEXT PRIMARY KEY` |

`PortId` was a mutable payload field, rewritten on every ingest (`nbr.PortId = portId; nbr.PathQuality = pathQuality;`), with the comment already conceding "a neighbour newly heard on a different-grade port".

Operational consequences on a multi-port node:

1. **Per-port QUALITY ([#455](https://github.com/packet-net/packet.net/issues/455)) was defeated for exactly the neighbours it exists for.** GB7RDG runs port 1 at `QUALITY=191`, ports 2 and 6 at `192`, port 9 at `191`. A neighbour audible on 2m and on the AXUDP backbone got whichever quality arrived last, and every derived route quality (`NetRomQuality.Combine`) inherited that flap. The node's NODES advert therefore lied about half the time, which is the whole point of the feature.
2. **One interlink per neighbour.** The session tap `TryAdd`ed by callsign, so the first port to carry a 0xCF datagram owned the neighbour. A dual-homed backbone peer got no port diversity and no failover: when that port died, `MarkNeighbourDown` dropped **all** routes via that callsign, including the ones the other band could still carry.
3. **Nondeterministic egress.** `EnsureInterlinkAsync` read a first-match-by-callsign neighbour scan, then fell back to `attachments.Values.FirstOrDefault()` over a `ConcurrentDictionary`. The chosen port decided the SABM's band, the capability-cache key and the recorded interlink.
4. **The store enforced the defect.** `Save` is delete-then-insert; the moment the in-memory table could hold two rows for one callsign it would throw a UNIQUE violation, caught and swallowed (silent loss of persistence). Schema and key had to land together.

## 2. Reference semantics (BPQ, Linux)

LinBPQ (local clone `/home/tf/src/linbpq`, plan §13.2) is unambiguous: **a neighbour is a (callsign, port) pair.**

- `struct ROUTE` carries `NEIGHBOUR_CALL[7]`, `NEIGHBOUR_PORT`, `NEIGHBOUR_QUAL`, its own `NEIGHBOUR_LINK` (the L2 session), its own `SRTT`/`RTT`/`RTTIncrement` (the INP3 SNTT), its own `noV2point2` dial hint and its own `LastConnectAttempt` (`asmstrucs.h:208-266`).
- Lookup is keyed on both: `FindNeighbour(Call, Port)` skips every entry whose `NEIGHBOUR_PORT != Port` before comparing callsigns (`cMain.c:1685-1717`). NODES ingest calls it with the receiving port and creates the entry at `ROUTE->NEIGHBOUR_QUAL = PORT->PORTQUALITY` (`L3Code.c:340-376`); the connected-mode path does the same from the link's port (`L3Code.c:1476-1487`).
- A destination holds `NRROUTE[3]` plus `INP3ROUTE[3]`, each a **pointer to a ROUTE** with its own quality/obscount (`asmstrucs.h:482-514`). Insertion compares pointer identity (`PROCROUTES`, `L3Code.c:629-632`), so the same callsign on two ports occupies two of the three slots, each with its own quality.
- The sysop surfaces show the port per row: `ROUTES` prints `port call qual nodecount ...` (`Cmd.c:1935-1960`) and the `ROUTES <call> <port> <qual>` command requires the port ("Port Number Missing", `Cmd.c:2037-2055`). `NODES <call>` prints each route as `quality obscount port neighbour` (`Cmd.c:3325-3332`), and the INP3 routes likewise (`:3345-3356`).
- **Concretely.** On a multi-port BPQ node with `GB7XYZ` heard on ports 2 and 6, `ROUTES` shows two rows (`2 GB7XYZ 192 ...` and `6 GB7XYZ 192 ...`), each with its port's `QUALITY`; `NODES GB7XYZ` shows two of the three route slots pointing at the same callsign on different ports. The **NODES broadcast is one entry per destination**, carrying dest call, dest alias, `NRROUTE[0]`'s neighbour callsign and its quality (`L3Code.c:917-951`). The port is never on the wire. Two per-port adjustments do apply on transmit and are *not* what pdn does: `TXMINQUAL = PORT->PORTMINQUAL` gates which destinations go out that port (`L3Code.c:846-849`), and `QUAL_ADJUST` shaves the advertised quality when the best neighbour is on the port being sent to (`L3Code.c:940-946`). Both are out of scope here - see §10 and [#732](https://github.com/packet-net/packet.net/issues/732).
- INP3: every RIF/L3RTT function takes a `struct ROUTE *` (`BPQINP3.c:392 ProcessINP3RIF`, `:496 UpdateNode`, `:737 AddHere`), so target-time state is per (call, port), and `DEST->RouteLastTT` is sized `MAXNEIGHBOURS` (`asmstrucs.h:521`).

Linux kernel `net/netrom/nr_route.c` agrees: `struct nr_neigh` carries `callsign`, `digipeat`, `dev` and `quality`, and neighbour lookup matches callsign **and** device; a `struct nr_node` holds up to three routes, each pointing at an `nr_neigh` with its own quality and obs count. `/proc/net/nr_neigh` prints one row per (callsign, device).

Verdict: both references key exactly as #725 proposes. pdn was the outlier.

## 3. The model

The value type in `Packet.NetRom.Routing`:

```csharp
public readonly record struct NeighbourKey(string PortId, Callsign Callsign)
{
    public bool Equals(NeighbourKey other) =>
        string.Equals(PortId, other.PortId, StringComparison.Ordinal) && Callsign.Equals(other.Callsign);
    public override int GetHashCode() =>
        HashCode.Combine(PortId is null ? 0 : StringComparer.Ordinal.GetHashCode(PortId), Callsign);
}
```

Changes in `NetRomRoutingTable`:

- `Dictionary<NeighbourKey, NeighbourState> neighbours`; `NeighbourState` loses `PortId` (it is in the key) and keeps `Alias`, `PathQuality`, `LastHeard`.
- `DestinationState.Routes` becomes `Dictionary<NeighbourKey, RouteState>`; `RouteState.Neighbour` becomes `NeighbourKey`. Obsolescence stays per route, so it is now per (dest, port, neighbour), matching BPQ's per-`NRROUTE[i]` `ROUT_OBSCOUNT`.
- `Ingest` keeps its signature (it already takes `portId`) and keys on `new NeighbourKey(portId, originator)`; `UpsertRoute` and `EnforceRouteCap` take the key.
- `IngestRif` gains `string portId` after `receivedFromNeighbour`; `WithdrawInp3`/`UpsertInp3Route` key by `NeighbourKey`.
- `MarkNeighbourDown(Callsign)` becomes `MarkNeighbourDown(NeighbourKey)`, so a failed dial on 2m no longer kills the 70cm routes. New `int MarkPortDown(string portId)` drops every neighbour and route on that port in one pass (the detach path, §4).
- `Restore` keys by `(route.PortId, route.Neighbour)`; `PruneOrphanNeighbours` compares the composite key.
- Read models (`NetRomRoutingModel.cs`): `NetRomRoute` gains the port - `record NetRomRoute(Callsign Neighbour, string PortId, byte Quality, int Obsolescence, Inp3RouteMetric? Inp3 = null)`, plus a `Key` convenience. `NetRomNeighbour` keeps its shape but the list now holds one row per (port, callsign); snapshot ordering gains `.ThenBy(n => n.PortId, StringComparer.Ordinal)`, and destination routes sort quality desc, port rank, callsign so the winner is deterministic.
- `NeighbourFor(Callsign)` splits into `NeighbourFor(NeighbourKey)`, `NeighboursOf(Callsign)` (all rows, best first) and `BestNeighbourFor(Callsign)`.
- `NetRomRoutingTable.BestNeighbourPort(Callsign)` is the allocation-free form of the same question, for the per-frame decisions (the INP3 selected-link rule, §4): a snapshot would materialise the whole table on every inbound interlink frame.

**Route selection.** Best quality wins across the per-port entries exactly as before (`Inp3RouteSelector.SelectActiveRoute`, `NetRomForwarding.Decide`); the only new rule is the tie-break. Ties break on canonical port order, then callsign ordinal. The library cannot know config order, so `NetRomRoutingOptions` gains `Func<string, int>? PortRank` defaulting to ordinal-string rank; the host wires it to PC2's canonical (configuration) ordering. `ForwardDecision` carries `NextHopPortId` (and a `NextHopKey`), because the caller now needs the port to pick the interlink.

**NODES broadcast: unchanged on the wire.** `BuildAdvertisement` still emits one entry per destination carrying the best route's neighbour **callsign** and quality; the port is not a wire field. The only change is that "best route" is now chosen over per-port entries with the tie-break above.

**INP3.** RIF ingest keys per (port, neighbour) in the table; SNTT stays per neighbour in this change (see §4 for the interim rule); obsolescence and withdrawal are per route, so invariant (W) fires only when a destination loses its last INP3-bearing route across **all** ports. `BuildRif`'s poison-reverse compares the selected route's `NeighbourKey.Callsign` against the target neighbour callsign, not the key: a route learned on another port to the same neighbour must still be poisoned toward it.

**Port down / port back.** `DetachPort` calls `MarkPortDown(portId)`: that port's neighbour rows and its routes leave at once, other ports' routes survive untouched, and a destination that loses its last route is removed. A neighbour that reappears on a different port is simply a different key, learned at that port's QUALITY on the first broadcast, with no flap and no overwrite of the old row (which ages out by obsolescence if the old port never returns).

## 4. Interlinks per (port, neighbour)

- `ConcurrentDictionary<NeighbourKey, Interlink> interlinks`; `Interlink` drops its `PortId` field (it is the key's).
- The session tap keys by port: `interlinks.TryAdd(new NeighbourKey(portId, peer), ...)`, and the disconnect branch removes the same key. `portId` is already in scope (`OnSessionAccepted(string portId, ...)`).
- `EnsureInterlinkAsync(NeighbourKey key, ...)`: the port comes from the key, so both the first-match neighbour scan and the `attachments.Values.FirstOrDefault()` fallback disappear. If the key's port is not attached, the dial **fails** rather than silently dialling another band. This supersedes PC2's interim deterministic-egress rule ([#723](https://github.com/packet-net/packet.net/issues/723) item 4), which is removed rather than layered on top.
- Egress: `TrySendOverInterlink` takes the selected route's key; `ForwardDatagram` and `SendNetRomPacket` pass the route's port. `SendNetRomPacket`'s "direct interlink to the destination node itself" shortcut becomes "any live interlink whose key callsign is the destination, best by the table's own preference then port rank".
- Failover: a dial that throws marks **only that key** down, so the next selection can pick the same callsign on another port. That is the port diversity the issue asks for.
- **INP3 interim rule (deliberate).** `Inp3Engine` neighbours and `Inp3UpdateScheduler.dirty` stay keyed by `Callsign` in this change. SNTT is a per-link measurement, so blending two links would corrupt it; the rule is therefore: the host observes, probes and ingests INP3 **only on the neighbour's selected interlink** (`NetRomService.SelectedInp3Port` - the port of its best adjacency, else its best live interlink), and a RIF or L3RTT arriving on a non-selected link is dropped with a log line. An L4 datagram is never gated by this rule. INP3 is default-off and enabled in no fixture (`netrom-inp3-interop.md` §2.1), so the exposure is bounded. Keying `Inp3Engine`/`Inp3UpdateScheduler` per (port, callsign) is the follow-up: [#733](https://github.com/packet-net/packet.net/issues/733).

## 5. L4 circuits: unaffected, and per-port keying would be wrong here

`CircuitManager` keys circuits `(byte Index, byte Id)` locally and `(Callsign Node, byte Index, byte Id)` by peer. A NET/ROM circuit is a **node-to-node** transport association; its datagrams are routed hop by hop and may legitimately change next hop, and therefore port, mid-circuit. Putting a port in the circuit key would tear a circuit down on exactly the failover this change exists to enable, and would not match BPQ (its L4 sessions key on node + index/id, with the port living on the ROUTE the datagram happens to take). The only circuit-adjacent change is the egress sink `SendNetRomPacket` choosing which interlink carries the datagram.

## 6. API, UI, MCP, OARC

- `PdnReadApi.BuildNetRomRoutes`: neighbour rows keep their shape but the array can now contain two rows with the same `neighbour` and different `portId`; each route object gains `portId` next to `neighbour`.
- `types.ts`: `NetRomRoute` gains `portId: string`. The contract fixture carries a dual-port neighbour sample (same callsign, two ports) so the shape change is exercised, with `mock.ts` matching.
- Routes screen (`routes.tsx`): the neighbour row key becomes `` `${n.portId}:${n.neighbour}` `` (duplicate React keys otherwise), and the `portOfNeighbour` first-match helper collapses to the route's own `portId`. The destinations table gains a Port column. The Connect hand-off stays free of `&port=` ([#727](https://github.com/packet-net/packet.net/issues/727)).
- MCP: the tool is `network_topology`. `McpRoute` gains `PortId`, mirrored in `LiveNodeMcpBackend` and the REST mirror DTO. `docs/mcp-design.md` says the tool returns "the `/netrom/routes` shape", so it stays true by construction.
- Counts: `/status` and the `pdn_netrom_neighbours` gauge now count **rows**, not callsigns (row semantics, matching `ROUTES`); the metric HELP text says so. It is a visible step change on a multi-port node and belongs in the release notes.
- `PdnReadApi.NeighbourCallsigns` stays deliberately port-blind: it classifies a session as interlink-vs-console, where the port is irrelevant.
- Console `NODES` already printed the port per neighbour; route lines now print `via CALL/port(quality,obsolescence)`, mirroring BPQ's `NODES <call>` output.
- OARC: `NodeOarcStateSource` reports only `L3Relayed` and circuits; no neighbour or destination is sent upstream, so it is **unaffected**.

## 7. Persistence

Schema v1 was `neighbour(callsign PRIMARY KEY, alias, port_id, path_quality, last_heard_utc)` and `route(dest_callsign, via_neighbour, quality, obsolescence, PRIMARY KEY (dest_callsign, via_neighbour))`.

Schema v2: `neighbour` PK becomes `(port_id, callsign)`; `route` gains `port_id NOT NULL` with PK `(dest_callsign, port_id, via_neighbour)`; `Load`/`Save` and the `RouteRow` DTO carry it.

Migration: `EnsureSchema` is a version **stamp**, not a runner, and `CREATE TABLE IF NOT EXISTS` no-ops on an existing table, so bumping `SchemaVersion` alone would leave the old callsign-PK table in place with the new stamp on it. Two options were considered:

1. **Drop and recreate at version 2 - chosen.** `DROP TABLE IF EXISTS route; ... destination; ... neighbour; ... meta;` then the new `SchemaSql`, all in one transaction. The table is a cache: it is fully re-learnt within one `NODESINTERVAL` (60 s at GB7RDG), and the code already argues exactly this for not persisting INP3 metrics. Cost is one broadcast interval of reduced table on the first boot after upgrade, on a node that just restarted anyway.
2. Backfill: `ALTER TABLE route ADD COLUMN port_id`, `UPDATE route SET port_id = (SELECT port_id FROM neighbour WHERE callsign = via_neighbour)`, rebuild `neighbour` with the new PK. Correct because a v1 route's neighbour has exactly one port, but three times the code for one interval of table.

Ordering constraint: the schema change is in the same commit as the in-memory key change, or `Save` starts throwing UNIQUE violations that are swallowed (silent loss of persistence).

## 8. Cross-stack

**This is not a fleet-consistency issue like the INP3 hop model.** Nothing here reaches the wire: a NODES entry has no port field (`L3Code.c:917-951` and our `NodesBroadcastBuilder.Entry`), and neither does a RIF. Contrast [`netrom-inp3-interop.md`](netrom-inp3-interop.md) §6.2, where the receiver-adds-cost model has to flip in all three stacks at once. Each stack can therefore land this independently; a mixed fleet behaves correctly throughout.

What still must be checked and filed:

- `ax25-ts` (`src/netrom/routing-table.ts`, interlinks in `src/netrom/connector.ts`, the free `neighbourFor`): the same change, cheap, since the maps are already string-keyed (key becomes `` `${portId}|${call}` `` or a nested map). **Separately** there is a pre-existing divergence found while surveying: `ingest()` there takes no `neighbourQuality`/`minQuality`, so per-port QUALITY/MINQUAL (#455/GB7RDG) was never mirrored to TS.
- `pico-node` (`crates/ax25-node-core/src/netrom/routing/table.rs`): the expensive one. Neighbours are `[Option<NeighbourState>; MAX_NBRS]` on `no_std`, so a composite key is a **memory budget change**: a callsign heard on N ports consumes N slots, and slot exhaustion is silent. `PortId` derives `PartialEq, Eq` but not `Hash`/`Ord`, so the tie-break needs `Ord`. File with an explicit "re-derate `MAX_NBRS`/`MAX_DESTS`" note.
- `scripts/parity-check.mjs` covers `Ax25ParseOptions`/quirks/listener surface only, not NET/ROM routing, so nothing fails automatically. The guard has to be the mirrored unit tests in each stack.

## 9. Migration and risk

GB7RDG is the worst case: ports 1 (2m, `QUALITY=191`, `MINQUAL=100`), 2 (70cm, 192/20), 3 (40m, no `QUALITY` so NET/ROM silent), 6 (6m, 192/100, `NODESPACLEN=160`), 7 (telnet), 9 (sim RF, 191/100), plus the multipoint AXUDP ports carrying GB7OUK, GB7BDH, GB7NDH, MB7NGP. Backbone nodes reachable over both the tunnel and 70cm are precisely the dual-audible case, and they used to flip-flop.

Regressions to watch:

- **Route-slot dilution.** `MaxRoutesPerDestination` is 3; one callsign on three ports can now fill all three, hiding genuine alternate next hops. BPQ has exactly this behaviour (`PROCROUTES`, `L3Code.c:629-632`), and mirroring the reference beat inventing a diversity heuristic. If the lab shows it hurts, the fix is to count distinct neighbour callsigns in the cap, not to abandon the key.
- **Two interlinks to one neighbour** doubles keepalive/RIF traffic to that peer and puts two AX.25 links up between the same pair of callsigns on different channels. BPQ does this natively (per-ROUTE `NEIGHBOUR_LINK`), so peers cope; it is still new on-air behaviour and should be watched during the rehearsal.
- **Neighbour counts step up** in `/status`, Prometheus and the routes screen.
- **First boot after upgrade starts with an empty routing store** (option 1 in §7).
- Neighbours see no change: the NODES advert is byte-identical apart from which port's quality feeds it, which is the bug being fixed.

Tests (the ones that would have caught this; none existed before, since every earlier test used distinct callsigns per port):

1. `NetRomRoutingTableTests`: the same callsign ingested on `"vhf"` (191) and `"hf"` (150) keeps two neighbour rows with their own `PathQuality`; the better port wins route selection; `MarkNeighbourDown` on one key leaves the other's routes; `MarkPortDown` drops one port's rows only; `BuildAdvertisement` still emits one entry per destination at the better quality; obsolescence decays per route; the `PortRank` tie-break; one station on three ports filling all three slots.
2. `Inp3IngestTests`: RIF ingest on two ports from one callsign keeps two time-routes; withdrawal on one leaves the other (and does not fire invariant (W)).
3. `NetRomRoutingRestoreTests` / `SqliteNetRomRoutingStoreTests`: round-trip two rows for one callsign (the UNIQUE-violation test), and a genuine v1 file recreated at v2.
4. `NetRomL3L4IntegrationTests`: one remote audible on both of a bridge node's ports - two adjacencies with their own qualities, the better port carries the circuit, and `DetachPortAsync` on that port fails the next circuit over to the surviving band.
5. `NetRomInterlinkEgressTests`: the interlink leaves on the port named in the key (a port that is neither first by alphabet nor first by config order); a key naming an unattached port fails rather than dialling another band; one station on two ports gets two interlinks.
6. Interop: the existing `NetRomNodesIngestViaAxudp` / `NetRomL4CircuitViaAxudp` LinBPQ fixtures with a second port mapped to the same BPQ instance, asserting pdn keeps two rows and dials the better one. Cheap because BPQ's `ROUTES` output already proves the reference behaviour.

## 10. Where per-port keying is wrong or unnecessary

1. **L4 circuits** (§5): keying by port would break a circuit on the failover this change enables. `CircuitManager` is untouched.
2. **Destinations**: a destination is a node, not a link. `destinations` stays keyed by `Callsign`, and `ResolveDestination` (alias or callsign) does not change.
3. **The wire**: no port belongs in a NODES entry or a RIF. Resist any temptation to widen the advert.
4. **`NeighbourCallsigns`**: port-blind on purpose; left alone.
5. **The INP3 engine**: keying it per (port, callsign) is *right in principle* (SNTT is a link property, and BPQ stores it on the ROUTE) but is deferred behind the selected-link rule of §4, because it ripples into three stacks for a default-off feature. [#733](https://github.com/packet-net/packet.net/issues/733).
6. **The route cap** (§9): BPQ's own semantics let one neighbour occupy all three slots. Fidelity to the reference was chosen over a diversity heuristic.
7. **Per-port advertisement** (`TXMINQUAL`, `QUAL_ADJUST`): BPQ's NODES *content* differs per port; pdn's is identical on every port and only the framing is per-port. Per-port neighbour keying neither requires nor delivers that. It is a separate feature: [#732](https://github.com/packet-net/packet.net/issues/732).
