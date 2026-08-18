# NET/ROM INP3 - slice I-4 design: triggered updates + poison-reverse

*Locked design for INP3 slice I-4 (plan §9, realising plan §6.2 + the emission half of §6.3). I-4 is the **emission** side of INP3 routing: it turns the route model + ingestion that I-3 landed into outgoing RIFs. Three pieces, each host-free and `TimeProvider`-driven: (1) **RIF content** - which routes we advertise, per target neighbour; (2) **poison-reverse** - the per-(destination, target-neighbour) loop-avoidance rule that decides the metric a route is advertised back at; (3) the **triggered-update timing policy** - a dirty-tracking + debounce/priority state machine that decides *when* to emit toward which neighbour, with negative info immediate-and-prioritised, positive info batched, and a periodic full RIF regardless.*

*Builds on: I-1 (`Wire/Inp3Rif.cs` - `Inp3Rif`/`Inp3Rip`/`Inp3Tlv` encode types, `Inp3Rip.HorizonMs = 600_000`, the `emitAliasTlv`-gated alias TLV); I-2 (`Transport/Inp3Engine.cs` - the host-free, snapshot-then-act-under-lock, intent-sink engine pattern this slice mirrors; `Inp3Sntt`); I-3 (`Routing/NetRomRoutingTable.cs` - the dual-metric route model, `IngestRif`, `Inp3RouteMetric`; `Routing/Inp3RouteSelector.cs` - the selection policy that decides each destination's **selected** route).*

**Status of this doc:** design lock only. No code is written as part of this doc (unlike I-3, which wrote the model). The emitter (`Routing/NetRomRoutingTable.BuildRif(...)`), the timing state machine (`Transport/Inp3UpdateScheduler.cs`), and the new `NetRomInp3Options` knobs (`RifInterval`, `PositiveDebounce`) are **specified** here and implemented in the I-4 build PR. Everything below is `TimeProvider`-driven and host-free per plan §3.3/§3.4: the table/scheduler produce **RIF bytes** and **"advertise to neighbour X now" intents**; the host turns an intent into `BuildRif(towardNeighbour)` + a send over the interlink.

---

## 0. TL;DR

- **RIF content (per target neighbour N):** advertise, as one RIP each, every destination D for which we hold a *selected* INP3 time-route - carrying our **local** target time + hop count for D - **plus our own node at target-time 0 / hop 0** (we are the source). Alias TLV emission stays **gated off** (`emitAliasTlv = false`, AMBIGUITY-RIF-2); we emit the bare prefix RIP (callsign + hop + target-time + EOP).
- **Poison-reverse (loop avoidance, the correctness feature):** when building the RIF *toward* N, any destination D whose currently-**selected** route is **via N** is advertised back to N **at the horizon** (`HorizonMs = 600_000`, the withdrawal/unreachable metric) - never at its finite local time. Destinations reached via *other* neighbours carry their real local target time. Invariant: **a node never advertises a finite metric for a destination back toward that destination's own next hop.** (Alternate-reverse is the spec's alternative; I-4 locks **poison-reverse**.)
- **Triggered-update timing (the state machine):** **negative** info (a route lost, or its selected target time **worsened past a threshold**) is marked dirty and emitted **immediately + prioritised**; **positive** info (a new/better route) is marked dirty and emitted after a short **debounce** (batched); a **periodic full RIF** fires on the `RifInterval` regardless. All three are `TimeProvider`-driven; the scheduler emits *intents* ("advertise to neighbour X now"), never bytes or I/O.
- **Storm/loop invariants, property-tested:** poison-reverse holds for every (dest, neighbour); target time is monotonic-nondecreasing per advertised hop; a withdrawn route stays withdrawn without a fresh ingest; the emitter never advertises a finite metric back to a route's own next hop.

---

## 1. RIF content - what we advertise

### 1.1 The set of destinations advertised

I-4 advertises the **INP3 (time-route) view** of the table - the time-space analogue of `BuildAdvertisement(int obsoleteMinimum)`, which builds the **quality-space** NODES view. The two are independent and ride different carriers (NODES = UI to `NODES`; RIF = a connected interlink I-frame, PID 0xCF). Building a RIF never disturbs NODES origination and vice-versa.

For a RIF built **toward a specific neighbour N** (every RIF is per-target-neighbour - see §1.4), we advertise exactly:

1. **Our own node** - one RIP for `myCall`, **target-time 0 ms, hop count 0**, no TLVs. We are the source of ourselves: the cost to reach us *from us* is zero, in zero hops. A neighbour ingesting this RIP learns `localTargetTimeMs = 0 + theirSnttToUs + 10` (their measured link cost to us + the per-hop floor) at hop 1 - which is exactly the cost of reaching us directly over their link. This is the seed every other node's path-time to us is built from. Our own node is **always** advertised (it is never poisoned and never gated by selection - it is not a learned route, it is the source identity).

2. **Every destination D that holds a *selected* INP3 time-route** - i.e. the destinations for which `Inp3RouteSelector.SelectActiveRoute(dest, preferInp3Routes: true)` returns a route whose `Inp3` metric is non-null. One RIP per such D, carrying:
   - **destination callsign** = D, in the AX.25-shifted form (the existing `NetRomCallsign.WriteShifted` codec - one source of truth, exactly as the NODES builder uses).
   - **hop count** = D's selected route's `Inp3.HopCount` (the local hop count I-3 learned: `peer.HopCount + 1`).
   - **target time** = D's selected route's `Inp3.TargetTimeMs`, **quantised to the 10 ms wire granule on emission only** (the stored metric is full-ms; the granule is a codec concern - design I-3 AMBIGUITY-I3-3) - **unless poison-reverse overrides it to the horizon** (§2).
   - **no alias TLV** (gated off, §1.3).

   "Selected" (not "best per neighbour", not "every route") is the locked rule: we advertise the route the node is **actually using** for D, mirroring how `BuildAdvertisement` advertises only each destination's **best** route, not every kept route. This keeps a RIF to one RIP per reachable destination and makes poison-reverse a clean per-destination decision (there is exactly one selected next-hop per destination to compare against N). A destination with no INP3 route (quality-only) is **not** in the RIF - it is reachable, but only in the quality/NODES space, which NODES already advertises.

### 1.2 Why "our own node at 0/0" and not "every directly-heard neighbour"

INP3 RIPs describe **reachability to a destination**, not link facts. The only zero-cost, zero-hop destination is ourselves. A directly-heard neighbour M is a *destination* in its own right only if we hold a selected time-route to M (we usually do - I-3's ingestion learns a route to the RIF source); it is then advertised under rule (2) at its real measured time, **subject to poison-reverse** (if M's selected route is *via* M itself - the direct case - and we are building the RIF toward M, poison-reverse fires; see §2.4). We do **not** synthesise extra "neighbour" RIPs: the link cost to a neighbour is something *that neighbour* measures (its own SNTT to us), not something we declare. Declaring it would double-count when the neighbour adds its own SNTT on ingest.

### 1.3 Alias TLV emission stays gated OFF (AMBIGUITY-RIF-2)

The alias TLV (`type 0x00`) collides byte-for-byte with the RIP's EOP (`0x00`); I-1 locked reading (a) (parser tolerates the collision positionally) but **gated alias *emission* off** behind `emitAliasTlv = false` until the reading is re-validated against a live INP3 peer / the PDF (I-1 wire-spec AMBIGUITY-RIF-2, the highest-risk I-1 decision). I-4 **honours that gate**: every RIP we build carries **zero TLVs** - the bare 10-byte prefix (7 callsign + 1 hop + 2 target-time) + the 1-byte EOP = 11 bytes per RIP. Concretely the emitter passes `Tlvs = []` to every `Inp3Rip`, so our *output* never depends on the ambiguous reading even though our *parser* tolerates it. (The destination's alias is still carried in the **NODES** space, which has an unambiguous fixed-6-byte alias field - no information is lost network-wide; the RIF simply omits it until the gate opens. When `emitAliasTlv` is later flipped on for I-5 interop work, the alias for D would be read from the table's `DestinationState.Alias` and appended as a single `Inp3Tlv.Alias(...)` TLV before the EOP; that is out of scope for I-4 and stays off.)

> **AMBIGUITY-I4-1 (flagged, locked OFF for I-4):** whether to emit the alias TLV at all. **Locked: no** - `emitAliasTlv` stays `false`; the RIF carries prefix-only RIPs. Re-evaluate in I-5 against a live peer, exactly as AMBIGUITY-RIF-2 prescribes. A `NetRomInp3Options.EmitAliasTlv` knob (default `false`) is the seam if/when it opens; I-4 does **not** add it (no consumer until the gate is re-validated) - flagged here so I-5 knows where it lands.

### 1.4 Every RIF is built *toward a specific neighbour*

`BuildRif` takes the target neighbour N as a parameter, because **poison-reverse content is N-specific** (§2): the same destination D is advertised at a finite time toward most neighbours but at the horizon toward the one neighbour D is routed through. There is therefore **no single "broadcast RIF"** - a periodic or triggered update fans out to N distinct RIFs, one per INP3-capable neighbour, each poison-reversed for that neighbour. This is identical in spirit to how the scheduler's intents are **per-neighbour** ("advertise to neighbour X now", §3).

### 1.5 The emitter signature (host-free, mirrors `BuildAdvertisement`)

On `NetRomRoutingTable`, a pure read under the table lock - it produces the parsed `Inp3Rif` (the host calls `.ToBytes()` and wraps it in a PID-0xCF I-frame on N's interlink session). It takes `myCall` and the `preferInp3Routes` knob as parameters (host-free: the table never reaches for options/identity - the same discipline as `IngestRif` taking `myCall`/`neighbourSnttMs`):

```csharp
// On NetRomRoutingTable. Host-free, pure read under the gate. Builds the per-neighbour,
// poison-reversed INP3 RIF to advertise TOWARD toTargetNeighbour. Mirrors
// BuildAdvertisement (the quality/NODES analogue). Alias TLVs gated off (AMBIGUITY-I4-1).
public Inp3Rif BuildRif(
    Callsign myCall,             // the source RIP (target-time 0 / hop 0) and the loop-guard identity
    Callsign toTargetNeighbour,  // N — the neighbour this RIF is built FOR (poison-reverse target)
    bool preferInp3Routes);      // resolve each dest's SELECTED route via Inp3RouteSelector
```

Construction is **strict** (CLAUDE.md "outbound construction path stays strict"): we only ever emit a RIP whose target time is in `[0, HorizonMs]` (a poisoned RIP is *exactly* `HorizonMs`, the encodable horizon value `0xEA60` units), our own node is exactly 0/0, and every learned RIP's stored finite metric is `< HorizonMs` by I-3's storage invariant - so `Inp3Rip.Write` never throws on emitter output.

> **AMBIGUITY-I4-4 (flagged, locked):** the RIP ordering inside a RIF. `BuildAdvertisement` orders quality-best-first then callsign. **Locked for RIF:** our own-node RIP **first** (the source seed), then the destination RIPs ordered by **ascending local target time, then destination callsign (ordinal)** - the time-space mirror of quality-best-first, and a deterministic, cross-stack-identical order (the C#/TS/Rust ports must emit byte-identical RIFs given identical state, the FNV-1a/quality-formula discipline). Ordering is cosmetic to correctness (the ingesting peer is order-insensitive) but pinned for byte-parity.

---

## 2. Poison-reverse (the loop-avoidance correctness feature)

### 2.1 The problem it solves

Without poison-reverse, distance/time-vector routing forms two-hop loops: if we reach D **via N**, and we advertise D back **to N** at our finite local time, then N can conclude "I can reach D via *them* (us)" - but our path to D *goes through N*. N now routes D→us→N→us→… a black-hole loop, exactly the failure class the vanilla NET/ROM trivial-loop guard (advertise quality 0 if the best-neighbour is the recipient) addresses in the quality space. Poison-reverse is the INP3 time-space analogue, and like the trivial-loop guard it is a **correctness** feature, not an optimisation (plan §3.5, §6.2).

### 2.2 The locked per-(destination, target-neighbour) rule

When building the RIF to advertise **toward neighbour N**, for each destination D we would otherwise include (its selected INP3 route, §1.1 rule 2):

> **Let `via(D)` be the neighbour of D's currently-*selected* INP3 route (the route `Inp3RouteSelector.SelectActiveRoute` returns for D under `preferInp3Routes`).**
>
> - **If `via(D) == N`** → advertise D toward N **at the horizon** (`TargetTimeMs = HorizonMs`, hop count carried as the route's hop count - the time, not the hops, encodes unreachability). This is the **poison**: N learns "D is unreachable via them (us)," so N never installs a route to D through us, breaking the would-be loop.
> - **If `via(D) != N`** (D is reached via some *other* neighbour, or D is ourselves / a directly-reached destination not via N) → advertise D toward N at its **real local target time** (`Inp3.TargetTimeMs`, quantised to the granule).

Our **own node** (the 0/0 source RIP, §1.1 rule 1) is **exempt** - it is never a learned route, has no `via`, and is never poisoned: every neighbour must always learn a finite (zero) path to us.

### 2.3 The invariant it guarantees (the property-test target)

> **A node never advertises a finite metric for a destination D back toward D's own next hop.**

Formally, for every neighbour N and every destination D that appears in `BuildRif(myCall, N, preferInp3Routes)` with `D != myCall`: if D's selected route's neighbour is N, then D's advertised `TargetTimeMs == HorizonMs` (and `IsHorizon` is true). Equivalently: `via(D) == N ⟹ advertised(D, N) == HorizonMs`. There is no input (no table state, no SNTT, no option) under which a route is advertised at a finite time back to the very neighbour it is routed through. This is invariant **(P)** of §4.

### 2.4 The direct-route corner

If D is a **directly-reached** destination whose selected route is *via D itself* (the common case: we learned D from D's own RIF, so `via(D) == D`), then building the RIF **toward D** (N == D) triggers poison-reverse: we advertise D back to D at the horizon. This is correct and harmless - D does not need us to tell it how to reach itself, and poisoning it prevents D from ever routing to-itself through us. Building the RIF toward any **other** neighbour M (N == M ≠ D) advertises D at its real time, because `via(D) == D ≠ M`. So a directly-heard neighbour is poisoned **only in the RIF sent to itself**, and advertised normally to everyone else - exactly right.

### 2.5 Alternate-reverse - the spec's alternative, NOT taken for I-4

The INP3 spec permits **alternate-reverse** instead: rather than poisoning D toward N, advertise toward N the *next-best* (alternate) route's metric for D - i.e. the best time to D that does **not** go via N. This is less pessimistic (N can use us as a backup to D if its own path dies) but is **strictly more complex and more storm-prone**: it requires the emitter to compute, per (D, N), the best D-route excluding N, and a flap on the alternate re-triggers updates. **I-4 locks poison-reverse** (the simpler, provably-loop-free choice) and treats alternate-reverse as a future, flag-gated option if fleet/interop testing shows poison-reverse loses useful backup paths.

> **AMBIGUITY-I4-2 (flagged, locked):** poison-reverse vs alternate-reverse. Plan §6.2 + plan risk #1 name both. **Locked: poison-reverse for I-4** (advertise the horizon toward the next hop). Rationale: it is the minimal change that makes invariant (P) hold by construction, it has no per-(D,N) alternate-route computation, and it cannot itself generate a flap (the horizon is a constant, not a measured value). Alternate-reverse is a future `NetRomInp3Options.ReverseStrategy` enum (`PoisonReverse` default | `AlternateReverse`) if I-5 fleet testing shows a real lost-backup-path cost; I-4 does **not** add the knob (no second strategy to switch to yet). Audit-doc row when/if the second strategy lands.

---

## 3. Triggered-update timing policy (the `TimeProvider`-driven state machine)

### 3.1 What it is and where it lives

The timing policy answers **"when do we emit a RIF, and toward whom?"** It is a host-free state machine, `Inp3UpdateScheduler`, living in `Transport/` beside `Inp3Engine` and mirroring that engine's established pattern exactly:

- **Host-free + intent-emitting.** It owns no I/O, no routing table, no AX.25 session. It consumes **dirty signals** (the table/ingestion tells it "destination D changed how"), consumes **`Tick(now)`** (or self-drives off an injected `TimeProvider` timer, like `Inp3Engine`'s `tickInterval`), and emits **`AdvertiseToNeighbour` intents** ("advertise to neighbour X now"). The host turns each intent into `table.BuildRif(myCall, X, preferInp3Routes)` + a send over X's interlink. The scheduler never builds a RIF, never touches bytes - it only decides *who* to advertise to and *when*. (This is the §6.2 "small state machine driven by the injected clock" made concrete, and the plan §3.4 sans-io shape: drainable intents, fed events, `tick(now)`.)
- **`TimeProvider`-monotonic.** Like `Inp3Engine`, it times debounce/interval off the **monotonic** source (`GetTimestamp`/`GetElapsedTime`), never wall-clock - an NTP/DST step can never fire or suppress a debounce. Deterministic under `FakeTimeProvider`: advance the clock, call `Tick`, assert the intents drained.

The split (table emits *content*, scheduler emits *timing*) keeps each piece pure: `BuildRif` is a pure read of table state; the scheduler is a pure function of (dirty signals, clock) → intents. The host is the only stateful glue.

### 3.2 The dirty-tracking model

The scheduler holds, per **destination**, a dirty classification - *not* per neighbour, because a single destination change must fan out to **all** INP3-capable neighbours (each gets its own poison-reversed RIF at emit time). The set of target neighbours is supplied by the host (the INP3-capable interlink set, e.g. from `Inp3Engine.Neighbours` filtered to `Inp3Capable`) - the scheduler is told the neighbour set, it does not discover it (host-free).

Each dirty destination carries a **priority class**, set by whoever marks it dirty (the table's `IngestRif` / withdrawal path, surfaced to the host, which calls the scheduler):

| Signal (what changed for destination D) | Class | Source |
|---|---|---|
| D's selected INP3 route **lost** (withdrawn at horizon; `MarkNeighbourDown` dropped it; aged out of the table) | **NEGATIVE** | I-3 withdrawal / failover path |
| D's selected target time **worsened by ≥ `WorsenThreshold`** (got slower past the threshold) | **NEGATIVE** | re-ingest comparing old vs new selected time |
| D's selected target time **worsened by < `WorsenThreshold`** (a small slowdown) | **POSITIVE** | re-ingest (treated as routine churn, batched) |
| D is **new** (first INP3 route learned), or its selected time **improved** (got faster), or its selected next-hop changed to a faster route | **POSITIVE** | re-ingest |

The classification is **monotonic within a debounce window**: if D is marked POSITIVE and then NEGATIVE before it drains, it is **upgraded to NEGATIVE** (negative dominates - a loss must not be held back by a coincident positive). Re-marking NEGATIVE→POSITIVE does **not** downgrade. A destination is in at most one class at a time.

> **AMBIGUITY-I4-3 (flagged, locked):** the worsen-past-a-threshold boundary. Plan §6.2 says negative info is "a route lost or got slower"; the spec does not pin "how much slower = negative." A pure "any worsening = immediate" would let measurement jitter (the SNTT smoother twitching a few ms) trigger immediate fan-outs - a storm vector. **Locked:** a worsening is NEGATIVE (immediate) only if the selected target time increased by at least `WorsenThreshold`; smaller worsenings are POSITIVE (batched with the periodic/debounce path). **Default `WorsenThreshold = 1000 ms`** (1 s of added path time is materially worse; sub-second jitter is routine). It is a `NetRomInp3Options` knob (§3.5) so it is tunable and cross-stack-pinned. A **loss/withdrawal is always NEGATIVE regardless of threshold** (it is not a "worsening", it is a removal - invariant: a withdrawal always propagates immediately, never batched).

### 3.3 The emit decision (per `Tick(now)`)

On each `Tick(now)` (or a self-driven timer fire), the scheduler computes the set of (neighbour, reason) intents to drain, in this precedence:

1. **NEGATIVE dirty present → immediate, prioritised.** If any destination is dirty-NEGATIVE, emit an `AdvertiseToNeighbour` intent for **every** INP3-capable neighbour **now** (a NEGATIVE change is poison/withdrawal - it must reach every neighbour at once; there is no per-destination RIF, the host rebuilds the full poison-reversed RIF per neighbour, which naturally includes the changed destination's new state, withdrawn-or-worse). NEGATIVE intents are emitted **ahead of** any pending POSITIVE batch (priority), and clear **all** dirty flags (the rebuilt full RIF subsumes pending positives too - a single fan-out carries everything current). NEGATIVE has **no debounce**: `now` ≥ the mark time suffices.

2. **POSITIVE dirty present and debounce elapsed → batched.** Else, if any destination is dirty-POSITIVE and `now - earliestPositiveMarkMs ≥ PositiveDebounce`, emit an intent for every INP3-capable neighbour now and clear the POSITIVE dirty flags. The debounce coalesces a burst of positive changes (e.g. a NODES sweep learning ten new time-routes) into **one** fan-out instead of ten. The debounce timer is **anchored at the earliest still-pending POSITIVE mark** (so a steady drip of positives still drains within one `PositiveDebounce` of the first, not perpetually deferred).

3. **Periodic interval elapsed → full RIF regardless.** Independently of dirty state, if `now - lastPeriodicEmitMs ≥ RifInterval`, emit an intent for every INP3-capable neighbour and stamp `lastPeriodicEmitMs = now`. This is the **baseline refresh** (plan §6.2 "a periodic full RIF on the INP3 interval regardless"): it re-asserts every route so a peer that missed a triggered update (lost I-frame, just-(re)connected interlink) converges within one interval, and it re-poisons (a peer that somehow installed a stale route to D-via-us is re-told the horizon every interval). A periodic emit **also clears all dirty flags** (the full RIF subsumes any pending triggered update) and resets the debounce.

Because (1) and (3) both rebuild and fan out the **entire** poison-reversed RIF per neighbour, a single emit per neighbour carries the complete current state - the scheduler never needs to emit "just the changed RIP" (it tracks *which destinations* are dirty only to decide **whether/when** to fan out and at **what priority**, not to build partial RIFs). This is simpler than partial-RIF deltas and is safe: a full RIF is idempotent at the ingesting peer (re-asserting the same metrics is a no-op; the only cost is bytes, bounded by `RifInterval` + the debounce coalescing).

> **Partial vs full RIF - locked: full.** The spec allows a triggered RIF carrying *only* the changed RIP(s). I-4 **locks full-RIF emission** (every triggered/periodic fan-out rebuilds the complete poison-reversed RIF). Rationale: (a) it makes withdrawal trivially correct (a withdrawn D simply *isn't in* the rebuilt RIF, or appears poisoned - the peer ages it out / re-poisons; no special "withdraw this one" RIP plumbing - see §3.4); (b) it makes poison-reverse trivially correct (the whole RIF is poison-reversed for N every time, no risk of a stale un-poisoned partial); (c) RIF size is bounded by the table's destination count and coalesced by the debounce. Partial/delta RIFs are a future optimisation if fleet testing shows RIF size is a problem on a slow interlink; not needed for I-4.

### 3.4 How withdrawal propagates (and stays withdrawn)

A withdrawal (D's selected INP3 route lost) marks D **NEGATIVE**, triggering an immediate fan-out (§3.3 rule 1). In the rebuilt RIF toward each neighbour, D is now in one of two states:

- **D still has *another* selected INP3 route** (a backup neighbour) → D appears at the new (worse, via-the-backup) target time, poison-reversed for the new next hop. The peer updates to the backup metric.
- **D has *no* selected INP3 route left** (fully withdrawn) → D **is absent** from the rebuilt RIF entirely. The ingesting peer does **not** see a RIP for D. **This is where the "stays withdrawn without a fresh ingest" invariant is enforced at the *ingesting* node, not the emitter:** the peer's own table ages D's now-stale time-route out via obsolescence sweep (it stops being refreshed), OR - to make withdrawal *immediate* rather than sweep-paced - the emitter **explicitly poisons** the just-withdrawn D in the **next** RIF.

> **Explicit-withdrawal-RIP - locked: poison the just-withdrawn destination once.** A fully-withdrawn D simply vanishing from the RIF relies on the peer's sweep to forget it (slow). To make withdrawal propagate at the same speed as the rest of INP3, the scheduler keeps a small **recently-withdrawn set**: a destination that transitioned to "no selected INP3 route" is held in this set and **explicitly emitted at the horizon** (a poison/withdrawal RIP, `TargetTimeMs = HorizonMs`) in the **immediate NEGATIVE fan-out** that the withdrawal triggered, then **dropped from the set** (emitted once - a withdrawal RIP need not be repeated; the peer withdraws on receipt, and the periodic RIF's *absence* of D thereafter keeps it gone). This is the emit-side mirror of I-3's ingest-side "a RIP at the horizon withdraws the route." `BuildRif` therefore takes the recently-withdrawn set into account (it is supplied by the scheduler-driven host call, or `BuildRif` reads a table-held withdrawn set - see §3.6). **Invariant (W):** once withdrawn, D is emitted at the horizon exactly once and then never re-advertised at a finite metric **until a fresh `IngestRif` re-learns a time-route to D** - there is no path in the emitter that resurrects a withdrawn route from nothing.

### 3.5 New `NetRomInp3Options` knobs

I-4 adds two timing knobs to `NetRomInp3Options` (the §6.2 / plan §8 "add the timing knobs if needed" - `rifIntervalSeconds` is already named in plan §8's config surface but is not yet a field; `positive-debounce` is new). Defaulted-init-only properties on the record are source-compatible (the I-3 precedent: adding `Enabled`/`PreferInp3Routes`/`HopLimit` broke no caller):

```csharp
/// Periodic full-RIF cadence — the baseline refresh interval (plan §8 rifIntervalSeconds,
/// §6.2 "a periodic full RIF on the INP3 interval regardless"). Triggered updates fire
/// regardless of this. Default 300 s. Separate from NODESINTERVAL and from L3RttInterval.
public TimeSpan RifInterval { get; init; } = TimeSpan.FromSeconds(300);

/// Positive-update debounce — how long a NEW/BETTER (positive) route change is batched
/// before a fan-out, coalescing a burst of positive changes into one RIF (§3.3 rule 2).
/// NEGATIVE changes (loss / worsen-past-threshold) ignore this and fan out immediately.
/// Default 5 s. Must be < RifInterval (a debounce ≥ the periodic interval is pointless —
/// the periodic emit would always drain the batch first).
public TimeSpan PositiveDebounce { get; init; } = TimeSpan.FromSeconds(5);

/// The worsen-by amount (ms) at/above which a slowed selected route counts as NEGATIVE
/// (immediate) rather than POSITIVE (batched) — AMBIGUITY-I4-3. Sub-threshold worsenings
/// are routine SNTT jitter and batched. A loss/withdrawal is always NEGATIVE regardless.
/// Default 1000 ms.
public int WorsenThresholdMs { get; init; } = 1000;
```

`Validate()` gains: `RifInterval > 0`; `PositiveDebounce > 0` **and** `PositiveDebounce < RifInterval`; `WorsenThresholdMs >= 0`. (Mirrors the existing `L3RttResetWindow > L3RttInterval` guard discipline.)

### 3.6 The scheduler surface (mirrors `Inp3Engine`)

```csharp
// Transport/Inp3UpdateScheduler.cs — host-free, TimeProvider-monotonic, intent-emitting.
public sealed class Inp3UpdateScheduler : IDisposable
{
    public Inp3UpdateScheduler(NetRomInp3Options? options = null,
                               TimeProvider? time = null,
                               TimeSpan? tickInterval = null);   // self-drive or manual Tick (Inp3Engine pattern)

    /// The set of INP3-capable neighbours to fan out to. Host-supplied (e.g. from
    /// Inp3Engine.Neighbours where Inp3Capable); the scheduler never discovers neighbours.
    public void SetTargetNeighbours(IReadOnlyCollection<Callsign> capableNeighbours);

    /// Mark a destination dirty with a class (the table/ingestion path computes the class
    /// per §3.2 — POSITIVE / NEGATIVE; a withdrawal also enters the recently-withdrawn set).
    public void MarkDirty(Callsign destination, Inp3UpdateClass cls);
    public void MarkWithdrawn(Callsign destination);   // → NEGATIVE + recently-withdrawn set

    /// Advance the clock-driven state machine; returns/raises the intents to act on now
    /// (§3.3). Snapshot-then-act under the gate, intents invoked after release — the
    /// Inp3Engine.Tick discipline (a re-entrant host handler cannot deadlock).
    public void Tick();

    /// The intent sink the host wires: "(re)build BuildRif(myCall, neighbour, prefer) and
    /// send it over neighbour's interlink now." Carries the reason for observability.
    public Action<Inp3AdvertiseIntent>? Advertise { get; set; }
}

public enum Inp3UpdateClass { Positive, Negative }
public enum Inp3AdvertiseReason { Triggered, Periodic }
public readonly record struct Inp3AdvertiseIntent(Callsign Neighbour, Inp3AdvertiseReason Reason);
```

The recently-withdrawn set is held by the **scheduler** and passed to the host's `BuildRif` call (the host calls `BuildRif(myCall, N, prefer)` and, for the withdrawn destinations the intent carried, the host/table appends horizon RIPs) - **or**, the cleaner locked option: the **table** holds the recently-withdrawn set (populated by `IngestRif`/`MarkNeighbourDown`/`Sweep` when an INP3 route fully leaves) and `BuildRif` consumes-and-clears it, emitting one horizon RIP per just-withdrawn destination per neighbour. **Locked: the table holds the withdrawn set** (it is table state - the table is where a route leaves; the scheduler should not duplicate the table's knowledge of which routes exist). `BuildRif` then needs no extra parameter; it reads + drains the table's per-emit withdrawn set. The scheduler only decides *when/to-whom* to call `BuildRif`.

> **AMBIGUITY-I4-5 (flagged, locked):** where the recently-withdrawn set lives. **Locked: the routing table** (it is table state; `BuildRif` consumes-and-clears it). The scheduler tracks dirty/priority/timing only. This keeps `BuildRif` parameter-stable (no withdrawn-set parameter) and avoids the table and scheduler disagreeing about what exists.

---

## 4. Storm-safety / loop invariants to property-test

The locked property assertions for the I-4 tests (plan §10 "the loop/storm-safety net"; these are the *correctness* bar, mirroring the I-3 degeneracy proofs). Each must hold for **arbitrary** table state, SNTT values, neighbour sets, and options - generated, not just example-based.

- **(P) Poison-reverse holds for every (dest, neighbour).** For every neighbour N and every RIF `BuildRif(myCall, N, prefer)`: every RIP whose destination D has `via(D) == N` (D's selected route is via N) is advertised at `TargetTimeMs == HorizonMs` (`IsHorizon` true); and `myCall` is always present at 0/0 and never poisoned. *Equivalently:* **the emitter never advertises a finite metric for a destination back toward that destination's own next hop.** This is the headline loop-freedom invariant.

- **(M) Target time is monotonic-nondecreasing per advertised hop.** Every finite RIP we emit for D carries `TargetTimeMs == quantise(route.Inp3.TargetTimeMs)` where, by I-3's storage rule, `route.Inp3.TargetTimeMs = peerTime + neighbourSntt + PerHopIncrementMs` ≥ `peerTime + 10`. So the time we advertise for D strictly exceeds the time the neighbour advertised to us for D (by ≥ the per-hop floor) - across the whole network, target time strictly increases along every path away from a source. No emitted finite metric is ever *less* than what we ingested for that destination minus the link cost; quantising to the 10 ms granule (floor or round - locked to match I-3 emission, AMBIGUITY-I3-3) never violates the strict per-hop increase because the +10 ms floor dominates the ≤5 ms quantisation slack. (This plus the hop limit bounds path length and forbids a finite-metric cycle.)

- **(W) A withdrawn route stays withdrawn without a fresh ingest.** After a destination D is fully withdrawn (no selected INP3 route): D is emitted at the horizon exactly once (the explicit withdrawal RIP, §3.4) and is thereafter **absent** from every RIF - there is **no** emitter path that re-advertises D at a finite metric **unless** a fresh `IngestRif` re-learns a time-route to D. Property: drive a withdrawal, then any sequence of `Tick`s / periodic emits / fan-outs with **no** intervening `IngestRif` → D never reappears at a finite metric.

- **(P′) The emitter never advertises a route at a finite metric back to its own next hop.** The operational restatement of (P), asserted directly on emitter output across generated states: `∀ N, ∀ RIP r ∈ BuildRif(myCall, N, prefer): (r.Destination != myCall ∧ selectedVia(r.Destination) == N) ⟹ r.TargetTimeMs == HorizonMs`.

- **(Storm) Bounded fan-out under churn.** A burst of K positive changes within one `PositiveDebounce` window produces **at most one** triggered fan-out per neighbour (coalescing); a steady positive drip drains within `PositiveDebounce` of the first pending mark (no perpetual deferral); a NEGATIVE change always drains on the **next** `Tick` (no debounce) and clears subsumed positives. Property (fake clock): the count of `Advertise` intents over a window is bounded by `neighbours × (ceil(window / RifInterval) + ceil(window / PositiveDebounce) + negativeCount)` - i.e. no unbounded emission from bounded input.

- **(Source) Our own node is always advertised, finite, never poisoned.** `∀ N: BuildRif(myCall, N, prefer)` contains exactly one RIP for `myCall` with `TargetTimeMs == 0, HopCount == 0` (independent of table state, SNTT, withdrawn set). A neighbour can therefore always learn a path to us.

- **(Degenerate-off) INP3 disabled ⇒ no RIF emission.** With `options.Enabled == false`, the host never drives the scheduler/`BuildRif` (the host-layer gate above the always-correct engine, mirroring I-3's selector gate) - zero INP3 frames on the wire, exactly today's behaviour. (Asserted at the host-wiring layer; the table's `BuildRif` is pure and would build a valid RIF if called, but nothing calls it when disabled.)

---

## 5. Ambiguities / decisions flagged (summary)

| Tag | Question | Locked decision |
|---|---|---|
| **AMBIGUITY-I4-1** | Emit the alias TLV in RIPs? | **No** - `emitAliasTlv` stays off (honours I-1 AMBIGUITY-RIF-2); prefix-only RIPs. Re-evaluate I-5; `EmitAliasTlv` knob is the future seam (not added in I-4). |
| **AMBIGUITY-I4-2** | Poison-reverse vs alternate-reverse? | **Poison-reverse** - advertise the horizon back toward the next hop (provably loop-free, no per-(D,N) alternate computation, cannot flap). Alternate-reverse is a future `ReverseStrategy` enum if fleet testing shows lost-backup cost (not added in I-4). |
| **AMBIGUITY-I4-3** | When is a worsening NEGATIVE (immediate) vs POSITIVE (batched)? | Worsened by ≥ `WorsenThresholdMs` (default 1000 ms) = NEGATIVE; smaller = POSITIVE (SNTT jitter). A **loss/withdrawal is always NEGATIVE** regardless of threshold. |
| **AMBIGUITY-I4-4** | RIP ordering inside a RIF? | Own-node RIP first, then destinations by ascending target time, then callsign (ordinal) - deterministic + cross-stack byte-identical. Cosmetic to correctness; pinned for parity. |
| **AMBIGUITY-I4-5** | Where does the recently-withdrawn set live? | The **routing table** (it is table state; `BuildRif` consumes-and-clears it). Scheduler tracks only dirty/priority/timing. |
| **Partial vs full RIF** | Emit only changed RIPs, or the whole RIF? | **Full RIF** every fan-out - trivially-correct withdrawal + poison-reverse; size bounded + debounce-coalesced. Partial/delta RIFs a future optimisation. |

Open items explicitly deferred (named, per "don't silently narrow scope"):
- **Alias TLV on the wire** (AMBIGUITY-I4-1) - gated off until I-5 re-validates AMBIGUITY-RIF-2 against a live peer.
- **Alternate-reverse** (AMBIGUITY-I4-2) - poison-reverse only for I-4; the alternate strategy + its knob are I-5+ if needed.
- **Partial/delta RIFs** - full-RIF only for I-4; delta emission is an I-5+ optimisation if RIF size hurts on a slow interlink.
- **Host wiring** (scheduler ↔ `Inp3Engine` capable-neighbour set ↔ `BuildRif` ↔ interlink send ↔ `NetRomService`) is the I-4 **build-PR** scope (the host turns intents into sends); this doc locks the host-free table + scheduler contracts only.

---

## 6. Returned artefacts (for the caller)

### 6.1 RIF content rule (per target neighbour N)

```
BuildRif(myCall, N, preferInp3Routes) emits, in this order:
  1. own-node RIP:   destination = myCall, targetTime = 0, hop = 0, no TLVs   (always; never poisoned)
  2. for each destination D with a SELECTED INP3 route (Inp3RouteSelector under preferInp3Routes),
     ordered by ascending target time then callsign:
        via(D) = D's selected route's neighbour
        targetTime = (via(D) == N) ? HorizonMs                          // POISON-REVERSE
                                   : quantise10(D.selected.Inp3.TargetTimeMs)
        hop        = D.selected.Inp3.HopCount
        no TLVs    (emitAliasTlv = false, AMBIGUITY-I4-1)
  3. for each destination in the table's recently-withdrawn set (consumed+cleared):
        one horizon RIP (targetTime = HorizonMs) — the explicit one-shot withdrawal (§3.4)
Quality-only destinations (no INP3 route) are NOT in the RIF (NODES carries them).
```

### 6.2 Poison-reverse rule + invariant

```
RULE (per destination D, per target neighbour N):
  let via(D) = the neighbour of D's currently-SELECTED INP3 route.
  via(D) == N  -> advertise D toward N at HorizonMs (600_000 ms) = unreachable   (POISON)
  via(D) != N  -> advertise D toward N at its real local target time
  D == myCall  -> exempt: always 0/0, never poisoned.

INVARIANT (P): a node never advertises a finite metric for a destination back toward
that destination's own next hop.  ∀N,∀D≠myCall:  via(D)==N ⟹ advertised(D,N)==HorizonMs.

ALTERNATE-REVERSE (spec's alternative) = advertise the best D-route NOT via N instead of
the horizon. NOT taken for I-4 (more complex, can flap). Poison-reverse locked.
```

### 6.3 Triggered-update timing policy

```
DIRTY CLASSES (per destination):
  NEGATIVE = route LOST (withdrawal / MarkNeighbourDown / aged out)
           | selected target time WORSENED by >= WorsenThresholdMs (default 1000)
  POSITIVE = new route | improved time | next-hop changed to faster | worsened < threshold
  upgrade-only within a window: POSITIVE→NEGATIVE upgrades; NEGATIVE→POSITIVE does NOT downgrade.

ON Tick(now)  [TimeProvider-monotonic, host-free, emits per-neighbour intents]:
  1. ANY NEGATIVE dirty            -> emit "advertise to neighbour X now" for EVERY capable X
                                      IMMEDIATELY + PRIORITISED; clear ALL dirty (full RIF subsumes).
  2. else POSITIVE dirty & now - earliestPositiveMark >= PositiveDebounce (default 5s)
                                   -> emit for every X (batched); clear POSITIVE dirty.
  3. independently: now - lastPeriodic >= RifInterval (default 300s)
                                   -> emit for every X (Periodic); clear ALL dirty; reset debounce.
Each emit = host calls table.BuildRif(myCall, X, prefer) (full, poison-reversed) + sends over X.
A withdrawal additionally enters the table's recently-withdrawn set -> one horizon RIP next RIF (§3.4).

NEW NetRomInp3Options knobs: RifInterval (300s), PositiveDebounce (5s), WorsenThresholdMs (1000).
Validate: RifInterval>0; 0<PositiveDebounce<RifInterval; WorsenThresholdMs>=0.
```

### 6.4 Invariants to property-test

```
(P)      poison-reverse: ∀N,∀D≠myCall: via(D)==N ⟹ advertised(D,N)==HorizonMs.
(P′)     emitter never advertises a finite metric back to a route's own next hop (op. restatement of P).
(M)      target time monotonic-nondecreasing per advertised hop (the +10ms floor dominates ≤5ms quantise slack).
(W)      a withdrawn route stays withdrawn: emitted at horizon once, then absent, never finite again w/o a fresh IngestRif.
(Storm)  bounded fan-out: a burst within PositiveDebounce coalesces to ≤1 triggered emit/neighbour; NEGATIVE drains next Tick.
(Source) own node always present, 0/0, never poisoned — every neighbour can always learn a path to us.
(Off)    Enabled==false ⇒ host never drives scheduler/BuildRif ⇒ zero INP3 frames (today's behaviour).
```
