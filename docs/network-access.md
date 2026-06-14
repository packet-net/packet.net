# Network access — design / ADR

How a pdn node is reached over the network, and where TLS, trusted certs, and passkeys come from. Supersedes the previous "public cert via acme.sh + Cloudflare DNS-01 for `pdn.m0lte.uk`" approach. Status: decided 2026-06-14 (Tom); execution to follow.

## The problem with what we had

To make **passkeys** work over the WLAN, pdn was given a publicly-trusted cert for `pdn.m0lte.uk` via **acme.sh + Cloudflare DNS-01** (a "grey" DNS record + an API token on the box). That works but is fragile and operator-hostile: it needs a public domain, a Cloudflare account + scoped token, the DNS-01 dance, and renewal plumbing — none of which a typical node operator should have to run just to log into their own box.

## The hard constraint (drives everything)

WebAuthn/passkeys require a **secure context**: HTTPS **with a cert the browser already trusts** (no click-through), **or** `http://localhost`/`127.0.0.1` (loopback is exempt). Two corollaries:

- The **RP ID can't be an IP** and can't be a bare public suffix — it must be a hostname.
- **Self-signed counts only if trusted** (CA installed on the device); an untrusted self-signed cert is not a secure context for WebAuthn.

So bare-LAN HTTP (`http://<ip>`) **cannot** do passkeys — accepted. The whole design follows from relocating the trusted cert off pdn and onto something that provides one for free.

## Decisions (Tom, 2026-06-14)

1. **pdn is HTTP-first; TLS is the edge's job.** The default deployment serves plain **HTTP** (loopback + LAN). pdn does not run ACME and does not need a public domain. (pdn already matches this: `management.https` is opt-in/off-by-default with self-signed-or-BYO `.pfx`; there is no ACME inside pdn — only the *external* acme.sh dance, which is retired.)
2. **Tailscale is the blessed remote + passkey path — embedded (Tier C).** pdn ships a small **embedded Tailscale node (a `tsnet` Go sidecar)**: the node joins the operator's tailnet, gets a real Let's Encrypt cert for `pdn.<tailnet>.ts.net`, terminates TLS, and reverse-proxies to pdn's loopback HTTP. Browser sees trusted HTTPS → **passkeys work remotely**, RP ID = the `.ts.net` host, with **no public DNS, no port-forward, no cert management**.
3. **Keep the optional built-in HTTPS** (self-signed or BYO `.pfx`) for the LAN/DIY operator and as the future IMAP/SMTP cert. Default off.
4. **IMAP/SMTP** (future, the BBS arc): **self-signed on the LAN** (one-time device trust), **Tailscale real cert remotely**. Documented here; implemented when IMAP lands.
5. **DNS-01 / Cloudflare is fully retired** — not even kept as an "advanced" option. An operator who wants public, no-VPN TLS runs their own edge (reverse proxy / Cloudflare Tunnel / their own LE cert) — explicitly out of pdn's scope.
6. The **self-signed UX smoothing** (local CA + `ca.crt`/`.mobileconfig`) is **parked** — [issue #439](https://github.com/packet-net/packet.net/issues/439).

## Why Tier C, and why a Go sidecar

`tsnet` (`tailscale.com/tsnet`) is Tailscale's **embeddable** node: userspace, **no `tailscaled`, no root, no TUN**, with `ListenTLS` serving the auto LE cert. It is **Go-only** — there is no managed/C# Tailscale client, and reimplementing the stack (userspace WireGuard + the control/DERP/disco protocols + cert flow) in C# is out of scope. Consuming the Go core from .NET means either a **sidecar process** or P/Invoke into the official `libtailscale` c-shared binding; the **sidecar wins** (process isolation, simple lifecycle, no in-proc Go runtime/cgo). Either way **Go enters packet.net's build** — accepted as the cost of a genuinely built-in experience. (Go 1.26 is already on the self-hosted runner.)

The pure-.NET alternative is **Tier A** (orchestrate the operator's *separately-installed* system Tailscale via the CLI). We are not doing Tier A as the primary path because Tier C removes the separate-install step for the new/no-daemon operator — but an operator who already runs the system daemon can simply leave the sidecar disabled and front pdn with their own `tailscale serve`; that remains supported by virtue of decision 1 (pdn is just HTTP behind any edge).

## The sidecar — `packetnet-tsnet`

- **Source:** a small Go module at `sidecar/tsnet/` in this repo (≈ a `tsnet.Server` + `httputil.ReverseProxy`). Built **static, `CGO_ENABLED=0`**, cross-compiled per arch (`GOARCH=amd64/arm64/arm`, `GOARM` for armhf) — fits the self-contained `.deb` ethos (no libc dep).
- **Packaging:** staged into the `.deb` at `/usr/lib/packetnet/packetnet-tsnet` (beside the self-update helpers), one per-arch binary. CI gains a `go build` step (runner has Go); `build-deb.sh` stages the right-arch binary.
- **Supervision:** a dedicated `TailscaleSidecarHostedService` in `Packet.Node` launches it when `tailscale.enabled`, restarts on failure with backoff, SIGTERMs on shutdown (same teardown discipline as the app supervisor). It is **infra, not an app** — it does not appear in the apps inventory.
- **What it does:** `tsnet.Server{Hostname, AuthKey, Dir: stateDir}` → `ListenTLS(":443")` → reverse-proxy to `target` (pdn's loopback HTTP). Optional **Funnel** (public exposure) is opt-in (`funnel: true`) and off by default.
- **Status readback:** the sidecar emits its assigned **MagicDNS FQDN** (and, if interactive auth is needed, the **login URL**) as JSON (stdout / a status file). pdn reads it to (a) surface "Tailscale: connected as `pdn.<tailnet>.ts.net`" + any pending login URL in the control panel, and (b) **suggest** the RP ID / `allowedOrigins` — the operator confirms; **pdn never silently changes the RP ID** (that would invalidate existing passkeys).

### Config (`management` / top-level `tailscale:` block)

```yaml
tailscale:
  enabled: false              # default off — pdn stays HTTP-only until opted in
  authKey: null               # a tailnet pre-auth key (first-join only); or:
  authKeyFile: null           # path to a 0600 file holding the key (preferred for secrets)
  hostname: pdn               # desired node name → pdn.<tailnet>.ts.net (actual name read back)
  tags: []                    # e.g. [tag:server] — a tailnet-owned node, right for an always-on box
  stateDir: /var/lib/packetnet/tsnet   # PERSISTENT — rejoin as the same node/cert across restarts
  target: 127.0.0.1:8080      # the loopback HTTP pdn serves
  funnel: false               # opt-in public exposure (vs tailnet-only)
```

### Onboarding (existing vs new Tailscale users)

- **Existing tailnet:** mint a pre-auth key (ideally `--tags=tag:server`, key-expiry disabled on the node), drop it in `authKeyFile` → the node joins their tailnet as `pdn.<tailnet>.ts.net`; their already-Tailscale'd devices reach it immediately. (An operator already running the *system* daemon may prefer to leave the sidecar off and use their own `tailscale serve`.)
- **New user:** no key → the sidecar prints a `login.tailscale.com` URL (surfaced in the UI/logs); the operator signs up (free SSO, creates the tailnet) and authorizes pdn, then installs the Tailscale app on each device they'll use. The per-device app install is intrinsic to Tailscale and unavoidable.
- **Operational notes (documented for the operator):** flip **"HTTPS Certificates" on** once in the admin console (free; required before any cert issues); **disable key expiry** on the pdn node so it stays up unattended; the **stateDir is load-bearing** for a stable hostname/cert (and thus stable passkeys). Renaming the node/tailnet changes the FQDN → invalidates existing passkeys.

### Free tier

Everything needed is on the free **Personal** plan: **MagicDNS**, the **Let's Encrypt cert + TLS termination** (`ListenTLS`), **Serve**, **Funnel** (opt-in), `tsnet`, auth keys, tags/ACLs. The client lib is BSD-licensed; the control plane is Tailscale SaaS (free) or self-hosted **Headscale**. (Free-tier *limits* — ≈100 devices / up to 3 users — far exceed a node + the operator's devices; Tailscale reshuffles plan limits over time, but the feature set above is not paywalled.)

## IMAP / SMTP posture (doc-only until the BBS arc)

Mail clients have no "secure context" notion — they care about **cert trust**:

- **LAN:** self-signed (one-time iOS "Trust" tap, or the parked `.mobileconfig`); plaintext is resisted by modern iOS — avoid.
- **Remote (tailnet):** the **real `.ts.net` cert** via `tailscale serve --tls-terminated-tcp` (system daemon) or the embedded node terminating IMAPS/SMTPS → **no prompt, no profile**. This is the everyday path (the phone is on the tailnet).
- **Net effect of retiring DNS-01:** we **keep** trusted-cert mail for tailnet devices (the realistic case, incl. Tom's phone) and **lose only** internet-public, no-VPN trusted mail — Funnel can't carry raw IMAP (it's HTTPS-port-restricted), so that becomes the operator's own public edge if ever needed.

## pdn code changes

1. **ForwardedHeaders** (`UseForwardedHeaders` for X-Forwarded-Proto/Host) on pdn's own pipeline — today only the app-gateway reads forwarded headers. Behind the sidecar's TLS, pdn sees HTTP; honoring the headers makes `pdn_at`'s `Secure` flag (`= Request.IsHttps`) correct and the request-derived WebAuthn origin `https://`. (WebAuthn already passes if `allowedOrigins` lists the `https://` origin — Fido2 checks the browser-reported origin — but this makes it clean.) Trust the headers only from the loopback sidecar.
2. **`isSecureContext` UI gate** — the passkey register/enroll UI is currently offered unconditionally; gate it on `window.isSecureContext` so plain-HTTP LAN hides passkeys and presents password (+TOTP) instead, degrading gracefully rather than erroring.
3. **`tailscale:` config** — the block above, parsed/validated; the `TailscaleSidecarHostedService` consuming it.
4. **Status surfacing** — control-panel: Tailscale connection state, the assigned FQDN, any pending login URL, and a "use this as your passkey hostname" RP-ID suggestion.
5. **Retire** the acme.sh/Cloudflare/DNS-01 recipe from docs + the config template; keep the optional self-signed/BYO HTTPS unchanged.

## Slice plan

- **S1 — HTTP-first foundation (no Go).** ForwardedHeaders + the `isSecureContext` passkey gate + the `tailscale:` config schema (parsed/validated, inert) + retire the DNS-01 recipe from docs/template + this ADR. Ships the posture without the Go build.
- **S2 — the embedded tsnet sidecar.** The Go module + CI `go build` + `build-deb.sh` staging + `TailscaleSidecarHostedService` + cert/serve + status readback + the UI surfacing. The structural slice (Go enters the build).
- **S3 — migrate the lab + retire DNS-01 for real.** Enable the sidecar on `packetdotnet`, join Tom's tailnet, set the RP ID to the `.ts.net` name, verify passkeys end-to-end over Tailscale; stop the `pdn.m0lte.uk` DNS-01 cert; update the cloudflare-token memory (superseded).

## Security posture

- The sidecar exposes **only** the one proxied port; Funnel (public) is opt-in and clearly flagged. A tailnet node is a real network identity governed by the operator's ACLs.
- The **auth key is sensitive** (first-join only) — `authKeyFile` at 0600, packetnet-owned; never logged. After first join the node identity lives in `stateDir`.
- ForwardedHeaders are trusted **only** from the loopback sidecar, not arbitrary clients (anti-spoof).

## Non-goals

- A native C# Tailscale client / P/Invoke `libtailscale` (sidecar chosen).
- Tier A (system-daemon orchestration) as a *first-class pdn feature* — supported only implicitly (pdn is HTTP behind any edge the operator runs).
- Funnel/public exposure by default.
- The self-signed CA + `.mobileconfig` smoothing ([#439](https://github.com/packet-net/packet.net/issues/439), parked).
- IMAP/SMTP implementation (the BBS arc; this ADR only fixes the posture).

## Cross-references

- The secure-context + self-signed analysis that led here: this session's discussion.
- TLS cert provider + HTTPS listener: `Packet.Node.Core/TlsCertificateProvider.cs`, `Program.cs` (`management.https`).
- The privilege seam precedent (unprivileged service + helpers): [`node-self-update-design.md`](node-self-update-design.md).
- Parked self-signed smoothing: [#439](https://github.com/packet-net/packet.net/issues/439).
</content>
