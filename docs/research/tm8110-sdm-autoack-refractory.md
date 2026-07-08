# TM8110 SDM delivery-receipt "wedge" — empirical characterisation

**Status:** characterised & proven on the bench (2026-07-08). Supersedes the earlier
"keying wedges the auto-ack engine" story in `SdmTuningLink` remarks and the
`tait-ninotnc-bench-rig` note — **that story is wrong** (see §Disproven). No fix written yet;
this doc is the proof that licenses the fix.

Rig: 2× Tait TM8110 (CCDI 03.02, s/n 19925328 = radio **A** on `/dev/ttyUSB0`, s/n 19925369
= radio **B** on `/dev/ttyUSB1`), cabled with 100 dB pad, no antenna. SDM identities A=`PDN00002`,
B=`PDN00001`. Harness: `$CLAUDE_JOB_DIR/tmp/wedge` (raw CCDI event logger on both radios —
`ProgressReceived`/`SdmReceived`/`SdmDeliveryReceipt`; `SetTransmitterAsync` for bare-carrier keying).

## The phenomenon

When two radios exchange SDMs, the **payload is delivered every time** (receiver always buffers
it), but the **over-air delivery receipt** (CCDI PROGRESS `1D`, para `1`=ack / `0`=nak) is often
lost: the sender waits out the ~6 s protocol timeout and reports NAK (`para='0'`). This is what
sabotaged the TXDELAY sweep — the coordinator declared "not acknowledged" for telegrams that were
in fact delivered.

## Method — falsify first, one variable at a time

Every radio-reset below is `packet-tune radio-reset` (CCR soft reboot). Sends are timed for the
`SdmDeliveryReceipt` event on the sending radio.

## Disproven hypotheses

| Hypothesis | Test | Result | Verdict |
|---|---|---|---|
| "Keying (data PTT) while an auto-ack is in flight wedges the ack engine" (the documented story) | `keytx`: bare-carrier key A 500 ms, then A→B ×3 | **3/3 ACK** | **FALSE** — generic TX is harmless |
| Full one-deep SDM RX buffer blocks auto-ack | `noclear` (output-off, no q1 read), unidir A→B ×6 | **6/6 ACK** | FALSE — a full buffer does not block acking |
| `SdmOutputOnReception` mode matters | 2×2 {ON,OFF}×{ping-pong,unidir} | ON≡OFF within each direction | FALSE — irrelevant |
| Explicit q1 buffer-read is what re-arms acking | `bufclear` vs `noclear` (both unidir) both all-ACK | | FALSE — the earlier "buffer" reading was a direction confound |
| Idle time recovers it | `primesend`: 1 auto-ack, wait D, 1 send | **NAK for every D up to 20 s** | FALSE for a *first* send — recovery is sequence-driven, not time-alone |

The 2×2 that killed the output-mode/buffer theories (all no-read, `gap=3000`):

| direction | `SdmOutputOnReception` | result |
|---|---|---|
| ping-pong A↔B | ON | 1 ACK then wedge |
| ping-pong A↔B | OFF | 1 ACK then wedge |
| unidirectional A→B | ON | 6/6 ACK |
| unidirectional A→B | OFF | 6/6 ACK |

**Only direction matters.** Unidirectional never wedges (the sender never auto-acks); ping-pong
wedges after the first exchange.

## The proven mechanism — auto-ack TX poisons the same radio's next send-receipt

Isolation (`roleswitch`): make A auto-ack repeatedly (B→A ×3, all ACK — A auto-acks fine), then
have A send to a **provably-healthy B that never auto-acked** (A→B). A NAKs. Since B is healthy,
the NAK can only be **A failing to register B's auto-ack** — i.e. A's *ack-reception* is impaired
by A's own prior *auto-ack transmission*. Symmetric (ab-then-ba identical). Bare keying does **not**
do this (`keytx`), so it is specific to the SDM auto-acknowledge transmission, not to keying.

A radio's SDM send captures its delivery receipt (ACK) **iff both**:

- **(a) latch** — the radio has **not transmitted an auto-ack since its previous SDM send**. The
  *first* send after any auto-ack **always** NAKs (even 12–20 s later) and self-clears the latch.
- **(b) refractory** — **≥ ~9 s** have elapsed since the radio's most recent auto-ack transmission.

Otherwise: NAK after ~6 s, payload still delivered. A reset clears both. `(a)` and `(b)` are
independent — proven by `wait2` below. Both likely stem from the radio holding an SDM/Selcall
"call context" for ~9 s after auto-acking.

### Evidence the model fits every run

| run | setup | sends (time since last auto-ack) | model prediction | observed |
|---|---|---|---|---|
| `primesend` (all D≤20) | 1 auto-ack, 1 send | seq-1 always fails (a) | NAK | **NAK** every D |
| `wait2 12` (×2) | 1 auto-ack, wait 12 s, 2 sends | send1 NAK (a); send2 ACK (a clear, b met) | NAK,ACK | **NAK,ACK** |
| `latch` (×2) | 1 auto-ack, 4 sends @1.5 s | fail (a); fail (b,~7.7 s); ok(~15 s); ok | NAK,NAK,ACK,ACK | **NAK,NAK,ACK,ACK** |
| `latchN` | 4 auto-acks, 4 sends | fail (a); ok (b,~9.7 s); ok; ok | NAK,ACK,ACK,ACK | **NAK,ACK,ACK,ACK** |
| `roleswitch` | 3 auto-acks, 3 sends | fail (a); ok (~12.7 s); ok | NAK,ACK,ACK | **NAK,ACK,ACK** |
| ping-pong | each send preceded by a fresh auto-ack | every send fails (a) | all NAK after 1st | **1 ACK then all NAK** |
| unidir A→B | sender never auto-acks | all ok | all ACK | **6/6 ACK** |

Threshold for (b) bracketed: `latch` send-2 NAK at 7.7 s vs `latchN` send-2 ACK at 9.7 s ⇒ ~8–9 s.
Auto-ack TX raises no PTT progress on the acking radio; it is timed by the *peer* hearing it
(`FfskDataReceived` → `ack=True`), ~1.6 s after the triggering send.

## Why TXDELAY tripped it

The tuning protocols are inherently **bidirectional SDM within a few seconds** (propose→confirm,
step→report). The coordinator auto-acks the meter's replies, so its very next telegram send falls
foul of (a)/(b) → NAK → the link's receipt-based retry declares COORDINATION LOST — even though
every payload arrived. Unidirectional or ≥9 s-spaced SDM would not show it.

## Can we opt out of SDM auto-ack at runtime? No — it is codeplug-only

The CCDI manual (MMA-00038-06) is explicit that SDM auto-acknowledge is a **programming-application
(codeplug) setting**, with **no runtime CCDI control**:

- p31: "if the **'SDM Auto Acknowledge Delay' field is set in the programming application**, the
  radio waits for an acknowledgement before it generates a PROGRESS message… the delay before the
  acknowledgement is sent and how long the radio waits is **also set in the programming application**."
- p45 / p48: the 1D receipt "will **only be generated if the radio has been programmed to transmit
  SDM Auto Acknowledge** in the programming application."
- p34: there are *two independent* codeplug fields — **"SDM Auto Acknowledge"** (receiver transmits
  the ack; this is the refractory trigger) and **"Wait For Acknowledgment"** (sender waits and emits
  1D). Acks are configurable **per SDM type** (Text / GPS), but still in the codeplug.

There is **no `f`-command** to toggle either (the library's SDM FUNCTION toggles are only 1/0
output-on-reception, 1/1 caller-ID encode, 1/2 caller-ID decode), and no per-message
"acknowledgement requested" bit in `SEND_ADAPTABLE_SDM` — the send frame is
`[SIZE][LEAD_IN][GFI][SFI][ID][MSG][CHK]`; ack behaviour is entirely governed by codeplug. So
disabling auto-ack means **reprogramming every radio** (CPS or the codeplug-patch path in
`tait-codeplug-programming-brief.md`).

**Consequence:** PDN adopts *users'* radios whose codeplugs we do not control, so the software must
be correct with auto-ack **ON**. Disabling it cannot be a load-bearing part of the fix.

## Option analysis (three routes Tom raised)

| Route | Removes the refractory? | Keeps CCDI telemetry (DCD/RSSI/PTT/channel)? | Needs reprogramming? | Verdict |
|---|---|---|---|---|
| **A. Keep SDM, make the transport receipt-tolerant** (rely on the peer's app-level reply; payload always delivers) | N/A — refractory becomes irrelevant | ✅ yes (SDM is a CCDI command, port stays in Command mode) | ❌ no | **Recommended.** Works on any radio as-is. |
| **B. Disable SDM auto-ack in the codeplug** (receiver-side) | ✅ yes (no ack TX → no refractory) + saves ack airtime | ✅ yes | ⚠️ **yes, every radio** | Optional optimisation for radios we own (bench, dedicated head-ends); **not** dependable in the field. |
| **C. Switch the coordination link to the Tait FFSK Transparent modem** (`TaitTransparentTransport`, the "other modem" — AX.25 over the radio's own FFSK, no auto-ack) | ✅ yes (raw byte pipe, no ack) | ❌ **NO — Transparent mode makes the serial port a byte pipe; CCDI/PROGRESS is unavailable while active** | ❌ no | **Rejected for tuning.** Tuning's whole job is measuring DCD/RSSI/PTT *while* coordinating; Transparent blinds that and would force slow enter/exit toggling per telegram. Fine as a general data path, wrong tool here. |
| D. Coordinate over the NinoTNC AX.25 link | ✅ | ✅ (CCDI free) | ❌ | **Rejected — chicken-and-egg:** the TXDELAY sweep changes that very link; coordination must be independent of the modem under tune (the reason SDM was chosen). |

**Bottom line.** No modem swap or auto-ack toggle gives a free win: the FFSK Transparent modem
costs the concurrent telemetry tuning depends on, and auto-ack can't be disabled without
reprogramming radios we don't own. The robust, deployable fix is **Route A** — the SDM over-air
delivery receipt is structurally unreliable for close bidirectional SDM, so the transport must not
treat a missing/NAK receipt as delivery failure; the payload is delivered regardless and the
protocol already carries its own confirmation (the peer's reply telegram). The receipt may remain
an optimistic fast-path when present, never the reliability mechanism. This is exactly #597's
direction — this characterisation is *why* it's correct. Route B is a documented nice-to-have for
radios we control (removes the refractory + saves airtime); Route C stays available for non-tuning
data.

## Follow-ups
- (fix phase, not started) make `SdmTuningLink` / the tuning transport receipt-tolerant per Route A.
- correct the `SdmTuningLink` class remarks (they carry the disproven keying story). *(done: `tait-ninotnc-bench-rig` memory + this doc + the Tait README.)*
