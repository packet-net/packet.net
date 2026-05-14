# ax25sdl (TypeScript)

TypeScript consumers of the AX.25 SDL specification, generated from
the YAML transcriptions under `/spec-sdl/` by
`tools/Packet.Sdl.CodeGen.Ts`.

## Layout

```
ts-spec/
  package.json
  tsconfig.json
  src/ax25sdl/
    types.ts     # hand-written runtime types — DO NOT regenerate
    index.ts     # GENERATED — re-exports every page + types
    *.g.ts       # GENERATED — one per *.sdl.yaml
    *.test.ts    # hand-written smoke tests
```

## Regenerate

From the repo root:

```sh
dotnet run --project tools/Packet.Sdl.CodeGen -- \
  --in spec-sdl \
  --out src/Packet.Ax25.Sdl \
  --tests tests/Packet.Ax25.Conformance.Tests \
  --ts ts-spec/src/ax25sdl
```

The orchestrator emits C#, Go, and TypeScript in the same pass so all
three stay in lockstep. CI verifies the generated files are checked
in (idempotent regen + `tsc --noEmit` + `node --test`).

## Local development

```sh
cd ts-spec
npm ci                    # install devDependencies
npm run typecheck         # tsc --noEmit
npm test                  # node --test against src/**/*.test.ts
npm run build             # emit JS + .d.ts to dist/
```

Node 22+ is required (the test runner uses
`--experimental-strip-types` to load `.ts` directly).

## What's not here

This package is **specification data**, not a runtime. It exposes the
SDL transitions, subroutine bodies, predicates, and action verbs as
plain TS values. Building an actual AX.25 session on top requires
binding predicates and action verbs to behaviour (the TS equivalent of
`Packet.Ax25.Session.GuardEvaluator` / `ActionDispatcher` /
`DefaultSubroutineRegistry`) and wiring it to frame I/O (WebSerial,
AGWPE-over-WebSocket, etc.). That work is intentionally out of scope
for the Tier 1c codegen; the goal here is to prove the codegen IR
survives a third backend.
