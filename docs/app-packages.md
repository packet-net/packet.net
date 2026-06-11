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
```

At least one of `session` / `service` / `ui` must be present. Examples of the shapes:

- **WALL**: `session` (kind process, wall.py) + `service` (wall_web.py, its web view) + `ui`.
- **LOBBY**: `session` (kind socket) + `service` (the daemon pdn now keeps alive — no more hand-run systemd units).
- **DAPPS**: `service` (the dotnet daemon, environment selecting the RHPv2 bearer) + `ui` + `capabilities: [network, web]`. No `session` — it binds its own callsigns over RHP.
- **The BBS**: `service` + `ui`, like DAPPS.

Path resolution: a relative `command`/`args` element that names an existing file in the package dir resolves to it; everything else passes through untouched. `workingDirectory` defaults to the state dir.

Environment given to a supervised service: the node's own environment, plus `PDN_APP_ID`, `PDN_APP_DIR` (the package dir), `PDN_APP_STATE` (the state dir), `PDN_NODE_CALLSIGN` (the node's own callsign, so an app can derive its identity per the an-app-lives-at-an-SSID-of-the-node-callsign convention) and `PDN_NODE_ALIAS` when set, and — when the RHP server is enabled — `PDN_RHP_HOST`/`PDN_RHP_PORT`, then the manifest `environment` map, then the owner's override map (last wins).

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

- `GET /api/v1/apps` — unchanged (launcher tiles for enabled apps with a `ui`).
- `GET /api/v1/apps/packages` — the admin inventory: every discovered package + every inline entry, with `{id, name, version, description, icon, capabilities, enabled, source: package|inline, service: none|managed|external, state: Stopped|Starting|Running|Backoff|Faulted|External, pid?, detail?}`. Read scope to view; **admin scope to mutate**.
- `POST /api/v1/apps/packages/{id}/enable` · `/disable` — writes the `apps:` override via the config-write seam (admin).
- `POST /api/v1/apps/packages/{id}/restart` — supervisor action for a managed service (admin); also the way out of `Faulted`.
- **UI**: the Apps screen grows a management section — every package with its toggle, status pill, version; enabling shows the declared `capabilities` in the confirm step (the owner sees what they are trusting before the switch flips).

## Security posture (v1)

Unchanged from the platform's owner-owns-trust model: enabling a package is the trust grant; services run as the `packetnet` user with no new privilege; `capabilities` are displayed, not enforced (enforcement tiers are a later slice); manifests are read-only data — nothing in a manifest executes until the owner enables the app. The state dir is per-app and 0750. RHP remains a loopback TCP surface; a `network`-capable app reaches it exactly like any local process — the manifest's capability declaration is documentation for the owner, and `PDN_RHP_HOST`/`PDN_RHP_PORT` are a convenience, not a grant.

## Distribution

The directory **is** the format. Conventions on top of it:

- **deb**: a package may ship as its own `.deb` dropping `/usr/share/packetnet/apps/<id>/`. pdn's own deb ships the bundled apps (WALL, LOBBY, and DAPPS staged from its **published release artifact** — never vendored source) the same way.
- **`.pdnapp`** (later): a tarball of the package dir, manifest at root, installable by UI upload into `/var/lib/packetnet/apps/<id>/`.
- **apt repo** (later, with the node's own apt distribution): the store.

The "only public interfaces" rule applies to packaging too: pdn's build may **fetch** another project's published release artifact to stage a bundled package, but never builds from or vendors its source; the manifest for an external app belongs upstream in that app's repo as soon as it can carry one.

## Implementation map

- `Packet.Node.Core/Applications/Packages/` — manifest records + YAML parsing + `IAppPackageCatalog` (discovery, override merge, validation) — the **catalog**.
- `Packet.Node.Core/Hosting/AppServiceSupervisor.cs` — the **supervisor** (`IAppServiceSupervisor`), reconciled from the hosted service alongside the port supervisor.
- `ApplicationHost` resolves session verbs from the union (inline entries + enabled packages).
- `PdnAppGateway` tiles from the union; new `apps/packages` endpoints beside it.
- Web UI: Apps screen management section.
- Packaging: manifests for WALL/LOBBY in `/usr/share/packetnet/apps/*`; the `apps:` examples in `packaging/packetnet.yaml`; DAPPS staging in `build-deb.sh`.
