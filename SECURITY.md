# Security policy

## Reporting a vulnerability

Email **tom@fann.ing** rather than opening a public issue. You will get a response within
7 days. There is no CVE numbering authority and no security announcement list; if that
changes it will be said here first.

## What is supported

Packet.NET ships releases (`node-v*` for the node `.deb` and tarball, `lib-v*` for the
NuGet libraries) but is pre-1.0 and has no long-term-support branch. Fixes land on `main`
and go out in the next release; there are no backports to older tags. If you are running
a node, run a current one.

## The posture the node actually ships

This is the state of the code, not an aspiration. The same table, with the per-surface
detail, is in [`docs/plan.md` §10](docs/plan.md#10-security-threat-model).

**HTTP first, with a login.** The control panel and its REST API bind `0.0.0.0:8080` over
plain HTTP by default, and authentication is **on** by default. Those two defaults were
flipped together and belong together: a panel reachable from your LAN needs the login,
and the login is what makes the LAN bind survivable. A fresh node has no users, so the
first visitor gets the setup wizard, creates the admin, and everything after that needs a
session. Each user holds exactly one scope, `read` then `operate` then `admin`, and the
higher ones imply the lower.

**TLS is opt-in, and pdn does not manage certificates.** The built-in HTTPS listener is
off by default; when enabled it generates a self-signed certificate on first start or
serves a PKCS#12 you point it at. **There is no built-in ACME or DNS-01.** For a real,
browser-trusted certificate without public DNS or a port forward, the supported path is
the embedded Tailscale sidecar (also opt-in), which terminates TLS on the tailnet and
proxies to the node's loopback HTTP; exposing that beyond the tailnet with Funnel is a
further separate opt-in. Anything else is your own edge's job. Forwarded headers
(`X-Forwarded-Proto/Host/For`) are honoured only from loopback, so a remote client cannot
spoof its scheme or host.

**Credentials.** Passwords are hashed with Argon2id. A login returns a short-lived access
JWT plus an opaque, one-time-use refresh token; reusing a consumed refresh token revokes
the whole family. The JWT signing key is generated on first start and lives in `pdn.db`.
WebAuthn passkeys are supported on a secure origin. Failed logins are throttled at five
per five minutes, keyed by username *and* by source IP, checked before the user lookup so
it is not an account-existence oracle. There is no general HTTP rate limiter beyond that.

**Bearer tokens, not ambient cookies.** The API authenticates on the `Authorization`
header, so it is not CSRF-shaped and carries no antiforgery machinery. The one cookie is
the app gateway's, scoped to `/apps/`, `HttpOnly`, `SameSite=Strict`, and `Secure`
whenever the request arrived over HTTPS. The six SSE routes additionally accept
`?access_token=`, because a browser `EventSource` has no header API; that permission is
endpoint metadata carried by those routes, not a path list something can forget to update.

**`/metrics` is deliberately anonymous.** It is served on the same listener and is never
gated, regardless of the auth setting, because a Prometheus agent carries a static config
rather than a login. It exposes heard callsigns, per-peer SNR, port and radio health, and
the running version. If your node's port is reachable somewhere you would not want that,
put a reverse proxy in front of it or keep it on a trusted network. See
[`docs/observability.md`](docs/observability.md).

**Optional surfaces, all off by default.** The RHPv2 TCP front-end binds loopback,
requires no auth message unless you ask it to, and is resource-bounded against a hostile
peer (64 connections, 256 handles per client, a 30 second in-frame timeout, and the same
login throttle on its auth message). MCP is mounted at `/mcp` on the web listener, not on
a separate port, and takes a bearer token minted on its own JWT audience so a panel token
cannot reach it and an MCP token cannot reach the control API; the OAuth 2.1 flow for
hosted connectors is a further opt-in and is refused outright if panel auth is off. The
telnet console binds loopback; privileged sysop verbs on it are gated by TOTP.

**There is no AGW server.** `Packet.Agw` is a client library for dialling someone else's
LinBPQ, direwolf or XRouter. The node hosts no AGW listener and no RHPv2 WebSocket mount.

**Auditing.** Every privileged action is written to an audit log in `pdn.db` with the
actor, source IP, scope and a summarised detail, never secrets; the log is bounded and
pruned. A logging fault never takes the node down.

**Updates.** The `.deb` can update itself two ways. The apt channel trusts the repository
GPG key. The GitHub channel is checksum-verified: a root helper parses the download host
(it must be GitHub), fetches that release's `SHA256SUMS` itself from a URL it derives
rather than trusting anything the unprivileged service user wrote, verifies the `.deb`
against it with no fallback if the fetch fails, keeps the previous package, and rolls back
if the node does not come healthy afterwards. **Artifacts are not signed with cosign**;
that is a documented deferral ([#188](https://github.com/packet-net/packet.net/issues/188)),
not something that has shipped.

## What is out of scope

Amateur radio forbids obscuring the meaning of a transmission, so the RF side is
cleartext by design and by law: AX.25, KISS over TCP or serial, and AXUDP carry no
authentication and no encryption, and none is planned. What *is* permitted, and is used,
is authentication: TOTP gates privileged on-air commands. Do not put anything on a radio
link that you would mind the world reading.

## Past reviews

- [`docs/security-review-2026-06-13.md`](docs/security-review-2026-06-13.md): a focused
  security and hardness pass (RHPv2 server bounds, wire-parser fuzzing, the web auth core,
  a dependency-vulnerability CI gate).
- [`docs/code-review-2026-08-16.md`](docs/code-review-2026-08-16.md): a whole-tree code
  review whose findings included several security ones, remediated under umbrella issue
  [#703](https://github.com/packet-net/packet.net/issues/703). This file was rewritten as
  part of it: the version before 2026-08-17 described TLS by default, ACME, cosign-signed
  updates and an AGW server, none of which the node has ever had.
