# CLAUDE.md

Operating notes for Claude Code (and other agents) working in `packet-net/packet.net`.

## What this repo is

The .NET libraries (`Packet.Core`, `Packet.Ax25`, `Packet.Kiss`, `Packet.Aprs`, `Packet.Agw`, `Packet.Axudp`, `Packet.Kiss.NinoTnc`, `Packet.Mcp`, `Packet.Rhp2*`) and the packet-radio node host (`Packet.Node*`). Plus C# interop CI (LinBPQ / XRouter / rax25 / netsim / NinoTNC-loop) — the `interop.yml` workflow also clones [`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts) and runs its integration suite against the docker stack standing up here.

After the 5-repo split on 2026-05-17, the SDL transcriptions, codegen, and multi-language artefacts live in [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl), and the TypeScript library lives in [`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts). See [`README.md` § Sibling repos](README.md#sibling-repos). If a task is about spec-side work (transcribing a new figc4.x page, the YAML DSL, the codegen tools, the `Packet.Ax25.Sdl` package), it belongs in `packet-net/ax25sdl`. If it's about the browser AX.25 library (`@packet-net/ax25`), it belongs in `packet-net/ax25-ts`. Not here.

## Read first

**[`docs/plan.md`](docs/plan.md) is the living source of truth.** Read it before doing anything substantive. Pay particular attention to:

- §2 Working agreements — these take precedence over your defaults.
- §17 Amendment log — what changed and why. The 2026-05-17 entries cover the recent multi-repo split.
- §18 How to update this document — you are expected to keep `docs/plan.md` current as part of any work you do.

## Hard rules

### Keep the plan current

`docs/plan.md` §17 Amendment log is updated *in the same PR* as the work that triggers it. If you complete a phase exit criterion and don't update the status and add a log entry, you have not finished the task. See §18 of the plan for the full discipline.

Override: include `[skip-plan]` or `[no-plan-update]` in any commit message or the PR description. Reach for this only when the change is genuinely plan-irrelevant (typo fix, hotfix without behavioural change, test-only refactor).

### Spec-compliant by default; pragmatism is a named flag

Packet.NET's philosophy: **the libraries produce and accept exactly what AX.25 v2.2 / APRS101 / KISS-TNC-protocol describe by default.** Pragmatic accommodations for real-world peers (BPQ, Xrouter, direwolf, the wild APRS-IS feed, station firmwares) exist — but they're **named** flags on a `…ParseOptions` record, not silent defaults baked into the parser.

When you encounter a frame the strict spec rejects:

1. **Don't widen the parser silently.** Trace the spec section that the wire data violates and confirm the violation is real (sometimes the spec table contradicts the spec prose — see [`docs/strict-vs-pragmatic-audit.md`](docs/strict-vs-pragmatic-audit.md) for examples — and the "violation" is actually a spec interpretation, not pragmatism).
2. **If it's genuine pragmatism**, add a named flag to `Ax25ParseOptions` or `AprsParseOptions` (whichever layer it belongs to). Default the flag to whatever preserves current behaviour (lenient if we already accepted it; strict if it's new acceptance).
3. **Update the audit doc** ([`docs/strict-vs-pragmatic-audit.md`](docs/strict-vs-pragmatic-audit.md)) with a row capturing: location, what we accept, what strict spec says, the real-world driver, the flag name, the default.
4. **Pick presets**: decide which of `Strict` / `Lenient` / peer-specific (`Bpq`, `Xrouter`, `Direwolf`, `AprsIs`) presets should set the flag on.
5. **Write a paired test**: one assertion that `Strict` rejects the wire input, one that the relevant preset (or `Lenient`) accepts it. Strict-rejects-without-lenient-accepts is a code smell — it means we couldn't justify why the leniency exists.

**Don't take BPQ, Xrouter, direwolf or any other implementation as the spec.** They are interop targets, not reference truth. Their quirks are flag-gated behaviour for us; the spec is the canonical behaviour.

The outbound construction path (frame factories, encoder construction-time `Callsign`) stays strict — we never produce frames that violate spec, even when we accept inbound frames that do.

### ax25-ts parity is CI-enforced — a new named flag needs its TS leg

[`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts) tracks this repo's libraries behaviour-for-behaviour, and the `interop.yml` job runs its `scripts/parity-check.mjs` against **this PR's head** + ax25-ts `main`: it compares the named-flag inventories (`Ax25ParseOptions`, `Ax25SessionQuirks`, `XidParseOptions`), the presets, and the `Ax25Listener` options/surface, and **fails on any undocumented gap**. So a PR here that adds a named flag (step 2 above) or widens the listener surface will fail interop until either (a) the TS counterpart ships in ax25-ts (preferred — land it first or alongside), or (b) a *reviewed* exception with a reason is recorded in ax25-ts `scripts/parity-exceptions.json` (ship that first, then re-run). The mirror check runs in ax25-ts's own CI, so drift fails on whichever side introduces it.

### Releasing is a tag-driven cascade — follow the doc

Shipping a change to the world is more than merging the PR: it's a **release cascade** (tag `lib-v*`/`node-v*` on green main → NuGet + `.deb`s → downstream `axcall`/`packet-term-tui` releases → TS leg if `ax25-ts` moved → plan §17 ledger). The full ordered procedure — preconditions, what each tag publishes, the downstream fan-out, and the green-CI gate (investigate reds, don't reflexively re-run — they keep turning out to be real bugs) — lives in **[`docs/releasing.md`](docs/releasing.md)**. Read it before tagging; don't rediscover it.

### SDL state-machine library is a NuGet dependency

The AX.25 SDL state machine tables come from the [`Packet.Ax25.Sdl`](https://www.nuget.org/packages/Packet.Ax25.Sdl) NuGet package, built and published by [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl). **Do not** try to regenerate, edit, or extend the SDL state machines from this repo — they don't live here. If a change is needed, raise it against `packet-net/ax25sdl`, publish a new version, and bump the `Packet.Ax25.Sdl` pin in [`Directory.Packages.props`](Directory.Packages.props).

## Common commands

```sh
# Build everything (libraries, tests, node host)
dotnet build

# Run the normal test suite (excludes hardware-loop and interop)
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"

# Bring up the interop stack (LinBPQ + Xrouter + rax25 + net-sim)
docker compose -f docker/compose.interop.yml up -d --wait

# Tear down the interop stack
docker compose -f docker/compose.interop.yml down -v

# Run C# interop tests (requires the stack up)
dotnet test --filter "Category=Interop"

# Run hardware-loop tests (requires 2x NinoTNC over USB)
dotnet test --filter "Category=HardwareLoop"
```

## Test category filter convention

- Default: `--filter "Category!=HardwareLoop&Category!=Interop"` — what CI's `ci.yml` runs.
- Hardware loop: `--filter "Category=HardwareLoop"` — only on the self-hosted runner with TNCs attached.
- Interop: `--filter "Category=Interop"` — only on the interop CI job after the compose stack is up.

Apply traits via `[Trait("Category", "HardwareLoop")]` or `[Trait("Category", "Interop")]` on the test class.

## Conditional skips

xUnit 2 has no native `Skip` API. Use `Xunit.SkippableFact` from the `Xunit.SkippableFact` package:

```csharp
[SkippableFact]
public void NeedsHardware()
{
    Skip.If(ports.Count < 2, "no TNCs attached");
    // ...
}
```

## Working with `Directory.Packages.props`

Central Package Management is in effect. **Do not** put `Version=` attributes on `<PackageReference>`. Add the version to `Directory.Packages.props` instead, then reference the package by name. Test projects inherit common test dependencies from `tests/Directory.Build.props`.

## What lives where

```
src/Packet.*                     .NET libraries (NuGet-publishable; see README for which are published)
src/Packet.Node*                 the packet-radio node host (web SDK; the deployable binary)
tests/Packet.*.Tests             one test project per library
tests/.../Hardware/              hardware-loop-only tests
tests/Packet.Interop.Tests/      C# interop CI tests against the docker stack
tools/Packet.*.Spike/            scratch experiments
tools/Packet.Fuzz/               AFL-style fuzzer for the AX.25 / KISS parsers
sidecar/tsnet/                   embedded Tailscale node (Go; built by build-deb.sh, staged at /usr/lib/packetnet/packetnet-tsnet)
docker/                          interop compose stack (LinBPQ + XRouter + rax25 + netsim) + fixtures
docs/                            plan, ADRs, runtime capability docs, strict/pragmatic audit
.github/workflows/               CI (ci.yml + interop.yml + plan-check.yml + publish-libs.yml)
```

## Things to avoid

- Don't add `[Version=...]` on `<PackageReference>` items — CPM enforces a central version table.
- Don't write `appsettings.Local.json` to git. It's `.gitignore`d for a reason.
- Don't add new GitHub Actions jobs with `runs-on: ubuntu-latest` (or any other GitHub-hosted runner label). This project has no Actions minutes budget for hosted runners — every workflow job MUST target `[self-hosted, Linux, X64]`, matching the existing CI / interop / publish-libs jobs. Reach for hosted runners only after Tom explicitly authorises a budget for them.
- Don't try to edit the SDL state machines from this repo. They come from [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl) via the `Packet.Ax25.Sdl` NuGet package — raise spec-side issues there.

## When in doubt

Ask Tom. The cost of a clarifying question is much lower than the cost of a load-bearing wrong assumption — especially in AX.25 territory.
