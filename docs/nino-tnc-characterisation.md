# NinoTNC pair — measured characterisation

This document captures empirical measurements from the back-to-back NinoTNC
pair (audio-cross-wired, USB-attached) on the dev host. Numbers are
calibration data — they help us pick sensible defaults, evaluate adaptive
algorithms, and detect regressions. They are **not** universally
applicable: real RF links have noise, voltage standing wave ratios that
shift over temperature, multi-path, etc. that this benchtop setup has
none of.

Method: [`tools/Packet.NinoTnc.Spike`](../tools/Packet.NinoTnc.Spike/Soak.cs)
`soak` sub-commands. Reports land under `artifacts/nino-tnc-soak/<ts>/`.

## Hardware

- Two NinoTNC N9600A boards.
- Connection: USB-CDC ACM to the dev host (Windows 11). VID/PID
  `04D8:00DD` (Microchip USB-CDC reference design).
- Audio: TX↔RX cross-wired between the boards (cable, no RF stage).
- MODE DIPs: `1111` ("Set from KISS"). All mode selection via
  `KISS SETHW` from software with the `+16` non-persist offset.
- TXDELAY pots: minimum. KISS TXDELAY parameter is therefore honoured.
- Firmware: 3.44 (board-reported via TX-Test frame).

## What we know after the 2026-05-14 campaign

### Mode compatibility (1200 AFSK → 19200 4FSK)

All seven candidate modes round-trip cleanly over the audio cross-wire:

| Mode | Name | bps | A↔B success | Notes |
|---:|---|---:|---:|---|
| 6 | 1200 AFSK AX.25 | 1200 | 5/5 both | the historical packet-radio mode |
| 7 | 1200 AFSK IL2P+CRC | 1200 | 5/5 both | IL2P-framed at AFSK rates |
| 12 | 300 AFSK AX.25 | 300 | 5/5 + 4/5 | one A→B timeout in 5 |
| 0 | 9600 GFSK AX.25 | 9600 | 5/5 both | legacy G3RUH-style 9600 |
| 2 | 9600 GFSK IL2P+CRC | 9600 | 5/5 both | recommended modern 9600 |
| 3 | 9600 4FSK | 9600 | 5/5 both | 4FSK at 9600 |
| 1 | 19200 4FSK | 19200 | 5/5 both | fastest mode in the catalog |

The audio cross-wire is high-bandwidth enough to support every mode in
the catalog up to 19200 4FSK. On a real FM-voice link, the audio
bandwidth caps out around 2.5–3 kHz and the higher modes are out of
reach. **For interop tests on real RF, plan for mode 6 (most lenient)
or mode 2 (good 9600 baseline).**

### TXDELAY floor on this hardware (mode 6)

The KISS spec default is TXDELAY=50 (500 ms). On this audio link, the
modem maintained 10/10 success all the way down to:

```
| TXDELAY units | TXDELAY ms | A→B 10/10 | B→A 10/10 |
|---:|---:|---:|---:|
|  1 |  10 ms | yes | yes |
```

That tells us:
- The audio path adds essentially no preamble-lock latency.
- The modem's keying chain is fast — sub-10 ms.
- Any defensive TXDELAY > ~10 ms in this setup is pure airtime tax.

On real RF this number will rise significantly (every transmitter has a
key-up delay, and FM receivers need preamble to AGC and lock). The
adaptive estimator's job is to discover the *actual* per-link minimum,
not to use the spec-default.

### ACKMODE concurrency

| N submitted concurrently | All echoed? | Min ms | Mean ms | Max ms |
|---:|---:|---:|---:|---:|
| 2 | yes | 224 | 335 | 446 |
| 4 | yes | 226 | 559 | 892 |
| 8 | yes | 225 | 1000 | 1773 |

The N=1 batch took 15 s on the campaign run — likely a first-TX warmup
artefact on a freshly-opened port; subsequent batches at N=2,4,8 all
returned in well under 2 s for the slowest member. Worth a follow-up
investigation if it reproduces.

### Bidirectional simultaneous send (half-duplex contention)

10 rounds of A and B sending the same instant: 9/10 both peers received.
CSMA + the modem's built-in deferral is doing its job; one collision in
ten is consistent with KISS PERSIST=63 / SLOTTIME=10 ms defaults.

### Adaptive estimator (`TxDelayHillClimbEstimator`) — live run

40 frames with aggressive tuning (`SuccessesPerStepDown=3`, `StepUnits=2`,
`MinTxDelay=2`) starting from TXDELAY=50:

- All 40 ACKMODE round trips succeeded.
- Estimator walked TXDELAY 50 → 24 across the 40 frames.
- Final 240 ms is still wildly above the 10 ms floor — the algorithm
  is conservative. Tuning for production: a larger `StepUnits` or
  smaller `SuccessesPerStepDown` would converge faster; we'll set
  defaults after a real-RF run.

### Throughput (back-to-back, no airtime spacing)

Mode 6: 20 × 200 B frames in ~3.3 s yielded only 1 echoed-and-uniqued
frame — most were swallowed somewhere between the KISS layer and the
TNC's TX queue. This is a **finding**: rapid-fire submission of plain
KISS data frames does not produce rapid air transmission; the TNC drops
or queues silently. For sustained throughput the host needs to:

1. Use ACKMODE (so we know when one frame is actually on the air).
2. Pace submissions to one-at-a-time, or
3. Build a multi-outstanding pipeline with explicit ACK correlation.

The driver's `SendFrameWithAckAsync` already supports option (3). The
upcoming session-layer integration should default to ACK-paced TX.

### Long-idle stability

2-minute idle watch with both modems open and in mode 6: no spurious
inbound frames, no pump errors. Driver is stable while quiescent.

## Caveats

- **Single host, single setup.** Findings reflect the dev box's specific
  USB ports and one cable. Different USB controllers may produce
  different TXDELAY floors.
- **No noise, no fading.** A real link will see retransmits where this
  setup sees clean ACKs.
- **Firmware v3.44.** Future firmware may change TX behaviour.

## How to re-run

```sh
$env:PACKETNET_NINOTNC_PORTS = "COM6,COM8"
dotnet run --project tools/Packet.NinoTnc.Spike -- soak all COM6 COM8
# or, for the longer "I have hours" run with stress + per-mode TXDELAY:
dotnet run --project tools/Packet.NinoTnc.Spike -- soak marathon COM6 COM8
```

Output lands under `artifacts/nino-tnc-soak/<timestamp>/results.md`.
