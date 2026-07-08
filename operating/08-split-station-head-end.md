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

> [!WARNING]
> **Upgrading the `.deb` restarts the daemon and drops every bridge.** The
> package's postinst runs `systemctl try-restart` on upgrade (a running unit
> must be bounced or the new binary never loads), which disconnects every raw
> pipe — so every adopted port on the PDN side loses its modem *and* radio
> sockets at once. Adopted ports **reconnect automatically**: PDN's per-socket
> supervision self-heals both the data channel (`nino-tnc-tcp`) and the
> radio-control channel (`tait-ccdi`) once the daemon is back, re-resolving the
> current TCP ports from the inventory. Expect a brief outage on every port the
> head-end hosts; time upgrades accordingly.

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
| Hot-plug rescan interval | `--rescan-interval` | `3s` | How often the daemon re-enumerates to pick up plugged/unplugged devices at runtime (see [Hot-plug](#hot-plug)). `0` disables → startup-only enumeration. |
| Allow / Deny globs | `--allow` / `--deny` | `["*"]` / `[]` | Shell globs over each device's `/dev` **and** by-id basename — bridge only some devices (e.g. `--allow 'usb-Nino*'`). |
| Bind address | `--bind-addr` | *(empty = all interfaces)* | Restrict every listener to one trusted address (a Tailscale `100.x.y.z`). |

> [!NOTE]
> The head-end is **auth-less and unencrypted by design** — it trusts its network
> (a LAN, or a Tailscale tailnet). If the Pi also faces something untrusted, set
> **`--bind-addr`** to its address on the trusted interface: that fences both the
> HTTP API and every raw bridge port onto that one address. See
> [`headend/README.md` § Restricting the listen interface](../headend/README.md#restricting-the-listen-interface).
>
> The trust runs the other way too: **mDNS discovery trusts whatever `instanceId`
> is advertised.** Anything on the broadcast domain can advertise
> `_pdnhead._tcp` claiming a real instance's id — and if the genuine Pi is quiet
> at that moment (powered off, rebooting), the impostor resolves cleanly and PDN
> would dial *its* pipes. Pinning an explicit `address` on the PDN side
> (`headEnds:` — see [the config way](#attach-from-pdn--the-config-way)) bypasses
> mDNS resolution entirely, so it is an **integrity measure**, not just a
> reachability fallback for routed networks. **For a fixed install, pin both**:
> the `instanceId` on the head-end and the `address` in PDN's `headEnds:` list.

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

## Band naming and keyup pairing

Two things the scan/adopt flow reads off the hardware itself, so you don't have
to type them:

**Band naming.** A Tait's *tuned frequency* isn't readable over CCDI, but its
**band split is**: the scan reads the product code (the `RADIO_VERSIONS` record
`[00]`, e.g. `TMAB12-B100_0201`) and maps the designator after the first `-`
(`B1` here) to the band — `A4` = 4 m, `B1` = 2 m, `H5`/`H6`/`H7` = 70 cm. When
you adopt, the amateur band (when known) **defaults the new port's id** (unless
you chose one in Options) **and its MQTT `{instance}` label** — so a band-named
port (`2m`) drops straight into an existing kissproxy/`kiss-collector` pipeline
with no operator input.

**Keyup pairing.** The passive scan can only *guess* which modem is cabled to
which radio (the guess is safe only when a head-end has exactly one free modem
and one free radio — the auto-adopt case). For the ambiguous case there is an
**admin** action that discovers the physical map as ground truth:

```
POST /api/v1/radios/headends/{instanceId}/pair-by-keyup
```

It **briefly keys each free NinoTNC in turn** — transmitting a short frame
through the raw pipe, which asserts the cabled radio's PTT — while watching
every free Tait's CCDI PROGRESS stream for the PTT edge. The Tait that reports
transmit is that modem's physical pair. A keyup that fires no radio is reported
unpaired; one that fires more than one is flagged ambiguous, never guessed.

> [!WARNING]
> **Keyup pairing transmits on-air.** It keys every free modem's radio,
> briefly, one at a time. That's why it is admin-scope, explicitly
> operator-initiated, and never folded into the passive scan — run it only on
> frequencies you are licensed and clear to key. The response carries the same
> RF caveat. (It's API-only today — there is no web-UI button.)

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
      deviceId: platform-xhci-hcd.1-usb-0:1:1.0         # the NinoTNC's inventory id on that head-end
      mode: 6                     # NinoTNC modem mode
    radio:
      kind: tait-ccdi             # the co-located radio's CCDI control channel
      headEndId: shack-north      # SAME instance — a modem+radio pair is always co-located
      deviceId: platform-xhci-hcd.1-usb-0:2:1.0-port0   # the Tait's inventory id on that head-end
      baud: 28800                 # CCDI control-channel baud (Tait default)
```

The `deviceId`s are the stable inventory ids the head-end reports (its
`GET /inventory` — the same ids the web screen shows). Since `headend-v0.1.3`
the id is the **`/dev/serial/by-path` basename** — it names the **physical USB
socket** the device is plugged into, so it is unique by construction and stable
across reboots and same-socket replugs. Two consequences worth knowing:

- **Moving a device to a different USB socket changes its id** (the id names
  the socket, not the device) — a physical reconfiguration, so PDN treats it as
  a new device: **re-adopt it** (or update the `deviceId` in config).
- The `/dev/serial/by-id` string is **informational only** (a serial/model hint
  in the inventory's `byId` field) — it is *not* the identity key, because
  shared USB serials make it collide (see
  [`headend/README.md` § Device identity](../headend/README.md#device-identity--stability)).

Validation enforces the
rules the UI enforces for you: every `headEndId` a port references must be declared
in `headEnds:`, and a head-end-bound radio must pair with the co-located
`nino-tnc-tcp` transport on the **same** instance.

> [!NOTE]
> Because PDN binds by `(headEndId, deviceId)` and resolves the id to a current
> address at bring-up, a head-end that reboots onto a new DHCP address **doesn't
> orphan** these port configs — as long as its `instanceId` stayed the same.

## Hot-plug

You don't have to restart the daemon to change the hardware. Every
`rescan-interval` (default **3s**) the head-end re-enumerates its serial
devices and diffs the result against the live bridge set:

- **Plug a device in** → it's bridged, gets a TCP port, and appears in the
  inventory within a few seconds (`bridge added …` in the journal). Rescan the
  Head-ends screen (or re-poll `/inventory`) and it's there to adopt.
- **Unplug a device** → its bridge closes, its TCP port is freed, and it drops
  from the inventory (`bridge removed …`).
- **Everything else is untouched** — an unchanged device's bridge and any
  connected client keep running undisturbed; the rescan only acts on the delta.

**A re-plugged device may come back on a different TCP port** (its old port may
have been reused in the meantime). That's fine: PDN binds by
`(instanceId, deviceId)` — never by TCP port — and re-resolves the current port
from the inventory at bring-up, so a reconnecting adopted port self-heals. A
**same-socket** replug keeps the device id; moving it to a **different** USB
socket gives it a new id (see [the config way](#attach-from-pdn--the-config-way))
— re-adopt it.

Set `--rescan-interval 0` to disable the poll entirely (startup-only
enumeration). Details: [`headend/README.md` § Hot-plug](../headend/README.md#hot-plug).

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
  in CCDI mode.
- **A port stopped binding after you re-cabled the Pi.** The device id **is the
  physical USB socket** (the `/dev/serial/by-path` basename) — moving a device to
  a different socket gives it a **new id**, so the old binding no longer matches:
  re-adopt it (or update the port's `deviceId`). A same-socket replug and a reboot
  keep the id. The `/dev/serial/by-id` string is informational only — it is *not*
  the identity key (shared USB serials make it collide and flip between siblings).
  If the head-end logs an **unstable id** warning (no by-path link at all), install
  the standard `60-serial.rules` udev rules on the Pi.
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
