# packetnet-headend — split-station RF head-end daemon

A small, static, headless Go daemon for a Raspberry Pi that physically holds the
NinoTNC modems + Tait radio-control cables. It exposes **every attached serial
device as a raw TCP byte pipe**, plus a tiny machine API (inventory +
line-control) and an mDNS advertisement, so a **remote PDN** box can discover the
fleet, dial the pipes, and drive the devices with its own drivers.

The head-end is a **dumb, transparent multiplexer**. It does **no device
identification** (no NinoTNC `GETVER`, no Tait CCDI `MODEL`) and **no protocol
parsing** — KISS and CCDI are both just bytes on the wire. Identification and the
whole AX.25 / radio-control stack live on PDN, reaching *through* the pipe. There
is no auth, no TLS, no web UI: it runs on a trusted LAN / Tailscale and PDN
monitors everything upstream.

Design + rationale: [`docs/research/split-station-rf-headend.md`](../docs/research/split-station-rf-headend.md)
(this is **Stage 2** of that arc; Stage 1 was the PDN-side TCP serial seam).

## What it does

1. **Enumerates** serial devices with **stable, unique IDs** from the physical USB
   topology (`/dev/serial/by-path` → `/dev` basename), so two devices that share a
   USB serial still get distinct stable ids that can never collide — see
   [Device identity & stability](#device-identity--stability). Captures USB
   `VID:PID` (from sysfs) + the by-id/by-path strings as hints for PDN's identify
   step.
2. **Bridges** each device: opens the serial port and listens on its own TCP
   port, pumping bytes bidirectionally and transparently (no framing, no
   escaping — `0xC0`/`0xFF` pass straight through). One client at a time per
   device; clean re-accept on disconnect.
3. Serves an **HTTP machine API** (JSON, no UI) — inventory, line-control,
   health.
4. **Advertises** over mDNS so PDN can discover the fleet and re-find an instance
   across IP changes.
5. **Hot-plugs**: a periodic re-enumerate + diff adds a bridge for a device
   plugged in after startup and tears one down for a device unplugged — without a
   restart, leaving untouched devices (and their clients) alone. See
   [Hot-plug](#hot-plug).

## Install

Single static binary — no runtime, no deps.

```sh
# On the Pi (arm64 shown; use -linux-arm for 32-bit userland):
sudo install -m0755 packetnet-headend-linux-arm64 /usr/local/bin/packetnet-headend
sudo install -m0644 packetnet-headend.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now packetnet-headend
```

The unit runs as a `DynamicUser` in the `dialout` group (least-privilege access
to `/dev/tty{USB,ACM}*`). If your distro gates serial access on a different group
(e.g. `uucp`), adjust `SupplementaryGroups=`.

## Configuration

Resolution order (later wins): **defaults < JSON file < environment < flags**.
Everything has a sane default; a bare `packetnet-headend` bridges every serial
device it finds.

| Setting | Flag | Env (`PACKETNET_HEADEND_…`) | JSON key | Default |
| --- | --- | --- | --- | --- |
| Instance id/name | `--instance` | `INSTANCE` | `instanceId` | **`{hostname}-{machine-id hash}`** |
| Bind address | `--bind-addr` | `BIND_ADDR` | `bindAddr` | *(empty = all interfaces)* |
| HTTP API port | `--http-port` | `HTTP_PORT` | `httpPort` | `7300` |
| Base TCP bridge port | `--base-tcp-port` | `BASE_TCP_PORT` | `baseTcpPort` | `7301` |
| Default serial baud | `--baud` | `BAUD` | `baud` | `9600` |
| Allow globs | `--allow` | `ALLOW` | `allow` | `["*"]` |
| Deny globs | `--deny` | `DENY` | `deny` | `[]` |
| Hot-plug rescan interval | `--rescan-interval` | `RESCAN_INTERVAL` | `rescanInterval` | `3s` |
| Config file path | `--config` | `CONFIG` | — | *(none)* |

- **Instance id** is the *stable, unique* identity advertised over mDNS (as both
  the DNS-SD instance label and the TXT `instance=` key) and returned in the
  inventory. PDN keys device→port bindings by `(instanceId, port.id)`, so it
  **must not change** across reboots/address changes **and must be unique per box
  on the LAN**. Zero-config default = `{hostname}-{short machine-id hash}` (see
  [Multiple head-ends / instance identity](#multiple-head-ends--instance-identity)),
  so two Pis imaged from one card don't collide on a shared hostname. **For fixed
  installs, pin an explicit stable id** (e.g. `--instance shack-north`).
- **Bind address**: the local address every listener binds to — the HTTP API
  **and** every raw-serial bridge. Empty (the default) binds **all interfaces**
  (`:port`), exactly as before. Set it to a single address (a Tailscale
  `100.x.y.z`, a `tailscale0` / VPN address, or `127.0.0.1`) to fence the
  **auth-less-by-design** daemon onto one trusted interface — see
  [Restricting the listen interface](#restricting-the-listen-interface).
- **Base TCP port**: bridge ports are allocated **sequentially** from here in
  inventory order (`7301`, `7302`, …).
- **Baud**: the rate serial ports are *opened* at. NinoTNC CDC-ACM ignores baud;
  a Tait UART needs it, but PDN re-clocks via the line-control verb (its baud
  sweep), so this default rarely matters.
- **Allow/Deny** are shell globs (`path.Match`) matched against a device's
  `/dev` basename **and** its by-id basename. A device is bridged when it matches
  some Allow glob and no Deny glob.
- **Hot-plug rescan interval**: how often the daemon re-enumerates to pick up a
  device plugged in (or drop one unplugged) *after* startup — see
  [Hot-plug](#hot-plug). A Go duration (`3s`, `500ms`); JSON also accepts a bare
  number of seconds. **`0` disables** the rescan → startup-only enumeration
  (today's original behaviour, no re-scan).

Example JSON config (`/etc/packetnet-headend/config.json`):

```json
{
  "instanceId": "pi-shack-north",
  "bindAddr": "",
  "httpPort": 7300,
  "baseTcpPort": 7301,
  "baud": 9600,
  "allow": ["*"],
  "deny": [],
  "rescanInterval": "3s"
}
```

(`bindAddr` shown at its default — an empty string binds all interfaces. Omit it
or leave it empty unless you want to restrict the listen interface, below.)

## Multiple head-ends / instance identity

Several head-ends (one per Pi) coexist on one LAN — PDN discovers the whole fleet
over mDNS and keeps them apart by `instanceId`. So **every box must advertise a
distinct `instanceId`**; a duplicate makes PDN unable to tell two boxes apart.

- **Recommended for production (fixed installs): pin an explicit stable id.**
  Choose something meaningful and stable, independent of hostname —
  `--instance shack-north`, `PACKETNET_HEADEND_INSTANCE=garage-pi`, or
  `"instanceId": "shack-north"` in the JSON config. This is the least surprising
  setup: the identity is what *you* chose, survives re-imaging, and reads clearly
  in a browse.
- **Zero-config default (no override): `{hostname}-{short}`.** `{short}` is an
  8-hex-char stable per-machine token — the first 8 hex of a SHA-256 of
  `/etc/machine-id` (falling back to `/var/lib/dbus/machine-id`, then to a hash of
  the first non-loopback NIC MAC, then — last resort — a fixed literal with a
  logged warning). It is deterministic across reboots yet distinct across
  image-cloned Pis (systemd re-seeds `/etc/machine-id` on a fresh image's first
  boot), so two cards flashed from one image and both named `raspberrypi` come up
  as e.g. `raspberrypi-1a2b3c4d` and `raspberrypi-9f8e7d6c` instead of colliding.

Note that mDNS's own probe-and-rename (RFC 6762 §8.1/§9) only deconflicts the
DNS-SD label and `.local` hostname — **not** the TXT `instance=` payload PDN keys
on — and the responder library here doesn't probe anyway. So `instanceId`
uniqueness is an **application-level** guarantee: the derived default provides it
by construction, and an explicit pin is the operator asserting it. PDN is the
backstop — a duplicate `instance=` surfaces as a loud conflict there, never a
silent mis-bind.

## Device identity & stability

PDN binds each remote radio/modem by `(instanceId, port.id)`, so a port's `id`
must be both **stable** (same across reboots/same-port replug) *and* **unique**
(never two devices with the same `id` in one inventory). The head-end derives `id`
from the **physical USB topology**, with a `/dev` last resort, and de-duplicates
the result so both guarantees always hold:

1. **`/dev/serial/by-path` basename** — udev's per-**physical-socket** name (the USB
   topology). The **primary id for every device**. It is **unique by construction**
   (two devices can't share a socket, so it can never collide) and **stable across
   reboot + same-port replug**. `idSource: "by-path"`, `idStable: true`.
2. **`/dev` basename** (`ttyUSB0`, …) — **last resort only**, when a device has no
   by-path link (a minimal udev config lacking `60-serial.rules`). The kernel name
   **reorders across reboot/replug**, so this is logged as unstable and surfaced as
   `idSource: "dev"`, `idStable: false`. PDN should warn on it.

The **`/dev/serial/by-id` string is retained** in each port's `byId` field as an
informational hint (the device serial/model, and the basename the allow/deny
filter matches on) — but it is **no longer used to derive the `id`**. See
[Why by-path, not by-id: shared USB serials](#why-by-path-not-by-id-shared-usb-serials).

After deriving, a **dedupe pass** guarantees no two devices share an `id`: on the
(rare, cross-source) chance two derive the same key (a by-path basename equal to
another device's `/dev` basename), a unique discriminator is appended — a by-path
segment (kept stable) where available, else the `/dev` basename or an enumeration
index (which downgrades that `id` to `idStable: false`).

> **Moving a device to a different USB port changes its `id`** (the by-path names
> the socket, not the device). That is correct: a physical re-cabling is a
> reconfiguration, so PDN re-adopts the port. A **same-port replug and a reboot
> keep the `id`.**

### Why by-path, not by-id: shared USB serials

Some USB-serial chips report a **fixed, non-unique serial** — e.g. the CP2102 in
the Tait CCDI cables all report serial `0001`. udev can only create **one**
`by-id` symlink for a colliding serial, so with two such dongles plugged in only
one gets a `by-id` link at all. Worse, on a **hot-replug** udev can **flip** that
single shared `by-id` symlink onto the *returned* dongle — stealing it from the
still-present sibling, so the returned device enumerates with the **same id** as
its sibling's live bridge. The rescan diff then sees that id already bridged and
**fails to re-add** the returned device, and the id→device mapping is ambiguous
(this is [#574], found on the bench).

**by-id is only stable-unique when the serial is unique**, so it can't be the id.
**by-path names the physical socket**, which is unique by construction and can't
flip: both CP2102 `0001` dongles get their **distinct by-path ids** (e.g.
`…-usb-0:1:1.0-port0` vs `…-usb-0:2:1.0-port0`), stable, no flip, no collision —
regardless of which one currently holds the shared `by-id` symlink. (A belt-and-
suspenders guard in the rescan additionally refuses to bind a new device onto an
existing bridge's id, disambiguating instead of dropping it.) This supersedes the
earlier by-id-first chain (#569).

> udev normally populates `/dev/serial/by-path` for every USB-serial device
> out of the box. If a minimal image lacks it, install/enable the standard
> `60-serial.rules` (udev/systemd) so the head-end has the by-path link to reach
> for — otherwise a device drops to the unstable `/dev` fallback.

[#574]: https://github.com/packet-net/packet.net/issues/574

## Hot-plug

The head-end picks up devices plugged in — and drops devices unplugged — **at
runtime, without a restart**. Every `rescanInterval` (default **3s**) it
re-enumerates (the same `/dev/serial/by-path → /dev` id derivation and the same
allow/deny filter as startup) and **diffs** the result against the live bridge
set, keyed by each device's stable `id` **and** its resolved kernel path:

- **New device** (enumerated, not currently bridged) → the daemon opens its
  serial at the default line params, binds the **lowest free TCP port ≥
  `baseTcpPort`**, starts the bridge, and adds it to the inventory. It logs
  `bridge added …`. A transient open failure (busy / permission) is logged
  **once** and retried on the next tick (not re-logged every interval).
- **Gone device** (bridged, no longer enumerated) → the daemon **closes** its
  bridge (stops accepting, disconnects any connected client, drops the serial
  handle), **frees its TCP port**, and removes it from the inventory. It logs
  `bridge removed …`.
- **Unchanged device** (still enumerated, already bridged) → left **completely
  untouched**. Its bridge and any **connected client keep running undisturbed** —
  the rescan only ever acts on the delta. (A device whose `id` shifts but whose
  kernel path is unchanged — e.g. a late udev by-path link appearing — counts as
  unchanged, so it never churns. And a *new* device that would collide on an
  existing bridge's `id` is disambiguated and added, never dropped or mis-bound —
  the [#574] cross-pass guard.)

`GET /inventory` always reflects the **live** set (the rescan and the HTTP API
share a mutex-guarded registry), so PDN sees adds/removes on its next poll.

**A re-plugged device may come back on a different TCP port** (its old port may
have been reused by another device in the meantime). That is fine: PDN
re-resolves `tcpPort` from `/inventory` at bring-up, so its reconnecting transport
self-heals — bindings are keyed by `(instanceId, id)`, not by port.

**Disabling it.** Set `rescanInterval` to **`0`** (`--rescan-interval 0` /
`PACKETNET_HEADEND_RESCAN_INTERVAL=0` / `"rescanInterval": 0`) to turn the poll
off entirely — the daemon then enumerates **once at startup** and never re-scans,
exactly the original behaviour.

> **Why poll, not udev-netlink?** It keeps the daemon dependency-free and tiny
> (its design goal); a few-seconds latency is irrelevant for hot-plug, and
> polling sidesteps the udev by-id/by-path symlink-creation lag a netlink watcher
> would race. A udev-netlink watcher could be added later as an "instant"
> optimisation on top of (or in place of) the poll.

## Restricting the listen interface

The head-end is **auth-less and unencrypted by design** — it trusts its network
(a LAN, or a Tailscale tailnet). If the box also has an untrusted interface (a
public NIC, a guest VLAN), pin **`bindAddr`** so PDN can reach it but the wider
world can't.

`bindAddr` restricts **both** the HTTP machine API **and every raw-serial bridge
port** to one local address. Empty (the default) binds all interfaces — no
behaviour change. Set it to the head-end's address on the trusted network:

```sh
# Restrict to this box's Tailscale address (from `tailscale ip -4`):
packetnet-headend --instance shack-north --bind-addr 100.101.102.103

# …or via env (e.g. in the systemd unit's Environment=):
PACKETNET_HEADEND_BIND_ADDR=100.101.102.103

# …or in the JSON config:
#   { "instanceId": "shack-north", "bindAddr": "100.101.102.103" }
```

Notes:

- **mDNS still multicasts** on all interfaces (it is not gated by `bindAddr`);
  what's fenced is where the API + bridges *accept connections*. On a
  Tailscale-only reach you'd typically set an explicit
  [`headEnds` address](../docs/research/split-station-rf-headend.md) on the PDN
  side anyway (multicast rarely crosses a tailnet), and this makes the daemon
  itself only reachable there.
- Pick an address that is **stable** for the trusted interface. A Tailscale
  `100.x` address is stable per node; a DHCP LAN address may not be (PDN keys on
  the `instanceId`, so a moved address doesn't orphan port configs — but a
  `bindAddr` pinned to a stale IP stops the daemon from listening).
- Binding to `127.0.0.1` makes the head-end reachable only from the same box —
  useful with an SSH tunnel or a co-located reverse proxy, but not for the normal
  "separate PDN box" split.

## API contract

Machine API on the HTTP port (default `7300`). JSON, no UI, no auth.

### `GET /inventory`

```json
{
  "instanceId": "pi-shack-north",
  "ports": [
    {
      "id": "pci-0000:00:14.0-usb-0:3:1.0",
      "devPath": "/dev/ttyACM0",
      "usbVid": "04d8",
      "usbPid": "000a",
      "byId": "/dev/serial/by-id/usb-NinoTNC_TARPN-if00",
      "byPath": "/dev/serial/by-path/pci-0000:00:14.0-usb-0:3:1.0",
      "idSource": "by-path",
      "idStable": true,
      "tcpPort": 7301,
      "baud": 9600,
      "dataBits": 8,
      "parity": "none",
      "stopBits": 1
    }
  ]
}
```

- `id` — the **stable, unique identity key**. PDN binds `(instanceId, id)`.
  Derived from the [physical USB topology](#device-identity--stability) (by-path →
  `/dev` basename) and de-duplicated so no two ports ever share an `id`.
- `idSource` — which link the `id` came from: `"by-path"` | `"dev"`. `"dev"` is
  the last-resort, unstable fallback. (`"by-id"` is **not** an id source — see
  `byId` below.)
- `idStable` — `false` **only** for the `"dev"` fallback (the kernel name can
  reorder across reboot/replug); `true` for `by-path`. PDN can warn on an unstable
  binding.
- `devPath` — resolved kernel device; informational.
- `usbVid` / `usbPid` — lowercase 4-hex USB IDs, or `""` if unavailable. Hints
  for PDN's identify step (e.g. NinoTNC `04d8`, CP2102 `10c4`, FTDI `0403`).
- `byId` — full by-id symlink path, or `""`. **Informational only** (device
  serial/model hint, and the allow/deny match key) — **not** the id, because a
  shared USB serial makes it collide/flip ([#574](#why-by-path-not-by-id-shared-usb-serials)).
- `byPath` — full by-path symlink path, or `""`. This is the physical-topology
  link the `id` is derived from.
- `tcpPort` — the raw byte-pipe port. **Dial this** and speak KISS/CCDI directly.
- `baud`/`dataBits`/`parity`/`stopBits` — current serial line params.
  `parity` ∈ `none|even|odd`; `stopBits` ∈ `1|2`.

### `POST /ports/{id}/line`

Reconfigure a port's serial line params (PDN's baud sweep + rare re-clock). The
data socket stays a pure binary pipe — line params ride here, out-of-band
(deliberately **not** RFC2217, whose `0xFF` escaping collides with binary
CCDI/KISS). Partial requests merge onto the current params; `baud` is required.

Request:

```json
{ "baud": 19200, "dataBits": 8, "parity": "none", "stopBits": 1 }
```

`dataBits`/`parity`/`stopBits` are optional (omitted → unchanged). Response is
the effective, normalized params (200):

```json
{ "baud": 19200, "dataBits": 8, "parity": "none", "stopBits": 1 }
```

Errors: `404` unknown `id`; `400` missing/invalid `baud`, or invalid
`dataBits` (5–8) / `parity` / `stopBits`. Error body: `{"error":"…"}`.

### `GET /healthz`

`200` with body `ok`.

## mDNS discovery

Advertises DNS-SD service **`_pdnhead._tcp`** in domain `local.`, with the
**DNS-SD instance label = the `instanceId`** (so the box is human-identifiable in
a `dns-sd -B` / Avahi browse, and rides any probing responder's rename) and the
**SRV port = the HTTP API port** (so a browse result hits `/inventory` directly).
TXT record keys:

| TXT key | Value | Meaning |
| --- | --- | --- |
| `instance` | instance id | stable identity — tell instances apart, re-find across IP changes |
| `httpport` | HTTP port | echo of the SRV port for TXT-only clients |
| `v` | `1` | advertisement schema version |

mDNS is **best-effort**: on a routed/VLAN/Tailscale LAN where multicast doesn't
cross, the daemon logs the failure and carries on — PDN falls back to a manual
`host:port` list (PDN-side).

## Build

Static, `CGO_ENABLED=0`, cross-compiled per Pi arch:

```sh
make            # builds dist/packetnet-headend-linux-arm64 + -linux-arm
make arm64      # 64-bit (Pi 3/4/5, Zero 2 W 64-bit)
make arm        # 32-bit ARMv7 userland
make amd64      # host/dev box
make check      # local gate: gofmt + go vet + go test
```

Equivalent raw command:

```sh
CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build -trimpath -ldflags="-s -w" -o packetnet-headend .
```

The arm64 binary is ~6.4 MB stripped.

## Dependencies

Deliberately minimal: [`go.bug.st/serial`](https://pkg.go.dev/go.bug.st/serial)
(pure-Go serial, CGO-free) and
[`grandcat/zeroconf`](https://pkg.go.dev/github.com/grandcat/zeroconf) (mDNS);
everything else is the standard library.

## Known simplifications (Stage 2 scope)

- Serial handles are opened **eagerly** when a device is bridged (at startup, or
  when the [hot-plug](#hot-plug) rescan first sees it) and held for the bridge's
  life. A device that fails to open (busy/permission) is logged and skipped; the
  rest still come up, and the rescan retries it on later ticks. A device
  unplugged at runtime **is** hot-re-enumerated now (its bridge is torn down); the
  poll is a few-seconds-latency re-scan, not an instant udev-netlink watch (see
  [Hot-plug](#hot-plug)).
- One client at a time per device pipe; a second connection waits in the accept
  backlog until the first disconnects.
