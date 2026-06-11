# pdn RHPv2 server — scope, wire fidelity, and named deviations

**Status:** R-1 (codec) + R-2 (outbound) shipped 2026-06-10; R-3 (the passive half — listen/accept + multi-callsign) and **R-4 (the conformance gate — live-XRouter wire diff + raw-wire suite + DAPPS acceptance)** shipped 2026-06-11. The app platform's **network plane** ([`app-extensibility.md`](app-extensibility.md) Slice 4): pdn hosts the Radio Host Protocol v2 (PWP-0222 / PWP-0245, JSON-over-TCP/9000) so an external application can open and accept packet connections through the node's AX.25 engine — the XRouter-compatible host API that [rhp2lib](https://rhp2lib.pages.dev/) clients (DAPPS, and ultimately Whatspac) speak.

## The honesty problem, and how this implementation handles it

The reference client (rhp2lib) and its docs are **ours**, and the only complete server (XRouter) is closed-source. rhp2lib has, to date, been validated only as far as **getting DAPPS working against XRouter** — so its model of the protocol is exactly the DAPPS-exercised subset, no more. Validating pdn's server against rhp2lib's mock alone would be marking our own homework.

Discipline adopted (mirrors the repo's strict-vs-pragmatic AX.25 rules):

1. **The spec (PWP-0222/0245) is canonical; XRouter is the de-facto wire.** Where they differ, we match **XRouter's live wire** (as pinned by rhp2lib's `RealXRouterTests` against `ghcr.io/packethacking/xrouter`) and record the delta in the table below.
2. **V1 scope = the DAPPS-proven subset** — the only path any client has validated end-to-end against the real server. Everything else is **deferred by name** (see Scope), not silently dropped.
3. **Oracles, in increasing independence:** (a) golden wire fixtures captured from real XRouter (replayed through our codec); (b) rhp2lib's `MockRhpServer` behavioural contract (same-family — necessary, not sufficient); (c) **DAPPS itself pointed at pdn** (`DAPPS_NODE_BEARER=rhpv2`) — the acceptance bar; (d) R-4: the `RealXRouterTests` assertion set re-pointed at pdn + a live-XRouter diff in interop CI.
4. **Genuine XRouter defects are fixed, not reproduced** (Tom's call, 2026-06-10) — each named below.

## V1 scope (R-2): the DAPPS-proven subset

- **Family/mode:** `ax25` + `stream` only. Addresses are callsign(+SSID) strings. `port` is the 1-indexed label of the node's configured ports, as a string (`"1"` = the first `ports:` entry); a null/absent port means the first port (outbound) / all ports (bind, R-3).
- **Messages:** `auth`/`authReply`, `open`(Active)/`openReply`, `send`/`sendReply`, async `recv`, `close`/`closeReply` both ways, **and the passive half (R-3): `socket`/`bind`/`listen` + async `accept`** — the full DAPPS surface, both directions. A `bind` with a null `port` listens on all ports (exactly what DAPPS sends); `accept` pushes carry the listener handle, the new `child` handle, the caller's callsign, and the arrival port as a string.
- **Deferred by name:** `netrom`/`inet`/`unix` families; `dgram`/`raw`/`trace`/`seqpkt`/`semiraw`/`custom` modes; `connect` (separate-step), `sendto`, `status` queries; the WebSocket transport (PWP-0245). None are needed by any validated client today.
- **Multi-callsign engine (R-3, the load-bearing piece):** `Ax25Listener` now keys its session cache by the **(local, remote) pair** and accepts inbound SABM/TEST for **registered local aliases** (`AddLocalAlias`/`RemoveLocalAlias`, refcounted) — the callsigns come **from the RHP client** (`bind` registers one; Tom: "the RHP client tells us what callsigns we should answer for"). `ConnectAsync(remote, local)` originates from an alias, so outbound `open.local` may be **any valid callsign** (the former R-2 node-callsign limitation is lifted). Inbound sessions whose `Local` is an app callsign route to the registration's handler (the `accept` push), never to the node console; the node's own callsign keeps serving the console untouched; TEST to an alias is answered *as* the alias. The listener-surface widening is parity-guarded — the reviewed exception shipped in ax25-ts first (`parity-exceptions.json`, ax25-ts#58), with a TS leg as a named follow-up. One listener per callsign (a second `listen` → `errCode 9`); registering the node's own callsign is refused (the console listens there).

## Wire fidelity — XRouter behaviours we match (the spec-vs-wire deltas)

These are places the live XRouter wire differs from the published spec; pdn matches the wire (sources: rhp2lib `protocol.md` + `RealXRouterTests`, pinned against the real image):

| # | Behaviour | Spec says | Live wire / pdn does |
|---|---|---|---|
| 1 | Error field casing | `errcode`/`errtext` (lowercase, capital-C "only on AUTHREPLY") | **`errCode`/`errText` capital on every reply**; reads accept either case |
| 2 | `connectReply` type casing | `ConnectReply` | emit **`connectReply`**; accept both on read |
| 3 | `accept.port` | unquoted number in the example | **JSON string** (`"2"`); reader normalises both |
| 4 | `recv.port` | one shape | **number in TRACE, string in DGRAM**; reader normalises both |
| 5 | Unknown `type` | "return error 2" | reply is **`{type}Reply` with `errCode:2`** (e.g. `foo` → `fooReply`) |
| 6 | `id` vs `seqno` | — | replies **echo the request `id`**; async pushes (`recv`/`accept`/server-`close`/`status`) carry **`seqno` and never an `id`** |
| 7 | `data` encoding | "control characters JSON-escaped" | bytes ↔ **Latin-1 string** (one byte per code unit), JSON escaping does the rest — not base64 |
| 8 | Extra error code | not listed | **`errCode 17` "Not connected"** for `send` on a not-yet-connected stream |
| 9 | Re-`listen` on an already-listening socket | implies an error | **idempotent `errCode 0` Ok**, no double-registration — observed live via the R-4 wire diff (which corrected pdn's initial `9` here) |

## Named deviations — where pdn deliberately differs from XRouter

Each is a conscious product decision, not drift:

| # | XRouter behaviour | pdn behaviour | Why |
|---|---|---|---|
| D1 | A `send.data` over ~8 KB is **silently dropped** (no reply, no error) | accepted up to the frame limit (chunked to the radio); errors are always replied | A silent drop is a defect (xrouter/rhp2lib-net#7). No validated client sends >4 KB, so nothing depends on the drop. |
| D2 | One bad `auth` **wedges the TCP connection** (every later request answers `errCode 14`) | a bad `auth` fails that attempt only; a subsequent good `auth` succeeds | The wedge is a defect; recovery-by-reconnect is hostile to clients. Pre-auth requests still get `errCode 14` per request when auth is required. |
| D3 | Handles are a **global namespace** usable across TCP connections | handles are numbered from one server-wide counter (so numbering *looks* alike) but are **usable only by the connection that created them**; another connection's handle gets `errCode 3` | Cross-connection handle use is an isolation hole. No client legitimately does it (DAPPS keeps each connection's handles to itself). |
| D4 | `open`(Active) replies **immediately**, before the SABM/UA handshake; the outcome arrives later as an async `status` push (failure ≈ 20–45 s later as `status flags:0` + `close`) | `openReply` is sent **after the connect resolves**: success → `errCode 0` + handle (then a `status` `Connected` push for shape-compatibility); failure → `openReply` with a real `errCode` (e.g. 15 "No Route") | Synchronous reply gives clients a true success/failure at open time. rhp2lib's `OpenAsync` just awaits the reply, so it tolerates either timing; DAPPS ignores `status` entirely. Revisited in R-4: the wire diff exercises protocol shapes only, and no validated client depends on early-reply — D4 stands. |
| D5 | A **second socket** `listen`ing on an already-claimed callsign answers **Ok** — leaving accept routing ambiguous between two listeners (found by the R-4 live wire diff) | the second socket's `listen` is refused with **`errCode 9` "Duplicate socket"** (the spec's own code); re-`listen` on the *same* socket stays idempotent Ok (row 9 above) | Two listeners for one callsign with undefined accept routing is a defect-class behaviour. Deterministic refusal is what a client can actually program against. Asserted on both sides in `XRouterRhpWireDiffTests` so a change in either surfaces loudly. |

Anything found later that doesn't fit these tables goes **in these tables** — never silently into the code.

## Configuration

```yaml
rhp:
  enabled: true        # default false — a node that doesn't opt in serves no RHP
  bind: 127.0.0.1      # loopback is the trust boundary; RHP has no TLS — never expose it publicly
  port: 9000           # the RHPv2 convention
  requireAuth: false   # when true, `auth` is validated against the node's user store before anything else
```

Hot-reload: enable/bind/port changes restart just the RHP listener (like telnet); sessions on other surfaces are untouched.

## Roadmap

- **R-1 ✅** — `Packet.Rhp2` codec: framing, the full message catalogue, Latin-1 data encoding; pinned by golden fixtures captured from real XRouter.
- **R-2 ✅** — `Packet.Rhp2.Server`: TCP host, auth, handle table, outbound `open`(Active)/`send`/`recv`/`close` over the node's existing connect-out seam. DAPPS's *forward* path.
- **R-3 ✅** — the passive half: multi-callsign acceptance in `Ax25Listener` ((local, remote) session keying + refcounted aliases + alias origination), `socket`/`bind`/`listen`/`accept` with child handles, arbitrary-`local` outbound. DAPPS's *listener* path — the full DAPPS message surface now exists. Parity exception recorded in ax25-ts first (ax25-ts#58).
- **R-4 ✅** — conformance, three legs. **(a)** `XRouterWireConformanceTests` — rhp2lib's `RealXRouterTests` assertion set re-pointed at pdn, every assertion against the raw JSON text (capital `errCode`, type-first key, id-echo vs seqno, `{type}Reply`, quoted `accept.port`, the numeric error ladder, Latin-1-not-base64). **(b)** `XRouterRhpWireDiffTests` (interop lane) — the same protocol scenarios driven against the **live XRouter** (`RHPPORT=9000` added to the fixture, host 127.0.0.1:8900) and pdn side by side; the diff earned its keep immediately: it corrected pdn's re-listen answer to the idempotent Ok (wire-fidelity row 9) and surfaced XRouter's ambiguous cross-socket duplicate (deviation D5). **(c)** **DAPPS-against-pdn acceptance** — stock DAPPS (`DAPPS_NODE_BEARER=rhpv2`) against the deployed lab node: `RHP inbound: listener bound to M0LTE-7 on handle 103`, then an independent raw-AX.25 station over the net-sim channel connected to the bound callsign, pdn answered UA, DAPPS got `ACCEPT child=104` and its `DAPPSv1>` greeting arrived at the station over RF. D4 revisited and stands (no validated client depends on early-reply).
- **Later** — `netrom` family over `NetRomService` circuits; dgram/UI; trace (the monitor feed already exists internally); shared codec library convergence with rhp2lib (Tom, 2026-06-10: "value in converging on a shared library in the longer run").
