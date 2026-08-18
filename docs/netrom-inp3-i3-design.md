# NET/ROM INP3 - slice I-3 design: route metric + selection

*Locked design for INP3 slice I-3 (plan §9). I-3 extends the routing **model** with a second metric space (measured target time, from RIF) alongside the existing NODES quality, defines the **host-free RIF-ingestion math** that learns time-routes into the table, and defines the pure **selection policy** that picks between the two metric spaces. It builds on I-1 (the RIF/RIP/horizon wire types, `Wire/Inp3Rif.cs`) and I-2 (the SNTT smoother + the L3RTT link-timing loop, `Routing/Inp3Sntt.cs` / `Transport/Inp3Engine.cs`). Plan sections this realises: §5.2 (routes - a second metric space), §5.3 (the 600 s horizon = withdrawal), §6.3 (RIF ingestion), and risk #4 (metric-space coexistence - the selection truth table).*

**Status of this doc:** the model extension (`Inp3RouteMetric` + the optional `NetRomRoute.Inp3` field) is **written** into `src/Packet.NetRom/Routing/NetRomRoutingModel.cs` as part of locking this design. The ingestion method and the selection policy are **specified** here and implemented in the I-3 build PR (not in this doc's scope, which is the model + the locked math/truth-table).

---

## 1. The dual-metric route model (written)

Today a route is the vanilla triple `(Neighbour, Quality:byte, Obsolescence:int)`. INP3 routes carry a *different* metric - measured target time + hop count - in a *different* space (lowest-time-best, not highest-quality-best). Rather than overload `Quality`, a route gains an **optional** INP3 metric, so one destination can hold **both** quality-routes (from NODES) and time-routes (from RIF) at once.

### 1.1 Shape (as written into `NetRomRoutingModel.cs`)

```csharp
/// The INP3 metric for a learned route — the second metric space (measured
/// target time, lowest = best), present only on a route learned from a RIF.
public sealed record Inp3RouteMetric(int TargetTimeMs, byte HopCount);

public sealed record NetRomRoute(
    Callsign Neighbour,
    byte Quality,
    int Obsolescence,
    Inp3RouteMetric? Inp3 = null);   // 4th, OPTIONAL, defaults null
```

- **`Inp3RouteMetric`** is a new tiny record: `int TargetTimeMs` (milliseconds; **strictly below** the 600 s horizon `Inp3Rip.HorizonMs` - a route at/over the horizon is withdrawn, never stored) and `byte HopCount` (the hop count to the destination along this route). `TargetTimeMs` is `int` (not `uint`) to match `Inp3Rip.TargetTimeMs` and to make arithmetic with the existing `int` obsolescence and the clamp constants uniform; values are always non-negative and `< 600_000`. Units are **plain milliseconds**, not the wire's 10 ms granules - the per-hop increment and the neighbour SNTT need not be multiples of 10, so we keep full ms precision locally and only the codec deals in granules.
- **`NetRomRoute.Inp3`** is a **4th positional/init parameter with a `= null` default**. `null` ⇒ "this route is not INP3-learned" - which is every route until INP3 is enabled. Best INP3 route = **lowest `TargetTimeMs`**, ties broken by lowest `HopCount` then neighbour callsign (the time-space mirror of quality-space's "highest quality, then callsign").

### 1.2 Why the default keeps every caller compiling unchanged (verified)

Adding a defaulted 4th positional param to a positional record is source-compatible: every existing `new NetRomRoute(n, q, o)` call binds the three given args and takes `Inp3 = null`. The three callers in the tree are unchanged and **verified to compile** after the edit:

| Caller | Site | Effect |
|---|---|---|
| `NetRomRoutingTable.Snapshot()` | `Routing/NetRomRoutingTable.cs:242` | `new NetRomRoute(r.Neighbour, r.Quality, r.Obsolescence)` → `Inp3 = null` (snapshot of quality-only state for now; I-3's ingestion will populate it via the table-internal route state, then project it here) |
| `SqliteNetRomRoutingStore.Load()` | `Node.Core/NetRom/SqliteNetRomRoutingStore.cs:151` | `new NetRomRoute(via, (byte)r.Quality, (int)r.Obsolescence)` → `Inp3 = null` |
| `NetRomForwardingTests` | `tests/Packet.NetRom.Tests/NetRomForwardingTests.cs:42` | `new NetRomRoute(r.neighbour, r.quality, 6)` → `Inp3 = null` |

The **SQLite round-trip is deliberately INP3-blind**: `Save` writes only `(callsign, quality, obsolescence)`; `Load` reads only those and constructs a route with `Inp3 = null`. This is correct, not a gap - INP3 routes (and their underlying link SNTTs) re-learn from RIF/L3RTT within one interval of restart, so persisting a stale measured time would be *worse* than re-learning. No store schema change, no migration. This is documented on the `NetRomRoute.Inp3` XML doc.

`build src/Packet.NetRom`, `build src/Packet.Node.Core`, and `build tests/Packet.NetRom.Tests` all succeed with 0 warnings / 0 errors after the edit.

---

## 2. RIF-ingestion math (host-free)

The table learns time-routes by ingesting RIFs. This is the time-space analogue of `Ingest(... NodesBroadcast ...)` for the quality space, and it mirrors `UpsertRoute`'s discipline (per-dest cap, best-first, drop-below-floor).

### 2.1 Signature - the table takes the SNTT as a parameter (no `Inp3Engine` dependency)

The routing table must **not** depend on `Transport/Inp3Engine` (the engine already depends on the wire + SNTT types; a back-dependency would cycle, and the table layer is deliberately I/O-free and host-free). So the **caller passes the neighbour's current SNTT in milliseconds** as a parameter. The host (`NetRomService`) reads `Inp3Engine`'s per-neighbour SNTT and hands it to the table - the same shape as `Ingest` taking `myCall`/`portId` rather than reaching for them.

```csharp
// On NetRomRoutingTable. Host-free: neighbourSnttMs is supplied by the caller
// (the node host reads it from Inp3Engine); the table never touches the engine.
public void IngestRif(
    Callsign fromNeighbour,     // the interlink neighbour the RIF arrived on (= the via)
    Callsign myCall,            // our node callsign (for the trivial-loop guard, as Ingest)
    uint neighbourSnttMs,       // the smoothed transport time to fromNeighbour (Inp3Sntt.Ms);
                                //   Inp3Sntt.Unset (uint.MaxValue) ⇒ no measurement yet
    Inp3Rif rif,                // the parsed RIF (I-1 wire type)
    NetRomInp3Options options); // for the (new) HopLimit knob — see §4 AMBIGUITY-I3-1
```

### 2.2 The per-RIP formula

For each `rip` in `rif.Rips`, the local INP3 metric for `rip.Destination` **via `fromNeighbour`** is:

```
localTargetTimeMs = rip.TargetTimeMs            // the peer's advertised target time
                  + neighbourSnttMs             // + the measured cost of THIS link (I-2 SNTT)
                  + PerHopIncrementMs           // + a fixed per-hop increment (10 ms)
localHopCount     = rip.HopCount + 1            // one more hop: through us

PerHopIncrementMs = 10                          // plan §1 item 2 / §6.3 ("10 ms per hop")
```

Rationale, term by term (plan §6.3): `rip.TargetTimeMs` is the summed transport time from the neighbour onward; `neighbourSnttMs` adds the measured cost of the link we just received the RIF over (this is exactly what I-2's SNTT measures and what makes INP3 RTT-based rather than config-based); the **+10 ms per hop** is the spec's fixed per-hop floor that keeps target time strictly monotonic-increasing per hop even when a link measures ~0 ms (a loopback or a same-host fleet), which is the property-test invariant "target time monotonic-nondecreasing per hop" relied on by loop-safety.

### 2.3 The horizon, clamp, and withdrawal (plan §5.3)

- **Withdrawal at/over the horizon.** If `rip.IsHorizon` (`rip.TargetTimeMs >= Inp3Rip.HorizonMs`, i.e. ≥ 600 000 ms) **OR** the computed `localTargetTimeMs >= Inp3Rip.HorizonMs`, this RIP is a **withdrawal**: drop the INP3 metric for `(destination via fromNeighbour)`. "Drop the INP3 metric" means clear `Inp3` on that route (set it `null`) - it does **not** drop the route's *quality* metric if one exists (the route stays as a pure quality-route). A route left with neither a usable quality (≤ MINQUAL / 0) nor an INP3 metric is removed; a destination left with no routes is removed. Withdrawal feeds the same failover path as `MarkNeighbourDown` (a now-dead time-route stops being selectable on the very next decision, not at the next sweep).
- **Clamp.** A computed `localTargetTimeMs` is only ever *stored* when it is `< HorizonMs` (otherwise it withdraws, above), so a stored `TargetTimeMs` is always finite and in `[0, 600_000)`. No upper clamp into a stored route is needed; the `neighbourSnttMs == Inp3Sntt.Unset` case is treated as "link cost unknown" → **do not learn a time-route yet** (skip the RIP's INP3 metric; the route may still exist as a quality-route). Adding `Unset` (`uint.MaxValue`) as a transport time would trivially exceed the horizon and (correctly, if accidentally) withdraw, but we skip explicitly so an un-probed link never *removes* a time-route it never learned.

### 2.4 Caps and the hop limit (mirror `UpsertRoute`)

- **Per-destination route cap.** The same `options.MaxRoutesPerDestination` (canonical 3) bounds the routes kept per destination - INP3 and quality routes share the per-destination route set (a route is one `NetRomRoute` that may carry one or both metrics; the cap counts routes, i.e. distinct next-hop neighbours, not metrics). When the cap is exceeded after an upsert, eviction keeps the best - but "best" is metric-space-dependent, so eviction keeps the union of (top-N by quality) and the active selection's preference is applied at *read* time; the simplest spec-faithful rule that mirrors `UpsertRoute` is: **evict by the same key the selection would order by** (see §4 AMBIGUITY-I3-2 - locked: evict lowest-quality first, exactly as today, so a node that never enables `preferInp3Routes` is byte-identical; an INP3 route with no quality sorts as quality 0 for eviction only).
- **Hop limit.** A RIP whose `localHopCount` exceeds the configured hop limit is **not learned** (the routing horizon in hops, plan §8 `hopLimit`, default 30). This is the hop-count analogue of the horizon and bounds path length independently of measured time. Because `NetRomInp3Options` does **not yet carry a hop-limit knob** (it currently holds only the I-2 link-timing knobs), I-3 adds `HopLimit` to it - see §4 AMBIGUITY-I3-1.

### 2.5 Best-INP3-route ordering

Within a destination, INP3 routes order by **lowest `TargetTimeMs`**, then lowest `HopCount`, then neighbour callsign (ordinal) for determinism. This is the time-space mirror of the quality-space `OrderByDescending(Quality).ThenBy(callsign)` already in `Snapshot()` / `BuildAdvertisement()`. The snapshot projects both orderings; the selection policy (§3) consumes whichever the active mode wants.

---

## 3. The selection policy - truth table (plan risk #4)

Selection is a **pure function** over a destination's routes + `NetRomInp3Options` (the same shape as `NetRomForwarding.Decide` being pure). It answers: *"which route(s) does this destination forward / connect over?"* The single source of truth is `preferInp3Routes` (BPQ's `PREFERINP3ROUTES`) gated by the overlay `enabled` switch.

> **Critical invariant: degenerate to today, byte-for-byte.** With INP3 **disabled** (the default), the selection is *exactly* today's code path - `NetRomForwarding.SelectBest`/`SelectWeighted` over `Routes` ordered by quality. A single-route destination, and any quality-only destination, returns the *same* neighbour it returns today. INP3 selection is reachable **only** when `enabled == true`, and even then `preferInp3Routes == false` (the default-on-enable) keeps quality primary.

### 3.1 Truth table

| `enabled` | `preferInp3Routes` | Selection behaviour | INP3 routes |
|---|---|---|---|
| **false** (default) | *(ignored)* | **Today's behaviour exactly.** Best quality (best-first quality order; `BestRoute` / per-flow weighted over quality). The `Inp3` field is never read. | Not ingested at all (RIF ingestion is gated off when the overlay is disabled) → no INP3 routes exist. |
| **true** | **true** | The **lowest-`TargetTimeMs` INP3 route** if the destination has *any* INP3 route; **else fall back to best quality**. (Per-flow load-balancing spreads across the eligible *time*-routes when ≥ 2 exist, mirroring the quality spread; the fallback path is identical to the disabled path.) | Ingested, kept, visible, and **preferred**. |
| **true** | **false** (default on enable) | **Quality wins** - selection is the quality path (identical result to disabled for a quality-bearing destination). INP3 routes are still **ingested, kept, and visible** in the snapshot (for monitoring + for I-4 re-advertisement) but do **not** influence forwarding/connect selection. | Ingested, kept, visible, but **not selected** (conservative default). |

### 3.2 The two subtle rows, made precise

- **`enabled=true, preferInp3Routes=true`, no INP3 route for this dest:** fall back to the **quality** path. This is *not* "drop the datagram" - a destination known only via NODES must still route. The fallback is the *same function* as the disabled path, so it degenerates byte-for-byte for that destination.
- **`enabled=true, preferInp3Routes=false`:** INP3 routes are kept and visible but **invisible to selection**. Concretely, the selector ignores `route.Inp3` entirely and runs the quality path - so a destination that has *both* a quality route and a (better-time) INP3 route still forwards over the **quality** route. This is the conservative default that makes "turn INP3 on" safe: you start ingesting + advertising time-routes without changing where traffic goes, then flip `preferInp3Routes` once you trust them.

### 3.3 Degeneracy proof sketch (the acceptance bar)

1. **Disabled ⇒ identical.** With `enabled=false`, ingestion never runs, so `route.Inp3` is `null` on every route, and the selector takes the quality path unchanged. Output ≡ today.
2. **Single-route ⇒ identical.** With one route, the quality path returns that route's neighbour; the time path (if reached) returns the same single neighbour. Identical regardless of mode.
3. **Quality-only dest ⇒ identical.** A destination with no INP3 routes: `preferInp3Routes=true` falls back to quality; `preferInp3Routes=false` uses quality. Both ≡ today.

These three are the locked unit/property assertions for the I-3 selection tests (plan §10 "the pure selection policy is exhaustively unit + property tested").

---

## 4. Ambiguities / decisions flagged

- **AMBIGUITY-I3-1 - `NetRomInp3Options` lacks `Enabled`, `PreferInp3Routes`, and `HopLimit`.** The record today (I-2) holds only link-timing knobs (`L3RttInterval`, `L3RttResetWindow`, `SnttGainShift`, `ProbeUnknownCapability`, `AdvertiseIpAccept`, `CapabilityTextWidth`). The plan §8 config surface names `enabled`, `preferInp3Routes`, and `hopLimit`, which I-3 needs. **Locked decision:** I-3 adds `bool Enabled = false`, `bool PreferInp3Routes = false`, and `int HopLimit = 30` to `NetRomInp3Options`, with `Validate()` rejecting `HopLimit < 1`. Defaults preserve "off = exactly today." (Adding defaulted init-only properties to the record is source-compatible - no existing caller breaks; this is an I-3 build-PR edit, *not* part of this doc's model-only write, which is scoped to `Inp3RouteMetric` + `NetRomRoute.Inp3`.)
- **AMBIGUITY-I3-2 - eviction key when the per-dest cap is exceeded and the two metric spaces disagree.** Quality-best and time-best can rank routes differently, so "evict the worst" is ambiguous. **Locked decision:** evict by **quality** (lowest-quality-first, an INP3-only route counting as quality 0 *for eviction ordering only*), identical to today's `UpsertRoute`. This guarantees a node that never enables `preferInp3Routes` evicts byte-identically to today; the cost - an INP3-only route can be evicted in favour of a higher-quality route even under `preferInp3Routes=true` - is acceptable because (a) the cap is generous (3) and a real INP3 destination usually also has a NODES quality route, and (b) it is conservative (never evicts a *quality* route a vanilla node would keep). Revisit in I-5 if fleet testing shows a starvation case; capture as a `NetRomInp3Options` knob then if so.
- **AMBIGUITY-I3-3 - per-hop increment placement vs the wire's 10 ms granule.** `Inp3Rip.TargetTimeMs` is always a multiple of 10 (decoded from 10 ms granules); the +10 ms per-hop increment is also 10. But `neighbourSnttMs` is full-precision ms (the SNTT smoother does not round to 10). **Locked decision:** compute and store `localTargetTimeMs` in full ms (not re-quantised to 10 ms) - quantisation to the granule happens only at *emission* (I-4, when we build an outgoing RIP), never in the stored route. This keeps the stored metric as precise as the measurement and avoids cumulative rounding drift across hops. The horizon comparison uses the full-ms value against `HorizonMs = 600_000`.
- **AMBIGUITY-I3-4 - RIF carrier vs the trivial-loop guard.** A RIF arrives on a *connected interlink* from a known neighbour (`fromNeighbour`), so `IngestRif` knows the via directly (unlike NODES, where the originator is the UI source). The trivial-loop guard still applies: if a RIP's destination *is* `myCall`, or the RIP would advertise a route back through the neighbour it came from at a finite metric (poison-reverse territory), it is not learned as a usable route. Full poison-reverse on *emission* is **I-4**; I-3's ingestion only needs the `destination == myCall` drop (the receive-side trivial-loop guard, mirroring `Ingest`'s `entry.BestNeighbour.Equals(myCall)` check) and the "via == fromNeighbour" identity (always true here, so no self-via can be learned).

---

## 5. Returned artefacts (for the caller)

### 5.1 Model shape (written into `NetRomRoutingModel.cs`)

```csharp
public sealed record Inp3RouteMetric(int TargetTimeMs, byte HopCount);

public sealed record NetRomRoute(
    Callsign Neighbour, byte Quality, int Obsolescence, Inp3RouteMetric? Inp3 = null);
```

### 5.2 Ingestion formula (host-free)

```
per RIP, route to rip.Destination via fromNeighbour:
  localTargetTimeMs = rip.TargetTimeMs + neighbourSnttMs + 10        // +10 ms per hop
  localHopCount     = rip.HopCount + 1

  if rip.IsHorizon || localTargetTimeMs >= 600_000:  WITHDRAW (clear Inp3 on that route)
  else if neighbourSnttMs == Inp3Sntt.Unset:         skip (link cost unknown — learn no time-route)
  else if localHopCount > options.HopLimit:           skip (hop horizon)
  else if rip.Destination == myCall:                  skip (trivial-loop guard)
  else: UPSERT Inp3 = (localTargetTimeMs, localHopCount) on the (dest via fromNeighbour) route
        respecting MaxRoutesPerDestination (evict lowest-quality first, INP3-only = quality 0)
  best INP3 route per dest = lowest TargetTimeMs, then lowest HopCount, then neighbour callsign
```

### 5.3 Selection truth table

```
enabled=false                       -> best quality (TODAY, byte-for-byte); Inp3 never read; no RIF ingested
enabled=true, preferInp3Routes=true -> lowest-TargetTimeMs INP3 route if any; else best quality
enabled=true, preferInp3Routes=false-> best quality (== disabled result for a quality dest);
                                       INP3 routes kept + visible but NOT selected
single-route / quality-only dest    -> degenerates to TODAY in all three rows
```
