# NET/ROM INP3 - BPQ / XRouter interop status (2026-06-08)

*Evidence-based resolution of the `docs/netrom-inp3-plan.md` §9 **I-5** "then best-effort BPQ (`PREFERINP3ROUTES=1`) + XRouter interop" item. This doc replaces that vague bullet with a concrete verdict: what each peer supports, whether a deterministic CI interop test is feasible **now** vs **after a named config delta**, and the exact delta required. Mirrors `docs/strict-vs-pragmatic-audit.md`: peers are interop targets, not reference truth; divergences become named `NetRomInp3Options` flags, not silent parser widening.*

## TL;DR

- **Our-fleet end-to-end is the acceptance bar and it is closed** (the I-5 3-stack deterministic harnesses - C# ↔ ax25-ts ↔ pico-node - over the in-process interlink; byte-identical codecs). External BPQ/XRouter interop is a **separate, best-effort tier** that can characterise + flag quirks without blocking the feature, exactly as plan §2 prescribes.
- **Both peers do implement INP3** (LinBPQ via `PREFERINP3ROUTES`; XRouter via its own author-labelled-experimental L3RTT/INP3). That is not the blocker.
- **A deterministic CI INP3 interop test is NOT feasible against the current docker stack** - and the reason is **structural, not timing**: ① neither `docker/linbpq/bpq32.cfg` nor `docker/xrouter/XROUTER.CFG` enables INP3, and ② the NET/ROM tests that talk to these peers over net-sim only observe **connectionless NODES broadcasts** - there is no **connected-mode PID-0xCF interlink** between pdn and the peer, and **L3RTT / RIF frames ride exactly that interlink**. No interlink ⇒ no INP3 traffic to observe.
- **One concretely-feasible path exists**, gated behind a named config delta: enable INP3 on the BPQ fixture (`PREFERINP3ROUTES=1` + interval pins) **and** reuse the existing `NetRomL4CircuitViaAxudp` interlink seam (pdn opens a real interlink to BPQ via `ConnectCircuitAsync`). With INP3 on at both ends and pdn's `L3RttInterval` shortened, pdn would observe BPQ's **L3RTT probe** on that interlink and reflect it - an observable, bounded, single-frame check.
- **One wire divergence is now recorded and deliberately not fixed** (§6, added 2026-08-16): pdn adds a link's measured cost on **receive**, BPQ adds it on **send**, so at a mixed boundary pdn double-counts the first link and BPQ sees pdn as a free zero-hop node. Harmless within our fleet (all three stacks agree) and dead while INP3 is off, but it must be flipped cross-stack *before* the BPQ tiers below mean anything.
- **Recommendation:** ship a `[SkippableFact]` skeleton **gated on both stack reachability AND the INP3-config-present precondition** (so it stays inert/green-skipped until the delta lands), and leave the BPQ config delta + an XRouter follow-up named in the plan. Do **not** modify `interop.yml` or the docker configs as part of I-5 - the deltas are spelled out below for a deliberate follow-up PR. **Do not block I-5 closure on a live external green.**

---

## 1. What each peer supports (public knowledge + source-grounded)

### 1.1 LinBPQ (G8BPQ)

| Aspect | Status | Source / grounding |
|---|---|---|
| INP3 implemented | **Yes.** BPQ implements INP3 link-time routing alongside vanilla NODES. | BPQ config reference (`BPQCFGFile.html`); plan §13. |
| Enable knob | **`PREFERINP3ROUTES=1`** - a global L3 directive: when both an INP3 (time) route and a vanilla (quality) route exist to a destination, prefer the time route. This is the exact knob our `NetRomInp3Options.PreferInp3Routes` mirrors (I-3). | BPQ config reference; plan §2/§8. |
| L3RTT probing | BPQ emits L3RTT datagrams (dest `L3RTT-0`, opcode 0x02, `$N`/`$IX` capability text) on its INP3-capable interlinks and reflects peers' probes - the same wire mechanic `Inp3L3RttFrame` builds/recognises. | I-1 wire-spec; codec `src/Packet.NetRom/Wire/Inp3L3Rtt.cs`. |
| Known risk | **Historically considered problematic** (Tom, plan §2/§12 risk #2): in mixed networks BPQ's INP3 has been observed to fill timer tables / behave erratically. May have improved in 6.0.25.x - **unverified against a live peer.** | plan §2, §12. |
| Version in stack | **LinBPQ 6.0.25.23** (the pinned `m0lte/linbpq` image). The mod-128 note in `bpq32.cfg` already source-quotes this build's `L2Code.c`; the INP3 path is compiled in but **off in our fixture**. | `docker/compose.interop.yml` pin; `docker/linbpq/bpq32.cfg`. |

**Treatment (CLAUDE.md discipline):** BPQ is the de-facto **vanilla** NET/ROM reference, but it is **not** the INP3 reference - its INP3 is the shaky tier. A divergence we find against live BPQ becomes a named `NetRomInp3Options` flag + a `strict-vs-pragmatic-audit.md` row, never a bend of the spec-faithful core.

### 1.2 XRouter (G8PZT)

| Aspect | Status | Source / grounding |
|---|---|---|
| INP3 implemented | **Yes, but author-labelled experimental.** XRouter's own docs call its INP3 experimental and warn that enabling it can leave timer tables full of nodes that are "unconnectable and incompatible with vanilla NetRom." | XRouter man9 (`wiki.oarc.uk/packet:xrouter:docs:man9`); plan §2/§13. |
| RTT → quality | XRouter maps measured RTT to a quality-ish number ("British notion of quality") - **do not adopt its RTT↔quality maths as canonical**; our metric is target-time-in-10ms-units per the Gal spec. | plan §2. |
| Enable knob | An INP3/L3RTT enable exists in XRouter config, but the **knob name + semantics in this pinned image are unconfirmed** - the current `XROUTER.CFG` does not set it, and XRouter's config is column-1-strict and image-version-sensitive. | `docker/xrouter/XROUTER.CFG`. |
| Version in stack | `ghcr.io/packethacking/xrouter:latest` @ 2026-05-12 (pinned by digest). | `docker/compose.interop.yml`. |

**Treatment:** XRouter is the **lowest-confidence** INP3 peer (experimental by its own author + a config surface we have not exercised for INP3). It is best-effort-of-best-effort: characterise if/when it is cheap, never block on it.

---

## 2. Why a deterministic CI test is not feasible *now*

Two independent blockers, both structural:

### 2.1 INP3 is off in both fixtures

`docker/linbpq/bpq32.cfg` runs `SIMPLE=1` (vanilla NODES defaults) with **no `PREFERINP3ROUTES`** and no INP3 interval directives. `docker/xrouter/XROUTER.CFG` sets no INP3/L3RTT knob. So neither peer emits L3RTT or RIF today. (Confirmed by reading both fixtures end-to-end.)

### 2.2 The peers are reached over the wrong AX.25 mode for INP3

This is the deeper blocker. INP3's two observable behaviours both ride a **connected-mode AX.25 PID-0xCF interlink**:

- **L3RTT** - an L3 info datagram exchanged on the interlink (probe → verbatim reflection).
- **RIF/RIP** - a `0xFF`-signature **numbered I-frame on the connected interlink** (distinct from a vanilla NODES broadcast, which is UI/connectionless to `NODES`).

But the existing pdn↔peer NET/ROM interop tests observe only **connectionless** behaviour:

| Test | Seam | INP3-observable? |
|---|---|---|
| `Netsim/NetRomNodesIngestViaNetsim` | passive promiscuous receiver - hears BPQ + XRouter **NODES UI broadcasts**; never opens an interlink | **No** - no interlink, no L3RTT/RIF |
| `Linbpq/NetRomNodesIngestViaAxudp` | warms BPQ with pdn's own NODES; observes BPQ's NODES UI back | **No** - connectionless only |
| `Linbpq/NetRomL4CircuitViaAxudp` | **opens a real interlink** to BPQ via `ConnectCircuitAsync` (PID-0xCF AX.25 session) for an L4 circuit | **Yes - this is the only existing seam that builds the interlink INP3 needs** |

So even if we flipped INP3 on in the BPQ fixture, the NODES-ingest tests would see nothing new - INP3 frames do not appear on the broadcast path. A real interlink has to exist, and only the L4-circuit test stands one up.

### 2.3 Net-sim half-duplex timing makes the broadcast tier flaky anyway

The net-sim NODES-ingest path is already documented as environmentally flaky (half-duplex AFSK channel; plan §7.1). The L4-circuit test side-steps this by running over the **AXUDP** lossless tunnel (`NetRomL4CircuitViaAxudp`), which is also where the feasible INP3 path lives. So the feasible path is *also* the load-insensitive one - convenient.

---

## 3. The one concretely-feasible path (and its exact config delta)

**Claim:** with a named config delta, pdn can deterministically observe **BPQ's L3RTT probe on a live interlink and reflect it** - a bounded, single-frame, lossless-tunnel check. This is feasible because:

1. The wire codec is shipped + total (`Inp3L3RttFrame.TryParse`/`IsL3Rtt`), and the host already peels L3RTT off the interlink stream **before** L4 (`NetRomService.Inp3.cs DispatchInp3`, precedence (B)).
2. The interlink seam already exists and is proven over AXUDP (`NetRomL4CircuitViaAxudp` → `ConnectCircuitAsync` opens the PID-0xCF session).
3. pdn's INP3 overlay is config-reachable from a test: `new NetRomConfig { Enabled = true, Connect = true, …, Inp3 = new NetRomInp3Options { Enabled = true, L3RttInterval = <short> } }`. With `Connect = true` + `Inp3.Enabled`, the `Inp3Host` is constructed (`NetRomService.cs:189`), and on any 0xCF frame from a neighbour the host calls `ObserveNeighbour` and begins probing (optimistic probing is on by default).

### 3.1 What the test would assert (the minimal useful check)

Over the AXUDP interlink to BPQ, with INP3 on at **both** ends:

- **Tier A (pdn-side, always assertable once INP3 is on at pdn):** pdn sends its **own** L3RTT probe to BPQ on the interlink and folds BPQ's **reflection** into a finite SNTT for that neighbour (`Inp3EngineForTest.SnttMs(bpq) is not null`). This proves BPQ **reflects** L3RTT verbatim - a real interop fact - and needs only pdn's INP3 on (BPQ need not originate, only reflect, which any L3-datagram-forwarding node does).
- **Tier B (BPQ-originated, needs BPQ INP3 on):** pdn observes BPQ's **own** L3RTT probe arriving (`$N` capability) and reflects it; pdn marks BPQ `Inp3Capable`. This is the stronger "BPQ actively speaks INP3" assertion and is what `PREFERINP3ROUTES`/INP3-enable in the fixture unlocks.
- **Tier C (RIF, optional, highest-risk):** pdn ingests a BPQ-originated **RIF** (`0xFF`-led I-frame) and upserts a time-route. Deferred - RIF emission cadence + the alias-TLV ambiguity (I-4 AMBIGUITY-I4-1, locked off) make this the least deterministic; characterise only after Tiers A/B are green.

**Tier A is the headline feasible check** - it needs only pdn's INP3 enabled and a reachable BPQ interlink, because verbatim L3-datagram reflection is behaviour any NET/ROM node exhibits regardless of its INP3 setting. Tier B is the BPQ-INP3-on upgrade.

### 3.2 The exact config delta required (recommended, NOT applied here)

A follow-up PR (not I-5) would add to `docker/linbpq/bpq32.cfg`:

```ini
; INP3 link-time routing — enable so the BPQ fixture ORIGINATES L3RTT/RIF on its
; interlinks (Tier B of docs/netrom-inp3-interop.md). PREFERINP3ROUTES makes BPQ
; prefer measured-time routes over quality routes when both exist; the L3RTT/INP3
; interval pins shorten BPQ's probe cadence so a CI test observes a probe within
; seconds rather than BPQ's stock cadence. EXACT directive names + minute/second
; units must be confirmed against the BPQ 6.0.25.23 config reference before this
; lands — they are version-sensitive and column/case-sensitive like the rest of
; bpq32.cfg. Validate on the wire that BPQ emits an L3RTT-0 datagram on the
; interlink the L4-circuit test opens.
PREFERINP3ROUTES=1
; INP3INTERVAL / L3RTTINTERVAL (name TBC) = 1   ; minimum, for CI observability
```

> **Why this stays a follow-up, not part of I-5:** the directive names are unverified against this BPQ build, and turning INP3 on in the shared fixture risks perturbing the *vanilla* NET/ROM interop tests that share the daemon (BPQ's documented "timer tables fill" risk). It needs its own PR with on-the-wire verification + a check that the existing `NetRom*ViaAxudp` / `NetRomNodesIngest*` greens are unaffected. **Tier A does not need this delta** - only pdn's INP3 - so the skeleton's Tier-A path can go green the moment a future config marker is present (§4), independent of the BPQ-origination work.

For XRouter, the equivalent delta (an INP3/L3RTT enable in `XROUTER.CFG`) is **left unspecified** - the knob name in the pinned image is unconfirmed and XRouter's INP3 is experimental. Recommend deferring XRouter INP3 interop until BPQ Tier A/B is green and the live behaviour is understood; capturing it then as a `NetRomInp3Options` quirk row if it diverges.

---

## 4. The SkippableFact: gating discipline

A skeleton ships at `tests/Packet.Interop.Tests/Linbpq/NetRomInp3L3RttViaAxudp.cs`, `[Trait("Category","Interop")]` + `[Trait("Group","NetRom")]`, `[Collection(NetsimCollection.Name)]` - the established interop shape. It is **double-gated** so it is inert until the stack *and* the INP3 config are both present:

1. `Skip.IfNot(await IsTcpPortReachable(Host, BpqHttpPort), …)` - the standard stack-up gate (matches `NetRomL4CircuitViaAxudp`). Skips green when the docker stack is down.
2. `Skip.IfNot(BpqInp3FixtureMarkerPresent(), …)` - an **INP3-config-present precondition**. Until the §3.2 fixture delta lands, this skips green, so the test never red-flags a stack that simply hasn't enabled INP3. The marker is a cheap, deliberate signal (e.g. an env var `PDN_INTEROP_BPQ_INP3=1` the future config-delta PR sets in `interop.yml`, or a probe of a BPQ INP3-status line over telnet) - chosen so flipping it on is an explicit, reviewable act, not an accident.

The body drives **Tier A** (pdn-INP3-on, observe BPQ's reflection → finite SNTT) as the primary assertion, with a commented Tier-B block (BPQ-originated probe) to un-comment once the fixture delta + marker are in. This keeps the skeleton honest: it asserts the one thing that is feasible the moment INP3 is enabled at pdn against a reachable BPQ, and documents the upgrade path inline.

> **Why gate Tier A behind the marker too, when it only needs pdn's INP3?** Because Tier A still requires a *reachable, INP3-reflecting* BPQ interlink that the CI lane is intended to exercise, and because we do not want a half-configured stack to flake the NET/ROM phase. The marker is the single switch that says "this lane is meant to run INP3 now" - until the follow-up PR flips it, the row stays a green skip and I-5 is closed without a flaky external dependency.

---

## 5. Recommendation (the I-5 resolution)

1. **Close I-5 on the our-fleet bar.** The 3-stack deterministic harnesses are the acceptance criterion (plan §2/§10); they are done. External interop is explicitly best-effort and non-blocking.
2. **Ship the double-gated `[SkippableFact]` skeleton** (this PR) so the lane exists, is discoverable, and is green-skipped until enabled - no `interop.yml` / docker-config change.
3. **File a named follow-up** for the BPQ INP3 fixture delta (§3.2): verify the directive names against BPQ 6.0.25.23, enable INP3 on a *copy*/guarded path, confirm on the wire BPQ emits L3RTT on the L4-circuit interlink, confirm the vanilla NET/ROM greens are unperturbed, then flip the marker to activate Tiers A→B. This is where the real external evidence gets captured.
4. **Defer XRouter INP3 interop** behind BPQ - experimental peer + unconfirmed config knob. Characterise + flag (`NetRomInp3Options` row) if/when it is cheap.
5. **Any divergence found against either live peer** is a named `NetRomInp3Options` flag + a `strict-vs-pragmatic-audit.md` row (CLAUDE.md discipline), never a bend of the spec-faithful core. A red against BPQ/XRouter INP3 is "characterise + flag", not "we're wrong" (plan §2).

---

## 6. Known divergence: where the per-hop cost is added (C099, 2026-08-16)

*Found by the 2026-08-16 code review ([#697](https://github.com/packet-net/packet.net/issues/697) item C099, umbrella #703). Recorded here rather than fixed; the decision and its reasoning are below.*

### 6.1 The two models

Both models produce **identical routing tables when both ends use the same one**; they differ only in *which* end adds a link's cost, and therefore in what a RIP's metric means on the wire.

| | pdn (all three stacks) | LinBPQ |
|---|---|---|
| Own-node RIP | `hop 0`, `target time 0` (`NetRomRoutingTable.BuildRif`) | `hop 1`, `target time = RTTIncrement`, the cost of the link it is sending on (`BPQINP3.c:1155,1757`) |
| Relayed RIP | our stored local metric, unchanged (`BuildRif`) | `sendHops = Hops + 1`, `sendTT = STT + RTTIncrement` (`BPQINP3.c:1408,1541`) |
| On receipt | `localTargetTimeMs = rip.TargetTimeMs + neighbourSnttMs + 10`, `localHopCount = rip.HopCount + 1` (`IngestRif`, locked in `netrom-inp3-i3-design.md` §2.3) | stored verbatim; explicitly does **not** add the link RTT (`BPQINP3.c:456`: *"Don't do this - other end has added linkrtt"*) |
| A RIP therefore means | cost from the **sender** to D, excluding the link you heard it on | cost from the **receiver** to D, including that link |

**At a mixed boundary both directions are wrong:** pdn adds a link cost BPQ already added (double-counting the first link, and one extra hop), while BPQ stores pdn's own-node RIP verbatim as `hop 0 / 0 ms`: a free, zero-hop route to us that outranks everything it holds.

### 6.2 Decision: keep the receiver-adds model for now; flip it as part of I-5, cross-stack

**Not changed in the WP10 review PR.** Reasons, in order of weight:

1. **The our-fleet bar is what a unilateral flip would break.** `Packet.NetRom` (C#), `@packet-net/ax25` (`src/netrom/routing-table.ts`) and pico-node (`crates/ax25-node-core/src/netrom/routing/table.rs`) all implement receiver-adds identically and byte-for-byte, and §TL;DR makes the 3-stack harness the acceptance criterion. Changing one stack reproduces the *exact* BPQ-boundary pathology **inside our own fleet** for as long as a fleet is mixed: an upgraded node advertises itself at hop 1 with the cost pre-added, a not-yet-upgraded neighbour adds it again, and the upgraded node stores its neighbours at `0 ms / 0 hops`.
2. **Nothing is broken today.** INP3 is opt-in (`netrom.inp3` defaults Disabled) and enabled in no fixture (§2.1), so this is future-interop drift, not a live defect. Severity was corrected to *low* on that basis.
3. **The spec side is unverified.** The BPQ behaviour above is read from source. The INP3 PDF line usually quoted for this ("the hop-counter has to be 1") is **not** in this repo, and `netrom-inp3-i1-wire-spec.md` records the octet only as a *"hop count"*, *"hops to the destination"*, which does not settle whose perspective it is. Re-reading the Gal PDF is step 1 of the flip, not an afterthought: this repo does not take BPQ as the spec (CLAUDE.md).
4. **It is an API change, not a constant change.** `BuildRif` would need the per-neighbour SNTT it does not currently take (to add the outgoing link's cost per RIF), and `IngestRif` would stop needing the `neighbourSnttMs` it does take. That ripples through `NetRomService.Inp3.cs`, the I-3/I-4 design docs' locked math, invariants (M) and (Source) and their property tests, and the equivalents in two sibling repos.

**No named flag was added.** A `NetRomInp3Options` toggle between the two models is not the fix: the models must agree network-wide, so a per-node knob just moves the mixed-boundary problem behind config. When the flip happens it is a straight change of the wire semantics in all three stacks at once.

### 6.3 What the flip entails (for the I-5 follow-up)

1. Re-read the Gal INP3 PDF on the own-node RIP's hop counter and the metric's perspective; record the quote here.
2. `BuildRif(myCall, toTargetNeighbour, snttToThatNeighbour, recentlyWithdrawn)`: emit the own node at `hop 1 / quantise(sntt + PerHopIncrementMs)`, and every relayed RIP at `hop + 1 / quantise(stored + sntt + PerHopIncrementMs)`, horizon-clamped on the way out.
3. `IngestRif`: store `rip.HopCount` / `rip.TargetTimeMs` verbatim; the `neighbourSnttMs` parameter and its `linkMeasured` gate go (an unmeasured link no longer blocks learning, which also removes the "un-probed link learns nothing" skip).
4. Update `netrom-inp3-i3-design.md` §2.3, `netrom-inp3-i4-design.md` §1.1/§2 (invariants (M) and (Source): the own node becomes `1 / link cost`, never `0/0`), and the property tests that pin them.
5. Land it in `packet.net`, `ax25-ts` and `pico-node` together, and re-run the 3-stack harness before any BPQ tier runs.

### 6.4 Not a divergence: per-port neighbour keying (#725)

Since port-concept PC4 a NET/ROM neighbour is the **(port, callsign)** pair, and `NetRomRoutingTable.IngestRif` files a RIF's time-routes under the adjacency it arrived on ([`netrom-multiport-neighbours.md`](netrom-multiport-neighbours.md)). Unlike §6.1 this is **not** a fleet-consistency question: no port appears in a RIP or a NODES entry, so each stack can adopt it independently and a mixed fleet is unaffected throughout.

What it does change for INP3 is one interim rule. `Inp3Engine` and `Inp3UpdateScheduler` are still keyed by callsign, and SNTT is a per-*link* measurement - blending two links to one station would corrupt exactly the number INP3 exists to carry. So INP3 runs on a neighbour's **selected link** only (`NetRomService.SelectedInp3Port`: the port of its best adjacency, else its best live interlink); a RIF or L3RTT arriving on any other link to that station is dropped with a log line, and an L4 datagram is never gated by the rule. Keying the engine and the scheduler per (port, callsign) - which is what BPQ does, storing `SRTT`/`RTTIncrement` on the `ROUTE` - is [#733](https://github.com/packet-net/packet.net/issues/733), and it is a three-stack change for a default-off feature, which is why it is not folded in here.

---

## 7. References

- `docs/netrom-inp3-plan.md` §2 (interop targets ranked), §9 (I-5), §10 (testing tiers), §12 (risks).
- `docs/netrom-inp3-i1-wire-spec.md` (L3RTT + RIF/RIP wire formats); `…-i2-design.md` (L3RTT engine, reflection, capability `$N`); `…-i4-design.md` (RIF emission, AMBIGUITY-I4-1 alias-TLV locked off).
- `src/Packet.NetRom/Wire/Inp3L3Rtt.cs`, `…/Wire/Inp3Rif.cs`; `src/Packet.Node.Core/NetRom/NetRomService.Inp3.cs` (`DispatchInp3` precedence; `Inp3Host`).
- `tests/Packet.Interop.Tests/Linbpq/NetRomL4CircuitViaAxudp.cs` (the interlink seam + `SkippableFact`/`IsTcpPortReachable` pattern reused here).
- `docker/linbpq/bpq32.cfg`, `docker/xrouter/XROUTER.CFG`, `docker/compose.interop.yml` (version pins); `.github/workflows/interop.yml` (the clean-stack-fenced NET/ROM phase a live test would join).
- BPQ config reference (`PREFERINP3ROUTES`): https://www.cantab.net/users/john.wiseman/Documents/BPQCFGFile.html
- XRouter man9 (INP3 experimental + incompatibility warning): https://wiki.oarc.uk/packet:xrouter:docs:man9
- INP3 spec (Gal DB7KG / NORD><LINK): https://wiki.oarc.uk/_media/packet:internodeprotocolnp.pdf
