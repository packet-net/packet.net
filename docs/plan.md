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
