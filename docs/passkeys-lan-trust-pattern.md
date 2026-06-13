# Passkeys on a LAN-only node — trust & naming pattern (candidate)

*How a `pdn` node that people run on their own LANs — never exposed to the internet — can offer WebAuthn/passkeys for the web control panel. This is the open question the TLS work (auth part 1/4, [§17 2026-06-09](plan.md#17-amendment-log)) explicitly parked: self-signed TLS encrypts the password/JWT traffic, but a passkey ceremony needs a* ***trusted*** *secure context, which a self-signed cert on a LAN IP does not provide. This doc captures a viable answer.*

**Status:** **candidate pattern — NOT adopted, nothing built.** It informs the design of auth **part 3 (passkeys)** in the four-part auth arc (TLS · refresh tokens · passkeys · over-RF sysop TOTP). The decision to adopt, defer, or pick a different shape is still open and belongs to that part. The simpler local-dev flow in [§2](#2-the-simple-flow--start-here) is usable **today** and has no dependencies on the rest of this doc.

---

## 0. TL;DR

- Two browser constraints make "just open `https://<lan-ip>`" impossible for passkeys: WebAuthn requires a **secure context** (a publicly-trusted cert), **and** the **RP ID must be a registrable domain — never an IP address**. A self-signed cert fails the first; a bare IP fails the second even with a valid cert. ([§1](#1-the-two-hard-constraints))
- **`localhost` is exempt from both** — plain HTTP on `localhost` is a secure context and `localhost` is a legal RP ID. So for **same-machine dev there is nothing to build**: bind the panel to loopback and passkeys work today. ([§2](#2-the-simple-flow--start-here))
- **For a lab box you reach from a phone** (deployed `pdn`, iPhone on the WLAN), loopback doesn't apply — but you don't need the full pattern either: issue a **real wildcard cert via DNS-01** (Cloudflare + Let's Encrypt, a domain you already have), drop it into the existing `management.https` bring-your-own-cert slot, and resolve `pdn.lab.<domain>` to the box's LAN IP. Trusted on iOS with **no profile install**. This is the recommended dev / single-operator path. ([§2a](#2a-a-box-you-control-reachable-from-phones--the-dev--single-operator-path))
- For **distribution to strangers' LANs**, the candidate is **`B + mDNS`**: a vendor-owned domain + a call-home ACME service issues a real, per-install **wildcard cert** (the private key never leaves the box); **mDNS** gives zero-config discovery; a **stable per-install subdomain** is the WebAuthn RP ID so passkeys survive the box's LAN IP changing. ([§3](#3-the-distribution-pattern--b--mdns))
- The one unavoidable residual risk is **DNS-rebinding protection** blocking a public name that resolves to a private IP on some networks; mitigations in [§5](#5-the-residual-risk--dns-rebinding).
- This **supersedes nothing**. The self-signed TLS that already shipped (#357) keeps earning its keep encrypting password/token traffic regardless of which passkey path (if any) is adopted.

---

## 1. The two hard constraints

These are the whole reason the naming/cert design is non-trivial. Both are enforced by browsers; neither is negotiable.

1. **WebAuthn requires a secure context.** `navigator.credentials.create()/.get()` only run in a "potentially trustworthy" origin: HTTPS with a cert the browser trusts, **or** a loopback origin (`localhost` / `127.0.0.1` / `::1`), which browsers treat as secure even over plain HTTP. So `http://192.168.x.y` is out, and `https://192.168.x.y` with our *self-signed* cert is out unless that cert has been trust-installed on the client (the thing we are trying to avoid having every user — and every phone — do).
2. **The RP ID must be a registrable domain, never an IP literal.** The Relying Party ID is the scope a passkey is bound to; the spec requires it to be a valid domain string and a registrable suffix of the page's origin. An IP address is not a valid domain string, so **`https://192.168.x.y` cannot do WebAuthn even with a perfectly valid cert** — there is no legal RP ID to use.

**Consequence:** every install that wants passkeys needs a real **domain name** and a **trusted cert** for it. That is the SSL problem from the TLS slice, now mandatory and *per-install*. Everything below is about delivering that to a box on someone else's LAN without them touching DNS or installing a root CA.

---

## 2. The simple flow — start here

For local development and any single-machine use, the constraints above collapse to nothing, because **loopback is exempt**:

- Serve the panel on `http://localhost:<port>` (or `127.0.0.1`). It is a secure context with **no cert at all**.
- Set the WebAuthn **RP ID = `localhost`** and the expected **origin = `http://localhost:<port>`** in `Fido2NetLib`'s config.
- Register and authenticate a passkey. It works today, in any modern browser, with zero DNS, zero mDNS, and zero certificate machinery.

This is what Slice-3 passkey development should use: build and exercise the full `Fido2NetLib` register/verify flow against `localhost` first, where none of the distribution complexity exists. The shipped self-signed TLS (`management.https`, [#357](plan.md#17-amendment-log)) is orthogonal — it secures the password/JWT on the LAN for *non-passkey* access and remains useful regardless.

The realistic ladder, simplest first:

| Tier | When | Cert | Name / RP ID | Cost |
|---|---|---|---|---|
| **localhost** | dev, single-box, same-machine access | none needed | `localhost` | **zero** — do this now |
| **trust-installed self-signed / `mkcert`** | a LAN box whose *clients you control* | the shipped self-signed cert (or `mkcert`), trust-installed per device | `pdn.local` or the box's name | one root install **per device** — bad for phones; fine for a couple of dev machines |
| **real wildcard via DNS-01** ([§2a](#2a-a-box-you-control-reachable-from-phones--the-dev--single-operator-path)) | **a box *you* control, reached from any device incl. phones** | real LE wildcard, DNS-01 — trust-installed **nowhere** | `pdn.lab.<your-domain>` | own a domain on a DNS-01 provider (Cloudflare); manual issue/renew |
| **`B + mDNS`** ([§3](#3-the-distribution-pattern--b--mdns)) | distribution to strangers, incl. phones | real per-install LE wildcard | stable `<id>.<vendor-domain>` | a small vendor-hosted control plane |

The jump from tier 2 to tier 4 is exactly "stop making every user install a root CA." **Tier 3 is the same trusted-cert outcome as tier 4, done by hand for one box you control — and it is the right answer for a dev lab or any operator who controls their own domain + DNS. `B + mDNS` (tier 4) is essentially tier 3 *automated* for the audience that can't do DNS themselves.** If passkeys are only ever needed by the sysop on the same box, **tier 1 is the whole answer** and tiers 3–4 may never be needed.

### 2a. A box you control, reachable from phones — the dev / single-operator path

This is the right tier for the `pdn` **dev lab** (`root@pdn-lab`) and for any self-hoster who controls a domain — including the iPhone-on-WLAN case. It is the manual version of [§3](#3-the-distribution-pattern--b--mdns): no control plane, no mDNS, no IP-encoding, and **no new node code** — the shipped `management.https` already accepts a bring-your-own PKCS#12 (`CertificatePath` + `CertificatePassword`, [#357](plan.md#17-amendment-log)).

Recipe (one-time, on a domain you have on Cloudflare):

1. **Issue a wildcard via DNS-01.** `*.lab.<your-domain>` from Let's Encrypt, validated by a Cloudflare API TXT write (scoped token: `Zone → DNS → Edit` for that one zone). DNS-01 is required for wildcards and works regardless of the box being internet-unreachable. One cert then covers `pdn.lab.<your-domain>` *and* every future LAN dev service.
2. **Point the cert at the node.** Convert to PKCS#12 and set `management.https.certificatePath` / `certificatePassword` in the conffile; the node serves it on 8443. (Renewal is manual every ~90 days for now — re-issue, drop in the new `.pfx`, restart. Fine for a dev box; automatable later.)
3. **Resolve the name to the box's LAN IP** so the iPhone on the WLAN can reach it — two choices:
   - **Split-horizon (recommended if you run Pi-hole / AdGuard / Unbound / a router-local zone — likely):** add a *local* record `pdn.lab.<your-domain>` → the box's LAN IP (e.g. `10.x.x.x`); keep **only** the ACME TXT in public Cloudflare. The iPhone uses the WLAN's resolver and gets the private IP; nothing about the LAN is published, and there is no DNS-rebinding exposure.
   - **Grey-cloud A record (zero extra infra):** a Cloudflare "DNS only" A record `pdn.lab.<your-domain>` → the private IP. Simplest, but it publishes the private IP and can be stripped by DNS-rebinding protection — on your own WLAN you control the router, so allowlist the domain if needed.
4. **WebAuthn:** `rp.id = "pdn.lab.<your-domain>"`, expected origin `https://pdn.lab.<your-domain>:8443`. Because the cert is publicly-trusted and the name is a real domain, **iOS Safari does the passkey ceremony with no profile install and no per-device trust toggle.**

Why this matters specifically for the **iPhone**: a *self-signed* cert (tier 2) on iOS is genuinely painful — you must install a configuration profile and then manually flip **Settings → General → About → Certificate Trust Settings** for that root before any secure-context API will run. A real Let's Encrypt cert is already in the iOS trust store, so all of that vanishes. (iCloud Private Relay is not a problem here: it does not proxy connections to local-network / private-IP destinations, so it connects directly.) Web passkeys in Safari need only the RP-ID-is-a-real-domain + trusted-cert conditions met — the Associated-Domains / `apple-app-site-association` machinery is for *native-app* credential sharing, not a pure web passkey.

---

## 3. The distribution pattern — `B + mDNS`

The name says "B + mDNS" but there is one fact that decides the whole shape: **mDNS only resolves `.local`, and a publicly-trusted cert cannot be issued for `.local`.** Clients route only `*.local` queries to multicast (a query for `id.vendor.io` always goes to unicast DNS), and CAs are forbidden from issuing for the reserved `.local` TLD. So the cert name and the mDNS name **cannot be the same hostname** — this is a **two-name** design, each name doing a different job:

| Name | Job | Trust |
|---|---|---|
| `<id>.<vendor-domain>` (e.g. `abcd1234.nodes.example`) | TLS origin **and** the WebAuthn **RP ID** | publicly-trusted LE cert via call-home ACME |
| `<box>.local` (e.g. `pdn-abcd.local`) | zero-config **discovery** + IP-change detection | none — plain-HTTP landing/redirect only |

mDNS gets a user (or the app) *to* the box with no configuration; the public name is where TLS terminates and the passkey ceremony actually happens.

### Names, borrowing the Plex/`sslip.io` trick so there are no per-device DNS writes

- **RP ID (stable for the life of the install):** `abcd1234.nodes.example` — a random per-install label. This is the value passed to `Fido2NetLib` as `rp.id`. It **never changes**, so passkeys survive the box's LAN IP changing.
- **Serving origin (encodes the current LAN IP):** `https://10-0-0-5.abcd1234.nodes.example`. The hyphenated-IP label is decoded to an A record by the vendor DNS (this is exactly what `sslip.io`/`nip.io` do; the vendor runs its own instance for its zone). One generic wildcard rule resolves *any* IP-encoded label — **no dynamic per-device A-record updates**.
- **Discovery name:** `pdn-abcd.local`, advertised over mDNS.

### Cert — call-home ACME, key stays on the box

A **wildcard `*.abcd1234.nodes.example`** issued by **DNS-01** through a thin vendor-hosted ACME proxy. The box generates its keypair locally, authenticates to the proxy with a per-device provisioning token, the proxy writes the `_acme-challenge` TXT in the vendor zone, Let's Encrypt issues, and the cert comes back. **The private key never leaves the box.** Because it is a wildcard, an IP change (which changes the serving origin's IP-encoded label) needs **no reissue**. This is a natural extension of the existing `TlsCertificateProvider` seam — a second provider implementation alongside "supplied PKCS#12" and "self-signed."

### Bootstrap flow

1. User opens `http://pdn-abcd.local` (mDNS, plain HTTP — no passkeys here, just a redirect, so no cert needed).
2. The box knows its own current LAN IP, so it `302`-redirects to `https://10-0-0-5.abcd1234.nodes.example`.
3. That origin presents the trusted wildcard cert → secure context → the passkey register/login ceremony runs against **RP ID `abcd1234.nodes.example`**. ✅

### On an IP change

The box notices locally, re-advertises mDNS, and future redirects use the new IP-encoded label. The wildcard cert still covers it and the **RP ID is unchanged**, so **existing passkeys keep working untouched** — the failure mode you get if you naively put the IP in the RP ID.

```
                         vendor control plane (you host)
                         ┌───────────────────────────────┐
   call-home ACME  ──────►  ACME proxy (DNS-01 TXT writer) │ ── Let's Encrypt
   (per-device token)     │  + sslip-style wildcard DNS    │
                         └───────────────────────────────┘
                                      ▲ resolves *.abcd1234.nodes.example
                                      │ (IP-encoded label → that IP)
   browser ──http──► pdn-abcd.local ──302──► https://10-0-0-5.abcd1234.nodes.example
            (mDNS, discovery)                 (trusted cert; RP ID = abcd1234.nodes.example)
```

A free property falls out: each install has its **own** RP ID, so passkeys are naturally **isolated per node** — exactly right for self-hosted boxes.

---

## 4. WebAuthn wiring specifics (`Fido2NetLib`)

The one subtlety when the serving origin and the RP ID differ:

- `rp.id` = the **stable** suffix, `abcd1234.nodes.example`. Not the IP-encoded origin.
- The **expected origin** passed to registration/assertion verification must be the **actual serving origin** the browser used — `https://10-0-0-5.abcd1234.nodes.example` — which changes with the IP. So the server must accept the *current* IP-encoded origin (or validate that the origin is `https://<anything>.abcd1234.nodes.example`), while pinning `rp.id` to the stable suffix. RP ID being a registrable suffix of the origin is what makes the credential valid across origin changes.
- In dev (tier 1), both are loopback: `rp.id = "localhost"`, expected origin `http://localhost:<port>`.

This origin-vs-RP-ID split is the single most error-prone part of the implementation and the reason the dev flow ([§2](#2-the-simple-flow--start-here)) should be nailed first, where they coincide.

---

## 5. The residual risk — DNS rebinding

The serving name is a **public name that resolves to a private (RFC1918) IP**, which is precisely the shape **DNS-rebinding protection** blocks — `dnsmasq stop-dns-rebind`, Fritz!Box, Pi-hole/AdGuard, NextDNS, some ISP resolvers. mDNS does **not** rescue this, because phones won't route a `*.nodes.example` query to multicast. On a network with rebind protection on, the redirect target simply won't resolve.

Mitigations, cheapest first:

1. **Document an allowlist** for the vendor domain (most routers/resolvers can exempt one domain). Covers the majority.
2. **Detect it client-side** — when the redirect target fails to resolve, show a specific "your network is blocking this, here is the one setting to change" page instead of a generic browser error.
3. **Local-resolver fallback** — the box answers DNS authoritatively for its own name on the LAN, for locked-down networks. More setup, bulletproof.

This risk is inherent to *any* "trusted cert on a private IP" scheme (it is not specific to this design); it must be surfaced, not hidden.

---

## 6. What it would cost to build

**Vendor-hosted (one small service, shared by all installs):**
- An authenticated **ACME proxy**: per-device token → DNS-01 TXT write in the vendor zone → return the LE-issued wildcard. Scope each token so an install can only touch its **own** label.
- An **`sslip.io`-style DNS server** for `*.nodes.example` that decodes the IP-encoded label to an A record. (`sslip.io` is open source.)

**In the shipped `pdn` binary:**
- An **ACME client** driving issuance/renewal through the proxy — a new `TlsCertificateProvider` implementation behind the existing seam.
- An **mDNS responder** advertising `pdn-<id>.local`.
- **Local-IP detection** + the `http://*.local` → `https://<ip-encoded>.<id>.nodes.example` **redirect**.
- WebAuthn RP pinned to the stable `<id>.nodes.example` ([§4](#4-webauthn-wiring-specifics-fido2netlib)).

Default-off and opt-in, matching the auth-arc discipline: a node that never opts into managed naming behaves exactly as today (self-signed or HTTP).

---

## 7. Alternatives considered

| Option | Why not (for distribution) |
|---|---|
| **Private CA shipped + installed (`mkcert`/`step-ca`)** | Works on machines you configure, but every client — **especially phones** — must install the root. That is the support/security cost this pattern exists to avoid. Fine as tier 2 for a couple of dev machines. |
| **Tailscale `tsnet` overlay** | Genuinely easy "it just works" (real LE cert + MagicDNS name, passkeys work with near-zero code), but **requires every user to run Tailscale**. Reasonable if that dependency is acceptable to the audience; heavy if not. Worth reconsidering if a fleet/overlay story emerges. |
| **Cloudflare Tunnel / any reverse-tunnel** | Terminates TLS at an edge and **exposes the panel beyond the LAN** — the opposite of the LAN-only requirement, plus an external dependency on the data path. |
| **Shared public-key cert (`traefik.me`, public `*.nip.io` certs)** | The private key is public, so anyone can MITM the login. **Fatal for passkeys.** Per-install issuance ([§3](#3-the-distribution-pattern--b--mdns)) is the real version. |

---

## 8. Open questions / decision gates (for auth part 3)

- **Is distribution-grade passkey support even in scope for v1?** If passkeys are only ever for the local sysop, **tier 1 (`localhost`) is the entire answer** and §3–§7 can stay parked.
- **Do we want to run a vendor control plane at all?** `B + mDNS` requires a small always-on hosted service (ACME proxy + DNS). That is a real operational commitment for an otherwise zero-backend, self-hosted project.
- **Domain.** Which of the available domains becomes `<vendor-domain>` for node naming, and does it want to be a dedicated zone (e.g. `nodes.<domain>`) to isolate the wildcard DNS.
- **mDNS on .NET.** Library choice for the responder (e.g. a `Makaretu.Dns`-style multicast responder) and its behaviour across the OSes operators run.
- **Relationship to the other auth parts.** Passkeys (part 3) layer on TLS (part 1, shipped) and the JWT/refresh-token work (part 2); this doc only addresses the *trust + naming* substrate passkeys need, not the credential lifecycle.
