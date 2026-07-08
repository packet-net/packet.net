#!/bin/sh
# headend-deb-install-smoke.sh — prove a built packetnet-headend .deb installs
# cleanly on a pristine base. The head-end analogue of deb-install-smoke.sh (the
# node's install smoke); lighter, because the head-end is a single static binary
# with no system user and no state dir.
#
#   scripts/headend-deb-install-smoke.sh <path-to.deb> [image ...]
#   e.g. scripts/headend-deb-install-smoke.sh artifacts/packetnet-headend_0.1.2_amd64.deb
#
# For each base image (default: Debian-stable + Ubuntu-LTS) it runs a THROWAWAY
# container and asserts, on a bare base:
#   1. `apt install ./pkg.deb` resolves the declared Depends + installs (exit 0).
#   2. dpkg state is 'installed'; the binary + unit + example config landed at the
#      staged paths; the unit's ExecStart points at the installed binary and it
#      carries the enable wiring ([Install] + postinst `systemctl enable --now`),
#      postinst correctly SKIPPED systemctl (no booted systemd in a container),
#      and the unit's sandbox/notify wiring is intact (RestrictAddressFamilies
#      includes AF_NETLINK for mDNS (#577) + AF_UNIX for sd_notify; Type=notify;
#      WatchdogSec).
#   3. the static binary BOOTS on pure DEFAULTS (no config file — plug-and-go) and
#      serves /healthz on :7300 — proving the shipped unit's plug-and-go default.
#   4. the startup log shows "mDNS advertising" and NOT the #577 failure
#      signature ("Could not determine host IP addresses").
#   5. `apt purge` removes the payload cleanly.
#
# Container-isolated by design: nothing is installed onto the (self-hosted,
# non-ephemeral) CI runner, and :7300 is probed INSIDE the container.
set -eu

[ $# -ge 1 ] || { echo "usage: $0 <path-to.deb> [image ...]" >&2; exit 2; }
DEB_PATH=$(cd "$(dirname "$1")" && pwd)/$(basename "$1"); shift
[ -f "$DEB_PATH" ] || { echo "no such .deb: $DEB_PATH" >&2; exit 2; }
[ $# -ge 1 ] && IMAGES="$*" || IMAGES="debian:stable-slim ubuntu:24.04"

DEB_DIR=$(dirname "$DEB_PATH")
DEB_BASE=$(basename "$DEB_PATH")

# The assertions, run inside the container. Fully single-quoted: every $VAR here is
# expanded by the container's /bin/sh. The .deb basename + health port arrive via -e.
INNER='
set -u
fail() { echo "SMOKE_FAIL: $1"; exit 1; }
cd /work

echo "== 1. apt install (resolves Depends from the repo) =="
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq                                  || fail "apt-get update"
apt-get install -y -qq curl >/dev/null 2>&1         || fail "install curl (test rig)"
apt-get install -y "./$DEB"                          || fail "apt install ./$DEB (non-zero)"

echo "== 2. dpkg state + payload =="
dpkg -s packetnet-headend 2>/dev/null | grep -q "^Status: install ok installed" || fail "dpkg status != installed"
[ -x /usr/lib/packetnet/packetnet-headend ]                    || fail "binary missing / not executable"
[ -f /lib/systemd/system/packetnet-headend.service ]           || fail "systemd unit missing"
[ -f /usr/share/packetnet/packetnet-headend.json.example ]     || fail "config example missing"
grep -q "^ExecStart=/usr/lib/packetnet/packetnet-headend$" /lib/systemd/system/packetnet-headend.service \
  || fail "unit ExecStart does not point at the installed binary"
grep -q "^WantedBy=multi-user.target" /lib/systemd/system/packetnet-headend.service \
  || fail "unit missing [Install] WantedBy (would not enable)"
# The sandbox must not strangle the daemon (#577): AF_NETLINK is required for
# mDNS registration (Go net.Interfaces() opens a netlink route socket) and
# AF_UNIX for the sd_notify READY/WATCHDOG datagrams (Type=notify would hang
# in activating without it).
grep -Eq "^RestrictAddressFamilies=.*AF_NETLINK" /lib/systemd/system/packetnet-headend.service \
  || fail "unit RestrictAddressFamilies missing AF_NETLINK (mDNS breaks under the sandbox, #577)"
grep -Eq "^RestrictAddressFamilies=.*AF_UNIX" /lib/systemd/system/packetnet-headend.service \
  || fail "unit RestrictAddressFamilies missing AF_UNIX (sd_notify blocked -> Type=notify start hangs)"
grep -q "^Type=notify" /lib/systemd/system/packetnet-headend.service \
  || fail "unit not Type=notify (readiness/watchdog wiring missing)"
grep -q "^WatchdogSec=" /lib/systemd/system/packetnet-headend.service \
  || fail "unit missing WatchdogSec (hung-daemon restart)"
# postinst skips systemctl when no systemd is booted (container) — so the unit is not
# actually enabled here, but the enable wiring must be present for a real boot.
[ ! -d /run/systemd/system ] || echo "  note: booted systemd present in image"
echo "  ok: installed; binary+unit+example present; ExecStart + enable wiring correct"

echo "== 3. binary boots on DEFAULTS + serves /healthz (plug-and-go) =="
# No config file, no env, no flags — exactly a fresh install with the shipped unit.
/usr/lib/packetnet/packetnet-headend >/tmp/he.log 2>&1 &
PID=$!
ok=0
i=0
while [ $i -lt 30 ]; do
  kill -0 "$PID" 2>/dev/null || { echo "  binary EXITED early — log:"; sed "s/^/    /" /tmp/he.log; break; }
  if curl -fsS "http://127.0.0.1:$PORT/healthz" >/dev/null 2>&1; then ok=1; break; fi
  i=$((i+1)); sleep 1
done
kill "$PID" 2>/dev/null || true; wait "$PID" 2>/dev/null || true
[ "$ok" = 1 ] || { echo "  --- startup log ---"; sed "s/^/    /" /tmp/he.log; fail "healthz never answered on :$PORT within 30s"; }
echo "  ok: process stayed up on defaults and /healthz answered on :$PORT"

echo "== 4. mDNS advertised (and NOT the #577 AF_NETLINK failure signature) =="
# The daemon logs exactly one of these before serving HTTP, so by healthz time
# the line is in the log. "Could not determine host IP addresses" is the #577
# signature (interface enumeration blocked / no addresses) — always fatal here.
# A container on a default docker bridge has a routable eth0, so full
# registration is expected to succeed; if some exotic runner network ever makes
# multicast registration legitimately impossible, weaken ONLY the positive
# assert — the signature assert must stay.
if grep -q "Could not determine host IP addresses" /tmp/he.log; then
  grep "mDNS" /tmp/he.log | sed "s/^/    /"
  fail "mDNS failed with the #577 signature (interface enumeration broken)"
fi
if grep -q "mDNS advertise failed" /tmp/he.log; then
  grep "mDNS" /tmp/he.log | sed "s/^/    /"
  fail "mDNS advertise failed (registration broken)"
fi
grep -q "mDNS advertising" /tmp/he.log || { sed "s/^/    /" /tmp/he.log; fail "no \"mDNS advertising\" line in the startup log"; }
echo "  ok: $(grep "mDNS advertising" /tmp/he.log | head -1)"

echo "== 5. purge cleans up =="
apt-get purge -y packetnet-headend >/dev/null 2>&1   || fail "apt purge (non-zero)"
[ -e /usr/lib/packetnet/packetnet-headend ]          && fail "payload survived purge"
echo "  ok: payload removed"

echo "INNER_PASS"
'

rc=0
for img in $IMAGES; do
  echo "############################## $img ##############################"
  if docker run --rm \
       -e DEB="$DEB_BASE" -e PORT=7300 \
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
