# packetnet-tsnet - pdn's embedded Tailscale node

A small, static Go binary that gives a pdn node a real, browser-trusted HTTPS
endpoint with **no public DNS, no port-forward, and no cert management** - so
WebAuthn/passkeys work remotely. It embeds a Tailscale node via
[`tailscale.com/tsnet`](https://pkg.go.dev/tailscale.com/tsnet) (userspace - no
`tailscaled`, no root, no TUN), terminates TLS for `pdn.<tailnet>.ts.net` with
the automatic Let's Encrypt cert, and reverse-proxies to pdn's loopback HTTP.

This is the Go half of the network-access design - see
[`docs/network-access.md`](../../docs/network-access.md), §"The sidecar". pdn's
`TailscaleSidecarHostedService` supervises it: launches it when
`tailscale.enabled`, restarts on failure with backoff, and SIGTERMs it on
shutdown. It is **infrastructure, not an app** - it never appears in the apps
inventory.

## Flags

| Flag             | Meaning                                                                       |
| ---------------- | ----------------------------------------------------------------------------- |
| `--hostname`     | Desired node name → `<hostname>.<tailnet>.ts.net` (the actual name is read back). |
| `--state-dir`    | Persistent tsnet state directory. **Load-bearing** for a stable hostname/cert (and therefore stable passkeys) across restarts. Required. |
| `--target`       | The loopback HTTP `host:port` pdn serves, to reverse-proxy to (e.g. `127.0.0.1:8080`). |
| `--authkey-file` | Optional path to a file holding a tailnet pre-auth key. If the file exists and is non-empty its contents are used as the tsnet `AuthKey` for first join; otherwise the sidecar falls back to interactive login. |
| `--funnel`       | Expose publicly via Tailscale Funnel instead of tailnet-only. Off by default. |
| `--forwards-file`| Optional path to a JSON array of app-declared TCP port forwards (see below). Independent of `--target`; absent ⇒ no forwards. |

## Status contract (stdout)

`stdout` is a **JSON status stream - one object per line** - that pdn's
supervisor parses. `stderr` is free-form logs (the raw tsnet/`Logf` output).

```json
{"state":"starting"}
{"state":"needs-login","authURL":"https://login.tailscale.com/a/…"}
{"state":"running","fqdn":"pdn.<tailnet>.ts.net"}
{"state":"error","error":"…"}
```

- `starting` - emitted at launch.
- `needs-login` - emitted when interactive auth is required (no/invalid auth
  key); `authURL` is the `login.tailscale.com` URL to surface to the operator.
- `running` - emitted once joined and serving TLS; `fqdn` is the assigned
  MagicDNS name (trailing dot trimmed).
- `error` - emitted on a fatal error before exit.

On **SIGTERM** (or SIGINT) the sidecar shuts down gracefully and exits 0.

## Tags come from the auth key

There is deliberately **no `--tags` flag**. A `tsnet` node inherits its tags
from the pre-auth key it joins with - mint the key with the tags you want
(e.g. `--tags=tag:server`, key-expiry disabled) and drop it in the
`--authkey-file`. The sidecar cannot set tags after the fact.

## What the reverse proxy does

The proxy is a `httputil.NewSingleHostReverseProxy` at the loopback `--target`.
Its Director sets, on every outbound request:

- `X-Forwarded-Proto: https`
- `X-Forwarded-Host: <fqdn>`

(plus the default `X-Forwarded-For`). pdn's loopback-trusted `ForwardedHeaders`
read these so the request appears as HTTPS at the `.ts.net` host - making
`Request.IsHttps` true and the WebAuthn origin `https://…`. That is the whole
point of the sidecar.

## App-declared port forwarding (`--forwards-file`)

Beyond the web reverse-proxy, the sidecar can expose **arbitrary TCP ports** on
the tsnet node and pipe them to loopback targets - e.g. so the BBS arc's
IMAPS/SMTPS reach the phone over Tailscale with a real, trusted cert while the
mail app stays plaintext on loopback. This is driven by a JSON file written by
pdn's supervisor and pointed at via `--forwards-file`. It is **independent of
`--target`**: the web reverse-proxy on `:443` is unchanged.

### File format

A JSON **array** of forward objects:

```json
[ {"listen":993,"target":"127.0.0.1:1430","tls":"terminate"},
  {"listen":465,"target":"127.0.0.1:1465","tls":"terminate"},
  {"listen":4000,"target":"127.0.0.1:4000","tls":"raw"} ]
```

| Field    | Type   | Meaning |
| -------- | ------ | ------- |
| `listen` | int    | Port to open **on the tsnet node** (1-65535). |
| `target` | string | The loopback `host:port` to pipe accepted connections to (plaintext TCP). |
| `tls`    | string | `"terminate"` or `"raw"`. Omitted ⇒ `"raw"`. |

### `terminate` vs `raw`

- **`terminate`** → the node listens with `ListenTLS` (implicit TLS using the
  node's **auto Let's Encrypt cert** - the same cert mechanism the web path
  uses, available on any port). The client gets a browser/OS-trusted cert; the
  sidecar terminates TLS and dials `target` over **plaintext** TCP. This is the
  **IMAPS / SMTPS** case (`993` / `465`): the phone sees a trusted cert, the
  mail app never has to do TLS.
- **`raw`** → the node listens **plaintext** (`Listen`) and relies on the
  **tailnet's WireGuard encryption** for the on-the-wire hop. The same dial +
  bidirectional copy to `target` follows. Use for protocols that bring their
  own TLS, or where tailnet encryption is sufficient.

In both modes the byte-pump is identical (dial `target`, `io.Copy` both ways
until either side closes, half-close on EOF); the only difference is which
listener is created on the node.

### Robustness

Forwards are a **best-effort overlay** - they never take down the web path:

- Each forward is logged at startup: `forward 993 (terminate) -> 127.0.0.1:1430`.
- A missing, unreadable, empty, or unparseable forwards file → logged; the
  sidecar runs with **no forwards** (the web reverse-proxy is unaffected).
- A single bad entry (out-of-range `listen`, empty `target`, unknown `tls`
  mode) → logged and **skipped**; the rest are still served.
- A failed listen on one port → logged and skipped; others continue.
- A per-connection dial failure to `target` → logged, that one connection
  closed; the listener keeps accepting.
- On **SIGTERM/SIGINT** (ctx cancel) every forward listener is closed alongside
  the web listener.

Each forward runs in its own goroutine; one connection's failure never affects
another.

### UDP

**Not supported** (deliberately). `tsnet` does expose `ListenPacket("udp", …)`,
but unlike the TCP `Listen`/`ListenTLS` (`:port`) forms it requires the node's
own tailnet **IP** in the listen address (`ip:port`, IP mandatory), and a
datagram relay needs its own per-source connection tracking - neither maps onto
the symmetric TCP dial+copy here. TCP is the actual need (IMAPS/SMTPS), so UDP
is out of scope for this slice; it can be added later if a real UDP forward
appears.

## Build

Static, `CGO_ENABLED=0`, cross-compiled per arch (`build-deb.sh` does this for
the target RID and stages the binary at `/usr/lib/packetnet/packetnet-tsnet`):

```sh
# amd64
CGO_ENABLED=0 GOOS=linux GOARCH=amd64        go build -trimpath -ldflags="-s -w" -o packetnet-tsnet .
# arm64
CGO_ENABLED=0 GOOS=linux GOARCH=arm64        go build -trimpath -ldflags="-s -w" -o packetnet-tsnet .
# armhf
CGO_ENABLED=0 GOOS=linux GOARCH=arm GOARM=7  go build -trimpath -ldflags="-s -w" -o packetnet-tsnet .
```
