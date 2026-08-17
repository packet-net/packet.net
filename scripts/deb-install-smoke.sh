#!/bin/sh
# deb-install-smoke.sh — prove a built .deb installs cleanly on a pristine base.
#
#   scripts/deb-install-smoke.sh <path-to.deb> [image ...]
#   e.g. scripts/deb-install-smoke.sh artifacts/packetnet_0.3.0_amd64.deb
#
# For each base image (default: Debian-stable + Ubuntu-LTS, the suites the .deb
# claims) it runs a THROWAWAY container and asserts, on a bare base:
#   1. `apt install ./pkg.deb` resolves the declared Depends + installs (exit 0)
#      — `apt`, not `dpkg -i`, because adduser is NOT in slim images and must be
#      pulled from the repo (a `dpkg -i` would fail with an unmet dependency).
#   2. dpkg state is 'installed'; binary + unit + conffile landed; postinst made
#      the system user (and correctly skipped systemctl — the container has no
#      booted systemd, which the maintainer scripts guard with -d /run/systemd/system).
#   3. the self-contained binary actually BOOTS and serves /healthz on a bare base
#      (the real payoff: proves no missing native dep — libicu etc. — given the
#      InvariantGlobalization self-contained publish; runtime start under systemd
#      is covered separately by the lab deploy).
#   4. `apt purge` removes the user + payload cleanly.
#
# Container-isolated by design: nothing is installed onto the (self-hosted,
# non-ephemeral) CI runner, and the node's :8080 is probed INSIDE the container
# (no published port), so there is no host pollution or port clash.
set -eu

[ $# -ge 1 ] || { echo "usage: $0 <path-to.deb> [image ...]" >&2; exit 2; }
DEB_PATH=$(cd "$(dirname "$1")" && pwd)/$(basename "$1"); shift
[ -f "$DEB_PATH" ] || { echo "no such .deb: $DEB_PATH" >&2; exit 2; }
[ $# -ge 1 ] && IMAGES="$*" || IMAGES="debian:stable-slim ubuntu:24.04"

DEB_DIR=$(dirname "$DEB_PATH")
DEB_BASE=$(basename "$DEB_PATH")

# The assertions, run inside the container. Fully single-quoted: every $VAR here
# is expanded by the container's /bin/sh, not the host. The .deb basename and the
# health port arrive via -e env so this stays interpolation-free.
INNER='
set -u
fail() { echo "SMOKE_FAIL: $1"; exit 1; }
cd /work

echo "== 1. apt install (resolves Depends from the repo) =="
# NOTHING is installed here first. The base images ship no curl, and step 3 probes /healthz
# with it, so the smoke only gets that far if the .deb DECLARED curl - which it must, because
# the github self-update helper (/usr/lib/packetnet/packetnet-github-update) shells out to it
# with no wget fallback. Pre-installing curl as a "test rig" step is what hid the missing
# Depends (packet.net#699 / C100).
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq                                  || fail "apt-get update"
apt-get install -y "./$DEB"                          || fail "apt install ./$DEB (non-zero)"

echo "== 2. dpkg state + payload =="
dpkg -s packetnet 2>/dev/null | grep -q "^Status: install ok installed" || fail "dpkg status != installed"
[ -x /opt/packetnet/app/packetnet ]          || fail "binary missing / not executable"
[ -f /lib/systemd/system/packetnet.service ] || fail "systemd unit missing"
# The unit must grant CAP_NET_BIND_SERVICE so the non-root service (and the
# pdn-supervised app daemons that inherit its ambient caps) can bind privileged
# ports (<1024) on a fresh install — IMAP 993 / SMTP 465/587 — with no manual
# systemd surgery (packet.net#469). Bounding set is pinned to the same single cap.
grep -q "^AmbientCapabilities=CAP_NET_BIND_SERVICE\b"     /lib/systemd/system/packetnet.service || fail "unit missing AmbientCapabilities=CAP_NET_BIND_SERVICE (cannot bind privileged ports)"
grep -q "^CapabilityBoundingSet=CAP_NET_BIND_SERVICE\b"   /lib/systemd/system/packetnet.service || fail "unit missing CapabilityBoundingSet=CAP_NET_BIND_SERVICE (ambient cap would be outside the bounding set)"
# Config now lives in pdn.db under the systemd StateDirectory (/var/lib/packetnet),
# NOT a dpkg conffile (#473) — so the package ships no /etc/packetnet; instead a
# read-only template at /usr/share seeds the DB on first boot. The dpkg conffile
# (and its upgrade prompt) is gone by design.
[ -f /usr/share/packetnet/packetnet.yaml.example ] || fail "config template missing (config-in-DB bootstrap, #473)"
id packetnet >/dev/null 2>&1                        || fail "postinst did not create the packetnet user"
# The declared Depends must cover what the shipped maintainer/helper scripts actually run.
# curl is the one with teeth: packetnet-github-update downloads the release .deb and the
# release SHA256SUMS with it, so a node whose Depends omits it cannot self-update at all
# (packet.net#699 / C100). Nothing installed it here - only the .deb could have.
command -v curl >/dev/null 2>&1                     || fail "curl absent: the declared Depends do not cover the github self-update helper"
echo "  ok: installed; binary+unit+config-template present; user created; curl pulled in by Depends; systemctl correctly skipped"

echo "== 3. binary boots + serves /healthz on a bare base =="
export DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/pnx HOME=/root
mkdir -p /tmp/pnx
# Boot from a WRITABLE state dir, the way the unit does (WorkingDirectory=StateDirectory):
# pdn.db, the config blob and the first-run marker live in the cwd. /work is the read-only
# .deb mount; booting there used to pass only because an unreadable user store failed
# OPEN and printed First-run setup pending regardless. It now fails closed (packet.net#693,
# C026), so the smoke must give the node a real state dir, which also makes the first-run
# detection it asserts on a genuine one.
mkdir -p /tmp/pnstate
cd /tmp/pnstate
/opt/packetnet/app/packetnet --config /etc/packetnet/packetnet.yaml >/tmp/pn.log 2>&1 &
PID=$!
ok=0
i=0
while [ $i -lt 60 ]; do
  kill -0 "$PID" 2>/dev/null || { echo "  binary EXITED early — log:"; sed "s/^/    /" /tmp/pn.log; break; }
  if curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then ok=1; break; fi
  i=$((i+1)); sleep 1
done
urls_ok=0
if [ "$ok" = 1 ]; then
  # The startup log must NAME the panel (the only signpost a headless operator gets)
  # and flag first-run setup on a fresh node. Logged on ApplicationStarted, so it may
  # trail the first healthz answer by a beat - allow a few seconds for the logger to
  # flush. NOTE: this whole INNER block is one single-quoted string - no apostrophes.
  i=0
  while [ $i -lt 10 ]; do
    if grep -q "Control panel: http" /tmp/pn.log && grep -q "First-run setup pending" /tmp/pn.log; then urls_ok=1; break; fi
    i=$((i+1)); sleep 1
  done
fi
kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true
[ "$ok" = 1 ] || { echo "  --- startup log ---"; sed "s/^/    /" /tmp/pn.log; fail "healthz never answered on :$PORT within 60s"; }
[ "$urls_ok" = 1 ] || { echo "  --- startup log ---"; sed "s/^/    /" /tmp/pn.log; fail "startup log never named the control panel URL / first-run setup"; }
echo "  ok: process stayed up, /healthz answered on :$PORT, startup log names the panel URL + pending setup"

echo "== 4. purge cleans up =="
apt-get purge -y packetnet >/dev/null 2>&1   || fail "apt purge (non-zero)"
id packetnet >/dev/null 2>&1                 && fail "packetnet user survived purge"
[ -e /opt/packetnet/app/packetnet ]          && fail "payload survived purge"
echo "  ok: user + payload removed"

echo "INNER_PASS"
'

rc=0
for img in $IMAGES; do
  echo "############################## $img ##############################"
  if docker run --rm \
       -e DEB="$DEB_BASE" -e PORT=8080 \
       -v "$DEB_DIR":/work:ro \
       "$img" sh -c "$INNER"; then
    echo ">>> $img: PASS"
  else
    echo ">>> $img: FAIL"
    rc=1
  fi
done

[ "$rc" = 0 ] && echo "SMOKE_PASS (all images)" || echo "SMOKE_FAIL (one or more images)"
exit "$rc"
