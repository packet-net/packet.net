# Split-station RF head-end over TCP + PDN autodiscovery

**Status:** ✅ **arc complete** (2026-07-05) — all four stages shipped (see the Stage plan
below and plan.md §17), with post-arc follow-on releases tracked under the Stage plan
(`.deb` packaging, hot-plug + by-path ids; #576/#577 resilience in flight). Anchors the
"Pi holds the modems+radios, a separate LXC runs PDN" topology.

## Topology

Boxes on a LAN (or Tailscale):

- **RF head-end(s)** — one *or more* instances, each a box (typically a Pi) with N NinoTNC + Tait
  radio(s) hanging off its USB. Each owns its serial devices and nothing else. A single PDN
  compute node connects to *all* of them concurrently. **Invariant: a modem+radio pair is always
  co-located on the same head-end instance** (they're cabled together at one physical location), so
  PDN never bridges a TNC on one instance to its radio-control on another.
- **Compute node** — an LXC running PDN. It owns the AX.25 stack, sessions, the node host, the
  radio-control/tuning/metrics logic. It has *no* local serial, and aggregates the radios/modems
  across every head-end instance.

Today PDN can already reach a **KISS TNC** over TCP (the `kiss-tcp` transport → `KissTcpClient`).
What it cannot reach remotely is **radio control** (the Tait CCDI channel: RSSI/SNR, PTT,
carrier-sense/DCD, tuning, SDM, hail) or a NinoTNC's *full* control surface (GETVER / mode agility
/ GETRSSI), both of which bind to local serial. This arc closes that.

## Decisions (Tom, 2026-07-05)

1. **Head-end vehicle: a fresh, purpose-built head-end daemon** — *not* an extension of
   `M0LTE/kissproxy`. Rationale: `kiss-over-tcp` is a protocol PDN doesn't own (built for LinBPQ);
   leave it and kissproxy alone. Our head-end bridges raw serial for PDN↔PDN and does not speak the
   LinBPQ kiss-tcp protocol. **It need not be .NET** — since identification is reach-through (PDN
   side) and MQTT emission lives in PDN, the head-end reuses no packet.net library, so the priority
   is a *small, trivially Pi-installable* footprint. **Language: Go** (Tom, 2026-07-05) — a single
   static binary per arch (arm/arm64), zero runtime, no pip/venv; install = copy one file + a
   systemd unit; CI cross-compiles. Repo precedent: `sidecar/tsnet` (Go). It lives in
   `packet.net/headend/` (sibling to `sidecar/`), out of the .NET solution.
   **It is headless: no human/web UI.** Nothing is configured on the head-end and nothing is
   monitored there — PDN drives and observes everything upstream. The only local surface is a tiny
   machine API (inventory + line-control, JSON over HTTP) plus mDNS.
2. **Identification: PDN reaches through the pipe.** The head-end stays a dumb bridge that lists
   "I'm bridging these ports"; PDN opens each remote port with its existing drivers (NinoTNC
   `GETVER`, Tait CCDI `MODEL`) to identify. No device knowledge — and no packet.net library
   dependency — on the head-end.
3. **Discovery: mDNS/zeroconf + manual-address fallback.** Auto-discover on a flat LAN; allow a
   typed `host:port` for routed / VLAN / Tailscale setups where multicast doesn't cross.
4. **Multiple head-end instances.** PDN supports a *fleet* of head-ends concurrently, each hosting
   N modem/radio pairs. A modem+radio pair is always on one instance (never split across two). See
   "Multiple instances" below.

## Multiple instances

- **Stable instance identity.** Each head-end advertises a stable id/name (a config value, not its
  IP) in its mDNS TXT record and its inventory. This is how PDN tells instances apart and re-finds
  one whose IP changed (DHCP lease, reboot).
- **PDN keys device→port bindings by `(instance-id, device-id)`**, not by `host:port`. A Pi
  rebooting onto a new address must not orphan its port configs — PDN resolves `instance-id →
  current address` via mDNS (or the manual map) at bring-up, then dials the raw pipe. The
  `device-id` is the head-end's stable inventory id: since #575 (headend-v0.1.3) that is the
  **`/dev/serial/by-path` basename — the physical USB socket** — unique by construction and stable
  across reboot/same-socket replug (moving a device to a different socket changes its id →
  re-adopt). This superseded the original "stable-serial" (by-id) notion: a shared, non-unique USB
  serial makes by-id collide and flip between siblings (#574), so by-id is informational only.
- **Pairing is within an instance.** The discover-and-offer flow matches a TNC with a radio only
  within a single instance's inventory (honouring the co-location invariant); it never proposes a
  cross-instance pair.
- **Per-instance supervision.** Each head-end can bounce independently; PDN's per-socket
  reconnect (below) means one instance going away takes down only its own ports, not the fleet.
- **Discovery yields a set.** mDNS browse returns every advertiser; the manual fallback is a
  *list* of head-end addresses. The node UI/config lists the fleet and which devices live where.

### Instance identity & deconfliction

DNS-SD carries a fleet natively — a browse returns *every* instance, so multiple head-ends on one
broadcast domain is the normal case, not a clash. But uniqueness of the identity PDN keys on is **not**
something mDNS gives us for free: the probe-and-rename that a responder runs (RFC 6762 §8.1/§9)
deconflicts the DNS-SD **label** and the `.local` **hostname**, but **not** the TXT `instance=`
payload — and the head-end's responder (`grandcat/zeroconf`) skips probing altogether. So
**`instanceId` uniqueness is an application-level concern**, enforced at three layers:

- **Production (recommended).** Operators pin a stable, unique `instanceId` independent of hostname
  (`shack-north`, `garage-pi`) via `--instance` / env / config. The identity is deliberate, survives
  re-imaging, and is human-readable in a browse.
- **Zero-config default.** When unset, the head-end derives `{hostname}-{short}`, where `{short}` is an
  8-hex-char stable per-machine token: the leading 8 hex of `SHA-256(/etc/machine-id)` (falling back to
  `/var/lib/dbus/machine-id`, then to a hash of the first non-loopback NIC MAC, then a logged fixed
  literal). Deterministic across reboots yet distinct across image-cloned Pis (systemd re-seeds
  `machine-id` on a fresh image's first boot) — so two cards flashed from one image, both `raspberrypi`,
  no longer collide on the bare hostname.
- **Advertised as the DNS-SD instance label + TXT.** `instanceId` is the browse-visible instance label
  *and* the TXT `instance=` key — so it's human-identifiable in a `dns-sd -B` / Avahi browse and would
  ride any probing responder's §8.1/§9 rename, while remaining the exact key PDN binds on.
- **PDN is the backstop (Stage 3b).** If two boxes still advertise the same `instance=` (e.g. an
  operator pins a duplicate by hand), PDN raises a **loud conflict** rather than silently binding one
  box's ports onto the other — the last line of defence behind the derived-default and the operator pin.

## The pivot: one seam carries everything

`TaitCcdiRadio` does **not** depend on `System.IO.Ports`. It drives an internal 3-method byte pipe
`ISerialIo` (`src/Packet.Radio.Tait/ISerialIo.cs`), and there is already an injection point
(`TaitCcdiRadio.OpenForTest(ISerialIo)`). Everything above the driver — `RssiTaggingTransport`,
`RadioCarrierSense`, tuning, SDM, hail — consumes `IRadioControl` or the concrete `TaitCcdiRadio`.

**So a single TCP-backed `ISerialIo` makes the entire radio-control stack operate over a socket,
unchanged.** `NinoTncSerialPort` has the identical seam (`ISerialPortIo` under `KissSerialModem`),
so a NinoTNC can go remote *with full control* — distinct from the control-less generic `kiss-tcp`.

### Carrier-sense over the wire — solved for free

DCD is not a separate poll. It arrives as CCDI `PROGRESS` bytes **on the same serial stream**. Run
`TaitCcdiRadio`'s read pump against the TCP socket and it raises `CarrierSenseChanged` and updates
the cached `ChannelBusy` bool exactly as it does locally. The `CarrierSenseGate` stays co-located
with `Ax25Listener.SendBytes` on the PDN box and reads a **cached bool — never network I/O**; only
the DCD *edge* crosses the wire (~tens of ms, well inside the 0.5–1 s hardware-DCD head-start).

**Hard rule: never make `ICarrierSense.ChannelBusy` a synchronous network poll.** The value's worth
is its freshness; a per-slot network RTT would both add latency and read stale.

## Serial parameters (baud, DTR/RTS)

Baud is almost never a runtime variable:

- **NinoTNC (CDC-ACM):** baud is fictional; `SetBaudRate` is a no-op. Nothing to handle.
- **Tait CCDI (real UART via CP2102):** baud is a genuine clock that must match the codeplug CCDI
  rate, but it is **fixed per radio for the whole control session** — the plain `tait-ccdi`
  attachment never re-clocks.
- The **only** runtime `SetBaudRate` caller is the `tait-transparent` Command↔Transparent switch.

Handling:

1. **Data plane = raw binary TCP pipe**, already clocked at the head-end. Deliberately **not**
   RFC2217 — its IAC (`0xFF`) escaping collides with binary CCDI/KISS streams that legitimately
   contain `0xFF`. Keep the data socket pure binary.
2. **Line parameters live on the head-end's HTTP control plane** (same plane as inventory): the
   head-end reports each port's current params and exposes a "set line params" verb.
3. **Baud discovery is PDN-side** (consistent with "PDN reaches through the pipe"): for a Tait
   port, PDN sweeps the standard CCDI rates via the control verb, running a `MODEL` query after
   each until a valid checksummed reply — clocking *and* identifying in one step. NinoTNC needs no
   sweep. Operator configures baud nowhere. `TcpSerialIo.SetBaudRate` routes to the verb, so the
   rare runtime re-clock works too.
4. **DTR/RTS:** not needed for CCDI/KISS on these dongles (confirm); if ever needed, another verb.
5. **Future:** an RFC2217 client mode on `TcpSerialIo` would let PDN point at a generic ser2net
   box — same seam, different line-control transport. Out of scope now.

## Responsibilities

**Head-end service (new, deployable to the Pi):**

- Enumerate serial ports with stable IDs + USB VID:PID hints (reuse the shape of
  `TaitRadioPortDiscovery` / `NinoTncPortDiscovery` / `SerialByIdResolver`, generalised off local
  `/dev`).
- Bridge each device as a **raw TCP byte pipe**, one TCP port per device.
- **Inventory endpoint** (HTTP): stable id, `/dev` path, USB hints, current line params, TCP port.
- **Line-control verb** (HTTP): set baud/params on a port (PDN's baud sweep + rare re-clock).
- **mDNS advertisement** (e.g. `_pdnhead._tcp`) carrying a **stable instance id/name** (TXT
  record) so PDN can distinguish instances and re-find one across address changes. The same id is
  in the inventory response.

**PDN (compute node):**

- `TcpSerialIo : ISerialIo` and `TcpSerialPortIo : ISerialPortIo` — raw byte pipe over TCP with
  `KissTcpClient`-style robustness (TCP keepalive + read-idle half-open detection); `SetBaudRate`
  routed to an injectable line-control callback (default no-op).
- Public open paths: `TaitCcdiRadio.OpenTcp(...)` and a NinoTNC remote-open, parallel to the
  local `Open`.
- New networked radio/transport kinds carrying `host:port`; lift the `PortRadioConfig` validation
  that forbids a `radio:` block on non-serial transports.
- A remote `IRadioScanner`: mDNS discovery (+ a manual *list* of addresses) → enumerate **every**
  head-end instance → pull each one's inventory → reach through each pipe to identify + baud-lock →
  return `RadioScanResult` rows tagged with their `(instance-id, device-id)`.
- Port configs reference the radio/modem by `(instance-id, device-id)` (the by-path socket id —
  see "Multiple instances"), resolved to a current `host:port` at bring-up — so a re-addressed
  head-end doesn't orphan its ports.
- A discover-and-offer-matched-pairs flow on `PdnRadiosApi` → `PdnPortsApi` that pairs a TNC with a
  radio **within a single instance's inventory** (create the matched KISS + control ports).
- Apply `ReconnectingKissModem`-style supervision **per socket**, so any one head-end bouncing
  self-heals its own ports without disturbing the rest of the fleet.

## Stage plan

1. **Foundational seam** — `TcpSerialIo`/`TcpSerialPortIo` + public `OpenTcp` paths + tests
   (scripted CCDI/GETVER transactions over an in-memory/loopback stream proving RSSI, DCD edges,
   identity work remotely). Library-only; no node config yet. **✅ Done — PR #556 (`22d4efb`):
   `TaitCcdiRadio.OpenTcp` / `KissSerialModem.OpenTcp` / `NinoTncSerialPort.OpenTcp`; the `setBaud`
   callback (`Func<int,CancellationToken,Task>?`, default no-op) carries all line-control; internal
   `TcpSerialIo`/`TcpSerialPortIo`; read-idle→fault semantics for half-open supervision.**
2. **Head-end service** (Go, `packet.net/headend/`) — enumerate → raw bridge → inventory +
   line-control HTTP → mDNS. **✅ Done (Stage 2 amendment, `headend/`).**
3. **PDN remote scanner + config** — networked kinds, lift validation, mDNS+manual discovery,
   reach-through identify + baud sweep, discover-and-offer flow, socket supervision. **✅ Done**
   (both sub-stages below). Split into:
   - **3a — manual config + factories. ✅ Done** (§17 Stage 3a): `HeadEndClient` +
     `HeadEndDeviceResolver` (`Packet.Node.Core/HeadEnd/`), `NodeConfig.HeadEnds` +
     `HeadEndConfig`, the `PortRadioConfig` head-end binding (`headEndId`/`deviceId`), the
     `nino-tnc-tcp` transport kind, the lifted radio-on-networked-transport validation, the
     `RadioControlFactory` / `TransportFactory` remote branches, and per-socket
     `ReconnectingKissModem` supervision for `nino-tnc-tcp`. **Manual head-end addresses only.**
   - **3b — discovery + pairing. ✅ Done** (§17 Stage 3b): mDNS browse of the `_pdnhead._tcp` fleet
     behind `IHeadEndDiscovery` (thin `Zeroconf` wrapper — coexists with the node's own
     `avahi-publish` advertiser); `IHeadEndAddressResolver` resolving `instanceId → address` (config
     address wins, else a single mDNS hit — a duplicate-id discovery is a **conflict**, never a
     guess), wired into `HeadEndDeviceResolver` at bring-up so a blank-address (discover-mode) or
     re-addressed head-end still resolves; the remote `IHeadEndRadioScanner` (reach-through identify
     over the raw pipe — GETVER for NinoTNC, MODEL for Tait — with the CCDI baud **sweep** via the
     wired `setBaud → POST /ports/{id}/line` seam, skipping already-bound devices); and the
     discover-and-offer-matched-pairs flow on `PdnRadiosApi` (`GET /api/v1/radios/headends` scan/preview
     + `POST /api/v1/radios/headends/{instanceId}/adopt` creating the matched port through the
     `PdnPortsApi` validate→preview→apply seam). Per-socket `ReconnectingKissModem` supervision from
     Stage 3a carries the reconnect story.
4. **Wire-up + docs + plan** — operator guide ("plug into any port and go"), plan §17.
   **✅ Done.** Split into:
   - **4a — the discover→offer→adopt web UI** (§17 Stage 4a, PR #562): the **Head-ends**
     screen (`/headends`) in `web/packetnet-ui` over the Stage-3b backend — instance cards
     (id, `host:httpPort`, config-vs-mDNS source, reachable/unreachable), device list
     (free vs in use), one-click **auto adopt** / **ambiguous picker** / loud **duplicate-id
     conflict** card, operate-scope gated.
   - **4b — the operator guide + head-end `bindAddr` + this plan entry** (§17 "arc complete"):
     [`operating/08-split-station-head-end.md`](../../operating/08-split-station-head-end.md)
     ("plug into any port and go" — topology, deploy the Pi daemon, adopt from the web UI or by
     YAML config, troubleshooting), an optional `bindAddr` on the head-end daemon (fence the
     auth-less listeners onto one trusted interface; default empty = bind-all, no behaviour
     change), and the arc-complete ledger entry.

### Post-arc follow-ons (after the 2026-07-05 arc-complete)

- **headend-v0.1.2 — `.deb` packaging. ✅ Shipped** (§17 2026-07-05): the daemon ships as a
  Debian package per arch (amd64/arm64/armhf), mirroring the node —
  `scripts/build-headend-deb.sh`, systemd unit **enabled + started on install** (plug-and-go on
  defaults), postinst **`try-restart` on upgrade** (a running daemon is bounced so the new binary
  loads — every bridge drops; PDN's supervision reconnects adopted ports), amd64 install-smoke in
  `publish-headend.yml`.
- **headend-v0.1.3 — hot-plug + by-path device ids. ✅ Shipped** (§17 2026-07-06,
  hardware-validated): poll-based re-enumerate + diff (`--rescan-interval`, default **3s**, `0`
  disables) adds/removes bridges at runtime, leaving unchanged bridges and their connected clients
  untouched (#572); and the device id became the **`/dev/serial/by-path` basename** — the physical
  USB socket, collision-proof by construction — with `/dev` basename as the unstable last resort
  and by-id demoted to an informational hint (#574 fix, PR #575). See the keying note under
  "Multiple instances".
- **headend-v0.1.4 — resilience follow-ons, in flight (2026-07-08 arc review).**
  [#577](https://github.com/packet-net/packet.net/issues/577): the shipped systemd unit's
  `RestrictAddressFamilies` blocks `AF_NETLINK`, so every `.deb`-installed head-end silently loses
  mDNS advertisement (confirmed empirically) — fix allows `AF_NETLINK` + adds an mDNS assert to the
  install-smoke, shipping as `headend-v0.1.4`. Alongside it (node-side, not a head-end release):
  [#576](https://github.com/packet-net/packet.net/issues/576) — head-end-bound **radio-control
  supervision**, so the CCDI control socket reconnects like the data path does (data + radio
  channels both self-heal across a head-end bounce), reopens at the *configured* CCDI baud rather
  than the inventory's current rate, clears the latched carrier-sense on fault, and retries an
  unreachable head-end at bring-up with backoff.

## Discovery & adoption flow

The "plug into any port and go" layer (Stage 3b), end to end:

1. **Discover.** `MdnsHeadEndDiscovery` (behind `IHeadEndDiscovery`) browses `_pdnhead._tcp` once and
   reads each responder's TXT `instance=` (authoritative id, regardless of the DNS-SD label) + address
   (SRV port, or TXT `httpport=`). It is thin and total — no multicast / no responder yields an empty
   list, never a throw. The manual `HeadEndConfig.Address` always works without any mDNS hit.
2. **Resolve address (config-then-mDNS).** `IHeadEndAddressResolver` maps `instanceId → base http URI`:
   a pinned config address wins (no browse, no override); otherwise a single mDNS hit. Two advertisers
   sharing one id with **no** config address to disambiguate is a loud conflict — logged, resolved to
   `null`, never guessed (mDNS doesn't police TXT payloads). `HeadEndDeviceResolver` consults this at
   bring-up, so a discover-mode (blank-address) or re-addressed head-end still resolves — keyed by
   instance id, so a DHCP-lease change never orphans a port config.
3. **Scan / identify (`GET /api/v1/radios/headends`).** `HeadEndRadioScanner` merges config ∪ discovery,
   fetches each instance's inventory, and reaches through each **free** device's raw pipe to classify
   it: the USB VID hint picks the likely probe first (NinoTNC ≈ Microchip `04d8`; Tait CP2102 ≈ SiLabs
   `10c4`), then the other confirms — NinoTNC via `GETVER`, Tait via `MODEL`. A Tait that doesn't answer
   at the inventory clock triggers the **baud sweep**: re-clock via `POST /ports/{id}/line` and re-query
   MODEL across the standard CCDI rates until a checksummed reply — clocking and identifying in one step.
   Devices already bound to a configured port are listed (role from the binding) but **not** probed
   (single-client-per-pipe). Within each instance a TNC pairs only with a radio (co-location invariant):
   exactly one free of each is an **auto** suggestion; more than one is flagged ambiguous with the
   candidate combinations listed for manual choice.
4. **Adopt (`POST /api/v1/radios/headends/{instanceId}/adopt`).** Operator-confirmed, not silent
   auto-create: the chosen `(tncDeviceId, radioDeviceId)` becomes **one** matched port — a
   `nino-tnc-tcp` transport + a head-end-bound `tait-ccdi` radio referencing the same instance — plus a
   `HeadEndConfig` for the instance if it wasn't already declared (discover mode: blank address). It
   flows through the same `PdnPortsApi` validate→preview→apply seam a hand-edit uses, so the co-location
   pairing rule and the declared-reference rule are enforced by the existing validator.

## Identify / pair / name v2 (2026-07-05)

A follow-on enhancement makes auto-discovery read the whole modem↔radio map off the hardware — all
lib+node, riding the existing raw bridge (no head-end/Go change):

- **Band naming — the band split *is* runtime-readable.** Earlier notes assumed the band could not be
  read off the wire. **Correction:** while the *tuned frequency* is not CCDI-readable, the *band split*
  is. The Tait `q3 RADIO_VERSIONS` record `[00]` is the product code (e.g. `TMAB12-B100_0201`); the
  `[A-Z][0-9]` designator immediately after the first `-` (`B1` here) names the band split. A new
  `TaitBandCatalog` (`Packet.Radio.Tait`) maps that designator to `{ Code, MinHz, MaxHz, AmateurBand }`
  (`A4`=4m, `B1`=2m, `H5`/`H6`/`H7`=70cm; the OARC-wiki "no amateur band" on `A4` is corrected to the
  UK 70&nbsp;MHz / 4&nbsp;m allocation). `TaitRadioIdentity.Band` surfaces it. The scan
  (`HeadEndDeviceScan.BandCode` / `.AmateurBand`) carries it for a Tait, and **adopt** defaults the new
  port's MQTT `{instance}` label (`PortConfig.MqttInstance`) — and, absent an explicit id, the port id —
  to the amateur band (`2m`), so a band-named port drops in with no operator input.
- **Keyup pairing — physical ground truth.** `POST /api/v1/radios/headends/{instanceId}/pair-by-keyup`
  (**admin-scope** — it transmits, the same bar as hail/tuning/doctor — RF caveat on the response)
  resolves the *physical* modem↔radio map: for each free
  NinoTNC it opens the pipe and **briefly keys it** (a CQBEEP request frame keys the NinoTNC's cabled
  radio) while watching every free Tait's CCDI PROGRESS stream for a PTT-activated edge
  (`TaitCcdiRadio.TransmitterStateChanged`) — the Tait whose PTT the NinoTNC asserted is its physical
  pair. This replaces the co-location *guess* for the ambiguous case and verifies the unambiguous one.
  It transmits, so it is **explicitly operator-initiated** and never folded into the passive scan.
- **#567 — NinoTNC KISS baud is a fixed 57600.** A NinoTNC's KISS rate never changes; there is nothing
  to sweep. Both the reach-through identify and the `nino-tnc-tcp` bring-up now clock the head-end line
  to `57600` (`POST /ports/{id}/line`) before GETVER / open.

## Parity note

The radio-side seams (`ISerialIo`, `TaitCcdiRadio`, radio kinds) are **not** on the ax25-ts parity
surface (that check covers `Ax25ParseOptions` / `Ax25SessionQuirks` / `XidParseOptions` /
`Ax25Listener`). This arc adds no named parse flag and does not widen the listener surface, so no TS
leg is required — the carrier-sense seam it rides was already made parity-symmetric in OQ-012.
