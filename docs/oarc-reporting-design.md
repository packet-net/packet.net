# OARC network-map reporting — design (#459)

**Status:** design approved 2026-06-18; build in progress on `feat/459-oarc-reporting`.
**Scope:** node-host only (`Packet.Node` / `Packet.Node.Core`). Zero `Packet.Ax25` change → no `ax25-ts` parity leg.

## 1. What this is

OARC runs a packet-network telemetry collector — the data behind the OARC packet-network map (`https://node-api.packet.oarc.uk/`). BPQ/XRouter nodes already feed it (link/node/circuit up-down events, periodic status, per-frame traces); the map shows who is on the air, the links between nodes, live circuits, and a frame's-eye view of traffic. This feature makes a **pdn** node a first-class reporter on that map, so a pdn station shows up alongside the BPQ/XRouter estate.

This is **outbound reporting only** for v1: the node *pushes* its telemetry. The collector also has a rich read/query surface (`/api/network/*`, `/api/history/*`) and an MCP tool surface (`/mcp`) — consuming that into a "wider network" view inside pdn is a deliberate **follow-up**, parked behind this (see §9).

There is **no privacy gate** on this being a feature — a node operator choosing to appear on the community map is normal and expected. What we *do* honour is **operator control**: the whole thing is **off until enabled**, and every category of telemetry is an independent UI toggle (§7). The most revealing/highest-volume category — per-frame L2 traces — is **off even when reporting is on**, opt-in only.

## 2. Decisions (locked 2026-06-18)

| Decision | Choice |
|---|---|
| Scope | **Outbound reporting only**. Consume side (`/network`, `/history`, `/mcp`) deferred. |
| Default-on categories (once enabled) | **Node status**, **L2 links**, **L4 circuits** — each an independent toggle. |
| Per-frame L2 traces | **Off by default**, opt-in toggle; **RF-only** filter on by default when enabled. |
| Auth | **None** — open ingest, confirmed against the live API. |
| Where it lives | Node-host **built-in** (needs link/circuit/frame internals; a sandboxed out-of-process pdn-app over RHPv2 cannot see them). |
| Transport | HTTPS POST to the **typed route endpoints** (see §4). Batch/`@type` envelope is a named, deferred optimisation. |

## 3. The collector, as validated against the live API

All of the following was **probed live on 2026-06-18** using the synthetic callsign **`Q0PDN`** (a non-allocatable `Q`-prefix, safe on the public map; the probe node was reported up, exercised across every event type, then marked down). Findings drive the implementation — they are not inferred from the (sparse, untyped) OpenAPI alone.

- **Base URL:** `https://node-api.packet.oarc.uk/` (configurable).
- **No authentication.** Ingest is open. The collector records the source IP, geo-locates it, and stores it **obfuscated** (`ipAddressObfuscated: "***.***. 146.216"`) — so the node's public IP is visible to OARC but not published in full. (Position on the map comes from the reported Maidenhead locator, not the IP.)
- **Async accept.** Every ingest endpoint returns **`202 Accepted`** with `{"status":"queued", "type":"<EventType>", "processingMode":"rabbitmq", ...}` and processes the event off a queue. So a `202` means *accepted*, not *applied* — verification is via the read-back endpoints after a short delay.
- **Server-side required-field validation.** Malformed/under-specified payloads get a `400` with a precise error (e.g. `node-down` without `nodeAlias` → *"missing required properties including: 'nodeAlias'"*). The DTOs below match the server's required set.

### 3.1 Field conventions (validated)

- **`time`** = Unix epoch **seconds** (a JSON number). Accepted and stored verbatim.
- **`direction`** = **`"outgoing"` / `"incoming"`** — the real-world value the map expects. `"out"`/`"in"` are *not* recognised by the current-link view (a probe with `"out"` ingested but never surfaced as a current link). This was the single most important wire-format correction the probe surfaced.
- **`status`** on a node record: `1` = up, `2` = down.
- **L2 traces** land in a **separate trace store** (`/api/history/traces?reportFrom=<call>`), not in `/api/history/events`. `node.l2TraceCount` updates lazily.
- The generic `/api/ingest` (single) wraps the event in a `datagram` field and discriminates with a JSON-LD **`@type`** property whose ids are `NodeUpEvent` / `NodeStatus` / `NodeDownEvent` / `LinkUpEvent` / `LinkStatus` / `LinkDownEvent` / `CircuitUpEvent` / `CircuitStatus` / `CircuitDownEvent` / `L2Trace`. `/api/ingest/batch` takes a top-level **array** of those `@type`-tagged objects (a `LinkStatus` batch item was confirmed to land). **We do not use these in v1** — the typed routes are unambiguous (type implied by route, bare event body) and were every-case verified.

### 3.2 Validation contract (from the server source, `M0LTE/node-api`)

The collector validates every datagram **asynchronously** (FluentValidation, off the RabbitMQ queue) — so a `202` is *not* a guarantee it will be applied; an invalid datagram is accepted-then-dropped. Our DTOs + mapping match the server's rules exactly, and our tests assert them:

- **Locator** (`NodeUp`/`NodeStatus`, required): `^[A-R]{2}\d{2}[A-Xa-x]{2}$` — a 6-char Maidenhead grid. The node's configured `Identity.Grid` is free-form, so the reporter **validates/normalises it and will not report node events without a valid locator** (the UI flags this). This is the one hard precondition for appearing on the map.
- **`direction`** on link/circuit events ∈ `{"incoming","outgoing"}` (case-insensitive); anything else is dropped. (`"incoming"` = remote-initiated uplink; `"outgoing"` = locally-initiated downlink.)
- **`id`** on links/circuits must be **> 0** — the reporter assigns a positive per-session serial.
- **Callsigns** (`node`/`remote`/`local`/`nodeCall`/`srce`/`dest`/`reportFrom`): `^[A-Z0-9]{1,6}(-(\d|1[0-5]))?$`.
- **L2Trace** is its own dialect: `dirn` ∈ `{"sent","rcvd"}` (**not** incoming/outgoing); `l2Type` ∈ `{SABME,C,D,DM,UA,UI,I,FRMR,RR,RNR,REJ,XID,TEST,SREJ,?}` (so `SABM`→`C`, `DISC`→`D`); `cr` ∈ `{C,R,V1}`; `modulo` ∈ `{8,128}`; `ilen` only on `I`/`UI`; `icrc` only on `I`; `info` only on `UI`; supervisory frames (`RR/RNR/REJ/SREJ`) carry **no `tseq`**. The heavy NET/ROM L3/L4 trace decode is conditional on `ptcl=="NET/ROM"` — **v1 traces report the L2 view only** and omit the NET/ROM decode, sidestepping that whole conditional block (a named refinement, §9).
- **Typed routes need no `@type`** — the route binds the concrete type and the server's `DatagramType` getter (computed from the runtime type) satisfies the `DatagramType == "<id>"` rule. `@type` is only for the generic/batch envelope.

## 4. Ingest endpoints we use (typed routes)

All `POST`, `Content-Type: application/json`, camelCase JSON, `202` on accept. `?` marks an optional field; everything else is sent.

| Endpoint | Event | Fields |
|---|---|---|
| `/api/ingest/node-up` | `NodeUpEvent` | `time, nodeCall, nodeAlias, locator, latitude?, longitude?, software, version` |
| `/api/ingest/node-status` | `NodeStatusReportEvent` | …above + `uptimeSecs, linksIn, linksOut, cctsIn, cctsOut, l3Relayed` |
| `/api/ingest/node-down` | `NodeDownEvent` | `time, nodeCall, nodeAlias (required), uptimeSecs?, reason?, linksIn?, linksOut?, cctsIn?, cctsOut?, l3Relayed?` |
| `/api/ingest/link-up` | `LinkUpEvent` | `time, node, id, direction, port, remote, local` |
| `/api/ingest/link-status` | `LinkStatus` | …above + `upForSecs?, frmsSent, frmsRcvd, frmsResent, frmsQueued, frmsQdPeak?, bytesSent?, bytesRcvd?, bpsTxMean?, bpsRxMean?, frmQMax?, l2rttMs?` |
| `/api/ingest/link-down` | `LinkDisconnectionEvent` | `time, node, id, direction, port, remote, local, upForSecs?, frmsSent, frmsRcvd, frmsResent, frmsQueued, frmsQdPeak?, bytesSent?, bytesRcvd?, reason?` |
| `/api/ingest/circuit-up` | `CircuitUpEvent` | `time, node, id, direction, service?, remote, local` |
| `/api/ingest/circuit-status` | `CircuitStatus` | …above + `segsSent, segsRcvd, segsResent, segsQueued, bytesSent?, bytesRcvd?, upForSecs?` |
| `/api/ingest/circuit-down` | `CircuitDisconnectionEvent` | …`circuit-status` fields + `reason?` |
| `/api/ingest/l2trace` | `L2Trace` | `reportFrom, time, port, dirn?, isRF?, srce, dest, ctrl, l2Type, cr, modulo?, digis?[{call,rptd}], rseq?, tseq?, pf?, pid?, ptcl?, ilen?, l3type?, l3src?, l3dst?, ttl?, l4type?, fromCct?, toCct?, txSeq?, rxSeq?, payLen?, srcUser?, srcNode?, service?, window?, …` |

**Verify / read-back endpoints** (used by live integration tests, not by the node at runtime): `GET /api/network/nodes/base/{base}`, `…/links/base/{base}`, `…/circuits/base/{base}`, `GET /api/network/nodes/reporting`, `GET /api/history/events?node={call}&sortOrder=desc`, `GET /api/history/traces?reportFrom={call}`, `GET /api/system/server-time`.

## 5. Architecture

```
                 NodeConfig.Oarc (config-in-DB)             OARC collector
                        │                                  (node-api.packet.oarc.uk)
        ┌───────────────┴───────────────┐                         ▲
        │        OarcReporter            │   typed-route POSTs     │
        │  (BackgroundService)           ├──────────  IOarcIngestClient
        │  • subscribes link/ckt events  │            (HttpClient + retry/backoff)
        │  • periodic status timer       │
        │  • maps node state → DTOs      │
        └───────────────┬───────────────┘
                        │ reads
     link table · circuit table · MH/heard · traffic-log · L3-relayed counter
```

- **`IOarcIngestClient` / `OarcIngestClient`** — the thin HTTP layer. One method per event type (`ReportNodeUpAsync`, `ReportLinkStatusAsync`, …) over a named `HttpClient`. Owns: JSON shaping (epoch time, camelCase, the `direction` enum→string), the base-URL, timeouts, and translating non-202 into a typed result. **No domain logic** — it does not know *when* to send, only *how*.
- **`OarcReporter`** — the `BackgroundService` that owns the *policy*: it reads `NodeConfig.Oarc`, subscribes to the link/circuit lifecycle events, runs the status timer (`TimeProvider`-driven), maps the node's live state into the DTOs, and applies the per-category toggles + the trace RF-only filter. Hot-reload aware (re-reads config on change; a flip of the master switch starts/stops cleanly, sending `node-up`/`node-down` at the boundary).
- **`OarcReportOptions`** (the mapped `NodeConfig.Oarc` section) — see §6.
- **Web UI** — an "OARC network map" panel of toggles, writing the `oarc:` section through the existing config GET/PUT API.

`software` is reported as **`"pdn"`** and `version` as the node version, so pdn nodes are distinguishable on the map.

## 6. Config schema (`oarc:` section of `NodeConfig`)

```yaml
oarc:
  enabled: false              # master switch — nothing is sent until true
  baseUrl: "https://node-api.packet.oarc.uk/"
  reportNodeStatus: true      # node-up / node-status / node-down
  reportLinks: true           # link-up / link-status / link-down  (L2)
  reportCircuits: true        # circuit-up / circuit-status / circuit-down  (L4)
  reportTraces: false         # l2trace firehose — opt-in
  tracesRfOnly: true          # when traces on, only isRF frames (skip internal/loopback)
  publishExactPosition: false # send lat/lon; when false, locator only
  statusIntervalSecs: 300     # node-status heartbeat cadence
  sessionStatusIntervalSecs: 60  # link/circuit *-status refresh for active sessions
```

Defaults encode the decisions: master **off**; the three aggregate categories **on** (so enabling the master "just works" for the normal case); traces **off**; position **locator-only**. All are independently togglable. The locator is taken from station identity config; `publishExactPosition` gates lat/lon.

## 7. Reporting model & cadence

- **On enable / startup** (master on): send `node-up`. On a clean stop or master-off: best-effort `node-down` (never delays shutdown).
- **Lifecycle events** (immediate, event-driven): `link-up`/`link-down`, `circuit-up`/`circuit-down`, gated by `reportLinks`/`reportCircuits`.
- **Periodic** (`TimeProvider` timer): `node-status` every `statusIntervalSecs` (default 5 min) with live link/circuit counts + L3-relayed; `link-status`/`circuit-status` for each active session every `sessionStatusIntervalSecs` (default 60 s).
- **Traces** (only if `reportTraces`): each decoded frame → `l2trace`, filtered to `isRF` when `tracesRfOnly`. This is the firehose; it is sampled/rate-limited under backpressure (§8) and never blocks the frame path.
- All outbound work is **fire-and-forget from the data path** — the reporter enqueues; a sender drains. The radio is never slowed by the collector.

## 8. Resilience (the collector is best-effort, never load-bearing)

- **Bounded in-memory queue**, **drop-oldest** on overflow — a dead/slow collector must never apply backpressure to the node or grow memory without bound. Drops are counted and logged (not silent).
- **Exponential backoff** on transport failure; **honour `429`** (rate limits exist — `/api/system/ratelimit/stats` — with no documented ceiling, so we default conservative and back off rather than hammer).
- `202` is treated as success. A `400` is logged at warning with the server's error text (a payload bug on our side) and the event dropped (not retried — it will never succeed). `5x`/network → retry with backoff.
- Everything `TimeProvider`-driven and `CancellationToken`-cooperative for clean shutdown + deterministic tests.

## 9. Out of scope for v1 (named, not silently dropped)

- **Consume side** — pulling `/api/network/*` + `/api/history/*` into a pdn "wider network map" UI, and wiring the OARC `/mcp` tools into pdn's MCP surface. This is the natural next arc once reporting is live.
- **Batch / `@type` envelope** — the typed routes are sufficient and unambiguous at our volumes (traces off by default). If trace volume later justifies it, `/api/ingest/batch` (array of `@type`-tagged datagrams) is the optimisation, now that the discriminator is mapped (§3.1).
- **`ax25-ts` parity** — node-host-only feature; no `Packet.Ax25` surface change, so no TS leg.

## 10. Build slices

1. **Ingest client** — `IOarcIngestClient` + DTOs (exact §4 shapes) + the `HttpClient` registration + unit tests (DTO JSON shape incl. epoch/`direction`, non-202 handling) against a stub handler.
2. **Config** — `NodeConfig.Oarc` record + defaults + `NodeConfigJson` round-trip + config-in-DB persistence; tests.
3. **Reporter service** — `OarcReporter` `BackgroundService`: subscribe to link/circuit events, status timer, state→DTO mapping, the toggles + trace filter, hot-reload, queue/backoff. The bulk of the logic + the bulk of the tests (deterministic, `FakeTimeProvider` + capturing logger + stub client).
4. **Web UI** — the OARC toggle panel wired to the config API.
5. **Live validation** — an opt-in (env-gated) integration test that reports `Q0PDN` to the real collector and asserts read-back; plus a lab deploy reporting the lab node, verified on the real map.

## 11. Plan ledger

Tracked under **#459**. Plan §17 amendment lands with the implementation PR(s). Standing release discipline (`docs/releasing.md`) applies for shipping; standing lab-deploy order applies once merged.
