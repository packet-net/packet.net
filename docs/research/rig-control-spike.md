# Rig control (CAT) for PDN — research + spike + `Packet.Rig*` v0

**Date:** 2026-07-13 · **Status:** shipped (v0 libraries + tests on this branch)
**Ask (Tom):** add rig control to PDN, using hamlib presumably; also research flrig and OmniRig
and whatever else exists. Get/set mode and frequency at minimum, SWR and power monitoring if
supportable. Integration piece only (no UI yet). Reusable NuGet package. Think about testing.

## TL;DR

- **Shipped three NuGet-able libraries:** `Packet.Rig` (dependency-free `IRigControl`
  abstraction), `Packet.Rig.Hamlib` (pure-managed client for hamlib's `rigctld` TCP protocol),
  `Packet.Rig.Flrig` (flrig XML-RPC client). OmniRig is research-only (below).
- **hamlib integration = speak the `rigctld` network protocol, never P/Invoke.** Every .NET
  hamlib binding is dead (the P/Invoke lineage stopped 2017–2021, pre-hamlib-4 ABI); hamlib 5
  (in development) breaks the C ABI again, while the NET-rigctl TCP protocol has been stable
  since 4.0 (2020) and is the surface hamlib itself tells non-C clients to use ("programs that
  only use the network daemons should be unaffected"). Every surviving client ecosystem
  (Python, Rust, Go, Node, and the active C# apps) made the same call.
- **Testing:** the ecosystem's gold standard is exactly what we shipped — in-process scriptable
  fakes for unit tests, parsers exercised against wire text captured from a real daemon, and
  integration tests against `rigctld -m 1` (the dummy rig) that skip when hamlib isn't
  installed. 95 tests; the interop leg ran green against real rigctld 4.5.5 in this session.

## 1. Landscape

### hamlib (chosen: primary backend)

Current stable 4.7.2 (2026-06); 4.7.x is LTS while master is 5.0-dev. Two integration surfaces:

| Surface | Verdict |
|---|---|
| libhamlib C API via P/Invoke | **No.** Struct-heavy headers with order-dependent layouts (`rig_caps` ~182 members, non-opaque `RIG` handle), an announced ABI break in 5.0 (structs going opaque), per-RID native packaging on us, LGPL native distribution questions. The .NET P/Invoke lineage (HamLibSharp → HamLibSharpStandard → NRig.Hamlib — the latter two being Tom's own dormant packages) is uniformly dead, targeting hamlib ≤3.1/4-early. |
| `rigctld` TCP text protocol (port 4532) | **Yes.** Stable since 4.0 (`RIGCTLD_PROT_VER 1`, unchanged through 5.0-dev), documented for exactly this purpose, multi-client, and the same client also reaches the rigctld-protocol *emulators*: wfview (4533), SDR++, GQRX (subset, 7356), SparkSDR, VE3NEA's skycatd, nCAT (FlexRadio). What WSJT-X (via NET-rigctl model 2), gpredict, CQRLOG, Log4OM all do. |

Protocol facts the client is built on (verified in source + live spike):

- **Extended Response Protocol** (`+` prefix) exclusively: every reply is `echo line → payload
  lines → RPRT n`. The default protocol has no success terminator on gets (per-command line
  counts) — the regex fragility every naive client hits. `get_level` payloads are bare values;
  structured gets are `Key: value`.
- **`\chk_vfo` is special**: replies one line, never an `RPRT`, and its shape varies (`0` on
  4.x, `CHKVFO 0` on 3.3, `ChkVFO: 0` echo form). Sent at connect in the default protocol, like
  hamlib's own netrigctl client; if the daemon runs `--vfo`, every rig command gets `currVFO`
  injected as its first argument.
- **Capabilities** from `\dump_caps` (`Can get Frequency:\tY|N|E`, `Get level: SWR(…) …`).
  Advertised caps are intent, not guarantee — the dummy rig advertises PTT it can't key without
  a PTT device; runtime rejections surface as typed errors.
- **Errors**: `RPRT -n` maps to hamlib's `rig_errcode_e` (1 EINVAL … 22 EACCESS); -4
  ENIMPL/-11 ENAVAIL are the "not supported after all" signals.
- **Metering**: `l SWR` (ratio), `l RFPOWER_METER` (0–1), `l RFPOWER_METER_WATTS` (watts,
  ≥4.4), `l STRENGTH` (dB rel. S9). `l ?` lists the rig's tokens.
- **Poll-only.** The TCP side never pushes; transceive is deprecated. (4.6+ has an experimental
  off-by-default UDP-multicast JSON state broadcast — a future listen-only enhancement, and the
  one thing hamlib's repo has C# example code for.)
- rigctld has **no authentication** (the 4.x password feature was compile-gated out after
  CVE-2026-54634 in 4.7.2) — loopback by default, exposure is an operator decision.

### flrig (chosen: second backend)

fldigi's rig server; XML-RPC over HTTP, default `127.0.0.1:12345`, multi-client, poll-only, no
headless mode. Hamlib's own flrig backend (`rigs/dummy/flrig.c`) is the de-facto client
contract and our reference: frequency **get** returns a *string* of Hz (`rig.get_vfo`) while
**set** takes a *double* (`main.set_frequency`); mode names are rig-native strings (enumerate
via `rig.get_modes`; unknown names are silently dropped by flrig — our client throws instead);
no passband on the get side; SWR = `rig.get_SWR` (newer) else interpolate the 0–100
`rig.get_swrmeter` deflection through hamlib's anchor table (0→1.0, 10.5→1.5, 23→2.0, 35→2.5,
48→3.0, 100→10); power = `rig.get_pwrmeter` deflection × `rig.get_pwrmeter_scale` (watts) or
/100 × scale (relative). Porting note: hamlib rounds SWR with C `round()` (half away from
zero) — .NET's default banker's rounding disagrees at midpoints; our tests caught this.

### OmniRig (research only — not implemented)

Windows-only out-of-process **COM** server (VE3NEA's 1.20; the HB9RYZ 2.x fork is
client-incompatible with 1.x). `IRigX` exposes `Freq/FreqA/FreqB` (**32-bit** — overflows above
~2.147 GHz), `Mode`/`Tx` as `PM_*` bitmask params, and `ParamsChange` events — genuinely
event-driven, which the others aren't. **No SWR or power metering at all** (confirmed from
OmniRig.ridl — the workaround is raw CAT via `SendCustomCommand`, i.e. writing per-rig CAT
anyway). No non-Windows story, COM interop untestable in our Linux CI, and PDN nodes are
Linux-first ⇒ document, don't build. If Windows demand appears: late-bound
`Type.GetTypeFromProgID("OmniRig.OmniRigX")` in a windows-only TFM (cloudlog-helper's pattern).

### The rest of the field (for the record)

- **TCI** (Expert Electronics; MIT-licensed spec): WebSocket, and the only open protocol with
  first-class **push** telemetry including `tx_sensors` (fwd/rev power, SWR). Implemented by
  ExpertSDR and Thetis. Strongest candidate for a third backend if SDR users appear.
- **Kenwood/Elecraft ASCII CAT over TCP**: K4 (port 9200) and TS-890/990 KNS (60000 + login)
  speak it natively; Thetis/SmartSDR/piHPSDR emulate TS-2000 over TCP. It's a transport option
  on a future direct-CAT backend, not a separate protocol; hamlib covers these rigs meanwhile.
- **FlexRadio SmartSDR API** (4992 + VITA-49): richest telemetry, Flex-only, big surface;
  reachable today via nCAT's rigctld emulation.
- **DX Lab Commander / HRD**: Windows app TCP protocols; no meters (Commander) / proprietary
  (HRD). Not worth a backend.
- **.NET prior art**: no maintained general CAT library on NuGet (survey 2026-07). Notable:
  VE3NEA's SkyCAT (C# CAT engine + rigctld-compatible daemon, active, not on NuGet), FT891.Core
  (single-rig, exemplary simulator+tests), NRig (Tom's, dormant 2022). `Packet.Rig` is
  effectively NRig's successor inside the Packet.NET family.

## 2. What shipped

```
src/Packet.Rig            IRigControl + RigCapabilities + RigMode/RigModeState + RigInfo
                          + typed RigException taxonomy. Zero dependencies.
src/Packet.Rig.Hamlib     RigctldRig (+options) — extended-protocol rigctld client;
                          RigctldProtocol (internal, IO-free parsing).
src/Packet.Rig.Flrig      FlrigRig (+options) — flrig XML-RPC client; XmlRpcCodec +
                          FlrigMeters (internal, IO-free).
tests/Packet.Rig.Tests            RigMode semantics (10 tests)
tests/Packet.Rig.Hamlib.Tests     parser tests on captured wire text + FakeRigctld
                                  behaviour suite + real-rigctld interop (52 tests)
tests/Packet.Rig.Flrig.Tests      codec/meter tests + scripted-handler behaviour suite (33)
```

### The abstraction (`IRigControl`)

Cross-backend common subset: frequency get/set (Hz, `long`), mode get/set
(`RigMode` canonical-token-or-native-string + optional passband; `null` passband = "rig default
for the mode" — the only semantics all backends can honour), PTT get/set, `ReadSwrAsync`
(ratio), `ReadRfPowerAsync` (0–1), `ReadRfPowerWattsAsync` (watts) — each gated by
`RigCapabilities` flags probed at connect; unadvertised member ⇒ `NotSupportedException` (the
`IRadioControl` discipline). Both backends keep an escape hatch below the abstraction
(`ReadLevelAsync`/`TransactRawAsync`; `CallRawAsync`) — the `TaitCcdiRadio.TransactRawAsync`
pattern. PTT contract inherited from `IRadioControl`: **best-effort unkey on dispose**.

**Relationship to `Packet.Radio` (OQ-011):** deliberately a *sibling seam*, not a merge.
`IRadioControl` is the packet-medium seam (RSSI/DCD/PTT for CSMA on channelised PMR radios);
`IRigControl` is the station-control seam (QSY/mode/TX-health for CAT transceivers). They share
the capability-flag pattern. A node-side bridge (e.g. an `IRigControl`-backed PTT/DCD source,
hamlib `\get_dcd` exists) is future work and would be the *third* data point OQ-011 wants
before freezing `IRadioControl`'s frequency members. Packaging is dependency-free specifically
so non-PDN consumers can take `Packet.Rig*` without the AX.25 stack.

### Error taxonomy

`RigException` base → `RigConnectionException` (link down; backends self-heal by
redialling on the next command), `RigTimeoutException` (reply budget blown; connection dropped
so a late reply can't desync the stream), `RigCommandException` (backend said no; carries the
native code — hamlib RPRT or XML-RPC fault), `RigProtocolException` (unparseable reply).
Timeouts run on injected `TimeProvider` clocks (§2.7: no wall-clock; `FakeTimeProvider` in
tests via `CancellationTokenSource(delay, timeProvider)`).

## 3. Spike evidence (rigctld 4.5.5, this container)

Live transcripts driving `rigctld -m 1` over netcat (abridged; these shaped the parser and are
baked into the parser tests):

```
+f                → get_freq:\nFrequency: 14074000\nRPRT 0
+m                → get_mode:\nMode: USB\nPassband: 2400\nRPRT 0
+F 7074000        → set_freq: 7074000\nRPRT 0
+l SWR            → get_level: SWR\n0.000000\nRPRT 0        (bare value — no "Key:" label)
\chk_vfo          → 0                                        (one line, NO RPRT — ever)
+\chk_vfo         → ChkVFO: 0                                (echo form, still no RPRT)
+\set_ptt 1       → set_ptt: 1\nRPRT -1                      (dummy has no PTT device: EINVAL)
t                 → RPRT -11                                 (RIG_ENAVAIL)
M FM 0            → passband becomes 15000 (FM default);  M USB -1 → passband unchanged
l ?               → PREAMP ATT … SWR ALC STRENGTH RFPOWER_METER RFPOWER_METER_WATTS …
V VFOB then v     → Sub                                      (VFO naming is loose — don't over-model)
```

Findings that drove design: state lives in rigctld (reconnect is free); extended protocol is
the only dialect with deterministic reply termination; passband `0` = mode default / `-1` =
no-change; `PKTUSB` etc. accepted; VFO tokens are backend-mapped (v0 stays on the current VFO).

## 4. Testing strategy (the "existing harnesses" question, answered)

Surveyed how everyone tests rig-control clients (hamlib's own suite, WSJT-X, Go/Rust/Python/
Node clients, 2025-26 cohort):

1. **The real daemon with the dummy rig IS the ecosystem's mock.** `rigctld -m 1` serves a
   stateful fake rig; hamlib's own pytest suite runs against it; nobody maintains a standalone
   mock rigctld. Fresh state (145 MHz/FM/VFOA) is stable 4.3→master; `--set-conf=static_data=1`
   pins the simulated meters (RFPOWER_METER 0.5, WATTS 50.0; SWR reads 0 — not simulated).
   ⇒ `RigctldInteropTests`: spawn `rigctld -m 1` on a free port (plus a `--vfo` variant),
   `SkippableFact`-skipped when the binary is absent, in the default test category (no docker,
   ~fast). Green in this session against 4.5.5; CI runners without hamlib skip cleanly —
   installing `libhamlib-utils` on the runner lights them up.
2. **In-process scriptable fakes for unit tests** (the strongest 2025-26 pattern — rigplane's
   `fake_rigctld.py`, rigproxy's fake conn): `FakeRigctld` (real TCP, scripted state + fault
   injection: RPRT errors, swallowed replies, mid-command disconnects, vfo-mode, caps
   shaping) and `FakeFlrigHandler` (an `HttpMessageHandler` — no sockets at all; flrig has no
   headless mode so faking the server is the established approach, per Wizkers precedent).
3. **Recorded-transcript parser tests**: parser units are fed verbatim wire text captured from
   the real daemon in this session's spike (the technique gps-dashboard documents). This is
   what caught the banker's-rounding SWR divergence.
4. **Hamlib's per-rig simulators** (`simulators/simts590.c` etc., pty-based) exist but are
   build-tree-only developer tools, never packaged — not usable for us in CI. Noted for
   completeness.

## 5. Follow-ups (named, not started)

- **Node integration**: a `rig:` binding on PDN ports/config, a poller (rigproxy's
  typed-poll-with-auto-demotion model is the one to copy), `/api/v1` surfacing, then UI. This
  unlocks Phase 10's frequency-agile workstream (QSY across a plan; HF packet).
- **`IRadioControl` bridge**: expose an `IRigControl` rig's PTT + `\get_dcd` as the packet
  stack's carrier-sense/PTT seam — OQ-011's third data point.
- **TCI backend** (`Packet.Rig.Tci`) if SDR (Thetis/ExpertSDR) users appear — push telemetry
  incl. SWR/fwd/rev power would make TX-health monitoring event-driven.
- **rigctld-emulator compat pass**: wfview/SDR++/GQRX speak subsets (some may lack the extended
  protocol / `\dump_caps`); if real-world use hits gaps, that's a named-flag/options moment
  (spec-vs-pragmatism discipline), plus interop tests against those emulators.
- **hamlib UDP-multicast listener**: optional push state channel (4.6+, experimental upstream).
- **Interop-tier hardening**: pin a rigctld version in the docker interop stack if version skew
  ever bites (the protocol has been stable since 4.0, so deferred).
- **Meter semantics niggle**: dummy-rig SWR reads 0.0 (unsimulated); real rigs read ~1.0 idle.
  Callers averaging meters should sample only while `GetPttAsync()` is true.

## 6. Sources

Primary: hamlib source (rigctl_parse.c, rigctld.c, netrigctl.c, dummy.c, flrig.c, rig.h, NEWS,
man pages) at tags 3.3/4.0/4.5.5/4.6.5/4.7.2/master; flrig XML-RPC method table (w1hkj);
OmniRig.ridl (VE3NEA GitHub); live rigctld 4.5.5 spike in this container. Ecosystem surveys
(NuGet/GitHub/PyPI/crates.io/npm, 2026-07-13) summarised in §1/§4; notable references:
ftl/rigproxy (Go), rigplane fake_rigctld, cloudlog-helper (C#), SkyCAT/SkyRoof (VE3NEA),
FT891-Interface simulator pattern, tuxlink's CI dummy-rig smoke test.
