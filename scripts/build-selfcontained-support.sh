#!/usr/bin/env bash
# build-selfcontained-support.sh — assemble the self-contained channel's "support bundle":
# the files install.sh lays down once at install time (the systemd units, the privileged
# update helper, the polkit rule, the config template). The per-release app payload is the
# separate packetnet_<ver>_<arch>.tar.gz; this bundle is the version-independent scaffolding.
#
#   scripts/build-selfcontained-support.sh [output.tar.gz]
#   (default: artifacts/packetnet-selfcontained-support.tar.gz)
#
# Single source of truth: every file comes from packaging/, so there is no duplicate unit
# to drift. The ONE transform is the service ExecStart — a self-contained install runs from
# the `current` symlink the in-app updater flips (/opt/packetnet/current), not the .deb's
# fixed /opt/packetnet/app. Used by both publish-node.yml (to publish) and install-smoke.sh.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/artifacts/packetnet-selfcontained-support.tar.gz}"
pkg="$root/packaging"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT

# The unit, retargeted from the .deb's app/ path to the self-contained current/ symlink.
sed 's#/opt/packetnet/app/packetnet#/opt/packetnet/current/packetnet#' \
    "$pkg/packetnet.service" > "$stage/packetnet.service"
grep -q '/opt/packetnet/current/packetnet' "$stage/packetnet.service" \
    || { echo "ERROR: ExecStart retarget failed (packetnet.service shape changed?)" >&2; exit 1; }

cp "$pkg/packetnet-update.service"   "$stage/packetnet-update.service"
cp "$pkg/packetnet-selfupdate"       "$stage/packetnet-update"          # the self-contained apply helper
cp "$pkg/49-packetnet-update.rules"  "$stage/49-packetnet-update.rules"
cp "$pkg/packetnet.yaml"             "$stage/packetnet.yaml.example"
chmod 0755 "$stage/packetnet-update"

mkdir -p "$(dirname "$out")"
tar -C "$stage" -czf "$out" \
    packetnet.service packetnet-update.service packetnet-update 49-packetnet-update.rules packetnet.yaml.example
echo "wrote $out"
tar -tzf "$out" | sed 's/^/  /'
