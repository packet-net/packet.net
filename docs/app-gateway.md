# pdn app gateway — the human plane (proxied web UIs)

**Status:** v1, agreed 2026-06-10. The "human plane" of the app platform ([`app-extensibility.md`](app-extensibility.md)): how an app exposes a **web UI through pdn** without pdn knowing anything about the app. The node is a **broker** — a manifest reader, a reverse proxy, an auth gateway. It never imports a line of the app's code.

This is the contract an app author writes a web UI against. Companion to the packet-plane [`app-local-session-wire.md`](app-local-session-wire.md); an app may use one plane or both.

## The deal

1. The owner runs the app's **own web server** on a loopback port (e.g. `http://127.0.0.1:9090`) — any stack, any language. **The app must bind loopback only** (see Trust).
2. The owner adds a `ui` block to the app's `applications:` entry pointing at that upstream (plus a tile name/icon for the launcher).
3. pdn's web control panel renders an **Apps launcher** from the registered `ui` manifests, and **reverse-proxies `/apps/{id}/*`** to the app's upstream.
4. pdn sits in front as the **auth gateway**: only an authenticated panel user (with `read` scope) reaches `/apps/{id}/*`, and pdn **injects the authenticated identity** as request headers the app can trust. TLS, passkeys, refresh — all the auth lives at the gateway, once. The app reimplements none of it.

## Config

```yaml
applications:
  - id: wall
    match: WALL                       # packet-plane verb (optional — an app may be web-only)
    enabled: true
    kind: process
    command: /usr/bin/python3
    args: [ /usr/share/packetnet/apps/wall/wall.py ]
    workingDirectory: /var/lib/packetnet/apps/wall
    ui:                               # the human-plane manifest — presence is what puts it in the launcher
      upstream: http://127.0.0.1:9090 # the app's own web server (loopback)
      name: WALL                      # launcher tile label (defaults to id)
      icon: message-square            # optional lucide icon name for the tile
    capabilities: [ session ]
```

An app with a `ui` block appears in the launcher and is reachable at `/apps/{id}/`. An app without one is packet-plane-only (no tile, no proxy). The `ui.upstream` is just "where to proxy" — **in v1 pdn does not manage the upstream's lifecycle** (the owner runs it, e.g. as their own systemd unit); a later slice may let the node supervise long-running app servers.

## What pdn does to each request

For `GET|POST|… /apps/{id}/<path>?<query>`:

- **Auth gate.** When node auth is enabled, the request must carry a valid panel session (the `pdn_at` cookie pdn sets at login, or a bearer token) with at least `read` scope — else 401/redirect to login. When auth is off, the request passes (identity is anonymous).
- **Path rebase.** The `/apps/{id}` prefix is stripped: the app sees `<path>?<query>`. So **your app is mounted at the site root from its own point of view** — but the browser sees it under `/apps/{id}/`, so **use relative URLs only** for your assets and links (`./style.css`, `form action="post"`), or emit `<base href="./">`. Absolute-rooted URLs (`/style.css`) will break.
- **Identity injection.** pdn **strips any client-supplied copy** of these headers, then sets them from the authenticated session:
  - `X-Pdn-User` — the viewer's callsign / username (empty when anonymous / auth-off).
  - `X-Pdn-Scope` — `read` | `operate` | `admin` (empty when anonymous).
  - `X-Pdn-Gateway` — `1` (marks the request as gateway-originated).
  - `X-Forwarded-Prefix` — the public mount point (`/apps/{id}`). A server-rendered app MUST
    prefix its absolute URLs (links, form actions, redirect `Location`s) with this value (treat
    absent as empty — direct loopback access then renders root-relative URLs unchanged);
    otherwise a form posted from the proxied page escapes to pdn's root and breaks.

  Read these to know who is viewing. (v1 does not cryptographically sign them — see Trust.)
- **Credential stripping.** pdn removes the viewer's `Authorization` header and the `pdn_at`
  cookie before forwarding (the app's own cookies are re-emitted untouched). The app is told
  *who* is viewing; it is never handed the credential that proves it - so a compromised or
  merely chatty upstream can't replay a panel token against `/api/v1`, and pdn's session token
  can't end up in the app's request log.
- **Forward.** Method, the remaining headers (minus hop-by-hop), query, and the request/response bodies are streamed through unchanged (so SSE / chunked responses work).

## Trust (read this)

The app's web server **MUST bind loopback only** (`127.0.0.1`). The identity headers are trustworthy because (a) pdn strips any client-supplied copy before injecting its own, and (b) the only thing that can reach a loopback-bound upstream is pdn itself. If the app binds a routable interface, anything on the network could hit it directly and forge `X-Pdn-User` — so don't. (v1 keeps the headers unsigned and relies on this loopback boundary, matching the node-owner-owns-trust model. A future hardening can sign them with an owner-provisioned per-app secret — the same shape as the network-plane service credential.)

## Notes / v1 limits

- **WebSockets** through the gateway are supported (the forwarder handles the upgrade); long-poll / SSE stream fine.
- **Cookie freshness:** the `pdn_at` gateway cookie carries the access token and is refreshed when the panel session renews; if it lapses mid-use, re-open the app from the launcher.
- See [`examples/wall/wall_web.py`](../examples/wall/wall_web.py) for the worked example — a stdlib read-only web view of the same wall the packet-plane app writes.
