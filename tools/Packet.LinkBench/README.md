# Packet.LinkBench

The AX.25 connected-mode link bench from [`docs/link-bench-plan.md`](../../docs/link-bench-plan.md): two `Ax25Listener` engines in one process, joined by a pluggable channel, pushing a bulk connected-mode payload A→B and printing a metrics table. It exists to answer engine/transport-timing questions in seconds instead of multi-minute live windows — throughput vs `k`/T1/T2, the #79 duplicate-supervisory question, and what ackmode pacing actually buys.

## Channels (plan §3)

- `inproc` (default) — in-memory channel model: per-frame airtime (`frameBits/baud` + txdelay/txtail), optional half-duplex with turnaround, optional seeded frame loss, and an ackmode TX-complete echo emitted **after the modeled airtime**. `--baud 0` (the default) disables airtime modelling entirely: rung 1, pure lossless engine↔engine. `--baud 1200 --half-duplex` is rung 1b.
- `axudp` — two `AxudpFrameTransport`s over UDP loopback. Real sockets, lossless, full-duplex, and **no ackmode by nature** (no TNC, no echo) — the cross-check that an in-proc result isn't an artifact of the in-proc model.
- `netsim` — two KISS-TCP clients into net-sim ports (rung 2). Needs the ackmode-capable image pinned in plan §7. `--netsim host:8101,host:8102`.

## ackmode (plan §2)

`--ackmode on` (the default, per the standing assumption) wraps each engine's modem in `PacingKissModem`: the engine's fire-and-forget frame blast is serialised one-at-a-time, each frame released only when the prior frame's 0x0C TX-complete echo arrives. `--ackmode off` is the control for the isolation experiment, not a supported operating mode. The bench records every echo's round-trip — on netsim, compare the RTTs against modeled airtime to settle whether its echo is immediate-on-receive (pacing is a no-op) or post-transmission (plan §7's open question).

ackmode ≠ carrier sense: the DCD-over-KISS seam (plan §8) is designed but deliberately unwired — see `ITxGatePolicy` (the CSMA-by-DCD plug point, no-op today) and `InProcChannel.ChannelStateChanged` (the modeled busy/clear signal).

## SREJ (selective reject)

`--srej on` forces SREJ on **both** engines (mirroring the figc4.7 v2.2 `Set_Selective_Reject` default), set after the handshake completes — a mod-8 connect runs `Set_Version_2_0`, which clears the flag, so it has to be (re)applied post-connect. Default `off` = the engine's mod-8 default of implicit-reject / go-back-N recovery. The bench bypasses XID negotiation here on purpose: it forces the runtime flag directly to exercise the **recovery mechanics**, not the negotiation, exactly as `DataLinkSrejUnderLossTests` do.

SREJ only changes behaviour **under loss** — a clean run never produces an out-of-sequence frame, so the selective-vs-go-back-N choice never arises. Pair it with `--loss` (the `inproc` channel) or run it on `netsim` (collisions drop frames): with `--srej on` a single dropped frame is recovered by one selective retransmit; with `--srej off` the same gap triggers REJ go-back-N (the whole window replays). The table's `rej` column shows the configured mode (`srej` / `gbn`); the separate `SREJ` column counts SREJ frames actually emitted (0 unless a gap occurred under `--srej on`).

> mod-128 is **not** covered — the bench connects mod-8 (SABM) only. That's deliberate: the lab's LinBPQ peer rejects SABME, so extended sequencing is academic for the live path. v2.2 mod-128 + SREJ-128 *correctness* lives in `DirewolfMod128Interop` and the conformance harness; this bench is the mod-8 throughput/timing/recovery rig.

## Sweeps

Comma-separated values on `--k`, `--t1`, `--t2`, `--paclen`, `--ackmode`, `--t1-tx-complete`, `--srej`, `--loss` expand to a cartesian product of runs, one table row each. `--json out.jsonl` writes machine-readable results.

```sh
# rung 1: lossless, no channel — is #79 engine-intrinsic? (dupS column)
dotnet run --project tools/Packet.LinkBench -- --payload 64k

# same, ack-per-frame (T2=0) — milliseconds instead of T2-paced minutes
dotnet run --project tools/Packet.LinkBench -- --payload 64k --t2 0

# AXUDP cross-check
dotnet run --project tools/Packet.LinkBench -- --channel axudp --payload 64k --t2 0

# SREJ vs go-back-N under 5% loss — selective recovery should retransmit less
dotnet run --project tools/Packet.LinkBench -- --payload 16k --loss 0.05 --k 4 --srej off,on

# rung 1b: k × ackmode at modeled 1200 baud half-duplex, 50× real time
dotnet run --project tools/Packet.LinkBench -- --payload 16k --baud 1200 --half-duplex \
    --time-scale 50 --k 1,2,4,7 --ackmode on,off
```

`--time-scale F` runs the modeled channel F× faster than real time **and scales the engines' T1/T2/T3 defaults by the same factor**, so timer-vs-airtime ratios stay honest. Explicit `--t1`/`--t2` values are taken as already-scaled.

### Zero-airtime timer auto-scaling

At `--baud 0` (the default rung-1 channel) frames and acks deliver instantly, so the 6 s spec-default T1V — sized for real ~1200-baud airtime + turnaround — is wildly mismatched. It doesn't matter for lossless runs (T1 never fires), but under loss recovery latency is `losses × T1V`: a loss-heavy stop-and-wait or even pipelined run burns a full 6 s per dropped frame/ack and times out. That's a timer:airtime mismatch, **not** an engine defect — recovery, pipelining and go-back-N/SREJ all work; the bench was just measuring the ratio. So when `--baud 0` and no explicit `--t1` is given, the bench auto-scales the default T1V to 500 ms (T2 to 250 ms) and prints a one-line notice. An explicit `--t1`/`--t2` is always respected (it means you're tuning deliberately); modelled channels (`--baud N`, `--time-scale`) keep the real spec defaults.

## Metrics (plan §5)

Per run: wall-time and throughput (payload bytes over the transfer window), I-frame / supervisory TX counts per endpoint (RR/RNR/REJ/SREJ split), retransmissions (an I-frame keyed with an N(S) already outstanding), **duplicate-supervisory count** (`dupS` — extra copies in runs of consecutive identical supervisory frames within `--dup-window-ms`; this is #79 quantified, with `burst` the longest identical run), window-stall time (cumulative time the sender sat on a full window of k unacked I-frames), payload integrity (byte-exact compare), and clean-DISC confirmation. Frame data comes from tapping both listeners' `FrameTraced` streams; ackmode echo RTTs from a tap under the pacing decorator.

Exit code 0 only if every run completed with an intact payload.
