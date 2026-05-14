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
Last campaign: 2026-05-14 `soak marathon` run.

## Hardware

- Two NinoTNC N9600A boards.
- Connection: USB-CDC ACM to the dev host (Windows 11). VID/PID
  `04D8:00DD` (Microchip USB-CDC reference design).
- Audio: TX↔RX cross-wired between the boards (cable, no RF stage).
- MODE DIPs: `1111` ("Set from KISS"). All mode selection via
  `KISS SETHW` from software with the `+16` non-persist offset.
- TXDELAY pots: minimum. KISS TXDELAY parameter is therefore honoured.
- Firmware: 3.44 (board-reported via TX-Test frame).

## Headlines from the 2026-05-14 marathon

### Mode compatibility (1200 AFSK → 19200 4FSK)

All seven candidate modes round-trip cleanly on the bench audio
cross-wire:

| Mode | Name | bps | A↔B success | Mean RTT ms (A→B / B→A) | Notes |
|---:|---|---:|---:|---|---|
| 6 | 1200 AFSK AX.25 | 1200 | 5/5 both | 1225 / 664 | the historical packet-radio mode |
| 7 | 1200 AFSK IL2P+CRC | 1200 | 5/5 both | 1411 / 1138 | IL2P-framed at AFSK rates |
| 12 | 300 AFSK AX.25 | 300 | 99/100 + 100/100 | 2579 / 2600 | see [§Intermittent demod wedge](#intermittent-demod-wedge--unreproducible) |
| 0 | 9600 GFSK AX.25 | 9600 | 5/5 both | 912 / 878 | legacy G3RUH-style 9600 |
| 2 | 9600 GFSK IL2P+CRC | 9600 | 5/5 both | 509 / 374 | recommended modern 9600 |
| 3 | 9600 4FSK | 9600 | 5/5 both | 935 / 746 | 4FSK at 9600 |
| 1 | 19200 4FSK | 19200 | 5/5 both | 510 / 297 | fastest mode in the catalog |

**For real-RF interop tests, plan for mode 6 (most lenient) or mode 2
(good 9600 baseline).** The audio cross-wire is high-bandwidth enough
to support every mode; on a real FM-voice link the higher modes are
out of reach.

### Intermittent demod wedge — unreproducible

The marathon's 5-frame mode-12 row read "4/5 A→B" and got flagged as
flaky. Larger samples (N=100 per direction) reveal one robust finding
and one that we have since retracted.

**Robust finding — first-frame-after-SetMode artefact.** In every
A→B run across every TXDELAY tried, the failure is the same: the
very first frame (index 0) is lost. Every other frame in the run is
fine. The 700 ms post-`SetMode` settle this probe uses is *not*
enough to guarantee the first frame is received on the partner. The
`tools/Packet.NinoTnc.Spike` `ack-warmup-probe` shows the same
first-frame fragility at mode 6 when there's a backed-up TX queue.
Both point at "warm up the modem before the first measured frame"
as the right discipline for tests and the session layer.

**Retracted — "mode 12 catastrophic at TXDELAY=100" claim.** One
probe in the marathon saw a B→A run go 4/100 at mode 12 +
TXDELAY=100 (frames 5–99 lost in a contiguous run). That data point
is real, but the interpretation we hung on it — that mode 12's
AFSK-without-PLL demod path was specifically vulnerable to a
particular preamble length — does not hold up. A focused follow-up
investigation on 2026-05-14 PM (~30 short trials across baseline,
TX-queue back-pressure, SETHW thrash chains, same-port mode swaps,
close+reopen mode swaps, TXDELAY changes mid-mode, persistent vs.
non-persistent SETHW, and a dense TXDELAY ladder around the values
that have historically fired) **could not reproduce the wedge on
demand**, and the one fire that did occur was in **mode 14**, not
mode 12. The per-trial rate is ~5–15 % with very high run-to-run
variance — sometimes a 30-trial session hits several, sometimes a
30-trial session is clean.

We have no substantiated theory for the wedge and no reliable
trigger. **It is not mode-12-specific** on the evidence we have,
and we are not actively investigating further. The historical
data tables that follow are kept as a record of what was observed
but should be read as point-in-time measurements, not as a
characterisation of mode 12.

| TXDELAY | A→B success | B→A success | Failure indexes |
|---:|---:|---:|---|
| 20 ms | 99/100 | 100/100 | A→B index 0 |
| 50 ms | 99/100 | 100/100 | A→B index 0 |
| 100 ms | 99/100 | **4/100** | A→B index 0 ; B→A 0, 5–99 |

| Mode | Demod / framing | TXDELAY=500 ms | TXDELAY=1000 ms |
|---:|---|---|---|
| 12 | AFSK / AX.25 | 49/50 A→B (first only), 50/50 B→A | 49/50 A→B (first only), 50/50 B→A |
| 13 | AFSKPLL / IL2P | 49/50 A→B (idx 9), 50/50 B→A | 50/50 both |
| 14 | AFSKPLL / IL2P+CRC | 50/50 A→B, 49/50 B→A (first only) | 50/50 both |

### Per-mode TXDELAY floor

Walking TXDELAY down a ladder `{50, 30, 20, 15, 10, 8, 5, 3, 2, 1}` per
mode, 10 frames each direction:

| Mode | Name | bps | Min TXDELAY 10/10 | Min ms | Note |
|---:|---|---:|---:|---:|---|
| 6 | 1200 AFSK AX.25 | 1200 | 1 | 10 | |
| 7 | 1200 AFSK IL2P+CRC | 1200 | 1 | 10 | |
| 12 | 300 AFSK AX.25 | 300 | — | — | breaks at TXDELAY=50 with 9/10 |
| 0 | 9600 GFSK AX.25 | 9600 | 1 | 10 | |
| 2 | 9600 GFSK IL2P+CRC | 9600 | 1 | 10 | |
| 3 | 9600 4FSK | 9600 | 1 | 10 | |
| 1 | 19200 4FSK | 19200 | 1 | 10 | |

The KISS spec default of TXDELAY=50 (500 ms) is **50× over-conservative
on this audio path**. Six of the seven modes maintain 100 % success at
TXDELAY=1 (10 ms). Mode 12 missed one frame at the spec default in this
run — consistent with the intermittent demod wedge described above,
not with a TXDELAY floor.

On real RF this floor will rise significantly (transmitter key-up
delays, receiver AGC + sync acquisition). The adaptive estimator's
job is to discover the *actual* per-link minimum, not to use the
spec-default.

### Payload size sweep (mode 6)

| INFO bytes | A→B success | A→B mean ms | B→A success | B→A mean ms |
|---:|---:|---:|---:|---:|
| 1 | 5/5 | 528 | 5/5 | 814 |
| 16 | 5/5 | 1169 | 5/5 | 616 |
| 64 | 5/5 | 1467 | 5/5 | 1112 |
| 128 | 5/5 | 1571 | 5/5 | 1486 |
| 200 | 5/5 | 2120 | 5/5 | 1993 |
| 230 | 5/5 | 2386 | 5/5 | 2173 |

The 230-byte frame's air time at 1200 bps is `(230 + 17 header + 2
FCS) * 8 / 1200 ≈ 1.66 s`. RTT of ~2.4 s allows ~700 ms for TX-delay
+ key-up + receive-side decode + ACK echo — consistent with the
slower TXDELAYs we used here.

### Binary payload (KISS escape stress)

30 frames of pseudo-random AX.25 INFO bytes biased to ~60 % `0xC0` /
`0xDB` / `0xDC` / `0xDD` (FEND/FESC/TFEND/TFESC), to exercise the KISS
escape path through the actual driver and modem:

- **30/30 (100 %) round-trip success.** Escape coding is solid.

### Sustained throughput, ACK-paced

20 frames of 200-byte AX.25 INFO sent A→B in ACKMODE: each
`SendFrameWithAckAsync` awaits the TX-completion echo before the next
is queued. TXDELAY=5 (50 ms) on every mode.

| Mode | Frames | All ACKed? | Effective B/s | Efficiency |
|---:|---:|---:|---:|---:|
| 6 (1200 AFSK AX.25) | 20 | yes | 87 | 58 % |
| 7 (1200 AFSK IL2P+CRC) | 20 | yes | 78 | 52 % |
| 12 (300 AFSK AX.25) | 20 | yes | 34 | 91 % |
| 2 (9600 GFSK IL2P+CRC) | 20 | yes | 346 | 29 % |

**Finding:** the ACK-pacing pattern is the right one for the
session-layer integration. Every frame ACKs on the first try at
TXDELAY=50 ms, so the only overhead per frame is the TX-DELAY +
slot-time + per-frame ACK-echo round-trip. Mode 12 (300 AFSK)
shows the best efficiency because its per-frame air time is large
relative to that fixed overhead; mode 2 (9600 GFSK) shows the
lowest because the per-frame air time is much shorter than the
fixed overhead.

**Earlier finding, retracted:** a non-paced version of this test
bursted Data frames without awaiting completion; on three of four
modes it delivered ~0 % of the frames because the TNC silently
queued and (under sufficient pressure) dropped them. That number is
**not** representative of the modem's actual throughput — it's a
property of unmanaged-queue behaviour. The session-layer integration
must use ACK-pacing or some other queue-depth control.

### High-volume stress, ACK-paced (mode 6, TXDELAY=5)

200 sequential ACK-paced frames at the lowest reliable TXDELAY:

- **200/200 (100 %) success.**
- Round-trip ms: min 1166, p50 1600, p95 2443, p99 2763, max 2970.

p99 within ~2× of p50 — well-behaved tail.

### ACKMODE concurrency

Multiple ACKMODE frames in flight simultaneously, mode 6:

| N concurrent | All echoed? | Total elapsed s | Min ms | Mean ms | Max ms |
|---:|---:|---:|---:|---:|---:|
| 1 | yes | **15.03** | 15029 | 15029 | 15029 |
| 2 | yes | 0.45 | 222 | 335 | 447 |
| 4 | yes | 0.91 | 226 | 560 | 891 |
| 8 | yes | 1.81 | 226 | 1001 | 1773 |

**Resolved post-marathon:** the soak's `throughput` sub-command runs
just before `ackmode-concurrent`, bursting 20 frames at the modem
without ACK pacing. The TNC silently queues frames it can't transmit
immediately. When `ackmode-concurrent`'s first measured frame is
submitted, its ACKMODE echo correctly waits for every previously-
queued frame to drain off the TX queue first — that's ~15 s of
1200 AFSK at 200 bytes per frame.

The `tools/Packet.NinoTnc.Spike` `ack-warmup-probe` sub-command
reproduces this on demand:

```
Scenario 5: Open + SetMode + 20 back-to-back Data frames + ACKMODE
  first ACK (after burst): 17 467 ms
  second ACK:                  213 ms
```

The other four scenarios (vanilla, long settle, prime-with-data,
prime-with-ack) all return the first ACK in **228–857 ms**. The 15 s
is not a firmware initialisation cost; it is the modem's TX queue
draining correctly.

**Implication:** the driver / session layer should treat ACKMODE
elapsed time as a *true* TX-completion measure, not a queueing
measure. If you `await Send(...)` and care about TX completion, use
`SendFrameWithAckAsync`. Don't burst plain Data frames followed by
"how long did the next one take" — that measures the queue, not the
modem.

### Bidirectional simultaneous send (half-duplex CSMA contention)

10 rounds of A and B sending the same instant, TXDELAY=20:

- **9/10 rounds both peers received the other's frame.**

CSMA + the modem's built-in deferral does its job. PERSIST=63 /
SLOTTIME=10 ms defaults are good enough for two-station contention.

### Adaptive estimator (`TxDelayHillClimbEstimator`) — live run

40 frames with aggressive tuning (`SuccessesPerStepDown=3`,
`StepUnits=2`, `MinTxDelay=2`) starting from TXDELAY=50:

- All 40 ACKMODE round trips succeeded.
- Estimator walked TXDELAY 50 → 24 across the 40 frames (reproducible
  across runs).
- Final 240 ms is still wildly above the 10 ms floor — the algorithm
  is conservative. For production on a stable benchtop link, tune
  `StepUnits` higher or `SuccessesPerStepDown` lower; for production
  on real RF, the current cadence is sensible (frame-loss penalty is
  +10 units, so 5 consecutive non-losses to recover from one bad
  frame).

### Long-idle stability

2-minute idle watch with both modems open and in mode 6:

- 0 spurious inbound frames on either side.
- 0 driver pump errors.

The driver is quiet when there's nothing to do.

## Caveats

- **Single host, single setup.** Findings reflect the dev box's specific
  USB ports and one audio cable. Different USB controllers may produce
  different TXDELAY floors.
- **No noise, no fading, no real radios.** The bench audio cross-wire
  is artificially clean. **Adaptive TXDELAY tuning is essentially
  unexercised here** — every mode hits the 10 ms floor and the
  estimator has no failures to ratchet against. The estimator's value
  shows up when there are real transmitters with hundreds of ms of
  key-up delay, real receivers needing meaningful preamble for AGC
  and sync, and conditions that drift over time. **The numbers in this
  document are a benchtop floor, not a tuning target.**
- **Firmware v3.44.** Future firmware may change TX behaviour.
- **Intermittent AFSK demod wedge, unreproducible.** See the section
  above. We have seen it at mode 12 and mode 14, ~5–15 % per-trial
  in some sessions, no reliable trigger. No mode-specific avoidance
  rule follows.

## How to re-run

```sh
$env:PACKETNET_NINOTNC_PORTS = "COM6,COM8"
dotnet run --project tools/Packet.NinoTnc.Spike -- soak marathon COM6 COM8
```

Output lands under `artifacts/nino-tnc-soak/<timestamp>/results.md`.
The `soak` tool has narrower sub-commands too (`mode-sweep`,
`txdelay-sweep`, `payload-sweep`, `throughput`, `ackmode`,
`bidirectional`, `idle`, `estimator-live`, `stress`,
`per-mode-txdelay`, `binary-payload`).
