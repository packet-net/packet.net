# 1. Attach a radio

**Goal:** tell the node about the radio behind one of your TNC ports, so it can
read signal strength and health from the radio while packets flow.

This chapter is about **option 1** from the [overview](index.md#two-ways-a-tait-radio-can-join-a-port):
a radio *attached to* a TNC port. Your modem stays a TNC; the radio's own serial
control channel is a **second cable** the node reads alongside.

## Before you start

- A **NinoTNC** or **serial KISS** port already working in PDN. (A *cabled*
  `tait-ccdi` radio block is only valid on the serial-modem port kinds —
  `nino-tnc` and `serial-kiss`. A `kiss-tcp` or AXUDP port has no physical radio
  beside the node to cable to — though a `kiss-tcp` port *can* carry a
  [rig-backed radio](#kind-rig--re-use-the-ports-cat-rig-as-its-radio), which
  needs no cable at all.)
- A **Tait TM8100 / TM8200** radio wired to the machine by its **CCDI** serial
  cable (typically a CP2102 USB-serial dongle showing up as `/dev/ttyUSB0`).
- The radio programmed so CCDI answers at its serial rate (the Tait factory default
  is **28800 baud**).

> [!NOTE]
> The control cable is *separate* from whatever carries audio/PTT to the TNC. You
> are adding a data path so the node can *ask the radio questions*; you are not
> changing how packets get modulated.

## The easy way: scan and click (web UI)

1. Open the node's web control panel and go to **Ports**.
2. Open the port your TNC is on (or add it) and find the **Radio control**
   section. Flip it **on**.
3. Leave **Radio type** on *Tait CCDI (TM8100 / TM8200)*.
4. Click **Scan for radios**. The node probes every candidate serial port and lists
   what answers — each entry shows the **model and serial number** (and the device
   path it was found on), e.g.

   > **TM8110 · s/n 19925328** — `/dev/ttyUSB0 · 28800 baud`

5. Click your radio in the list. That pins the port to it **by CCDI serial** — the
   robust choice (see below). Save the port.

The whole point of scan-to-attach is **"pick your radio by model + serial, not a
device path"** — you never have to know or guess which `/dev/ttyUSB*` it landed on.

Once saved and the port restarts, an **attached** badge appears, inbound frames
start carrying RSSI/SNR, and the radio shows up on the dashboard
([chapter 2](02-see-your-link-quality.md)).

## The config way: the `radio:` block (YAML)

If you edit node config directly, add a `radio:` block under the port. Here is a
NinoTNC port with a Tait radio attached, bound by serial:

```yaml
ports:
  - id: nino
    enabled: true
    transport:
      kind: nino-tnc
      device: /dev/ttyACM1
      baud: 57600
      mode: 6
    radio:                       # attach the radio's serial CONTROL channel
      kind: tait-ccdi            # Tait TM8100/TM8200 CCDI (only kind today)
      serial: "19925328"         # PREFERRED — pin by CCDI serial (stable)
      baud: 28800                # CCDI control-channel baud (Tait default)
      healthIntervalSeconds: 10  # optional — how often to sample health (default 10 s)
```

The fields:

| Field | Meaning | Default |
|---|---|---|
| `kind` | Control protocol: `tait-ccdi` (this chapter), or `rig` (below). | *(required)* |
| `serial` | The radio's **CCDI serial number** — the stable binding. `tait-ccdi` only. | — |
| `port` | **OR** the control device path, e.g. `/dev/ttyUSB0`. `tait-ccdi` only. | — |
| `baud` | Control-channel baud. `tait-ccdi` only. | `28800` |
| `healthIntervalSeconds` | Health-sample cadence, seconds. `tait-ccdi` only. | `10` |

> [!IMPORTANT]
> For `tait-ccdi`, set **exactly one** of `serial` or `port` — not both, not
> neither. Binding by `serial` is strongly preferred (next section). `port` is the
> advanced fallback. For `kind: rig` set **neither** — the rig-backed radio has no
> control cable of its own.

## `kind: rig` — re-use the port's CAT rig as its radio

A port that already has a `rig:` block (hamlib `rigctld` / flrig CAT control) can
re-present **that same rig** as the port's radio:

```yaml
ports:
  - id: hf
    enabled: true
    transport:
      kind: kiss-tcp             # e.g. a soundmodem beside the node
      host: 127.0.0.1
      port: 8001
    rig:                         # the CAT view: dial/mode/PTT/meters
      kind: hamlib               # rigctld on its stock port 4532
    radio:                       # AND the packet-medium view of the same rig
      kind: rig
```

The node dials a **second, dedicated connection** to the same daemon, so the
carrier-sense polling never queues behind the status poller's meter reads. What you
get depends on what the rig reports:

- **DCD** (`get_dcd`) gates the node's CSMA — the node holds its keyups while the
  rig hears carrier, exactly like a Tait's hardware DCD.
- **Calibrated signal strength** (dBm) RSSI-tags inbound frames, feeding the same
  link-quality surfaces as a Tait attachment ([chapter 2](02-see-your-link-quality.md)).
  A rig with DCD but no calibrated meter still works — the port just runs without
  per-frame signal metadata.

Because there is no control cable, a rig-backed radio works with **any** transport —
the `kiss-tcp` soundmodem + `rigctld` pairing above is the motivating setup. The
`rig:` block on the same port is **required** (it says which daemon to dial); the
`serial`/`port`/`baud` fields above are `tait-ccdi`-only and must stay unset. An
unreachable daemon degrades exactly like an unplugged control cable: the port runs,
just without the radio.

### Let the node run rigctld for you (`device:` + `model:`)

The `rig:` block has **two shapes**. `host:`/`port:` (as above) points at a daemon
**you** run — and remains the only shape for **flrig** (it's a GUI application the
node can't sensibly spawn). Alternatively, give the node the rig's serial device and
hamlib model number, and it **spawns and supervises `rigctld` itself**:

```yaml
    rig:
      kind: hamlib               # node-managed shape is hamlib-only
      device: /dev/serial/by-id/usb-Icom_Inc._IC-7300_02012345-if00-port0
      model: 3073                # hamlib rig model number — `rigctl -l` lists them
      # serialSpeed: 115200      # optional — omit for hamlib's per-model default
```

The node launches `rigctld -m <model> -r <device>` on a loopback port it allocates
itself, points its own rig client(s) at it, and **restarts it with backoff** if it
dies or the USB device disappears — plug the rig back in and the attachment
self-heals, no config edit, no systemd unit of your own. Set **either** `device:` +
`model:` **or** `host:`/`port:` — never both (`device:` selects the node-managed
shape, so `port:` and a remote `host:` must stay unset). Prefer the
`/dev/serial/by-id/…` path: it survives USB renumbering, exactly like binding a Tait
by CCDI serial.

## Why bind by serial, not device path

USB serial devices **renumber**. Unplug and replug, or reboot, and the radio that
was `/dev/ttyUSB0` can come back as `/dev/ttyUSB1`. Worse, the CP2102 dongles many
Tait CCDI cables use all share the same USB serial identity, so `/dev/serial/by-id`
can't tell two of them apart.

The **CCDI serial number is baked into the radio** and never changes. When you bind
by `serial`, the node **scans at bring-up** for whichever port answers with that
serial and opens that one — so re-enumeration across a replug or reboot just works.
Bind by `port` (device path) only when you have a reason to.

You can list attachable radios at any time from the API (this is what the **Scan**
button calls):

```
GET /api/v1/radios/scan
```

It returns each radio's `serial`, `model`, `ccdiVersion`, `baud`, `devicePath`, and
`byIdPath` — everything you need to fill in a `serial:` binding.

## If the radio isn't there

Attaching a radio is **best-effort and non-fatal**. If the control cable is
unplugged, the radio is off, or a `serial:` bind finds no match, the node **logs the
fault and runs the port anyway** — just without signal metadata. An unplugged
control cable must never take a working packet channel down.

Changing the `radio:` block is a **restart-class** edit: the port restarts to pick
it up (the telemetry wrap is chosen when the port is built, not live-toggled).

## Next

You've attached a radio — now go **see the link**:
[2. See your link quality →](02-see-your-link-quality.md)
