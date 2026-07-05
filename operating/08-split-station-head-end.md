# 8. Split-station RF head-end

**Goal:** run your modems and radios on one box (a Raspberry Pi in the shack, by
the antenna) and PDN on a *different* box, with the two talking over your network.
Then, from PDN, **discover that Pi and adopt any modem+radio you plugged into it**
— no per-device wiring on the PDN side.

This is a different axis from [chapters 1](01-attach-a-radio.md) and
[6](06-tnc-less-tait-links.md): those are about *how* a radio joins a port; this
is about *where the hardware physically lives*. Everything you already know about
attaching a radio, seeing link quality, the doctor and tuning still applies — the
serial cables just terminate on a remote Pi instead of the PDN box.

## The topology

```
   shack / mast                              wherever PDN runs
 ┌───────────────────┐                    ┌────────────────────┐
 │  RF head-end (Pi) │   your network     │  PDN compute node  │
 │  NinoTNC ─┐       │  (LAN / Tailscale) │  AX.25 stack,      │
 │  Tait   ─┤ USB    │ ◄────────────────► │  sessions, node,   │
 │  NinoTNC ─┤       │   raw TCP pipes    │  radio control,    │
 │  Tait   ─┘       │   + inventory API  │  tuning, metrics    │
 │  packetnet-headend│                    │  (no local serial) │
 └───────────────────┘                    └────────────────────┘
```

- The **RF head-end** is a small box — typically a Pi — that physically holds the
  NinoTNC modems and the Tait radio-control cables on its USB. It runs a tiny,
  headless Go daemon (`packetnet-headend`) that exposes **every attached serial
  device as a raw TCP byte pipe**, plus a small inventory API and an mDNS
  advertisement. It does no AX.25, no KISS, no CCDI — it is a dumb, transparent
  multiplexer.
- **PDN** runs on a separate box (often an LXC or a mini-PC) with **no local
  serial** at all. It reaches *through* the pipes with its own drivers, so RSSI/SNR,
  hardware carrier-sense (DCD), tuning and SDM all work exactly as if the radios
  were local.

**Why split them?** The radios want to be at the antenna (short feedline, up a
mast, in a weather box); PDN wants to be where it's convenient to run and update a
Linux service (a rack, a NUC, a VM). You can also fan **one PDN node out to several
head-ends** — a Pi in the loft, one in the garage, one at a remote site over
Tailscale — and adopt the modems/radios on each. A modem and its radio are always
cabled together on **one** head-end (they're a co-located pair); PDN never splits a
modem on one Pi from its radio on another.

Design rationale and the full protocol live in
[`docs/research/split-station-rf-headend.md`](../docs/research/split-station-rf-headend.md).

## Deploy the head-end (on the Pi)

### Build the binary

The daemon is a single static binary — no runtime, no dependencies. Cross-compile
it for the Pi's architecture from any dev box:

```sh
cd headend
make arm64      # 64-bit Pi (Pi 3/4/5, Zero 2 W 64-bit) → dist/packetnet-headend-linux-arm64
make arm        # 32-bit ARMv7 userland → dist/packetnet-headend-linux-arm
```

Each is a static `CGO_ENABLED=0` build (~6.4 MB, stripped) that copies straight
onto the Pi.

### Install it

Two ways — the **`.deb`** (recommended: it enables + starts the service for you,
just like the node package) or a manual binary copy.

**Option A — the `.deb` (recommended).** The
[head-end release](https://github.com/packet-net/packet.net/releases?q=headend)
ships a Debian package per arch (`amd64` / `arm64` / `armhf`). Copy the one for
your Pi and install it — no build step needed:

```sh
# arm64 shown; use _armhf.deb for a 32-bit userland, _amd64.deb for a dev box.
sudo apt install ./packetnet-headend_<version>_arm64.deb
#   …or, offline:  sudo dpkg -i ./packetnet-headend_<version>_arm64.deb
```

The `.deb` drops the static binary at `/usr/lib/packetnet/packetnet-headend`,
installs the systemd unit, and **enables + starts the service on install** — a
fresh install is plug-and-go on defaults (a hostname-derived `instanceId` and
every serial device auto-bridged). It ships a config example at
`/usr/share/packetnet/packetnet-headend.json.example`; the config is optional (see
[Configure it](#configure-it--pin-a-stable-instance-id) — you should still pin a
stable `instanceId`). Confirm it came up:

```sh
systemctl status packetnet-headend
curl -s http://localhost:7300/inventory
```

**Option B — manual binary copy.** For a non-`.deb` system, or to run straight
from a `make` build:

```sh
# On the Pi (arm64 shown):
sudo install -m0755 packetnet-headend-linux-arm64 /usr/local/bin/packetnet-headend
sudo install -m0644 packetnet-headend.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now packetnet-headend
```

Either way, the unit runs as a locked-down `DynamicUser` in the `dialout` group
(least-privilege access to `/dev/tty{USB,ACM}*`). If your distro gates serial on a
different group (e.g. `uucp`), adjust `SupplementaryGroups=` in the unit.

### Configure it — pin a stable instance id

Everything has a sane default, so a bare `packetnet-headend` already bridges every
serial device it finds. But there is **one setting you should always set on a fixed
install**:

> [!IMPORTANT]
> **Pin a stable, unique `instanceId`.** PDN keys every device→port binding by
> `(instanceId, deviceId)`, not by IP — that's what lets the Pi reboot onto a new
> DHCP address without orphaning your port configs. The zero-config default is
> `{hostname}-{machine-id hash}`, which is unique but ugly; for a real install
> choose something meaningful and stable that survives re-imaging:
>
> ```sh
> packetnet-headend --instance shack-north
> ```
>
> or in the systemd unit: `Environment=PACKETNET_HEADEND_INSTANCE=shack-north`, or
> in the JSON config: `"instanceId": "shack-north"`. Two Pis with the **same** id
> is the one thing that trips PDN up (see [Troubleshooting](#troubleshooting)).

The other settings you may touch (all in the config table in
[`headend/README.md`](../headend/README.md)):

| Setting | Flag | Default | Notes |
|---|---|---|---|
| Instance id | `--instance` | `{hostname}-{hash}` | **Pin it** (above). |
| HTTP API port | `--http-port` | `7300` | Where PDN reads the inventory. |
| Base bridge port | `--base-tcp-port` | `7301` | Bridge ports are allocated **sequentially** from here (`7301`, `7302`, …) in inventory order. |
| Allow / Deny globs | `--allow` / `--deny` | `["*"]` / `[]` | Shell globs over each device's `/dev` **and** by-id basename — bridge only some devices (e.g. `--allow 'usb-Nino*'`). |
| Bind address | `--bind-addr` | *(empty = all interfaces)* | Restrict every listener to one trusted address (a Tailscale `100.x.y.z`). |

> [!NOTE]
> The head-end is **auth-less and unencrypted by design** — it trusts its network
> (a LAN, or a Tailscale tailnet). If the Pi also faces something untrusted, set
> **`--bind-addr`** to its address on the trusted interface: that fences both the
> HTTP API and every raw bridge port onto that one address. See
> [`headend/README.md` § Restricting the listen interface](../headend/README.md#restricting-the-listen-interface).

## Attach from PDN — the web way

This is the "plug into any port and go" path. Open the node's web control panel and
go to **Head-ends** (`/headends`).

The screen discovers the fleet — every head-end advertising itself over mDNS, plus
any you pinned in config — and lists each one as a card with its id, its
`host:port`, a **Source** badge (`mDNS` or `config`), and a reachable/unreachable
status dot. Hit **Rescan** to browse again.

Each reachable card shows the **devices** that head-end bridges — id, a kind badge
(**NinoTNC** / **Tait CCDI radio**), model, version, baud, and whether each is
**free** or already **in use** (a device bound to a running port isn't re-probed or
re-adoptable — the head-end is one-client-per-pipe). Below the device list is the
adopt affordance, which takes one of a few shapes. The shipped demo (run
`web/packetnet-ui` with no node to see it — `VITE_API_MODE` unset) shows all four:

- **`shack-north` — one-click auto adopt.** A reachable head-end with **exactly one
  free NinoTNC and one free Tait radio** shows a green **"suggested pairing"** —
  the two devices with a link between them — and a single **Adopt** button. Click it
  and PDN creates **one matched port** for the pair. (It also has a third device
  already **in use** by a running port, shown greyed — proof that adopted devices
  drop out of the free pool.)
- **`garage-pi` — the ambiguous picker.** A head-end with **more than one** free
  modem or radio can't be auto-paired, so it shows **"choose a pairing"**: a
  **Modem (NinoTNC)** dropdown and a **Radio (Tait CCDI)** dropdown over its free
  devices. **Adopt** stays disabled until you've picked one of each. (This
  instance also demonstrates a **config-pinned** source badge rather than mDNS.)
- **`spare-pi` — the duplicate-id conflict card.** If two boxes advertise the *same*
  `instanceId` with no config address to tell them apart, PDN **won't guess** which
  to bind — it renders a loud red **"Duplicate head-end id"** card at the top,
  listing both clashing addresses, and adopts neither. The fix is in the card: pin
  **distinct** `instanceId`s on the two boxes, or set an explicit `address` in
  config to disambiguate.
- **`attic-relay` — unreachable / error.** A head-end that was discovered but can't
  be reached (daemon down, wrong address) shows an **unreachable** badge and the
  underlying error (e.g. *connection refused*), with no device list — nothing came
  back to adopt.

Both adopt panels have an **Options** disclosure for a custom **Port id** (defaults
to the head-end's instance id) and the NinoTNC **Modem mode** (0–15, defaults to 0).
Adopting is **operate-scope** gated — a read-only login sees the pairing but the
**Adopt** button is disabled.

Once you adopt, the new port comes up just like a local one — go
[see the link](02-see-your-link-quality.md).

## Attach from PDN — the config way

If you drive PDN by YAML/API rather than the UI, you declare the fleet and the
head-end-bound ports by hand. Two pieces:

**1. Declare the head-end(s)** in a top-level `headEnds:` list — each is an `id`
(the head-end's `instanceId`) and, optionally, its `address`:

```yaml
headEnds:
  - id: shack-north               # == the daemon's instanceId
    address: 192.168.1.44:7300    # optional; omit to resolve by mDNS discovery
  - id: garage-pi
    # no address → discovered over mDNS (or pinned later)
```

**2. Reference its devices from a port.** A head-end-bound port pairs a
`nino-tnc-tcp` transport (the modem, as a raw TCP pipe) with a head-end-bound
`tait-ccdi` radio (the co-located radio's control channel), **both on the same
`headEndId`**:

```yaml
ports:
  - id: shack-north
    enabled: true
    transport:
      kind: nino-tnc-tcp          # a NinoTNC bridged by the head-end (not local serial)
      headEndId: shack-north      # which head-end
      deviceId: usb-0             # the NinoTNC's inventory id on that head-end
      mode: 6                     # NinoTNC modem mode
    radio:
      kind: tait-ccdi             # the co-located radio's CCDI control channel
      headEndId: shack-north      # SAME instance — a modem+radio pair is always co-located
      deviceId: usb-1             # the Tait's inventory id on that head-end
      baud: 28800                 # CCDI control-channel baud (Tait default)
```

The `deviceId`s are the stable inventory ids the head-end reports (its
`GET /inventory` — the same ids the web screen shows). Validation enforces the
rules the UI enforces for you: every `headEndId` a port references must be declared
in `headEnds:`, and a head-end-bound radio must pair with the co-located
`nino-tnc-tcp` transport on the **same** instance.

> [!NOTE]
> Because PDN binds by `(headEndId, deviceId)` and resolves the id to a current
> address at bring-up, a head-end that reboots onto a new DHCP address **doesn't
> orphan** these port configs — as long as its `instanceId` stayed the same.

## Troubleshooting

- **The Head-ends screen is empty / a head-end never appears.** mDNS is best-effort
  and multicast **does not cross** subnets, most VLANs, or a Tailscale tailnet. Give
  the head-end an explicit `address` in the `headEnds:` list (`{ id, address:
  host:port }`) — that's the manual fallback and needs no discovery at all. Check
  the daemon is up (`systemctl status packetnet-headend`) and reachable on its HTTP
  port (default `7300`).
- **A "Duplicate head-end id" conflict card.** Two boxes are advertising the same
  `instanceId` and PDN refuses to guess which one to bind. Give each box a
  **distinct** `instanceId` (`--instance` / env / config — the recommended fix), or
  set an explicit `address` for each in the PDN-side `headEnds:` config to
  disambiguate them.
- **A device shows up but isn't identified (kind "Unknown").** Identification is
  reach-through: PDN probes the pipe (NinoTNC `GETVER`, Tait `MODEL`). A Tait that
  doesn't answer at the inventory baud triggers a **baud sweep** — PDN re-clocks and
  re-queries `MODEL` across the standard CCDI rates until it gets a checksummed
  reply. If a device still won't identify, confirm it's actually a NinoTNC or a Tait
  in CCDI mode, and prefer stable **`/dev/serial/by-id`** devices on the Pi so the
  by-id string is a reliable identity key.
- **A device is stuck as "in use" and won't re-probe.** The head-end is
  **one-client-per-pipe**: a device already bound to a configured PDN port holds the
  socket, so it isn't re-probed or offered for adoption. Remove/disable the port
  that owns it first if you need to re-adopt it elsewhere.

## Where next

- [`docs/research/split-station-rf-headend.md`](../docs/research/split-station-rf-headend.md)
  — the full design: the seam that carries everything, carrier-sense over the wire,
  instance identity/deconfliction, and the discovery/adoption flow.
- [`headend/README.md`](../headend/README.md) — the daemon's own reference: config
  table, the inventory/line-control API, mDNS record, and the build.

Return to the [operating guide index](index.md).
