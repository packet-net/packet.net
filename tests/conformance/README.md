# Cross-runtime conformance scenarios

> A language-neutral library of test scenarios that any runtime consuming the AX.25 SDL codegen can replay through its own session machinery. The strategy that motivates this directory lives at [`../../docs/runtime-capability-strategy.md`](../../docs/runtime-capability-strategy.md), §6 ("Conformance suite - forward-looking").

This directory is intentionally minimal today: one worked-example scenario file (`connect-sabm-ua-disc.yaml`) and no executors. The scenario file is **the format strawman** - concrete enough that an executor implementation is obvious from reading it, sparse enough that authoring more scenarios is cheap.

## What's here

```
tests/conformance/
  README.md                          (this file)
  connect-sabm-ua-disc.yaml          worked example — outbound connect / disconnect
```

## What's not here yet

- **Executors.** No code today turns a scenario YAML into a passing/failing test in any runtime. The plan is one ~150 LOC executor per runtime - see the strategy doc §6.3.
- **CI rollup.** Once executors exist, each runtime emits `conformance-<runtime>.json` listing per-scenario pass/fail; a top-level CI job aggregates the JSONs into a heatmap appended to the matrix.
- **Coverage.** One scenario isn't conformance coverage - it's a format strawman. Future scenarios will cover the figc4.7 subroutines, REJ recovery, N2 exhaustion, and inbound SABM acceptance, at minimum.

## Scenario format

Each scenario is a single YAML file with three top-level keys: `name`, `setup`, `steps`. An optional `description` block explains intent.

```yaml
name: <snake_case_identifier>
description: |
  Free-form prose for humans.

setup:
  myCall: <callsign>
  remote: <callsign>
  initialState: Disconnected | AwaitingConnection | ...
  # Optional knobs:
  t1Ms: 3000
  t3Ms: 30000
  n2: 10
  k: 1
  version_2_2: false

steps:
  - <step>
  - <step>
  ...
```

### Step verbs

| Verb | Purpose | Required fields |
| --- | --- | --- |
| `post_event` | Push an `Ax25Event` into the session. Event names match `spec-sdl/events.yaml`. | `name`. Optional: `command`, `pollFinal`, `ns`, `nr`, `infoHex` for frame-arrival events. |
| `expect_tx` | Assert the session emitted a frame with these attributes since the last `expect_tx` / `expect_state` / start. Matches the **first** TX in the pending-TX queue and dequeues it. | `kind` (SABM/UA/DISC/...). Optional: `command`, `pollFinal`, `ns`, `nr`, `infoHex`. |
| `expect_state` | Assert the session is in this state right now. | `<state-name>` as scalar. |
| `expect_upward` | Assert the session emitted this `DataLinkSignal` (e.g. `DL_CONNECT_confirm`) since the last `expect_upward`. Matches the first pending signal and dequeues it. | `<signal-name>` as scalar. |
| `advance_clock_ms` | Push the virtual clock forward by N ms. Any timer due in that span fires before the next step runs. | `<int>` as scalar. |
| `expect_timer` | Assert a named timer is in the given state. | `name` (`T1`/`T2`/`T3`), `state` (`running`/`stopped`). |

An executor walks `steps` top-to-bottom. A `post_event` / `advance_clock_ms` *drives* the session; an `expect_*` *asserts*. The first failed assertion aborts the scenario.

### Frame-arrival events

When the event name in `post_event` matches a frame-receipt event (`SABM_received`, `UA_received`, `I_received`, etc. per `spec-sdl/events.yaml`), the executor synthesises a frame with the appropriate control byte from the optional fields:

- `command: true|false` → sets the C-bits per §6.1.2
- `pollFinal: true|false` → sets the P/F bit
- `ns: <int>`, `nr: <int>` → sequence numbers for I-frames / S-frames
- `infoHex: "<hex>"` → information field bytes (for I-frames)

Defaults: `command: true`, `pollFinal: false`, `ns: 0`, `nr: 0`, `infoHex: ""`.

For `post_event` calls whose name is a DL primitive (`DL_CONNECT_request`, etc.) the frame-shape fields are ignored.

## Why YAML

- Easy to author by hand without losing alignment.
- Same format as `spec-sdl/*.sdl.yaml`, so each runtime already has a YAML parser dependency in its build.
- Comment-friendly - non-trivial scenarios benefit from inline `# figc4.4 t12, REJ-recovery happy path` annotations.
- Maps cleanly to JSON / Go / TS / C# without bespoke deserialisation.

## What a scenario is, and isn't

A scenario is **the externally-observable contract of a session over a sequence of inputs.** It says: given these starting parameters and this stream of events, the session must emit *these* frames upward to the upper layer and *these* frames downward to the link layer, transit through *these* states, and arm/disarm *these* timers.

A scenario is **not** a unit test for the inside of the runtime. Inside the runtime, each implementation can do whatever it likes - walk the SDL tables differently, batch frame TX differently, schedule timers differently. The conformance suite only cares about what crosses the seam.

A scenario is also **not** a wire-format test. The frame codec is tested separately by each runtime's unit suite. The conformance scenarios assume the codec works and assert on the session's behaviour.

## Authoring a new scenario

1. Pick a single concrete behaviour (one figure transition, one subroutine, one recovery path).
2. Write the description first - the prose drives the test.
3. Author the `steps` list. Use `expect_state` liberally to keep the assertions local to each transition.
4. Run it through each existing-runtime executor (once executors exist). Mismatches between runtimes are the entire point of this artefact - surface them.
5. Add the scenario to the conformance-suite section of the runtime-capability matrix.

## Worked example

[`connect-sabm-ua-disc.yaml`](connect-sabm-ua-disc.yaml) - outbound connect + disconnect against a cooperating peer. Walks figc4.1 t01 → figc4.2 t05 → figc4.4 t01 → figc4.3 t05. Asserts both wire output (SABM, DISC) and upward signals (DL_CONNECT_confirm, DL_DISCONNECT_confirm) end-to-end.

This is the simplest non-trivial happy path the C# runtime exercises in interop CI against LinBPQ / XRouter / rax25. Running it through both the C# and TS runtimes (once executors exist) will prove the two runtimes are doing the same thing on the same input.
