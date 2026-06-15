#!/usr/bin/env bash
#
# deploy-node.sh — build a real .deb and install it on the live box.
#
# The tight build -> deploy -> show loop for the node host (src/Packet.Node),
# bypassing CI/GHA but using the SAME packaging as the release: build a .deb with
# scripts/build-deb.sh (PDN_FAST=1 — skips R2R/single-file for a fast publish; the
# .deb includes the built web UI under app/wwwroot), scp it to the box, `dpkg -i`
# it (keeping the lab's edited /etc/packetnet/packetnet.yaml conffile), restart the
# systemd service, then print a liveness summary (service state, /healthz, recent
# logs) so you can see it came up. Shipping the real .deb means the dev loop and
# the release install the identical artifact shape — no hand-staged wwwroot.
#
# Default target is root@pdn-lab (Ubuntu/systemd LXC on the LAN, no .NET
# runtime installed -> self-contained). Layout there: /opt/packetnet/app holds
# the binaries + wwwroot (root-owned, read-only to the service); /opt/packetnet
# holds the data, /etc/packetnet the config conffile, owned/edited on the box.

set -euo pipefail

# --- Config (env-overridable) -----------------------------------------------
HOST="${PACKETNET_HOST:-root@pdn-lab}"
SSH_KEY="${PACKETNET_SSH_KEY:-$HOME/.ssh/id_ed25519}"
SERVICE="${PACKETNET_SERVICE:-packetnet}"
REMOTE_APP="${PACKETNET_REMOTE_APP:-/opt/packetnet/app}"
HTTP_PORT="${PACKETNET_HTTP_PORT:-8080}"
RID="linux-x64"
ARCH="amd64"
# A dev version that's distinct per build and genuinely sorts at/above the latest release:
# base it on the newest published node-v* tag, so the node self-update's dev-above-release rule
# reports "up to date" instead of offering a downgrade to that release. The +dev<timestamp>
# build-metadata segment keeps it distinct per build and obviously non-release (and ranks it above
# the plain release of the same base in both semver and `dpkg --compare-versions`). Falls back to
# 0.1.0 if the tag can't be read (e.g. no gh / offline).
_latest_node_tag="$(gh release list -R packet-net/packet.net --json tagName \
  -q '[.[] | select(.tagName | startswith("node-v"))][0].tagName' 2>/dev/null || true)"
_dev_base="${_latest_node_tag#node-v}"; _dev_base="${_dev_base:-0.1.0}"
VERSION="${PACKETNET_DEB_VERSION:-${_dev_base}+dev$(date +%Y%m%d%H%M%S)}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() {
  cat <<'EOF'
deploy-node.sh — build a real .deb and install it on the live box.

Builds a .deb with scripts/build-deb.sh (PDN_FAST=1; the .deb includes the web
UI), scp's it to the deploy box, dpkg-installs it (keeping the box's edited
config conffile), restarts the systemd service, and prints a liveness summary.
The tight build->deploy->show dev loop for src/Packet.Node, no CI wait — and the
same artifact shape GHA ships.

Usage: scripts/deploy-node.sh [--skip-build] [--logs] [-h|--help]

  --skip-build   Deploy the most recent existing artifacts/packetnet_*_amd64.deb
                 without rebuilding.
  --logs         Follow the service log after deploying (Ctrl-C to stop).

Env overrides:
  PACKETNET_HOST         (default root@pdn-lab)
  PACKETNET_SSH_KEY      (default ~/.ssh/id_ed25519)
  PACKETNET_SERVICE      (default packetnet)
  PACKETNET_REMOTE_APP   (default /opt/packetnet/app)
  PACKETNET_HTTP_PORT    (default 8080)
  PACKETNET_DEB_VERSION  (default 0.1.0+dev<UTCstamp>)
EOF
}

SKIP_BUILD=0
FOLLOW_LOGS=0
for arg in "$@"; do
  case "$arg" in
    --skip-build) SKIP_BUILD=1 ;;
    --logs)       FOLLOW_LOGS=1 ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "unknown argument: $arg (try --help)" >&2; exit 2 ;;
  esac
done

SSH=(ssh -i "$SSH_KEY" -o BatchMode=yes)
SCP=(scp -i "$SSH_KEY" -o BatchMode=yes)
say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }

# --- 1. Build the .deb ------------------------------------------------------
# PDN_FAST=1: a fast publish (no R2R/single-file) — the dev-loop tradeoff. The
# .deb carries the built web UI under app/wwwroot.
if [[ "$SKIP_BUILD" -eq 1 ]]; then
  DEB="$(ls -t "$REPO_ROOT"/artifacts/packetnet_*_"$ARCH".deb 2>/dev/null | head -n1)"
  [[ -n "$DEB" && -f "$DEB" ]] || { echo "no artifacts/packetnet_*_${ARCH}.deb — build first (run without --skip-build)" >&2; exit 1; }
  say "Skipping build; deploying most recent .deb: $DEB"
else
  say "Building .deb $VERSION ($RID, PDN_FAST)"
  PDN_FAST=1 "$REPO_ROOT/scripts/build-deb.sh" "$RID" "$VERSION"
  DEB="$REPO_ROOT/artifacts/packetnet_${VERSION}_${ARCH}.deb"
  [[ -f "$DEB" ]] || { echo "expected $DEB but it wasn't produced" >&2; exit 1; }
fi
DEB_BASE="$(basename "$DEB")"

# --- 2. Ship + install ------------------------------------------------------
# scp to /tmp, then dpkg -i (reinstalls regardless of version; --force-confold
# keeps the box's edited /etc/packetnet/packetnet.yaml conffile). Fall back to
# apt -f install only if dpkg reports unmet deps (the box already has
# adduser+libc6, so this should be clean). Then remove the staged .deb.
say "Shipping $DEB_BASE to $HOST:/tmp"
"${SCP[@]}" "$DEB" "$HOST:/tmp/$DEB_BASE"

say "Installing on $HOST (dpkg -i --force-confold)"
"${SSH[@]}" "$HOST" bash -s "$DEB_BASE" <<'REMOTE'
set -e
deb="$1"
if ! dpkg -i --force-confold "/tmp/$deb"; then
  echo "dpkg reported unmet deps — running apt-get -f install"
  apt-get -y -f install
fi
rm -f "/tmp/$deb"
REMOTE

# --- 3. Restart -------------------------------------------------------------
# The .deb postinst try-restarts on upgrade, but be explicit: a fresh install or
# a stopped unit wouldn't be (re)started by the loaded new binary otherwise.
say "Restarting $SERVICE"
"${SSH[@]}" "$HOST" "systemctl restart $SERVICE"

# --- 4. Verify --------------------------------------------------------------
# The box has no curl; probe /healthz with bash's /dev/tcp. systemd marks the
# unit "active" as soon as the process execs (Type=simple), but Kestrel binds a
# beat later, so poll until /healthz answers rather than racing it. Run the
# remote check as a stdin script (bash -s) with args, to avoid quote-escaping.
say "Verifying"
"${SSH[@]}" "$HOST" bash -s "$SERVICE" "$HTTP_PORT" <<'REMOTE' || true
svc="$1"; port="$2"
printf 'service: '; systemctl is-active "$svc" || true
printf 'healthz: '
ok=0
for _ in $(seq 1 20); do
  if { exec 3<>/dev/tcp/127.0.0.1/"$port"; } 2>/dev/null; then
    printf 'GET /healthz HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3
    body=$(timeout 3 cat <&3 | tr -d '\r' | tail -n1)
    exec 3>&- 2>/dev/null || true
    echo "$body"; ok=1; break
  fi
  sleep 0.5
done
[ "$ok" -eq 1 ] || echo "(no answer on 127.0.0.1:$port after 10s)"
echo '--- recent logs ---'
journalctl -u "$svc" -n 6 --no-pager -o cat
REMOTE

say "Done — $HOST updated."

if [[ "$FOLLOW_LOGS" -eq 1 ]]; then
  say "Following logs (Ctrl-C to stop)"
  exec "${SSH[@]}" "$HOST" "journalctl -u $SERVICE -f -o cat"
fi
