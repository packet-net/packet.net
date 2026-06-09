#!/usr/bin/env bash
# ============================================================
# Passkey end-to-end proof for node-passkeys (auth part 3) — the headline verification.
#
# Proves the localhost-first WebAuthn flow ACTUALLY works end-to-end: register a passkey
# then sign in with it passwordlessly, against a real pdn node, using a Chrome
# DevTools-Protocol VIRTUAL AUTHENTICATOR (so the ceremony is genuine — real signed
# attestation/assertion the node's Fido2 verifier accepts — not a stub).
#
# WHY DOCKER (same reason as scripts/screenshot.sh): the dev box is an LXC that denies a
# host Chrome's network sockets, and Playwright's chromium doesn't support the host OS.
# A debian container has working sockets + a Playwright-supported OS. So we run BOTH the
# node backend AND the browser inside ONE container, talking over loopback —
# http://localhost:8080, which is a SECURE CONTEXT over plain HTTP (no cert needed) and a
# legal WebAuthn RP id (localhost). That is exactly the tier-1 case the trust-pattern doc
# (docs/passkeys-lan-trust-pattern.md §2) says works with zero config.
#
# To keep the container to node:22-bookworm-slim + Playwright (no .NET install), the node
# is published SELF-CONTAINED for linux-x64 on the host and just run in the container.
#
# Usage:  scripts/passkey-e2e.sh
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
ui="$root/web/packetnet-ui"
proj="$root/src/Packet.Node/Packet.Node.csproj"
work="$(mktemp -d)"
pub="$work/app"
host_uid="$(id -u)"; host_gid="$(id -g)"
# The container runs as root (for playwright --with-deps apt); it chowns /state back to
# the host uid at the end so this trap can remove it. Fall back to a docker-based rm if
# anything is still root-owned (e.g. the container died before the chown).
cleanup() {
  rm -rf "$work" 2>/dev/null && return 0
  docker run --rm -v "$work:/w" node:22-bookworm-slim bash -c 'rm -rf /w/* /w/.[!.]* 2>/dev/null || true' || true
  rm -rf "$work" 2>/dev/null || true
}
trap cleanup EXIT

rid="linux-x64"

echo "==> build the SPA (VITE_API_MODE=live)"
( cd "$ui" && { [ -d node_modules ] || npm ci; } && VITE_API_MODE=live npm run build >/dev/null )

echo "==> publish the node self-contained ($rid) — no R2R/single-file (faster; runs once)"
dotnet publish "$proj" -c Release -r "$rid" --self-contained true \
  -p:InvariantGlobalization=true -p:DebugType=none -p:DebugSymbols=false \
  -v minimal -o "$pub" >/dev/null

# Serve the SPA from {ContentRoot=the binary dir}/wwwroot, exactly as the .deb does.
install -d "$pub/wwwroot"
cp -a "$ui/dist/." "$pub/wwwroot/"

echo "==> run node + a CDP-virtual-authenticator passkey ceremony in a container"
# A persistent host cache so re-runs skip the slow npm + chromium download (the playwright
# browser bundle lands here; node_modules is reused too). First run is slow; later ones fast.
cache="${PDN_E2E_CACHE:-$HOME/.cache/pdn-passkey-e2e}"
mkdir -p "$cache/ms-playwright" "$cache/node_modules"

# Run as root so playwright can apt-install chromium's OS deps (--with-deps). The slim
# image has no curl, so the health/setup probes use node's built-in fetch. The container
# chowns /state + the cache back to the host uid at the end so the trap can clean up.
docker run --rm --shm-size=1g \
  -v "$pub:/app:ro" \
  -v "$root/scripts/passkey-e2e.mjs:/tmp/e2e.mjs:ro" \
  -v "$work:/state" \
  -v "$cache/ms-playwright:/ms-playwright" \
  -v "$cache/node_modules:/work/node_modules" \
  -e PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
  -e PDN_BASE="http://localhost:8080" \
  -e HOST_UID="$host_uid" -e HOST_GID="$host_gid" \
  node:22-bookworm-slim bash -lc '
    set -e
    trap "chown -R \"$HOST_UID:$HOST_GID\" /ms-playwright /work/node_modules 2>/dev/null || true" EXIT

    mkdir -p /work && cd /work && npm init -y >/dev/null 2>&1
    npm i playwright >/tmp/npm.log 2>&1
    npx playwright install --with-deps chromium >/tmp/pw.log 2>&1
    export NODE_PATH=/work/node_modules

    # A writable state dir for the node (config + pdn.db). The published binary lives in
    # the RO /app mount and runs from there (the apphost needs its sibling DLLs); only the
    # config + db are written, and those live under /state via the --config/--db flags.
    # Auth is ON from the first boot. /setup is ALWAYS open (the bootstrap path), so we can
    # create the admin over the API even with the gate enforcing — no restart, no config
    # rewrite (the earlier approach rewrote the whole config through the write seam and a
    # blunt sed flip caught every "enabled: false", which is brittle). The node binds
    # 0.0.0.0:8080 so it is reachable however a name resolves.
    mkdir -p /state/run
    cat > /state/run/packetnet.yaml <<YAML
schemaVersion: 1
identity:
  callsign: M0LTE-1
  alias: LONDON
ports: []
management:
  telnet:
    enabled: false
  http:
    bind: 0.0.0.0
    port: 8080
  auth:
    enabled: true
    webAuthn:
      relyingPartyId: localhost
      relyingPartyName: pdn e2e node
YAML

    # The probes hit 127.0.0.1 explicitly (Node 22 would otherwise resolve "localhost" to
    # ::1 first, where nothing listens, and a no-timeout fetch could hang). Playwright
    # still uses http://localhost:8080 (Chrome resolves localhost → loopback and treats it
    # as a secure context — the whole point of the localhost RP id).
    waitup() {
      node -e "(async()=>{const u=process.argv[1];for(let i=0;i<120;i++){try{const r=await fetch(u,{signal:AbortSignal.timeout(1000)});if(r.ok)process.exit(0);}catch{}await new Promise(s=>setTimeout(s,500));}console.error(\"timed out waiting for \"+u);process.exit(1);})()" "$1"
    }
    killnode() { for d in /proc/[0-9]*; do [ "$(cat "$d/comm" 2>/dev/null)" = packetnet ] && kill -9 "$(basename "$d")" 2>/dev/null || true; done; }

    echo "--> start the node (auth ON; /setup stays open for the bootstrap)"
    # Background directly (no $()-capture — that can block waiting for the job to close the
    # substitution pipe); exec so the subshell IS the node (one pid).
    ( cd /state/run && exec /app/packetnet --config /state/run/packetnet.yaml --db /state/run/pdn.db ) >/tmp/node.log 2>&1 &
    waitup http://127.0.0.1:8080/healthz || { cat /tmp/node.log; exit 1; }

    echo "--> bootstrap the admin (one-shot /setup, always open even with auth on)"
    node -e "(async()=>{const r=await fetch(\"http://127.0.0.1:8080/api/v1/setup\",{method:\"POST\",headers:{\"content-type\":\"application/json\"},body:JSON.stringify({identity:{callsign:\"M0LTE-1\",alias:\"LONDON\"},admin:{username:\"sysop\",password:\"hunter2hunter2\"}})});if(!r.ok){console.error(\"setup failed \"+r.status);process.exit(1);}})()"

    echo "--> drive the passkey ceremony"
    # Run the e2e script FROM /work so its ESM `import "playwright"` resolves against
    # /work/node_modules (ESM ignores NODE_PATH and walks up from the module dir).
    cp /tmp/e2e.mjs /work/e2e.mjs
    ( cd /work && node /work/e2e.mjs )
    rc=$?
    killnode
    exit $rc
  '

echo "==> passkey E2E complete"
