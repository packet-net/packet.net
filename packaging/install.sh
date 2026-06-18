#!/bin/sh
# install.sh — one-line first install of a pdn (Packet.NET) node on the SELF-CONTAINED
# update channel. Run as root:
#
#     curl -fsSL https://pdn-dist.m0lte.compute.oarc.uk/install.sh | sudo sh
#
# It detects the architecture, pulls the latest self-contained build + its support files
# from the feed, lays the binary out under /opt/packetnet in the releases/<ver> + `current`
# symlink shape the in-app updater (packetnet-update) flips, installs the systemd units +
# the privileged update helper + polkit rule, writes the feed config, and starts the node.
# After this, the node self-updates over the same feed from the web UI's Apply button.
#
# Integrity is checksum-verified against latest.json (cosign is a planned hardening, like
# the apply helper). docs/node-self-update-design.md.
#
# Every path + the privileged actions are overridable via PDN_* env so the whole flow is
# testable against a fixture without root or systemd (see scripts/install-smoke.sh).
set -eu

TAG=pdn-install
FEED_URL="${FEED_URL:-https://pdn-dist.m0lte.compute.oarc.uk}"
FEED_URL="${FEED_URL%/}"                                   # tolerate a trailing slash
PREFIX="${PDN_PREFIX:-/opt/packetnet}"
RELEASES="$PREFIX/releases"
CURRENT="$PREFIX/current"
ETC="${PDN_ETC:-/etc/packetnet}"
STATE="${PDN_STATE:-/var/lib/packetnet}"
UNIT_DIR="${PDN_UNIT_DIR:-/lib/systemd/system}"
LIB_DIR="${PDN_LIB_DIR:-/usr/lib/packetnet}"
POLKIT_DIR="${PDN_POLKIT_DIR:-/usr/share/polkit-1/rules.d}"
SHARE_DIR="${PDN_SHARE_DIR:-/usr/share/packetnet}"
DROPIN_DIR="${PDN_DROPIN_DIR:-/etc/systemd/system/packetnet.service.d}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:8080/healthz}"
SVC_USER="${PDN_USER:-packetnet}"
# Test seams: skip the privileged bits a fixture can't do.
NO_SYSTEMD="${PDN_NO_SYSTEMD:-0}"   # 1 → don't touch systemctl
SKIP_USER="${PDN_SKIP_USER:-0}"     # 1 → don't create the system user

log()  { logger -t "$TAG" -- "$*" 2>/dev/null || true; echo "$TAG: $*"; }
fail() { log "ERROR: $*"; exit 1; }

[ "$(id -u)" = 0 ] || [ "$NO_SYSTEMD" = 1 ] || fail "run as root (curl ... | sudo sh)"
command -v curl >/dev/null 2>&1 || fail "curl is required"
command -v tar  >/dev/null 2>&1 || fail "tar is required"

case "$(uname -m)" in
    x86_64)        arch=amd64 ;;
    aarch64)       arch=arm64 ;;
    armv7l|armv6l) arch=armhf ;;
    *)             fail "unsupported architecture $(uname -m)" ;;
esac

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# --- 1. resolve the release from the feed (same manifest shape the apply helper reads) ---
log "fetching feed manifest from $FEED_URL (arch=$arch)"
curl -fsSL --max-time 30 "$FEED_URL/latest.json" -o "$work/latest.json" || fail "feed unreachable: $FEED_URL/latest.json"
ver="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$work/latest.json" | head -1)"
block="$(grep -o "\"$arch\"[[:space:]]*:[[:space:]]*{[^}]*}" "$work/latest.json" || true)"
afile="$(printf '%s' "$block" | sed -n 's/.*"file"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
asha="$(printf '%s' "$block" | sed -n 's/.*"sha256"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
[ -n "$ver" ] && [ -n "$afile" ] && [ -n "$asha" ] || fail "malformed latest.json (no version / $arch entry)"

# --- 2. download + verify the app payload + the support bundle ---
log "downloading $afile (version $ver)"
curl -fsSL --max-time 600 "$FEED_URL/$afile" -o "$work/app.tar.gz" || fail "download failed: $afile"
calc="$(sha256sum "$work/app.tar.gz" | cut -d' ' -f1)"
[ "$calc" = "$asha" ] || fail "sha256 MISMATCH (want $asha, got $calc) — refusing to install"

log "downloading support bundle"
curl -fsSL --max-time 120 "$FEED_URL/packetnet-selfcontained-support.tar.gz" -o "$work/support.tar.gz" \
    || fail "support bundle unreachable"
mkdir -p "$work/support"
tar -C "$work/support" -xzf "$work/support.tar.gz" || fail "support bundle unpack failed"
for need in packetnet.service packetnet-update.service packetnet-update 49-packetnet-update.rules packetnet.yaml.example; do
    [ -f "$work/support/$need" ] || fail "support bundle missing $need"
done

# --- 3. system user + directories (mirrors the .deb postinst) ---
if [ "$SKIP_USER" != 1 ]; then
    if ! getent passwd "$SVC_USER" >/dev/null 2>&1; then
        log "creating system user $SVC_USER"
        adduser --system --group --no-create-home --home "$STATE" --shell /usr/sbin/nologin "$SVC_USER" \
            || useradd --system --no-create-home --home-dir "$STATE" --shell /usr/sbin/nologin "$SVC_USER" \
            || fail "could not create user $SVC_USER"
    fi
fi
install -d -m 0750 "$STATE" "$STATE/certs" "$STATE/apps" "$STATE/apps/wall" "$STATE/apps/lobby"
[ "$SKIP_USER" = 1 ] || chown -R "$SVC_USER:$SVC_USER" "$STATE" 2>/dev/null || true

# --- 4. lay the release out under PREFIX (releases/<ver> + atomic `current` flip) ---
log "installing release $ver under $PREFIX"
install -d "$RELEASES"
dest="$RELEASES/$ver"
rm -rf "$dest.tmp"; mkdir -p "$dest.tmp"
tar -C "$dest.tmp" -xzf "$work/app.tar.gz" || { rm -rf "$dest.tmp"; fail "app unpack failed"; }
[ -x "$dest.tmp/packetnet" ] || fail "release payload has no packetnet binary"
rm -rf "$dest"; mv "$dest.tmp" "$dest"
ln -sfn "releases/$ver" "$CURRENT.new"; mv -T "$CURRENT.new" "$CURRENT"

# --- 5. support files: units + update helper + polkit rule + config template ---
install -d "$UNIT_DIR" "$LIB_DIR" "$POLKIT_DIR" "$SHARE_DIR" "$ETC" "$DROPIN_DIR"
install -m 0644 "$work/support/packetnet.service"          "$UNIT_DIR/packetnet.service"
install -m 0644 "$work/support/packetnet-update.service"   "$UNIT_DIR/packetnet-update.service"
install -m 0755 "$work/support/packetnet-update"           "$LIB_DIR/packetnet-update"
install -m 0644 "$work/support/49-packetnet-update.rules"  "$POLKIT_DIR/49-packetnet-update.rules"
install -m 0644 "$work/support/packetnet.yaml.example"     "$SHARE_DIR/packetnet.yaml.example"

# --- 6. feed config: the apply helper reads update.conf; the node reads the env for its
#        available-version banner. Both point at THIS feed. ---
umask 022
printf 'FEED_URL=%s\nHEALTH_URL=%s\n' "$FEED_URL" "$HEALTH_URL" > "$ETC/update.conf"
cat > "$DROPIN_DIR/10-selfcontained.conf" <<EOF
[Service]
Environment=PDN_UPDATE_FEED_URL=$FEED_URL
EOF

# --- 7. start it ---
if [ "$NO_SYSTEMD" != 1 ] && [ -d /run/systemd/system ]; then
    systemctl daemon-reload || true
    systemctl enable --now packetnet.service || fail "failed to start packetnet.service"
    log "started packetnet.service (version $ver)"
    i=0
    while [ "$i" -lt 30 ]; do
        if curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then log "healthz OK — node is up"; break; fi
        i=$((i + 1)); sleep 1
    done
    [ "$i" -lt 30 ] || log "WARNING: node did not answer $HEALTH_URL within 30s; check 'journalctl -u packetnet'"
else
    log "systemd not used (PDN_NO_SYSTEMD/${NO_SYSTEMD}); installed but not started"
fi

log "done — pdn $ver installed on the self-contained channel (feed $FEED_URL)"
