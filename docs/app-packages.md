# pdn app packages — the user-facing packaging / plugin model

**Status:** design locked 2026-06-11 (Tom: lifecycle = pdn-supervised services with an external escape hatch; bundled apps use the same mechanism as everyone else). This document is the **contract**: app authors write to it, the node implements it, and the bundled apps (WALL, LOBBY, DAPPS, the BBS) consume it with **zero special-casing** — pdn never branches on an app id.

It builds on the three existing planes ([`app-extensibility.md`](app-extensibility.md)): the packet plane ([`app-local-session-wire.md`](app-local-session-wire.md) + the RHPv2 server, [`rhp2-server.md`](rhp2-server.md)), and the human plane ([`app-gateway.md`](app-gateway.md)). What this adds is **distribution and lifecycle**: how an app gets onto a node, how the owner turns it on and off from the control panel, and who keeps its daemon alive.

## The model in one paragraph

An **app package** is a directory containing a `pdn-app.yaml` manifest **authored by the app, not the node owner**. pdn **discovers** packages by scanning well-known roots. Discovered is not enabled: a discovered package is **off until the owner enables it** (the trust grant), from the control panel or config. Enabling an app makes its console verb live, its web tile appear, and — when the manifest declares a `service` — makes **pdn itself start, supervise, and stop the daemon**. The owner's config holds only the on/off state and small overrides; everything else lives in the manifest.

## Discovery

pdn scans, in order (later roots win on id collision):

1. `/usr/share/packetnet/apps/<id>/pdn-app.yaml` — distro-installed packages (a `.deb` drops a directory here; pdn's own deb ships the bundled apps this way).
2. `/var/lib/packetnet/apps/<id>/pdn-app.yaml` — owner-installed packages (unpacked by hand or, later, uploaded through the UI).

The directory name MUST equal the manifest `id` (validated). Scanning happens at startup and on every config apply/reload — a freshly dropped package appears in the UI on the next reload, no restart. The roots are overridable for dev/tests via the top-level `appPackageRoots:` config list (when set, it replaces the defaults entirely).

Per-app state lives in `/var/lib/packetnet/apps/<id>/` (auto-created, `packetnet`-owned, 0750) — for an owner-installed package this is also the package dir; that is fine and deliberate.

## The manifest — `pdn-app.yaml`

```yaml
manifest: 1                  # schema version, required
id: lobby                    # required; must equal the directory name; [a-z0-9-]
name: LOBBY                  # human label (default: id)
version: "1.0.0"             # informational, shown in the UI
description: Live multi-user lobby — WHO presence + SAY broadcast.
icon: users                  # lucide icon name, cosmetic
capabilities: [session]      # declared, shown to the owner at enable time (not enforced in v1)

session:                     # OPTIONAL — packet-plane console attachment
  match: LOBBY               # the console verb (owner may override)
  kind: socket               # process | socket (the pdn-app/1 wire, both transports)
  command: /usr/bin/python3  # kind: process only — absolute, or relative to the package dir
  args: [lobby.py]           # relative paths resolve against the package dir
  socketPath: /run/packetnet/lobby.sock   # kind: socket only

service:                     # OPTIONAL — a long-running daemon
  command: /usr/bin/python3  # absolute, or relative to the package dir
  args: [lobby.py, --socket, /run/packetnet/lobby.sock]
  environment:               # map; merged under the owner's override map
    EXAMPLE_FLAG: "1"
  workingDirectory: null     # default: the app's state dir /var/lib/packetnet/apps/<id>
  restart: on-failure        # on-failure (default) | always | never
  managed: pdn               # pdn (default) | external — see Lifecycle

ui:                          # OPTIONAL — human plane (the existing app-gateway contract)
  upstream: http://127.0.0.1:9090
  name: LOBBY
  icon: users
  mode: standalone           # standalone (default) | embedded | slot — see § UI surface modes

forward:                     # OPTIONAL — tailnet port forwarding (see below)
  - listen: 993              # the tailnet-facing port on the node's tsnet node
    target: 127.0.0.1:1430   # the app's plaintext loopback listener
    tls: terminate           # terminate (default) | raw — see the rules below
```

At least one of `session` / `service` / `ui` must be present. Examples of the shapes:

- **WALL**: `session` (kind process, wall.py) + `service` (wall_web.py, its web view) + `ui`.
- **LOBBY**: `session` (kind socket) + `service` (the daemon pdn now keeps alive — no more hand-run systemd units).
- **DAPPS**: `service` (the dotnet daemon, environment selecting the RHPv2 bearer) + `ui` + `capabilities: [packet, web]` (the `network` → `packet` rename; see § The `packet` capability). No `session` — it binds its own callsigns over RHP.
- **The BBS**: `service` + `ui`, like DAPPS.

Path resolution: a relative `command`/`args` element that names an existing file in the package dir resolves to it; everything else passes through untouched. `workingDirectory` defaults to the state dir.

Environment given to a supervised service: the node's own environment, plus `PDN_APP_ID`, `PDN_APP_DIR` (the package dir), `PDN_APP_STATE` (the state dir), `PDN_NODE_CALLSIGN` (the node's own callsign, so an app can derive its identity per the an-app-lives-at-an-SSID-of-the-node-callsign convention) and `PDN_NODE_ALIAS` when set, and — when the RHP server is enabled — `PDN_RHP_HOST`/`PDN_RHP_PORT`, then the manifest `environment` map, then the owner's override map (last wins).

### `ui.mode` — UI surface modes (how the panel opens the app)

The `ui:` block may carry an optional **`mode`** that tells the control panel how to open the app from its left-nav entry. It is an enum with a **safe default** — an unknown or missing value binds to `standalone` (an app authored against a future mode set still loads on an older node; this is the one manifest enum that is forgiving rather than an error). The three modes:

- **`standalone`** (the default) — the nav entry is a **full browser navigation** to the app's own page at `/apps/<id>/` (the same-origin path pdn reverse-proxies). This is the historical behaviour: a plain `<a href>`, the app owns the whole tab. Use it for an app that wants its own full-page UI (its own header, its own navigation).
- **`embedded`** — the nav entry is an **in-panel SPA route** (`/apps/<id>`) that renders the panel shell (the left nav + a header showing the app's name) around a **borderless, content-area-filling `<iframe>`** whose `src` is the app's page (`/apps/<id>/`). The app renders **its own full page** inside the frame — no signal param is appended. Use it to keep the operator inside the panel chrome while the app still draws its own complete UI.
- **`slot`** — like `embedded`, but the iframe `src` carries **`?pdn_embed=1`** (`/apps/<id>/?pdn_embed=1`). This **signals the app to render chrome-less** — to drop its own header/nav so it blends into the single PDN chrome (one set of chrome: the panel's). Use it for an app that can render a bare content pane on request, for the most seamless "part of the node UI" feel.

The launcher feed (`GET /api/v1/apps`) surfaces the resolved mode as **`uiMode`** (`standalone | embedded | slot`) on each `NodeApp` so the nav knows how to open each app from this one fetch. For `embedded`/`slot` the panel's in-panel screen also offers an "open in new tab" affordance pointing at the app's own page (`/apps/<id>/`, never the chrome-less slot variant).

**Why an iframe (not inline DOM injection):** the apps are server-rendered with forms — links, form `action`s, and redirects all assume a real browser navigation context. An iframe **is** a real browser context, so the app's links/forms/navigation work natively while pdn still owns the surrounding chrome. Rendering an app's HTML inline into the SPA would mean intercepting every in-app navigation (each link click, each form submit, each redirect) to keep it inside the panel — out of scope. The iframe is the pragmatic seam: one PDN chrome, native app behaviour, no navigation interception.

### The `forward:` block — tailnet port forwarding (built S4)

An app may ask pdn's embedded Tailscale node ([`network-access.md`](network-access.md)) to expose one or more ports **on the tailnet** and reverse-proxy each to one of the app's loopback listeners. This is how a packaged BBS gets IMAPS/SMTPS to the operator's phone without running its own TLS or owning a public DNS name: the app stays **plaintext on loopback** and pdn owns the TLS edge with the node's `.ts.net` cert.

```yaml
forward:
  - listen: 993            # tailnet-facing port on the node's tsnet node
    target: 127.0.0.1:1430 # the app's plaintext loopback listener
    tls: terminate         # terminate (default) | raw
```

Each forward is a **capability** — it appears in the enable confirm ("Exposes on your tailnet: IMAPS :993 → 127.0.0.1:1430") so the owner sees the exposure they are granting before they flip the switch. The contract and its rules:

- **`listen`** — the tailnet-facing port, `1..65535`, and **never `443`** (reserved for the web reverse-proxy the sidecar already runs). Two *discovered* packages can't claim the same `listen` port — pdn flags **both** as broken (the same disambiguation pattern as a session-verb collision), so the owner picks one.
- **`target`** — the app's listener as `host:port`, and the host **must be loopback** (`127.0.0.1` / `::1` / `localhost`). pdn will not proxy the tailnet to an arbitrary host — a non-loopback target is a validation error.
- **`tls`** — `terminate` (the default): the sidecar terminates TLS with the node's tailnet cert and proxies plaintext to `target` (the everyday IMAPS/SMTPS shape — the app never sees TLS). `raw`: the sidecar passes the TCP stream through unterminated, relying on WireGuard for transport encryption (for an app that speaks its own TLS or a plaintext tailnet protocol).

Forwards are **tailnet-only** and apply **only when `tailscale.enabled`** — no Tailscale, no forwards. A broken or duplicate forward makes the whole package an error entry (hence never enabled, hence its forwards are never collected). The supervisor writes the collected forwards from every enabled, error-free package to the sidecar's `--forwards-file`; enabling/disabling a forward-declaring app (or any forward change) restarts the sidecar. The flow is documented in [`network-access.md`](network-access.md) § App-declared port forwarding.

## Owner state — `packetnet.yaml`

```yaml
apps:
  - id: lobby
    enabled: true
  - id: dapps
    enabled: true
    environment:             # merged over the manifest's service environment
      DAPPS_CALLSIGN: M0LTE-7
    match: null              # optional session-verb override
```

That is the whole owner surface: **discovered packages default to disabled**; `apps:` entries flip them on and carry small overrides. An `apps:` entry whose id matches no discovered package is a validation warning, not an error (the package may be installed later). The legacy inline `applications:` list keeps working unchanged (those entries are owner-authored, so they keep their `enabled: true` default); an id collision between an inline entry and a discovered package is a validation **error**. Verb-collision rules extend across the union of both sources.

The enable/disable toggle in the UI is a config write through the existing `IWritableConfigProvider` seam — it lands in the YAML, survives restarts, and hot-applies.

## Lifecycle — the service supervisor

For every **enabled** package whose manifest has a `service` block with `managed: pdn` (the default), pdn owns the daemon:

- **Start** on enable / node start / config apply. The child runs as the node's user, in its own process group, stdout+stderr captured to the node log prefixed `app:<id>`.
- **Stop** on disable / node shutdown: SIGTERM to the process group → 5 s grace → SIGKILL the tree (same discipline as the pdn-app/1 process teardown).
- **Restart** per the manifest `restart` policy with exponential backoff (1 s doubling, capped at 60 s). A **crash-loop breaker** trips after 5 failures inside 5 minutes → the service enters `Faulted` and stays down until the owner toggles it or hits restart in the UI.
- **Reconcile** is idempotent and runs at startup, on every config apply, and on demand: desired set vs running set; start the missing, stop the surplus, leave the matching alone. Manifest changes (rescan) take effect on the next reconcile.

`managed: external` is the escape hatch: the owner runs the daemon (systemd, container, whatever). pdn never starts or stops it; the toggle still gates the session verb and the web tile, and the UI shows the service as `External` rather than guessing at its health.

Session attachment (`session:` block) and the web tile (`ui:` block) follow the toggle exactly as inline `applications:` entries do today — disabled means the verb falls through to "unknown command" and the tile disappears.

## Surfaces

- `GET /api/v1/apps` — the launcher feed: enabled apps with a `ui` (inline + discovered packages), each `{id, name, icon?, url, uiMode, state}`. `url` is always `/apps/{id}/` (the reverse-proxied absolute path). **`uiMode`** is the resolved `ui.mode` (`standalone | embedded | slot`, see § `ui.mode`) — how the panel's nav opens the app (a full navigation vs an in-panel iframe). **`state`** is the live supervisor state — an `AppServiceState` name (`Stopped|Starting|Running|Backoff|Faulted|External`) — or `null` when the app has no pdn-managed service to watch (an inline app, or a package with no `service:` block). It is derived exactly as the packages inventory derives it, so the panel's left-nav can render a not-running warning from this **one** fetch (no second call to `/apps/packages`). Read scope.
- `GET /api/v1/apps/packages` — the admin inventory: every discovered package + every inline entry, with `{id, name, version, description, icon, capabilities, enabled, source: package|inline, service: none|managed|external, state: Stopped|Starting|Running|Backoff|Faulted|External, pid?, detail?, forwards: [{listen, target, tls}]}`. Read scope to view; **admin scope to mutate**.
- `POST /api/v1/apps/packages/{id}/enable` · `/disable` — writes the `apps:` override via the config-write seam (admin).
- `POST /api/v1/apps/packages/{id}/restart` — supervisor action for a managed service (admin); also the way out of `Faulted`.
- **UI — apps in the left nav.** Each enabled, web-capable app (every entry the `/api/v1/apps` feed returns) is a first-class entry in a dedicated **"Apps" group** in the left nav, below the core items, rendered with its manifest `icon` + `name`. How clicking one opens the app depends on its `uiMode` (see § `ui.mode`): a **`standalone`** app is a **full navigation** to its own UI at `url` (`/apps/{id}/`) — an absolute same-origin path pdn reverse-proxies, **not** a client-side SPA route; an **`embedded`**/**`slot`** app opens via the in-panel SPA route `/apps/{id}` (an iframe inside the panel chrome — `slot` chrome-less via `?pdn_embed=1`). A non-web app (no `ui`) gets no nav entry — it isn't openable, so it lives only on the management page. An enabled app whose service isn't running (`Stopped`/`Backoff`/`Faulted`) shows a **not-running warning badge** on its nav entry regardless of mode (a *disabled* app is expected to be stopped, so it never warns); the same warning mirrors onto its row on the management page.
- **UI — the Apps page is pure management.** It no longer carries a launcher grid (the nav lists the openable apps now). It is "Available apps" (install) + the installed-package list (enable/disable/restart/update/uninstall). Enabling shows the declared `capabilities` in the confirm step (the owner sees what they are trusting before the switch flips).

## Security posture (v1)

Unchanged from the platform's owner-owns-trust model: enabling a package is the trust grant; services run as the `packetnet` user with no new privilege; `capabilities` are displayed, not enforced (enforcement tiers are a later slice); manifests are read-only data — nothing in a manifest executes until the owner enables the app. The state dir is per-app and 0750. RHP remains a loopback TCP surface; a `packet`-capable app reaches it exactly like any local process — the manifest's capability declaration is documentation for the owner, and `PDN_RHP_HOST`/`PDN_RHP_PORT` are a convenience, not a grant.

### The `packet` capability (renamed from `network`)

`capabilities` are free-form strings (`AppPackageManifest.Capabilities` is `IReadOnlyList<string>`) shown to the owner only as the install/enable trust prompt — there is no enum. The conventional values are `session`, `packet`, and `web`.

**`packet`** means *full packet-radio network access*: the app binds a callsign and sends/receives over AX.25 / NET-ROM (RF / KISS-TCP / AXUDP / sim). It replaces the older `network` spelling, which read as TCP/IP / LAN / internet — the wrong mental model for what the capability grants. `packet` is transport-accurate and unambiguous.

`network` is kept as a **back-compat alias**: pdn still accepts it on the wire and **display-normalises** it to `packet` wherever a capability list is projected to the API (`AppCapabilities.Normalize`, applied in the `/apps/packages` and `/apps/available` projections, and mirrored client-side by `displayCapability`). So a third-party app whose manifest still declares `network` shows up as `packet` in the trust prompt without that app having to re-release. Any future code that *semantically* gates on this capability must accept both spellings (`AppCapabilities.GrantsPacketAccess`). The catalog (`catalog/apps.yaml`) ships `packet` directly; the alias covers app manifests living in their own repos until they rename per-app.

## Distribution

The directory **is** the format. Conventions on top of it:

- **app catalog (the primary path — [`app-catalog.md`](app-catalog.md)):** pdn ships a curated **index** (`catalog/apps.yaml`, baked into the deb at `/usr/share/packetnet/catalog/`) of vetted apps the owner installs on demand from the control panel's **"Available apps"** view — by URL-fetch of a sha256-pinned artifact (a per-RID binary, a per-arch `.deb` extracted unprivileged, or a `.pdnapp`) or by uploading a `.pdnapp`. Install lands a discovered-but-disabled package in `/var/lib/packetnet/apps/<id>/`. This is how DAPPS, bpqchat and convers reach a node — **not** by being bundled in the deb.
- **bundled in pdn's deb**: only the tiny reference apps (WALL, LOBBY) are staged into `/usr/share/packetnet/apps/<id>/` at build time, for a good first-boot experience. Heavyweight apps deliberately are not — that is what the catalog is for.
- **`.pdnapp`** (shipped): a tarball of the package dir, manifest at root, installable by UI upload (or a catalog `kind: pdnapp` URL) into `/var/lib/packetnet/apps/<id>/`.
- **own `.deb`**: an app may still publish its own `.deb` dropping `/usr/share/packetnet/apps/<id>/`; the catalog consumes such `.deb`s by **extraction** (`kind: deb`), and `apt install`ing one is a complementary CLI path (later, with the node's own apt distribution).

The "only public interfaces" rule applies to packaging too: pdn never builds from or vendors another app's source — the catalog references the app's **published release artifact** by URL + sha256, and the manifest for an external app belongs upstream in that app's repo.

## Implementation map

- `Packet.Node.Core/Applications/Packages/` — manifest records + YAML parsing + `IAppPackageCatalog` (discovery, override merge, validation) — the **catalog**.
- `Packet.Node.Core/Hosting/AppServiceSupervisor.cs` — the **supervisor** (`IAppServiceSupervisor`), reconciled from the hosted service alongside the port supervisor.
- `ApplicationHost` resolves session verbs from the union (inline entries + enabled packages).
- `PdnAppGateway` tiles from the union; new `apps/packages` endpoints beside it.
- Web UI: Apps screen management section.
- Packaging: manifests for WALL/LOBBY in `/usr/share/packetnet/apps/*`; the `apps:` examples in `packaging/packetnet.yaml`; the app catalog `catalog/apps.yaml` staged into `/usr/share/packetnet/catalog/` by `build-deb.sh` (DAPPS/bpqchat/convers install from it on demand — [`app-catalog.md`](app-catalog.md)).
