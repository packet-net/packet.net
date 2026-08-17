# packetnet-ui — pdn node control panel (Phase 5)

The operator web control panel for the **pdn** (`packetnet`) packet-radio node — the
Phase-5 UI over the Phase-4 Slice-3 API. Recreated from the converged design handoff
(`../../design_handoff_pdn_control_panel/`) per `../../docs/node-ui-design.md`.

**Stack:** Vite + React + TypeScript + Tailwind + shadcn-style components (the design
tokens + primitives live locally; see `src/components/ui`). Icons: lucide-react. Routing:
react-router. Theme: dark-first, light/dark via a `.dark` class.

## Run it

```sh
npm install
npm run dev          # http://localhost:5173 — runs against the MOCK backend by default
```

The app ships with a **mock backend** (`src/lib/mock.ts`, ported from the handoff's
`data.jsx` using the real record field names) so every screen renders and demos with no
node. Log in with **"Continue with passkey"** (the mock auth gate).

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

The API boundary is `src/lib/api.ts` (typed client + SSE) — flipping `VITE_API_MODE`
to `live` swaps the mock backend for real `fetch`/`EventSource` against `/api/v1` with
no screen changes. The contract is locked in `../../docs/node-api.yaml`.

## Verify

```sh
npm run lint         # eslint, --max-warnings 0 (incl. the no-mock-in-screens rule)
npm run build        # tsc --noEmit + vite production build
npm test             # vitest: render smoke test for every screen (jsdom)
```

CI's `web-ui` job runs all three.

> Note: headless-browser screenshot verification is **not** possible on the current dev
> LXC (the sandbox blocks Chrome's network sockets). The render smoke test
> (`src/test/screens.smoke.test.tsx`) is the runtime gate instead — it mounts every
> screen against the mock backend and asserts it renders without crashing.

## Layout

```
src/
  lib/        types.ts (the §6 data model) · api.ts (client + SSE) · catalogue.ts (UI copy,
              presets, unit helpers) · mock.ts (the fake node: api.ts + tests only) · utils.ts
  components/ ui/ (primitives → shadcn-style) · icon.tsx (→ lucide) · layout/shell.tsx · ping.tsx
  app/        auth.tsx (gate) · router.tsx
  screens/    dashboard · monitor · sessions · routes · ports · config · users · login · setup · link-tuner
  test/       screens.smoke.test.tsx + setup.ts + per-screen behaviour suites
public/       fonts/ (self-hosted Inter + JetBrains Mono woff2; the panel loads nothing
              off-box, see the @font-face block at the top of src/index.css)
```

## Production build → served by the node

`npm run build` emits `dist/` (including `public/` verbatim, so the fonts ship with it).
The .NET host (Kestrel) serves it as static files under `/`, with a deep-link fallback to
`index.html`; `build-deb.sh` bakes it into the `.deb`. `dist/` and `node_modules/` are
gitignored.

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
