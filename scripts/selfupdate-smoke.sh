#!/usr/bin/env bash
# selfupdate-smoke.sh — exercises packaging/packetnet-selfupdate (the self-contained
# update helper) against a local file:// feed, with the restart stubbed. Proves the
# happy path (download → sha256-verify → unpack → flip current → GC keeps current+prev),
# a checksum mismatch is refused, and an unhealthy node rolls back. No network, no systemd.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
helper="$root/packaging/packetnet-selfupdate"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# A uname shim so the test is arch-independent (always resolves amd64).
mkdir -p "$work/bin"; printf '#!/bin/sh\necho x86_64\n' > "$work/bin/uname"; chmod +x "$work/bin/uname"

# Fixture: a prefix with two existing releases + a feed dir we publish into.
mkdir -p "$work/prefix/releases/0.7.0" "$work/prefix/releases/0.8.0" "$work/feed" "$work/etc"
ln -sfn releases/0.8.0 "$work/prefix/current"
printf 'FEED_URL=file://%s/feed\nHEALTH_URL=file://%s/healthy\n' "$work" "$work" > "$work/etc/update.conf"

publish() {  # publish <version> -> writes the tarball + latest.json with its real sha
  local v="$1"
  echo "build-$v" > "$work/payload"
  tar -C "$work" -czf "$work/feed/packetnet_${v}_amd64.tar.gz" payload
  local sha; sha="$(sha256sum "$work/feed/packetnet_${v}_amd64.tar.gz" | cut -d' ' -f1)"
  printf '{ "version": "%s", "artifacts": { "amd64": { "file": "packetnet_%s_amd64.tar.gz", "sha256": "%s" } } }\n' "$v" "$v" "$sha" > "$work/feed/latest.json"
}
run() { PATH="$work/bin:$PATH" PDN_PREFIX="$work/prefix" PDN_UPDATE_CONF="$work/etc/update.conf" PDN_RESTART_CMD=':' sh "$helper"; }
cur() { readlink "$work/prefix/current"; }
fail() { echo "SMOKE FAIL: $*" >&2; exit 1; }

# 1) Happy path: 0.8.0 -> 0.9.0, current flips, 0.7.0 GC'd, rollback target 0.8.0 kept.
touch "$work/healthy"; publish 0.9.0; run >/dev/null 2>&1
[ "$(cur)" = "releases/0.9.0" ] || fail "happy: current=$(cur) want releases/0.9.0"
[ -d "$work/prefix/releases/0.8.0" ] || fail "happy: rollback target 0.8.0 was GC'd"
[ ! -d "$work/prefix/releases/0.7.0" ] || fail "happy: 0.7.0 not GC'd"
echo "ok: happy path (flip + GC keeps current+prev)"

# 2) Checksum mismatch is refused — current must NOT move.
publish 0.9.1
sed -i 's/"sha256": "[0-9a-f]*"/"sha256": "deadbeef"/' "$work/feed/latest.json"
if run >/dev/null 2>&1; then fail "mismatch: helper exited 0 on a bad checksum"; fi
[ "$(cur)" = "releases/0.9.0" ] || fail "mismatch: current moved to $(cur)"
[ ! -d "$work/prefix/releases/0.9.1" ] || fail "mismatch: a bad release was installed"
echo "ok: checksum mismatch refused"

# 3) Unhealthy new version rolls back to the prior current.
rm -f "$work/healthy"   # HEALTH_URL now 404s → unhealthy
publish 1.0.0
if run >/dev/null 2>&1; then fail "rollback: helper exited 0 despite unhealthy node"; fi
[ "$(cur)" = "releases/0.9.0" ] || fail "rollback: current=$(cur) want releases/0.9.0 (rolled back)"
echo "ok: unhealthy update rolls back"

echo "selfupdate-smoke: all checks passed"
