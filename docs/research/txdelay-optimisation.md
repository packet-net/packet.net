# TXDELAY optimisation — passive observation + active minimisation

*Design note. Status: shipped 2026-07-08 (plan §17). Bench-validated: the live sweep is
run by an operator over the Tait/NinoTNC rig, not by CI — no test in this repo keys a
radio.*

## The problem

Every AX.25 transmission pays a **TXDELAY**: a preamble of flag/idle bytes the sender
keys *before* its data so the receiver's demodulator, the sender's PA and the squelch
tail all settle. Too little and the opening bytes are lost (the whole frame fails IL2P/
FCS); too much and every single transmission wastes airtime on a shared channel. On a
1200 Bd AFSK link a station keying 500 ms of preamble where 150 ms decodes fine is
burning ~⅓ of a second of channel time on *every* frame — and on packet radio the
channel is the scarce resource.

Operators set TXDELAY blind: bump it until frames get through, then leave a fat margin
"to be safe". This feature makes it measurable and minimisable.

There are two layers, deliberately separate because they have very different costs.

## Layer 1 — passive excess-TXDELAY observation (zero RF)

The radio-tagging transport (`Packet.Radio.RssiTaggingTransport`) already measures, for
every received burst-opening frame, the **pre-data carrier time**
(`RadioMetadata.PreDataCarrier`): the interval between the radio's hardware carrier-sense
(DCD) rising and the frame's data starting, computed as
`(ReceivedAt − EstimatedAirtime) − CarrierRiseAt`. That *is* the transmitting station's
effective TXDELAY as heard here, plus a small constant rig overhead (bench-measured
~40–75 ms at 1200 Bd — the modem's decode+serial delivery latency the delivery timestamp
carries). It costs nothing extra: the station is already listening.

**Aggregation.** `Packet.Node.Core.Heard.HeardLog` keeps, per heard *source* callsign, a
bounded rolling window (`Packet.Tune.Core.PreDataCarrierWindow`, last N=32 measured
samples) and its median. Unlike the last-heard RSSI/SNR fields (newest-frame-wins), the
pre-data figure is a **rolling median** — a station's TXDELAY is a slow-moving property a
single frame shouldn't redefine, and the median rejects the odd mis-attributed window.
Only *burst-opening* frames carry a sample (later frames in a multi-frame train share the
one preamble), and only ports with a carrier-sensing radio produce any — a KISS-TCP or
radio-less port measures nothing and the fields stay null.

**Surfaces:**

- `HeardEntry` / `HeardStationSummary` gain `MedianPreDataCarrierMs` + `PreDataCarrierSamples`;
  the SQLite heard store persists them (additive ADD-COLUMN migration, mirroring the
  RSSI/SNR columns). The persisted median/count are display-only across a restart — the
  live window restarts empty, so the first post-restart sample honestly resets the count.
- The REST `/api/v1/heard` rows carry both fields plus a computed `txDelayAdvisory` string.
- A Prometheus gauge `pdn_link_predata_carrier_ms{port,peer}` — one series per heard
  partner with a measured median, the same accepted per-peer cardinality exception as the
  existing `pdn_link_snr_db` (Tom's call: an amateur-packet node hears a small,
  slowly-changing set of stations).
- The advisory itself is `Packet.Tune.Core.ExcessTxDelayAdvisor.Assess(...)`: it flags a
  peer whose median exceeds a threshold (default **250 ms**, configurable) with enough
  samples behind it (default **≥4**, so one long first-after-power-on keying never flags
  a station). The threshold is generous on purpose — a healthy 1200 Bd link decodes at
  ~150 ms and the measurement carries the ~40–75 ms rig overhead, so 250 ms of *measured*
  lead is real waste, not noise. Living in `Packet.Tune.Core` lets the `packet-tune` CLI
  and the node reuse one implementation.

Layer 1 tells you *which peers* are wasteful. It cannot tell a station its *own* best
TXDELAY — a station never hears its own preamble. That needs layer 2.

## Layer 2 — active own-TXDELAY minimisation (operator-initiated, two-station)

A **coordinator** (the station under test) sweeps its own KISS TXDELAY *down* while a
**meter** (a cooperating far station) counts how many probes decode at each step. The
coordination rides the existing `ITuningLink` side channel (canonically `SdmTuningLink` —
Tait CCDI short data messages, radio-native FFSK, independent of the very TXDELAY under
test), reusing the telegram vocabulary the mode-coordination and deviation work
established. It is **not** a new protocol: a new `TXD` verb was added to `TuningVerb`
alongside `MODE`/`HAIL`/`STAT`, with a `TxDelayMinMessage` sub-protocol codec
(propose/confirm/reject/step/sent/report/apply/done/abort) exactly parallel to
`ModeCoordMessage`.

### The sweep algorithm

1. **propose → confirm.** `C→M propose|k|start|step`; the meter answers `confirm|k` (or
   `reject` — nothing has transmitted yet). The meter caps `k` at 20 (a runaway probe
   count would key the channel for minutes per step).
2. **pin channel access.** The coordinator pins persistence **255** / slottime **0** for
   the sweep (`TxDelayControlCheck` does exactly this and for the same reason: the default
   p-persistence adds random ~100 ms slot deferrals that would swamp the TXDELAY step
   under test). From this point every exit path restores.
3. **per step**, descending from `start` by `step`, floored at `min` (default 20 ms):
   - `C→M step|ms|k` — the telegram's **sequence number is the tag** every probe of this
     step carries, so a stale probe from a previous step can never satisfy the current one.
     The meter opens its counter on receipt.
   - a wedge-guard delay (the coordination radio's SDM auto-ack must clear before we key —
     the TM8110 auto-ack wedge, see the bench notes).
   - **set TXDELAY + settle frame + unkey gap.** The NinoTNC applies a changed KISS
     parameter from the **SECOND** frame after the command (bench-observed,
     NinoTNC-specific), so a throwaway settle frame is transmitted and discarded first;
     it still pays the *old* preamble and carries no probe marker so the meter never counts
     it.
   - **K probe keyings** as **K separate keyings** with unkey gaps — **not** one
     multi-frame train. Back-to-back ACKMODE sends chain into one keying with a *single*
     shared preamble (bench-observed echo ~430 ms regardless of TXDELAY), which would
     measure nothing. Each probe pays its own preamble.
   - `C→M sent|ms|k`; the meter grace-waits, snapshots its counter and replies
     `report|ms|dec/k[|preMs]`.
4. **knee + margin.** Stepping stops at the first non-full-decode step (the *drop* step)
   or the floor. The **knee** is the lowest full-decode step. The **recommendation** is
   `knee + margin`, where margin = `max(2 steps, 25% of the knee)` rounded up to the KISS
   10 ms unit (`TxDelayMinReport.Recommend`). If the *first* step (the current configured
   TXDELAY) already drops probes, the sweep reports `NotSolidAtStart` and makes no
   recommendation — the link itself is marginal (deviation/SNR), fix that first.

### The self-evidencing cross-check

Because the coordinator's probes are separate keyings and the meter has a carrier-sensing
radio, the meter measures *each probe's* pre-data carrier — the **same** attribution
`RssiTaggingTransport` performs — and reports the per-step median. That median is the
coordinator's *effective TXDELAY as actually heard on air*, so the sweep table is
self-evidencing: the commanded column and the heard column track each other (offset by the
constant rig overhead), and a divergence exposes a pot override or a mis-set modem
directly. The meter-side `ITxDelayProbeCount` tracks the radio's DCD rises and computes
`(arrival − airtime) − rise` per decoded probe, discarding implausible readings before
taking the median.

### APPLY is a separate explicit step

`RunSweepAsync` **never** leaves the reduced TXDELAY in place — it always restores the
starting value + polite channel access. Applying the recommendation is a separate
`ApplyAsync(ms)` call: set + settle + verify with K more probes at the recommended value,
and only when *every* verify probe decodes does it leave the value in place (a failed
verify restores the fallback). Persisting it beyond the TNC's RAM is the caller's step —
on the node, the port's `kiss.txDelay` config; on the CLI, a printed reminder.

### Abort safety

Every failure or timeout — link loss, a station operation throwing, cancellation, a failed
verify — routes through a `finally` that restores the original TXDELAY (set + settle) and
the polite channel-access defaults (persistence 63 / slottime 10; KISS has no read-back,
so "restore" is the convention). Restore does **not** depend on the side channel still
working: a link that dies mid-sweep still gets the local modem restored. The meter changes
nothing on its own hardware, so it has nothing to revert — a silent coordinator simply
times its loop out.

## Surfaces

- **`Packet.Tune.Core`** (the reusable engine, no node dependency):
  `TxDelayMinMessage`/`TxDelayProbe` (the `TXD` codec + probe/settle frames),
  `ITxDelayMinStation`/`NinoTncTxDelayMinStation` (the hardware adapter — KISS writes,
  separate keyings, DCD-based pre-data measurement), `TxDelayMinimizer` (coordinator),
  `TxDelayMinResponder` (meter), `TxDelayMinReport` (recommendation + Markdown table),
  and the layer-1 `PreDataCarrierWindow`/`ExcessTxDelayAdvisor`.
- **CLI**: `packet-tune txdelay-min --role coordinator|meter --tnc <port> --radio <ccdi>
  --peer <8charId> [--start 500] [--step 40] [--min 20] [--probes 5] [--apply | --apply-at
  ms]` — mirrors the `mode-coord` / `deviation-sdm` arg conventions.
- **Node API** (admin-scoped, audited, RF-caveated — the keyup-pairing pattern):
  `POST /api/v1/ports/{id}/tuning/txdelay-min` (sweep, coordinator|meter) and
  `POST /api/v1/ports/{id}/tuning/txdelay-min/apply` (verify + optionally persist to
  `kiss.txDelay`). They share the one-session-per-port claim, the SSE `tuning/events`
  feed and the stop verbs with the deviation sessions (a common `IPortTuningSession`
  seam; `TxDelayMinPortSession` projects each sweep step into the same `TuningEvent`
  feed). The port is always paused during a session and rebuilt (restored) on exit — so an
  apply only outlives the session when it also persists.

## Bench constraints honoured

- **NinoTNC KISS param applies from the SECOND frame** → a settle frame after every set.
- **Back-to-back sends chain into one preamble** → K separate keyings with unkey gaps.
- **Default p-persistence adds ~100 ms random deferrals** → pin p=255/slottime 0 during
  the sweep, restore 63/10.
- **TM8110 SDM auto-ack wedge** → a pre-key guard delay after each side-channel exchange.
- **~40–75 ms constant rig overhead on the pre-data measurement** → the advisory threshold
  and the "heard vs commanded" offset both account for it; never treated as zero.
- **2 W attenuators on the bench rig** → the sweep only ever keys at the configured channel
  power; nothing here sweeps TX power.

## Not built / follow-ups

- No auto-apply: the recommendation is always operator-confirmed (APPLY is a separate call).
- No ax25-ts leg — this is the radio/tuning C#-only surface; nothing on the parity-tracked
  AX.25 contract changed.
- The advisory is per-heard-peer; it does not (and cannot) tell a peer's *operator* — it's
  a diagnostic for the listening node, and a prompt to run layer 2 against that peer.

## Bench tuning campaign (2026-07-08)

First live end-to-end sweeps on the 2× TM8110 + 2× NinoTNC rig, after the SDM receipt-tolerance
fix (see [tm8110-sdm-autoack-refractory.md](tm8110-sdm-autoack-refractory.md) — the SDM coordination
was previously aborting on the auto-ack refractory; now the whole sweep runs clean).

**Mode 6 (1200 AFSK, ch 0):** full stepping sweep 400→100 ms, all 5/5 decoded, floor still solid
(knee ≤ 100 ms), recommend 220 ms. Validated the tool + the fix end-to-end.

**Mode 2 (9600 GFSK IL2P+CRC, ch 1 wide):** 200→0 ms all 5/5; knee ≤ 0 ms. A 4-second inter-probe
gap (receiver fully cold between probes, via the new `--gap`) made no difference — still 5/5 at
0 ms — so the low knee is **not** a warm-lock/AGC artifact.

**TXDELAY provably takes effect on the wire (the self-evidencing cross-check, live).** Mean on-air
TX burst per probe tracked TXDELAY ~1:1: 203/184/163/143/123/113 ms at 100/80/60/40/20/0 ms
TXDELAY — every 20 ms removed shortened the burst ~20 ms. So the setting is genuinely applied and
measured, not ignored.

**NinoTNC residual preamble floor ≈ 10 ms.** The last step (20 → 0 ms) shortened the burst only
**10 ms**, not 20. So "0 ms" on the wire is really ~10 ms of preamble — the TNC cannot emit less.
That residual is exactly why this pristine link still decodes at a commanded 0: there is still
~10 ms of real preamble, and the link needs no more.

**Mode 1 (19200 4FSK IL2P+CRC):** 0/5 even at 200 ms — the link is not solid in this direction
(a deviation / TNC-A-pot fault the earlier survey already flagged), so no TXDELAY knee could be
measured. Needs deviation tuning before it can be swept.

**Parked discrepancy (revisit on-air).** This ≤~10 ms bench floor is far below the "a few tens of
ms" these radios want in real operation. The cabled 100 dB-pad link has none of the impairments
that *form* the knee — squelch opening on a real signal, AGC settling on a fading path, multipath,
weaker SNR — and the NinoTNC taps discriminator/flat audio (no squelch gate in the data path). So
the bench is a **lower bound**; realistic TXDELAY tuning must run over a real RF path. The tool is
ready to find that knee on-air. **Feature left as-is** — no default or behaviour change on the
strength of a bench that can't reproduce the real knee.

**Tool change:** `txdelay-min` gained `--gap <ms>` (inter-probe unkey gap; default 750 ms) for
cold- vs warm-receiver testing.
