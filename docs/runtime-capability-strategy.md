# Runtime capability strategy

> Cross-runtime alignment for consumers of the AX.25 SDL codegen.

This document describes *how* we track and *aim to* align the AX.25 capabilities exposed by every runtime that consumes the codegen output. It is a strategy doc — not a per-runtime task list and not a prescription of how each runtime is implemented. The current-state snapshot (the matrix) lives next door at [`runtime-capability-matrix.md`](runtime-capability-matrix.md).

The strategy is forward-looking in places (the conformance suite is sketched, not built). Where it sketches something not-yet-built, the section says so and points at a follow-up.

---

## 1. Why this exists

Packet.NET ships AX.25 v2.2 multiple times, in multiple languages, from a single SDL source. Today:

- **C#** — `src/Packet.Ax25/` — the reference runtime. The figures' transcribed transitions are walked by `Ax25Session`; predicates and action verbs bind through `Ax25SessionBindings` / `ActionDispatcher`; the figc4.7 subroutines run through `DefaultSubroutineRegistry`. Frame codec, KISS framing, and several transports (TCP, AGW, AXUDP, USB-serial / NinoTNC) live alongside.
- **TypeScript** — `packet-net/ax25-ts` — a browser-first runtime that walks the same generated SDL transition tables (from `ts-spec/src/ax25sdl/`). Same architecture as the C# runtime (same dispatcher / guard / bindings shape) but a much smaller transport surface and a smaller subset of the figc4.7 subroutines wired.
- **Go / Rust / C / JSON / Python** — emitters exist; no runtimes yet. The codegen ships *data* in those targets, not behaviour.

Drift between runtimes shows up as cross-runtime interop bugs. A frame parsed differently on each side, a state-machine transition that gets walked in one runtime but not the other, a subroutine that's wired in C# but stubbed in TS — all of these produce a "wire-compatible but subtly different" footgun. The footgun is *much worse for a packet node* (the server side that's expected to interoperate with everything in the wild) than for a client app — a node operator inherits the bug surface of every peer that connects to it.

The codegen lints catch the cheapest version of drift automatically: every predicate name referenced in the SDL must be bound in each runtime's bindings file; every action verb (post alias resolution) must have a dispatcher case. Those two checks ship today via `spec-sdl/lint-targets.yaml` ([introduced 2026-05-16](plan.md#17-amendment-log)). What the lints **don't** catch:

- A subroutine that's wired in one runtime and a no-op stub in another (the lint allows the `subroutines:` section to be absent per target, on purpose — see [the lint-targets config](../spec-sdl/lint-targets.yaml)).
- A state page that's transcribed in the SDL but lacks a transcribed *opposite-direction* state. The codegen happily walks `Connected` whose t38/t39 target `TimerRecovery` regardless of whether `TimerRecovery` itself is transcribed.
- A frame type the codec can encode but not decode (or vice versa).
- A transport that exists on one runtime and is missing on the other.
- A lifecycle feature — inbound listener, retry budget, dynamic T1 — that's wired one place and absent the other.

This document fills that gap. The matrix is the human-readable backstop for everything the lints can't see.

---

## 2. Stakes — who gets hurt by drift

Three audiences, in increasing order of pain:

1. **Library users on one runtime.** Cosmetic — they get whichever capability surface that runtime ships. Documented behaviour is honoured; missing behaviour is documented as missing.
2. **Cross-runtime developers.** A team that builds the same app in two languages (e.g. a browser client + a Node bridge) and expects the same wire output. Drift here surfaces as "the C# version works against BPQ but the TS version doesn't" or "the TS app times out where the C# app retries".
3. **Node operators.** A station running a packet *node* (server) talks to whatever shows up on the air — anything from a hand-rolled APRS gateway to a 1990s firmware TNC. Every gap in our spec coverage is a gap *the node operator* inherits. If the C# node correctly generates FRMR on a malformed frame and a hypothetical TS node silently drops it, the C# node converges with the peer and the TS node hangs. The node-side runtime carries the biggest "match the spec faithfully" obligation.

Today only C# can host a packet node — the foundational `Ax25Listener` API (shipped 2026-05-16) is C#-only. The first consumer beyond tests is `Packet.Term` (the TUI); `src/Packet.Node/` is still an empty scaffold. When TS gets a listener (see matrix row), the node-pain-surface doubles.

---

## 3. Capability taxonomy

A capability is anything a downstream consumer might reasonably ask "does this runtime do X". The taxonomy below covers the AX.25 v2.2 spec surface, the transports that carry it, and the lifecycle features that wrap it. Categories are organised by layer (codec → state-machine → session → transport → role).

Each category becomes a *row group* in the matrix. Anchor ids are stable so other docs can deep-link.

### 3.1 Wire-format codec

Per-frame-type encode + decode. Mod-8 and mod-128 are separate columns within the row because mod-128 is a v2.2-only superset.

Rows:

- <a id="cap-frame-sabm"></a>**SABM** (§4.3.3.1) — encode + decode
- <a id="cap-frame-sabme"></a>**SABME** (§4.3.3.2) — encode + decode, mod-128 only
- <a id="cap-frame-disc"></a>**DISC** (§4.3.3.3) — encode + decode
- <a id="cap-frame-ua"></a>**UA** (§4.3.3.4) — encode + decode
- <a id="cap-frame-dm"></a>**DM** (§4.3.3.5) — encode + decode
- <a id="cap-frame-frmr"></a>**FRMR** (§4.3.3.6) — encode + decode
- <a id="cap-frame-xid"></a>**XID** (§4.3.4.1) — encode + decode
- <a id="cap-frame-test"></a>**TEST** (§4.3.4.2) — encode + decode
- <a id="cap-frame-rr"></a>**RR** (§4.3.2.1) — encode + decode, both modulus
- <a id="cap-frame-rnr"></a>**RNR** (§4.3.2.2) — encode + decode, both modulus
- <a id="cap-frame-rej"></a>**REJ** (§4.3.2.3) — encode + decode, both modulus
- <a id="cap-frame-srej"></a>**SREJ** (§4.3.2.4) — encode + decode, mod-128 only
- <a id="cap-frame-i"></a>**I-frame** — encode + decode, both modulus
- <a id="cap-frame-ui"></a>**UI** — encode + decode

### 3.2 KISS framing

- <a id="cap-kiss-encode"></a>**Encoder** — FEND/FESC/TFEND/TFESC framing
- <a id="cap-kiss-decode"></a>**Stateful decoder** — handles split-across-pushes, inter-frame-FEND resync
- <a id="cap-kiss-multiport"></a>**Multi-port nibble** — port index in the high nibble of the command byte
- <a id="cap-kiss-ackmode"></a>**ACKMODE** — KISS command 0x0C (request) + 0x0C (ack) round-trip

### 3.3 Callsign codec

- <a id="cap-callsign-base"></a>**7-octet encode + decode**
- <a id="cap-callsign-ssid"></a>**SSID** — 4-bit suffix
- <a id="cap-callsign-ch-bit"></a>**C/H bit handling** — command-vs-response per §6.1.2
- <a id="cap-callsign-e-bit"></a>**E-bit (end-of-address)** — last-address-in-chain flag
- <a id="cap-callsign-digi-chain"></a>**Digipeater chain** — 0..8 digipeaters, H-bit "has been digipeated" migration

### 3.4 SDL state-machine pages (figc4.x)

Each top-level state page becomes a row. "Transcribed" means a `*.sdl.yaml` exists in `spec-sdl/`. "Walked" means the runtime registers the transcribed transition table on its session and reaches the state at runtime.

- <a id="cap-state-disconnected"></a>**Disconnected** (figc4.1)
- <a id="cap-state-awaiting-connection"></a>**AwaitingConnection** (figc4.2, mod-8)
- <a id="cap-state-awaiting-connection-22"></a>**AwaitingConnection22** (figc4.6, mod-128 / SABME)
- <a id="cap-state-awaiting-release"></a>**AwaitingRelease** (figc4.3)
- <a id="cap-state-connected"></a>**Connected** (figc4.4a-c)
- <a id="cap-state-timer-recovery"></a>**TimerRecovery** (figc4.5a-e) — *not transcribed in spec-sdl yet*

### 3.5 figc4.7 subroutines (individual)

The subroutine page (`spec-sdl/data-link/subroutines.sdl.yaml`) declares 13 subroutines. Each gets its own row because they can be wired independently. "Wired" means the runtime walks the SubroutineSpec body (or has an inlined equivalent in the dispatcher). "Stubbed" means the name resolves to a no-op.

- <a id="cap-sub-establish-data-link"></a>**Establish_Data_Link**
- <a id="cap-sub-establish-extended"></a>**Establish_Extended_Data_Link**
- <a id="cap-sub-clear-exception"></a>**Clear_Exception_Conditions**
- <a id="cap-sub-check-i-frame-ack"></a>**Check_I_Frame_Acknowledged**
- <a id="cap-sub-select-t1"></a>**Select_T1_Value** — dynamic T1V
- <a id="cap-sub-transmit-enquiry"></a>**Transmit_Enquiry**
- <a id="cap-sub-invoke-retransmission"></a>**Invoke_Retransmission**
- <a id="cap-sub-nr-error-recovery"></a>**N_r_Error_Recovery**
- <a id="cap-sub-enquiry-response"></a>**Enquiry_Response**
- <a id="cap-sub-check-need-for-response"></a>**Check_Need_For_Response**
- <a id="cap-sub-ui-check"></a>**UI_Check**
- <a id="cap-sub-set-version-2-0"></a>**Set_Version_2_0**
- <a id="cap-sub-set-version-2-2"></a>**Set_Version_2_2**

### 3.6 Predicate bindings

One row, rolled up. "All bound" / "some stubbed" with a link to the runtime's bindings source. The codegen lint (`spec-sdl/lint-targets.yaml`) enforces this at codegen time, but runtimes are free to wire a predicate to a constant (e.g. `version_2_2 → false`). Constant-bound predicates count as bound for lint purposes but should be flagged in the matrix as a known degradation.

- <a id="cap-bindings"></a>**Predicate bindings**

### 3.7 Action dispatcher coverage

Same shape as predicate bindings — every action verb (post alias resolution from `spec-sdl/actions.yaml`) must have a dispatcher case-arm. The lint catches missing cases; the matrix row records whether each case actually *does* the spec-described thing or is a comment-and-return placeholder.

- <a id="cap-dispatcher"></a>**Action dispatcher coverage**

### 3.8 Subroutine walker

The mechanism that consumes a `kind: subroutine` action step. C# walks the SubroutineSpec table via `DefaultSubroutineRegistry.Wire(...)`; TS dispatches via an inline-or-stub strategy with no walker. This is a one-row rollup; the per-subroutine rows in §3.5 capture which subroutines are *effectively* wired.

- <a id="cap-subroutine-walker"></a>**Subroutine walker**

### 3.9 Session lifecycle features

The wrap-around-the-state-machine features that make a session usable.

- <a id="cap-lifecycle-outbound-connect"></a>**Outbound connect** — caller-initiated SABM → wait for UA / DM
- <a id="cap-lifecycle-inbound-listener"></a>**Inbound listener** — accept peer-initiated SABM, surface an `onConnectRequest` (or equivalent)
- <a id="cap-lifecycle-per-peer-reuse"></a>**Per-peer session reuse** — multiple sessions keyed by (local, remote, digi-chain)
- <a id="cap-lifecycle-t1v-dynamic"></a>**T1V dynamic** — `Select_T1_Value` actually adjusts T1 across retries
- <a id="cap-lifecycle-n2-retry"></a>**N2 retry budget** — `RC_eq_N2` guard fires after N2 retries, surfaces an error
- <a id="cap-lifecycle-srej-recovery"></a>**SREJ recovery** — receive SREJ → retransmit named I-frame
- <a id="cap-lifecycle-rej-recovery"></a>**REJ recovery** — receive REJ → retransmit from N(r)
- <a id="cap-lifecycle-frmr-generation"></a>**FRMR generation** — emit FRMR on protocol violation
- <a id="cap-lifecycle-xid-negotiation"></a>**XID negotiation** — modulus + parameter exchange at link establishment

### 3.10 Transport surfaces

The wire side. Each transport is one row. "Provided" = ships in the runtime's main bundle or a documented subpath; "documented seam" = the runtime exposes an interface and points at the place to plug your own implementation.

- <a id="cap-transport-web-serial"></a>**KISS over Web Serial**
- <a id="cap-transport-tcp"></a>**KISS over TCP**
- <a id="cap-transport-usb-serial"></a>**KISS over USB-serial (SerialPort)**
- <a id="cap-transport-ble"></a>**KISS over BLE (Nordic UART)**
- <a id="cap-transport-websocket-bridge"></a>**KISS over WebSocket bridge**
- <a id="cap-transport-agw-client"></a>**AGW client** (talks to a remote AGWPE server)
- <a id="cap-transport-agw-server"></a>**AGW server** (accepts AGWPE clients)
- <a id="cap-transport-axudp"></a>**AXUDP**
- <a id="cap-transport-raw-audio"></a>**Raw audio (in-process AFSK modem)**

### 3.11 Roles

What kind of program can a consumer build on top of this runtime? "Pure client" = outbound only. "Pure listener-node" = inbound accept + surface to upper layer. "Monitor" = passive frame-decoder UI.

- <a id="cap-role-client"></a>**Pure-client role**
- <a id="cap-role-listener-node"></a>**Pure-listener-node role**
- <a id="cap-role-monitor"></a>**Monitor role**

---

## 4. Conformance levels

Each (capability, runtime) cell in the matrix gets one of four scores:

| Level | Symbol | Meaning |
| --- | --- | --- |
| 1. **Absent** | `-` | No implementation, no tests. The capability is acknowledged but not built. |
| 2. **Stub** | `S` | Code exists but is a no-op / returns false / silently drops. The compile passes, the runtime doesn't crash, but the capability does nothing meaningful. |
| 3. **Partial** | `P` | Code does the right thing on the happy path but lacks recovery / edge cases. Concrete examples: the C# session connects fine but doesn't recover from N2 exhaustion; the TS frame codec parses I-frames cleanly but doesn't handle invalid PIDs gracefully. |
| 4. **Conformant** | `C` | Passes the (eventual) shared conformance suite for that capability. Until the conformance suite exists, "conformant" means "passes the runtime's own unit + integration tests and has been observed working against a third-party peer in interop CI". |

When the level can't be determined from a desk-check, the cell is marked `?` and a TODO line beneath the matrix records what would need to be checked. We don't guess.

The levels are coarse on purpose. A 5- or 7-level scale invites argument over rubric edges; a 4-level scale forces the reader to pick the closest of four obviously-different states.

---

## 5. The matrix

The current-state snapshot lives in [`runtime-capability-matrix.md`](runtime-capability-matrix.md). That doc is *just* the table — easy to scan, easy to update. The strategy doc and the matrix doc are deliberately separate so a PR that updates capability state only touches the matrix.

Format:

- One row per capability (per §3).
- One column per runtime — today C# and TS. Future columns: Go, Rust, Python (when those gain runtimes).
- Each cell carries a level symbol per §4.
- Cells linking to a follow-up issue, an in-flight PR, or an explanatory note carry a footnote marker; the footnotes live below the table.
- Rows group by category with a heading row separator so the eye can scan to a section.

---

## 6. Conformance suite — forward-looking

This section sketches a not-yet-built shared test suite. The shape is here so we can reason about the matrix in terms of "is this row testable against the suite". No executors are written yet; the [tests/conformance/ skeleton](../tests/conformance/README.md) ships a worked-example scenario file but no harness.

### 6.1 Goals

- One language-neutral test artefact per scenario.
- Each runtime ships a minimal *executor* that consumes the artefact, runs it through its own `Ax25Session` + dispatcher + bindings, and asserts the captured wire / signal output matches.
- Pass/fail per (runtime × scenario) becomes a heatmap that CI computes and rolls up into the matrix's "Conformant" column.

The point is *not* to replace each runtime's unit tests. Unit tests cover the runtime's internal seams (e.g. the C# tests cover `ActionDispatcher` directly); the conformance suite covers the *externally-observable* contract — same input, same output, regardless of runtime.

### 6.2 Scenario format

A scenario is a YAML file with three top-level keys:

```yaml
name: <snake_case_identifier>
description: |
  <free-form narrative for humans>

setup:
  myCall: <callsign-with-ssid>
  remote: <callsign-with-ssid>
  initialState: <state-name>
  # Optional: t1Ms, t3Ms, n2, k, version_2_2

steps:
  - post_event:
      name: <event-name from spec-sdl/events.yaml>
      # Optional flags for frame-arrival events:
      command: true|false
      pollFinal: true|false
  - expect_tx:
      kind: <SABM|UA|DISC|...>
      command: true|false
      pollFinal: true|false
      # Optional: ns, nr, info-hex
  - expect_state: <state-name>
  - expect_upward: <DL_*_indication-or-confirm name>
  - advance_clock_ms: <int>
  - expect_timer:
      name: T1|T2|T3
      state: running|stopped
```

The four step verbs (`post_event`, `expect_tx`, `expect_state`, `expect_upward`) plus the two timer verbs (`advance_clock_ms`, `expect_timer`) cover the SDL-observable interface. A scenario reads left-to-right; each `expect_*` step asserts; each `post_event` / `advance_clock_ms` drives. An executor runs the steps in order and fails the scenario at the first `expect_*` mismatch.

The strawman in [`tests/conformance/connect-sabm-ua-disc.yaml`](../tests/conformance/connect-sabm-ua-disc.yaml) is the worked example.

### 6.3 Executor shape (per runtime)

Each runtime grows a small (~150 LOC) test that:

1. Walks `tests/conformance/*.yaml`.
2. For each scenario, instantiates an `Ax25Session` configured per `setup`.
3. Loops over `steps`, dispatching each one through the session.
4. Asserts each `expect_*` against captured outputs.
5. Reports a single pass/fail per scenario.

Reporting format: each runtime's CI emits a JSON file `conformance-<runtime>.json` listing `{ scenario: pass | fail | skip }`. A repo-level job aggregates the JSONs into a heatmap that's appended to the matrix as a generated section. (Generation isn't implemented yet; the manual matrix is authoritative until the heatmap exists.)

### 6.4 Why not just port the existing C# tests

The C# `Ax25Session` tests assert against `Ax25Session.CurrentState`, `dispatcher.History`, and the captured frames. Those internal surfaces (`History`, the in-memory dispatcher) don't exist in the TS runtime — its dispatcher is a method, its captured output goes via the transport. Sharing the C# test bodies verbatim would require replicating internal seams across runtimes, which is the opposite of what we want.

A language-neutral scenario format inverts that: each runtime *implements its own captures* (the C# executor reads `dispatcher.History`; the TS executor reads a mock transport's `sent` queue) and asserts against the *same shared expected*.

### 6.5 What the suite explicitly does NOT cover

- Performance / throughput — different runtimes have wildly different perf profiles; an interop bug doesn't usually surface as latency.
- Concurrency / multi-session scheduling — each runtime's scheduler model differs (C# uses a `Timer` per session; TS uses `setTimeout`). The single-session conformance suite stays out of that.
- Transport-specific quirks — KISS-over-Web-Serial vs KISS-over-TCP both feed bytes; the conformance suite asserts against bytes, not the transport.
- Real-peer interop — that's the cross-runtime tests in §7. The conformance suite is the runtime-vs-spec test; the cross-runtime tests are the runtime-vs-peer test.

---

## 7. Cross-runtime interop testing

The highest-signal test we have. Already partially exists:

- **C# vs LinBPQ over net-sim** — `tests/Packet.Interop.Tests/Linbpq/LinbpqViaNetsimConnectedMode.cs`
- **C# vs XRouter over net-sim** — `XrouterViaNetsimConnectedMode.cs`
- **C# vs rax25 over net-sim** — `Rax25ViaNetsimConnectedMode.cs`
- **TS vs LinBPQ over net-sim** — `packet-net/ax25-ts: tests/integration/linbpq-via-netsim.test.ts`

The pattern:

1. The interop compose stack (`docker/compose.interop.yml`) brings up a net-sim node + a third-party peer.
2. Each runtime ships an integration test that talks to its assigned net-sim port and asserts wire-level handshakes against the peer.
3. CI runs the test suite for each runtime against the same up-stack; passing means *that runtime* interoperates with the peer.

What's missing today:

- **TS vs XRouter** and **TS vs rax25** — straightforward to add once the existing TS-vs-LinBPQ test stabilises; one PR per peer to keep the blast radius small.
- **Direct runtime-to-runtime** — currently every test goes via a third-party peer. The highest-signal test would be C# directly talking to TS over net-sim (no third-party stack in the loop). This catches drift between *us and ourselves* before it surfaces against an external peer.

Strategy for new runtimes:

When a new runtime gains a connected-mode session (Go runtime, Python runtime, …) it gets two flavours of interop test in the same PR as the runtime:

1. **Runtime ↔ LinBPQ** — proves wire compatibility against a known-good third-party peer.
2. **Runtime ↔ another packet.net runtime** — proves the new runtime doesn't drift from its siblings. Pick whichever existing runtime is closest in maturity (today: C#).

Both run in `interop.yml`. Adding a runtime without these two tests means the runtime can't claim "Conformant" in the matrix's Session-lifecycle rows.

---

## 8. Update discipline

The matrix is honest only if it gets updated. The rules:

### When a PR changes capability state

The matrix MUST be updated in the same PR. Concrete triggers:

- A new frame type gains encode-or-decode support.
- A subroutine moves from stub to inlined-in-dispatcher or to walker-routed.
- A new transport is added.
- A lifecycle feature (listener, dynamic T1V, …) lands.
- A predicate's binding changes from a hard-coded `false` to a real evaluation.

The PR description's "Plan changes" section calls out the matrix row(s) touched. Reviewers verify the matrix update matches the PR's actual diff. A matrix update is just as load-bearing as a plan amendment — same rule applies (see [§18 of the plan](plan.md#18-how-to-update-this-document)).

### When a row's level is uncertain

Mark `?`, add a TODO line beneath the matrix that names the row and the question. Don't guess. The point of `?` is that the next person to touch that row decides — not that we average-out the ambiguity.

### When a new runtime appears

A new column. The PR introducing the runtime's first session-walker code includes the new column with every capability levelled. The first-runtime-PR is the most painful — every row needs a fresh assessment. After that, individual capability PRs touch one cell at a time.

### Cross-link discipline

Every row's anchor (`#cap-<slug>`) is stable. Other docs (per-runtime READMEs, ADRs, plan amendments) deep-link the matrix when they explain *why* a row is at a given level. The matrix itself stays terse — it points at the source-of-truth document for each row rather than duplicating its prose.

The same shape ts-spec/README's "How to keep this section honest" describes for its narrative status table applies here: the matrix is the multi-runtime view, the per-runtime READMEs are the single-runtime narrative views. Neither replaces the other.

---

## 9. Scope notes

What this document is **not**:

- A roadmap. The [plan's §5](plan.md#5-phased-roadmap) is the roadmap. Capability rows that aren't on a phase deliverable list yet stay at "Absent" in the matrix until a phase picks them up.
- A spec. The [packethacking/ax25spec](https://github.com/packethacking/ax25spec) repository is the spec. Capability rows that exist in the figures but haven't been transcribed are still capability rows; they're just at "Absent" in every column today.
- A test plan for any single runtime. Each runtime's own test suite is the authoritative coverage for *that runtime*. The conformance suite is a different artefact — its job is to assert runtimes-against-each-other, not runtime-against-itself.

What it IS:

- The single place to look for "does runtime X do Y" across the whole capability surface, with a follow-up trail when the answer is "no" or "partially".
- The discipline document that says the matrix update is part of capability PRs, not an afterthought.
- The forward-looking sketch for the shared conformance suite, before that suite exists.

---

## 10. Related documents

- [`runtime-capability-matrix.md`](runtime-capability-matrix.md) — the current-state snapshot
- [`plan.md`](plan.md) — project plan; particularly §6 (SDL discipline), §7 (test pyramid), §17 (amendment log)
- [`../ts-spec/README.md`](../ts-spec/README.md) — TS-runtime narrative; "Not yet transcribed" section is the per-page status companion to the matrix
- [`packet-net/ax25-ts: README.md`](https://github.com/packet-net/ax25-ts#readme) — TS-runtime public-API view; scope tables here are the consumer-facing companion
- [`../spec-sdl/lint-targets.yaml`](../spec-sdl/lint-targets.yaml) — the codegen-time consistency check; catches the cheapest drift automatically
- [`../tests/conformance/README.md`](../tests/conformance/README.md) — conformance-suite skeleton (worked example scenario, no executors)
