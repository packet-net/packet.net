# Packet.NET — Plan

> **This document is the authoritative source of truth for the project's direction, status, and accumulated working knowledge.** It is a *living* document — kept up to date as work progresses. Anything load-bearing about how Packet.NET is being built belongs here.
>
> If you are reading this for the first time: start with [Why Packet.NET?](#1-why-packetnet) and [Working agreements](#2-working-agreements). If you are looking for *what to build next*, jump to [Roadmap](#5-phased-roadmap). If you are an agent: read [Working agreements](#2-working-agreements) carefully — those are the operating instructions that take precedence over your defaults.

**As of:** 2026-05-14
**Current phase:** Phase 2 in progress — `Ax25Session` runner online. First transcribed transitions (figc4.4a cols 5+6) drive end-to-end through the orchestrator. Phase 3 (KISS hardening) pulled partially forward overnight on 2026-05-14 against the live NinoTNC pair: serial driver, ACKMODE round-trip, TX-Test frame parser, adaptive-parameter scaffolding, adaptive-transport glue, and a first soak campaign producing [`docs/nino-tnc-characterisation.md`](nino-tnc-characterisation.md). Next: more SDL pages, plus a real-RF soak campaign once we have field data to compare against the bench.
**Latest amendment:** [§17 entry 2026-05-14 Soak campaign + adaptive transport + hardware-loop test serialisation](#17-amendment-log)

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
  Packet.Ax25.Conformance.Tests/      GENERATED, mirrors src/Packet.Ax25.Sdl/
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

### 5.2 Phase 2 — AX.25 v2.2 Data-Link state machine ⬜

Goal: full AX.25 connected-mode operation against LinBPQ — connect, send, retry, disconnect, mod-8 and mod-128.

**Scope**

- Transcribe all remaining 26 SDL pages into `/spec-sdl/` (Simplex Physical 7, Duplex Physical 5, Link Multiplexer 3, Data-Link remaining 6, Management Data-Link, Segmenter, Reassembler).
- Hand-written orchestration: timer driver (T1 / T2 / T3 + phy timers), session lookup by `(local, remote, port)`.
- Management Data-Link drives XID. Defaults: PI 0x06 N1=256, PI 0x08 k=4 (mod-8) / 32 (mod-128), PI 0x09 T1=3000 ms, PI 0x0A N2=10.
- Segmenter for PID 0x08 with `F | seg-remaining(6)` first byte.
- FRMR generation disabled under v2.2 (deprecated per §4.3.4).

**Exit criteria**

- Connect/disconnect mod-8 + mod-128 vs LinBPQ.
- REJ and SREJ retransmits observed on the wire.
- Timer Recovery entered + exited under scripted net-sim 100 % loss for `(T1−1)·N2` then recovery.
- Segmenter reassembles a 1500-byte payload across multiple I-frames.
- FsCheck property tests prove window invariants (`V(A) ≤ V(S) ≤ V(A)+k`, no orphan transitions, no stuck Timer Recovery).
- Hardware loop sustains 10 kB transfer across NinoTNCs with 0–30 % scripted loss.

### 5.3 Phase 3 — KISS hardening ⬜

ACKMODE round-trip, multi-drop, NinoTNC mode catalog (0–15) + SETHW byte (mode + 16 for non-persist), TX-Test frame parser, ParameterSendInterval. Verified against both LinBPQ container and the back-to-back NinoTNC pair.

### 5.4 Phase 4 — Node host ⬜

ASP.NET Core 10 minimal-API host. `Konscious.Security.Cryptography` Argon2id + `Fido2NetLib` WebAuthn + `Microsoft.IdentityModel.JsonWebTokens` JWT. SQLite via `Microsoft.Data.Sqlite` + `Dapper`. First-start bootstrap: random admin password + one-time `/setup?token=…` URL.

### 5.5 Phase 5 — Web UI ⬜

Vite + React + TS + Tailwind + shadcn/ui. Routes: Ports, Sessions, Live Monitor, TNC2 Prompt, Users, Link Troubleshoot. OpenAPI client via `openapi-typescript` + `@tanstack/react-query`.

### 5.6 Phase 6 — RHPv2 + AGW ⬜

Mirror RHPv2 framing from `/home/tf/src/rhp2lib-net/src/RhpV2.Client/Protocol/`. TCP + WS listeners. `--linbpq-compat` flag for the WS subset. AGW server **127.0.0.1 only** by default; `--listen-public --i-understand-agw-is-unauthenticated` required to expose.

### 5.7 Phase 7 — Packaging + one-click update ⬜

Self-contained `dotnet publish` matrix for linux-x64/arm64/arm (v7), win-x64, osx-arm64/x64. Multi-arch Docker via `buildx`. `installers/install.sh` with cosign verification. In-app `POST /api/system/update` with watchdog rollback.

### 5.8 Phase 8 — MCP + live monitor v2 + link troubleshooting ⬜

`Packet.Mcp` over stdio + SSE. Read tools: `list_ports`, `list_sessions`, `recent_frames(filter)`, `link_quality(remote)`, `network_topology`, `decode_frame(hex)`. Write tools: `send_ui_frame`, `reset_port`, `disconnect_session`, `set_kiss_param` — require separate `mcp:invoke` scope token and audit-logged. Link troubleshoot view: per-link RTT, retries, REJ/SREJ counts, T1/T3 graphs.

### 5.9 Phase 9 — Plugin API + NET/ROM + hardening ⬜ (post-v1)

`Packet.Node.Extensions/IApplicationModule.cs` — REST routes, MCP tools, frontend bundle, AX.25 session handler. Loaded from `/var/lib/packetnet/plugins/*.dll` in isolated `AssemblyLoadContext`. DAPPS validates the API design. `Packet.NetRom` ships as a separate package. SharpFuzz harnesses + 72 h soak vs LinBPQ.

### 5.10 Phase 10 — Hardware ecosystem & adaptive RF ⬜ (post-v1)

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

### 5.X Spike backlog ⬜

Smaller, time-boxed exploration tasks. Not phase-sequenced — each can land
whenever it's the highest-leverage next thing. Listed in approximate order
of leverage-per-effort.

**SP-001 — LinBPQ MQTT frame feed ingestion** (high leverage, low effort).
Tom operates a live LinBPQ node with four real RF ports and is willing to
expose an MQTT feed of every AX.25 frame sent/received (with or without KISS
wrapper). Build a `Packet.Replay.MqttIngest` tool that subscribes to the
feed, decodes frames, runs them through our parser, and emits structured
records (frame type, callsigns, control bits, payload size, decoded errors).
Acts as a 24/7 corpus of real-world frame traffic across four real RF
channels — vastly richer than synthetic test traffic. Specifically catches
parser edge cases that property-tests won't generate. Feeds the regression
corpus in SP-003.

**SP-002 — Direwolf-as-reference end-to-end harness** (high leverage, medium
effort). Docker-containerise direwolf, expose KISS-TCP, point Packet.NET at
it. For every transcribed transition, A/B our orchestrator's behaviour
against direwolf's actual output. Direwolf is the closest-to-figure
implementation (see SI-* triangulation in [`docs/spec-issues.md`](spec-issues.md)),
so behavioural equivalence with direwolf is a strong correctness signal.
Strongest force multiplier on the SDL transcription work.

**SP-003 — Replay/record harness** (high leverage, medium effort). pcap-
style timestamped capture of KISS + AX.25 wire bytes with per-frame direction
+ port + source labels. Replay later to repro bugs, build a regression
library of weird-frames-seen-in-the-wild, and A/B real on-air behaviour
against the orchestrator's projection. Foundation for SP-001's persistence
+ for future "I saw a strange frame, replay this against the state machine"
debugging. Storage as JSON-lines or capnproto; both fine.

**SP-004 — AX.25 wire-format fuzzer (SharpFuzz)** (medium leverage, low
effort). Plan §7 already promises this for nightly. Frame parser as target.
Mostly mechanical: a fuzz harness against `Ax25Frame.TryParse`, plus
`KissFrame.TryParse`. Likely surfaces real bugs in edge cases (oversized
fields, weird PIDs, malformed addresses). Cheap and overdue.

**SP-005 — Multi-Packet.NET-instance interop via net-sim** (medium leverage,
low effort). Net-sim already in the interop stack. Spin up 2-3 Packet.NET
instances and let them talk to each other through net-sim's lossy channel.
Any drift between identical implementations exposes a state-machine bug
cheaper than against LinBPQ — and complements interop tests against
heterogeneous peers.

**SP-006 — Spec-prose-to-stub-test extractor** (higher effort, payoff after
Phase 2). Parse the AX.25 v2.2 spec markdown, pull every "shall" sentence
into a stub xUnit test (initially `[Skip]`). Surfaces gaps where the SDL
transcription doesn't cover a prose mandate. Lower priority — the SDL
transcription itself already does most of the work, but this acts as a
backstop.

**SP-007 — NinoTNC KISS-side SNR proxy** (low leverage but useful fallback).
Even without Tait integration, can we infer link quality from frame-quality
bits / retry rates / error counts alone? If yes, gives `IRadioControl`'s
quality signal a fallback path for users without CAT-capable radios.
Investigate after SP-001 has produced enough real-world data to baseline.

**SP-008 — Full APRS encoding/decoding library** (large scope; significant
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

### 5.Y Hardware-arrival probe playbook ⬜

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
              ▼  dotnet run --project tools/Packet.Sdl.CodeGen
src/Packet.Ax25.Sdl/<Machine>_<State>.g.cs                          ← generated, checked in
tests/Packet.Ax25.Conformance.Tests/<Machine>_<State>.g.Tests.cs    ← generated, checked in
```

CI runs the codegen and `git diff --exit-code`. Drift fails CI.

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
3. Agent runs `dotnet run --project tools/Packet.Sdl.CodeGen -- --in spec-sdl --out src/Packet.Ax25.Sdl --tests tests/Packet.Ax25.Conformance.Tests`.
4. `dotnet build` + `dotnet test` should pass. CI re-runs the codegen and fails on diff.
5. PR review pairs the YAML diff with the source figure. The PR description embeds or links to the figure.
6. On non-obvious transitions, the PR also cites at least one implementation reference per [§2.3](#23-pin-implementation-evidence).
7. Any `verification_pending` note added to the YAML must also be added to [`docs/spec-issues.md`](spec-issues.md) — the central tracker for candidate upstream-spec issues. Cross-link in both directions (YAML transition notes reference the SI-NN id; the tracker cites the YAML transition id).

### 6.4 SDL inventory & status

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
| 2 | SABM/UA cycle (mod-8) vs LinBPQ | wire capture matches expected sequence numbers |
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
| OQ-001 | Provenance / spec-deviation field — promote `notes:` prefix to a structured `verification:` block? Deferred until we have ≥3 examples to validate the schema against. | Open | Tom |
| OQ-002 | Hardware-loop runner — should we expose it via GitHub Actions self-hosted runner labels (`hardware-loop-runner`)? Tom is providing the rig. Will sort once kit lands on the bench. | Open | Tom |
| OQ-003 | Cosign key management for one-click update — where does the trusted public key live in the installer? Probably `installers/sigstore-pubkey.pem` baked into the install script, but needs a key-rotation story. | Open | Phase 7 |
| OQ-004 | NuGet org name — is `Packet.*` claimable on nuget.org or do we need `PacketNET.*` / `Packethacking.*`? Tom to check before first NuGet publish (Phase 6+). | Open | Tom |
| OQ-005 | OIDC pluggability — what's the abstraction shape so we don't paint ourselves into a corner? Affects Phase 4 user model. | Open | Phase 4 |
| OQ-006 | yEd / GraphML SDL workflow — Tom tried Mermaid and reported the rendering looks too unlike the original SDL to be useful for visual comparison. Mermaid output is shipped anyway (cheap, catches transcription bugs the YAML diff might miss) but does NOT solve the input-side problem. yEd spike (one figure, agreed shape mapping, parse the .graphml back to our YAML) still on the table whenever Tom has big-screen time. | Open | Tom |
| ~~OQ-007~~ | ~~Stryker mutation score for `Packet.Kiss` is dragged below 70 % by `KissTcpClient`…~~ | ✅ Resolved 2026-05-12 — excluded via `stryker-config.json`; score 67.07→73.33. Fake-socket harness left for whenever Phase 6/7 wants tighter coverage. |
| OQ-008 | Publish `/spec-sdl/` as a community-canonical AX.25 v2.2 state-machine artifact, separate from this repo? The YAML is language-agnostic by design — a Rust/Python/Go/TS codegen against the same files would produce the same transitions, and our C# codegen becomes the reference implementation rather than the source of truth. Three things would need to firm up before "authoritative" is defensible: (a) the guard mini-DSL needs a real grammar (today `GuardEvaluator` parses by ad-hoc string splitting — fine for one consumer, not for many); (b) action verbs need a stable catalog with documented semantics (today they're free-form strings like `"RNR response"`, `"start_T1"`); (c) the schema + events catalog need semver. Realistic move: finish the 27 pages, stabilise the schema, then split `/spec-sdl/` + schema + events into a sibling repo (likely under `packethacking/`). What makes this credibly authoritative rather than just *another* transcription is the encode-then-verify discipline + collaboration with the spec author — both already in place. Revisit at the end of Phase 2. | Open | Tom |
| OQ-009 | NinoTNC mode-change handshake — does the firmware support runtime mode switching without a write-to-flash cycle? `SETHW(mode + 16)` is the "don't write to flash" form; is the actual mode change immediate, or does it require power-cycle? Affects feasibility of Phase 10's mode-agility workstream. Probe once hardware is on the bench. | Open | Phase 10 / hardware-arrival |
| OQ-010 | NinoTNC bootloader / firmware-update protocol — `flashtnc` is the canonical tool; what's the wire protocol? Best to read [`ninocarrillo/flashtnc`](https://github.com/ninocarrillo/flashtnc) source rather than reinvent. Affects feasibility + risk of `packetnet ctl flash-tnc`. | Open | Phase 10 |
| OQ-011 | Radio-control abstraction shape — what's the right `IRadioControl` API? Tait CCDI gives us SNR / RSSI / busy / channel / TX-keying. Yaesu CAT and ICOM CI-V have different feature sets. Common subset is probably {frequency-set, frequency-get, RSSI-get, busy-get, PTT-set} — anything radio-specific (Tait's SNR is unusually rich) goes behind a feature-probe. Decide before locking the Tait implementation. | Open | Phase 10 |

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
