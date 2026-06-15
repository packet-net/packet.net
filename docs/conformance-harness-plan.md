# Conformance harness & generative testing — sub-plan

Sub-plan of [`docs/plan.md`](plan.md). Status: **proposed** (2026-06-02).

## Why

The two-session integration rigs (`DataLinkSrejUnderLossTests`, `DataLinkTimerRecoveryIntegrationTests`, `DataLinkConnectedRetransmitTests`) are quietly more than tests — they're a **deterministic protocol oracle**. Two real `Ax25Session` instances run the real SDL tables over an in-process `Link`, driven by a `FakeTimeProvider` and a single-threaded, ordered `Settle()` pump, with a scriptable per-frame channel. Every run is a pure function of `(scenario, seed)`: reproducible, replayable, and *shrinkable*. That's exactly the substrate property-based testing wants — and it found #231 (every connected-mode retransmit renumbered → no loss recoverable) on first real use, with a one-frame deterministic repro.

The ambition: **one substrate that stress-tests three layers** —

1. **the runtime engine** (does packet.net implement the figures correctly?),
2. **the SDL figures** in `packet-net/ax25sdl` (are the figures themselves correct?), and
3. **AX.25 itself** (does the protocol's design satisfy the invariants under adversarial conditions?).

The repo already seeds this: FsCheck window invariants + the no-stuck-TimerRecovery property. This plan generalises that from hand-written single-session properties to **generated two-session scenarios judged against a shared invariant oracle**, and routes every failure to the right layer.

## Guiding principle: happy-path first, then adversarial

It's a new stack. Before throwing generated chaos at it we do two things, in order:

1. **Prove the fundamentals end-to-end** — connect, transfer, flow-control, disconnect, both moduli — deterministically, with zero channel disruption. A young stack's cheapest, highest-value bugs live on the happy path.
2. **Validate the oracle on cases where we already know the answer.** A fuzzer is only as trustworthy as its invariant checker; an oracle debugged against green happy-path scenarios is one we can *believe* when it later flags a generated counterexample. Shipping the oracle straight into adversarial generation risks chasing oracle bugs instead of protocol bugs.

So the build order is **Phase H (happy-path conformance + reusable primitives + a proven oracle)** → **Phases A1–A4 (progressively adversarial generative testing)**. Phase H is not throwaway: it doubles as living documentation and as the always-on regression suite for the stack.

## Reusable primitives (built in Phase H, used by every later phase)

- **`LinkScenario`** — a serialisable script of steps. A step is an *application action* (DL-CONNECT / DL-DATA(payload) / DL-DISCONNECT on either station), a *channel action* (deliver / drop / delay / reorder / duplicate / corrupt / bit-flip a named control field), or a *time advance* (fire T1/T2/T3 deterministically).
- **Driver** — `Settle()` (drain both inbound queues to quiescence) + `AdvanceTimers(dt)`; single-threaded, ordered, no wall-clock.
- **Channel** — the in-process `Link` with a per-frame policy. Phase H uses deliver-only; later phases enable the disruptive verbs.
- **`InvariantChecker`** — the oracle below, run after every step.
- **Two-session builder** over the real SDL tables + `FakeTimeProvider`, parametrised on modulus (8 / 128), SREJ on/off, and window `k`.

## The invariant catalogue (the oracle — the shared contract across all phases and both implementations)

**Safety** (must hold after every step):

- **Reliable delivery** — what station B surfaces upward (DL-DATA indications) is an in-order, gap-free, duplicate-free prefix of what A submitted, and vice versa.
- **Content integrity** — a delivered info field is byte-identical to the submitted one.
- **Sequence sanity** — `0 ≤ (V(s) − V(a)) mod N ≤ k` at all times; every transmitted N(s)/N(r) lies in the legal window. *(This alone catches #231 instantly — V(s) ran away.)*
- **No transition throws** — no `(state, event)` ever throws (the #225/#55 class: a non-DL-DATA trigger hitting a DL-DATA verb). Any throw is a bug, engine or figure.
- **Defined states only** — both machines occupy only defined states; no "no transition for event" surprises beyond defined no-ops.

**Liveness** (must hold eventually):

- **Convergence after a clean tail** — given a *finite* channel-disruption prefix followed by a clean channel, the link converges (all submitted data delivered, `V(s) == V(a)`, both stations in a stable state) **or** cleanly disconnects (N2 exhausted), within a bounded number of T1 cycles. This is the anti-storm / anti-livelock invariant — exactly what #231's retransmit storm violated.
- **Clean teardown terminates** — a disconnect always drives both sides to Disconnected; no half-open links.

## Phases

### Phase H — Happy-path conformance (do first)

Build the primitives + oracle and exercise them on hand-written, loss-free scenarios where every invariant must hold and the outcome is known. Coverage envelope of normal operation:

- **Lifecycle** — SABM/UA connect (each side initiating); DISC/UA disconnect; SABM/SABM collision; DM-on-refuse; reconnect; connect-while-connected.
- **Data** — single I-frame; fill the window (`k`); cross the mod-8 wrap (V(s) 7→0); bidirectional simultaneous data; min and max info-field sizes.
- **Modulus** — mod-8 and mod-128 (extended) both.
- **Segmentation / reassembly** (§6.6) end-to-end over the session.
- **Flow control** — own-busy and peer-busy (RNR) set/clear cycles; T2 delayed-ack; T3 keep-alive (no spurious T1).
- **SREJ-enabled and REJ-only** in-sequence happy paths.

**Exit criteria:** a green happy-path suite, a trustworthy `InvariantChecker` (proven on cases with known answers), and the reusable `LinkScenario`/driver primitives.

### Phase A1 — Engine fuzzing (in-process, single implementation)

FsCheck generates `LinkScenario`s over the full alphabet. Disruption is a **finite prefix followed by a clean tail**, so the convergence invariant is well-posed (see Risks — this also dodges the determinism×determinism phase-lock false positive). Shrinking yields minimal repros. Catches engine (L1) and figure (L2) bugs together.

### Phase A2 — Engine-vs-figure auto-triage

Dual-run each generated scenario under `Ax25SessionQuirks.StrictlyFaithful` (figures as drawn) vs `Default` (quirked). **Strict breaks + Default holds ⇒ a known figure defect we've already quirked. Both break ⇒ an unquirked figure defect or an engine bug.** Every counterexample is auto-tagged, turning the quirk facility into a spec-vs-implementation differential.

### Phase A3 — Differential conformance (cross-implementation)

Serialise scenarios to language-neutral JSON; run the *same* suite + invariants in `packet-net/ax25-ts` → C#↔TS differential. Then feed survivors into the docker interop stack (LinBPQ / Xrouter / rax25 / direwolf) → de-facto differential. Divergence on identical input means someone's wrong; cross-check against the figure + the de-facto citations. This is also how figures get validated against reality: all de-facto impls agree but `StrictlyFaithful` disagrees ⇒ the figure is the outlier (the #38 pattern, found automatically rather than on a hardware bench). This is the "shared conformance vectors" idea — except the vectors are *generated and shrunk*, not hand-authored.

### Phase A4 — Coverage-guided generation + SDL coverage metric

Instrument which SDL transition IDs fire (`t24_srej_…`, the figc4.7 retransmit loop, …); steer generation to maximise transition/path coverage; report **SDL coverage as a first-class metric** — "every t-path in every figure, exercised under loss" becomes measurable.

## Where findings go (the three layers)

- **Engine (L1)** — runtime mis-implements a correct figure → fix in packet.net (e.g. #231).
- **Figure (L2)** — a faithful execution of the figure still breaks an invariant → raise a `packethacking/ax25spec` issue + add a named `Ax25SessionQuirks` quirk (e.g. #38). The Strict-vs-Default dual-run (A2) is the detector.
- **AX.25 design (L3)** — faithful, and no reasonable figure fix recovers it → the protocol itself can't satisfy the invariant under that scenario. Rare, and the gold: feed the v2.3 draft.

## Risks & cautions

- **Oracle trust** — build and validate the `InvariantChecker` on Phase-H green scenarios before believing any adversarial verdict.
- **Determinism × determinism** — a seeded disruption stream against a deterministic protocol can phase-lock into a livelock that no real (independent-loss) channel would ever hit (the `LossyHardwareSender` lesson, #223). Frame liveness as *"converges once disruption ceases"* (finite prefix + clean tail), never *"converges under unbounded adversarial loss"* (which no protocol can promise).
- **CI budget** — generative runs are gated / nightly (like the existing fuzz workflow), not on every push; the Phase-H happy-path suite runs on every push. Self-hosted runners only.
- **Reproducibility** — deterministic seeds + serialised scenarios ⇒ no flakes; every failure replays and shrinks.

## Relationship to existing work

- FsCheck window invariants + no-stuck-TimerRecovery = the seed; this generalises them to two-session generated scenarios.
- The integration rigs = the prototype substrate (to be refactored into the reusable primitives).
- `Ax25SessionQuirks` = the figure-deviation mechanism and the L2 routing/detector.
- The original "shared conformance vectors" goal = the serialised generated scenarios of Phase A3.

## First steps

1. **(Phase H)** Extract `LinkScenario` + driver + `InvariantChecker` from the existing rigs; write the happy-path coverage envelope above; get it green; prove the oracle.
2. **(Phase A1)** Wire FsCheck over the alphabet with finite-prefix disruption + shrinking.
3. A2 → A3 → A4 as above.

Each phase is independently valuable and shippable on its own.
