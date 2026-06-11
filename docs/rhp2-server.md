# pdn RHPv2 server — scope, wire fidelity, and named deviations

**Status:** R-1 (codec) + R-2 (outbound) shipped 2026-06-10; R-3 (the passive half — listen/accept + multi-callsign) and R-4 (the conformance gate — live-XRouter wire diff + raw-wire suite + DAPPS acceptance) shipped 2026-06-11; **R-5 (the field-notes/RHPTEST conformance sweep — per-connection seqno, absent-field semantics, the 16/17 listener split, callsign refusal, `hello`)** shipped 2026-06-11. The app platform's **network plane** ([`app-extensibility.md`](app-extensibility.md) Slice 4): pdn hosts the Radio Host Protocol v2 (PWP-0222 / PWP-0245, JSON-over-TCP/9000) so an external application can open and accept packet connections through the node's AX.25 engine — the XRouter-compatible host API that [rhp2lib](https://rhp2lib.pages.dev/) clients (DAPPS, and ultimately Whatspac) speak.

## The honesty problem, and how this implementation handles it

The reference client (rhp2lib) and its docs are **ours**, and the only complete server (XRouter) is closed-source. rhp2lib has, to date, been validated only as far as **getting DAPPS working against XRouter** — so its model of the protocol is exactly the DAPPS-exercised subset, no more. Validating pdn's server against rhp2lib's mock alone would be marking our own homework.

Discipline adopted (mirrors the repo's strict-vs-pragmatic AX.25 rules):

1. **The spec (PWP-0222/0245) is canonical; XRouter is the de-facto wire.** Where they differ, we match **XRouter's live wire** (as pinned by rhp2lib's `RealXRouterTests` against `ghcr.io/packethacking/xrouter`) and record the delta in the table below. Since 2026-06, two further primary sources sharpen this: rhp2lib's [protocol field notes](https://rhp2lib.pages.dev/protocol-field-notes/) and **RHPTEST** (`rhptest.c` — the protocol author's own harness, Paula Dowie G8PZT, tested against XRouter **v505d**; ingested into rhp2lib-net 2026-06-11). RHPTEST documents *intent* the white papers don't; where it conflicts with the pinned live container (image label **505c**) the conflict is recorded by name below, never resolved silently.
2. **V1 scope = the DAPPS-proven subset** — the only path any client has validated end-to-end against the real server. Everything else is **deferred by name** (see Scope), not silently dropped.
3. **Oracles, in increasing independence:** (a) golden wire fixtures captured from real XRouter (replayed through our codec); (b) rhp2lib's `MockRhpServer` behavioural contract (same-family — necessary, not sufficient); (c) **DAPPS itself pointed at pdn** (`DAPPS_NODE_BEARER=rhpv2`) — the acceptance bar; (d) R-4: the `RealXRouterTests` assertion set re-pointed at pdn + a live-XRouter diff in interop CI.
4. **Genuine XRouter defects are fixed, not reproduced** (Tom's call, 2026-06-10) — each named below.

## V1 scope (R-2): the DAPPS-proven subset

- **Family/mode:** `ax25` + `stream` only. Addresses are callsign(+SSID) strings. `port` is the 1-indexed label of the node's configured ports, as a string (`"1"` = the first `ports:` entry); a null/absent port means the first port (outbound) / all ports (bind, R-3).
- **Messages:** `auth`/`authReply`, `open`(Active)/`openReply`, `send`/`sendReply`, async `recv`, `close`/`closeReply` both ways, **and the passive half (R-3): `socket`/`bind`/`listen` + async `accept`** — the full DAPPS surface, both directions — plus the **`hello`/`helloReply` capability-discovery extension (R-5; see §pdn extensions)**. A `bind` with a null `port` (or the string `"0"`) listens on all ports (exactly what DAPPS sends); `accept` pushes carry the listener handle, the new `child` handle, the caller's callsign, and the arrival port as a string, followed by the child's `status` Connected push.
- **Deferred by name:** `netrom`/`inet`/`unix` families; `dgram`/`raw`/`trace`/`seqpkt`/`semiraw`/`custom` modes; `connect` (separate-step), `sendto`, `status` queries; **passive `open`** (the `open`-message listener form, flags without the Active bit 0x80 — deterministic `errCode 16`; the BSD `socket`/`bind`/`listen` path covers every validated client's listener needs); **Busy flow control** (StatusFlags `Busy`=4 in `sendReply.status` and async `status` pushes on TX-queue saturation — no queue-depth signal exists in the engine seam yet to drive it honestly); the WebSocket transport (PWP-0245). None are needed by any validated client today.
- **Multi-callsign engine (R-3, the load-bearing piece):** `Ax25Listener` now keys its session cache by the **(local, remote) pair** and accepts inbound SABM/TEST for **registered local aliases** (`AddLocalAlias`/`RemoveLocalAlias`, refcounted) — the callsigns come **from the RHP client** (`bind` registers one; Tom: "the RHP client tells us what callsigns we should answer for"). `ConnectAsync(remote, local)` originates from an alias, so outbound `open.local` may be **any valid callsign** (the former R-2 node-callsign limitation is lifted). Inbound sessions whose `Local` is an app callsign route to the registration's handler (the `accept` push), never to the node console; the node's own callsign keeps serving the console untouched; TEST to an alias is answered *as* the alias. The listener-surface widening is parity-guarded — the reviewed exception shipped in ax25-ts first (`parity-exceptions.json`, ax25-ts#58), with a TS leg as a named follow-up. One listener per callsign (a second `listen` → `errCode 9`); registering the node's own callsign is refused (the console listens there).

## Wire fidelity — XRouter behaviours we match (the spec-vs-wire deltas)

These are places the live XRouter wire differs from the published spec; pdn matches the wire (sources: rhp2lib `protocol.md` + `RealXRouterTests` + the protocol field notes, RHPTEST, and the R-4/R-5 live wire diffs, pinned against the real image):

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
| 9 | Re-`listen` on an already-listening socket | implies an error | **idempotent `errCode 0` Ok**, no double-registration — observed live via the R-4 wire diff (which corrected pdn's initial `9` here) † |
| 10 | Reply when `id` is omitted | "the server only replies on error" | the live wire **replies to every request, success included** — the reply simply carries no `id` (R-5 probe: an id-less `socket` is answered `socketReply errCode 0`); pdn matches |
| 11 | `seqno` numbering | — | **one counter per RHP TCP connection, starting at 0**, shared across all push types — the first push after an active open is `status` `seqno:0`, the first `recv` is `seqno:1` (RHPTEST asserts both; confirmed live — a fresh connection's first push carries `"seqno":0`) |
| 12 | Absent `handle` field | — | **`errCode 12`** with errText **"Missing handle"** (not the spec table's "Bad parameter" wording, and *not* 3 — RHPTEST: "3 is for handles that are well-formed but unknown"); verified live on close/send/bind/listen/connect/status/sendto. Parameter-error replies **omit the handle echo** (nothing truthful to echo); pdn does too, though pdn still echoes the handle on `errCode 3` replies where XRouter omits it — a named shape difference no client keys on |
| 13 | Absent `data` on `send` | "data: the data to be sent" | the field is **mandatory even when empty** (RHPTEST): absent → **`errCode 12` "Missing data"**; `"data":""` is a **legal zero-byte send** (passes parameter validation — live 505c answers 17 pre-connect, i.e. it fails on state, not shape; pdn answers Ok on a connected stream) |
| 14 | `bind` port `"0"` | — | the string `"0"` is a **synonym for null/absent = "all ports"** (XRouter convention, field notes §12); pdn normalises it to the same all-ports registration DAPPS's null-port bind gets |
| 15 | Accept lifecycle | `accept` announces the child | the `accept` push is followed by a **`status` push for the child** (`handle:child`, Connected set, seqno, no id) before any `recv` — the protocol primer's incoming-listener sequence; pdn pushes it (added in R-5) |

† RHPTEST (asserted against XRouter **v505d**) says a second `listen` on the same socket returns **16** — but pdn's R-4 live wire diff against `ghcr.io/packethacking/xrouter` (image label **505c**) observed **idempotent Ok**, and pdn matches the live wire. A version skew between the author's intent and the only obtainable build; revisit if/when the container moves to 505d+ (the diff test will fail loudly).

## Named deviations — where pdn deliberately differs from XRouter

Each is a conscious product decision, not drift:

| # | XRouter behaviour | pdn behaviour | Why |
|---|---|---|---|
| D1 | A `send.data` over ~8 KB is **silently dropped** (no reply, no error — the cliff sits between **8100 and 8200 bytes**, per the field notes) | accepted up to the frame limit (chunked to the radio); errors are always replied | A silent drop is a defect (xrouter/rhp2lib-net#7). No validated client sends >4 KB, so nothing depends on the drop. |
| D2 | One bad `auth` **wedges the TCP connection**: every later request — whatever its type — is answered with a reply typed **`authReply`** carrying `errCode 14` | a bad `auth` fails that attempt only; a subsequent good `auth` succeeds | The wedge is a defect; recovery-by-reconnect is hostile to clients. Pre-auth requests still get `errCode 14` per request when auth is required — on the *matching* reply type, not `authReply`. |
| D3 | Handles are a **global namespace** usable across TCP connections | handles are numbered from one server-wide counter (so numbering *looks* alike) but are **usable only by the connection that created them**; another connection's handle gets `errCode 3` | Cross-connection handle use is an isolation hole. No client legitimately does it (DAPPS keeps each connection's handles to itself). |
| D4 | `open`(Active) replies **immediately**, before the SABM/UA handshake; the outcome arrives later as an async `status` push (failure ≈ 20–45 s later as `status flags:0` + `close`) | `openReply` is sent **after the connect resolves**: success → `errCode 0` + handle (then a `status` `Connected` push for shape-compatibility); failure → `openReply` with a real `errCode` (e.g. 15 "No Route") | Synchronous reply gives clients a true success/failure at open time. rhp2lib's `OpenAsync` just awaits the reply, so it tolerates either timing; DAPPS ignores `status` entirely. Revisited in R-4: the wire diff exercises protocol shapes only, and no validated client depends on early-reply — D4 stands. |
| D5 | A **second socket** `listen`ing on an already-claimed callsign answers **Ok** — leaving accept routing ambiguous between two listeners (found by the R-4 live wire diff) | the second socket's `listen` is refused with **`errCode 9` "Duplicate socket"** (the spec's own code); re-`listen` on the *same* socket stays idempotent Ok (row 9 above) | Two listeners for one callsign with undefined accept routing is a defect-class behaviour. Deterministic refusal is what a client can actually program against. Asserted on both sides in `XRouterRhpWireDiffTests` so a change in either surfaces loudly. |
| D6 | **Admission**: localhost/RFC1918 clients are admitted **without auth**; public clients must `auth` against XRouter's `USERPASS.SYS` | admission is **config**: `rhp.bind` defaults to loopback (the trust boundary — no implicit RFC1918 trust) and `rhp.requireAuth` validates `auth` against the node's existing Argon2id user store | The field notes themselves flag RFC1918-as-trust-boundary as unsound (CGNAT, shared LANs, containers). pdn's posture is explicit configuration, no second credential system — a deliberate, documented difference, not an oversight. |
| D7 | An **invalid callsign** (e.g. the alphabetic SSID `G9DUM-S`) is **accepted** by `bind`/`open`, a `connect` from it answers "Ok" — and the link then **wedges**: background SABM retries, no status, no error, eventual "No memory" on unrelated requests (field notes; bind-accepts verified live) | `bind`/`open` with an invalid `local` → **`errCode 6`**; an invalid `remote` → **`errCode 7`** — refused at the wire, before the engine ever sees it | AX.25 SSIDs are numeric 0–15; pdn's strict construction-path `Callsign` discipline applies at the RHP wire too. A clean deterministic refusal beats a silent wedge. Asserted on both sides in the wire diff (bind-only on the XRouter side, so the diff itself can never wedge the shared container). |
| D8 | `open` **requires `local`** — answers `errCode 6` without it (verified live). RHPTEST is explicit this is **"NOT a requirement of the RHP protocol, it's just the way XRouter works"** | an absent `local` is accepted and defaults to the **node's own callsign** | Deliberately more permissive where the protocol's author says the restriction is XRouter's, not RHP's. A null local reaches the gateway, which dials as the node — the natural default for a node-hosted API. |
| D9 | `send` on a **listening** socket answers **`errCode 17`** on the pinned live container (505c) — indistinguishable from a plain unconnected socket | a listener answers **`errCode 16` "Operation not supported"** (listeners reject everything but accept/close — RHPTEST, asserted against **v505d**); a non-listening unconnected stream handle stays **17** | RHPTEST documents the author's intent and post-dates the 505c container; the 16/17 split tells a client *why* its send failed. Asserted on both sides in the wire diff, so a container bump to 505d+ (where XRouter presumably answers 16 too) surfaces immediately. |

Anything found later that doesn't fit these tables goes **in these tables** — never silently into the code.

## pdn extensions

pdn is the first RHPv2 server to answer **capability discovery** — the `hello`/`helloReply` exchange proposed in rhp2lib's [protocol field notes](https://rhp2lib.pages.dev/protocol-field-notes/) (observation 4: "there's no way to ask the server what it supports"). A `hello` request is answered with `helloReply`, `errCode 0`, the request `id` echoed, and the capability fields from the proposal:

```json
{ "type": "helloReply", "id": 1, "errCode": 0, "errText": "Ok",
  "proto": "2", "impl": "pdn/<informational version>",
  "pfams": ["ax25"], "maxData": 65535, "enc": "latin1" }
```

- `proto` — the protocol revision pdn speaks (`"2"`; it becomes `"2.1"` if/when that spec exists).
- `impl` — `pdn/` + the build's informational version (the same stamp the node binary reports).
- `pfams` — the families actually accepted (`ax25` only in v1; grows with the scope list, never ahead of it).
- `maxData` — the codec frame cap (65535, the 2-byte length prefix); pdn has no 8 KB cliff (D1).
- `enc` — the `data` encoding in effect (`latin1`, wire-fidelity row 7).

This is **perfectly backwards-compatible by construction**: the unknown-type fallback (wire-fidelity row 5) means a server without the extension — including every current XRouter — answers `helloReply` with `errCode 2`, which a client treats as "baseline v2, no discovery". Verified against the live container in `XRouterRhpWireDiffTests` (XRouter: errCode 2; pdn: errCode 0), and pinned raw-wire in `XRouterWireConformanceTests`. Under `requireAuth`, a pre-auth `hello` is refused with `errCode 14` like any other request (the gate is uniform) — still distinguishable from "not supported".

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
- **R-5 ✅** — the field-notes/RHPTEST conformance sweep (sources: rhp2lib's [protocol field notes](https://rhp2lib.pages.dev/protocol-field-notes/) + **RHPTEST**, G8PZT's own harness, ingested 2026-06-11; every diff-verifiable item probed against the live container first). **Fixes:** per-connection `seqno` starting at 0 (row 11 — pdn had a server-wide counter starting at 1); absent `send.data` → 12 "Missing data" with `"data":""` legal (row 13 — the DTO previously defaulted to `""`, making absence invisible); absent `handle` on any handle-bearing request → 12 "Missing handle", never 3 (row 12 — nullable request DTO fields, parameter-error replies omit the handle echo); the listener 16/17 send split (D9). **Pins:** id-less requests still answered (row 10), `bind` port `"0"` = all ports (row 14), the accept→child-status lifecycle push (row 15, added), alphabetic-SSID refusal 6/7 (D7), open-without-local kept permissive (D8), admission posture documented (D6), D1/D2 refined, the RHPTEST-vs-505c re-listen footnote (†). **Extension:** `hello`/`helloReply` capability discovery (§pdn extensions) — pdn answers errCode 0 + capabilities; XRouter's errCode-2 fallback keeps it backwards-compatible. Deferred by name: passive `open` (deterministic 16), Busy flow control (no queue-depth signal yet).
- **Later** — `netrom` family over `NetRomService` circuits; dgram/UI; trace (the monitor feed already exists internally); shared codec library convergence with rhp2lib (Tom, 2026-06-10: "value in converging on a shared library in the longer run").
