#!/usr/bin/env bash
# Screenshot every screen of the built UI, for visual verification.
#
# WHY DOCKER: the dev box (an LXC) denies a host Chrome's network sockets
# (CreatePlatformSocket: Permission denied) and Playwright's chromium doesn't
# support the host OS — so no host browser can reach a local dev server here.
# A debian container has working sockets + a Playwright-supported OS, so we run
# the whole thing (serve dist + drive chromium) inside one container and write
# the PNGs out to a mounted dir.
#
# Usage:  scripts/screenshot.sh [OUTDIR]   (default: .shots/)
#         VITE_API_MODE=live scripts/screenshot.sh   # screenshot a live build
set -euo pipefail

here="$(cd "$(dirname "$0")/.." && pwd)"        # web/packetnet-ui
out="${1:-$here/.shots}"
mkdir -p "$out"

echo "==> building dist (VITE_API_MODE=${VITE_API_MODE:-mock})"
( cd "$here" && npm run build >/dev/null )

echo "==> screenshotting in a debian container (node:22-bookworm-slim + playwright chromium)"
docker run --rm --shm-size=1g \
  -v "$here/dist:/dist:ro" \
  -v "$here/scripts/screenshot.mjs:/tmp/shoot.mjs:ro" \
  -v "$out:/shots" \
  node:22-bookworm-slim bash -lc '
    set -e
    cd /tmp && npm init -y >/dev/null 2>&1
    npm i playwright serve >/tmp/npm.log 2>&1
    npx playwright install --with-deps chromium >/tmp/pw.log 2>&1
    npx serve -s /dist -l 4173 >/tmp/serve.log 2>&1 &
    sleep 3
    node /tmp/shoot.mjs
  '

echo "==> screenshots in $out:"
ls -1 "$out"
