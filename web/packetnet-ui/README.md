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

The app ships with a **mock data layer** (`src/lib/mock.ts`, ported from the handoff's
`data.jsx` using the real record field names) so every screen renders and demos with no
node. Log in with **"Continue with passkey"** (the mock auth gate).

To run against a **real node** (once the Slice-3 backend exists):

```sh
VITE_API_MODE=live VITE_API_PROXY=http://127.0.0.1:8080 npm run dev
```

The API boundary is `src/lib/api.ts` (typed client + SSE) — flipping `VITE_API_MODE`
to `live` swaps the mock backend for real `fetch`/`EventSource` against `/api/v1` with
no screen changes. The contract is locked in `../../docs/node-api.yaml`.

## Verify

```sh
npm run build        # tsc --noEmit + vite production build
npm test             # vitest: render smoke test for every screen (jsdom)
```

> Note: headless-browser screenshot verification is **not** possible on the current dev
> LXC (the sandbox blocks Chrome's network sockets). The render smoke test
> (`src/test/screens.smoke.test.tsx`) is the runtime gate instead — it mounts every
> screen against the mock backend and asserts it renders without crashing.

## Layout

```
src/
  lib/        types.ts (the §6 data model) · mock.ts · api.ts (client + SSE) · utils.ts
  components/ ui/ (primitives → shadcn-style) · icon.tsx (→ lucide) · layout/shell.tsx · ping.tsx
  app/        auth.tsx (gate) · router.tsx
  screens/    dashboard · monitor · sessions · routes · ports · config · users · login · setup · link-tuner
  test/       screens.smoke.test.tsx + setup.ts
```

## Production build → served by the node

`npm run build` emits `dist/`. The .NET host (Kestrel) serves it as static files under
`/` (Slice-3 wiring — TODO). `dist/` and `node_modules/` are gitignored.

## What's mock vs real

Everything renders from `src/lib/mock.ts` today. The **screens** and the **typed API
client** are production; the **backend behind the client is not built yet** (Slice 3 —
read endpoints + SSE + auth, per `docs/node-api.yaml`). Operator-facing helper models
(radio profiles, NinoTNC modes, parameter help, beacons) are UI-layer copy in `mock.ts`;
where they should live server-side is a Slice-3 decision (noted in the design doc).
