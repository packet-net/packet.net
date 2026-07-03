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
| **GETRSSI (0x09 A7)** | **REMOVED** — no reply at all (bench: 2 s probe times out). It was an undocumented 3.41 feature. Driver keeps `GetRssiAsync` with "firmware 3.41 only" docs; `measure`/`deviation` degrade to "n/a on this firmware"; the deviation METER never uses it (signals below) — *since superseded: on 3.41 the meter now probes it as an optional fast path, see "The 3.41 fast path" below* |
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

## The 3.41 fast path: GETRSSI as the deviation meter's optional level source (2026-07-03, session 5)

The bench **deliberately runs firmware 3.41 for the current test campaign** —
GETRSSI (KISS `0x09 A7` → cmd `0xE0` ASCII `RSSI:-34.37`, an RX-audio RMS dB
meter) is **3.41-only** per the version mapping (flashed down through 3.43 and
3.42: both lack it). **3.44 stays the production guidance** (its 3.42–3.44
fixes matter: CSMA slot timing, preamble-after-beacon, ACKMODE header rip);
the fast path is strictly opportunistic and everything degrades to the 3.44
signal set when the probe times out.

What shipped (branch `tune-fastpath`): the meter role probes GETRSSI **once
at session start** (`NinoTncBurstMeter.ProbeAudioLevelMeterAsync`, 2 s
timeout) and, when it answers, captures an idle-channel baseline (median of
5) then samples the level on the RSSI cadence during each burst window
(DCD-gated median; max-quieting minimum without a radio attached). The `MS`
telegram gains an optional `lvl:`/`l` field — absent on the wire parses as
`null`, so old peers interop cleanly; if the compact form would bust the
32-char SDM budget the level is the field that gets dropped (bracketing
signals keep priority). `DeviationAdvisor.DescribeLevel` renders level +
quieting + burst-on-burst trend on the meter's advice line and under the
tuned end's trend table; the **UP/DN/OK bracketing verdicts stay
authoritative** — the level never changes them. Doctor's getrssi PASS now
reads "available (firmware 3.41-era) — deviation meter fast path active".

Validated on the rig (both TNCs 3.41 mode 6, beacons off, both radios
confirmed on the newly-programmed **channel 0 = narrow** — 1200 AFSK belongs
there; channel 1 = wide is for the 9600 retest):

- doctor `/dev/ttyACM0 /dev/ttyUSB1`: all 9 probes PASS, getrssi = "available
  (firmware 3.41-era) — deviation meter fast path active (idle -34.0 dB)".
- `deviation-sdm` two-round scripted session (meter ACM0/USB1 ← peer
  PDN00001; tuned ACM1/USB0 ← peer PDN00002): probe reported "GETRSSI fast
  path active (firmware 3.41-era) — idle RX-audio -33.1 dB (n=5)"; MS
  telegrams carried the level over the SDM within budget
  (`V1|1|MS|5/5|c0|r-89.7|l-31.7`, 28 chars); zero receipt retries; tuned-end
  trend table grew the level column:

  ```
  burst   decoded   fec Δ    clip Δ   rssi dBm   level dB   advice
      1     5/5        n/a        0      -89.7      -31.7   OK
      2     5/5        n/a        0      -89.7      -31.9   OK
  level -31.9 dB, level steady (RX audio at the meter end)
  ```

  Meter advice lines: `5/5|fec:na|clip:0|rssi:-89.7|lvl:-31.7 → OK (level
  -31.7 dB, -1.4 dB quieting)`; burst 2 added `level steady`.
- **Measurement note**: a 1200 AFSK data burst reads ≈ the open-squelch idle
  noise level (−31.7 vs −33.1 dB idle → *negative* quieting, −1.4 dB), unlike
  the 440 Hz CQBEEP tone (−64.3 vs −32.5 dB idle, session 3). Post-demod
  AFSK audio RMS at these (untuned, midpoint) pot positions is simply
  noise-like in level — so for data bursts the *level itself* is the
  deviation signal to trend as the pot moves, and the quieting figure is only
  dramatic for tone stimuli. The advice-line wording deliberately reports
  both without treating either as a verdict.

## Wide-channel IL2P+CRC survey (2026-07-03, session 6) — §5.Y item 4 for this rig

The radios' new two-channel programming (**channel 0 = narrow 12.5 kHz, channel 1 = wide
25 kHz**) got its first scripted workout: the new `packet-tune mode-survey` command (plus the
`set-mode` / `radio-channel` primitives) swept **every IL2P+CRC catalog mode** (2, 4, 5, 7, 8,
9, 10, 11, 14 — Tom's directive: name contains `IL2P+CRC` exactly; plain-IL2P mode 13 and the
legacy AX.25 modes excluded) across both channels, 5 probe frames per direction per cell, on
the usual rig (both TNCs firmware 3.41, SETHW +16 RAM-only + settle frame per mode change,
beacons off, link −90 dBm / ~38 dB SNR). Per cell: decode count, mean send→decode latency,
busy-gated CCDI RSSI at the receiver, and the receiver's GETALL IL2P counter deltas
(`IL2PRxPkts` / `IL2PRxUnCr` — both present in 3.41's labelled reply). Raw log + JSON in
`artifacts/hardware-probe/20260703-il2pc-survey/` (untracked); the full table:

| Ch | Mode | Name | Dir | Decoded | Mean latency | RSSI @ RX | IL2P rx Δ | IL2P uncorr Δ | Verdict |
|---:|-----:|------|-----|--------:|-------------:|----------:|----------:|--------------:|---------|
| 0 | 2 | 9600 GFSK IL2P+CRC | A→B | 0/5 | n/a | -90.0 dBm | 0 | 0 | dead |
| 0 | 2 | 9600 GFSK IL2P+CRC | B→A | 0/5 | n/a | -90.0 dBm | 0 | 0 | dead |
| 0 | 4 | 4800 GFSK IL2P+CRC | A→B | 0/5 | n/a | -90.2 dBm | 0 | 0 | dead |
| 0 | 4 | 4800 GFSK IL2P+CRC | B→A | 0/5 | n/a | -90.0 dBm | 0 | 0 | dead |
| 0 | 5 | 3600 QPSK IL2P+CRC | A→B | 5/5 | 833 ms | -89.5 dBm | 5 | 0 | solid |
| 0 | 5 | 3600 QPSK IL2P+CRC | B→A | 5/5 | 837 ms | -89.5 dBm | 5 | 0 | solid |
| 0 | 7 | 1200 AFSK IL2P+CRC | A→B | 5/5 | 1251 ms | -90.2 dBm | 5 | 0 | solid |
| 0 | 7 | 1200 AFSK IL2P+CRC | B→A | 5/5 | 1227 ms | -89.8 dBm | 5 | 0 | solid |
| 0 | 8 | 300 BPSK IL2P+CRC | A→B | 5/5 | 3168 ms | -89.7 dBm | 5 | 0 | solid |
| 0 | 8 | 300 BPSK IL2P+CRC | B→A | 5/5 | 3202 ms | -89.7 dBm | 5 | 0 | solid |
| 0 | 9 | 600 QPSK IL2P+CRC | A→B | 4/5 | 1950 ms | -89.6 dBm | 4 | 0 | MARGINAL |
| 0 | 9 | 600 QPSK IL2P+CRC | B→A | 5/5 | 1925 ms | -89.5 dBm | 5 | 0 | solid |
| 0 | 10 | 1200 BPSK IL2P+CRC | A→B | 5/5 | 1223 ms | -89.6 dBm | 5 | 0 | solid |
| 0 | 10 | 1200 BPSK IL2P+CRC | B→A | 5/5 | 1230 ms | -89.5 dBm | 5 | 0 | solid |
| 0 | 11 | 2400 QPSK IL2P+CRC | A→B | 4/5 | 950 ms | -89.6 dBm | 4 | 0 | MARGINAL |
| 0 | 11 | 2400 QPSK IL2P+CRC | B→A | 5/5 | 917 ms | -89.5 dBm | 5 | 0 | solid |
| 0 | 14 | 300 AFSKPLL IL2P+CRC | A→B | 3/5 | 3136 ms | -90.0 dBm | 3 | 0 | MARGINAL |
| 0 | 14 | 300 AFSKPLL IL2P+CRC | B→A | 5/5 | 3136 ms | -89.8 dBm | 5 | 0 | solid |
| 1 | 2 | 9600 GFSK IL2P+CRC | A→B | 0/5 | n/a | -89.4 dBm | 0 | 0 | dead |
| 1 | 2 | 9600 GFSK IL2P+CRC | B→A | 0/5 | n/a | -89.5 dBm | 0 | 0 | dead |
| 1 | 4 | 4800 GFSK IL2P+CRC | A→B | 0/5 | n/a | -89.5 dBm | 0 | 0 | dead |
| 1 | 4 | 4800 GFSK IL2P+CRC | B→A | 0/5 | n/a | -89.4 dBm | 0 | 0 | dead |
| 1 | 5 | 3600 QPSK IL2P+CRC | A→B | 5/5 | 836 ms | -89.3 dBm | 5 | 0 | solid |
| 1 | 5 | 3600 QPSK IL2P+CRC | B→A | 5/5 | 832 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 7 | 1200 AFSK IL2P+CRC | A→B | 5/5 | 1226 ms | -89.6 dBm | 5 | 0 | solid |
| 1 | 7 | 1200 AFSK IL2P+CRC | B→A | 5/5 | 1223 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 8 | 300 BPSK IL2P+CRC | A→B | 5/5 | 3231 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 8 | 300 BPSK IL2P+CRC | B→A | 5/5 | 3221 ms | -89.5 dBm | 5 | 0 | solid |
| 1 | 9 | 600 QPSK IL2P+CRC | A→B | 4/5 | 1942 ms | -89.4 dBm | 4 | 0 | MARGINAL |
| 1 | 9 | 600 QPSK IL2P+CRC | B→A | 5/5 | 1931 ms | -89.5 dBm | 5 | 0 | solid |
| 1 | 10 | 1200 BPSK IL2P+CRC | A→B | 5/5 | 1226 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 10 | 1200 BPSK IL2P+CRC | B→A | 5/5 | 1228 ms | -89.5 dBm | 5 | 0 | solid |
| 1 | 11 | 2400 QPSK IL2P+CRC | A→B | 5/5 | 919 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 11 | 2400 QPSK IL2P+CRC | B→A | 5/5 | 910 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 14 | 300 AFSKPLL IL2P+CRC | A→B | 5/5 | 3252 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 14 | 300 AFSKPLL IL2P+CRC | B→A | 5/5 | 3137 ms | -89.4 dBm | 5 | 0 | solid |

Findings:

1. **The wide channel did NOT bring up the GFSK modes.** 9600 GFSK (mode 2) and 4800 GFSK
   (mode 4) are dead on **both** widths — with carrier confirmed at the receiver every burst
   (−89.x dBm busy-gated) and `IL2PRxUnCr` Δ0, i.e. the receiving modem never even achieved
   IL2P sync (these are not FEC-exhausted near-misses). Same shape as the session-2 CCR
   bandwidth experiment. With QPSK at 3600 solid on the *narrow* channel, occupied bandwidth
   alone no longer explains it: the untuned TX-DEV pots (midpoint, uncalibrated) are now the
   prime suspect for the deviation-critical GFSK direct-FSK path — set levels properly
   (deviation-tuning session) and re-survey before declaring 9600 dead on this hardware; radio
   channel-programming details (data deviation scaling on the wide channel) remain the backup
   suspect.
2. **The wide channel DID stabilise the marginals.** Mode 11 (2400 QPSK) A→B went 4/5
   MARGINAL → 5/5 solid; mode 14 (300 AFSKPLL) A→B went 3/5 MARGINAL → 5/5 solid. Mode 9
   (600 QPSK) A→B stayed 4/5 on both widths.
3. **Every marginal cell is A→B** (TNC A transmitting); B→A was solid in all 18 cells. A
   directional deviation/level asymmetry at TNC A's TX pot — exactly the cell the deviation
   tuning assistant exists for; run it against TNC A next bench session.
4. **The IL2P GETALL counters are wired and truthful**: `IL2PRxPkts` Δ matched the decoded
   count in every cell; `IL2PRxUnCr` stayed 0 across all 36 cells (losses were sync misses,
   not uncorrectable frames).
5. Mean latencies scale with bit rate exactly as expected (≈0.83 s at 3600 baud → ≈3.2 s at
   300 baud for the 40-byte-info probe, TXDELAY + CSMA included) — a usable per-mode
   round-trip budget table for the node's future mode selection.

**GO_TO_CHANNEL live behaviour** (first hardware use, TM8110 / CCDI 03.02): works cleanly.
The command prompt-completes, the radio then emits an **unsolicited PROGRESS 21**
(user-initiated channel change, para = kind+channel, e.g. `01` = single-channel 1) on the
retune itself, and the solicited FUNCTION 0/5/2 verify reports the new channel immediately —
every switch (0→1, 1→0, and the same-channel no-ops) verified first try; the radios never
split. The survey command still verifies after every switch and, if the radios ever end up
split, recovers by commanding both to channel 0 before aborting.

Tooling quirks observed (all tolerated by the command, worth knowing): the **settle frame's
ACKMODE TX-completion echo is sporadically absent** right after SETHW (mostly TNC B; the
frame itself still keys — the tool logs and continues); the **very first ACKMODE send after
port open** may not echo at all; and on 3.41 **mode 14 reports firmware mode byte `0x90`**,
which the v3.44-locked `NinoTncCatalog.FirmwareByteToMode` doesn't know (3.44 reports
`0x23`) — the GETALL verify prints "unrecognised firmware byte" while the mode is in fact
engaged (the 300 AFSKPLL traffic proves it). A 3.41 column for the firmware-byte table is a
small follow-up if 3.41 stays the campaign firmware.

### SDM delivery on the wide channel (deliverable-3 conditional: not met)

Mode 2 did **not** decode on channel 1, so the planned mode-2 deviation-sdm baseline (the
first deviation-sensitive-mode trend capture) did not run as specified. Instead, a scripted
two-round `deviation-sdm` session (meter ACM0/USB1 ← peer PDN00001; tuned ACM1/USB0 ← peer
PDN00002) ran **on channel 1 with both TNCs in mode 2 anyway**, strictly as (a) the
SDM-coordination-on-wide confirmation and (b) an honest dead-link capture. Result: **SDM
delivery works on the wide channel exactly as on narrow** — all 9 telegrams
(HI / RQ / MS / AD ×2 rounds / BY) delivered on send attempt 1/3 with over-air receipts
acknowledged, zero retries; MS carried the level field within the SDM budget
(`V1|1|MS|0/5|c0|r-89.5|l-48.2`). The assistant read the dead 9600 link correctly: 0/5
decode both bursts, `AD:UP` both rounds, CCDI RSSI −89.5 dBm (carrier present), and — a
nice contrast with the session-5 AFSK finding — the *undecodable* 9600 GFSK carrier
**quiets** the meter's RX audio like a tone does (idle −35.8 dB → −48.2 / −44.9 dB during
bursts = 12.4 / 9.2 dB quieting), so the GETRSSI level field is live and useful in exactly
the deviation-sensitive regime it was built for. The real mode-2 baseline moves to after a
TX-level calibration session brings mode 2 up.

## The R5→R2 retap (2026-07-03, session 7): the direct-FSK modes were never dead — the RX tap was

### The diagnosis story (and the reusable diagnostic it leaves behind)

Two sessions of evidence had piled up against the GFSK modes on this rig, and none of it made
sense as a deviation or bandwidth problem on its own:

- **Every audio-band mode passed** — AFSK, BPSK, QPSK all solid, *including 3600 QPSK on the
  narrow channel*, which rules out occupied bandwidth as the killer for 9600-class modes.
- **The direct-FSK modes (9600/4800 GFSK) were dead in BOTH directions**, on both channel
  widths, with carrier confirmed at the receiver (−89 dBm busy-gated) and `IL2PRxUnCr` Δ0 —
  the modem never even achieved IL2P sync. A symmetric, both-directions failure is an
  awkward fit for a per-TNC pot mis-set (a *directional* fault, like the A→B marginals).
- The CCR bandwidth switch didn't help (session 2), and neither did a properly *programmed*
  wide channel (session 6) — so it wasn't channel programming either.

That combination is a signature: **audio-band modes pass + direct-FSK dead both directions ⇒
the radio's RX audio tap sits after the voice filters.** A 300–3000 Hz voice filter passes
every audio-band modem cleanly and guts the near-DC energy a G3RUH-style direct-FSK signal
lives on — symmetrically, in both directions, at any pot position. Worth keeping as a
**doctor diagnostic** (a candidate `TuningDoctor` probe: one audio-band and one direct-FSK
loopback burst, classify on the split) — it would have saved two bench sessions of
pot-suspicion here.

The fix: both TM8110s' global RX tap point reprogrammed **R5 → R2**. R5 is tapped after the
300–3000 Hz voice filters (the root cause). R2 is pre-filter, post-**PSD-normaliser** — the
correct *modem pair* with the TX tap T13 already in use. R2 was chosen over R1 (raw
discriminator) deliberately: the PSD normaliser makes the tapped level **invariant across
channel-width switching**, which matters on this rig's narrow/wide two-channel programming —
no TNC RX-pot retouching when a session flips channels. (Bench confirmation: the meter-end
idle RX-audio level barely moved across the retap — ≈−33/−35 dB before, −34.4 dB after — the
R2 tap slots straight into the existing level budget.)

Immediately after the retap, at **untouched pot positions**, 9600 GFSK IL2P+CRC (mode 2)
decoded 5/5 A→B on channel 1 — after two sessions of 0/5 everywhere.

### Port re-enumeration during the reprogramming

Reprogramming the radios re-enumerated the CCDI USB dongles: the old `/dev/ttyUSB0`/`USB1`
are gone, and the new numbers carry the **opposite** TNC association:

| CCDI port | Radio s/n | SDM identity | Audio-paired TNC |
|---|---|---|---|
| `/dev/ttyUSB2` | 19925328 | `PDN00002` | NinoTNC A `/dev/ttyACM0` |
| `/dev/ttyUSB3` | 19925369 | `PDN00001` | NinoTNC B `/dev/ttyACM1` |

The two CP2102 dongles still share USB serial "0001", so `/dev/serial/by-id` still cannot
tell them apart — the re-map was recovered with `TaitRadioPortDiscovery`, which
distinguishes the radios by **CCDI serial number** where the USB descriptors cannot.
Standing rule: re-run the discovery after *any* cable or radio-programming work; never
trust remembered `/dev/ttyUSB*` numbers.

### Post-retap IL2P+CRC survey — the GFSK columns come alive

Same `packet-tune mode-survey` procedure as session 6 (both TNCs firmware 3.41, 5 probes
per direction per cell, link −90 dBm / ~38 dB SNR), with one selection change: the
catalog's two **4FSK modes now carry their IL2P+CRC protocol in their names** — the
kissproxy-inherited bare names ("19200 4FSK", "9600 4FSK") were a misclassification; both
modes are IL2P+CRC per the OARC wiki mode table (Tom's citation — there is no AX.25
variant of 4FSK), so modes 1 and 3 joined the `IL2P+CRC` name-filtered survey for the
first time: 11 modes × 2 channels × 2 directions = 44 cells. Raw log + JSON in
`artifacts/hardware-probe/20260703-postr2-il2pc-survey/` (untracked). Full table:

| Ch | Mode | Name | Dir | Decoded | Mean latency | RSSI @ RX | IL2P rx Δ | IL2P uncorr Δ | Verdict |
|---:|-----:|------|-----|--------:|-------------:|----------:|----------:|--------------:|---------|
| 0 | 1 | 19200 4FSK IL2P+CRC | A→B | 0/5 | n/a | -89.6 dBm | 0 | 0 | dead |
| 0 | 1 | 19200 4FSK IL2P+CRC | B→A | 0/5 | n/a | -89.7 dBm | 0 | 0 | dead |
| 0 | 2 | 9600 GFSK IL2P+CRC | A→B | 5/5 | 625 ms | -90.9 dBm | 5 | 0 | solid |
| 0 | 2 | 9600 GFSK IL2P+CRC | B→A | 5/5 | 626 ms | -89.8 dBm | 5 | 0 | solid |
| 0 | 3 | 9600 4FSK IL2P+CRC | A→B | 0/5 | n/a | -93.1 dBm | 0 | 0 | dead |
| 0 | 3 | 9600 4FSK IL2P+CRC | B→A | 5/5 | 633 ms | -89.8 dBm | 5 | 0 | solid |
| 0 | 4 | 4800 GFSK IL2P+CRC | A→B | 5/5 | 751 ms | -91.2 dBm | 5 | 0 | solid |
| 0 | 4 | 4800 GFSK IL2P+CRC | B→A | 5/5 | 747 ms | -89.6 dBm | 5 | 0 | solid |
| 0 | 5 | 3600 QPSK IL2P+CRC | A→B | 5/5 | 834 ms | -89.6 dBm | 5 | 0 | solid |
| 0 | 5 | 3600 QPSK IL2P+CRC | B→A | 5/5 | 832 ms | -89.4 dBm | 5 | 0 | solid |
| 0 | 7 | 1200 AFSK IL2P+CRC | A→B | 5/5 | 1228 ms | -91.3 dBm | 5 | 0 | solid |
| 0 | 7 | 1200 AFSK IL2P+CRC | B→A | 5/5 | 1221 ms | -89.5 dBm | 5 | 0 | solid |
| 0 | 8 | 300 BPSK IL2P+CRC | A→B | 5/5 | 3227 ms | -89.9 dBm | 5 | 0 | solid |
| 0 | 8 | 300 BPSK IL2P+CRC | B→A | 5/5 | 3237 ms | -89.4 dBm | 5 | 0 | solid |
| 0 | 9 | 600 QPSK IL2P+CRC | A→B | 4/5 | 1945 ms | -89.7 dBm | 4 | 0 | MARGINAL |
| 0 | 9 | 600 QPSK IL2P+CRC | B→A | 5/5 | 1928 ms | -89.4 dBm | 5 | 0 | solid |
| 0 | 10 | 1200 BPSK IL2P+CRC | A→B | 5/5 | 1227 ms | -89.9 dBm | 5 | 0 | solid |
| 0 | 10 | 1200 BPSK IL2P+CRC | B→A | 5/5 | 1215 ms | -89.4 dBm | 5 | 0 | solid |
| 0 | 11 | 2400 QPSK IL2P+CRC | A→B | 5/5 | 909 ms | -89.7 dBm | 5 | 0 | solid |
| 0 | 11 | 2400 QPSK IL2P+CRC | B→A | 4/5 | 900 ms | -89.2 dBm | 4 | 0 | MARGINAL |
| 0 | 14 | 300 AFSKPLL IL2P+CRC | A→B | 5/5 | 3256 ms | -91.3 dBm | 5 | 0 | solid |
| 0 | 14 | 300 AFSKPLL IL2P+CRC | B→A | 5/5 | 3222 ms | -89.7 dBm | 5 | 0 | solid |
| 1 | 1 | 19200 4FSK IL2P+CRC | A→B | 5/5 | 553 ms | -89.9 dBm | 5 | 0 | solid |
| 1 | 1 | 19200 4FSK IL2P+CRC | B→A | 5/5 | 555 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 2 | 9600 GFSK IL2P+CRC | A→B | 5/5 | 635 ms | -89.7 dBm | 5 | 0 | solid |
| 1 | 2 | 9600 GFSK IL2P+CRC | B→A | 5/5 | 625 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 3 | 9600 4FSK IL2P+CRC | A→B | 5/5 | 630 ms | -90.0 dBm | 5 | 0 | solid |
| 1 | 3 | 9600 4FSK IL2P+CRC | B→A | 5/5 | 629 ms | -89.1 dBm | 5 | 0 | solid |
| 1 | 4 | 4800 GFSK IL2P+CRC | A→B | 5/5 | 784 ms | -89.8 dBm | 5 | 0 | solid |
| 1 | 4 | 4800 GFSK IL2P+CRC | B→A | 5/5 | 766 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 5 | 3600 QPSK IL2P+CRC | A→B | 5/5 | 833 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 5 | 3600 QPSK IL2P+CRC | B→A | 5/5 | 826 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 7 | 1200 AFSK IL2P+CRC | A→B | 4/5 | 1232 ms | -89.6 dBm | 4 | 0 | MARGINAL |
| 1 | 7 | 1200 AFSK IL2P+CRC | B→A | 5/5 | 1254 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 8 | 300 BPSK IL2P+CRC | A→B | 5/5 | 3222 ms | -89.3 dBm | 5 | 0 | solid |
| 1 | 8 | 300 BPSK IL2P+CRC | B→A | 5/5 | 3238 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 9 | 600 QPSK IL2P+CRC | A→B | 4/5 | 1928 ms | -89.2 dBm | 4 | 0 | MARGINAL |
| 1 | 9 | 600 QPSK IL2P+CRC | B→A | 5/5 | 1916 ms | -89.4 dBm | 5 | 0 | solid |
| 1 | 10 | 1200 BPSK IL2P+CRC | A→B | 5/5 | 1226 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 10 | 1200 BPSK IL2P+CRC | B→A | 5/5 | 1217 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 11 | 2400 QPSK IL2P+CRC | A→B | 4/5 | 910 ms | -89.2 dBm | 4 | 0 | MARGINAL |
| 1 | 11 | 2400 QPSK IL2P+CRC | B→A | 5/5 | 916 ms | -89.2 dBm | 5 | 0 | solid |
| 1 | 14 | 300 AFSKPLL IL2P+CRC | A→B | 5/5 | 3182 ms | -89.7 dBm | 5 | 0 | solid |
| 1 | 14 | 300 AFSKPLL IL2P+CRC | B→A | 5/5 | 3223 ms | -89.1 dBm | 5 | 0 | solid |

Findings (against the session-6 pre-retap table above):

1. **The retap brought the GFSK direct-FSK modes up — everywhere.** Mode 2 (9600 GFSK)
   went 0/5-everywhere → **5/5 in all four cells, including the narrow channel**; mode 4
   (4800 GFSK) likewise. The session-6 readings ("occupied bandwidth", then "untuned
   pots") are retired: the R5 tap was the killer all along, and 9600 GFSK decodes in a
   12.5 kHz programmed channel on this bench link (38 dB SNR through a pad —
   adjacent-channel behaviour on air is a separate question).
2. **The newly-surveyed 4FSK modes behave exactly per their published bandwidths**:
   19200 4FSK (a 25 kHz mode) is dead on narrow / solid both ways on wide at ~554 ms mean
   latency — the fastest working cells this rig has produced; 9600 4FSK (a 12.5 kHz mode)
   is solid on wide and B→A-solid on narrow.
3. **The one hard directional cell is 9600 4FSK A→B on narrow** (0/5, receiver-side RSSI
   −93.1 dBm — the lowest of the whole run), and 4 of the 5 MARGINAL cells are A→B
   (600 QPSK on both widths, 1200 AFSK IL2Pc and 2400 QPSK on wide; one B→A 4/5 blip on
   ch-0 2400 QPSK). TNC A's TX side remains the deviation-tuning target — now with its
   tightest-margin reproducer identified.
4. **IL2P counters stayed truthful in all 44 cells** (`IL2PRxPkts` Δ = decode count,
   `IL2PRxUnCr` Δ0 throughout — losses are still sync misses, not FEC exhaustion).
5. Latency continues to scale with bit rate: ~554 ms at 19200 → ~3.2 s at 300 baud for
   the same probe.

Tooling notes from the run: the 3.41 mode-14 firmware byte `0x90` now resolves (catalog
alias shipped this session — the GETALL verify prints "running mode 14 (300 AFSKPLL
IL2P+CRC)" instead of "unrecognised firmware byte 0x90"); the sporadic settle-frame
ACKMODE echo absence persists (seen on both TNCs, logged and tolerated); the rig restore
verified clean (both radios channel 0, both TNCs mode 6).

### The 9600 baseline (deviation-sdm, mode 2 / channel 1) — "before pot tuning"

The deliverable session 6 had to skip — a deviation-sensitive-mode trend baseline — ran
scripted end-to-end (no human): both TNCs mode 2, both radios channel 1; meter =
`/dev/ttyACM1` + `/dev/ttyUSB3` (peer `PDN00002`), tuned = `/dev/ttyACM0` +
`/dev/ttyUSB2` (peer `PDN00001`) — the tuned end is TNC A, the survey's marginal
transmitter, deliberately. Two rounds, pots untouched. Every telegram delivered on send
attempt 1/3 with acknowledged receipts (zero retries); the meter's GETRSSI probe reported
the **new R2-tap idle level: −34.4 dB** (n=5; ≈−34.5 — compare −33.1/−35.8 pre-retap, the
level budget is unchanged). The tuned end's trend table, the record to compare after any
future pot adjustment:

```
  burst   decoded   fec Δ    clip Δ   rssi dBm   level dB   advice
      1     5/5        n/a        0      -89.7      -39.3   OK
      2     5/5        n/a        0      -89.9      -39.5   OK
  level -39.5 dB, level steady (RX audio at the meter end)
```

Meter advice lines: `5/5|fec:na|clip:0|rssi:-89.7|lvl:-39.3 → OK (level -39.3 dB, 4.9 dB
quieting)`; burst 2 added `5.1 dB quieting, level steady`. Two notes for the record:
a *decoding* 9600 GFSK burst at these pot positions reads ≈5 dB of quieting — between the
session-5 AFSK case (≈0, noise-like) and the session-6 *undecodable* 9600 case (9–12 dB,
tone-like) — so the level column now spans a usable dynamic range exactly where deviation
tuning operates; and `AD:OK` at 0 FEC signal (mode 2's fec is n/a on the labelled GETALL)
is the decode-rate + no-clipping verdict, honest but coarse — mode 7 remains the
recommendation when the FEC bracket matters.

Shipped alongside this session (same PR): the advisor's **`SW` "no decode — sweep the
pot" advice state** — session 6's dead-link capture showed `AD:UP` for 0/5 bursts, a
directional lie when a fully-dead burst carries no direction at all (too quiet, too loud
and no-path all read 0/n; this rig's GFSK cells were 0/5 at *any* pot position). Zero
decodes without clipping now advise `SW` (wire token additive; UP/DN/OK unchanged, old
peers parse `SW` as unknown advice, never a direction) — and clipping still wins, because
clipping at 0/5 *is* directional evidence. Plus the **3.41 mode-14 firmware-byte alias
(`0x90` → mode 14)** in `NinoTncCatalog`, closing session 6's "unrecognised firmware
byte" quirk.

## Mode coordination over SDM (2026-07-03, session 8) — the §5.10 mode-agility seed

The radios' FFSK side channel is mode- and channel-agnostic — proven repeatedly in the
sessions above (SDM delivery identical with the TNCs in dead 9600 GFSK, on both channel
widths, at any pot position). This session promoted that observation into the seam Phase 10's
mode agility needs and validated the first **coordinated TNC-mode/radio-channel renegotiation**
end-to-end on the rig.

### What shipped

- **`Packet.Radio.IRadioSideChannel`** — the radio-native small-datagram control plane
  (send/receive + async over-air `DeliveryReceipt` + `MaxPayloadLength` budget), XML-doc'd
  with the capability-gating intent: drivers advertise the machinery
  (`RadioCapabilities.SideChannel`, now on `TaitCcdiRadio`), consumers gate features on a
  live probe (the doctor's SDM check) because programming can still have the feature off.
  `Packet.Radio` gains no Tait dependency — the implementation is
  **`Packet.Radio.Tait.TaitSdmSideChannel`** (the former Tune.Core `TaitSdmChannel`, moved
  down a layer), and `SdmTuningLink` now rides `IRadioSideChannel` (`ITuningLink` unchanged
  for consumers; the payload-budget check now comes from the channel).
- **Mode-coordination protocol** (`Packet.Tune.Core`): new `MODE` telegram verb carrying
  `propose|confirm|reject|commit|sent|report|revert` (`ModeCoordMessage`; every form inside
  the 32-char SDM budget, reasons capped at 9 chars), driven by
  **`ModeCoordinator`/`ModeResponder`** over any `ITuningLink` with the hardware behind an
  `IModeCoordStation` seam (`NinoTncModeCoordStation` = SETHW+16 + settle frame + GETALL
  verify, GO_TO_CHANNEL + verify + retry, tagged probe frames). Key shapes: nothing switches
  until the **commit's delivery receipt** proves both ends hold it; the commit telegram's
  sequence number **tags the probe frames** (`PMODE a<tag> i/n`) so stale probes can't
  validate the wrong attempt; verification is **N probes each way** with decode counts
  exchanged over SDM (`sent`/`report` — the latency column is sender-side send→TX-complete,
  which bench data shows trails the receiver's decode by only ~35–115 ms); any failure past
  the commit **reverts both ends to the session's home mode/channel** and re-verifies the
  home link with a short burst; and the responder carries an **idle watchdog** (default
  150 s) that reverts it home unilaterally when the coordinator goes silent — the recovery
  for the one failure SDM can't coordinate across, a split channel. All wedge guards carry
  over (2 s post-receive in the link; 2.5 s pre-key before any settle frame or probe burst
  that follows an SDM arrival).
- **CLI**: `packet-tune mode-coord --role coordinator|responder --tnc <port> --radio <ccdi>
  --peer <8charId> [--sequence m[@ch],… | --sweep] [--strict-bandwidth] [--channel-width
  narrow|wide] [--home-mode 6] [--home-channel 0] [--probes 5]`. `--sweep` walks every
  IL2P+CRC catalog mode; **lenient by default** (tries everything and lets the probe verdicts
  speak — mode 2 decodes on this rig's narrow channel despite being a 25 kHz mode per the
  wiki); `--strict-bandwidth` skips the wide-only modes (new catalog knowledge:
  `NinoTncCatalog.WideChannelModes` = {0, 1, 2}, wiki-cited) when the current channel is
  narrow. The coordinator always finishes with a coordinated return to home, falling back to
  a local restore when coordination is lost — and the responder ends every exit path
  (BY/cancel/link-loss/watchdog) at home.
- Tests: +38 in `Packet.Tune.Core.Tests` (119 total) — codec round-trips/budget/malformed
  wire, coordinator+responder end-to-end over the in-memory link pair with a fake two-station
  ether (success, channel moves, dead-probe revert with home verify, rejection, both-sided
  switch-failure reverts, confirm timeout, watchdog revert, BY-goes-home, stale-tag probes).

### Hardware validation 1: full `--sweep` on channel 0 (narrow)

Both TNCs 3.41, coordinator = TNC A `/dev/ttyACM0` + `/dev/ttyUSB2` (peer `PDN00001`),
responder = TNC B `/dev/ttyACM1` + `/dev/ttyUSB3` (peer `PDN00002`), 5 probes/direction,
raw logs in `artifacts/hardware-probe/20260703-mode-coord/` (untracked). The sweep
coordinated 8 modes cleanly — and then a **live unplanned failure** exercised every backstop
(next subsection). Final coordinator table, verbatim:

```
| Ch | Mode | Name | Dir | Decoded | Mean TX latency | Outcome |
|---:|-----:|------|-----|--------:|----------------:|---------|
| 0 | 1 | 19200 4FSK IL2P+CRC | C→R | 0/5 | 531 ms | PROBE DEAD — reverted, home link alive |
| 0 | 1 | 19200 4FSK IL2P+CRC | R→C | 0/5 | 531 ms | PROBE DEAD — reverted, home link alive |
| 0 | 2 | 9600 GFSK IL2P+CRC | C→R | 5/5 | 602 ms | switched (solid both ways) |
| 0 | 2 | 9600 GFSK IL2P+CRC | R→C | 5/5 | 603 ms | switched (solid both ways) |
| 0 | 3 | 9600 4FSK IL2P+CRC | C→R | 0/5 | 612 ms | PROBE DEAD — reverted, home link alive |
| 0 | 3 | 9600 4FSK IL2P+CRC | R→C | 5/5 | 613 ms | PROBE DEAD — reverted, home link alive |
| 0 | 4 | 4800 GFSK IL2P+CRC | C→R | 5/5 | 744 ms | switched (solid both ways) |
| 0 | 4 | 4800 GFSK IL2P+CRC | R→C | 5/5 | 744 ms | switched (solid both ways) |
| 0 | 5 | 3600 QPSK IL2P+CRC | C→R | 5/5 | 835 ms | switched (solid both ways) |
| 0 | 5 | 3600 QPSK IL2P+CRC | R→C | 5/5 | 825 ms | switched (solid both ways) |
| 0 | 7 | 1200 AFSK IL2P+CRC | C→R | 4/5 | 1212 ms | switched (MARGINAL) |
| 0 | 7 | 1200 AFSK IL2P+CRC | R→C | 5/5 | 1212 ms | switched (MARGINAL) |
| 0 | 8 | 300 BPSK IL2P+CRC | C→R | 5/5 | 3080 ms | switched (solid both ways) |
| 0 | 8 | 300 BPSK IL2P+CRC | R→C | 5/5 | 3080 ms | switched (solid both ways) |
| 0 | 9 | 600 QPSK IL2P+CRC | C→R | 5/5 | 1831 ms | switched (solid both ways) |
| 0 | 9 | 600 QPSK IL2P+CRC | R→C | 5/5 | 1831 ms | switched (solid both ways) |
| 0 | 10 | 1200 BPSK IL2P+CRC | — | n/a | n/a | commit undelivered |
| 0 | 11 | 2400 QPSK IL2P+CRC | — | n/a | n/a | COORDINATION LOST |
| 0 | 14 | 300 AFSKPLL IL2P+CRC | — | n/a | n/a | COORDINATION LOST |
| 0 | 6 | 1200 AFSK AX.25 | — | n/a | n/a | COORDINATION LOST |
```

The probed cells reproduce the post-retap survey exactly: mode 1 dead both ways on narrow
(25 kHz mode — the protocol reverted and confirmed the home link 2/2 each time), mode 3 dead
A→B only (TNC A's known tightest-margin cell — one dead direction correctly fails the
attempt), mode 7 the familiar A→B 4/5 marginal, everything else solid; TX latencies scale
with bit rate as always. Each mode change is propose→confirm→commit→switch→probe×2→report,
all receipted over SDM, ~60–90 s per mode at these settings.

### The unplanned live failure — and every backstop firing

Between mode 9's success and mode 10's commit, the coordinator radio's **`ChannelBusy`
latched true**: `p0205` (busy) had arrived without its matching `p0206` (clear) — after the
run, a fresh process measured the channel genuinely idle (raw RSSI −128.2 dBm = the squelched
noise floor), so this was a **missed/lost DCD-clear PROGRESS edge latching the driver's
edge-derived busy state**, not RF. The consequence chain, exactly as designed, verbatim from
the two logs:

```
coord:  coord: responder confirmed — committing
coord:  coord: commit delivery unconfirmed (channel still busy after 30s — refusing to
        transmit over it) — staying home, sending revert
coord:  coord: revert notification undelivered (channel still busy after 30s — refusing to
        transmit over it) — responder watchdog backstops
coord:  → commit undelivered
…
rsp:    responder: confirming mode 10 (1200 BPSK IL2P+CRC)
rsp:    responder: WATCHDOG — nothing heard for 150s while away from home; reverting
rsp:    station: SETHW 6+16 → GETALL running mode 6 (1200 AFSK AX.25)
rsp:    responder: at home (mode 6, channel 0)
…
coord:  home coordination failed — restoring this end locally
coord:  station: SETHW 6+16 → GETALL running mode 6 (1200 AFSK AX.25)
coord:  station: GO_TO_CHANNEL 0 → reports kind '0' channel '0'
```

The link **never transmitted over what it believed was a busy channel**, the ambiguous
commit was answered with stay-home + best-effort revert, the responder's idle watchdog
brought its end home unilaterally (it had been left holding mode 9 + an unconfirmed
mode-10 proposal), and the coordinator's CLI fall-back restored its own end locally — **both
ends converged on home (mode 6 / channel 0, verified) with no operator and no shared
channel state.** A deliberately-planned fault injection could not have staged a better
test. Driver follow-up filed below: an edge-derived DCD state needs a staleness escape.

### Hardware validation 2: coordinated channel switch (narrow → wide → back)

`--sequence 1@1,3@1,6@0` (fresh processes both ends): a **coordinated mode+channel switch**
onto the wide channel with mode 1 (19k2 — the mode that is *dead* on narrow, so this only
works if the radios really moved), a second wide mode, and a coordinated return to
channel 0 / mode 6. Every leg first try, exit 0, responder ended at home on BY. Verbatim
table:

```
| Ch | Mode | Name | Dir | Decoded | Mean TX latency | Outcome |
|---:|-----:|------|-----|--------:|----------------:|---------|
| 1 | 1 | 19200 4FSK IL2P+CRC | C→R | 5/5 | 531 ms | switched (solid both ways) |
| 1 | 1 | 19200 4FSK IL2P+CRC | R→C | 5/5 | 530 ms | switched (solid both ways) |
| 1 | 3 | 9600 4FSK IL2P+CRC | C→R | 5/5 | 606 ms | switched (solid both ways) |
| 1 | 3 | 9600 4FSK IL2P+CRC | R→C | 5/5 | 606 ms | switched (solid both ways) |
| 0 | 6 | 1200 AFSK AX.25 | C→R | 5/5 | 984 ms | switched (solid both ways) |
| 0 | 6 | 1200 AFSK AX.25 | R→C | 5/5 | 986 ms | switched (solid both ways) |
```

Notable in the logs: the commit-before-move discipline holds across the channel boundary
(the responder's `GO_TO_CHANNEL 1 → reports … channel '1'` verify lands before the
coordinator's probes), and the SDM link carried the whole conversation across **both**
channel widths within one session — including the propose/confirm for the return leg sent
*on the wide channel* to coordinate the move back to narrow. Mode 3 (9600 4FSK), the
narrow-channel A→B dead cell, is 5/5 both ways on wide — matching the survey.

### Hardware validation 3: the failure-revert demo (mode 1 on narrow)

`--sequence 1` on channel 0: propose 19200 4FSK on a narrow channel — a switch that
coordinates perfectly and then cannot carry traffic. Coordinator output, verbatim (take 2;
take 1 is below):

```
  ── coordinate: mode 1 (19200 4FSK IL2P+CRC) ──
  coord: proposing mode 1 (19200 4FSK IL2P+CRC)
  coord: responder confirmed — committing
  station: SETHW 1+16 → GETALL running mode 1 (19200 4FSK IL2P+CRC)
  coord: transmitting 5 probe frames (tag a3)
  coord: C→R 0/5 decoded at the responder
  coord: R→C 0/5 decoded here
  coord: probe verdict DEAD in at least one direction — reverting both ends
  station: settle frame TX-completion not echoed within 8 s (continuing)
  station: SETHW 6+16 → GETALL running mode 6 (1200 AFSK AX.25)
  coord: reverted to home (mode 6, channel 0)
  coord: home link alive (2/2 decoded at the responder)
  → PROBE DEAD — reverted, home link alive

| Ch | Mode | Name | Dir | Decoded | Mean TX latency | Outcome |
|---:|-----:|------|-----|--------:|----------------:|---------|
| 0 | 1 | 19200 4FSK IL2P+CRC | C→R | 0/5 | 531 ms | PROBE DEAD — reverted, home link alive |
| 0 | 1 | 19200 4FSK IL2P+CRC | R→C | 0/5 | 531 ms | PROBE DEAD — reverted, home link alive |
```

and the responder's half: `revert requested (probedead) — returning to home` → `at home
(mode 6, channel 0)` → `2/2 probe frames decoded — reporting` → clean BY exit. Note the
probes themselves report the failure honestly — the dead-mode probes even *keyed* fine
(531 ms TX latency; the 19k2 carrier just doesn't demodulate in 12.5 kHz) — and both SDM
verdict exchanges rode the same radios that couldn't pass a single AX.25 frame at that
moment.

**Take 1 of this demo found two real bugs** (the reason this section says "take 2"): the
run reverted correctly but reported `HOME LINK NOT CONFIRMED` (exit 1 — correctly loud)
because the responder opened its home-verify probe window only *after* its home apply, and
that apply had blocked the full 15 s settle-echo timeout (the known sporadic
missing-TX-completion quirk), so the coordinator's 2 verify probes sailed past a
not-yet-open counter. Fixed: the responder opens the count window immediately on the
revert telegram (counting is passive), and the settle-echo wait dropped 15 s → 8 s (the
echo either arrives within ~1 frame-time + TXDELAY or never). A regression test pins the
ordering (`count:` before `mode:` in the station-call trace).

## Radio-health trending (2026-07-03, session 9): `TaitRadioHealthMonitor` + `packet-tune radio-health`

Backlog item 3 (periodic radio-health sampling) shipped as a library seam plus a CLI, built on
the Tait service-doc research (sources below): **`Packet.Radio.Tait.TaitRadioHealthMonitor`**
samples on an interval — averaged RSSI (CCTM 063, skipped while transmitting: own-RSSI reads
the muted receiver), PA temperature (CCTM 047), and the raw forward/reverse power detectors
(CCTM 318/319) — classifying the detector readings by the radio's own PTT reports
(`TransmitterStateChanged`): idle readings feed rolling **idle-offset medians**, transmit
readings become **offset-corrected fwd/rev + a reverse/forward ratio**, and a keying edge
triggers an immediate extra sample (150 ms PA settle) so short keyings are captured between
ticks. Samples surface as typed events plus a rolling min/median/max window summary
(`Summarize()`); a mid-sample keying edge discards that tick's detector readings rather than
corrupt either pool. Library-only for now — node-story wiring is a later, optional element.

**Why the ratio is a TREND, never VSWR** (the service-doc answer to session 2's shrug): CCTM
318/319 are the raw DC millivolts of the detector diodes on the directional coupler; detector
voltage goes as √P (the calibration DB stores "Power Level Sqrt" constants); Tait never
computes VSWR anywhere in its service tooling — the reverse detector exists for mismatch
*protection* (foldback), and the go/no-go is specced **only at High power** (25 W bodies, B1:
fwd 1100–2000 mV, rev < 500 mV into a good 50 Ω load; no per-level table exists). At low power
the reverse reading is dominated by detector-diode knee/offset and coupler directivity floor —
so the monitor subtracts idle offsets and exposes the corrected ratio strictly as a
per-station trend; consumers alert on **change**. Sources (all cited in the XML docs):
[TM8100/TM8200 Service Manual MMA-00005-05](https://www.repeater-builder.com/tait/pdf/tait-tm8100-tm8200-service-manual.pdf)
(CCTM chapter, High-power go/no-go tables),
[TM8000 CCDI Protocol Manual MMA-00038-02](https://manuals.repeater-builder.com/2007/TM8000/TM8000%20CCDI%20Protocol%20Manual/TM8000%20CCDI%20Protocol%20Manual%20MMA-00038-02.pdf)
(QUERY-5 relays exactly CCTM 047/063/064/318/319; mirrored at
[wiki.oarc.uk](https://wiki.oarc.uk/_media/radios:tm8100-protocol-manual.pdf)),
[TM8100 Calibration Application User's Manual](https://manuals.repeater-builder.com/2007/TM8000/TM8100%20Calibration_Application_User's_Manual/TM8100_Calibration_Application_User's_Manual.pdf)
(Coupler Cal Power, √-power constants),
[TN-1038b](https://manuals.repeater-builder.com/2007/TECHNOTE/TM8000/TN-1038b_SR_TM8100%20Firmware%20v2.09%20and%20Programming%20Applicatio.pdf)
(318/319/064 wire examples, RSSI range),
[TN-1011](https://manuals.repeater-builder.com/2007/TECHNOTE/TM8000/TN-1011_Terminal_Application.pdf)
(fullest public CCTM catalogue).

**CLI**: `packet-tune radio-health <ccdi> [--interval 10] [--duration 60] [--key-once [s]]` —
live table + summary; `--key-once` keys ONCE via FUNCTION 9 at the channel's programmed power,
hard-capped at 3 s. Exercised on the rig (TM8110 s/n 19925328, `/dev/ttyUSB2`, radios idle on
channel 0), one 2 s keyed sample:

```
  time      tx   rssi dBm   pa °C   pa mV   fwd mV   rev mV   fwd-idle   rev-idle   rev/fwd
  11:33:48  --     -128.4      27     471        0        0          ·          ·         ·
  11:33:50  TX          ·      28     469      388      164        388        164     0.423
  11:33:53  --     -128.0      28     469        0        0          ·          ·         ·
  11:33:58  --     -127.9      27     471       15        0          ·          ·         ·
```

Numbers line up with everything previously measured: idle RSSI at the −128 dBm squelched
floor, PA 27–28 °C / ~470 mV, keyed VeryLow fwd/rev 388/164 mV (session 2 read 388/172), and
the sporadic 15 mV idle forward blip (session 1's "15/0 mV idle") — the reading the idle-offset
subtraction exists for. Rig left unkeyed, both radios verified channel 0, TNCs untouched
(mode 6). Tests: 7 new scripted-`FakeSerialIo` driver tests (offset bookkeeping, keyed-edge
sample, mid-sample-keying discard, ratio floor, failure resilience, window bounds/summary).

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
6. **§5.Y items 3, 5–6** (SNR-vs-level calibration, 1000-iteration soak, mistune
   degradation) — the rig and the tooling are now in place to script them. Item 4's
   mode survey is done for this rig and re-run post-retap (see §The R5→R2 retap): the
   GFSK modes are up; what remains level-sensitive is **TNC A's TX side** (the A→B
   marginals and the 9600-4FSK-A→B-on-narrow dead cell) — run a deviation-tuning
   session against TNC A with the mode-2/channel-1 baseline as the before-picture,
   then re-survey the marginal cells.
7. **Doctor probe for the tap-point signature** (from §The R5→R2 retap): one audio-band
   + one direct-FSK loopback burst; audio-band-passes + direct-FSK-dead-both-ways ⇒
   "check the radio's RX tap point (post-voice-filter tap)" — would have saved two
   sessions here.
8. Housekeeping: `docs/releasing.md`'s "six published packages" list is stale against the
   `publish-libs.yml` matrix (now 13); reconcile on the next release pass.
9. **Stale-DCD escape for the busy gate** (from session 8's live failure): `TaitCcdiRadio`'s
   `ChannelBusy` is edge-derived, and one lost `p0206` latched it busy for the rest of the
   process — every SDM send then (correctly) refused to key, and only the watchdogs
   converged the session. Give the driver (or `SdmTuningLink`'s clear-wait) a staleness
   check: busy for implausibly long with no edges → re-validate with a solicited query
   (RSSI vs noise floor is a serviceable proxy) before trusting the latch.
10. **Session epochs for coordination protocols** (session 8): telegram seq numbers restart
    with the process and the link dedupe window would swallow a restarted coordinator's
    first telegrams at a long-lived responder. One-process-per-session is the bench rule;
    a node-resident responder needs an epoch/session id (V2 wire concern). See
    [`radio-side-channel-mode-agility.md`](radio-side-channel-mode-agility.md).
11. The **settle-frame TX-completion echo goes missing far more often under mode-coord than
    it did under mode-survey** (session-8 counts: 9/12 applies on the sweep coordinator,
    6/12 on its responder, ~1/4 on the shorter runs; both TNCs, 3.41) — worth a look at
    what differs (timing relative to SETHW? echo swallowed during the mode transition?)
    before trusting the echo for anything more than logging.
