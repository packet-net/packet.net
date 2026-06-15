# pdn app catalog — design / ADR

How a node operator discovers and installs **vetted apps that are available but not yet installed**, entirely from the browser, without those apps' payloads bloating the node `.deb`. Phase 4 / app-platform Slice 6. Status: design (no code yet); decisions settled with Tom 2026-06-13.

User-facing, this is the **"Available apps"** view in the control panel — apps the node knows about and can install on request. Internally the index of those apps is the **catalog**. (We deliberately do *not* call it a "store".)

This builds on the existing package model ([`app-packages.md`](app-packages.md)) — discovery of `pdn-app.yaml` packages under two roots, the local `IAppPackageCatalog`, the `AppServiceSupervisor`, the enable/disable API+UI, the `IWritableConfigProvider` write seam. It changes **how a package gets onto a node**: instead of bundling app payloads into the node deb, pdn ships a small **catalog** (an index of available apps), and fetches a chosen app on demand into the owner-installed root, where the existing discovery picks it up.

## Context — the problem

Today the node deb bundles app payloads. WALL and LOBBY are a few KB of stdlib Python and cost nothing. **DAPPS is the problem:** `build-deb.sh` fetches DAPPS's per-arch self-contained .NET binary from its published GitHub release, pins it by sha256, and stages it into the deb — adding **~33 MB per arch**, which took the deb from **~39.8 MB to ~73.6 MB**. This does not scale: **pdn-bpqchat**, **pdn-convers**, the **BBS** and **whatspac-client** are all coming and several are heavyweight. Bundling the app fleet into the node deb is the wrong distribution model for everything except the trivial reference apps.

The fix is to ship the **index**, not the payloads. The deb carries a small catalog file; the operator browses available apps in the control panel and installs the ones they want; pdn fetches each app's artifact (pinned by sha256 in the catalog), verifies it, and stages it into the owner-installed root — at which point it is an ordinary discovered-but-disabled package and the entire existing flow takes over.

Sideloading is untouched: an operator can still drop a package dir into `/var/lib/packetnet/apps/<id>/` by hand, `apt install` a third-party app `.deb` that lands one under `/usr/share/packetnet/apps/`, or author inline `applications:` entries. The catalog is an *additional, easy* path, never the only one.

## Decisions (settled 2026-06-13)

1. **Embedded catalog now (A1), designed so a signed network refresh (A2) is a drop-in.** v1 ships a `catalog/apps.yaml` committed in this repo and baked into the deb at `/usr/share/packetnet/catalog/apps.yaml`. Its trust derives from the deb's own integrity (the same trust roots the self-updater already established — apt's repo GPG signature on the apt channel; the release `SHA256SUMS`/cosign on the self-contained channel). The list of available apps and their advertised versions therefore moves at **pdn-release cadence** in v1 — acceptable, because the deb-size win is fully realised regardless, and A2 lifts the cadence limit later without reshaping anything. The catalog reader is a seam (`IAppCatalog`) with the embedded file as one implementation; A2 adds a refreshing implementation + a verifier behind the same seam.
2. **DAPPS moves out of the deb and into the catalog.** This is the concrete size win: it reverts each arch deb from ~73.6 MB to ~40 MB. DAPPS becomes the flagship catalog entry — one click to install, then the existing enable grant. (This refines the earlier "DAPPS ships *with* pdn" decision: it still ships *with* pdn in the sense of being the curated, top-of-list, one-click app — just not as bytes in the deb.)
3. **Both faces of the installer in v1: URL-fetch and browser upload.** The installer's job is "produce a verified package dir under `/var/lib/packetnet/apps/<id>/`"; the *source* of the bytes is pluggable — a URL pinned in the catalog, or a `.pdnapp` file uploaded through the control panel. The upload path covers air-gapped / RF-only nodes (the operator downloads on a laptop, uploads through the browser) and satisfies the `.pdnapp`-upload feature foreshadowed in [`app-packages.md`](app-packages.md) § Distribution.

## The initial vetted set

| id | source repo | shape | catalog `kind` |
|----|-------------|-------|----------------|
| `dapps`   | `packet-net/dapps`           | per-RID raw binary + `pdn-app.yaml` asset | `assets` |
| `bpqchat` | `packet-net/pdn-bpqchat`| per-arch `.deb` (Go static)               | `deb` |
| `convers` | `packet-net/pdn-convers`| per-arch `.deb` (.NET self-contained)     | `deb` |

WALL and LOBBY stay **bundled** in the node deb (tiny, good first-boot OOBE); they appear in the installed-packages list, not the available list.

## Why this is mostly relocation, not new machinery

The fetch-verify-stage logic already exists — in `scripts/build-deb.sh` (the DAPPS block), written in bash and run at **build** time. The catalog moves it into the node, written in C# and run at **install** time, driven by a catalog entry instead of hardcoded pins. Everything downstream of "a package dir exists on disk" is already built:

- Discovery (`AppPackageCatalog`, `src/Packet.Node.Core/Applications/Packages/`) scans `/usr/share/packetnet/apps` then `/var/lib/packetnet/apps` **live on every call** and on every config apply — a freshly installed package appears with no restart.
- A discovered package is **disabled until the owner enables it** — installing lands bits, it does not run them.
- Enable/disable/restart, the supervisor lifecycle, the launcher tiles, the reverse-proxy gateway — all unchanged.

## Trust model

The chain is exactly apt's `Release → Packages → .deb` hash chain:

- **The catalog is the trust root.** In v1 (A1) it is anchored by the deb's integrity (above). In A2, a network-refreshed catalog is verified by a detached signature against a public key shipped in the deb — the same cosign seam the self-updater stubbed (`docs/node-self-update-design.md`, OQ-003 / #188). Until A2 ships, there is no network-fetched catalog to forge.
- **Every artifact is sha256-pinned in the (authentic) catalog.** So an artifact hosted on a third-party GitHub release over plain HTTPS is tamper-evident: a mismatched hash is a hard refusal, never a staged file. The pins come straight from GitHub's recorded per-asset digests. "Vetted" means **Tom chose the version and the hash** — identical in meaning to today's `build-deb.sh` pins, relocated to a YAML that a small regen script can refresh (see § Catalog maintenance).
- **Install ≠ enable.** Install writes a **disabled** package; enabling is the separate, deliberate, admin-scoped human run-grant that already exists. A forged catalog (only reachable once A2 exists) therefore still cannot auto-execute code — the worst it can do is drop files, bounded by an https-only fetch policy, a download size cap, and the hash refusal. This separation is load-bearing; keep it.
- **No new privilege — including for `.deb` apps.** The install root `/var/lib/packetnet/apps/<id>/` is writable by the unprivileged `packetnet` service user. A `kind: deb` artifact is **extracted, not installed**: `dpkg-deb -x` unpacks the `.deb`'s `data.tar` (no maintainer scripts, no dpkg registration) and pdn relocates the `usr/share/packetnet/apps/<id>/` subtree into the owner root. dpkg never learns about it, so the "one owner per file" rule (see `node-self-update-design.md`) is respected — these files live under `/var/lib`, never the dpkg-owned `/usr/share`. Unlike self-update (which needs the `packetnet-update.service` polkit seam to touch dpkg-owned files), the catalog installer runs **in-process as the service user** with no root and no helper. Installs are **admin-scoped** and **audited** (`IAuditLog`), the same as enable and the self-update Apply.

## The catalog file — `catalog/apps.yaml`

Committed in this repo; staged into the deb at `/usr/share/packetnet/catalog/apps.yaml`. Schema (v1 proposed):

```yaml
catalog: 1                       # schema version, required

# A2 ONLY — absent in v1. When present, pdn refreshes the catalog from `url`,
# verifying `signatureUrl` against a pubkey shipped in the deb. The embedded
# file remains the offline baseline / floor.
# source:
#   url: https://pdn.m0lte.uk/catalog/apps.yaml
#   signatureUrl: https://pdn.m0lte.uk/catalog/apps.yaml.sig

apps:
  # kind: assets — a per-RID binary + a separately-fetched manifest (DAPPS today).
  - id: dapps                    # must equal the installed package's manifest id
    name: DAPPS
    version: "0.34.1"            # the curated version (drives "update available")
    description: Distributed Asynchronous Packet Pub/Sub — store-and-forward messaging.
    icon: inbox
    capabilities: [packet, web]  # shown to the owner at install/enable time (packet = full
                                 # packet-radio network access; renamed from network — see below)
    homepage: https://github.com/packet-net/dapps
    artifact:
      kind: assets
      manifest: { url: ".../v0.34.1/pdn-app.yaml", sha256: ae0b2f50… }
      binaries:
        linux-x64:   { url: ".../v0.34.1/dapps-linux-x64",   sha256: a509c31d…, dest: dapps, mode: "0755" }
        linux-arm64: { url: ".../v0.34.1/dapps-linux-arm64", sha256: 2205fed8…, dest: dapps, mode: "0755" }
        linux-arm:   { url: ".../v0.34.1/dapps-linux-arm",   sha256: 54d0d0a9…, dest: dapps, mode: "0755" }

  # kind: deb — a per-arch .deb, extracted (not installed); manifest comes from inside it.
  - id: bpqchat
    name: BPQ Chat
    version: "0.1.0"
    description: BPQ-Chat-compatible chat node — RF + web chat, peering with the BPQ Chat network.
    icon: chat
    capabilities: [packet, web]
    homepage: https://github.com/packet-net/pdn-bpqchat
    artifact:
      kind: deb
      debs:                      # RID → the per-arch .deb (Debian arch resolved from the RID)
        linux-x64:   { url: ".../v0.1.0/pdn-bpqchat_0.1.0_amd64.deb", sha256: eb614492… }
        linux-arm64: { url: ".../v0.1.0/pdn-bpqchat_0.1.0_arm64.deb", sha256: c9a55eb9… }
        linux-arm:   { url: ".../v0.1.0/pdn-bpqchat_0.1.0_armhf.deb", sha256: ba13b367… }

  # kind: pdnapp — the go-forward format AND the upload format: a tar.gz of the package
  # dir, manifest at root. One artifact for interpreted apps; a per-RID variant map for
  # compiled ones. (No catalog entry uses it yet; the UI upload path produces this shape.)
  # artifact:
  #   kind: pdnapp
  #   pdnapp: { url: …, sha256: … }
  #   # or: variants: { linux-x64: {url,sha256}, … }
```

Three `artifact.kind`s map onto the three real publishing shapes: **`assets`** consumes an app's existing per-RID release binaries + its upstream `pdn-app.yaml` with no change required of the app author; **`deb`** consumes an app's existing per-arch Debian packages by extraction (no root, no dpkg); **`pdnapp`** is the recommended go-forward format (a single tarball of the package dir, manifest at root) and the shape the UI upload produces. The Debian arch in a `.deb` filename maps from the RID: `linux-x64→amd64`, `linux-arm64→arm64`, `linux-arm→armhf`.

## Install flow

1. **List.** `GET /api/v1/apps/available` returns each catalog entry projected with the node's view: `installed` (is a package with this id present?), the installed `version` vs the catalog `version`, and `updateAvailable`. The node's RID (from `RuntimeInformation`) selects the right per-arch artifact for display/fetch.
2. **Install** (`POST /api/v1/apps/available/{id}/install`, admin, audited): resolve the artifact for this RID → download to a temp area → **sha256-verify against the catalog pin** (hard refusal on mismatch) → assemble the package dir in a temp dir: for `kind: assets` write the verified manifest + binary at their `dest`/`mode`; for `kind: deb` run `dpkg-deb -x` and lift the `usr/share/packetnet/apps/<id>/` subtree; for `kind: pdnapp` untar with a path-traversal-safe extractor → **commit** into `/var/lib/packetnet/apps/<id>/` (the `pdn-app.yaml` lands at the package-dir root, exactly like a hand-installed package) and write a `.pdn-install.json` **payload marker** (see O1). The package then appears as discovered-but-**disabled** on the next `Discover()` (immediate; disk is scanned live; discovery is unchanged). The operator enables it via the existing toggle.
3. **Upload** (`POST /api/v1/apps/packages/upload`, admin, audited): same staging pipeline, bytes from a multipart `.pdnapp` instead of a URL; the manifest id inside the tarball is authoritative and must equal the (validated) directory it lands in.
4. **Update**: re-run the install pipeline for an installed app at a newer catalog version — the marker's recorded payload is deleted and the new payload committed, **preserving the app's state** (the O1 marker makes this safe). Under A1, "update available" only surfaces at pdn-release cadence; A2 makes it live. **Auto-restart on update:** after the payload is committed, if the app is currently **enabled**, **pdn-managed** (`service.managed == pdn`), and **error-free**, the install endpoint drives the supervisor's `RestartAsync(id)` synchronously within the request so the freshly-laid binary actually runs. This is necessary because the supervisor only restarts a service when its *spawn fingerprint* (resolved command + args + working dir + merged environment) changes, and a version bump changes none of those — so a plain reconcile would keep the old process alive and the operator would otherwise have to restart by hand. The restart is **best-effort and never demotes a committed install to a failure**: a missing supervisor, a disabled/unmanaged/broken package, or any restart error yields `restarted: false` in the JSON response (alongside `ok: true`) and an audit line, never a 422 — the payload is already on disk and the operator can restart from the package row. A **fresh** install lands disabled (install ≠ enable), so nothing is restarted (`restarted: false`); the owner's separate enable grant is what first runs it.
5. **Uninstall** (`POST /api/v1/apps/packages/{id}/uninstall`, admin, audited): only for catalog/upload-installed packages (those carrying a marker, always under `/var/lib/packetnet/apps/`, never the dpkg-owned `/usr/share/...` root); requires the app to be **disabled first** (removes nothing that is running); removes exactly the marker's recorded payload files + the marker, leaving app-created state, and removes the dir only if it ends up empty. A marker-less (hand-sideloaded) dir is refused — pdn never deletes files it did not place.

## Surfaces

- `GET /api/v1/apps/available` — read scope. The available-apps list (catalog ⋈ installed state). Each entry's `capabilities` are **display-normalised** before projection (`network` → `packet`, the back-compat alias — see § Capabilities), so the install confirm shows the new spelling even for an app whose catalog/manifest entry still says `network`.
- `POST /api/v1/apps/available/{id}/install` — admin, audited. Install (or update) from the catalog. On a successful **update** of an already-enabled, pdn-managed, error-free app it also drives the supervisor's `RestartAsync` so the new binary runs (best-effort; reported as `restarted` in the response — see § Install flow item 4).
- `POST /api/v1/apps/packages/{id}/uninstall` — admin, audited. Beside the existing enable/disable/restart.
- `POST /api/v1/apps/packages/upload` — admin, audited; multipart `.pdnapp`.
- Existing `GET /api/v1/apps/packages` + enable/disable/restart are unchanged; an installed catalog app shows up there exactly like any other package.
- **UI**: an **"Available apps"** section on the Apps screen (`web/packetnet-ui/src/screens/apps.tsx`) listing not-yet-installed (and updatable) apps with Install / Update and the capability confirm step, plus an "upload a `.pdnapp`" affordance; Uninstall sits on the existing installed-package rows. The Apps screen is now **management-only** — the old launcher grid was dropped (enabled web apps are first-class **left-nav** entries now; see `docs/app-packages.md` § Surfaces); the page is "Available apps" + the package manager. No "Store" label anywhere.

## Capabilities

`capabilities` are free-form strings shown to the owner as the install/enable trust prompt (not enforced in v1). Conventional values: `session`, `packet`, `web`. **`packet`** — full packet-radio network access (the app binds a callsign and works over AX.25 / NET-ROM: RF / KISS-TCP / AXUDP / sim) — **replaces the old `network`** spelling, which read as TCP/IP / LAN. pdn still **accepts `network` as a back-compat alias** and display-normalises it to `packet` in every API projection (`AppCapabilities.Normalize`); the committed catalog ships `packet` directly. Full rationale + the back-compat rules: `docs/app-packages.md` § The `packet` capability.

## Implementation map

- `catalog/apps.yaml` — the committed catalog (and the canonical home of the DAPPS pins, moved out of `build-deb.sh`).
- `src/Packet.Node.Core/Applications/Catalog/` — catalog records + YAML parse + validation; `IAppCatalog` (v1: `EmbeddedAppCatalog` reading `/usr/share/packetnet/catalog/apps.yaml`; A2 adds a refreshing source + `ICatalogVerifier`). Mirrors the `SelfUpdate/` seam style. Named to distinguish from `IAppPackageCatalog` (which is *installed/discovered* packages); this is the *available* catalog.
- `src/Packet.Node.Core/Applications/Catalog/IAppInstaller.cs` + impl — fetch/verify/stage; RID-aware; https-only; size-capped; atomic place; accepts a URL (assets/deb/pdnapp) or uploaded bytes (pdnapp). `kind: deb` shells to `dpkg-deb -x` behind an `IDebExtractor` seam (a pure-managed ar+tar+zstd extractor can replace it later).
- `src/Packet.Node/Api/PdnAvailableAppsApi.cs` — the `MapGroup("/api/v1/apps/available")` endpoints, including the post-install auto-restart (`TryRestartIfEnabledAsync`, driving `IAppServiceSupervisor.RestartAsync` for an enabled, pdn-managed, error-free app); uninstall + upload added to `PdnAppPackagesApi`.
- `web/packetnet-ui/src/screens/apps.tsx` + `lib/api.ts` + `lib/types.ts` — the Available-apps UI + client.
- `scripts/build-deb.sh` — **delete** the DAPPS fetch/stage block (current lines ~114-160) and the `dapps_version` + four sha256 pins (~24-39); **add** staging of `catalog/apps.yaml` to `/usr/share/packetnet/catalog/`. WALL/LOBBY staging (lines ~99-113) stays.
- `NodeConfig` (`src/Packet.Node.Core/Configuration/NodeConfig.cs`) — A2 only: an optional `appCatalog:` block (`feedUrl`, refresh interval) behind the seam; v1 needs no config.
- Tests: `ShippedManifestsTests` re-pointed from the build-deb pins to the catalog entry; installer unit tests (hash-mismatch-refused, atomic place, RID selection, deb-extract + subtree lift, path-traversal-safe untar, upload path) driven by a **local fixture source** (`file://` / in-memory + a tiny hand-built fixture `.deb`) so CI needs no live network; live GitHub fetch validated on the lab.

## Slice plan

- **6a — Catalog + installer foundation. ✅ SHIPPED.** `catalog/apps.yaml` (dapps/bpqchat/convers) + records/parser/validator + `IAppCatalog` (embedded) + `IAppInstaller` (URL + upload; assets/deb/pdnapp; verify; commit) + the O1 payload marker + 27 unit tests. No deb change yet, no UI yet (DI-registered but unsurfaced). DAPPS still bundled at this point.
- **6b — API + UI. ✅ SHIPPED.** `PdnAvailableAppsApi` + uninstall/upload on `PdnAppPackagesApi` (scoped + audited) + the "Available apps" UI. End-to-end installable from the browser against the embedded catalog.
- **6c — Move DAPPS out of the deb + release. ✅ SHIPPED.** Stripped the DAPPS staging + pins from `build-deb.sh` (catalog `apps.yaml` staged instead); dev deb dropped **73.6→40.7 MB**; re-pointed `ShippedManifestsTests`; updated [`app-packages.md`](app-packages.md) § Distribution + this doc. Lab deploy + live-verify (install DAPPS/bpqchat/convers from the catalog + a `.pdnapp` upload) and the `node-v*` release per [`releasing.md`](releasing.md) follow on merge.
- **6d — A2 (network refresh + signature), deferred, drop-in by design.** A refreshing `IAppCatalog` + `ICatalogVerifier` (cosign, shared with the self-updater's seam) + `appCatalog.feedUrl` config + a manual/periodic refresh + a public catalog host (`pdn.m0lte.uk` or OARC static). Lifts the v1 release-cadence limit on app discovery/versions. Not in the v1 cut.

## Open questions

- **O1 — uninstall/update and app state. ✅ RESOLVED (6a) — a payload marker, not a dir split.** For an owner-installed package the package dir is also the state dir (`app-packages.md`: "deliberate"). A naive uninstall/update would nuke the app's data. The dir-split idea (`payload/` + `state/` subdirs) was rejected because it would have changed discovery and `PDN_APP_STATE` for *every* `/var/lib` package, regressing the hand-sideload case. **Instead:** each install writes a `.pdn-install.json` marker into the package dir recording exactly the files the installer placed (`{id, source, kind, version, installedUtc, sha256s, payload[]}`). Update deletes only the recorded payload before restaging; uninstall removes only the recorded payload + the marker (and the dir if it ends up empty). App-created state (`dapps.db`, `convers.yaml`, …) is never in the payload list, so it always survives. Discovery, `PackageDir`, and `PDN_APP_STATE` are **unchanged** — the `pdn-app.yaml` sits at the package-dir root exactly as a hand-installed package, and a marker-less dir is treated as hand-sideloaded (uninstall refuses it). Lower-risk than the split and identical UX.
- **O2 — catalog vs the apt channel.** bpqchat/convers already ship as proper Debian packages, so `apt install pdn-bpqchat` (once hibby's OARC repo carries them) is a real alternative path: Debian deps/trust + a CLI install. It needs that repo to exist (deferred) and needs root — so it is **not** the v1 path, but it coexists: the catalog is the browser/unprivileged/air-gapped path (via `.deb` *extraction*), apt is the CLI path (via `.deb` *installation*). Same artifact, two install mechanisms. Named, not built.
- **O3 — catalog maintenance / regen.** v1 is a hand-edited YAML (same effort as today's build-deb re-pin). A small `scripts/` regen — given `(app repo, version)`, pull each asset's GitHub-recorded sha256 digest + the manifest metadata, emit the entry — keeps "vetted = Tom ran regen against a chosen release" and removes hand-hashing. Fast-follow, not v1-blocking.
- **O4 — manifest version drift.** The apps' in-repo `pdn-app.yaml` `version:` lags their release tag (bpqchat manifest says `0.0.1` vs release `v0.1.0`; convers `0.1.0` vs `v0.1.2`). The catalog `version` tracks the **release tag** (the real artifact); the installed package shows its manifest's version. Until the upstream manifests catch up this can show a spurious "update available". Minor; flag upstream. Decide in 6b whether to compare on the catalog-recorded installed marker instead of the manifest version.

## Non-goals (v1)

- No capability *enforcement* — `capabilities` stay displayed-not-enforced, unchanged from the platform's owner-owns-trust model.
- No A2 network refresh / signing (6d).
- No apt-as-store channel (O2).
- No pure-managed `.deb` extraction — v1 shells to `dpkg-deb -x`, which is always present on a host that installed pdn's own `.deb`. A self-contained-tarball install on a genuinely non-dpkg host can't install `kind: deb` apps via URL (use the `.pdnapp` upload); named limitation, behind the `IDebExtractor` seam.
- No parity concern — this is node-host-only (`Packet.Node*`); it does not touch the AX.25 libraries, so no ax25-ts leg.

## Cross-references

- The package model this extends: [`app-packages.md`](app-packages.md).
- The trust-root + verify-seam precedent (cosign, polkit, channels): [`node-self-update-design.md`](node-self-update-design.md), OQ-003 / [#188](https://github.com/packet-net/packet.net/issues/188).
- Packaging + release: `scripts/build-deb.sh`, `publish-node.yml`, [`releasing.md`](releasing.md).
</content>
