# Handoff: pdn — node control panel (web UI)

## Overview

This is the operator-facing web control panel for **`pdn`** (the `packetnet` host), an amateur-radio
packet node implementing AX.25 v2.2 + NET/ROM (the .NET `Packet.Node` application). The node is
headless today (YAML over SSH + a telnet/AX.25 console); this UI is the control plane: **observe +
configure + basic session control**, served by the node itself from Kestrel (default `127.0.0.1:8080`),
typically on a Raspberry Pi on a LAN, one operator, often on a phone, sometimes over a tunnel.

It implements the screen inventory in the repo's `docs/node-ui-design.md` (Phase-4 Slice-3 API +
Phase-5 UI). These mockups were built to seed the real Slice-5 components and to confirm the §7 API
contract before it is locked.

## About the design files

The files in this bundle are **design references written in HTML/React-via-Babel** — runnable
prototypes that show intended look and behaviour. They are **not** production code to copy directly.
The task is to **recreate these designs in the target codebase** — the locked stack is **Vite +
React + TypeScript + Tailwind + shadcn/ui** — using its established patterns and component library.

Concretely: most of the bespoke primitives in `pdn/ui.jsx` (Button, Badge, Card, Input, Select,
Switch, Tabs, Sheet, Modal, Tooltip, Slider, Table cells) map 1:1 onto **shadcn/ui** components —
prefer the real shadcn component over re-deriving these. The value here is the **layout, the
information architecture, the copy, the interaction model, and the domain-specific composites**
(monitor decode row, reconcile/disruption confirm, profile-first port editor, link tuner, etc.).

## Fidelity

**High-fidelity.** Final colours, typography, spacing, dark+light theming, and interactions are all
intended as shown. Recreate pixel-faithfully using shadcn/ui + Tailwind tokens. Where this design
defines a token (below), map it onto the shadcn CSS-variable theme rather than hard-coding.

## Tech / framework target

- **Vite + React + TypeScript + Tailwind + shadcn/ui** (locked in the repo plan).
- **Real-time:** SSE (`GET /api/v1/events`), not WebSocket — one multiplexed stream, events tagged
  by type (`frame`, `session`, `port`, `config`, `route`); the client subscribes/filters and keeps a
  ring buffer for the high-rate `frame` feed. (In the prototype this is faked with timers.)
- **Auth:** every screen except first-run setup + login sits behind auth. Web login = Argon2id
  password + WebAuthn passkeys → JWT with `read` / `operate` / `admin` scopes. **On-air auth = TOTP**
  (separate concept — see Users).
- **Config writes** ride the node's existing reconcile path; the UI's job is to *surface* disruption
  (live vs port-restart vs node-reset) before applying. Never silently apply.

---

## Design tokens

All colours are HSL CSS variables, themed for light (`:root`) and dark (`.dark`), wired into Tailwind
as `bg-background`, `text-foreground`, `border-border`, `bg-primary`, `bg-card`, `text-muted-foreground`,
`bg-success/warning/danger`, etc. Full definitions live in the `<style>` block of
`pdn-control-panel.html`. Key values:

| Token | Light (HSL) | Dark (HSL) |
|---|---|---|
| background | `0 0% 100%` | `220 26% 7%` |
| foreground | `222 24% 12%` | `210 22% 92%` |
| card | `0 0% 100%` | `220 23% 9.5%` |
| primary (sky accent) | `200 92% 44%` | `199 89% 52%` |
| primary-foreground | `0 0% 100%` | `220 40% 8%` |
| muted | `220 14% 96%` | `220 18% 14%` |
| muted-foreground | `220 9% 46%` | `218 12% 58%` |
| border / input | `220 13% 90%` / `88%` | `220 16% 17%` / `19%` |
| ring | `200 92% 44%` | `199 89% 52%` |
| success | `152 58% 40%` | `152 56% 46%` |
| warning | `33 92% 45%` | `38 92% 52%` |
| danger | `0 72% 50%` | `0 74% 56%` |

- **Radius:** `--radius: 0.5rem` (`lg`); `md = radius-2px`, `sm = radius-4px`.
- **Type:** UI sans = **Inter** (400/500/600/700); monospace = **JetBrains Mono** (400/500/600).
  Mono is used heavily for callsigns, device paths, frame data, timestamps, config values.
- **Spacing:** Tailwind default scale. Cards `p-4`; page gutter `p-4 sm:p-6`; max content width `1400px`.
- **Status colours:** up = success, faulted = warning, down = muted-foreground, error = danger.
  Frame-type badges (monitor) are colour-coded by AX.25 class: I-frame = primary, U-frame = violet,
  S-frame = amber, UI = emerald, REJ/SREJ/FRMR = danger.
- **Type scale (representative):** page title `text-xl font-semibold`; card title `text-sm font-semibold`;
  body `text-sm`; secondary `text-xs`; table header `text-[11px] uppercase tracking-wide`. Metric
  numbers `text-2xl font-semibold` with `font-variant-numeric: tabular-nums` (`.tnum`).

---

## App shell & navigation

- **Left sidebar** (`w-60`, `bg-card`, collapses to an off-canvas drawer below `md`): logo + 7 nav
  items — Dashboard, Monitor, Sessions, Routes, Ports, Config, Users — active item gets
  `bg-primary/10 text-primary`. Footer shows the build version.
- **Top bar** (`h-14`, translucent `bg-card/60 backdrop-blur`): node callsign + a pulsing status dot
  + identity, then uptime, a **theme toggle** (light/dark, persisted only in-session), and a user menu.
- **Router:** client-side, state-driven (`route` string). Gate states: `setup` → `login` → `app`.
- Decorative entrance animation on screen change must NOT animate opacity from 0 (it can freeze the
  content invisible if the tab is backgrounded) — animate transform only. Same rule for any
  slide/entrance.

---

## Screens / views

### 1. Login (`pdn/screens-auth.jsx` → `Login`)
- Centred card on a faint grid backdrop; logo + tagline "amateur packet radio node".
- **Passkey-first**: primary "Continue with passkey" button, then an "or password" divider, then
  username + password + outline "Sign in". Theme toggle top-right.

### 2. First-run setup wizard (`screens-auth.jsx` → `Setup`)
- Reached via a one-time `/setup?token=…` link printed to the node log; token burns after use.
- 3-step stepper: **Station identity** (callsign req / alias / locator) → **Create admin** (username +
  Argon2id password + optional passkey enrol toggle) → **First port** (optional; add one port).
- Back/Skip + Continue; final step "Finish setup". *(Known gap: the port step still uses the simple
  transport form; it should adopt the profile-first flow from the Ports editor.)*

### 3. Dashboard (`pdn/screens-observe.jsx` → `Dashboard`)
- **Metric strip** (4 cards): Status (+uptime), Ports up `n/total` (+faulted count, warning-tinted),
  Active sessions (+role breakdown), Frames/sec (live, ticks via timer). Cards link to detail screens.
- Three cards: **Station** (callsign/alias/locator/version/uptime), **Ports** (per-port status rows
  with a "needs attention"/"faulted" badge + transport kind; links to Ports), **NET/ROM** (neighbour
  & destination counts, forwarding mode, INP3 on/off).
- **Recent activity**: monospace journald-style log tail, level-coloured (info/warn/error).
- Real data: `NodeStatus` (§6.4). `portsUp`, unhealthy-port highlight derived from per-link stats.

### 4. Live monitor (`screens-observe.jsx` → `Monitor`) — the marquee screen
- **Per-link stat strip** (cards): peer, port, smoothed RTT, retries, REJ/SREJ (colour-coded).
- **Filters:** callsign search, port select, frame-type select; Pause/Resume; Clear; a live/paused
  indicator + frame count.
- **Streaming frame table** (sticky header): time (ms precision), port, direction arrow (in=emerald ↓,
  out=sky ↑), `source → dest` (+ optional digi path), **frame-type badge**, PID, length, one-line
  summary. New rows flash.
- **Smooth-prepend behaviour (important):** newest rows insert at the top. When the user is at the
  top ("follow mode"), the list holds the visual frame then glides up via a **custom easeOutCubic
  rAF tween (~520ms)** — *not* native `scroll-behavior:smooth* (too steppy at this cadence). When the
  user has scrolled down to read (`scrollTop > 140`), follow mode disengages and scroll position is
  preserved by offsetting `scrollTop` by the added height. Re-baseline height on filter/expand change.
- **Click a row → full Wireshark-style decode** (`FrameDecode`): two columns — decoded AX.25 fields
  (direction, SSIDs, digi path, type, class, command/response, N(S)/N(R), P/F, PID, length) and a
  **hex+ASCII octet dump** with a copy-hex affordance.
- Real data: `MonitorEvent` (§6.3) over the SSE `frame` feed; client ring buffer + client-side filter.

### 5. Sessions (`pdn/screens-net.jsx` → `Sessions`)
- Table of active circuits: peer, port, role (console/interlink/bridge), state
  (Connected/TimerRecovery…) with status dot, V(S)/V(R), window, uptime, bytes ↓/↑, last activity,
  open/disconnect actions.
- **Connect-out** (`ConnectOut` modal): callsign/alias with **autocomplete from the routes list**, via
  port select. This is a **sysop interactive connect** — on connect it creates the session AND opens
  the session console immediately. From Routes, the port is defaulted to the best neighbour's port.
- **Session console drawer** (`SessionConsole`, right-side Sheet): V(S)/V(R), window, uptime, byte
  counters; a **scrollable session stream**; a **send-a-line** input (minimal v1 affordance that
  pushes one text line into the connected-mode session); Disconnect.
- Real data: `SessionInfo` (§6.4); SSE `session` events for add/remove/state-change.

### 6. NET/ROM routes (`screens-net.jsx` → `Routes`)
- Tabs: **Destinations** / **Neighbours**.
- **Neighbours:** callsign, alias, port, **path-quality bar** (0–255, colour by threshold), last heard,
  and a **Ping** action (AX.25 TEST over that neighbour's port).
- **Destinations:** callsign, alias, best-via neighbour (+N alt routes), quality bar, **obsolescence**,
  **INP3 time** (ms, primary-coloured when present), hops, plus **Ping** + **Connect** (sysop connect,
  port defaulted from best neighbour). Footer legend for quality thresholds.
- **Every non-obvious column header has an ⓘ tooltip** with a plain-English explanation (Quality,
  Obsolescence, INP3 time, Hops, Path quality) — see `InfoHint` usages; copy is in the headers.
- Read-only view (no edits in v1). Real data: `NetRomRoutingSnapshot` (§6.2).

### 7. Ports (`pdn/screens-manage.jsx` → `Ports` + `PortEditor`)
- **NinoTNC test-frame banner** (`NinoTestFlash`): when the modem's hardware test button fires a
  diagnostic frame, pdn decodes it and flashes firmware/mode/RSSI/CRC here, plus a prominent nudge to
  switch the modem into **software-control mode** (so pdn can own TX delay + mode). Dismissible;
  "How to enable" opens that port's editor.
- **Port cards:** status dot + id + badges (disabled / faulted / **"needs attention"** with tooltip
  reason for a degraded link), transport descriptor, transport-kind badge, a **setup summary line**
  (profile · channel · difficulty, or "Custom parameters"), session/frame counters, and actions:
  Edit, **Tune link**, Restart, Down/Bring up. Degraded/faulted cards get a tinted border.
- **Port editor** (right Sheet, `PortEditor`) — **profile-first**:
  - **Port id + Enabled** (each with ⓘ help).
  - **Connection**: type select (kiss-tcp / serial-kiss / **ninotnc** / axudp — note "ninotnc" is the
    label, kind is `nino-tnc`) with a **discriminated form per kind**. For ninotnc: serial device,
    **USB wire speed shown read-only at 57600 (fixed)**, and **modem mode as a dropdown** (named
    modes served by the node — `NINO_MODES`). KISS params only exist for KISS transports; AXUDP shows
    none.
  - **Profile**: pick a **radio profile** (fills sensible tuneables), **Channel use** (shared/dedicated)
    and **Link difficulty** (easy/moderate/marginal) as segmented controls with per-option tooltips;
    a "customised" badge appears when overridden.
  - **Advanced parameters** (collapsible): friendly-labelled tuneables with **ⓘ tooltips and unit
    suffixes** — Link timing (Ack timeout/Reply delay/Keep-alive poll/Retries/Window; protocol names
    T1/T2/T3/N2 noted only in the tooltip) and Modem keying (TX delay/TX tail/Slot time in **ms**;
    **Persistence as a 0–100% slider** stored as a 0–255 byte). **Non-default fields get a "modified"
    badge + accent border.** "Reset to profile" restores baselines.
  - **Save → disruption confirm** (`Modal`): plain-language summary — params apply *live* (no drop),
    a transport/enable change *restarts the port* (N sessions drop), renaming *resets the node*. No
    internal class names in copy.
- Real data/config: `PortConfig` + `PortStatus` (§6.1/§6.4). `PORT_SETUP`, `NINO_MODES`,
  `RADIO_PROFILES`, `PARAM_HELP`, `portHealth()` model the profile/health layer.

### 8. Config editor (`screens-manage.jsx` → `ConfigEditor`)
- **Forms** vs **Raw YAML** toggle. Left sub-nav: Identity, Services, Management, NET/ROM + INP3,
  Beacons, and a **Ports →** cross-link (ports are edited on the Ports screen).
- Edits accumulate a dirty set; **"Review & apply"** opens the **reconcile preview** (`ReconcilePreview`):
  groups changes into **apply live / restart a port / reset the node**, plain-language, atomic apply.
  (Copy must NOT leak internal types — e.g. say "checked before anything is applied", not
  `IConfigProvider → ReconcilePlanner`.)
- **Identity / Services / Management**: typed fields with per-field impact badges (live / port-restart
  / node-reset).
- **NET/ROM + INP3**: rewritten as **guidance** — an intro paragraph, four **labelled toggle rows with
  one-line descriptions** (`ToggleRow`: NET/ROM networking, Advertise my routes, Accept connects
  through me, Forward transit traffic), a Node alias field, an **"Advanced routing tuning"**
  collapsible (`AdvancedDetails` + `GuidedNum`: new-neighbour quality, min quality, sweep, hop limit,
  window — each tooltip'd, with unit suffixes), and an **INP3 section** with its own explanation,
  two toggles, and a collapsible timing-intervals group. Copy lives in `NETROM_TOGGLE_HELP`,
  `NETROM_FIELD_HELP`, `INP3_FIELD_HELP` (`pdn/data.jsx`).
- **Raw YAML**: textarea with a (mock) valid/validate affordance that also routes through the reconcile
  preview.
- Real data: `NodeConfig` (§6.1) + a field-metadata/schema descriptor the API should serve for forms.

### 9. Beacons (Config sub-tab → `BeaconsSection`)
- **System default beacon**: interval (minutes) + templated text (`{node}`/`{call}`).
- **Per-port**: enable toggle, interval, and either "uses default" (with an Override button) or a
  custom text field (with "use default"). Data: `BEACON_DEFAULT`, `PORT_BEACONS`.

### 10. Users & on-air auth (`screens-manage.jsx` → `Users`, `AuthMethod`, `TotpEnroll`)
- Single-admin in this slice; per-user **card**: avatar, name, role, scopes, last login.
- **Two auth worlds, visually separated:**
  - **Web login**: Password (Argon2id) + Passkeys (WebAuthn) rows, each an `AuthMethod` row with an
    enabled/not-set badge + action (Reset / Add passkey).
  - **On-air auth**: **Authenticator (TOTP)** — used because a station reaching the node over a plain
    packet session has no browser, just a 6-digit code. Enrol opens `TotpEnroll`: **scan** (QR
    placeholder + manual base32 key + `otpauth://` URI) → **verify** (6-digit code entry) → **done**
    (success + one-time recovery codes). Enrolled users get Re-enrol / Remove.

---

## Interactions & behaviour (cross-cutting)

- **Theme:** light/dark via a `.dark` class on `<html>`; toggle in the top bar and on auth screens.
- **Confirmation pattern:** any disruptive action (config apply, port save) shows a "what will this
  disrupt?" confirm with tone (success/warning/danger) before committing. Restart/Down/Bring-up on a
  port should adopt the same confirm (noted as a gap in the prototype).
- **Tooltips:** `Tooltip`/`InfoHint` are CSS hover/focus bubbles; map onto shadcn `Tooltip`. Used
  pervasively to explain protocol jargon at the point of use.
- **Sheets/Modals:** right-side drawers for detail/edit (Sessions console, Port editor), centred
  modals for confirms and short flows (Connect-out, AX.25 ping, TOTP). Esc closes; backdrop click closes.
- **Empty/loading/error states:** present for tables (e.g. filtered monitor, no sessions). Build real
  loading skeletons + SSE-reconnect handling in production.
- **Responsive:** desktop-first, must work down to a phone (sidebar → drawer; tables scroll; the link
  tuner stacks its chat under the workspace).

### Link tuning workspace (`pdn/screens-tools.jsx` → `LinkTuner`)
A focused full-screen tool opened from a port's **Tune link**: pick a partner + burst size, **Send
burst** of numbered frames and watch a **delivery grid** resolve to ack/lost with a delivered/loss %
summary; **tune TX delay / persistence / ack-timeout live** while testing; a **run history** to
compare parameter sets; **Auto-tune** sweeps TX delay and picks the best; and a **coordination chat**
panel to talk to the other operator. (Delivery is simulated from the params + path difficulty; in
production this drives real frame bursts and reads results.)

### AX.25 ping (`screens-tools.jsx` → `Ax25Ping`, `PingButton`)
Connectionless **AX.25 TEST** frames (the `axping` analogue): station + via-port + count → streams
`TEST seq=n reply · NNN ms` (or timeout) with min/avg/max RTT + loss. Reachable from Routes
(neighbours & destinations) and a Ports header button.

---

## State management (per the prototype; production uses the API)

- **App-level:** `gate` (`setup|login|app`), `route`, `connectTarget` ({call, portId}) for Routes→Sessions
  hand-off, `tuneTarget` (portId for the tuner overlay).
- **Per-screen:** monitor frame ring buffer + filters + paused + expanded row + follow/scroll refs;
  ports list + editor draft (profile/transport/ax25/kiss) + save-confirm; config cfg + dirty set +
  reconcile modal; users list + TOTP enroll step.
- **Production data sources** map to the §7 API: `GET /status`, `/ports`(+lifecycle), `/sessions`
  (+connect/disconnect), `/config`(+schema/raw/validate), `/netrom/routes`, `/events` (SSE),
  `/setup`+`/auth`+`/users`. Lock these as OpenAPI before implementing (per `docs/node-ui-design.md` §7/§10).

## Domain data shapes

The prototype's mock data in **`pdn/data.jsx`** deliberately uses the **real record field names** from
the codebase (`NodeConfig`, `PortConfig`, `NetRomRoutingSnapshot`, `MonitorEvent`, `SessionInfo`,
`PortStatus`, `LinkStats`) — use it as the field-name reference. The operator-facing helper models
(`RADIO_PROFILES`, `NINO_MODES`, `PORT_SETUP`, `PARAM_HELP`, `*_HELP`, `portHealth`, beacons, TOTP)
are UI-layer concepts introduced by this design — decide where they live server-side.

## Assets

No external image assets. Icons are inline single-path SVGs (Lucide-style) in `pdn/ui.jsx` (`ICON`
map) — in production use **lucide-react**. The logo is an icon tile + "pdn" wordmark (no bitmap).
Fonts: Inter + JetBrains Mono via Google Fonts.

## Files in this bundle

- `pdn-control-panel.html` — entry point: theme tokens (`:root`/`.dark`), Tailwind config, font
  imports, script load order, and the inline `App` router/mount.
- `pdn/data.jsx` — mock data + domain models + help/guidance copy + formatters.
- `pdn/ui.jsx` — primitives (→ shadcn/ui) + icons (→ lucide-react) + app shell.
- `pdn/screens-observe.jsx` — Dashboard, Monitor (+ FrameDecode, smooth-prepend).
- `pdn/screens-net.jsx` — Sessions (+ console, connect-out), Routes.
- `pdn/screens-manage.jsx` — Ports (+ editor, NinoTNC banner, save-confirm), Config (+ reconcile,
  NET/ROM guidance, Beacons), Users (+ AuthMethod, TOTP enroll).
- `pdn/screens-tools.jsx` — LinkTuner, Ax25Ping/PingButton.
- `pdn/screens-auth.jsx` — Login, Setup wizard.
- `pdn/design-canvas.jsx` — canvas scaffold used by the style-variations file (not part of the app).
- `pdn-style-variations.html` — side-by-side **style explorations** (accent family, monitor row
  treatments, theme/elevation) — reference for the chosen direction (sky accent, hairline rows,
  dark-first), not a screen to ship.

## How to run the reference

Open `pdn-control-panel.html` in a browser. It loads React + Babel from CDN and transpiles the JSX in
the browser (dev convenience only — do not ship this approach). Log in with "Continue with passkey".

## Screenshots (`screenshots/`)

Visual targets for the chosen direction (sky accent, dark-first):

| File | Screen |
|---|---|
| `01-pdn.png` | Login (passkey-first) |
| `02-pdn.png` | Dashboard |
| `03-pdn.png` | Live monitor (+ per-link stat strip) |
| `04-pdn.png` | Sessions (+ session console drawer) |
| `05-pdn.png` | NET/ROM routes (+ ping/connect, column tooltips) |
| `06-pdn.png` | Ports (+ profile-first editor drawer, NinoTNC banner) |
| `07-pdn.png` | Config → NET/ROM + INP3 (guided) |
| `08-pdn.png` | Users → TOTP on-air auth enrolment |
| `09-pdn.png` | Link tuning workspace (burst + live tweak + chat) |
