# Split-station RF head-end over TCP + PDN autodiscovery

**Status:** design (arc kickoff 2026-07-05). Anchors the "Pi holds the modems+radios, a
separate LXC runs PDN" topology.

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
- **PDN keys device→port bindings by `(instance-id, stable-serial)`**, not by `host:port`. A Pi
  rebooting onto a new address must not orphan its port configs — PDN resolves `instance-id →
  current address` via mDNS (or the manual map) at bring-up, then dials the raw pipe.
- **Pairing is within an instance.** The discover-and-offer flow matches a TNC with a radio only
  within a single instance's inventory (honouring the co-location invariant); it never proposes a
  cross-instance pair.
- **Per-instance supervision.** Each head-end can bounce independently; PDN's per-socket
  reconnect (below) means one instance going away takes down only its own ports, not the fleet.
- **Discovery yields a set.** mDNS browse returns every advertiser; the manual fallback is a
  *list* of head-end addresses. The node UI/config lists the fleet and which devices live where.

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
  return `RadioScanResult` rows tagged with their `(instance-id, stable-serial)`.
- Port configs reference the radio/modem by `(instance-id, stable-serial)`, resolved to a current
  `host:port` at bring-up — so a re-addressed head-end doesn't orphan its ports.
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
   reach-through identify + baud sweep, discover-and-offer flow, socket supervision. Split into:
   - **3a — manual config + factories. ✅ Done** (§17 Stage 3a): `HeadEndClient` +
     `HeadEndDeviceResolver` (`Packet.Node.Core/HeadEnd/`), `NodeConfig.HeadEnds` +
     `HeadEndConfig`, the `PortRadioConfig` head-end binding (`headEndId`/`deviceId`), the
     `nino-tnc-tcp` transport kind, the lifted radio-on-networked-transport validation, the
     `RadioControlFactory` / `TransportFactory` remote branches, and per-socket
     `ReconnectingKissModem` supervision for `nino-tnc-tcp`. **Manual head-end addresses only.**
   - **3b — discovery + pairing (next):** mDNS browse of the `_pdnhead._tcp` fleet resolving
     `instanceId → address` (the `HeadEndConfig.Address` becomes an optional fallback), the remote
     `IRadioScanner`, reach-through identify + the CCDI baud **sweep** (via the wired
     `setBaud → POST /ports/{id}/line` seam), and the discover-and-offer-matched-pairs flow.
4. **Wire-up + docs + plan** — operator guide ("plug into any port and go"), plan §17.

## Parity note

The radio-side seams (`ISerialIo`, `TaitCcdiRadio`, radio kinds) are **not** on the ax25-ts parity
surface (that check covers `Ax25ParseOptions` / `Ax25SessionQuirks` / `XidParseOptions` /
`Ax25Listener`). This arc adds no named parse flag and does not widen the listener surface, so no TS
leg is required — the carrier-sense seam it rides was already made parity-symmetric in OQ-012.
