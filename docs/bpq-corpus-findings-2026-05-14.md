# BPQ MQTT corpus — first findings

Mining notes from `/home/tf/packet-mqtt-data/gb7rdg-2026-05-14.sqlite`
(9 014 messages, ~36 hours of GB7RDG's MQTT-bridged traffic). Each
on-air frame is published twice: once as `kiss` (raw bytes) and once
as `ax25/trace/bpqformat` (BPQ's monitor render). Total: 4 488 paired
(kiss, bpqformat) frames after dropping orphans.

This is a small first slice — the collector has been running ~36 h.
Patterns described here are signals, not statistical conclusions.

## Frame-type inventory

| Tag | Count | What it is |
|---|---:|---|
| `RR` | 2 374 | Receive-Ready supervisory |
| `I`  |   998 | Information frames |
| `UI` |   555 | Unconnected info (NODES broadcasts, beacons, identification) |
| `XID`|   435 | Capability exchange (mostly retries — see "noise" below) |
| `C`  |   259 | SABM Connect requests |
| `UA` |    87 | Unnumbered Acknowledge |
| `REJ`|    80 | Reject — all from one lossy session |
| `D`  |    77 | DISC Disconnect |
| `DM` |     7 | Disconnected Mode response |

Direction split: 2 558 sent, 2 314 rcvd — roughly balanced.

## Sessions (active vs noise)

The corpus contains **45 distinct (callsign-pair, port) combinations**.
They split sharply into two classes.

### Sessions that carried I-frames (active data transfer)

| Pair (sorted callsigns) | Port | Duration | I-frames | RR | REJ | Lifecycle |
|---|---:|---:|---:|---:|---:|---|
| GB7BEX ↔ GB7RDG-2 | 2 | 7.2h | 422 | 396 | 0 | 7×(SABM→UA→I…→DISC) |
| GB7RDG-2 ↔ GB7WEM-7 | 3 | 9.6h | 313 | 498 | **80** | many retries, lossy |
| GB7BSK-1 ↔ GB7RDG-2 | 2 | 6.8h | 107 | 107 | 0 | 16 SABM, 15 DISC |
| GB7BSK ↔ GB7RDG | 2 | 9.8h | 46 | 417 | 0 | RR-heavy maintenance |
| GB7RDG ↔ GB7WOD | 1 | 9.8h | 47 | 433 | 0 | likewise |
| GB7RDG ↔ GB7WOD | 2 | 9.8h | 46 | 434 | 0 | likewise |
| GB7LOX-2 ↔ GB7RDG-2 | 3 | 9.1h | 10 | 34 | 0 | 19 SABM, 4 DISC — flaky |
| IW2OHX-8 ↔ PD4R-12 | 3 | 3.7h | 4 | 51 | 0 | only 4 I-frames in 3.7 h |
| GB7BPQ ↔ GB7RDG-2 | 3 | 9.1h | 3 | 3 | 0 | 80 XIDs, 1 brief connection |

### "Noise" pairs (no I-frames, no UA)

| Pair | Port | Frames | Tags |
|---|---:|---:|---|
| EI5IYB-1 ↔ GB7RDG-2 | 3 | 147 | all `XID` — peer never accepts |
| GB7WOD → ID | 1, 2 | 40+40 | BPQ identification beacons |
| GB7RDG → ID | 1, 2, 3, 6 | 40 each | likewise |
| BEACON → GB7WOD | 1, 2 | ~40 each | GB7WOD station status broadcast |

The largest source of "wasted" traffic is **GB7RDG-2 → GB7WEM-7
port 3**: it retried SABM 212 times over 9.6 hours, eventually
completing 11 connections, with 80 REJ frames during the active
periods. That's the only pair generating REJ — see below.

## REJ behaviour

All 80 REJ frames belong to a single session (GB7RDG-2 ↔ GB7WEM-7
port 3). N(r) distribution across the REJs:

| N(r) | Count |
|---:|---:|
| 0 | 7 |
| 1 | 13 |
| 2 | 6 |
| 3 | 5 |
| 4 | 10 |
| 5 | 9 |
| 6 | 18 |
| 7 | 12 |

Even spread across the mod-8 N(r) space → the loss pattern is random
RF noise, not a specific N(s) value the peer can't decode. SREJ never
appears — neither station offers it via XID capabilities, so the spec
falls back to REJ for any out-of-sequence I-frame.

## XID parameter exchange

The 435 XID frames mostly carry the same parameter set:

| Count | Parameters |
|---:|---|
| 371 | `2=21 3=22a486 RX Paclen=256 RX Window=7 Can Compress` |
|  28 | `2=21 3=22a486 RX Paclen=256 RX Window=7` |
|  27 | `2=21 3=22a480 RX Paclen=256 RX Window=7` |
|   9 | `2=21 3=22a480 RX Paclen=256 RX Window=7 Compress ok` |

**Paclen=256 and Window=7** are universal — every station in the
corpus uses the maximum mod-8 send window and the BPQ default
paclen. The Compress flag varies (`Can Compress` vs `Compress ok`
vs absent).

## I-frame N(s) / N(r) distributions

N(s) is reasonably flat across the mod-8 space — sessions are
running through normal sequence-number progression rather than
getting stuck at one value:

| N(s) | Count |
|---:|---:|
| 0 | 172 |
| 1 | 161 |
| 2 | 139 |
| 3 | 116 |
| 4 | 107 |
| 5 | 107 |
| 6 | 101 |
| 7 |  95 |

N(r) is more skewed, reflecting how often each "next-expected" value
appears as an ACK in I and S frames.

## I-frame PID distribution

| PID | Frames | Meaning | Info length range |
|---:|---:|---|---:|
| `0xF0` | 859 | No Layer 3 — plain text node-to-node | 3 – 240 B (median 90) |
| `0xCF` | 139 | NET/ROM | always exactly 20 B |

**No PID 0x08 (segmented) frames** in this corpus — meaning all
single-packet payloads fit under each pair's negotiated paclen
(256 B), so segmentation never triggers. If we want to exercise our
segment-frame path for real, we'll need either a corpus from
higher-payload-volume nodes (e.g. mail-store traffic with large I-frames)
or synthetic test cases.

## ACKMODE TX-completion echoes

**Not visible in the MQTT corpus.** The MQTT plugin publishes the
ACKMODE data frame (cmd `0x0C`, 2-byte sequence tag + AX.25 payload)
but does not publish the TX-complete echo frame the TNC sends back to
the host. 853 ACKMODE data frames, zero echoes. Result: we can't
measure host-to-TNC TX timing from this corpus — we'd need a tap
between the host and the TNC instead.

## What this means for our implementation

1. **Connected-mode flows we need to handle well**: SABM/UA → I/RR
   → REJ → DISC/UA, all mod-8, paclen up to 256 B, max window 7.
   No SREJ in the wild, no SABME (mod-128), no segmentation.

2. **Retry behaviour is real**: a single failed SABM can be retried
   hundreds of times over hours. Our session machine's retry counter
   needs to accommodate this — or be willing to give up earlier.
   BPQ doesn't, apparently.

3. **Compress capability is exchanged** but we don't model L7
   compression yet. Worth a note for the future.

4. **XID parameters are stable** in this corpus (all `Paclen=256
   Window=7`). When we wire up XID negotiation we can pick those
   as sensible defaults.

5. **Segmented frames need synthetic coverage** — the live corpus
   won't exercise our PID 0x08 path. Add a property test that builds
   a long I-payload, segments it, parses the segments back.

6. **ACKMODE timing must come from a different tap**, not MQTT.
   That's outside the scope of any decoder work — it's a host/TNC
   interface concern.

## Next collection targets

- A higher-throughput node where I-frame payloads exceed 256 B,
  forcing segmentation.
- A node that runs SREJ-capable peers (recent direwolf, modern
  AGW/PaclinkUnix builds), to capture an SREJ in the wild.
- A pure-mod-128 (SABME-initiated) session — none observed yet.
