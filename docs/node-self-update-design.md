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

### Channel = `apt` → notifier, never self-overwrite

The node stays hands-off the filesystem. It still does the useful half — polls a version endpoint and surfaces *"0.9.0 available"* in the web UI — but **apply defers to the system**:

- **Default: informational only.** The UI shows *"managed by apt — run `sudo apt upgrade packetnet`"*. No file is touched by the node, ever. This is the expected behaviour for a Debian daemon and the maintainer's preference.
- **Optional (maintainer opt-in only):** an "apply" button that runs `apt-get update && apt-get install --only-upgrade -y packetnet` through the privileged helper (below). dpkg stays the source of truth; rollback = `apt-get install packetnet=<prev>` from the repo/cache. Off by default; gated on the maintainer wanting it.

### Channel = `selfcontained` → atomic, rollback-safe self-update

Here the in-app updater earns its keep. It is **atomic via a symlink swap** and never edits a running tree in place:

- Install layout is **versioned directories** with a `current` symlink the systemd unit points at:
  ```
  /opt/packetnet/releases/0.8.0/      ← binaries + wwwroot
  /opt/packetnet/releases/0.9.0/
  /opt/packetnet/current -> releases/0.9.0     ← ExecStart=/opt/packetnet/current/Packet.Node
  ```
- **Update:** download the new build → **cosign-verify** signature against the baked-in sigstore pubkey → unpack into `releases/<new>` *alongside* the running one → flip `current` → restart.
- **Watchdog rollback:** if the new version fails its post-start health check (`/healthz` within a timeout), the helper flips `current` back to the prior release and restarts. The atomic symlink swap means there is never a half-written running tree; rollback is one symlink + one restart.
- **Never in the swap set:** config (`/etc/packetnet`) and state/data (`/var/lib/packetnet`, `/opt/packetnet/...`). Only binaries + `wwwroot` move. Old `releases/*` are GC'd keeping the last N (e.g. 2) for rollback.

## Privilege model (both channels)

The service runs **unprivileged** (the `packetnet` user) and must stay that way. The actual swap-or-apt-call needs root, so it goes through a **small privileged helper** — a `packetnet-update` systemd oneshot (or a polkit action) that the node *triggers* but does not embody. The node never runs as root. Same seam whether the helper calls `apt-get` or flips the symlink + restarts.

## What this means for Phase 7 scope

- **In scope (packet.net):** the self-contained `install.sh` + signed tarball + cosign verification, the versioned-dir/`current`-symlink layout, the privileged update helper, and `POST /api/system/update` (channel-aware: notifier vs self-update) with watchdog rollback. The web UI's update affordance.
- **Out of scope (maintainer-owned):** the apt repo itself — hibby runs + signs the OARC reprepro repo. packet.net's job on the packaged side is only to be a **well-behaved package** (never self-mutate; expose the channel marker; honour the systemd `Restart=` contract) and a polite update **notifier**.

## Open decisions (coordinate with the maintainer)

1. **Does the apt build expose any "apply" affordance, or stay purely informational?** (Default: informational-only.)
2. **Does the package ship the `InstallChannel=apt` marker** so the node needn't sniff dpkg? (Cheap for the maintainer, robust for us — preferred.)
3. **systemd `Restart=` contract** — the self-contained channel's watchdog should match whatever the packaged unit sets, so update/restart behaviour is consistent across install methods.

## Cross-references

- Cosign key management for the trusted pubkey + rotation: OQ-003 ([#188](https://github.com/m0lte/packet.net/issues/188)).
- Packaging status + the shipped `.deb` path: §9 and §5.7; `scripts/build-deb.sh`, `publish-node.yml`, `docs/releasing.md`.
