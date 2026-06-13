# pdn MCP endpoint — design

**Status:** design agreed 2026-06-13 (kickoff of Phase 8, [#173](https://github.com/m0lte/packet.net/issues/173)). Decisions taken with Tom at kickoff:

1. **In-process.** The MCP server lives inside the node host and reads live node state — it is not a standalone process re-projecting the REST API.
2. **Auth is the shipped `read`/`operate`/`admin` model**, not the `mcp:invoke`/granular scheme §6 once penciled in (that scheme was never built — see [§17](plan.md#17-amendment-log) and the §6 reconciliation in this doc).
3. **Full §5.8 surface in the opening slice** — read tools + write tools + both transports — rather than a read-only first cut. (Internal build order below; "one slice" ≠ "one commit".)

This is Slice 4 of the node arc ([plan §5.4](plan.md)) and the home of the deferred link-tuner. Companion to the REST contract in [`node-api.yaml`](node-api.yaml): MCP and REST are two faces of the **same** live node state, never a second copy of it.

## The shape

`Packet.Mcp` is the **transport-agnostic tool surface** — the tool names, schemas, descriptions, and handlers — written against one seam:

```csharp
// Packet.Mcp
public interface INodeMcpBackend
{
    // read
    Task<IReadOnlyList<PortStatus>>   ListPortsAsync(CancellationToken ct);
    Task<IReadOnlyList<SessionInfo>>  ListSessionsAsync(CancellationToken ct);
    Task<IReadOnlyList<MonitorFrame>> RecentFramesAsync(FrameFilter filter, CancellationToken ct);
    Task<LinkQuality>                 LinkQualityAsync(string remote, string? portId, CancellationToken ct);
    Task<NetworkTopology>             NetworkTopologyAsync(CancellationToken ct);
    // write (operate-gated, audited)
    Task<SendResult>       SendUiFrameAsync(SendUiRequest req, McpCaller caller, CancellationToken ct);
    Task<PortActionResult> ResetPortAsync(string portId, McpCaller caller, CancellationToken ct);
    Task<SessionResult>    DisconnectSessionAsync(string sessionId, McpCaller caller, CancellationToken ct);
    Task<KissParamResult>  SetKissParamAsync(SetKissParamRequest req, McpCaller caller, CancellationToken ct);
}
```

`decode_frame(hex)` is **pure** — it parses bytes through `Packet.Ax25` (KISS-unwrap first if framed) and needs no backend at all, so it lives entirely in `Packet.Mcp` and is the natural first tool + golden-test anchor.

Two backends implement the seam:

- **`LiveNodeMcpBackend`** (in `Packet.Node` / `Packet.Node.Core`) — calls `NodeHostedService.{Supervisor,NetRom,Telemetry}` and the existing action paths directly. This is the true in-process binding the SSE transport serves. It reuses the *exact* projection helpers `PdnReadApi` already has (`ProjectSession`, `BuildLinks`, the NET/ROM snapshot shaping) — MCP must never drift from what `/api/v1` reports.
- **`RestNodeMcpBackend`** (in the `pdn mcp` stdio entrypoint) — an HTTP client of the running node's loopback REST API. This resolves the process-boundary reality: a `pdn mcp` subcommand is a *separate process* and cannot share the running node's in-proc state, so the stdio server bridges to the live node over `127.0.0.1`. Tool definitions stay DRY; only the backend differs.

A `FakeNodeMcpBackend` in tests exercises every tool handler with no node running.

```
                       Packet.Mcp  (tool surface: names/schemas/handlers + decode_frame)
                              │  INodeMcpBackend
              ┌───────────────┴───────────────┐
   LiveNodeMcpBackend                   RestNodeMcpBackend
   (in the node process)                (in `pdn mcp`, HTTP→127.0.0.1)
        │                                      │
   NodeHostedService                     node REST /api/v1
   {Supervisor,NetRom,Telemetry}
        │                                      │
   SSE  /mcp  (ASP.NET pipeline)         stdio  (Claude Code, local clients)
```

## SDK

Use the official C# SDK — **`ModelContextProtocol`** (core, stdio) + **`ModelContextProtocol.AspNetCore`** (Streamable-HTTP) — not a hand-rolled JSON-RPC layer. `[McpServerToolType]` / `[McpServerTool]` with DI; `WithStdioServerTransport()` for the subcommand; `MapMcp()` to mount the HTTP transport.

**Transport note:** the plan §5.8/§6 say "SSE". The 2025 MCP spec deprecated the standalone HTTP+SSE transport in favour of **Streamable HTTP** (SSE is its streaming leg). We use the SDK's Streamable-HTTP endpoint, which is SSE on the wire for streamed responses — so "SSE" in the plan is satisfied by the modern transport, and we don't implement the deprecated one. Both packages go in [`Directory.Packages.props`](../Directory.Packages.props) (CPM — no inline `Version=`) and are recorded in [plan §12](plan.md#12-locked-external-dependencies) when taken.

## Tool catalog

| Tool | Scope | Backend call | Args | Returns |
|---|---|---|---|---|
| `decode_frame` | `read` | *(pure — no backend)* | `hex`, `framing?` (`raw`\|`kiss`, default auto) | decoded AX.25: addresses/path, control (incl. mod-128), PID, payload, any APRS/NET-ROM hint |
| `list_ports` | `read` | `ListPortsAsync` | — | `PortStatus[]` (id, enabled, state, sessions, frames in/out) |
| `list_sessions` | `read` | `ListSessionsAsync` | — | `SessionInfo[]` (id `port:peer`, role, state, V(s)/V(r)/K, bytes, uptime) |
| `recent_frames` | `read` | `RecentFramesAsync` | `port?`, `peer?`, `kind?`, `since?`, `limit?` (≤250) | `MonitorFrame[]` from the telemetry ring (oldest→newest) |
| `link_quality` | `read` | `LinkQualityAsync` | `remote`, `port?` | per-link SRTT, retries, REJ/SREJ, frame/byte counts, T1/T3 (see Monitor-v2) |
| `network_topology` | `read` | `NetworkTopologyAsync` | — | NET/ROM neighbours + destinations + routes (the `/netrom/routes` shape) |
| `send_ui_frame` | `operate` | `SendUiFrameAsync` | `port`, `dest`, `payload`, `path?`, `pid?` | send result (accepted/queued) |
| `reset_port` | `operate` | `ResetPortAsync` | `port` | port-restart result (maps to the `restart` lifecycle) |
| `disconnect_session` | `operate` | `DisconnectSessionAsync` | `id` (`port:peer`) | disconnect result |
| `set_kiss_param` | `operate` | `SetKissParamAsync` | `port`, `param`, `value` | applied/queued, plus whether it took live or needs a restart |

Outbound stays **strict** (the §2 construction-path rule): `send_ui_frame` builds frames through the spec-strict factories — MCP never produces a frame the encoder would reject, even though decode/inbound accepts lenient input.

`set_kiss_param` distinguishes **live** params (TXDELAY/persist/slottime/TXtail via a KISS SetHardware/param write) from **construction-time** ones (e.g. `kiss.ackMode`, which restarts the port — see the 2026-06-11 ACKMODE amendment); the result says which happened so the caller isn't surprised.

## Auth & audit (reconciling §6)

- **Read tools** require `read`; **write tools** require `operate`. The gate is the same `ScopeRequirementHandler` the REST API uses, so it **passes through when `management.auth.enabled` is off** — the default-unauthenticated loopback behaviour is unchanged, exactly like `/api/v1`.
- **stdio = local user, no token** (the §6 table row): a process that can exec `pdn mcp` and reach loopback is already trusted at the OS level. When the node has auth on, the stdio bridge carries a local token (config/`PDN_*`), same as any loopback REST client.
- **Every write tool is audit-logged** — actor, transport, scope, payload hash — through the same `AuthLog`/`SystemLog` sink §6 mandates for write endpoints. `McpCaller` carries the actor identity (token subject over SSE; `local-stdio` over stdio).

**§6 correction (recorded in the plan):** the granular `frames:read`/`ports:write`/`sessions:write`/`mcp:invoke` scope list in §6 was aspirational and never shipped; the node runs the hierarchical `read`/`operate`/`admin` model (`AuthScopes.cs`). MCP uses the shipped model. The §6 table's `mcp:invoke` cell and scope list are updated to match.

## Config

A new `mcp:` block (defaults preserve "off until asked", like AGW/RHPv2):

```yaml
mcp:
  enabled: false            # master switch: registers the MCP tool surface in DI
  sse:
    enabled: false          # mount the Streamable-HTTP transport on the web listener
    path: /mcp              # served on the EXISTING web listener (piggyback, like RHPv2-WS at /rhp)
  # stdio needs no config — it's the `pdn mcp` subcommand
```

The SSE transport is **not** a separate socket/port — it mounts on the node's existing web
listener (`management.http` / `management.https`), so it inherits that listener's bind, TLS,
and auth. (The §6 "8051" note is superseded by this piggyback.) The `pdn mcp` subcommand reads
the node base URL from `--node-url` / `PDN_NODE_URL` (default loopback) + an optional local token.

## Deployment & reachability

**The model (decided 2026-06-13): the SSE/Streamable-HTTP `/mcp` endpoint is the network transport, and MCP reachability == web-panel reachability.** Because `/mcp` rides the same web listener as the control panel, whatever path already reaches the pdn web UI from a phone/laptop reaches MCP — same host, same port, same TLS, same JWT auth. There is **no hard Tailscale dependency**; Tailscale is one supported reachability option, not a requirement. stdio (`pdn mcp`) is a **co-located convenience** (on the node box, or a laptop with the binary + LAN line-of-sight via `--node-url`), *not* the mainstream remote path.

All four reachability paths are in scope (operator's choice — they're the same endpoint, differing only in how the client reaches the listener and authenticates):

| Path | How the client reaches it | Auth | Status |
|---|---|---|---|
| **LAN-direct** | `https://node.lan:8443/mcp` (web listener bound to a LAN addr, TLS on) | node JWT bearer header | works once `mcp.sse` on + listener LAN-bound |
| **Tailscale/WireGuard** | `https://<node-ts-name>/mcp` over the overlay; no public exposure | node JWT bearer | works (recipe to document) |
| **Public HTTPS domain** | the lab `pdn.m0lte.uk` model — DDNS/port-forward + real cert | node JWT bearer | works (harden: rate-limit, token lifetime) |
| **Hosted claude.ai connector** | claude.ai "custom connector" → public HTTPS URL | **OAuth 2.1** (MCP auth spec) | **needs building** — see below |

**Auth over the wire.** `/mcp` is gated `read` (read tools) / `operate` (write tools) via the node's existing JWT bearer when `management.auth.enabled` is on, with TLS from the web listener. **Claude Code (desktop/CLI)** supports remote HTTP MCP servers with an `Authorization: Bearer …` header, so it works against `/mcp` today given reachability + a node-issued token. Planned convenience: a long-lived, `read`/`operate`-scoped **MCP token** an operator mints in the panel (rather than reusing a short-lived login JWT).

**The OAuth gap (the claude.ai connector path).** Hosted claude.ai custom connectors follow the MCP authorization spec — OAuth 2.1 with the MCP server as an OAuth **resource server**, discovered via `/.well-known/oauth-protected-resource` + an authorization server (the node can be its own AS, reusing the existing user store / passkeys for the consent step, or delegate). This is a real, security-sensitive build (its own design pass + review, like the WebAuthn work) — the long pole of the four paths. The other three need only config + docs + minor hardening on top of what's shipped.


## Monitor-v2 — the one piece that needs new instrumentation

`link_quality` is only as good as its inputs, and **SRTT + retries are not derivable from the frame tap** — `PdnReadApi.BuildLinks` honestly stubs them to `0` today because they live in each session's T1/SRTT timer state, which the telemetry tap doesn't observe. REJ/SREJ/frame/byte counts *are* real (from the tap).

So Phase 8's "monitor v2 / link troubleshooting" half is: **surface the per-session timer state** (T1 SRTT, retry count, T3) from the AX.25 session into telemetry, then feed it to **both** `link_quality` (MCP) **and** `LinkStats` (REST `/links`, retiring those two stubbed zeros) and the link-troubleshoot view (T1/T3 graphs). One instrumentation seam, three consumers. This is the larger, riskier part of the arc — it reaches into the session engine — so within the slice it lands **last**, after the tool surface is up on the data that already exists.

## ax25-ts parity

**No TS leg required.** The CI parity check ([CLAUDE.md](../CLAUDE.md)) compares the *parser* named-flag inventories (`Ax25ParseOptions`, `Ax25SessionQuirks`, `XidParseOptions`), the presets, and the `Ax25Listener` surface. The MCP server is a node-host control surface — it adds no parser flag and does not widen the listener options — so it is outside the parity contract. `decode_frame` *consumes* the existing parser (default options) but defines no new flag. If a future tool needs a new parse flag, that flag — not the tool — is what would need its TS counterpart.

## Build order (within the one slice)

1. **`decode_frame`** — pure, golden tests, no backend. Proves the SDK wiring + the tool-schema shape end to end.
2. **Read tools on existing data** — `list_ports`/`list_sessions`/`recent_frames`/`network_topology` over `LiveNodeMcpBackend`, reusing `PdnReadApi`'s projections. `link_quality` ships here too, returning the real counters with SRTT/retries still zero (honest, consistent with `/links`).
3. **SSE mount** — `MapMcp()` in the node pipeline behind the `read` gate + the `mcp.sse` config.
4. **stdio bridge** — the `pdn mcp` subcommand + `RestNodeMcpBackend`. (Program.cs grows its first subcommand check.)
5. **Write tools + audit** — `send_ui_frame`/`reset_port`/`disconnect_session`/`set_kiss_param`, each through the existing action path under the host's exclusive gate, `operate`-gated, audit-logged.
6. **Monitor-v2 instrumentation** — surface session T1-SRTT/retries/T3; retire the `LinkStats` zeros; T1/T3 troubleshoot view.

## Testing

- **Unit:** `FakeNodeMcpBackend` drives every tool handler (schema, arg validation, scope tagging) with no node. `decode_frame` gets golden vectors (mod-8 + mod-128, UI, with/without KISS framing, an APRS and a NET-ROM payload).
- **Auth:** read-tool-passes / write-tool-rejected under auth-on without `operate`; pass-through under auth-off. Mirrors the REST scope tests.
- **Wire smoke:** an SDK MCP client over stdio against `pdn mcp` (and over SSE against a test node) lists tools and round-trips `decode_frame` + one read tool.
- Default category (not Interop/HardwareLoop) so it runs in `ci.yml`.

## Out of scope (named)

- The **link-tuner `tune` flow** stays deferred (held for a net-sim tuneables design pass, per §5.4) — Phase 8 hosts it but this arc doesn't build it.
- **Resources/prompts** (MCP's non-tool primitives) — tools only for v1; a `resources` surface (e.g. config as a readable resource) is a later follow-up if it earns its place.
