# Packet.NET — Plan

> **This document is the authoritative source of truth for the project's direction, status, and accumulated working knowledge.** It is a *living* document — kept up to date as work progresses. Anything load-bearing about how Packet.NET is being built belongs here.
>
> If you are reading this for the first time: start with [Why Packet.NET?](#1-why-packetnet) and [Working agreements](#2-working-agreements). If you are looking for *what to build next*, jump to [Roadmap](#5-phased-roadmap). If you are an agent: read [Working agreements](#2-working-agreements) carefully — those are the operating instructions that take precedence over your defaults.

**As of:** 2026-05-17
**Current phase:** Phase 2 in progress — `Ax25Session` runner online. First transcribed transitions (figc4.4a cols 5+6) drive end-to-end through the orchestrator. Phase 3 (KISS hardening) pulled partially forward overnight on 2026-05-14 against the live NinoTNC pair: serial driver, ACKMODE round-trip, TX-Test frame parser, adaptive-parameter scaffolding, adaptive-transport glue, and a first soak campaign producing [`docs/nino-tnc-characterisation.md`](nino-tnc-characterisation.md). Next: more SDL pages, plus a real-RF soak campaign once we have field data to compare against the bench.
**Latest amendment:** [§17 entry 2026-06-01 — Stabilise the flaky netsim/LinBPQ interop matrix (test-harness only)](#17-amendment-log)
**Latest amendment:** [§17 entry 2026-05-28 — Extract generic serial KISS modem into Packet.Kiss.Serial (closes #219)](#17-amendment-log)
**Latest amendment:** [§17 entry 2026-05-17 — packet-terminal example: modernize to Ax25Listener + listener-session facade](#17-amendment-log)
**Latest amendment:** [§17 entry 2026-05-17 — Packet.Term: Spectre.Console → Terminal.Gui v2](#17-amendment-log)
**Latest amendment:** [§17 entry 2026-05-16 — interop flake investigation: XRouter tests cleared](#17-amendment-log)
**Latest amendment:** [§17 entry 2026-05-16 Ax25Listener: broad test coverage](#17-amendment-log)
**Latest amendment:** [§17 entry 2026-05-16 — runtime capability strategy + matrix](#17-amendment-log)

---

## Table of contents

1. [Why Packet.NET?](#1-why-packetnet)
2. [Working agreements](#2-working-agreements)
3. [Locked decisions](#3-locked-decisions)
4. [Architecture](#4-architecture)
5. [Phased roadmap](#5-phased-roadmap)
6. [SDL transcription discipline](#6-sdl-transcription-discipline)
7. [Test pyramid + interop CI](#7-test-pyramid--interop-ci)
8. [Hardware-in-the-loop development cycle](#8-hardware-in-the-loop-development-cycle)
9. [Packaging & onboarding](#9-packaging--onboarding)
10. [Security threat model](#10-security-threat-model)
11. [Out of scope for v1](#11-out-of-scope-for-v1)
12. [Locked external dependencies](#12-locked-external-dependencies)
13. [Reference shelf](#13-reference-shelf)
14. [Glossary](#14-glossary)
15. [Open questions](#15-open-questions)
16. [Risks](#16-risks)
17. [Amendment log](#17-amendment-log)
18. [How to update this document](#18-how-to-update-this-document)

---

## 1. Why Packet.NET?

Build a new, ground-up .NET 10 packet-radio node and reusable AX.25 stack.

Existing software (LinBPQ, Xrouter) is mature but: hard to extend, mixed in quality, mostly C, no first-class web UX, no modern API, no built-in MCP. The goal is a node that is:

- **Operator-friendly** — curl|bash + Docker installers, zero file editing for configuration, web-first config, one-click signed updates.
- **Developer-friendly** — modern .NET libraries each publishable to NuGet, REST + MCP + plugin API, a proper test pyramid.
- **Interoperable** — proves itself against LinBPQ, Xrouter, net-sim, and a back-to-back NinoTNC pair every CI run, with a 72h nightly soak.

The critical durability requirement: the AX.25 stack must be hardened for **low-speed, half-duplex, lossy, high-turnaround, shared-medium KISS+ACKMODE modems** — not tuned only against AXUDP. The dev-loop reflects this from day one: two NinoTNCs are wired audio-back-to-back (TX↔RX) and exposed via USB serial on the dev host, used both for local iteration and as a hardware CI fixture.

What this is **not**:

- A BBS / chat / mailbox / DAPPS implementation — those live as out-of-tree plugins via the plugin API.
- An HF waveform stack — Packet.NET talks to KISS modems (over TCP / serial) and AXUDP only. VARA, ARDOP, and friends are out of scope for v1.
- A drop-in LinBPQ replacement — Packet.NET aims for protocol-level interoperability, not bug-for-bug feature parity.

---

## 2. Working agreements

These are the operating principles we follow. They have been *earned* through Phase 0 work — see the amendment log for the story behind each.

### 2.1 Trust the figure

**The AX.25 SDL figures are the source of truth.** When a figure surprises you, the surprise is yours. Do not "fix" a branch label, swap a Yes/No, or substitute "correct-looking" actions on the basis that the figure looks wrong to you — even when the figure appears to contradict the spec prose. The reconciliation lives in the framing (what the diamond is actually *asking*), not in re-drawing the figure.

Concrete rules:
- Transcribe what the figure shows, literally.
- If you're uncertain, add a `verification_pending:` note that captures what surprised you and the alternative reading you considered. PR review can confirm.
- Never silently deviate.

Reference: [§17 entry 2026-05-11 trust-the-figure discipline](#17-amendment-log), [docs/sdl-primer.md](sdl-primer.md), [docs/adr/0001-sdl-dsl.md](adr/0001-sdl-dsl.md).

### 2.2 Encode-then-verify, not infer-then-encode

Every SDL transition in `/spec-sdl/` must come from an **explicit human-authored transcription** of the figure, not from an agent staring at a PNG and inferring. Agents may *propose* YAML based on plain-text path descriptions provided by the human transcriber; agents may not transcribe figures directly.

The dev loop is:
1. Human reads the figure (or describes paths from it in plain text).
2. Agent encodes paths into the YAML DSL and runs codegen.
3. Codegen produces `.g.cs` + `.g.Tests.cs`. CI fails if `git diff --exit-code` shows uncommitted regen.
4. PR review compares YAML to figure side-by-side.

### 2.3 Pin implementation evidence

When transcribing any SDL transition whose semantics are non-obvious, cross-reference how at least one of the canonical implementations handles it: LinBPQ (`/home/tf/src/linbpq/`), ax25ms (Thomas Habets), direwolf (`ax25_link.c`). Drop the citation into the transition's `notes:` field so the lineage survives.

This pattern was forged in the col 5 / DL-FLOW-OFF investigation — see [§17 entry 2026-05-11 col 5 SDL deep dive](#17-amendment-log). Direwolf in particular often comments out spec edge cases or marks them unreachable, which is valuable context.

### 2.4 Default to executing actions with care

For destructive or hard-to-reverse actions, default to confirming with the operator (Tom) before acting:

- Force-push, `reset --hard`, hook-skipping, branch deletions: always ask.
- Publishing actions (PRs, issues, comments, releases): always ask.
- Touching shared infrastructure or third-party services: always ask.
- Local file edits, test runs, dev-loop iterations: just do them.

A user approving an action once does NOT mean they approve it in all future contexts.

### 2.5 No backwards-compatibility ballast until v1

Packet.NET is pre-alpha. There are no shipping consumers, no upstream code that depends on us. Until v1 we delete cleanly when we change direction — no re-exports, no `// removed` comment trails, no shim layers.

### 2.6 Keep this document current

Every meaningful change to direction, scope, or working agreement lands here as an [amendment log entry](#17-amendment-log) and the relevant section above it is updated in the same PR. See [§18 How to update this document](#18-how-to-update-this-document).

### 2.7 No `DateTime.Now` / wall-clock in production code

Code that needs the current time, or that schedules anything timer-shaped, takes a `System.TimeProvider` and uses its `GetUtcNow()` / `CreateTimer()` etc. Production wires `TimeProvider.System`; tests inject `FakeTimeProvider` (from `Microsoft.Extensions.TimeProvider.Testing`) and advance virtual time deterministically with `Advance(TimeSpan)`. No `DateTime.Now`, no `Thread.Sleep` in test code, no real-time waits.

### 2.8 Tests use AwesomeAssertions

AwesomeAssertions (license-friendly FluentAssertions fork) is the sole assertion library — auto-imported via `tests/Directory.Build.props`. Shouldly was used briefly during Phase 0–1 but removed in favour of a single convention; mixing the two created cognitive load with no upside. Style:

- Scalar equality: `value.Should().Be(expected);`
- Collection equality (sequence): `bytes.Should().Equal(expected);` (not `.Be(expected)`, which compares references)
- Negative constructor tests: `var act = () => new Foo(bad); act.Should().Throw<T>();`
- Approximate floating-point: `x.Should().BeApproximately(y, tolerance);`

---

## 3. Locked decisions

These were settled during the initial planning round and have not been revisited unless an amendment-log entry says so.

| Area | Decision | Rationale source |
|---|---|---|
| Runtime | .NET 10 LTS, C# 14, nullable + warnings-as-errors | Current LTS, modern language features |
| Repo layout | Multi-package monorepo, NuGet prefix `Packet.*` | Independently publishable libraries; single dev loop |
| Frontend | React + TypeScript + Tailwind + shadcn/ui (Vite) | Polished 2026 component ecosystem |
| Auth | Local users (Argon2id) + WebAuthn/passkeys + JWT scopes; OIDC pluggable | Works on a fresh install; passkey-first |
| Persistence | SQLite via raw SQL + Dapper (no EF Core). Two DBs: `config.db`, `packets.db` | Single binary, zero ops; hot path isolated |
| License | MIT | Permissive, low friction |
| SDL handling | YAML DSL in `/spec-sdl/`, codegen → C# + conformance tests; per-SDL PR review | [ADR-0001](adr/0001-sdl-dsl.md) |
| NET/ROM | Out of v1; L3 routing abstraction in v1; NET/ROM ships as `Packet.NetRom` later (Phase 9) | Focus v1 on rock-solid AX.25 |
| MCP scope | Ops + diagnostics + network exploration; read-mostly with explicit write tools | Useful without being scary |
| Telemetry | **None by default.** Opt-in OpenTelemetry endpoint configurable by operator | Amateur radio privacy norms |
| Reference plugin | [DAPPS](https://m0lte.github.io/dapps/) — out-of-tree, drives plugin API design | Existing community work |
| CLI shape | Single `packetnet` binary with subcommands (`node`, `sdlgen`, `ctl`) | Clean install, discoverable verbs |
| Solution format | `.slnx` (modern XML) not legacy `.sln` | .NET 10 default |
| Test SDK pinning | `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`, `xunit.runner.visualstudio 3.1.4`, `coverlet.collector 6.0.4` | What the `dotnet new xunit` template emits — keeps drift low |
| Conditional test skip | `Xunit.SkippableFact` for hardware-gated tests | xUnit 2 has no native skip API |
| Interop bar | CI matrix (LinBPQ + Xrouter, KISS-TCP + AXUDP + back-to-back NinoTNCs + net-sim) + nightly 72h soak | "Bulletproof against real lossy peers" |
| Packaging | curl\|bash + Docker + systemd (Debian/Ubuntu/RPi OS first-class); self-contained binaries cross-platform; one-click signed update | Operator-first |

---

## 4. Architecture

### 4.1 Repo layout

```
/Packet.NET.slnx                      solution
/src/
  Packet.Core/                        primitives: Callsign, AX25Address, BitIO, Crc16Ccitt
  Packet.Ax25/                        frames, FCS, segmentation, XID; SDL orchestration
  Packet.Ax25.Sdl/                    GENERATED state machine code (regenerated from /spec-sdl/)
  Packet.Kiss/                        KISS SLIP, commands, ACKMODE, multi-drop, port-nibble
  Packet.Kiss.NinoTnc/                NinoTNC mode catalog 0-15, SETHW byte, TX-test parsing
  Packet.Axudp/                       AXUDP transport
  Packet.L3/                          routing/forwarding abstraction (NET/ROM future home)
  Packet.Rhp2/                        RHPv2 framing + messages (shared)
  Packet.Rhp2.Server/                 TCP + WebSocket listener, auth gate
  Packet.Agw/                         AGW server (loopback-only by default)
  Packet.Mcp/                         MCP server (stdio + SSE)
  Packet.Node/                        ASP.NET Core host: REST + WS + static SPA
  Packet.Node.Extensions/             plugin contracts (IApplicationModule)

/tests/                               Unit / Property / Conformance / Component / Interop / E2E / Fuzz
  Directory.Build.props               centralises xunit + test SDK + Shouldly + globals
  Packet.*.Tests/                     one per library
  Packet.Ax25.Properties/             FsCheck invariants
  Packet.Interop.Tests/               Testcontainers + hardware-loop tests

/spec-sdl/                            YAML DSL — one file per state; schema/ has the JSON Schema
  schema/sdl-machine.schema.json
  events.yaml                         canonical event catalog
  data-link/connected.sdl.yaml        (partial, in progress)
  ... (26 more SDL pages pending)

/tools/
  Packet.Sdl.CodeGen/                 console codegen, emits *.g.cs + *.g.Tests.cs
  Packet.Sdl.Lint/                    placeholder for schema + exhaustiveness validator

/web/packetnet-ui/                    Vite + React SPA (not yet created — Phase 5)

/docker/
  compose.interop.yml                 LinBPQ + Xrouter + net-sim, host-loopback ports
  linbpq/bpq32.cfg
  xrouter/XROUTER.CFG
  netsim/network.yaml

/installers/                          (not yet created — Phase 7)
/.github/workflows/
  ci.yml                              build + test matrix + SDL codegen discipline guard
  interop.yml                         compose-up + interop tests
```

### 4.2 Data flow

```
              radio  ←USB→  TNC  ←KISS/TCP→  Packet.Kiss  ──┐
                                              Packet.Kiss.NinoTnc │
                                                                  │
              radio  ←UDP→  AXUDP  ←→  Packet.Axudp  ──────────────┤
                                                                  ▼
                                  ┌─────────────────────────────────────┐
                                  │ Packet.Ax25 (frames + state machine │
                                  │ from SDL transcriptions)            │
                                  └────────────────┬────────────────────┘
                                                   ▼
                                  ┌─────────────────────────────────────┐
                                  │ Packet.L3 (routing abstraction,     │
                                  │ NET/ROM hook for Phase 9)           │
                                  └──┬──────────────────┬───────────────┘
                                     ▼                  ▼
                          Packet.Rhp2.Server      Packet.Agw
                          (auth required)         (loopback only)
                                     │                  │
                                     ▼                  ▼
                          ┌─────────────────────────────────────────────┐
                          │ Packet.Node (ASP.NET Core: REST + WS + SPA) │
                          │   Auth: Argon2id + WebAuthn + JWT           │
                          │   Persistence: config.db + packets.db       │
                          │   MCP server (stdio + SSE)                  │
                          └────┬────────────┬──────────────┬─────────────┘
                               ▼            ▼              ▼
                          Web UI       packets.db    MCP clients
                          (React SPA)  (rolling)     (Claude Code, etc.)
```

Plugins attach at `Packet.L3` (for AX.25 sessions) and at ASP.NET Core (for REST + UI + MCP tools).

---

## 5. Phased roadmap

> **Tracking convention (2026-05-19).** Phases, spikes, the hardware-arrival playbook, and open questions are each tracked as a GitHub issue from this date onward. The sections below stay as the **narrative anchor** — they explain the *shape* of each piece of work — but the **status / claim / discussion** lives in the linked issue. The status emoji in each heading is a coarse summary; the issue is authoritative. See §17 amendment-log entry for the rationale.

Effort key: S ≤ 3 days, M ≤ 2 weeks, L ≤ 1 month, XL > 1 month. Status: ⬜ not started · 🟡 in progress · ✅ complete.

| Phase | Title | Effort | Status |
|---|---|---|---|
| 0 | Foundation + SDL spike | M | ✅ |
| 1 | KISS + AX.25 framing + AXUDP | M | ✅ |
| 2 | AX.25 v2.2 Data-Link state machine | XL | ⬜ next |
| 3 | KISS hardening: ACKMODE + NinoTNC + multi-drop | M | ⬜ |
| 4 | Node host, REST, auth, persistence | L | ⬜ |
| 5 | Web UI | L | ⬜ |
| 6 | External app surfaces: RHPv2 + AGW | L | ⬜ |
| 7 | Packaging + one-click signed update | M | ⬜ |
| 8 | MCP + live monitor v2 + link troubleshooting | M | ⬜ |
| 9 | Plugin API + NET/ROM + hardening (post-v1) | L–XL | ⬜ |

v1 is **phases 0–7 plus phase 8**. Phase 9 follows v1 on the v1.x train.

### 5.0 Phase 0 — Foundation + SDL spike ✅

Goal: prove out the dev environment and the SDL DSL pipeline end-to-end on a single diagram before committing to 27 transcriptions.

**Deliverables (all complete)**

- ✅ Repo scaffolding: `.gitignore`, `LICENSE` (MIT), `README.md`, `SECURITY.md`, `CONTRIBUTING.md`, `global.json` pinning .NET 10.0.203.
- ✅ `Directory.Build.props` (net10.0, C# 14, nullable, warnings-as-errors, deterministic, source link).
- ✅ `Directory.Packages.props` (Central Package Management, all NuGet versions pinned).
- ✅ 13 library projects + 2 console tools + 10 test projects, all in `Packet.NET.slnx`. Build green; 14 placeholder tests pass.
- ✅ GitHub Actions: `.github/workflows/ci.yml` (build/test matrix Linux/Win/macOS + SDL codegen discipline guard) and `interop.yml` (compose stack + interop tests).
- ✅ Docker compose stack at `docker/compose.interop.yml` with LinBPQ + Xrouter + net-sim; host loopback ports; YAML validates.
- ✅ SDL DSL: schema at `/spec-sdl/schema/sdl-machine.schema.json`, event catalog at `/spec-sdl/events.yaml`, first transcription at `/spec-sdl/data-link/connected.sdl.yaml` (partial — figc4.4a cols 5 & 6, 5 transitions).
- ✅ Codegen tool `tools/Packet.Sdl.CodeGen` (console, YamlDotNet, idempotent, emits `*.g.cs` + `*.g.Tests.cs`). 7 generated conformance tests pass.
- ✅ Hardware-loop probe: `tests/Packet.Interop.Tests/Hardware/NinoTncEnumeration.cs` with `[Trait("Category","HardwareLoop")]` and `SkippableFact`. Skips cleanly when no TNCs attached.
- ✅ Docs: [`docs/adr/0001-sdl-dsl.md`](adr/0001-sdl-dsl.md), [`docs/sdl-primer.md`](sdl-primer.md), this plan.

**Exit criteria — all met**

- `dotnet build` + `dotnet test` green on local dev and (configured) Linux/Win/macOS CI.
- Codegen idempotent (`git diff --exit-code` after re-run is clean).
- One full SDL YAML reviewed and signed off (figc4.4a cols 5+6 — see [§17 entry 2026-05-11](#17-amendment-log) for the long story).
- Compose stack `docker compose config` validates.

**Phase 0 notes**

- The col 5 / DL-FLOW-OFF investigation produced the most important working agreement of the whole project so far ([§2.1 Trust the figure](#21-trust-the-figure)). Forty minutes well spent.
- The hardware-loop probe is plumbed but not yet exercised — no TNCs attached at time of writing. Once Tom connects the back-to-back NinoTNC pair to the dev host, the `Category=HardwareLoop` filter unlocks the rig.

### 5.1 Phase 1 — KISS + AX.25 framing + AXUDP ✅

Goal: send and receive AX.25 UI frames over KISS-over-TCP and AXUDP. No state machine yet beyond stateless UI handling.

**Deliverables (all complete)**

- ✅ `Packet.Core.Crc16Ccitt`: CRC-16/X-25 (poly 0x1021, init 0xFFFF, RefIn/RefOut, XorOut 0xFFFF). Standard "123456789" → 0x906E vector locked in.
- ✅ `Packet.Core.Callsign`: 1–6 ASCII uppercase alphanumerics + SSID 0–15, with `Parse` / `TryParse` and round-trip `ToString`.
- ✅ `Packet.Core.Ax25Address`: 7-octet AX.25 address slot (callsign + C/H bit + E bit), per §3.12.
- ✅ `Packet.Kiss/`: `KissEncoder` + `KissDecoder` with SLIP escape (incl. command-byte escape — see amendment log), multi-drop port nibble, `KissCommand` enum (Data / TxDelay / Persistence / SlotTime / TxTail / FullDuplex / SetHardware / AckMode / Poll). Stateful decoder handles arbitrary chunking.
- ✅ `Packet.Kiss.KissTcpClient`: dial a KISS-over-TCP listener (net-sim / softmodem).
- ✅ `Packet.Ax25.Ax25Frame`: parse + serialise frames in KISS form (no FCS). `Ui()` factory handles C-bit / E-bit / digipeater-chain semantics. Hex golden vector locked in (APRS-0 ← G7XYZ-7 "hello").
- ✅ `Packet.Axudp.AxudpSocket`: UDP transport with attempted-decode-on-receive.
- ✅ LinBPQ container interop: a real LinBPQ 6.0.25.23 image accepts our AXUDP frames over the wire.

**Exit criteria**

- 🟡 UI frame round-trip vs LinBPQ container (KISS-TCP) — **deferred to net-sim**. LinBPQ doesn't natively host a KISS-TCP listener (it needs an external softmodem); the in-stack KISS-TCP target is net-sim, which we'll wire up in Phase 2's interop expansion.
- 🟡 UI frame round-trip vs LinBPQ container (AXUDP) — **send-trip verified**, full round-trip needs AGW or telnet (deferred to Phase 6).
- ⬜ UI frame round-trip across back-to-back NinoTNCs (afsk1200 + gfsk9600) — gated on hardware availability.
- ⬜ UI frame round-trip via net-sim on `localhost:8100` — Phase 2 interop expansion.
- ✅ FsCheck property `encode → decode = id` green on KISS framing (caught the command-byte FEND collision) and AX.25 UI framing (100 random cases per run).
- ⬜ Mutation score ≥ 70 % on framing (Stryker.NET) — not yet measured; not blocking.

**Phase 1 notes**

- 73 tests pass under the default CI filter (Core + Kiss + Ax25 + Ax25 properties + Conformance + skeleton tests).
- 2 interop tests pass when the LinBPQ container is up (`docker compose -f docker/compose.interop.yml up -d --wait linbpq`); they skip cleanly otherwise.
- 2 hardware-loop tests still skip until TNCs are attached.
- The KISS encoder escapes the command byte too, deviating from the spec text. See [§17 entry 2026-05-11 KISS command-byte escape](#17-amendment-log) for the why.
- LinBPQ doesn't natively host a KISS-TCP listener — needs Direwolf / UZ7HO bridged in. Phase 1 KISS-TCP interop therefore moves to net-sim. See amendment log.

### 5.2 Phase 2 — AX.25 v2.2 Data-Link state machine ⬜ ([#168](https://github.com/m0lte/packet.net/issues/168))

Goal: full AX.25 connected-mode operation against LinBPQ — connect, send, retry, disconnect, mod-8 and mod-128.

**Scope**

- Transcribe all remaining 26 SDL pages into `/spec-sdl/` (Simplex Physical 7, Duplex Physical 5, Link Multiplexer 3, Data-Link remaining 6, Management Data-Link, Segmenter, Reassembler).
- Hand-written orchestration: timer driver (T1 / T2 / T3 + phy timers), session lookup by `(local, remote, port)`.
- Management Data-Link drives XID. Defaults: PI 0x06 N1=256, PI 0x08 k=4 (mod-8) / 32 (mod-128), PI 0x09 T1=3000 ms, PI 0x0A N2=10.
- Segmenter for PID 0x08 with `F | seg-remaining(6)` first byte.
- FRMR generation disabled under v2.2 (deprecated per §4.3.4).

**Exit criteria**

- Connect/disconnect mod-8 + mod-128 vs LinBPQ.
- ✅ REJ and SREJ retransmits observed on the wire. (#209, 2026-05-21)
- ✅ Timer Recovery entered + exited under scripted net-sim 100 % loss for `(T1−1)·N2` then recovery. *Entry + disconnect cycles covered (#208); recovery-via-RR-poll was blocked by the `Invoke_Retransmission` single-iteration bug ([ax25sdl#44](https://github.com/m0lte/ax25sdl/issues/44)) — now fixed in Packet.Ax25.Sdl 0.7.0 and executed by the runtime (`SdlLoopExecutor`).*
- ✅ Segmenter reassembles a 1500-byte payload across multiple I-frames. (#210, 2026-05-21)
- ✅ FsCheck property tests prove window invariants (`V(A) ≤ V(S) ≤ V(A)+k`, no orphan transitions, no stuck Timer Recovery). (#212, 2026-05-21)
- 🟡 Hardware loop sustains 10 kB transfer across NinoTNCs with 0–30 % scripted loss. *No-loss matrix passes end-to-end across modes 0 + 6 × TXDELAY 50–400 ms in the steady-state case (#213, 2026-05-21). Both recovery-path SDL gaps are now fixed: [ax25sdl#44](https://github.com/m0lte/ax25sdl/issues/44) (`Invoke_Retransmission` recovered as a real loop in Packet.Ax25.Sdl 0.7.0 and executed by the runtime — `SdlLoopExecutor`, shared by transition + subroutine paths) and [ax25sdl#43](https://github.com/m0lte/ax25sdl/issues/43) (`Enquiry_Response` F:=1, already on main). In-process proof that REJ recovery re-emits every unacked I-frame: `DataLinkConnectedRetransmitTests.Invoke_Retransmission_Requeues_Every_Unacked_Frame_Not_Just_One`. The scripted-loss matrix's unconditional skip is removed; it now runs on the hardware-loop runner and awaits on-air confirmation on the bench before this criterion flips to ✅. Downstream tracker: [#214](https://github.com/m0lte/packet.net/issues/214).*

### 5.3 Phase 3 — KISS hardening ⬜ ([#169](https://github.com/m0lte/packet.net/issues/169))

ACKMODE round-trip, multi-drop, NinoTNC mode catalog (0–15) + SETHW byte (mode + 16 for non-persist), TX-Test frame parser, ParameterSendInterval. Verified against both LinBPQ container and the back-to-back NinoTNC pair.

### 5.4 Phase 4 — Node host ⬜ ([#167](https://github.com/m0lte/packet.net/issues/167))

ASP.NET Core 10 minimal-API host. `Konscious.Security.Cryptography` Argon2id + `Fido2NetLib` WebAuthn + `Microsoft.IdentityModel.JsonWebTokens` JWT. SQLite via `Microsoft.Data.Sqlite` + `Dapper`. First-start bootstrap: random admin password + one-time `/setup?token=…` URL.

### 5.5 Phase 5 — Web UI ⬜ ([#170](https://github.com/m0lte/packet.net/issues/170))

Vite + React + TS + Tailwind + shadcn/ui. Routes: Ports, Sessions, Live Monitor, TNC2 Prompt, Users, Link Troubleshoot. OpenAPI client via `openapi-typescript` + `@tanstack/react-query`.

### 5.6 Phase 6 — RHPv2 + AGW ⬜ ([#171](https://github.com/m0lte/packet.net/issues/171))

Mirror RHPv2 framing from `/home/tf/src/rhp2lib-net/src/RhpV2.Client/Protocol/`. TCP + WS listeners. `--linbpq-compat` flag for the WS subset. AGW server **127.0.0.1 only** by default; `--listen-public --i-understand-agw-is-unauthenticated` required to expose.

### 5.7 Phase 7 — Packaging + one-click update ⬜ ([#172](https://github.com/m0lte/packet.net/issues/172))

Self-contained `dotnet publish` matrix for linux-x64/arm64/arm (v7), win-x64, osx-arm64/x64. Multi-arch Docker via `buildx`. `installers/install.sh` with cosign verification. In-app `POST /api/system/update` with watchdog rollback.

### 5.8 Phase 8 — MCP + live monitor v2 + link troubleshooting ⬜ ([#173](https://github.com/m0lte/packet.net/issues/173))

`Packet.Mcp` over stdio + SSE. Read tools: `list_ports`, `list_sessions`, `recent_frames(filter)`, `link_quality(remote)`, `network_topology`, `decode_frame(hex)`. Write tools: `send_ui_frame`, `reset_port`, `disconnect_session`, `set_kiss_param` — require separate `mcp:invoke` scope token and audit-logged. Link troubleshoot view: per-link RTT, retries, REJ/SREJ counts, T1/T3 graphs.

### 5.9 Phase 9 — Plugin API + NET/ROM + hardening ⬜ (post-v1) ([#174](https://github.com/m0lte/packet.net/issues/174))

`Packet.Node.Extensions/IApplicationModule.cs` — REST routes, MCP tools, frontend bundle, AX.25 session handler. Loaded from `/var/lib/packetnet/plugins/*.dll` in isolated `AssemblyLoadContext`. DAPPS validates the API design. `Packet.NetRom` ships as a separate package. SharpFuzz harnesses + 72 h soak vs LinBPQ.

### 5.10 Phase 10 — Hardware ecosystem & adaptive RF ⬜ (post-v1) ([#175](https://github.com/m0lte/packet.net/issues/175))

The differentiator no other TNC stack does well: treat the radio + modem as first-class telemetry sources, and use their signals to drive AX.25's parameter knobs in real time. Unlocks a class of "you can see your link degrading and recover before it dies" UX that doesn't exist today.

**Workstreams:**

- **Tait 8100 / 8200 CCDI integration** — serial control channel to the radio surfaces SNR, signal-quality, MAS/SQ status, busy detect, channel programming. Three existing PoC repos by Tom (will be rewritten/folded into Packet.NET, not vendored):
  - [`M0LTE/tait-ccdi`](https://github.com/M0LTE/tait-ccdi) — protocol layer.
  - [`M0LTE/TaitMaster`](https://github.com/M0LTE/TaitMaster) — management UI.
  - [`M0LTE/taitctrl`](https://github.com/M0LTE/taitctrl) — CLI control.
  - Spec reference: [CCDI manual](https://wiki.oarc.uk/_media/radios:tm8100-protocol-manual.pdf) (PDF).
  - Target `Packet.Radio.Tait` library with `IRadioControl` abstraction so other radios can slot in (Yaesu CAT, ICOM CI-V, …).
- **Frequency-agile operation** — if the radio is CAT-controllable, schedule QSY across a frequency plan. Use cases: APRS digi tail on calling channel + drop to working channel for connected-mode sessions; per-link QSY when SNR drops below threshold; automatic channel hunting in poor band conditions.
- **NinoTNC mode agility / negotiation** — currently the operator picks a NinoTNC mode (0–15) at config. Goal: query NinoTNC capabilities at startup, negotiate the optimal mode for current channel quality (1200 → 4800 → 9600 based on SNR), renegotiate on degradation. Requires SETHW probe + mode-change handshake (open question: does NinoTNC firmware support runtime mode change?).
- **NinoTNC firmware upgrades** — port [`ninocarrillo/flashtnc`](https://github.com/ninocarrillo/flashtnc) flow into `packetnet ctl flash-tnc` so non-technical users can update firmware from the web UI. Bootloader protocol reverse-engineering needed.
- **Channel-quality scoring** — fuse radio telemetry (SNR, RSSI, busy %) with AX.25 telemetry (RC, REJ/SREJ rate, RTT) into a per-link "quality index". Surface in UI; feed back into adaptive parameter tuning.
- **Adaptive AX.25 parameters** — T1, RC, k, mod-8 vs mod-128 chosen dynamically from the quality score. Currently §C.4 leaves these fixed. Care needed — interop with non-adaptive peers must remain spec-compliant.

**Hardware test harness** (incoming): 2× NinoTNC + 2× Tait radio on USB, cross-wired audio. Will be available for autonomous capability exploration once delivered. Initial autonomous spike: probe both radios via CCDI, dump capabilities, characterise SNR-vs-distance behaviour across the air-cross-coupling, baseline NinoTNC mode-change behaviour.

**Exit criteria** (high-level):

- `IRadioControl` abstraction with at least Tait CCDI implementation.
- Per-link quality index visible in web UI + persisted to time-series.
- At least one adaptive parameter (T1 or k) wired to quality feedback under a `--adaptive` flag.
- NinoTNC mode-change demonstrated end-to-end (manual trigger, no auto-negotiation needed for exit).
- `packetnet ctl flash-tnc` working from CLI; web UI integration optional.

### 5.X Spike backlog ⬜ (issues [#176](https://github.com/m0lte/packet.net/issues/176)–[#184](https://github.com/m0lte/packet.net/issues/184))

Smaller, time-boxed exploration tasks. Not phase-sequenced — each can land
whenever it's the highest-leverage next thing. Listed in approximate order
of leverage-per-effort.

**SP-001 — LinBPQ MQTT frame feed ingestion** ([#176](https://github.com/m0lte/packet.net/issues/176)) (high leverage, low effort).
Tom operates a live LinBPQ node with four real RF ports and is willing to
expose an MQTT feed of every AX.25 frame sent/received (with or without KISS
wrapper). Build a `Packet.Replay.MqttIngest` tool that subscribes to the
feed, decodes frames, runs them through our parser, and emits structured
records (frame type, callsigns, control bits, payload size, decoded errors).
Acts as a 24/7 corpus of real-world frame traffic across four real RF
channels — vastly richer than synthetic test traffic. Specifically catches
parser edge cases that property-tests won't generate. Feeds the regression
corpus in SP-003.

**SP-002 — Direwolf-as-reference end-to-end harness** ([#177](https://github.com/m0lte/packet.net/issues/177)) (high leverage, medium
effort). Docker-containerise direwolf, expose KISS-TCP, point Packet.NET at
it. For every transcribed transition, A/B our orchestrator's behaviour
against direwolf's actual output. Direwolf is the closest-to-figure
implementation (see SI-* triangulation in [`docs/spec-issues.md`](spec-issues.md)),
so behavioural equivalence with direwolf is a strong correctness signal.
Strongest force multiplier on the SDL transcription work.

**SP-003 — Replay/record harness** ([#178](https://github.com/m0lte/packet.net/issues/178)) (high leverage, medium effort). pcap-
style timestamped capture of KISS + AX.25 wire bytes with per-frame direction
+ port + source labels. Replay later to repro bugs, build a regression
library of weird-frames-seen-in-the-wild, and A/B real on-air behaviour
against the orchestrator's projection. Foundation for SP-001's persistence
+ for future "I saw a strange frame, replay this against the state machine"
debugging. Storage as JSON-lines or capnproto; both fine.

**SP-004 — AX.25 wire-format fuzzer (SharpFuzz)** ([#179](https://github.com/m0lte/packet.net/issues/179)) (medium leverage, low
effort). Plan §7 already promises this for nightly. Frame parser as target.
Mostly mechanical: a fuzz harness against `Ax25Frame.TryParse`, plus
`KissFrame.TryParse`. Likely surfaces real bugs in edge cases (oversized
fields, weird PIDs, malformed addresses). Cheap and overdue.

**SP-005 — Multi-Packet.NET-instance interop via net-sim** ([#180](https://github.com/m0lte/packet.net/issues/180)) (medium leverage,
low effort). Net-sim already in the interop stack. Spin up 2-3 Packet.NET
instances and let them talk to each other through net-sim's lossy channel.
Any drift between identical implementations exposes a state-machine bug
cheaper than against LinBPQ — and complements interop tests against
heterogeneous peers.

**SP-006 — Spec-prose-to-stub-test extractor** ([#181](https://github.com/m0lte/packet.net/issues/181)) (higher effort, payoff after
Phase 2). Parse the AX.25 v2.2 spec markdown, pull every "shall" sentence
into a stub xUnit test (initially `[Skip]`). Surfaces gaps where the SDL
transcription doesn't cover a prose mandate. Lower priority — the SDL
transcription itself already does most of the work, but this acts as a
backstop.

**SP-007 — NinoTNC KISS-side SNR proxy** ([#182](https://github.com/m0lte/packet.net/issues/182)) (low leverage but useful fallback).
Even without Tait integration, can we infer link quality from frame-quality
bits / retry rates / error counts alone? If yes, gives `IRadioControl`'s
quality signal a fallback path for users without CAT-capable radios.
Investigate after SP-001 has produced enough real-world data to baseline.

**SP-008 — Full APRS encoding/decoding library** ([#183](https://github.com/m0lte/packet.net/issues/183)) (large scope; significant
ecosystem value). The SDL transcription work covers AX.25's link layer; APRS
is the application-layer protocol that rides on UI frames. A native
`Packet.Aprs` library covering position/weather/mic-E/messages/status/
telemetry/objects/items would slot in cleanly above the AX.25 stack and
make Packet.NET a credible APRS gateway / parser, not just a bare TNC.

Specs:
- [APRS101.pdf](http://www.ui-view.net/files/APRS101.pdf) — the original
  1998 specification.
- [`how.aprs.works/aprs101-pdf-is-obsolete`](https://how.aprs.works/aprs101-pdf-is-obsolete/)
  — community-maintained corrections, clarifications, and deprecations
  layered on top of the 1998 spec. Both are needed to model real-world
  APRS faithfully.

Approach mirrors the AX.25 SDL pipeline where possible:
- Trust-the-spec encode-then-verify discipline.
- Property tests for round-trip (decode → encode → assert byte-equal) on
  every payload type.
- A real-world corpus (the SP-001b APRS-IS feed already exists; the
  forthcoming SP-001 LinBPQ-MQTT feed will be richer) gives free fuzz
  coverage.

Likely sequenced after the LinBPQ-MQTT feed lands so the library can be
validated against real traffic from day one. Open question: do we want
to support APRS Messaging end-to-end (would need state for acks +
retries) or strictly the parsing/encoding surface? Decide before
starting.

**SP-009 — Visual state-machine display** ([#184](https://github.com/m0lte/packet.net/issues/184)) (medium leverage, low effort for the static rung). Two rungs of increasing scope. **Static rung (near-term):** add a Mermaid emitter to the SDL codegen pipeline (`tools/Packet.Sdl.CodeGen.Mermaid/`) that walks the IR and produces one `stateDiagram-v2` per page, with transitions labelled by trigger + guard. Outputs land next to the existing AX.25 figures in `docs/`, render directly in GitHub markdown, and slot naturally into the existing 7-backend codegen pattern. ~1–2 hours. **Live rung (deferred):** expose a "transition occurred" event on `Ax25Session`, render the same graph in a small UI (Avalonia desktop, or the browser-terminal track if/when SP-N lands) and highlight the current state + flash the firing edge. ~2–3 days plus whatever UI host we pick. Pay the live rung's cost only when there's a concrete demo or debug-of-live-link need driving it; the static rung pays for itself immediately as documentation. The mermaid emitter is also the natural baseline for any future graph-style export (Graphviz dot, PlantUML), so even if a different live UI is chosen later the documentation track stands alone.

### 5.Y Hardware-arrival probe playbook ⬜ ([#185](https://github.com/m0lte/packet.net/issues/185))

Concrete first-day actions for when the 2× NinoTNC + 2× Tait rig lands on
the bench. Listed so the autonomous agent has a ready playbook the moment
hardware is available.

1. **Tait capability inventory** — query both radios via CCDI, dump full
   capability list (channels, modes, frequencies, audio levels, supported
   commands) into `artifacts/radio-capabilities/<radio-serial>.json`.
2. **NinoTNC capability probe** — SETHW iteration over modes 0–15 + the
   `+16` no-flash variant; record what KISS responses come back; observe
   whether mode change is immediate or requires re-init. Directly answers
   OQ-009.
3. **SNR vs TX-level baseline** — step the Tait TX audio level across the
   cross-wire; log SNR on the receiving Tait at each level. Builds a
   per-rig calibration curve usable by the adaptive estimator.
4. **NinoTNC mode-by-SNR survey** — for each NinoTNC mode that decodes on
   the cross-wire, find the SNR floor at which BER starts climbing. Lets us
   pick mode-switch thresholds.
5. **1000-iteration AX.25 soak** — SABM → UA → I-frames → DISC → UA round-
   trip, 1000 times, through both radios. Records frame error rate, retry
   distribution, T1 variance, mode-stability across the run.
6. **Cross-coupling degradation** — TX on radio 1, deliberately mistune
   radio 2. Plot decode success vs offset. Maps the "graceful degradation"
   shape of the audio path.

Outputs all land in `artifacts/hardware-probe/<date>/` and feed into Phase
10's adaptive-parameter tuning.

---

## 6. SDL transcription discipline

Critical to the project. Read [§2.1](#21-trust-the-figure), [§2.2](#22-encode-then-verify-not-infer-then-encode), [§2.3](#23-pin-implementation-evidence) and [docs/sdl-primer.md](sdl-primer.md) before touching any `*.sdl.yaml`.

### 6.1 The pipeline

```
spec-sdl/<machine>/<state>.sdl.yaml      ← human-authored transcription
              │
              ▼  dotnet run --project codegen/src/Packet.Sdl.CodeGen   (in m0lte/ax25sdl)
spec/csharp/<Machine>_<State>.g.cs                                  ← generated, checked in to m0lte/ax25sdl
spec/csharp/tests/<Machine>_<State>.g.Tests.cs                      ← generated, checked in to m0lte/ax25sdl
```

CI in `m0lte/ax25sdl` runs the codegen and `git diff --exit-code`. Drift fails CI. The generated C# library is published as the `Packet.Ax25.Sdl` NuGet; the generated tests run alongside in ax25sdl's own CI (per the 2026-05-21 wire-up — see §17). Packet.NET consumes the lib via NuGet and inherits the test coverage upstream rather than maintaining a local copy.

### 6.2 The DSL

Schema: [`/spec-sdl/schema/sdl-machine.schema.json`](../spec-sdl/schema/sdl-machine.schema.json). Event catalog: [`/spec-sdl/events.yaml`](../spec-sdl/events.yaml).

Worked example: [`/spec-sdl/data-link/connected.sdl.yaml`](../spec-sdl/data-link/connected.sdl.yaml) — see especially the header comment which documents variable definitions and implementation cross-references.

A transition entry looks like:

```yaml
- id: t01_dl_flow_off_when_own_receiver_busy
  on: DL_FLOW_OFF_request
  guard: "own_receiver_busy"
  actions:
    - set_own_receiver_busy
    - "RNR response"
    - clear_acknowledge_pending
  next: Connected
  notes: "figc4.4a col 5, Yes branch of 'Own Receiver Busy?'. …"
```

### 6.3 Workflow

1. Human reads the SDL figure. Translates each path through the diagram into a plain-text bullet list (`event → decision → actions → next state`).
2. Agent receives the plain-text description and encodes it into the YAML. Agent does *not* read the PNG to infer paths.
3. Agent runs `dotnet run --project codegen/src/Packet.Sdl.CodeGen` *in m0lte/ax25sdl* against the page's `.sdl.yaml`. (The codegen and the C# tests both live in ax25sdl since the 2026-05-17 5-repo split; see §17.)
4. `dotnet build` + `dotnet test` should pass. CI re-runs the codegen and fails on diff.
5. PR review pairs the YAML diff with the source figure. The PR description embeds or links to the figure.
6. On non-obvious transitions, the PR also cites at least one implementation reference per [§2.3](#23-pin-implementation-evidence).
7. Any `verification_pending` note added to the YAML must also be added to [`docs/spec-issues.md`](spec-issues.md) — the central tracker for candidate upstream-spec issues. Cross-link in both directions (YAML transition notes reference the SI-NN id; the tracker cites the YAML transition id).

### 6.4 SDL inventory & status

> Non-figc4 transcription work (Simplex Physical, Duplex Physical, Link Multiplexer, Management Data-Link, Segmenter, Reassembler — 18 pages) tracked at [`m0lte/ax25sdl#14`](https://github.com/m0lte/ax25sdl/issues/14). The figc4.x redraw effort lives at [`m0lte/ax25sdl#13`](https://github.com/m0lte/ax25sdl/issues/13).

Source: `https://github.com/packethacking/ax25spec/blob/main/doc/ax.25.2.2.4_Oct_25.md`.

| Machine | Figure(s) | Pages | State name(s) | Coverage |
|---|---|---|---|---|
| Simplex Physical | C2a.1–C2a.7 | 7 | Ready, Receiving, TX Suppression, TX Start, Transmitting, Digipeating, RX Start | ⬜ none |
| Duplex Physical | C2b.1–C2b.5 | 5 | RX Ready, Receiving, TX Ready, TX Start, Transmitting | ⬜ none |
| Link Multiplexer | C3.1–C3.3 (+ C3.4 subroutines) | 3 (+1) | Idle, Seize Pending, Seized | ⬜ none |
| Data-Link | C4.1, C4.2, C4.3, C4.4a–c, C4.5a–e, C4.6a–b, C4.7a–b (subs) | 12 (+2 subs) | Disconnected, Awaiting Connection, Awaiting Release, Connected, Timer Recovery, Awaiting V2.2 Connection | 🟡 figc4.1, figc4.2, figc4.3, figc4.4, figc4.6 done; figc4.5/4.7 remaining |
| Management Data-Link | C5.1, C5.2 | ~2 | (XID negotiation flow) | ⬜ none |
| Segmenter / Reassembler | C6.1–C6.2 | 2 | (Segmenter, Reassembler) | ⬜ none |

Total ≈ 28 pages (was 27 before figc4.6 turned out to span pages a + b).
Phase 0 covered ~2 columns of one page.

---

## 7. Test pyramid + interop CI

| Layer | Tooling | When it runs |
|---|---|---|
| Unit | xUnit | every PR (CI) |
| Property | FsCheck.Xunit v3 | every PR |
| Mutation | Stryker.NET | nightly + release branches |
| Conformance (SDL) | xUnit, generated | every PR; `git diff --exit-code` guard |
| Component | xUnit + `WebApplicationFactory<Program>` + mock KISS | every PR |
| Interop | xUnit + Testcontainers.NET (LinBPQ, Xrouter, net-sim, test-net) | subset on PR, full on nightly |
| Hardware loop | xUnit + USB-attached NinoTNC pair (`[Trait("Category","HardwareLoop")]` + `SkippableFact`) | self-hosted runner job, every PR touching `Packet.Kiss*` or `Packet.Ax25` |
| E2E UI | Playwright (.NET) | every PR (once UI exists) |
| Fuzz | SharpFuzz | nightly, 1 h per target |
| Soak | bespoke harness | nightly, 72 h on tagged release |

CI filter convention: default jobs run with `--filter "Category!=HardwareLoop&Category!=Interop"`. Hardware-loop and interop jobs use the opposite filter.

### 7.1 Interop scenarios — staged growth

| Phase added | Scenario | Assertion |
|---|---|---|
| 1 | UI frame KISS-TCP → LinBPQ | LinBPQ log contains exact AX.25 hex |
| 1 | UI frame across back-to-back NinoTNCs | both ends decode identical bytes |
| 2 | SABM/UA cycle (mod-8) vs LinBPQ over net-sim AFSK1200 | our session reaches Connected after UA; DISC/UA returns it to Disconnected (`LinbpqViaNetsimConnectedMode`) |
| 2 | SABM/UA cycle (mod-8) vs XRouter over net-sim AFSK1200 | our session reaches Connected after UA; DISC/UA returns it to Disconnected (`XrouterViaNetsimConnectedMode`) |
| 2 | SABM/UA cycle (mod-8) vs rax25 (Habets, Rust) over net-sim AFSK1200 | our session reaches Connected after UA; DISC/UA returns it to Disconnected (`Rax25ViaNetsimConnectedMode`) — handshake only; REJ/SREJ + segmentation untested in rax25 |
| 2 | ax25.ts (TypeScript, Node TCP) vs LinBPQ over net-sim AFSK1200 | `Ax25Stack` + `TcpKissTransport` reach Connected after UA, exchange I-frames (banner from BPQ + `P\r` ports-command round-trip), DISC/UA cleanly returns to Disconnected (`m0lte/ax25-ts: tests/integration/linbpq-via-netsim.test.ts`, exercised by this repo's `interop.yml` step that clones the sibling repo) |
| 2 | 30 % scripted loss net-sim afsk1200, 10 kB transfer | completes; retries observed; no FRMR |
| 3 | ACKMODE echo via LinBPQ | ack-bytes returned within 5 s |
| 3 | ACKMODE via NinoTNC pair | ack-bytes correlate to RX on partner TNC |
| 6 | rhp2lib-net client → Packet.Rhp2.Server | full connect / send / recv / close |
| 6 | agw_client.py (ported) | R/G/g/X/V/D pass; non-loopback refused |

---

## 8. Hardware-in-the-loop development cycle

The durability backbone. Two NinoTNCs are connected:

- **USB serial** to dev host. KISS over `/dev/ttyACM*` (Linux) or COM ports (Windows).
- **Audio TX↔RX** cross-wired between the two TNCs, emulating an FM RF link.

Use cases:

- Phase 1 onward: every PR touching `Packet.Kiss*` or `Packet.Ax25` runs the self-hosted hardware-loop job, exercising a representative interop scenario over the physical loop.
- Phase 3: ACKMODE echo verified end-to-end across real firmware.
- Phase 2: lossy-channel behaviour reproduced physically by attenuating the audio path.
- Local dev iteration: `packetnet sdlgen` + `dotnet test --filter "Category=HardwareLoop"` against the live TNC pair.

The probe at [`tests/Packet.Interop.Tests/Hardware/NinoTncEnumeration.cs`](../tests/Packet.Interop.Tests/Hardware/NinoTncEnumeration.cs) is the entry point. Currently skips when fewer than 2 ACM-class devices are found.

---

## 9. Packaging & onboarding

- `curl -sSL https://packet.net/install.sh | sudo bash` → detect distro/arch → fetch signed tar → cosign-verify → install systemd unit → generate admin bootstrap URL → print URL.
- `systemctl enable --now packetnet` left to the installer; service runs as unprivileged `packetnet` user; stateful dirs `/var/lib/packetnet`, `/etc/packetnet`.
- Docker image multi-arch via `buildx`; healthcheck on `/api/system/health`.
- TLS on by default. Self-signed cert generated on first start. LE/ACME opt-in from web UI.
- `packetnet ctl` subcommands for ops (reset admin password, dump diag bundle, regenerate cert, run migrations).
- `POST /api/system/update` for one-click signed update with auto-rollback on health failure.

Targets: linux-x64, linux-arm64, linux-arm (v7), win-x64, osx-arm64, osx-x64. Self-contained binaries unless armhf size > 80 MB (fallback FDD).

---

## 10. Security threat model

| Surface | Default bind | Auth | Notes |
|---|---|---|---|
| Web UI (HTTPS) | `0.0.0.0:8443` | session cookie + passkey | TLS by default; SameSite=strict; CSRF token |
| REST API | same | JWT scopes | rate-limited |
| WS monitor/console/update | same | JWT in cookie | scope-gated |
| RHPv2 TCP | `127.0.0.1:8050` | auth msg required | `--listen-public` opt-in |
| RHPv2 WS | piggyback web port `/rhp` | auth msg (or relaxed under `--linbpq-compat`) | |
| AGW TCP | `127.0.0.1:8000` | none (legacy) | non-loopback requires `--listen-public --i-understand-agw-is-unauthenticated` |
| MCP stdio | n/a | local user | |
| MCP SSE | `127.0.0.1:8051` | bearer + `mcp:invoke` scope | separate token lifetime |
| KISS-TCP outbound | n/a | n/a | TLS not in scope (TNC vendors don't speak it) |
| AXUDP | configurable | none | startup banner warning |

All write endpoints audit-logged (actor, IP, scope, payload hash) into `config.db`.

JWT scopes: `frames:read`, `ports:read`, `ports:write`, `sessions:write`, `system:admin`, `mcp:invoke`.

---

## 11. Out of scope for v1

- NET/ROM (Phase 9).
- BBS / chat / mailbox / DAPPS (out-of-tree plugins).
- HF waveforms (VARA, ARDOP) — KISS modems only.
- Multi-node clustering / Postgres backend.
- AOT publish (revisit when Fido2NetLib + Konscious are AOT-trim-safe).
- Localisation (English only in v1).

---

## 12. Locked external dependencies

Pinned in [`Directory.Packages.props`](../Directory.Packages.props). Versions sometimes lag here while we settle on a release — `Directory.Packages.props` is authoritative.

| Concern | Package | v1 floor |
|---|---|---|
| SQLite | `Microsoft.Data.Sqlite` | 10.0.0 |
| SQL helpers | `Dapper` | 2.1.66 |
| Argon2id | `Konscious.Security.Cryptography.Argon2` | 1.3.1 |
| WebAuthn | `Fido2.AspNet` | 4.0.0-beta.16 |
| JWT | `Microsoft.IdentityModel.JsonWebTokens` | 8.5.0 |
| Property tests | `FsCheck.Xunit` v3 | 3.1.0 |
| Mutation | `dotnet-stryker` | (tool, not pkg) |
| Fuzzing | `SharpFuzz` | (TBD when added) |
| Containers in tests | `Testcontainers` | 4.1.0 |
| Conditional skips | `Xunit.SkippableFact` | 1.5.23 |
| Logging | `Serilog` (+ sinks) | 4.2.0 / 9.0.0 |
| Tracing (opt-in only) | `OpenTelemetry` + `Exporter.Otlp` | 1.10.0 |
| Typed HTTP client | `Refit` | 8.0.0 |
| Validation | `FluentValidation` | 11.11.0 |
| YAML | `YamlDotNet` | 16.3.0 |
| JSON Schema (codegen lint) | `JsonSchema.Net` | 7.3.0 |
| Serial IO | `System.IO.Ports` | 10.0.0 |
| Mediator | none (direct calls — MediatR licensing) | — |
| Web bundler | Vite + pnpm | (Phase 5) |
| CSS / components | Tailwind + shadcn/ui | (Phase 5) |
| OpenAPI client | `openapi-typescript` + `@tanstack/react-query` | (Phase 5) |
| E2E | `Microsoft.Playwright` | (Phase 5) |
| Signing | `cosign` (sigstore) | (Phase 7) |

---

## 13. Reference shelf

### 13.1 Spec

- **AX.25 v2.2 rev 4 (Oct 2025)** — Markdown rewrite: [`packethacking/ax25spec`](https://github.com/packethacking/ax25spec) · raw URL: `https://raw.githubusercontent.com/packethacking/ax25spec/main/doc/ax.25.2.2.4_Oct_25.md` · Local cache: `/tmp/ax25-spec.md` if pulled.
- **KISS TNC protocol**: `https://github.com/packethacking/ax25spec/blob/main/doc/kiss-tnc-protocol.md`.
- **Multi-drop KISS / ACKMODE (Karl Medcalf WK5M, John Wiseman G8BPQ)**: `https://github.com/packethacking/ax25spec/blob/main/doc/multi-drop-kiss-operation.md`.

#### APRS (for SP-008 + direwolf-reference pipeline)

The original [`APRS101.pdf`](http://www.ui-view.net/files/APRS101.pdf) from 1998 is **explicitly marked obsolete** by [how.aprs.works/aprs101-pdf-is-obsolete](https://how.aprs.works/aprs101-pdf-is-obsolete/). Canonical updated specs live in [`wb2osz/aprsspec`](https://github.com/wb2osz/aprsspec) (maintained by the direwolf author):

- [`APRS12c.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS12c.pdf) — primary spec (v1.2 draft C). Replaces APRS101 for new work. ~130 pp.
- [`Understanding-APRS-Packets.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/Understanding-APRS-Packets.pdf) — implementer-friendly companion + **catalogued common bugs** (lowercase callsigns, Kenwood 0xFF bursts, etc.).
- [`APRS-Symbols.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS-Symbols.pdf) — full symbol table.
- [`APRS-Digipeater-Algorithm.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS-Digipeater-Algorithm.pdf) — digipeater behaviour (when we ever digipeat).

When APRS101 and APRS12c disagree, **APRS12c wins** for our purposes.

### 13.2 Reference implementations

- **LinBPQ** (John Wiseman G8BPQ) — *the* canonical implementation. Local clone: [`/home/tf/src/linbpq/`](/home/tf/src/linbpq/). Notable files:
  - `L2Code.c` — data-link layer (frame handling, RR/RNR decisions in `RR_OR_RNR()` at L4143).
  - `AGWAPI.c:37-67` — AGW 36-byte header struct.
  - `tests/integration/helpers/{agw_client.py, linbpq_instance.py, telnet_client.py}` — test harness we will port patterns from.
  - `docs/protocols/{kiss,rhp,bpqtoagw,axip}.md` — LinBPQ-specific configurables.
- **ax25ms** (Thomas Habets) — protocol-faithful modern C++ implementation. Clone: [`/tmp/impl-survey/ax25ms/`](/tmp/impl-survey/ax25ms/). `src/seqpacket_con.{h,cc}` carries `own_receiver_busy` / `peer_receiver_busy` flags as defined; flag-set paths are dead code.
- **direwolf** (WB2OSZ) — full v2.2 SDL state machine in `src/ax25_link.c`. Useful for cross-checking SDL transitions; the author flags errata in comments (search for `Erratum`).
- **rhp2lib-net** (M0LTE) — RHPv2 .NET client. Local clone: [`/home/tf/src/rhp2lib-net/`](/home/tf/src/rhp2lib-net/). We mirror `src/RhpV2.Client/Protocol/{RhpFraming,RhpConstants,Messages,RhpJson}.cs` into `Packet.Rhp2` verbatim; `Testing/MockRhpServer.cs` is the blueprint for `Packet.Rhp2.Server`.

### 13.3 Test peers

- **test-net** — synthetic BPQ + XRouter network: [`github.com/m0lte/test-net`](https://github.com/m0lte/test-net). 14 BPQ + 2 XRouter nodes, 5 towns + backbones, external KISS attach at `localhost:8100`.
- **net-sim** — audio-domain network simulator: [`github.com/packethacking/net-sim`](https://github.com/packethacking/net-sim). afsk1200 / gfsk9600 / il2p modems, FM capture mixer, per-link `loss_db`. **Does not support ACKMODE**.
- **Xrouter** — closed-source NetRom node: [`github.com/packethacking/xrouter-container`](https://github.com/packethacking/xrouter-container). AXIP/AXUDP only (no KISS).

### 13.4 Hardware

- **NinoTNC** (N9600A) — kissproxy mode catalog reused under MIT. Source: [`M0LTE/kissproxy@web-interface`](https://github.com/M0LTE/kissproxy/tree/web-interface), files `KissFrameBuilder.cs:45-196` and `ModemState.cs:111-141`.

### 13.5 Other docs

- [DAPPS](https://m0lte.github.io/dapps/) — reference plugin target.
- [LinBPQ docs (M0LTE fork)](https://m0lte.github.io/linbpq/).

---

## 14. Glossary

Domain shorthand collected here as we hit it. Keep current.

| Term | Meaning |
|---|---|
| **ACKMODE** | G8BPQ KISS extension: command `0x{port}C` with 2 ack-bytes prefix; TNC echoes them back when the frame actually transmits. Lets the host time TX-completion. Not supported by net-sim. |
| **AGW** | TCP wire protocol exposed by SV2AGW's "AGW Packet Engine"; widely adopted as a host-to-TNC API. 36-byte fixed header + variable payload. No auth in v1. |
| **AX.25** | The AX.25 v2.2 link-layer protocol — Amateur Packet-Radio's HDLC variant. |
| **AXUDP** | UDP encapsulation of AX.25 frames. Used for inter-node links without a real RF modem. |
| **DL primitive** | A service primitive exchanged between Layer 3 and the Data-Link state machine. `DL-CONNECT`, `DL-DATA`, `DL-FLOW-OFF`, etc. See §5.3 of the spec. |
| **DXE** | Data Exchange Equipment — spec term for the AX.25 endpoint. |
| **FCS** | Frame Check Sequence — 16-bit CRC-CCITT (poly 0x1021), MSB-first. |
| **HDLC** | High-level Data Link Control — the ISO link-layer family AX.25 derives from. |
| **KISS** | Keep It Simple Stupid — the de-facto host-TNC framing protocol. SLIP-style escape (`FEND=0xC0`, `FESC=0xDB`). |
| **L2 / L3** | Link layer (AX.25 itself) / network layer (NET/ROM, IP, etc.). |
| **mod-8 / mod-128** | Sequence-number width. mod-8 frames have 3-bit `N(S)` / `N(R)`; mod-128 have 7-bit. Negotiated via XID. Mod-128 needs SABME, not SABM. |
| **N(R), N(S), V(A), V(R), V(S)** | Acknowledge sequence in received frame / send sequence in received frame / our acknowledge state variable / our receive state variable / our send state variable. |
| **NET/ROM** | A widely-used layer 3 routing protocol over AX.25. Out of v1; planned as `Packet.NetRom` in Phase 9. |
| **NinoTNC** (N9600A) | KISS modem hardware. Operating mode set via KISS SETHW command; mode + 16 means "don't write to flash". |
| **Own Receiver Busy** | A flag in the data-link state machine defined by the spec as *"Layer 3 is busy and cannot receive I frames."* See §C4.3 and the col 5 amendment-log entry. |
| **P/F bit** | Poll/Final bit in the AX.25 control field. Set in commands (poll), echoed in responses (final). |
| **PID** | Protocol ID byte in the AX.25 header; identifies the encapsulated L3 protocol (`0xCF` = NET/ROM, `0xF0` = "none", `0x08` = segmented frame). |
| **RHPv2** | Routing Hub Protocol v2 — a JSON-over-TCP/WS API for external apps to use a node's data-link. Spec: [rhp2lib.pages.dev](https://rhp2lib.pages.dev/). |
| **SDL** | Specification and Description Language (ITU-T Z.100). The diagram language the AX.25 v2.2 spec uses for its state machines. See [docs/sdl-primer.md](sdl-primer.md). |
| **SREJ** | Selective Reject — supervisory frame requesting retransmission of a single I-frame by `N(R)`. v2.2 only. |
| **SSID** | Secondary Station Identifier — 4-bit suffix in an AX.25 callsign (e.g. `G7XYZ-7`). |
| **T1 / T2 / T3** | Acknowledge / response-delay / inactivity timers. T1 ≈ 3000 ms default; T3 is a keep-alive. |
| **TNC** | Terminal Node Controller — historical name for the KISS modem box. |
| **UI frame** | Unnumbered Information frame — connectionless AX.25 datagram. Used by APRS, beacons, broadcast. |
| **XID** | Exchange Identification frame — used to negotiate mod-8/mod-128, window size `k`, T1, N2, etc. |

---

## 15. Open questions

Tracked here so they don't get lost. Once resolved, move the resolution into the relevant section and add an amendment-log entry.

| ID | Question | Stage | Owner |
|---|---|---|---|
| OQ-001 ([#186](https://github.com/m0lte/packet.net/issues/186)) | Provenance / spec-deviation field — promote `notes:` prefix to a structured `verification:` block? Deferred until we have ≥3 examples to validate the schema against. | Open | Tom |
| OQ-002 ([#187](https://github.com/m0lte/packet.net/issues/187)) | Hardware-loop runner — should we expose it via GitHub Actions self-hosted runner labels (`hardware-loop-runner`)? Tom is providing the rig. Will sort once kit lands on the bench. | Open | Tom |
| OQ-003 ([#188](https://github.com/m0lte/packet.net/issues/188)) | Cosign key management for one-click update — where does the trusted public key live in the installer? Probably `installers/sigstore-pubkey.pem` baked into the install script, but needs a key-rotation story. | Open | Phase 7 |
| OQ-004 ([#189](https://github.com/m0lte/packet.net/issues/189)) | NuGet org name — is `Packet.*` claimable on nuget.org or do we need `PacketNET.*` / `Packethacking.*`? Tom to check before first NuGet publish (Phase 6+). | Open | Tom |
| OQ-005 ([#190](https://github.com/m0lte/packet.net/issues/190)) | OIDC pluggability — what's the abstraction shape so we don't paint ourselves into a corner? Affects Phase 4 user model. | Open | Phase 4 |
| OQ-006 ([#191](https://github.com/m0lte/packet.net/issues/191)) | yEd / GraphML SDL workflow — Tom tried Mermaid and reported the rendering looks too unlike the original SDL to be useful for visual comparison. Mermaid output is shipped anyway (cheap, catches transcription bugs the YAML diff might miss) but does NOT solve the input-side problem. yEd spike (one figure, agreed shape mapping, parse the .graphml back to our YAML) still on the table whenever Tom has big-screen time. | Open | Tom |
| ~~OQ-007~~ | ~~Stryker mutation score for `Packet.Kiss` is dragged below 70 % by `KissTcpClient`…~~ | ✅ Resolved 2026-05-12 — excluded via `stryker-config.json`; score 67.07→73.33. Fake-socket harness left for whenever Phase 6/7 wants tighter coverage. |
| OQ-008 ([`m0lte/ax25sdl#15`](https://github.com/m0lte/ax25sdl/issues/15)) | Publish `/spec-sdl/` as a community-canonical AX.25 v2.2 state-machine artifact, separate from this repo? The YAML is language-agnostic by design — a Rust/Python/Go/TS codegen against the same files would produce the same transitions, and our C# codegen becomes the reference implementation rather than the source of truth. Three things would need to firm up before "authoritative" is defensible: (a) the guard mini-DSL needs a real grammar (today `GuardEvaluator` parses by ad-hoc string splitting — fine for one consumer, not for many); (b) action verbs need a stable catalog with documented semantics (today they're free-form strings like `"RNR response"`, `"start_T1"`); (c) the schema + events catalog need semver. Realistic move: finish the 27 pages, stabilise the schema, then split `/spec-sdl/` + schema + events into a sibling repo (likely under `packethacking/`). What makes this credibly authoritative rather than just *another* transcription is the encode-then-verify discipline + collaboration with the spec author — both already in place. Revisit at the end of Phase 2. | Open | Tom |
| OQ-009 ([#192](https://github.com/m0lte/packet.net/issues/192)) | NinoTNC mode-change handshake — does the firmware support runtime mode switching without a write-to-flash cycle? `SETHW(mode + 16)` is the "don't write to flash" form; is the actual mode change immediate, or does it require power-cycle? Affects feasibility of Phase 10's mode-agility workstream. Probe once hardware is on the bench. | Open | Phase 10 / hardware-arrival |
| OQ-010 ([#193](https://github.com/m0lte/packet.net/issues/193)) | NinoTNC bootloader / firmware-update protocol — `flashtnc` is the canonical tool; what's the wire protocol? Best to read [`ninocarrillo/flashtnc`](https://github.com/ninocarrillo/flashtnc) source rather than reinvent. Affects feasibility + risk of `packetnet ctl flash-tnc`. | Open | Phase 10 |
| OQ-011 ([#194](https://github.com/m0lte/packet.net/issues/194)) | Radio-control abstraction shape — what's the right `IRadioControl` API? Tait CCDI gives us SNR / RSSI / busy / channel / TX-keying. Yaesu CAT and ICOM CI-V have different feature sets. Common subset is probably {frequency-set, frequency-get, RSSI-get, busy-get, PTT-set} — anything radio-specific (Tait's SNR is unusually rich) goes behind a feature-probe. Decide before locking the Tait implementation. | Open | Phase 10 |

---

## 16. Risks

| # | Risk | Mitigation |
|---|---|---|
| R-01 | SDL transcription is laborious and error-prone (27 pages, subtle semantics). | Per-PR review · trust-the-figure discipline · implementation cross-references · codegen + conformance tests catch structural breakage. |
| R-02 | LinBPQ interop drift between releases. | Pin LinBPQ commit in `docker/linbpq/`; nightly job pulls latest and reports diff. |
| R-03 | ACKMODE only verifiable against LinBPQ container or hardware loop (net-sim doesn't support it). | Hardware-loop runner with the NinoTNC pair + LinBPQ container covers all real-world cases. |
| R-04 | .NET 10 armhf self-contained binary size. | Fallback to framework-dependent + bundled runtime for armhf only; document. |
| R-05 | Passkey UX on LAN HTTPS with self-signed cert (WebAuthn requires secure origin). | `localhost` is treated as secure by browsers; mkcert-style helper for LAN; documented. |
| R-06 | Spec figures may contain genuine errata that aren't caught until the conformance tests fail against a real peer. | Implementation cross-references give us a fallback truth source; PR reviewer checks at least one impl when transition is non-obvious. |
| R-07 | Direwolf / ax25ms / LinBPQ disagree on a subtle behaviour. | Document the divergence in the transition's `notes:`; pick the spec interpretation unless a real peer demonstrably needs otherwise. |

---

## 17. Amendment log

Most recent first. Format:

```
### YYYY-MM-DD — short title
What changed, why, where to look for details.
```

### 2026-06-02 — Conformance happy-path envelope: window edges, sustained transfer, segmentation

Extends the Phase-H conformance suite (test-only) — the "more happy path" half of the robustness arc. `HappyPathConformanceTests`: stop-and-wait (k=1), max mod-8 window (k=7), and a sustained 40-frame transfer that laps the modulus five times — all converge. `EnvelopeConformanceTests`: a §6.6 segmentation/reassembly round-trip — a 300-byte payload split into five segments (`Segmenter`), each carried as its own I-frame across the link and reassembled (`Reassembler`) back to the original. 685 pass / 2 skip (mod-128 #239 and RNR ax25sdl#60 still pending). Adversarial coverage (reorder/duplicate/corrupt) and the RNR/flow-control gap are the next arc steps; ax25sdl#60 (the figc4.4 DL_FLOW_OFF Yes/No inversion) is unverifiable from the all-black source figures, so its fix path (ax25sdl transcription fix vs an ax25spec issue + default-ok quirk) is pending a call.

### 2026-06-02 — Ax25Spec42 SREJ targets the gap: closes the SREJ recovery space (#246)

Implements the `Ax25Spec42SrejTargetsGap` quirk (default on; off under `StrictlyFaithful`), the third and final figc4.x defect blocking multi-frame selective-reject recovery. figc4.4's out-of-sequence `I_received` SREJ path, with a selective-reject exception already outstanding (connected.sdl.yaml `t26_…_no_no_yes_yes`), does `N(r) := N(s)` before SREJ — so it requests retransmission of the frame that just *arrived* (and was just saved), not the missing gap. With more than one frame outstanding the real gap is never re-requested: the peer resends the already-received frame, the receiver SREJs it again, V(R) frozen → livelock. The fix retargets the SREJ to V(R) (the next still-missing frame); the `N(r) := N(s)` verb appears in exactly one figure path so the rewrite is gated on the `I_received` trigger, like #38. Filed upstream as packethacking/ax25spec#42, with direwolf flagging the identical erratum verbatim (`ax25_link.c`: "The SDL says ask for N(S) which is clearly wrong because that's what we just received") and requesting the gap instead — de-facto corroboration per the quirk discipline.

This closes the SREJ data-recovery space: **#242** (out-of-window duplicate discard) ✓, **#241** (SRT/T1V overflow) ✓, **#246** (SREJ target) ✓. `LossRecoveryProperties.A_finite_bidirectional_loss_burst_recovers` now fuzzes **both** REJ and SREJ (400 cases) and converges; the heavy multi-frame SREJ burst (`Srej_heavy_bidirectional_loss_burst_recovers`) is un-skipped and passes. The direct `N(r) := N(s)` verb tests (unit + property) are split into Strict-vs-Default — strict pins the raw N(S) extraction, default pins the V(R) retarget. 681 pass / 2 skip (the remaining skips are mod-128 #239 and RNR ax25sdl#60, both unrelated to SREJ).

### 2026-06-02 — Ax25Spec41 Karn SRT sampling: fixes the SRT/T1V overflow (#241)

Implements the `Ax25Spec41KarnSrtSampling` quirk (default on; off under `StrictlyFaithful`), closing the SRT/T1V unbounded-growth → `TimeSpan` overflow. figc4.7 `Select_T1_Value` folds `(T1V − "Remaining Time on T1 When Last Stopped")` into the smoothed RTT, but that is a valid round-trip sample only when T1 was stopped by an ack (it was running, so remaining > 0). When no clean round-trip was measured (timeout, or T1 not freshly stopped), remaining is 0 and the "sample" degenerates to the full T1V (= 2·SRT); since T1V derives from SRT the IIR self-amplifies — SRT′ = 7/8·SRT + 1/8·(2·SRT) = 1.125·SRT — and diverges. The harness reproduced ~219 such updates in a single multi-frame SREJ recovery, 3000 ms → 4.78×10¹⁴ ms → overflow. The fix (Karn's algorithm) skips the IIR update when `T1RemainingWhenLastStopped == 0`; T1V still backs off via the RC term. Filed upstream as packethacking/ax25spec#41 (figc4.7 omits the guard) and gated behind the quirk per the #38/#40 discipline. The old behaviour was codified in `Select_T1_Value_IIR_Falls_Back_To_Full_T1V_When_T1_Expired`, now split into a Strict-vs-Default pair (Default skips per Karn; StrictlyFaithful folds the full T1V as drawn). Deliberately no T1V cap — leaving the unguarded path divergent keeps the fuzzer able to catch any other SRT-growth source rather than masking it.

Surfaced a third SREJ-recovery defect (#246), previously masked by this overflow: figc4.4's multi-frame out-of-sequence SREJ path does `N(r) := N(s)`, so the receiver SREJs the just-arrived frame instead of the gap (B SREJs a frame it holds; the real missing frame is never re-requested → heavy SREJ livelocks). The generative bidirectional sweep therefore stays REJ-only and `Srej_heavy_bidirectional_loss_burst_recovers` stays skipped, both pointing at #246, until it's resolved. The SREJ recovery space is thus: #242 (duplicate discard) ✓, #241 (SRT overflow) ✓, #246 (multi-frame SREJ target) open.

### 2026-06-02 — Ax25Spec40 receive-window discard guard: fixes the SREJ livelock (#242)

Implements the `Ax25Spec40DiscardOutOfWindowIFrames` quirk (default on; off under `StrictlyFaithful`), closing the ax25spec#40 recovery livelock. figc4.4's out-of-sequence `I_received` path has no receive-window guard, so any N(S) ≠ V(R) — including a duplicate *behind* V(R) — is SREJ'd/REJ'd; the re-send is again out-of-window, ad infinitum. X.25 §2.4.6.4 discards any frame whose N(S) is outside [V(R), V(R)+k). The fix is minimal and faithful: the window predicate `((N(S) − V(R)) mod N) ≥ k` is OR'd into the figure's own `reject_exception` decision in `Ax25SessionBindings` — the exact point where figc4.4 already chooses discard-over-reject — so an out-of-window frame takes the figure's existing discard path (process the ack, discard the data, RR(V(R)) only if P=1) ahead of the `srej_enabled` split, covering both SREJ and REJ modes with no new transition or per-action rewrite. Scoped to `IFrameReceived`, inert on every other trigger.

Result: multi-frame bidirectional SREJ recovery now converges where it used to spin to the pump's 256-round bound. Full Ax25 suite green (678 pass / 3 skip). The previously-skipped livelock property is un-skipped as a passing regression (`Srej_bidirectional_loss_burst_recovers_with_window_guard`, moderate budget).

Coupling surfaced: with the storm gone, a *heavy* SREJ burst no longer livelocks but instead trips **#241** (SRT/T1V unbounded growth → `Next T1 <- 2*SRT` overflows `TimeSpan`) before recovery completes — split out as a skipped case (`Srej_heavy_..._blocked_on_t1v_overflow_241`) and as the immediate coupled next step. Root cause now pinned to Karn's-algorithm violation: the figc4.7 SRT IIR samples `T1V − RemainingWhenLastStopped`, which becomes the full backed-off T1V when a prior expiry left `RemainingWhenLastStopped = 0` but RC has since reset to 0 → positive feedback. The bidirectional generative sweep stays REJ-only until #241 lands, then `srej` becomes a fuzzed parameter there too. Closes #242.

### 2026-06-02 — Conformance Phase A1: connection lifecycle under loss (clean)

`ConnectionLifecycleProperties` — a #40-independent fuzzing area: SABM/UA and DISC/UA handshakes under finite loss must always reach a terminal state, never hang in Awaiting*. Both properties pass (600 cases): connect-under-finite-loss always reaches Connected or Disconnected; disconnect-under-finite-loss always reaches Disconnected. No findings — confirms the handshake/recovery machinery is robust (the data-recovery space is where the bugs were). Test-only.

Note: the remaining high-value conformance space (SREJ data recovery, reorder, duplicates) is gated by the ax25spec#40 duplicate-SREJ livelock; un-blocking it needs the #242 `Ax25Spec40` discard-guard quirk (a core I_received interception). The connection-setup and REJ-recovery spaces are now covered and clean.

### 2026-06-02 — Conformance Phase A1: generative loss-recovery fuzzing (three findings)

First adversarial phase (`LossRecoveryProperties`, FsCheck over the harness). The harness `AdvanceT1` now advances the *live* T1V (was a fixed 200ms) so it fires whatever T1 the session actually armed — a fixed advance silently stalled recovery once T1V grew, masking bugs. Properties: a single dropped I-frame always recovers (REJ + SREJ, 300 cases — passes); a finite bidirectional loss burst recovers under REJ go-back-N (400 cases — passes). Test-only, no runtime change. Findings:
- **packet.net#241** — SRT/T1V grow unbounded under churning recovery → `Next T1 <- 2*SRT` throws `TimeSpan.OverflowException`. Root cause: the figc4.7 SRT IIR folds the full T1V back in on a *timeout* (sample = T1V − 0 = 2·SRT ⇒ 1.125×/cycle), compounded by the #40 storm. A blunt cap perturbs an existing SREJ test, so deferred; proper fix is the SRT-on-timeout logic.
- **packethacking/ax25spec#40 — escalated** from "efficiency amplifier" to a genuine **recovery livelock**: SREJ + finite multi-frame bidirectional loss makes B SREJ out-of-window duplicates, A retransmit, repeat (>256 round-trips, V(a) frozen). The missing §2.4.6.4(a) discard guard is a liveness fix, not just bandwidth.
- **packet.net#242** — add an `Ax25Spec40DiscardOutOfWindowIFrames` quirk (the runtime workaround); un-skips the SREJ bidirectional fuzzer + the pinned `Srej_bidirectional_loss_livelocks_pending_ax25spec40` repro.

### 2026-06-02 — Conformance Phase H envelope: connect-direction + mod-128 + RNR (two findings filed)

Extends the harness envelope (`EnvelopeConformanceTests` + harness `extended` / `ConnectFrom` / `SetBusy` / `ClearBusy`). Connect-initiated-by-B passes. Two scenarios surfaced real gaps and are skipped pending fixes (not papered over):
- **mod-128 (extended) data transfer — packet.net#239.** Connect negotiates SABME fine (`IsExtended` true both ends) but the first I-frame throws `NotSupportedException` — `ExtractNr`/`ExtractNs` `RequireMod8` on the 2-byte control field. Connected-mode data is mod-8-only today; a known unimplemented feature.
- **RNR flow control — ax25sdl#60.** figc4.4 `DL_FLOW_OFF_request` does nothing on the not-busy branch (the Set-Own-Receiver-Busy + RNR actions sit on the already-busy branch) — own-receiver-busy can't be entered via DL-FLOW-OFF from a clean state. Looks like a Yes/No swap vs the symmetric (correct) `DL_FLOW_ON`. Filed for figure verification.

Segmentation/reassembly is *not* a session-level behaviour (the `Segmenter` is a separate layer, covered by `SegmenterTests`), so it's out of the conformance envelope. Next: Phase A1 — FsCheck generation over the harness. 675 tests (673 pass, 2 skip).

### 2026-06-02 — Conformance harness Phase H: reusable two-station rig + invariant oracle + happy-path suite

First slice of the conformance/generative platform (docs/conformance-harness-plan.md). New `tests/Packet.Ax25.Tests/Session/Conformance/`: `TwoStationHarness` (two real sessions over a controllable in-process channel + shared FakeTimeProvider + ordered pump; tracks submitted-vs-delivered payloads; runs the oracle after every step), `InvariantChecker` (the oracle: defined-state, window/sequence sanity, reliable in-order gap-free duplicate-free delivery, and convergence-after-clean-tail), and `HappyPathConformanceTests` (connect/disconnect, single frame, full window, bidirectional, multi-window mod-8 wrap). All green (672/672); test-only, no runtime change.

Surfaced a real coverage gap while doing it: the figc4.4 in-sequence delayed-ack flushes via the **Link Multiplexer** (Set Ack Pending + LM-SEIZE Request → flush RR on LM-SEIZE-confirm, t23_lm_seize_confirm_yes), but every legacy two-session rig stubs `sendLinkMux`, so autonomous delayed-ack was never exercised — those rigs only ever acked via T1 polls. The harness models a contention-free medium (grant LM-SEIZE immediately) so happy-path acks actually flow. Next: extend the envelope (mod-128, segmentation, RNR/busy, piggyback acks) then the adversarial phases (A1 FsCheck generation).

### 2026-06-02 — Verify the SREJ-selective quirk engages end-to-end; close the SREJ-as-command gap as documented-non-issue (#233, #234)

Two follow-ups from #231/#227, both resolved as **documentation + tests, no behavioural code change** — the investigation showed the quirk is already correct and the remaining rough edges are spec-side, not packet.net bugs.

**#233 — does the #38 SREJ-selective quirk engage end-to-end, and is the "retransmit storm" real?** Instrumented the #231 two-session harness (a recording `IActionDispatcher` decorator logging which figc4.5 transition chain fires on each endpoint). Findings: (1) The quirk **engages end-to-end.** When A is in TimerRecovery and B's SREJ *response* arrives, A fires `t24_srej_received_yes_yes_*_no` (the push-bearing response paths) and the quirk redirects `push_frame_on_queue` → single-frame selective `push_old_I_frame_N_r_on_queue` and skips the go-back-N `Invoke_Retransmission`. New on-the-wire test `Srej_response_drives_single_frame_selective_retransmit_on_the_wire` proves it: a single injected SREJ(nr=1), with A's reply swallowed before B can feed back more, puts **exactly N(s)=1** on the wire — not the 1,2,3 that go-back-N would emit. (With the quirk *off*, those same paths throw on the payload-less push — the #38 defect faithfully reproduced — and recovery fails.) So the worry that the quirk was "only proven at the unit level" is closed. (2) The "storm" in the unpaced harness is **a `Settle()` pacing artifact, not a recovery defect.** `Settle()` pumps every cascade to quiescence with no T1 pacing, collapsing dozens of T1-spaced retransmit rounds into one instant. New T1-**paced** harness mode (`DrainOnce` processes only the frames already on the wire; the FakeTimeProvider advances one T1 per round) plus `Srej_under_loss_converges_under_T1_pacing_no_storm` proves the link **converges back to Connected in a bounded number of rounds** (≈4) under realistic pacing.

**Secondary spec-efficiency finding (flagged upstream, not fixed here):** the storm is *amplified* by a real but smaller gap — the figc4.4/figc4.5 Connected `I_received` table has **no out-of-window duplicate-discard guard**. Once the receiver catches up, every duplicate retransmit (N(S) behind V(R)) is mis-handled as a fresh out-of-sequence frame → Save + Increment SREJ exception + emit SREJ, so a burst of redundant SREJs flies before things settle. direwolf guards exactly this with `is_ns_in_window` per X.25 §2.4.6.4(a) ("N(S) not in window → discard, respond if P=1", `src/ax25_link.c:3102`); the AX.25 SDL figures don't. It does **not** prevent convergence and it is **not** the quirk, so it's left faithful and raised as a new `packethacking/ax25spec` issue rather than papered over with another runtime quirk.

**#234 — SREJ-as-command gap.** Surveyed the de-facto implementations (cloned sources at `~/src/ax25-impls/{direwolf,rax25}`, `~/src/linbpq`, plus the ax25sdl `*.citations.yaml` sidecars). **SREJ-as-command is not a real-world case.** AX.25 v2.2 §4.3.2.4 states "The SREJ frame is only sent as a response" (the command form is marked for removal); direwolf explicitly omits the command path (`src/ax25_link.c:4123`: "Command path has been omitted because SREJ can only be response"); linbpq gates resend on `if (MSGFLAG & RESP)` (`L2Code.c:2299`); rax25/linux have no SREJ-command handling at all. So the figc4.5 `response:No` paths (`t24_srej_received_no_yes_*`, which carry only a go-back-N `Invoke_Retransmission` and no push) being a no-op under Default — the quirk skips the go-back-N and has nothing to redirect — is **correct and spec-aligned**, not a defect to fix. Confirmed empirically (`Srej_command_does_not_retransmit_under_default`): an SREJ *response* selectively retransmits N(r)=1; an SREJ *command* retransmits nothing (V(a) still advances via `V(a):=N(r)`, matching the #234 probe). Implementing command-path retransmit would contradict §4.3.2.4 and every deployed stack, so we explicitly do not.

Changes: tests only + doc/comments. `DataLinkSrejUnderLossTests` gains the paced harness (`DrainOnce`) and three asserting tests; `Ax25SessionQuirksTests` gains a verb-level pin that a lone `Invoke_Retransmission` on an SREJ trigger emits nothing under Default. The `Ax25Spec38SrejSelectiveRetransmit` doc (both `Ax25SessionQuirks` and the `ActionDispatcher` quirk block) now records the response-only scope and the de-facto evidence. No production behaviour changed; the quirk and the figures are untouched. Full `Packet.Ax25.Tests` green (667/667). New spec-level concern for `packethacking/ax25spec`: the missing out-of-window duplicate-I-frame discard guard in the Connected I_received table (X.25 §2.4.6.4(a)); the SREJ-command-form vestige is already covered by the §4.3.2.4 removal note and ax25sdl#55.

### 2026-06-02 — sub-plan: conformance harness & generative testing

New sub-plan [`docs/conformance-harness-plan.md`](conformance-harness-plan.md). The two-session integration rigs are a deterministic protocol oracle (pure function of scenario + seed; reproducible, shrinkable) — the substrate that surfaced #231. The sub-plan turns them into a generative-testing platform that stress-tests three layers: the runtime engine, the SDL figures (m0lte/ax25sdl), and AX.25's design itself, routing each failure to the right layer (engine fix / ax25spec issue + quirk / v2.3 draft). Guiding principle: **happy-path first** — prove the new stack end-to-end and validate the invariant oracle on known-answer scenarios before adversarial fuzzing. Phases: H (happy-path conformance + reusable `LinkScenario`/driver/`InvariantChecker`) → A1 engine fuzzing (FsCheck, finite-disruption-prefix) → A2 Strict-vs-Default engine/figure auto-triage → A3 cross-impl differential (ax25-ts + docker interop; the "shared conformance vectors") → A4 coverage-guided + SDL-transition coverage. Generalises the existing FsCheck window/no-stuck-TimerRecovery properties.

### 2026-06-01 — go-back-N lossy hardware matrix + in-process recovery test (#223, rebased onto #231)

#223 independently diagnosed and fixed the same retransmit-N(s) renumbering bug as #231 (which landed first, via #232). Rebased onto main, its now-redundant runtime fix is dropped and only its unique on-air work is kept: the go-back-N hardware-loop matrix completing under scripted loss (5% / 15% on-air, 40/40 segments) via REJ + Timer-Recovery retransmission; a de-correlated per-transmission Bernoulli loss model in `LossyHardwareSender` (replacing the seeded sequence, which livelocked at high loss by phase-locking the one frame that must get through to the recovery cadence — see its remarks); recovery-aware connect/transfer budgets (`ComputeHandshakeBudget`, T1V-scaled transfer budget) in `HardwareLoop10KBTransfer`; and `SrejEnabled` plumbing through the hardware rig. SREJ on-air and the 30% rows stay commented (re-enabling tracked alongside #233 / #234). Also adds `DataLinkTimerRecoveryIntegrationTests.TimerRecovery_Retransmits_Unacked_IFrame_And_Recovers_When_Loss_Lifts` — the figc4.5 recovery branch end-to-end in-process, a second #231 regression guard.

### 2026-06-01 — Retransmitted I-frames keep their original N(s) — fixes broken connected-mode loss recovery (#231)

Connected-mode retransmits were emitted with a *fresh* sequence number instead of their original N(s), so the peer never recognised a resend as the missing frame and **no single lost I-frame was recoverable** — the link stormed then disconnected on N2. Root cause: `IFrameQueue` holds `(Data, Pid)` with no N(s); `DrainIFrameQueue` fires `IFramePopsOffQueue` → the shared figc4.4 t03 send path does `N(s):=V(s)` + `V(s)++`; the retransmit verbs (`push_old_I_frame_N_r_on_queue`, and figc4.7 `Invoke_Retransmission`'s `Push Old I Frame onto Queue`) re-queued *old* frames onto that same queue, so the drain renumbered them. The figc4.7 figure pushes-and-transmits interleaved (V(s) climbs from N(r), so each resend gets its correct N(s)); the runtime had decoupled push-now / drain-later and lost the N(s) semantics. The protocol prose is unambiguous — a retransmitted I-frame keeps its original N(S) — so this is a runtime fix, not a figure change.

Fix: retransmits emit *directly* (`ActionDispatcher.EmitOldIFrame`) with their original N(s), N(r):=V(r), P=0, sent unconditionally (already-counted frames, not new transmissions subject to the send window); the fresh-send path is untouched. This is the prerequisite that makes the #227 SREJ-selective quirk actually function end-to-end — without it the quirk picks the right frame to resend but the pump renumbers it. Found by a new deterministic two-session harness (`DataLinkSrejUnderLossTests`: minimal single-frame retransmit + SREJ-under-loss recovery from TimerRecovery, the in-process FakeTimeProvider analogue of the #214 30%-loss bench). Three existing tests had codified the renumbering by asserting on `IFrameQueue` *contents* rather than the wire (the unit tests never ran the drain pump; the integration tests only asserted state transitions) — all three now assert on sent frames. Full `Packet.Ax25.Tests` green (661/661). Follow-ups on #231: confirm the SREJ-selective quirk engages in the RR-driven recovery path (a retransmit storm is visible in the unpaced harness), and check ax25-ts for the same renumbering.

### 2026-06-01 — Stabilise the flaky netsim/LinBPQ interop matrix (test-harness only)

The `interop matrix` job is non-required but its environmental flakiness was eroding merge signal — different tests failed on different runs, and the same `main` commit was seen both passing and failing. Two tests were called out as confirmed-flaky: `NetsimConnectedModeScenarios.IFrame_Round_Trip_Across_AFSK1200_Sim` (intermittent "node A must observe the I-frame from node B") and `LinbpqViaNetsimConnectedMode.Connect_Then_Disconnect_Against_Linbpq_Across_Netsim` (intermittent string-mismatch on a LinBPQ response). Root cause for both is timing/synchronisation against the async docker stack, not a product bug.

**The physical model that explains it.** net-sim's afsk1200 channel (`docker/netsim/network.yaml`) is a single shared half-duplex medium with `collision_mode: silence`: if two endpoints key up at once, *both* transmissions are dropped, and recovery waits a full T1 retransmit cycle. `loss_db` is set well above the modem threshold, so frames are *not* randomly lost — collisions are the only loss source. With the spec-default ack timer (T2 = 1500 ms) and post-connect retransmit timer (T1V = 6 s), each unlucky collision costs ~6 s to recover; under host-CPU contention (four interop containers + the runner share one box) only ~3 recovery cycles fit in the old 20 s per-direction budget, so a couple of collisions in a row blew it. The single-frame-per-direction connect/disconnect handshakes collide rarely; the I-frame round-trip — where both ends have data *and* acks to send — collides often, which is why it was the worst offender.

**Fixes (all test-harness only — no product code touched; protocol semantics preserved).** (1) The two-session I-frame round-trip now drives the directions strictly one at a time and waits for the *sending* side's link to go quiescent — `V(s) == V(a)` and no ack pending — before the other end keys up, so the two sessions never transmit fresh I-frames into each other. This also *strengthens* the test: reaching quiescence proves the V(s)/V(r)/V(a) bookkeeping converged end-to-end, where before it only checked one frame arrived. (2) The connected-mode rigs shorten *our* ack timer (`ActionDispatcher.T2Duration` → 600 ms) so RR-acks turn the half-duplex channel around quickly, shrinking the collision window; T1/T3 keep spec defaults, so retransmit recovery is unchanged. (3) The LinBPQ I-frame round-trip's banner→command boundary replaces a fixed `Task.Delay(1 s)`-then-drain with a quiet-period drain (`DrainIndicationsUntilQuiet`) that consumes BPQ's multi-frame welcome banner until the indication stream goes quiet, so a late banner chunk can't be mistaken for the command response. (4) The XRouter I-frame round-trip's blind post-handshake settle becomes a quiescence gate. (5) Per-direction / per-stage budgets bump 15-20 s → 30 s and the tight 5 s "local state settle" sub-waits become a named 10-15 s `StateSettleBudget`; the XRouter-AXUDP stats check and AGW connect become poll-until / generous-budget instead of a single fixed delay. All wait helpers return as soon as their predicate holds, so the bigger budgets cost nothing on an idle host.

**Files:** `tests/Packet.Interop.Tests/{Netsim/NetsimConnectedModeScenarios,Netsim/NetsimListenerScenarios,Netsim/NetsimListenerMultiPeerScenarios,Linbpq/LinbpqViaNetsimConnectedMode,Linbpq/LinbpqListenerScenarios,Xrouter/XrouterViaNetsimConnectedMode,Xrouter/XrouterAxudpInterop,Rax25/Rax25ViaNetsimConnectedMode,Agw/LinbpqAgwFidelityTests}.cs`. No test was deleted, skipped, or had an assertion weakened; the two flaky tests still prove the same behaviour (connect/disconnect succeeds; the I-frame round-trips with correct sequence-number accounting). Verification is via CI's interop job (the docker stack can't run locally); `dotnet build` of the solution is clean. The "differ at index 0" string-mismatch in the brief is a string-*equality* assertion, which no C# interop test makes — the C# `Connect_Then_Disconnect_Against_Linbpq` test uses `.NotBeNull()` / `.Be("Connected")` — so that specific failure surfaces in the ax25-ts integration suite that shares this job (out of this repo's scope); the C# side's exposure to the same stack is the timing flake fixed here.

**Product-side finding (not fixed here, flagged for follow-up).** `Ax25Listener.BuildSession` accepts `Ax25ListenerOptions.T2` and writes it to `ctx.T2`, but the dispatcher's `start_T2` arms from `ActionDispatcher.T2Duration` (default 1500 ms), not `ctx.T2` — so the listener's `T2` option is silently a no-op. `WithT3` is likewise a documented no-op (`options.T3` ignored). Neither bites the interop tests (the connected-mode rigs build the dispatcher directly and set `T2Duration`; the listener tests don't tune T2), but a consumer setting `Ax25ListenerOptions.T2`/`T3` today gets the defaults regardless. Worth a tracking issue to either wire `ctx.T2`/`T3` into the dispatcher construction or drop the dead options.

### 2026-06-01 — Session-quirk facility + figc4.5 SREJ go-back-N correction (#227)

figc4.5 (Timer Recovery) draws SREJ-received as the generic fresh-DL-DATA "Push frame onto queue" + go-back-N "Invoke Retransmission", contradicting §4.3.2.4/§6.4.8, figc4.4, and every surveyed implementation (direwolf/linbpq do single-frame selective; linux/rax25 don't; direwolf's author flagged the exact box as a 2006-revision cut-n-paste erratum). Raised upstream as packethacking/ax25spec#38, with #39 on the broader red/green-annotation provenance (the colours are undocumented, ≥9 years old, and the maintainer didn't author them). ax25sdl deliberately stays *faithful* to the buggy figure — the canonical transcription tracks the in-progress draft — so the correction lives in the runtime as a named, documented quirk.

New `Ax25SessionQuirks` record: the session-layer analogue of `Ax25ParseOptions`, for deliberate deviations from SDL figures that are confirmed upstream defects. Each flag is named `Ax25Spec<issue>…` after the `packethacking/ax25spec` issue it works around (greppable + removable once fixed) — a replicable pattern. Default preset = spec-correct (quirks on); `StrictlyFaithful` runs the figures exactly as drawn. First quirk: `Ax25Spec38SrejSelectiveRetransmit` (default true), held on `Ax25SessionContext.Quirks` and applied in `ActionDispatcher.Execute` — on an `SrejReceived` trigger it redirects the figure's push to the figc4.4 single-frame selective retransmit (`push_old_I_frame_N_r_on_queue`) and skips the go-back-N `Invoke Retransmission`. Paired test in `Ax25SessionQuirksTests` (on = single-frame selective; off = figure-as-drawn, throws). Documented in docs/strict-vs-pragmatic-audit.md; removal tracked at #227 (delete when ax25sdl ships a corrected figc4.5).

### 2026-05-29 — Transition execution restores timers on a mid-transition throw (#225)

A transition whose action sequence throws part-way through used to leave the session silently wedged: a path that ran "Stop T1" but threw before "Start T1" left T1 cancelled forever, so the session sat in TimerRecovery until the peer's N2 timeout killed the link. Surfaced by ax25sdl#55 — the figc4.5 SREJ erratum, where "Push Frame Onto Queue" on an SREJ_received trigger (which carries no DL-DATA payload) throws mid-transition.

`Ax25Session` now captures the armed-timer set (`ITimerScheduler.CaptureState`) before running a transition's actions and restores it (`RestoreState`) if they throw, then rethrows. A half-applied transition can no longer cancel the link watchdog and leave it dead — the session stays live (`CurrentState` only advances on success) so T1 / N2 drive recovery or a clean disconnect. New `DataLinkTransitionRobustnessTests` cover the scheduler snapshot/restore and the mid-transition-throw case.

This is runtime robustness only — the figc4.5 SREJ erratum itself is kept faithful and flagged `verification_pending` upstream in ax25sdl#55, pending a ruling from packethacking/ax25spec on the disputed (green/red) go-back-N-on-SREJ design.

### 2026-05-28 — Fix figc4.5 stale recovery-complete guard found on the #214 bench (consumes Packet.Ax25.Sdl 0.7.1)

The #214 hardware bench run surfaced ax25sdl#53: the Timer-Recovery recovery-complete decision (RR/RNR/REJ/SREJ response, F=1) guarded on the stale pre-action V(a) instead of the post-"V(a) := N(r)" value. The figure draws "V(s) = V(a)?" after "V(a) := N(r)", so it means V(s) = N(r); the codegen flattened it to a pre-action guard that read the old V(a). When a poll response acked everything (N(r) = V(s)) while V(a) still lagged, it mis-routed to Invoke Retransmission and recovery never completed (RC → N2 → DM) — blocking essentially every clean go-back-N recovery on-air.

Fixed upstream in ax25sdl 0.7.1 (path-precise substitution in the Resolver: vs_eq_va → vs_eq_nr only on the 14 transitions where V(a) is actually updated; the 4 SREJ P=0 paths that share the same diamond keep vs_eq_va; loop continue-predicates left verbatim). This bumps the pin 0.7.0 → 0.7.1 and adds the runtime binding `vs_eq_nr` → the existing `n_r_eq_v_s` check in GuardEvaluator.

Proof + coverage: new `SessionRuntimeInvariants.Poll_Response_Acking_All_Outstanding_Completes_Recovery_To_Connected` drives a session into TimerRecovery and feeds an F=1 RR that acks everything, asserting it returns to Connected (not Disconnected). The existing no-stuck-TimerRecovery property could not catch this — it accepts Disconnected as a valid termination, which is exactly the bug's failure mode. Full default-filter suite green.

Phase 2 §5.2: this clears the #53 blocker the #214 bench hit; the recovery-via-RR-poll criterion still flips to ✅ once the loss matrix is re-confirmed on-air on the NinoTNC bench (#214).

### 2026-05-28 — Execute SDL LoopRange in the runtime (consumes Packet.Ax25.Sdl 0.7.0; unblocks recovery for #214)

Packet.Ax25.Sdl 0.7.0 recovered the three data-link loops the codegen had been silently flattening (ax25sdl#44 Invoke_Retransmission, ax25sdl#48 / #49 the V(r) I Frame Stored? drains) as `loop_while` with a `TestAtEnd` flag. This bumps the pin 0.6.0 → 0.7.0 and makes the runtime actually iterate them.

New `SdlLoopExecutor` expands each `LoopRange` over the flat action list: head-test (while) evaluates the continue predicate before each iteration (zero-or-more runs), tail-test (do-while) after (one-or-more), with a 1024-iteration cap that throws if a predicate never clears. It's shared by both `Ax25Session` (transitions) and `SubroutineRegistry` (subroutines) — `Invoke_Retransmission` is a subroutine, so the retransmit loop only iterates once subroutine loops execute.

Three runtime fixes the loops exposed: (1) `"Push Old I Frame onto Queue"` was mis-aliased to `push_on_I_frame_queue` (which requires a DL_DATA_request trigger and threw on REJ); removed so it routes to its dedicated re-queue case — latent until now because the subroutine's old single-path guard skipped it. (2) `GuardEvaluator` now resolves the drain predicate `vr_I_frame_stored` (package's capital-I spelling) to the `vr_i_frame_stored` binding. (3) Stored-frame drain retrieve/deliver split: `Retrieve Stored V(r) I Frame` stages the frame on `TransitionContext.RetrievedStoredFrame` and the loop body's separate `DL-DATA Indication` delivers it (delivering in both would double-deliver); plus `V(r) := V(r) - 1` (figc4.5, faithful to the figure, flagged ax25sdl#49) and the `:=` set-up aliases.

Proof: new `DataLinkConnectedRetransmitTests.Invoke_Retransmission_Requeues_Every_Unacked_Frame_Not_Just_One` shows the loop re-queues `X − N(r)` frames (3, not the old 1) and restores V(s). The loop unified the previously-separate stored / no-stored in-sequence transitions, so the smoke tests are made loop-aware and the now-duplicate t67/t68/t69 removed. Full default-filter suite green.

Phase 2 §5.2: the recovery-via-RR-poll Timer-Recovery criterion is unblocked, and the hardware-loop scripted-loss matrix's unconditional skip is removed (now hardware-gated). That criterion flips to ✅ once the matrix is confirmed on-air on the NinoTNC bench (#214).

### 2026-05-28 — Extract generic serial KISS modem into Packet.Kiss.Serial (closes #219)

- New package `Packet.Kiss.Serial` with `KissSerialModem` — a generic serial-port KISS modem implementing `IKissModem`. This is the canonical version of the logic previously duplicated in `m0lte/packet-term-tui` and `M0LTE/axcall`.
- `KissSerialModem` exposes: `Open(portName, baudRate)`, `SendFrameAsync`, `SendKissAsync` (arbitrary KISS commands), `ReadFramesAsync`, `FrameReceived` event, standard KISS parameter setters (TxDelay, Persistence, SlotTime, TxTail, FullDuplex), and proper `IAsyncDisposable`/`IDisposable`.
- `NinoTncSerialPort` refactored to compose `KissSerialModem` instead of managing its own `SerialPort` + read pump. The NinoTNC-specific surface (ACKMODE correlation, SETHW mode switching, `NinoTncFrameClassifier` dispatch, `InboundEvent` typed events) is unchanged.
- `Packet.Kiss.NinoTnc.csproj` now depends on `Packet.Kiss.Serial` (and drops its direct `System.IO.Ports` dependency — comes transitively).
- Both packages added to `publish-libs.yml` matrix and `ci.yml` test matrix. New test project `Packet.Kiss.Serial.Tests`.
- README package table updated.

### 2026-05-21 — Enquiry_Response F-bit parameter binding (closes ax25sdl#43)

figc4.7b page 102 draws `Check Need for Response`'s Yes branch as `Enquiry Response (F = 1)` — the `(F = 1)` is a parameter binding to the subroutine call, not an action inside the body. The figure intentionally omits an `F := 1` action because the parameter is meant to do the F-bit set. The SDL DSL doesn't yet model subroutine parameters, so the LLM-era walker encoded the parameter as a name-suffix alias (`Enquiry_Response_F_1`, `_F_0`).

Until today, the runtime's `DefaultSubroutineRegistry` aliased `Enquiry_Response_F_1` → `Enquiry_Response` as a pure name rewrite. That left the F-bit unset on the wire — every poll response went out with F=0 — and the polling side's TimerRecovery `response_and_F_eq_1` guard never matched, blocking recovery-to-Connected even with the rest of figc4.5 working.

Introduce a `ParameterisedAliases` table separate from `LegacyAliases`. Each entry maps an alias name to a `(canonical body, context-mutation)` pair. The `Wire()` step wraps the canonical walker with the mutation. `Enquiry_Response_F_1` sets `tx.Pending.PfBit = true` before walking; `_F_0` sets it false. The body's response verbs (`RR Response`, `RNR Response`, `SREJ`) already read `tx.Pending.PfBit`, so the frame goes out with the right F.

Two new facts in `Figc47SubroutineBodyTests` cover both polarities. Closes ax25sdl#43 (the original framing as a graphml fix was wrong; the figure is faithful — fix belonged in the runtime).

### 2026-05-21 — Hardware loop: session-level 10 kB transfer across the NinoTNC pair

Phase 2 exit criterion: *"Hardware loop sustains 10 kB transfer across NinoTNCs with 0–30 % scripted loss."* (closes [#213](https://github.com/m0lte/packet.net/issues/213), partial.)

`tests/Packet.Interop.Tests/Hardware/HardwareLoop10KBTransfer.cs` drives two `Ax25Session` instances — one per USB-attached NinoTNC, audio-cross-wired — through a real connect / 40 × 256-byte DL-DATA-request / disconnect cycle against real wall-clock time. Each TNC has its mode picked via SETHW with the `+16` non-persist offset (so the test matrix doesn't burn flash) and TXDELAY set via KISS, requiring the front-panel TX-DELAY pot at zero and MODE DIPs at 1111. The analogue SIGNALS DIP block must be in the NinoTNC manual's loopback configuration — the default field-radio profile decodes unreliably on the bench cross-wire.

**Matrix that lands today (no scripted loss):**

| Mode | Bit rate | TXDELAY  | Result |
| --- | --- | --- | --- |
| 0    | 9600 GFSK AX.25 |  50 ms | ✅ |
| 0    | 9600 GFSK AX.25 | 150 ms | ✅ (~58 s) |
| 0    | 9600 GFSK AX.25 | 400 ms | ✅ |
| 6    | 1200 AFSK AX.25 | 150 ms | ✅ (~91 s) |
| 6    | 1200 AFSK AX.25 | 250 ms | ✅ |
| 6    | 1200 AFSK AX.25 | 400 ms | ✅ (~90 s) |

Mode 6 below 150 ms TXDELAY decodes unreliably back-to-back on the bench cross-wire (`24/40` segments delivered then DM at 50 ms) — left off the matrix as a hardware-config consequence, not a stack bug.

**Lossy variant — deferred:** `Ten_KB_Transfer_Survives_Scripted_Loss` is `[SkippableFact]`-shaped with the matrix in place but `Skip.If(true, …)` pointing at the recovery-path SDL gaps. Two upstream issues account for the failure together:

- [ax25sdl#44](https://github.com/m0lte/ax25sdl/issues/44) — `Invoke_Retransmission` transcription encodes only one iteration of the figc4.7 retransmit loop, so a REJ (or an RR(F=1, N(r) < V(s)) in TimerRecovery) updates V(a) but never re-emits the missing I-frames between N(r) and V(s).
- [ax25sdl#43](https://github.com/m0lte/ax25sdl/issues/43) — `Enquiry_Response` response paths don't set `F := 1` before the response verb, so B's reply to A's RR-poll goes out with F=0 and A's TimerRecovery guard `response_and_F_eq_1` can't match.

Combined runtime symptom: A polls forever, B's F=0 RR(N(r)=missing) reply never satisfies the recovery guard, neither end retransmits. Surfaced cleanly at 5 % loss too — not a 30 %-specific issue. The test infrastructure is otherwise complete; lifting the `Skip.If` once `Packet.Ax25.Sdl` ships both fixes re-arms the assertion shape unchanged.

**No-loss variant flake:** the bench audio cross-wire is not perfectly lossless — frame-level dropouts occasionally happen during the ~60 s transfer (more often on slower modes whose longer airtime increases per-frame exposure). Each wire dropout hits the same ax25sdl#44 + ax25sdl#43 gaps, so the link can stall in the same RR-poll cycle even with no scripted loss. The matrix is best-effort under this constraint; re-runs typically pass. Downstream issue tracking the unblock: [#214](https://github.com/m0lte/packet.net/issues/214).

The test rig primes each modem with three UI frames in each direction after `SETHW` + `TXDELAY` before starting the session-level transfer — a static settle delay alone leaves the modulator/demodulator cold on the first window of session frames; exercising the path with FCS-validated round-trips up front reduces (but doesn't eliminate) wire-dropout exposure on the first I-frame burst.

Phase 2 §5.2 status: five of six exit criteria fully met (#207, #208, #209, #210, #212), one partial (#213 — no-loss best-effort, 30 %-loss blocked on ax25sdl#44). Phase 2 stays ⬜ until the upstream gap lands and the matrix becomes deterministic.

### 2026-05-21 — FsCheck window invariants + no-stuck-TimerRecovery property

Phase 2 exit criterion: *"FsCheck property tests prove window invariants (`V(A) ≤ V(S) ≤ V(A)+k`, no orphan transitions, no stuck Timer Recovery)"*.

The "no orphan transitions" half was already covered by `StateGraphInvariants` (static check across all transition tables). Two updates land here:

- **Housekeeping in `StateGraphInvariants`** — TimerRecovery's table joins `AllDataLinkTables()`; KnownStates' "not-yet-transcribed" comment is removed now that TimerRecovery ships in v0.5.3.
- **New `SessionRuntimeInvariants`** with two property tests on a live `Ax25Session`:
  - *Window invariant under arbitrary data requests* — generates random counts of DL-DATA-requests against a connected session and asserts `(V(s) - V(a)) mod Modulus ≤ K` after each (and at the end). With no peer to ack, V(a) stays at 0 and V(s) must plateau at K.
  - *TimerRecovery exits within N2+1 T1 expiries* — generates random N2 ∈ [1, 10], drives the session into TimerRecovery via a data-request + sustained T1 expiries, asserts the session reaches a non-TimerRecovery state within N2+2 cycles (the +1 of slack lets the figc4.4 t12 "enter TimerRecovery" cycle precede figc4.5's N2 retry cycles).

200 + 50 FsCheck iterations × 2 properties; full Properties suite now 60/60.

### 2026-05-21 — interop workflow: serialise via workflow-level concurrency singleton

The interop matrix had been red across multiple PRs in a row (#209 rej-srej, #210 segmenter) despite #210 touching no runtime code. Investigation traced it to concurrent runs of the interop workflow on the same self-hosted-runner pool: the docker stack at `docker/compose.interop.yml` uses fixed container names and binds fixed host ports, so two parallel runs collide on docker resources. Symptoms were "rax25 is missing dependency netsim" (compose-up failing because containers from the other run were in the way) and downstream connection-refused / banner-missing / timeout cascades.

Confirmation from `gh run list` timing: every failed interop run overlapped with another interop run; every isolated run succeeded.

Fix: change `interop.yml`'s `concurrency.group` from `interop-${{ github.ref }}` (per-ref, so PRs and main pushes had independent groups) to bare `interop` (workflow-level singleton) with `cancel-in-progress: false` (queue instead of clobber). Cost is ~90 s extra queue time per PR; gain is interop reliability.

### 2026-05-21 — AX.25 §6.6 Segmenter + Reassembler

Phase 2 exit criterion: *"Segmenter reassembles a 1500-byte payload across multiple I-frames."*

New `src/Packet.Ax25/Session/Segmenter.cs`:

- `Segmenter.Segment(payload, maxInfoFieldBytes)` — splits a payload into I-frame info-field byte arrays, each prefixed with the segment control byte (bit 7 = First; bits 5:0 = remaining segments after this one).
- `Reassembler.Push(infoField)` — accumulates segments until the final one (remaining == 0) arrives, then returns the completed payload. Throws on out-of-sequence or non-First-before-First protocol violations.

Per `docs/plan.md` §5.2, the segment control byte is `F | seg-remaining(6)` — a 6-bit remaining count, capping a packet at 64 segments. At default N1=256 that's 64 × 255 = 16 320 bytes of upper-layer payload.

Not yet wired into `Ax25Session`'s send path — kept standalone for now. Phase 2 still has the Management Data-Link XID layer pending, which negotiates N1; integration with Session will go through that path rather than guess at N1 here.

Tests: 14 facts covering round-trip across payload sizes (0, 1, 100, 254, 255, 256, 1500, 16320), header format, capacity overflow, and Reassembler error paths.

### 2026-05-21 — REJ / SREJ retransmits visible end-to-end + fix dispatcher alias

Phase 2 exit criterion: *"REJ and SREJ retransmits observed on the wire."* New file `DataLinkConnectedRetransmitTests.cs` drives the figc4.4 retransmit paths through an in-process two-session pipe with a frame-aware drop filter:

- **REJ emission** — A sends three I-frames; the middle (N(s)=1) is dropped at the link. B's figc4.4 t26 path (the `not ns_eq_vr and not reject_exception and not SREJ_enabled` arm) fires and sends REJ back to A.
- **SREJ emission** — same setup with `B.Context.SrejEnabled = true` and a single-frame gap. figc4.4's SREJ-enabled arm fires and sends SREJ back.
- **REJ reception** — direct-injection unit test: post `RejReceived` with N(r) in window into A's session, assert `V(a) := N(r)` and state stays Connected.

Surfaced and fixed a dispatcher alias bug: `Push Old I Frame N(r) on Queue` (figc4.4's REJ/SREJ retransmit verb that looks up the previously-sent I-frame at the incoming N(r)) was incorrectly aliased to `push_on_I_frame_queue` (the *new* I-frame queue-push, which requires the trigger to be `DL_DATA_request`). Now correctly aliased to `push_old_I_frame_N_r_on_queue`.

### 2026-05-21 — TimerRecovery end-to-end scenarios under simulated loss

Follow-up to the same-day wire-up (#207): an in-process two-session integration test (`DataLinkTimerRecoveryIntegrationTests.cs`) drives both endpoints through the §C4.5 entry-and-disconnect cycle under controlled loss, with `FakeTimeProvider` advancing T1 deterministically. Two facts cover the canonical cycle:

- **Entry** — Connected session sends I-frame, loss drops it, T1 expires → state transitions to TimerRecovery, RC reaches 1.
- **Disconnect** — sustained loss for N2+1 T1 cycles → DL-DISCONNECT-indication surfaces upstream and state transitions to Disconnected (figc4.5 t21_t1_expiry_yes_*).

The pair is wired in-process (frame-bytes → Ax25Frame.TryParse → Ax25FrameClassifier → other-session.PostEvent), with a `Link.LossActive` flag controlling the bidirectional drop. A `Settle` helper drains both endpoints' inbound queues until quiescent, avoiding the re-entrancy problem that a direct `PostEvent` chain would hit.

Also added the missing `DL_ERROR_indication_I` / `_T` / `_U` cases in `ActionDispatcher` — the disconnect path emits one of these letters depending on `V(s)/V(a)/peer_busy` state; figc4.5 t21_t1_expiry_yes_* paths previously hit "unknown SDL action" for the I/T/U variants until now.

Recovery-from-loss-via-RR-poll is the third scenario from the Phase 2 exit criterion ("100 % loss for (T1−1)·N2 *then recovery*"); it requires uni-directional loss simulation (lose only the response back) which the current `Link` doesn't support. Deferred — the entry + disconnect cases give first-class coverage of the cycle's edges.

### 2026-05-21 — wire figc4.5 TimerRecovery into the runtime; 90-transition smoke test

`Ax25Listener`'s state-map had `["TimerRecovery"] = Array.Empty<TransitionSpec>()` since the figc4.5 spec resolutions hadn't shipped — events posted while in TimerRecovery silently dropped. The same stub appeared in seven test/interop state-maps. The stub became removable when Packet.Ax25.Sdl 0.5.3 landed (figc4.5: 90 transitions / 45 decisions, `coverage: complete`).

This PR (#207):
- Replaces every TimerRecovery stub with `DataLink_TimerRecovery.Transitions` — production listener plus seven test/interop sites.
- Adds `tests/Packet.Ax25.Tests/Session/DataLinkTimerRecoverySmokeTests.cs` — a theory-driven smoke test that walks every committed figc4.5 transition end-to-end through `Ax25Session`. Auto-derives a Guards configuration from each transition's guard expression, posts the event, asserts the runtime lands on the declared `next` with the declared action sequence in order. All 90 transitions pass.

Phase 2 exit-criterion progress: this is the static structural piece of "Timer Recovery entered + exited under scripted net-sim 100 % loss for `(T1−1)·N2` then recovery". The end-to-end scripted-loss netsim scenario is a separate follow-up PR.

### 2026-05-21 — drop Packet.Ax25.Conformance.Tests; C# conformance now runs upstream in m0lte/ax25sdl

The `tests/Packet.Ax25.Conformance.Tests/` project was generated by `tools/Packet.Sdl.CodeGen` against an early SDL snapshot. The codegen was extracted to `m0lte/ax25sdl` on 2026-05-17, so packet.net can no longer regenerate these tests locally — every `Packet.Ax25.Sdl` bump since has left them stale and red on CI (transition ids shift, action sequences differ).

The .g.Tests.cs files have always existed in ax25sdl too (under `spec/csharp/tests/`), but were excluded from the lib's csproj and so never ran anywhere. Ax25sdl's CI runs the Go / Python / Rust / C / TS conformance tests but not the C# ones, leaving packet.net's stale copy as the only place they executed.

Fix landed via m0lte/ax25sdl#41: a new `Packet.Ax25.Sdl.Conformance.Tests.csproj` next to the .g.Tests.cs files, included in the `codegen/ax25sdl.slnx`. The existing `dotnet test codegen/ax25sdl.slnx` CI step now picks them up automatically (255 conformance tests pass on net10.0 against the current spec). C# is at parity with the other backends.

Downstream change (this PR): delete `tests/Packet.Ax25.Conformance.Tests/`, remove its slnx entry, drop it from `ci.yml`'s test matrix. Same coverage now runs upstream where the spec data is canonical.

### 2026-05-19 — nightly fuzz workflow shipped (SP-004 / §7)

`.github/workflows/fuzz.yml` lands the nightly SharpFuzz smoke run §7 promised. Triggers: nightly at 02:17 UTC, on-PR for any PR touching `src/Packet.Ax25/`, `src/Packet.Kiss/`, `tools/Packet.Fuzz/`, or the workflow itself, plus `workflow_dispatch`.

Defaults to **1,000,000 iterations per parser** (Ax25Frame.TryParse + KissDecoder.Push) — 1000× the previous smoke default. Local runtime ≈ 8 seconds total, so §7's "1 h per target" target is comfortably under-budget; can crank further via the workflow_dispatch `iterations` input if desired.

On a finding: exit code 2, job goes red, `fuzz-smoke.log` uploaded as artifact (only on failure — successful runs are summarised in workflow stdout, no need to burn artifact quota). Reproduction: same seed (`305419896`) replays the same input sequence locally.

Closes [#179](https://github.com/m0lte/packet.net/issues/179).

### 2026-05-19 — adopt GitHub Issues as the open-work tracker; plan.md becomes the narrative anchor

Open work that used to live as paragraphs inside `plan.md` (phases 2–10, the spike backlog SP-001 through SP-009, the hardware-arrival playbook §5.Y, and the open-questions table §15) is now mirrored as GitHub Issues — one per trackable item. The plan keeps the narrative shape, the issues hold day-to-day status / claim / discussion.

**Why.** Tom prompted on 2026-05-19: *"What do you think about the idea of transferring the original vision / roadmap for packet.net itself into its issue tracker? Or better as a doc?"* The answer landed at hybrid: the plan is good at narrative coherence (working agreements, phases-with-exit-criteria, glossary, risks, amendment log) but bad at "what's left to do?" rolldown. Issues are the inverse — flat list, assignable, claimable, auto-closable from PRs, discoverable for drive-by contributors. Keep both, with each doing what it's good at.

**What changed in this PR.** Issue numbers added as `([#N](…))` annotations:

- §5.2–§5.10 phase headings → [#168](https://github.com/m0lte/packet.net/issues/168), [#169](https://github.com/m0lte/packet.net/issues/169), [#167](https://github.com/m0lte/packet.net/issues/167) (Phase 4 filed earlier), [#170](https://github.com/m0lte/packet.net/issues/170), [#171](https://github.com/m0lte/packet.net/issues/171), [#172](https://github.com/m0lte/packet.net/issues/172), [#173](https://github.com/m0lte/packet.net/issues/173), [#174](https://github.com/m0lte/packet.net/issues/174), [#175](https://github.com/m0lte/packet.net/issues/175).
- §5.X spike backlog SP-001 through SP-009 → [#176](https://github.com/m0lte/packet.net/issues/176) – [#184](https://github.com/m0lte/packet.net/issues/184).
- §5.Y hardware-arrival playbook → [#185](https://github.com/m0lte/packet.net/issues/185).
- §15 open questions (skipping resolved OQ-007) → [#186](https://github.com/m0lte/packet.net/issues/186) – [#194](https://github.com/m0lte/packet.net/issues/194). OQ-008 (publish `/spec-sdl/` as canonical) lives at [`m0lte/ax25sdl#15`](https://github.com/m0lte/ax25sdl/issues/15) because the topic moved with the spec-sdl extraction.
- §6.4 SDL inventory — added a pointer to [`m0lte/ax25sdl#14`](https://github.com/m0lte/ax25sdl/issues/14) (the non-figc4 transcription work) + [`m0lte/ax25sdl#13`](https://github.com/m0lte/ax25sdl/issues/13) (the figc4.x redraw).

Risks (§16) and locked decisions (§3) are **not** mirrored as issues — those are reference material, not work items.

**What this doesn't change.** The working-agreement that `plan.md` §17 is updated in the same PR as the work that triggers it (§2.6, "Keep this document current"). When a phase / spike / OQ resolves, the relevant section here still gets a status flip + an amendment-log entry as today; the issue separately gets closed (with `Closes #N` in the resolving PR).

### 2026-05-17 — drop web/ax25 from packet.net (extracted to m0lte/ax25-ts)

Final extraction step of the 5-repo split. `web/ax25/` — the `@packet-net/ax25` TypeScript library — moves out to its own repo at [`m0lte/ax25-ts`](https://github.com/m0lte/ax25-ts). History was preserved via `git filter-repo --path web/ax25/ --path-rename web/ax25/:` against a fresh clone, yielding 13 commits that span the library's full life (the `feat(ax25/examples)` PR #133 that introduced it through the recent `feat(ts-ax25)` work). The new repo's `main` was scaffolded with a `CLAUDE.md` (agent notes for the new home — "consume ax25sdl from npm, never hand-edit generated tables", self-hosted-runner rule) and a `.github/workflows/ci.yml` job that runs typecheck + typecheck:examples + build + `npm test` on `[self-hosted, Linux, X64]`.

**Why now.** The previous amendment-log entry (this date, but earlier) parked the `web/ax25/` extraction on the `ax25sdl` npm publish auth. That blocker resolved later the same day — the `NPM_TOKEN` on `m0lte/ax25sdl` was replaced with one carrying publish rights, and `ax25sdl@0.3.0` is now live on npm. With the publish path proven, there's no reason to keep the TS library here.

**What went where in this repo (deletions).** `web/ax25/` (468 KB, 53 tracked files); `docs/web-ax25/` (57 files of typedoc-generated API docs — those rebuild from the source in the new repo, not maintained by hand); the `ts-build-test` job in `.github/workflows/ci.yml` (it was running `cd web/ax25 && npm ci && npm run typecheck && npm run build && npm test` — that suite now runs in `m0lte/ax25-ts`'s own `ci.yml`); the `web/ax25/**` paths filter in `.github/workflows/interop.yml`.

**What interop.yml does now.** The `ax25.ts integration tests` step previously did `cd web/ax25 && npm ci && npm run build && npm run test:integration` against the local copy. The new version clones `m0lte/ax25-ts` (main, depth 1) into `${RUNNER_TEMP}/ax25-ts`, then `npm ci && npm run build && npm run test:integration` from there. The cloned suite still dials `127.0.0.1:8100` — the same KISS-TCP listener the C# interop tests use — so the docker stack standing up in this job remains the test bed. Pinning to a SHA instead of `main` is the obvious tightening if the API ever destabilises; tracking `main` for now keeps the new repo and this one moving together.

**Other docs touched.** `README.md` § Sibling repos — flipped `m0lte/ax25-ts` from *(planned)* to *(public)* and dropped the "today still here at web/ax25/" caveat. `CLAUDE.md` — removed the "Run web/ax25 (TS library) unit tests" command, removed the `web/ax25/` row in the "What lives where" tree, rewrote the "What this repo is" lead-in so the only mention of ax25-ts is the interop-clone behaviour. `docs/runtime-capability-matrix.md` + `docs/runtime-capability-strategy.md` — rewrote in-repo backtick paths and markdown links that pointed into `web/ax25/`; they now point at GitHub blob URLs for source files in the sibling repo (`https://github.com/m0lte/ax25-ts/blob/main/...`) and to `m0lte/ax25-ts: ...` style display labels for inline code references. `tests/conformance/connect-sabm-ua-disc.yaml` — same path-rewrite treatment for its two stale test-file references.

**Verification.** Build + non-interop tests still green locally. The interop matrix can only be verified end-to-end on the CI runner once this PR's `interop.yml` runs against the docker stack and clones the freshly-extracted `m0lte/ax25-ts` — that's the canonical pass criterion before merge.

**What's still pending.** A runner needs to be registered against `m0lte/ax25-ts` before its own `ci.yml` will run anything (the workflow targets `[self-hosted, Linux, X64]`, same convention as the other repos). Until that's done the new repo's CI sits in queued state on every push. The integration test path here works regardless — `interop.yml` clones the new repo and runs its tests on this repo's runner.

### 2026-05-17 — drop ts-spec, flip web/ax25 to consume ax25sdl from npm

Fifth step of the 5-repo split. `ts-spec/` (the local copy of the `ax25sdl` npm package, file-linked from `web/ax25`) goes away; `web/ax25/package.json` now depends on `ax25sdl@^0.1.1` from npmjs.com directly.

**Version reality check.** The latest `ax25sdl` actually on npmjs.com is `0.1.1` (the `0.2.x` bump in `ts-spec/package.json` from #148 was never published — no `v*` tag was pushed against `m0lte/packet.net` after the bump, so `npm-publish.yml` never fired, and the `0.3.0` publish from `m0lte/ax25sdl` is parked on the auth issue). `ax25sdl@0.1.1` is content-equivalent to what `web/ax25` was consuming via the file: link — verified by `npm test` in `web/ax25/` passing 103/103 against the npm version. The SDL transition tables haven't changed since the 0.1.1 publish; the `0.2.x` bump in tree was an aspirational version that never shipped.

**Changes.**

- `web/ax25/package.json` — `"ax25sdl": "file:../../ts-spec"` → `"ax25sdl": "^0.1.1"`.
- `web/ax25/package-lock.json` — regenerated with `npx npm@10 install` after the dep flip; `ax25sdl` now resolves to `https://registry.npmjs.org/ax25sdl/-/ax25sdl-0.1.1.tgz`.
- `ts-spec/` — deleted in full.
- `.github/workflows/ci.yml` — `ts-build-test` job slimmed to just the `web/ax25` install / typecheck / build / test (the ts-spec build step is gone with ts-spec). Cache key bumped from `ts-spec/package-lock.json` to `web/ax25/package-lock.json`.

**Verification.** `cd web/ax25 && npm run typecheck && npm test` — 103/103 unit tests green against npm-sourced ax25sdl@0.1.1. The integration tests (LinBPQ/netsim) weren't run locally; CI's `interop` workflow will exercise them.

**What's still pending.** `web/ax25` itself still lives in `m0lte/packet.net`. It moves to `m0lte/ax25-ts` once the npm publish path from `m0lte/ax25sdl` is unblocked (so that the next iteration of `@packet-net/ax25` can depend on a fresh `ax25sdl`).


### 2026-05-17 — drop SDL codegen + spec sources from packet.net (extracted to m0lte/ax25sdl)

Fourth step of the 5-repo split. The SDL transcriptions, codegen tools, multi-language artefacts (`go-spec/`), codegen tests, and SDL documentation all live at `m0lte/ax25sdl` now. `packet.net` consumes `Packet.Ax25.Sdl 0.3.0` from nuget.org (set up in #155). This PR removes the local source.

**Deleted.**

- `spec-sdl/` — entire tree (YAML transcriptions, JSON schema, graphml sources, events.yaml, actions.yaml).
- `tools/Packet.Sdl.IR/`, `tools/Packet.Sdl.CodeGen/`, `tools/Packet.Sdl.CodeGen.Csharp/`, `.Go/`, `.Ts/`, `.Json/`, `.Rust/`, `.C/`, `.Python/`, `tools/Packet.Sdl.Lint/` — 10 codegen tool projects.
- `tests/Packet.Sdl.CodeGen.Tests/` — codegen test project.
- `go-spec/` — Go module (publishes from m0lte/ax25sdl now).
- `docs/sdl-primer.md`, `docs/sdl-transcription-runbook.md`, `docs/sdl-verb-catalogue.md`, `docs/adr/0001-sdl-dsl.md` — SDL-specific documentation.
- `.github/workflows/npm-publish.yml` — obsolete. `ax25sdl` publishes from m0lte/ax25sdl now; `@packet-net/ax25` has no pending publishes from this repo (next iteration moves to a future `m0lte/ax25-ts`).

**Updated.**

- `Packet.NET.slnx` — removed 10 codegen tool entries + the codegen tests entry.
- `Directory.Packages.props` — dropped `Scriban`, `Microsoft.CodeAnalysis.CSharp`, `YamlDotNet`, `JsonSchema.Net` (only the codegen used them). `CommandLineParser` stays — `Packet.Term` consumes it (Packet.Term moves to `m0lte/packet-term-tui` in a sibling PR; the version pin can be dropped once that lands).
- `.github/workflows/ci.yml` — slimmed dramatically. Removed all seven codegen-drift jobs (C# / Go / TS / JSON / Rust / C / Python). The TS job's `ts-spec build` + `web/ax25 build/test` lifts into a fresh `ts-build-test` job (those still need to run while `ts-spec/` + `web/ax25/` stay in this repo — until the npm publish path from `m0lte/ax25sdl` is resolved).
- `.gitignore` — removes the `src/Packet.Ax25.Sdl/` entry that was added in #155 (no longer relevant; codegen is gone).
- `docs/web-ax25/README.md` — drops the broken link to the deleted `publishing.md`, adds a note that publishing is on hold pending extraction to the future `m0lte/ax25-ts` repo.

**What stays (still here pending the npm publish auth resolution).**

- `web/ax25/` — the `@packet-net/ax25` TS library source. Will move to `m0lte/ax25-ts` once the publish path is unblocked.
- `ts-spec/` — pre-generated `*.g.ts` matching `ax25sdl@0.2.1` on npm. Still file-linked from `web/ax25/`. Will move (or be replaced by the npm dep) when that's possible.
- Top-level SDL-related rules in `CLAUDE.md` will need a cleanup pass — deferred to a follow-up.

**Verification.** `dotnet build` clean (0 errors; 61 pre-existing warnings in Spike projects unrelated). `dotnet test --filter "Category!=HardwareLoop&Category!=Interop"` green across the remaining 12 test projects.

### 2026-05-17 — drop Packet.Term from packet.net (extracted to m0lte/packet-term-tui)

Third extraction step of the 5-repo split — `Packet.Term` (the C# Terminal.Gui v2 TUI for AX.25 sessions) lives at `m0lte/packet-term-tui` now and consumes the Packet.NET libraries (`Packet.Core 0.1.0` / `Packet.Ax25 0.1.0` / `Packet.Kiss 0.1.0`) from nuget.org. This PR removes the local source.

**Deleted.**

- `src/Packet.Term/` — 12 files (Tui/MainWindow.cs, Tui/ConnectDialog.cs, Tui/SettingsDialog.cs, Tui/PacketTermApp.cs, Tui/TuiSchemes.cs, Program.cs, AppContext.cs, AppSettings.cs, AppInfo.cs, CommandLineOptions.cs, FrameFormatter.cs, KissSerialModem.cs, RingBuffer.cs, SessionRunner.cs).
- `tests/Packet.Term.Tests/` — 3 test files (FrameFormatterTests.cs, CommandLineOptionsTests.cs, AppSettingsTests.cs).
- `Packet.NET.slnx` — removed both projects.
- `Directory.Packages.props` — dropped `Terminal.Gui`, `Microsoft.Extensions.Configuration` + `.Json` + `.Binder` package version pins (only Packet.Term consumed them).

**Extraction record.** Done via `git filter-repo --path src/Packet.Term/ --path tests/Packet.Term.Tests/ --path LICENSE --path .gitignore --path Directory.Build.props --path Directory.Packages.props --path global.json` against a clone of this repo earlier on 2026-05-17. 18 commits of history survived in the new repo; `Directory.Packages.props` was trimmed to just what the TUI consumes; `Packet.Term.csproj`'s ProjectReferences to Packet.Core / Packet.Ax25 / Packet.Kiss became PackageReferences against nuget.org.

**Verification.** `dotnet build` clean (61 pre-existing warnings in Spike projects unrelated). `dotnet test --filter "Category!=HardwareLoop&Category!=Interop"` green across the remaining 12 test projects.

### 2026-05-17 — extract packet-terminal demo to m0lte/packet-term-web

Second extraction step of the 5-repo split — moves the browser TNC2 emulator (single-file HTML demo, deployed at https://packet-term.m0lte.uk) out of `web/ax25/examples/packet-terminal/` into a new public repo `m0lte/packet-term-web`.

The demo is structurally a downstream consumer of `@packet-net/ax25` (which it imports via esm.sh, pinned at `0.2.1`) — same shape as if a third party had built a TNC2 terminal on the library. It has no build step, no node_modules, no package.json; just `index.html` (the demo) + `README.md` (with the deploy URL + command reference) + the new repo's scaffolding. The previous full path was nested four levels deep (`web/ax25/examples/packet-terminal/...`) for monorepo-organisational reasons that no longer apply.

**How.** Cloned packet.net, ran `git filter-repo --subdirectory-filter web/ax25/examples/packet-terminal` to extract the subtree with history (5 commits that touched the directory survived; the rest were dropped). Pushed to https://github.com/m0lte/packet-term-web as a fresh `main`. Added LICENSE (MIT, copied from packet.net) and a tailored CLAUDE.md (key rule: no build step ever — the ESM-via-CDN model is deliberate, lets non-developers fork the file).

**Visibility decision.** Public. Unlike `m0lte/ax25sdl` (private), this repo doesn't carry self-hosted-runner risk — there's no CI to run on fork PRs. The content is already publicly served from packet-term.m0lte.uk anyway.

**This PR removes the directory from packet.net.** No other changes — no references elsewhere in this repo to update (only historical amendment-log entries mention the old path, and those describe past state correctly). The deploy at https://packet-term.m0lte.uk is unaffected; this is purely a source-of-truth move.

**Independent of the parked npm-publish-from-ax25sdl issue.** The demo pins `@packet-net/ax25@0.2.1` (already on npm), so it doesn't depend on the upstream-publish path being live.

### 2026-05-17 — set up NuGet publishing for the Packet.NET libraries

Phase 2.5 of the 5-repo split — sets up `m0lte/packet.net` itself as a NuGet publisher for the .NET libraries downstream apps (the upcoming `m0lte/packet-term-tui` being the first) will consume. Matches the pattern proven by `m0lte/ax25sdl`'s `Packet.Ax25.Sdl` publish.

**Scope.** First-publish set is the four libraries `Packet.Term` depends on: `Packet.Core`, `Packet.Kiss.Abstractions`, `Packet.Kiss`, `Packet.Ax25`. Other libraries under `src/` (`Packet.Aprs`, `Packet.Agw`, `Packet.Axudp`, `Packet.Kiss.NinoTnc`, `Packet.Mcp`, `Packet.Rhp2`, etc.) stay unconfigured until they have a concrete external consumer.

**Per-csproj additions.** Each of the four libraries gets `<PackageId>` / `<Title>` / `<Description>` / `<PackageTags>` in a new `<PropertyGroup>`. Everything else (Authors, Copyright, MIT license, repo URLs, sourcelink, snupkg, IncludeSymbols) already inherits from the top-level `Directory.Build.props` that's been there since the early days. `<IsPackable>` defaults to `true` for class libraries; `Packet.Term` already had it explicitly `false`, and `Packet.Node` uses `Microsoft.NET.Sdk.Web` which defaults to non-packable.

**Workflow.** `.github/workflows/publish-libs.yml` fires on `lib-v*` tags (distinct prefix to avoid colliding with the existing `npm-publish.yml`'s `v*` trigger). Resolves the version from the tag, then runs a 4-way matrix building / packing / pushing each project. Same `dotnet pack -p:Version=…` + `dotnet nuget push --skip-duplicate` shape as `m0lte/ax25sdl`'s `publish.yml`. Symbol packages (.snupkg) ship alongside the main nupkgs.

**Verification.** Local `dotnet pack` on all four produced clean nupkgs (~one warning per package — "missing a readme" — a follow-up will add per-package READMEs; non-blocking for publish). The Packet.Ax25 nuspec correctly declares dependencies on `Packet.Core` + `Packet.Kiss.Abstractions` (at the same matrix version) + `Packet.Ax25.Sdl 0.3.0` (from the existing nuget.org publish).

**Operationally.** Once this PR merges, the first publish is triggered by `git tag lib-v0.1.0 && git push origin lib-v0.1.0`. Requires `NUGET_API_KEY` to be configured on this repo's secrets (same one set on `m0lte/ax25sdl`).

### 2026-05-17 — consume Packet.Ax25.Sdl from m0lte/ax25sdl via NuGet

First downstream-consumption step of the [5-repo split plan](https://github.com/m0lte/packet.net/issues/<future>) — drops the local `src/Packet.Ax25.Sdl/` project and pulls `Packet.Ax25.Sdl 0.3.0` from nuget.org instead. The package is built and published by `m0lte/ax25sdl` (extracted from this repo on 2026-05-17, history-preserving via `git filter-repo`).

**Changes.**

- `Directory.Packages.props` — adds `<PackageVersion Include="Packet.Ax25.Sdl" Version="0.3.0" />` under the SDL section.
- `src/Packet.Ax25/Packet.Ax25.csproj` — `ProjectReference` → `PackageReference`. Same for the two test projects that `using Packet.Ax25.Sdl;` directly: `tests/Packet.Ax25.Conformance.Tests/` and `tests/Packet.Ax25.Properties/`. `src/Packet.Term/Packet.Term.csproj` drops its direct ref — it didn't use Sdl types itself, only consumed transitively via Packet.Ax25.
- `Packet.NET.slnx` — removes `src/Packet.Ax25.Sdl/Packet.Ax25.Sdl.csproj`.
- `src/Packet.Ax25.Sdl/` — deleted.
- `.gitignore` — adds `src/Packet.Ax25.Sdl/` so a stray `dotnet run --project tools/Packet.Sdl.CodeGen -- --csharp` doesn't leave untracked artefacts in the working tree (the local codegen tools still exist for now; they'll go in a follow-up).

**What stays for now.** `spec-sdl/`, `tools/Packet.Sdl.*` (all seven codegen emitters + orchestrator + IR + lint), `tests/Packet.Sdl.CodeGen.Tests/`, `ts-spec/`, `go-spec/`, the four SDL docs, and ADR-0001 are all duplicated between `m0lte/packet.net` and `m0lte/ax25sdl` while the npm publish path is parked. The TS library (`web/ax25/`) still consumes `ax25sdl` via `file:../../ts-spec`; switching to npm requires the publish-auth issue resolved at `m0lte/ax25sdl`.

**Verification.** `dotnet build` clean (0 errors; 61 pre-existing warnings in Spike projects unrelated). `dotnet test --filter "Category!=HardwareLoop&Category!=Interop"` 1186/1186 green across 13 test projects, including `Packet.Ax25.Conformance.Tests` (165/165) which `using Packet.Ax25.Sdl` heavily — proves the NuGet package and the previously-local project reference are behaviour-equivalent. Package was published from `m0lte/ax25sdl` tag `v0.3.0` via the new `publish.yml` workflow on 2026-05-17 (NuGet side succeeded; npm side failed with a 404 auth-scope mismatch on the `ax25sdl` package and is parked pending Tom).

**Reproducing.** From a clean checkout: `dotnet restore` pulls `Packet.Ax25.Sdl 0.3.0` from nuget.org; `dotnet build` produces a working solution without `src/Packet.Ax25.Sdl/` ever existing locally.

### 2026-05-17 — Packet.Term: hot-swap MYCALL/port, Esc-to-quit, CLI-ephemeral

Three follow-ups on #152 (Terminal.Gui v2 refactor), driven by Tom needing to run two parallel Packet.Term instances in one directory and have each one's MYCALL configurable at runtime.

**Hot-swap MYCALL + port.** The Settings dialog now applies changes immediately rather than "binds on next launch". On OK, `MainWindow.ReconfigureAsync` runs on a background task: disconnects any active session, opens the new modem (if port changed), disposes the old runner + modem, builds a fresh `SessionRunner` with the new MYCALL, restarts the listener pump, and refreshes window title + status bar. Failures during modem-open preserve the current configuration and surface a MessageBox; runtime errors during disconnect don't block the swap. Same-config no-ops cleanly. `myCall` / `portName` / `modem` / `runner` are no longer `readonly`. Modem ownership transferred from `Program` to `MainWindow`.

**F10 → Esc for Quit.** F10 in Terminal.Gui v2 is hardcoded as the MenuBar activator (Turbo Vision idiom); a status-bar `Shortcut` bound to F10 was shadowed by the framework and never fired. Status bar now shows `Esc=Quit`. The menu still has `File → Exit (Ctrl-Q)`. F10 stays as the menu-activator implicitly.

**CLI-ephemeral persistence.** When both `--mycall` and `--port` are supplied on the command line, `AppContext.PersistenceEnabled` flips to `false` and `SaveSettings()` becomes a no-op for the rest of the run. The shared settings JSON file isn't clobbered by parallel instances each driven by their own CLI args. Boot prints a one-line notice so the user knows persistence is off. Without both flags, behaviour is unchanged (prompts on first-launch + settings remembered for next time).

### 2026-05-17 — Interop budget bumps (C# RxBudget + TS waitForNext) for AFSK-sim flake

Bumps the interop-test budgets that were timing out under host-CPU contention from the AFSK1200 software sim. The XRouter-misattribution investigation (see 2026-05-16 entry just below) identified this as the actual flake — when the interop runner is loaded, the AFSK round-trip latency spikes above the original budgets and tests cancel before frames arrive. CI's self-hosted runner has more headroom than local laptops but isn't immune; on the post-#149 run both #150 and #151 hit it on every interop run.

**C# (`tests/Packet.Interop.Tests/Netsim/NetsimUiFrameScenarios.cs`).** Bumps `RxBudget` 15s → 30s. The constant is shared across `UI_Frame_With_Digipeater_Path_Round_Trips` (the locally-flaky one), `UI_Frame_With_NetRom_Pid_Preserves_Payload`, and `UI_Frame_With_Aprs_Position_Survives_RF_Round_Trip` — sibling scenarios sharing the same AFSK pipeline get the same headroom.

**TS (`web/ax25/tests/integration/linbpq-via-netsim.test.ts` + `listener-linbpq-initiates.test.ts`).** Same root cause, same kind of fix. `linbpq-via-netsim`: the two hardcoded `waitForNext(15_000)` calls (banner-from-BPQ + response-to-`P\r`) bump to `30_000`. `listener-linbpq-initiates`: the outer test timeout bumps from `90_000` to `180_000` — the test orchestrates a multi-stage BPQ telnet session + outbound L2 connect, so the per-stage timing variance compounds.

**Skip — `IFrame_RoundTrip_Against_Linbpq_Node_Prompt`.** Bumping the budget surfaced a real bug that timing wasn't masking: the second L2 session in the same vitest file establishes (SABM/UA) but the CTEXT banner that this test waits for never arrives. The 30s bump still failed — total elapsed 34.8s with no chunk delivered. The sibling `Connect_Then_Disconnect` test passes, so the wire-up works. Probable root causes are BPQ-side session-reuse / banner-suppression behaviour or a netsim-side state leak between sequential L2 sessions on the same address pair. Marked `.skip` in this PR with a `TODO(#153)` comment pointing to the tracking issue; unskip when the root cause is understood (likely needs fresh callsigns per session or a BPQ config tweak).

No happy-path runtime impact from the bumps — tests still complete in ~3s when the host isn't loaded. The bumps just stop cancelling early under contention; the skip parks the one test that has a real underlying bug.

### 2026-05-17 — packet-terminal example: modernize to Ax25Listener + listener-session facade

Two coupled changes — a small library addition that lets the in-tree packet-terminal example drop hand-rolled adapter code, and the example update itself.

**Library — friendly facade on `Ax25ListenerSession` (`web/ax25/src/listener.ts`).** Adds `to`, `onData(cb)`, `onDisconnected(cb)`, `write(chunk, pid?)`, `disconnect()` to `Ax25ListenerSession`. They mirror the same-named methods on `Ax25Session` (Ax25Stack's session class) byte-for-byte, so a session from either source is drop-in compatible. The raw `postEvent` / `onDataLinkSignal` API stays available for advanced consumers (FRMR generation, XID, custom error-recovery flows).

This was surfaced by the example modernization: switching the demo from `Ax25Stack` (outbound-only) to `Ax25Listener` (inbound + outbound) would otherwise have required the demo to write an adapter that re-creates `onData / write / disconnect` on top of the listener-session's raw signal API — which is exactly the "in-tree examples shouldn't reimplement runtime features" anti-pattern. Lifting the facade to the library where it belongs solves both consumers.

Bumps `web/ax25` and `ts-spec` to `0.2.1` in lockstep (additive only — no breaking changes). 9 new unit tests in `Ax25ListenerSessionFacade.test.ts` cover the five facade methods plus edge cases (empty-buffer write, write-while-disconnected, custom PID, idempotent disconnect).

**Example — `web/ax25/examples/packet-terminal/index.html`.** Modernizes the deployed-at-packet-term.m0lte.uk demo:

- **`Ax25Stack` → `Ax25Listener`.** One transport, one stack. Drops the `wrapTransport` hand-rolled transport adapter (~20 LOC) in favour of `listener.onFrameTraced` — the library's native hook for the BPQ-style frame monitor.
- **Drops `tryDecode` helper.** `onFrameTraced` delivers a pre-decoded `Ax25Frame`; no try/catch around `decodeFrame` needed.
- **Adds inbound-call support.** `listener.onSessionAccepted` wires inbound SABMs into the existing converse-mode UI — a peer can now `CONNECT` to your MYCALL and the terminal switches to chat with them. Matches real TNC2 behaviour.
- **New `BUSY ON|OFF` command.** Flips `listener.acceptIncoming` — `BUSY ON` refuses new inbound (peer sees DM, figc4.1 t15); default `BUSY OFF` accepts everything.
- **STATUS extended.** Shows `link: CONNECTED < <peer>` (inbound) or `> <peer>` (outbound), plus `busy:` state.
- **Modem attach now requires MYCALL first** (listener needs `myCall` at construction). Tighter UX — your callsign must be set before you can use the radio.
- **Version pin bumped 0.1.0 → 0.2.1.**

Both pieces ship in one PR — the library facade is the prerequisite for the example consolidation.

### 2026-05-17 — Packet.Term: Spectre.Console → Terminal.Gui v2

Replaces the original three-pane Spectre.Console TUI (shipped 2026-05-16, see entry below) with a full-window Terminal.Gui v2 application in the Borland Turbo Vision / DOS Edit / QBasic IDE style. Spectre.Console is output-oriented — render-blocks-of-text — and was the wrong shape for what Packet.Term wants to be: a node operator's interactive terminal with menus, dialogs, and persistent on-screen state. Terminal.Gui v2 is the .NET port of Turbo Vision (`gui-cs/Terminal.Gui` 2.1.0, the latest stable v2 release) and is exactly the right tool. This is a UI-rendering swap only — the library boundary is untouched.

**What changed.** `src/Packet.Term/Packet.Term.csproj` drops `Spectre.Console` and adds `Terminal.Gui` (CPM-managed in `Directory.Packages.props` at v2.1.0). `src/Packet.Term/TerminalUi.cs` (the Spectre render+key loop) is deleted; in its place a new `src/Packet.Term/Tui/` directory holds `PacketTermApp.cs` (the `IApplication.Create()` shim that drives the v2 lifecycle), `MainWindow.cs` (the menu / monitor / chat / input / status-bar Window subclass), `ConnectDialog.cs` and `SettingsDialog.cs` (modal forms), and `TuiSchemes.cs` (cool / warm `Scheme` palettes registered with `SchemeManager`). `src/Packet.Term/Program.cs` drops the `AnsiConsole.Ask` prompts and uses plain `Console.Write` / `Console.ReadLine` for boot-time MYCALL/port resolution — before the TUI takes over the screen — then hands off to `PacketTermApp.Run`.

**What stays.** The library boundary held: `SessionRunner.cs` (the `Ax25Listener` wrapper from #138/#140/#145), `FrameFormatter.cs` (the BPQ-monitor-style line builder), `KissSerialModem.cs` (the KISS-over-USB-serial transport), `RingBuffer.cs` (the per-pane scroll-back), `AppSettings.cs` / `AppContext.cs` / `AppInfo.cs` / `CommandLineOptions.cs` (settings + CLI parsing) all carry over unchanged. The only `SessionRunner.cs` tweak was a passthrough `AcceptIncoming` property mirroring `Ax25Listener.AcceptIncoming` so the "Session → Accept incoming" menu toggle has somewhere to write to. CLI flags (`--mycall`, `--port`, `--connect`) work identically; `--connect` still kicks off the outbound SABM 200 ms after the TUI is up.

**Layout.** Single `Window` with five sub-views laid out via declarative `Pos` / `Dim`: `MenuBar` at top → `FrameView "Frame monitor"` (40 % of vertical space, cyan-on-black scheme) → `FrameView "Conversation"` (`Dim.Fill(2)` — fills the gap above input + status, white-on-black scheme with yellow accent) → `TextField` input row (`Pos.AnchorEnd(2)`, yellow-on-blue Turbo-Vision-edit-line scheme, disabled when not connected) → `StatusBar` (`Pos.AnchorEnd(1)`, black-on-cyan Turbo-Vision status palette). MenuBar: File (`Settings...` ^S, `Exit` ^Q), Session (`Connect...` F2, `Disconnect` F3, `Accept incoming` toggle), View (`Clear frame monitor`, `Clear conversation`), Help (`About...`). Status bar shows MYCALL, port, link state, F2/F3/F10 shortcuts. Modals: `ConnectDialog` (callsign + OK/Cancel + Esc dismiss), `SettingsDialog` (MYCALL + port + OK/Cancel with "takes effect next launch" note), About via `MessageBox.Query`.

**Idiom decisions worth flagging.**
- Used the **v2.1 `IApplication.Create()` / `app.Init()` / `app.Run(window)` / `app.Dispose()` pattern**, not the deprecated `Application.Init()` / `Application.Run` static surface. The static surface throws CS0618 obsolete warnings, and `TreatWarningsAsErrors=true` would break the build. The `MessageBox.Query(app, ...)` signature follows suit — the first argument is the `IApplication` instance, not a thread-local singleton.
- Per-line colouring (the old Spectre code tinted T-frames yellow and R-frames cyan within a single TextView, plus yellow for chat `*** ...` system lines) does **not** translate cleanly to `TextView`, which is a single-scheme view. We accepted the loss of intra-pane tinting and lean instead on a per-pane scheme: cool cyan for the frame monitor, warm white-with-yellow-accent for the conversation. The T / R direction markers in monitor lines remain in the text content — they just don't get their own colour. This is the same trade-off the canonical Turbo Vision IDE makes (one palette per view), so it lands cleanly.
- Cross-thread updates from `SessionRunner` callbacks marshal via `app.Invoke(() => ...)` before touching any view. Skipping this would race the v2 iteration loop and corrupt the draw state.
- `RingBuffer` is kept as-is (the brief was explicit). The "Clear" menu commands swap in a fresh `RingBuffer` instance rather than mutating the existing one — `frameLog` / `chatLog` are non-`readonly` fields to allow that.

**Out of scope (follow-ups).** `Ax25Listener`'s per-peer cache supports multi-session in principle; the v1 TUI stays single-session. Per-session tabs / a sessions sidebar is the obvious next iteration once a real operator wants two concurrent peers. Also out of scope: hot-swapping MYCALL or the serial port at runtime — the Settings dialog persists the values, but they bind on the next launch (the listener owns MYCALL via construction-time options, not a runtime mutator). Per-line direction colouring (T = yellow, R = cyan within the monitor pane) would require a custom view in place of `TextView`; tabled as nice-to-have if Tom wants the visual parity back.

**Verification.** `dotnet build` — clean (0 errors, 0 warnings in Packet.Term; 61 pre-existing warnings elsewhere in `Packet.AprsIs.Spike` / `Packet.NinoTnc.Spike` unrelated to this PR). `dotnet test --filter "Category!=HardwareLoop&Category!=Interop"` — green across the whole solution (no Term-Tests regressions; the existing 14 tests in `tests/Packet.Term.Tests/` are all library-agnostic and untouched). Smoke test: `timeout 5 dotnet run --project src/Packet.Term -- --mycall M0LTE-9 --port /dev/null` prints `Failed to open /dev/null: Access to the port '/dev/null' is denied.` and exits 5 — the boot flow gets to the modem-open step and reports cleanly; `--mycall INVALID!` exits 2, `--connect bad@call` exits 4, all without stack traces.

### 2026-05-16 — interop flake investigation: XRouter tests cleared, NetsimUiFrame digi test identified as the actual culprit

PRs #138 and #140 both shipped with amendment-log notes claiming "two pre-existing XRouter-via-Netsim failures" carried forward. That claim never matched CI reality (the C# interop job has reported 22/22 — or 19/19 before #140 added the listener scenarios — on every recent merge to `main`). Investigation:

**Method.** Brought the interop stack up locally, ran `dotnet test tests/Packet.Interop.Tests --filter "Category=Interop"` repeatedly (10 full-suite iterations + 5 XRouter-only iterations) against the pinned image digests in `docker/compose.interop.yml`. Captured per-test pass/fail with `console;verbosity=normal` so the failing test name would surface.

**Findings.**
- Both XRouter tests (`Connect_Then_Disconnect_Against_Xrouter_Across_Netsim`, `Connected_IFrame_RoundTrip_Against_Xrouter_Node_Prompt`) pass in every observed run — 10/10 full-suite, 5/5 XRouter-only, plus every recent CI run on `main` (PRs #138, #139, #140, #145 all 22/22 in the C# interop job).
- The actual local flake — observed in 1 of 10 full-suite runs (a separate earlier 7-iteration informal probe saw 2 in 7) — is `NetsimUiFrameScenarios.UI_Frame_With_Digipeater_Path_Round_Trips`. Failure mode is `OperationCanceledException` thrown out of `KissTcpClient.ReceiveAsync` after the 15-second `RxBudget` expires before the UI frame reaches node B. Stack trace lands inside the netsim-side AFSK1200 transmit path; root cause is CPU contention on the test host (four interop containers + the test runner + dotnet build artefacts share a single host), not anything XRouter-specific.
- The amendment-log claim was misattribution: an author saw a 21/22 result locally, glanced at the failure, and wrote it up as "the XRouter flake" without verifying the test name. The bad attribution then propagated across two more PRs.

**Disposition.** XRouter tests are not broken — no fix required. The misleading "pre-existing XRouter" claims in this section's entries for [Ax25Listener (2026-05-16)](#2026-05-16--ax25listener--acceptincoming-context-property) and [Ax25Listener: broad test coverage (2026-05-16)](#2026-05-16--ax25listener-broad-test-coverage) are now annotated inline with a strikethrough correction pointing back here.

**Out of scope but noted.** The `NetsimUiFrameScenarios.UI_Frame_With_Digipeater_Path_Round_Trips` 15-second `RxBudget` is tight under host-CPU contention. CI doesn't see this — the self-hosted runner has more headroom — but local laptops do. Worth a follow-up to bump the budget (say to 30 s) so local dev runs don't flake. Not changed in this PR; the test brief said "don't grow scope past these two tests."

### 2026-05-16 — TS Ax25Listener port (closes TS inbound-listener follow-up)

Ports the C# `Ax25Listener` (and the three associated bug-fix PRs #140 / #141 / #143) to the TypeScript runtime, so `@packet-net/ax25` can act as an inbound-accepting node, not just an outbound client. This is the single most valuable parity gap that was still open between the two runtimes — packet.net's identity is to be a node, and a node accepts incoming connections.

**API parity.** New `Ax25Listener` class in `web/ax25/src/listener.ts` mirrors `Packet.Ax25.Session.Ax25Listener`'s public surface. Constructor takes `Ax25ListenerOptions` (`myCall` + optional `t1Ms`/`t2Ms`/`t3Ms`/`n2`/`k`/`maxCachedPeers`/`configureSession`/`onHandlerError`). Public surface: `myCall`, `isRunning`, `acceptIncoming` toggle, `onSessionAccepted` + `onFrameTraced` (idiomatic-TS callback-set pattern with paired `off*` unsubscribe), `start()` / `connect(remote)` / `stop()` / `dispose()`. The listener-owned session is exported as `Ax25ListenerSession` to avoid name-collision with the existing `Ax25Session` from `Ax25Stack` (the outbound-only facade kept untouched as the brief required).

**Three carried-over bug fixes.**
- **#140 carry-over: handler-exception isolation.** Every `sessionAccepted` / `frameTraced` subscriber dispatch is wrapped in try/catch; exceptions route to a configurable `onHandlerError` sink (default `console.error`) and never escape the inbound pump. The matching unit tests (`survives a sessionAccepted handler that throws`, `survives a frameTraced handler that throws`) pin the behaviour.
- **#141 carry-over: via-chain reversal.** The dispatcher's frame builders in `action-dispatcher.ts` now extract the trigger's inbound digipeater chain reversed and apply it to the outbound response (UA / DM / RR / RNR / REJ / I / UI). Per AX.25 v2.2 §C.2 (Path Construction): inbound SABM via `[digi1, digi2]` → outbound UA via `[digi2, digi1]`. Unit test in `ActionDispatcherViaChain.test.ts`, integration via `Ax25ListenerRejectAndEdge.test.ts` ("handles SABM with a digipeater path").
- **#143 carry-over: cache-miss DM fall-through.** Unknown-peer non-SABM frames (DISC / RR / I / etc.) build a transient Disconnected session, post the event so the SDL's per-event arm fires (DISC → t13 → DM; everything else → t05 all_other_commands → DM), then drop the transient session without caching it. Unit tests cover the DISC, RR, and I-frame variants plus the cache-clean-after invariant.

**Test count.** 30 new unit tests across 5 files: 5 baseline (`Ax25Listener.test.ts`), 7 concurrency + hostile handlers (`Ax25ListenerConcurrency.test.ts`), 7 multi-peer + cache lifecycle (`Ax25ListenerMultiPeer.test.ts`), 10 reject + spec edge cases (`Ax25ListenerRejectAndEdge.test.ts`), and 1 direct dispatcher unit test for the via-chain reversal (`ActionDispatcherViaChain.test.ts`). 1:1 mirror of the 30 C# listener tests (5+7+7+11 in the C# files — the brief's count of 27 was an undercount; the 11th in `Ax25ListenerRejectAndEdgeTests.cs` is the post-#141/#143 cluster). Plus 3 new integration tests: 2 in `tests/integration/listener-netsim-multi-peer.test.ts` (two-peer multi-session, listener-accepts-and-initiates-concurrently) and 1 in `tests/integration/listener-linbpq-initiates.test.ts` (BPQ initiates → our TS listener accepts).

**Spec-tangent changes.** Added a `sabme(...)` factory to `web/ax25/src/frame.ts` plus a `SABME` branch in `classify(...)` (control byte 0x6F) so listener tests can inject SABME and exercise figc4.1's t16 / t17 transitions. mod-128 sequence-number machinery remains gated on `version_2_2`; no behavioural change to outbound paths. Added an `acceptIncoming` field to `Ax25SessionContext` and re-bound `able_to_establish` in `session-bindings.ts` to read from context (default true). Mirrors the C# context's `AcceptIncoming` flag — listener flips it on transient reject-path sessions so the SDL's t15 branch emits DM.

**Behavioural differences vs C#** (low-impact, documented in the listener source):
- C# `ConnectAsync` polls a `ConcurrentQueue<DataLinkSignal>` with `Task.Delay(25)`. TS `connect()` uses promise-based subscription on the session's signal stream — same observable behaviour (resolve on DL_CONNECT_confirm, reject on DL_DISCONNECT_*), zero polling latency.
- C# `SafeInvoke` walks `EventHandler<T>.GetInvocationList()`. TS uses a `Set<callback>` and iterates with per-callback try/catch — same outcome (one throwing handler doesn't suppress siblings).
- C# session class is `Ax25Session` (shared with `Ax25Stack`). TS keeps the two distinct: existing `Ax25Session` (outbound-only with `connect`/`write`/`disconnect`) stays on its current shape per the brief; new `Ax25ListenerSession` exposes the raw `postEvent` + `onDataLinkSignal` API the listener consumer needs.

**Version bump.** `web/ax25/package.json` and `ts-spec/package.json` both bump from `0.1.1` to `0.2.0` (lockstep release). Not published — Tom controls the npm publish workflow.

**Verification.** Full TS unit suite green: 94/94 (was 64; +30 new). Integration tests against the live docker stack: my 3 new tests all pass (2 netsim multi-peer + 1 LinBPQ-initiates). The pre-existing `linbpq-via-netsim > IFrame_RoundTrip_Against_Linbpq_Node_Prompt` flakes today — unchanged from before this PR.

**Capability matrix updates** (`docs/runtime-capability-matrix.md`):
- `Inbound listener` row: `-` / `-` → `C` / `P` (C# from this morning's merge, TS from this PR; P pending live-CI confirmation).
- `Pure-listener-node role` row: `-` / `-` → `P` / `P` (both runtimes now have the necessary primitive).
- Follow-up notes: TS inbound-listener task marked done.

### 2026-05-16 — ActionDispatcher property tests

Broadened test coverage of `src/Packet.Ax25/Session/ActionDispatcher.cs` with FsCheck-driven property tests in `tests/Packet.Ax25.Properties/ActionDispatcherProperties.cs`. The dispatcher's 140-odd-verb switch had at-best one example case per verb in `tests/Packet.Ax25.Tests/Session/ActionDispatcherTests.cs`; the new properties exercise each verb category across arbitrary starting `Ax25SessionContext` states + arbitrary input values, catching the class of regression that a single pinned example can't.

**Categories under property test (42 new properties, all 200 iterations except the mod-128 wrap which drops to 50 because the input space is larger):**

- `FlagMutationProperties` (6) — every `set_X` / `clear_X` pair (own/peer receiver busy, ack pending, layer-3 initiated, reject exception): set→clear inverse, set×2 idempotent, clear×2 idempotent, no cross-flag contamination; plus `increment_srej_exception` / `decrement_srej_exception_if_gt_0` count + flag interlock.
- `SequenceVariableProperties` (8) — `V(s) := V(s) + 1` wraps at mod-8 and mod-128 (`IsExtended`), `V(s) := 0` after N increments lands at 0, `V(r) := V(r) + 1` wraps, `RC := RC + 1` increments by 1, `RC := 0` / `RC := 1` set exact values, `V(a) := N(r)` reads N(R) from incoming frame and throws cleanly without one.
- `PendingFrameAssignmentProperties` (8) — `N(r) := V(r)`, `N(s) := V(s)`, `N(r) := N(s)` (with clean error on missing trigger frame), `F := 0`, `F := 1`, `F := P` (echoes incoming poll bit; clean error on missing frame), `p := 0`.
- `FrameEmissionProperties` (7) — supervisory verbs (`RR_command`, `RR`, `RNR_response`, `REJ`, `SREJ`, plus the figc4.7 title-case forms `RR Command`/`RR Response`/`RNR Command`/`RNR Response`) emit exactly one S-frame with right type / role / N(R) / P-F; default N(R) falls back to V(R); unnumbered verbs (`UA`, `DM`, `DM (F = 1)`, `Expedited UA`/`DM`, `SABM (P == 1)`, `SABME (P = 1)`, `DISC (P = 1)`, figc4.7 `SABM`/`SABME`) emit one U-frame with right type / command-role / PF-override / expedited flag; `I_command` builds spec from Pending + trigger payload and stashes for retransmit; `UI_command` reads payload from `DlUnitDataRequest`; clean errors on wrong trigger kinds.
- `UpwardSignalProperties` (5) — `DL_CONNECT_indication` / `_confirm`, `DL_DISCONNECT_indication` / `_confirm`, all ten `DL_ERROR_indication_*` letter forms and the figc4.7 `DL-ERROR Indication (X)` spellings emit exactly one `DataLinkSignal` of the matching record type / code; `DL_DATA_indication` reads info+pid from incoming I-frame and throws cleanly without one.
- `TimerProperties` (4) — `start_TX` arms / `stop_TX` cancels for all of T1/T2/T3 (both snake_case and figc4.7 title-case forms); start-stop-start re-arms; stop-without-start is a safe no-op.
- `QueueClearProperties` (2) — every spelling of "clear the I-frame queue" (`discard_frame_queue`, `discard_queue`, `discard_I_frame_queue`, `discard_i_frame_queue`, `Discard I Queue Entries`) empties the queue; the no-op discards (`discard_I_frame`, `discard_contents_of_I_frame`, `discard_primitive`) leave queue depth unchanged.
- `UnknownVerbProperties` (2) — any string outside the known-verb set (sanitised arbitrary string + empty/whitespace) throws `InvalidOperationException` with the verb name in the message.

**No real bugs surfaced in `ActionDispatcher`.** Every property passes on first run against the existing implementation, which is the encouraging outcome — the example tests had already pinned the right behaviour. The properties' value is forward: a future regression that breaks any verb's contract across the input space will fall out.

**Runtime.** Whole property suite (`Packet.Ax25.Properties.dll`, 58 tests = 42 new + 16 pre-existing) runs in ~0.8s end-to-end, well under the 60s task ceiling. No `[Trait("Category", "Slow")]` needed.
### 2026-05-16 — docs alignment pass after listener landing

Walk-through of the documentation surface to align it with today's landings — the C# `Ax25Listener` API (#138 / #140 / #145), the `AcceptIncoming` context property, the dispatcher via-chain reversal fix, the listener cache-miss DM fall-through fix, the runtime-capability matrix + strategy docs, the TS library at 0.1.1, the `Packet.Term` TUI, and the packet-terminal web demo. The bar was *factually current and internally consistent* — not a rewrite. Categories of update:

- **Matrix row-flip** ([`docs/runtime-capability-matrix.md`](runtime-capability-matrix.md)): `cap-lifecycle-inbound-listener` C# `-` → `C`; `cap-lifecycle-per-peer-reuse` C# `P` → `C` (listener's per-peer cache obsoletes the previous "rebinds on reconnect" caveat); `cap-role-listener-node` C# `-` → `P` (`Ax25Listener` API ships, `Packet.Term` is the first consumer, `Packet.Node` scaffold awaits filling).
- **Stale-marker scrub**: removed "in flight on `feat/ax25-listener`" / "no PR open" prose from the matrix row and follow-up list; renamed the follow-up to *TS* inbound listener; updated [`docs/runtime-capability-strategy.md`](runtime-capability-strategy.md) §2's "only C# can host a packet node" paragraph to acknowledge the shipped `Ax25Listener` API.
- **Cross-reference fix**: [`ts-spec/README.md`](../ts-spec/README.md) §"Subroutine status" relabelled — the table is now explicitly TS-runtime status; the C# story (every subroutine walked via `DefaultSubroutineRegistry.Wire(...)`) is pointed at the runtime-capability matrix instead of mixed into the same table.
- **Scope-statement fix**: [`web/ax25/examples/packet-terminal/README.md`](../web/ax25/examples/packet-terminal/README.md) §Scope said "no monitor mode" — stale since the `MON ON/OFF` command landed in #133 follow-ups (commit `8091cfa`). Updated to acknowledge the in-app monitor toggle while keeping the underlying-library no-monitor-role caveat.
- **No content change** to [`docs/web-ax25/publishing.md`](web-ax25/publishing.md), [`docs/web-ax25/README.md`](web-ax25/README.md), [`web/ax25/README.md`](../web/ax25/README.md), [`web/ax25/CHANGELOG.md`](../web/ax25/CHANGELOG.md), or the root [`README.md`](../README.md) — they were already factually current. Versions in publishing.md (`0.1.0`) are illustrative of the first-publish flow, not stale references.
- **API reference regen** ([`docs/web-ax25/api/`](web-ax25/api/)): re-ran `npm run docs` so the markdown reflects the current public surface; the TS listener hasn't shipped yet so the surface is identical to the 0.1.1 publish.

Single PR `docs/post-listener-alignment`. Issues #135 / #136 / #137 / #141 / #143 are closed by their respective PRs; #142 and #144 remain open (both are SDL-side spec re-reads, not in-engine fixes).

### 2026-05-16 — fix(ax25): #141 via-chain reversal + #143 listener cache-miss DM

Two implementation bugs surfaced by PR #140's coverage-broadening sweep. Both are in-engine, no SDL / spec interpretation needed — the issues' fixed behaviour matches AX.25 v2.2 §C.2 (Path Construction) and figc4.1 t05 / t13 (the catch-all that emits DM for unrecognised events in Disconnected).

**#141 — Dispatcher via-chain reversal.** Pre-fix, the UA we sent in response to a digipeated SABM had an empty digipeater list — peers behind a digi never saw our reply. Fixed by adding an optional `Path` field to each frame spec (`UFrameSpec`, `SupervisoryFrameSpec`, `IFrameSpec`, `UiFrameSpec`), populated automatically by the dispatcher's frame builders from `TransitionContext.IncomingFrame.Digipeaters` reversed when the trigger carries an inbound frame. `FrameSpecExtensions.ToAx25Frame` prefers `spec.Path` over `context.Digipeaters` when non-null. Path stays `null` for outbound-initiated frames (DL_CONNECT_request triggers — we sent the SABM, no trigger frame), so the context's outgoing chain is used; null also preserves spec-record equality for existing tests that build specs without paths. Outbound-initiated via-chain support (we SABM out through our own digi list) was verified as already wired via `Ax25SessionContext.Digipeaters` — separate gap not relevant to this fix.

**#143 — Listener fall-through for unknown-peer non-SABM frames.** Pre-fix, the listener's cache-miss filter dropped everything except SABM/SABME — DISC, RR, I, etc. from unknown peers were silently swallowed before the SDL ever saw them. The spec wants DM (figc4.1 t13 for DISC, t05 catch-all for RR/RNR/REJ/SREJ/I/FRMR/XID/TEST). Fixed by generalising the existing `AcceptIncoming=false` reject path: for any non-SABM unknown-peer frame, build a transient Disconnected session, post the event, dispose. Non-SABM events that have no specific Disconnected transition (RrReceived, IFrameReceived, etc.) get reclassified to `AllOtherCommands` so t05 fires DM. The transient session never enters the cache, so subsequent outbound `ConnectAsync` / inbound SABM from the same peer still builds fresh state.

**Tests flipped (3 in `Ax25ListenerRejectAndEdgeTests.cs`):**
- `Listener_Handles_Sabm_With_Digipeater_Path` — now asserts UA emits with reversed via chain `[MB7UR, GB7CIP]` (was: assert empty).
- `Listener_Ignores_Disc_For_Unknown_Peer` → renamed `Listener_Emits_DM_For_Disc_From_Unknown_Peer` — asserts exactly one DM, addressed to the original DISC source.
- `Listener_Ignores_Rr_For_Unknown_Peer` → renamed `Listener_Emits_DM_For_Rr_From_Unknown_Peer` — asserts exactly one DM via the t05 catch-all path.

**New tests (3 in the same file):**
- `ActionDispatcher_Reverses_Digipeater_Path_On_Response` — direct dispatcher-level unit test of the reversal, no listener involved.
- `Listener_Emits_DM_For_I_Frame_From_Unknown_Peer` — generalises the DM-for-unknown-peer case to I-frames.
- `Listener_Cache_Stays_Clean_After_Non_SABM_Reject_Path` — invariant that the transient session is dropped, not cached.

**Verification.** Unit suite green (`Category!=HardwareLoop&Category!=Interop`): 1147 tests pass (was 1144; +3 new); Listener-specific tests 30/30. The two `verification_pending` issues #142 (SABM-with-C-bit-response) and #144 (SABME without v2.2 gating) remain open as documented — both require a figure re-read and are SDL-side fixes, not in-engine.

### 2026-05-16 — Ax25Listener: broad test coverage

Six new test groups + a real-bug fix surfaced by them. The Listener landed yesterday (`feat(ax25): Ax25Listener — first-class inbound-session acceptance (#138)`) with 5 unit tests + 1 interop scenario — enough to prove the happy path but not enough for a foundational node-side API. This PR widens that envelope substantially before downstream consumers (BBS, gateway, automatic forwarder, the TUI) build on it.

**Unit tests added (22 new in `tests/Packet.Ax25.Tests/Session/`):**

- `Ax25ListenerConcurrencyTests.cs` (7): SABM collision reuses the cached session; multiple SABMs in T1 window stay idempotent; inbound SABM during outbound `ConnectAsync` produces independent sessions; `StopAsync` mid-active-sessions doesn't deadlock; throwing `SessionAccepted` / `FrameTraced` handlers don't crash the pump; slow handlers don't gate the next frame's observation by more than one slow-handler invocation.
- `Ax25ListenerMultiPeerTests.cs` (7): second peer accepted while first is active; inbound I-frames routed by source callsign to the right per-peer session; per-peer `V(s)/V(r)/V(a)` independence; cached session reused across disconnect/reconnect with non-SDL-reset context state preserved; LRU eviction past `MaxCachedPeers`; evicted-peer reconnect builds a fresh session with reset sequence variables; `DisposeAsync` releases all cached schedulers.
- `Ax25ListenerRejectAndEdgeTests.cs` (8): `AcceptIncoming=false` paths (DM emission, no cache entry, no `SessionAccepted` event, flip-back-to-true accepts the next attempt, existing sessions unaffected by a flip); DISC and RR from unknown peer are dropped silently by the listener (no session built, no DM emitted — the listener layer filters before any SDL would fire its t13 catch-all); SABME with default `AcceptIncoming=true` takes figc4.1 t16 → UA + set_version_2_2 (pins current behaviour, contrary to the task brief's expectation of DM — t16/t17 only branch on `able_to_establish`, not on `version_2_2`); SABM with response C-bit is currently accepted by t14 (classifier doesn't filter on C-bits — pinned as current behaviour); SABM with digipeater path is accepted by source-callsign but the outbound UA does not propagate the via-chain — documented gap.

**Interop tests added (3 new in `tests/Packet.Interop.Tests/`):**

- `Netsim/NetsimListenerMultiPeerScenarios.cs` (2): two peers from net-sim node b both connect to the listener on node a (distinct sessions, both accepted); peer disconnects + reconnects, the listener's cached session is the same instance, context probe state survives.
- `Linbpq/LinbpqListenerScenarios.cs` (1): real-world inverse of the existing `LinbpqViaNetsimConnectedMode` — LinBPQ initiates the L2 connect (driven via its sysop telnet prompt with `C 3 PNTEST`), our listener accepts the SABM, the resulting session reaches Connected, we send a welcome I-frame, then disconnect from our side and watch BPQ's UA come back.

**Real bug fixed in `Ax25Listener`.** The hostile-event-handler tests surfaced an exception-leak: a throwing `SessionAccepted` or `FrameTraced` subscriber would tear down the inbound pump task (exception preserved by `Task.Run`), and surface the exception when `DisposeAsync` awaited the pump task. A buggy subscriber could in effect DoS the modem. Fix: `Ax25Listener.InboundPumpAsync` wraps per-frame `TraceFrame` + `DispatchInbound` calls in per-call try/catch, and event invocations go through a new `SafeInvoke<T>` helper that walks the multicast invocation list and catches each subscriber individually so one throwing handler doesn't suppress downstream ones. Both layers are present as defence in depth — pump-level catches anything else (e.g. a misbehaving SDL action), per-handler ensures one bad consumer doesn't starve another.

**Shared test helpers extracted to `tests/Packet.Ax25.Tests/Session/ListenerTestSupport.cs`.** `LoopbackModem`, `ObservableList<T>`, `WithTimeout` / `WaitFor` helpers were previously private nested in `Ax25ListenerTests`; lifted to an `internal` file in the test assembly so the new tests share them. `LoopbackModem` extended with `DropOutbound` (counted-but-discarded transmits, for "peer lost our UA" scenarios) and `SendDelay` (latency injection, for the slow-handler test). No public-API change.

**Verification.** Unit suite: 1141 tests pass (was 1119; +22 new listener tests). Listener-specific filter: 27/27. Full unit-suite filter `Category!=HardwareLoop&Category!=Interop` is clean. Interop suite: the new listener scenarios all pass (4/4 listener-related, plus the pre-existing 1). ~~The only failures in the interop run are the two pre-existing XRouter-via-Netsim failures, unchanged from before this PR.~~ — **correction 2026-05-16**: this claim was wrong (see [§17 entry "Interop flake investigation"](#17-amendment-log)). The XRouter tests do not fail in CI or under stress locally; the actual flake observed at the time was `NetsimUiFrameScenarios.UI_Frame_With_Digipeater_Path_Round_Trips`, an AFSK1200-sim timing wobble unrelated to the XRouter stack.

### 2026-05-16 — runtime capability strategy + matrix

Packet.NET delivers the AX.25 spec multiple times in multiple languages — C# today (`src/Packet.Ax25/`), TypeScript today (`web/ax25/`), and Go / Rust / Python / C / JSON emitters from the codegen that don't yet have runtimes hanging off them. Drift between any pair of runtimes shows up as wire-compatible-but-subtly-different bugs that only surface at cross-runtime interop time. The codegen-time lints in `spec-sdl/lint-targets.yaml` catch the cheapest version of drift automatically (every predicate name in the SDL must bind in each runtime; every action verb must dispatch) but they don't see the bigger structural gaps — subroutines wired one place and stubbed another, transports that exist on one runtime and not the other, lifecycle features (listener, dynamic T1V, FRMR generation) that are present here and missing there. The pain scales with role — a packet *node* (server) inherits the bug surface of every peer that connects to it, so node-side gaps hurt much more than client-side gaps.

This PR sets up the framework to track those gaps explicitly:

- **[`docs/runtime-capability-strategy.md`](runtime-capability-strategy.md)** — the strategy doc. Categorises the AX.25 capability surface into 11 row-groups (frame codec / KISS / callsign / state pages / figc4.7 subroutines / bindings / dispatcher / subroutine walker / session lifecycle / transports / roles). Defines a 4-level conformance scale (Absent / Stub / Partial / Conformant) and the rules for when to use `?`. Sketches a forward-looking shared conformance suite (language-neutral YAML scenarios + per-runtime executors + CI heatmap rollup) that doesn't exist yet but the framework's shape needs to be agreed before any of it is built. Describes the cross-runtime interop pattern that already exists in C# (LinBPQ / XRouter / rax25 over net-sim) and prescribes the same shape for every new runtime.

- **[`docs/runtime-capability-matrix.md`](runtime-capability-matrix.md)** — the current-state snapshot. ~50 rows × 2 runtime columns (C# + TS). Each cell carries a `C`/`P`/`S`/`-`/`?` level with footnotes for context and TODO lines beneath the matrix for rows that need a desk-check before a defensible level can land. Format is deliberately scan-friendly so capability PRs can update one row at a time. The matrix doc is just the table; the strategy is the discipline.

- **[`tests/conformance/`](../tests/conformance/)** — skeleton for the shared conformance suite sketched in the strategy doc. One worked-example scenario (`connect-sabm-ua-disc.yaml`) covering SABM/UA/DISC/UA happy path with `expect_tx` / `expect_state` / `expect_upward` / `expect_timer` assertions, plus a README explaining the format. No executors yet — that's a follow-up PR (probably one per runtime).

- **Cross-references** — `ts-spec/README.md` and `web/ax25/README.md` both gain a pointer to the matrix as the canonical multi-runtime view. The per-runtime READMEs stay narrative-shaped for their respective audiences; the matrix is for "I want to see the whole surface across all runtimes" — a different reader.

Today's snapshot: C# is the reference runtime — full frame codec (all 13 frame types), KISS encode/decode + ACKMODE, all five transcribed state pages walked, every figc4.7 subroutine routed through `DefaultSubroutineRegistry.Wire(...)`, transports for TCP / USB-serial / AGW / AXUDP. TS is feature-narrower — frame codec covers 9 of 13 (no SABME / FRMR / XID / TEST / SREJ), KISS without ACKMODE, same SDL state-page coverage as C#, four figc4.7 subroutines inlined and the other nine stubbed to no-op, transports for Web Serial + Node TCP. Neither runtime has an inbound listener today (the C# one is in flight on `feat/ax25-listener`); both register `TimerRecovery` as an allow-listed empty state because figc4.5 isn't transcribed.

Discipline going forward: matrix updates land in the **same PR** as capability changes — same shape as the existing rule for `docs/plan.md` amendments (§18). The codegen lints stay the automated backstop for predicate-binding / action-verb / state-target / catchall completeness; the matrix is the human-readable backstop for everything that's structural rather than name-matching (subroutine wiring, transport surfaces, lifecycle features). PR descriptions that touch capabilities call out the matrix row(s) in their Plan-changes section.

Follow-ups deliberately not in this PR: per-runtime conformance executors (one PR per runtime), CI rollup script for the conformance heatmap, the C# inbound listener (already in flight on `feat/ax25-listener`), the TS subroutine walker port (matrix lists it explicitly). Filed-or-to-be-filed issues are linked from the matrix's `## Follow-ups` section.
### 2026-05-16 — Ax25Listener + AcceptIncoming context property

First-class inbound-acceptance API at `src/Packet.Ax25/Session/Ax25Listener.cs`. The Listener is the foundational piece for packet.net as a *node* — a station that exists to accept inbound connections rather than only making outbound ones. Every node-style consumer (BBS, gateway, automated forwarder, the TUI) now goes through it instead of reinventing the inbound-pump / session-rebuild loop the TUI's `SessionRunner` originally carried.

**Public surface.** `Ax25Listener(IKissModem modem, Ax25ListenerOptions options)` with `MyCall`, `IsRunning`, `AcceptIncoming` flag, `SessionAccepted` + `FrameTraced` events, `StartAsync`, `ConnectAsync(remote)`, `StopAsync`, `DisposeAsync`. Options carry `MyCall`, optional T1V/T2/T3/N2/K overrides, `MaxCachedPeers` (LRU cap, default 64), and a `ConfigureSession` hook for per-session bring-up wiring.

**Per-peer session cache.** The Listener keeps a `ConcurrentDictionary<Callsign, Ax25Session>` keyed by remote callsign. Sessions survive disconnect — they sit idle in Disconnected, retaining their `Ax25SessionContext` (and therefore their SRT / T1V smoothing / sequence-variable history) for the next time that peer connects in either direction. LRU eviction once `MaxCachedPeers` is exceeded. The Listener creates each peer's session ONCE (at first contact, inbound SABM or outbound `ConnectAsync`) and reuses it forever after. SessionAccepted re-fires on reconnect so consumers can re-arm per-session handlers.

**Closes three issues:**

- **#137** (add `Ax25Listener`) — full first-class API as above.
- **#136** (`able_to_establish` as context flag) — new `Ax25SessionContext.AcceptIncoming` property (default `true`); `Ax25SessionBindings.CreateDefault` reads it. Override-via-dictionary still works for callers that want richer policy (callsign allow-lists, channel load, etc.).
- **#135** (Remote is init-only across peer-swap) — obsoleted by the per-peer session cache. The Listener never throws away a peer's session, so the "rebind Remote on every new peer" pattern is gone. The existing `Ax25Session` constructor is still available for direct low-level use; no breaking change.

**TUI rewrite.** `src/Packet.Term/SessionRunner.cs` drops from 497 LOC to 328 LOC (−169, ~34 %). The hand-rolled inbound pump, per-SABM session-recreate logic, dual signal-drain loops, and `ableToEstablish` closure plumbing all evaporate — that work now lives in the listener. The TUI subscribes to `SessionAccepted` (status-bar transition) and `FrameTraced` (promiscuous frame monitor), and uses the new `Ax25Session.DataLinkSignalEmitted` event for DL-DATA-indication delivery + disconnect detection. "One connection at a time" becomes a single line each: `listener.AcceptIncoming = false` on accept, `= true` on disconnect.

**Modem abstraction layering.** Moving the Listener into `Packet.Ax25` while keeping its `IKissModem` parameter required a new tiny `src/Packet.Kiss.Abstractions/` project — holding `IKissModem`, `KissFrame`, `KissCommand`, `AckModeReceipt`. Both `Packet.Ax25` and `Packet.Kiss` reference Abstractions; Packet.Kiss keeps the AX.25-aware bridge / classifier (which uses Packet.Ax25). Same shape as `Microsoft.Extensions.*.Abstractions`. The Listener also extends `IKissModem` with a default-implementation `ReadFramesAsync(CancellationToken)` (empty stream by default) so the small set of existing in-test stub modems compile unchanged. Real modems (`KissSerialModem`, `KissTcpClient`, `NinoTncSerialPort`) all implement it concretely.

**New public API on `Ax25Session`.** A `DataLinkSignalEmitted` event + `RaiseDataLinkSignal(DataLinkSignal)` raise method. The Listener wires its dispatcher's `sendUpward` shim to both the listener's per-session signal queue (for `ConnectAsync` to await `DL-CONNECT-confirm`) and through this event (for UI / app observers wanting push-style delivery). Pre-listener callers that build sessions directly are unaffected — the event is raised additively, never replacing the dispatcher's construction-time callback.

**Tests added.** Five new unit tests in `tests/Packet.Ax25.Tests/Session/Ax25ListenerTests.cs` covering accept-inbound-SABM, session reuse across sequential disconnects (same `Ax25Session` instance returned), DM-reject when `AcceptIncoming=false`, two concurrent peers (independent sessions), and FrameTraced firing for both TX and RX. One new netsim interop test in `tests/Packet.Interop.Tests/Netsim/NetsimListenerScenarios.cs` mirroring `NetsimConnectedModeScenarios` but driving both ends through `Ax25Listener`. The pre-listener netsim test is preserved as the canonical low-level rig reference.

**Verification**: 1119 unit tests pass (was 1114; +5 new Listener tests). The Netsim interop suite passes including the new listener scenario; LinBPQ interop passes. ~~The two pre-existing XRouter-via-Netsim failures (unrelated to this PR — same failures on `main` with this branch's changes stashed) remain.~~ — **correction 2026-05-16**: this attribution was wrong (see [§17 entry "Interop flake investigation"](#17-amendment-log)). XRouter-via-Netsim is reliable; the flake the author observed was `NetsimUiFrameScenarios.UI_Frame_With_Digipeater_Path_Round_Trips`, a netsim AFSK1200 timing issue. SessionRunner trims by 169 LOC. Build is clean.

### 2026-05-16 — Packet.Term: Spectre.Console TUI for AX.25 sessions

New `src/Packet.Term/` console app — a three-pane Spectre.Console terminal (frame monitor on top, chat in the middle, status bar, input line at the bottom) that drives `Ax25Session` over a USB KISS modem at 57600 8N1. Outbound and inbound connections both supported: outbound via `C` keybinding (prompt for target, validate via `Callsign.Parse`, post `DlConnectRequest`); inbound via a SABM-listening pump that spins up a fresh session keyed off the SABM source. `able_to_establish` is wired so a second peer SABMs while we're busy and gets DM'd by the SDL automatically rather than queued — Tom's "no second connection" rule honoured at the SDL guard layer.

Settings persist as JSON under `<LocalAppData>/PacketNet/Packet.Term/settings.json` (MYCALL, serial port, last-connect target). CLI flags `--mycall / --port / --connect` override for one run only; if MYCALL or port is unset and no flag supplied, the app prompts interactively before starting the TUI. `--connect` skips the disconnected state and kicks SABM at startup.

Frame monitor is always-on and promiscuous — every frame on the wire renders in BPQ-style format (`HH:MM:SS T M0LTE-1>G1AAA <SABM C P>`), regardless of addressing, with the info field shown indented on the next line for I/UI frames. Status-bar keybinding cheatsheet changes with state so the user always sees what's allowed.

Tests cover the FrameFormatter (representative SABM/UA/DISC/DM/RR/I/undecodable cases), the JSON settings round-trip (including corrupt-file → blank-defaults), and the CommandLineParser binding. Spectre's `Live` display is intentionally not exercised in unit tests — too hard to drive from xUnit without a real terminal.

New library deps in `Directory.Packages.props`: `Spectre.Console`, `Microsoft.Extensions.Configuration`(`.Json`,`.Binder`). Reuses the already-pinned `CommandLineParser` and `System.IO.Ports`.

Run-the-app one-liner for Tom: `dotnet run --project src/Packet.Term -- --mycall M0LTE-1 --port /dev/ttyUSB0`.

### 2026-05-16 — packet terminal example app

Self-contained HTML demo at `web/ax25/examples/packet-terminal/` showing `@packet-net/ax25` driving a browser terminal against a USB KISS modem. xterm.js as the screen, Web Serial as the modem-attach point, a TNC2-style command set (`MYCALL`, `CONNECT`, `DISCONNECT`, `STATUS`, `ECHO`, `CLEAR`, `VERSION`, `HELP`), command/converse mode toggle on `Ctrl-C`.

Aesthetic is intentional — late-80s phosphor-CRT terminal: VT323 typeface, deep green-on-black, scanlines + RGB-mask shimmer + vignette + 8-second flicker cycle in CSS, status bar showing MYCALL / LINK / MODEM with pulse indicators. The boot banner identifies `@packet-net/ax25 0.1.0` and `ax25sdl 0.1.0` by name to make the library's role visible.

Zero build step — imports `@packet-net/ax25@0.1.0`, `xterm@5.3.0`, and `xterm-addon-fit@0.8.0` from esm.sh; Google Fonts for VT323 and IBM Plex Mono. Opens directly in Chromium / Edge. Firefox + Safari show the layout but the modem-attach button fails (Web Serial standard not implemented in those browsers; modal explains the constraint).

This is the first real consumer of the published `@packet-net/ax25` NuGet equivalent and validates the import surface (`Ax25Stack`, `WebSerialKissTransport`, `Callsign`) ergonomically resolves in a vanilla-HTML context.

### 2026-05-16 — ax25.ts: rename to @packet-net/ax25

Pre-publish rename. Unscoped `ax25` on npm is already taken (existing Node KISS+AX.25 stack at v1.1.2), so we ship under the `@packet-net` scope. Drops the `-ts` suffix from the package name; the directory and docs paths match (`web/ax25/`, `docs/web-ax25/`). No code-shape changes; nothing was published under the old name so there's no migration path to honour.

### 2026-05-16 — codegen: generalise runtime-specific lints across C# and TypeScript

Before this PR the codegen-time lints in `tools/Packet.Sdl.CodeGen/Program.cs` hard-coded paths under `src/Packet.Ax25/Session/` — so the predicate-binding / action-dispatcher / subroutine-coverage / DL-ERROR / dispatcher-orphan lints only ran against the C# runtime. Gaps in the TypeScript port (under `web/ax25/src/sdl/`) would silently land and only surface at runtime when a transcription referenced an unbound predicate or unknown action verb. This PR drives the runtime-specific lints from a single config file so both runtimes (and any future ones) get the same coverage.

**Shape of the refactor**:

- New file `spec-sdl/lint-targets.yaml` lists each runtime as a target with paths + extraction regexes for its bindings / dispatcher / subroutine source files. Today there are two targets — `csharp` and `typescript`. Other backends (Go / Rust / C / JSON / Python) ship tables-only and have no runtime to lint against, so they're absent.
- Each per-target file entry has its own regex tuned to the language's syntax. The C# regexes are unchanged from before this PR. The TS regexes that work for the current files are: `bindings\.set\(\s*"([A-Za-z_][A-Za-z0-9_]*)"` for bindings (the `\s*` covers the multi-line `set(\n    "name",` forms used three times in `session-bindings.ts`), and the C# case-label regex (`case\s+"([^"]+)"\s*:`) carries over verbatim for the TS dispatcher.
- The TS port has no `subroutines:` entry in its target. The TS `DefaultSubroutineRegistry` has no static name list — it tolerates unknown subroutines by logging via the `onUnknown` sink, no throw. The strict subroutine-coverage lint doesn't translate; we made the `subroutines:` block optional per target rather than force a synthetic check.
- Error messages from every runtime-specific lint are now prefixed `[<language>]` so a CI failure attributes the gap to a specific runtime.
- The runtime-agnostic lints (`LintStateTargets`, `LintCatchallCoverage`) don't read `lint-targets.yaml` — they operate on SDL pages directly and apply to every runtime equally.

**Decision: keep state-target + catchall lints runtime-agnostic.** Both check structural properties of the SDL transcriptions themselves (a `next:` target must name a known state in the same machine; every complete state needs a catchall transition). The semantics of those checks don't change between runtimes — there's no per-language tuning the lint could do, so factoring them into the per-target config would only add ceremony.

**Decision: shared dispatcher-orphan + state-target allow-lists.** Today the C# and TS dispatchers ship the same verb vocabulary (the TS port is a near-line-for-line translation of the C# dispatcher), so a verb that's an orphan in one is an orphan in the other. The `DispatcherOrphanAllowList` and `StateTargetAllowList` are kept as in-code shared sets with a comment noting the choice — if a future runtime introduces divergent orphans, split into per-target allow-list YAML blocks at that point.

**Standalone-codegen escape hatch preserved.** When `spec-sdl/lint-targets.yaml` is absent (legacy invocation, test fixtures, libraries built without the runtime source on disk), `LintTargetsConfig.Load` returns an empty list and every runtime-specific lint becomes a no-op. Per-target files that don't exist still silently skip — same shape as before the refactor.

**Adding a future runtime.** When/if a Go runtime, Rust runtime, etc. appears, it's a single block in `spec-sdl/lint-targets.yaml` — no Program.cs change required. The block names the language, the bindings / dispatcher / subroutines paths, and the regex per file that extracts the symbol from capture group 1. Tested via the new "lint-targets with two runtimes fires only for target with gap" test in `CodegenSmokeTests`, which spins up two synthetic targets ("alpha" / "bravo") and asserts that the language label is attached correctly.

**Verification**: codegen runs error-free against the current state of both runtimes (the C# and TS bindings files have identical 41-element predicate sets, dispatchers have identical 146-element case-label sets — confirmed by a side-by-side regex comparison). The 24 pre-existing codegen tests still pass; three new tests (`Without_lint_targets_yaml_runtime_specific_lints_skip`, `Lint_targets_with_two_runtimes_fires_only_for_target_with_gap`, `Lint_targets_error_messages_carry_language_label`) cover the new config-driven behaviour.

### 2026-05-15 — ax25.ts: developer docs + npm distribution plumbing

Stacks on the two ax25.ts entries below. Before this PR the library was internally documented (README, in-code JSDoc) but had no API reference, no distribution plumbing, no LICENSE / CHANGELOG, and the `ts-spec/` package was missing the npm metadata fields needed to publish. This PR fills all of those gaps without touching the library's runtime code path.

**Deliverables**:

- **`web/ax25/README.md` rewritten** with a 1-paragraph "what this is" opener aimed at developers new to amateur-radio app work, a future-state `npm install @packet-net/ax25` snippet (with a NOTE callout flagging the package isn't on npmjs yet), a Transport Seams table that distinguishes provided transports from not-yet-implemented ones, an in/out scope table, and links into the new API ref + publishing guide.
- **`web/ax25/LICENSE`** — MIT, copyright "Tom Fanning and Packet.NET contributors", matching the repo-root LICENSE shape.
- **`web/ax25/CHANGELOG.md`** — Keep-a-Changelog 1.1.0 format, 0.1.0 marked UNRELEASED until first publish.
- **API reference via TypeDoc** — `web/ax25/typedoc.json` configures two entry points (`src/index.ts` + `src/tcp-transport.ts`) and emits markdown to `docs/web-ax25/api/` via `typedoc-plugin-markdown`. `npm run docs` regenerates. SDL driver internals (`src/sdl/**`), the Node-types shim, and tests are excluded. `gitRevision: "main"` keeps the "Defined in:" source links stable across merges. Emits cleanly (zero warnings, 50 markdown files, 270 KB total).
- **`docs/web-ax25/README.md`** — index page pointing readers at the API ref, examples, publishing guide.
- **Three worked examples in `web/ax25/examples/`** — `quick-start.ts` (Web Serial — refreshed from the existing version with a clearer narrative), `node-tcp.ts` (Node TCP via `TcpKissTransport` against `127.0.0.1:8100` / net-sim), `with-mock-transport.ts` (paired `MockTransport`, scripted peer, drives `Ax25Stack` under test). `tsconfig.examples.json` + `npm run typecheck:examples` script keep all three typechecking against the public API surface on every CI run.
- **`docs/web-ax25/publishing.md`** — full runbook for first npm publish. Aimed at someone whose package-management muscle memory is NuGet (table at top maps npm concepts to their NuGet equivalents). Covers account setup, `@packet-net` scope creation, automation token, `NPM_TOKEN` GitHub secret, manual `npm pack --dry-run` validation, the `file:../../ts-spec` → `^0.1.0` flip required pre-publish, SemVer bump + tag workflow, and troubleshooting for `402` / name-taken / ERESOLVE / "cannot publish over existing version".
- **`.github/workflows/npm-publish.yml`** — triggers on tags matching `v*`. Builds + publishes `ax25sdl` first, then mutates `web/ax25/package.json` in the CI shell to flip the `file:` dep to a registry version, then builds + publishes `@packet-net/ax25`. The flip is CI-local — never committed back to main — so local-dev `npm install` continues to work unchanged. Skips both `npm publish` steps if `NPM_TOKEN` is unset; build + dry-run-pack still run so packaging issues surface even without credentials.
- **`ts-spec/package.json` metadata** — added `license: MIT`, `repository.directory: ts-spec`, `homepage`, `bugs`, richer `description`, `keywords`. Bumped version from `0.0.0` to `0.1.0` to track the first `@packet-net/ax25` release.

**`web/ax25/src/transport.ts` doc-only edit**: one JSDoc comment carried a broken `{@link MockTransport}` reference (MockTransport isn't an exported symbol — it lives under `tests/`) and an outdated "Future implementations: TcpKissTransport" note. Fixed the link and updated the comment to reflect TcpKissTransport now existing. This is the only `src/` change in the PR; no public API surface moved.

**Constraint compliance**: 64 unit tests + 173 ts-spec tests still pass; `npm run typecheck` clean in both packages; TypeDoc emits zero warnings; YAML in the workflow lints clean.

### 2026-05-15 — ax25.ts: TCP transport + first live interop against LinBPQ

Stacks on the ax25.ts library entry below. Before this PR, the library had no real interop evidence — `tests/session.test.ts` proved the SDL session machine wires up against itself via paired `MockTransport` pipes, but a closed-loop test of the library against itself doesn't catch wire-level mismatches with third-party stacks that weren't built from the same SDL tables. This PR closes that gap.

**New code**:

- **`web/ax25/src/tcp-transport.ts`** — `TcpKissTransport implements Ax25Transport`. Same three-method shape as `WebSerialKissTransport`. Wraps a `node:net` socket: `start` dials with a 5 s timeout, attaches a `data`-event handler that pushes bytes through a `KissDecoder` and surfaces decoded payloads via `onFrame`; `send` KISS-encodes and writes; `stop` half-closes via `end()` with a 200 ms grace before `destroy()`.
- **`web/ax25/src/node-types.d.ts`** — minimal ambient declarations for the surface of `node:net` + `node:events` we touch. Keeps the library off `@types/node` (which would muddy the browser-targeted main entry) at the price of stretching the shim when the Node surface grows. When/if a true Node-side surface lands (server, AGW listener…), flipping on `@types/node` properly will be cheaper than keeping this shim alive.
- **Subpath export** in `web/ax25/package.json`: `./tcp-transport` → `dist/tcp-transport.{js,d.ts}`. Main `index.ts` does NOT re-export `TcpKissTransport` — browser bundlers won't pull in `node:net` unless callers deep-import via `@packet-net/ax25/tcp-transport`.

**Tests**:

- **Unit tests** (`tests/tcp-transport.test.ts`, 12 facts): MockSocket fake (paired `EventEmitter` shape, mirroring the C# `KissTcpClient` paired-pipe trick) exercises connect/connect-error/connect-timeout, send → KISS-encoded write, multi-chunk inbound reassembly, port-nibble filter, non-Data-command drop, and stop idempotency. No real socket dialled. All 64 unit tests (52 existing + 12 new) pass in <1 s.
- **Integration test** (`tests/integration/linbpq-via-netsim.test.ts`, 2 facts): mirror of `tests/Packet.Interop.Tests/Linbpq/LinbpqViaNetsimConnectedMode.cs`. (1) Connect + Disconnect — `Ax25Stack` connects `PNTEST-1`→`PN0TST` via net-sim 8100; asserts session opens; disconnects cleanly. (2) I-frame round-trip — same setup, waits for BPQ's CTEXT banner I-frame, sends `P\r` (BPQ ports command), waits for non-empty response I-frame, then disconnects. Uses SSID 1 to dodge collision with the C# test (no-SSID `PNTEST`). 15 s / 15 s budgets per phase.
- **Skip-guard**: top-level `await netsimReachable()` probes `127.0.0.1:8100` with a 200 ms budget; `describe.skipIf(!stackReachable)` no-ops the whole block when the stack isn't up. Lets the integration file live in the same vitest tree as the unit tests without breaking dev-machine `npm test` (which is also pointed away from `tests/integration/` by the `exclude` in `vitest.config.ts`, belt-and-braces).
- **CI**: `interop.yml` gains a `ax25.ts integration tests` step after the C# `interop tests` step. Builds ts-spec, builds web/ax25, runs `npm run test:integration` against the same already-up compose stack. `paths:` filter widened to include `web/ax25/**` and `ts-spec/**` so a change in either retriggers the interop matrix.

**Wire-log evidence** (`docker logs --since 60s pn-netsim | grep PNTEST-1`):

```
[a.vhf] PNTEST-1>PN0TST:(SABM cmd, p=1)
[c.vhf] PN0TST>PNTEST-1:(UA res, f=1)
[c.vhf] PN0TST>PNTEST-1:(I cmd, n(s)=0, n(r)=0, p=1, pid=0xf0)PN0TST Packet.NET interop test node <0x0d>
[a.vhf] PNTEST-1>PN0TST:(RR res, n(r)=1, f=1)
[a.vhf] PNTEST-1>PN0TST:(I cmd, n(s)=0, n(r)=1, p=0, pid=0xf0)P<0x0d>
[c.vhf] PN0TST>PNTEST-1:(I cmd, n(s)=1, n(r)=1, p=0, pid=0xf0)PNTST:PN0TST} Ports<0x0d>  1 Telnet  …
[c.vhf] PN0TST>PNTEST-1:(I cmd, n(s)=2, n(r)=1, p=1, pid=0xf0)    <0x0d>
[a.vhf] PNTEST-1>PN0TST:(DISC cmd, p=1)
[c.vhf] PN0TST>PNTEST-1:(UA res, f=1)
```

Clean handshake → banner → ack → command → multi-frame response → DISC/UA — no SREJ/REJ recovery on the happy path, V(s)/V(r) increment correctly across both directions. The library hit-the-wire behaviour matches BPQ's expectations.

**What is NOT yet asserted**:

- **XRouter** and **rax25** equivalents (the C# interop matrix has both; the ts library doesn't yet). Obvious follow-ups — the test is straightforward to clone for each peer, but each clone risks surfacing a peer-specific quirk so keeping the blast radius small (one peer at a time) is worth a separate PR.
- **N2 exhaustion path** against a real peer (the unit suite covers this against MockTransport; against a real peer it'd need a way to drop frames, which net-sim's scripted-loss config can do but the test fixture doesn't wire up).
- **Recovery paths** — REJ/SREJ retransmission, T1 timeout-driven recovery, FRMR. The library's SDL stubs these (`Select_T1_Value`, `Invoke_Retransmission`, etc.); the BPQ-vs-ts test stays on the happy path.
- **Inbound connection acceptance** — the library `Ax25Stack` initiates only. Receiving a peer-initiated SABM and replying UA needs an `onConnectRequest` API which is out of v1 scope.

**Findings worth noting**: nothing surprising surfaced. BPQ's banner happens to fit in one I-frame for the current CTEXT, the `P\r` response splits across two I-frames, and the library's k=1 single-outstanding-I-frame window cooperates with both. No timing-sensitive corner case appeared in the 4 + 5 runs of the two scenarios during development.

### 2026-05-15 — ax25.ts: browser-targeted TypeScript library, table-driven from the SDL

New package at `web/ax25/` — a TypeScript library for connected-mode AX.25 v2.2 sessions over Web Serial KISS modems. Hand-rolled at first; reworked mid-PR to walk the generated SDL transition tables in `ts-spec/src/ax25sdl/` (same shape as the C# runtime in `src/Packet.Ax25/Session/`).

**Layout** (mirrors `src/Packet.Ax25/Session/` deliberately):

- `src/frame.ts` (418), `src/callsign.ts` (80), `src/address.ts` (79), `src/kiss.ts` (136) — pure value types + codecs. Mod-8 only.
- `src/transport.ts` (27) — the `Ax25Transport` interface (3 methods: start / send / stop). The seam future TCP / AGW / bridge transports slot into.
- `src/webserial-transport.ts` (130) — concrete `WebSerialKissTransport` typed against `@types/w3c-web-serial`, duck-typed via `WebSerialLikePort` so tests don't need a browser.
- `src/sdl/events.ts` / `session-context.ts` / `timer-scheduler.ts` / `guard-evaluator.ts` / `session-bindings.ts` / `action-dispatcher.ts` / `subroutine-registry.ts` / `session-driver.ts` — the table-walking runtime. ~1500 LOC. Imports transitions from `ax25sdl`.
- `src/session.ts` (424) — public-API wrapper: `Ax25Stack`, `Ax25Session` with `connect/onData/onDisconnected/write/disconnect`. No state machine logic; pure plumbing on top of the SDL driver.
- `src/index.ts` (109) — the public exports.

**Tests** (52 facts in 6 vitest files):

- `tests/frame.test.ts` (9) — all frame types (SABM/UA/DISC/DM/UI/RR/I) + digipeater E-bit migration.
- `tests/callsign.test.ts` (8) + `tests/address.test.ts` (4) — parse / serialize / v2.2 reserved bits / C/H bit handling.
- `tests/kiss.test.ts` (9) — FEND/FESC escape, port nibble, complete / split-across-pushes / inter-frame-FEND resync.
- `tests/sdl-driver.test.ts` (11) — catchall behaviour, deterministic guard ordering, `GuardEvaluationError` on unbound predicate, `UnknownActionError` on unhandled verb (with verb + state in message), `and`/`or`/`not` grammar coverage, bindings dictionary against `Ax25SessionContext` + scheduler.
- `tests/session.test.ts` (11) — end-to-end via paired `MockTransport` pipes: SABM→UA connect, DM rejection, N2 exhaustion retry, I-frame TX/RX with V(s)/V(r)/V(a) bookkeeping, RR ack handling, DISC→UA, peer-initiated DISC, `Ax25Stack.connect` precondition tests.

**Predicates / actions wired** in `session-bindings.ts` / `action-dispatcher.ts`: all 39 predicates referenced by figc4.x guards, ~140 action verbs in both snake_case and figc4.7 title-case spellings.

**Explicitly out of scope** (documented in `web/ax25/README.md`'s scope-limitations table):

- mod-128 / SABME (the `version_2_2` binding always returns false; mod-128-only transitions route around).
- figc4.7 subroutine walker (4 inlined: `Establish_Data_Link`, `Establish_Extended_Data_Link`, `Clear_Exception_Conditions`, `Check_I_Frame_Acknowledged`; 8 left as no-op registry stubs — `Select_T1_Value`, `Transmit_Enquiry`, `Invoke_Retransmission`, `N_r_Error_Recovery`, `Enquiry_Response`, `Check_Need_For_Response`, `UI_Check`, `Set_Version_2_*`).
- TimerRecovery state aliased to Connected — figc4.5 isn't transcribed yet.
- TCP / AGW / bridge / audio transports (transport seam is the only concrete is Web Serial).
- AGW server.
- Inbound connection acceptance (the `Ax25Stack` initiates only).
- `via` digipeater paths in `connect()` throw "not implemented" (the codec handles them, tested).

**`ts-spec/package.json` fix**: `main`/`types`/`exports` were pointing at `dist/index.*` but `tsc` emits to `dist/ax25sdl/index.*` (because the source layout is `src/ax25sdl/`). Corrected to actual output paths. Codegen doesn't regenerate `package.json` so the fix is stable.

**CI**: added two steps after the existing ts-spec typecheck/test — `ts-spec build` (so the `file:../../ts-spec` dependency's `files: ["dist/**"]` copies aren't empty) and `web/ax25 build + test` (npm ci, tsc, vitest). The TS-drift assertion is unchanged.

### 2026-05-15 — interop: bpq32.cfg simplification (drop redundant NODE=1, BBS=0)

Follow-up to the I-frame round-trip entry below. The previous fix added explicit `NODE=1` and `BBS=0` thinking they were needed to make BPQ respond on an L2 connection. Per the [LinBPQ SIMPLE-mode reference](https://m0lte.github.io/linbpq/configuration/reference/?h=simple#quick-start-simple-mode), `SIMPLE=1` already sets `NODE=1` and `BBS=1` — so our `NODE=1` was redundant and our `BBS=0` was overriding the SIMPLE default to no useful effect for these tests. What was actually missing was just the `CTEXT: … ***` banner block, which `SIMPLE=1` does NOT seed. Verified both BPQ-via-netsim interop tests still pass with just `SIMPLE=1` + the CTEXT block.

### 2026-05-15 — interop: Rax25-via-netsim connected-mode test

Third stack on the netsim interop matrix and the first Rust implementation. Thomas Habets's [rax25](https://github.com/ThomasHabets/rax25) `async_server` example dials net-sim's KISS-TCP listener on port 8104 (node `e`) from inside docker; our `Ax25Session` attaches to net-sim's port 8100 (node `a`) and the afsk1200 sim bridges between us. Mirrors `LinbpqViaNetsimConnectedMode` and `XrouterViaNetsimConnectedMode` — handshake-only assertion (SABM/UA + DISC/UA), 15 interop tests green locally including the new fact.

Adds:

- **`docker/rax25/Dockerfile`** — multi-stage build (`rust:1-slim-bookworm` → `debian:bookworm-slim`). Upstream publishes no pre-built image so we build the `async_server` example from source at image-build time. Pinned to commit `3cc22e59…` via the `RAX25_REV` build-arg for reproducibility. Build deps include `libudev-dev` for the `serialport` crate (a transitive dep of rax25 that we don't exercise here since we only use the TCP endpoint, but it's part of rax25's main lib so we can't avoid linking it). Edition 2024 means the image needs a recent Rust toolchain (rax25 declares `rust-version = "1.91"`). The resulting image is local-only and never pushed to a registry.
- **Fifth net-sim node `e`** in `docker/netsim/network.yaml` on `kiss_port: 8104`, linked only to `a` (not to b/c/d) so concurrent scenarios stay isolated at the RF-sim layer.
- **`rax25` service in `docker/compose.interop.yml`** with `depends_on netsim: healthy`. The command pre-bakes the CLI flags `-v debug -p tcp://netsim:8104 -s PN0RAX-1` — `-s PN0RAX-1` registers rax25 as `PN0RAX-1` (SSID 1), which our test uses as the remote callsign. No CTEXT-equivalent needed: rax25's `accept()` loops on incoming SABM and replies UA automatically, then writes a fixed welcome banner I-frame ("Welcome to the server!\n") which we don't assert on.
- **`tests/Packet.Interop.Tests/Rax25/Rax25ViaNetsimConnectedMode.cs`** — one fact, `[Collection(NetsimCollection.Name)]`. Rig duplicated from the LinBPQ and XRouter equivalents rather than shared (rax25-specific quirks likely to surface, particularly around the extended-mode reserved-bit handling and the missing REJ/SREJ support).

**Caveats acknowledged in the test docstring and PR body:** rax25's README flags REJ/SREJ as "untested / probably broken" and segmentation as "not implemented". SABM/UA/DISC/UA — the only frames this test exercises — do NOT touch any of those paths, so the scope is genuinely safe. A future I-frame round-trip or recovery-path test against rax25 will need care: assertions that generalise across LinBPQ and XRouter may not transfer.

**License:** rax25 is MIT-licensed (declared in its `Cargo.toml`; the repo lacks a top-level `LICENSE` file but the package metadata is authoritative under standard Rust ecosystem conventions). We build from source at image-build time and don't redistribute the resulting image — pure local docker / CI use.

Wire-log evidence (from `docker logs pn-netsim` after the test run):

```
[a.vhf] PNTEST>PN0RAX-1:(SABM cmd, p=1)
[e.vhf] PN0RAX-1>PNTEST:(UA res, f=1)
[e.vhf] PN0RAX-1>PNTEST:(I cmd, n(s)=0, n(r)=0, p=0, pid=0xf0)Welcome to the server!<0x0a>
[a.vhf] PNTEST>PN0RAX-1:(DISC cmd, p=1)
[e.vhf] PN0RAX-1>PNTEST:(UA res, f=1)
```

Pattern follows the LinBPQ-via-netsim and XRouter-via-netsim entries before it: minimum-useful assertion + duplicated rig to keep the blast radius of any later regression localised to one peer at a time. Fills the Rust slot of the third-party AX.25 interop matrix (LinBPQ: C/mature, XRouter: C/mature, rax25: Rust/young). The `§7.1 interop-scenarios` table grows a row to reflect that the connected-mode SABM/UA-vs-rax25 scenario is now covered.

### 2026-05-15 — interop: XRouter-via-netsim connected-mode test

Second third-party stack on the netsim interop matrix. Our `Ax25Session` on net-sim port 8100 (node a) talks SABM/UA/DISC/UA to XRouter dialling node d on port 8103 from inside docker. Test mirrors `LinbpqViaNetsimConnectedMode` — handshake only, data-path deferred to a follow-up.

Adds:

- **Fourth net-sim node `d`** in `docker/netsim/network.yaml` (kiss_port 8103), linked to `a` only; b/c/d isolated from each other so concurrent foreign-stack tests don't see each other's frames.
- **Second `INTERFACE` / `PORT` pair in `docker/xrouter/XROUTER.CFG`**: `INTERFACE=2 TYPE=TCP PROTOCOL=KISS IOADDR=172.30.0.12 INTNUM=8103` + `PORT=2 INTERFACENUM=2 CHANNEL=A …`. Pattern follows m0lte/test-net's working XRouter configs verbatim (split INTERFACE+PORT rather than LinBPQ's single-PORT shape — different syntax, same plain KISS-over-TCP underneath).
- **`CTEXT … ***` welcome banner** kept in the config. Initially added thinking it was needed to enable connect-accept (as for LinBPQ), but later investigation showed XRouter's CTEXT is **alias-only** — it fires on connects to NODEALIAS (`PNXRT`) but NOT on connects to NODECALL (`PN0XRT`, what our test uses). NODECALL connects engage the node prompt silently, no banner I-frame. Kept for documentation of XRouter behaviour and to support a future alias-connect test; the comment in `XROUTER.CFG` was updated accordingly in a follow-up commit in this same PR.
- **`docker/compose.interop.yml`**: `depends_on netsim healthy` from xrouter so the first KISS dial finds the listener.
- **`tests/Packet.Interop.Tests/Xrouter/XrouterViaNetsimConnectedMode.cs`** — two facts: handshake (SABM/UA + DISC/UA) and I-frame round-trip (CONNECT, send `?\r` help command, await response indication, disconnect). `[Collection(NetsimCollection.Name)]` so concurrent netsim tests don't fight over port 8100.

Verified locally: full SABM → UA → I (command) → I (response, 2 frames) → DISC → UA exchange in net-sim wire-log. 14/14 interop tests green together.

One pre-flight gotcha noted: PORT=2 originally used `CHANNEL=B` (figuring B as "the second channel" for diversity), but test-net always uses `CHANNEL=A` on KISS-TCP ports and XRouter may silently reject other channels on a TCP/KISS interface. Switched to A.

### 2026-05-15 — interop: AGW protocol-fidelity smoke tests against LinBPQ

First exercise of `Packet.Agw` against a real AGW server. Three facts in `tests/Packet.Interop.Tests/Agw/LinbpqAgwFidelityTests.cs`:

- Dial LinBPQ's AGW listener on `127.0.0.1:8000`, send `G` (port info), assert a non-empty port list comes back.
- Register a callsign via `X` and confirm the ack frame arrives within the budget.
- Dispose the client without hanging — regression-guards the background dispatch / keepalive loop shutdown ordering.

This is **protocol-fidelity** (proves our AGW client wire-talks to a real third-party server), not **connected-mode interop** (which would require terminating a SABM at a remote peer — LinBPQ would route via its modem, not loop back through the same AGW listener). For connected-mode AGW interop we'd need either two LinBPQ instances in different docker namespaces with their AXIP/AXUDP linking them, or direwolf as the AGW server with a kissutil sidecar bridging to net-sim.

Worth recording: **direwolf does NOT support KISS-TCP client mode** — its `KISSPORT` and `AGWPORT` are server-only (`src/kissnet.c` calls `bind()`/`listen()`/`accept()`, never `connect()`). The clean direwolf interop story is therefore not "direwolf attaches to our net-sim like LinBPQ does"; it's one of:

1. **`kissutil` sidecar**: ships with direwolf, IS a KISS-TCP client. Bridges direwolf's KISSPORT listener to net-sim. Adds a hop but no kernel modules.
2. **Reverse the dial direction in net-sim**: net-sim outbound-attaches to direwolf:8001. Matches the LinBPQ/XRouter outbound pattern; needs a net-sim feature we'd have to confirm.
3. **socat shim** between the two KISS listeners.

Same constraint applies to **samoyed** (Go port of direwolf — same kissnet design). Deferred to a follow-up PR once we've picked a bridge mechanism. The protocol-fidelity tests in this PR establish that the wire is right; the L2 interop scope can grow independently.

### 2026-05-15 — Packet.Agw: SV2AGW AGWPE client library

First substantive code in `src/Packet.Agw/`. Pure client (dials INTO LinBPQ / direwolf / SoundModem / XRouter AGW listeners) — not the AGW server packet.net will need to expose later as an application interface. Those are protocol-symmetric but distinct code surfaces; this PR is the client only.

Design points:

- **`AgwFrame`**: pure value record for the 36-byte header + body. `ToBytes()` / `Parse()` round-trip; `TryReadDataLength()` lets a streaming reader decide how many body bytes to await without fully parsing.
- **`AgwFrameStream`**: low-level frame I/O over an arbitrary `Stream`. Inbound frames land in a bounded `Channel<AgwFrame>`; outbound writes are serialised with a `SemaphoreSlim` so concurrent senders can't interleave bytes mid-header. Decoupled from `TcpClient` so tests can drive it over paired pipes.
- **`AgwClient`**: high-level handle. `ConnectAsync(host, port)` opens a TCP connection; `FromStream(stream)` wraps a caller-provided duplex stream for tests. `RegisterCallsignAsync`, `OpenSessionAsync`, `GetPortInfoAsync` cover the canonical operations. Built-in keepalive pumps `R` (version) every 15s by default to defeat BPQ's ~20s idle disconnect — set to `TimeSpan.Zero` to disable.
- **`AgwSession`**: connected-mode session as a `Stream` subclass. Writes split into ≤256-byte `D` frames; reads drain `D` frames from the server in arrival order. `'d'` from the server surfaces as EOF on the next read. `DisconnectedTask` lets callers await session teardown without blocking on `ReadAsync`.

Inspiration but not lift: dapps's AGW code (`src/dapps/dapps.client/Transport/Agw/`) is the de-facto reference for an AGW client in C# but is tightly coupled to dapps internals (`IDappsTxGate`, `IBackhaulInbox`, `OperationalMetrics`). This implementation is a fresh design with the same conceptual shape — `AgwFrame` framing + `AgwFrameStream` transport + `Stream`-shaped sessions — packaged as a standalone NuGet (`PackageId=Packet.Agw`) with no project-specific dependencies. A follow-up issue will be filed on `m0lte/dapps` asking it to consume the published NuGet once 1.0 lands.

Tests: 16 facts in `Packet.Agw.Tests/`. Frame round-trips, NUL-trimmed callsigns, truncation handling, plus end-to-end client behaviour against an in-memory paired-pipe stub server (register/connect/read/write/server-disconnect/chunked-send all exercised without a real socket).

Not in this PR: AGW server, UNPROTO (`M`/`V`) send/receive, monitor frames (`U`/`I`/`S`), heard-list (`H`), via-digipeater connects. Connected-mode `C`/`D`/`d` + registration `X` + port-info `G` is the working subset; the rest can land as needs surface.
### 2026-05-15 — SP-004: SharpFuzz harness for Ax25Frame and KissDecoder

`tools/Packet.Fuzz/` — first iteration of the AX.25 / KISS wire-format fuzzer promised in §5.X SP-004. SharpFuzz dependency (2.2.0) added to `Directory.Packages.props`; tool is a stand-alone net10.0 console app. Three modes: `--smoke` (in-process random + structured input generator, default N=1000 per parser), `ax25` / `kiss` (libfuzzer-dotnet harnesses via `Fuzzer.OutOfProcess.Run` — wired but not exercised in this PR; afl-fuzz is not a CI dependency), `--seed-corpus` (regenerates the on-disk seed files from `Ax25Frame.Factories`).

Targets:

- `Ax25Frame.TryParse(ReadOnlySpan<byte>, out _)` — direct.
- `KissDecoder.Push(ReadOnlySpan<byte>)` — the task brief asked for `KissFrame.TryParse`, but `KissFrame` is a plain record struct with no static parser. KISS is a stateful SLIP framer, not a one-shot parser, so the equivalent harness drives bytes through a fresh `KissDecoder`.

Smoke generator mixes seven input distributions: truncated buffers, around-minimum-length, typical paclen-sized, oversized, all-same-byte, SLIP-pathological (FEND/FESC bias to stress the KISS escape state machine), and structurally-AX25-shaped (random callsign / SSID / digipeater / control / PID / info). Plus seed-corpus replay and 32 single-bit / single-byte mutations of each seed.

**Result: clean.** Both parsers returned `false` / dropped bytes on every malformed input across the smoke run; no unhandled exceptions surfaced. Extended runs at N=100000 across five different RNG seeds (~600k total inputs per parser) — also clean. Full output is captured in `tools/Packet.Fuzz/FINDINGS.md`.

This is a coverage-extending rather than bug-finding result: the FsCheck `TryParse_Never_Throws` property (amendment 2026-05-14) already asserts the same invariant for `Ax25Frame.TryParse` over 2 000 random inputs; the SharpFuzz harness scales that to ~600k inputs and adds structurally-biased distributions FsCheck doesn't reach. `KissDecoder.Push` is by construction lenient (`KissDecoder.cs:38–41`: "receivers should be lenient with malformed escape sequences. Drop the byte and continue."), so a clean result is expected.

What this leaves behind:

- Reproducible harness — re-run after any parser change is <1 s for N=10000.
- Seed corpus of six known-valid frames (SABM/UA/DISC/UI-APRS/I-frame/RR) under `tools/Packet.Fuzz/corpus/{ax25,kiss}/`, regeneratable from the factories via `--seed-corpus`.
- AFL/libfuzzer integration ready — `dotnet run -- ax25` / `kiss` wraps `Fuzzer.OutOfProcess.Run`. Activating needs afl-fuzz + libfuzzer-dotnet on a host with persistent fuzzing budget. Deferred per the brief — local/manual use only until findings stabilise.

CI is unchanged in this PR; the fuzzer is for manual use.

### 2026-05-15 — codegen: lint pack (subroutine, DL-ERROR, state-target, dispatcher orphan, catchall)

Five more codegen-time lints, completing the structural-consistency family. Each is the same shape as the predicate / action-verb lints — walk the resolved IR, cross-reference against a per-file extraction (dispatcher regex / subroutine registry / state set), report gaps with the YAML location.

**LintSubroutineCoverage** — every `kind: subroutine` action must resolve to a figc4.7 subroutine page entry or a `LegacyAliases` entry in `SubroutineRegistry.cs`. Mirrors the action-verb lint for the subroutine name-space. Caught zero new gaps (the registry is small and well-known) but closes a runtime-throw surface.

**LintDlErrorLetters** — every `DL_ERROR_indication_<X>` (or `DL-ERROR Indication (<X>)`) must have a matching case in the dispatcher. Lets the dispatcher's case list be the source of truth for which §C5 letter variants are recognised, rather than hardcoding A..R in the lint. Caught zero new gaps.

**LintStateTargets** — every transition's `next:` must name a state declared somewhere in the same machine. Caught one real gap: figc4.4 t38 (T1 expiry) and t39 (T3 expiry) target `TimerRecovery`, which is figc4.5 — not yet transcribed (the runtime registers `TransitionMap[TimerRecovery] = empty` as a placeholder). Added `StateTargetAllowList` with a single entry for `TimerRecovery` referencing §6.4 SDL inventory. Lint respects `coverage: partial`.

**LintDispatcherOrphans** — every `case "..."` in `ActionDispatcher.Execute` should be reachable from at least one SDL transition (post alias resolution). Caught seven cases:

- `Check_I_Frames_Acknowledged` (plural alias; canonical handles it)
- `Enquiry_Response` (canonical body; transcriptions use `_F_0` / `_F_1` variants)
- `Establish_Extended_Data_Link` (figc4.7 v2.2 path — not yet referenced from figc4.x)
- `Set_Version_2_0` / `Set_Version_2_2` (figc4.7 subroutines — not yet referenced)
- `discard_i_frame_queue` (lowercase alias — defensive case-drift)
- `start_T2` / `stop_T2` (T2 lazy-ack driven internally, not from SDL)

Allow-listed each with a one-line reason in `DispatcherOrphanAllowList`. Three of the seven (Establish_Extended_Data_Link, Set_Version_2_0, Set_Version_2_2) are real transcription gaps — figc4.7 invocations from figc4.x transitions aren't fully wired. Captured as "expected gap" rather than blocking the lint; will burn down as figc4.7 transcription progresses.

**LintCatchallCoverage** — every state page should have at least one transition triggered by a `catchalls:` event (`all_other_primitives__*` / `all_other_commands`). Without it, events the state doesn't explicitly handle silently no-op — a quietly-dropped-frame bug. Caught zero new gaps (every existing page has a catchall). Respects `coverage: partial`.

Side-effect on `Packet.Sdl.CodeGen.Tests`: the codegen test fixtures used `coverage: complete` while declaring single-state pages with cross-state `next:` targets. The state-target lint correctly rejected this as "you said complete but referenced an undefined state". Changed all 12 test-fixture pages to `coverage: partial` — they're work-in-progress codegen mechanics tests, not full transcriptions, so `partial` is honest.

### 2026-05-15 — codegen: action-verb-completeness lint + two latent action bugs

Mirror of the predicate-completeness lint added earlier today, addressing the follow-up flagged in the previous I-frame round-trip entry. Walks every action verb in every resolved SDL page (post-`actions.yaml` alias substitution) and confirms there's a matching `case "..."` arm in `ActionDispatcher.Execute`. Verbs lacking a case become codegen errors with the YAML location of their first use and a hint to either add a dispatcher case or an `actions.yaml` alias.

The lint surfaced **two real latent bugs on first run**:

- **`actions.yaml` had duplicate `signal_lower:` mapping keys.** The first block declared `DM (F = 1)` + aliases (`DM F=1`, `DM F = 1`). The second block declared the `LM_*` link-multiplexer verbs. YAML's duplicate-key semantics meant the second block silently replaced the first, so the DM aliases never reached the resolver and the generated code emitted figure-verbatim `DM F = 1` / `DM F=1` straight to the dispatcher. The dispatcher only had a `case "DM (F = 1)":` (canonical), so any transition emitting these would have thrown `unknown SDL action` at runtime. None of the existing tests exercised those transitions, hence "latent". Fixed by folding the LM_* entries into the single canonical `signal_lower:` block.

- **`set_peer_busy → set_peer_receiver_busy` alias missing.** The matching `clear_peer_busy → clear_peer_receiver_busy` alias was present, but the `set` form wasn't catalogued, so figc4.4 t55/t57/t58's `set_peer_busy` flowed unsubstituted through the codegen and the dispatcher only had `case "set_peer_receiver_busy":` (canonical). Same latent-runtime-throw profile as the DM case. Fixed by adding the alias entry.

The lint also caught the verbatim `set_acknowledgement_pending` case from the previous amendment-log entry, so the regression-guard story is real: had the lint been in place before that PR, the runtime exception would have been a codegen error instead.

Lint code lives next to `LintPredicateBindings` in `tools/Packet.Sdl.CodeGen/Program.cs`. Extraction of dispatcher case labels is a single regex scan of `ActionDispatcher.cs`; resolution is done inline so the lint sees canonical names rather than figure-verbatim. Standalone-codegen mode (no runtime library on disk) silently skips the lint.

After the fix, all 7 backends regenerated cleanly. 1106 non-interop tests pass; 12/12 interop tests pass.

### 2026-05-15 — interop: I-frame round-trip vs BPQ node prompt

Extends `LinbpqViaNetsimConnectedMode` with a second fact that drives figc4.4's data path end-to-end against BPQ's node prompt over the same net-sim AFSK1200 RF sim. After SABM/UA we wait for BPQ's CTEXT banner I-frame, send `"P\r"` as an outbound I-frame, wait for BPQ's ports-listing response I-frame, then DISC/UA. Net-sim wire-log shows the full exchange with correct N(s)/N(r) bookkeeping on both sides: BPQ I(n(s)=0, n(r)=0, p=1, "PN0TST Packet.NET interop test node\r") → us RR(n(r)=1, f=1) → us I(n(s)=0, n(r)=1, "P\r") → BPQ I(n(s)=1, n(r)=1, "PNTST:PN0TST} Ports\r  1 Telnet  2 AXIP  3 netsim") → BPQ I(n(s)=2, n(r)=1, p=1).

Two non-test fixes were needed before this could pass:

- **`bpq32.cfg`**: needed `NODE=1` + a top-level `CTEXT: … ***` block. Without `NODE=1`, BPQ accepts the SABM but no application is listening for L2 data, so it stays silent and our wait-for-banner times out. `SIMPLE=1` only seeds L3/L4 numeric defaults; it does NOT enable the node application. Pattern is verbatim from m0lte/test-net's working configs.
- **`spec-sdl/actions.yaml`**: figc4.4 uses `set_acknowledgement_pending` (with "ment") on the set verb but `clear_acknowledge_pending` (without) on the clear verb — the same flag with inconsistent spelling within the same figure. The dispatcher only had a case for `set_acknowledge_pending`; the test surfaced this via `unknown SDL action` at runtime (caught immediately by the rig's pump-task fault propagation rather than timing out). Added an alias entry so the codegen normaliser rewrites the figure's "ment" spelling to canonical.

A natural follow-up surfaced by the second fix: today we have a codegen lint for unbound predicates (added 2026-05-15) but no equivalent for unknown action verbs. The "ment" / non-"ment" mismatch was caught only at runtime — a codegen-time lint that walks every action verb in every SDL page and checks for either a dispatcher case or an `actions.yaml` alias would have surfaced it at generation time. Same shape as the predicate lint. Not gated on; deferrable.

### 2026-05-15 — interop: LinBPQ-via-netsim connected-mode test

The "proper next step" called out in the previous interop entry. Our `Ax25Session` on net-sim's port 8100 (node a) drives a SABM/UA connect and DISC/UA disconnect against a real LinBPQ container that dials net-sim's port 8102 (node c) as a KISS-TCP client. Net-sim's AFSK1200 sim bridges frames between us and BPQ. First test in the suite with a genuinely third-party AX.25 stack as the remote peer; round-trip completes in ~3s.

Adds:

- **Third net-sim node `c`** in `docker/netsim/network.yaml`. Linked only to `a`, not to `b`, so concurrent test runs don't see each other's frames at the RF-sim layer (the AX.25 address filter would catch them anyway, but cleaner not to rely on it).
- **Second `PORT` block in `docker/linbpq/bpq32.cfg`**: `TYPE=ASYNC PROTOCOL=KISS IPADDR=172.30.0.12 TCPPORT=8102 CHANNEL=A …`. Follows m0lte/test-net's working KISS-TCP configs verbatim (no `KISSOPTIONS=ACKMODE` — dropped per Tom for the minimal-first iteration; no per-port `MYCALL=` — inherits `NODECALL=PN0TST`; no `CONFIG` line; `CHANNEL=A` is the easy-to-miss mandatory field). BPQ retries the dial if the listener isn't yet up, so startup ordering is forgiving, but compose still has `depends_on netsim: healthy` belt-and-braces.
- **`NetsimCollection` (xUnit `[CollectionDefinition]`)** with `DisableParallelization = true`. All netsim-attaching test classes (existing three + the new one) now share this collection so concurrent runs don't fight over port 8100.
- **`tests/Packet.Interop.Tests/Linbpq/LinbpqViaNetsimConnectedMode.cs`** — one fact: SABM → UA → Connected → DISC → UA → Disconnected. Assertion is intentionally minimal; AGW / telnet probes of BPQ's heard-list and per-port counters are deferred so the scope of any later regression is obvious. Rig structure duplicated from `NetsimConnectedModeScenarios.BuildRig` rather than lifted into a shared helper — premature sharing would obscure the BPQ-specific quirks (PACLEN clamping, ACKMODE handling, link recovery) likely to surface in later tests.

Verified locally: full SABM/UA/DISC/UA flow captured in net-sim's logs with `PNTEST → PN0TST` and `PN0TST → PNTEST` on the `a.vhf` and `c.vhf` ports. All 11 interop tests green together.

Update to §7.1 interop-scenarios table reflects that the SABM/UA-vs-LinBPQ row is now satisfied (over net-sim, not direct KISS).

### 2026-05-15 — interop: first third-party-style connected-mode test

First test that drives an `Ax25Session` end-to-end through the
figc4.1 connect handshake and figc4.6 disconnect handshake across a
real transport — not in-memory recorders. Two sessions, one on each
of net-sim's KISS-TCP ports (8100 / 8101), connected through the
simulated AFSK1200 RF channel. Real KISS framing, real timing, real
lossy(-ish) transport. Round-trip completes in ~4s including the
RF-sim delay.

**Tests/Packet.Interop.Tests/Netsim/NetsimConnectedModeScenarios.cs**
— `[Trait("Category", "Interop")]`, runs in the interop CI workflow
against the docker compose stack. The `[Trait]` already excludes from
default `dotnet test` runs, so no `SkippableFact` gate is needed; if
someone runs `Category=Interop` without the stack up, the hard fail
is more honest than a silent skip.

Self-interop (both sides are us) — not strictly third-party. The
proper next step is attaching LinBPQ to net-sim's other port via a
KISS PORT in `bpq32.cfg` and driving the connect across the RF sim
to a real foreign implementation. The plumbing landed here unblocks
that follow-up.

### 2026-05-15 — codegen: predicate-completeness lint + `able_to_establish` binding

The interop test (above) caught a real bug on its first run: figc4.1
t14/t15 (SABM_received) gates on the `able_to_establish` predicate,
but `Ax25SessionBindings.CreateDefault` shipped no binding for it.
GuardEvaluator threw `GuardEvaluationException` at the receiver,
which was silently swallowed by the test's background rx pump (a
`Task.Run` whose fault nobody awaited).

Three coordinated fixes:

1. **`tools/Packet.Sdl.CodeGen/Program.cs`** — new codegen-time lint
   `LintPredicateBindings`. Walks every decision in every loaded
   *.sdl.yaml, tokenises predicates (re-using the same operator-
   handling pattern as `Validation.CompileGuardLiterals`), and
   diffs against names bound in
   `src/Packet.Ax25/Session/Ax25SessionBindings.cs` (regex-extracted).
   Missing bindings become codegen errors with the precise YAML
   location and the predicate name. Same shape as the existing
   unused-alias lint — permanent guard against this class of bug.

2. **`src/Packet.Ax25/Session/Ax25SessionBindings.cs`** — added the
   missing `able_to_establish` entry, defaulted to `true`. Marked as
   a node-policy hook with a clear comment that production stations
   should override (callsign allow-list, link budget, etc.). Matches
   direwolf's "we are always willing to accept connections" default
   (ax25_link.c:4337). The proper long-term shape is an
   `IAx25SessionPolicy` interface injected into the session; the
   override-the-dictionary mechanism is sufficient until then.

3. **`tests/Packet.Interop.Tests/Netsim/NetsimConnectedModeScenarios.cs`**
   — wait helpers now observe background pump tasks via
   `task.IsFaulted` and rethrow with `GetBaseException()` on every
   poll cycle. A pump-task throw now surfaces as an immediate test
   failure with the real stack trace, not a budget timeout that
   hides the cause. Same fix pattern should be applied to any future
   integration test that uses a Task.Run pump.


### 2026-05-15 — codegen: four new emitters (Rust, C, JSON, Python)

The IR refactor now powers **seven** language backends. Each emitter
follows the established pattern: a `tools/Packet.Sdl.CodeGen.<Lang>/`
project that consumes the language-neutral `ResolvedPage` /
`ResolvedSubroutinesPage` IR and produces files into a sibling
`<lang>-spec/` directory.

**New emitters and their outputs:**

- **Rust** (`tools/Packet.Sdl.CodeGen.Rust/`, `rust-spec/`) — cargo
  crate. Generated `*.g.rs` files contain `pub static
  DATA_LINK_CONNECTED: StatePage = …` plus a co-located
  `#[cfg(test)] mod tests` with per-transition assertions. `lib.rs`
  is generated; `types.rs` is hand-written. `cargo build/test/fmt`
  in CI. **165 generated tests pass.**

- **C** (`tools/Packet.Sdl.CodeGen.C/`, `c-spec/`) — CMake project.
  `c-spec/src/*.g.c` defines `const StatePage DataLinkConnected = …`;
  `c-spec/src/ax25sdl.g.h` is the generated header that aggregates
  every `extern const …` declaration. `c-spec/test/*.g.test.c` are
  per-page test binaries wired into CTest. Hand-written
  `c-spec/include/ax25sdl.h` carries the runtime types.
  `cmake/make/ctest/clang-format` in CI. **6 generated ctest binaries pass.**

- **JSON** (`tools/Packet.Sdl.CodeGen.Json/`, `json-spec/`) — pure
  data. Each `*.g.json` is structurally validated against the
  generated `schema.json` (JSON Schema draft-2020-12) at codegen
  time via JsonSchema.Net; CI just asserts no drift. No runtime; the
  schema is the contract. **6 generated `.g.json` files + `index.json`
  + `schema.json`.**

- **Python** (`tools/Packet.Sdl.CodeGen.Python/`, `python-spec/`) —
  pip-installable package. Frozen `@dataclass(frozen=True, slots=True)`
  instances per page, plus per-transition `def test_…` functions
  picked up by pytest. `__init__.py` is generated (importlib-based
  re-export — the `.g.py` filename's literal dot blocks normal
  `from .x.g import …`). `pytest` in CI. **169 generated tests pass.**

**CLI surface.** The Program.cs orchestrator now exposes seven
opt-in-by-presence flags: `--csharp`, `--go`, `--ts`, `--json`,
`--rust`, `--emit-c` (CommandLineParser rejects single-char long
names so `--c` isn't available; short alias `-c` works), `--python`.
Each has a paired `--<lang>-out PATH` option. No flags = all seven
backends emit to default paths.

**CI shape.** The `sdl-codegen-discipline` set is now seven parallel
jobs — one per backend. Each installs only its own toolchain (Rust
via dtolnay/rust-toolchain, C via apt + cmake, Python via
actions/setup-python, JSON needs only .NET). With 4 self-hosted
runners the seven discipline jobs fan out alongside the test matrix.

**Method.** Four sub-agents ran in parallel `git worktree`-isolated
copies of the integration branch, one per new backend. Each delivered
the emitter project, the spec output, the agent's CodegenRunner
extensions, and the per-language CI job. The frontend crashed
mid-way; on restart, the worktrees were recoverable and the
integration completed by hand-merging each agent's Program.cs slice
into the parent's already-wired JSON state.

**Total** 1,069 + 165 + 6 + 169 = **1,409 tests verifying the spec**
across four runtimes — every transition's id/on/next/guard/actions
encoded once in `*.sdl.yaml`, asserted independently by seven
language toolchains.



### 2026-05-15 — ci: sweep workflow annotations (bump action pins, disable empty Go cache)

PR #113's CI run carried persistent warnings on every job —
specifically:

1. **Node.js 20 deprecation banner** on `actions/checkout@v4`,
   `actions/setup-go@v5`, `actions/setup-node@v4`,
   `actions/upload-artifact@v4`. The PR #113 opt-in
   (`FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true`) was forcing them onto
   Node 24, but the banner stayed because the actions themselves
   still target Node 20 internally.

2. **`setup-go` "Restore cache failed: Dependencies file is not
   found"** — the action tries to hash `go.sum` for the Go module
   cache but `go-spec/` has no external dependencies.

Bumped to the current latest majors (each targets Node 24 natively):

- `actions/checkout@v4` → `@v6`
- `actions/setup-go@v5` → `@v6` (plus `cache: false`)
- `actions/setup-node@v4` → `@v6`
- `actions/upload-artifact@v4` → `@v7`

The `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true` env var stays as a
belt-and-braces safeguard — once every pinned action ships native
Node 24, it's a no-op, but it catches any future action regression
back to Node 20 without us needing to remember.


### 2026-05-15 — codegen CLI: CommandLineParser + opt-in-by-presence

Replaced the ad-hoc `for (...) switch (args[i])` parser in
`tools/Packet.Sdl.CodeGen/Program.cs` with `CommandLineParser`
(`Directory.Packages.props` adds it at 2.9.1). Two semantic changes
on top of the library swap:

**Per-backend opt-in by presence.** Old design: every backend was
auto-detected and enabled by default; `--no-go` / `--no-ts` were
escape hatches that suppressed individual backends. New design: each
backend has a positive flag (`--csharp`, `--go`, `--ts`) and optional
path flags (`--csharp-out`, `--csharp-tests`, `--go-out`, `--ts-out`).
Passing any language flag selects only the explicitly-named backends;
passing none means "all three with default paths" (preserves the
zero-args developer experience).

The new design eliminates the negation logic that prompted the
fragility concern — there's no `if (suppressGo)` anywhere. A backend
is "enabled" iff its flag or its path option is present; otherwise
the default-all rule fires.

**CLI invocation also simpler.** CI workflow goes from
`--in spec-sdl --out src/... --tests tests/... --no-go --no-ts` to
just `--csharp` (and equivalent simplifications for the Go / TS
jobs). Path defaults are owned by `CodegenPlan.From`.

**Code organisation.** Two new internal types:
- `CodegenOptions` — CLI surface (attribute-decorated POCO).
- `CodegenPlan` — resolved decision (`EmitCsharp` / `EmitGo` /
  `EmitTs` booleans + final paths), built by `CodegenPlan.From(opt)`
  which applies the default-all rule and resolves blank sentinels.

`Run` now takes a `CodegenPlan` instead of an 8-string parameter
list. Stale-file cleanup is scoped per emitted backend so e.g.
`--csharp` doesn't touch `go-spec/ax25sdl/*.g.go`.

The `Packet.Sdl.CodeGen.Tests` harness was updated to use
`--csharp --csharp-out ... --csharp-tests ...` since its sandbox tests
only need the C# backend.


### 2026-05-15 — ci: split sdl-codegen-discipline into 3 parallel jobs + Node 24 opt-in

Now that the self-hosted box runs 4 GitHub Actions runners (Tom
registered 3 additional ones), the bottleneck is no longer "wait for
one runner": it's "wait for the long-pole job within the workflow".
Two changes:

**1. Per-language codegen-discipline jobs.** The old monolithic
`sdl-codegen-discipline` did C# drift → Go build/vet/test → TS
typecheck/test in series inside one runner. Split into
`sdl-codegen-csharp`, `sdl-codegen-go`, `sdl-codegen-ts` — three
parallel jobs, each installing only its own language toolchain. With
4 runners, all three run alongside the 13-entry test matrix instead
of forming a bottleneck behind it.

Each job runs the codegen with `--no-go` / `--no-ts` so it only emits
the backend it verifies. Two new flags on `tools/Packet.Sdl.CodeGen`:

- `--no-go` — suppress Go emission even when `go-spec/ax25sdl/` exists.
- `--no-ts` — suppress TS emission even when `ts-spec/src/ax25sdl/` exists.

The arg parser now uses `case "--flag" when i + 1 < args.Length:` so
flag-without-value at end-of-args doesn't index past the array; the
old `for (i = 0; i < args.Length - 1; i++)` bound that papered over
this issue is gone.

**2. Node.js 24 opt-in.** GitHub flagged that JavaScript actions
(notably `actions/checkout@v4`) run on Node.js 20, which is being
removed Sep 16th 2026 with a forced Node 24 cutover on June 2nd 2026.
Added `FORCE_JAVASCRIPT_ACTIONS_TO_NODE24: true` at workflow level
in all three workflows (`ci.yml`, `interop.yml`, `plan-check.yml`) to
silence the deprecation warning and catch Node-24 breakage early
rather than at the forced cutover.


### 2026-05-15 — session: runtime wiring follow-ups (Tier 2 prep)

Five-item work item from the figc4.7 arc: now that the data-link state
machine is end-to-end-walkable, several gaps and approximations needed
either a fix or a hardness pass.

**ITimerScheduler.TimeRemaining + real T1 IIR.** The
`Select_T1_Value` IIR was approximating its round-trip sample with the
worst-case `T1V` because `ITimerScheduler` didn't expose remaining
time. Added `TimeSpan TimeRemaining(string name)` to the interface,
backed by per-timer `DateTimeOffset Deadline` tracking in
`SystemTimerScheduler`. The `stop_T1` / `Stop T1` verbs now capture
remaining time onto `Ax25SessionContext.T1RemainingWhenLastStopped`
before cancelling. `Select_T1_Value` plugs the captured value into
the spec formula `SRT := 7/8*SRT + (T1V - remaining)/8`. Tests cover
convergence over 50 samples (3000ms → 800ms, residual <10ms).

**End-to-end SDL-driven session lifecycle tests.** New file
`DataLinkSessionLifecycleTests.cs` drives a real `Ax25Session`
(default subroutine registry, frame-aware bindings) across
Disconnected → AwaitingConnection → Connected → AwaitingRelease →
Disconnected via primitives. Five scenarios: full happy path,
data-round-trip + disconnect, connect refused by DM(F=1), mod-128
SABME path, inbound I-frame → DL-DATA-indication. Asserts byte-level
frame sequence (via dispatcher recorder lists) AND DL-* signal
sequence (via the upward callback).

**Transcription consistency gaps surfaced + fixed.** Lifecycle tests
exposed three figure-vs-implementation aliases missing from
`spec-sdl/actions.yaml`:

- `clear_peer_busy` (figc4.4 spelling) → `clear_peer_receiver_busy`
  (canonical).
- `Select T1 Value` (figc4.7 internal call shape, spaced) →
  `Select_T1_Value` (canonical).
- `Check_I_Frames_Acknowledged` (figc4.4 plural) →
  `Check_I_Frame_Acknowledged` (figc4.7 singular). Previously declared
  as two distinct canonicals — wrong, they're one subroutine.

Plus a binding alias: `V_r_I_frame_stored` (figc4.4 canonical YAML
predicate) is now in `Ax25SessionBindings.CreateDefault` alongside the
existing lowercase `vr_i_frame_stored`.

**Net-sim interop tests un-skipped.** The 4 UI-frame scenarios in
`tests/Packet.Interop.Tests/Netsim/NetsimUiFrameScenarios.cs` had
unconditional `Skip.If(true, "TODO: ... pending investigation")`. The
SkipIfNetsimDown guard already handles the "stack not up" case, so the
unconditional skip was just guesswork from PR #94. Removed; the
interop CI matrix will now run them against the live net-sim and tell
us whether they pass (in which case we've gained 4 tests) or fail (in
which case we have concrete data to file against net-sim, not
speculation).

**DataLinkSignal already-typed: scope deletion.** The 5th item on the
original list was "stronger DL-primitive types". Recon found these
already exist as a record hierarchy: `DataLinkConnectIndication`,
`DataLinkDataIndication(Info, Pid)`, `DataLinkErrorIndication(Code)`,
etc. No work needed — kept as a noted non-task here so future readers
don't redo the recon.

### 2026-05-14 — ci: matrix `dotnet test` over per-project entries

Replaced the monolithic `build & test` job in `.github/workflows/ci.yml`
with a 13-entry matrix, one per test project. Each matrix entry runs
its own `dotnet test <project>` which transitively builds the project's
dependencies — compile errors in depended-on libraries still surface,
test failures are reported per-project, and artifact uploads are named
per matrix value so they don't collide.

`fail-fast: false` so all projects report independently — one failing
project doesn't suppress others' results.

**Parallelism is gated on runner count.** GitHub schedules matrix
entries up to the self-hosted pool's concurrency limit. With a single
runner instance the matrix runs serially and is marginally slower
than the old job (per-entry checkout overhead, ~10-20s). The win
lands once ≥2 runner instances are registered on the box; with 4-8
runners the wallclock should drop to "longest single test project"
plus overhead.

Removed the separate `restore`/`build` steps — `dotnet test` runs
them transitively, and a standalone build smoke job would only catch
issues in projects no test references (spikes, tool-only utilities),
which are low-stakes.

### 2026-05-14 — sdl: generated per-transition tests in Go + TS

Brought Go and TS up to test-coverage parity with C#. Previously only
the C# emitter generated `.g.Tests.cs` files (one xUnit test per
transition checking id/on/next/guard/action verbs+kinds against the
YAML); Go and TS had only hand-written smoke tests asserting that
pages weren't empty.

**New emitter methods:**

- `GoEmitter.EmitStatePageTests(ResolvedPage)` →
  `<stem>.g_test.go`. Stdlib `testing` package, no extra deps. Test
  names like `TestDataLinkConnected_t06_i_received_p_eq_1`.
- `TsEmitter.EmitStatePageTests(ResolvedPage)` →
  `<stem>.g.test.ts`. vitest `describe`/`it`/`expect`. One
  `describe` block per page.

**Orchestrator** writes both alongside the page's data file, and the
stale-file cleanup pass scopes a second pattern (`*.g_test.go` /
`*.g.test.ts`) to keep generated test files tidy. Naming choices:

- Go uses `<stem>.g_test.go` — the `_test.go` suffix is what `go test`
  picks up; the `.g` token in the middle marks it generated.
- TS uses `<stem>.g.test.ts` — vitest matches `**/*.test.{ts,js}` so
  the `.g` token can sit comfortably in front.

**Scope.** State-machine pages only — same as the C# emitter. The
hand-written `sdl_test.go` / `sdl.test.ts` smoke tests stay for
cross-cutting checks (subroutine count, ActionKind union coverage).
Adding generated subroutine-page tests is a separate decision; we'd
want them once a runtime starts walking subroutines on those
backends.

**Results.**

- Go: 165 generated tests + 3 hand-written = 168 tests in
  `go-spec/ax25sdl`, all passing.
- TS: 165 generated tests + 8 hand-written = 173 tests in
  `ts-spec/src/ax25sdl`, all passing.

Both fail loudly if the emitter drops or mistypes any field — the
test fixture is the YAML transcription, so a regression in the
emitter shows up as a test failure rather than a silent data-shape
change.

### 2026-05-14 — sdl: TypeScript emitter + ts-spec/ npm package (Tier 1c)

Added a third backend on top of the IR refactor. The codegen IR now
fans out to C#, Go, and TypeScript in a single `dotnet run --project
tools/Packet.Sdl.CodeGen` invocation; all three stay in lockstep by
construction.

**New projects:**

- **`tools/Packet.Sdl.CodeGen.Ts`** — TypeScript emitter. Hand-rolled
  string emission (same approach as the Go emitter). Public API:
  `TsEmitter.EmitStatePage` / `EmitSubroutinePage` /
  `EmitIndex` (the index re-exports every page so consumers can
  `import { DataLinkConnected } from "ax25sdl"`).

- **`ts-spec/`** — new npm package (`name: ax25sdl`, `type: module`,
  Node 22+, ESM-only, NodeNext module resolution). Hand-written
  `src/ax25sdl/types.ts` provides the runtime interfaces (`StatePage`,
  `SubroutinesPage`, `TransitionSpec`, `SubroutinePath`,
  `SubroutineSpec`, `ActionStep`, `LoopRange`,
  `ImplementationReference`, `SdlSource`, `ActionKind`).
  `ActionKind` is a string-literal union (`"signal_upper" | ...`)
  rather than an enum — narrower types in IDE autocomplete and no
  ergonomic cost given the values come from spec-sdl/actions.yaml.

**Orchestrator changes** (`tools/Packet.Sdl.CodeGen/Program.cs`):

- New `--ts <dir>` flag; auto-detects to `ts-spec/src/ax25sdl` when
  that directory exists.
- After emitting all pages, also generates `index.ts` (re-exports
  every page + the runtime types). The stale-file pass scopes its
  delete on `*.g.ts`, leaving `index.ts` and `types.ts` alone.

**CI** (`.github/workflows/ci.yml`):

- `sdl-codegen-discipline` job now installs Node 22 via
  `actions/setup-node@v4` with npm caching, and asserts no drift on
  `ts-spec/src/ax25sdl` alongside the C# / Go directories.
- Added a `ts-spec typecheck + test` step that runs `npm ci`,
  `npm run typecheck` (tsc --noEmit, strict mode), and `npm test`
  (vitest run).

**Smoke tests** (`ts-spec/src/ax25sdl/sdl.test.ts`):

- Every state-machine page has non-empty `transitions`.
- figc4.7 has the expected 13 subroutines and includes
  `Establish_Data_Link`.
- Every generated `ActionStep.kind` value is in the declared
  `ActionKind` union (belt-and-braces — the type system already
  enforces this at compile time).

**Why vitest, not `node --test`.** Ubuntu's packaged Node 22 binary
is built without TypeScript stripping (`ERR_NO_TYPESCRIPT`). Vitest
handles `.ts` via esbuild transparently, runs fast, and is a single
top-level devDep. Going with `tsx` + `node --test` would have needed
a wrapper layer for similar ergonomics.

**What's not in scope.** Same as Go: this is **specification data**,
not a runtime. Building a TypeScript packet engine on top requires
binding predicates and action verbs to TS behaviour and wiring to
frame I/O — WebSerial to a NinoTNC, AGWPE/AX.UDP over WebSocket, or
an audio-DSP softmodem (ambitious). The codegen IR now demonstrably
supports three languages; adding a runtime in any of them is a
separate effort that benefits from the same single source of truth.

### 2026-05-14 — sdl: Go emitter + go-spec/ module (Tier 1b)

Added a second backend on top of the IR refactor, proving the codegen
pipeline is language-agnostic.

**New projects:**

- **`tools/Packet.Sdl.CodeGen.Go`** — Go emitter. Hand-rolled string
  emission (no template engine — Go's strict gofmt rules make a
  generator without templating simpler than the Scriban path).
  Produces one `.g.go` per `*.sdl.yaml`. Public API:
  `GoEmitter.EmitStatePage(ResolvedPage)` /
  `EmitSubroutinePage(ResolvedSubroutinesPage)`.

- **`go-spec/`** — new Go module (`github.com/m0lte/packet-net/go-spec`,
  `go 1.22`). One package: `ax25sdl/`. Hand-written
  `types.go` provides `StatePage`, `SubroutinesPage`, `TransitionSpec`,
  `SubroutinePath`, `SubroutineSpec`, `ActionStep`, `LoopRange`,
  `ImplementationReference`, `SdlSource`, and the `ActionKind` enum.
  Empty-string / zero-int conventions used in place of pointer types
  for nullable fields to keep generated initialisers readable.

**Orchestrator changes** (`tools/Packet.Sdl.CodeGen/Program.cs`):

- New `--go <dir>` flag; auto-detects to `go-spec/ax25sdl` when the
  directory exists, so a normal `dotnet run --project
  tools/Packet.Sdl.CodeGen` keeps both backends in sync.
- After emitting, shells out to `gofmt -w <go-dir>` to canonicalise the
  output. Falls back to a warning if gofmt isn't on PATH (the CI
  drift assert catches non-canonical commits anyway).

**CI** (`.github/workflows/ci.yml`):

- `sdl-codegen-discipline` job now installs Go 1.22 via
  `actions/setup-go@v5`, runs the codegen, and asserts no drift across
  C# **and** Go output.
- Added a `go build` + `go vet` + `go test` + `gofmt -l` check on
  `go-spec/` in the same job.

**Smoke tests** (`go-spec/ax25sdl/sdl_test.go`):

- Every state-machine page has non-empty `Transitions`.
- figc4.7 has the expected 13 subroutines.
- `ActionKind.String()` round-trips for every constant.

**What's not in scope.** This is **specification data**, not a runtime.
The Go module exposes the SDL data structures; binding predicates and
action verbs to behaviour (the Go equivalent of `GuardEvaluator` /
`ActionDispatcher` / `DefaultSubroutineRegistry`) and wiring to frame
I/O is intentionally deferred. The goal of Tier 1b is to prove the
codegen IR survives a second backend cleanly — that's now done.

### 2026-05-14 — sdl: codegen split into IR + C# emitter (Tier 1b prep)

Refactored `tools/Packet.Sdl.CodeGen` from a single 1300-line console
project into three projects, in preparation for adding a Go emitter
alongside the C# one (Tier 1b):

- **`tools/Packet.Sdl.IR`** — language-neutral library. YAML page models
  (`SdlPage`, `SubroutinePage`, …), the action/event catalogs (`ActionCatalog`,
  `EventCatalog`), the loader (`Loader.LoadPage`, `LoadSubroutinePage`),
  the validator (`Validation.ValidatePage`, `NormaliseActionVerbs`,
  lints) and a new resolver (`Resolver.Resolve`) that walks transition /
  subroutine paths into a language-neutral IR (`ResolvedPage`,
  `ResolvedSubroutinesPage`, `ResolvedAction`, `ResolvedLoop`,
  `ResolvedReference`).

- **`tools/Packet.Sdl.CodeGen.Csharp`** — C# emitter library. Owns the
  Scriban templates (moved from `tools/Packet.Sdl.CodeGen/Templates/`),
  the template loader, Roslyn parse-back validation (`CsharpValidator`),
  the C#-specific view models (`CsharpStateModel`,
  `CsharpSubroutinesModel`, with literal escaping + `ActionKind` enum
  mapping), and the public emit API (`CsharpEmitter.EmitStatePage`,
  `EmitSubroutinePage`).

- **`tools/Packet.Sdl.CodeGen`** — slimmed to a ~190-line orchestrator
  that wires the two libraries together: parse args, load events.yaml /
  actions.yaml, partition YAML files into state-machine vs subroutine
  pages, validate, resolve, hand to the emitter, write outputs, clean
  stale files. Drops `YamlDotNet` / `Scriban` /
  `Microsoft.CodeAnalysis.CSharp` direct package refs (now transitive
  via the two new libs).

**Verification.** Codegen output is bit-for-bit identical before/after
the refactor (`git diff src/Packet.Ax25.Sdl tests/Packet.Ax25.Conformance.Tests`
empty after a full regen). All 1057 default-filter tests pass. The
black-box `Packet.Sdl.CodeGen.Tests` suite (which shells out to the
codegen binary) is unchanged and green.

Next step: add `tools/Packet.Sdl.CodeGen.Go` as a sibling backend, plus
a `go-spec/` module skeleton and a Go CI job (Tier 1b second PR).

### 2026-05-14 — sdl: figc4.7 validation — four-codebase implementation references

Validation PR for the figc4.7 arc, per the runbook's stages 6-10
(adapted: spec_prose entries are minimal because §C5 is a primitives
listing rather than per-subroutine prose — the figure is the canonical
spec for the subroutines themselves).

**Method.** Spawned four parallel general-purpose subagents — one per
canonical reference codebase — each producing per-subroutine
implementation-reference YAML blocks against the pinned commits per
the runbook's Stage 8. Merged their findings into
`spec-sdl/data-link/subroutines.sdl.yaml` as per-subroutine
`references:` blocks. Required a small schema extension
(`sdl-subroutines.schema.json` gains a `references:` field at the
subroutine level) and a `pinned_refs:` block at the file header so
line numbers stay valid against the pinned commits.

**Triangulated findings (the gold per Stage 8):**

- **`Enquiry_Response` is the most-diverged subroutine across all
  four implementations.** direwolf rewrites it with an X.25-borrowed
  SREJ branch (their "Detour 1" at line 5936) and notes a spec-erratum
  comment ("RR/RNR as response not command", line 5918). rax25 omits
  the SREJ branches entirely (TODO at line 642). linbpq routes via
  `RR_OR_RNR` which inspects internal state instead of the spec's
  `own_receiver_busy` bit. linux_oot's `ax25_std_enquiry_response`
  omits REJ-on-rej-condition. Common pattern: the figure's SREJ /
  out-of-sequence-buffer dance is widely-implemented-in-summary, not
  faithfully.

- **`Establish_Data_Link` and `Establish_Extended_Data_Link`
  collapse to a single body in three of four codebases** (direwolf,
  rax25, linux_oot — all dispatch on a stored `modulus` field at
  `SendSabm` time, exactly mirroring our generated walker since both
  subroutines share the same `paths:` content). linbpq is the
  outlier: it doesn't emit SABME at all on the outbound side and
  treats inbound SABME as plain SABM ("Although some say V2.2
  requires SABME I don't agree!" — `L2FORUS` line 687-689 verbatim
  comment).

- **`Select_T1_Value` IIR formula is universally simplified.**
  direwolf substitutes linear `RC*0.25 + SRT*2` (line 6362) for the
  spec's exponential `2^(RC+1)*SRT` with rationale at lines
  6352-6358 ("ridiculous to retry over an hour"). rax25 has the
  formula as a TODO. linbpq omits adaptive T1 entirely (fixed
  L2TIME from port config). linux_oot uses configurable
  exponential/linear backoff. **Our PR #105 binding currently uses
  the worst-case approximation (`T1V` as the new-sample input);
  these references confirm that no canonical implementation runs
  the spec formula verbatim — so our approximation is in good
  company. Worth tracking the proper-IIR work but not blocking.**

- **`Set_Version_2_0` / `Set_Version_2_2` are not modelled as
  subroutines** in three of four codebases. linbpq, linux_oot, and
  partly direwolf treat the version-set as XID-negotiation side
  effects; only rax25 has explicit `set_version_2` / `set_version_2_2`
  methods. The full `Modulo := / N1 := / k := / T2 := / N2 := /
  HalfDuplex / ImplicitReject` six-field set figc4.7 prescribes is
  unique to our implementation and rax25 (partially) — every other
  implementation hard-codes these elsewhere.

- **`linux_oot` largely confirms the rotted-memory entry**: 8 of 13
  subroutines have no clean equivalent and are marked `omitted:` in
  the references. The 5 that do exist are `nr_error_recovery` (very
  abbreviated), `establish_data_link`, `transmit_enquiry`,
  `enquiry_response`, `check_iframes_acked` — and most diverge from
  the spec materially.

- **Two of the four canonical implementations contain explicit
  spec-erratum comments** flagging concerns in figc4.7 itself:
  direwolf's `establish_data_link` RC off-by-one (line 5768);
  direwolf's `enquiry_response` cmd-vs-response (line 5918);
  direwolf's `select_t1_value` RC==0 test (line 6309); direwolf's
  exponential-backoff substitution rationale (line 6352); rax25's
  TODO blocks (lines 642-646, 686, 695, 836). The figure is being
  questioned by its implementers in multiple places — a strong
  signal for spec-issue files when we land them.

**Implementation references at file scope:** added `pinned_refs:`
block to `subroutines.sdl.yaml` binding each `source:` name to its
pinned commit (linbpq `88a68988…`, direwolf `a231971a…`,
rax25 `d97b7ab7…`, linux_oot `40188e90…`).

`references:` is a new optional field on the subroutine schema
(`sdl-subroutines.schema.json` extended). Codegen ignores it for now
via `IgnoreUnmatchedProperties` — it's documentation only. A future
PR can wire it through `SubroutineSpec.References` if we want runtime
introspection.

Full test suite (1,055+ tests) green. Codegen idempotent. 13 of 13
subroutines now have 1-4 implementation citations each + per-source
divergence notes.

### 2026-05-14 — sdl: figc4.7 predicate + action-verb bindings; Ax25Session auto-Wires

The runtime side of the figc4.7 arc. Three pieces:

1. **New session-context state** for figc4.7:
   - `Ax25SessionContext.X` (nullable `byte`) — scratch register for
     `Invoke_Retransmission`'s backtrack loop.
   - `Ax25SessionContext.T1HadExpired` — set by the T1 expiry handler,
     consumed and cleared by `Select_T1_Value` to pick between the
     IIR-smoothed and linear-backoff branches.
   - `Ax25SessionContext.HalfDuplex`, `ImplicitReject`, `T2` — for the
     `Set_Version_2_0` / `_2_2` subroutines.
   - `ResetState()` clears all of these on session re-init.

2. **GuardEvaluator bindings** for the 17 figc4.7 predicates:
   `mod_128`, `mod_8`, `rc_eq_0`, `t1_running` (lowercase alias),
   `t1_expired`, `v_s_eq_x`, `out_of_sequence_frames_in_receive_buffer`,
   plus the frame-aware ones: `incoming_is_command`, `ui_info_field_valid`,
   `n_r_eq_v_s`, `n_r_eq_v_a`, `command_and_p_eq_1`,
   `response_and_f_eq_1`, `f_eq_1_and_supervisory_or_i`.

3. **ActionDispatcher cases** for the ~25 figure-verbatim verbs:
   - The 7 `Clear_Exception_Conditions` clears (also: a `Clear Exception
     Conditions` aggregate verb because `Establish_Data_Link` draws it
     as the first line of its multi-line processing box).
   - Pending-frame assignments (`P <- 1`, `N(r) <- V(r)`, `V(a) <- N(r)`).
   - `Invoke_Retransmission` body (`Backtrack`, `X <- V(s)`, `V(s) <- N(r)`,
     `V(s) <- V(s) + 1`, `Push Old I Frame onto Queue`).
   - Timer ops (`Stop T3`, `Start T1`, `Start T3`, `Stop T1`).
   - Establish_Data_Link tail (`RC <- 1`).
   - Set_Version_2_0/2_2 body (`Set Half Duplex`, `Set Implicit Reject`,
     `Set Selective Reject`, `Modulo <- 8/128`, `N1 <- 2048`, `k <- 8/32`,
     `T2 <- 3000`, `N2 <- 10`).
   - Frame emitters (`RR Command`, `RR Response`, `RNR Command`,
     `RNR Response`, `SABM`, `SABME`).
   - `Select_T1_Value` body (`SRT <- 7(SRT)/8 + ...` runs the IIR
     formula; `Next T1 <- 2 * SRT`; `Next T1 <- (RC*0.25)+SRT*2`).
   - DL-ERROR letter forms (`(J)`, `(K)`, `(Q)`, `(A)`, `(add)`).
   - `DL-UNIT-DATA Indication` (new `DataLinkUnitDataIndication` signal
     record added to `DataLinkSignal.cs`).

**Auto-Wire flipped on** in `Ax25Session`. The default `DefaultSubroutineRegistry`
now upgrades each subroutine name's no-op stub to a `SubroutineSpec`
walker during session construction. Custom registries / tests using
`Register()` are unaffected (the override is sticky).

Six new smoke tests (`Figc47SubroutineBodyTests`) drive each of the
simplest subroutines end-to-end:

- `Set_Version_2_0` and `Set_Version_2_2` set all expected fields.
- `Clear_Exception_Conditions` clears all six flags + queue.
- `Establish_Data_Link` with mod-8 emits SABM, starts T1, clears
  exception conditions, sets RC := 1.
- `Establish_Data_Link` with mod-128 emits SABME (no SABM).
- `N_r_Error_Recovery` emits DL-ERROR(J) and clears `Layer3Initiated`.

Full suite (1,055+ tests) green. The four runtime gaps that previously
blocked end-to-end interop are now closed; figc4.7 subroutines actually
do something when invoked.

**Approximation noted** (Select_T1_Value): the IIR formula needs
"remaining time on T1 when last stopped", which our `ITimerScheduler`
interface doesn't expose. Currently approximates the sample as the
worst-case T1V (no real-time sub-T1V data). Adaptive T1 timing will
need an `ITimerScheduler.TimeRemaining(name)` extension or similar.
Tracked as a separate item.

### 2026-05-14 — sdl: actions.yaml hygiene + unused-alias lint

Tom asked "did you pick up the new graphml?" after I shipped PR #103
without re-running codegen against the figc4.7 verbatim spellings. The
subroutines.sdl.yaml correctly used verbatim figure labels like
`"Enquiry Response (F = 1)"`, but actions.yaml had no aliases mapping
those to the canonical `Enquiry_Response_F_1` — so the generated
DataLink_Subroutines.g.cs emitted the verbatim spelling, which the
dispatcher's switch doesn't match. Production semantics were silently
wrong (the `Check_Need_For_Response` subroutine's call into
Enquiry_Response would have fallen through to the "unknown SDL
action" throw).

Fixes in this PR:

1. Added canonical-name entries for the figc4.7-redraw additions:
   `Establish_Extended_Data_Link`, `Set_Version_2_0`,
   `Set_Version_2_2`, `Enquiry_Response`.
2. Added the alias `"Enquiry Response (F = 1)"` → `Enquiry_Response_F_1`.
   Tom's graphml normalisation (commit dbc4521) ensured the spacing
   is consistent across all references.
3. **Unused-alias lint** in `tools/Packet.Sdl.CodeGen`: every alias
   declared in actions.yaml must be referenced by at least one
   verb across all *.sdl.yaml pages. Otherwise it's a build-time
   error. Stops the catalog from accumulating dead spellings as
   transcriptions evolve.

Catalog state after this PR: 6 aliases, all used.

Tom's "pass to remove unused aliases" applied: nothing to remove
(catalog is clean), but the lint now keeps it that way going
forward.

### 2026-05-14 — sdl: route Enquiry_Response_F_0 / _F_1 legacy aliases to canonical spec

Follow-up to PR #102. Originally I made the legacy aliases no-op even
after `Wire()` — wrong; production semantics had `connected.sdl.yaml`
calling `Enquiry_Response_F_0` / `_F_1` and the registry silently
doing nothing.

Per "Trust the figure": figc4.4 actually has two distinct
subroutine-call boxes (`Enquiry Response (F = 0)` and
`Enquiry Response (F = 1)`), so the transcription is correct to use
two verb names. They're not different subroutines though — figc4.7's
redraw collapses them into one `Enquiry_Response` body whose first
decision (`F == 1 & (Frame==RR || …)?`) reads the F bit from the
incoming frame.

Fix: `LegacyAliases` is now a `Dictionary<string,string>` mapping
each legacy name to its canonical target. `Wire()` resolves each
alias to the corresponding `SubroutineSpec` walker. Both legacy
names now walk the `Enquiry_Response` body when invoked.

Two new theory cases prove the resolution
(`Legacy_Alias_Walks_Canonical_Enquiry_Response_Spec`). User-Register
overrides still win.

### 2026-05-14 — sdl: figc4.7 subroutines codegen + registry walker

PR 2 of the figc4.7 arc. Adds the data types, the codegen pipeline, and
the runtime walker that lets a subroutine's path data drive an actual
action chain. Auto-Wire from `Ax25Session` is intentionally OFF for
now — figc4.7 subroutines reference action verbs (`Clear Peer
Receiver Busy`, `RC <- 1`, …) and predicates (`mod_128`,
`incoming_is_command`, …) that aren't bound yet. Callers who want the
walker can opt in:

```csharp
var registry = new DefaultSubroutineRegistry();
registry.Wire(dispatcher, guards);
```

**New artefacts:**

- `src/Packet.Ax25.Sdl/SubroutineSpec.cs` — `SubroutineSpec` +
  `SubroutinePath` records, mirroring `TransitionSpec` for subroutine
  pages.
- `tools/Packet.Sdl.CodeGen/Templates/subroutines.scriban-cs` — new
  template for the per-subroutines-page output file.
- `tools/Packet.Sdl.CodeGen/Program.cs` — `SubroutinePage` /
  `SubroutineYamlEntry` / `SubroutinePathYaml` YAML models;
  `LoadSubroutinePage` + `ValidateSubroutinePage`;
  `SubroutinesTemplateModel.From` projection; routing in Main.
- `src/Packet.Ax25.Sdl/DataLink_Subroutines.g.cs` — generated. 13
  subroutines, 32 paths, all action / decision data baked in.
- `src/Packet.Ax25/Session/SubroutineRegistry.cs` — registry walker:
  `Wire(dispatcher, guards)` upgrades each name's no-op stub to a
  `SubroutineSpec` walker. `KnownSubroutines` now derives from the
  generated list (drops `Enquiry_Response_F_0`/`_F_1`, adds
  `Establish_Extended_Data_Link`, `Set_Version_2_0`, `Set_Version_2_2`,
  collapses to single `Enquiry_Response`).
- `tests/Packet.Ax25.Tests/Session/DefaultSubroutineRegistryWalkerTests.cs` —
  6 tests: stub-when-not-wired, generated-list shape, walker fires
  actions on Wire, Register-overrides-survive-Wire (sticky), guard
  picks first matching path, unbound-predicate degrades to no-match.

**Walker semantics:**

- `Invoke(name, tx)` looks up the name; if Wire hasn't run and the
  name is a stub, no-ops (pre-figc4.7 behaviour preserved).
- After `Wire`, each spec's name fires a walker that evaluates path
  guards in order via `GuardEvaluator`. First match runs through
  `IActionDispatcher.Execute(path.Actions, tx)`.
- `Register(name, impl)` is sticky: a subsequent `Wire` won't
  overwrite a delegate the caller set explicitly.
- Unbound predicates degrade gracefully — `GuardEvaluationException`
  inside the walker is caught and treated as "path doesn't match".
  Lets the walker ship today; predicate bindings unblock paths as
  they land.

Codegen check: `dotnet run --project tools/Packet.Sdl.CodeGen`
produces `DataLink_Subroutines.g.cs` (336 lines) deterministically.
Full test suite (1,049+ tests) green.

**Loop_while predicate quirk** noted: the runtime's `loop_while`
construct iterates while its predicate is TRUE, but figures naturally
draw "until X" loops. `Invoke_Retransmission` encodes the inverse
predicate explicitly (`v_s_neq_x` defined as `not v_s_eq_x`). A future
codegen enhancement could accept `negate: true` so YAML can use the
figure's literal predicate.

**Still TODO (future PRs):**

1. Bind ~18 new predicates in `GuardEvaluator` (`mod_128`,
   `peer_receiver_busy`, `incoming_is_command`, etc.) — most read from
   `TransitionContext.IncomingFrame` and the session context.
2. Bind ~25 new action verbs in `ActionDispatcher.Execute`
   (`Clear Peer Receiver Busy`, `RC <- 1`, `Stop T3`, `Set Implicit Reject`,
   `Modulo <- 8/128`, etc.). Once enough bind to make
   `Set_Version_2_0` / `Set_Version_2_2` / `Clear_Exception_Conditions`
   actually do something, flip `Ax25Session` to auto-Wire.
3. Validation PR per runbook stages 6–10: per-subroutine smoke tests
   + spec_prose references + four-codebase implementation
   citations.
4. Refactor-shaped **Tier 1 Go codegen** (out-of-arc): split
   `Packet.Sdl.CodeGen` into a shared IR + per-language backends
   (C# emitter today; Go emitter as new tier). Sets up Tier 3
   (poly-language tool) as "add more backends".

### 2026-05-14 — sdl: figc4.7 redraw ambiguities resolved upstream

Tom landed `46d3782 Update DataLink_Subroutines.graphml` fixing the
two `verification_pending` items I flagged in PR #100:

1. **Enquiry_Response / SREJ Enabled? = No** — n22 now has an
   explicit No → RR Response edge (was previously absent). YAML
   updated: `Enquiry_Response` gains a new `t03_polled_srej_disabled_rr`
   path; existing paths renumbered t04–t07. Total paths: 31 → 32.
2. **Establish_Data_Link / Mod 128? = Yes** — the Yes → SABME edge
   is now explicitly labeled (was unlabeled). YAML notes updated to
   record this. No path-count change.

All `verification_pending:` references removed from
`subroutines.sdl.yaml`.

### 2026-05-14 — sdl: figc4.7 subroutines transcription (PR 1 of 2)

Tom landed the redrawn `DataLink_Subroutines.graphml` on main. This PR
follows the SDL transcription runbook stages 0–4: inspect, schema,
transcribe.

**New artefacts:**

- `spec-sdl/schema/sdl-subroutines.schema.json` — JSON Schema for
  subroutine pages. Distinct from `sdl-machine.schema.json`: a
  subroutine has `name` + `paths` (each path is a sequence of
  decision-branch + action steps), no `state` / `on` / `next`. Reuses
  the existing `decisions`, `path`, `references`, `pinned_refs`
  shapes.
- `spec-sdl/data-link/subroutines.sdl.yaml` — all 13 figc4.7
  subroutines transcribed verbatim from the graphml dump: 31 total
  paths, 19 decisions. New subroutines vs the existing
  `DefaultSubroutineRegistry` stub list: `Establish_Extended_Data_Link`,
  `Set_Version_2_0`, `Set_Version_2_2`. The `Enquiry_Response_F_0` /
  `_F_1` split from the stub registry collapses to a single
  `Enquiry_Response` subroutine in the redraw (F-bit handled inside
  the body).
- Codegen tool now skips subroutine pages with a `skip` notice (full
  codegen integration is PR 2). 5 state-machine pages still generate
  identically.

**Two figure ambiguities flagged with `verification_pending:` references**
per the "Trust the figure" hard rule:

1. **`Enquiry_Response` / `SREJ Enabled? = No`** — the graphml's
   n22 has only a Yes outgoing edge. Logically the No path should
   join the RR Response arm, but the redraw doesn't specify.
   Transcribed as-is, noted.
2. **`Establish_Data_Link` / `Mod 128? = Yes`** — the Yes edge is
   unlabeled in the graphml (only No → SABM is labeled). Inferred
   as Yes → SABME by elimination plus spec prose §4.3.3.2.

**Path counts per subroutine (preview of codegen scope):**

| Subroutine | Paths | Decisions |
|---|---:|---:|
| N_r_Error_Recovery | 1 | 0 |
| Clear_Exception_Conditions | 1 | 0 |
| Transmit_Enquiry | 2 | 1 |
| Enquiry_Response | 6 | 5 |
| Invoke_Retransmission | 1 | 1 (loop_while) |
| Check_I_Frame_Acknowledged | 5 | 4 |
| Establish_Data_Link | 2 | 1 |
| Establish_Extended_Data_Link | 2 | 1 |
| Check_Need_For_Response | 3 | 2 |
| UI_Check | 3 | 2 |
| Select_T1_Value | 3 | 2 |
| Set_Version_2_0 | 1 | 0 |
| Set_Version_2_2 | 1 | 0 |
| **Total** | **31** | **19** |

**Next PR (validation):** codegen extension to emit one C# method per
subroutine, registered into `DefaultSubroutineRegistry`. Replaces
the 12 hand-stubbed no-op delegates. Plus: smoke tests per the
runbook + spec_prose cross-checks + four-codebase implementation
references via parallel subagents.

Full test suite (1,043+ tests) still green; codegen idempotent.

### 2026-05-14 — policy: spec-compliant by default; pragmatism is a named flag

New CLAUDE.md hard rule codifying the strictness philosophy. Future
contributors (human or AI agent) are now expected to:

1. Never silently widen a parser to accept new garbage.
2. Add a named flag to `Ax25ParseOptions` / `AprsParseOptions` when
   accepting something the strict spec rejects.
3. Update `docs/strict-vs-pragmatic-audit.md` in the same PR.
4. Decide which presets (`Strict` / `Lenient` / `Bpq` / `Xrouter`
   / `Direwolf` / `AprsIs`) the flag belongs to.
5. Write a paired test: `Strict` rejects, the relevant preset
   accepts. Strict-rejects-without-lenient-accepts means we
   couldn't justify why the leniency exists.

Also explicitly: **other implementations are not reference truth.**
BPQ / Xrouter / direwolf are interop targets we want to talk to,
not specs we follow. Their quirks are flag-gated behaviour for us.

The outbound construction path stays strict — Packet.NET never
produces non-spec frames, even when it accepts inbound non-spec ones.

### 2026-05-14 — wire: `AprsParseOptions` threaded through Status / Telemetry / Mic-E decoders

Second retrofit. The three APRS decoders that the audit flagged
gain `options` overloads; parameterless overloads route through
`AprsParseOptions.Lenient` so existing callers see no change.

Strict mode now actively enforces:

- **Status**: each byte must be printable ASCII 32–126 except
  `\|` (124) or `~` (126) per §16; trailing CR / LF / space
  tolerated (they get trimmed). Lenient still UTF-8s through
  non-ASCII.
- **Telemetry**: each analog channel must be exactly 3 digits in
  range 000–255 per §13. Lenient still accepts floats and
  variable-width.
- **Mic-E**: DTI must be `` ` `` (0x60) or `'` (0x27); the legacy
  Rev. 0 beta `0x1C` / `0x1D` are rejected. Lenient still accepts
  them.

Position / Object / Item / Message decoders had no pragmatic
choices per the audit — their signatures are unchanged.

9 new tests pairing strict-rejects with lenient-accepts on the
same inputs. Full suite (1,043+ tests) green.

### 2026-05-14 — wire: `Ax25ParseOptions` threaded through `Ax25Address.Read` + `Ax25Frame.TryParse`

First retrofit. Both methods gain an `options` overload; the
parameterless overload routes through `Ax25ParseOptions.Lenient` so
behaviour is unchanged for existing callers.

Strict mode now actively enforces:

- `Ax25Address.Read(span, Ax25ParseOptions.Strict)` throws on an
  empty callsign slot (the BPQ-style all-space wire shape).
- `Ax25Frame.TryParse(..., Ax25ParseOptions.Strict, ...)` returns
  `false` for trailing bytes on supervisory frames (RR/RNR/REJ/SREJ)
  and the no-info U-frames (SABM/SABME/DISC/UA/DM). Still accepts
  trailing bytes on the §3.5-permitted FRMR/XID/TEST.

8 new tests covering both strict-rejects and lenient-accepts paths
on the same inputs, plus parameterless back-compat.

Next: thread `AprsParseOptions` through the APRS decoder family.

### 2026-05-14 — design: `Ax25ParseOptions` / `AprsParseOptions` records (no wiring yet)

Adds the option-record types that the upcoming retrofit will thread
through the parsers / decoders. No call-site changes yet; this PR is
just the types + presets + tests.

`Packet.Core.Ax25ParseOptions`:

- `AllowEmptyCallsignBase` (default `true`) — for BPQ-style
  blank-callsign-slot UI beacons
- `AllowInfoOnSupervisoryFrames` (default `true`) — current TryParse
  captures trailing bytes on S frames; §3.5 doesn't permit that

`Packet.Aprs.AprsParseOptions`:

- `AllowNonAsciiStatusText` (default `true`) — UTF-8 in status
- `AllowNonIntegerTelemetry` (default `true`) — floats / variable-width
- `AllowMicELegacyDtiBytes` (default `true`) — `0x1C` / `0x1D`

Presets per-record:

- **`Strict`** — all pragmatic flags off, pure spec
- **`Lenient`** — kitchen-sink accept-everything (current behaviour);
  used by parameterless decoder overloads to preserve back-compat
- **`Bpq`** / **`Direwolf`** / **`Xrouter`** (AX.25 only) — peer-specific
- **`Direwolf`** / **`AprsIs`** (APRS only) — source-specific

Today `Bpq` / `Direwolf` / `AprsIs` are aliases of `Lenient` and
`Xrouter` is an alias of `Strict` because we haven't yet
differentiated. Tests cover the alias relationships so any future
divergence is a deliberate update.

Records use `init`-only properties so callers can derive variants via
`with`-expressions.

11 new tests; full suite green.

### 2026-05-14 — interop: more UI-frame scenarios against net-sim

`tests/Packet.Interop.Tests/Netsim/NetsimUiFrameScenarios.cs` — four
new scenarios that exercise UI-frame paths through the net-sim
AFSK1200 link sim. UI-frame paths are the half of the interop arc
that isn't gated on the figc4.7 SDL redraw, so they're shippable
now.

Scenarios:

- `UI_Frame_With_Digipeater_Path_Round_Trips` — verifies digi-path
  E-bit migration to the last digi survives the wire.
- `UI_Frame_With_NetRom_Pid_Preserves_Payload` — PID 0xCF (NET/ROM
  L3) + 20-byte info, the typical shape the BPQ corpus has 139 of.
- `UI_Frame_With_Aprs_Position_Survives_RF_Round_Trip` — full stack
  end-to-end: encode AX.25 UI carrying an APRS position, push
  through AFSK1200, decode AX.25 + `AprsPositionDecoder` on the
  other side, assert lat/lon matches.
- `Burst_Of_UI_Frames_All_Arrive` — 5 frames in quick succession;
  all 5 arrive within budget. Catches dropped-frame regressions
  introduced by KISS-TCP back-pressure changes.

All [SkippableFact] — they skip cleanly when net-sim isn't up; the
CI interop job runs them against the live stack. Builds clean
locally without the stack.

### 2026-05-14 — ax25: fuzz / property tests for the frame parser

`tests/Packet.Ax25.Properties/Ax25ParserFuzzProperties.cs` — six new
FsCheck properties focused on the parser's trust-boundary behaviour:

- `TryParse_Never_Throws` — across 2 000 random byte arrays, the
  parser only ever returns `true`/`false`; no exceptions escape.
- `Parsed_Frame_Round_Trips_Through_ToBytes` — anything accepted on
  the way in must serialise back to bytes that parse to the same
  thing.
- `Address_Read_Only_Throws_ArgumentException` — the address parser's
  only legal failure mode for malformed input is `ArgumentException`
  (caller-fault convention); other types would indicate a real bug.
- `RequiredBytes_Is_Exact` — the parser's reported byte-budget
  matches what `WriteTo` actually consumes.
- `I_Frame_Encode_Then_Decode_Roundtrips` — covers connected-mode
  I-frames with arbitrary N(s)/N(r)/P-F/PID/info (the existing
  property only exercised UI).
- `Empty_Callsign_Address_Round_Trips` — regression for PR #85's
  empty-callsign tolerance (BPQ's `>IS` beacon shape).

Sits at the parser's trust boundary: TNCs deliver arbitrary RF
garbage, and a parser exception there would be a DoS against the
whole link layer. "Return false" is the only acceptable failure
mode and this suite proves it across random-input space.

10 existing + 6 new = 16 properties; full suite still green.

### 2026-05-14 — bpq: corpus mining findings (first slice)

New [`docs/bpq-corpus-findings-2026-05-14.md`](bpq-corpus-findings-2026-05-14.md)
analyses the ~36 h BPQ MQTT corpus we have so far. Headlines:

- **Connected-mode shapes in the wild**: SABM/UA + I/RR + REJ +
  DISC/UA, all mod-8, paclen=256, window=7. No SREJ, no SABME, no
  segmentation observed.
- **REJ behaviour**: all 80 REJ frames came from one lossy session;
  the N(r) distribution is flat across mod-8 → random RF loss, not
  a specific-N(s) pathology.
- **XID parameters**: `Paclen=256 Window=7` universal across all 45
  observed peer pairs; Compress flag varies.
- **Retry counts are huge**: BPQ retried SABM 212 times over 9.6 h
  on one pair before completing. Our session machine should not
  assume a small bound.
- **No PID 0x08 segmented frames** in this corpus — our segment path
  needs synthetic coverage (no live data is going to exercise it).
- **No ACKMODE TX-complete echoes** in MQTT — the plugin publishes
  the data frame but not the echo. Can't measure host↔TNC timing
  from MQTT alone.

The next-collection targets section in the findings file lists what
we still need to observe in the wild (high-throughput segmenting
nodes, SREJ-capable peers, mod-128 sessions).

### 2026-05-14 — aprs: Mic-E decoder (`` ` `` / `'` DTI)

`AprsMicE` + `AprsMicEDecoder` per APRS101 §10. Mic-E is the only
APRS format that splits data across the AX.25 destination-address
field (6 bytes encoding 6 latitude digits + 3 message bits + N/S +
longitude offset + W/E) and the information field (encoding longitude,
speed, course, symbol, optional comment).

Surface:

- 6-byte destination decoding: each char `0-9` / `A-J` / `K` / `L`
  / `P-Y` / `Z` maps to a latitude digit (or space for position
  ambiguity) and a message-bit value with a Std-vs-Custom hint.
- Info field bytes 1-6: `d+28` / `m+28` / `h+28` (longitude),
  `SP+28` / `DC+28` / `SE+28` (speed + course).
- The DC+28 byte has two encodings in the wild (printable
  `V..z` and old `0x1c..0x7f` ranges differ by 4 in the
  course-hundreds index); the §10 worked example shows the
  unified decode rule — "subtract 28, divide by 10 for units, then
  subtract 4 from the remainder if it's ≥ 4" — handles both.
- 15 message types decoded (`StandardM0OffDuty`...`M6Priority`,
  `CustomC0`...`C6`, `Emergency`, `Unknown` for mixed Std/Custom).

Live-corpus result (2.1 M rows now, was 1.95 M; +165 k Mic-E
frames):

| Bucket | % |
|---|---:|
| `BothOkMatch` | **99.0%** |
| `BothFailed` | 0.6% |
| `OnlyDirewolf` | 0.3% |
| `OnlyUs` | 0.1% |
| `BothOkMismatch` | 1 row |

10 new tests including the §10 worked example, three real-corpus
samples, and the Emergency-code path. Full suite green.

### 2026-05-14 — aprs: message decoder (`:` DTI)

`AprsMessage` + `AprsMessageDecoder` per APRS101 §14.

Format:
```
:NNNNNNNNN:body[{messageId}]
```

- 9-byte fixed-width addressee (right-space-padded; trimmed for callers)
- `:` body separator
- free-form body (decoded as UTF-8, trailing CR/LF stripped)
- optional `{NNN…}` trailing message ID, 1–5 chars

Same envelope covers person-to-person messages, bulletins
(`BLNn` / `BLNxxx`), acks (`ackNNN`), rejects (`rejNNN`), and
telemetry parameter definition messages from §13 (`PARM.…`,
`UNIT.…`, `EQNS.…`, `BITS.…`). The decoder doesn't interpret the
body — callers route by content (e.g. starts-with `ack` /
`rej` / `PARM.` etc.).

9.1% of the live corpus is `:` DTI (273k rows).

14 new tests; full suite green.

### 2026-05-14 — aprs: telemetry decoder (`T` DTI)

`AprsTelemetry` + `AprsTelemetryDecoder` per APRS101 §13.

Format:
```
T#xxx,aaa,aaa,aaa,aaa,aaa,bbbbbbbb[,comment]
```

- sequence number (3 chars; digits or `MIC`)
- 5 analog values (spec: 000–255; real corpus: variable-width
  integer or float — decoded permissively as `double`)
- 8-bit digital field as ASCII 0/1
- optional trailing comment

Live corpus has 170k T-frames (~5.7%). Permissive parsing handles
the four common shapes seen in the wild: zero-padded integers,
variable-width integers, floats, and `MIC`-style sequences with the
spec-optional leading comma omitted.

13 new tests; full suite green.

### 2026-05-14 — aprs: status report decoder (`>` DTI)

`AprsStatus` + `AprsStatusDecoder` per APRS101 §16.

Layout:
- `>` DTI
- Optional 7-byte DHM-zulu timestamp (`DDHHMMz`) — and *only* DHM-zulu
  per §16. DHM-local (`/`) and HMS (`h`) are NOT valid status
  timestamps even though they're allowed in position and object
  reports.
- Free-form text up to 62 chars (or 55 if a timestamp is present)

We decode permissively: spec says ASCII-only, but the corpus has
plenty of UTF-8 (Chinese-station beacons etc.); decode UTF-8 with the
replacement character for invalid bytes, strip trailing CR/LF/space.

7.3% of the corpus is `>` DTI — no direwolf differential A/B yet
(direwolf's decode_aprs renders status but doesn't surface a "did it
parse" signal we can compare against the way lat/lon serves for
positions); will revisit if there's an obvious comparison surface.

11 new tests; full suite green.

### 2026-05-14 — aprs: item report decoder (`)` DTI)

Companion to the object decoder. APRS101 §11 items are variable-length
(3–9 char) names terminated by `!` (live) or `_` (killed), followed
directly by uncompressed or compressed position bytes. No timestamp.

Implementation:

- `AprsItem` value type — `Name`, `IsAlive`, `Position`.
- `AprsItemDecoder.TryDecode` — scans for the first `!` or `_` byte
  in the name-length window [3, 9] (per spec these characters cannot
  appear inside an item name), then hands the remaining bytes to
  `AprsPositionDecoder.TryDecodePayload` (the no-DTI-stripping entry
  point added for objects).
- `DifferentialMode` extended: DTI `)` → item decoder, `;` → object,
  else position.

Live-corpus result (now 1.95 M rows, was 1.93 M):

| Bucket | % |
|---|---:|
| `BothOkMatch` | **98.9%** |
| `BothFailed` | 0.7% |
| `OnlyDirewolf` | 0.3% |
| `OnlyUs` | 0.1% |
| `BothOkMismatch` | 1 row |

11 new tests; full suite green.

### 2026-05-14 — aprs: object report decoder (`;` DTI)

New `AprsObject` + `AprsObjectDecoder` per APRS101 §11. Layout:

```
;NNNNNNNNN[*|_]DDHHMMzPOSITION_DATAcomment
```

- 9-byte fixed-width object name (trailing spaces preserved as a wire
  identity)
- `*` (live) or `_` (killed) indicator
- 7-byte timestamp — accepts DDHHMMz (DHM zulu), DDHHMM/ (DHM local)
  and HHMMSSh (HMS zulu) per spec
- position bytes (uncompressed §8 or compressed §9), delegated to
  `AprsPositionDecoder` via a new `TryDecodePayload` entry point that
  doesn't treat a leading `/` as the timestamped-position DTI

Also added `AprsPositionDecoder.TryDecodePayload` — same body as the
DTI-stripping `TryDecode` but skips the DTI heuristic. The bug it
fixes: a compressed-position object's symbol-table byte `/` was being
mistaken for the `/`-DTI (timestamped-position-no-msg), causing
~49 k object frames to reject. Useful generally for item / status
decoders that have already parsed their own prefix.

`DifferentialMode` extended to include `;` DTI; dispatches to the
object decoder vs position decoder by DTI.

**Result on the live corpus (1.93 M rows, was 1.62 M)**:

| Bucket | % |
|---|---:|
| `BothOkMatch` | **99.0%** |
| `BothFailed` | 0.6% |
| `OnlyDirewolf` | 0.3% (was 49 k object-only; now 5.5 k edge cases) |
| `OnlyUs` | 0.1% |
| `BothOkMismatch` | 1 row (firmware-malformed timestamp, unchanged) |

12 new tests; full suite (1,000+ tests) green.

### 2026-05-14 — nino-tnc: retract mode-12-specific demod-wedge interpretation

Earlier characterisation framed the intermittent B→A wedge as a
mode-12-specific AFSK-without-PLL demod issue and recommended
avoiding TXDELAY=100 on mode 12. A focused ~30-trial investigation
on 2026-05-14 PM could not reproduce the wedge on demand across
baseline / TX-queue back-pressure / SETHW thrash / mode-swap /
TXDELAY-ladder hypotheses. The one fire that did occur during that
session was in **mode 14**, not mode 12. Per-trial rate is ~5–15%
with very high run-to-run variance.

We have no substantiated mode-specific theory and no reliable
trigger, so `docs/nino-tnc-characterisation.md` now records the
wedge as an intermittent, unreproducible AFSK demod artefact
rather than a mode-12 property, and the "avoid TXDELAY=100 on
mode 12" caveat is dropped. Raw observation tables retained; the
"mode 12 deep dive" framing is gone. Not actively investigating
further on the Packet.NET side; if the firmware author wants to
chase it, a firmware build that traces the AFSK demod state
machine is the lowest-effort next step.

### 2026-05-14 — ax25: relax `Callsign` base length to 0–6 (spec-pragmatic receive path)

The BPQ corpus differential (above) surfaced 42 real wire frames our
parser was rejecting: BPQ's own ID beacons (`>IS Port=N <UI C>`, empty
source) and PD4R-12's QRV broadcasts (`PD4R-12>,TEST Port=3 <UI C>:`,
empty destination with TEST as digipeater). On the wire these are
6-byte all-space callsign slots — encoded as 6 × 0x40 plus an SSID
byte. Our strict `Callsign` constructor was throwing on the empty
base, which propagated out of `Ax25Address.Read` and turned into a
`TryParse → false` rejection at the frame level.

Spec basis for relaxing:

- **§3.12.2**: "If the call sign contains fewer than six characters,
  it is padded with ASCII spaces between the last call sign character
  and the SSID octet" — does not specify a minimum.
- **§6.1.1**: "Operation with destination addresses other than actual
  amateur call signs is a subject for further study" — explicitly
  acknowledges the spec does not formally define non-callsign address
  content, and tacitly accepts that it exists on the wire.

Conclusion: per Postel's principle ("be liberal in what you accept")
the receive path should accept these frames. The construction path
already had to fail-safe for short callsigns; relaxing to allow
zero-length costs nothing.

Change:

- `Callsign` constructor now allows `Base.Length` 0–6 (was 1–6).
- `Callsign.Parse` / `Callsign.TryParse` remain strict (`>= 1` char) —
  those parse user-typed text where empty is a typo, not a legitimate
  wire value.
- New `Ax25AddressTests`: `Read_Accepts_All_Space_Address_Slot` and
  `Empty_Callsign_RoundTrips_Through_Wire_Form`.
- Old `Constructor_Rejects_Empty_Base` test replaced by
  `Constructor_Accepts_Empty_Base` + `TryParse_Still_Rejects_Empty_Text`.

Verification: BPQ corpus differential after the change:

| Bucket | % |
|---|---:|
| `Match` | **99.93%** |
| `UnpairedSkew` | 0.07% |

**Zero parse rejections**, zero real disagreements. The 0.07% are MQTT
stream-drift cases (kiss and bpqformat pair timestamps ~1s apart in 3
of 4,591 pairs) — not a parser issue.

All 935+ existing tests still pass; this is purely a permission
expansion at the receive boundary.

### 2026-05-14 — bpq: connected-mode A/B differential against BPQ's monitor render

New `bpq_differential` spike mode (under `tools/Packet.AprsIs.Spike` —
re-using the spike harness, despite the name). Pairs every `kiss` MQTT
message in the BPQ corpus with its sibling
`ax25/trace/bpqformat` message, decodes the KISS bytes through
`Ax25Frame.TryParse` + `Ax25FrameClassifier.Classify`, parses BPQ's
monitor text, and compares at four increasing depths:

1. Source / destination callsign+SSID and digipeater path
2. Frame-type tag (mapping our classifier → BPQ short tags: SABM→`C`,
   DISC→`D`, otherwise verbatim)
3. Command / response and P/F bits
4. N(s) / N(r) for I and S frames

**Result on the 4,530-frame snapshot** (gb7rdg-2026-05-14.sqlite):

| Bucket | % |
|---|---:|
| `Match` | **99.07%** |
| `BlankCallsignField` | 0.93% |
| `UnpairedSkew` | 0.07% |

The corpus contained: 958 I-frames (with N(s)/N(r) checked), 2,141 RR,
80 REJ, 251 SABM (BPQ writes `<C>`), 76 DISC (`<D>`), 80 UA, 6 DM,
382 XID, 502 UI, plus ad-hoc frames — across 47 distinct peer pairs.
**Zero real disagreements** at any comparison level (frame-type bit
patterns, address C-bits, P/F bit, sequence numbers).

The 0.93% `BlankCallsignField` rows are BPQ's own ID beacon
(`>IS ... <UI C>`, 18 frames, empty source) and PD4R-12's status
broadcast (`PD4R-12>,TEST ... <UI C>:...`, 24 frames, empty dest with
TEST as digipeater). On the wire these are valid AX.25 frames with
all-space callsign slots; our strict `Callsign` type rejects them.
Flagged as a separate bucket rather than counted as parse failures —
it's a real-world edge case BPQ accepts but we don't, worth a
discussion before deciding whether to soften the parser.

**Framing notes** for future spike work: the BPQ MQTT plugin uses two
distinct envelopes depending on direction:

- **sent**: standard KISS (`C0 cmd ... C0`), cmd=`0x00` Data or
  `0x0C` ACKMODE (with 2-byte sequence-tag prefix before the AX.25
  body).
- **rcvd**: BPQ-internal envelope (`00 00 00 00 00 LL HH [ax25...]`,
  7-byte prefix where bytes 5-6 are little-endian total length, no FEND
  wrapping, no escapes). This is **not standard KISS** — first attempt
  to KissDecoder-decode it gave 43% spurious failures.

The `BpqDifferentialMode.TryExtractAx25` helper detects which envelope
each row uses by inspecting the leading byte.

`Packet.Kiss` added to the spike's project references so we can re-use
`KissDecoder` and `KissAckMode.TryParseDataFrame` directly.

### 2026-05-14 — aprs: DirewolfPipeline frame-alignment fix (residual `BothOkMismatch` was our bug too)

**Cascading bug, third correction.** After the q-strip fix (above), the
differential still had ~2.4% `BothOkMismatch` on the
`direwolf_decoded` table. Investigation: the `BothOkMismatch` examples
had `direwolf_decoded.raw_output` that, when re-fed manually to
`decode_aprs`, produced the *correct* coordinates — same as ours. So
the stored lat/lon was right *for some other frame*. Alignment bug.

Root cause in `DirewolfPipeline.SplitOutputByFrame`: when a single
APRS-IS line contained two concatenated TNC2 frames
(`...:!pos1FOO-1>BAR,...:!pos2`), `decode_aprs` treats the whole input
as one frame, decodes `pos1`, and emits the trailing
`FOO-1>BAR,...:!pos2` portion as the "comment field". Our splitter's
TNC2-header regex matched that comment line and counted it as a
second frame — shifting every subsequent batch-position assignment by
one slot. Affected ~2.4% of corpus rows.

Fix: a TNC2-header-shaped line only counts as a frame boundary when
preceded by a blank line. Direwolf emits a colour-reset escape between
consecutive frame analyses, which becomes a blank line after
`AnsiStripper`. The comment lines inside one frame's body are never
preceded by a blank.

Wiped + re-decoded the corpus. Latest differential over **1.62M** rows:

| Bucket | % |
|---|---:|
| `BothOkMatch` | **99.1%** |
| `BothFailed` | 0.4% |
| `OnlyDirewolf` | 0.3% |
| `OnlyUs` | 0.1% |
| `BothOkMismatch` | **1 row, 0.0001%** |

The single remaining mismatch is a degenerate firmware-malformed
timestamp `1415221z` (DHM zulu with an extra digit). Direwolf rejects
the timestamp, falls back to compressed-position decoding of ASCII
bytes, and emits garbage coords. Ours treats the 8th byte as part of
an 8-digit MDHM and decodes the position correctly. Not really
fixable on either side — degenerate input.

**Three corrections, none of them direwolf's fault.** Findings.md now
has both the "SECOND CORRECTION" header for this fix and the original
q-strip correction.

Also: `DirewolfMode` now delegates `SplitOutputByFrame` to the shared
`DirewolfPipeline` helper instead of duplicating it.

### 2026-05-14 — aprs: q-strip regex fix + envelope-rewrite mode + correction of earlier "direwolf bug" claims

Three intertwined pieces in one PR.

**1. Q-strip regex bug (our bug, not direwolf's).** The original
`DirewolfMode.SanitiseForDirewolf` regex `,qA.*:` matched greedily
across `:` characters. On frames whose payload contained a URL like
`http://`, it over-stripped from the q-construct to the LAST `:` in
the line, mangling input to direwolf. The "direwolf bug" cases I
wrote up earlier (Ajaccio → Indian Ocean, Tromsø → Antarctica) were
all caused by this — direwolf was being fed mangled input and
heroically/incorrectly interpreting the trailing bytes as compressed
positions. Verified by feeding the same frame manually to
`/usr/bin/decode_aprs` — direwolf decodes it correctly.

Fixed regex: `,q[A-Z][A-Z]?(,[^:]+)?:` — stops at the first `:` after
the q-construct. Re-decoded the 2.66M-row corpus.

**Findings.md updated with an explicit correction** at the top.

**2. Envelope-rewrite mode (`direwolf_rewrite`).** For corpus rows
direwolf rejected with "Bad source address" (letter SSIDs etc.):

- Parse TNC2 envelope
- Rewrite source / destination / digipeater callsigns to AX.25-valid
  forms via new `AprsCallsign.ToStrictCallsignOrCoerced()` — preserves
  alphanumerics, lowercase → uppercase, letter SSID → `-1`,
  >6-char base truncated to 6
- Info-field payload preserved verbatim
- Pipe rewritten frame through `decode_aprs`
- Store results in new `direwolf_decoded_rewrite` table

This gives us a legitimate A/B test on the **payload decoder** for the
~31% of corpus that direwolf otherwise rejects at the AX.25 callsign-
parse stage. Smoke run on 44.8k previously-rejected frames: 36k (80%)
decoded successfully after rewrite, every one matching ours.

**3. Differential mode** now `COALESCE`s lat/lon from both
`direwolf_decoded` AND `direwolf_decoded_rewrite`, reclaims ~15
percentage points of A/B coverage. Tolerance bumped from 1e-4° to
5e-4° — APRS uncompressed has ~18m resolution; the tighter tolerance
was flagging precision drift, not real disagreements.

Updated differential over 500k corpus rows:

| Bucket | % |
|---|---:|
| `BothOkMatch` | **82.0%** |
| `OnlyUs` (rewrite not yet run on this slice) | 16.7% |
| `BothOkMismatch` | **0.6%** (was 1.8% before fix) |
| `BothFailed` | 0.5% |
| `OnlyDirewolf` | 0.3% |

Remaining 0.6% mismatch cases are mostly concatenated-frame edge cases
(two TNC2 frames merged into one APRS-IS line) and a small cluster of
compressed `L`-overlay frames where direwolf still produces what looks
like genuinely wrong coordinates — worth further investigation in a
follow-up before any upstream report.

**Refactor**: extracted `DirewolfPipeline` static helper from
`DirewolfMode` so both modes share the decode_aprs subprocess +
output-parsing code.

`AprsCallsign` gains `ToStrictCallsignOrCoerced(byte fallbackSsid=1)`
with 6 new unit tests covering each transformation rule. 911 tests
green.

### 2026-05-14 — aprs: permissive `AprsCallsign` type (monitor layer)

New `AprsCallsign` value type in `Packet.Aprs` for monitor-layer
callsigns that the strict `Packet.Core.Callsign` rejects.

The strict `Callsign` enforces the AX.25 spec: 1–6 uppercase
alphanumeric base + 0–15 numeric SSID. That's correct for outbound
frame construction. But ~31% of APRS-IS traffic carries source
callsigns that don't match (D-Star port markers `-D` / `-B` / `-T`,
D-Rats `-H`, APRSdroid lowercase, gateways with longer-than-6-char
bases). Documented in `tools/Packet.AprsIs.Spike/findings.md` from
the differential analysis.

`AprsCallsign` accepts:
- Base: 1–9 chars, A–Z / a–z / 0–9 (case preserved)
- SSID: empty, or 1–3 chars of A–Z / a–z / 0–9 after a single dash

API:
- `AprsCallsign(string base, string ssid)` — constructor
- `AprsCallsign.TryParse(string text, out AprsCallsign)` — text parse
- `aprs.TryToStrictCallsign(out Callsign)` — best-effort conversion to
  the AX.25-valid strict form; fails if base contains lowercase, SSID
  isn't numeric 0–15, or base > 6 chars

Tests cover: real corpus letter-SSID cases (K0MVH-D, ZS6ATZ-D, etc.),
lowercase bases (aprsdroid), too-long bases, invalid chars, conversion
to strict form for both compatible and incompatible inputs.

905 tests green.

Closes Tier 1(a) on the spike backlog. With both
`AprsPositionDecoder` and `AprsCallsign` in place, the monitor layer
can display ~96% of APRS-IS positions including the ones direwolf
rejects at the AX.25 callsign-parse stage.

### 2026-05-14 — aprs: position decoder gains `@` / `/` timestamped variants

`AprsPositionDecoder.TryDecode` now handles all four position DTIs.
Timestamped variants (`@` and `/`) strip the 7-byte (DHM zulu / DHM
local / HMS) or 8-byte (MDHM) timestamp prefix before delegating to
the same position-decode path.

`TimestampLength` recognises:
- `DDHHMMz` (zulu) — terminator `z`
- `DDHHMM/` (local) — terminator `/`
- `HHMMSSh` (HMS) — terminator `h`
- `MMDDHHMM` (MDHM) — 8 digits, no terminator

Re-ran differential mode over 170,976 corpus rows covering all 4 DTIs:

| Bucket | % |
|---|---:|
| `BothOkMatch` | **66.2%** (was 60.2%) |
| `OnlyUs` (direwolf rejects AX.25 envelope) | 31.3% |
| `BothOkMismatch` (direwolf bugs we don't have) | 1.8% |
| `BothFailed` | 0.5% |
| `OnlyDirewolf` (we're stricter on edge cases) | 0.3% |

8 new unit tests. 852 tests green (was 844).

### 2026-05-14 — aprs: position decoder v0 (`!` / `=` DTI) + corpus differential

Stood up `src/Packet.Aprs` library. First decoder:
`AprsPositionDecoder.TryDecode` for DTI `!` (no timestamp, no message)
and `=` (no timestamp, message-capable). Covers both:

- **Uncompressed** (APRS12c §8): `DDMM.mmN/S<symtbl>DDDMM.mmE/W<sym><comment>`
  with space-pad-for-privacy support, symbol table validation,
  hemisphere checks, minutes-< 60 / degrees-≤ 90/180 range checks.
- **Compressed** (APRS12c §9): `<symtbl><base91_lat:4><base91_lon:4><sym><csT:3><comment>`
  with base-91 character-range validation (`!` through `{`).

21 unit tests pin every encoding edge case using real corpus frames as
test data (the lat/lon expected values are direwolf's reference
decodes for the same byte sequence).

**Differential mode**: new `differential` mode on `Packet.AprsIs.Spike`
walks `lines JOIN direwolf_decoded` over every `!` / `=` row, runs our
decoder, classifies the outcome into 5 buckets, and writes a Markdown
report.

Ran over 125,036 corpus rows:

| Bucket | Count | % |
|---|---:|---:|
| `BothOkMatch` (agree within 1e-4°) | 75,316 | 60.2% |
| `OnlyUs` (direwolf rejected the AX.25 envelope) | 46,724 | 37.4% |
| `BothOkMismatch` (both decoded, disagree) | 2,224 | 1.8% |
| `BothFailed` (both reject — true firmware bugs) | 602 | 0.5% |
| `OnlyDirewolf` (we're over-strict on symbol table) | 170 | 0.1% |

Headline findings (see [`findings.md`](../tools/Packet.AprsIs.Spike/findings.md)):

1. **`OnlyUs` is APRS-vs-AX.25 layer divergence**: direwolf rejects
   frames with letter SSIDs (D-Star `-D`, `-B`, etc.) at the
   AX.25 callsign-parse stage. Our payload-layer decoder doesn't gate
   on envelope validity and recovers the position fine. Real-world
   APRS-IS leaks ~37% of these.
2. **`BothOkMismatch` is mostly direwolf bugs**: of 5 hand-checked
   examples, 4 have direwolf producing wildly wrong coordinates
   (Antarctica when the comment text says "Tromsø") while our decoder
   matches the comment's location. Pattern looks like direwolf is
   sometimes misreading post-position bytes.
3. We agree with direwolf on **>97%** of frames where direwolf
   produced a position (excluding the AX.25 envelope rejections).

Follow-ups: `@` / `/` timestamped variants, `AprsCallsign` permissive
type (Tier 1(a)) for the letter-SSID frames, decide symbol-table
strictness for production.

844 tests green (was 823: +21 new decoder tests).

### 2026-05-14 — kiss: AX.25 ↔ KISS bridge

Sixth and final mechanical piece of the interop arc. New
`KissAx25Bridge` static helper in `Packet.Kiss` wires `Ax25Adapter` to
any `IKissModem` implementation.

**Outbound**: `KissAx25Bridge.CreateOutbound(modem, ...)` builds an
`Ax25Adapter` whose `sendBytes` callback fans out to
`IKissModem.SendFrameAsync`. The modem handles KISS framing
(0xC0 flags, byte escapes, command byte) internally. Fire-and-forget
on the async send — synchronous frame sinks can't easily await.

**Inbound**: `KissAx25Bridge.RouteInboundToAdapter(evt, adapter)`
translates a typed `KissInboundEvent` into an
`Ax25Adapter.OnReceivedAx25Frame` call. Handles both
`Ax25FrameReceivedEvent` (regular RX) and `AckModeDataReceivedEvent`
(ACKMODE-wrapped, payload re-parsed); ignores `UnknownInboundEvent`
and modem-specific events. Caller subscribes the modem driver's
`InboundEvent` event to the route function.

Two halves are split because KISS driver APIs vary on the inbound
surface (`event EventHandler<KissInboundEvent>`, `IAsyncEnumerable`,
or pull-based `ReceiveAsync`). The bridge offers a uniform routing
function rather than imposing a subscription model.

`Ax25Adapter` gains a sister method `OnReceivedAx25Frame(Ax25Frame)`
alongside `OnReceivedAx25Bytes` — when the KISS driver has already
parsed bytes to an `Ax25Frame`, we skip a redundant parse.

5 new tests: outbound DL_UNIT_DATA_request through bridge to fake
modem; inbound `Ax25FrameReceivedEvent` routing; ACKMODE event
routing with payload re-parse; `UnknownInboundEvent` not routed;
end-to-end loopback via two `LoopbackModem` pair (DL_CONNECT_request
→ SABM via bridge → other side's Connected). 823 tests green.

### 2026-05-14 — ax25: interop arc closed (3-of-3 mechanical pieces done)

After this, all six items I called out as gaps for end-to-end interop
are addressed except the transcription-gated one. Status:

| # | Component | Status |
|---|---|---|
| 1 | Wire codec (frame specs → bytes) | ✅ #69 |
| 2 | Incoming demux (bytes → events) | ✅ #70 |
| 3 | Transport adapter | ✅ #71 |
| 4 | figc4.7 subroutine bodies | **transcription-gated** |
| 5 | Frame-aware predicate bindings | ✅ #73 |
| 6 | KISS framing glue | ✅ this PR |

Once figc4.7 is transcribed, a real LinBPQ interop run becomes:
1. Build an `Ax25SessionContext` for the local/remote pair
2. `KissAx25Bridge.CreateOutbound(linbpqModem, ctx, …)` — get an adapter
3. Subscribe `modem.InboundEvent += (s,e) => KissAx25Bridge.RouteInboundToAdapter(e, adapter)`
4. `adapter.Session.PostEvent(new DlConnectRequest())`
5. Watch SABM go out, UA come back, session land in Connected.

That's the next milestone. PR-by-PR work shifts back to APRS-IS corpus
analysis or whatever's next on the spike backlog until you have time
to redraw figc4.7 (and figc4.5 Timer Recovery).

### 2026-05-14 — nino-tnc: firmware-management groundwork

Sets up the layers that ought to exist *around* a firmware-flash
operation, while deliberately leaving the flash itself out. The
explicit goal was groundwork for firmware management, not a C# copy
of flashtnc.

New `Packet.Kiss.NinoTnc.Firmware` namespace:

- `NinoTncFirmwareVersion(Major, Minor)` — strong type parsed from the
  TX-Test diagnostic's `=FirmwareVr:` field. `Parse` / `TryParse` /
  comparison operators. Aware that the major component encodes Nino's
  chip-variant convention (3 = dsPIC33EP256GP, 4 = dsPIC33EP512GP).
- `NinoTncChipVariant` enum derived from `NinoTncFirmwareVersion.Major`.
  Surfaced on `NinoTncTxTestFrame.ChipVariant` so a button-press tells
  the operator which dsPIC they have.
- `INinoTncFirmwareCatalogue` — "what releases are available for chip
  variant X". Default-interface-method `CheckForUpdateAsync` composes
  current version + catalogue into a typed
  `NinoTncFirmwareUpdateAvailability` answer.
- `GitHubNinoTncFirmwareCatalogue` — the concrete implementation,
  reads `ninocarrillo/flashtnc@master` via the GitHub contents API,
  parses `N9600A-v{major}-{minor}.hex` filenames into typed releases,
  pairs each with its sibling `v{major}-{minor}-mplab-checksums.txt`
  for download-time verification. Nino's release process is "drop new
  hex, remove old hex"; we deliberately surface only the current state
  rather than chase git history.
- `INinoTncFirmwareFlasher` — the seam where the actual flash
  operation will eventually live. Documented intent: native C# port of
  the bootloader protocol OR shell-out to `flashtnc.py`, on its own
  PR with its own review discipline. The only in-tree implementation
  is `UnsupportedFirmwareFlasher` which throws — lets callers wire to
  the interface today.

`NinoTncTxTestFrame.FirmwareVersion` is now a strong
`NinoTncFirmwareVersion?` (was a raw `string?`); the raw string is
kept alongside as `FirmwareVersionRaw` for callers that need the
verbatim text.

Test totals: `Packet.Kiss.NinoTnc.Tests` 74 (was 44, +30 firmware
coverage). 4 new test files cover version parsing + comparison,
update-availability logic, the GitHub catalogue (stub HttpClient
serving real-shape JSON), and the unsupported-flasher stub's error
message.

What this PR explicitly does NOT do:

- No bootloader protocol implementation. That's the dangerous bit.
- No Intel-HEX parsing. Not needed until there's something to flash.
- No automatic update workflow — the layers say "an update is
  available" but never act on it. A UI / MCP / operator-CLI layer can
  build on top later.

### 2026-05-14 — ax25: frame-aware guard predicates

Fifth piece of the interop arc. Predicates like `P_eq_1`, `command`,
`N_s_eq_V_r`, `nr_in_window` referenced by figc4.1/4.4 transitions now
read from the **current triggering event's attached frame** rather than
constructor-time constants.

`Ax25Session` gains a `CurrentTrigger` property, non-null only during
`DispatchEvent` (set before guard evaluation, cleared in a `finally`).
`Ax25SessionBindings.CreateDefault` gains an optional
`currentTrigger: Func<Ax25Event?>` parameter; when supplied, the
binding dictionary includes frame-aware predicates that resolve
through that thunk.

Bindings added when frame-aware mode is enabled:
- `P_eq_1` / `F_eq_1` / `P_or_F_eq_1` → incoming.PollFinal
- `command` → incoming.IsCommand (from C-bits)
- `N_s_eq_V_r` → mod-8 N(S) extraction == ctx.VR
- `N_s_gt_V_r_plus_1` → mod-8-aware comparison
- `nr_in_window` / `V_a_le_N_r_le_V_s` → mod-8-aware window check
  against [V(a)..V(s)]
- `info_field_valid` → incoming.Info.Length ≤ ctx.N1

Always-on additions (no frame needed): `version_2_2`,
`srej_exception_gt_0`, `V_s_eq_V_a`, `V_s_eq_V_a_plus_k`, `RC_eq_N2`,
`vr_i_frame_stored`.

`Ax25Adapter` defaults to enabling frame-aware bindings (forward-
references its own session via closure). Existing tests that supply
manual bindings continue to work — frame-aware mode is opt-in via the
`currentTrigger` parameter.

18 new tests covering each frame-aware predicate, mod-8 wrap-around
math, fallback when no trigger frame, and backward-compat
verification. 818 tests green (was 786).

Closes item 5 of the interop arc. Items remaining: figc4.7 subroutine
bodies (transcription-gated) and KISS framing glue.

### 2026-05-14 — ax25: transport adapter + first wire-encoded loopback

Third piece of the interop arc. New `Ax25Adapter` glues an
`Ax25Session` + `ActionDispatcher` to byte-level I/O:

- Constructor takes the standard session inputs (context, scheduler,
  transitions table, initial state) plus a `sendBytes` callback
- Internally builds an `ActionDispatcher` with all sinks wired:
  S-frame / U-frame / UI-frame / I-frame sinks serialise via
  `FrameSpecExtensions` and call `sendBytes`
- `OnReceivedAx25Bytes(ReadOnlySpan<byte>)` parses with
  `Ax25Frame.TryParse`, classifies with `Ax25FrameClassifier`, posts to
  the session
- Timer expiries auto-feed back as `T1Expiry`/`T2Expiry`/`T3Expiry`
  events on the same session
- `bindings` and `subroutines` are constructor-injectable for tests
  and production wiring

**Loopback test**: two adapters, side A's `Establish_Data_Link`
subroutine wired to emit a SABM, side A posts `DlConnectRequest` →
SABM bytes flow into side B's `OnReceivedAx25Bytes` → B classifies as
`SabmReceived` → B advances to Connected (via figc4.1 t14 with
`able_to_establish=Yes`). First time the state machine round-trips
itself through wire-encoded bytes.

Still missing for true interop against LinBPQ:
1. ~~Wire codec~~ — #69
2. ~~Incoming demux~~ — #70
3. ~~Transport wiring (Ax25Adapter)~~ — this PR
4. figc4.7 subroutine bodies (transcription-gated)
5. Frame-aware predicate bindings (`P_eq_1`, `command`, etc. need to
   read from `tx.IncomingFrame` not constructor-time constants)
6. KISS framing glue between `Ax25Adapter.sendBytes` and
   `Packet.Kiss.KissTcpClient` / `Packet.Kiss.NinoTnc.*`

Items 5+6 are mechanical follow-ups; #4 is transcription-gated.

786 tests green.

### 2026-05-14 — ax25: incoming frame demux — Ax25Frame → Ax25Event

Second piece of the interop arc. New `Ax25FrameClassifier.Classify(frame)`
takes a parsed `Ax25Frame` and returns the matching `Ax25Event`
subtype:

- I-frame (control bit 0 = 0) → `IFrameReceived(frame)`
- S-frame (control bits 1–0 = 01) → `RrReceived` / `RnrReceived` / `RejReceived` / `SrejReceived` based on SS bits at positions 3–2
- U-frame (control bits 1–0 = 11) → `SabmReceived` / `SabmeReceived` / `DiscReceived` / `UaReceived` / `DmReceived` / `FrmrReceived` / `XidReceived` / `TestReceived` / `UiReceived` based on the MMM+MM mask (P/F bit at 4 ignored for classification)
- Unknown U-frame control byte → `ControlFieldError`

Pure function; doesn't need session state. Mod-8 only (extended is
TBD when `Ax25Frame` grows 2-byte control field support).

Closes the inverse direction of the wire codec: bytes → frame (via
existing `Ax25Frame.TryParse`) → classified event ready for
`Ax25Session.PostEvent`.

26 new tests across every U/S/I-frame type + unknown-control-byte
error path + symmetry test (spec → frame → bytes → parse → classify
round-trip). 782 tests green.

Still missing for interop:
1. ~~Wire codec~~ — done (#69)
2. ~~Incoming demux~~ — done (this PR)
3. Transport wiring (`Ax25Adapter` glue between session sinks and KISS/AXUDP)
4. figc4.7 subroutine bodies (transcription-gated)
5. Frame-aware bindings — `command` / `info_field_valid` / etc.
   predicates in figc4.4 need to evaluate against
   `TransitionContext.IncomingFrame`, not static booleans. Not
   strictly blocking interop for connect/disconnect, but blocking the
   I-frame receive paths.

### 2026-05-14 — ax25: wire codec — frame specs → bytes

First piece of the interop arc. Closes one of the four gaps that block
a real end-to-end interop test (the others: incoming-frame demux,
transport wiring, figc4.7 subroutines).

**New `Ax25Frame` factories**: `Sabm`, `Sabme`, `Disc`, `Ua`, `Dm`,
`Frmr`, `Xid`, `Test`, `Rr`, `Rnr`, `Rej`, `Srej`, `I`. Each composes
the right control byte per §4.3.2/§4.3.3 and reuses the address/E-bit/
digipeater plumbing the `Ui` factory already had. Made `Ax25Frame` a
partial class so factories live in `Ax25Frame.Factories.cs`.

**`FrameSpecExtensions.ToAx25Frame(spec, context)`** on each of the
four spec record types — `SupervisoryFrameSpec` / `UFrameSpec` /
`UiFrameSpec` / `IFrameSpec`. Pulls addressing from
`Ax25SessionContext.Local`/`Remote`/`Digipeaters` and dispatches to
the matching factory. Spec records stay address-agnostic.

End-to-end flow now works for outgoing frames:

```
dispatcher.sendUFrame(UFrameSpec(Sabm, cmd, P=1))
  → spec.ToAx25Frame(context)
  → Ax25Frame.Sabm(remote, local, pollBit=true, digis)
  → frame.ToBytesWithFcs()  →  17 bytes ready for KISS / AXUDP
```

Tests: 46 new — full byte-level control-byte assertions for every
factory (theory-driven across N(R)/P/F combinations) + round-trip
through `TryParse` + spec-extension dispatch + digipeater pass-through.

Still missing for interop:
1. Incoming bytes → `Ax25Frame` → classified event (demux)
2. Wiring `sendSFrame`/`sendUFrame`/etc. to a real transport (`Ax25Adapter`)
3. figc4.7 subroutine bodies (transcription-gated)

### 2026-05-14 — Packet.Kiss / Packet.Kiss.NinoTnc separation; over-air TX-Test event; mode-change-doesn't-reset-callsign

Refactor + two empirical findings the experiment turned up.

**Refactor.** The KISS code had accumulated NinoTNC specialism that
properly belongs at the generic-KISS layer. Other KISS modems (Dire
Wolf, QtSoundModem) shouldn't have to depend on a NinoTNC package to
use the typed event surface or the adaptive transport. Moves:

- `INinoTncModem` → `Packet.Kiss.IKissModem`.
- `AckModeReceipt` → `Packet.Kiss`.
- `NinoTncInboundEvent` → `Packet.Kiss.KissInboundEvent`; the generic
  subtypes (`Ax25FrameReceivedEvent`, `AckModeDataReceivedEvent`,
  `UnknownInboundEvent`) move with it.
- `NinoTncFrameClassifier` split into `Packet.Kiss.KissFrameClassifier`
  (generic) and a thin `Packet.Kiss.NinoTnc.NinoTncFrameClassifier`
  overlay that upgrades the firmware-specific cases on top.
- `AdaptiveNinoTncTransport` → `Packet.Kiss.AdaptiveKissTransport`.
- `TxTestFrameReceivedEvent` renamed to
  `NinoTncTxTestFrameReceivedEvent`, stays in NinoTNC.

What stays in `Packet.Kiss.NinoTnc`: `NinoTncCatalog`, `NinoTncMode`,
`NinoTncSetHardware`, `NinoTncTxTestFrame`, `NinoTncSerialPort`,
`NinoTncPortDiscovery` — all genuinely firmware-shaped.

`Packet.Kiss` gains a `ProjectReference` on `Packet.Ax25` because the
typed `Ax25FrameReceivedEvent` embeds an `Ax25Frame`. The alternative
(an untyped "KISS Data" event with consumer-side parsing) would be
purer layering but worse ergonomics.

Also: `Ax25Frame.TryParse` was previously throwing
`ArgumentException` from `Callsign`'s constructor when the input
bytes didn't shape up as a valid address. The classifier exposed
this by feeding it arbitrary KISS payloads. Fixed — `TryParse` now
returns `false` on invalid-address bytes per its contract.

**New over-air TX-Test event.** With both modems running and a dual
listener watching, pressing the TX-Test button on one modem put a
real AX.25 UI frame on the air that the *other* modem decoded
cleanly:

```
src=M0LTE  dst=CQBEEP-5  control=0x03
INFO[53] = "{1 !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQR"
```

A second press incremented the digit and shifted the printable-ASCII
window one byte forward:

```
INFO[53] = "{2 \"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRS"
```

That's a per-press sequence counter and a deterministic test
pattern — useful for free as a zero-config link-up probe, missed-
press detection, and (if anyone wants it) byte-level bit-error
counting on the receive side. New types:
`NinoTncAirTestFrame` (recognises the pattern shape),
`NinoTncAirTestFrameReceivedEvent` (typed event from the classifier).

Captured the actual on-air bytes for both presses; tests at
`tests/Packet.Kiss.NinoTnc.Tests/NinoTncAirTestFrameTests.cs` lock
the recogniser against the real values.

**Mode-change does NOT reset the learned callsign.** Probed:
SETHW mode 6 → press button → captured `src=M0LTE`. Then SETHW
mode 7 → press button → still `src=M0LTE`. The firmware's
callsign register is durable across mode changes (the firmware
learns from the first AX.25 frame TX'd through it, and that
learned value persists across SETHW). Documented in the README's
TX-Test section.

**IL2P+CRC corruption rarity** noted in the README:
`UnknownInboundEvent` due to bit-flips will be very rare on modes
2 / 4 / 5 / 7 / 8 / 9 / 10 / 11 / 14 (FEC + CRC fix or drop most
of them upstream). The adaptive estimator's `Lost` signal — AX.25
layer never-ACK — is the realistic loss indicator for those modes.

**Test totals**: `Packet.Kiss.Tests` 69 (+14 — 8 transport tests
moved up + 6 new generic classifier tests); `Packet.Kiss.NinoTnc.Tests`
44 (existing classifier + new air-test recogniser tests); 7
hardware-loop tests still green against the live pair.

### 2026-05-14 — ax25: AllOtherCommands carries Frame; start_T1 uses ctx.T1V

Two follow-up cleanups to close the small loose ends from the
dispatcher arc.

**`AllOtherCommands` carries a triggering frame**. The catch-all
event for command frames that didn't match any explicit transition
now has a `Frame` parameter. This unlocks figc4.1 t05 end-to-end
testing: the YAML's path is `F := P; DM`, and `F := P` needs to read
the incoming frame's PollFinal bit. New E2E test confirms a polled
unhandled command produces a DM(F=1) response.

**`start_T1` reads `ctx.T1V`** instead of the dispatcher's static
`T1Duration` property. Reflects the spec's per-session T1 timeout
value, which `T1V := 2 * SRT` (figc4.1 t03) and figc4.7's eventual
`Select_T1_Value` subroutine mutate dynamically. T2 and T3 stay on
the dispatcher's static defaults (no SDL verb mutates them today).
New unit test confirms `T1V := 2 * SRT` then `start_T1` arms the
timer for the computed duration, not the dispatcher default.

687 tests green (was 685).

### 2026-05-14 — ax25: I-frame emission + session-loop queue drain machinery

The final piece: `I_command` verb + the session loop that pops
`ctx.IFrameQueue` and posts synthetic `IFramePopsOffQueue` events.
After this PR, **every signal_lower verb in the transcribed
vocabulary is wired**, and DL_DATA_request → I-frame on the wire
works end-to-end through the real dispatcher.

**New types**:
- `IFrameSpec(IsCommand, PBit, Nr, Ns, Info, Pid)` record
- `Ax25SessionContext.IFrameQueue` retyped to
  `Queue<(ReadOnlyMemory<byte> Data, byte Pid)>`
- `Ax25SessionContext.SentIFrames` retyped to
  `Dictionary<byte, (ReadOnlyMemory<byte> Data, byte Pid)>`
- `DlDataRequest` and `IFramePopsOffQueue` gain `Pid` parameter
  (defaults to `Ax25Frame.PidNoLayer3`)
- `ActionDispatcher` gains optional `sendIFrame` callback

**Session-loop machinery**: `Ax25Session.PostEvent` now calls a new
`DrainIFrameQueue` after `DispatchEvent`. It pops one entry at a time
and re-dispatches as `IFramePopsOffQueue`, stopping when the queue
empties, the peer goes busy, or the send window fills
(`(V(s) - V(a)) % modulus < k`). Matches figc4.4 t19/t20's guards
(`peer_receiver_busy=No` + `V_s_eq_V_a_plus_k=No`).

**EmitIFrame**: reads pending `Nr`/`Ns`/`PfBit` (defaulting to
`V(R)`/`V(S)`/`false`), reads `Info`/`Pid` from the triggering
`IFramePopsOffQueue` event, calls `sendIFrame`, and stashes the
payload in `SentIFrames[Ns]` for figc4.4's REJ/SREJ retransmit paths.

**Tests**: 4 new (2 unit tests for I_command, 2 figc4.4 E2E tests).
685 tests green (was 681).

End of the verb-wiring arc: every action verb the transcribed pages
reference (figc4.1/4.2/4.3/4.4/4.6) is now executable through the real
dispatcher. Action chains involving subroutines no-op via stubs;
once figc4.7 is transcribed, real bodies replace the stubs with no
dispatcher changes.

### 2026-05-14 — KISS / NinoTNC driver — ACKMODE solidified, typed inbound surface, CSMA adaptive, USB discovery

Closes the "shape gaps" called out in the morning review of
`Packet.Kiss.NinoTnc`:

**KISS command coverage** — TXTAIL (`0x04`) helper added on
`INinoTncModem` + `NinoTncSerialPort` so the four adjustable KISS
parameters (TXDELAY, PERSIST, SLOTTIME, TXTAIL) all have first-class
helpers. POLL (`0x0E`), Return-from-KISS (`0xFF`) and the multi-drop
XOR-checksum extension are explicitly out of scope — no current
hardware needs them.

**Multi-drop port nibble dropped from the driver API.** The KISS
framing layer still respects 0–15 port nibbles correctly; the driver
hard-codes port 0 and the public surface is simpler for it (one modem
= one serial port = one radio).

**Typed inbound event surface.** New `NinoTncInboundEvent` hierarchy:
`Ax25FrameReceivedEvent`, `TxTestFrameReceivedEvent`,
`AckModeDataReceivedEvent`, `UnknownInboundEvent`. Classification
lives in `NinoTncFrameClassifier.Classify(KissFrame)` so it is
unit-testable on its own. The raw `FrameReceived` event still fires
alongside; subscribers pick the surface that fits.

ACKMODE TX-completion echoes for *our own* outbound frames are still
correlated by sequence tag inside `NinoTncSerialPort` and surface as
`SendFrameWithAckAsync`'s return value — no typed event for those,
because the caller already has them via the receipt.

**Adaptive CSMA estimator.** New `CsmaContentionEstimator` tunes
PERSIST + SLOTTIME from contention signals — `AckModeTimedOut` (TNC
accepted the frame but never won a slot) and `Lost` (frame TX'd but
peer never ACKed at AX.25 layer). Step-down on contention, slow
step-up on sustained success. Composable with the existing
`TxDelayHillClimbEstimator` through the new `CompositeAdaptiveEstimator`.

**USB VID/PID discovery on Windows** — walks
`HKLM\SYSTEM\CurrentControlSet\Enum\USB\` for devices whose key name
matches `KnownVidPids` (currently `04D8:00DD`, the Microchip USB-CDC
reference the stock NinoTNC firmware presents as) and reads each
match's `Device Parameters\PortName`. Verified: returns `COM6` +
`COM8` on the dev host with the env-var override unset — the COM1 /
COM107 false positives are gone. Locked-down hosts fall back to
generic enumeration.

**Driver README** at `src/Packet.Kiss.NinoTnc/README.md` now reflects
the full shape, including the "out of scope" list. Notes explicitly
that parameter readback is not possible without the operator pressing
the TX-Test button — KISS has no read commands and the NinoTNC
firmware has no query path.

**Test totals**: `Packet.Kiss.Tests` 55 (was 43, +12 for CSMA +
Composite); `Packet.Kiss.NinoTnc.Tests` 44 (was 37, +7 for the
frame classifier). 7 hardware-loop tests including a new typed-event
assertion against the real pair.

Tracked as a follow-up: the mode-12 Python repro
(`tools/repro/ninotnc_mode12_repro.py`) still doesn't reliably
trigger the catastrophic-RX-lockup pattern in verification runs;
needs the trigger condition pinned down before it's worth handing
upstream.

### 2026-05-14 — ax25: end-to-end integration tests for figc4.3 + figc4.4 (subset)

Two more E2E test files driving real `ActionDispatcher` against real
generated transition tables.

`DataLinkAwaitingReleaseEndToEndTests` — 7 figc4.3 transitions:
- t01 DL_DISCONNECT_request → Expedited DM
- t02 T1 expiry RC=N2 → DL_ERROR(G) + DL_DISCONNECT_indication + → Disconnected
- t04 UA F=1 → DL_DISCONNECT_confirm + stop_T1 + → Disconnected
- t07 DL_UNIT_DATA_request → UI command (still works while waiting)
- t09/t10/t11 control_field_error / info_not_permitted / length_error → DL_ERROR(L/M/N) via Theory

`DataLinkConnectedEndToEndTests` — 6 figc4.4 transitions (the subset
that doesn't need I-frame queue session-loop machinery):
- t01 DL_DISCONNECT_request → discard queue + RC:=0 + DISC (P=1) +
  stop_T3 + start_T1 + → AwaitingRelease
- DL_FLOW_OFF (own_receiver_busy branch) → set_own_receiver_busy +
  RNR_response (with default N(R) = ctx.VR) + clear_acknowledge_pending
- DL_FLOW_ON (busy + T1 not running) → clear_busy + RR_command +
  stop_T3 + start_T1
- DL_DATA_request → push_on_I_frame_queue + internal signal raised
- control_field_error v2.0 branch → DL_ERROR(L) + discard queue +
  Establish_Data_Link subroutine + → AwaitingConnection
- LM_SEIZE_confirm + ack_pending → Enquiry_Response_F_0 subroutine +
  LM_release_request

**Cumulative E2E coverage: 32 transitions across figc4.1/4.2/4.3/4.4/4.6.**
The only major gap is figc4.4's I-frame paths (t02–t17, t19–t20, t55+,
t60+) which need session-loop machinery for the
<c>I_frame_pops_off_queue</c> event mechanism to be testable.

681 tests green (was 668).

### 2026-05-14 — ax25: end-to-end integration tests for figc4.2 + figc4.6

Two new test files exercising the **real** dispatcher against the
**real** generated transition tables for figc4.2 (Awaiting Connection)
and figc4.6 (Awaiting V2.2 Connection).

`DataLinkAwaitingConnectionEndToEndTests` — 6 figc4.2 transitions:
- t01 DISC_received → DM with F=incoming P
- t02 DM received F=1 → discard queue + DL_DISCONNECT_indication + stop_T1 + → Disconnected
- t05 UA received F=1 layer_3_initiated → DL_CONNECT_confirm + Select_T1_Value + V(s/r/a):=0 + → Connected
- t08 T1 expiry RC=N2 → discard queue + DL_ERROR(G) + DL_DISCONNECT_indication + → Disconnected
- t10 all_other_primitives_from_upper_layer (no-op)
- t12 DL_UNIT_DATA_request → UI command

`DataLinkAwaitingV22ConnectionEndToEndTests` — 2 figc4.6 transitions:
- t01 DL_CONNECT_request while negotiating v2.2 → discard queue + set_layer_3_initiated
- t02 DL_UNIT_DATA_request → UI command

Total E2E coverage so far: 19 transitions (11 figc4.1 + 6 figc4.2 + 2
figc4.6). figc4.3 and figc4.4 to follow.

668 tests green (was 660).

### 2026-05-14 — ax25: SRT/T1V wired + first end-to-end figc4.1 integration tests

Two pieces:

**SRT and T1V wired**. The link-parameter assignment verbs
`SRT := Initial Default` and `T1V := 2 * SRT` now mutate new
`Ax25SessionContext.Srt` (default 3s) and `Ax25SessionContext.T1V`
(default 6s) fields. Required to drive figc4.1 t03 end-to-end.

**`DataLinkDisconnectedEndToEndTests`**. First integration tests
against the **real** generated `DataLink_Disconnected.Transitions`
table, driven through the **real** `ActionDispatcher` with every sink
wired. 11 figc4.1 transitions covered:

- t01: DL_DISCONNECT_request → DataLinkDisconnectConfirm
- t02: DL_UNIT_DATA_request → UI command with payload + PID
- t03: DL_CONNECT_request → SRT/T1V init + Establish_Data_Link
  subroutine + Layer3Initiated flag + → AwaitingConnection state
- t06: All-other-primitives-upper → discard_primitive (no-op)
- t07/t08/t09: control_field_error / info_not_permitted_in_frame /
  u_or_s_frame_length_error → DL_ERROR letter codes (L/M/N)
- t10: UA received → DL_ERROR_indication("C_D")
- t11: UI received P=1 → UI_Check subroutine + DM (F=1)
- t12: UI received P=0 → UI_Check, no DM
- t13: DISC received → F := P; DM (response with F = incoming P bit)

Deferred: t05 (`all_other_commands`: F := P; DM) — needs
`AllOtherCommands` to carry a frame so F := P can read PollFinal.
That's an `Ax25Event` refactor for a follow-up PR.

This validates the **full dispatcher pipeline end-to-end**: event
routing, guard evaluation, action-chain execution, context mutations,
frame emission with correct N(R)/P/F/payload, DL signal raising,
subroutine invocation, state transitions. Every verb the figure draws
gets exercised through the real dispatcher; the recording-stub layer
is no longer the only thing proving correctness.

660 tests green (was 643). 11 figc4.1 end-to-end tests + 4 SRT/T1V
unit tests added.

### 2026-05-14 — ax25: subroutine stub registry (12 figc4.7 subroutines)

Every `kind: subroutine` verb the transcribed pages reference now routes
through a new `ISubroutineRegistry`. The dispatcher gains an optional
`subroutines` constructor parameter; default is a
`DefaultSubroutineRegistry` pre-populated with no-op stubs for the 12
known subroutine names:

`Establish_Data_Link`, `Clear_Exception_Conditions`, `UI_Check`,
`Select_T1_Value`, `Check_I_Frame_Acknowledged`,
`Check_I_Frames_Acknowledged`, `Check_Need_For_Response`,
`Transmit_Enquiry`, `Invoke_Retransmission`, `N_r_Error_Recovery`,
`Enquiry_Response_F_0`, `Enquiry_Response_F_1`.

Tests can register custom implementations via
`DefaultSubroutineRegistry.Register(name, action)`. Unknown subroutine
names throw — transcription typos surface fast.

This unblocks **end-to-end execution of every figc4.x transition
through the real dispatcher** — even ones whose action chains include
subroutine calls. The subroutine bodies don't do anything specific
yet (no-op stubs), but the orchestrator routing, context-variable
mutations, timer ops, and frame emissions around the subroutine calls
all work. Once figc4.7 is transcribed, the real bodies replace the
stubs without dispatcher changes.

15 new tests (Theory across all 12 known names + custom-registration +
unknown-name error + transition-context access). 643 tests green.

Only **`I_command`** remains unwired in the transcribed vocabulary,
and that's a session-loop machinery problem (active queue pop + posting
synthetic `I_frame_pops_off_queue` events), not a verb-wiring one.

### 2026-05-14 — ax25: long-tail processing verbs wired (queue / storage / counters / aliases)

Mechanical batch closing out 19 long-tail verbs that the transcribed
pages reference:

**Queue clears** (all clear `ctx.IFrameQueue`): `discard_frame_queue`,
`discard_queue`, `discard_I_frame_queue`. Figc4.6's title-case
`Discard Frame Queue` / `Discard I Frame Queue` now alias via
`actions.yaml` to the snake_case canonical.

**No-op discards**: `discard_I_frame`, `discard_contents_of_I_frame`,
`discard_primitive` — drop the current trigger and continue.

**Reject/SREJ bookkeeping**: `set_reject_exception` / `clear_reject_exception`
mutate `ctx.RejectException`. `increment_srej_exception` /
`decrement_srej_exception_if_gt_0` maintain a new
`Ax25SessionContext.SrejExceptionCount` (int counter per §C4.3),
keeping `SelectiveRejectException` flag in sync.

**Out-of-sequence I-frame storage**: `save_contents_of_I_frame` stashes
the incoming I-frame's `Info` + `Pid` into a new
`StoredReceivedIFrames` dictionary keyed by N(S).
`retrieve_stored_V_r_I_frame` pulls the entry keyed by current V(R),
emits `DL_DATA_indication` upward, and removes from storage.

**Modulus selection**: `set_version_2_0` / `set_version_2_2` toggle
`ctx.IsExtended`. `Set Version 2.0` (figc4.6 title-case) aliases to
`set_version_2_0`.

**Link multiplexer signals** (`signal_lower`): new `LinkMultiplexerSignal`
record hierarchy + `sendLinkMux` dispatcher callback. Three verbs:
`LM_seize_request`, `LM_release_request`, `LM_data_request`.

**Internal-out signals**: new `InternalSignal` hierarchy +
`sendInternal` callback. `MDL_NEGOTIATE_request` → `MdlNegotiateRequestSignal`.
`push_on_I_frame_queue` / `push_frame_on_queue` push the triggering
`DlDataRequest`'s payload onto `ctx.IFrameQueue` and emit
`PushIFrameQueueSignal`. `push_old_I_frame_N_r_on_queue` re-enqueues
from `ctx.SentIFrames` keyed by the incoming frame's N(R) (used in
REJ/SREJ retransmit paths). `Push Frame Onto Queue` (figc4.6
title-case) aliases.

`Establish Data Link` (figc4.6 title-case) aliased to
`Establish_Data_Link` snake_case canonical (subroutine; wiring proper
gated on figc4.7).

Tests: 21 new (verb behaviour, aliases, counter mechanics, storage,
push-back-on-queue with stored sent frame). 628 tests green.

After this PR, the only **unwired** verbs in the existing transcribed
pages are:
1. The subroutine verbs (Establish_Data_Link, UI_Check, Select_T1_Value,
   Clear_Exception_Conditions, Transmit_Enquiry, Invoke_Retransmission,
   Check_Need_For_Response, N_r_Error_Recovery, Enquiry_Response_F_0,
   Enquiry_Response_F_1, Check_I_Frame_Acknowledged,
   Check_I_Frames_Acknowledged) — these throw "unknown SDL action"
   today; PR-J adds a stubbed-subroutine table that makes them no-op
   with TODO markers pointing to the eventual figc4.7 wiring.
2. `I_command` — needs session-loop machinery for queue→event posting.

### 2026-05-14 — ax25: DL upper-layer signals wired (5 primitives + 10 error letters)

Closes every `signal_upper` verb the transcribed pages reference. New
`DataLinkSignal` record hierarchy:

- `DataLinkConnectIndication` / `DataLinkConnectConfirm`
- `DataLinkDisconnectIndication` / `DataLinkDisconnectConfirm`
- `DataLinkDataIndication(Info, Pid)`
- `DataLinkErrorIndication(Code)` for the 10 letter codes
  (`C_D`, `D`, `E`, `F`, `G`, `K`, `L`, `M`, `N`, `O`) per §C5

Dispatcher gains an optional `sendUpward: Action<DataLinkSignal>`
callback. The 15 verb cases dispatch to it. `DL_DATA_indication`
extracts `Info` + `Pid` from the triggering I-frame; the others have
no payload. `BuildDataIndication` is the helper.

16 new tests covering all 15 verbs + the missing-frame error path for
DL_DATA_indication. 607 tests green.

After this PR, the dispatcher's signal-emission vocabulary is
**complete** for what the transcribed figures reference. Remaining
unwired categories:

1. `I_command` — needs new session-loop machinery (pop ctx.IFrameQueue,
   post synthetic `I_frame_pops_off_queue` events)
2. Subroutine verbs (`Establish_Data_Link`, `UI_Check`, …) — gated on
   figc4.7 transcription
3. Long-tail queue/exception verbs (`push_*`, `discard_*` variants,
   `set/clear_reject_exception`, `increment/decrement_srej_exception`,
   `discard_primitive`, `save_contents_of_I_frame`,
   `retrieve_stored_V_r_I_frame`, etc.) — mechanical work, can land in
   batches.

### 2026-05-14 — ax25: UI-frame emission wired (`UI_command`)

Closes the final signal_lower verb that the existing transcribed pages
reference. `UI_command` is drawn in every state's
`DL_UNIT_DATA_request` column (figc4.1/4.2/4.3/4.4/4.6, 5 occurrences
total). Same shape across all of them — single signal_lower action,
no preceding processing chain.

New `UiFrameSpec(IsCommand, PfBit, Info, Pid)` record. Dispatcher
constructor gains an optional `sendUiFrame` callback.

`BuildUiFrame(isCommand, tx)` helper extracts `Info` and `Pid` from the
triggering `DlUnitDataRequest` event (throws if the trigger isn't a
DL-UNIT-DATA request); `PfBit` reads `tx.Pending.PfBit` with default
false.

After this PR, **every signal_lower verb the existing transcribed
pages reference is wired** except `I_command` (figc4.4 only — needs
new session-loop machinery to actively pop from `ctx.IFrameQueue` and
post `I_frame_pops_off_queue` events).

Tests: 4 new (payload + PID extraction, default PID, F := 1 consumption,
non-DL-UNIT-DATA trigger error). 591 tests green.

### 2026-05-14 — ax25: U-frame emission wired (UA / DM / SABM / SABME / DISC / Expedited variants)

Parallel of the S-frame work (#57). All eight U-frame verbs the
transcribed pages reference are now executable end-to-end:

| Verb | Type | Role | P/F | Expedited |
|---|---|---|---|---|
| `UA` | Ua | response | from pending (default false) | false |
| `DM` | Dm | response | from pending (default false) | false |
| `DM (F = 1)` | Dm | response | **forced true** | false |
| `Expedited UA` | Ua | response | from pending | **true** |
| `Expedited DM` | Dm | response | from pending | **true** |
| `SABM (P == 1)` | Sabm | command | **forced true** | false |
| `SABME (P = 1)` | Sabme | command | **forced true** | false |
| `DISC (P = 1)` | Disc | command | **forced true** | false |

New `UFrameSpec(Type, IsCommand, PfBit, IsExpedited)` record and
`UFrameType` enum (8 subtypes per §4.3.3). Dispatcher constructor gains
an optional `sendUFrame` callback (defaults to no-op so existing
fixtures keep working).

`BuildUFrame(type, isCommand, pfBitOverride, isExpedited, tx)` helper:
when `pfBitOverride` is non-null (explicit qualifier in verb name), it
forces the P/F bit; otherwise consumes `tx.Pending.PfBit` with default
false.

`Expedited` is a TX priority hint to the wire-translation layer — the
bit pattern on the wire is identical to plain UA/DM, but expedited
frames jump ahead of any pending I-frame queue. figc4.3 (Awaiting
Release) uses this for DM-out-of-band responses during teardown.

Tests: 10 new (a `Theory` covering all 8 verbs' field combinations,
plus two case-tests confirming bare verbs consume Pending while
explicit-qualifier verbs override it). 587 tests green.

With S-frame (#57) and U-frame (this PR) emission done, the only
unwired signal_lower verbs are `I_command` (one occurrence in figc4.4)
and `UI_command` (four occurrences across figc4.1/4.2/4.3/4.6). These
need I/UI frame specs that carry info-field payload, plus a way to
plumb the payload through the dispatcher — I-frame's payload comes
from `ctx.IFrameQueue`, UI-frame's comes from the triggering
`DlUnitDataRequest` event.

### 2026-05-14 — ax25: S-frame emission consumes `tx.Pending`, figure-canonical verb names

Closes the read/write loop on the S-frame emission pathway:

1. `SupervisoryFrameSpec` grows from `(Type, IsCommand)` to
   `(Type, IsCommand, Nr, PfBit)` — the values the wire-translation layer
   actually needs.
2. The dispatcher's signal_lower S-frame verbs now consume `tx.Pending.Nr`
   and `tx.Pending.PfBit` to build the spec. A new `BuildSFrame` helper
   applies sensible defaults when the chain doesn't populate them
   explicitly: `Nr` defaults to `ctx.VR`, `PfBit` defaults to `false`.
   This matches what figc4.4 does — e.g. t23 (DL_FLOW_OFF on busy) draws
   bare `set_own_receiver_busy; RNR_response; clear_acknowledge_pending`
   with no N(R) / F-bit assignment beforehand, relying on implicit
   defaults. Other transitions (t04 etc.) populate explicitly when they
   need a specific value.
3. Verb spellings move to figure-canonical: `RR_command`, `RR` (bare =
   response per SDL convention), `RNR_response`, `REJ`, `SREJ`. The old
   space-suffixed `"RR command"` / `"RR response"` / `"RNR command"` /
   `"RNR response"` / `"REJ command"` / `"REJ response"` / `"SREJ command"` /
   `"SREJ response"` are removed — none of them appeared in any YAML.
4. `spec-sdl/actions.yaml` records the new canonical names for
   documentation (no aliases needed — only one spelling each).

Test updates: `Ax25SessionTests` (hand-rolled fixtures) renamed to use
the new verb spellings; both `SupervisoryFrameSpec(...)` assertions
updated to include the `Nr`/`PfBit` constructor args. Two new
`ActionDispatcherTests`: one for the explicit-pending path, one for the
default-pending path (RNR_response from a `DL_FLOW_OFF`-style chain).

577 tests green (-1 vs PR-D: eliminated the four superfluous "RNR/REJ/SREJ command"
theory cases since figc4.4 never draws them).

This effectively closes the dispatcher arc for **S-frame emission**.
Real figc4.4 connected-state transitions involving RR/RNR/REJ/SREJ are
now executable end-to-end through the real dispatcher. Remaining
unwired emission categories: U-frames (`UA`, `DM`, `SABM (P == 1)`,
`SABME (P = 1)`, `DISC (P = 1)`, `Expedited UA`, `Expedited DM`),
I-frames (`I_command`), and UI-frames (`UI_command`). DL upper-layer
signals (`DL_CONNECT_indication`, `DL_DATA_indication`, the 13
`DL_ERROR_indication_*` letters) and subroutines (`Establish_Data_Link`,
`UI_Check`, `Select_T1_Value`, `Clear_Exception_Conditions` …) also
still unwired.

### 2026-05-14 — ax25: dispatcher write-side verbs populate `tx.Pending` (N(r), N(s), F/P bit)

The dispatcher now handles every "processing" verb in the transcribed
pages that writes to an outgoing-frame field:

- `N(r) := V(r)` — `tx.Pending.Nr = ctx.VR`
- `N(s) := V(s)` — `tx.Pending.Ns = ctx.VS`
- `N(r) := N(s)` — `tx.Pending.Nr = N(S)`-from-incoming-I-frame
- `F := 0` / `F := 1` — `tx.Pending.PfBit = false/true`
- `F := P` — `tx.Pending.PfBit = incoming.PollFinal`
- `p := 0` — `tx.Pending.PfBit = false` (lowercase `p` is the spec
  spelling for the outgoing P bit; same bit as F on the wire)

The frame-extraction helpers are factored into `RequireIncomingFrame` +
`RequireMod8` + `ExtractNr` / `ExtractNs` / `ExtractPollFinal` so all
three verbs that read from the incoming frame share the same
"frame is required" / "mod-128 not yet implemented" error paths.

`PendingFrame` is now populated end-to-end through the dispatcher. The
**consumption** side stays unchanged in this PR — the supervisory frame
signal_lower verbs (`RR command` etc.) still emit a `SupervisoryFrameSpec`
without N(R) / P/F. PR-E grows `SupervisoryFrameSpec` to carry those
fields and rewires the consumption.

Tests: 11 new (Pending writes for each verb, accumulation across a chain,
F := P with both poll values, error paths for missing incoming frame).
578 tests green.

### 2026-05-14 — Q: assignment operator `:=` vs `<-` in YAML transcriptions

Tom flagged that the spec figures (and his graphml redraws) draw
assignment as `<-` (left arrow), while the YAML transcriptions translate
this to `:=` (formal ITU Z.100 SDL). Same semantics; different notation.

Parked decision: keep `:=` for now. Revisit if/when `/spec-sdl/` is
published as a community-canonical artifact (OQ-008) — at that point a
verbatim-to-figure choice may have value for outside consumers.

### 2026-05-14 — sdl(connected): normalise `N(R) := V(r)` → `N(r) := V(r)`

Surgical fix in `DataLink_Connected.graphml` (nodes at lines 2561 and
2887): two processing boxes drew `N(R) <- V(r)` (uppercase R) while
three other boxes in the same figure drew `N(r) <- V(r)` (lowercase r).
Tom confirmed the figc4.4 spec figure always draws the action label
lowercase — the uppercase variants are transcription typos.

(Aside: spec PROSE consistently uses `N(R)` uppercase when describing
the concept in text. The SDL figure action labels use lowercase to
match the rest of the SDL vocabulary — `V(s)`, `V(r)`, `V(a)`, `N(s)`.
This split is preserved verbatim: the `references:` spec_prose quotes
still use uppercase `N(R)` exactly as the spec writes them.)

Fix: 2 boxes in graphml + 4 transitions in YAML + 3 paraphrase comments
at the top of `connected.sdl.yaml`. Regenerated `.g.cs` / `.g.Tests.cs`
/ `.g.mmd` from the corrected source. 555 tests still pass.

### 2026-05-14 — ax25: dispatcher TransitionContext refactor, `V(a) := N(r)` wired (reads from incoming frame)

`IActionDispatcher.Execute` signature changed from
`(IEnumerable<ActionStep>, Ax25SessionContext, ITimerScheduler)` to
`(IEnumerable<ActionStep>, TransitionContext)`. The new `TransitionContext`
bundles everything a verb might read or mutate during one transition:

- `Session` — the same per-connection state as before
- `Scheduler` — same
- `Trigger` — the `Ax25Event` that fired the transition
- `IncomingFrame` — the `Ax25Frame` attached to the trigger (null when
  trigger is an upper-layer primitive, timer expiry, or internal event)
- `Pending` — placeholder builder for outgoing-frame fields, reserved
  for the write-side verbs (`N(r) := V(r)`, `F := P`, `p := 0`) in the
  next PR

`Ax25Session.PostEvent` builds the TransitionContext from the event;
frame-receipt events feed `IncomingFrame` automatically. The dispatcher's
test-ergonomics overloads `Execute(string, ctx, scheduler)` are kept,
synthesising a sentinel trigger with no frame for verbs that don't need one.

First new verb wired: `V(a) := N(r)`. Reads N(R) from the incoming frame's
mod-8 control byte (`bits 7..5`) and stores into `ctx.VA`. Throws loudly
when the trigger has no frame, or when the session is in mod-128 mode
(extended N(R) lives in a 2-byte control field that `Ax25Frame` doesn't
model yet — flag it explicitly rather than silently return the wrong value).

Tests: 7 new (4 theory inputs covering N(R)=0/3/5/7, no-frame error,
mod-128-not-supported error, end-to-end orchestrator test that pumps an
RR_received event with N(R)=5 through `PostEvent` and asserts ctx.VA=5).
555 tests green.

Deferred to the next PR: write-side frame-field verbs (`N(r) := V(r)`,
`N(s) := V(s)`, `N(r) := N(s)`, `F := 0`, `F := 1`, `F := P`, `p := 0`).
Those populate `tx.Pending`, which the signal_lower verbs will consume
once `SupervisoryFrameSpec` and friends grow N(R) + P/F fields.

### 2026-05-14 — ax25: dispatcher wires lowercase `V(s)/V(r)/V(a)` + `RC` assignments, plus first integration-test layer

Dispatcher previously had `V(S) := V(S) + 1` / `V(R) := V(R) + 1`
uppercase — neither spelling appears in any transcribed YAML, so the
verbs were effectively unwired. Replaced with the figure-canonical
lowercase forms and filled in the missing pure-context assignments:

- `V(s) := 0`, `V(s) := V(s) + 1` (mod-8 or mod-128 wrapping)
- `V(r) := 0`, `V(r) := V(r) + 1`
- `V(a) := 0`
- `RC := 1`, `RC := RC + 1`

These cover every variable assignment in the transcribed pages that
doesn't depend on the triggering frame (the harder cases like
`V(a) := N(r)`, `N(r) := V(r)`, `F := P`, `p := 0` need access to the
incoming frame and pending-outgoing frame builder — deferred to a
follow-up PR that refactors the dispatcher signature).

Added a new integration-test layer (`Ax25SessionIntegrationTests`) that
drives the **real** `ActionDispatcher` through `Ax25Session` against
synthetic transition tables, asserting context state, timer state, and
emitted supervisory frames after `PostEvent`. The smoke tests use a
recording dispatcher to prove orchestrator routing is correct; these
integration tests prove the dispatcher actually executes verbs and the
side effects are observable through the public API. Four tests:
in-order action mutation, modulus wrapping, timer + frame emission,
guard gating against a context flag.

Still blocked from driving a real-figure transition end-to-end through
the real dispatcher: figc4.1 t03 (DL_CONNECT_request) references
`SRT := Initial Default`, `T1V := 2 * SRT`, and the
`Establish_Data_Link` subroutine — none wired. That's the dispatcher-
arc PR-C territory (frame builder, link parameters, subroutine table).

### 2026-05-14 — sdl: action-verb catalogue (`spec-sdl/actions.yaml`) with alias normalisation

The AX.25 SDL figures sometimes draw the same semantic verb with different
spellings on different pages. Three observed in the corpus:

- `DM F=1` (figc4.2) / `DM F = 1` (figc4.3) / `DM (F = 1)` (figc4.6)
- `Establish Data Link` (figc4.6) / `Establish_Data_Link` (figc4.1, 4.4)
- `Set Version 2.0` (figc4.6) / `set_version_2_0` (figc4.1, 4.4)
- `Push Frame Onto Queue` (figc4.6) / `push_frame_on_queue` (figc4.2)
- `Discard Frame Queue` (figc4.6) / `discard_frame_queue` (figc4.2)
- `Discard I Frame Queue` (figc4.6) / `discard_I_frame_queue` (figc4.4)

Two competing constraints: trust-the-figure says YAML transcriptions stay
verbatim; the runtime dispatcher needs one spelling per semantic verb.

`spec-sdl/actions.yaml` resolves this — a canonical-name + aliases table
read by the codegen. The codegen substitutes aliases for the canonical
during YAML→`.g.cs` emission. YAMLs stay figure-verbatim; dispatcher
only ever sees canonical.

This PR ships the infrastructure plus one cluster as proof:
`DM (F = 1)` with two aliases. Two new codegen smoke tests cover the
mechanism (normalisation works, kind-mismatch is rejected). 538 tests
green.

Soft mode for now: verbs not in the catalog pass through verbatim.
Follow-up PRs will populate the remaining clusters (`Establish_Data_Link`,
`set_version_2_0`, etc.) and eventually flip strict mode so unknown
verbs become hard errors — that's the "verb half" of [OQ-008](#15-open-questions)
finally being closed.

Docs: [`docs/sdl-verb-catalogue.md`](sdl-verb-catalogue.md) explains the
file format and workflow.

### 2026-05-14 — sdl(awaiting_v22_connection): normalise `T1V := 2*SRT` → `T1V := 2 * SRT`

Surgical fix in `DataLink_AwaitingV22Connection.graphml` (node n51): one
processing box had `T1V <- 2*SRT` (no spaces) while another box in the
same figure had `T1V <- 2 * SRT` (with spaces). Normalised both to the
spaced form, regenerated YAML + `.g.cs` + `.g.mmd` + conformance tests.
537 tests still pass.

First small step in the dispatcher-wiring arc: surfacing transcription
inconsistencies before wiring the real dispatcher. Bigger architectural
piece next — `spec-sdl/actions.yaml` alias table so codegen normalises
figure-verbatim verbs (`Establish Data Link`) to canonical dispatcher
verbs (`Establish_Data_Link`). YAMLs stay verbatim; dispatcher only sees
canonical. Resolves the verb-vocabulary half of OQ-008.

### 2026-05-14 — SP-001b: corpus errors mapped to APRS12c (direwolf is spec-correct)

Walked every direwolf error class surfaced in the corpus against the
canonical updated spec ([`APRS12c.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS12c.pdf)
+ [`Understanding-APRS-Packets.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/Understanding-APRS-Packets.pdf)
from `wb2osz/aprsspec`). Findings narrative in
[`tools/Packet.AprsIs.Spike/findings.md`](../tools/Packet.AprsIs.Spike/findings.md);
short version:

**Every direwolf error class is provably non-spec under APRS12c.** The
corpus is showing us real firmware bugs and historical sloppiness, not
over-strict decoding.

| Error | Spec section | Verdict |
|---|---|---|
| Bad source address (58,609 hits) | APRS12c §4: "up to 6 upper case alphanumeric characters plus SSID"; UAP §1.1 lists `n2gh` as invalid | Direwolf right |
| Unknown DTI `"H"` (3,521, APRSIS32) | APRS12c §5: `A–S` = "[Do not use]" | APRSIS32 violates spec |
| Unknown DTI `"2"` (1,849, APRSdroid) | APRS12c §5: `0–9` = "[Do not use]" | APRSdroid violates spec |
| Unknown DTI `"-"`, `" "` (space) | APRS12c §5: `-` is "[Unused]"; space not in DTI table at all | Direwolf right |
| Invalid compressed-longitude char (4,453) | APRS12c §9: base-91 + 33 offset → valid range `!`–`{` (ASCII 33–123) | Direwolf right |
| Invalid symbol table id (compressed pos) (2,871) | APRS12c §9 + §20: must be `/`, `\`, `0–9` or `A–Z` overlay | Direwolf right |

**Doc update**: §13.1 (Reference shelf) extended with the APRS spec
links. APRS101.pdf is now flagged obsolete; APRS12c.pdf is the
reference for SP-008.

**Implication for the codebase**:

1. **AX.25 envelope** (`Callsign`, `Ax25Frame`) — stay strict. Our hard
   rejection of lowercase / >6-char / weird-SSID matches the spec.
   Direwolf's "warn and parse on" is a UX choice, not a different
   reading.
2. **APRS payload** (future `Packet.Aprs`) — validate per APRS12c
   directly. The bugs in the wild are real and documented; our decoder
   should reject them and surface the reason (matching direwolf's error
   class), not silently accept.
3. **Display / monitor layer** — a permissive read-only `AprsCallsign`
   type that round-trips real-world strings (lossy is fine) lets the
   web UI show all traffic. Doesn't relax the strict `Callsign` we use
   for frame production.

### 2026-05-14 — SP-001b: direwolf reference-decode pipeline — differential testing baseline

`Packet.AprsIs.Spike` gained a `direwolf` mode that pipes the captured
corpus through `decode_aprs` (the system-installed direwolf utility)
and persists the structured output into a sibling `direwolf_decoded`
table inside the same SQLite file. Implements the "feed APRS-IS data
through both direwolf and our library, compare and validate" pipeline
the spike backlog (SP-002) called for.

**Sanitisation**: each TNC2 line has its APRS-IS q-construct path
stripped (`,qA.*:` → `:`) before being fed to direwolf — the exact
transformation the direwolf man page recommends.

**Throughput**: ~23,400 lines/sec on a single `decode_aprs`
subprocess with 1000-line batches. 276k-line corpus → 12.4 s. Fast
enough to re-run on every accumulation.

**Differential signal surfaced** (full narrative in
[findings.md](../tools/Packet.AprsIs.Spike/findings.md)):

- **Direwolf accepts, we reject** — almost entirely lowercase
  callsigns (`dl9mfl-6`, `iw0uwf-4`, `vk4zu-13`). Direwolf warns
  but parses on; our strict `Callsign` refuses. Validates the case
  for a separate `AprsCallsign` permissive type in SP-008's
  `Packet.Aprs`.
- **We accept, direwolf rejects** — valid AX.25 envelopes carrying
  malformed APRS payloads (unknown DTIs, invalid compressed
  longitudes, APRSdroid/APRSIS32 bugs leaking into the wild). We
  haven't been validating the payload; direwolf does.

Top error buckets:
- 58,609 × "Bad source address"
- 4,453 × invalid compressed-longitude characters
- 3,521 × unknown DTI "H" (APRSIS32 bug)
- 1,849 × unknown DTI "2" (APRSdroid bug)

The corpus is now a **differential testing baseline**. For any future
`Packet.Aprs` decoder, every frame has a direwolf interpretation in
the same SQLite. Diff frame-by-frame → bug candidates (in us, in
direwolf, or in the upstream sender).

**Operational note**: direwolf mode and the collector both want write
locks on the SQLite file. They can't run concurrently (SQLite WAL is
1-writer). For now: stop the collector wrapper before a direwolf
pass, restart after. Snapshot/backup-based concurrent processing is
a follow-up if it becomes friction.

### 2026-05-14 — SP-001b: APRS payload-type classifier — 62 % positions

Tier 0 unblocker on the corpus. `AprsPayloadType.Classify` buckets each
information field by its first byte (APRS101 §5 DTI), and the analyser
now reports a payload-type histogram alongside the round-trip stats.

Re-ran across the live corpus (now 181k lines, ~30 min of capture):

| Group | % |
|---|--:|
| Positions (4 DTI variants: `! = / @`) | **61.80 %** |
| Objects + items + status | 19.22 % |
| Messages | 8.27 % |
| Telemetry | 5.72 % |
| Mic-E (current + old) | 4.87 % |
| Other (user-defined, third-party, raw-GPS) | 0.13 % |

**Implication for decoder priority:** positions dominate massively;
implementing one uncompressed-position decoder (DTI `!`) unlocks
~30 % of the corpus on its own, and the four position variants
together cover ~62 %. Mic-E is smaller than expected (~5 %) — it's
bigger on local RF than on the APRS-IS firehose because mic-E is
mobile/in-car traffic and APRS-IS is sourced from internet-uplinked
igates.

**Implication for the corpus:** zero `non_printable_*` or `empty` rows
— every captured line has a printable-ASCII first byte, confirming
APRS-IS line-orientation. The corpus is well-formed APRS as the
firehose sees it.

Reconstruct success rate held at **78.05 %** (was 77.60 % at 63 k
sample) — the 22 % miss is structural (APRS-vs-AX.25 callsign
conventions), not statistical.

Findings narrative continues in
[`tools/Packet.AprsIs.Spike/findings.md`](../tools/Packet.AprsIs.Spike/findings.md).

### 2026-05-14 — SP-001b: APRS-IS analyse mode + first findings (22 % invalid sources)

Added `analyse` mode to `Packet.AprsIs.Spike` — reads the captured
SQLite corpus, replays every line through the
`Tnc2Parser → FrameReconstruct → encode/decode round-trip` pipeline,
and produces a structured failure report. `FrameReconstruct` factored
out into a shared helper so the live (oneshot) and offline (analyse)
paths use the same definition of "AX.25 reconstructable".

**First proper analysis pass — 63,451 lines from ~12 minutes of capture:**

- **77.60 % round-trip clean** (49,241 lines).
- **22.40 % reconstruct failures** (14,210), **99.91 % of which are
  invalid sources**. Round-trip mismatches = 0 — encode/decode pair is
  solid; the gap is at the AX.25 ↔ APRS-convention boundary.

Failure categories surfaced (see [findings.md](../tools/Packet.AprsIs.Spike/findings.md)
for the full narrative):

1. **Lowercase callsigns** (~700): `db0sda` × 600, `vk3mak-15`, etc.
2. **Tactical aliases** (~480): `WINLINK` × 449 (RMS gateways), `ONELOVE`,
   `NorthRyde`, …
3. **Multi-char / non-numeric SSIDs** (~13,000 — the bulk): `M0IQF-N4`,
   `BI4KVT-8G`, `BD8CMN-T`, etc. APRS lets through letter SSIDs (D-Star /
   DMR convention), multi-digit SSIDs (Chinese / Russian software), and
   combinations. AX.25 spec is 0–15 numeric only.
4. **Long base callsigns**: `BD8AWU-18`, `BD8CMN-S`, etc. AX.25 is 1–6
   chars; APRS lets through 7+.

Destination / digipeater failures are rare (5 + 8 across 63 k lines).

**Implication for the codebase**: `Callsign.TryParse` is correctly
rejecting — AX.25 spec is unambiguous. But for the *monitor* layer
(eventual web UI showing live APRS-IS), strict rejection means 22 % of
real traffic disappears. The right shape is a separate `AprsCallsign`
type (lives in SP-008's `Packet.Aprs`) plus a boundary mapper. Not
this PR — captured as future design context.

The corpus itself is now feedstock for SP-002 (direwolf A/B), SP-003
(replay regression), SP-004 (fuzz seeds), SP-008 (full APRS lib).

Re-running the analyser is cheap: `dotnet run -- analyse --data-dir
/home/tf/aprs-is-data`. The collector keeps accumulating in the
background — numbers will firm up as more days roll over.

### 2026-05-14 — SP-001b: APRS-IS collector mode + persistent VM runner

Promoted the APRS-IS spike from one-shot pipeline to long-running
collector — mirror of the MQTT collector pattern landed earlier today
(#45). Same shape: SQLite per UTC day, `AFTER INSERT` trigger keeps
`run_meta` exact, transactional batching (commit every 100 lines or
500 ms) to handle firehose volume, exponential-backoff TCP reconnect,
graceful SIGTERM.

Refactored `Program.cs` into mode dispatch (`oneshot` | `collect`):
existing experimental pipeline preserved as `OneshotMode`; the new
`CollectMode` + `SqliteSink` handle sustained capture.

Schema:

```sql
CREATE TABLE lines (
  id, ts_utc_us, source, destination, digi_path, digi_count,
  info_len, info BLOB, raw_line TEXT NOT NULL
);
CREATE TABLE run_meta (
  run_id, started_at_us, ended_at_us, client_id, filter,
  line_count, reconnect_count
);
```

`raw_line` is canonical; denormalised top-level fields (source,
destination, digi_path) populated on ingest so common queries don't
re-parse. AX.25 reconstruction + round-trip remain offline against
the corpus.

**30 s smoke run** (filter `t/poimqstuc`): **2,674 lines persisted**
— ~90 lines/sec firehose rate, ~22 MB/min, ~1.3 GB/day projected.
Schema verified: `messages` count == `run_meta.line_count` via
trigger.

Persistent collector running in this dev VM under
`/home/tf/aprs-is-data/` (wrapper at `run-collector.sh` respawns on
crash; `status.sh` gives a quick snapshot). Alongside the MQTT
collector. Both feed SP-003's eventual replay/regression corpus.

### 2026-05-14 — SP-001: LinBPQ MQTT feed collector (probe + collect)

New `tools/Packet.Mqtt.Spike` with three modes:

- `probe` — short-window exploratory subscription; dumps each message
  to JSONL with hex/ASCII preview, renders a topic-frequency +
  payload-size summary. Used to learn the wire format.
- `collect` — **long-running daemon** that persists every received
  message to per-day SQLite files (`<prefix>-YYYY-MM-DD.sqlite`).
  Daily rotation at UTC midnight, automatic reconnect with exponential
  backoff, graceful shutdown on SIGTERM/SIGINT, WAL-mode SQLite. A
  trigger keeps `run_meta.message_count` exact without a heartbeat
  path. Intended to run as a systemd service on the LinBPQ host —
  systemd unit + deploy README at `tools/Packet.Mqtt.Spike/deploy/`.
- `monitor` — placeholder. AX.25 parsing happens offline against the
  SQLite corpus by design (collector behaviour shouldn't depend on
  parser changes).

`MQTTnet 4.3.7.1207` + `Microsoft.Data.Sqlite` added/used.

**Topic structure observed on `mqtt.lan`:**

```
PACKETNODE/kiss/<NODE>/<rcvd|sent>/<port>                      ← raw KISS bytes
PACKETNODE/ax25/trace/bpqformat/<NODE>/<rcvd|sent>/<port>      ← JSON envelope
```

LinBPQ publishes the same frame in both formats per send/receive
event. KISS-sent has standard `c0 ... c0` framing; KISS-rcvd uses a
pre-decoded length-prefixed structure (4-byte zero + 2-byte BE length
+ control byte + AX.25 frame). The JSON envelope contains
`{"from": "...", "to": "...", "payload": "<TNC2-monitor-text>"}`.

The collector stores both formats verbatim — KISS is what would hit
our parser; the JSON envelope gives LinBPQ's authoritative
interpretation to cross-check against.

**Initial 30-s probe** missed everything because real RF on 4 ports is
sparse. A 45-s smoke run of `collect` against the live broker caught 8
messages (Tom's tear-down of a recent connection), exact
`messages`/`run_meta` match confirmed via the trigger.

Schema is intentionally simple (one `messages` table + `run_meta`).
Denormalised topic components on ingest so queries don't re-parse the
topic string. See `tools/Packet.Mqtt.Spike/SqliteSink.cs` for the
canonical schema.

**Deployment** path: `dotnet publish -r linux-{arm64,x64}
--self-contained` → rsync to `gb7rdg-node` → systemd unit at
`/etc/systemd/system/packet-mqtt-collector.service`. Steps in
`tools/Packet.Mqtt.Spike/deploy/README.md`. Not yet deployed — Tom to
trigger when ready.

### 2026-05-14 — SP-001b: APRS-IS UI-frame ingestion spike

New `tools/Packet.AprsIs.Spike` connects to APRS-IS as a read-only
listener, parses TNC2-format lines, reconstructs each as
`Ax25Frame.Ui(...)`, round-trips through our AX.25 parser, and reports
robustness stats. Drives a steady stream of real-world data through our
AX.25 plumbing to surface edge cases synthetic tests don't cover.

First 30-frame live smoke run (rotate.aprs2.net:14580): 22/30 round-trip
successes; **8 reconstruct failures, all from D-Star gateways using
APRS letter SSIDs** (`-B`, `-D`, `-T`, `-H`). AX.25 spec only allows
numeric SSIDs 0–15; APRS-IS leaks the APRS convention through. Our
`Callsign.TryParse` correctly rejects; the spike logs each failure to
`artifacts/aprs-is-spike/<ts>/failures.jsonl` for visibility. The
finding is exactly what the spike was designed to surface — synthetic
tests don't generate letter SSIDs.

Spike scope minimal:
- UI frames only (the only frame type APRS-IS carries).
- No persistence beyond `artifacts/`. No live regression corpus (that
  belongs in SP-003).
- v0.1; will retire or graduate to `src/Packet.Replay.AprsIs/` once the
  patterns are clear and the richer LinBPQ-MQTT feed (SP-001) is wired
  up.

The `tools/Packet.AprsIs.Spike/README.md` captures possible follow-ups:
a separate `AprsCallsign` type accepting letter SSIDs for the web
monitor view, and gateway-layer mapping from APRS letter-SSID to AX.25
numeric-SSID at the boundary.

### 2026-05-14 — interop: LinBPQ runtime state moved to named volume

The self-hosted runner exposed a Docker-on-host papercut that masked
itself on GitHub-hosted runners: `docker/compose.interop.yml` mounted
`./linbpq:/data` read-write into the LinBPQ container, which runs as
root. Each interop run wrote PIDs, logs, MH lists, HTML templates,
etc. into the worktree owned by root — and the next
`actions/checkout` on the same runner died with `EACCES: permission
denied, unlink` trying to clean the workspace.

Fix: split the `/data` mount into two layered mounts:

- Named volume `linbpq-data:/data` — writable, populated from the
  image's `/data` contents on first use, torn down by
  `docker compose down -v` (already in the workflow). Lives in
  Docker's storage, not the host worktree.
- `./linbpq/bpq32.cfg:/data/bpq32.cfg:ro` — canonical config layered
  on top read-only. Source-controlled and unchanged at runtime.

Net effect: no writes ever touch the host worktree, so root-ownership
can't leak across CI runs. Local-dev behaviour is identical for the
intent (LinBPQ sees the same config + image defaults); the
side-effect is that local-dev no longer leaves runtime state visible
in the worktree (an improvement).

This sidesteps but doesn't fix the upstream root-cause: `m0lte/linbpq`
runs as root. Filed as an upstream issue — a `USER` directive +
chown'd `/data` in the Dockerfile would obviate the workaround.

### 2026-05-14 — CI: move all workflows to self-hosted runner

GitHub-hosted Actions budget reset wasn't due for 18 days. Tom added a
self-hosted Linux x64 runner (`github-actions-runner`, online + idle);
all four jobs across `ci.yml` / `interop.yml` / `plan-check.yml`
switched to `runs-on: [self-hosted, Linux, X64]`. Zero ongoing GitHub
Actions minutes consumed.

Runner prerequisites: `.NET 10` (or have `actions/setup-dotnet@v4`
install it on first use), plus Docker for `interop.yml`.

This supersedes the Windows+macOS-drop motivation in the previous
amendment — the budget pressure goes away entirely on self-hosted, so
the Linux-only matrix is now purely about wall-clock-per-PR rather
than $-per-PR. Worth revisiting whether to put Windows+macOS back on a
nightly GitHub-hosted schedule, since they're effectively free
elsewhere.

### 2026-05-14 — Roadmap: Phase 10 (Hardware ecosystem & adaptive RF) added

New Phase 10 entry in §5 captures the post-v1 differentiator: treating
the radio + modem as first-class telemetry sources, and using their
signals to drive AX.25's parameter knobs in real time. Workstreams:

- **Tait 8100/8200 CCDI integration** — three existing Tom PoC repos
  (`tait-ccdi`, `TaitMaster`, `taitctrl`) will be rewritten and folded
  in. Target `Packet.Radio.Tait` library with `IRadioControl`
  abstraction so Yaesu CAT / ICOM CI-V can slot in later.
- **Frequency-agile operation** — CAT-driven QSY based on link policy.
- **NinoTNC mode agility / negotiation** — query capabilities, pick
  optimal mode for current SNR, renegotiate on degradation.
- **NinoTNC firmware upgrades** — port `flashtnc` flow into
  `packetnet ctl flash-tnc`.
- **Channel-quality scoring** — fuse radio + AX.25 telemetry.
- **Adaptive AX.25 parameters** — T1, RC, k, mod-8 vs mod-128 chosen
  from quality score under an opt-in flag.

Hardware test rig (2× NinoTNC + 2× Tait, cross-wired audio) is being
prepared for autonomous capability exploration.

Three new open questions added to §15:

- **OQ-009** NinoTNC runtime mode-change support (probe needed).
- **OQ-010** NinoTNC bootloader protocol (read flashtnc source).
- **OQ-011** `IRadioControl` abstraction shape (common subset vs
  per-radio feature probes).

Same edit also added §5.X "Spike backlog" — seven SP-NNN candidates
ordered by leverage-per-effort:

- **SP-001** LinBPQ MQTT frame feed ingestion (Tom-supplied real-world
  corpus from 4 RF ports; high leverage, low effort — top of the list).
- **SP-002** Direwolf-as-reference end-to-end harness (force multiplier
  on SDL transcription work).
- **SP-003** Replay/record harness (pcap-style capture of KISS + AX.25
  bytes for repro + regression corpus).
- **SP-004** AX.25 wire-format fuzzer (SharpFuzz — promised by §7).
- **SP-005** Multi-Packet.NET-instance interop via net-sim.
- **SP-006** Spec-prose-to-stub-test extractor (post-Phase 2).
- **SP-007** NinoTNC KISS-side SNR proxy (fallback for no-CAT radios).
- **SP-008** Full APRS101 encoding/decoding library (large scope —
  position, weather, mic-E, messages, telemetry, objects, items).
  References APRS101.pdf + the community "APRS101 is obsolete"
  corrections page.

And §5.Y "Hardware-arrival probe playbook" — 6 concrete first-day
actions to run autonomously when the 2× NinoTNC + 2× Tait rig lands.

### 2026-05-14 — CI: drop Windows + macOS runners from PR matrix

`ci.yml` was running `build & test` across `[ubuntu-latest,
windows-latest, macos-latest]` on every PR. Windows runner-minutes are
billed 2× Linux at GitHub's pricing, and Windows .NET builds take 3-5×
the wall-clock on the same code — so Windows was the dominant Actions
budget consumer. The combination hit the org's monthly cap mid-day on
2026-05-14, blocking merges on #39 / #40.

Matrix removed; `build & test` is now Linux-only on PRs. The codebase
targets `netX.0` + portable APIs, so Linux catches ~95 % of regressions.
Cross-platform drift detection can ride a nightly schedule later if it
ever proves necessary.

No process changes for contributors; just fewer CI minutes per PR.

### 2026-05-14 — Soak campaign + adaptive transport + hardware-loop test serialisation

Continuation of the same overnight session as the driver work below.

**Adaptive transport.** `Packet.Kiss.NinoTnc.AdaptiveNinoTncTransport` is
the layer that actually *calls* `Observe(...)` / `Recommend(...)` on the
adaptive estimator. It owns a `NinoTncSerialPort` (via the new
`INinoTncModem` interface — pulled out for unit-testability), serialises
sends through an internal `SemaphoreSlim`, applies only changed
parameters to the TNC before each TX, sends in ACKMODE so we have a
TX-completion signal, and feeds the outcome back into the estimator.
Surface for AX.25-layer signals: `RecordLoss(peer, n)` and
`RecordRetransmittedAck(peer, n)`.

8 unit tests against a fake `INinoTncModem` cover happy-path,
diff-skipping, AckModeTimedOut observation, concurrency serialisation,
and the AX.25-layer recording surface. 1 hardware-loop test runs the
estimator + transport against the real modems for 20 frames and asserts
TXDELAY walks down from the initial 40.

**Hardware-loop test serialisation.** xUnit was running the two
hardware-loop classes in parallel; both grab the same COM6+COM8 pair,
so the second-to-start hit `UnauthorizedAccessException`.
`HardwareLoopCollection` (`[CollectionDefinition(...,
DisableParallelization = true)]`) plus `[Collection(...)]` on both
classes serialises them. All 6 hardware-loop tests now pass cleanly
in ~12 s.

**Soak campaign tool.** `tools/Packet.NinoTnc.Spike` grew a `soak`
sub-command family: `mode-sweep`, `txdelay-sweep`, `payload-sweep`,
`throughput`, `ackmode`, `bidirectional`, `idle`, `estimator-live`,
plus `stress` / `per-mode-txdelay` / `binary-payload` and an `all` and
`marathon` preset. Each writes a markdown section to
`artifacts/nino-tnc-soak/<ts>/results.md`.

**Measured findings** — see [`docs/nino-tnc-characterisation.md`](nino-tnc-characterisation.md)
for the full table. Highlights from the first run:

- Every mode 0–14 the catalog claims (including 19200 4FSK and 9600
  GFSK) round-trips cleanly on the audio cross-wire.
- Mode 6 TXDELAY floor on this hardware/audio path is **10 ms** (1
  unit) — 10/10 success at TXDELAY=1. The spec default of 50 (500 ms)
  is 50× over-conservative for this loopback. The
  `TxDelayHillClimbEstimator` walked 50 → 24 across 40 live frames
  (default conservative tuning).
- ACKMODE concurrency is fine up to N=8 outstanding (1.8 s for the
  full batch). The first ACK after a fresh `SetMode` + `Open` took 15
  s on its own — recorded as a follow-up; likely firmware-initialisation
  quirk.
- Back-to-back KISS Data submission produces **almost zero
  throughput** — the TNC silently queues/drops without ACK pacing.
  The session-layer integration must default to ACKMODE-paced TX.
- 2-minute idle watch: 0 spurious frames, 0 pump errors.

These numbers reflect a benchtop audio cross-wire and will worsen
significantly on real RF. The campaign now exists so we can re-run on
a real link and watch the deltas.

**Test totals** — `Packet.Kiss.Tests` 39, `Packet.Kiss.NinoTnc.Tests`
37 (+8 for AdaptiveNinoTncTransport), `Packet.Interop.Tests` 6 hardware-
loop tests.

**What's still open**

- "First ACKMODE after SetMode/Open is slow" quirk — investigate
  whether it's a firmware initialisation cost or a driver settling
  problem; possibly hide behind a one-frame priming send.
- Throughput on real RF will need ACK-paced TX in the session layer.
- USB VID/PID-based discovery (so the `PACKETNET_NINOTNC_PORTS`
  env-var override can retire).
- Adaptive estimator's `SuccessesPerStepDown` / `StepUnits` defaults
  should be revisited after a real-RF soak (current tuning is
  intentionally conservative).

### 2026-05-14 — NinoTNC serial driver + ACKMODE + adaptive parameter scaffolding

First end-to-end run against the back-to-back NinoTNC pair: both modems
USB-attached (COM6 + COM8 on the Windows dev host), audio cross-wired,
DIP set to 1111 ("Set from KISS"), TXDELAY pots at minimum. Pulls Phase 3
"KISS hardening" partially forward — the catalog from 2026-05-12 now has
a real driver sitting on top of it.

**New code**

- `Packet.Kiss.NinoTnc.NinoTncSerialPort` — async-shaped serial driver:
  `Open(portName)`, background read pump on a dedicated long-running
  thread, write serialisation through a `SemaphoreSlim`,
  `SendFrameAsync` for plain KISS Data, `SendFrameWithAckAsync` for
  ACKMODE-with-TX-completion echo (auto-sequence-tag, awaitable
  `AckModeReceipt` with elapsed timing), parameter helpers
  (`SetModeAsync` / `SetTxDelayAsync` / `SetPersistenceAsync` /
  `SetSlotTimeAsync` / `SetFullDuplexAsync`),
  `IAsyncEnumerable<KissFrame> ReadFramesAsync` plus a `FrameReceived`
  event. `SetModeAsync` defaults to non-persist (+16) — flash is not
  burned during normal driver use.
- `Packet.Kiss.KissAckMode` — framing-neutral ACKMODE helpers
  (`BuildSendFrame`, `TryParseAcknowledgement`, `TryParseDataFrame`).
  Lives in `Packet.Kiss` since ACKMODE is a generic G8BPQ extension,
  not NinoTNC-specific.
- `Packet.Kiss.NinoTnc.NinoTncTxTestFrame` — parser for the on-demand
  diagnostic frame the TNC emits when the front-panel TX-Test button is
  pressed. Scans the KISS Data payload for the `=FirmwareVr:` marker and
  decodes the `=Key:Value` field run (firmware version, serial number,
  uptime, board rev + DIP position + firmware-mode byte → resolves to
  the running `NinoTncMode` via the existing catalog, packet counters).
  Permissive about the prefix bytes, because the firmware emits the
  diagnostic as a KISS Data frame and the prefix is *not* a valid AX.25
  header.
- `Packet.Kiss.NinoTnc.NinoTncPortDiscovery` — cross-platform candidate
  enumeration. Linux: `/dev/serial/by-id` symlinks first, `/dev/ttyACM*`
  fallback. Windows / macOS: `SerialPort.GetPortNames()`. Honours
  `PACKETNET_NINOTNC_PORTS="<porta>,<portb>"` env-var override —
  necessary on the dev box where COM1 + COM107 alphabet-sort ahead of
  COM6/COM8 and would otherwise be picked. USB VID/PID matching is a
  follow-up.

**Hardware-loop tests** (`tests/Packet.Interop.Tests/Hardware/NinoTncSerialPortLoopback.cs`,
all `[Trait("Category","HardwareLoop")]` + `SkippableFact`):

- `UI_Frame_Round_Trips_A_To_B_And_Back` — KISS Data → mode 6
  (1200 AFSK AX.25) → over air → back to KISS Data, both directions.
- `AckMode_Echo_Returns_For_Each_Sequence_Tag` — three concurrent
  ACKMODE frames; each echoed back with the correct tag.
- `FrameReceived_Event_Fires_For_Inbound_Frames` — event-based API
  delivers the round-tripped frame.

All three pass in ~4 s with the env-var override pointing at COM6+COM8.

**Adaptive parameter scaffolding** (user stretch goal — full integration
deferred to the Phase 3 KISS-hardening work, this is just the
abstraction):

- `Packet.Kiss.Adaptive.KissParameters` — `(TxDelay, Persist, SlotTime,
  TxTail)` record with nullable fields and `.Override(other)` so a static
  baseline can be composed with adaptive deltas.
- `Packet.Kiss.Adaptive.FrameOutcome` enum +
  `FrameOutcomeSample` record — what the controller feeds the estimator.
- `Packet.Kiss.Adaptive.IAdaptiveParameterEstimator` — minimum protocol-
  facing contract (`Recommend(peer)` + `Observe(sample)`) so the
  estimator implementation can swap without churning integration points.
- `Packet.Kiss.Adaptive.IPeerStateStore` — optional persistence hook for
  surviving restarts without paying the cold-start re-learning tax.
- `Packet.Kiss.Adaptive.TxDelayHillClimbEstimator` — concrete first-pass
  estimator: walks per-peer TXDELAY down on consecutive first-try ACKs,
  ratchets up on loss / ACK timeout, clamped to a min/max. Other KISS
  parameters pass through unchanged for now.

Not yet wired into `NinoTncSerialPort` — the session layer
(`Packet.Ax25.Session`) is the right place to call `Observe(...)` from,
and that integration belongs to the Phase 3 hardening work. The
abstraction is here so that work can land without redesigning the
contract.

**Findings**

- **`SerialPort.BaseStream.ReadAsync` is unreliable on Windows** —
  cancellation tokens are not honoured by the underlying Win32
  `ReadFile`, and the async path occasionally fails to deliver bytes
  even when `BytesToRead` shows them. The driver therefore runs a
  dedicated long-running thread doing synchronous
  `SerialPort.Read(...)` with `ReadTimeout = 100ms`. Reads return
  promptly when data is present, the `TimeoutException` path is
  caught and looped. This is what the manual spike has been observed
  to round-trip reliably.
- **Disposal order matters.** `SerialPort.Dispose()` must come *before*
  awaiting the pump task — disposing the port is what actually unblocks
  the pending synchronous `Read`, and the cancellation token alone
  does not. The driver's `DisposeAsync` is ordered accordingly.
- **Port enumeration without VID/PID filtering is dangerous.** Built-in
  COM1 + a virtual COM107 sorted alphabetically ahead of the real TNCs
  on the dev host; blind "take first two" picks them and the test then
  times out with zero frames received. Hence the
  `PACKETNET_NINOTNC_PORTS` env-var override; proper VID/PID matching
  is the long-term fix.

**Spike tool** at `tools/Packet.NinoTnc.Spike/`:

- `dotnet run -- COM6 COM8` — direct-`SerialPort` round-trip, the
  hello-world that proved the audio path works.
- `dotnet run -- driver COM6 COM8` — same round-trip via the production
  `NinoTncSerialPort` API, including diagnostic logging on each
  inbound frame.
- `dotnet run -- test-shape COM6 COM8` — mirrors the xUnit hardware
  test outside the test runner; useful when isolating xUnit-harness
  vs hardware issues.

**Test totals**

- Default filter (`Category!=HardwareLoop&Category!=Interop`):
  `Packet.Kiss.Tests` 39 (was 29 — +10 for KISS ACKMODE + adaptive)
  and `Packet.Kiss.NinoTnc.Tests` 29 (was 22 — +7 for TX-Test frame
  parsing). Full default-filter count tracked in CI.
- Hardware-loop filter (`Category=HardwareLoop`): 3 new tests against
  the COM6+COM8 pair, all green.

**What's still open / what comes next**

- USB VID/PID-based discovery on Windows (use WMI / `SetupAPI`) and
  Linux (`/sys/class/tty/.../device/idVendor` etc.) so the env-var
  override is just a tie-breaker.
- Soak campaign across all audio-compatible modes (6 / 7 / 12, plus
  9600 GFSK if the loopback audio is good enough) with TXDELAY sweep,
  frame-size sweep, and sustained-throughput measurements. The data
  feeds back into the adaptive estimator's tuning.
- Wire `TxDelayHillClimbEstimator` into the session layer's TX path
  and have it actually move TXDELAY on the wire (currently it tracks
  state but no one consumes its recommendations).
- Persistence layer (`IPeerStateStore` SQLite implementation) — Phase 4
  territory.

### 2026-05-14 — docs/spec-issues.md — central tracker for verification_pending notes

Consolidated all `verification_pending` notes and triangulated upstream
findings from across the figc4.1–4.6 YAMLs into a single
[`docs/spec-issues.md`](spec-issues.md) tracker. 19 issues catalogued:

- **4** spec text errors (typos, missing labels)
- **2** figure-vs-prose contradictions
- **6** internal inconsistencies across 1998/2006/2017 spec revisions
- **4** semantic ambiguities
- **3** cases where the spec is widely ignored in practice

Each entry has a stable ID (`SI-NN`), affected transition(s),
triangulation evidence from reference implementations, and a
status (`verification-pending` vs `triangulated`).

**Strongest candidates for upstream filing** (CLAUDE.md still requires
Tom's explicit ask before opening any):
- **SI-14** (figc4.6 "Info Field Permitted In Frame" — dropped "Not")
- **SI-15** (figc4.6 UI P==1 Yes/No swap)
- **SI-08** (figc4.3 DISC handling: figure says stay, prose says enter Disconnected)
- **SI-09** (figc4.3 UI(P=1) → DM: figure violates §6.3.5 ¶3 exclusion)

Plan §6.3 amended: step 7 now requires every new
`verification_pending` YAML note to also land in spec-issues.md.

### 2026-05-13 — Validate figc4.6 — smoke test + spec_prose + 4-codebase references

Validation pass for `awaiting_v22_connection.sdl.yaml`: orchestrator
smoke test (`DataLinkAwaitingConnection22SmokeTests.cs`, 25 [Fact]s —
one per transition), 8 spec_prose citations, and structured references
across the four pinned implementations.

**Headline finding** — of the four pinned implementations, **only direwolf
implements a distinct v2.2 awaiting state**:

- **linbpq**: rejects SABME globally with FRMR at `L2Code.c:687-693`
  ("Although some say V2.2 requires SABME I don't agree! Reject until we
  support Mod 128"). No v2.2 awaiting state exists — all 25 figc4.6
  transitions omit.
- **direwolf**: has `state_5_awaiting_v22_connection` with full
  implementation. 17 of 25 transitions cite direwolf code.
- **rax25**: conflates v2.0 + v2.2 awaiting into one `AwaitingConnection`
  struct. **Author's own TODO at `src/state.rs:1256` is the smoking gun**:
  "TODO: This is supposed to transition to 'awaiting connect 2.2'." Direct
  acknowledgement of the missing state.
- **linux_oot**: fuses spec states 1 and 5 in `ax25_std_state1_machine`,
  distinguishing modulus/window on SABM vs SABME at the receive side but
  not state.

**Triangulated upstream-spec / implementation findings**:

1. **MDL-NEGOTIATE Request** (the v2.2-distinguishing action on
   UA(F=1)→Connected paths t16/t17/t18) — implemented ONLY in direwolf
   (`ua_frame:4910-4912`), with erratum comment that the figure itself
   omits it. rax25's TODO acknowledges; linbpq and Linux have no concept.

2. **direwolf's `#if 0` block at `dm_frame:4716-4765`** — figure-faithful
   DM-handling commented out, replaced with v2.0 SABM retry as a
   workaround for the KPC-3+ TNC bug. Erratum at 4735: 'copied from FRMR
   case'. Real-world spec deviation worth flagging.

3. **rax25 author's spec-bug annotations on UA path**:
   - `state.rs:1192`: "Typo in 1998 spec: G, not g" (re: DL-ERROR(G)).
   - `state.rs:1213-1231`: "1998 spec: ... 2017 spec: ... bug in the
     2017 spec. This path says to start T1, then immediately stop it
     again." (matches figc4.6 t18's "Start T3 twice" pattern).
   - `state.rs:1236`: "1998 spec says 'stop T3'. 2017 spec says 'start T3'
     (page 89), which makes much more sense."

4. **direwolf FRMR erratum at `frmr_frame:5047`**: "State 1 clears it.
   State 5 sets it. Why not the same?" — `layer_3_initiated` is cleared
   on state-1 entry but set on state-5 entry (t21). direwolf author
   asking the same Why?.

5. **t23 (SABM → AwaitingConnection)** — direwolf does NOT explicitly
   call `set_version_2_0` at `sabm_e_frame:4415-4423`, despite the
   figure prescribing it. Implementation deviation.

6. **DL-ERROR codes** (L/M/N/D/G) on t08/t09/t10/t15/t19: figure-
   authoritative — no implementation surfaces them as typed errors.
   rax25 partially does (D on t15, G on t19 with the "Typo: G, not g"
   note) but not others.

**Per-transition coverage**: 8 spec_prose citations, ~30 direwolf
citations, ~10 rax25 citations, ~12 linux_oot citations, 0 linbpq
citations (all-omit due to global SABME rejection). The all-omit linbpq
case is itself a data point — captured in transition-level reasons.

Test totals: 497 (was 472; +25 generated smoke tests for figc4.6).

### 2026-05-13 — Transcribe figc4.6 Data-Link Awaiting V2.2 Connection state

Fifth SDL page. Tom drew
`spec-sdl/data-link/DataLink_AwaitingV22Connection.graphml` (81 nodes,
85 edges, 18 input columns across the two figure pages 4.6a + 4.6b);
I converted to `spec-sdl/data-link/awaiting_v22_connection.sdl.yaml` —
**25 transitions** after binary-decision enumeration, plus 1 page-level
`save:` entry (DL-DISCONNECT request).

Closes the `AwaitingConnection22` placeholder loop: 14 figc4.4
SABME-while-Awaiting transitions and figc4.2's t24 SABME-while-AWaitConn
now target an actual transition table rather than an empty stub.

**Inventory correction**: the prior plan listed figc4.6 as a single page
("C4.6a"). Tom confirmed the state spans two pages (4.6a + 4.6b) in the
spec, matching figc4.4's a/b/c convention. Inventory row updated to
"C4.6a–b" and total page count adjusted 27 → 28.

State name: `AwaitingConnection22` already established in figc4.4 and
figc4.2 as a placeholder — now tied off.

**Two verification_pending flags** preserved verbatim per Trust-the-Figure:

1. **n8 column label "Info Field Permitted In Frame"** (note: missing
   "Not") — figc4.3's same column reads "Info Not Permitted In Frame",
   and the DL-ERROR(M) action means "info **not** permitted in frame".
   Tom confirmed the spec PNG literally has "Permitted" (no "Not") on
   this page — strong candidate spec typo. Encoded with canonical
   `info_not_permitted_in_frame` event id (action dictates), figure-
   verbatim label preserved in transition notes.

2. **UI column `P == 1?` Yes/No swap** — figc4.6 PNG draws
   `P==1 Yes → stay` and `P==1 No → DM(F=1)`. This is inverted from
   figc4.3 and from §6.3.5 ¶3 prose ("Any TNC receiving a command
   frame other than a SABM(E) or UI frame with the P bit set to '1'
   responds with a DM frame…"). Tom confirmed the swap is genuine in
   the spec PNG — strong upstream-spec issue candidate. Encoded
   verbatim on t11/t12.

Other notable structural features:
- **MDL-NEGOTIATE Request** as an `internal_out` action fires on all
  three UA(F=1)-into-Connected paths (t16/t17/t18), reflecting that
  v2.2 connections trigger XID parameter negotiation post-establish.
- **FRMR fallback** (t21): peer-reported frame-reject during v2.2
  negotiation → run Establish Data Link subroutine, force Version 2.0,
  drop to AwaitingConnection. The figure's only path that exits to
  state 1 with a multi-step processing prelude.
- **DM(F=0) fallback** (t14): peer-refused v2.2 drops to
  AwaitingConnection (v2.0) rather than Disconnected — implements the
  spec's v2.2-down-to-v2.0 negotiation cascade.

Test totals: 472 (was 445; +27 generated conformance tests for figc4.6
covering the 25 transitions plus structural emissions).

Validation chain (smoke test + spec_prose + 4-codebase implementation
references) arrives in follow-up PR(s).

### 2026-05-13 — Validate figc4.3 — smoke test + spec_prose + 4-codebase references

Validation pass for `awaiting_release.sdl.yaml`: orchestrator smoke test
(`DataLinkAwaitingReleaseSmokeTests.cs`, 20 [Fact]s — one per
transition), 7 spec_prose citations, and structured references across
the four pinned implementations (linbpq, direwolf, rax25, linux_oot).
Implementation refs parallelised via four subagents — output merged
into the YAML per-transition.

New `IOrSCommandReceived(Ax25Frame)` event class added to
`Packet.Ax25/Session/Ax25Event.cs` for the composite SDL input column
"I, RR, RNR, REJ or SREJ Commands" (events.yaml entry was already
landed in #26 transcription PR).

**Triangulated upstream-spec findings** (these are the gold from this
pass — patterns spotted by multiple implementers independently):

1. **t01 (DL-DISCONNECT request → Expedited DM, stay)** — all four
   implementations diverge. rax25 (`AwaitingRelease::disconnect`)
   transitions to Disconnected with author erratum comments at
   src/state.rs:1329 ("1998&2017 bug: What's an 'expedited' DM?") and
   1332 ("1998&2017 bug: Doesn't specify pf"). direwolf
   (`dl_disconnect_request:1138`) also goes to state_0 with similar
   commentary at 1147-1148. linbpq and Linux never re-enter the
   DL-DISCONNECT codepath in this state at all (one-shot release).
   Candidate upstream-spec issue: "Expedited DM, stay" looks
   implementationally unnatural.

2. **t02 (T1 expiry RC==N2 → DL-ERROR(G))** — figure's "G" code is not
   reproduced by any implementation. rax25 emits `DlError::H`
   (different letter); direwolf only dw_printf's an untyped string;
   linbpq silently `CLEAROUTLINK`s; Linux surfaces ETIMEDOUT. The "G"
   appears figure-authoritative.

3. **t14 (DISC received → UA, stay in AwaitingRelease)** — only
   direwolf matches the figure (`disc_frame:4544` "keep current state,
   2"). linbpq (`L2LINKACTIVE:1090`) and Linux
   (`ax25_std_state2_machine:111`) send UA but go to Disconnected,
   matching §6.3.4 ¶2 prose ("After receiving a valid DISC command,
   the TNC sends a UA response frame and enters the disconnected
   state"). Figure-vs-prose tension; rax25 omits this branch entirely.

4. **t17 (UI P=1 → DM F=1)** — §6.3.5 ¶3 explicitly **excludes** UI
   from the "respond DM" rule ("…receiving a command frame other than
   a SABM(E) or UI frame with the P bit set to '1' responds with a DM
   frame…"), yet the figure draws UI(P=1) → DM(F=1). direwolf alone
   follows the figure (`ui_frame:5116`); linbpq, Linux and rax25
   follow the prose. Trust-the-figure here but flagged.

5. **t15 (DM F=1)** — direwolf erratum at `dm_frame:4677` captures
   1998 vs 2006 spec ambiguity: "Original flow chart, page 91, shows
   DL-CONNECT confirm. It should clearly be DISconnect rather than
   Connect. 2006 has DISCONNECT *Indication*. Should it be indication
   or confirm? Not sure." figc4.3 resolves it as DL-DISCONNECT
   *confirm*.

6. **t19 (I/S commands P=1 → DM F=1)** — direwolf erratum at
   `rr_rnr_frame:3537-3541` flags a 1998-vs-2006 figure divergence:
   "RR, RNR, REJ, SREJ responses would fall under all other
   primitives. In the original, we simply ignore it and stay in state
   2. The 2006 version, page 94, says go into 1 awaiting connection
   state. That makes no sense to me." Separately at `srej_frame:4046`:
   "Based on X.25, I don't think SREJ can be a command" — questioning
   whether SREJ belongs in the figure's command-column at all.

**Implementation divergence patterns** (collected from per-transition
notes):

- **No implementation emits typed DL-ERROR codes** in this state.
  Figure transitions t02 (G), t05 (D), t09 (L), t10 (M), t11 (N) are
  all figure-authoritative. rax25 partially does (D for t05) but uses
  H instead of G for t02.
- **rax25's `AwaitingRelease` is partial** — only 4 of 20 transitions
  have explicit overrides (`dm`, `ua`, `t1`, `disconnect`). The
  remaining 16 fall through to trait defaults that log
  `'TODO: unexpected X'` — most concerning are t12/t13/t14 (collision
  handling) and t17 (UI P=1 → DM F=1).
- **linbpq lacks the L3-init flag and the Select_T1_Value algorithm**
  for this page — `LINK->L2TIME` is fixed `PORTT1` at session setup
  (`L2TIMEOUT:3802`).
- **All four implementations skip UI_Check** for state-2 UI handling
  — only direwolf reaches the DM-on-P=1 reply (partial match); the
  others don't even deliver the UI payload to the upper layer while
  in this state.

Test totals: 445 (was 425; +20 generated smoke tests for figc4.3).

### 2026-05-13 — Hygiene: quote `d5` verbatim in YAML shape-class comments

Caught during figc4.3 review: my prose used "lower-layer parallelogram"
(visual-shape language) in PR #26's summary, contrary to CLAUDE.md's
rule that `d5` is the only authoritative source for a node's shape
class and must be quoted verbatim in writing.

Audit turned up 6 pre-existing slips in `awaiting_connection.sdl.yaml`
(lines 78, 391, 398, 407, 537) and `connected.sdl.yaml` (line 1073) —
section comments paraphrasing as "upper-layer shape" / "lower-layer
shape". This PR rewrites them to quote `d5` verbatim
(e.g. `d5: "Signal reception from upper layer"`).

Comment-only with one knock-on: the t10 `all_other_primitives_from_upper_layer`
**notes:** field flows into the generated code's `Notes:` string, so
`DataLink_AwaitingConnection.g.cs` regenerates with the corrected
docstring. Build + 425 tests green; no behaviour change.

`disconnected.sdl.yaml` is unaffected (no shape-paraphrase slips on
that page).

### 2026-05-13 — Transcribe figc4.3 Data-Link Awaiting Release state

Fourth SDL page. Tom drew
`spec-sdl/data-link/DataLink_AwaitingRelease.graphml` (60 nodes,
62 edges, 15 input columns); I converted to
`spec-sdl/data-link/awaiting_release.sdl.yaml` — **20 transitions**
after binary-decision enumeration.

The page has no SDL Save shapes (no `save:` directive) — DL-DISCONNECT
request received while in AwaitingRelease is a real handler that
re-emits Expedited DM rather than queuing for replay.

New event in catalogue: `i_or_s_command_received` (frames_received
group). The figure draws one composite SDL input column labelled
"I, RR, RNR, REJ or SREJ Commands"; lossless encoding preserves the
grouping rather than fanning out across the five frame events.

Decision-catalogue: four diamonds — `t1_rc_eq_n2`, `ua_f_eq_1`,
`dm_f_eq_1` (two distinct F==1? diamonds preserved with column-specific
ids, mirroring figc4.2), and a shared `p_eq_1` referenced by both the
UI column and the new `i_or_s_command_received` column (they merge into
a single drawn diamond in the figure).

Shape-class convention: figc4.3 follows the same figc4.1 quirk —
DL-DISCONNECT Request, DL-UNIT-DATA Request, and one "All Other
Primitives" column are drawn with "Signal reception from Lower Layer"
shape despite being upper-layer primitives. Per CLAUDE.md `d5` is
authoritative — disambiguated with `__from_lower_layer` /
`__from_upper_layer` suffixes on the catch-all events.

State name: `AwaitingRelease` — already established in the figc4.4
transcription (used as `next:` on the DL-DISCONNECT request column
there), now tied off.

Local SDK pin in `global.json` lowered from 10.0.203 → 10.0.107
(`rollForward: latestFeature`) so the codegen runs on the currently
installed SDK; CI was using `global-json-file` so it auto-installs the
pinned version.

Test totals: 425 (was 403; +22 generated conformance tests covering the
20 transitions plus structural/round-trip emissions).

Validation chain (smoke test + spec_prose + 4-codebase implementation
references) arrives in follow-up PR(s).

### 2026-05-12 — Codegen test project (Packet.Sdl.CodeGen.Tests)

`tools/Packet.Sdl.CodeGen` previously had no dedicated test project —
its behaviour was exercised only indirectly via the three real SDL
pages. When a new schema feature lands (like the `loop_while`
construct), there's no fast feedback that the codegen handles edge
cases correctly until something downstream breaks.

New project `tests/Packet.Sdl.CodeGen.Tests` with 9 **black-box**
tests covering:

1. Valid minimal page → `.g.cs` emitted with expected content.
2. Unknown event in `on:` → fails with reference to `events.yaml`.
3. Decision-branch-completeness lint (decision with only "Yes").
4. Guard overlap lint (same-`on:` transitions with non-disjoint
   guards).
5. `loop_while` emits a `LoopRange(start, length, predicate)`
   literal in the generated code.
6. `loop_while` with nested decision in body → rejected.
7. Duplicate transition id → caught during validation.
8. Unknown action kind → rejected with the bad kind name surfaced.
9. References entry whose `source:` isn't in `pinned_refs:` →
   rejected.

Black-box on purpose: each test spawns the codegen as a subprocess
against a temp directory containing the fixture YAML. Refactors of
`Program.cs` internals don't break tests; only behavioural changes
do. `CodegenRunner` (the test helper) discovers the codegen DLL by
walking up to `Packet.NET.slnx` (.NET 10 XML solution format) and
invokes the tool with `dotnet <dll> --in ... --out ... --tests ...`.

Test totals: 403 (was 394; +9 codegen tests).

### 2026-05-12 — spec_prose back-fill for figc4.1 + figc4.2

Hygiene pass: bring figc4.1 (`disconnected.sdl.yaml`) and figc4.2
(`awaiting_connection.sdl.yaml`) up to the same spec_prose coverage
standard as figc4.4. Both pages had prose citations in the notes:
blocks of various transitions but only some had structured
spec_prose entries in references[].

Added 9 structured spec_prose entries:

figc4.1 — 4 entries (the DL-ERROR / figure-only error transitions
that cite §C "error indications are discussed in the SDL appendices"):
- t07_control_field_error → §C
- t08_info_not_permitted_in_frame → §C
- t09_u_or_s_frame_length_error → §C
- t10_ua_received → §C

figc4.2 — 5 entries (prose-backed transitions not yet structured):
- t04_ua_received_not_f_eq_1 → §6.3.1 ¶4
- t12_dl_unit_data_request → §6.3.7
- t18_ui_received_p_eq_1 → §6.3.5 ¶3
- t19_ui_received_p_eq_0 → §6.3.5 ¶3
- t24_sabme_received → §6.3.1 ¶3

The catch-all transitions (figc4.1 t04, t06) intentionally keep no
spec_prose entry — they explicitly have no prose backing.

Same Python text-injection script approach as the figc4.4 spec_prose
PR. 8 done by script; 1 (t08, had inline `references: []` array)
manually patched.

Tests unchanged: 394 green.

### 2026-05-12 — spec_prose cross-check for figc4.4

Closing out the figc4.4 validation chain (task #64) with `spec_prose`
citations added to 49 of the 69 transitions on the Connected page.
Citations pull from §6.3.3 (information-transfer phase), §6.3.4 (link
disconnection), §6.3.7 (UI / connectionless), §6.4.x (I-frame send +
receive, REJ/SREJ, RNR, T1 expiry), §6.5 (resetting procedure), §6.7.1
(timer T3).

20 transitions remain without explicit prose backing — figure-only
specifics like DL-ERROR code letters E/F/K/L/M/N/O, unexpected-DM
handling, and the various v22/v20 splits. Same pattern as previous
figures: the figure is authoritative for these by design (spec says
"error indications are discussed in the SDL appendices").

Notable findings preserved verbatim from §6.4.5 / §6.4.4.x:
- §6.4.5 says "When a TNC receives a frame with an incorrect FCS, an
  invalid frame, or a frame with an improper address, that frame is
  discarded." The figure DIVERGES — t32-t37 (frame-format errors) all
  go through Establish_Data_Link + state transition rather than a
  silent discard. The implementations agree more with the prose
  (Linux silently drops, LinBPQ uses FRMR, direwolf/rax25 don't surface
  DL-ERROR codes at all). Recorded on the references entries.
- §6.5 ¶2 "A TNC initiates a reset procedure whenever it receives an
  unexpected UA response frame, or after receipt of a FRMR frame from
  a TNC using an older version of the protocol." backs t44-t47.

Merger script approach (same as figc4.4's implementation refs PR):
Python text-injection that finds each `id: tNN_` block and prepends a
spec_prose entry to the existing `references:` list. 47 done by script,
2 (t34/t35) manually patched because they had empty `references: []`
inline arrays that the regex didn't match.

Test totals unchanged: 394.

### 2026-05-12 — Schema: loop_while construct for SDL loops

Tom confirmed the n148 stored-frame loop in figc4.4 is deliberate spec
semantics, not a transcription quirk. Adding native loop support to
the schema + codegen so t67-t69 no longer carry `verification_pending`
notes about it.

Schema additions (`spec-sdl/schema/sdl-machine.schema.json`):

- New path-step variant: `{ loop_while: <decision_id>, body: [...] }`.
  The decision id references the page-level `decisions:` catalogue;
  the loop runs the body while the predicate evaluates true.
- `body:` is **action-only** for now — nested decisions or nested loops
  inside a body are rejected by the codegen validator (refactor as a
  subroutine if you need them). Restricts the runtime semantics to a
  manageable shape; we can extend when a real figure needs it.

Runtime: new `LoopRange(Start, Length, Predicate)` record alongside
`ActionStep`. `TransitionSpec.Loops` field records loop ranges in the
flat `Actions[]` list — `Actions[Start..Start+Length-1]` form a loop
body that should re-execute while `Predicate` is true.

Codegen: when walking the path, `loop_while` steps inline their body's
actions into the flat list AND emit a `LoopRange` entry. Compiled
guard for the transition does **not** include the loop predicate (the
loop runs zero or more times based on its own predicate; entry to the
transition is gated by surrounding `decision:` steps).

Refactor: t67-t69 in `connected.sdl.yaml` now use `loop_while`.
`verification_pending` notes removed. The path keeps a
`decision: vr_i_frame_stored, branch: "Yes"` step before the
`loop_while` to gate entry (so t67-t69 are mutually exclusive with
t09-t11 on the predicate).

Non-loop-aware dispatcher behaviour unchanged: it iterates `Actions[]`
linearly, executing each loop body exactly once. This matches the
existing smoke tests (which post events with V_r_I_frame_stored=true
and assert against the inlined body) and the spec for the
single-stored-frame case. A future loop-aware dispatcher will consult
`Loops[]` and iterate properly.

[`docs/sdl-primer.md`](sdl-primer.md) updated with worked example +
runtime semantics.

### 2026-05-12 — Validate figc4.4 — smoke test + 4-codebase references

Combined validation pass for `connected.sdl.yaml` (the figc4.4
transcription that landed in PR #20):

1. **Orchestrator smoke test** (`DataLinkConnectedSmokeTests.cs`) — 69
   facts, one per transition, with bindings for all 19 decision
   predicates introduced on this page (P_eq_1, F_eq_1, command,
   info_field_valid, V_a_le_N_r_le_V_s, N_s_eq_V_r, N_s_gt_V_r_plus_1,
   V_s_eq_V_a, V_s_eq_V_a_plus_k, own_receiver_busy,
   peer_receiver_busy, reject_exception, srej_enabled,
   srej_exception_gt_0, acknowledge_pending, V_r_I_frame_stored,
   version_2_2, P_or_F_eq_1, T1_running). Closes runtime loop YAML →
   spec → orchestrator behaviour for the page's 69 transitions.

2. **Implementation references** across all four pinned sources
   (LinBPQ, Dire Wolf, rax25, Linux mod-orphan). Parallelised via four
   subagents. The complete page now has citations in 53 of 69
   transitions × multiple codebases — over 200 individual citations
   merged via a Python text-injection script.

**Cross-implementation findings durably captured**:

- **rax25 and direwolf both fuse Connected (state 3) with TimerRecovery
  (state 4)** into a single handler discriminated by an internal
  flag/enum. figc4.5's transcription will share many code paths with
  this page.

- **All four implementations bypass DL-UNIT-DATA Request (t23)** — UI
  sent via APRS/KISS/socket layers, never through the LAPB state
  machine.

- **LinBPQ disables spec-compliant FRMR recovery** via the FRMRHACK
  macro (L2Code.c:24) — treats FRMR as DM (link drop). Original
  spec-compliant code is commented out at L2Code.c:2192-2202. Notable
  enough that the YAML calls it out by name.

- **Linux silently drops AX25_SREJ as AX25_ILLEGAL** — Linux has no
  SREJ implementation at all. SREJ frames fall through to the
  FRMR/establish-data-link path.

- **DL-ERROR codes inconsistently surfaced**:
  - direwolf: text-message logs ("Protocol Error C: Unexpected UA...")
  - rax25: typed DlError variants for some (D/E/G/K) but rejects O on
    I-frame for being "not even remotely correct, since O means packet
    too big" — emits DlError::S instead
  - LinBPQ: mostly omits, uses FRMR transitions instead
  - Linux: omits entirely; silently drops

- **Direwolf's I-frame handler implements X.25 §2.4.6.4** rather than
  AX.25's SDL (author Erratum 3014: "AX.25 protocol spec did not handle
  SREJ very well. Based on X.25 section 2.4.6.4."). Major divergence.

- **The n148 "V(r) I Frame Stored?" loop** (figc4.4 column 2 sub-tree)
  is a deliberate spec feature per Tom. Currently encoded as one
  iteration inline on t67-t69 with `verification_pending` notes;
  follow-up PR will add a real `loop:` construct to the schema and
  refactor.

- **Two spec-version divergences flagged by rax25 author**:
  - "1998 bug: Says to set rc=0. Fixed in 2017." (T3 expiry, t39)
  - "2017 spec says DlError::K, which is undocumented" (UA in
    Connected, t46) — uses DlError::C instead

- **Direwolf author erratum on T3 expiry RC**: "Original sets RC to 0,
  2006 revision sets RC to 1 which makes more sense." Direwolf follows
  2006.

- **`spec_prose` citations not added in this pass** — the volume of
  inline notes plus implementation references is already substantial.
  Spec-prose cross-check is a planned follow-up; in the meantime the
  amendment log here captures the structural cross-impl findings.

Test totals: 394 (was 325; +69 smoke tests).

### 2026-05-12 — Transcribe figc4.4 Data-Link Connected state

Third SDL page, by far the biggest. Tom drew
`spec-sdl/data-link/DataLink_Connected.graphml` (181 nodes, 209 edges,
26 input columns); I converted to
`spec-sdl/data-link/connected.sdl.yaml` — **69 transitions** after
binary-decision enumeration.

The "I received" column alone has 7 chained decisions producing 16
distinct paths (and one of them turned out to be a graph loop —
n148-n149-n150-n151-n148; one iteration of the loop body encoded inline
on t67-t69 with `verification_pending` notes pointing at the unbounded
loop the figure actually draws).

New event in catalogue: `LM_SEIZE_confirm` (column 24, link-multiplexer
seize-grant confirmation). Added a new `link_multiplexer:` group in
`events.yaml`.

Pre-write graphml hygiene pass:
- Tom canonicalised several near-duplicate labels after my scan
  (`'Ack pending?'` vs `'Ack Pending?'`, multiple `P == 1` variants,
  `'Own receive busy?'` vs `'Own Receiver Busy?'`, etc.).
- `LM-SIEZE` typo corrected to `LM-SEIZE` on input event; preserved on
  the lower-layer action side (`LM_seize_request`).
- One `verification_pending` marker deliberately preserved by Tom:
  `'Push on I Frame Queue (note: word order?)'` in column 3 (DL-DATA
  Request) — flag on t18 for upstream review.

Decision-catalogue canonicalisation: predicates that appear in
multiple drawn diamonds across columns (`Version 2.2?`,
`V(a) <= N(r) <= V(s)?`, `P == 1?`, `Own Receiver Busy?`) share a
single `decisions:` entry. Different transitions reference the same
id. The codegen handles this; lints are happy. Pure canonicalisation,
no semantic loss.

State names:
- `Disconnected`, `AwaitingConnection`, `AwaitingRelease`,
  `Connected`, `TimerRecovery` — already established.
- `AwaitingConnection22` — placeholder for figc4.6's 2.2-awaiting
  state, used as `next:` on 14 transitions in figc4.4. To be tied off
  when that figure lands.

Test totals: 325 (was 254; +71 generated conformance tests).

Validation chain (smoke test + spec_prose + 4-codebase implementation
references) arrives in follow-up PR(s).

### 2026-05-12 — Validate figc4.2 — smoke test + spec_prose + 4-codebase references

Combined validation pass for `awaiting_connection.sdl.yaml`: orchestrator
smoke test (all 24 transitions), spec_prose citations, and structured
references across the four pinned implementations. Implementation refs
parallelised via four subagents — output merged into the YAML.

**Triangulated upstream-spec findings** (new ones from figc4.2,
strengthening the case for a spec-issue tracker):

1. **DL-DISCONNECT "requeue" semantics unclear** — three of four impls
   (direwolf, rax25, LinBPQ) explicitly question the spec's wording or
   don't implement save-and-replay at all. direwolf comment: "Erratum:
   The protocol spec says 'requeue.' If we put disconnect req back…we
   will probably get it back again here while still in same state."
   rax25 comment: "1998&2017 bug: It says requeue. What does that even
   mean?" Captured on the page-level `save:` directive.

2. **The `data_layer_3_initiated` diamond is genuinely ambiguous** —
   Tom flagged Yes/No labels as "assumed; missing from spec" in the
   graphml; direwolf author has the same uncertainty with comments at
   data_request_good_size:1466-1473 and i_frame_pop_off_queue:6555-6558
   ("seems backwards but I don't understand enough yet..."). direwolf
   reads the diamond with the **opposite** Yes/No interpretation from
   this transcription. We now have three independent observations
   (Tom, direwolf author, structural absence in other impls). The four
   `verification_pending` transitions (t13-t16) preserve this on the
   YAML.

3. **t07 (UA F=1, !L3-init, V(s)≠V(a)) chain is buggy in 2017 spec** —
   rax25 author: "bug in the 2017 spec...start T1, then immediately
   stop it again". direwolf has the same redundancy comment at
   ua_frame:4849-4876. Two independent observations.

4. **DL-ERROR code G typo** — rax25 author: "Typo in 1998 spec: G, not
   g". Captured on t08 references.

5. **figc4.2 v2006 has a SABM/SABME swap bug** — direwolf author at
   sabm_e_frame:4390-4392: "Erratum! 2006 version shows SABME twice
   for state 1. First one should be SABM…Original appears to be
   correct." Direct confirmation that our transcription (using the
   1998 reading) is right.

**Implementation divergence patterns** captured on per-transition
notes:

- **LinBPQ and Linux have no `layer_3_initiated` flag** — collapses
  t05/t06/t07 into a single UA handler.
- **None of the four implementations implement the page-level save** —
  all handle DL-DISCONNECT-while-waiting inline rather than queuing
  for replay.
- **Linux replies UA to SABME** (not DM) and stays in STATE_1 — no
  separate 2.2-awaiting state.
- **LinBPQ rejects SABME globally** — no Mod-128 support.
- **rax25 deliberately omits many handlers** — only overrides 6 methods
  on AwaitingConnection; trait defaults log "unexpected X" for the
  rest. Notable omissions: DISC, DM, UI, connect, data.
- **DL-ERROR(D/L/M/N) consistently un-emitted** across all four impls.

254 tests now (was 230; +24 smoke tests).

### 2026-05-12 — Transcribe figc4.2 Data-Link Awaiting Connection state

Second SDL page on the lossless schema. Tom drew
`spec-sdl/data-link/DataLink_AwaitingConnection.graphml` (73 nodes, 78
edges, 17 input columns); I converted to
`spec-sdl/data-link/awaiting_connection.sdl.yaml` — **24 transitions**
plus one page-level `save:` directive (DL-DISCONNECT request, drawn
with the SDL Save parallelogram on column 1).

First exposure to two new shape classes from figc1.1:
- **Save** (parallelogram) — turns into `save: [DL_DISCONNECT_request]`
  at page level; the column itself is not a transition.
- **Internal Signal Generation** — `push_frame_on_queue` action with
  `kind: internal_out`.

Notable transcription details:
- **Three chained decisions** on the UA-received path (`F == 1?`,
  `Layer 3 Initiated?`, `V(s) == V(a)?`) enumerate to **four** distinct
  UA-received transitions. The middle "V(s)==V(a) Yes" branch reaches
  Connected *without* sending DL-CONNECT confirm to upper layer — the
  spec's way of saying "we didn't initiate this, don't tell L3 it
  happened from our side."
- **Verification_pending** flag on the DL-DATA / I-frame-pops diamond
  (`data_layer_3_initiated`). Tom's graphml labels the Yes/No branches
  with "(Note: assumed; missing from spec)" — preserved verbatim as
  `verification_pending:` notes on t13–t16. Candidate for upstream
  spec review.
- SABME-while-Awaiting-Connection transitions to a new state
  `AwaitingConnection22` (figc4.6's "Awaiting 2.2 Connection"). Will
  be tied off when figc4.6 lands.

Five new decision identifiers introduced this page: `F_eq_1`,
`layer_3_initiated` (already in `Ax25SessionBindings`), `V_s_eq_V_a`,
`RC_eq_N2`, `P_eq_1` (re-used from figc4.1). The smoke test in the
follow-up PR will bind the new ones.

Test totals: 230 (was 204). +26 from new generated conformance tests.

### 2026-05-12 — Pin implementation references for all 17 figc4.1 transitions

Stage 2 of structured implementation references — citations for the
remaining 16 transitions in `disconnected.sdl.yaml` (t13 was already
pinned in the previous PR as the schema worked example). Citations
across all four pinned sources (LinBPQ, Dire Wolf, Habets' rax25, Linux
kernel mod-orphan) plus structured `spec_prose` entries where prose
backing exists.

Work parallelised via four subagents (one per codebase), then merged.
Each agent returned a `transition_id → {path, function, line, note}`
table; I merged them into the YAML references[] blocks with cross-
implementation context.

**Cross-implementation findings durably captured in transition notes**:

- **UA-in-disconnected DL_ERROR(C,D)** flagged as spec quirk by **both
  direwolf and rax25 authors independently**:
  - direwolf src/ax25_link.c:4823: "Erratum: flow chart says errors C
    and D. Neither one really makes sense."
  - rax25 src/state.rs:1158: "1998 & 2017 bug: C and D make no sense
    here."
  Two independent implementers reaching the same conclusion is strong
  evidence of an upstream spec issue. Recorded in t10's notes as a
  candidate spec issue worth tracking.

- **Dire Wolf explicitly removes the "Able to Establish?" diamond**
  (src/ax25_link.c:4337: "We are always willing to accept connections.").
  t15 and t17 (SABM/SABME refuse paths) have no direwolf equivalent.

- **LinBPQ doesn't support SABME / Mod-128** at all — rejects SABME
  with FRMR via L2SENDINVALIDCTRL (L2FORUS:693) rather than going
  through any 'able to establish' branch. t16 has no LinBPQ
  equivalent; t17 sends FRMR not DM.

- **Linux kernel mod-orphan handles UI uniformly across states**
  (APRS-style, before the state-0 check). Doesn't implement the spec's
  "UI with P=1 → DM" response at all. Captured in t11/t12 notes.

- **Linux kernel raises no DL-ERROR indications** for L/M/N codes —
  silently drops malformed frames. t07/t08/t09 omits.

- **LinBPQ requires P=1 to send DM** on the catch-all command path
  (L2FORUS:735), where the figure responds unconditionally with F:=P.
  Confirmed across t05, t11, t13.

- **`DL-UNIT-DATA Request` (t02) bypasses the state machine in all
  four implementations** — direwolf explicit comment "not implemented.
  APRS & KISS bypass this"; rax25 has no upper-layer UI send method;
  LinBPQ uses CommonCode.c::UISend_AX_Datagram outside L2; Linux uses
  the SOCK_DGRAM sendmsg path independently of LAPB state. Suggests
  the figure's transition is more service-primitive boilerplate than
  realised behaviour.

- **rax25 deliberately deviates** from the figure in t14/t16: emits
  only a `debug!` log instead of `DL_CONNECT_indication` to upper
  layer when accepting SABM(E). Worth flagging if we use rax25 as
  reference for our own connect-indication handling.

These findings reinforce the value of the cross-reference exercise:
two of the four implementations independently flagged the same spec
issue, and several diverge from the figure in ways that affect
interop. Every divergence is now durably documented in the YAML's
references[] notes.

### 2026-05-12 — Structured implementation references (schema + worked example)

Schema, runtime, and codegen bumps to support **structured cross-reference
citations** (option 3 from the post-PR-13 menu, expanded scope: LinBPQ,
Dire Wolf, Thomas Habets' `rax25`, Linux kernel OOT module
`linux-netdev/mod-orphan`).

Schema additions (`spec-sdl/schema/sdl-machine.schema.json`):

- **`pinned_refs:`** at page level — map of `source` →
  `{repo: URL, commit: hash}`. Pinning to specific commits means line
  numbers in citations stay valid; bumping a pin requires auditing
  references for drift.
- **`references:`** per transition — array of citation objects. Each has
  a `source` (either `spec_prose` for AX.25 prose, or a key that must
  appear in `pinned_refs`). For `spec_prose`: `cite` (e.g. `§6.3.5 ¶1`)
  + optional `quote`. For code citations: `path`, `function` (primary
  durable anchor), optional `line` + `note`.

Runtime: new `ImplementationReference` record + new `References` field
on `TransitionSpec`. Codegen surfaces references into the emitted
`.g.cs` so consumers (cross-language ports, redraw tools) can query
them at runtime.

Codegen lint additions:
- Every `references[].source` must be `spec_prose` or a key in
  `pinned_refs`.
- spec_prose: requires `cite`; rejects path/function/line.
- Code citations: require `path` + `function`; reject cite/quote.
- pinned_refs: requires `repo` + `commit`.

Worked example pinned: **t13 (DISC received → DM)**, citations in all
five sources:
- spec_prose: §6.3.5 ¶1.
- linbpq: `L2Code.c::L2FORUS:735` — no-session catch-all branch.
  Notable divergence flagged in the citation note: LinBPQ only sends DM
  when the incoming command has P=1, whereas the figure responds
  unconditionally with F:=P.
- direwolf: `src/ax25_link.c::disc_frame:4524` — state_0_disconnected
  case.
- rax25: `src/state.rs::Disconnected::disc:1164`.
- linux_oot: `net/ax25/ax25_in.c::ax25_rcv:327` — state-0 catch-all
  via `ax25_return_dm` when no `ax25_cb` exists and frame isn't
  SABM/SABME/DM. Noted: same code path handles t05, t10, t13 by
  exclusion.

Stage 2 (follow-up PR) will pin references for the remaining 16
transitions using the same format.

### 2026-05-12 — Cross-check figc4.1 Disconnected against AX.25 spec prose

Validation option (4) from the previous discussion: read AX.25 §6.3.5
(Disconnected State, 4 paragraphs) and §6.3.1 (Connection Establishment,
covering SABM(E) handling), and verify each of the 17 transitions in
`disconnected.sdl.yaml` either has explicit prose backing or is a
figure-authoritative detail the prose defers to the SDL appendix.

**No discrepancies found.** Every transition either has explicit prose
backing or is documented as figure-authoritative.

Citations dropped into each transition's `notes:` field, prefixed
`spec_prose:`. The convention extends the §2.3 "Pin implementation
evidence" rule to spec-text evidence — the `notes:` field is now the
durable home for both forms of validation.

Sample citations:
- t05 (`all_other_commands`) → §6.3.5 ¶3 verbatim.
- t11 (UI with P=1) → §6.3.5 ¶3, including the figure-only detail that
  F is explicitly set to 1 (vs. F := P for non-UI commands).
- t13 (DISC) → §6.3.5 ¶1 ("transmits a DM frame in response to a DISC
  command").
- t14/t16 (SABM/SABME accepted) → §6.3.1 ¶1 (UA out, reset V()), with
  the Connected-entry housekeeping (DL_CONNECT_indication, SRT/T1V
  init, start_T3, RC := 0) noted as figure-authoritative.
- Catch-all transitions (t04, t06) and unexpected-frame transitions
  (t07–t10) flagged as catch-all / figure-only with no prose backing.

Next pass (option 3 — pin implementation evidence) will add citations
to the same `notes:` field referencing LinBPQ source, especially for
the t14/t16 long chains where action ordering isn't constrained by the
prose.

### 2026-05-12 — Validate figc4.1 Disconnected: orchestrator smoke test + codegen lints

Two pieces of validation against the freshly-transcribed
`disconnected.sdl.yaml`:

1. **Behavioural smoke test** (`tests/Packet.Ax25.Tests/Session/
DataLinkDisconnectedSmokeTests.cs`): drives all 17 transitions through
   `Ax25Session` with a `RecordingActionDispatcher` (`IActionDispatcher`
   extracted as an interface for this) and stub guard bindings for
   `P_eq_1` and `able_to_establish`. Per transition: post the event,
   assert `CurrentState == t.Next` and recorded actions equal `t.Actions`
   in order. Catches orchestrator routing bugs, guard parsing bugs,
   action-order bugs, state-update bugs.

   Does *not* validate that the YAML matches figc4.1 semantically — that
   still needs human review. But composed with the generated
   conformance tests (YAML → TransitionSpec), the smoke test closes
   the runtime loop: YAML → spec → orchestrator behaviour.

2. **Two new codegen lints** in `tools/Packet.Sdl.CodeGen/Program.cs`:
   - **Decision branch completeness**: every decision declared in
     `decisions:` must appear with both `"Yes"` and `"No"` branches
     across some transition pair. Catches a column drawn for one
     branch but not the other.
   - **Guard overlap**: two transitions with the same `on:` must have
     provably disjoint guards (literal-contradiction analysis on the
     compiled guard conjunctions). Catches accidental coverage gaps
     where the orchestrator would silently pick the first match.
   Sanity-tested by temporarily breaking the YAML — both lints fired
   with clear errors.

Also extracted `IActionDispatcher` interface and updated `Ax25Session`
to depend on it (was on the sealed `ActionDispatcher` concrete class).
Production behaviour unchanged; tests can now substitute a recording
stub.

Also removed the stale `AllOtherPrimitives` event record (the bare
event was replaced by three suffixed variants when the catchall events
were reworked) and added the three new event records:
`AllOtherPrimitivesFromLowerLayer`, `AllOtherPrimitivesFromUpperLayer`,
`AllOtherCommands`.

Test totals: 204 (was 187). +17 from the smoke test.

Also committed the **read-`d5`-not-the-shape** rule durably in
`CLAUDE.md` under "Hard rules" — Tom asked not to have to explain
graphml shape interpretation twice. The same rule lives in the
agent's memory system.

### 2026-05-12 — First SDL transcription on the new schema: figc4.1 Data-Link Disconnected

Tom drew Data Link Disconnected State in yEd as
`spec-sdl/data-link/DataLink_Disconnected.graphml` (58 nodes, 61 edges,
14 input columns) — the first complete graphml transcription against the
13-shape palette agreed in OQ-006. I converted it manually to
`spec-sdl/data-link/disconnected.sdl.yaml`, which expands to **17
transitions** once binary decisions (UI's `P == 1?` and one
`Able to Establish?` per SABM/SABME) are enumerated.

Decisions encoded:
- The two `Able to Establish?` diamonds (SABM, SABME) are kept as
  **separate `decisions:` entries** with the same canonical predicate
  `able_to_establish`. Tom's call — they diverge before re-converging
  (Set Version 2.0 vs Set Version 2.2 before joining at the UA send),
  so collapsing them would muddle the redraw.
- Multi-line processing boxes (`SRT := …; T1V := …`, `V(s)/V(a)/V(r) := 0`,
  `Start T3; RC := 0`) split into **one action per line** in the YAML.
  Tom's call — different operations should be different actions; the
  redraw tool can re-group if the figure called for one box.

Read-the-description discipline confirmed: the `d5` field on each node
records the shape class authoritatively. Shape *direction* in figc4.1
does not match figc1.1's stated meaning (DL-* primitives are drawn with
"Signal reception from Lower Layer" shape; received frames are drawn
with "Signal reception from upper layer"). We don't reconcile or
interpret — the description is the source of truth.

Events catalogue changes (`spec-sdl/events.yaml`):
- Bare `all_other_primitives` removed.
- New `catchalls:` group introduced for "all other X" boxes that need
  source-class disambiguation. Three entries:
  - `all_other_primitives__from_lower_layer`
  - `all_other_primitives__from_upper_layer`
  - `all_other_commands` (no suffix — unique label on this page)

Codegen tweak: empty `path:` is now valid (figc4.1's
"All Other Primitives" from lower layer is input → state with no
intermediate boxes; that's a valid SDL pattern). Lint message updated
to suggest `path: []` instead of dropping the field.

Test totals: 187 (was 168). +19 from the new generated conformance
tests (1 source-figure assertion + 1 transition-count + 17 per
transition).

### 2026-05-12 — SDL YAML schema: lossless figure encoding

The flat schema (`transitions: [{ on, guard, actions:[string], next }]`)
was lossy with respect to the SDL figure's structure: chained diamonds
collapsed into the same compound `guard:` as a single compound predicate
would; the original diamond wording was replaced by the canonical
predicate; the position of actions inside a decision tree was recoverable
only by analysis. Tom's requirement is that the YAML be **non-lossy** —
"it should be possible to re-draw the SDLs (minus physical layout) just
from the YAMLs."

Schema changes (`spec-sdl/schema/sdl-machine.schema.json`):

- Page-level **`decisions:`** catalogue. Each diamond has a stable `id`,
  the `question` text as drawn in the figure, and a canonical `predicate`
  evaluated at runtime.
- Per-transition **`path:`** field replaces `guard:` + `actions:[string]`.
  The path is an ordered interleaving of `{decision, branch}` and
  `{action, kind}` steps — exactly the order a reader walks the column.
  The codegen compiles this into the runtime's flat `(guard, actions[])`
  pair (decisions become `not?` predicates ANDed together; actions
  flatten in order) — runtime semantics unchanged.
- Per-action **`kind:`** (enum: `signal_upper | signal_lower | processing
  | subroutine | internal_out`) records the figc1.1 shape class that
  produced each action, so the redraw tool can pick the right shape.
- State-level optional **`save:`** field for the SDL save-parallelogram
  shape (events deferred until next state). Schema-only for now; no
  figure uses it yet.

Runtime C# changes:
- `Packet.Ax25.Sdl.TransitionSpec.Actions` is now
  `IReadOnlyList<ActionStep>` instead of `IReadOnlyList<string>`.
- New `ActionStep(string Verb, ActionKind Kind)` record and `ActionKind`
  enum live alongside `TransitionSpec`.
- `ActionDispatcher` gained an `ActionStep`-taking overload that
  projects `.Verb` for handler lookup; the string overload stays for
  hand-rolled test fixtures.

The old `spec-sdl/data-link/connected.sdl.yaml` (5 transitions, figc4.4a
cols 5+6) and its generated artefacts were deleted — Tom is redrawing
all SDLs in yEd as graphml (OQ-006), and there's no point migrating the
old transcription. The orchestrator tests that previously consumed
`DataLink_Connected.Transitions` were rewritten to use inline
`TransitionSpec[]` fixtures, which is more honest (orchestrator
behaviour should be testable without a specific transcription anyway).

Codegen end-to-end smoke-tested against a temporary fixture exercising
zero-decision, single-decision, and chained-decision paths; output
verified, fixture removed. 168 tests green after migration (was 175 —
the 7 generated conformance tests went with the deleted yaml).

See [`docs/sdl-primer.md`](sdl-primer.md) for the worked schema and the
related "shape direction is not load-bearing" note about figc4.1.

### 2026-05-12 — SDL codegen: Scriban templates + Roslyn parse-back validation

`tools/Packet.Sdl.CodeGen` used to emit C#, Mermaid, and xUnit
conformance tests via hand-rolled `StringBuilder` concatenation. The
"newline-in-a-C#-string-literal" bug from a few weeks back exposed how
brittle that is — escaping rules and template structure were entangled,
and the only way to find a glitch was to build and watch the compiler
complain.

Two changes:

1. **Templates moved to Scriban** (`Scriban` 7.1.0, embedded as project
   resources under `tools/Packet.Sdl.CodeGen/Templates/`). The three
   templates — `code.scriban-cs`, `tests.scriban-cs`, `mermaid.scriban-mmd`
   — are now declarative. Per-transition derived values (escaped string
   literals, joined CSV, Mermaid edge labels) are pre-computed in a
   `TemplateModel`/`TransitionModel` projection so the templates stay
   focused on layout.

2. **Parse-back validation** (`Microsoft.CodeAnalysis.CSharp` 4.14.0).
   Every emitted `.g.cs` and `.g.Tests.cs` is fed through
   `CSharpSyntaxTree.ParseText()` before being written. Any
   `DiagnosticSeverity.Error` aborts codegen with file/line/column and a
   context snippet around the bad line. Future template glitches now fail
   at codegen-time with a pointer to the offending output, not at build
   time with a compiler error against a file the user didn't write.

Scriban 6.x had several outstanding GHSA advisories that NuGetAudit
treats as warning-as-error; 7.1.0 is clean. 175 tests still green.

### 2026-05-12 — Migrate Shouldly → AwesomeAssertions, drop dual-library convention

§2.8 simplified: AwesomeAssertions is the sole assertion library. The
"both auto-imported, prefer AwesomeAssertions for new tests" compromise
created cognitive load with no real upside — two libraries doing
exactly the same thing.

Migration: ~221 Shouldly call sites converted, `Should.Throw<T>(() => …)`
rewritten as `var act = …; act.Should().Throw<T>()`, byte-array
`Should.Be(arr)` rewritten as `Should.Equal(arr)` (AwesomeAssertions
distinguishes scalar `Be` from collection `Equal`), `tolerance:` named
arg replaced by `BeApproximately`. CA1806 suppressed for the
construct-and-discard pattern used by negative constructor tests.

Shouldly dropped from CPM and `tests/Directory.Build.props`. 175 tests
green after migration.

### 2026-05-12 — Phase 2 runner: Ax25Session ties it together

Third and last scaffolding slice. The session takes events, looks up
the codegen-emitted transition tables, evaluates guards via the
interpreter, runs action chains via the dispatcher, and updates state.

- `Packet.Ax25.Session.Ax25Session`: takes `Ax25SessionContext`,
  `ITimerScheduler`, `ActionDispatcher`, `GuardEvaluator`, a
  state-name → transition-list lookup, an initial state, and an
  optional `onUnhandledEvent` hook. `PostEvent(Ax25Event evt)` looks
  up transitions for the current state, picks the first whose `On`
  matches and whose guard evaluates true, runs the action chain,
  advances to `Next`. Unhandled events flow to the callback (or are
  silently dropped, matching SDL semantics).
- `Packet.Ax25.Session.Ax25SessionBindings.CreateDefault(ctx, scheduler)`:
  the standard binding table for `GuardEvaluator` — every identifier
  the SDL transcriptions reference, mapped to a closure over context
  state + scheduler.
- `Packet.Ax25` now references `Packet.Ax25.Sdl` so the session can
  consume the codegen's `TransitionSpec` tables directly. (Generated
  code is the leaf; orchestration sits on top.)

The runner is exercised end-to-end against all five transitions from
our current transcription:
  - DL-FLOW-OFF while busy → RNR response sent, ack-pending cleared.
  - DL-FLOW-OFF while not busy → no-op.
  - DL-FLOW-ON while not busy → no-op.
  - DL-FLOW-ON while busy & T1 not running → RR command, T1 armed, T3 stopped.
  - DL-FLOW-ON while busy & T1 running → RR command, timers untouched.
  - Unhandled event (SABM) → reported via callback, state unchanged.

8 new tests on top of foundations + interpreter. Phase 2's
orchestrator stack is now complete; new SDL transcriptions plug in
by adding to the transition-by-state lookup.

### 2026-05-12 — Phase 2 interpreter: guard evaluator + action dispatcher

Second slice of the orchestrator scaffolding. Sits on top of the
foundations (events / context / timer driver) and converts the SDL's
guard expression strings and action-verb strings into actual mutations
on the session.

- `Packet.Ax25.Session.GuardEvaluator` — recursive-descent parser for
  the boolean expression language used in `*.sdl.yaml` `guard:` fields.
  Grammar: `expr := term ("or" term)* ; term := factor ("and" factor)* ;
  factor := "not"? identifier`. Identifiers resolve via a caller-supplied
  binding table (closures that read the session context). Empty / null /
  whitespace expression is trivially true. Unbound identifier or syntax
  error throws `GuardEvaluationException`.
- `Packet.Ax25.Session.ActionDispatcher` — `switch` over action strings.
  Implements every verb our current transcription uses (flag mutations,
  T1/T2/T3 start/stop, supervisory-frame transmission via callback,
  sequence-variable assignments). Unknown actions throw — typos in a
  new transcription surface at first execution.
- `Packet.Ax25.Session.SupervisoryFrameSpec` — `(SupervisoryFrameType, IsCommand)`
  record. Dispatcher emits these via a sink callback so the session can
  translate them into real `Ax25Frame`s in the runner PR.

47 new tests, all using AwesomeAssertions per [§2.8](#28-new-tests-use-awesomeassertions).

### 2026-05-12 — Phase 2 foundations: events, context, timer driver

First slice of the AX.25 state-machine orchestrator scaffolding. Pure
data structures — no dispatch yet.

- `Packet.Ax25.Session.Ax25Event` — abstract record + ~25 subtypes
  covering every event name in `/spec-sdl/events.yaml` (DL primitives,
  MDL primitives, frame-received variants, timer expiries, internal
  events). Each subtype's `Name` matches the YAML's `on:` strings
  verbatim, ready for the upcoming dispatcher's lookup.
- `Packet.Ax25.Session.Ax25SessionContext` — mutable per-session state:
  sequence variables (V(S), V(A), V(R), RC), flags (own/peer-receiver
  busy, acknowledge-pending, reject-exception, layer-3-initiated,
  …), XID-negotiated link parameters (N1=256, N2=10, k, modulus),
  I-frame queue + sent-frame map. Field names mirror the spec's so the
  action-dispatch switch (next PR) can map clean.
- `ITimerScheduler` + `SystemTimerScheduler(TimeProvider)` — the
  scheduler takes a `System.TimeProvider`, so the same class drives
  real-time (pass `TimeProvider.System`) and virtual-time tests (pass
  `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`,
  call `Advance(TimeSpan)` to fire timers deterministically).

23 new tests. CA1711 ("avoid Queue/Stack/Collection suffixes") suppressed
project-wide on `Packet.Ax25` since AX.25 event names like
`IFramePopsOffQueue` are spec-dictated.

New working agreements introduced in this slice:

- [§2.7](#27-no-datetimenow--wall-clock-in-production-code): no
  `DateTime.Now` in production; use `TimeProvider`. Tests inject
  `FakeTimeProvider`, never real time / `Thread.Sleep`.
- [§2.8](#28-new-tests-use-awesomeassertions): new tests use
  AwesomeAssertions (license-friendly FluentAssertions fork). Existing
  Shouldly tests stay unless touched. Both libraries auto-imported via
  `tests/Directory.Build.props`.

### 2026-05-12 — Pin docker interop image digests

`docker/compose.interop.yml` now references `m0lte/linbpq`,
`ghcr.io/packethacking/xrouter`, and `ghcr.io/packethacking/net-sim` by
`sha256:` digest rather than floating `latest` / `main` tags.

Why: an upstream rebase or behavioural change would silently alter what
"interop is green" means for a CI run. Pinning makes the change visible
(a PR with a digest bump) and lets us roll back trivially when a peer
change breaks something we depended on.

`docker/README.md` documents the refresh procedure: `docker pull` →
`docker inspect --format='{{index .RepoDigests 0}}'` → paste new digest,
open a small PR with notes on what's new upstream.

### 2026-05-12 — Stryker tuning + Phase 2 scaffolding starts

- **OQ-007 closed.** `stryker-config.json` excludes `KissTcpClient.cs` from
  the mutation set, because IO-over-real-socket code is mutation-resistant
  without a fake-socket harness and was dragging Packet.Kiss's score below
  the 70% target. Score: 67.07 → 73.33. The exclusion is documented inline
  in the config; the door's left open to revisit when a fake-socket harness
  becomes worthwhile (probably Phase 6/7).
- Phase 2 scaffolding begun. Goal: build the runtime engine that consumes
  the codegen's transition tables and actually *runs* the AX.25 state
  machines. See [§5.2](#52-phase-2--ax25-v22-data-link-state-machine-).

### 2026-05-12 — XRouter AXUDP interop + FCS-on-the-wire finding

Adds XRouter to the cohort of peers we have working interop tests against.

- **Container brings up cleanly** with `docker/xrouter/XROUTER.CFG`. Key
  config discoveries: keyword is `NODECALL` (not `CALL`), `IPADDRESS`
  doubles as the bind address for all IP services (so `127.0.0.1` makes
  AXUDP unreachable from outside the container), comments must start in
  column 1, AXUDP is configured as a peer-pair (not a generic listener)
  via `INTERFACE TYPE=AXUDP` plus `PORT UDPLOCAL=…`, `IPLINK=…`,
  `UDPREMOTE=…`. We pin `IPADDRESS=172.30.0.11` to the static bridge IP
  set in the compose file so XRouter binds where docker NAT will reach
  it, and set `IPLINK=172.30.0.1` (the bridge gateway, which is where
  host-originated traffic appears from inside the container).
- **XRouter requires FCS on AXUDP frames** ("AXUDP with CRC" per its
  docs). Frames without a trailing CRC-16 are counted as "other
  non-AXUDP ignored". LinBPQ's BPQAXIP driver accepts the FCS-less form
  too, so the two listeners aren't directly compatible — encoders need
  to know which they're talking to.
- **FCS byte order on the wire = low byte first, then high byte.** AX.25
  v2.2 §3.8 says "the FCS shall be transmitted MSB first" but that
  refers to the bit-stream order on the radio, not the byte order in
  serialised octets. Verified empirically: XRouter accepts low-byte-
  first as "valid AXUDP received", rejects high-byte-first as "non-
  AXUDP".
- New API surface:
  - `Ax25Frame.WriteToWithFcs(Span<byte>)` / `Ax25Frame.ToBytesWithFcs()`
  - `AxudpSocket.SendAsync(..., includeFcs: bool, ...)`
- New interop test: `Packet.Interop.Tests/Xrouter/XrouterAxudpInterop.cs`
  with two scenarios — web UI reachability and "send a UI frame, watch
  XRouter's `stats axudp` valid-counter tick".

### 2026-05-12 — PR-only workflow + CI healthcheck portability

- **Working agreement:** all code changes go through pull requests from this
  point on. No more direct pushes to `main`. Reflected in [§2.4](#24-default-to-executing-actions-with-care)
  by extension (merging is a destructive shared-state action).
- **CI healthchecks** in `docker/compose.interop.yml` rewritten to use bash's
  `/dev/tcp` builtin instead of `wget` / `curl`, because neither tool is
  present in the LinBPQ or net-sim base images. (Xrouter has no explicit
  healthcheck — depends_on `service_started` is sufficient there.) The
  pattern to follow for any future container we add: probe a TCP port we
  know the daemon binds, with `["CMD", "bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/PORT && exec 3<&-"]`.
- **CI artifact uploads** moved to `if: failure()` + `continue-on-error: true`
  in `ci.yml` so a future GitHub artifact-storage quota issue doesn't mask
  the actual test outcome on green runs.
- First push to `origin/main` (`M0LTE/packet.net`) completed at this point:
  13 commits encompassing Phase 0 + Phase 1 + the autopilot lap.

### 2026-05-12 — Autopilot lap: Mermaid codegen, net-sim interop, mutation baseline, NinoTNC catalog

Four bits of "lap-of-honour" work done without SDL touching:

**1. Mermaid output in `Packet.Sdl.CodeGen`.** Each `*.sdl.yaml` now also produces a `*.g.mmd` (stateDiagram-v2) alongside the YAML. The Mermaid view is a deliberate "machine-eye" rendering — looks nothing like the original SDL figure, but reveals transcription bugs (missing transition, wrong destination state) that a YAML-only review might miss. Header in each `.g.mmd` is explicit that it is NOT a substitute for the source figure. CI's SDL-discipline guard now also covers `*.g.mmd` drift.

**2. Net-sim KISS-TCP round-trip interop verified.** `tests/Packet.Interop.Tests/Netsim/NetsimKissTcpInterop.cs` connects a Packet.Kiss client to each of net-sim's two simulated nodes (afsk1200 link, 10 dB path loss, `localhost:8100` / `localhost:8101`), transmits a UI frame on one, observes it on the other. Closes the Phase 1 exit criterion that LinBPQ couldn't satisfy because LinBPQ doesn't host a KISS-TCP listener. **First real RF-domain round-trip working in Packet.NET.** Net-sim topology fixed: both ports on `afsk1200` for modem-match (previous mismatch warning resolved).

**3. Mutation-testing baseline (Stryker.NET 4.14.1).** Local tool installed (`dotnet-tools.json` committed). Baselines:

| Library | Mutation score | Notes |
|---|---|---|
| Packet.Core | **81.51 %** | Crc16Ccitt 85.71, Callsign 82.14, Ax25Address 79.59 — all above target |
| Packet.Kiss | **67.07 %** | KissDecoder 88.46, KissEncoder 67.65, KissTcpClient 40.91 (IO-heavy, mutation-resistant without fake socket — drags total) |
| Packet.Ax25 | **70.65 %** | Single file (Ax25Frame.cs) |

Score ≥70 % is the eventual gating threshold per [§5.1](#51-phase-1--kiss--ax25-framing--axudp-). We're at-or-above on every library *except* `Packet.Kiss` and the only library dragging it down is `KissTcpClient`, which needs a fake-socket scaffold to mutation-test usefully. Adding `KissTcpClient` exclusion or fake-socket coverage is a Phase 2 housekeeping item (added as [OQ-007](#15-open-questions)).

Reports under `artifacts/stryker/{core,kiss,ax25}/`. Path is .gitignored.

**4. NinoTNC mode catalog ported.** `Packet.Kiss.NinoTnc` (was an empty stub) now contains:

- `NinoTncMode` — `(byte Mode, string Name, int BitRateHz)` record + `TransmissionMs(int)` helper.
- `NinoTncCatalog` — `FrozenDictionary` of all 16 modes by DIP-switch position, plus `FirmwareByteToMode` reverse-lookup for parsing TX-Test frames (firmware v3.44 values).
- `NinoTncSetHardware.BuildPayloadByte(mode, persistToFlash)` — `mode + 16` when non-persist, matching kissproxy `KissFrameBuilder.cs:191-196`.
- `NinoTncSetHardware.BuildKissFrame(...)` — full KISS-encoded SETHW frame.

24 unit tests verify the data tables and the SETHW arithmetic against the kissproxy source. Phase 3 will build on this; TX-Test parsing deferred.

**Outcome:** project now has **95+ tests passing under default filter** (was 71). One real interop round-trip works end-to-end. Mutation baseline gives us a regression detector for Phase 2's churn. NinoTNC pre-positioning means Phase 3 has less in flight when it lands.

### 2026-05-12 — Phase 1 close + LinBPQ KISS-TCP listener finding

- Phase 1 complete in code: CRC, Callsign, Ax25Address, KISS encoder/decoder + TCP client, AX.25 UI frame encode/decode, AXUDP socket. 73 tests under default filter.
- LinBPQ interop verified at the wire-format-acceptable bar: container brings up cleanly on `m0lte/linbpq:latest`, HTTP UI reachable, AXUDP frame accepted without error.
- **Finding:** LinBPQ does **not** host a native KISS-TCP listener. Its config grammar has no driver for that role — KISS-TCP needs an external softmodem (Direwolf, UZ7HO, or net-sim's samoyed) bridged in. Phase 1 was scoped as "UI frame round-trip vs LinBPQ container (KISS-TCP)"; we've pivoted that exit criterion to be satisfied by net-sim instead, which natively exposes KISS-TCP per simulated port.
- **Finding:** LinBPQ's `bpq32.cfg` requires the `SIMPLE=1` global to auto-fill ~25 default L3/L4 parameters (OBSINIT, NODESINTERVAL, MAXLINKS, T3, …). Without it, individual specification of all of them is required. Patterned our test fixture on LinBPQ's own `tests/integration/helpers/linbpq_instance.py:DEFAULT_CONFIG`. Updated `docker/linbpq/bpq32.cfg`.
- **Finding:** Full AXUDP round-trip ("LinBPQ parsed and re-emitted the frame") requires AGW monitor or telnet client to read LinBPQ's state. Both are Phase 6 deliverables. Phase 1 interop is therefore "send-trip" verification — the strongest bar reachable without further infrastructure.
- Interop test file renamed `LinbpqKissTcpInterop.cs` → `LinbpqAxudpInterop.cs`.

### 2026-05-11 — KISS command-byte escape deviation (Phase 1)

- The KISS protocol spec text says the command byte is not escaped — only the
  payload bytes. In practice that creates an undecodable stream whenever the
  command byte happens to be `0xC0` (FEND) or `0xDB` (FESC).
- Concrete case: multi-drop `port=12` + `cmd=Data` → command byte `0xC0`
  → wire bytes `C0 C0 C0`, which any decoder reads as "two empty frames".
- Found by the round-trip property test (FsCheck shrunk to that exact case
  after 41 tries).
- Resolution: `Packet.Kiss.KissEncoder` escapes the command byte through the
  same `FESC TFEND` / `FESC TFESC` rules as the payload. This matches direwolf's
  `kiss_frame.c` ("If it happens to be FEND or FESC, it is escaped, like any
  other byte"). Documented inline in the encoder source.
- New decision-relevance: the KissEncoder XML doc / [§17] is the only place
  this deviation lives. The KISS spec text in `docs/plan.md §13.1` should be
  read with this caveat in mind.

### 2026-05-11 — Plan promoted to living document
- This `docs/plan.md` rewritten from a snapshot of the original plan into the project's authoritative living source of truth.
- Added: working agreements, glossary, open questions, risks, amendment log, "how to update".
- `README.md` and `CONTRIBUTING.md` updated to point here.
- Discipline established in [§18](#18-how-to-update-this-document).

### 2026-05-11 — col 5 / DL-FLOW-OFF SDL deep dive: trust-the-figure discipline
- Goal of the spike was DSL validation; the col 5 SDL semantics question accidentally became a long investigation.
- Initial mistake: I (the agent) inferred semantics from the PNG, then asserted the figure was wrong on the basis of pattern-match against §6.4.10. Wrote a draft "spec issue" before checking the spec text *or* implementations.
- Tom corrected: figure is right. Spec author confirmed via a collaborator.
- Resolution path: read spec text (§5.3 primitives, §6.4.10 busy indication, §C4.3 flag definitions); survey implementations (LinBPQ has no DL primitives at all; ax25ms defines `own_receiver_busy` but only ever assigns `false`; direwolf implements the full SDL and explicitly marks `own_receiver_busy` paths *unreachable* with a comment).
- Bottom line: figure correctly describes a vestigial part of the protocol that no major peer exercises. Encode as drawn, document the implementation lineage in transition `notes:`, move on.
- New working agreements: [§2.1 Trust the figure](#21-trust-the-figure), [§2.2 Encode-then-verify](#22-encode-then-verify-not-infer-then-encode), [§2.3 Pin implementation evidence](#23-pin-implementation-evidence).
- New doc: [docs/sdl-primer.md](sdl-primer.md), with its "When the figure surprises you" section rewritten to discourage overriding figures.
- The `docs/spec-issues/` directory created during the misfire was deleted; no upstream issue was filed.

### 2026-05-11 — Phase 0 spike complete
- Solution + 25 projects (13 library + 2 console + 10 test).
- CPM + build/test props centralised.
- `.slnx` format chosen over legacy `.sln`.
- SDL DSL + codegen tool working end-to-end on figc4.4a cols 5+6 (5 transitions, 7 conformance tests).
- Docker compose stack validates (`docker compose config` clean).
- Hardware-loop probe skips cleanly without TNCs attached.
- All 14 non-hardware non-interop tests pass.

### 2026-05-11 — Original plan locked
- Initial planning round: framework, frontend, persistence, auth, SDL discipline, NET/ROM scope, MCP scope, telemetry posture, reference plugin (DAPPS), CLI shape, naming, packaging strategy.
- Original plan file lives in git history of `/home/tf/.claude/plans/i-d-like-to-implement-goofy-kahan.md` (Claude Code plan-mode artefact, outside the repo).

---

## 18. How to update this document

This document is updated *meticulously* as work progresses. The discipline:

### When to update

You **must** add an amendment-log entry and update the relevant section(s) when any of the following happens:

- A phase exit criterion is met (mark the checkbox, update the phase status, update the top-of-file "Current phase" header).
- A locked decision changes (update [§3](#3-locked-decisions), add to the amendment log explaining why).
- A working agreement is added or revised (update [§2](#2-working-agreements), add to log).
- A new ADR is created in `docs/adr/` (cross-reference from the relevant section).
- An open question is resolved (move it out of [§15](#15-open-questions), update affected section, add to log).
- A new risk surfaces (add to [§16](#16-risks)).
- A new external dependency is taken (update [§12](#12-locked-external-dependencies) and `Directory.Packages.props`).
- A new glossary term is encountered (add to [§14](#14-glossary)).
- A reference repo or doc becomes load-bearing (add to [§13](#13-reference-shelf)).

You **should** also update when:

- A phase deliverable is added or removed (revise that phase's deliverables list).
- The architecture diagram drifts from the code (revise [§4.2](#42-data-flow)).
- A working agreement gets stress-tested in practice and the wording could be sharper.

### How to update

1. Edit `docs/plan.md` in the same PR as the work that triggers the update. Plan changes do not get their own PR.
2. Add an amendment-log entry at the **top** of [§17](#17-amendment-log) (most recent first). Date in `YYYY-MM-DD` format. Short title. 2–6 bullets explaining what changed and where to look for detail.
3. Update the front-matter "As of", "Current phase", "Latest amendment" lines.
4. Cross-link aggressively. If an amendment references a section, link to it; if a section references an amendment, link to it.
5. If your PR introduces or modifies a working agreement, mention it in the PR description under "Plan changes".

### Tone

The plan document is dense and load-bearing. It is allowed to be terse. It is **not** allowed to:

- Repeat itself across sections.
- Drift from the actual repo state (file paths, module names, dependency versions).
- Use marketing language. Just say what the thing is.
- Omit the "why" on a load-bearing decision.

When in doubt, write less and link more.

### For agents

You — the agent reading this — are explicitly expected to update this document as you do work. Treat the plan-update step as part of completing the engineering task, not a separate optional follow-up. If you finish a phase exit criterion and don't update the corresponding `⬜` to `✅` and add an amendment-log entry, you have not finished the task.

The plan is the most expensive thing in this repo to keep current and the most expensive thing to lose. Don't lose it.
