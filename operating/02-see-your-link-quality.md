# 2. See your link quality

**Goal:** once a radio is [attached](01-attach-a-radio.md), actually *see* how good
your link is — per-frame signal, live radio health, and who you're hearing and how
strongly.

There are three places this shows up: the **monitor** (per frame), the
**dashboard** (per radio), and the **heard list** (per station).

## Per-frame RSSI and SNR (the monitor)

Open the **Monitor** screen. Every received frame now shows an **RSSI** column, and
expanding a frame shows the full signal readout:

- **RSSI** — received signal strength, in dBm.
- **SNR** — signal-to-noise ratio, in dB.
- **Noise floor** — the idle-channel noise level the SNR is measured against, in dBm.

These come from the radio's control channel, sampled as each frame arrives. Frames
the node *transmitted*, and frames on ports with no radio attached, show a dash
(`—`) rather than a fake `0` — an absent reading is never rendered as a real one.

This is the signal data **standard KISS cannot give you**: a bare TNC hands the node
decoded bytes with no idea how strong the carrier was. Attaching the radio is what
puts a number next to every frame.

## Live radio health (the dashboard)

The **Dashboard** grows a **Radios** panel — one card per radio-attached port. Each
card shows:

- **Identity** — the radio model and CCDI version, and its serial number.
- **Connection state** — a badge: **healthy** (the radio is answering), **faulted**
  (the control link died or the radio stopped answering), or **unknown**.
- **Channel busy** — a live-ish pill showing whether the radio currently senses RF
  on the channel (hardware carrier-sense / DCD).
- **RSSI** — the headline signal value, plus a rolling **average**.
- **PA temperature** — the power-amplifier temperature, in °C.
- **Antenna-health trend** — forward and reverse power-detector readings and their
  ratio.

> [!CAUTION]
> The forward/reverse readout is an **antenna-health trend, NOT VSWR.** The
> detectors are uncalibrated and √P-scaled, so the absolute numbers are not a power
> or VSWR measurement. Treat them as a **per-station trend**: watch for a *change*
> over time (e.g. a reverse reading creeping up = something changed at the antenna),
> and alert on the shift, never on the raw value.

The same data is available from the API:

```
GET /api/v1/radios              # all attached radios
GET /api/v1/ports/{id}/radio    # one port's radio
```

Each returns `portId`, `attached`, `kind`, `serial`, `identity` (`model`,
`ccdiVersion`), `connectionState`, `channelBusy`, and a `health` object
(`rssiDbm`, `averagedRssiDbm`, `paTemperatureC`, `forwardTrendMillivolts`,
`reverseTrendMillivolts`, `reverseForwardRatio`, `sampleAt`). The panel absent-ness
is intentional: a node with **no** radios attached shows no Radios panel at all.

## Who you're hearing, and how strongly (the heard list)

The **heard** list — stations the node has recently received — now carries the
signal of the **newest frame** from each station:

```
GET /api/v1/heard            # node-wide
GET /api/v1/heard?port=nino  # one port
```

Alongside the usual `callsign`, `firstHeard`, `lastHeard`, `count` and `ports`, each
entry gains **`lastRssiDbm`** and **`lastSnrDb`** — the RSSI/SNR of the last frame
heard from that station (null when no radio attributed a reading). This is the quick
"is EI5xyz coming in strong or marginal today?" answer without watching the monitor.

## Carrier-sense: it just works

A radio-attached port also gets **native carrier-sense CSMA** for free: the node
uses the radio's hardware DCD to **hold off transmitting while the channel is
busy**, automatically, and keys up only when it's clear. This behaves like a TNC
that exposes DCD to the host.

There is **nothing to configure** — attach a radio and the port defers to a busy
channel. You'll see the same channel-busy state reflected in the dashboard's
**channel busy** pill. (If the radio ever stops reporting, the gate fails *open* —
i.e. it transmits rather than jamming up — so a control-channel glitch never wedges
the port.)

## Is the port actually on the air? (port state)

Every configured port carries a **state** the node itself keeps, and the same answer
appears everywhere: the Ports screen and the dashboard, `GET /api/v1/ports`, the
`pdn_port_state` metric, and the `PORTS` command over the air or on telnet.

| State | What it means |
|---|---|
| `up` | Serving: the listener is running and everything the port asked for is attached. |
| `degraded` | Serving, with a piece missing - the port still carries traffic, but its radio, its rig, its node-managed `rigctld`, or its (self-healing) network transport is not there. The missing pieces are listed, with the reason. |
| `disabled` | Switched off in config. Not running by design. |
| `configured` | In config and wanted, but not attempted yet - during boot, or between a teardown and the bring-up that follows it. |
| `starting` | A bring-up is in flight. |
| `faulted` | Not serving: the bring-up failed, or a running port died. The reason is on the port. |
| `retrying` | Faulted, with a retry armed: the node is trying to bring it back on its own, every 5 s backing off to every 60 s, for as long as it takes. |
| `stopping` | A teardown is in flight. |

Two things follow from this that are worth knowing:

- **A port that dies is noticed.** If a TNC is unplugged, or a port's receive pump
  otherwise faults, the node sees it within a few seconds, says so (`faulted`, with the
  reason), tears that one port down and keeps trying to bring it back. No other port is
  disturbed, and you do not have to notice the silence yourself.
- **A late device is not a manual job.** A serial TNC that enumerates a few seconds after
  the node, or a softmodem that is not listening yet, is retried automatically. A port only
  stays down for good if it stops being wanted.

`degraded` is the one to read carefully: the packet channel is fine, so the port keeps
working - but something you configured is not there. Attaching a radio that will not open
leaves the port `degraded` with `radio` named, and you lose per-frame RSSI and the
carrier-sense gate while the port carries on regardless.

## Next

Seeing a bad link? Ask the doctor what's wrong:
[3. Check your setup (the doctor) →](03-check-your-setup-doctor.md)
