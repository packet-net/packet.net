# Strict-vs-pragmatic audit (2026-05-14)

## Why

Packet.NET's philosophy: **spec-compliant by default; pragmatic
accommodations for real-world peers are named opt-in flags in code,
not silent defaults**.

This document inventories every place where the current code accepts
something the strict spec wouldn't require. Each row will be lifted
into a named flag on a `…ParseOptions` record. Defaults stay where
they are today (lenient) — the change is that each accommodation
becomes discoverable, individually toggleable, and documented.

Outcome of this audit feeds the design of `Ax25ParseOptions` /
`AprsParseOptions` and the per-implementation presets (`Strict`,
`Bpq`, `Xrouter`, `Direwolf`, `AprsIs`).

## Packet.Core

Single pragmatic choice. The CRC and the bulk of `Ax25Address` are
spec-pure.

| Location | What we accept | Strict spec says | Driver | Flag name | Default |
|---|---|---|---|---|---|
| `Callsign.cs:30` constructor | `Base.Length` 0–6 | §3.12 "the call sign is made up of upper-case alpha and numeric ASCII characters only" — singular *characters*, plural, implying ≥1. §6.1.1 "operation with destination addresses other than actual amateur call signs is a subject for further study" acknowledges it happens but doesn't bless it. | BPQ's `>IS` ID beacons + PD4R-12 QRV broadcasts (all-space wire slots). PR #85 / 42 frames in a 36 h corpus. | `AllowEmptyCallsignBase` | `true` |

`Callsign.Parse` / `Callsign.TryParse` stay strict by design — they
parse user-typed input where empty is a typo. The flag only affects
the wire-parse path through `Ax25Address.Read`.

## Packet.Ax25

The frame parser is mostly strict. One genuine pragmatic
accommodation, plus a couple of borderline interpretation choices and
some gaps in spec-enforcement that aren't *pragmatic* per se but
should be tracked.

### Pragmatic accommodations

| Location | What we accept | Strict spec says | Driver | Flag name | Default |
|---|---|---|---|---|---|
| `Ax25Frame.cs:344–348` `TryParse` else-branch | Capture trailing bytes as `Info` on **any** non-I/UI frame, including S frames | §3.5 "The Information Field follows the Control Field in I-frames, and the Control or PID field in UI, FRMR, XID and TEST frames." S frames are not in that list — they have no info field. | Defensive: corrupted S frames sometimes carry trailing bytes; FRMR/XID/TEST legitimately have info. Capturing-all sidesteps "which U-frame is allowed an info field" classification. **Note (2026-06-02):** accepting the info at parse no longer means silently processing the frame as valid — `Ax25FrameClassifier` now classifies an info-bearing S frame or no-info U frame (SABM/SABME/DISC/UA/DM) as `InfoNotPermittedInFrame` (DL-ERROR M), so the figc4.x "information not permitted" error-input transition fires at the data-link layer. Strict still rejects the frame outright at decode. | `AllowInfoOnSupervisoryFrames` | `true` |
| `Ax25Frame.cs` `TryParse` (pre-construct guard) | A command-only U-frame (SABM/SABME/DISC) whose address C-bits don't mark it a command (`!IsCommand`) | §4.3.3.1 / §6.1.2 — SABM, SABME and DISC are *always* commands | Legacy AX.25 v1.x peers predate the v2.0 command/response C-bit encoding, so their connect/disconnect frames don't set the v2.2 command bits; rejecting by default would break v1.x interop. Strict drops such a frame at decode so a bogus-direction SABM can never open a session. (#142, 2026-06-10) | `AllowCommandFrameAsResponse` | `true` |

### Spec interpretations (not pragmatic)

| Location | Choice | Why it's not a flag |
|---|---|---|
| `Ax25FrameClassifier.cs:79` returns `ControlFieldError` for unknown control bytes | The spec mandates a response (FRMR per §4.3.3.9), not a parse rejection. Emitting an event is the correct way to surface this to the session machine, which then sends FRMR. | Spec-compliant. |
| `Ax25Frame.IsUi` / `PollFinal` / `IsCommand` / `IsResponse` mask the right bits per §4.2 / §6.1.2 | Per spec; the masking is the decoding rule, not pragmatism. | — |

### Missing strictness (not pragmatism; under-enforcement)

These aren't pragmatic *choices* — they're checks the parser could be
making but isn't. Worth tracking separately so we don't conflate
"deliberately accept" with "didn't bother to reject".

| Location | What's missing | Spec ref | Severity |
|---|---|---|---|
| `Ax25Frame.TryParse` | No reject of S-frames that carry a PID byte | §3.5 | Medium — misclassified S would be accepted with a phantom PID. (Connected to `AllowInfoOnSupervisoryFrames` above — same code branch.) |
| `Ax25Frame.TryParse` | No length check ≤256 info bytes on I/UI | §3.5 (default paclen 256) | Low — corpus shows all payloads ≤240B; spec allows negotiated paclen >256 via XID anyway. |
| `Ax25Frame.TryParse` | No mod-128 (SABME-initiated session) support | §4.2.2 | N/A — separate work item, not a flag. |
| `Ax25Frame.Factories.cs` | No reject of SABME construction when caller expects mod-8 context | §4.3.3.2 | Low — never observed in BPQ corpus; SABME is the construction-time concern, not parser. |

### XID information-field codec (`XidInfoField` — §4.3.3.7 / Fig 4.5)

The XID parameter-negotiation TLV codec (added with the v2.2 arc V3, part 1). The encoder (`XidInfoField.Encode`) is unconditionally strict — it never emits a malformed information field. The parser's two leniency knobs live on `XidParseOptions` (defaulted off; `XidParseOptions.Lenient` turns both on). These are *new* acceptance, so the default is strict per the CLAUDE.md rule.

| Location | What we accept | Strict spec says | Driver | Flag name | Default |
|---|---|---|---|---|---|
| `XidInfoField.TryParse` GL check | A Group Length that claims more parameter bytes than the buffer holds (clamp to available) | §4.3.3.7 ¶1021 — GL is the exact parameter-field length | Defensive: a peer that mis-sizes GL or appends garbage past the parameter field. No confirmed corpus yet (XID is BPQ-only on our interop matrix); seeded off until interop verifies a real driver. | `AllowGroupLengthOverrun` | `false` |
| `XidInfoField.TryParse` PI/PL check | A trailing PI with no PL octet, or a PL whose PV runs past the parameter field (take what remains) | §4.3.3.7 ¶1023 — the parameter field is an exact run of complete PI/PL/PV triples | As above — tolerate a truncated trailing parameter. Seeded off until interop verifies a real driver. | `AllowTruncatedParameter` | `false` |

Note: unrecognised PIs, `PL=0` (PV-absent ⇒ default), and the ISO-8885-only Tx variants (PI=5/7) are **not** leniency — §4.3.3.7 ¶1024 mandates skipping unknown PIs and treating an absent/zero-length parameter as "use the current/default value". The strict parser does all of this; it's spec-compliant behaviour, not a flag.

### XID Classes-of-Procedures ABM bit (spec worked-example defect)

| Location | Choice | Why it's not a flag |
|---|---|---|
| `ClassesOfProcedures.ToOctets` puts Balanced-ABM at **bit 0** (half-duplex ⇒ `0x21 0x00`) | Figure 4.5's table and §6.3.2 ¶1077 ("Bit 0 is always a 1") put ABM at bit 0. Figure 4.6's worked example instead shows `0x22 0x00` — it placed the always-1 ABM bit at position 1. (Figure 4.6's HDLC-Optional-Functions field, by contrast, *is* table-faithful: ext-addr=bit7, TEST=bit13, 16-bit-FCS=bit15, sync-Tx=bit17 all land exactly per the Fig 4.5 table.) We follow the normative table/prose, so the ABM bit is 0 and half-duplex encodes `0x21`. | Spec-table-vs-figure contradiction, like the APRS `h`-timestamp row — we follow the table. The duplex selection (bit 5) — the only field a peer reads — is identical either way, so there is no interop consequence. **Upstream**: candidate `packethacking/ax25spec` issue (Fig 4.6 ABM byte should be `0x21`, not `0x22`); flagged for Tom, not filed here. |

## Packet.Aprs

Three pragmatic accommodations, several spec-interpretation choices
(not pragmatism), and one decoder that's entirely spec-strict.

### Pragmatic accommodations

| Location | What we accept | Strict spec says | Driver | Flag name | Default |
|---|---|---|---|---|---|
| `AprsStatusDecoder.cs:55` `Encoding.UTF8.GetString` | UTF-8 multi-byte sequences in status text; invalid bytes get replacement char | APRS101 §16 "any printable ASCII characters except `\|` or `~`" — codes 33–126 only | Chinese-station status beacons emit UTF-8 in the APRS-IS firehose | `AllowNonAsciiStatusText` | `true` |
| `AprsTelemetryDecoder.cs:67–73` | Analog values parsed as `double`; accepts `0`, `184`, `3.2`, etc. | APRS101 §13 "five 8-bit unsigned analog data values (expressed as 3-digit decimal numbers in the range 000–255)" | ~30% of corpus telemetry is variable-width int or float (SimplexLogic, RepeaterLogic, BPQ etc.) | `AllowNonIntegerTelemetry` | `true` |
| `AprsMicEDecoder.cs:42` | DTI bytes `0x1C` and `0x1D` in addition to `` ` `` (0x60) and `'` (0x27) | APRS101 §10 lists `` ` `` and `'` (and notes `0x1C` / `0x1D` are "Rev. 0 beta units only") | Early Kenwood firmware still exists on the air | `AllowMicELegacyDtiBytes` | `true` |

### `AprsCallsign` — by-design permissive type

| Location | Behaviour |
|---|---|
| `AprsCallsign.cs` | Accepts 1–9 char bases (vs strict 1–6), letter SSIDs (`-R`, `-D`, `-B`, etc.), lowercase, all-numeric. Used at the **monitor / display** layer for the ~31% of APRS-IS traffic that doesn't fit `Callsign`. Outbound frame construction still uses strict `Callsign`. |

`AprsCallsign` is the existing precedent for the "strict outbound /
permissive inbound" split. It's not a pragmatic *flag* — it's a
parallel type. No retrofit needed; the type itself documents its
intent.

### Spec interpretations (not pragmatic)

| Location | Choice | Why it's not a flag |
|---|---|---|
| `AprsPositionDecoder.TryParseLatitude/Longitude` accept ASCII space in minute/hundredth positions | APRS101 §6 explicitly defines position ambiguity via spaces. Our acceptance is the spec-defined feature. | Spec-blessed. |
| `AprsObjectDecoder` accepts `h` HMS timestamp | APRS101 §11's diagram column header says "Time DHM/HMS"; the prose says "DHM only" but is contradicted by the format diagram. Real-world frames use `h`. | Spec-table says it's legal; prose disagrees. We follow the table. |
| `AprsTelemetryDecoder` optional comma after `MIC` sequence | APRS101 §13 prose: "there may or may not be a comma preceding the first analog data value" | Explicitly spec-blessed both ways. |
| `AprsMicEDecoder` SP+28 / DC+28 dual encodings | APRS101 §10 documents both "old" (non-printable prefix) and "new" (printable prefix) encodings; the §10 worked example shows the unified decode rule. | Spec-blessed both. |

### Decoders that are spec-strict

- `AprsMessageDecoder` — no pragmatic choices found.
- `AprsItemDecoder` — strict spec, name-length window `[3, 9]` per §11.
- `AprsObjectDecoder` apart from the `h` timestamp (which is an
  interpretation, not pragmatism).

## Packet.NetRom (full vanilla L3+L4 stack)

NET/ROM has **no single normative standard** — the closest thing to
canonical is the original protocol appendix, and in practice
**G8BPQ/LinBPQ is the de-facto reference** (XRouter and the Linux
kernel `netrom` family diverge). So this is the trap the project's
whole "don't take an implementation as the spec" discipline exists for.
`Packet.NetRom` is faithful to the canonical appendix by default, and
every accommodation is a named flag/knob — `NetRomParseOptions` for the
wire parse, `NetRomRoutingOptions` for route maintenance, and
`NetRomCircuitOptions` for the L4 transport — never a silent
BPQ-/XRouter-ism. Default-on parse flags are chosen because NODES ingest
is promiscuous read-only consumption of third-party broadcasts, where
resilience beats rejecting a stray byte; the **outbound** paths (NODES
origination via `NodesBroadcastBuilder`, the L4 circuit datagrams) stay
strictly spec-faithful — we never emit a frame that violates the
canonical format.

### Pragmatic accommodations (wire parse — `NetRomParseOptions`)

| Location | What we accept | Strict (canonical appendix) says | Driver | Flag name | Default |
|---|---|---|---|---|---|
| `NodesBroadcast.TryParse` | A routing-info region whose length isn't an exact multiple of 21 bytes: parse the whole entries, ignore a short remainder | Canonical NODES dumps emit only whole 21-byte entries; a remainder is trailing pad or truncation | Real nodes (BPQ included) pad the final UI frame of a multi-frame dump; a noisy RF link clips the tail. Dropping every learned route over a stray tail is hostile | `AllowTrailingPartialEntry` | `true` |
| `NodesBroadcast.TryParse` | A broadcast carrying zero destination entries (signature + 6-byte sender alias only, info field exactly 7 B) | The appendix frames the entry list as "repeated up to 11 times" — zero is in range, but a contentless broadcast carries no routes | XRouter (PN0XRT, the interop bed's reference node) broadcasts a **header-only** NODES on its cadence regardless of table contents — observed on the wire | `AllowEmptyDestinationList` | `true` |

The parameterless `TryParse` overload uses `NetRomParseOptions.Lenient`
(both flags on) for promiscuous ingest. `NetRomParseOptions.Strict`
turns both off (whole entries + at least one entry required). `Bpq`
and `Xrouter` presets currently equal `Lenient`; XRouter's notable
divergence is the **quality** it advertises (its RTT→quality is
deliberately lower — the "British notion of quality"), which is a
routing-table concern, not a wire-parse one — the bytes parse the same.

### Route-maintenance knobs (not pragmatism — configurable canonical defaults; `NetRomRoutingOptions`)

These exist because the canonical *defaults* are agreed (OBSINIT 6,
three routes/destination, the multiplicative quality formula) but the
*floors and caps* vary per real node (BPQ's per-port MINQUAL, XRouter's
lower qualities). They default to the canonical/most-interoperable
value and are operator-overridable via the node's `netRom:` config:
`DefaultNeighbourQuality` (192), `MinQuality` (0 — keep everything above
zero), `ObsoleteInitial` (6), `ObsoleteMinimum` (4 — OBSMIN, the
advertise-gate: a faded route stops being advertised before it is purged
at 0), `MaxRoutesPerDestination` (3), `MaxDestinations` (1024 — a
memory-safety cap, not a NET/ROM concept). The trivial-loop guard
(advertised best-neighbour == us → quality 0 → never kept) and the
"quality-0 is never usable" rule are correctness features, always on,
independent of the floor.

### L4 transport knobs (not pragmatism — configurable de-facto defaults; `NetRomCircuitOptions`)

The L4 circuit transport (`NetRomCircuit` / `CircuitManager`) is
hand-written (no SDL, no normative state machine) with a textbook
sliding-window FSM. The canonical *message set* (the six opcodes + the
choke/NAK/more-follows flags) is universal and not configurable; the
*timers, window, retries, and TTL* come from the de-facto reference
(BPQ's `L4*` knobs / the Linux `transport_*` tunables) and so are named
knobs defaulted to a widely-interoperable value, operator-overridable via
the node's `netRom:` config: `WindowSize` (4 — `L4WINDOW`; negotiated
down to the responder's ceiling), `RetransmitTimeout` (5 s — `L4TIMEOUT`),
`MaxRetries` (3 — `L4RETRIES`), `TimeToLive` (25 — `L3TIMETOLIVE`, the L3
hop limit), `FragmentSize` (236 — the canonical 256 − 20 header maximum),
`ChokeThreshold` (0 — the receiver self-chokes only when a host can stall
its reader; the node bridge drains synchronously). The 8-bit sequence
space (window ≤ 127) is canonical, not a knob.

### Connect Request info field — canonical layout, wire-verified vs LinBPQ (`ConnectRequestInfo`)

The one transport message with a structured info field is the **Connect
Request** (opcode 0x01): it carries the *proposed send-window* and the
*originating user + originating node* callsigns end-to-end. The canonical
NET/ROM appendix puts all three in the **info field** — `[window:1]
[origuser:7][orignode:7]` (15 octets), the callsigns AX.25-shifted — and
the 5-octet transport header's TX/RX-sequence slots are 0 on a connect.
This is **not pragmatism**: it is the spec-faithful, single, canonical
layout, with one source of truth (`ConnectRequestInfo.Build`/`TryParse`)
used by both the sender (`NetRomCircuit.SendConnectRequest`) and the
inbound demux (`CircuitManager.MintInbound`).

It is recorded here because it was **verified on the wire against a real
LinBPQ 6.0.25.23** (the #308 interop follow-up; now asserted frame-perfectly
over AXUDP in `NetRomL4CircuitViaAxudp` — the original net-sim
`NetRomL4CircuitViaNetsim` was re-homed to the modem-less Tier-2 transport,
see docs/plan.md §7) and because it *corrected an earlier divergence*:
`NetRomCircuit` had
placed the proposed window in the transport-header TX byte (with the
callsigns at info offset 0). BPQ originates `[window][user][node]` + a
2-octet BPQ extension (a real `PN0TST` connect on the wire was `04
A09C60A8A6A860 A09C60A8A6A860 3C00`) and reads the same shape inbound; it
*accepted* our old framing (the mis-placed window only mis-set the
negotiated window), but we mis-read BPQ's originating user (empty) and
proposed window (0 → default) until the fix. `TryParse` tolerates a peer's
trailing extension octets beyond the canonical 15 (BPQ's `3C00`).
Construction stays strict — we always emit the canonical 15-octet form.

### TX-bearing behaviours are opt-in (safe-by-default, not pragmatism)

NODES origination (`netRom.broadcast`) and L4 connect-routing
(`netRom.connect`) default **off**. This is not a spec accommodation — a
stock node *hears* NET/ROM (the read-only table, on by default, harmless)
but does not *transmit* NODES or open interlinks/circuits until the
operator opts in. Transmitting on a shared RF channel and opening
sessions to neighbours are operator decisions; defaulting them off keeps
a freshly-installed node from injecting traffic onto the air or the
network without intent.

### Interop fixture (de-facto, documented — not baked into the library)

The interop bed nudges the reference nodes to broadcast NODES promptly
for the read-only ingest test, in the docker fixtures (not the library):
`docker/xrouter/XROUTER.CFG` and `docker/linbpq/bpq32.cfg` both pin
`NODESINTERVAL=1` (the 1-minute minimum). With it pinned, XRouter
broadcasts on a steady ~75 s cadence (measured); LinBPQ broadcasts only
when its table is non-empty, which on the deliberately-isolated netsim
topology it never is, so the test asserts the reliable XRouter ingest
and records BPQ opportunistically. This is fixture tuning, openly
documented in those files — the parser is not specialised to either node.

## Summary table: flags × presets

The presets we agreed to seed are `Strict`, `Bpq`, `Xrouter`,
`Direwolf`, and `AprsIs`. Mapping the four flags found above:

| Flag | `Strict` | `Bpq` | `Xrouter` | `Direwolf` | `AprsIs` |
|---|:-:|:-:|:-:|:-:|:-:|
| `AllowEmptyCallsignBase` | ✗ | ✓ | ? | ? | ? |
| `AllowInfoOnSupervisoryFrames` | ✗ | ✓ | ? | ? | n/a |
| `AllowNonAsciiStatusText` | ✗ | n/a | n/a | ✓ | ✓ |
| `AllowNonIntegerTelemetry` | ✗ | n/a | n/a | ✓ | ✓ |
| `AllowMicELegacyDtiBytes` | ✗ | n/a | n/a | ✓ | ✓ |

`?` = not yet verified for that implementation (needs a targeted
corpus or a test against the live container before we commit).
`n/a` = flag doesn't apply to that layer (e.g. the BPQ AX.25-layer
preset doesn't care about APRS status text encoding).

`Xrouter` is mostly TBD — we don't have a corpus of frames emitted
by Xrouter yet. Seed it as a copy of `Strict` and let it grow as we
discover specific quirks during interop testing.

## Node surface: per-port `compat:` (#366, 2026-06-10)

The AX.25-layer flags and presets above are **operator-reachable at the node** via a per-port `compat:` block in the node config (and a preset dropdown in the web UI's port editor): `compat.preset` names one of `strict | lenient | bpq | xrouter | direwolf`, the three AX.25 flags (`allowEmptyCallsignBase`, `allowInfoOnSupervisoryFrames`, `allowCommandFrameAsResponse`) can be overridden individually on top of the preset (explicit wins), and `compat.quirks` selects the `Ax25SessionQuirks` set (`default | strictly-faithful`) for sessions on that port. Absent `compat:` = `Lenient` + `Default` — the node's historical behaviour. The mapping authority is `Packet.Node.Core.Configuration.Ax25CompatPresets`; resolution is per-port so an operator matches each port to its neighbour (a BPQ-facing port runs `bpq`, a clean v2.2 link can run `strict`). A frame the resolved options reject is dropped before the monitor trace and session dispatch — a strict port is deaf to it end-to-end. Changes apply live (no port restart): parse options from the next inbound frame, quirks for newly-built sessions.

## What's not in scope of this audit

- The session machine (`Packet.Ax25.Session`) — strictness questions
  there belong to the dispatcher / SDL transcription work, not to
  parsing. Re-audit once figc4.7 is transcribed.
- The KISS layer (`Packet.Kiss`) — the framing is a transport
  protocol, not AX.25. ACKMODE / multi-drop KISS extensions are
  separately specified (`docs/plan.md` references).
- Outbound frame construction — `Callsign` strict constructor /
  `Ax25Frame.Ui()` etc. are the production path; we want these to
  stay strict so we never produce non-spec frames.

## Next steps

1. **Options shape** (next PR): introduce `Ax25ParseOptions` and
   `AprsParseOptions` records with the flags listed above, plus the
   five preset static factories. Add the types but don't wire them
   in yet so the PR is small and reviewable.
2. **Retrofit `Packet.Core`**: lift `AllowEmptyCallsignBase` into
   `Ax25Address.Read(span, options)` and have the parameterless
   overload use the current lenient default.
3. **Retrofit `Packet.Ax25`**: lift `AllowInfoOnSupervisoryFrames`
   into `Ax25Frame.TryParse(span, options, out frame)`.
4. **Retrofit `Packet.Aprs`**: the three flags above + thread
   options through `TryDecode` overloads on each decoder.
5. **Verify the `?` cells**: once `Xrouter` and `Direwolf` AX.25-layer
   coverage is on, run targeted tests to populate the
   `AllowEmptyCallsignBase` and `AllowInfoOnSupervisoryFrames`
   columns for those presets.
6. **CLAUDE.md update**: codify the rule "when accepting something
   the strict spec doesn't require, surface a named flag — don't
   silently widen the parser".

## Session quirks (SDL-figure deviations — `Ax25SessionQuirks`)

Distinct from the wire-parse pragmatism above: these are *session-layer* deviations from the AX.25 SDL **figures**, used where a figure is a confirmed upstream spec **defect**. The SDL tables (`Packet.Ax25.Sdl`, from `packet-net/ax25sdl`) stay faithful to the published figures — defects and all — so the canonical transcription tracks the in-progress draft; the runtime corrects provable figure errors here, behind named flags. Each flag is named `Ax25Spec<issue>…` after the `packethacking/ax25spec` issue it works around (greppable, removable once the spec is fixed). The default preset is spec-correct (quirks on); `Ax25SessionQuirks.StrictlyFaithful` runs the figures exactly as drawn for conformance testing.

| Flag | What the figure draws | Correct behaviour | Evidence | Default | Remove when |
|---|---|---|---|---|---|
| `Ax25Spec38SrejSelectiveRetransmit` | figc4.5 SREJ-received: generic "Push frame onto queue" + go-back-N "Invoke Retransmission" | single-frame selective retransmit (figc4.4 "Push Old I Frame N(r) on Queue", no Invoke Retransmission). Scope: fires only on the SREJ *response* paths (which carry the push); the SREJ *command* paths carry only Invoke Retransmission, so under the quirk they retransmit nothing — correct, since SREJ is response-only per §4.3.2.4 and no deployed stack sends/acts on a command-form SREJ (packet.net#234). | §4.3.2.4/§6.4.8 + figc4.4 + all four surveyed impls (direwolf/linbpq selective; linux/rax25 none) + direwolf author's "2006 cut-n-paste" erratum; direwolf "Command path has been omitted because SREJ can only be response" (ax25_link.c) + linbpq `if (MSGFLAG & RESP)` (L2Code.c) for the response-only point. Upstream: ax25spec#38; engages-end-to-end + paced-convergence verified in packet.net#233. | `true` | ax25sdl ships a corrected figc4.5 (tracked: packet.net#227) |

(The figc4.4/4.6/4.7 figure-defect quirks `Ax25Spec40/41/42/43/44/45` follow the identical pattern and live on the same `Ax25SessionQuirks` record — see the XML docs there and the §17 amendment log; not all are tabulated above.)

## De-facto-interop session quirks (wire-format, not figure defects — `Ax25SessionQuirks`)

A second flavour of `Ax25SessionQuirks` flag: where the published spec is genuinely **ambiguous or silent** about a wire format and a single real implementation establishes the de-facto answer. These are **not** tied to a filed `ax25spec` figure-defect issue, so they do **not** take the `Ax25Spec<NN>` prefix — name them descriptively. Like the figure-defect quirks they default **on** (interoperate out of the box) and turn **off** under `Ax25SessionQuirks.StrictlyFaithful` (reproduce the spec-literal reading).

| Flag | Spec-literal reading | De-facto (default-on) behaviour | Evidence | Default | Notes |
|---|---|---|---|---|---|
| `SegmentFirstCarriesL3Pid` | §6.6 segmentation, AX.25 v2.2 Figure 6.2: a segmented I-frame's info field is the 0x08 segmented-PID octet + a single `FXXXXXXX` F/X octet, then data — **no field for the original Layer-3 PID**, so figure-literal reassembly delivers `PidNoLayer3` (0xF0) and the L3 PID is lost across the series. | The **first** segment carries an extra **inner-PID octet** (the original L3 PID) between the F/X octet and the data — `[F/X][inner-PID][data]` first, `[F/X][data]` subsequent; the inner octet counts toward the budget (first segment holds N1−2 data, subsequent N1−1, via `DIVROUNDUP(len+1, N1−1)`). The reassembler reads it back and delivers the payload with its **original L3 PID**. Interoperates with Dire Wolf out of the box *and* fixes the figure-literal PID-loss limitation. | §6.6 prose ("a two-octet header") admits both readings; **Dire Wolf (WB2OSZ) — the only known v2.2 segmenter** — takes the inner-PID reading (`ax25_link.c` `dl_data_request` ~L1330–1410 first-segment `[F/X][original_pid][data]` + `dl_data_indication` reassembler ~L2010–2030 `ra_buff->pid = data[1]`). Verified byte-exact against its own worked example (N1=4, "ABCDEF", PID 0xF0 → `82 F0 41 42` / `01 43 44 45` / `00 46`) **and** on the wire round-trip both directions via the #177 docker stack (`DirewolfMod128Interop` case d). | `true` | Not a figure-defect — no `ax25spec` issue filed. The underlying spec gap (Figure 6.2 / §6.6 two-octet header drops the L3 PID; Dire Wolf fills it non-standardly) is a candidate ax25spec clarification (Tom's call). `StrictlyFaithful` = off → figure-literal `[F/X][data]`, reassembled as `PidNoLayer3`. |

## AXUDP / AXIP-over-IP FCS framing (transport-config, not parser-leniency — and no longer a *choice*)

Strictly this is a **transport-config** matter, not an `Ax25ParseOptions`/`AprsParseOptions` parser-leniency one (the AX.25 parser never sees the FCS — `AxudpSocket` strips it before the listener parses), so it lives in the AXUDP code + interop tests, not in a `…ParseOptions` flag. It is captured here because the **de-facto-survey discipline is exactly the same** (§2: "any implementation is an interop target, not reference truth; survey the real ones"), and the surrounding docstrings point here for the citation trail.

**There is no strict-vs-pragmatic *choice* left here: AXUDP unconditionally carries the 2-octet AX.25 FCS.** This section is now a record of *why* the FCS is mandatory (the de-facto survey below), not of a toggle. The history: PR #299 added the AXUDP transport (`AxudpKissModem` over `AxudpSocket`) defaulting **FCS-off** "for a pdn↔pdn tunnel", on a (wrong) belief that FCS-less "matches BPQAXIP". #301 source-verified that BPQAXIP/UDP actually *requires* the FCS and fixed the docs. #304 ran the citation survey, found FCS-less matches **no** real implementation, and flipped the default to FCS-on — but kept `IncludeFcs` as a "non-standard pdn↔pdn opt-out". **This PR removed that opt-out entirely**: it interoperated with nothing (a self-only wire format), so it was dead weight and a footgun. `AxudpSocket.SendAsync` now always appends the FCS and `AxudpSocket.ReceiveAsync` always strips + validates it (dropping a bad-FCS datagram, as the real peers do); `AxudpKissModem` and `AxudpTransport` no longer carry an FCS flag.

**Verdict (the survey that justifies "mandatory"): FCS-less was a pdn-invented, self-only wire format. Every real AXIP/AXUDP implementation surveyed mandates the 2-octet AX.25 FCS; none omits it or offers an FCS-less mode.** All surveyed peers compute the **identical** CRC (poly 0x1021, init 0xffff, final ^0xffff, **low byte first** on the wire, good residue `0xf0b8`) — byte-for-byte our `Packet.Core.Crc16Ccitt` — so the FCSes interoperate exactly.

### De-facto survey (the citations)

| Implementation | FCS in the UDP/IP payload? | Configurable? | Default | Citation |
|---|---|---|---|---|
| **RFC 1226** ("Internet Protocol Encapsulation of AX.25 Frames", Kantor, 1991) — the AX.25-over-IP standard | **Included (mandatory).** HDLC flags + zero-stuffing omitted; FCS kept. | No | n/a (normative) | "The 16-bit CRC-CCITT frame check sequence (normally generated by the HDLC transmission hardware) is included." — <https://www.rfc-editor.org/rfc/rfc1226> §"The AX.25…Protocol" |
| **rfc1226-bis** (Learmonth, modern revision draft) | **Included (mandatory).** "encapsulated unaltered" aside from removing HDLC framing. No omit option. | No | n/a (normative) | "The CRC-16-CCITT frame check sequence (normally generated by the HDLC transmission hardware) is included trailing the information field." — `draft-learmonth-intarea-rfc1226-bis-00` |
| **ax25ipd** (the classic Linux AXIP daemon, `ax25-apps`) — *the key data point* | **Included (mandatory), both directions.** TX appends unconditionally; RX drops a datagram whose FCS residue ≠ `0xf0b8`. | **No** — there is no `crc` config keyword (route flags are only `b`/`d`); the FCS is hard-coded. | FCS-on (only mode) | `process.c`: `from_kiss()` calls `add_crc(buf,l)` unconditionally (L113/L124) before `send_ip`; `from_ip()` does `if (!ok_crc(buf,l)) { …dumped - CRC incorrect!…return; } l -= 2;` (L154-159). `crc.c`: `compute_crc` init `0xffff` ^`0xffff`; `ok_crc` checks `== 0xf0b8` (PPPGOODFCS). Comment L48: "the AX25 frame from kiss does not include the CRC bytes. These are computed by this routine". README/RFC1226 embed: "The standard CRC is computed, tacked onto the [frame] … frames … from the IP interface have the CRC checked and removed." (`eblanton/ax25-apps` @ 52056a0; man page `ax25ipd.conf(5)` route flags = `b`,`d` only — <https://www.mankier.com/5/ax25ipd.conf>) |
| **LinBPQ BPQAXIP** (over UDP) | **Included (mandatory), both directions.** | **No** — no per-MAP "no CRC" knob. | FCS-on (only mode) | `bpqaxip.c` (LinBPQ 6.0.25.23): UDP RX `crc = compute_crc(&rxbuff[0], len); if (crc == 0xf0b8) {len-=2;…} else …"BPQAXIP Invalid CRC"` (L498-576); proto-93 RX identical at offset `[20]` past the IP header (L395-461); TX `crc=compute_crc(&buff->DEST[0], txlen-2); crc ^= 0xffff; …[low][high]` (L619-623). Confirmed on the wire in `LinbpqViaAxudpConnectedMode` (#301). |
| **XRouter** (G8PZT) AXUDP | **Included (required).** | (Unknown knob; treats FCS-less as "non-AXUDP".) | FCS-on | "XRouter requires FCS on AXUDP frames ('AXUDP with CRC' per its docs). Frames without a trailing CRC-16 are counted as 'other non-AXUDP ignored'." (plan §17 2026-05-12; on-the-wire `XrouterAxudpInterop.Sends_A_UI_Frame_With_Fcs…` — FCS-less rejected, FCS-bearing counted "valid AXUDP received"). |
| **JNOS** axip/axudp | **Included** (RFC-1226-based encapsulation; `attach axip|axudp`). | — | FCS-on | RFC-1226 lineage; AX.25-HOWTO §"AX.25 over IP" — <https://tldp.org/HOWTO/AX25-HOWTO/x2141.html> |
| **Linux kernel `ax25_ip.c`** | n/a — it's **IP-over-AX.25** (the reverse: IP packets inside AX.25 UI frames, ETH_P_AX25), *not* AXIP/AXUDP. AXIP framing on Linux is done in userspace by `ax25ipd`, not the kernel. | n/a | n/a | `linux .../net/ax25/ax25_ip.c` `ax25_hard_header` — "Shove an AX.25 UI header on an IP packet". |
| **Dire Wolf / soundmodem** | n/a — **no AXIP/AXUDP at all.** Dire Wolf speaks KISS (serial/TCP) + AGW only; not an AXIP peer. | n/a | n/a | `ax25-impls/direwolf/src` — no `axip`/`axudp`/`protocol 93`/`rfc1226` references (exhaustive grep). |
| **pdn (`Packet.Axudp`/`AxudpKissModem`)** | **Included, unconditional.** The FCS is part of the wire format — there is no FCS-less form (the self-only opt-out was removed). | **No** — no knob; the FCS is always present, matching every real peer. | FCS-on (only mode) | `AxudpSocket.SendAsync` always `frame.ToBytesWithFcs()`; `AxudpSocket.ReceiveAsync` always strips + validates (drops a bad-FCS datagram); `AxudpKissModem` / `AxudpTransport` carry no FCS flag. |

### Removal of the FCS-less opt-out

There is no flag here any more. #304 left `IncludeFcs` (default `true`) as a "non-standard pdn↔pdn opt-out"; this PR **removed it** because the survey above is conclusive that the FCS-less form interoperates with **nothing** — even a symmetric pdn↔pdn tunnel gains nothing from omitting the FCS, while the flag is a footgun (flip it and you silently stop talking to every real peer, and an unstripped FCS tail makes `Ax25Frame.TryParse` reject an S-frame's RR/RNR ack and break connected mode). Per §2.5 (no backwards-compatibility ballast until v1) the removal is clean: the `IncludeFcs` field, the `AxudpKissModem`/`AxudpSocket.SendAsync` ctor/send params, and the YAML `includeFcs:` key handling are all gone. A pre-removal config carrying a stale `includeFcs:` key still loads — the transport YAML converter reads only the fields it knows, so the stale key is ignored, not a parse error (pinned by `NodeConfigYamlTests.Axudp_tolerates_a_stale_includeFcs_key_from_a_pre_removal_config`).

The FCS-always behaviour is pinned by: `AxudpSocketTests` (send always appends the FCS low-byte-first; receive strips + validates; a bad-FCS datagram is dropped), `AxudpKissModemTests` (the adapter always emits the FCS and surfaces a clean FCS-stripped body), the pdn↔pdn `AxudpNodeToNodeIntegrationTests` (FCS now unconditional, RR/RNR acks survive the strip), and the on-the-wire BPQ guard `LinbpqViaAxudpConnectedMode.FcsLess_Sabm_IsDropped_FcsBearing_Sabm_GetsUa` (`Category=Interop`: an FCS-less datagram gets no reply from BPQ, an FCS-bearing one gets a UA).
