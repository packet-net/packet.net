# pdn RHPv2 server — scope, wire fidelity, and named deviations

**Status:** R-1 (codec) + R-2 (server core + outbound) in progress, 2026-06-10. The app platform's **network plane** ([`app-extensibility.md`](app-extensibility.md) Slice 4): pdn hosts the Radio Host Protocol v2 (PWP-0222 / PWP-0245, JSON-over-TCP/9000) so an external application can open and accept packet connections through the node's AX.25 engine — the XRouter-compatible host API that [rhp2lib](https://rhp2lib.pages.dev/) clients (DAPPS, and ultimately Whatspac) speak.

## The honesty problem, and how this implementation handles it

The reference client (rhp2lib) and its docs are **ours**, and the only complete server (XRouter) is closed-source. rhp2lib has, to date, been validated only as far as **getting DAPPS working against XRouter** — so its model of the protocol is exactly the DAPPS-exercised subset, no more. Validating pdn's server against rhp2lib's mock alone would be marking our own homework.

Discipline adopted (mirrors the repo's strict-vs-pragmatic AX.25 rules):

1. **The spec (PWP-0222/0245) is canonical; XRouter is the de-facto wire.** Where they differ, we match **XRouter's live wire** (as pinned by rhp2lib's `RealXRouterTests` against `ghcr.io/packethacking/xrouter`) and record the delta in the table below.
2. **V1 scope = the DAPPS-proven subset** — the only path any client has validated end-to-end against the real server. Everything else is **deferred by name** (see Scope), not silently dropped.
3. **Oracles, in increasing independence:** (a) golden wire fixtures captured from real XRouter (replayed through our codec); (b) rhp2lib's `MockRhpServer` behavioural contract (same-family — necessary, not sufficient); (c) **DAPPS itself pointed at pdn** (`DAPPS_NODE_BEARER=rhpv2`) — the acceptance bar; (d) R-4: the `RealXRouterTests` assertion set re-pointed at pdn + a live-XRouter diff in interop CI.
4. **Genuine XRouter defects are fixed, not reproduced** (Tom's call, 2026-06-10) — each named below.

## V1 scope (R-2): the DAPPS-proven subset

- **Family/mode:** `ax25` + `stream` only. Addresses are callsign(+SSID) strings. `port` is the 1-indexed label of the node's configured ports, as a string (`"1"` = the first `ports:` entry); a null/absent port means the first port (outbound) / all ports (bind, R-3).
- **Messages:** `auth`/`authReply`, `open`(Active)/`openReply`, `send`/`sendReply`, async `recv`, `close`/`closeReply` both ways. R-3 adds the passive half: `socket`/`bind`/`listen` + async `accept`. Until then those reply `errCode 16` ("Operation not supported") with an explanatory `errText` — visible, not dropped.
- **Deferred by name:** `netrom`/`inet`/`unix` families; `dgram`/`raw`/`trace`/`seqpkt`/`semiraw`/`custom` modes; `connect` (separate-step), `sendto`, `status` queries; the WebSocket transport (PWP-0245). None are needed by any validated client today.
- **R-2 limitation (lifted by R-3):** outbound `open.local` must be the node's own callsign — originating a session from an arbitrary app callsign needs the same multi-callsign engine work as inbound listening (`Ax25Listener` accepts one `MyCall` today; widening it is R-3's core, and touches the ax25-ts parity-guarded listener surface). A mismatching `local` gets `errCode 6` with an `errText` saying exactly this.

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

## Named deviations — where pdn deliberately differs from XRouter

Each is a conscious product decision, not drift:

| # | XRouter behaviour | pdn behaviour | Why |
|---|---|---|---|
| D1 | A `send.data` over ~8 KB is **silently dropped** (no reply, no error) | accepted up to the frame limit (chunked to the radio); errors are always replied | A silent drop is a defect (xrouter/rhp2lib-net#7). No validated client sends >4 KB, so nothing depends on the drop. |
| D2 | One bad `auth` **wedges the TCP connection** (every later request answers `errCode 14`) | a bad `auth` fails that attempt only; a subsequent good `auth` succeeds | The wedge is a defect; recovery-by-reconnect is hostile to clients. Pre-auth requests still get `errCode 14` per request when auth is required. |
| D3 | Handles are a **global namespace** usable across TCP connections | handles are numbered from one server-wide counter (so numbering *looks* alike) but are **usable only by the connection that created them**; another connection's handle gets `errCode 3` | Cross-connection handle use is an isolation hole. No client legitimately does it (DAPPS keeps each connection's handles to itself). |
| D4 | `open`(Active) replies **immediately**, before the SABM/UA handshake; the outcome arrives later as an async `status` push (failure ≈ 20–45 s later as `status flags:0` + `close`) | `openReply` is sent **after the connect resolves**: success → `errCode 0` + handle (then a `status` `Connected` push for shape-compatibility); failure → `openReply` with a real `errCode` (e.g. 15 "No Route") | Synchronous reply gives clients a true success/failure at open time. rhp2lib's `OpenAsync` just awaits the reply, so it tolerates either timing; DAPPS ignores `status` entirely. Revisit in R-4 if the XRouter-diff shows a client that depends on early-reply. |

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

- **R-1 ✅(in flight)** — `Packet.Rhp2` codec: framing, the full message catalogue, Latin-1 data encoding; pinned by golden fixtures captured from real XRouter.
- **R-2 (this PR)** — `Packet.Rhp2.Server`: TCP host, auth, handle table, outbound `open`(Active)/`send`/`recv`/`close` over the node's existing connect-out seam. DAPPS's *forward* path.
- **R-3** — the passive half: multi-callsign acceptance in `Ax25Listener` (registration driven by the client's `bind`, per Tom: "the RHP client tells us what callsigns to answer for"), `socket`/`bind`/`listen`/`accept`, and arbitrary-`local` outbound. Lifts the D-noted R-2 limitation. Touches the parity-guarded listener surface (ax25-ts leg or recorded exception). DAPPS's *listener* path — after this, DAPPS runs against pdn.
- **R-4** — conformance: re-point rhp2lib's `RealXRouterTests` assertions at pdn; live-XRouter diff via the interop stack; **DAPPS-against-pdn** as the acceptance gate; revisit D4.
- **Later** — `netrom` family over `NetRomService` circuits; dgram/UI; trace (the monitor feed already exists internally); shared codec library convergence with rhp2lib (Tom, 2026-06-10: "value in converging on a shared library in the longer run").
