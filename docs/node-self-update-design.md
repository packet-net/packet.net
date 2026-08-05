# Node self-update - design

How the pdn node host updates itself **without fighting a system package manager**. Phase 7 (`docs/plan.md` §5.7). Status: shipped - the `apt` and `github` channels, runtime channel detection, and the web-UI Apply surface are all built (see **Implementation status**). The maintainer-owned OARC apt repo is out of scope here.

## Context

The node is distributed as exactly two artifacts, both attached to every GitHub release, and they have opposite ownership models:

1. **The `.deb`** - Debian, Ubuntu, Raspberry Pi OS. **dpkg owns** `/opt/packetnet/app/*`. It may be `apt install`ed from the maintainer's repo, or installed straight from the release file; either way dpkg is the owner. (The OARC apt repo is run + signed by the Debian maintainer, **hibby**, via reprepro - packet.net does **not** ship an apt repo of its own; see §5.7.)
2. **The `.tar.gz` archive** - everything else. **Nothing manages it.** The operator unpacked it, and the operator replaces it.

An in-app updater that overwrites files in place is **actively harmful** for (1):

- Debian Policy forbids a package modifying its own shipped files; a self-mutating daemon won't be accepted by a maintainer.
- `dpkg -V` integrity verification starts failing.
- The **next `apt upgrade` clobbers the self-updated build** - or "downgrades" it, because dpkg still records the packaged version - and can stomp conffiles.

**The hard rule: one owner per file.** The node never touches files it does not own. For (2) it owns nothing it can safely reason about - an archive can be unpacked anywhere, under any layout, alongside anything - so it declines to update itself at all rather than guess.

## Decision

`POST /api/v1/system/update` branches on the resolved **install channel**, with a shared "is an update available?" check and a shared web-UI affordance. Only the *apply* path differs.

### Channel detection - resolved at runtime, no build stamp

There are **two** update channels, `apt` and `github`, plus `unknown` for everything else. The build **cannot** know which of the two applies, because the *same* `.deb` can be installed from an apt repo (`apt`) or installed straight from a GitHub Release (`github`). dpkg records no install method, and it does not matter: what we actually need is **the update mechanism available now**, not the historical provenance.

The build therefore stamps nothing. Resolution is entirely from what's on the box, in order - every external probe guarded so a non-Debian / dpkg-less / Windows host never throws:

1. **Does dpkg own the running binary?** Not repo presence: `dpkg-query -S "$(readlink -f /proc/self/exe)"` (our self-contained .NET apphost *is* `/proc/self/exe`, and it is the dpkg-tracked file). If `dpkg-query` is absent (caught as a missing-executable launch error) **or** the binary is not owned by `packetnet` → **`Unknown`**. That is the archive install, the container image, and a `dotnet run` from source: nothing here owns the files, so the node offers nothing.
2. **dpkg owns it** → probe **apt's actual upgrade source** (not a `sources.list` line): `apt-cache policy packetnet`. A real repo origin in the version table (an http(s) source, not just `/var/lib/dpkg/status`) → `Apt`; only the installed dpkg status with no repo → `Github`; `apt-cache` absent → `Github`. Because `apt-cache policy` reports a package only after `apt-get update` has genuinely seen it in a repo, a configured-but-unused repo line does **not** force `Apt`, and a stale/absent cache falls **conservatively** to `Github` (worst case we update from Releases instead of apt, never the reverse).

The node resolves once at boot and caches; `PDN_INSTALL_CHANNEL` overrides for testing. **Why repo *presence* is the wrong signal:** a box can carry the OARC repo in `sources.list.d` while the running binary came from a GitHub `.deb`, or vice-versa - repo presence proves nothing about *this* binary or its upgrade source. dpkg-ownership + `apt-cache policy` answer the two questions that actually matter: is it dpkg-managed, and can apt upgrade it from a repo.

### Channel = `apt` → apply button that drives a targeted `apt upgrade`

The apt channel gets an active **Apply** button (not notify-only). The node polls a version endpoint, surfaces *"0.9.0 available"* in the web UI, and on Apply triggers the privileged helper to run a **targeted** upgrade - never touching files itself, so dpkg stays the sole owner:

```
apt-get update
apt-get install --only-upgrade -y packetnet      # only this package, not a full dist-upgrade
```

dpkg remains the source of truth; the package's own maintainer scripts restart the unit. Key mechanics, because an *active* self-upgrade restarts the very process that triggered it:

- **Trust root = apt's repo signature.** On this channel, integrity comes from apt verifying the maintainer's (hibby's) GPG-signed reprepro repo.
- **The helper must be detached from the node.** `apt-get install --only-upgrade packetnet` replaces `/opt/packetnet/app/*` and the postinst restarts `packetnet.service` - which kills the node mid-request. So the node does **not** run apt as a child: it triggers the **`packetnet-update.service` systemd oneshot** (via the polkit/D-Bus seam below) and returns immediately. The oneshot runs apt independently of the node's lifecycle and survives the service restart.
- **UI reconnect, not in-band result.** Because the triggering request's process is replaced, the result isn't returned in-band - the web UI polls `GET /api/v1/system/info` (+ `/healthz`) until the node reappears on the new version (or times out). The Apply call is fire-and-acknowledge.
- **Targeted, never broad.** `--only-upgrade` + the explicit package name: the helper upgrades `packetnet` and its strict deps only, never a `dist-upgrade`, so a node "update" can't drag the whole box forward.
- **Rollback.** If the upgrade or the post-restart health check fails, the helper pins back: `apt-get install --allow-downgrades -y packetnet=<prev>` from the apt cache / a retained prior version. (Requires the repo or local cache to retain the previous `.deb` - note for the maintainer.)
- **Authorization + audit.** Apply is gated behind the **admin** scope and audit-logged, same as the other privileged control-API actions.

### Channel = `github` → apply button that pulls the next `.deb` from GitHub Releases

A `.deb` installed straight from a GitHub Release (the common case, and the lab) is dpkg-managed but has **no apt repo to upgrade from**. dpkg still owns the files, so the update must go **through dpkg**. This channel mirrors the `apt` channel's *shape* (detached oneshot, health-gate, rollback, UI reconnect) but with GitHub Releases as the source instead of a repo:

- **Available-version check:** poll the GitHub Releases API for the latest `node-v*` tag and compare to the running version (rate-limited; unauthenticated is sufficient). A node running a `0.1.0+dev...` build sorts *above* any release, so it correctly reports "up to date" rather than offering a downgrade.
- **Apply:** the helper downloads the matching per-arch `packetnet_<ver>_<arch>.deb` from the release → **verifies its sha256** against the release `SHA256SUMS` (HTTPS; the same trust model the app catalog uses) → `dpkg -i` it (the detached `packetnet-update.service` oneshot survives the postinst restart) → health-gates → on failure **rolls back** with `dpkg -i` of the retained prior `.deb` (`/var/cache/apt/archives` or a retained copy).
- **Trust root = the GitHub release `SHA256SUMS` over HTTPS** (checksum-only; cosign/minisign hardening is a follow-up). Distinct from `apt`'s repo GPG signature.
- **Targeted, dpkg-owned, never a dist-upgrade:** only the `packetnet` package moves and dpkg stays the sole owner of the files - no in-place self-mutation. The request file passes the target version + arch + download URL + expected sha256 so the helper validates rather than trusting the caller.

### Channel = `unknown` → the node does not update itself

An archive install, a container, or a build from source. `GET /api/v1/system/info` reports `channel: "unknown"`, `updateMechanism: "none"`, and `updateAvailable: false` - the availability check is not even run, so an unmanaged node makes no outbound calls looking for versions it cannot install. `POST /api/v1/system/update` declines with **409**. The panel labels the install *unmanaged* and explains that the operator upgrades it by unpacking the next release over it.

This is deliberate rather than a gap. The archive has no fixed layout to reason about, no record of what it replaced, and no owner to defer to; a self-update would be guessing at all three.

## Privilege model

The service runs **unprivileged** (the `packetnet` user) and must stay that way. The actual apt/dpkg call needs root, so it goes through a **`packetnet-update.service` systemd oneshot** the node *triggers* but does not embody - the node never runs as root. The node starts it over D-Bus, authorized by a **polkit** rule scoped to that one unit for the `packetnet` user (so the node can start *only* the update unit, nothing else). The oneshot is deliberately **detached** from the node's lifecycle so it survives the service restart the upgrade causes.

One oneshot serves both channels. Its `ExecStart` is the `packetnet-update` **dispatcher**, which reads the runtime-resolved channel the node spools to `/run/packetnet/update.channel` and execs the matching helper (`packetnet-apt-update` / `packetnet-github-update`). With no resolved channel it refuses rather than guessing. The target version + arch + download URL + expected sha256 are passed to the helper as a request file so the helper validates rather than trusting the caller blindly.

## What this means for Phase 7 scope

- **In scope (packet.net):** the `packetnet-update.service` helper + polkit rule; `POST /api/v1/system/update` (channel-aware) with a version/health-poll completion model and rollback; the web UI Apply affordance (admin-gated, audited).
- **Out of scope (maintainer-owned):** the apt repo itself - hibby runs + signs the OARC reprepro repo. packet.net's job on the packaged side is to be a **well-behaved package** (never self-mutate the filesystem; honour the systemd `Restart=` contract) whose Apply button just drives the maintainer's own `apt`.

## Implementation status

**Shipped.**

- **API** (`PdnSystemApi`): `GET /api/v1/system/info` (read scope) → `{ version, channel, updateMechanism, updateAvailable, latestVersion }`; `POST /api/v1/system/update` (admin scope, audited via `SystemLog`/`Packet.Node.System`) → on `apt` and `github`, dispatch the helper and **202** (fire-and-acknowledge); on `unknown` **409**; no systemd **501**; launch failure **503**.
- **Seams** (`Packet.Node.Core/SelfUpdate/`): `IInstallChannelProvider` (`RuntimeInstallChannelProvider` - dpkg-ownership then `apt-cache policy`, every probe guarded, `PDN_INSTALL_CHANNEL` override); `ISystemUpdateLauncher` (`SystemctlUpdateLauncher` runs `systemctl start --no-block packetnet-update.service`, `NotSupported` when systemd is absent); `IUpdateAvailabilityProbe` (`ChannelUpdateAvailabilityProbe` - `apt-cache policy` / the GitHub Releases API, total in every branch); `GithubUpdateRequestBuilder` (resolves the per-arch `.deb` URL + sha256 from `SHA256SUMS`).
- **Packaging** (`packaging/` + `build-deb.sh`): the `packetnet-update.service` oneshot, the `packetnet-update` dispatcher, the `packetnet-apt-update` helper (targeted `apt-get install --only-upgrade` + is-active health-gate + downgrade rollback), the `packetnet-github-update` helper (download → sha-verify → `dpkg -i` → health-gate → rollback), and the `49-packetnet-update.rules` polkit rule; `Depends: polkitd | policykit-1`.
- **Web UI:** the node version + channel in the control panel, an "update available · vX → vY" banner, and an admin Apply button that calls `POST /api/v1/system/update` then polls `GET /api/v1/system/info` (+ `/healthz`) until the version changes. Apply is disabled on `unknown`.
- **Tests:** `InstallChannelProviderTests`, `UpdateAvailabilityProbeTests`, `SystemUpdateApiTests`; `scripts/deb-install-smoke.sh` in `deb-smoke.yml`.

**Deferred:** deepening the apt-channel health gate from `systemctl is-active` to a real `/healthz` probe; **cosign/minisign** signing/verify (the checksum-only seam hardens to it later).

## Cross-references

- Cosign key management for the trusted pubkey + rotation: OQ-003 ([#188](https://github.com/packet-net/packet.net/issues/188)).
- Packaging status + the shipped `.deb` path: §9 and §5.7; `scripts/build-deb.sh`, `publish-node.yml`, `docs/releasing.md`.
- What pdn distributes, and how an operator installs each: [`operating/00-install.md`](../operating/00-install.md).
