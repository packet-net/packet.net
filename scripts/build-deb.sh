#!/usr/bin/env bash
#
# build-deb.sh — publish pdn (the packet.net node host) self-contained for one
# RID and package it as a Debian .deb. Used locally and by publish-node.yml.
#
#   scripts/build-deb.sh <rid> <version>
#   e.g. scripts/build-deb.sh linux-arm64 0.1.0
#
# Cross-publishes from x64 (ReadyToRun via crossgen2), so all three arches build
# on the one self-hosted runner — no arch-native machine or cross C-toolchain.
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

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
proj="$root/src/Packet.Node/Packet.Node.csproj"
pub="$root/artifacts/node/$rid"
stage="$root/artifacts/deb/$rid"
out="$root/artifacts/packetnet_${version}_${arch}.deb"

# The node serves the Vite SPA from {ContentRoot}/wwwroot. The publish itself builds
# it (VITE_API_MODE=live) and emits it into the publish output's wwwroot/ — the
# Packet.Node.csproj BuildWebUi target (#468); npm ci + vite build run as part of
# `dotnet publish` below. (Previously this script built the SPA by hand; that left a
# bare `dotnet publish` shipping no/stale wwwroot. The csproj is now the single source
# of truth, so a plain publish — not just this script — produces a current live UI.)

# PDN_FAST=1: a faster publish for the dev deploy loop — drops R2R (crossgen2) and
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
# NOTE: no $stage/etc/packetnet — config now lives in pdn.db (config-in-DB, #473), not a
# dpkg-tracked /etc YAML conffile. The node seeds the DB itself on first boot (from a
# pre-existing /etc YAML if one is left over from a pre-0.17 install, else the bootstrap
# template staged below). Dropping the conffile is what makes dpkg stop prompting on upgrade.
install -d "$stage/opt/packetnet/app" "$stage/lib/systemd/system" "$stage/DEBIAN"
# The publish output already carries the built SPA under wwwroot/ (the BuildWebUi
# csproj target — VITE_API_MODE=live), so copying $pub stages it at
# {ContentRoot=/opt/packetnet/app}/wwwroot with no separate step. Guard it: a publish
# that somehow produced no UI must never silently ship an empty wwwroot.
cp -a "$pub/." "$stage/opt/packetnet/app/"
[ -f "$stage/opt/packetnet/app/wwwroot/index.html" ] || {
  echo "publish produced no wwwroot/index.html — the web UI didn't build (BuildWebUi)" >&2; exit 1
}
cp "$root/packaging/packetnet.service" "$stage/lib/systemd/system/packetnet.service"
# The pristine config TEMPLATE — config-in-DB bootstrap (#473). Staged to /usr/share (NOT
# /etc, so it is never a dpkg conffile and never prompts). The node's first-boot seed reads
# it when there is no DB row, no legacy /etc YAML, and no PACKETNET_CONFIG_SEED; the in-code
# NodeConfigTemplate.Yaml is the ultimate fallback if even this is missing, so the node can
# never fail to boot for lack of a template.
install -d "$stage/usr/share/packetnet"
install -m 0644 "$root/packaging/packetnet.yaml" "$stage/usr/share/packetnet/packetnet.yaml.example"

# Phase 7 in-app self-update (docs/node-self-update-design.md):
#  - install-channel: the build-stamped marker the node reads to decide HOW it updates.
#    The .deb stamps "deb" — deb-vs-selfcontained is all the build knows; the node
#    resolves apt-vs-github at runtime (dpkg ownership of the running binary + apt-cache
#    repo origin). On both deb sub-channels the node defers to dpkg, never self-mutates.
#  - packetnet-update.service: a privileged, on-demand oneshot the node triggers. Its
#    ExecStart is the packetnet-update DISPATCHER, which reads the runtime-resolved channel
#    the node spools to /run/packetnet/update.channel and execs the matching helper — so the
#    SINGLE oneshot serves all the deb-side channels (apt + github).
#  - packetnet-apt-update: a targeted `apt-get install --only-upgrade packetnet` with
#    health-check rollback (the apt channel).
#  - packetnet-github-update: download the next release .deb -> sha256-verify -> dpkg -i ->
#    /healthz-gate -> dpkg -i rollback (the github channel; reads /run/packetnet/github-
#    update.json staged by the node, re-validating every field).
#  - the polkit rule: lets the unprivileged `packetnet` user start ONLY that one unit.
install -d "$stage/usr/lib/packetnet" "$stage/usr/share/polkit-1/rules.d"
install -m 0644 "$root/packaging/install-channel"           "$stage/usr/lib/packetnet/install-channel"
install -m 0755 "$root/packaging/packetnet-update"          "$stage/usr/lib/packetnet/packetnet-update"
install -m 0755 "$root/packaging/packetnet-apt-update"      "$stage/usr/lib/packetnet/packetnet-apt-update"
install -m 0755 "$root/packaging/packetnet-github-update"   "$stage/usr/lib/packetnet/packetnet-github-update"
install -m 0644 "$root/packaging/packetnet-update.service"  "$stage/lib/systemd/system/packetnet-update.service"
install -m 0644 "$root/packaging/49-packetnet-update.rules" "$stage/usr/share/polkit-1/rules.d/49-packetnet-update.rules"

# The embedded Tailscale sidecar (docs/network-access.md §"The sidecar"): a
# static, CGO-free Go binary (tailscale.com/tsnet) that pdn supervises to join a
# tailnet, terminate TLS for pdn.<tailnet>.ts.net, and reverse-proxy to pdn's
# loopback HTTP — so passkeys work remotely with no public DNS/cert plumbing.
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
# in memory at exec — no install-time decompression, no runtime tsnet behaviour change.
# UPX packs foreign-arch ELF fine on this x64 host, so the one runner covers all three
# arches. GUARD: if upx is absent we log + ship the uncompressed binary (it still works);
# we never fail the build over a missing compressor. (Release runners for publish-node.yml
# need 'upx'/'upx-ucl' installed to get the size win — see report / runner deps.)
tsnet_bin="$stage/usr/lib/packetnet/packetnet-tsnet"
if command -v upx >/dev/null 2>&1; then
  before=$(stat -c%s "$tsnet_bin")
  echo "==> compress tailscale sidecar with upx ($(upx --version 2>/dev/null | head -1))"
  if upx --best --lzma -q "$tsnet_bin" >/dev/null 2>&1; then
    after=$(stat -c%s "$tsnet_bin")
    awk -v b="$before" -v a="$after" 'BEGIN{printf "    sidecar: %d -> %d bytes (%.1f%%, saved %.2f MB)\n",b,a,100*a/b,(b-a)/1048576}'
  else
    echo "WARNING: upx failed to compress the sidecar — shipping the uncompressed binary." >&2
  fi
else
  echo "WARNING: upx not found — shipping the UNCOMPRESSED tailscale sidecar (~21 MB)." >&2
  echo "         Install 'upx-ucl' (or 'upx') on this host/runner to shrink the .deb by ~15 MB/arch." >&2
fi
# The bundled app PACKAGES (docs/app-packages.md): each directory under
# /usr/share/packetnet/apps carries a pdn-app.yaml manifest authored by the app; pdn
# discovers them at startup/reload and the owner enables them with an `apps:` entry (or
# the control panel toggle). The bundled apps use the same mechanism as everyone else —
# zero special-casing. Recommends: python3 pulls in the interpreter on a default install.
#
# WALL — the reference spawn-per-connect app (pdn-app/1 stdio wire; see examples/wall/):
# wall.py is the packet plane, wall_web.py the supervised loopback web view pdn
# reverse-proxies under /apps/wall/ (docs/app-gateway.md).
install -d "$stage/usr/share/packetnet/apps/wall"
install -m 0644 "$root/examples/wall/pdn-app.yaml" "$stage/usr/share/packetnet/apps/wall/pdn-app.yaml"
install -m 0755 "$root/examples/wall/wall.py" "$stage/usr/share/packetnet/apps/wall/wall.py"
install -m 0755 "$root/examples/wall/wall_web.py" "$stage/usr/share/packetnet/apps/wall/wall_web.py"
install -m 0644 "$root/examples/wall/README.md" "$stage/usr/share/packetnet/apps/wall/README.md"
# LOBBY — the long-running-socket rung (app platform Slice 2): a Python daemon (Unix
# socket) with shared in-memory state + broadcast across users. pdn supervises the daemon
# while the app is enabled and connects per session. See docs/app-local-session-wire.md §6.
install -d "$stage/usr/share/packetnet/apps/lobby"
install -m 0644 "$root/examples/lobby/pdn-app.yaml" "$stage/usr/share/packetnet/apps/lobby/pdn-app.yaml"
install -m 0755 "$root/examples/lobby/lobby.py" "$stage/usr/share/packetnet/apps/lobby/lobby.py"
install -m 0644 "$root/examples/lobby/README.md" "$stage/usr/share/packetnet/apps/lobby/README.md"
# The app CATALOG (docs/app-catalog.md): the curated index of "Available apps" the
# node owner can fetch + install from the control panel. We ship the INDEX, not the
# payloads — DAPPS (and bpqchat/convers, …) are fetched on demand at install time,
# sha256-pinned (catalog/apps.yaml holds every pin), instead of bloating the deb.
# This is what took DAPPS's ~33 MB/arch binary back out of the package. The catalog
# is committed in this repo; the node reads it from here at runtime.
install -d "$stage/usr/share/packetnet/catalog"
install -m 0644 "$root/catalog/apps.yaml" "$stage/usr/share/packetnet/catalog/apps.yaml"
sed -e "s/@ARCH@/$arch/" -e "s/@VERSION@/$version/" \
    "$root/packaging/control.in" > "$stage/DEBIAN/control"
cp "$root/packaging/postinst" "$root/packaging/prerm" "$root/packaging/postrm" "$stage/DEBIAN/"
# conffiles: only staged when NON-EMPTY. config-in-DB (#473) dropped the /etc YAML conffile,
# so packaging/conffiles is now empty and we ship NO DEBIAN/conffiles at all — dpkg then never
# runs its md5 conffile comparison and the keep/replace upgrade prompt vanishes structurally.
if [ -s "$root/packaging/conffiles" ]; then
  cp "$root/packaging/conffiles" "$stage/DEBIAN/conffiles"
fi
chmod 0755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm" "$stage/DEBIAN/postrm"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
dpkg-deb --build --root-owner-group "$stage" "$out"

echo "==> built $out"
dpkg-deb --info "$out"
# These listings are pure diagnostics. Disable pipefail around them: each is a
# `… | awk | head`/`grep` pipe where head closing early (SIGPIPE to awk) or a grep
# no-match returns non-zero, which under `set -o pipefail` would abort the whole
# build on a debug echo (the flaky publish-docker break — awk "broken pipe", exit 2).
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
echo "--- conffiles (config-in-DB: must be EMPTY — no upgrade prompt) ---"
dpkg-deb --info "$out" | grep -i 'conffiles' || echo "    (no Conffiles — correct)"
echo "--- tailscale sidecar ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/lib/packetnet/packetnet-tsnet'
set -o pipefail
if command -v lintian >/dev/null 2>&1; then
  lintian "$out" || true
else
  echo "(lintian not installed — skipping deb-lint)"
fi
