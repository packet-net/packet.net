# NET/ROM INP3 — implementation plan

*A detailed, cross-stack plan for adding INP3 (the modern time-based NET/ROM routing overlay) to the packet.net ecosystem: `Packet.NetRom` (C#), `@packet-net/ax25` (TypeScript), and pico-node (Rust). INP3 is a **post-v1, opt-in, default-off overlay** on the vanilla NET/ROM L3+L4 stack that already ships at parity across all three runtimes. This plan is the Phase-9 INP3 increment §5.9 of [`docs/plan.md`](plan.md) defers; the grounding research is `netrom-research.md` §1.4 / §3.3.*

**Status:** planning only — nothing here is built yet. Sizing from the research: **+M** on top of the shipped vanilla stack.

---

## 0. TL;DR

- INP3 (Andreas Gal DB7KG / NORD><LINK, ~1997) replaces NET/ROM's static, periodic, *quality*-based routing with **measured-RTT, triggered, link-state-ish** routing — while staying PID-0xCF and drop-in compatible with vanilla NET/ROM.
- It is **three new mechanisms** bolted onto the existing routing table: (1) **L3RTT** link-time measurement, (2) **target-time** routes (Σ measured time along the path) selected by lowest time, and (3) **triggered RIF/RIP** routing updates with poison-reverse loop avoidance.
- **Default-off, named-flag overlay.** Vanilla quality-based NET/ROM stays the interoperable common denominator and the default; INP3 is `PREFERINP3ROUTES`-style opt-in. Don't gate any vanilla behaviour on it.
- **The interop risk is real and asymmetric.** XRouter calls its *own* INP3 experimental; BPQ's INP3 has historically been considered problematic (may have improved — unverified). So the **reference is the INP3 spec + our own clean implementation**, *not* any one peer. The big de-risk: because pdn / ax25-ts / pico-node share a codebase, **our own three-stack fleet is a first-class, deterministic interop target** — we validate INP3 end-to-end among ourselves (byte-identical codecs, like the FNV-1a flow hash) before/independent of the messy BPQ/XRouter interop.
- Build order mirrors every prior NET/ROM increment: **C# reference → ax25-ts → pico-node Rust**, named flags, property-tested loop/storm safety, then external interop.

---

## 1. What INP3 is, precisely

Vanilla NET/ROM advertises, per destination, a **quality** (0–255, a configured/derived number) that decays multiplicatively per hop; the best route is the highest quality; the table ages via obsolescence and a periodic (hourly) full NODES broadcast. INP3 changes three things:

1. **Link metric = measured round-trip time, not a config number.** Each interlink continuously measures its **neighbour transport time** by exchanging **L3RTT frames**; the smoothed value (SNTT) is the link's cost.
2. **Route metric = target time + hop count.** A route's cost is the **sum of SNTTs** along the path (range 10 ms – 599.99 s at 10 ms granularity; **600 s = the routing horizon = unreachable**). Best route = **lowest target time**. Each hop adds 10 ms to the time and 1 to the hop count; routing stops at a configured hop limit.
3. **Updates are triggered, not (only) periodic.** Routing information travels as **RIF/RIP** frames; **negative** changes (a route lost or got slower) propagate **immediately and with priority**, **positive** changes can be batched/delayed. Loops are avoided with **poison-reverse / alternate-reverse** rather than NET/ROM's trivial-loop-quality-0 guard alone.

It stays **PID 0xCF** and is "drop-in compatible": an INP3 node and a vanilla node can share a network (the INP3 node still speaks NODES to vanilla neighbours), but a mixed network is less stable/performant than a homogeneous one — which is exactly why INP3 is an opt-in overlay and why **our own homogeneous fleet matters as a clean validation target**.

---

## 2. Interop reality — the targets, ranked

Per §1.5 of the grounding research (`netrom-research.md`, not in this repo) and `docs/strict-vs-pragmatic-audit.md` discipline: **implementations are interop targets, not reference truth.** For INP3 specifically:

| Target | Status | How we treat it |
|---|---|---|
| **INP3 spec** (Gal, NORD><LINK PDF) | The closest thing to normative | The reference. Our codecs + algorithms are spec-faithful; deviations are named flags. |
| **Our own fleet** (pdn ↔ ax25-ts ↔ pico-node) | We control all three; byte-identical codecs | **First-class deterministic interop target.** Validate INP3 end-to-end here first (3-stack harnesses, fake clocks) — fully in our control, no messy peer. This is the headline de-risk. |
| **XRouter** (G8PZT) | INP3 marked **experimental** by its own author; warns timer tables fill with "unconnectable and incompatible with vanilla NetRom" nodes; RTT→quality "British notion of quality" | Best-effort interop; quirks flag-gated. Do **not** adopt its RTT↔quality maths as canonical. |
| **LinBPQ** (G8BPQ) | INP3 + `PREFERINP3ROUTES=1`; **historically considered problematic, may be better now — unverified** | Best-effort interop, validated empirically against a live BPQ on the lab; **do not take BPQ's INP3 behaviour as the reference** even though BPQ is the de-facto vanilla reference. Capture any divergence as a named `NetRomInp3Options` flag with a `strict-vs-pragmatic-audit.md` row, exactly as we do for AX.25/BPQ quirks. |

**Consequence for the plan:** the acceptance bar for "INP3 works" is *our-fleet end-to-end green* + *spec-faithful codecs*. BPQ/XRouter interop is a **separate, best-effort tier** that can surface flag-gated quirks without blocking the feature — because their INP3 is itself shaky, a red there is a "characterise + flag", not a "we're wrong".

---

## 3. Design principles (carried from AX.25 + vanilla NET/ROM)

1. **Spec-faithful core; pragmatism is a named flag.** A new `NetRomInp3Options` record (mirroring `NetRomParseOptions` / `NetRomRoutingOptions` / `NetRomCircuitOptions`) holds every divergence knob; defaults interoperate; each flag gets a `strict-vs-pragmatic-audit.md` row. Hand-written, **not** via ax25sdl (INP3 has no SDL figures, same as the rest of NET/ROM).
2. **Default-off overlay.** A node with INP3 disabled behaves exactly as today. Enabling it adds L3RTT probing + RIF processing + a target-time route space alongside the quality space; a `preferInp3Routes` knob (BPQ's `PREFERINP3ROUTES`) decides which space wins when both have a route.
3. **`TimeProvider`/injected-clock throughout** (no wall-clock) — INP3 is *time*-centric, so deterministic time is doubly important. RTT, SNTT smoothing, the 180 s reset, triggered-update debounce, target-time arithmetic all run off the injected clock (C# `TimeProvider`, Rust `u64` monotonic tick, TS injected `now()`), so the whole overlay is fake-clock testable.
4. **Sans-io in Rust** (the established pico-node pattern): the INP3 engine owns no I/O and no timers — it emits drainable intents (`take_l3rtt_sends`, `take_rif_sends`, `take_interlink_resets`) and consumes `on_l3rtt(from, payload, now)`, `on_rif(from, bytes, now)`, `tick(now)`. The host wires it to the AX.25 interlink sessions. C#/TS keep their event/`Action`-sink seams as the vanilla layer does.
5. **No loops, ever — property-tested.** Poison-reverse + hop limit + the 600 s horizon are *correctness* features against routing storms (the same class as the trivial-loop guard). Property tests assert the invariants: target time is monotonic-nondecreasing per hop, no route advertises itself back to the neighbour it came from at a finite metric, hop count never exceeds the limit, a withdrawn route never resurrects without a fresh advertisement.

---

## 4. Wire formats

All on the interlink (a connected-mode AX.25 session, PID 0xCF) the vanilla stack already owns.

### 4.1 L3RTT (link-time measurement)

- An **L3 info datagram** (the existing 15-byte `NetRomNetworkHeader` + a transport body) addressed to the **literal callsign `L3RTT-0`**, **opcode `0x02`**, with a **text payload padded with spaces**.
- The neighbour **reflects the frame back unchanged**. The originator times the round trip; **RTT ÷ 2** is the raw neighbour transport time, fed into the SNTT smoother (§5).
- **Timeout: 180 s** — if a reflection doesn't return within 180 s, the interlink is **reset** (a link-down event that feeds the failover signal we just shipped — see [§5.9 of the plan](plan.md), `MarkNeighbourDown`).
- **Capability announcement:** the L3RTT text payload carries a **`$N` flag** advertising INP3 capability (`$IX` = IP-version-`X` accepted). This is how two nodes discover each other speak INP3 (and fall back to vanilla NODES if not).

New wire type: `Inp3L3RttFrame` (codec: build the padded `L3RTT-0`/opcode-0x02 datagram + the `$N` capability text; parse a reflection + extract the capability flags). Lives in `Wire/` next to `NetRomPacket`.

### 4.2 RIF / RIP (routing information)

- A **RIF** (Routing Information Frame) is a **numbered I-frame on the interlink** whose first byte is the **`0xFF` signature** (distinguishing it from a NODES broadcast, which is also 0xFF-signatured but UI/connectionless to `NODES` — the RIF rides the *connected* interlink). It carries one or more **RIPs**.
- A **RIP** (Routing Information Packet) = `[callsign, 7 bytes shifted][hop counter, 1 byte][target time, 2 bytes, MSB first][optional TLV fields][0x00 EOP]`.
  - **callsign**: the destination, AX.25-shifted (delegates to the existing `NetRomCallsign`/`ax25::Address` shift codec — one source of truth, as the read-only slice already does).
  - **hop counter**: hops to the destination.
  - **target time**: 2 bytes MSB-first, 10 ms units (so 0–65535 = 0–655.35 s; the spec horizon is **600 s / 0xEA60-ish** = unreachable).
  - **TLV fields**: `type 0x00` = alias, `type 0x01` = IP address; **unknown TLV types are stored-and-forwarded untouched** (forward-compat — do not drop a RIP because it carries a TLV we don't understand).
  - **`0x00` EOP** terminates the RIP.

New wire types: `Inp3Rip` (one routing entry + its TLVs) and `Inp3Rif` (the 0xFF-signed I-frame body = a sequence of RIPs). Codec is **total** (arbitrary bytes → `None`/`null`, never panic/throw — the same totality discipline the NODES parser has, validated by the fuzz harness).

### 4.3 TLV handling

A small `Inp3Tlv` model: `{ type: u8, value: bytes }`. Known types decoded into typed fields (alias → string, IP → `IPAddress`/`[u8]`); unknown types retained verbatim in an `unknown: Vec<Inp3Tlv>` and re-emitted on forward. This is the forward-compat seam INP3 explicitly requires.

---

## 5. Data model

### 5.1 Link timing (per interlink/neighbour)

Extend the neighbour record with INP3 state (only populated when INP3 is enabled):

```
Inp3NeighbourState {
    sntt_ms: u32,              // smoothed neighbour transport time (the link metric)
    last_l3rtt_sent_ms: u64,   // when we last sent an L3RTT probe (for the 180s timeout + probe cadence)
    last_reflection_ms: u64,   // when the last reflection returned (for the reset timer)
    inp3_capable: bool,        // learned from the peer's $N flag
    ip_accept: Option<u8>,     // from $IX, if advertised
}
```

**SNTT smoothing:** an exponential/IIR smoother on RTT/2, **integerised** (no FPU — the pico-node M0+ constraint; we already do this for the AX.25 SRT/Karn timer maths). The smoothing constant is a `NetRomInp3Options` knob; the spec's exact filter is the default. (Cross-stack: the smoother must be **byte-for-byte identical** across C#/TS/Rust so the three agree on link metrics — same discipline as the FNV-1a flow hash and the `(bq*pq+128)/256` quality formula.)

### 5.2 Routes — a second metric space

Today `NetRomRoute = (Neighbour, Quality:u8, Obsolescence:int)`. INP3 routes carry **target time + hop count** instead. Rather than overload `Quality`, add an **optional INP3 metric** to the route so a destination can hold quality-routes (from NODES) and time-routes (from RIF) simultaneously:

```
NetRomRoute {
    Neighbour,
    Quality: u8, Obsolescence: int,          // vanilla (NODES-learned)
    Inp3: Option<Inp3RouteMetric>,           // present iff INP3-learned
}
Inp3RouteMetric { target_time_ms: u32, hop_count: u8 }
```

**Selection policy** (a pure function, like `NetRomForwarding.Decide`): given a destination's routes and `NetRomInp3Options`:
- INP3 disabled → today's behaviour exactly (best quality).
- INP3 enabled, `preferInp3Routes = true` (BPQ's flag) → choose the lowest-`target_time` INP3 route if any; else fall back to best quality.
- INP3 enabled, `preferInp3Routes = false` → quality wins but INP3 routes are still maintained/advertised (the conservative default).

This keeps the multi-route load-balancer (`NetRomForwarding` per-flow) working — it spreads across the eligible routes in whichever metric space won.

### 5.3 The 600 s horizon = withdrawal

A target time of **≥ 600 s** means unreachable. Receiving a RIP at the horizon for a destination is a **withdrawal** → drop that route (and feed the failover signal). This is INP3's explicit route-poisoning encoding and dovetails with the `MarkNeighbourDown` primitive we just shipped.

---

## 6. Algorithms

### 6.1 RTT measurement loop (per interlink)

On a `tick(now)`: for each INP3-capable interlink, if it's time to probe (cadence = a `NetRomInp3Options` knob), emit an L3RTT send intent and stamp `last_l3rtt_sent_ms`. On `on_l3rtt(from, payload, now)`: if it's a **reflection of our probe**, compute RTT = `now − last_l3rtt_sent_ms`, feed RTT/2 to the SNTT smoother, stamp `last_reflection_ms`, and learn the peer's `$N`/`$IX` capability; if it's the peer's **probe to us**, reflect it unchanged. On `tick`: if `now − last_reflection_ms > 180_000`, **reset the interlink** (emit a reset intent → host DISCs/re-establishes → `MarkNeighbourDown`).

### 6.2 Triggered updates (RIF emission)

- **Periodic baseline:** emit a full RIF on each INP3 interlink at the INP3 interval (separate knob from NODESINTERVAL).
- **Triggered:** when a route's target time **worsens or the route is lost** (negative info), emit a RIF carrying just the changed RIP(s) **immediately and prioritised** (ahead of queued positive updates). **Positive** changes (a new/better route) are **debounced/batched** (a short timer) to avoid storms. The debounce + priority queue is a small state machine driven by the injected clock.
- **Poison-reverse / alternate-reverse:** when advertising a destination back toward the neighbour we route through for it, advertise it at the **horizon** (poison) — or advertise the alternate (alternate-reverse), per the spec — so the neighbour never believes it can reach the destination via us (loop avoidance). This is the INP3 analogue of the vanilla trivial-loop guard and is a **correctness** feature.

### 6.3 RIF ingestion

On `on_rif(from, bytes, now)`: parse the RIPs (total/fault-tolerant); for each, compute the local target time = `peer_target_time + this_link_SNTT + 10ms_per_hop`, increment hop count, and **upsert an INP3 route** to that destination via `from` (capped at the per-destination route limit, best-time-first), applying poison-reverse on re-advertisement. A RIP at the horizon withdraws the route. Unknown TLVs are retained for re-emission.

---

## 7. Architecture — where each piece lands

Mirrors the existing NET/ROM module split (`Wire/`, `Routing/`, `Transport/`, the host `NetRomService`):

| Piece | C# (`Packet.NetRom` + `Packet.Node.Core`) | TS (`@packet-net/ax25 src/netrom`) | Rust (pico-node `ax25-node-core::netrom`) |
|---|---|---|---|
| L3RTT codec | `Wire/Inp3L3Rtt.cs` | `wire/inp3-l3rtt.ts` | `netrom/wire/inp3_l3rtt.rs` |
| RIF/RIP/TLV codec | `Wire/Inp3Rif.cs` | `wire/inp3-rif.ts` | `netrom/wire/inp3_rif.rs` |
| SNTT smoother + route metric | `Routing/Inp3Metric.cs` + extend `NetRomRoute` | `routing/inp3-metric.ts` | `netrom/routing/inp3_metric.rs` |
| Selection policy (pure) | extend `NetRomForwarding` / a new `Inp3RouteSelector` | mirror | mirror |
| Engine (probe loop, triggered updates, ingestion) | `Transport/Inp3Engine.cs` | `inp3/inp3-engine.ts` | `netrom/inp3.rs` (sans-io) |
| Host wiring (interlink ↔ engine, reset → `MarkNeighbourDown`) | `NetRomService` | `NetRomConnector`/embedder tick | fw `session.rs` + the byte-seam |
| Options | `Wire/NetRomInp3Options.cs` | `inp3/options.ts` | `netrom/inp3/options.rs` |

The **interlink seam already exists** (the vanilla stack's connected-mode PID-0xCF sessions): in C#/TS via `Ax25Listener`-owned sessions, in Rust via the neighbour-keyed byte seam (`InterlinkSend` / `on_interlink_data`). INP3 frames ride the same seam — the engine just produces/consumes additional 0xCF datagrams on it. **The link-down failover signal (`MarkNeighbourDown`) shipped this week is the integration point for the 180 s L3RTT reset and the horizon withdrawal** — INP3 reuses it rather than inventing its own teardown.

---

## 8. Config surface (all default-off / interoperable)

```yaml
netRom:
  inp3:
    enabled: false            # the whole overlay; off = exactly today's behaviour
    preferInp3Routes: false   # BPQ PREFERINP3ROUTES: time-routes beat quality-routes when both exist
    l3RttIntervalSeconds: 60  # how often to probe each interlink
    l3RttResetSeconds: 180    # reflection-timeout → interlink reset (spec value)
    rifIntervalSeconds: 300   # periodic full RIF cadence (triggered updates fire regardless)
    hopLimit: 30              # routing horizon in hops
    advertiseIp: false        # emit the IP TLV (type 0x01) — off unless we run IP-over-NET/ROM
```

`NetRomInp3Options` is the in-code record; the YAML maps onto it via the existing config converter. Validator rejects out-of-range values. Hot-reload class: INP3 enable/disable is a **single-port-restart** (it changes the interlink engine), like a transport change.

---

## 9. Phasing (slices, mirroring the L4 work)

Each slice is C#-first (reference + tests) then cascaded; each is independently shippable and default-off.

- **I-1 — Wire codecs.** `Inp3L3Rtt` + `Inp3Rif`/`Inp3Rip`/`Inp3Tlv` encode/decode, total + fuzz-clean, byte-parity vectors shared across the three stacks. No behaviour yet. *(S)*
- **I-2 — Link timing.** The SNTT smoother (integerised, byte-identical) + the L3RTT probe/reflect loop + the 180 s reset wired to `MarkNeighbourDown`. Capability discovery (`$N`). *(S–M)*
- **I-3 — Route metric + selection.** Extend `NetRomRoute` with the INP3 metric; the pure selection policy (`preferInp3Routes`); RIF ingestion (upsert time-routes, horizon = withdraw). Property tests for the invariants. *(M)*
- **I-4 — Triggered updates + poison-reverse.** RIF emission: periodic + triggered (negative-immediate / positive-batched) + poison/alternate-reverse. The storm-safety property tests. *(M)*
- **I-5 — Our-fleet end-to-end + interop.** 3-stack deterministic harness (pdn↔ax25-ts↔pico-node over the in-process interlink: L3RTT converges, RIF propagates, a route fails over by time, poison-reverse blocks a loop). *Then* best-effort BPQ (`PREFERINP3ROUTES=1`) + XRouter interop on the lab/docker stack, characterising + flag-gating any divergence. *(M)*

---

## 10. Testing strategy

- **Unit / codec:** encode/decode totality + the shared byte-parity vectors (the three stacks must produce identical bytes — the FNV-1a/quality-formula discipline).
- **Property (the loop/storm-safety net):** target time monotonic-nondecreasing per hop; no finite-metric advertisement back to the route's own next-hop (poison-reverse holds); hop count ≤ limit; a withdrawn route stays gone without a fresh RIP; SNTT smoother is bounded + identical across stacks.
- **Deterministic engine tests** (fake clock): RTT→SNTT convergence; 180 s reset fires; triggered-negative beats batched-positive; horizon = withdrawal.
- **Our-fleet integration** (the headline tier): a 3-node in-process harness where all three runtimes run INP3 and converge on time-routes, fail over by measured time, and refuse a loop. Fully deterministic, fully in our control.
- **External interop** (best-effort tier, `Category=Interop`): pdn↔BPQ (`PREFERINP3ROUTES=1`) and pdn↔XRouter over the docker/lab stack. Because both peers' INP3 is shaky, a red here is "characterise + add a `NetRomInp3Options` flag + audit row", not "block the feature".

---

## 11. Cross-stack cascade

Same as every NET/ROM increment: **C# reference (with the canonical tests) → ax25-ts (mirror 1:1, the C# suite as oracle, publish `@packet-net/ax25`) → pico-node Rust (sans-io, mirror the TS suite, no_std-clean, push)**. The shared codecs + the SNTT smoother + the selection policy must be **byte/though-for-thought identical** so the three interoperate perfectly — which is what makes "our own fleet" a clean interop target. Watch the no_std constraints in Rust: integer-only SNTT maths (no FPU), fixed-capacity INP3 state (no heap map — const-generic arrays like the existing routing table), `u64`-tick time.

---

## 12. Risks & open questions

1. **Spec ambiguity vs the PDF.** The Gal spec is the reference but is ~1997 and terse; some details (exact SNTT filter constants, the precise poison-reverse vs alternate-reverse choice, RIF I-frame sequencing) may need calibration against a live INP3 peer. **Mitigation:** flag-gate anything ambiguous; default to the spec's stated value; capture the calibrated value in the audit doc.
2. **BPQ INP3 historically problematic (Tom).** May have improved; unverified. **Mitigation:** BPQ is the *best-effort* interop tier, not the reference; our-fleet is the acceptance bar. Validate against a live BPQ empirically and flag divergences; don't bend the spec-faithful core to a BPQ bug.
3. **Mixed-network instability** (XRouter's own warning). **Mitigation:** default-off; `preferInp3Routes=false` default keeps quality routing primary even when INP3 is on; the vanilla NODES path is untouched, so an INP3 node degrades gracefully to vanilla with non-INP3 neighbours.
4. **Metric-space coexistence.** Holding both quality and target-time routes per destination and picking between them is the subtlest design point. **Mitigation:** the pure selection policy is exhaustively unit + property tested; `preferInp3Routes` is the single switch; single-route destinations degenerate to today's behaviour.
5. **Is it worth it?** INP3's payoff (faster convergence, RTT-based routing) is mostly realised in a homogeneous/modern fleet; on a mixed amateur network it's a marginal, sometimes-destabilising improvement. This plan exists so the increment is *ready* — the decision to build it is Tom's, and the our-fleet-first framing means it has standalone value (a clean modern routing layer across pdn/ax25-ts/pico-node) even if BPQ/XRouter interop stays best-effort.

---

## 13. References

- **INP3 spec** — "A New Routing Specification for Packet Radio Datagram Networks", Andreas Gal DB7KG / NORD><LINK: https://wiki.oarc.uk/_media/packet:internodeprotocolnp.pdf
- **netrom-research.md** §1.4 (INP3), §1.5 (impl divergence map), §3.3 (sizing) — the grounding research.
- **BPQ** config (`PREFERINP3ROUTES`): https://www.cantab.net/users/john.wiseman/Documents/BPQCFGFile.html
- **XRouter** (INP3 experimental + incompatibility warnings): https://wiki.oarc.uk/packet:xrouter:docs:man9
- The shipped vanilla NET/ROM stack: `docs/plan.md` §5.9 + the §17 NET/ROM amendment entries; the link-down failover signal (`MarkNeighbourDown`) this plan reuses.
