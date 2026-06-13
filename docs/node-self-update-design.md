# Node self-update — design

How the pdn node host updates itself **without fighting a system package manager**. Phase 7 (`docs/plan.md` §5.7). Status: design (no code yet).

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

### Channel detection

The artifact carries its provenance, set at build time:

- the `.deb` ships `InstallChannel=apt` (a one-liner the package owns, e.g. `/usr/lib/packetnet/install-channel`);
- the self-contained tarball/installer ships `InstallChannel=selfcontained`.

Belt-and-braces fallback if the marker is absent: `dpkg-query -S "$(readlink /proc/self/exe)"` — if the running binary is registered to the `packetnet` package, treat it as `apt` regardless. The node resolves the channel **once at boot** and caches it.

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

**Deferred further:** the **web UI** Apply button + version-poll completion UX; deepening the apt-channel health gate from `systemctl is-active` to a real `/healthz` probe; **cosign** signing/verify (the checksum-only seam hardens to it later); the `dpkg-query` channel-sniff fallback.

## Cross-references

- Cosign key management for the trusted pubkey + rotation: OQ-003 ([#188](https://github.com/m0lte/packet.net/issues/188)).
- Packaging status + the shipped `.deb` path: §9 and §5.7; `scripts/build-deb.sh`, `publish-node.yml`, `docs/releasing.md`.
