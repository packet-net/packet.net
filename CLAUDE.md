# CLAUDE.md

Operating notes for Claude Code (and other agents) working in this repo.

## Read first

**[`docs/plan.md`](docs/plan.md) is the living source of truth.** Read it
before doing anything substantive. Pay particular attention to:

- §2 Working agreements — these take precedence over your defaults.
- §6 SDL transcription discipline — different rules apply to SDL work.
- §18 How to update this document — you are expected to keep `docs/plan.md`
  current as part of any work you do.

Other essential reading:

- [`docs/sdl-primer.md`](docs/sdl-primer.md) — SDL shape reference. Mandatory
  before touching `/spec-sdl/`.
- [`docs/sdl-transcription-runbook.md`](docs/sdl-transcription-runbook.md) —
  end-to-end per-figure workflow (graphml → transcription PR → validation
  PR). Read this when starting a new SDL page.
- [`docs/sdl-verb-catalogue.md`](docs/sdl-verb-catalogue.md) — how
  `spec-sdl/actions.yaml` normalises figure-verbatim action spellings to
  canonical verbs at codegen time.
- [`docs/adr/0001-sdl-dsl.md`](docs/adr/0001-sdl-dsl.md) — why the SDL YAML
  DSL + codegen exists.

## Hard rules

### Trust the figure (SDL work)

The AX.25 SDL figures are the source of truth. When a figure surprises you,
the surprise is yours. **Do not** "fix" a branch label, swap a Yes/No, or
substitute "correct-looking" actions on the basis that the figure looks wrong
to you. If you're uncertain, flag for human review with a
`verification_pending:` note — never silently deviate.

This was the most painful lesson of Phase 0. See `docs/plan.md` §2.1 and the
amendment-log entry from 2026-05-11.

### Encode-then-verify (SDL work)

Every transition in `/spec-sdl/` must come from an **explicit human-authored
transcription** of the figure. You may *encode* paths that Tom has described
in plain text; you may not *infer* paths by reading the PNG yourself.

### Pin implementation evidence (SDL work)

When transcribing any SDL transition whose semantics are non-obvious,
cross-reference how at least one of the canonical implementations handles it
(see `docs/plan.md` §13.2). Drop the citation into the transition's `notes:`
field.

### Reading SDL graphml: `d5` is the authoritative shape class (SDL work)

Each node in a `spec-sdl/**/*.graphml` file carries a `<data key="d5">`
description (e.g. "Signal reception from Lower Layer", "Signal generation
to upper layer", "Processing description", "Test or decision"). That `d5`
text is the **only** authoritative source for the node's shape class.

**Do not** infer a node's meaning from the visual direction of its
parallelogram (left-notch vs right-notch) or from your prior understanding
of which layer DL-\* vs frame events "should" come from. The figures in
the AX.25 spec do not always use shape direction the way figc1.1's legend
suggests — for example, figc4.1 draws upper-layer DL-\* primitives with
the "Signal reception from Lower Layer" shape. The figure is the source
of truth (§2.1); `d5` records the figure's choice verbatim; we transcribe.

When the same label appears under two different `d5` values, those are
**two distinct events** in the catalogue. Disambiguate with a
`__from_<shape-class>` suffix on the event id.

When referring to a shape class in writing, quote `d5` verbatim (e.g.
"`Signal reception from upper layer`"). Don't paraphrase from the shape's
appearance.

### Keep the plan current

`docs/plan.md` §17 Amendment log is updated *in the same PR* as the work that
triggers it. If you complete a phase exit criterion and don't update the
status and add a log entry, you have not finished the task. See §18 of the
plan for the full discipline.

### Spec-compliant by default; pragmatism is a named flag

Packet.NET's philosophy: **the libraries produce and accept exactly
what AX.25 v2.2 / APRS101 / KISS-TNC-protocol describe by default.**
Pragmatic accommodations for real-world peers (BPQ, Xrouter,
direwolf, the wild APRS-IS feed, station firmwares) exist — but
they're **named** flags on a `…ParseOptions` record, not silent
defaults baked into the parser.

When you encounter a frame the strict spec rejects:

1. **Don't widen the parser silently.** Trace the spec section that
   the wire data violates and confirm the violation is real
   (sometimes the spec table contradicts the spec prose — see
   `docs/strict-vs-pragmatic-audit.md` for examples — and the
   "violation" is actually a spec interpretation, not pragmatism).
2. **If it's genuine pragmatism**, add a named flag to
   `Ax25ParseOptions` or `AprsParseOptions` (whichever layer it
   belongs to). Default the flag to whatever preserves current
   behaviour (lenient if we already accepted it; strict if it's
   new acceptance).
3. **Update the audit doc** (`docs/strict-vs-pragmatic-audit.md`)
   with a row capturing: location, what we accept, what strict
   spec says, the real-world driver, the flag name, the default.
4. **Pick presets**: decide which of `Strict` / `Lenient` /
   peer-specific (`Bpq`, `Xrouter`, `Direwolf`, `AprsIs`) presets
   should set the flag on.
5. **Write a paired test**: one assertion that `Strict` rejects
   the wire input, one that the relevant preset (or `Lenient`)
   accepts it. Strict-rejects-without-lenient-accepts is a code
   smell — it means we couldn't justify why the leniency exists.

**Don't take BPQ, Xrouter, direwolf or any other implementation as
the spec.** They are interop targets, not reference truth. Their
quirks are flag-gated behaviour for us; the spec is the canonical
behaviour.

The outbound construction path (frame factories, encoder
construction-time `Callsign`) stays strict — we never produce
frames that violate spec, even when we accept inbound frames that
do.

## Common commands

```sh
# Build everything (libraries, tools, tests)
dotnet build

# Run the normal test suite (excludes hardware-loop and interop)
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"

# Regenerate SDL state machines after editing a *.sdl.yaml.
# With no flags, emits C# under src/Packet.Ax25.Sdl, Go under
# go-spec/ax25sdl, and TypeScript under ts-spec/src/ax25sdl in one
# pass — requires `gofmt`, `node`, and `npm` on PATH. (`sudo apt-get
# install -y golang-go nodejs npm` covers all three.)
#
# Pass any of --csharp / --go / --ts (with or without paired
# --csharp-out / --csharp-tests / --go-out / --ts-out) to emit only
# the named backend(s). See `dotnet run --project tools/Packet.Sdl.CodeGen
# -- --help` for the full surface.
dotnet run --project tools/Packet.Sdl.CodeGen

# Verify the generated Go compiles + passes gofmt
cd go-spec && go build ./... && go vet ./... && go test ./... && gofmt -l .

# Verify the generated TS typechecks + tests pass
cd ts-spec && npm ci && npm run typecheck && npm test

# Bring up the interop stack (LinBPQ + Xrouter + net-sim)
docker compose -f docker/compose.interop.yml up -d --wait

# Tear down the interop stack
docker compose -f docker/compose.interop.yml down -v

# Run hardware-loop tests (requires 2x NinoTNC over USB)
dotnet test --filter "Category=HardwareLoop"
```

## Test category filter convention

- Default: `--filter "Category!=HardwareLoop&Category!=Interop"` — what CI's
  `ci.yml` runs.
- Hardware loop: `--filter "Category=HardwareLoop"` — only on the self-hosted
  runner with TNCs attached.
- Interop: `--filter "Category=Interop"` — only on the interop CI job after
  the compose stack is up.

Apply traits via `[Trait("Category", "HardwareLoop")]` or
`[Trait("Category", "Interop")]` on the test class.

## Conditional skips

xUnit 2 has no native `Skip` API. Use `Xunit.SkippableFact` from the
`Xunit.SkippableFact` package:

```csharp
[SkippableFact]
public void NeedsHardware()
{
    Skip.If(ports.Count < 2, "no TNCs attached");
    // ...
}
```

## Working with `Directory.Packages.props`

Central Package Management is in effect. **Do not** put `Version=` attributes
on `<PackageReference>`. Add the version to `Directory.Packages.props`
instead, then reference the package by name. Test projects inherit common
test dependencies from `tests/Directory.Build.props`.

## What lives where

```
src/Packet.*                     libraries (NuGet-publishable)
src/Packet.Ax25.Sdl              GENERATED — do not hand-edit
tests/Packet.*.Tests             one test project per library
tests/.../Hardware/              hardware-loop-only tests
spec-sdl/                        YAML DSL — human-authored transcriptions
spec-sdl/schema/                 JSON Schema for the DSL
spec-sdl/events.yaml             canonical event catalog
tools/Packet.Sdl.IR/             language-neutral IR + validation
tools/Packet.Sdl.CodeGen.Csharp/ C# emitter (Scriban + Roslyn)
tools/Packet.Sdl.CodeGen.Go/     Go emitter (hand-rolled, gofmt-finalised)
tools/Packet.Sdl.CodeGen.Ts/     TypeScript emitter (hand-rolled)
tools/Packet.Sdl.CodeGen/        thin orchestrator (driver)
tools/Packet.Sdl.Lint/           standalone schema lint
tools/Packet.*.Spike/            scratch experiments
go-spec/                         Go module — GENERATED .g.go + hand-written types.go
ts-spec/                         npm package — GENERATED .g.ts + hand-written types.ts
docker/                          interop compose stack + fixtures
docs/                            plan, ADRs, primers
.github/workflows/               CI
```

## Things to avoid

- Don't hand-edit `src/Packet.Ax25.Sdl/*.g.cs`,
  `tests/Packet.Ax25.Conformance.Tests/*.g.Tests.cs`,
  `go-spec/ax25sdl/*.g.go`, or `ts-spec/src/ax25sdl/*.g.ts`. They are
  generated. Edit the corresponding `*.sdl.yaml` and rerun the codegen.
  (`go-spec/ax25sdl/types.go`, `ts-spec/src/ax25sdl/types.ts`, and
  `ts-spec/src/ax25sdl/*.test.ts` ARE hand-written — keep the type
  files in sync with the C# types in `src/Packet.Ax25.Sdl/`.)
- Don't add `[Version=...]` on `<PackageReference>` items — CPM enforces a
  central version table.
- Don't write `appsettings.Local.json` to git. It's `.gitignore`d for a
  reason.
- Don't file upstream issues against `packethacking/ax25spec` without an
  explicit ask from Tom. Capture suspected spec issues as
  `verification_pending:` notes in the relevant `*.sdl.yaml` instead.
- Don't infer protocol semantics from the spec PNGs. See "Encode-then-verify"
  above.
- Don't add new GitHub Actions jobs with `runs-on: ubuntu-latest` (or any other
  GitHub-hosted runner label). This project has no Actions minutes budget for
  hosted runners — every workflow job MUST target `[self-hosted, Linux, X64]`,
  matching the existing CI / interop / npm-publish jobs. Reach for hosted
  runners only after Tom explicitly authorises a budget for them.

## When in doubt

Ask Tom. The cost of a clarifying question is much lower than the cost of a
load-bearing wrong assumption — especially in AX.25 territory.
