# Runtime capability matrix

> Cross-runtime current-state snapshot. The strategy doc explaining the taxonomy, the levels, and the update discipline lives at [`runtime-capability-strategy.md`](runtime-capability-strategy.md).

**Last updated:** 2026-05-16 (TS `Ax25Listener` port — lifecycle row + role row updates)

**Legend:** `C` Conformant · `P` Partial · `S` Stub · `-` Absent · `?` Undetermined (see TODO below)

**Runtimes covered:** C# (`src/Packet.Ax25/`) · TS (`packet-net/ax25-ts`).
Future columns (Go, Rust, Python, …) appear here when those runtimes gain session-walker code; the codegen-only `go-spec/` / `rust-spec/` / `python-spec/` packages don't qualify as runtimes for matrix purposes (they're spec data, not behaviour).

---

## Wire-format codec — per-frame-type

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [SABM](runtime-capability-strategy.md#cap-frame-sabm) | C | C | both encode + decode |
| [SABME](runtime-capability-strategy.md#cap-frame-sabme) | C | - | C# factory + decode; TS frame.ts has no SABME factory and `classify()` returns `UNKNOWN` for SABME control bytes [^ts-frame-set] |
| [DISC](runtime-capability-strategy.md#cap-frame-disc) | C | C |  |
| [UA](runtime-capability-strategy.md#cap-frame-ua) | C | C |  |
| [DM](runtime-capability-strategy.md#cap-frame-dm) | C | C |  |
| [FRMR](runtime-capability-strategy.md#cap-frame-frmr) | C | - | C# factory + decode; TS has no FRMR codec [^ts-frame-set] |
| [XID](runtime-capability-strategy.md#cap-frame-xid) | C | - | C# factory + decode; TS has no XID codec [^ts-frame-set] |
| [TEST](runtime-capability-strategy.md#cap-frame-test) | C | - | C# factory + decode; TS has no TEST codec [^ts-frame-set] |
| [RR](runtime-capability-strategy.md#cap-frame-rr) | C | C | both runtimes mod-8 only on the I/S sequence-number side; C# has the mod-128 S-frame path scaffolded but not exercised on the wire [^mod128] |
| [RNR](runtime-capability-strategy.md#cap-frame-rnr) | C | C | same caveat as RR |
| [REJ](runtime-capability-strategy.md#cap-frame-rej) | C | C | same caveat as RR |
| [SREJ](runtime-capability-strategy.md#cap-frame-srej) | P | - | C# factory + decode exist; SREJ is mod-128-only on the wire and the dispatcher's SREJ-recovery path is not wired (`srej_enabled` binding returns the context flag, which is never set true). TS has no SREJ codec [^mod128] |
| [I-frame](runtime-capability-strategy.md#cap-frame-i) | C | C | both runtimes mod-8 only [^mod128] |
| [UI](runtime-capability-strategy.md#cap-frame-ui) | C | C |  |

## KISS framing

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [Encoder](runtime-capability-strategy.md#cap-kiss-encode) | C | C | both runtimes |
| [Stateful decoder](runtime-capability-strategy.md#cap-kiss-decode) | C | C | both handle split-across-pushes + inter-frame-FEND resync; covered by unit tests in `Packet.Kiss.Tests` and `tests/kiss.test.ts` |
| [Multi-port nibble](runtime-capability-strategy.md#cap-kiss-multiport) | C | C |  |
| [ACKMODE](runtime-capability-strategy.md#cap-kiss-ackmode) | C | - | C# `KissAckMode` + `KissAx25Bridge` ship encode/decode + receipt-correlation; TS has no ACKMODE [^ts-ackmode] |

## Callsign codec

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [7-octet encode + decode](runtime-capability-strategy.md#cap-callsign-base) | C | C |  |
| [SSID](runtime-capability-strategy.md#cap-callsign-ssid) | C | C |  |
| [C/H bit handling](runtime-capability-strategy.md#cap-callsign-ch-bit) | C | C |  |
| [E-bit](runtime-capability-strategy.md#cap-callsign-e-bit) | C | C |  |
| [Digipeater chain](runtime-capability-strategy.md#cap-callsign-digi-chain) | C | C | codec only; runtime `via:` outbound paths are partial — C# session handles digi-chains end-to-end (`Ax25Session` carries `Path`), TS `Ax25Stack.connect({ via })` throws "not implemented" |

## SDL state-machine pages (figc4.x)

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [Disconnected](runtime-capability-strategy.md#cap-state-disconnected) | C | C | figc4.1 transcribed + walked; happy-path inbound + outbound exercised |
| [AwaitingConnection](runtime-capability-strategy.md#cap-state-awaiting-connection) | C | C | figc4.2 transcribed + walked |
| [AwaitingConnection22](runtime-capability-strategy.md#cap-state-awaiting-connection-22) | P | P | figc4.6 transcribed + walked structurally, but the SABME handshake path is only reachable if `version_2_2` predicate returns true — that flag is bound to `context.IsExtended` in C# (settable, but no caller flips it for SABME-initiated connects today) and hardwired to `false` in TS [^mod128] |
| [AwaitingRelease](runtime-capability-strategy.md#cap-state-awaiting-release) | C | C | figc4.3 transcribed + walked |
| [Connected](runtime-capability-strategy.md#cap-state-connected) | P | P | figc4.4a-c transcribed + walked. Happy-path I-frame TX/RX with V(s)/V(r)/V(a) bookkeeping conformant in both runtimes; the t38/t39 transitions to TimerRecovery aren't exercised because TimerRecovery isn't transcribed [^timer-recovery] |
| [TimerRecovery](runtime-capability-strategy.md#cap-state-timer-recovery) | S | S | figc4.5 not transcribed. Both runtimes register `TimerRecovery` as a state name with an empty transition table so the state-target lint passes; entering it silently stalls until a peer-initiated DISC. See `StateTargetAllowList` in `tools/Packet.Sdl.CodeGen/Program.cs` [^timer-recovery] |

## figc4.7 subroutines

C# routes every subroutine call through `DefaultSubroutineRegistry.Wire(...)` — the registry walks the generated `SubroutineSpec` body via the dispatcher + guard evaluator. TS inlines four subroutines directly into the action-dispatcher case arms and stubs the other nine to no-op via `DefaultSubroutineRegistry`'s `onUnknown` sink. The four inlined names in TS match the four subroutines the C# happy path has interop evidence for; the nine TS stubs match the nine subroutines whose action verbs reference predicates / actions that the broader runtime hasn't proven against a peer yet.

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [Establish_Data_Link](runtime-capability-strategy.md#cap-sub-establish-data-link) | C | C | C# walks via Wire; TS inlines into dispatcher. Exercised in interop CI (SABM/UA round trip) |
| [Establish_Extended_Data_Link](runtime-capability-strategy.md#cap-sub-establish-extended) | P | C | C# walks via Wire but never reached because `version_2_2` is false; TS inlines [^mod128] |
| [Clear_Exception_Conditions](runtime-capability-strategy.md#cap-sub-clear-exception) | C | C | C# walks via Wire; TS inlines |
| [Check_I_Frame_Acknowledged](runtime-capability-strategy.md#cap-sub-check-i-frame-ack) | C | C | C# walks via Wire; TS inlines. Exercised on happy-path I-frame TX. |
| [Select_T1_Value](runtime-capability-strategy.md#cap-sub-select-t1) | P | S | C# walks the subroutine body via Wire; the inner actions adjust `T1V` per the spec but no peer-driven dynamic-T1V test exists. TS routes to no-op stub |
| [Transmit_Enquiry](runtime-capability-strategy.md#cap-sub-transmit-enquiry) | ? | S | C# walks the body via Wire but the path is only reached from TimerRecovery, which isn't transcribed; no test exercises it. TS stub. TODO: trace one Transmit_Enquiry path manually and confirm whether the C# walker hits a verb that's itself a stub |
| [Invoke_Retransmission](runtime-capability-strategy.md#cap-sub-invoke-retransmission) | ? | S | C# walks body via Wire; needs REJ/SREJ recovery path active to be reached. TS stub. TODO: same as Transmit_Enquiry |
| [N_r_Error_Recovery](runtime-capability-strategy.md#cap-sub-nr-error-recovery) | ? | S | C# walks; not exercised. TS stub. TODO: same shape |
| [Enquiry_Response](runtime-capability-strategy.md#cap-sub-enquiry-response) | ? | S | C# walks; not exercised. TS stub. TODO: same shape |
| [Check_Need_For_Response](runtime-capability-strategy.md#cap-sub-check-need-for-response) | ? | S | C# walks; not exercised. TS stub. TODO: same shape |
| [UI_Check](runtime-capability-strategy.md#cap-sub-ui-check) | ? | S | C# walks; not exercised. TS stub. TODO: same shape |
| [Set_Version_2_0](runtime-capability-strategy.md#cap-sub-set-version-2-0) | P | S | C# walks body via Wire; the inner `set_version_2_0` verb flips `ctx.IsExtended` to false. TS stub-routes-and-no-ops [^mod128] |
| [Set_Version_2_2](runtime-capability-strategy.md#cap-sub-set-version-2-2) | P | S | C# walks; flips `ctx.IsExtended` to true. TS stub [^mod128] |

## Bindings + dispatcher (rollup)

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [Predicate bindings](runtime-capability-strategy.md#cap-bindings) | C | P | both runtimes' bindings files declare 41 predicate names — the codegen lint (`spec-sdl/lint-targets.yaml`) verifies completeness on every PR. TS hardwires `version_2_2`, `mod_128`, and a handful of frame-shape predicates to constants (see [`packet-net/ax25-ts: src/sdl/session-bindings.ts`](https://github.com/packet-net/ax25-ts/blob/main/src/sdl/session-bindings.ts)); C# binds every predicate to a real evaluation in [`src/Packet.Ax25/Session/Ax25SessionBindings.cs`](../src/Packet.Ax25/Session/Ax25SessionBindings.cs) |
| [Action dispatcher coverage](runtime-capability-strategy.md#cap-dispatcher) | C | P | both dispatchers ship 146 case arms — the codegen lint verifies the verb set matches the SDL on every PR. C# dispatches every verb through real session-mutating code paths or the subroutine registry; TS leaves several verbs as comment-and-return placeholders (search `packet-net/ax25-ts: src/sdl/action-dispatcher.ts` for `// no-op`). TODO: enumerate the exact list of stubbed-verb placeholders in the TS dispatcher and decide whether each is a known degradation (matrix row) or a fix-now (separate PR) |
| [Subroutine walker](runtime-capability-strategy.md#cap-subroutine-walker) | C | - | C# `DefaultSubroutineRegistry.Wire(...)` walks generated `SubroutineSpec.Paths`. TS has no walker — `DefaultSubroutineRegistry` only routes pre-registered names; unknown names log via `onUnknown`. Building the walker is the [follow-up](#follow-ups) tracked for TS feature-parity work |

## Session lifecycle

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [Outbound connect](runtime-capability-strategy.md#cap-lifecycle-outbound-connect) | C | C | both runtimes — SABM → UA → Connected, exercised in interop CI against LinBPQ + XRouter + rax25 (C#) and LinBPQ (TS) |
| [Inbound listener](runtime-capability-strategy.md#cap-lifecycle-inbound-listener) | C | P | C# `Ax25Listener` shipped 2026-05-16 at [`src/Packet.Ax25/Session/Ax25Listener.cs`](../src/Packet.Ax25/Session/Ax25Listener.cs) — accepts inbound SABM/SABME, surfaces `SessionAccepted` + `FrameTraced` events, `AcceptIncoming` toggle, LRU per-peer session cache (default 64). Tracked issues #135 / #136 / #137 closed by #138; broad coverage in #140; bug fixes in #145 (closed #141, #143). TS port shipped same day at [`packet-net/ax25-ts: src/listener.ts`](https://github.com/packet-net/ax25-ts/blob/main/src/listener.ts) — 1:1 API mirror plus the three carried-over bug fixes; 30 unit tests + 3 integration tests (netsim multi-peer, LinBPQ initiates). TS at `P` until the LinBPQ-initiates interop test runs green in CI on the live stack |
| [Per-peer session reuse](runtime-capability-strategy.md#cap-lifecycle-per-peer-reuse) | C | P | C# `Ax25Listener` keys sessions by remote callsign and reuses them across disconnect/reconnect, preserving SRT / T1V / sequence-variable history; LRU cap (default 64). Covered by `Ax25ListenerMultiPeerTests` (multi-peer, reuse-across-disconnect, LRU eviction). TS `Ax25Listener` has the same per-peer cache shape; TS `Ax25Stack` (outbound-only facade) still exposes one session per `connect()` call. TODO: multi-session smoke test in TS Stack |
| [T1V dynamic](runtime-capability-strategy.md#cap-lifecycle-t1v-dynamic) | P | S | C# walks `Select_T1_Value` via subroutine Wire; TS stub. Neither runtime has a peer-driven test |
| [N2 retry budget](runtime-capability-strategy.md#cap-lifecycle-n2-retry) | C | C | both runtimes — `RC_eq_N2` guard wires through. TS test `session.test.ts` covers; C# covered by `Ax25Session.Tests` |
| [SREJ recovery](runtime-capability-strategy.md#cap-lifecycle-srej-recovery) | S | - | C# has SREJ codec + dispatcher case; the `srej_enabled` predicate is gated on a context flag that's never set true today. TS has no SREJ codec [^mod128] |
| [REJ recovery](runtime-capability-strategy.md#cap-lifecycle-rej-recovery) | P | S | C# routes through `Invoke_Retransmission` subroutine via the walker; not peer-exercised. TS has REJ codec but stubs the recovery subroutine |
| [FRMR generation](runtime-capability-strategy.md#cap-lifecycle-frmr-generation) | P | - | C# FRMR codec + factory exist; the SDL transitions that generate FRMR on protocol violation are partially wired (see action-dispatcher cases for the FRMR verbs). TS has no FRMR codec |
| [XID negotiation](runtime-capability-strategy.md#cap-lifecycle-xid-negotiation) | S | - | C# has XID codec; no negotiation flow wired (the figure for XID negotiation, figC5, isn't transcribed). TS has no XID codec |

## Transport surfaces

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [KISS over Web Serial](runtime-capability-strategy.md#cap-transport-web-serial) | - | C | browser-only; TS `WebSerialKissTransport`. C# has no browser story by design |
| [KISS over TCP](runtime-capability-strategy.md#cap-transport-tcp) | C | C | C# `KissTcpClient`; TS `TcpKissTransport` (subpath `@packet-net/ax25/tcp-transport`) |
| [KISS over USB-serial (SerialPort)](runtime-capability-strategy.md#cap-transport-usb-serial) | C | - | C# `Packet.Kiss.NinoTnc.NinoTncSerialPort` (uses `System.IO.Ports.SerialPort`); soak-tested against a NinoTNC pair on the hardware-loop runner. TS has no Node-side serial-port transport |
| [KISS over BLE (Nordic UART)](runtime-capability-strategy.md#cap-transport-ble) | - | - | not implemented in either runtime |
| [KISS over WebSocket bridge](runtime-capability-strategy.md#cap-transport-websocket-bridge) | - | - | not implemented in either runtime |
| [AGW client](runtime-capability-strategy.md#cap-transport-agw-client) | C | - | C# `Packet.Agw.AgwClient` with interop CI against LinBPQ's AGW listener. TS has no AGW client; the [follow-up](#follow-ups) tracks a port |
| [AGW server](runtime-capability-strategy.md#cap-transport-agw-server) | - | - | not implemented in either runtime |
| [AXUDP](runtime-capability-strategy.md#cap-transport-axudp) | C | - | C# `Packet.Axudp.AxudpSocket`. No TS implementation |
| [Raw audio (in-process AFSK modem)](runtime-capability-strategy.md#cap-transport-raw-audio) | - | - | not implemented in either runtime |

## Roles

| Capability | C# | TS | Notes |
| --- | :-: | :-: | --- |
| [Pure-client role](runtime-capability-strategy.md#cap-role-client) | C | C | both runtimes are usable as outbound clients today |
| [Pure-listener-node role](runtime-capability-strategy.md#cap-role-listener-node) | P | P | Both runtimes now have `Ax25Listener` (C# from #138/#140/#145; TS from #148). `Packet.Term` (the TUI from #134) is the first C# consumer beyond the test rig. `src/Packet.Node/` is still an empty `Program.cs` template — when filled out it'll be the canonical multi-peer node host. `P` until a node-shape consumer (BBS, gateway, daemon) is built on top — listener alone is necessary-but-not-sufficient |
| [Monitor role](runtime-capability-strategy.md#cap-role-monitor) | P | - | C# `Packet.Mcp` + `Packet.Term` (the TUI from #134) can render decoded frames passively. TS has no monitor-only surface; the SDL driver always opens a session per frame stream |

---

## Footnotes

[^ts-frame-set]: TS [`frame.ts`](https://github.com/packet-net/ax25-ts/blob/main/src/frame.ts) `FrameKind` covers SABM / DISC / UA / DM / UI / RR / RNR / REJ / I / UNKNOWN — no SABME, FRMR, XID, TEST, SREJ. Adding any one of those is a small PR (one factory + one classify case + one test) but each has to be matched to a real use-case before landing.

[^mod128]: Mod-128 is gated on the `version_2_2` predicate, which is hardwired to `false` in TS ([`packet-net/ax25-ts: src/sdl/session-bindings.ts`](https://github.com/packet-net/ax25-ts/blob/main/src/sdl/session-bindings.ts)) and bound to `context.IsExtended` in C# (no caller flips it true for outbound connects today, so behaviourally also false). The mod-128 transitions in figc4.4 and figc4.6 are walked by the table-driven engine but never matched. SABME / SREJ / mod-128 sequence numbers all stay dormant until this flag flips and a peer is found that speaks v2.2.

[^timer-recovery]: figc4.5 (TimerRecovery) is not transcribed in `spec-sdl/data-link/`. Both runtimes register `TimerRecovery` as a state name with an empty transition table so the state-target lint passes. The C# runtime's `Ax25Session` reaches it when `Connected` t38 (T1 expiry) or t39 (T3 expiry) fire, then silently stalls until a peer DISC; the TS runtime aliases TimerRecovery to Connected via the same allow-list mechanism. Long-running sessions where the peer goes quiet for > T3 are the practical hit-radius.

[^ts-ackmode]: ACKMODE is a KISS-level capability — TS could in principle ship it without any AX.25 changes. The TS `KissDecoder` doesn't handle command `0x0C`, and there's no `AckModeReceipt`-equivalent. The capability would only be useful in TS once a Node-side transport that pairs with an ACKMODE-capable TNC (e.g. NinoTNC over USB) exists, so it's gated on the [USB-serial transport follow-up](#follow-ups).

---

## TODOs

- **figc4.7 subroutine wiring depth (C#)** — the matrix marks Transmit_Enquiry, Invoke_Retransmission, N_r_Error_Recovery, Enquiry_Response, Check_Need_For_Response, UI_Check as `?` because the registry's `Wire(...)` call routes each through the walker but the walker's path actions reference verbs whose dispatcher implementation hasn't been audited end-to-end. Resolving these to `P` or `C` requires tracing each subroutine's body against the dispatcher's case arms.
- **Action-dispatcher stub enumeration (TS)** — the predicate-binding / action-dispatcher lints prove the *names* are present but not whether the body of each TS dispatcher case actually does the spec-described thing. Several cases are `// no-op` placeholders. Enumerating which gives the Dispatcher coverage row a defensible level.
- **Per-peer session-reuse smoke test** — neither runtime exercises multi-session-per-stack today. Adding a smoke test in each runtime promotes the row from `P` to `C`.

---

## Follow-ups

These are tracked-but-not-yet-filed issues. When Tom files them, link the issue numbers back here.

- ~~**C# inbound listener**~~ — landed 2026-05-16 (PRs #138 / #140 / #145). `Ax25Listener` API + per-peer session cache + handler-exception isolation + via-chain reversal + cache-miss DM fallthrough. Marked `C` on the lifecycle row.
- ~~**TS inbound listener**~~ — landed 2026-05-16 (PR #148). Mirrors the C# API; same three bug fixes carried over. Marked `P` until the LinBPQ-initiates interop test runs green in CI on the live stack.
- **TS figc4.7 subroutine walker** — port `DefaultSubroutineRegistry.Wire(...)` from C# so the nine currently-stubbed subroutines route through generated `SubroutineSpec.Paths` instead of `onUnknown`. Promotes nine TS rows in [§figc4.7 subroutines](#figc47-subroutines) from `S`.
- **TS XID negotiation, FRMR generation, SABME / SREJ / TEST codec** — small per-frame-type PRs. None blocked; each gated on a concrete use-case. Mod-128-dependent ones (SABME, SREJ) also need the `version_2_2` predicate to be flippable from the public API.
- **TS AGW client port** — port `Packet.Agw.AgwClient` to TypeScript for Node-side use against existing AGW servers.
- **figc4.5 (TimerRecovery) transcription** — the highest-value SDL transcription gap. Promotes [`cap-state-timer-recovery`](runtime-capability-strategy.md#cap-state-timer-recovery) from `S` in both runtimes once the YAML lands and the runtimes drop their `StateTargetAllowList` entries.

---

## How to update this document

In the same PR as a capability change. See the strategy doc's [§8 Update discipline](runtime-capability-strategy.md#8-update-discipline) for the full rules. TL;DR:

1. Edit the row's cell.
2. Update **Last updated** at the top.
3. If the change introduces a new TODO, list it in `## TODOs`.
4. If the change is a multi-PR project, list it in `## Follow-ups` and link from the row.
5. Mention "matrix update" in the PR description's Plan-changes section.
