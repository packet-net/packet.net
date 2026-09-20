#!/usr/bin/env bash
#
# build-deb.sh - publish pdn (the packet.net node host) self-contained for one
# RID and package it as a Debian .deb. Used locally and by publish-node.yml.
#
#   scripts/build-deb.sh <rid> <version>
#   e.g. scripts/build-deb.sh linux-arm64 0.1.0
#
# Cross-publishes from x64 (ReadyToRun via crossgen2), so all three arches build
# on the one self-hosted runner - no arch-native machine or cross C-toolchain.
# Produces artifacts/packetnet_<version>_<arch>.deb.
set -euo pipefail

rid="${1:?usage: build-deb.sh <rid> <version>}"
version="${2:?usage: build-deb.sh <rid> <version>}"

case "$rid" in
  linux-x64)   arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  linux-arm)   arch=armhf ;;
  *) echo "unknown rid: $rid (want linux-x64 | linux-arm64 | linux-arm)" >&2; exit 2 ;;
esac

# readelf reads the library-version floors that go into Depends (the library-floor block
# further down). Checked here, before the long publish, and fatal: falling back to an
# unversioned Depends is precisely the bug that block exists to fix, so a host without
# binutils must not be able to produce a .deb that understates what it needs.
command -v readelf >/dev/null 2>&1 || {
  echo "readelf not found - install binutils (the Depends library floors are read from the published ELF)" >&2; exit 2; }

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Packet.Node/Packet.Node.csproj"
pub="$root/artifacts/node/$rid"
stage="$root/artifacts/deb/$rid"
out="$root/artifacts/packetnet_${version}_${arch}.deb"

# The node serves the Vite SPA from {ContentRoot}/wwwroot. The publish itself builds
# it (VITE_API_MODE=live) and emits it into the publish output's wwwroot/ - the
# Packet.Node.csproj BuildWebUi target (#468); npm ci + vite build run as part of
# `dotnet publish` below. (Previously this script built the SPA by hand; that left a
# bare `dotnet publish` shipping no/stale wwwroot. The csproj is now the single source
# of truth, so a plain publish - not just this script - produces a current live UI.)

# PDN_FAST=1: a faster publish for the dev deploy loop - drops R2R (crossgen2) and
# single-file bundling, at the cost of a slightly slower cold start (fine for the
# lab). Releases (publish-node.yml) leave PDN_FAST unset and take the full path.
publish_flags=( -p:InvariantGlobalization=true -p:DebugType=none -p:DebugSymbols=false )
if [ "${PDN_FAST:-}" = "1" ]; then
  echo "==> publish $rid (PDN_FAST: self-contained, no R2R/single-file, invariant globalization)"
else
  publish_flags+=( -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true )
  echo "==> publish $rid (self-contained, single-file, R2R, invariant globalization)"
fi
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:Version="$version" \
  "${publish_flags[@]}" \
  -v minimal -o "$pub"

echo "==> stage .deb tree for $arch"
rm -rf "$stage"
# NOTE: no $stage/etc/packetnet - config now lives in pdn.db (config-in-DB, #473), not a
# dpkg-tracked /etc YAML conffile. The node seeds the DB itself on first boot (from a
# pre-existing /etc YAML if one is left over from a pre-0.17 install, else the bootstrap
# template staged below). Dropping the conffile is what makes dpkg stop prompting on upgrade.
install -d "$stage/opt/packetnet/app" "$stage/lib/systemd/system" "$stage/DEBIAN"
# The publish output already carries the built SPA under wwwroot/ (the BuildWebUi
# csproj target - VITE_API_MODE=live), so copying $pub stages it at
# {ContentRoot=/opt/packetnet/app}/wwwroot with no separate step. Guard it: a publish
# that somehow produced no UI must never silently ship an empty wwwroot.
cp -a "$pub/." "$stage/opt/packetnet/app/"
[ -f "$stage/opt/packetnet/app/wwwroot/index.html" ] || {
  echo "publish produced no wwwroot/index.html - the web UI didn't build (BuildWebUi)" >&2; exit 1
}
cp "$root/packaging/packetnet.service" "$stage/lib/systemd/system/packetnet.service"
# The pristine config TEMPLATE - config-in-DB bootstrap (#473). Staged to /usr/share (NOT
# /etc, so it is never a dpkg conffile and never prompts). The node's first-boot seed reads
# it when there is no DB row, no legacy /etc YAML, and no PACKETNET_CONFIG_SEED; the in-code
# NodeConfigTemplate.Yaml is the ultimate fallback if even this is missing, so the node can
# never fail to boot for lack of a template.
install -d "$stage/usr/share/packetnet"
install -m 0644 "$root/packaging/packetnet.yaml" "$stage/usr/share/packetnet/packetnet.yaml.example"

# Phase 7 in-app self-update (docs/node-self-update-design.md). There is no build stamp:
# the node resolves apt-vs-github at runtime (dpkg ownership of the running binary +
# apt-cache repo origin), and anything dpkg does not own reports `unknown` and offers no
# in-app update at all. On both deb channels the node defers to dpkg, never self-mutates.
#  - packetnet-update.service: a privileged, on-demand oneshot the node triggers. Its
#    ExecStart is the packetnet-update DISPATCHER, which reads the runtime-resolved channel
#    the node spools to /run/packetnet/update.channel and execs the matching helper - so the
#    SINGLE oneshot serves both channels (apt + github).
#  - packetnet-apt-update: a targeted `apt-get install --only-upgrade packetnet` with
#    health-check rollback (the apt channel).
#  - packetnet-github-update: download the next release .deb -> sha256-verify -> dpkg -i ->
#    /healthz-gate -> dpkg -i rollback (the github channel; reads /run/packetnet/github-
#    update.json staged by the node, re-validating every field).
#  - the polkit rule: lets the unprivileged `packetnet` user start ONLY that one unit.
install -d "$stage/usr/lib/packetnet" "$stage/usr/share/polkit-1/rules.d"
install -m 0755 "$root/packaging/packetnet-update"          "$stage/usr/lib/packetnet/packetnet-update"
install -m 0755 "$root/packaging/packetnet-apt-update"      "$stage/usr/lib/packetnet/packetnet-apt-update"
install -m 0755 "$root/packaging/packetnet-github-update"   "$stage/usr/lib/packetnet/packetnet-github-update"
install -m 0644 "$root/packaging/packetnet-update.service"  "$stage/lib/systemd/system/packetnet-update.service"
install -m 0644 "$root/packaging/49-packetnet-update.rules" "$stage/usr/share/polkit-1/rules.d/49-packetnet-update.rules"

# The embedded Tailscale sidecar (docs/network-access.md §"The sidecar"): a
# static, CGO-free Go binary (tailscale.com/tsnet) that pdn supervises to join a
# tailnet, terminate TLS for pdn.<tailnet>.ts.net, and reverse-proxy to pdn's
# loopback HTTP - so passkeys work remotely with no public DNS/cert plumbing.
# Cross-compiled for the target arch and staged beside the self-update helpers.
case "$arch" in
  amd64) goarch=amd64 ;;
  arm64) goarch=arm64 ;;
  armhf) goarch=arm ;;
  *) echo "no GOARCH mapping for arch: $arch" >&2; exit 2 ;;
esac
command -v go >/dev/null 2>&1 || { echo "go not found (need Go to build the tsnet sidecar; runner has it at /usr/bin/go)" >&2; exit 2; }
echo "==> build tailscale sidecar (GOARCH=$goarch)"
goarm=""                                  # armv7 (hard-float) for 32-bit ARM
[ "$goarch" = arm ] && goarm=7
( cd "$root/sidecar/tsnet" \
  && CGO_ENABLED=0 GOOS=linux GOARCH="$goarch" GOARM="$goarm" \
     go build -trimpath -ldflags="-s -w" -o "$stage/usr/lib/packetnet/packetnet-tsnet" . )
chmod 0755 "$stage/usr/lib/packetnet/packetnet-tsnet"
# UPX-compress the sidecar (~21 MB stripped -> ~6 MB). The Go binary self-extracts
# in memory at exec - no install-time decompression, no runtime tsnet behaviour change.
# UPX packs foreign-arch ELF fine on this x64 host, so the one runner covers all three
# arches. GUARD: if upx is absent we log + ship the uncompressed binary (it still works);
# we never fail the build over a missing compressor. (Release runners for publish-node.yml
# need 'upx'/'upx-ucl' installed to get the size win - see report / runner deps.)
tsnet_bin="$stage/usr/lib/packetnet/packetnet-tsnet"
if command -v upx >/dev/null 2>&1; then
  before=$(stat -c%s "$tsnet_bin")
  echo "==> compress tailscale sidecar with upx ($(upx --version 2>/dev/null | head -1))"
  if upx --best --lzma -q "$tsnet_bin" >/dev/null 2>&1; then
    after=$(stat -c%s "$tsnet_bin")
    awk -v b="$before" -v a="$after" 'BEGIN{printf "    sidecar: %d -> %d bytes (%.1f%%, saved %.2f MB)\n",b,a,100*a/b,(b-a)/1048576}'
  else
    echo "WARNING: upx failed to compress the sidecar - shipping the uncompressed binary." >&2
  fi
else
  echo "WARNING: upx not found - shipping the UNCOMPRESSED tailscale sidecar (~21 MB)." >&2
  echo "         Install 'upx-ucl' (or 'upx') on this host/runner to shrink the .deb by ~15 MB/arch." >&2
fi
# The bundled app PACKAGES (docs/app-packages.md): each directory under
# /usr/share/packetnet/apps carries a pdn-app.yaml manifest authored by the app; pdn
# discovers them at startup/reload and the owner enables them with an `apps:` entry (or
# the control panel toggle). The bundled apps use the same mechanism as everyone else -
# zero special-casing. Recommends: python3 pulls in the interpreter on a default install.
#
# WALL - the reference spawn-per-connect app (pdn-app/1 stdio wire; see examples/wall/):
# wall.py is the packet plane, wall_web.py the supervised loopback web view pdn
# reverse-proxies under /apps/wall/ (docs/app-gateway.md).
install -d "$stage/usr/share/packetnet/apps/wall"
install -m 0644 "$root/examples/wall/pdn-app.yaml" "$stage/usr/share/packetnet/apps/wall/pdn-app.yaml"
install -m 0755 "$root/examples/wall/wall.py" "$stage/usr/share/packetnet/apps/wall/wall.py"
install -m 0755 "$root/examples/wall/wall_web.py" "$stage/usr/share/packetnet/apps/wall/wall_web.py"
install -m 0644 "$root/examples/wall/README.md" "$stage/usr/share/packetnet/apps/wall/README.md"
# LOBBY - the long-running-socket rung (app platform Slice 2): a Python daemon (Unix
# socket) with shared in-memory state + broadcast across users. pdn supervises the daemon
# while the app is enabled and connects per session. See docs/app-local-session-wire.md §6.
install -d "$stage/usr/share/packetnet/apps/lobby"
install -m 0644 "$root/examples/lobby/pdn-app.yaml" "$stage/usr/share/packetnet/apps/lobby/pdn-app.yaml"
install -m 0755 "$root/examples/lobby/lobby.py" "$stage/usr/share/packetnet/apps/lobby/lobby.py"
install -m 0644 "$root/examples/lobby/README.md" "$stage/usr/share/packetnet/apps/lobby/README.md"
# The app CATALOG (docs/app-catalog.md): the curated index of "Available apps" the
# node owner can fetch + install from the control panel. We ship the INDEX, not the
# payloads - DAPPS (and bpqchat/convers, …) are fetched on demand at install time,
# sha256-pinned (catalog/apps.yaml holds every pin), instead of bloating the deb.
# This is what took DAPPS's ~33 MB/arch binary back out of the package. The catalog
# is committed in this repo; the node reads it from here at runtime.
install -d "$stage/usr/share/packetnet/catalog"
install -m 0644 "$root/catalog/apps.yaml" "$stage/usr/share/packetnet/catalog/apps.yaml"

# --- library version floors, read from the binaries we ship -------------------------
# pdn ships self-contained, so its symbol-version floor is whatever Microsoft's runtime
# pack and the native NuGet shims for this RID were built against, not anything this repo
# controls, and it moves without warning: .NET 10 raised linux-arm from glibc 2.16 to
# 2.34, which is above Debian 11's 2.31 and above 32-bit Raspberry Pi OS's. While
# Depends: named a bare `libc6`, apt installed that armhf package onto bullseye quite
# happily and the binary then died in the dynamic loader with "version `GLIBC_2.33' not
# found". Measure the floor off the ELF instead of asserting one here, so apt refuses the
# install with a reason an operator can read, and so the next pack bump corrects itself.
#
# .gnu.version_r is the authoritative record of which symbol versions of which libraries
# the loader must satisfy, so collect every shipped binary's copy of it into one blob and
# take the highest of each family out of that. "GLIBC_" cannot match inside "GLIBCXX_",
# so the two families do not overlap.
#
# Scratch under artifacts/, not /tmp: carving an embedded library out of the bundle copies
# the tail of a ~170 MB executable, which is not something to put on a runner's tmpfs.
scratch="$(mktemp -d -p "$root/artifacts" .floors.XXXXXX)"
trap 'rm -rf "$scratch"' EXIT
version_needs="$scratch/version-needs.txt"
carved="$scratch/embedded.elf"
: > "$version_needs"

collect_needs() {
  readelf --version-info "$1" 2>/dev/null | awk '/Version needs section/,0' >> "$version_needs" || true
}

# Every executable and shared object in the staged tree, not just the host binary: a
# PDN_FAST build leaves the runtime's native shims loose beside it, and what the loader
# has to satisfy is the highest floor any of them asks for. The static CGO-free Go
# sidecar and the bundled Python apps have no version needs and contribute nothing, so
# they can be fed in blind. wwwroot is 0644 data and never matches.
staged_elf=0
while IFS= read -r f; do
  collect_needs "$f"
  staged_elf=$((staged_elf + 1))
done < <(find "$stage" -type f \( -perm -u+x -o -name '*.so' -o -name '*.so.*' \) | sort)
[ "$staged_elf" -gt 0 ] || { echo "no executables staged for $arch - nothing to read a library floor from" >&2; exit 1; }

# The staged files are not the whole story, and reading only them is how a floor comes out
# too low. PublishSingleFile bundles the third-party native shims INSIDE the host
# executable and the runtime extracts them at first run, so they are invisible to a
# readelf of anything on disk - and they are not bound by the runtime pack's floor. The
# amd64 host asks for glibc 2.27, but the SQLite shim travelling inside it asks for 2.34,
# and it is the shim that aborts the process on Debian 11, long after apt said yes. The
# bundle stores its entries verbatim and uncompressed, so find each embedded ELF by its
# magic number and read it where it lies. Carving to end-of-file is enough: an ELF's
# offsets are all relative to its own start, and trailing bytes are ignored. A stray
# 0x7f 'E' 'L' 'F' in managed data carves to something readelf rejects, which costs a
# temporary file and contributes nothing. Matching the whole 7-byte identification prefix
# (magic, class, little-endian, version 1) rather than just the 4-byte magic keeps the
# stray matches down to a handful.
case "$arch" in
  armhf) elf_class=$'\001' ;;   # ELFCLASS32
  *)     elf_class=$'\002' ;;   # ELFCLASS64
esac
app_bin="$stage/opt/packetnet/app/packetnet"
while IFS= read -r offset; do
  dd if="$app_bin" of="$carved" bs=1M iflag=skip_bytes skip="$offset" status=none
  collect_needs "$carved"
done < <(grep -abo "$(printf '\177ELF')$elf_class$(printf '\001\001')" "$app_bin" | cut -d: -f1)

max_needed() {
  grep -oE "${1}_[0-9][0-9.]*" "$version_needs" | sed "s/^${1}_//" | sort -uV | tail -1 || true
}

# A glibc symbol version is the glibc release that introduced it, and libc6's package
# version is that same release, so this maps straight onto a Debian version constraint.
glibc_min="$(max_needed GLIBC)"
[ -n "$glibc_min" ] || { echo "could not read a GLIBC floor from the staged binaries for $arch" >&2; exit 1; }
libc_depends="libc6 (>= $glibc_min)"

# libstdc++ versions its symbols by C++ ABI, not by package version, so this needs a
# table. Anchors measured against the distributions themselves: Debian 10 ships GCC 8 and
# tops out at 3.4.25, Debian 11 / GCC 10 at 3.4.28, Debian 12 / GCC 12 at 3.4.30, Debian
# 13 / GCC 14 at 3.4.33. Unmeasured points round up to the next anchor, because the
# failure modes are not symmetric: too high refuses an install that would have worked and
# says why, too low ships the loader crash this whole block exists to prevent. An unknown
# value is a new GCC ABI nobody has checked, so stop and make someone extend the table.
# No GLIBCXX requirement at all means no libstdc++6 dependency; do not invent one.
# libgcc-s1 stays off the list: libstdc++6 depends on it, and nothing here asks for a
# GCC_* symbol version newer than the ones every distribution in scope has carried for
# twenty years, so there is no floor worth naming.
glibcxx_min="$(max_needed GLIBCXX)"
if [ -n "$glibcxx_min" ]; then
  case "$glibcxx_min" in
    3.4|3.4.[0-9]|3.4.1[0-9]|3.4.2[01]) stdcxx_min=5 ;;
    3.4.22)        stdcxx_min=6 ;;
    3.4.23|3.4.24) stdcxx_min=7 ;;
    3.4.25)        stdcxx_min=8 ;;
    3.4.26)        stdcxx_min=9 ;;
    3.4.27|3.4.28) stdcxx_min=10 ;;
    3.4.29)        stdcxx_min=11 ;;
    3.4.30)        stdcxx_min=12 ;;
    3.4.31|3.4.32) stdcxx_min=13 ;;
    3.4.33)        stdcxx_min=14 ;;
    3.4.34)        stdcxx_min=15 ;;
    *) echo "unknown GLIBCXX_$glibcxx_min - extend the table in $0" >&2; exit 1 ;;
  esac
  libc_depends="$libc_depends, libstdc++6 (>= $stdcxx_min)"
fi
echo "==> library floors for $arch: $libc_depends (GLIBC_$glibc_min${glibcxx_min:+, GLIBCXX_$glibcxx_min})"

# `|` as the sed delimiter: the substituted text carries version relations, not slashes.
sed -e "s/@ARCH@/$arch/" -e "s/@VERSION@/$version/" -e "s|@LIBC_DEPENDS@|$libc_depends|" \
    "$root/packaging/control.in" > "$stage/DEBIAN/control"
# A template that grew a placeholder this script does not know about would otherwise ship
# the literal text as a dependency name. The pattern is deliberately narrower than a bare
# `@`, which the Maintainer address contains.
if grep -qE '@[A-Z_]+@' "$stage/DEBIAN/control"; then
  echo "unsubstituted placeholder left in DEBIAN/control:" >&2
  grep -nE '@[A-Z_]+@' "$stage/DEBIAN/control" >&2
  exit 1
fi
cp "$root/packaging/postinst" "$root/packaging/prerm" "$root/packaging/postrm" "$stage/DEBIAN/"
# conffiles: only staged when NON-EMPTY. config-in-DB (#473) dropped the /etc YAML conffile,
# so packaging/conffiles is now empty and we ship NO DEBIAN/conffiles at all - dpkg then never
# runs its md5 conffile comparison and the keep/replace upgrade prompt vanishes structurally.
if [ -s "$root/packaging/conffiles" ]; then
  cp "$root/packaging/conffiles" "$stage/DEBIAN/conffiles"
fi
chmod 0755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm" "$stage/DEBIAN/postrm"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
# -Zxz: pin xz - dpkg-deb's zstd default (dpkg >= 1.21.18) can't be unpacked by
# Debian Bullseye's dpkg, so a zstd .deb refuses to install there.
dpkg-deb --build --root-owner-group -Zxz "$stage" "$out"

echo "==> built $out"
dpkg-deb --info "$out"
# These listings are pure diagnostics. Disable pipefail around them: each is a
# `… | awk | head`/`grep` pipe where head closing early (SIGPIPE to awk) or a grep
# no-match returns non-zero, which under `set -o pipefail` would abort the whole
# build on a debug echo (the flaky publish-docker break - awk "broken pipe", exit 2).
set +o pipefail
echo "--- contents (top) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | head -30
echo "--- wwwroot (the served SPA) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/opt/packetnet/app/wwwroot/' | head -10
echo "--- bundled app packages ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/share/packetnet/apps/'
echo "--- app catalog (the Available-apps index) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/share/packetnet/catalog/'
echo "--- config bootstrap template (config-in-DB seed) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/share/packetnet/packetnet.yaml.example'
echo "--- conffiles (config-in-DB: must be EMPTY - no upgrade prompt) ---"
dpkg-deb --info "$out" | grep -i 'conffiles' || echo "    (no Conffiles - correct)"
echo "--- tailscale sidecar ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/lib/packetnet/packetnet-tsnet'
set -o pipefail
if command -v lintian >/dev/null 2>&1; then
  lintian "$out" || true
else
  echo "(lintian not installed - skipping deb-lint)"
fi
