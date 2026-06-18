#!/usr/bin/env bash
# install-smoke.sh — exercise packaging/install.sh (the self-contained curl|bash installer)
# against a local file:// feed, with systemd + user-creation stubbed (PDN_* seams). Proves
# the happy path (manifest → download → sha256-verify → releases/<ver> + `current` flip →
# units/helper/polkit/template laid down → update.conf + feed-env written) AND that a bad
# checksum is refused. No network, no root, no systemd. Mirrors selfupdate-smoke.sh.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
installer="$root/packaging/install.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

ver="9.9.9"
pass=0; fail=0
ok()   { echo "  ok: $1"; pass=$((pass+1)); }
bad()  { echo "  FAIL: $1"; fail=$((fail+1)); }

# --- build the feed: support bundle + a fake app payload + latest.json ---
mkdir -p "$work/feed"
"$root/scripts/build-selfcontained-support.sh" "$work/feed/packetnet-selfcontained-support.tar.gz" >/dev/null

mkdir -p "$work/app"
printf '#!/bin/sh\necho fake-packetnet\n' > "$work/app/packetnet"; chmod +x "$work/app/packetnet"
echo '{}' > "$work/app/appsettings.json"
mkdir -p "$work/app/wwwroot"; echo '<html></html>' > "$work/app/wwwroot/index.html"
tar -C "$work/app" -czf "$work/feed/packetnet_${ver}_amd64.tar.gz" .
sha="$(sha256sum "$work/feed/packetnet_${ver}_amd64.tar.gz" | cut -d' ' -f1)"

cat > "$work/feed/latest.json" <<EOF
{ "version": "$ver",
  "artifacts": {
    "amd64": { "file": "packetnet_${ver}_amd64.tar.gz", "sha256": "$sha" },
    "arm64": { "file": "packetnet_${ver}_arm64.tar.gz", "sha256": "$sha" },
    "armhf": { "file": "packetnet_${ver}_armhf.tar.gz", "sha256": "$sha" }
  } }
EOF

# --- a uname shim so the test always resolves amd64 (arch-independent) ---
mkdir -p "$work/bin"; printf '#!/bin/sh\necho x86_64\n' > "$work/bin/uname"; chmod +x "$work/bin/uname"

run_install() {
    PATH="$work/bin:$PATH" \
    FEED_URL="file://$work/feed" \
    PDN_NO_SYSTEMD=1 PDN_SKIP_USER=1 \
    PDN_PREFIX="$work/opt/packetnet" PDN_ETC="$work/etc/packetnet" PDN_STATE="$work/var/packetnet" \
    PDN_UNIT_DIR="$work/units" PDN_LIB_DIR="$work/lib" PDN_POLKIT_DIR="$work/polkit" \
    PDN_SHARE_DIR="$work/share" PDN_DROPIN_DIR="$work/dropin" \
    sh "$installer"
}

echo "== happy path =="
if run_install >/tmp/install-smoke.log 2>&1; then ok "installer exited 0"; else bad "installer failed"; cat /tmp/install-smoke.log; fi

# current symlink → releases/<ver>, with the binary reachable through it
[ "$(readlink "$work/opt/packetnet/current")" = "releases/$ver" ] && ok "current -> releases/$ver" || bad "current symlink wrong"
[ -x "$work/opt/packetnet/current/packetnet" ] && ok "binary reachable via current/" || bad "binary missing via current/"
# the self-contained unit runs from the symlink, not the .deb's app/ path
grep -q 'ExecStart=/opt/packetnet/current/packetnet' "$work/units/packetnet.service" && ok "unit ExecStart uses current/" || bad "unit ExecStart not retargeted"
# support files placed
[ -x "$work/lib/packetnet-update" ]                              && ok "apply helper installed" || bad "apply helper missing"
[ -f "$work/units/packetnet-update.service" ]                    && ok "update unit installed" || bad "update unit missing"
[ -f "$work/polkit/49-packetnet-update.rules" ]                  && ok "polkit rule installed" || bad "polkit rule missing"
[ -f "$work/share/packetnet.yaml.example" ]                      && ok "config template installed" || bad "template missing"
# feed config in BOTH places (helper reads update.conf; node reads the env for its banner)
grep -q "FEED_URL=file://$work/feed" "$work/etc/packetnet/update.conf"            && ok "update.conf has FEED_URL" || bad "update.conf FEED_URL missing"
grep -q "PDN_UPDATE_FEED_URL=file://$work/feed" "$work/dropin/10-selfcontained.conf" && ok "feed env drop-in written" || bad "feed env drop-in missing"

echo "== bad checksum is refused =="
# Corrupt the manifest's sha and re-run into a clean prefix; the installer must refuse + not lay a release.
sed -i "s/$sha/0000000000000000000000000000000000000000000000000000000000000000/g" "$work/feed/latest.json"
rm -rf "$work/opt" "$work/units" "$work/lib"
if PATH="$work/bin:$PATH" FEED_URL="file://$work/feed" PDN_NO_SYSTEMD=1 PDN_SKIP_USER=1 \
   PDN_PREFIX="$work/opt/packetnet" PDN_ETC="$work/etc/packetnet" PDN_STATE="$work/var/packetnet" \
   PDN_UNIT_DIR="$work/units" PDN_LIB_DIR="$work/lib" PDN_POLKIT_DIR="$work/polkit" \
   PDN_SHARE_DIR="$work/share" PDN_DROPIN_DIR="$work/dropin" \
   sh "$installer" >/dev/null 2>&1
then bad "installer accepted a bad checksum"; else ok "installer refused the bad checksum"; fi
[ ! -e "$work/opt/packetnet/current" ] && ok "no release laid down on checksum failure" || bad "release laid down despite bad checksum"

echo ""
echo "install-smoke: $pass passed, $fail failed"
[ "$fail" -eq 0 ] || exit 1
echo "INSTALL_SMOKE_PASS"
