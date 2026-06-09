# Node UI design — Phase 4 Slice 3 (REST API + auth) + Phase 5 (web UI)

> **Status:** draft / design spike (Slice 2.5). This doc is the *consumer-first* design that shapes the Slice-3 API. It is produced **before** any Slice-3 endpoint code, deliberately: the API should fall out of the interaction model, not the other way around (build endpoints blind to the screens and you get over-/under-fetching, the wrong real-time model, and payloads shaped for the data store instead of the view).
>
> It serves two audiences at once:
> 1. **The in-repo spec** — the screen inventory, the real data shapes, and the derived API contract that Slice 3 (backend) and Slice 5 (React UI) both build against. Single source of truth, no drift.
> 2. **A Claude Design brief** — §8 is a paste-ready brief for [Claude Design](https://www.anthropic.com/news/claude-design-anthropic-labs) (claude.ai → palette icon). Take the screen inventory + the data shapes into Claude Design, generate interactive mockups, iterate on look/feel/flow visually, then fold the agreed design back here and lock §7 (the API contract) before implementation.

## 1. Context — what the node is today, and the gap

`pdn` (the `packetnet` host) is a real, deployed packet-radio node: a Generic Host owning a `PortSupervisor` (one `Ax25Listener` per AX.25 port) + a transport-agnostic console, all driven by a hot-reloadable `NodeConfig` behind `IConfigProvider`. The protocol layers underneath are feature-complete — AX.25 v2.2 (mod-128/SABME/XID/segmentation), full NET/ROM L3+L4 with transit forwarding, per-flow load-balancing, and the INP3 time-routing overlay, all proven on hardware.

What's missing is the **operator-facing control plane**. Today the node is headless: you configure it by SSH-editing a YAML file and observe it through a telnet/AX.25 console (`Connect`/`Nodes`/`Info`/`Bye`/`Help`) and `journald`. The web server exists but is **inert** — the only mapped route is `GET /healthz`.

Slice 3 makes that web server live (REST API + auth); Slice 5 puts a React UI on top of it. This doc designs both from the operator's screens inward.

## 2. Design constraints

- **Stack (locked, plan §3 / §5.5):** Vite + React + TypeScript + Tailwind + shadcn/ui. **This matters for the design tool**: Claude Design outputs in exactly this space and can establish + apply a design system, so its mockups are not throwaway — they can seed the real Slice-5 components.
- **Deployment reality:** the UI is served *by the node itself* (Kestrel, bound from `management.http`, default `127.0.0.1:8080`). It runs on a small box (often a Pi) on a LAN, usually one operator, occasionally remote. So: lightweight, fast on a Pi, works over a tunnel, no heavy client deps, good on a phone (operators check their node from the sofa).
- **Auth-gated:** every screen except first-run setup + login sits behind auth (Slice 3 = Argon2id local users + WebAuthn/passkeys + JWT scopes; first-start admin bootstrap via a one-time `/setup?token=…`).
- **Real-time model: SSE, not WebSocket.** The live data (frames, session-state changes, route updates, link stats) is one-way server→client; control actions are discrete `POST`s. Server-Sent Events are simpler, reverse-proxy-friendly, and auto-reconnecting. WebSocket only if a screen needs true bidirectional streaming — none identified.
- **Config writes ride the existing reconcile path.** A config edit from the API drives the *same* `IConfigProvider.OnChange` → `ReconcilePlanner` → `PortSupervisor` delta the YAML file-watch already uses. Hot-vs-restart scope is already solved (KISS + the six AX.25 params apply live via `UpdateSessionParameters`; transport change = single-port restart; identity = node-wide reset). The UI must *surface* which edits are hot vs disruptive, but the backend behaviour is built.
- **v1 UI scope:** observe + configure + basic session control (view/initiate/drop connects, trigger a beacon). Full link-troubleshooting visualisation is Phase 8 (MCP + monitor v2) — not pulled forward.

## 3. Operator — jobs to be done

1. **Get the node running** (first run): set callsign + identity, add a port, create an admin login.
2. **Know it's healthy at a glance**: is it up, which ports are live, who's connected, is it hearing the network.
3. **Watch the air**: see frames flowing in/out, per-link health (RTT, retries, REJ/SREJ).
4. **Manage connections**: see active AX.25/NET/ROM sessions; initiate a `connect <call/alias>`; drop one.
5. **See the network**: the NET/ROM routing table — neighbours + destinations, quality, and the INP3 time-metric.
6. **Reconfigure safely**: edit ports/identity/NET/ROM/INP3 without SSH; understand what an edit will disrupt before applying.
7. **Manage access**: add/remove operators, enrol passkeys.

## 4. Screen inventory

Each screen lists **reads** (data shown — real shapes in §6), **writes** (actions), **realtime** (SSE needs), and **notes**. `→ §7.x` points at the API endpoints that fall out.

### 4.1 First-run setup (unauthenticated, one-time)
- **Purpose:** bring a fresh node from "no config / no admin" to operational. Reached via the one-time `/setup?token=…` printed to the node log on first boot.
- **Reads:** whether setup is already complete (else redirect to login); the config template defaults.
- **Writes:** station `Identity {callsign, alias?, grid?}`; create the first admin (password → Argon2id, optional passkey enrol); optionally add a first port.
- **Realtime:** none.
- **Notes:** this is an onboarding *flow* (3 short steps) as much as an API. After completion the token is burned. → §7.5 (auth/bootstrap), §7.4 (config).

### 4.2 Login
- **Purpose:** authenticate. Password (Argon2id) and/or passkey (WebAuthn). Issues a JWT.
- **Writes:** credential → token.
- **Notes:** passkey-first with password fallback. → §7.5.

### 4.3 Dashboard (home)
- **Purpose:** the at-a-glance health screen — the default landing page.
- **Reads:** node identity + uptime + build/version; per-port status (up/down, transport, peer count); active-session count; NET/ROM summary (neighbour count, destination count, INP3 on/off); recent log tail.
- **Realtime:** live counters (sessions, frames/sec, port up/down) via SSE.
- **Notes:** cards — one per concern, each linking to its detail screen. → §7.1 (status), §7.6 (events SSE).

### 4.4 Ports — list + detail/edit
- **Purpose:** see and manage AX.25 ports.
- **Reads (list):** each port's `id`, enabled, transport descriptor (`kiss-tcp`/`serial-kiss`/`nino-tnc`/`axudp` + endpoint), live state (up/down/faulted), peer/session count, profile.
- **Reads (detail):** the full `PortConfig` (transport fields, `Ax25PortParams`, `KissParams`, profile) + live link stats for that port.
- **Writes:** add/remove a port; enable/disable; edit transport + params; **bring up / restart / down** (the reconcile actions). The UI flags which fields are *hot* (KISS + AX.25 params) vs *port-restart* (transport) vs *node-reset* (identity).
- **Realtime:** port state transitions.
- **Notes:** the transport editor is a discriminated form keyed on `kind`. → §7.2 (ports), §7.4 (config).

### 4.5 Live monitor
- **Purpose:** watch frames on the air — the marquee real-time screen.
- **Reads:** a rolling stream of `MonitorEvent` (per `Ax25Listener.FrameTraced`): timestamp, port, direction (in/out), source→dest, frame type (UI/SABM/I/RR/REJ/SREJ/…), PID, length, decoded summary. Plus per-link rollups (RTT, retries, REJ/SREJ counts).
- **Writes:** filter (by port / callsign / frame type); pause/resume; clear.
- **Realtime:** **this is the SSE feed.** High-rate; client-side ring buffer + filtering.
- **Notes:** a `tcpdump`/BPQ-`MHEARD`-meets-Wireshark feel. Decoded-summary depth is a Phase-8 expansion; v1 = type + addresses + length + a one-line summary. → §7.6.

### 4.6 Sessions
- **Purpose:** see and control active connections (AX.25 L2 + NET/ROM L4 circuits).
- **Reads:** each active `SessionInfo`: port, peer callsign, role (user console / interlink / bridge), state (Connected/TimerRecovery/…), V(S)/V(R), window, uptime, bytes in/out, last-activity.
- **Writes:** **connect out** (`connect <call|alias>` — same as the console command, now from the UI); **disconnect** a session; (maybe) send a line into a session.
- **Realtime:** session add/remove/state-change.
- **Notes:** "connect out" needs the NET/ROM destination list (alias resolution) from §4.7's data. → §7.3 (sessions), §7.6 (events).

### 4.7 NET/ROM routes
- **Purpose:** the network view — what this node knows. The web analogue of the `Nodes` console command we just built (incl. the INP3 metric).
- **Reads:** `NetRomRoutingSnapshot` — `Neighbours[]` (callsign, alias, port, path-quality, last-heard) and `Destinations[]` (callsign, alias, best route + all routes: via-neighbour, quality, obsolescence, and `Inp3 {targetTimeMs, hopCount}` when present).
- **Writes:** none in v1 (read-only view); a "connect" affordance per destination hands off to §4.6.
- **Realtime:** table refresh on sweep/ingest (low rate — a periodic SSE nudge or poll is fine).
- **Notes:** show both metric spaces side by side (quality *and* INP3 time) — the dual surfacing the lab demo proved. → §7.7.

### 4.8 Config editor
- **Purpose:** edit the whole `NodeConfig` without SSH. **The biggest API-shape driver.**
- **Model (recommended):** **form-first, with a raw-YAML escape hatch.** The primary surface is typed forms (identity, services, management, NET/ROM + INP3, and ports via §4.4) driven by a schema the API serves; an "advanced" tab shows the raw YAML with live validation for power users / bulk edits. Both ride the same `IConfigProvider`/`ReconcilePlanner` path.
- **Reads:** current `NodeConfig` (+ a JSON-schema/field-metadata descriptor for the forms); the raw YAML text.
- **Writes:** validated field/section updates **or** a full raw-YAML replace; every write returns the **reconcile preview** (what will be hot-applied vs which ports restart vs node-reset) and applies atomically (validate-before-swap; a bad edit never reaches consumers).
- **Realtime:** none (but a config change emits an event others' screens react to).
- **Notes:** the validate-before-apply + reconcile-preview is the safety story — surface it prominently. → §7.4.

### 4.9 Users & access
- **Purpose:** manage operators.
- **Reads:** user list (name, role/scopes, passkeys enrolled, last login).
- **Writes:** add/remove user; reset password; enrol/revoke a passkey; assign scopes.
- **Notes:** Slice 3 minimum is single-admin; multi-user is a small extension. → §7.5.

## 5. Information architecture

```
┌── /setup?token (one-time)   /login
│
└── (authed shell: top bar = node call + status dot + uptime; left nav)
    ├── Dashboard          (4.3)   ← landing
    ├── Monitor            (4.5)   ← the live SSE screen
    ├── Sessions           (4.6)
    ├── Routes (NET/ROM)   (4.7)
    ├── Ports              (4.4)   ── Port detail/edit
    ├── Config             (4.8)   ── form tabs + raw-YAML
    └── Users              (4.9)
```

## 6. Real data shapes (ground truth)

These are the **actual** records in the codebase today (config + NET/ROM), plus the **new read models** Slice 3 must expose (the runtime data exists — `PortSupervisor` owns the listeners, `Ax25Listener` raises `FrameTraced`/`SessionAccepted` — but there's no read API yet). Claude Design should use these field names so mockups stay honest.

### 6.1 Config tree (`Packet.Node.Core.Configuration`, exists)

```
NodeConfig {
  schemaVersion: int
  identity: { callsign (req), alias?, grid? }
  ports: PortConfig[] {
    id (req), enabled=true,
    transport: oneof {
      kiss-tcp   { host, port }
      serial-kiss{ device, baud }
      nino-tnc   { device, baud, mode 0..15 }
      axudp      { host, port, localPort }     // FCS always on
    },
    profile?,                                   // e.g. "slow-afsk1200"
    ax25?: { t1Ms?, t2Ms?, t3Ms?, n2?, windowSize?, maxCachedPeers? },
    kiss?: { txDelay?, persistence?, slotTime?, txTail? }
  }
  services: { banner, prompt }                  // {node}/{call} templated
  management: { telnet:{enabled,bind,port=8011}, http:{bind,port=8080} }
  netRom: {
    enabled=true, broadcast=false, connect=false,
    forward=true, forwardMode=PerFlow,
    alias?, defaultNeighbourQuality?, minQuality?,
    obsoleteInitial?, obsoleteMinimum?, sweepIntervalSeconds?,
    window?, transportTimeoutSeconds?, transportRetries?, timeToLive?,
    inp3: NetRomInp3Options { enabled, preferInp3Routes, l3RttInterval, l3RttResetWindow, rifInterval, positiveDebounce }
  }
}
```

### 6.2 NET/ROM routing model (`Packet.NetRom.Routing`, exists)

```
NetRomRoutingSnapshot { destinations: NetRomDestination[], neighbours: NetRomNeighbour[], generatedAt }
NetRomDestination     { destination: Callsign, alias, routes: NetRomRoute[], bestRoute }
NetRomRoute           { neighbour: Callsign, quality: 0..255, obsolescence: int, inp3?: Inp3RouteMetric }
Inp3RouteMetric       { targetTimeMs: int, hopCount: byte }
NetRomNeighbour       { neighbour: Callsign, alias, portId, pathQuality: 0..255, lastHeard: timestamp }
```

### 6.3 Live-monitor source (`Ax25Listener.FrameTraced`, exists; the read model is new)

```
Ax25FrameEventArgs { frame: Ax25Frame, direction: in|out, timestamp }   // fires per frame, every port, pre-address-filter
→ MonitorEvent (NEW, derived for the API) {
    timestamp, portId, direction, source, dest, type (UI/SABM/SABME/I/RR/RNR/REJ/SREJ/FRMR/UA/DISC/DM/XID), pid?, length, summary
  }
```

### 6.4 New read models Slice 3 must add

```
NodeStatus  { callsign, alias, grid?, version, uptimeSeconds, portsUp, portsTotal, sessionCount, netrom:{neighbours,destinations,inp3Enabled} }
PortStatus  { id, enabled, transport:{kind,endpoint}, state: up|down|faulted, profile?, sessionCount, lastError? }
SessionInfo { portId, peer: Callsign, role: console|interlink|bridge, state, vs, vr, window, uptimeSeconds, bytesIn, bytesOut, lastActivity }
LinkStats   { portId, peer, smoothedRttMs, retries, rejCount, srejCount, framesIn, framesOut }   // per-link rollup for monitor/sessions
```

## 7. API contract sketch (to be locked after the Claude Design pass)

REST + JSON, JWT bearer, scoped. SSE for the live feed. Versioned under `/api/v1`. **This is a sketch** — the Claude Design mockups will confirm exact payload shapes (what each screen needs in one call); finalise as an OpenAPI document before implementation.

| # | Method + path | Purpose | Screen |
|---|---|---|---|
| 7.1 | `GET /api/v1/status` | `NodeStatus` | 4.3 |
| 7.2 | `GET /api/v1/ports` · `GET /ports/{id}` · `POST /ports` · `PATCH /ports/{id}` · `DELETE /ports/{id}` · `POST /ports/{id}:up\|down\|restart` | port CRUD + lifecycle | 4.4 |
| 7.3 | `GET /api/v1/sessions` · `POST /sessions` (connect out) · `DELETE /sessions/{id}` (disconnect) | session list + control | 4.6 |
| 7.4 | `GET /api/v1/config` · `GET /config/schema` · `PUT /config` (full) · `PATCH /config` (section) · `GET /config/raw` · `PUT /config/raw` · `POST /config:validate` (→ reconcile preview) | config read/edit, both models | 4.8 |
| 7.5 | `GET /api/v1/setup/state` · `POST /setup` · `POST /auth/login` · WebAuthn `…/register\|authenticate` · `GET\|POST\|DELETE /users…` | onboarding + auth + users | 4.1/4.2/4.9 |
| 7.6 | `GET /api/v1/events` (SSE) | multiplexed live feed: `frame`, `session`, `port`, `config`, `route` event types (client subscribes/filters) | 4.3/4.5/4.6 |
| 7.7 | `GET /api/v1/netrom/routes` | `NetRomRoutingSnapshot` | 4.7 |

**Auth scopes (sketch):** `read` (status/monitor/routes/sessions-view), `operate` (connect/disconnect, port up/down, beacon), `admin` (config write, users). JWT carries scopes; passkey or password obtains it.

**SSE design:** one `GET /api/v1/events` stream, events tagged by type so a screen subscribes to what it needs (the monitor wants `frame`; the dashboard wants `session`/`port` counters). Server coalesces high-rate `frame` events; client keeps the ring buffer.

## 8. Claude Design brief (paste-ready)

> **Project:** Web control panel for `pdn`, an amateur-radio packet node (AX.25 / NET/ROM). Served by the node itself; runs on a small Linux box on a LAN; one operator, sometimes on a phone, sometimes over a tunnel.
>
> **Stack to target:** React + TypeScript + Tailwind + **shadcn/ui**. Establish a cohesive design system (dark-first; a calm, technical, "ops dashboard meets ham-radio terminal" feel — think a modern Grafana/Tailscale-admin, not a consumer app). Output should be reusable as real shadcn components.
>
> **Build interactive mockups for these screens** (data shapes + actions below; use these exact field names):
> 1. **Dashboard** — health-at-a-glance cards: identity + uptime + version; per-port up/down; active sessions; NET/ROM summary (neighbours, destinations, INP3 on); recent log tail. Live counters.
> 2. **Live monitor** — a streaming frame table (timestamp, port, in/out arrow, source→dest, type badge [UI/SABM/I/RR/REJ/SREJ/…], PID, length, one-line summary), with filters (port/callsign/type), pause/resume, and a per-link stats strip (RTT, retries, REJ/SREJ). High-rate; design for readability under flow.
> 3. **Sessions** — table of active connections (port, peer, role, state [Connected/TimerRecovery], V(S)/V(R), window, uptime, bytes in/out); actions: Connect-out (callsign or NET/ROM alias, with autocomplete from the routes list), Disconnect.
> 4. **NET/ROM routes** — neighbours table (callsign, alias, port, quality, last-heard) + destinations table (callsign, alias, best route → via-neighbour, quality, obsolescence, **and an INP3 time column [targetTimeMs / hopCount] shown when present**). Show quality and INP3-time as two parallel metrics. A "connect" affordance per destination.
> 5. **Ports** — list (id, transport kind+endpoint, up/down/faulted, peer count, profile) + a detail/edit drawer: a transport editor that switches fields by kind (kiss-tcp host/port · serial-kiss device/baud · nino-tnc device/baud/mode · axudp host/port/localPort), optional AX.25 params (t1/t2/t3/n2/window/maxCachedPeers) and KISS params (txDelay/persistence/slotTime/txTail), and **per-field badges showing apply-impact: "live" vs "port restart" vs "node reset."** Bring-up/restart/down buttons.
> 6. **Config editor** — tabbed forms (Identity · Services · Management · NET/ROM + INP3 · Ports) **plus** a raw-YAML advanced tab with live validation; every save shows a **reconcile preview** ("X applies live, port Y restarts") and an atomic apply/cancel.
> 7. **First-run setup** — a 3-step wizard (station identity → create admin [password + optional passkey] → add first port) reached from a one-time setup link.
> 8. **Login** — passkey-first, password fallback.
>
> **Cross-cutting:** an authed app shell (top bar: node callsign + a status dot + uptime; left nav per §5); responsive down to phone; empty/loading/error states; a "what will this disrupt?" confirmation pattern for config writes.
>
> *(Optionally point Claude Design at the repo so it pulls the real `NodeConfig` / NET/ROM record fields directly.)*

## 9. Open questions for the design pass to settle

1. **Config editor** — confirm form-first-with-YAML-escape-hatch (recommended) vs one or the other. Drives whether the API serves a forms schema (`/config/schema`) or just text+validate.
2. **Monitor depth in v1** — type + addresses + length + one-line summary (recommended) vs full frame decode (defer to Phase 8).
3. **Session "send a line"** — include a minimal send-into-session affordance in v1, or view/connect/disconnect only?
4. **Aesthetic** — dark-first ops-dashboard is my lean; confirm the vibe (and any existing pdn/M0LTE visual identity to carry).
5. **Multi-user in Slice 3** — single admin only, or the full users CRUD now?

## 10. Workflow + next steps

1. ✅ This doc (the brief + spec + ground-truth shapes).
2. **Claude Design pass (you, claude.ai):** §8 → interactive mockups; iterate visually; settle §9.
3. **Reconcile back here:** fold the agreed screens into this doc; **lock §7 as an OpenAPI document**.
4. **Slice 3 build:** implement the API to the locked contract (endpoints + SSE + auth), config writes through the existing reconcile path. Update `docs/plan.md` §5.4 + §17.
5. **Slice 5 build:** the React UI, seeded by the Claude Design mockups + the same contract.
