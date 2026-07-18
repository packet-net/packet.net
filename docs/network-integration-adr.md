# Existing-software network integration — design / ADR

*How **unmodified, existing software on an operator's machine** reaches the packet network
through the standard sockets idiom — and where IP is (and is **not**) a hard requirement.
This is the **client/host** direction: the OS's own software as a **consumer** of the packet
network. It is the mirror of [`app-extensibility.md`](app-extensibility.md) (apps **hosted
by** the node) and it builds on the socket-layer seam already shipped in
[`rhp2-server.md`](rhp2-server.md).*

**Status:** **accepted** (Tom, 2026-07-18) — execution to follow; nothing built yet. **Native
seam first; the TUN/IP seam is also in scope (not parked).** New work lands in a **separate
repo**, **AGPL-3.0** — with one flagged exception, the `libax25` shim (§5, Licence). See
**Decisions** below. Sizing in [`network-integration-plan.md`](network-integration-plan.md).

## Decisions (Tom, 2026-07-18)

1. **Build the native seam.** The `libax25` ABI shim is worth building — RHPv2 serves
   *new/ported* apps; the shim serves *existing, unmodified* ones. (ADR §7 Q1)
2. **Native seam first — but the TUN/IP seam is not neglected or parked.** Ship the shim
   first; deliver the TUN host stack too. **Both are v1 scope.** (Q2 + Q5)
3. **The IP seam is UI-frame datagram by default** — end-to-end TCP owns reliability, no
   connected-mode double-ARQ. Connected-mode IP stays a per-route opt-in. (Q3)
4. **Home: a separate component in a separate repo** (soft lean — not `Packet.Node`-internal);
   it consumes packet.net's engine + RHPv2 as a dependency, like the other satellites. (Q4)
5. **Licence: AGPL-3.0 for everything, unless we literally can't.** The one identified "can't"
   is the `libax25` shim (it links into third-party apps — likely **LGPL-3.0**); see §5.

---

## 0. TL;DR

- The recurring request — *"a stack I can hook in via tun/tap so all existing Linux software
  works over packet radio"* — is really about **interface + idiom parity**: the radio
  networks should be reachable the same way every other network is (WiFi, Ethernet, lo, and —
  historically — `ax0`/`nr0` in `ifconfig`): you open a **network socket**, you `connect()`,
  and existing software works unchanged because it already speaks sockets.
- **IP is not intrinsic to that.** The sockets API is transport-agnostic (`AF_UNIX`,
  `AF_AX25`, `AF_NETROM` all use `connect`/`send`/`recv`); the native AX.25/NET-ROM idiom is
  *connect-to-a-callsign*. IP is structurally forced in **exactly two places**, and nowhere
  else:
  1. a **userspace-created `ifconfig` netdevice** can only be **TUN (IP)** or **TAP
     (Ethernet)** — the kernel exposes no native-AX.25 netdev to userspace; and
  2. software that is **itself IP-only** (`curl`, `ssh`, `mosh`, `git`) has no concept of a
     callsign, so it can reach a station *only* via an IP address that routes over radio.
- So the answer is **two seams for two software worlds**, both riding the AX.25 engine's
  existing PID-demuxed L2 (`Ax25Frame.Pid`, already carried end-to-end):
  - **Native seam (primary, recommended, NO IP):** a drop-in **ABI-compatible `libax25`
    shim** backed by pdn over local IPC → every `libax25` app (`ax25d`, the node, `axcall`,
    `listen`, the BBSes) gets native connect-by-callsign, **unchanged**. It is a thin
    front-end on the seam **RHPv2 already exposes** (`OPEN`/`SEND`/`RECV`/`CLOSE`/`ACCEPT`
    for family `ax25`). No kernel, no IP.
  - **IP seam (secondary, opt-in, host-scoped):** a **TUN `pdn0`** netdev + a callsign↔IP
    resolver → genuinely IP-only apps work, auto-routed over AX.25 (and NET/ROM for
    multi-hop). This is a **host stack**, explicitly **not** the BPQ `IPGATEWAY` backbone
    router (which stays a non-goal — see §6).
- **Restoring `AF_AX25`/`nr0` literally** (for raw, non-`libax25` binaries and true `ifconfig`
  parity) needs an **out-of-tree kernel module** — the `linux_oot` lineage the spec work
  already tracks. pdn can be its L2/DSP backend, but **building/maintaining it is out of
  scope**: a community/kernel effort to *feed*, not to *own*.
- **Forcing function.** Mainline Linux **removed** `net/ax25`, `netrom`, `rose`,
  `drivers/net/hamradio` **and** the `AF_AX25`/`AF_NETROM` socket families in **Linux 7.1**
  (~2026-06; 6.18 is the last LTS that still ships them — see the sibling repo
  `f6fbb-on-kernel`). On a current-kernel box there is now **no way to put existing software
  on packet radio except a userspace stack**. This post-dates the §5.G `IPGATEWAY`-drop
  decision (2026-06-16) and is why host-side integration is worth **(re)deciding now** — even
  as the backbone-router non-goal stands.

---

## 1. What is being asked — and what it is not

The wish, stated precisely: *the packet-radio networks should be first-class to the operating
system, so the idiom to connect to a remote station is identical to connecting to anything
else, and therefore all existing software works over radio without modification.*

It is **not**:

- **Not** the BPQ **`IPGATEWAY`** feature (pdn acting as an IP↔AX.25 **backbone router** for
  the 44-net) — that is `plan.md` §5.G **DROP**, and stays dropped (§6). This ADR is about the
  operator's **own host** reaching the network, not about routing other people's IP.
- **Not** the **AGW emulator** (`AGWPORT`, §5.G DROP) or **RHPv2 for apps** — those give
  socket *semantics* over a **bespoke wire** that an application must be *written to*. They do
  not make *existing, unmodified* software work. RHPv2 is nonetheless the **engine seam this
  ADR reuses** (see §3-B).
- **Not** app *hosting* ([`app-extensibility.md`](app-extensibility.md)) — that is the
  opposite direction (the node runs apps for connecting users; here the OS's software connects
  *out*).

## 2. The load-bearing constraint

**Socket address families and network interfaces are kernel facilities.** A userspace program
(pdn) cannot register a new `AF_*` family for other processes, and cannot conjure a native
netdevice — those require kernel code. Userspace has exactly two levers:

- **TUN / TAP** (`/dev/net/tun`): the *only* way userspace can create a real, `ifconfig`-visible
  netdevice. **TUN carries IP; TAP carries Ethernet.** There is no "TUN for AX.25" — the
  kernel will not route a callsign-addressed destination to a userspace fd, because with
  `net/ax25` gone there is no AX.25 routing to hook. So TUN is *structurally* IP.
- **Nothing else** gives *unmodified* binaries a new socket family. `socket(AF_AX25, …)`
  either reaches a kernel that implements it (mainline: no longer; out-of-tree module: yes) or
  returns `EAFNOSUPPORT`. Userspace cannot make that syscall succeed for an arbitrary process.

Everything below follows from this. The **only** genuinely hard IP dependencies are the two in
§0: the `ifconfig` netdev, and IP-only applications. The native idiom itself needs neither IP
nor the kernel — because it can be delivered **inside the app's own address space**, via the
library the app already links.

## 3. Considered options

| # | Option | Idiom it gives | IP? | `ifconfig`? | Unmodified apps | Cost | Verdict |
|---|--------|----------------|-----|-------------|-----------------|------|---------|
| A | Feed the **kernel** stack (pdn-soundmodem KISS → `kissattach` → `ax0`) | full native `AF_AX25` + IP | both | yes | all | ~zero code, but **depends on removed kernel** | **Dying** — runs only on a pinned 6.18 / OOT module; a lab oracle, not a product |
| B | **`libax25` ABI shim** → pdn over local IPC | native connect-by-callsign | **no** | no | all `libax25` apps | S–M (RHPv2 seam exists) | **PRIMARY** |
| C | **TUN `pdn0`** + callsign↔IP resolver | standard `AF_INET` sockets | yes (by nature of the apps) | yes | all IP apps | M | **SECONDARY, opt-in** |
| D | **Out-of-tree kernel module** (`linux_oot`) restoring `AF_AX25`/`nr0` | full native, incl. raw binaries | either | yes | all | perpetual kernel-API maintenance, Linux-only | **EXTERNAL** — pdn as backend only |
| E | **RHPv2 / AGW** (already shipped/planned) | socket semantics, bespoke wire | no | no | only apps written to it | — | Orthogonal — the seam B/C reuse, not the answer for *existing* software |

**Why B is the primary answer.** The native ham ecosystem does not make raw syscalls — it
links **`libax25`**. A drop-in, ABI-compatible `libax25.so` whose backend is pdn over an
`AF_UNIX` socket (or the existing RHPv2 loopback listener) makes **every one of those apps**
work with native callsign addressing, connected-mode sessions, and digipeater paths — **no IP,
no kernel module**. It is a *small* piece precisely because the hard part (the socket seam)
already ships: RHPv2 is already `OPEN`/`SEND`/`RECV`/`CLOSE`/`ACCEPT` over family `ax25`
(`rhp2-server.md` R-2/R-3). The shim is `libax25 → RHPv2` translation over a stable,
decades-frozen API.

**Why C is secondary, not primary.** IP-only software (`curl`, `ssh`, `mosh`) *cannot* be
served any other way — it has no callsign concept — so a TUN seam has real, unique value. But
IP over AX.25 pays for it: extra headers on a byte-starved channel, a callsign↔IP mapping to
maintain, and the **TCP-over-ARQ double-retransmit pathology** (§5). It is worth having, opt-in,
for the IP-only long tail — not the default way to reach the network.

## 4. Decision

Accepted 2026-07-18 (Tom) — see **Decisions** at the top. In full:

1. **Adopt two seams.** Build **B (the `libax25` shim)** as the primary path for existing AX.25
   software **first**, and **C (the TUN host stack)** as a host-scoped, opt-in capability —
   **also v1 scope, not parked.** (Both ship; native leads.)
2. **Both ride the existing AX.25 engine** via its PID-demuxed L2 and the RHPv2/session seam —
   no new L2, no fork of the engine. The engine already carries the `pid` octet on
   `SendUiAsync`/`SendData` and surfaces it on `DataLinkUnitDataIndication`/`DataLinkDataIndication`,
   and the §6.6 `Segmenter` already handles L3 datagrams larger than N1.
3. **The TUN seam is a host stack, not a backbone gateway.** It carries **this node/host's**
   IP traffic. BPQ-style `IPGATEWAY` backbone routing **stays a non-goal** (§6).
4. **The out-of-tree kernel module (D) is external.** pdn will keep a **clean transport
   backend** usable by such a module (KISS/in-process), but pdn does not build or maintain
   kernel code. If the community revives `net/ax25` out-of-tree, pdn is its modem/L2.
5. **IP-over-AX.25 defaults to UI-frame datagrams** (§5), with connected-mode IP as a
   per-route opt-in.
6. **Home: a separate repo** (soft lean), consuming packet.net's engine + RHPv2 as a
   dependency — the satellite pattern, not a `Packet.Node`-internal listener.
7. **Licence: AGPL-3.0 throughout, except the `libax25` shim** (likely LGPL-3.0 — §5).

## 5. Consequences

- **Double-ARQ (the key IP design call).** Running TCP inside **connected-mode** AX.25 stacks
  two reliability layers (TCP RTO vs AX.25 T1) that fight; AX.25's local retransmit delays
  wreck TCP's RTT estimator. So the TUN seam carries IP in **UI frames (no L2 ARQ)** by
  default and lets end-to-end TCP own reliability — the best-effort-datagram behaviour IP
  expects of a link layer. Connected-mode IP stays a per-route opt-in for stable
  point-to-point links.
- **Realistic usable subset.** At 300–9600 bps half-duplex the *usable* IP apps are
  loss/latency-tolerant and small: **mosh** (the standout — UDP, built for bad links), MQTT,
  `ssh`, DNS, `syslog`, NTP, small `git`. **Not** web browsing or TLS-heavy anything. Frame it
  honestly: TUN turns the long tail of Unix IP tooling from *impossible* into *available* — not
  "the web over HF."
- **Addressing.** The native seam needs none (callsigns *are* the address). The IP seam needs
  a callsign↔IP map — static config / a `hosts`-style resolver / the 44-net (AMPRNet) space;
  dynamic AX.25-ARP (PID `0xCD`) is a later nicety. MTU is small (~256); VJ/ROHC header
  compression is a later lever, not v1.
- **Not a tri-runtime feature.** Unlike NET/ROM or the codec, this is **pdn-runtime host/OS
  integration** (TUN is Linux/macOS/*BSD/Windows; `libax25` is a Linux C library). The
  `C# → ax25-ts → pico-node` parity discipline **does not apply** — there is no browser or
  embedded "OS network stack" to reach. No `parity-exceptions.json` leg is owed.
- **Licence — AGPL-3.0 by default; one flagged exception (to ratify).** New work is
  **AGPL-3.0** to match pdn (Tom, 2026-07-18: *"all AGPL unless we literally can't"*). The
  `0xCC`/`0xCD` PIDs and the §6.6 IP-datagram motivation are in the AX.25 spec (`ax25spec`);
  the TUN device is a trivial P/Invoke (mirror pdn-soundmodem's ALSA P/Invoke). The **`libax25`
  shim is the "literally can't" case:** it is a drop-in replacement for a library **linked
  into arbitrary third-party apps** (`ax25d`, `axcall`, BBSes — some GPL-2.0-*only*), and
  upstream `libax25` is **LGPL** precisely to permit that linking. Shipping it AGPL would make
  the combined app+shim non-distributable for GPLv2-only / non-GPLv3-compatible apps and defeat
  its purpose as a true drop-in. So the shim is best **LGPL-3.0** (linking-permissive). This is
  safe alongside AGPL pdn: the **shim↔pdn boundary is a socket** (arm's-length IPC), so pdn's
  AGPL does not propagate through it — only the shim↔app linking boundary matters, and LGPL is
  the right licence for that boundary. Everything else — the TUN host stack, the core, the
  daemon — is **AGPL-3.0**.
- **Observability is half-built.** The monitor decoders already label `0xCC`→"ARPA IP",
  `0xCD`→"ARPA Address Resolution", `0x06`/`0x07`→compressed/uncompressed TCP/IP — IP traffic
  is named correctly the moment it hits the air.
- **`AF_AX25`-native limitation of B.** The shim serves apps that go through `libax25`
  (≈ the whole ham ecosystem) — **not** an arbitrary binary that hardcodes `socket(AF_AX25,…)`
  syscalls, and it produces **no `ifconfig` entry**. Those two gaps are D's remit only.

## 6. Relationship to existing decisions

- **`plan.md` §5.G — `IPGATEWAY` (IP-over-AX.25 gateway) = DROP.** *Reaffirmed.* That decision
  is about pdn being an **IP backbone router** for other stations ("GB7RDG's own AXIP port is
  commented out"). This ADR's TUN seam is a **host stack for the operator's own machine**,
  opt-in and off by default — a different scope. It does not reopen the backbone-router
  non-goal. (The distinction is the same one `plan.md`/`strict-vs-pragmatic-audit.md` already
  draw: `ax25_ip.c` = IP-over-AX.25, vs AXIP/AXUDP = the reverse.)
- **`plan.md` §5.G — AGW emulator = DROP.** Unaffected; apps use RHPv2. The `libax25` shim is a
  *different* client surface (the native `AF_AX25` API), not AGW.
- **[`rhp2-server.md`](rhp2-server.md).** The `libax25` shim and the TUN seam are new **clients
  of the same seam** RHPv2 exposes. RHPv2 currently ships family `ax25` + mode `stream`;
  `netrom`/`inet` families and `seqpkt` mode are **deferred by name** there — which bounds the
  native seam's phases (§ plan N3) and is called out, not silently assumed.
- **[`app-extensibility.md`](app-extensibility.md).** Opposite direction (hosting). Same
  "pdn is a broker over multiple families" philosophy; this ADR adds the OS as a client edge.
- **NET/ROM (`Packet.NetRom` + `NetRomService`).** The precedent for "an L3 layered on the
  AX.25 engine, riding its own PID." The IP seam is the same shape with PID `0xCC` and a TUN fd
  instead of NET/ROM routing; and the IP seam *reuses* NET/ROM for multi-hop delivery to far
  stations.

## 7. Open questions — resolved (Tom, 2026-07-18)

All five are answered in **Decisions** (top): build the native seam (Q1); native-first with the
TUN seam **also v1, not parked** (Q2, Q5); **UI-datagram** default (Q3); a **separate repo**
(Q4, soft lean). One directive added: **AGPL-3.0 unless we literally can't** — the `libax25`
shim is the identified exception (likely LGPL-3.0, §5).

**Residual detail for the plan / a follow-up decision:**

- **Repo shape.** One repo with a clearly-licensed LGPL subtree for the shim, or two repos
  (AGPL host stack + LGPL shim)? A single mixed-licence repo is workable but the split is
  cleaner — decide when the repo is created.
- **Ratify the shim's LGPL-3.0** (§5) — the one non-AGPL piece.
- **`SEQPACKET`↔`stream`** mapping for the shim (see the plan, N0).
