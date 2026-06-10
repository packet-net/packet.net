# pdn app extensibility — design (the "applications" platform)

**Status:** agreed 2026-06-10 (Tom + Claude); supersedes the in-process `.dll`-plugin sketch in plan §5.9. **Slices 1, 2 + 3 built.** Slice 1 — the `INodeApplication` seam + the `applications:` registry + the `pdn-app/1` external-process stdio wire ([`app-local-session-wire.md`](app-local-session-wire.md)) + the shipped **WALL** reference app ([`examples/wall/`](../examples/wall/)). Slice 2 — the long-running-socket rung (`kind: socket`, `SocketApplication`; shared in-memory state across users) + the **LOBBY** worked example ([`examples/lobby/`](../examples/lobby/)). Slice 3 — the **app-gateway** (manifest + reverse-proxy + auth-gateway shell): the launcher + `/apps/{id}/*` reverse proxy with injected identity ([`app-gateway.md`](app-gateway.md)) + WALL's web view.
**Supersedes:** the original Phase-9 idea — `Packet.Node.Extensions/IApplicationModule.cs` loaded from `*.dll` in an isolated `AssemblyLoadContext`. That model is .NET-only and in-process; this design replaces it with **out-of-process, language-agnostic** apps. See [Why not in-proc .NET plugins](#why-not-in-proc-net-plugins).

## Why

The node's own web UI is intrinsically an **admin surface** — status and config. Packet *comes to life* through **applications**: the BBS, the chat server, the message wall, DAPPS. A node platform's job is to make those easy to build and expose — and the existing seams in the wild (BPQ's "application" config) are fiddly, limited, and effectively language-locked.

Two design goals drive everything:

1. **Don't lock app authors to .NET.** A node owner should be able to write a useful app in Python, Go, a shell script — whatever — without learning pdn's internals or its frontend.
2. **Separate application *logic* from *presentation*.** A packet session is one renderer (text/terminal); a web page is another; a native mobile app is a third. Get this boundary right and "native mobile app" stops being a scary separate project and becomes "another client of an app's interface."

## Principles

- **The node owner owns trust.** Apps run **out-of-process** and declare a **capability manifest**; the owner approves those capabilities when they install the app (like accepting a container's compose file). The node *mediates* every capability but does not need to *contain* a hostile app, because the owner chose to run it. No enforced sandbox is required for v1; a WASM/capability sandbox is a later option for an untrusted marketplace, not a prerequisite.
- **Reuse existing protocols where interop matters; invent only where it doesn't.** The network plane reuses RHPv2 (XRouter interop); the local plane is pdn-native (no interop story to preserve).
- **pdn knows manifests, not app semantics.** The node is a *broker* — a session router, a reverse proxy, an auth gateway. It never imports an app's code or learns what "webmail" means.

## The model: two planes

App capability splits cleanly into a **packet plane** (how the app touches sessions and the radio network) and a **human plane** (how the app shows a UI). An app uses one or both; they compose.

### Packet plane — two seams, delineated by *purpose*

The split is not about wire ergonomics — it's about whether the app uses **the node's own inbound session** (local) or **the network** (mesh). Interop is a network-plane property only: a local-only app has no "run it against XRouter too" story, so the router-centric model is pure overhead for it.

**(a) Local-session seam — pdn-native, minimal, owner-trusted.** The node hands the app the connected user's session (the `INodeConnection` equivalent — a transport-agnostic byte stream + the connecting callsign / arrival port / transport / sysop-elevation status), keyed by an alias or console verb. For apps that just want "the user who connected to *me*": WALL, a local info service, a menu.
- *Floor:* an external process spawned per connect, session piped over stdio + a small connect header (callsign/port/transport) — the **`pdn-app/1` wire**, specified in [`app-local-session-wire.md`](app-local-session-wire.md). Any language; a guestbook is ~30 lines + a state file. No network, no callsign registration. **Built in slice 1** (`ExternalProcessApplication`); WALL is its worked example.
- *Next rung:* a long-running local socket (Unix-domain) the node bridges sessions to, for apps wanting shared in-memory state across users. **Built in slice 2** (`SocketApplication`, `kind: socket`) — same `pdn-app/1` wire, the app is a daemon the node connects to per session; LOBBY ([`examples/lobby/`](../examples/lobby/)) is the worked example (live presence + broadcast).
- The built-in console (`NodeCommandService`) is itself the first local-session app — this seam *generalises what the console already does*, it isn't a new subsystem.

**(b) Network seam — RHPv2.** For apps that use the mesh: DAPPS, BBS *forwarding*, chat *linking*. **pdn implements the RHPv2 server** (`Packet.Rhp2.Server`) backed by its own AX.25 / NET-ROM / INP3 engine, so **any app written against [rhp2lib](https://rhp2lib.pages.dev/) (RHPv2, PWP-0222/0245) runs against pdn and XRouter unchanged.** That is the ecosystem unlock — the app world isn't pdn-locked. RHPv2 is JSON-over-TCP (`OPEN`/`SEND`/`CLOSE`/`RECV`/`ACCEPT` across families AX.25/NET-ROM/APRS/raw-TCP/ICMP), at the connection/socket layer (not raw frames) — the right altitude, and trivially implementable in any language. pdn's RHPv2 server is a *new front-end on seams that already exist*: `ACCEPT` ← the existing inbound `INodeConnection` path (routed by registered app callsign), `OPEN ax25/connected` → `Ax25Listener.ConnectAsync`, `OPEN netrom` → a `NetRomService` circuit, `SEND`/`RECV` → session bytes.

### Human plane — the app-gateway shell

**Built in slice 3** — the app-author contract is [`app-gateway.md`](app-gateway.md). How an app exposes a UI *through* pdn without pdn knowing about the app: the **manifest + reverse-proxy + auth-gateway** pattern (how Home-Assistant ingress / k8s dashboard proxies work).

1. An app registers a manifest: `{ id, name, icon, ui: { upstream: http://127.0.0.1:9001 }, packet: {...}, capabilities: [...] }`.
2. pdn's web shell renders a **launcher** from the registered manifests (an "Apps" section — pdn knows the *tile*, not the behaviour).
3. pdn **reverse-proxies `/apps/{id}/*`** to the app's own web service, and **injects the authenticated identity** (logged-in callsign + scope, as a signed header) so the app doesn't reimplement auth — TLS, passkeys, and the sysop scopes all sit at the gateway, once.
4. The app serves whatever UI it wants — a webmail SPA, a chat client, a DAPPS dashboard — in **any stack**. pdn never imports a line of it.

Native mobile is a later client of the same gateway (responsive web in a WebView, or the app exposing an API a native client calls). The node stays the broker, not the UI.

## The unifying abstraction

```
INodeApplication.RunAsync(INodeConnection session, AppContext ctx)
```

`AppContext` carries what BPQ's seam can't give cleanly: connecting callsign, arrival port + transport, sysop/auth status, invocation args. A **registry in `NodeConfig`** (`applications: [{ id, match, kind, ui?, capabilities }]`) is hot-reloadable exactly like `ports`/`beacons` (same reconcile machinery). `match` is an alias / console verb, reusing the `connect <alias>` mental model hams already have (`C WALL`). The console is app #0; connect-out relay and a future dapp are the same shape. **One seam, everything routes through it.**

## Trust + capability model

Out-of-process by default (blast radius is a process boundary). Each app declares a capability manifest (`sessions` / `network` / `config` / `storage`); the owner approves at install; pdn mediates each. Network apps authenticate to the RHPv2 server with an owner-provisioned credential scoped to the families/callsigns they may use — a service-account analogue of the existing auth model. WASM (WASI + a packet host ABI) is the *later* in-proc-but-sandboxed option for an untrusted/marketplace tier; not v1.

### Why not in-proc .NET plugins

The original §5.9 sketch loaded `IApplicationModule` `.dll`s in an `AssemblyLoadContext`. Out-of-process language-agnostic apps win on every axis that matters here: (1) **any language**, not just .NET; (2) **blast radius** — a crashing/hostile app is a dead process, not a corrupted node; (3) **independent release cadence** — apps (and DAPPS, which is its own project) evolve without recompiling pdn; (4) **interop** — the network seam *is* RHPv2, so apps target the broader XRouter ecosystem, not a pdn-private ABI. The only thing the DLL model wins is raw in-proc speed, which a packet node does not need.

## Keeping app code separate from node code

WALL is the proof that the seam works, so the separation between app and node is *the* property under test — and it is enforced, not merely intended. Six mechanisms, layered:

1. **No shared code, no compile-time link.** The app links nothing from the node; the node links nothing from the app. The only contract is the documented `pdn-app/1` wire. (`Packet.Node*` references no application project — asserted by an arch test.)
2. **Out-of-process from day one.** Slice 1 *is* the external wire — there is no in-proc-first shortcut to retrofit. The process boundary is the blast-radius boundary.
3. **A language boundary, for the reference app.** WALL is **Python**, deliberately. A .NET WALL could always be *suspected* of quietly reaching into node internals; a separate-language process structurally *cannot*. The cleanest separation is one the type system can't even express across.
4. **Discovery via config + manifest only.** The node learns an app exists from an `applications:` entry (its verb, its command) — never from compiled-in knowledge. The node does not name WALL anywhere in its source (its config examples use a neutral placeholder); the WALL-specific path lives only in the deployment config.
5. **An enforced CI guardrail.** An architecture test fails the build if `Packet.Node*` gains a project reference to an app, or hardcodes the WALL app (`wall.py` / `examples/wall`) into its source. Drift is caught mechanically.
6. **Dogfooding the public contract.** WALL is built against exactly the `pdn-app/1` wire a third-party author would use — no privileged side channel. If WALL needs something the wire doesn't give, the *wire* gets fixed (for everyone), not WALL (specially).

The payoff: "write a node app in any language, fully separated from the node" stops being a slogan and becomes a thing the repo *can't accidentally break*.

## The validating cast

| App | Local-session seam | Network seam (RHPv2) | Human plane (proxied web) | State |
|---|---|---|---|---|
| **WALL** | ✅ (read/post) | — | optional one-page wall view | tiny store/file |
| **Chat** | ✅ (users join rooms) | ✅ (BPQ-compatible inter-node *linking*) | packet chat client | room state |
| **BBS** | ✅ (read mail at the node) | ✅ (mail *forwarding* across the mesh) | webmail browser | mailboxes/store |
| **DAPPS** (Tom's) | — | ✅ (network) | admin/monitoring panel | its own |

WALL and BBS bracket the whole range — **floor** (local session + tiny store, no network) to **ceiling** (every surface). If both fit cleanly, the seam is right.

## Roadmap / slicing

The platform is **two adopt/build decisions, not four invent decisions**: *adopt* RHPv2 on the server side (packet/network plane); *build* the manifest+reverse-proxy+auth shell (human plane). Sliced so each step is independently shippable:

1. **`INodeApplication` + registry + the `pdn-app/1` stdio wire + WALL (BUILT).** Extract `INodeApplication` from `NodeCommandService` (console becomes app #0, zero behaviour change); add the `applications:` registry to `NodeConfig` (read live per launch — each connect spawns fresh, so a config edit applies to the next launch with no reconcile machinery); route a console verb to a registered app; implement the **external-process stdio wire** ([`app-local-session-wire.md`](app-local-session-wire.md)) — spawn-per-connect, session bridged over stdio with a connect header + newline translation + clean teardown; ship **WALL** as a fully-separated out-of-process **Python** app ([`examples/wall/`](../examples/wall/)). The external wire is folded into slice 1 deliberately — it *is* the separation boundary (see below). Proves the abstraction + registry + a useful, arm's-length app, end to end.
2. **Next rung of the local-session seam. ✅ DONE.** A long-running Unix-domain socket the node bridges sessions to (`kind: socket`, `SocketApplication`), for apps wanting shared in-memory state across users (vs the spawn-per-connect stdio floor). Same `pdn-app/1` wire — the app is a daemon the owner runs; the node connects per session. **LOBBY** ([`examples/lobby/`](../examples/lobby/)) is the worked example: live `WHO` presence + `SAY` broadcast across users. See the 2026-06-10 §17 Slice-2 entry + [`app-local-session-wire.md`](app-local-session-wire.md) §6.
3. **The app-gateway (human plane). ✅ DONE.** Manifest (`ui:` block) + the `GET /api/v1/apps` launcher feed + an Apps screen in the web UI + the `/apps/{id}/*` reverse proxy (YARP `IHttpForwarder`) with the authenticated identity injected (HttpOnly gateway cookie for browser navigations; client `X-Pdn-*` stripped, `X-Pdn-User`/`X-Pdn-Scope`/`X-Pdn-Gateway` injected). WALL gained a proxied web view ([`examples/wall/wall_web.py`](../examples/wall/wall_web.py)), so it now exercises both planes. Contract: [`app-gateway.md`](app-gateway.md).
4. **`Packet.Rhp2.Server` (network plane). 🟡 R-1+R-2 BUILT.** RHPv2 server over pdn's AX.25 engine — the codec (byte-compatible with real-XRouter golden fixtures) + the outbound host API (`open`(Active)/`send`/`recv`/`close`, auth against the node's users) are in; the passive half (`socket`/`bind`/`listen`/`accept`, multi-callsign engine work) is R-3 and the XRouter-Testcontainers conformance diff + DAPPS acceptance is R-4. Scope, oracle strategy, and the named-deviations table: [`rhp2-server.md`](rhp2-server.md).
5. **BBS / Chat / DAPPS** on the above. BBS stress-tests the full surface; chat needs the BPQ-compatible link protocol (a separate spec from RHPv2 — scope it on its own).

WALL is **shipped** (a useful built-in / example app a node owner gets out of the box) **and** the **worked example** future docs are written around.

## Open questions

- **RHPv2 family coverage for v1** — AX.25-connected + NET-ROM circuit back WALL/chat/BBS/DAPPS; APRS / raw-TCP / ICMP follow as pdn's stack reaches them.
- **Conformance oracle** — point rhp2lib's mock server / XRouter Testcontainers suite at *pdn's* RHPv2 server to prove wire-fidelity (a rigorous server harness, near-free).
- **BPQ-compatible chat** — is "compatible" wire-compat with BPQ's chat *link* protocol (federation with BPQ chat nodes)? Separate spec from RHPv2; reverse-engineering effort; scope independently.
- **Spec deltas** — read rhp2lib's "protocol primer (spec-vs-wire deltas)" + PWP-0222/0245 before implementing the server, since pdn must match XRouter's *actual* behaviour to be a drop-in host.
