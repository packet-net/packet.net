# packetnet-ui: pdn node control panel (Phase 5)

The operator web control panel for the **pdn** (`packetnet`) packet-radio node: the
Phase-5 UI over the Phase-4 Slice-3 API. Recreated from the converged design handoff
(`../../design_handoff_pdn_control_panel/`) per `../../docs/node-ui-design.md`.

**Stack:** Vite + React + TypeScript + Tailwind + shadcn-style components (the design
tokens + primitives live locally; see `src/components/ui`). Icons: lucide-react. Routing:
react-router. Theme: dark-first, light/dark via a `.dark` class.

## Run it

```sh
npm install
npm run dev          # http://localhost:5173, runs against the MOCK backend by default
```

The app ships with a **mock backend** (`src/lib/mock.ts`, ported from the handoff's
`data.jsx` using the real record field names) so every screen renders and demos with no
node. **There is no login step in mock mode:** the gate (`src/app/router.tsx`) sees
`apiMode === "mock"`, enters a synthetic admin session (`auth.enterAnonymous("admin")`)
and drops you straight on the Dashboard. `/login` bounces back to `/`, and the "Continue
with passkey" button isn't offered at all: `api.ts` reports `webauthnSupported = false`
in mock and `api.passkeyAssert` throws, because we never fake a ceremony.

Mock vs live is chosen **only** by the Vite env var `VITE_API_MODE`, read once at module
load in `src/lib/api.ts` and defaulting to `mock`. Production builds hard-set
`VITE_API_MODE=live` (see § Production build), so a shipped panel cannot fall back to
fixtures.

`mock.ts` is a *fake node*, not a grab-bag: only `api.ts`'s mock branch and the tests may
import it, and an ESLint `no-restricted-imports` rule fails the build if anything under
`src/screens` or `src/components` does. (Screens used to default the connect-out port to
the mock's `vhf-1` and paint the mock's five GB7RDG ports while `/config` loaded, so real
operators saw an invented node, [#691](https://github.com/packet-net/packet.net/issues/691).)
The operator-facing copy the screens *do* need in every mode (labels, radio presets,
parameter help, the KISS 10 ms unit helpers, formatters) is `src/lib/catalogue.ts`.

To run against a **real node**:

```sh
VITE_API_MODE=live VITE_API_PROXY=http://127.0.0.1:8080 npm run dev
```

The API boundary is `src/lib/api.ts` (typed client + SSE); flipping `VITE_API_MODE` to
`live` swaps the mock backend for real `fetch`/`EventSource` against `/api/v1` with no
screen changes. Where the API contract actually lives (the C# DTOs, the generated route
inventory, the contract fixtures) is written up in `../../docs/node-api.md`.

### The gate, against a real node

`RequireAuth` (`src/app/router.tsx`) probes before it renders anything, showing a quiet
"Connecting to node" splash meanwhile:

1. `GET /setup/state`. `needsSetup` sends you to the setup wizard.
2. Otherwise `GET /status`, carrying the stored token if there is one:
   - **200** enters the app, either with that token or tokenless (a node running with
     `management.auth.enabled` off answers unauthenticated calls);
   - **401**, or any other failure (node down), lands on `/login`.

A 401 from any later call clears the session and returns to `/login` (`api.ts` dispatches
the `pdn:unauthorized` event `auth.tsx` listens for). Server auth **defaults on**, so a
fresh node's first load is the setup wizard, then the login form.

## Verify

```sh
npm run lint         # eslint, --max-warnings 0 (incl. the no-mock-in-screens rule)
npm run typecheck    # tsc --noEmit on its own
npm run build        # tsc --noEmit + vite production build
npm test             # vitest: the contract, api, screen-behaviour and smoke suites (jsdom)
npm run preview      # serve the built dist/ locally
npm run screenshot   # bash scripts/screenshot.sh, see the Screenshots section
```

CI's `web-ui` job runs lint, build and test.

### The test suite

`src/test/` holds 24 suites plus `setup.ts` (RTL cleanup + the jsdom polyfills the
monitor's rAF tween and the console's auto-scroll need). Most are regression suites
pinned to a named review item rather than smoke tests:

| suite | covers |
| --- | --- |
| `screens.smoke` | mounts every screen against the mock backend, asserting it renders and surfaces a key piece of copy |
| `shell.nav` | the left-nav Apps group: enabled web apps become nav entries, each a full navigation to `/apps/{id}/` |
| `app.frame` | `/apps/:id`: embedded vs slot iframe `src`, and the standalone / unknown-id fallbacks |
| `apps.available` | the "Available apps" catalog section + the uninstall control |
| `apps.packages` | the package-manager rows: capability confirm before enable, admin gating, restart visibility, busy state |
| `api.auth` | live-mode auth in `api.ts`: the 401 silent renew (one `/auth/refresh`, one retry) and the tab-focus proactive refresh |
| `api.errors` | error-message extraction across BOTH server error shapes, `{error}` and `ValidationProblem` (C029) |
| `api.stream` | SSE plumbing: re-subscribe when a stream's token expires, backlog reset on reconnect, buffer flush when `seq` restarts (C023/C045/C046) |
| `config.netrom` | the NET/ROM + INP3 form against the server's JSON dialect: enum member names, and seconds rather than duration strings |
| `config.roundtrip` | the GB7RDG migration fields survive a Forms-tab save: axudp multipoint peers, per-port MINQUAL/NODESPACLEN, NET/ROM compress |
| `console` | the console screen opens a session, writes streamed output into a real xterm, and POSTs a keystroke |
| `ports.editor` | the Ports editor's round-trip fidelity: no invented `profile`, no nulled mqtt/head-end fields, no UI defaults injected on open |
| `ports.kiss-units` | TXDELAY/TXTAIL/SLOTTIME are single bytes in 10 ms units on the wire, not milliseconds |
| `ports.rig` | the CAT-control section: scan, claimed devices, the hamlib model picker, saving through the existing port write path |
| `headends.panel` | the head-end adopt surface: auto / ambiguous / duplicate-id / unreachable states and the operate gate |
| `headends.keyup` | the RF-emitting keyup pairing flow: admin gate, the transmit confirm, resolved pairs, honest unreachable result |
| `system.panel` | the "About this node" panel: version/channel, the update banner, the admin-gated Apply, the poll-reconnect |
| `tailscale.panel` | the "Remote access (Tailscale)" card: needs-login link, running FQDN, the admin-gated RP-id adoption |
| `mock-leak` | the screens show THIS node and never the fixture node ([#691](https://github.com/packet-net/packet.net/issues/691) WP4) |
| `router.gate` | the auth gate's probe order and the 401 path, including that a silent renew is not re-persisted over a rotated pair (C035) |
| `config.scope` | the config screen against the write scopes the server enforces, including the admin carve-out on `management.auth` |
| `sessions.actions` | the Sessions screen's connect / disconnect / send actions against the live client |
| `link-tuner.gating` | the tuner only offers Start and Next round when the server would accept them |
| `users.self` | self-service passkey and TOTP enrolment for a non-admin operator |

## Screenshots

A **host** browser cannot screenshot here: the dev LXC denies Chrome's network sockets
(`CreatePlatformSocket: Permission denied`) and Playwright's chromium doesn't support the
host OS. Docker is how it is done instead, and it works: `scripts/screenshot.sh` builds
`dist/`, then runs a debian container that serves it and drives a real chromium
(`scripts/screenshot.mjs`) over every route, writing PNGs to `.shots/`. The repo's
`scripts/passkey-e2e.sh` proves the passkey ceremony end-to-end with the same
container pattern (plus a CDP virtual authenticator).

`screenshot.mjs` reads a few env knobs: `PDN_TOKEN` (or `PDN_USER`/`PDN_PASS`, which logs
in for real) to enter a **live** build, `PDN_PORT` for the tuner/waterfall shots, and
`PDN_APP` for the in-panel app frame. A mock build needs none of them, since the gate
enters its synthetic admin session by itself.

CI does not screenshot (the `web-ui` job runs lint + build + test only): the render
smoke test (`src/test/screens.smoke.test.tsx`) is the runtime gate there, mounting every
screen against the mock backend and asserting it renders without crashing.

> Note: headless-browser screenshot verification is **not** possible on the current dev
> LXC (the sandbox blocks Chrome's network sockets), so a *browser* runs only inside
> Docker; see the live smoke below and `scripts/screenshot.sh`.

### The client/server contract

`src/test/contract/*.json` are **generated**, never hand-edited. A test in the .NET suite
(`tests/Packet.Node.Tests/Contract/ClientContractFixtureTests.cs`) serialises a representative
instance of every DTO this panel consumes, with the node's *real* wire options (the same
`NodeConfigJson.ApplyTo` over `JsonSerializerDefaults.Web` that `Program.cs` gives the HTTP
layer), and fails if a fixture no longer matches. `src/test/contract.test.ts` reads those files
back, reads `src/lib/types.ts` with the TypeScript compiler API, and fails on:

- a wire field `types.ts` does not model,
- a `types.ts`-required field the server never sends,
- a value whose kind or union member is wrong,
- a closed set (`TransportKind`, `FrameType`, `AppPackageState`, …) that has drifted,
- and the same three, run over `src/lib/mock.ts`: the fake node may not describe a node the
  real server could not be.

`src/test/contract/fixtures.ts` is the compile-time half, so `npm run build` catches the
required-field case without running vitest.

**When the server changes shape,** regenerate rather than editing the JSON:

```sh
cd ../..                                                # repo root
PDN_UPDATE_CONTRACT=1 dotnet test tests/Packet.Node.Tests --filter "FullyQualifiedName~Contract"
cd web/packetnet-ui && npx vitest run src/test/contract.test.ts
```

then fix `types.ts` and `mock.ts` until that is green. Do not relax a check to make it pass;
that is the hole this closes ([#692](https://github.com/packet-net/packet.net/issues/692)).

### Live smoke against a real node

```sh
../../scripts/live-smoke/run.sh          # from anywhere; ~70 s warm
```

Boots `pdn` on free ports with a scratch YAML (auth ON, one kiss-tcp port to a throwaway TCP
sink) and a temp db, completes first-run setup + login over the API, serves *this* SPA's
live-mode build from the node, then drives it with Playwright chromium in a Docker container on
`--network host` (the LXC reason above). It walks Dashboard, Ports (add with the panel's own
defaults; edit-save-unchanged compared field-by-field against the `GET /config` it loaded),
Config's dry run, Sessions connect-out, Console, Monitor, Users, and opens every SSE feed with
`?access_token=`. It fails on any console error, any 4xx/5xx from the SPA's own requests, or
visible `undefined` / `NaN` / `[object Object]` text, and keeps its screenshots. CI runs it from
`.github/workflows/live-smoke.yml` on pushes to `main`.

## Layout

```
src/
  lib/        types.ts (the §6 data model) · api.ts (client + SSE) · catalogue.ts (UI copy,
              presets, unit helpers) · health.ts (pure port good/degraded/faulted verdict) ·
              secureContext.ts ("could a WebAuthn ceremony run here?") ·
              mock.ts (the fake node: api.ts + tests only) · utils.ts
  components/ ui/ (primitives → shadcn-style) · icon.tsx (→ lucide) · layout/shell.tsx · ping.tsx
  app/        auth.tsx (session + scopes) · router.tsx (the gate + the route table)
  screens/    dashboard · monitor · sessions · console · apps · app-frame · routes ·
              capabilities · ports · headends · config · users · link-troubleshoot ·
              link-tuner · waterfall · login · setup
  test/       the suites listed under The test suite + setup.ts
              contract.test.ts + contract/ (generated server fixtures, see The client/server contract)
              api.live.test.ts (every api.* member against a stubbed fetch) + helpers/
public/       fonts/ (self-hosted Inter + JetBrains Mono woff2; the panel loads nothing
              off-box, see the @font-face block at the top of src/index.css)
```

17 addressable routes (`src/app/router.tsx`): `/login`, `/setup`, `/` (dashboard),
`/monitor`, `/sessions`, `/console`, `/apps`, `/apps/:id`, `/routes`, `/capabilities`,
`/ports`, `/headends`, `/config`, `/users`, `/links`, `/tools/tuner`, `/tools/waterfall`,
plus a catch-all that redirects to `/`.

## Production build → served by the node

`npm run build` emits `dist/` (including `public/` verbatim, so the fonts ship with it).
The .NET host (Kestrel) serves it as static files under `/`, with a deep-link fallback to
`index.html`. `dist/` and `node_modules/` are gitignored.

The SPA build is wired into `dotnet publish`, not into the packaging script. The
`BuildWebUi` / `_ViteBuildWebUi` targets in `../../src/Packet.Node/Packet.Node.csproj`
run `npm ci` (only when `node_modules` is stale) and then `npm run build` with
`VITE_API_MODE=live`, both before `ComputeFilesToPublish`, and inject the result into the
publish output as `wwwroot/`. So a plain `dotnet publish` produces a current live UI;
`../../scripts/build-deb.sh` merely stages that publish output into the `.deb` tree and
hard-fails if `wwwroot/index.html` is missing. Publishing with `-p:BuildWebUi=false`
skips the whole thing (a host with no Node toolchain, or a `wwwroot` staged another way).

## What's mock vs real

With `VITE_API_MODE=mock` (the dev + vitest default) every screen renders against the fake
node in `src/lib/mock.ts`; production builds are `live` and every screen's data comes off
the wire. Nothing renders a fixture in live mode; a screen with no data yet shows a
loading or error state, because a fixture shown as if it were the operator's own node is a
lie about their station.

Operator-facing helper models that are genuinely client-side copy (radio presets, NinoTNC
modes, parameter help, the NET/ROM toggle wording) live in `src/lib/catalogue.ts`. Which of
those should eventually come from the server instead is still open (noted in the design
doc); the radio presets in particular are a *client-side baseline picker* and never travel
to the API as a `profile` id.
