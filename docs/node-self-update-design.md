# Node self-update — design

How the pdn node host updates itself **without fighting a system package manager**. Phase 7 (`docs/plan.md` §5.7). Status: the `apt` + `selfcontained` channels are built (see **Implementation status**); the **`github`** channel, **runtime** channel detection, and the **web-UI Apply** surface are the current arc (everything except the maintainer-owned OARC apt repo).

## Context

The node is distributed two ways, and they have opposite ownership models:

1. **Packaged (`apt`)** — a `.deb` from a Debian repo. **dpkg owns** `/opt/packetnet/app/*`; the distro/maintainer owns the update cadence. (The OARC apt repo is run + signed by the Debian maintainer, **hibby**, via reprepro — packet.net does **not** ship an apt repo of its own; see §5.7.)
2. **Self-contained (`install.sh` / tarball)** — the curl-bash / RaspiOS / "no distro packaging" path. **Nothing else manages it**; the operator expects the app to keep itself current.

An in-app updater that overwrites files in place is fine for (2) and **actively harmful** for (1):

- Debian Policy forbids a package modifying its own shipped files; a self-mutating daemon won't be accepted by a maintainer.
- `dpkg -V` integrity verification starts failing.
- The **next `apt upgrade` clobbers the self-updated build** — or "downgrades" it, because dpkg still records the packaged version — and can stomp conffiles.

**The hard rule: one owner per file.** The self-update path must never touch dpkg-managed files. So the *install channel* decides the update *mechanism*.

## Decision

`POST /api/system/update` branches on a build-stamped **install channel**, with a shared "is an update available?" check and a shared web-UI affordance. Only the *apply* path differs.

### Channel detection (revised 2026-06-14 — runtime resolution, three channels)

There are **three** channels — `apt`, `github`, `selfcontained` — and the build **cannot** know which of the first two applies, because the *same* `.deb` can be installed from an apt repo (`apt`) or `dpkg -i`'d from a GitHub Release (`github`). dpkg records no install method, and it does not matter: what we actually need is **the update mechanism available now**, not the historical provenance. So the build stamp carries only what the build genuinely knows, and the apt-vs-github split is resolved **at runtime**.

**The build stamp encodes `deb` vs `selfcontained` only** (`/usr/lib/packetnet/install-channel`): the `.deb` build stamps `deb`; the tarball / `install.sh` stamps `selfcontained`. (This supersedes the earlier "ship `InstallChannel=apt`" decision — baking `apt` into every `.deb` is exactly what makes a GitHub-installed node mislabel itself.)

**Runtime resolution, in order — every external probe guarded so a non-Debian / dpkg-less / Windows host never throws and never shells out to dpkg unless it has already proven itself a `.deb` install:**

1. **stamp `selfcontained`** → `Selfcontained`, immediately, with **zero dpkg/apt calls** (so Alpine / Fedora / RaspiOS-without-dpkg / Windows self-contained installs are decided on the stamp alone).
2. **stamp `deb` (or absent)** → probe **dpkg ownership of the running binary**, not repo presence: `dpkg-query -S "$(readlink -f /proc/self/exe)"` (our self-contained .NET apphost *is* `/proc/self/exe`, and it is the dpkg-tracked file). If `dpkg-query` is absent (caught as a missing-executable launch error) **or** the binary is not owned by `packetnet` → fall to `Selfcontained`.
3. **dpkg owns it** → probe **apt's actual upgrade source** (not a `sources.list` line): `apt-cache policy packetnet`. A real repo origin in the version table (an http(s) source, not just `/var/lib/dpkg/status`) → `Apt`; only the installed dpkg status with no repo → `Github`; `apt-cache` absent → `Github`. Because `apt-cache policy` reports a package only after `apt-get update` has genuinely seen it in a repo, a configured-but-unused repo line does **not** force `Apt`, and a stale/absent cache falls **conservatively** to `Github` (worst case we self-update from Releases instead of apt, never the reverse).

The node resolves once at boot and caches; `PDN_INSTALL_CHANNEL` overrides for testing. **Why repo *presence* is the wrong signal:** a box can carry the OARC repo in `sources.list.d` while the running binary was `dpkg -i`'d from a GitHub `.deb`, or vice-versa — repo presence proves nothing about *this* binary or its upgrade source. dpkg-ownership + `apt-cache policy` answer the two questions that actually matter: is it dpkg-managed, and can apt upgrade it from a repo.

### Channel = `apt` → apply button that drives a targeted `apt upgrade`

**Decided (2026-06-13):** the apt channel gets an active **Apply** button (not notify-only). The node polls a version endpoint, surfaces *"0.9.0 available"* in the web UI, and on Apply triggers the privileged helper to run a **targeted** upgrade — never touching files itself, so dpkg stays the sole owner:

```
apt-get update
apt-get install --only-upgrade -y packetnet      # only this package, not a full dist-upgrade
```

dpkg remains the source of truth; the package's own maintainer scripts restart the unit. Key mechanics, because an *active* self-upgrade restarts the very process that triggered it:

- **Trust root = apt's repo signature.** On this channel, integrity comes from apt verifying the maintainer's (hibby's) GPG-signed reprepro repo — **not** cosign. (Cosign is the self-contained channel's trust root only.) Two channels, two trust roots, both sound.
- **The helper must be detached from the node.** `apt-get install --only-upgrade packetnet` replaces `/opt/packetnet/app/*` and the postinst restarts `packetnet.service` — which kills the node mid-request. So the node does **not** run apt as a child: it triggers the **`packetnet-update.service` systemd oneshot** (via the polkit/D-Bus seam below) and returns immediately. The oneshot runs apt independently of the node's lifecycle and survives the service restart.
- **UI reconnect, not in-band result.** Because the triggering request's process is replaced, the result isn't returned in-band — the web UI polls `/api/system/version` (+ `/healthz`) until the node reappears on the new version (or times out). The Apply call is fire-and-acknowledge.
- **Targeted, never broad.** `--only-upgrade` + the explicit package name: the helper upgrades `packetnet` and its strict deps only, never a `dist-upgrade`, so a node "update" can't drag the whole box forward.
- **Rollback.** If the upgrade or the post-restart `/healthz` fails, the helper pins back: `apt-get install --allow-downgrades -y packetnet=<prev>` from the apt cache / a retained prior version. (Requires the repo or local cache to retain the previous `.deb` — note for the maintainer.)
- **Authorization + audit.** Apply is gated behind the **admin** scope and audit-logged (`Packet.Node.Auth`-style), same as the other privileged control-API actions.

### Channel = `github` → apply button that pulls the next `.deb` from GitHub Releases

A `.deb` that was `dpkg -i`'d from a GitHub Release (the common early-adopter case — and the lab) is dpkg-managed but has **no apt repo to upgrade from**. dpkg still owns the files, so the self-contained symlink-swap is wrong here — the update must go **through dpkg**. So this channel mirrors the `apt` channel's *shape* (detached oneshot, health-gate, rollback, UI reconnect) but with GitHub Releases as the source instead of a repo:

- **Available-version check:** poll the GitHub Releases API for the latest `node-v*` tag and compare to the running version (rate-limited; unauthenticated is sufficient). A node running a `0.1.0+dev…` build sorts *above* any release, so it correctly reports "up to date" rather than offering a downgrade.
- **Apply:** the helper downloads the matching per-arch `packetnet_<ver>_<arch>.deb` from the release → **verifies its sha256** against the release `SHA256SUMS` (HTTPS; the same trust model the app catalog uses) → `dpkg -i` it (the detached `packetnet-update.service` oneshot survives the postinst restart) → `/healthz`-gates → on failure **rolls back** with `dpkg -i` of the retained prior `.deb` (`/var/cache/apt/archives` or a retained copy).
- **Trust root = the GitHub release `SHA256SUMS` over HTTPS** (checksum-only first cut, the same seam as the self-contained channel; cosign/minisign hardening is the shared follow-up). Distinct from `apt` (apt's repo GPG signature) and `selfcontained` (the feed digest).
- **Targeted, dpkg-owned, never a dist-upgrade:** only the `packetnet` package moves and dpkg stays the sole owner of the files — no in-place self-mutation. The request file passes the target version + arch + download URL + expected sha256 so the helper validates rather than trusting the caller.

### Channel = `selfcontained` → atomic, rollback-safe self-update

Here the in-app updater earns its keep. It is **atomic via a symlink swap** and never edits a running tree in place:

- Install layout is **versioned directories** with a `current` symlink the systemd unit points at:
  ```
  /opt/packetnet/releases/0.8.0/      ← binaries + wwwroot
  /opt/packetnet/releases/0.9.0/
  /opt/packetnet/current -> releases/0.9.0     ← ExecStart=/opt/packetnet/current/Packet.Node
  ```
- **Update:** the node fetches `latest.json` from its configured **feed** (version + per-arch tarball + sha256), and if newer, the helper downloads the tarball → **verifies the sha256** → unpacks into `releases/<new>` *alongside* the running one → flips `current` → restarts.
- **Trust root = the release `SHA256SUMS` / `latest.json` digest (checksum-only, decided 2026-06-13).** Cosign signing is a planned **hardening follow-up**, not in the first cut; the helper has a clear verify seam so a `cosign verify-blob` step drops in later without reshaping the flow. (Contrast the apt channel, whose trust root is apt's repo GPG signature — two channels, two trust roots.)
- **Watchdog rollback:** if the new version fails its post-start health check (`/healthz` within a timeout), the helper flips `current` back to the prior release and restarts. The atomic symlink swap means there is never a half-written running tree; rollback is one symlink + one restart.
- **Never in the swap set:** config (`/etc/packetnet`) and state/data (`/var/lib/packetnet`, `/opt/packetnet/...`). Only binaries + `wwwroot` move. Old `releases/*` are GC'd keeping the last N (e.g. 2) for rollback.
- **Migrations stay additive (load-bearing rule).** Rolling the *binary* back does **not** roll back `pdn.db`, so every schema migration MUST be backward-compatible — additive only (a new column/table an older binary simply ignores, as `revoked_utc` is). A destructive migration would break binary rollback; so we don't ship one. This is a rule, not a hope.
- **Feed host (deployment dependency).** `packet.net` is a **private** repo, so a self-contained node can't anonymously pull release assets from it. The tarballs + `latest.json` therefore need a **public** home (the operator's domain / OARC object storage / a public mirror) — the same way the apt channel's repo is the maintainer's. The node's feed URL is **configuration** (`management.update.feedUrl`-style), defaulting to wherever that public home ends up; the build just *produces* the artifacts (see below).

## Privilege model (both channels)

The service runs **unprivileged** (the `packetnet` user) and must stay that way. The actual apt-call / symlink-swap needs root, so it goes through a **`packetnet-update.service` systemd oneshot** the node *triggers* but does not embody — the node never runs as root. The node starts it over D-Bus, authorized by a **polkit** rule scoped to that one unit for the `packetnet` user (so the node can start *only* the update unit, nothing else). The oneshot is deliberately **detached** from the node's lifecycle so it survives the service restart the upgrade causes. Same seam, two bodies: on `apt` it runs the targeted `apt upgrade`; on `selfcontained` it cosign-verifies + flips the `current` symlink + restarts. The new build's version + (selfcontained) the cosign signature material are passed to the helper as parameters / a request file so the helper validates rather than trusting the caller blindly.

## What this means for Phase 7 scope

- **In scope (packet.net):** the `packetnet-update.service` helper + polkit rule; `POST /api/system/update` (channel-aware: apt-apply vs self-update) with a version/health-poll completion model and rollback; the web UI Apply affordance (admin-gated, audited). For the self-contained channel: `install.sh` + signed tarball + cosign verification + the versioned-dir/`current`-symlink layout. For the apt channel: the helper's targeted-`apt-upgrade` + downgrade-rollback path.
- **Out of scope (maintainer-owned):** the apt repo itself — hibby runs + signs the OARC reprepro repo. packet.net's job on the packaged side is to be a **well-behaved package** (never self-mutate the filesystem; ship the `InstallChannel=apt` marker; honour the systemd `Restart=` contract) whose Apply button just drives the maintainer's own `apt`.

## Decisions (settled 2026-06-13)

1. **The apt build gets an active Apply button** — the helper runs the targeted `apt-get install --only-upgrade packetnet`, not notify-only. (Trust = apt's repo signature; restart handled via the detached oneshot + UI reconnect; rollback via `--allow-downgrades` to the prior version.)
2. **The package ships the `InstallChannel=apt` marker** (e.g. `/usr/lib/packetnet/install-channel`) so the node need not sniff dpkg; the `dpkg-query` sniff stays only as a fallback.
3. **The self-contained channel matches the packaged unit's `Restart=` contract**, so update/restart behaviour is identical across install methods.

These were the three open questions; all resolved with the maintainer.

## Implementation status

**Slice 1 — the apt-channel apply path — is shipped.**

- **API** (`PdnSystemApi`): `GET /api/v1/system/info` (read scope) → `{ version, channel, updateMechanism }`; `POST /api/v1/system/update` (admin scope, audited via `SystemLog`/`Packet.Node.System`) → on `apt`, dispatch the helper and **202** (fire-and-acknowledge); on `self-contained` **501**; on `unknown` **409**; launch failure **503**.
- **Seams** (`Packet.Node.Core/SelfUpdate/`): `IInstallChannelProvider` (`FileInstallChannelProvider` reads the marker + `PDN_INSTALL_CHANNEL` override, fail-safe to `Unknown`); `ISystemUpdateLauncher` (`SystemctlUpdateLauncher` runs `systemctl start --no-block packetnet-update.service`, `NotSupported` when systemd is absent).
- **Packaging** (`packaging/` + `build-deb.sh`): the `install-channel` marker (`apt`) at `/usr/lib/packetnet/`, the `packetnet-update.service` oneshot, the `packetnet-apt-update` helper (targeted `apt-get install --only-upgrade` + is-active health-gate + downgrade rollback), and the `49-packetnet-update.rules` polkit rule; `Depends: polkitd | policykit-1`.
- **Tests**: `InstallChannelProviderTests` + `SystemUpdateApiTests` (channel branches, admin gating, launcher invoked/not).

**Slice 2 — the self-contained channel — is in progress.**

- **Release artifacts (done):** `publish-node.yml` emits, alongside the `.deb`s, a per-arch self-contained **`packetnet_<ver>_<arch>.tar.gz`** (the published binary tree), a **`latest.json`** manifest (`{version, artifacts:{<arch>:{file,sha256}}}`), and an extended **`SHA256SUMS`** — the download targets a self-contained node consumes.
- **Update helper + API generalization (done):** **`packaging/packetnet-selfupdate`** — fetch `latest.json` → pick the arch tarball → download → **sha256-verify** (the cosign seam is marked for later) → unpack into `releases/<new>` via a temp dir + rename → atomically flip `current` → restart → `/healthz`-gate → **roll back** to `$prev` on failure → GC keeping `current`+`$prev`. Paths/restart/health are `PDN_*`-overridable for testing. The launcher + API are now channel-agnostic: `ISystemUpdateLauncher.StartUpdateAsync` starts the one `packetnet-update.service` (its helper body differs per install), and `POST /system/update` launches for **both** apt and self-contained (only `unknown` → 409). Covered by `scripts/selfupdate-smoke.sh` (happy/mismatch-refused/rollback, in `deb-smoke`) + the updated `SystemUpdateApiTests`.
- **Still to build (slice 2c):** `installers/install.sh` — lay out `releases/<ver>` + `current` + the self-contained systemd unit (ExecStart at `current`) + the `selfcontained` marker + `packetnet-selfupdate` as `/usr/lib/packetnet/packetnet-update` + the polkit rule + `/etc/packetnet/update.conf` (`FEED_URL`/`HEALTH_URL`) + the user/state dirs + the admin-bootstrap print. Then: the configurable feed URL surfaced in node config + the available-version check feeding the UI; a **public feed host** (deployment — resolved if packet.net is public).

**Slice 3 — the `github` channel + runtime detection + the web-UI Apply surface — is the current arc** (everything except the OARC apt repo, which is maintainer-owned).

- **Runtime channel detection** (the revised "Channel detection" section above): `FileInstallChannelProvider` → a runtime-resolving provider (dpkg-ownership + `apt-cache policy`, all probes guarded); `InstallChannel` gains `Github`; `build-deb.sh` stamps `deb` (not `apt`). This supersedes Decision #2 above.
- **`github` apply + version check:** a `packetnet-github-update` helper (download the release `.deb` → sha-verify → `dpkg -i` → health-gate → rollback) behind the existing detached oneshot; the available-version check (per channel: `apt-cache policy` / GitHub Releases API / `latest.json`) surfaced on `GET /api/v1/system/info` as `{ …, updateAvailable: bool, latestVersion: string|null }`.
- **web UI:** the node version + channel shown in the control panel (closing the "nothing distinguishes the running version" gap), an "update available · vX → vY" banner, and an admin Apply button that calls `POST /api/v1/system/update` then polls `GET /api/v1/system/info` (+ `/healthz`) until the version changes (the fire-and-acknowledge reconnect). Apply disabled on `unknown`.

**Deferred further:** deepening the apt-channel health gate from `systemctl is-active` to a real `/healthz` probe; **cosign/minisign** signing/verify (the checksum-only seam hardens to it later, shared by the `github` + self-contained channels).

## Cross-references

- Cosign key management for the trusted pubkey + rotation: OQ-003 ([#188](https://github.com/packet-net/packet.net/issues/188)).
- Packaging status + the shipped `.deb` path: §9 and §5.7; `scripts/build-deb.sh`, `publish-node.yml`, `docs/releasing.md`.
