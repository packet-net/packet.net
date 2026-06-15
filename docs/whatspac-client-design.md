# pdn-whatspac-client — design (ADR) + execution plan

**Status:** agreed 2026-06-13 (Tom + Claude); design record, no code yet. The *implementation* lives in its own repo — **`m0lte/pdn-whatspac-client`** — exactly as [`packet-net/dapps`](https://m0lte.github.io/dapps/) does; this document is the in-pdn design record + the rationale for why the **shipped** RHPv2 server ([`docs/rhp2-server.md`](rhp2-server.md)) is the only pdn-side surface this needs. It is *not* a plan to build anything inside `Packet.Node*`.

**TL;DR.** WhatsPac (Kevin M0AHN) is a browser SPA with no engine of its own and — its defining limitation — **no persistence**: the over-the-air session to the central WhatsPac Server (WPS, `MB7NPW-9`) lives and dies with the browser tab. We build **`pdn-whatspac-client`**: a persistent, out-of-process WhatsPac *client* (not a re-host of the SPA) that runs as a pdn app — DAPPS-shaped — holds the WPS link continuously, persists all state, and presents it through **two heads**: a fresh LAN/phone UI and a line-based RF terminal. It speaks the WPS application protocol (reverse-engineered below) end-to-end, over a transparent AX.25 stream obtained from pdn via **RHPv2** — the same decoupled app↔node boundary DAPPS already uses.

---

## 1. Context

### 1.1 What WhatsPac is, and its flaw

WhatsPac is a React/Vite SPA served from `whatspac.oarc.uk`. It contains **no packet engine**; it drives a *local* engine (XRouter or LinBPQ) over two channels — a **WebSocket carrying RHPv2** (radio traffic) and a small **REST** side-channel (engine version/state/config) — to obtain a transparent AX.25/NET-ROM byte stream, then runs its own application protocol inside that stream to reach WPS at `MB7NPW-9`. All client state lives in browser IndexedDB.

The flaw Tom wants to fix is structural, not cosmetic: **when the tab closes, the link drops and nothing keeps up with traffic while the user is away.** You cannot fix that by being a better engine under the same browser — only by moving the client into a persistent process. (Tom, on the OARC Discord 2025-01-26, wished for exactly this: "an installable service which … maintains a connection and keeps up with messages while I'm gone.")

### 1.2 This is not pdn's first app — the platform already exists

pdn's app-extensibility platform ([`docs/app-extensibility.md`](app-extensibility.md), Slices 1–5 shipped) is the substrate:

- **DAPPS** ([`packet-net/dapps`](https://m0lte.github.io/dapps/)) is the flagship **network-plane** app: an out-of-process binary that uses pdn purely through **RHPv2** (`DAPPS_NODE_BEARER=rhpv2`) to bind its own callsigns and open/accept connections, proven end-to-end against the deployed lab node (plan §17, 2026-06-11, Slice 4 R-4). It ships *with* pdn but strictly through the public package mechanism — pdn never special-cases it.
- **pdn-bpqchat** and **pdn-convers** are further apps in progress (separate repos), reinforcing the pattern: apps live out-of-tree and consume pdn's public seams.
- The local-session worked examples are **WALL** (`pdn-app/1` stdio) and **LOBBY** (long-running socket daemon with shared state).
- The human plane is the **app-gateway** ([`docs/app-gateway.md`](app-gateway.md)): a manifest + reverse-proxy + auth shell that surfaces an app's web UI under `/apps/{id}/*` with anti-spoofed `X-Pdn-User`/`X-Pdn-Scope` identity injection.

`pdn-whatspac-client` is the **first app to use both planes at once** plus a capability no shipped app has yet exercised in anger: a **persistent, app-originated outbound** connected-mode session. That outbound capability is already built — it is RHP `open(Active)` (Slice 4 R-2) — DAPPS uses the *inbound* (accept) half; this app leans on the *outbound* half.

### 1.3 The WPS application protocol is inferable

The SPA bundle (`/index.js`, ~2.2 MB, retained at `/tmp/whatspac.js` for the on-air cross-check) was analysed directly. The protocol is small, stable (Kevin: "it's stable now"), and almost entirely recoverable from the preserved string literals and the receive `switch`. §3 documents it. The remaining unknowns are a short list verifiable in one on-air session over the real RF network.

---

## 2. Decision

### 2.1 The decision

Build **`pdn-whatspac-client`** as a **separate-repo, out-of-process pdn app**, shaped like DAPPS:

1. **A persistent agent** that holds a single connected-mode AX.25 session to WPS (`MB7NPW-9`), obtained from pdn over **RHPv2** (`open(Active)` → transparent stream; pdn drives the modem). It runs WhatsPac's existing **connect-script** (`[{hop,cmd,val}]`) to reach WPS, speaks the WPS application protocol (§3), **persists** all channels/posts/DMs/users/avatars to a local SQLite store (mirroring WPS's own store choice), and keeps the link warm with the keepalive (§3.4). It reconnects and re-syncs on drop.
2. **Two heads on that one agent+store:**
   - **(a) LAN / phone head** — a fresh HTTP + SSE API and UI, surfaced over the owner's WLAN through pdn's **app-gateway** (`/apps/whatspac/*`, identity-injected). Optionally a slim native app later, hitting the same API. **Explicitly not** a re-host of the WhatsPac SPA.
   - **(b) RF terminal head** — a line-oriented view of the same store/agent, reachable from a dumb onward packet connection into the node (`C WHATSPAC`), via the **`pdn-app/1`** local-session wire (the WALL/LOBBY pattern).
3. **Single WPS user for v1.** Multi-user multiplexing is deferred to the **DAPPS federation** path (§6).
4. **Match the WPS wire as-is** (deflate + base64 + CR, §3.2). The efficient-binary improvement is a WPS-side change to co-design with Kevin later (§6).

### 2.2 Why RHP, and what this means for the shipped server

`pdn-whatspac-client` is "just another local RHP app." It speaks **RHPv2 over TCP/9000** to pdn — the *already-shipped* server (`Packet.Rhp2.Server`, Slices 4 R-1…R-5). This is the legitimate, documented role of that server: "an external application can open … packet connections through the node's AX.25 engine" ([`docs/rhp2-server.md`](rhp2-server.md)).

Consequence for the roadmap: the one piece of RHP that was **deferred by name** — the **PWP-0245 WebSocket transport** — is *not needed for this design at all*. It would only matter if we tried to back the unmodified WhatsPac **browser** (the rejected alternative, §2.3 A). Building the client instead means the WS-transport leg can stay deferred indefinitely; plan §6's "TCP + WS listeners" can drop the WS half from its critical path. (Should a future need re-appear — e.g. a third-party browser RHP client — it can be picked up then.)

### 2.3 Alternatives considered

- **A — pdn as the engine for the unmodified WhatsPac browser** (serve RHPv2 over WebSocket at `/rhp` + a REST shim, point the SPA at pdn). *Rejected.* It does not fix persistence (the flaw is in the browser-as-client model); it re-hosts someone else's UI; and it costs the deferred WS transport (PWP-0245) + a REST-shape shim + likely XRouter-console emulation for WhatsPac's `SWITCH`/`C`-script semantics. All cost, no cure.
- **B — bake WhatsPac support into `Packet.Node` core.** *Rejected.* It violates the platform's first principle — apps are out-of-process, language-agnostic, owner-trusted, and pdn "knows manifests, not app semantics" ([`docs/app-extensibility.md`](app-extensibility.md)). DAPPS/bpqchat/convers all live out-of-tree; WhatsPac is no different.
- **C — the "native engine" integration** (WhatsPac makes method calls into pdn's AX.25 engine; RHP becomes redundant — Tom's own 2025-06-18 sketch). *Deferred, not rejected.* It is the right long game but requires **WhatsPac client changes** and a three-way co-design (Tom + Kevin + John G8BPQ). The DAPPS-shaped client in this ADR is the prerequisite that gets pdn into WhatsPac now and earns the seat at that table.

### 2.4 Consequences

- **Positive:** fixes the actual user-felt flaw (persistence + store-and-forward while away); reuses the shipped RHP server and the whole app platform; adds a genuinely new capability — a **WhatsPac reachable from any dumb packet terminal**, which the browser-only product never had; positions pdn as a first-class WhatsPac client peer (the collaboration Kevin invited).
- **Neutral / no library impact:** this is an *app consuming existing seams*. It introduces **no new library surface** (`Ax25ParseOptions`/`Ax25SessionQuirks`/listener surface untouched), so it triggers **no ax25-ts parity leg** (CLAUDE.md). The only pdn-repo artefact is this doc.
- **Negative / risks:** we track a protocol we don't own and that is mid-evolution (binary framing — §6); the connect-object/version details need on-air confirmation; the RF-terminal head must degrade a rich chat UX to text. All bounded — see §5 Slice 0 and §7.

---

## 3. The WPS application protocol (reverse-engineered)

> Source: static analysis of the production SPA bundle. **Solid** items are read directly from the code; **(verify)** items are flagged for the on-air capture (§5 Slice 0).

### 3.1 Transport stack (bottom-up)

1. **KISS / AX.25** — pdn drives the modem.
2. **Transparent connected-mode byte stream** to `MB7NPW-9`. For this client, obtained via **RHP `open(Active)` / `send` / `recv`** over TCP/9000 to pdn. WhatsPac's `[{hop,cmd,val}]` connect-script runs inside it to reach WPS (the script's first hop is `{"hop":"SWITCH","cmd":"SWITCH","val":"Connected to RHP Server"}` for XRouter; the canonical final hop is `{"hop":"WhatsPac Server","cmd":"C MB7NPW-9","val":"*** Connected to WPS"}`). With pdn we can also use a **direct L2 `open(remote=MB7NPW-9, local=<mycall>, port=N)`** — the cleaner path Kevin flagged as the intended direction (verify which WPS expects).
3. **App framing** inside the stream — see §3.2.
4. **JSON objects** with a **`t` type discriminator** and short-key fields — see §3.3.

### 3.2 Framing (solid)

Each application message is:

```
payload = base64( zlib_deflate( JSON.stringify(obj) ) )
```

Confirmed from the decode function in the bundle:

```js
// decode (server -> client)
cfe = e => {
  const bytes = atob(e).split("").map(c => c.charCodeAt(0));
  return pako.inflate(new Uint8Array(bytes), { to: "string" });   // -> JSON text
};
// encode (client -> server) is the inverse: pako.deflate(JSON) -> base64   (the bundle's `ma()`)
```

**Delimiters are direction-asymmetric:**
- **client → server:** append **`"\r"` (0x0D)**. Observed at every send site, e.g. the keepalive: `msg.data = ma(JSON.stringify(obj)) + "\r"` placed in RHP `send.data`.
- **server → client:** **`0xC0`** (FEND). In the browser this arrives UTF-8-mangled as the byte pair `195,128` (`0xC3 0x80`) and the SPA copes with it.

> **Advantage for this client:** RHP delivers `recv.data` as a **Latin-1 string (one byte per code unit)** ([`docs/rhp2-server.md`](rhp2-server.md) wire-fidelity row 7), so `pdn-whatspac-client` sees a *clean* `0xC0` delimiter and clean base64 — it never suffers the browser's high-byte mangling. Accumulate `recv` bytes, split on `0xC0`, base64-decode, inflate, `JSON.parse`.

### 3.3 Message catalogue (the `t` field)

Direction: → client→WPS, ← WPS→client, ↔ both.

**Session**

| `t` | Dir | Meaning / fields |
|---|---|---|
| `c` | ↔ | client sends connect/login details after L2 up; server replies **Connect Object** `{w: new?1:0, mc: msgCount, pc: postCount, v: serverVersion}` ("Welcome to WhatsPac …" / "Welcome back …, N new messages and M new posts") |
| `k` | → | keepalive (the ~9-minute idle timer) |

**Users / presence**

| `t` | Dir | Fields |
|---|---|---|
| `u` | ← | user object(s) `{u:[...]}` → local `users` table |
| `o` | ← | online list `{o:[...]}` |
| `uc` / `ud` | ← | user connect / disconnect `{c: callsign}` |
| `ue` | ↔ | user enquiry / reply `{tc, r: found?, n: name, ls: lastSeen}` |
| `he` | ↔ | "ham" name/avatar enquiry / reply `{h:[{c, n, ts}]}` (outbound form `{t:"he", h:[callsign]}`) |
| `s` | ↔ | stats `{s: …}` |

**Direct messages (1:1)** — local id `sid = [fc,tc].sort().join("|")`

| `t` | Dir | Fields |
|---|---|---|
| `m` | ↔ | message `{fc, tc, m, ts}` (+ client-side `ms` status, `rs` read) |
| `mb` | ← | backfill batch `{md:{mc,mt}, m:[...]}` ("Downloaded X of Y new messages") |
| `med` / `medb` | ↔ / ← | edit / edit batch `{_id, m, edts}` |
| `mr` | ← | delivery receipt `{_id}` |
| `mem` / `memb` | ↔ / ← | emoji / emoji batch |

**Channels & posts** — post natural key is `ts`; local `_id = `${ts}-${fc}``

| `t` | Dir | Fields |
|---|---|---|
| `cs` | ↔ | channel subscribe/unsubscribe + response `{cid, s:1/0, pc: postCount}` |
| `pch` | ← | channel header/list `{ch:[...]}` |
| `cp` | ↔ | post `{cid, fc, p: text, ts}` |
| `cpb` | ← | post backfill batch `{cid, m:{pc,pt}, p:[...]}` ("Downloaded X of Y new posts in #…") |
| `cpe` / `cped` / `cpedb` | ↔ / ← | post edit / edit batch `{ts, cid, p, edts}` |
| `cpr` | ← | post delivery receipt `{ts, dts}` |
| `cpem` / `cpemb` | ↔ / ← | post emoji add/remove (`a:1/0`) / batch `{ts, cid, e}` |
| `cu` | → | channel create/unsubscribe (exact semantics **verify**) |

**WhatsPic (avatars / small images)**

| `t` | Dir | Fields |
|---|---|---|
| `a` | ↔ | avatar/WhatsPic `{c: callsign, a: image, ts}`; sync count via `{ac:N}` |
| `ae` | → | avatar enquiry |
| `ar` | ← | avatar response |

Image pipeline (solid): canvas **JPG resize → deflate → base64** ("Compressed WhatsPic Length …"). Chunking of larger images: **verify**.

### 3.4 Connect handshake (solid shape; fields to verify)

1. Establish the L2 session to `MB7NPW-9` (connect-script or direct `open`). UI shows "Connected to WPS … now sending your connect details".
2. Client sends a **`{t:"c", …}`** connect/login object — carries the user's callsign + name + client version, and (**verify**) likely delta-sync cursors (last-seen timestamps per channel/DM) so the server can compute backfill.
3. Server replies **`{t:"c", w, mc, pc, v}`** (new vs returning, counts, server version `v`).
4. Server streams backfill: `mb` (DMs), `cpb` per channel (posts), `u`/`o` (users/presence), `he` (names), `a`/`ar` (avatars), `s` (stats).
5. Steady state: `uc`/`ud` presence, `m`/`cp` new traffic, edits/receipts/emoji; client emits `{t:"k"}` on the idle timer.

### 3.5 Local store schema (from the SPA's Dexie tables)

`messages` (DMs, keyed `sid`+`ts`), `posts` (keyed `_id=${ts}-${fc}`, `cid`), `channels` (`cid`,`cn`), `users`, `hams` (callsign → name/avatar/ts). `pdn-whatspac-client` mirrors these in SQLite; this is the persisted state both heads render.

### 3.6 What still needs the on-air capture (verify list)

Outbound `{t:"c"}` connect-object exact fields + delta-sync cursors; the `v` version value + any negotiation; direct-`open` vs SWITCH-script preference of the live WPS; WhatsPic chunking for larger images; `cu`/`pch` specifics; and confirmation that raw-byte (Latin-1) framing round-trips cleanly without the browser's `0xC0` mangling.

---

## 4. Architecture

```
                          pdn node (packet.net)
                ┌───────────────────────────────────────┐
   RF / KISS    │  Ax25Listener / PortSupervisor        │
  ┌─────────┐   │      ▲                    ▲            │
  │ modem(s)│◄──┼──────┘                    │            │
  └─────────┘   │  RHPv2 server (TCP 9000)  │ pdn-app/1  │
                │      ▲                    │ (local     │
                │      │ open/send/recv     │  session)  │
                └──────┼────────────────────┼────────────┘
                       │                    │
        ┌──────────────┴────────────────────┴───────────────┐
        │            pdn-whatspac-client (separate repo)     │
        │                                                    │
        │   ┌────────────────────────────────────────────┐  │
        │   │  Agent: persistent WPS link                 │  │
        │   │   • RHP client  → open(MB7NPW-9)            │  │
        │   │   • connect-script runner ({hop,cmd,val})  │  │
        │   │   • WPS codec (deflate+b64+0xC0/CR, §3)     │  │
        │   │   • SQLite store (channels/posts/DMs/…)     │  │
        │   │   • keepalive + reconnect/resync            │  │
        │   └───────────────┬───────────────┬────────────┘  │
        │                   │ events/queries│                │
        │      ┌────────────┴───┐    ┌──────┴─────────────┐  │
        │      │ (a) LAN/phone  │    │ (b) RF terminal    │  │
        │      │ HTTP + SSE API │    │ pdn-app/1 line UI  │  │
        │      │ + new web UI   │    │ (C WHATSPAC)       │  │
        │      └────────┬───────┘    └────────────────────┘  │
        └───────────────┼───────────────────────────────────┘
                        │ via pdn app-gateway  /apps/whatspac/*
                   owner's WLAN  ──► browser / slim native app
```

- **Agent** is the only component that touches WPS. It is a long-running daemon (the LOBBY rung), but instead of (or as well as) accepting local sessions it **originates** the WPS link via RHP and owns the persisted state.
- **Identity:** maps the pdn user ↔ their WhatsPac callsign. pdn already injects `PDN_NODE_CALLSIGN`/`PDN_NODE_ALIAS` and the app-gateway injects `X-Pdn-User`; v1 single-user means one configured WhatsPac callsign for the agent.
- **Heads are pure renderers** over the agent's store + event stream — the same "separate logic from presentation" boundary the platform is built around. Adding a native app later is "another client of the (a) API," not a new project.

---

## 5. Execution plan (sliced)

Each slice is independently demoable. Slices 0–2 are headless and the high-value core; the heads (3–4) follow; packaging (5) mirrors DAPPS's Slice-5 path.

**Slice 0 — on-air capture + protocol confirmation.**
Goal: turn every §3.6 *(verify)* into *solid*. Run the real SPA + an engine against the live RF network; capture both the RHP `recv`/`send` frames and the decoded WPS messages of a full lifecycle (connect → backfill → post → DM → WhatsPic → keepalive → reconnect). Record golden fixtures. *Acceptance:* a documented, fixture-backed message reference; the connect-object fields and direct-`open`-vs-script question settled.

**Slice 1 — the WPS protocol library.**
Goal: a standalone codec + typed message model. Framing (deflate/base64, `0xC0`/`\r`, Latin-1-clean), the `t`-dispatch, encode/decode round-trips against the Slice-0 fixtures. *Acceptance:* every captured frame decodes to the expected object and re-encodes byte-identically (modulo deflate dictionary).

**Slice 2 — the persistent agent (headless).**
Goal: the daemon that holds the link and the store. RHP client (`open`/`send`/`recv`/`close` against pdn's 9000), connect-script runner, WPS codec wiring, SQLite persistence (§3.5), keepalive timer, reconnect + resync (delta cursors). *Acceptance:* against the lab node + real RF, the agent connects to WPS, backfills, stays up across hours and an induced link drop, and its SQLite store matches what the SPA shows for the same account.

**Slice 3 — RF terminal head.**
Goal: `C WHATSPAC` from an onward packet connection yields a line-based client over the same store (list channels, read/post, read/send DMs; WhatsPic → a placeholder/notice; emoji passthrough). Implemented on the **`pdn-app/1`** local-session wire. *Acceptance:* a station connecting to the node over RF can read a channel and post to it, and the post appears in the SPA.

**Slice 4 — LAN / phone head.**
Goal: an HTTP + SSE API over the agent + a fresh web UI, surfaced through the app-gateway (`/apps/whatspac/*`, `X-Forwarded-Prefix`, identity injection). *Acceptance:* the owner reads and posts from a phone on the WLAN with the agent's persistence behind it (messages received while the phone was asleep are present).

**Slice 5 — packaging.**
Goal: ship as a pdn app exactly like DAPPS — a `pdn-app.yaml` (authored by the app) declaring `capabilities: [network, web]` + a supervised `service:` + the `ui:` upstream + a `session:` verb for the RF terminal; pinned release artifact staged by pdn's `build-deb` if/when it becomes a bundled default. *Acceptance:* enable via the `apps:` override / Manage-apps toggle (capabilities shown at enable time); supervised lifecycle (backoff, crash-loop breaker) works; default-off.

**Slice 6 — later (out of v1 scope).**
Multi-user + multiplexing over **DAPPS federation** (§6); the **efficient-binary** WPS framing co-designed with Kevin (drop base64 now that a non-browser client controls the bytes).

---

## 6. Federation & the binary-framing future

- **Multiplexing many users** belongs on the **packet-net/dapps** federation transport (Tom + Kevin's half-plan to use DAPPS as an inter-WPS-server transport): one pipe carries many users' traffic, keeping N keepalive-bearing L2 sessions off the shared half-duplex channel. v1 is deliberately single-user so this is a clean later layer, not a retrofit.
- **Efficient binary:** the base64 hack exists only because the browser→WebSocket path mangles high bytes. `pdn-whatspac-client` speaks raw (Latin-1) AX.25 and suffers none of that, so it is the natural first client to carry **raw deflate bytes** — but only once WPS accepts a non-base64 framing (it currently expects base64 from every client). This is precisely Kevin's open 2026-06-13 question ("…any way to achieve this with RHP v2?") and is a WPS-side change to co-design, not a v1 unilateral move.

---

## 7. Open questions / risks

1. **Connect-object + version** (Slice 0 resolves). The single most load-bearing unknown for a clean first connect.
2. **Direct `open` vs SWITCH-script** — does the live WPS accept a direct L2 `open(MB7NPW-9)` or does it expect the node-console script flow? Determines how thin the agent's connect path is.
3. **Protocol churn** — WhatsPac is evolving (binary framing, federation). Mitigation: golden fixtures + a thin codec library + active collaboration with Kevin so we influence rather than chase.
4. **RF-terminal UX** — collapsing channels/emoji/images to text is a design exercise; get the channel-menu + post/read loop right first, degrade the rest.
5. **Collaboration** — WPS server source was not public as of the research window; the durable answer to several *(verify)* items is Kevin. Lead with the "persistent native client + efficient binary + RF reach" value.

---

## 8. References

- [`docs/app-extensibility.md`](app-extensibility.md) — the two-plane app platform (this app uses both).
- [`docs/app-local-session-wire.md`](app-local-session-wire.md) — `pdn-app/1` (the RF terminal head).
- [`docs/app-gateway.md`](app-gateway.md) — manifest + reverse-proxy + identity injection (the LAN head).
- [`docs/app-packages.md`](app-packages.md) — `pdn-app.yaml`, capabilities, supervised lifecycle (Slice 5).
- [`docs/rhp2-server.md`](rhp2-server.md) — the shipped RHPv2 server this app consumes (and why its WS-transport leg is unneeded here).
- [`packet-net/dapps`](https://m0lte.github.io/dapps/) — the precedent network-plane app; the shape `pdn-whatspac-client` follows.
- WhatsPac: `whatspac.oarc.uk`; BPQ RHP interface (John G8BPQ): `https://www.cantab.net/users/john.wiseman/Documents/WhatsPacInterface.html`; OARC wiki `https://wiki.oarc.uk/packet:whatspac`.
</content>
</invoke>
