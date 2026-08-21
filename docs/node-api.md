# The node control API: where the truth lives

The pdn node serves its operator control API from the same Kestrel listener as the
control panel, under `/api/v1`, on `0.0.0.0:8080` by default with authentication on.
This page is the index to it.

There is deliberately **no hand-written OpenAPI document**. There used to be
(`docs/node-api.yaml`); it was deleted on 2026-08-17. It had been written the other way
round from the way an API document is supposed to work, derived from the web UI's typed
client rather than from the server, and by the time of the 2026-08-16 code review it
documented ten operations that had no route behind them (`GET /ports/{id}`,
`POST /ports/{id}/tune`, `PATCH /config`, `GET /config/schema`, `POST /config/validate`,
`GET|PUT /beacons`, two WebAuthn paths and a per-user TOTP path), omitted roughly forty
routes that existed, including the entire OAuth, app-platform, console, audit and system
surfaces, and got every schema that was spot-checked wrong. `GET /setup/state` was
documented as returning `{setupComplete}` where the server returns `{needsSetup}`: the
opposite polarity under a different name.

That was not a stale file so much as a symptom. The review's root-cause finding was that
the server model had four hand-maintained mirrors (`types.ts`, `mock.ts`, the ports
editor and that YAML), none of them enforced, and that new work extended some and forgot
others. Adding a fifth copy of every schema by hand would have re-created the same
failure a year later. So the surface below is generated from the running node, and the
shapes are read from the types that define them.

## The four places to look

1. **The route inventory below.** Generated from the node's own `EndpointDataSource`:
   every path this build maps, its verbs, and the scope policy each carries. A guard test
   (`tests/Packet.Node.Tests/Integration/RouteInventoryTests.cs`) fails if the file and
   the code disagree, so a phantom endpoint cannot come back.
2. **The C# request and response records.** These *are* the schemas. They live next to
   the handlers in `src/Packet.Node/Api/Pdn*Api.cs`, with the shared read models in
   `src/Packet.Node.Core/` (config under `Configuration/NodeConfig*.cs`, notably
   `PortConfig` and the `kind`-discriminated `TransportConfig` union). Every one carries
   XML doc comments describing units and absence semantics.
3. **The contract fixtures** ([#692](https://github.com/packet-net/packet.net/issues/692)):
   captured server responses that the web client's types are checked against, so a
   server-side shape change fails a test rather than a screen.
4. **The typed client**, `web/packetnet-ui/src/lib/types.ts` and `api.ts`. Useful for
   seeing how a real consumer calls the API, but it is a consumer: when it disagrees with
   the C# records, the C# records are right.

## Conventions that hold across the surface

- **Base path** `/api/v1`. `GET /healthz` and `GET /metrics` sit outside it, at the root.
- **JSON dialect**: camelCase; enums as their string member names; `TimeSpan` values as a
  number of seconds. One `JsonSerializerOptions` (`NodeConfigJson`) is used for both the
  HTTP surface and the config blob in `pdn.db`, so the wire and the stored bytes cannot
  drift apart. The legacy integer-enum and `"hh:mm:ss"` forms are still *read*.
- **Auth**: one scope per user, hierarchical `read` then `operate` then `admin`. The
  policy on each route is in the table below. Tokens ride the `Authorization` header; the
  scope gate passes everything through when `management.auth.enabled` is off.
- **Audience**: a token is bound to an audience as well as a scope, and the two are
  checked separately. Everything under `/api/v1` requires `packet.net-control-api`;
  `/mcp` requires `packet.net-mcp`. A token minted for one is refused by the other, and
  the refusal is reported as a 403 (which reads as a scope problem, so check the audience
  first). Login and refresh issue control-API tokens; `POST /api/v1/mcp/token` issues an
  MCP one.
- **Service tokens**: `POST /api/v1/auth/service-token` (admin) mints a long-lived
  **control-API** bearer for a headless caller, with `sub` = `service:<name>`. Body:
  `{ "name": "mcp-bridge", "scope": "read" | "operate" | "admin", "days": 90 }` - `scope`
  defaults to `read` and may not exceed the minting admin's own, `days` defaults to 90 and
  is clamped to 365. The mint is audited (`service_token`). This is the credential the
  `pdn mcp` stdio bridge wants in `PDN_NODE_TOKEN`, because that bridge drives the REST
  control API rather than `/mcp`. Like every node-minted JWT it is stateless and cannot be
  revoked individually: `pdn auth rotate-signing-key` plus a restart invalidates all of
  them.
- **SSE**: the streaming routes are marked in the table. They also accept
  `?access_token=`, because a browser `EventSource` has no header API. That permission is
  endpoint metadata (`AcceptsQueryAccessToken`), not a path list, so a new feed either
  carries it or does not get it.
- **Errors**: a plain `{ "error": "..." }` for most failures, and an RFC 7807
  `ValidationProblem` (422) where a specific field is at fault, so a caller can render the
  message against the field that caused it.
- **Concurrent writes**: `GET /api/v1/config` serves an `ETag`; the config and port writes
  accept it as `If-Match` and answer 412 on a mismatch. No header means last writer wins,
  as before.
- **Dry runs**: `POST` and `PUT /api/v1/ports` take `?dryRun=true`, which returns the
  reconcile plan (what would restart) without applying it.
- **Port state**: `GET /api/v1/ports`, `GET /api/v1/ports/{id}` and the lifecycle responses
  all carry the SAME `PortStatus`, projected once from the supervisor's per-port state model
  (packet-net/packet.net#722). `state` is one of `configured` (in config, not attempted yet or
  between a teardown and its bring-up), `disabled`, `starting`, `up`, `degraded` (serving,
  with a non-data-path component missing), `faulted`, `retrying` (faulted with a
  bounded-backoff bring-up retry armed) or `stopping`. `up` and `degraded` are the serving
  states. `lastError` says why the port last failed or died - it is retained after a recovery,
  so a port that is up again still shows what happened - and `degraded` lists the missing
  components (`radio` / `rig` / `rigctld` / `transport`). `since` is when the port entered its
  current state. A reconcile is asynchronous, so the status returned straight after a
  `lifecycle up` may still read `configured` or `starting`: that is honest, not a failure.
- **Connect-out (`POST /api/v1/sessions`) and `portId`**: naming a port is a **direct AX.25
  dial on that port**, the same as the console's `C <port> <call>`, and is never NET/ROM
  wrapped. **Omitting `portId` is how you ask the node to route**: it resolves its default
  connector, which is NET/ROM-wrapped when NET/ROM connect routing is on, so an alias or a
  distant destination goes over the network. Send `portId` only when a direct dial on that
  specific port is what you want; a client that always sends one cannot reach a NET/ROM
  alias at all. With no `portId` the dial leaves on the first ENABLED and SERVING port in
  **configuration order** - the same order the console's `PORTS` numbers and `C <n> <call>`
  addresses, and the order `/ports` and `/sessions` are returned in. There is one port
  ordering on the node and this is it (packet-net/packet.net#723).
- **Session ids are opaque, and carry the full session key**: `{portId}:{peer}` for a circuit
  to the node's own callsign, `{portId}:{peer}>{local}` for one to an application callsign the
  node binds. The AX.25 engine keys a session on `(local, remote)` - one station can hold a
  link to the node console and another to a bound application at the same moment - so the id
  has to carry both halves for `DELETE /sessions/{id}`, `/send` and `/stream` to address the
  right one. **Pass the id back verbatim; do not parse it.** `SessionInfo.local` is the field
  to render if you want to show which identity a caller reached.

## The route inventory

Generated from `EndpointDataSource` with every conditional surface enabled (`/mcp` is
mapped only when `mcp.enabled` and `mcp.sse.enabled` are both set; the OAuth routes are
always mapped but every handler 404s unless `mcp.oauth.enabled`). `anonymous` means the
route carries no scope policy, which for the bootstrap endpoints is the point: a node with
no users has to be able to answer the setup wizard and a login. Two of those endpoints carry
their own gate in the handler instead of a policy: `POST /setup` and `GET /setup/devices` (the
wizard's modem picker, which enumerates the node's serial devices and identifies the NinoTNCs
among them) both refuse with 403 once any user exists, so the window they are open in is exactly
the window in which the node is unclaimed.

To regenerate after adding or moving a route: `scripts/update-node-api.sh`.

<!-- BEGIN generated route inventory -->

| Method | Path | Scope |
|---|---|---|
| GET | `/.well-known/oauth-authorization-server` | anonymous |
| GET | `/.well-known/oauth-protected-resource` | anonymous |
| GET | `/api/v1/apps` | `read` |
| GET | `/api/v1/apps/available/` | `read` |
| POST | `/api/v1/apps/available/{id}/install` | `admin` |
| GET | `/api/v1/apps/packages/` | `read` |
| POST | `/api/v1/apps/packages/upload` | `admin` |
| POST | `/api/v1/apps/packages/{id}/disable` | `admin` |
| POST | `/api/v1/apps/packages/{id}/enable` | `admin` |
| PUT | `/api/v1/apps/packages/{id}/identity` | `admin` |
| POST | `/api/v1/apps/packages/{id}/restart` | `admin` |
| POST | `/api/v1/apps/packages/{id}/uninstall` | `admin` |
| GET | `/api/v1/audit` | `admin` |
| POST | `/api/v1/auth/login` | anonymous |
| POST | `/api/v1/auth/logout` | anonymous |
| POST | `/api/v1/auth/refresh` | anonymous |
| POST | `/api/v1/auth/service-token` | `admin` |
| DELETE | `/api/v1/auth/totp/enroll` | `read` |
| GET | `/api/v1/auth/totp/enroll` | `read` |
| POST | `/api/v1/auth/totp/enroll/begin` | `read` |
| POST | `/api/v1/auth/totp/enroll/complete` | `read` |
| POST | `/api/v1/auth/webauthn/assert/begin` | anonymous |
| POST | `/api/v1/auth/webauthn/assert/complete` | anonymous |
| GET | `/api/v1/auth/webauthn/credentials` | `read` |
| DELETE | `/api/v1/auth/webauthn/credentials/{id}` | `read` |
| POST | `/api/v1/auth/webauthn/register/begin` | `read` |
| POST | `/api/v1/auth/webauthn/register/complete` | `read` |
| GET | `/api/v1/capabilities` | `read` |
| DELETE | `/api/v1/capabilities/{id}` | `operate` |
| GET | `/api/v1/config` | `read` |
| PUT | `/api/v1/config` | `operate` |
| GET | `/api/v1/config/raw` | `read` |
| PUT | `/api/v1/config/raw` | `operate` |
| POST | `/api/v1/console` | `admin` |
| DELETE | `/api/v1/console/{id}` | `admin` |
| POST | `/api/v1/console/{id}/input` | `admin` |
| GET | `/api/v1/console/{id}/stream` | `admin` (SSE, also takes `?access_token=`) |
| GET | `/api/v1/events` | `read` (SSE, also takes `?access_token=`) |
| GET | `/api/v1/heard` | `read` |
| GET | `/api/v1/links` | `read` |
| GET | `/api/v1/log` | `read` |
| POST | `/api/v1/mcp/token` | `admin` |
| GET | `/api/v1/modems/nino-tnc/modes` | `read` |
| GET | `/api/v1/monitor/recent` | `read` |
| GET | `/api/v1/netrom/routes` | `read` |
| POST | `/api/v1/ping` | `operate` |
| GET | `/api/v1/ports` | `read` |
| POST | `/api/v1/ports` | `operate` |
| DELETE | `/api/v1/ports/{id}` | `operate` |
| PUT | `/api/v1/ports/{id}` | `operate` |
| GET | `/api/v1/ports/{id}/doctor` | `read` |
| POST | `/api/v1/ports/{id}/doctor` | `admin` |
| POST | `/api/v1/ports/{id}/hail` | `admin` |
| POST | `/api/v1/ports/{id}/lifecycle` | `operate` |
| GET | `/api/v1/ports/{id}/quality` | `read` |
| GET | `/api/v1/ports/{id}/radio` | `read` |
| GET | `/api/v1/ports/{id}/rig` | `read` |
| POST | `/api/v1/ports/{id}/rig/frequency` | `operate` |
| POST | `/api/v1/ports/{id}/rig/mode` | `operate` |
| GET | `/api/v1/ports/{id}/spectrum/events` | `read` (SSE, also takes `?access_token=`) |
| GET | `/api/v1/ports/{id}/tuning/events` | `read` (SSE, also takes `?access_token=`) |
| POST | `/api/v1/ports/{id}/tuning/next` | `admin` |
| DELETE | `/api/v1/ports/{id}/tuning/session` | `admin` |
| POST | `/api/v1/ports/{id}/tuning/session` | `admin` |
| POST | `/api/v1/ports/{id}/tuning/stop` | `admin` |
| POST | `/api/v1/ports/{id}/tuning/txdelay-min` | `admin` |
| POST | `/api/v1/ports/{id}/tuning/txdelay-min/apply` | `admin` |
| GET | `/api/v1/radios` | `read` |
| GET | `/api/v1/radios/headends` | `read` |
| POST | `/api/v1/radios/headends/{instanceId}/adopt` | `operate` |
| POST | `/api/v1/radios/headends/{instanceId}/pair-by-keyup` | `admin` |
| GET | `/api/v1/radios/scan` | `read` |
| GET | `/api/v1/rigs` | `read` |
| GET | `/api/v1/rigs/events` | `read` (SSE, also takes `?access_token=`) |
| GET | `/api/v1/rigs/models` | `read` |
| GET | `/api/v1/rigs/scan` | `read` |
| GET | `/api/v1/sessions` | `read` |
| POST | `/api/v1/sessions` | `operate` |
| DELETE | `/api/v1/sessions/{id}` | `operate` |
| POST | `/api/v1/sessions/{id}/send` | `operate` |
| GET | `/api/v1/sessions/{id}/stream` | `operate` (SSE, also takes `?access_token=`) |
| POST | `/api/v1/setup` | anonymous |
| GET | `/api/v1/setup/devices` | anonymous |
| GET | `/api/v1/setup/state` | anonymous |
| GET | `/api/v1/status` | `read` |
| GET | `/api/v1/system/info` | `read` |
| GET | `/api/v1/system/loglevel` | `read` |
| PUT | `/api/v1/system/loglevel` | `admin` |
| GET | `/api/v1/system/tailscale` | `read` |
| POST | `/api/v1/system/update` | `admin` |
| GET | `/api/v1/traffic` | `read` |
| GET | `/api/v1/users/` | `admin` |
| POST | `/api/v1/users/` | `admin` |
| DELETE | `/api/v1/users/{username}` | `admin` |
| ANY | `/api/{**rest}` | anonymous |
| DELETE | `/apps/{id}/{**rest}` | `read` |
| GET | `/apps/{id}/{**rest}` | `read` |
| HEAD | `/apps/{id}/{**rest}` | `read` |
| OPTIONS | `/apps/{id}/{**rest}` | `read` |
| PATCH | `/apps/{id}/{**rest}` | `read` |
| POST | `/apps/{id}/{**rest}` | `read` |
| PUT | `/apps/{id}/{**rest}` | `read` |
| GET | `/healthz` | anonymous |
| POST | `/mcp/` | `mcp` |
| GET | `/metrics` | anonymous |
| GET | `/oauth/authorize` | anonymous |
| POST | `/oauth/authorize` | anonymous |
| POST | `/oauth/register` | anonymous |
| POST | `/oauth/revoke` | anonymous |
| POST | `/oauth/token` | anonymous |
| GET | `/{*path:nonfile}` | anonymous |
| HEAD | `/{*path:nonfile}` | anonymous |

<!-- END generated route inventory -->

`ANY` on `/api/{**rest}` is the catch-all that 404s an unknown API path rather than
letting it fall through to the SPA's `index.html`; the bare `{*path:nonfile}` route is
that SPA fallback. The `/apps/{id}/{**rest}` row is the app gateway, which reverse-proxies
to an installed app's own HTTP server.
