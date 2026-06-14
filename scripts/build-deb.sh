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
ui="$root/web/packetnet-ui"
pub="$root/artifacts/node/$rid"
stage="$root/artifacts/deb/$rid"
out="$root/artifacts/packetnet_${version}_${arch}.deb"

echo "==> build the web UI (Vite SPA -> $ui/dist)"
# The node serves this SPA from {ContentRoot}/wwwroot; it's gitignored/built here
# (not carried by `dotnet publish`), so build it before staging the .deb tree.
# A fresh CI checkout runs `npm ci` once; a dev box reuses its node_modules;
# per-arch calls in the publish loop only rebuild dist (cheap).
( cd "$ui" && { [ -d node_modules ] || npm ci; } && VITE_API_MODE=live npm run build )

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
install -d "$stage/opt/packetnet/app" "$stage/lib/systemd/system" \
           "$stage/etc/packetnet" "$stage/DEBIAN"
cp -a "$pub/." "$stage/opt/packetnet/app/"
# The SPA: served from {ContentRoot=/opt/packetnet/app}/wwwroot.
install -d "$stage/opt/packetnet/app/wwwroot"
cp -a "$ui/dist/." "$stage/opt/packetnet/app/wwwroot/"
cp "$root/packaging/packetnet.service" "$stage/lib/systemd/system/packetnet.service"
cp "$root/packaging/packetnet.yaml"    "$stage/etc/packetnet/packetnet.yaml"

# Phase 7 in-app self-update (apt channel; docs/node-self-update-design.md):
#  - install-channel: the build-stamped marker the node reads to decide HOW it updates
#    (this .deb is an apt install → "apt"; the node defers to dpkg, never self-mutates).
#  - packetnet-update.service: a privileged, on-demand oneshot the node triggers.
#  - packetnet-apt-update: that oneshot's helper — a targeted `apt-get install
#    --only-upgrade packetnet` with health-check rollback (mode 0755).
#  - the polkit rule: lets the unprivileged `packetnet` user start ONLY that one unit.
install -d "$stage/usr/lib/packetnet" "$stage/usr/share/polkit-1/rules.d"
install -m 0644 "$root/packaging/install-channel"          "$stage/usr/lib/packetnet/install-channel"
install -m 0755 "$root/packaging/packetnet-apt-update"     "$stage/usr/lib/packetnet/packetnet-apt-update"
install -m 0644 "$root/packaging/packetnet-update.service" "$stage/lib/systemd/system/packetnet-update.service"
install -m 0644 "$root/packaging/49-packetnet-update.rules" "$stage/usr/share/polkit-1/rules.d/49-packetnet-update.rules"
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
cp "$root/packaging/conffiles" "$root/packaging/postinst" \
   "$root/packaging/prerm" "$root/packaging/postrm" "$stage/DEBIAN/"
chmod 0755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/prerm" "$stage/DEBIAN/postrm"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
dpkg-deb --build --root-owner-group "$stage" "$out"

echo "==> built $out"
dpkg-deb --info "$out"
echo "--- contents (top) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | head -30
echo "--- wwwroot (the served SPA) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/opt/packetnet/app/wwwroot/' | head -10
echo "--- bundled app packages ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/share/packetnet/apps/'
echo "--- app catalog (the Available-apps index) ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}' | grep '/usr/share/packetnet/catalog/'
if command -v lintian >/dev/null 2>&1; then
  lintian "$out" || true
else
  echo "(lintian not installed — skipping deb-lint)"
fi
