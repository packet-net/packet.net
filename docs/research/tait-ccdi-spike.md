# Tait CCDI spike — per-frame RSSI and hardware DCD from the bench rig

*2026-07-02. The first hardware session of Phase 10 (§5.10): the §5.Y probe playbook run against
the real 2× NinoTNC + 2× Tait rig, ending with `Packet.Radio` + `Packet.Radio.Tait` shipped as
libraries and the headline capability — radio-attributed signal metadata on every received
AX.25 frame — working end-to-end.*

## The rig

```
/dev/ttyACM0 (NinoTNC A, MCP2221 #0004012240) ──audio/PTT── Tait TM8110 s/n 19925328 ── CCDI /dev/ttyUSB1
                                                                  │
                                                            ~100 dB attenuation, same frequency
                                                                  │
/dev/ttyACM1 (NinoTNC B, MCP2221 #0004807594) ──audio/PTT── Tait TM8110 s/n 19925369 ── CCDI /dev/ttyUSB0
```

- NinoTNCs: firmware-catalogue v3.44 era, DIPs in software-control mode, 57600 8N1 KISS.
- Taits: both `TMAB12-B100_0201`, CCDI **03.02**, firmware `QMA1F_std_02.18.00.00`, data port
  programmed to the mic socket at **28800 8N1**, Command mode at power-up. The two CP2102 CCDI
  dongles share USB serial "0001", so `/dev/serial/by-id` cannot distinguish them — map by
  behaviour (key one radio, see which CCDI port reports PTT).
- Received signal level radio-to-radio: **−90 dBm** against a **−128…−129 dBm** squelched noise
  floor (≈38 dB SNR) — a comfortable, repeatable link.

## What the spike proved (all on hardware)

1. **CCDI works as documented** for framing (`[ident][size hex2][params][checksum hex2]<CR>`,
   two's-complement checksum, `.` prompt = transaction-OK). The manual's own worked examples
   are now unit-test vectors in `tests/Packet.Radio.Tait.Tests`.
2. **RSSI queries** (CCTM 063 averaged / 064 raw, 0.1 dB resolution) return continuously and
   fast — a full query round-trip is ~10 ms of wire time at 28800, so a 60 ms poll cadence
   during carrier is cheap and gives several samples per packet even at 9600 bd.
3. **Hardware DCD exists and is push, not poll**: with PROGRESS output enabled (FUNCTION 0/4/1,
   per-session), the radio emits `p0205`/`p0206` (receiver busy / not busy) on RF appearing and
   disappearing. Observed latency: within one 125 ms poll cycle of the far radio keying.
4. **Software PTT** (FUNCTION 9): `f0291CE` keys, `f0290CF` unkeys. CCDI-forced TX ignores the
   radio's TX timer — the driver treats unkey-on-dispose as non-negotiable.
5. **The full chain**: KISS → NinoTNC (mode 6, 1200 AFSK) → TM8110 → 100 dB pad → TM8110 →
   NinoTNC → KISS, both directions, using `NinoTncSerialPort` unmodified.
6. **The headline PoC** (`tools/Packet.Tait.Spike rf-rssi`): 8/8 AX.25 UI frames received, every
   one stamped `RssiDbm` −90.3…−90.7 / `SnrDb` 37.3…39.0 via `RssiTaggingTransport` — the first
   time `Ax25InboundFrame.RadioMetadata` has ever been populated. **Hardware DCD led frame
   delivery by 559–1020 ms** (min/avg/max 559/812/1020 ms, scaling with frame length = TXDELAY
   + airtime). That lead is the entire CSMA value proposition: a bare KISS modem learns the
   channel was busy only after the frame fully demodulates; the radio knew at carrier-rise.

## Firmware/product quirks found (TM8110, CCDI 03.02)

| Command | Result |
|---|---|
| FUNCTION 0/7 set TX power (`f030719F`, manual example verbatim) | `e03001` **unsupported command** — no software power control on this firmware; `RadioCapabilities.TxPowerControl` not advertised |
| FUNCTION 0/5/2 report current channel | works (`p042100…` → single channel, id 0) |
| QUERY 7 display text | `e03003` parameter error (blank-front radios, most likely) |
| QUERY 6 GPS | `e03006` command error (not configured — correct per manual) |
| PA temperature (CCTM 047) | two results: °C then ADC mV (27 °C / ~470 mV at idle) |
| Forward/reverse power (CCTM 318/319) | returns (15/0 mV idle) — a VSWR/antenna-health proxy to sample **while transmitting** |
| Carrier-rise side effect | a `p0219` (FFSK data received) accompanies `p0205` on these radios' channel config — harmless, routed to `ProgressReceived` |

Also observed: the receiving-side NinoTNC emits garbage bytes onto its serial during/after the
far carrier drops (squelch-tail noise decode; the AX.25 CRC rejects it upstream). Worth a look
if spurious `UnknownInboundEvent`s ever bother anyone.

## What shipped

- **`src/Packet.Radio`** — `IRadioControl` (OQ-011 v0: RSSI-get, carrier-sense property+events,
  PTT-set, `RadioCapabilities` feature flags with reserved channel/frequency/power flags) and
  `RssiTaggingTransport` (decorator over any `IAx25Transport`; busy-fast/idle-slow RSSI sampler;
  idle samples track the noise floor; median-of-window attribution; frames with no qualifying
  sample get `null`, never a guess).
- **`src/Packet.Radio.Tait`** — CCDI codec (`CcdiFrame`/`CcdiChecksum`/`CcdiMessage`), and
  `TaitCcdiRadio`: prompt-disciplined transaction engine over a pump thread (same
  blocking-read + `ISerialIo` test-seam pattern as `KissSerialModem`), unsolicited
  PROGRESS/ERROR demux to events, identity/RSSI/PA-temp/power queries, PTT, monitor/mute,
  `TransactRawAsync` escape hatch. 29 unit tests (manual vectors + live captures + scripted-IO
  driver tests).
- **`tools/Packet.Tait.Spike`** — `inventory` (→ `artifacts/radio-capabilities/<serial>.json`,
  §5.Y item 1), `dcd` (live DCD/PTT/RSSI monitor), `rf-rssi` (the PoC loop with DCD-lead stats).
- Both libraries added to the `publish-libs.yml` matrix (publish happens on the next `lib-v*`
  tag as usual — nothing tagged from this spike).

## Design notes / known limitations

- **Attribution is timestamp-correlation, channel-level.** Back-to-back frames inside one busy
  window share a median; refining to per-frame windows (prev-frame-end → this-frame-end) is a
  cheap follow-up. Sub-noise-floor frames (below squelch) would get no attribution — also the
  regime where the radio's DCD itself goes quiet.
- **Set-commands complete on the CCDI prompt**, and the radio also prompts after unsolicited
  messages, so a pathologically-timed unsolicited message can complete a set-transaction a beat
  early. Benign (the command still executes; errors still surface via `ErrorReceived`), noted in
  the driver docs.
- **XON/XOFF**: the manual says the radio may use software flow control; the pump discards
  XON/XOFF bytes and we never came close to overrunning the radio at this poll cadence.
- **Untested on hardware**: GO_TO_CHANNEL (channel *switch* — report works), CCR mode (direct
  RX/TX frequency programming, volume, CTCSS/DCS, bandwidth — TM8100 only; entry `f0200D8`),
  Transparent mode (FFSK data through the radio's own 1200 bd modem, no TNC at all — a
  fascinating minimal-node option), SDM short-data messages. All are documented in the CCDI
  manual (MMA-00038-06) and deliberately out of this spike.

## Session 2 addendum (same day): timing, CCR, watchdog, discovery

The follow-up session measured the timing envelope, modelled both computer-control modes, and
enriched the transport seam. Hardware-measured numbers (1200 Bd AFSK link, CCDI at 28800):

- **RSSI poll RTT 14.4 ms median (14.3–15.3 ms, p95 15.2)** — sub-ms jitter; poll cadence up to
  ~50 Hz is available, `RssiTaggingTransport` defaults to 40 ms while carrier is up.
- **Carrier-edge (DCD) report latency ~27–28 ms, σ<1 ms** measured PTT-report→busy-report across
  12 keyings — effectively a subtractable rig constant, giving DCD edge timestamps ±2–3 ms.
- **Frame-edge derivation works**: `ReceivedAt − EstimatedAirtime` puts on-air start within tens
  of ms; measured **pre-data carrier tracked the TX TNC's configured TXDELAY + 40–75 ms**
  constant overhead across a 200/500/1000 ms sweep — ample for an **excess-TXDELAY detector**
  (`RadioMetadata.PreDataCarrier`).
- **Settings settle on the SECOND frame** (NinoTNC-specific behaviour; bench-confirmed): every
  TXDELAY step showed frame 1 at the old value, frames 2+ at the new.
  `TxDelayHillClimbEstimator` now discards the settling frame's outcome after each step —
  harmless insurance on other TNCs (one frame of climb latency). What IS general AX.25 is the
  multi-frame train: several frames per keying with one preamble, which the burst-aware
  attribution handles.
- **Frame trains**: 3 back-to-back frames = one carrier window, ~370 ms delivery spacing, no
  re-preamble. `RssiTaggingTransport` tracks windows and stamps `BurstIndex`; `PreDataCarrier`
  only on burst index 0. Frame delivery trails carrier-fall by ~35–115 ms (decode+serial), so
  closed windows stay attributable for a slack (default 500 ms).
- **CCR mode live-validated** (`TaitCcrSession`): entry banner `M01R00`, pulse, volume ACK,
  **TX-power ACK (the control CCDI FUNCTION 0/7 lacks on this firmware)**, exit = soft reset
  with clean recovery to Command mode in ~6 s. **The NinoTNC's data-line PTT still keys the
  radio inside CCR mode** (observer saw the usual −90 dBm) — so a CCR power-step SNR sweep
  would need no frequency knowledge (CCR inherits the active channel's parameters). **Sweep
  abandoned: the rig's attenuators are rated 2 W — never key anything above VeryLow here.**
- **Forward/reverse power telemetry works live during TX** (CCDI stays responsive while the
  radio transmits): idle 0/0 mV → keyed at VeryLow 388 mV fwd / 172 mV rev → 0 on unkey. Raw
  detector millivolts, uncalibrated — trend it per-station as a TX-health/antenna proxy rather
  than reading it as VSWR. (Own-RSSI during TX reads the muted receiver, as expected.)
- **SDM is disabled in these radios' programming** — `a…` answers error 0/06; the API is built
  and unit-tested but needs a programming-app change to exercise over air.
- **Protocol trap found on hardware**: the radio prompts *before* the ERROR of a rejected
  command (`.e03006A2\r.`), so prompt-completed transactions now wait a 100 ms
  `PromptErrorGrace` for a trailing rejection.
- **Watchdog**: `TaitCcdiRadioOptions.KeepAliveInterval` (default 30 s) probes on link silence
  (RSSI query in Command mode, `Q` pulse in CCR — the manual itself prescribes a 10 s pulse);
  3 consecutive failures → `ConnectionState.Faulted` + event, self-healing on recovery; a dead
  read pump faults immediately and fails the in-flight transaction.
- **Port auto-detection**: `TaitRadioPortDiscovery` (env override → `/dev/ttyUSB*` → all ports)
  probes candidates with a MODEL query — live scan found both radios and told them apart by
  CCDI serial where the identical CP2102 USB IDs cannot. PDN/node-host wiring (config UI,
  "found a TM8110 s/n … on ttyUSB0, attach it to port X?") is the named follow-up.

### NinoTNC mode sweep over the TM8110 RF path (§5.Y item 4, level-fixed)

`Packet.NinoTnc.Spike soak mode-sweep` through the radios (5 round trips per direction, both
TNCs SETHW'd per mode; full table in `artifacts/nino-tnc-soak/20260702-211619/results.md`):

| Mode | A→B | B→A | Verdict |
|---|---|---|---|
| 6 — 1200 AFSK AX.25 | 5/5 | 5/5 | solid |
| 7 — 1200 AFSK IL2P+CRC | 5/5 | 4/5 | solid |
| 12 — 300 AFSK AX.25 | 4/5 | 5/5 | works (better over RF than it was on the audio cross-wire) |
| 0/2/3 — 9600 GFSK/IL2P/4FSK | 0/5 | 0/5 | dead |
| 1 — 19200 4FSK | 0/5 | 0/5 | dead |

Reading (per Tom): the audio path IS flat — the TNCs use the correct tap points in the Taits —
the 9600-class failures are the **narrow (12.5 kHz) channels** these radios are programmed for,
a necessity on UK 2 m; 9600 GFSK doesn't fit a narrow channel. The SNR here is ~38 dB, so it is
occupied-bandwidth, not level. Follow-up tried: parking both radios in CCR and setting
bandwidth wide (`H01324`, ACKed by both) did NOT make 9600 decode (0/3) — the CCR bandwidth
switch alone doesn't reconfigure enough of the chain (deviation scaling / IF filtering for the
flat tap presumably follow the programmed channel); a wideband *programmed* channel is the
thing to test when regulations/bench allow. Caveat on all mode results: the NinoTNC TX audio
pots are untuned (midpoint), so deviation is uncalibrated — AFSK clearly tolerates it; 9600 is
far more deviation-sensitive, so retest after a proper level set before calling it definitively
dead even on a wide channel.

### SDM validated over the air (radios reprogrammed: IDs PDN00001/PDN00002, CCDI3+CCDI2 Text, auto-acks on)

Addressed SDM delivered (RING + buffered message on the target); wildcard `PDN0****` delivered;
wrong-ID correctly filtered (carrier + FFSK progress seen, no RING, buffer empty). **Delivery
receipts work both ways on the sender**: PROGRESS `1D1` (acked) on success, `1D0` after the 6 s
wait for the wrong-ID send — surfaced as the driver's typed `SdmDeliveryReceipt` event.
Radio-native messaging with acknowledgements, zero TNC involvement.

## Packet.Tune CLI (2026-07-02, session 3): NinoTNC remote-diagnostics driver + tuning loop

The bench-verified remote-diagnostics primitives are now first-class driver surface
(`Packet.Kiss.NinoTnc`) and a CLI (`tools/Packet.Tune`), validated live on this rig
(both TNCs firmware **3.41**, mode 6):

- **Driver**: `NinoTncCommands` (GETALL 0x0B/00, GETVER 0x08/00, STOPTX 0x09/00,
  SETBCNINT 0x09/F0+mins, GETRSSI 0x09/A7 — replies on raw KISS command byte `0xE0`),
  `NinoTncStatusFrame` (the numeric `=II:` register report, registers 00–11) +
  `NinoTncStatusDelta` (two snapshots → per-register deltas), `NinoTncRssiReading`,
  `NinoTncCqBeep` (`[TARPNstat` arming + `CQBEEP-N` beep-request factories), and awaiting
  helpers on `NinoTncSerialPort` (`GetAllAsync`/`GetVersionAsync`/`GetRssiAsync`/
  `StopTxAsync`/`SetBeaconIntervalAsync`/`ArmCqBeepResponderAsync`/`SendCqBeepRequestAsync`).
  `NinoTncAirTestFrame.TryRecognise` now accepts any `CQBEEP-N` (was hardcoded SSID 5) and
  exposes the SSID = commanded tone seconds.
- **`Packet.Tune verify-control <tncPort>`** — GETALL: DIP positions (reg 04; `1111` =
  software control) + config mode, then a commanded-vs-measured TXDELAY check (settling
  frame discarded — settings apply on the SECOND frame). Live result: DIPs `1111`, mode 6,
  echo timing moved **0.303 s for the 0.300 s commanded step** (200 → 500 ms) → software
  control confirmed.
- **`Packet.Tune measure <tncPort> [ccdiPort]`** — GETVER + GETRSSI baseline (n=5) +, via
  CCDI, the radio's averaged/raw RSSI. Live: TNC RX-audio idle −32.6 dB median; TM8110
  s/n 19925328 noise floor −129.4 dBm median — matching the session-1/2 numbers.
- **`Packet.Tune deviation <localTnc> <remoteTnc>`** — the TX-deviation tuning loop, local
  bench flavour: arm the REMOTE TNC (`[TARPNstat` through its own serial), trigger
  `CQBEEP-8` from the LOCAL TNC, meter the remote's 440 Hz tone with GETRSSI on the local
  TNC (~250 ms cadence), prompt the operator to adjust the remote's TX-DEV pot, repeat with
  a trend table. Live: armed B first try; tone window ~7.5 s at **−64.3 dB median vs
  −32.5 dB idle** — the received tone level is the deviation signal to trend. The
  internet/SDM-coordinated remote flavour (each end runs half the loop on its own host) is
  the named follow-up.

Firmware **3.41** findings (all bench-measured; newer firmware may differ):

| Behaviour | 3.41 observation |
|---|---|
| GETALL (0x0B/00) reply | the **labelled** `=FirmwareVr:` text on `0xE0` — not the numeric report; `GetAllAsync` maps it so callers see `NinoTncStatusFrame` either way |
| Numeric `=II:` report | emitted **spontaneously every 60 s** (uptime deltas exactly 60 000 ms; SETBCNINT's interval) as a fake UI frame `TNC>USB` on plain KISS Data; registers 00–04, 06–11 (no 05); reg 00 = plain-ASCII version, reg 01 = eight **raw** bytes (KAUP8R identity, zeros when unset), rest 8 hex digits |
| GETVER / GETRSSI | as documented: bare `3.41` / `RSSI:-32.86` ASCII on `0xE0`, ~10 ms RTT |
| Register 0B (preamble words) | **never increments** for host traffic — flat at TXDELAY 200/500/1000 ms, ACKMODE and plain sends, modes 6 and 7, TX and RX side. `verify-control` therefore falls back to ACKMODE TX-completion timing (which tracked the commanded step to 3 ms) |
| STOPTX vs CQBEEP tone | STOPTX sent mid-tone did **not** cut the tone short (ran the full 8 s) |
| CQBEEP responder | arming + N-second tone work exactly as advertised on 3.41 |

## Firmware 3.44 flash + remote deviation tuning (2026-07-02/03, session 4)

Both TNCs were flashed 3.41 → **3.44** (upstream flashtnc, exit 0 both), and the
remote-coordination deviation-tuning feature set (`Packet.Tune.Core` + the
`deviation-sdm` / `deviation-remote` / `doctor` / `rendezvous` / `radio-reset`
commands) was built and validated against the flashed rig.

### 3.44 flash findings (vs the 3.41 table above)

| Behaviour | 3.44 observation |
|---|---|
| **GETRSSI (0x09 A7)** | **REMOVED** — no reply at all (bench: 2 s probe times out). It was an undocumented 3.41 feature. Driver keeps `GetRssiAsync` with "firmware 3.41 only" docs; `measure`/`deviation` degrade to "n/a on this firmware"; the deviation METER never uses it (signals below) |
| GETALL (0x0B/00) | still answers the **labelled** `=FirmwareVr:` text on demand (numeric `=II:` report remains the 60 s beacon only) |
| Register 0B (preamble words) | still never increments for host traffic — the TXDELAY check keeps its ACKMODE echo-timing fallback |
| CQBEEP-N + `[TARPNstat` arming | unchanged (bench: 4.02 s tone for SSID 4). Arming stays volatile — the tuning assistant re-arms at session start |
| **Post-flash boot mode** | a fresh flash clears the RAM mode → boots **mode 0** (9600 GFSK — dead on the rig's narrow channels). This produced a false "pot override" verdict from `verify-control` before the fix: **always SETHW a known-good mode before timing checks** — `verify-control`/`doctor` now pin mode 6 (`--mode`/`--keep-mode` override) |

Two more measurement traps found while stabilising the TXDELAY check (fixed in
`TxDelayControlCheck`): back-to-back ACKMODE sends chain into **one keying
train** (a single preamble — chained frames echoed a constant ~430 ms
regardless of TXDELAY), and the default p-persistence CSMA adds a random
number of ~100 ms slot deferrals per keying, swamping the 300 ms step under
test. The check now pins persistence 255 / slottime 0 (restoring 63/10 after),
takes 3 unkey-gapped samples per point and keeps the minimum: measured
**0.298 / 0.301 / 0.299 s for the 0.300 s commanded step** across three
consecutive runs (was 0.001–0.793 s scatter).

### Deviation metering without GETRSSI

Per burst the meter now reads: **decoded-frame count vs sent** (counting
`PTUNE`-marked burst frames), **IL2P FEC-corrected-byte GETALL delta**
(register 11 — only meaningful in IL2P modes, so prefer **mode 7** for tuning
sessions; on 3.41/3.44 the labelled GETALL reply lacks the register, so this
reports n/a until firmware carries it — the labelled `LostADCSmp` is mapped
instead), **lost-ADC-sample delta** (clipping = gross over-deviation), and
**Tait CCDI RSSI** (busy-gated median) as the constant RF-path check. Advice
`UP`/`DN`/`OK` comes from decode-rate + FEC trend + clipping
(`DeviationAdvisor`).

### The TM8110 SDM auto-ack wedge (the big hardware find)

Keying a TM8110 through its data PTT line — or sending an SDM from it — while
the radio's **auto-acknowledgement of a just-received SDM is pending/in
flight** *wedges the radio's auto-ack engine*: from then on it still receives
SDMs (RING 4000 + buffer fills) but **never acks again**, so every peer send
reports 1D0 "not delivered". CCDI stays fully responsive — only the ack
engine is dead — and nothing short of a **soft reset (CCR enter/exit, ~6 s)**
or a power cycle recovers it. Bench-reproduced three ways (burst racing an
RQ's ack; CQBEEP-arming PTT racing an unsolicited HI's ack; an instant RQ
reply racing the ready-beacon's ack) and bench-recovered with the new
`packet-tune radio-reset <ccdiPort>` command.

`SdmTuningLink`/`TuningSession` now avoid all three triggers by construction:
a **2 s post-receive guard** before any send (the radio's own ack must clear
first), a **2.5 s pre-burst delay** on the tuned end, and a protocol shape
where **the meter never transmits unsolicited** (its first send is the RQ
answering the tuned end's ready beacon, which always lands on an idle end).

### Validation on the rig (all green after the fixes)

- **`doctor`** both stacks: all probes PASS (fw 3.44, DIPs 1111, mode 6,
  TXDELAY software control, TM8110 identities, PROGRESS, SDM accepted,
  TNC↔radio PTT pairing within 2 s); GETRSSI correctly reported as removed
  with the remedial pointer. `--json` variant works.
- **SDM link** (`deviation-sdm`, two processes, operator scripted via stdin):
  full two-round session — HI/RQ/MS/AD/BY over the radios' FFSK, telegrams
  like `V1|1|MS|5/5|c0|r-90.1` (compact MS form fits the 32-char SDM budget),
  **zero receipt retries after the guards** (earlier runs demonstrated the
  retry ×3 / 2 s-backoff path live, incl. honest failure against a wrong peer
  ID), 5/5 decode both bursts, `AD:OK` steady (pots untouched), RSSI −90.1 dBm
  matching the rig's known link budget.
- **Internet flavour**: `rendezvous --listen 8735` + both `deviation-remote`
  roles over loopback with the real generated-PIN flow (tuned printed
  "session PIN: 2-5-7-7-7-4", meter joined with it), one full RQ→MS→AD cycle
  with RF stimulus (5/5 decode, −90.2 dBm), clean BY, relay logged
  pair/close. PIN single-use + session-dies-with-either-socket are
  unit-tested over real loopback sockets.

## Follow-ups (rough priority)

1. **CSMA TX gate**: feed `IRadioControl.ChannelBusy`/`CarrierSenseChanged` into a transmit-gate
   seam (`Packet.LinkBench` already sketches `ITxGatePolicy`) so the AX.25 stack defers TX while
   the channel is busy — the 0.5–1 s DCD lead makes this materially better than p-persistence
   alone. Needs a home on the transport composition, not in `Ax25Listener` itself.
2. **Channel-quality scoring** (§5.10): the per-frame RSSI/SNR now exists to feed it; add busy-%
   from carrier-sense duty cycle.
3. **Per-burst attribution refinement** in `RssiTaggingTransport` (above).
4. **Second `IRadioControl` implementation** (Yaesu CAT or ICOM CI-V) before freezing the
   abstraction; then close OQ-011.
5. **GO_TO_CHANNEL / CCR** modelling for frequency-agile operation (§5.10's QSY workstream) —
   next bench session, since it risks splitting the two radios onto different channels.
6. **§5.Y items 3–6** (SNR-vs-level calibration, mode-by-SNR survey, 1000-iteration soak,
   mistune degradation) — the rig and the tooling are now in place to script them.
7. Housekeeping: `docs/releasing.md`'s "six published packages" list is stale against the
   `publish-libs.yml` matrix (now 13); reconcile on the next release pass.
