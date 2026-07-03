# Radio side channels and mode agility — how the SDM mode-coordination seed feeds Phase 10

*2026-07-03. Design note for the §5.10 mode-agility workstream. Status: the primitives below
(`IRadioSideChannel`, the mode-coordination protocol, the `packet-tune mode-coord` CLI) are
shipped and hardware-validated on the bench rig; **none of this is implemented in
`Packet.Node` yet** — this note is the map from the bench seed to the node feature.*

## The problem mode agility has to solve

Phase 10's mode-agility goal (§5.10): two stations negotiate the optimal NinoTNC mode for
current channel quality and renegotiate on degradation. The naive design — negotiate over the
AX.25 link itself — has a chicken-and-egg flaw: the moment both ends switch, the old link is
gone, and if the new mode doesn't decode (wrong bandwidth for the channel, deviation not set
for the direct-FSK path, marginal SNR), *neither end can tell the other*. Worse, a
half-applied switch (one end moved, one didn't) strands the link entirely, and the failure
mode of an automated renegotiator is a dead port at an unattended site.

The bench rig demonstrated the way out: the Tait TM8110s carry **Short Data Messages over
their own internal FFSK modem** — radio-native signalling that bypasses the TNC audio path
entirely. SDM delivery is **mode- and channel-width-agnostic**: it worked identically while
the TNCs sat in dead 9600 GFSK (session 6's honest dead-link capture), across the narrow and
wide channels, and at any deviation-pot position. A control plane that survives arbitrary
misconfiguration of the data plane is exactly what a switch-then-verify manoeuvre needs.

## What shipped (the seed)

Three layers, deliberately separated:

1. **`Packet.Radio.IRadioSideChannel`** — the abstraction: send/receive small datagrams
   through the radio itself, with async over-air delivery confirmation
   (`DeliveryReceipt`) and a `MaxPayloadLength` budget. Drivers advertise the machinery via
   `RadioCapabilities.SideChannel`; the XML docs pin the **capability-gating intent** —
   offering the API ≠ the feature being enabled in the radio's programming, so consumers
   gate on a live probe. `Packet.Radio` has no Tait dependency; the canonical
   implementation is **`Packet.Radio.Tait.TaitSdmSideChannel`** (SDM + PROGRESS 1D receipts,
   32-char budget, 8-char identities). `SdmTuningLink` (the reliable telegram layer with
   receipt-gated retries, seq dedupe, DCD etiquette and the TM8110 auto-ack-wedge guards) now
   rides `IRadioSideChannel`, so any future radio with a similar facility (paging channels,
   FSK subcarrier, even a co-sited LoRa beacon) slots in under the *same* tuning/negotiation
   stack unchanged.

2. **The mode-coordination protocol** (`Packet.Tune.Core`): `MODE` telegrams
   (`propose|confirm|reject|commit|sent|report|revert`, every form inside the 32-char SDM
   budget) driven by `ModeCoordinator`/`ModeResponder` over any `ITuningLink`. The shape that
   matters for the node:
   - **Propose/confirm/commit with delivery receipts.** Nothing switches until the commit's
     over-air receipt proves both ends hold it. The commit telegram's sequence number tags
     the verification probe frames, so stale probes can never validate the wrong attempt.
   - **Verify before trusting**: N tagged probe frames each way; both directions must decode
     ≥ 1 frame or the switch is declared dead.
   - **Revert is the failure mode, and it always works**: both ends return to the session's
     *home* mode/channel — coordinated over the side channel when it reaches the peer,
     backstopped by the **responder idle watchdog** when it doesn't (the one case the side
     channel can't cover is a split *channel*; the watchdog is the recovery for exactly
     that). The home link is then re-verified with a short probe burst.
   - The hardware seam (`IModeCoordStation`) isolates SETHW+16/settle-frame/GO_TO_CHANNEL/
     probe mechanics from the state machines, so the protocol is fully unit-tested without
     hardware — including every revert path.

3. **`packet-tune mode-coord`** — the bench CLI (coordinator/responder roles, `--sequence
   m[@ch],…` / `--sweep`, `--strict-bandwidth`), which is both the hardware validation
   harness and the reference consumer of layers 1–2.

## How this feeds Phase 10 in the node (not yet implemented)

The intended landing, in dependency order:

1. **Side channel on the port** — a Tait-attached port (the existing `radio:` config block)
   whose doctor probe confirms SDM end-to-end gets a side-channel service alongside its
   `RssiTaggingTransport`. Capability gating is the doctor's job: `SideChannel` in
   `RadioCapabilities` says "driver can", the probe (send-to-peer or the tuning doctor's
   existing SDM-accepted check) says "programming does". Ports without a working side channel
   simply never enable side-channel-coordinated features — no degraded half-modes.

2. **Propose/confirm/commit between Tait-attached ports** — `ModeCoordinator`/`ModeResponder`
   move (or are referenced) into node-side services on two peered nodes; the coordinator
   role is taken by whichever end's operator (or policy engine) initiates. The protocol
   already carries channels as well as modes, so the same machinery covers the §5.10
   frequency-agility workstream's programmed-channel case (QSY within the channel plan).
   The node's per-port supervisor must treat a coordinated switch like a KISS-param apply
   (no port restart), and the *home* mode/channel is simply the port's configured mode —
   what the config says is what the watchdog restores, which keeps crash-recovery semantics
   identical to today's.

3. **Periodic mode+quality beacons over the side channel** — the second use of the same
   datagram budget: each end periodically (and cheaply — one SDM a minute is generous)
   announces `mode|channel|quality-summary` (decode rate, RSSI/SNR from the per-frame
   metadata, busy %). This gives both the renegotiation *trigger* (quality degraded below
   the mode's threshold; better mode predicted viable) and the *drift detector* (peer's
   announced mode ≠ ours ⇒ someone rebooted mid-session → re-coordinate or revert). The
   compact-telegram pattern (`TuningTelegram` + budget-aware encoding) is the wire format to
   extend, not replace.

4. **Policy** — last, and gated behind an explicit flag per §5.10's adaptive-parameters
   caution: the quality index chooses a target mode from the surveyed ladder (the mode-survey
   latency/decode tables are exactly the calibration data), `mode-coord` executes it, and
   the revert discipline bounds the blast radius of a wrong prediction to one failed probe
   exchange (~1 minute) before the link is back on its home mode.

## Constraints the node design must carry forward (bench-learned)

- **The TM8110 auto-ack wedge**: never key the TNC while the paired radio's SDM auto-ack may
  be in flight. The guards (2 s post-receive, 2.5 s pre-key) live in `SdmTuningLink` /
  `ModeCoordOptions` and must survive any node-side re-plumbing; recovery is a CCR
  soft reset (`packet-tune radio-reset`).
- **Settle frame**: SETHW applies from the second frame — every mode apply transmits a
  throwaway, which costs airtime and must respect CSMA.
- **Pace**: a receipted SDM exchange costs seconds. A full coordinate-and-verify is tens of
  seconds — fine for renegotiate-on-degradation, not for per-frame adaptation. Beacons and
  triggers must be sized accordingly.
- **The side channel is a control plane.** 32 characters per datagram, one-deep receive
  buffer. Anything chattier belongs on the AX.25 link itself.

## Open questions for the node phase

- Who coordinates when both ends want to? (Lowest callsign wins is probably enough; the
  responder role is safe to hold concurrently with a coordinator role since dedupe is
  per-sender.)
- Multi-peer channels: the bench protocol is point-to-point. A shared channel with three
  stations needs either per-pair sessions or a mode-beacon consensus — out of scope for the
  first node cut (which should target the two-node point-to-point backbone link, the GB7RDG
  use case).
- Whether `ModeCoordinator`/`ModeResponder` migrate into a node-owned package or stay in
  `Packet.Tune.Core` with the node referencing it (they are transport-seamed either way).
- **Session epochs.** Sequence numbers restart with the process, and the link-level dedupe
  window would eat the first telegrams of a restarted coordinator talking to a long-lived
  responder. The bench tools are one-process-per-session by design (same as
  `deviation-sdm`); a long-lived node service needs an epoch/session-id in the telegram
  (a `V2` concern) or a hello-resets-dedupe rule.
