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
