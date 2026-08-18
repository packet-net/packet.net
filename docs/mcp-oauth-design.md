# MCP remote auth - OAuth 2.1 design (the hosted claude.ai connector)

**Status:** **implemented behind the default-off `mcp.oauth.enabled` flag, 2026-06-13** (slices 1-4 + revoke; see § Implementation slices for what's deferred). The "hosted claude.ai connector" path from [`mcp-design.md` § Deployment](mcp-design.md) - the one reachability option that needs more than config. The other three (LAN-direct / Tailscale / public-HTTPS) work today with a node-issued **JWT bearer** header on `/mcp`; this doc is about letting the **claude.ai web app** connect as a *custom connector*, which follows the MCP authorization spec (OAuth 2.1). **Security-critical - the code merged dormant behind the flag; review before ENABLING it in production (cf. the WebAuthn review).**

> **Implementation note (2026-06-13).** Live in `PdnOauthApi` + the `Packet.Node.Core.Auth.Oauth` stores. Endpoints return 404 unless `mcp.oauth.enabled`. **One remaining deviation from the design below (documented follow-up):**
> 1. **Access-token-only, no refresh token.** The connector re-runs the authorize flow on expiry (`mcp.oauth.accessTokenLifetimeMinutes`, default 60). Refresh-token rotation (reusing `RefreshTokenService`) + a meaningful `/oauth/revoke` are the next slice; revoke currently answers RFC-7009-style 200 but can't kill a live JWT (rotate the signing key to invalidate all).
>
> **Audience segregation - done (build step 10, security-review remediation).** MCP credentials (the OAuth connector token *and* the static `/api/v1/mcp/token` bearer) are minted on a dedicated `JwtTokenService.McpAudience` (`packet.net-mcp`). The JwtBearer middleware authenticates both audiences; the `read`/`operate`/`admin` policies pin the control-API audience and the new `pdn-mcp` policy pins the MCP audience, so an MCP token can't reach `/api/v1` and a control-API token can't reach `/mcp` - exactly as the design below specifies. Scopes still cap MCP at `operate` (no admin). The build-step-10 entry in `plan.md` §17 covers the rest of the remediation (the `oauth⇒auth` config guard, the login-timing fix, RFC 9207 `iss`).

## What the MCP spec requires

The MCP authorization spec (2025-06-18) makes the MCP server an **OAuth 2.1 resource server**; a client with no prior config discovers how to get a token and runs a standard authorization-code + PKCE flow:

1. Client hits `/mcp` unauthenticated → `401` with `WWW-Authenticate: Bearer resource_metadata="…/.well-known/oauth-protected-resource"`.
2. **Protected Resource Metadata** (RFC 9728): `GET /.well-known/oauth-protected-resource` → the resource id + the authorization server(s).
3. **AS Metadata** (RFC 8414): `GET /.well-known/oauth-authorization-server` → `authorization_endpoint`, `token_endpoint`, `registration_endpoint`, supported scopes, `code_challenge_methods_supported: ["S256"]`.
4. **Dynamic Client Registration** (RFC 7591): `POST /oauth/register` → claude.ai registers itself, gets a `client_id` (public client, no secret - it's a SPA-style client, so PKCE is the proof).
5. **Authorize** (OAuth 2.1 code + PKCE): `GET /oauth/authorize?response_type=code&client_id=…&redirect_uri=…&code_challenge=…&code_challenge_method=S256&scope=…&resource=…&state=…` → the node authenticates the **owner** (reusing the panel login / passkey), shows a **consent** screen, redirects back with a single-use `code`.
6. **Token**: `POST /oauth/token` (`grant_type=authorization_code`, `code`, `code_verifier`, `redirect_uri`, `client_id`) → an **access token** (+ optional refresh token).
7. Client calls `/mcp` with `Authorization: Bearer <access token>`; the RS validates it (signature, expiry, **audience = this resource**, scope).

## The decision: the node is its own Authorization Server

A pdn node is a single process that already **owns identities** (Argon2id users + WebAuthn passkeys in `SqliteUserStore`), **mints + validates JWTs** (`JwtTokenService`, HS256 off the per-node signing key), and **has a login/consent surface** (the React panel). So it is its own AS + RS - no external IdP, no extra moving parts, and it reuses everything the panel auth already shipped. (Delegating to an external AS is possible but pointless for a self-contained LAN node.)

### Reuse map (what already exists)

| Need | Reuse |
|---|---|
| User identity + credential check | `SqliteUserStore` + WebAuthn/passkey ceremony (`PdnWebAuthnApi`) + Argon2id login (`PdnAuthApi`) |
| Access-token minting/validation | `JwtTokenService` (HS256, per-node key) - **extended** to stamp an MCP audience + MCP scope |
| Refresh tokens (long-lived claude.ai session) | `RefreshTokenService` + `IRefreshTokenStore` (rotation + reuse-detection already built) |
| Brute-force protection | `LoginThrottle` |
| Audit | the new `IAuditLog` (register/authorize/token/revoke events) |
| Persistence | `pdn.db` (new tables: `oauth_client`, `oauth_code`) - same resilient store pattern |

## Token model

The access token stays an **HS256 JWT off the same signing key**, but **audience-segregated** so an MCP token and a panel token are not interchangeable:

- Panel API token: `aud = packet.net-control-api` (today, unchanged).
- MCP token: `aud = <the MCP resource id>` (the `/mcp` URL, per RFC 8707 resource indicators).
- `scope` claim maps to the existing hierarchy: OAuth scopes `mcp:read` / `mcp:operate` → the node's `read` / `operate`. (No `admin` over MCP - administration stays panel-only.)
- The `/mcp` bearer validation accepts **only** the MCP audience; `JwtBearer` for `/api/v1/*` accepts only the control-API audience. A token minted for one can't be replayed at the other.

`JwtTokenService.Issue` gains an audience + scope parameter (defaulting to the current control-API values, so existing call sites are unchanged).

## Endpoints (all under the existing web listener, default-off behind `mcp.oauth.enabled`)

```
GET  /.well-known/oauth-protected-resource     RFC 9728 — RS metadata (public)
GET  /.well-known/oauth-authorization-server    RFC 8414 — AS metadata (public)
POST /oauth/register                            RFC 7591 DCR (public, rate-limited)
GET  /oauth/authorize                           code+PKCE; requires owner login + consent
POST /oauth/token                               code→token (PKCE verify); refresh grant
POST /oauth/revoke                              RFC 7009 token revocation
```

## Threat model / hardening (the review surface)

- **PKCE S256 mandatory** - public client, no secret; reject missing/`plain` challenge.
- **Exact `redirect_uri` match** against the registered set; no wildcards; pre-registered at DCR.
- **Authorization codes**: single-use (atomic consume, like the WebAuthn challenge cache), short TTL (≤60 s), bound to `client_id` + `code_challenge` + `redirect_uri` + the authenticated user.
- **Audience binding** (above) - the MCP token can't hit the panel API and vice-versa.
- **Consent is explicit** and shows the requested scopes + the client's declared name/URI; the owner must be logged in (passkey/password) to approve.
- **HTTPS required** for the remote flow (the listener's TLS); refuse the OAuth endpoints over plain HTTP unless loopback.
- **DCR is open but rate-limited** (the spec expects open registration for clients like claude.ai); optionally gate behind an owner toggle. Registered clients are persisted + listable/revocable in the panel.
- **Refresh-token rotation + reuse-detection** - reuse the shipped `RefreshTokenService`.
- **Everything audited** via `IAuditLog` (register, authorize-grant, token-issue, revoke), actor = the consenting owner, source = `oauth`.
- **Default-off**; loopback + the bearer-header path (Claude Code) are unaffected when OAuth is disabled.

## Implementation slices

1. **Discovery** - `/.well-known/oauth-protected-resource` + `/.well-known/oauth-authorization-server` + the `401 + WWW-Authenticate` on `/mcp`. Low-risk, spec-mechanical, fully testable. Unblocks a client's discovery.
2. **DCR** - `POST /oauth/register` + the `oauth_client` store (persist, rate-limit, panel list/revoke).
3. **Authorize** - `GET /oauth/authorize` reusing the panel login/passkey + a consent screen; the `oauth_code` store (single-use, TTL, bound).
4. **Token** - `POST /oauth/token` (code+PKCE→audience-bound MCP JWT + refresh); `JwtTokenService` audience/scope extension; `/mcp` accepts the MCP audience.
5. **Revoke + harden + review** - `POST /oauth/revoke`, the panel "connected apps" screen, rate-limits, and the security review (its own `docs/mcp-oauth-review-*.md`, like WebAuthn).

**Status of the slices (2026-06-13; the audience claim corrected 2026-08-17):** 1 ✅ (discovery + the `WWW-Authenticate` hint on `/mcp`), 2 ✅ (DCR + persisted `oauth_client` store), 3 ✅ (authorize + server-rendered login/consent + single-use `oauth_code` store), 4 ✅ *partial* (token: code+PKCE→JWT, minted on the dedicated MCP audience `JwtTokenService.McpAudience` = `packet.net-mcp`, so a connector token reaches `/mcp` only and never the wider control API; **audience segregation shipped with this slice**. What is missing is **refresh**: the token response carries `access_token`/`token_type`/`expires_in`/`scope` only, and `grant_type` is restricted to `authorization_code`, so a connector re-runs authorize on expiry), 5 ⬜ (revoke is a stub-200: the MCP access token is a stateless JWT and cannot be individually revoked, so per-token revocation rides the refresh follow-up. The panel "connected apps" screen, refresh-token rotation, and the formal security review are the remaining work). All merged **dormant behind `mcp.oauth.enabled`**. **Review before enabling in production.** Server-rendered consent does **password** login only for now (passkey-in-consent is a follow-up; passkeys remain on the panel).

## Out of scope

- An external/upstream IdP (the node is self-contained).
- `admin`-over-MCP (administration stays panel-only; MCP tops out at `operate`).
- Replacing the bearer-header path - Claude Code keeps using a node-minted token directly; OAuth is additive, for clients that require it.
