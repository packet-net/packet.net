#!/usr/bin/env bash
#
# deploy-node.sh — build the Packet.NET node host and push it to the live box.
#
# The tight build -> deploy -> show loop for the node host (src/Packet.Node),
# bypassing CI/GHA: publish a self-contained linux-x64 binary, rsync it to the
# deploy box, restart the systemd service, then print a liveness summary
# (service state, /healthz, recent logs) so you can see it came up.
#
# Default target is root@packetdotnet (Ubuntu/systemd LXC on the LAN, no .NET
# runtime installed -> self-contained). Layout there: /opt/packetnet/app holds
# the binaries (root-owned, read-only to the service); /opt/packetnet holds the
# config (packetnet.yaml) + data, owned by the unprivileged `packetnet` user.

set -euo pipefail

# --- Config (env-overridable) -----------------------------------------------
HOST="${PACKETNET_HOST:-root@packetdotnet}"
SSH_KEY="${PACKETNET_SSH_KEY:-$HOME/.ssh/id_ed25519}"
SERVICE="${PACKETNET_SERVICE:-packetnet}"
REMOTE_APP="${PACKETNET_REMOTE_APP:-/opt/packetnet/app}"
HTTP_PORT="${PACKETNET_HTTP_PORT:-8080}"
RID="linux-x64"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$REPO_ROOT/src/Packet.Node/Packet.Node.csproj"
ARTIFACT="$REPO_ROOT/artifacts/node-$RID"

usage() {
  cat <<'EOF'
deploy-node.sh — build the node host and push it to the live box.

Publishes a self-contained linux-x64 binary, rsyncs it to the deploy box,
restarts the systemd service, and prints a liveness summary. The tight
build->deploy->show dev loop for src/Packet.Node, no CI wait.

Usage: scripts/deploy-node.sh [--skip-build] [--logs] [-h|--help]

  --skip-build   Deploy the existing artifact without rebuilding.
  --logs         Follow the service log after deploying (Ctrl-C to stop).

Env overrides:
  PACKETNET_HOST        (default root@packetdotnet)
  PACKETNET_SSH_KEY     (default ~/.ssh/id_ed25519)
  PACKETNET_SERVICE     (default packetnet)
  PACKETNET_REMOTE_APP  (default /opt/packetnet/app)
  PACKETNET_HTTP_PORT   (default 8080)
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
say() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }

# --- 1. Build ---------------------------------------------------------------
if [[ "$SKIP_BUILD" -eq 1 ]]; then
  say "Skipping build; deploying existing $ARTIFACT"
  [[ -x "$ARTIFACT/packetnet" ]] || { echo "no artifact at $ARTIFACT — build first (run without --skip-build)" >&2; exit 1; }
else
  say "Publishing $RID self-contained"
  dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -v minimal -o "$ARTIFACT"
fi

# --- 2. Sync ----------------------------------------------------------------
# --no-o/--no-g: don't carry the local uid/gid onto the box, so app/ stays
# root-owned and stable across redeploys. Config/data live outside app/ and are
# left untouched by --delete.
say "Syncing to $HOST:$REMOTE_APP"
rsync -az --delete --no-o --no-g -e "ssh -i $SSH_KEY -o BatchMode=yes" \
  "$ARTIFACT/" "$HOST:$REMOTE_APP/"

# --- 3. Restart -------------------------------------------------------------
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
