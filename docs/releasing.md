# Releasing packet.net

How a packet.net change reaches the world. This is the **release cascade**: a single substantive merge to `main` fans out into NuGet packages, `.deb`s, downstream app releases, and (when the TS library moved) an npm publish + a static-site redeploy. It is tag-driven and mostly automated, but the *order* and the *downstream fan-out* are easy to forget — hence this doc.

> **TL;DR** — on green `main`: tag `lib-v<semver>` and `node-v<semver>` → CI publishes NuGet + builds a `.deb` GitHub Release. Then bump the four `Packet.*` pins in `packet-net/axcall` and `packet-net/packet-term-tui` and cut their releases. If `ax25-ts` also changed, release it to npm and bump `packet-net/packet-term-web`'s pin. Finally, record the whole arc in `docs/plan.md` §17.

## When to release

After a **substantive** merge to `main` — a library behaviour change, a node-host fix/feature, a new named flag. Not for plan-only edits, dev-tooling (the link bench), or test-only changes. When in doubt, a release is cheap and the libraries/`.deb`s being a few commits behind `main` is the thing to avoid (the fixes only reach users once tagged).

The libraries and the node host version **in lockstep** in practice (`lib-v0.8.0` + `node-v0.8.0`), even though their tags are independent. Pre-1.0, a normal release is a **minor** bump (`0.7.0 → 0.8.0`) whether the changes are features or fixes; reserve patch bumps (`0.8.0 → 0.8.1`) for a hotfix on top of a release. The version lives **only in the tag** — the publish workflows pass `-p:Version=$VERSION` / read `${GITHUB_REF#refs/tags/...}`; there is no `<Version>` to edit in any `.csproj` or `Directory.Build.props`.

## Step 0 — verify `main` is green *before* tagging

Tag only a commit whose **`ci` and `interop`** workflows both went green **on the merge commit on `main`** (not just on the PR):

```sh
gh run list --repo packet-net/packet.net --branch main --limit 6
```

Both `ci` and `interop` must show `success` for the HEAD merge commit. The interop job stands up a docker stack on the self-hosted runner.

**Investigate test failures — don't reflexively re-run.** This used to list "known flake classes" to wave through, but every one we actually chased turned out to be a *real bug* hiding behind runner contention: a `NetRomCircuit` teardown deadlock, an `Ax25Session` connect-confirm/`CurrentState` ordering race, and several test-side timing/ordering races (plan §17, 2026-06-19/20). The playbook that found them — reproduce under CPU load (`nproc-1` busy loops), and for a hang add `--blame-hang` + `dotnet-dump` `syncblk`/`clrstack` — is the default response to a red, not `rerun`. A test failing, *especially the same test twice*, is a bug to fix.

The one genuinely re-runnable, non-code blip is a bare `Internal CLR error (0x80131506)` / exit 134 during `dotnet build` (a runtime/host hiccup). For that — and only that — `gh run rerun <run-id> --failed`.

## Step 1 — tag the libraries → NuGet (`publish-libs.yml`)

```sh
git fetch origin main
git tag -a lib-v<semver> origin/main -m "lib-v<semver> — <one-line summary>"
git push origin lib-v<semver>
```

The `lib-v*` tag triggers [`.github/workflows/publish-libs.yml`](../.github/workflows/publish-libs.yml), which packs and `dotnet nuget push`es the published packages:

- `Packet.Core`
- `Packet.Kiss`
- `Packet.Kiss.Serial`
- `Packet.Kiss.NinoTnc`
- `Packet.Ax25`
- `Packet.Ax25.Transport.Abstractions`
- `Packet.Axudp`
- `Packet.Aprs`
- `Packet.Agw`
- `Packet.NetRom`
- `Packet.Rhp2`
- `Packet.Radio`
- `Packet.Radio.Tait`

**The `projects:` matrix in the workflow is the authoritative list** — the list above is a 2026-07-02 snapshot (this doc had previously drifted from the matrix). `Packet.Node*` and `Packet.Rhp2.Server` are **not** on the NuGet publish set — add to the `projects:` matrix in the workflow if that changes. The version is the tag minus the `lib-v` prefix. Publishing needs the `NUGET_API_KEY` secret (set on the self-hosted runner org/repo); a missing key downgrades to a warning and *skips* the push, so check the run actually pushed.

Then **wait for nuget.org flat-container indexing (~5–10 min)** before any downstream bump — a consumer `dotnet restore` against an unindexed version 404s.

## Step 2 — tag the node host → `.deb` GitHub Release (`publish-node.yml`)

```sh
git tag -a node-v<semver> origin/main -m "node-v<semver> — <one-line summary>"
git push origin node-v<semver>
```

The `node-v*` tag triggers [`.github/workflows/publish-node.yml`](../.github/workflows/publish-node.yml): it builds **amd64 / arm64 / armhf** self-contained `.deb`s (arm cross-published from x64 via crossgen2 R2R — serial, because three concurrent cross-publishes OOM-kill), install-smokes the amd64 one on Debian-stable + Ubuntu-LTS, and `gh release create node-v<semver>` with the `.deb`s + `SHA256SUMS` attached. The web UI is built into the `.deb` (`npm ci && vite build` inside `scripts/build-deb.sh`). It's independent of the `lib-v*` tag — the node builds from `ProjectReference`s, not NuGet.

Verify the release is **non-draft with 4 assets** (three `.deb`s + `SHA256SUMS`):

```sh
gh release view node-v<semver> --repo packet-net/packet.net
```

Both tags can be pushed together; the two workflows run independently.

### Optional — move the lab to the release `.deb`

The lab (`root@pdn-lab`, M9YYY) is usually running a dev `.deb` from [`scripts/deploy-node.sh`](../scripts/deploy-node.sh) (version `0.1.0+dev<stamp>`). It already has the released *code* (deploy-node ships the same build), so a redeploy is optional; do it only to align versions or pick up the release artifact shape. `deploy-node.sh` keeps the box's edited `/etc/packetnet/packetnet.yaml`.

## Step 3 — downstream .NET consumers (`axcall`, `packet-term-tui`)

Two public app repos pin the `Packet.*` NuGet packages:

- [`packet-net/axcall`](https://github.com/packet-net/axcall)
- [`packet-net/packet-term-tui`](https://github.com/packet-net/packet-term-tui)

For each: open a PR bumping the four `Packet.*` pins in its `Directory.Packages.props` to the new version, **build + test locally against the freshly-published NuGet first** (so an indexing lag or a packaging slip is caught before merge), merge on green, then tag `v0.x.y` — their `release.yml` builds the six-platform binaries; verify the assets attached. Do this only after Step 1's NuGet indexing has settled.

## Step 4 — the TS leg (only when `ax25-ts` changed)

[`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts) mirrors this repo's behaviour and is parity-CI-enforced (see [CLAUDE.md](../CLAUDE.md) → "ax25-ts parity"). It only needs a release when *it* changed this cycle — e.g. a new named-flag parity leg landed there alongside a flag here. If so:

1. ax25-ts release PR: promote its `CHANGELOG` `[Unreleased]` → versioned, `npm pkg set version <semver>`, merge, tag `v<semver>` → its `publish.yml` → npm.
2. Bump [`packet-net/packet-term-web`](https://github.com/packet-net/packet-term-web)'s esm.sh pin in `index.html` (~line 423). That repo has **no CI and branch protection**, so the pin-bump merge needs `--admin`; pushing to `main` auto-deploys to packet-term.m0lte.uk via OARC object storage (see the OARC publish notes — self-hosted-runner image gotchas apply).

If `ax25-ts` did **not** change this cycle, skip Step 4 entirely.

## Step 5 — record it in the plan ledger

Add a `docs/plan.md` §17 amendment-log entry capturing the whole arc: the `lib-v*`/`node-v*` tags, the downstream consumer PRs/releases, and the TS leg if any. This is the durable record of what shipped when. Precedent ledgers: the §17 entries dated 2026-06-04 (V5c) and 2026-06-10. (This `docs/` change goes through the normal PR + `plan-check` flow; a tag push itself is not a plan event, but the *release* is.)

## Repo visibility

`packet-net/packet.net` is **private**. `packet-net/ax25-ts`, `packet-net/axcall`, `packet-net/packet-term-tui`, and `packet-net/packet-term-web` are **public** — mind what release notes say.

## Quick reference

| Tag / action | Workflow | Produces |
|---|---|---|
| `lib-v<semver>` | `publish-libs.yml` | 6 NuGet packages on nuget.org |
| `node-v<semver>` | `publish-node.yml` | amd64/arm64/armhf `.deb`s on a GitHub Release |
| `packet-net/axcall` `v*` | its `release.yml` | six-platform app binaries |
| `packet-net/packet-term-tui` `v*` | its `release.yml` | six-platform app binaries |
| `packet-net/ax25-ts` `v*` | its `publish.yml` | npm package |
| push to `packet-net/packet-term-web` `main` | OARC auto-deploy | packet-term.m0lte.uk |
