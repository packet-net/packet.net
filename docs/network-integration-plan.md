# Existing-software network integration — implementation plan

*Companion to [`network-integration-adr.md`](network-integration-adr.md). A phased build for
the **two seams** that ADR proposes: the **native `libax25` shim** (primary) and the **TUN/IP
host stack** (secondary, opt-in). This is **host/OS integration**, not a cross-runtime protocol
feature — the work is C#/.NET on the pdn side + a small native/C shim (`libax25`) + Linux OS
glue (TUN) — so the `C# → ax25-ts → pico-node` parity discipline **does not apply** (see ADR §5).*

**Status:** ADR **accepted** (Tom, 2026-07-18) — nothing built yet; execution to follow.
**Native seam first, then the TUN/IP seam — both v1, not parked.** New work lives in a
**separate repo** (soft lean), **AGPL-3.0** except the `libax25` shim (**LGPL-3.0** — it links
into third-party apps; ADR §5). Sizing: **native seam S–M, IP/TUN seam M, kernel module: not
us**.

---

## 0. TL;DR / build order

```
Phase N  — Native seam: libax25 ABI shim → RHPv2/IPC        (PRIMARY; do first)
Phase I  — IP seam:    TUN pdn0 host stack + resolver       (SECONDARY; opt-in)
(Phase D — out-of-tree AF_AX25 kernel module)               (EXTERNAL; not planned here)
```

Decided order (Tom, 2026-07-18): **N before I** — the native seam is the bigger unlock for the
smaller lift (the RHPv2 seam already exists), and it validates the "existing software, same
idiom" thesis before spending effort on IP encapsulation. **Both are v1 scope (the TUN seam is
not parked);** they are independent, so either can ship alone once N leads.

The engine seams both phases stand on already exist and need no change to *function*:
`Ax25Listener.ConnectAsync` / `SendData(session, data, pid)` / `SendUiAsync(dest, info, pid)`;
RX via `Ax25Session.DataLinkSignalEmitted` → `DataLinkDataIndication{Info,Pid}` /
`DataLinkUnitDataIndication{Info,Pid}`; the §6.6 `Segmenter`; and any `IAx25Transport`
(in-process `SoundModemFrameTransport` or `KissTcpClient`).

---

## 1. Phase N — Native seam (`libax25` ABI shim)

The goal: an existing `libax25` binary (`call`, `axcall`, `ax25d`, `listen`, `mheard`, the
BBSes) connects to a callsign **through pdn**, unmodified, with no kernel `AF_AX25`.

- **N0 — Seam readiness (mostly done).** RHPv2 already exposes family `ax25` + `stream`,
  outbound (`OPEN` → `Ax25Listener.ConnectAsync`) and inbound (`socket`/`bind`/`listen`/`accept`,
  R-3). Confirm/close the gaps that matter for the shim:
  - **Local IPC transport.** Add an `AF_UNIX` local endpoint for the shim, or reuse the
    existing loopback RHPv2 TCP listener (:9000) — decide (Unix socket is cleaner + avoids the
    `--i-understand-agw-is-unauthenticated`-style loopback caveats; TCP is zero new transport).
  - **`SEQPACKET` vs `stream`.** `libax25` connected-mode is `SOCK_SEQPACKET` (record
    boundaries); RHPv2 v1 is `stream` (`seqpkt` deferred, `rhp2-server.md` scope). Decide
    whether to map records onto the stream (most apps treat it as a byte pipe) or land the
    deferred `seqpkt` mode. Document the choice.
- **N1 — The shim (`libax25`-ABI `.so`).** Implement the `libax25` API surface against the IPC
  client: address parsing (`ax25_aton`/`ntoa`, digipeater paths), `socket`/`bind`/`connect`/
  `send`/`recv`/`close`, and the `setsockopt` knobs (`AX25_WINDOW`, `AX25_T1`, `AX25_PACLEN`,
  …) → RHPv2 `OPEN` flags or documented no-ops. The shim's fd is the IPC socket, so `select`/
  `poll` loops in the apps work unchanged. Deployment: drop-in `libax25.so.*` **or**
  `LD_PRELOAD` (support both; the drop-in `.so` is cleaner for packaging).
  **Licence: LGPL-3.0** (ADR §5) — it links into third-party apps, so it inherits upstream
  `libax25`'s linking-permissive licence; it reaches AGPL pdn over the socket boundary only.
- **N2 — Coverage + conformance.** Run the actual tools unmodified against pdn; a conformance
  matrix vs `linux_oot` (`libax25` on the pinned 6.18 kernel — the `f6fbb-on-kernel` VM is the
  reference oracle). Loopback pdn↔pdn (two aliases via `AddLocalAlias`) as the deterministic
  self-test. Record any app that bypasses `libax25` (raw syscalls) as "D-only, out of scope."
- **N3 — NET/ROM idiom (follow-on).** Extend to the `AF_NETROM` idiom (connect by node alias)
  once the RHPv2 `netrom` family lands (deferred in R-2, `rhp2-server.md`). `NetRomService`
  circuits already exist; this is a shim + RHPv2-family increment, not new L3.

## 2. Phase I — IP seam (TUN host stack)

The goal: `ifconfig` shows `pdn0`; `ssh w1abc.ampr` / `mosh` / `ping` route over radio.

- **I0 — PID plumbing.** Add `Ax25Frame.PidIp = 0xCC` / `PidArp = 0xCD` constants (clarity
  only — `SendUiAsync`/`SendData` already take an arbitrary `pid`). Add a PID-dispatch on the
  RX path so `0xCC`/`0xCD` route to the IP seam rather than the session console.
- **I1 — TUN device.** `/dev/net/tun` via P/Invoke (`open`/`ioctl TUNSETIFF`
  `IFF_TUN|IFF_NO_PI`/`read`/`write`) — mirror pdn-soundmodem's libasound P/Invoke pattern;
  ~100 lines, no new dependency. Read raw IP packets; write received IP payloads back.
- **I2 — Encapsulation.** **UI-frame datagram IP by default** (ADR §5 — avoid TCP/AX.25
  double-ARQ): IP packet → `SendUiAsync(nextHopCall, ipBytes, pid:0xCC)`, segmented via §6.6
  when > N1. Connected-mode IP (`SendData` on a per-peer session) as a per-route opt-in.
- **I3 — Addressing.** A callsign↔IP resolver: static table / `hosts`-style file / 44-net
  (AMPRNet) allocation. Optional dynamic **AX.25-ARP** (`0xCD`) later. On RX `0xCD`, maintain
  the reverse map.
- **I4 — Routing + sizing.** Hand far-station traffic to **NET/ROM** (`NetRomService`) for
  automatic multi-hop; the TUN interface hides it. Small MTU (~256); VJ/ROHC header
  compression a later lever, explicitly not v1.
- **I5 — Host wiring.** A daemon/service in the **separate repo** (ADR decision 6), consuming
  packet.net's engine + RHPv2 as a dependency (the satellite pattern) rather than a
  `Packet.Node`-internal listener; its own config; off by default, opt-in per the ADR.
  **Licence: AGPL-3.0.** Cross-platform TUN (Wintun/utun) is a later follow-on.

## 3. Testing & interop

- **Native (Phase N):** the real `libax25` tools run unmodified against pdn; behaviour diffed
  against `linux_oot` `libax25` in the 6.18 VM (`f6fbb-on-kernel`); loopback pdn↔pdn.
- **IP (Phase I):** `ping`/`ssh`/`mosh` over an in-memory / loopback modem pair
  (`InMemoryRadio.CreatePair()`); the 6.18 kernel `ax0` + IP-over-AX.25 path as an oracle
  *inside the VM* (the one place it still exists) for byte-level cross-check.
- Monitor decoders already label `0xCC`/`0xCD`/`0x06`/`0x07`, so on-air IP is legible in the
  existing tooling with no extra work.

## 4. Risks & deferred-by-name

- **`SEQPACKET`↔`stream` record semantics** (N0) — decide + document; a mis-map corrupts
  message boundaries for the few apps that rely on them.
- **Apps bypassing `libax25`** — served only by Phase D (kernel module), out of scope; log
  which ones so the gap is explicit, not silent.
- **`LD_PRELOAD` fragility** — prefer the drop-in `.so` for packaged deployments.
- **TCP-over-AX.25 pathology** — mitigated by the UI-datagram default (ADR §5); connected-mode
  IP is opt-in with a documented warning.
- **`IPGATEWAY` backbone routing — NOT in scope.** Reaffirmed non-goal (`plan.md` §5.G).
- **Windows/macOS TUN** (Wintun/utun) — later; the `libax25` shim is Linux-only by nature.
- **The out-of-tree kernel module (Phase D)** — external/community; pdn provides the transport
  backend, nothing more.

## 5. Definition of done (per seam)

- **Native seam:** an unmodified stock `libax25` app (e.g. `axcall`) completes a connected-mode
  session to a remote callsign entirely through pdn (no kernel `AF_AX25`), proven on the
  loopback self-test **and** against a real peer; conformance matrix vs `linux_oot` recorded.
- **IP seam:** `pdn0` appears in `ifconfig`; a stock IP client (`ssh`/`mosh`) completes a
  session to a station addressed by name, over a loopback modem pair, with UI-datagram
  encapsulation; the same exchange cross-checks byte-for-byte against the 6.18 kernel path in
  the VM.
