#!/usr/bin/env bash
#
# build-headend-deb.sh — build the split-station RF head-end daemon (headend/)
# static + CGO-free for one Go arch and package it as a Debian .deb. Used locally
# and by publish-headend.yml. The head-end analogue of scripts/build-deb.sh (the
# node's .deb builder); it stages the daemon binary at /usr/lib/packetnet, the
# same location build-deb.sh uses for the tsnet Go sidecar.
#
#   scripts/build-headend-deb.sh <rid> <version>
#   e.g. scripts/build-headend-deb.sh linux-amd64 0.1.2
#
# <rid> is a Go-style linux-<goarch> triple (matches the Makefile's dist names):
#   linux-amd64 -> amd64   linux-arm64 -> arm64   linux-arm -> armhf (ARMv7)
#
# CGO_ENABLED=0 cross-compiles every arch from x64 with no cross C-toolchain (the
# same way build-deb.sh cross-builds the tsnet sidecar), so one host packages all
# three. Produces artifacts/packetnet-headend_<version>_<arch>.deb.
set -euo pipefail

rid="${1:?usage: build-headend-deb.sh <rid> <version>}"
version="${2:?usage: build-headend-deb.sh <rid> <version>}"

# rid -> (dpkg arch, Makefile target). The Makefile target writes
# dist/packetnet-headend-linux-<target> (target == GOARCH).
case "$rid" in
  linux-amd64) arch=amd64; target=amd64 ;;
  linux-arm64) arch=arm64; target=arm64 ;;
  linux-arm)   arch=armhf; target=arm   ;;
  *) echo "unknown rid: $rid (want linux-amd64 | linux-arm64 | linux-arm)" >&2; exit 2 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
headend="$root/headend"
pkg="$headend/packaging"
bin="$headend/dist/packetnet-headend-linux-$target"
stage="$root/artifacts/headend-deb/$rid"
out="$root/artifacts/packetnet-headend_${version}_${arch}.deb"

command -v go >/dev/null 2>&1 || { echo "go not found (need Go to build the head-end; the runner has it at /usr/bin/go)" >&2; exit 2; }

echo "==> build static head-end binary ($rid, CGO_ENABLED=0)"
# Delegate to the Makefile's arch target so the build flags stay single-sourced
# (-trimpath -ldflags='-s -w', CGO_ENABLED=0, GOARM=7 for arm). Idempotent — safe
# to run after publish-headend.yml's raw-binary step already populated dist/.
make -C "$headend" "$target"
[ -f "$bin" ] || { echo "expected build output missing: $bin" >&2; exit 1; }

echo "==> stage .deb tree for $arch"
rm -rf "$stage"
install -d "$stage/usr/lib/packetnet" "$stage/lib/systemd/system" \
          "$stage/usr/share/packetnet" "$stage/DEBIAN"

# The daemon binary — installed at /usr/lib/packetnet/packetnet-headend, matching
# where build-deb.sh stages the node's tsnet Go sidecar (/usr/lib/packetnet/packetnet-tsnet).
install -m 0755 "$bin" "$stage/usr/lib/packetnet/packetnet-headend"

# The systemd unit — reuse the committed headend/packetnet-headend.service, but for
# the .deb rewrite two lines with sed (the committed file stays byte-for-byte for the
# make+scp operator path):
#   1. ExecStart -> the installed binary path (/usr/lib/packetnet/packetnet-headend).
#   2. Comment out the Environment=PACKETNET_HEADEND_CONFIG line. The daemon treats a
#      *configured-but-missing* file as fatal (loadConfig os.ReadFile), so leaving it
#      active would make a fresh install (no /etc config) fail its own restart loop.
#      Commenting it out is what delivers the plug-and-go default: the daemon runs on
#      defaults (hostname-derived instanceId, auto-enumerate all serial). To opt into a
#      JSON config, copy the shipped example and uncomment the line (or pass --config).
sed -e 's#^ExecStart=/usr/local/bin/packetnet-headend$#ExecStart=/usr/lib/packetnet/packetnet-headend#' \
    -e 's#^Environment=PACKETNET_HEADEND_CONFIG=#\#Environment=PACKETNET_HEADEND_CONFIG=#' \
    "$headend/packetnet-headend.service" > "$stage/lib/systemd/system/packetnet-headend.service"
chmod 0644 "$stage/lib/systemd/system/packetnet-headend.service"

# Guard the two rewrites actually took — a drifted committed unit must not silently
# ship a .deb whose service points at the wrong binary or fails on a missing config.
grep -q '^ExecStart=/usr/lib/packetnet/packetnet-headend$' "$stage/lib/systemd/system/packetnet-headend.service" \
  || { echo "unit ExecStart rewrite failed (did headend/packetnet-headend.service change?)" >&2; exit 1; }
grep -q '^#Environment=PACKETNET_HEADEND_CONFIG=' "$stage/lib/systemd/system/packetnet-headend.service" \
  || { echo "unit Environment=PACKETNET_HEADEND_CONFIG comment-out failed (did the unit change?)" >&2; exit 1; }

# A documented default config EXAMPLE. Staged to /usr/share (NOT /etc), so it is never
# a dpkg conffile and never prompts on upgrade — the config is opt-in and the daemon
# runs on defaults without it. Shows instanceId/bindAddr/httpPort/baseTcpPort/allow/deny.
install -m 0644 "$pkg/packetnet-headend.json.example" "$stage/usr/share/packetnet/packetnet-headend.json.example"

# DEBIAN metadata.
sed -e "s/@ARCH@/$arch/" -e "s/@VERSION@/$version/" \
    "$pkg/control.in" > "$stage/DEBIAN/control"
install -m 0755 "$pkg/postinst" "$pkg/prerm" "$pkg/postrm" "$stage/DEBIAN/"

echo "==> build .deb"
mkdir -p "$root/artifacts"
# --root-owner-group (dpkg >= 1.19): root:root files without fakeroot.
dpkg-deb --build --root-owner-group "$stage" "$out"

echo "==> built $out"
dpkg-deb --info "$out"
# Diagnostics only — disable pipefail so a `head`/`grep` closing the pipe early
# can't abort the build (same guard build-deb.sh uses).
set +o pipefail
echo "--- contents ---"
dpkg-deb --contents "$out" | awk '{print $1, $6}'
echo "--- conffiles (must be EMPTY — config is opt-in, no upgrade prompt) ---"
dpkg-deb --info "$out" | grep -i 'conffiles' || echo "    (no Conffiles — correct)"
set -o pipefail
if command -v lintian >/dev/null 2>&1; then
  lintian "$out" || true
else
  echo "(lintian not installed — skipping deb-lint)"
fi
