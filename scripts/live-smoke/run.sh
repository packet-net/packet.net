#!/usr/bin/env bash
# Live smoke: boot a REAL pdn node with auth ON, serve the REAL live-mode SPA off it,
# and drive that SPA with a real chromium. Fails the build on any console error, any
# 4xx/5xx from a request the SPA itself made, or any visible "undefined" / "NaN" /
# "[object Object]".
#
# WHY THIS EXISTS: every other test in this repo either exercises the API without a
# browser (Packet.Node.Tests) or exercises the SPA without a node (vitest against the
# mock data layer). Neither can see a contract break BETWEEN them - the UI review found
# the Ports editor's "Add port" defaults 422ing against the real validator, and a
# dashboard rendering the mock's callsign, both with every unit test green. This leg
# closes that gap: one process boundary, both real.
#
# WHY DOCKER: this dev box (an LXC) denies a host Chrome's network sockets
# (CreatePlatformSocket: Permission denied) and Playwright's chromium doesn't support
# the host OS - so no host browser can reach a local node here. The container has
# working sockets and a Playwright-supported OS, and we run it on --network host so it
# reaches the node on 127.0.0.1 with no port plumbing. The image is the PREBUILT
# playwright image, so chromium is already installed: no `playwright install` at run
# time (that cost ~4 minutes in the review probe).
#
# Usage:  scripts/live-smoke/run.sh
#   PDN_BIN=/path/to/packetnet     point at a publish output instead of bin/Debug
#   LIVE_SMOKE_OUT=/path/to/dir    keep the screenshots + report here (default: a temp dir)
#   LIVE_SMOKE_CACHE=/path/to/dir  where the playwright-core node module is cached
#   PW_IMAGE=...                   override the playwright image tag
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"

PW_IMAGE="${PW_IMAGE:-mcr.microsoft.com/playwright:v1.56.0-noble}"
PW_VERSION="1.56.0"                 # must match the image tag: the client and the baked browser are a pair
PDN_BIN="${PDN_BIN:-$repo/src/Packet.Node/bin/Debug/net10.0/packetnet}"
pw_cache="${LIVE_SMOKE_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/pdn-live-smoke}"

for tool in node npm curl docker python3; do
  command -v "$tool" >/dev/null 2>&1 || { echo "FAILED: $tool is required by the live smoke and is not on PATH"; exit 2; }
done

# The smoke's own identity + credentials. The callsign is deliberately NOT the mock
# fixture's (GB7RDG): the dashboard step asserts the panel shows THIS node.
CALLSIGN="M0SMK-7"
ALIAS="SMOKE"
ADMIN_USER="smokeop"
ADMIN_PASS="live-smoke-pass-123"
BOOT_PORT_ID="smk-a"          # the port the config file boots with
NEW_PORT_ID="smk-b"           # the port the SPA's "Add port" panel creates

# ---------------------------------------------------------------- scratch + cleanup
scratch="$(mktemp -d "${TMPDIR:-/tmp}/pdn-live-smoke.XXXXXXXX")"
out="${LIVE_SMOKE_OUT:-$scratch/out}"
mkdir -p "$out"
node_log="$scratch/node.log"
sink_log="$scratch/sink.log"
sink_pid=""
node_pid=""

cleanup() {
  local rc=$?
  set +e
  # Kill BY PID, never by pattern: `pkill -f packetnet` would match this very script's
  # own command line (and any other agent's node on this box) and take out the wrong
  # processes. The PIDs are recorded at spawn for exactly this reason.
  if [[ -n "$node_pid" ]]; then kill "$node_pid" 2>/dev/null; wait "$node_pid" 2>/dev/null; fi
  if [[ -n "$sink_pid" ]]; then kill "$sink_pid" 2>/dev/null; wait "$sink_pid" 2>/dev/null; fi
  if [[ "$out" != "$scratch/out" ]]; then
    # An explicit output dir (what CI passes): keep the evidence, drop the bulk.
    rm -rf "$scratch/app" "$scratch/pdn.db" "$scratch/pdn.db-wal" "$scratch/pdn.db-shm"
  elif [[ "$rc" -ne 0 ]]; then
    # A local failure with no output dir set: keep everything, and say where it is -
    # the screenshots are the whole point of a failed UI run.
    rm -rf "$scratch/app"
    echo "evidence kept in $scratch (screenshots in $out)"
  else
    rm -rf "$scratch"
  fi
  exit "$rc"
}
trap cleanup EXIT

# ---------------------------------------------------------------- free ports
# Ask the kernel for a port rather than hardcoding one: this box runs several agents and
# a CI runner pool, so a fixed 18080 is a coin toss. bind(0) then close, and use the
# number the kernel handed back - a small race window we accept, over a certain clash.
free_port() {
  python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
}

http_port="$(free_port)"
telnet_port="$(free_port)"
sink_a="$(free_port)"
sink_b="$(free_port)"
base="http://127.0.0.1:$http_port"

echo "==> scratch $scratch"
echo "==> ports http=$http_port telnet=$telnet_port sink=$sink_a,$sink_b"

# ---------------------------------------------------------------- build the node
if [[ ! -x "$PDN_BIN" ]]; then
  echo "==> dotnet build Packet.Node (Debug)"
  dotnet build "$repo/src/Packet.Node/Packet.Node.csproj" -c Debug --nologo -v minimal
fi
[[ -x "$PDN_BIN" ]] || { echo "FAILED: no node binary at $PDN_BIN"; exit 1; }

# Run from a COPY of the build output, not from the tree. The node's ContentRoot is its
# own directory, so wwwroot has to sit beside the binary - and staging a wwwroot into
# (plus holding a running process against) src/Packet.Node/bin would collide with any
# concurrent `dotnet build` in this worktree. A ~50 MB copy buys full isolation.
app="$scratch/app"
echo "==> staging the node into $app"
cp -a "$(dirname "$PDN_BIN")" "$app"
app_bin="$app/$(basename "$PDN_BIN")"
# The staged binary is a framework-dependent apphost: it finds the runtime via DOTNET_ROOT or the
# machine-wide install locations. A runner whose dotnet lives somewhere else (a user-local SDK)
# builds the app fine but the apphost then fails with "You must install .NET". Point it at the
# very dotnet that built it unless the environment already says.
if [[ -z "${DOTNET_ROOT:-}" ]] && command -v dotnet >/dev/null 2>&1; then
  export DOTNET_ROOT="$(dirname "$(readlink -f "$(command -v dotnet)")")"
fi

# ---------------------------------------------------------------- build the SPA (live)
ui="$repo/web/packetnet-ui"
if [[ ! -d "$ui/node_modules" ]]; then
  echo "==> npm ci"
  ( cd "$ui" && npm ci )
fi
echo "==> vite build (VITE_API_MODE=live)"
( cd "$ui" && VITE_API_MODE=live npm run build )
[[ -f "$ui/dist/index.html" ]] || { echo "FAILED: vite produced no dist/index.html"; exit 1; }

# BEFORE the node starts: ASP.NET resolves the web root once at host build, so a node
# that starts without a wwwroot gets a NullFileProvider for its whole life and serves
# 404 for every SPA asset. Stage first, start second.
rm -rf "$app/wwwroot"
cp -a "$ui/dist" "$app/wwwroot"

# ---------------------------------------------------------------- config
# Two kiss-tcp ports' worth of sink, because the transport-endpoint uniqueness rule
# rejects two ports pointing at the same host:port - and the SPA's "Add port" step
# creates a second, real port.
cat > "$scratch/packetnet.yaml" <<YAML
schemaVersion: 2
identity:
  # Deliberately NOT the callsign the setup step posts: the dashboard assertion proves
  # the panel renders what setup wrote, through the real config write seam.
  callsign: M0SMK-1
  alias: BOOT
ports:
  - id: $BOOT_PORT_ID
    enabled: true
    transport:
      kind: kiss-tcp
      host: 127.0.0.1
      port: $sink_a
    # A short T1 and a tiny N2 so the Sessions step's dial to a station that isn't
    # there fails in ~2 s instead of sitting on the 30 s API dial ceiling. It also
    # gives the ports round-trip step a port with a populated optional block to
    # prove the editor does not drop.
    ax25:
      t1Ms: 500
      n2: 2
    kiss:
      txDelay: 20
services:
  banner: "Welcome to {node} ({call})"
  prompt: "{call}> "
management:
  telnet:
    enabled: true
    bind: 127.0.0.1
    port: $telnet_port
  http:
    bind: 127.0.0.1
    port: $http_port
  auth:
    enabled: true
YAML

# ---------------------------------------------------------------- start the sink
echo "==> starting tcp sink"
python3 "$here/tcp-sink.py" "$sink_a" "$sink_b" > "$sink_log" 2>&1 &
sink_pid=$!

# ---------------------------------------------------------------- start the node
echo "==> starting the node (auth ON)"
( cd "$scratch" && "$app_bin" --config "$scratch/packetnet.yaml" --db "$scratch/pdn.db" ) \
  > "$node_log" 2>&1 &
node_pid=$!

dump_node_log() {
  echo "---- last 100 lines of $node_log ----"
  tail -n 100 "$node_log" 2>/dev/null || echo "(no node log)"
  echo "-------------------------------------"
}

echo "==> waiting for /healthz"
healthy=0
for _ in $(seq 1 120); do
  if ! kill -0 "$node_pid" 2>/dev/null; then
    echo "FAILED: the node exited before it answered /healthz"
    dump_node_log
    exit 1
  fi
  code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 2 "$base/healthz" || true)"
  if [[ "$code" == "200" ]]; then healthy=1; break; fi
  sleep 0.5
done
if [[ "$healthy" != "1" ]]; then
  echo "FAILED: /healthz never answered 200 on $base within 60s"
  dump_node_log
  exit 1
fi

# ---------------------------------------------------------------- setup + login
# POST /api/v1/setup is always open while zero users exist (there is no setup token in
# this codebase); it applies the identity through the same write seam the editor uses,
# then creates the admin. Shapes taken from tests/Packet.Node.Tests/Integration/AuthApiTests.cs.
echo "==> first-run setup"
# JSON bodies via node (already required for the SPA build) rather than jq, which the
# self-hosted runners do not have.
setup_body="$(node -e 'process.stdout.write(JSON.stringify({identity:{callsign:process.argv[1], alias:process.argv[2]}, admin:{username:process.argv[3], password:process.argv[4]}}))' \
  "$CALLSIGN" "$ALIAS" "$ADMIN_USER" "$ADMIN_PASS")"
setup_out="$scratch/setup.json"
setup_code="$(curl -s -o "$setup_out" -w '%{http_code}' -X POST "$base/api/v1/setup" \
  -H 'content-type: application/json' -d "$setup_body")"
if [[ "$setup_code" != "200" ]]; then
  echo "FAILED: POST /api/v1/setup returned $setup_code"
  cat "$setup_out"; echo
  dump_node_log
  exit 1
fi

echo "==> login"
login_out="$scratch/login.json"
login_code="$(curl -s -o "$login_out" -w '%{http_code}' -X POST "$base/api/v1/auth/login" \
  -H 'content-type: application/json' \
  -d "$(node -e 'process.stdout.write(JSON.stringify({username:process.argv[1], password:process.argv[2]}))' "$ADMIN_USER" "$ADMIN_PASS")")"
if [[ "$login_code" != "200" ]]; then
  echo "FAILED: POST /api/v1/auth/login returned $login_code"
  cat "$login_out"; echo
  dump_node_log
  exit 1
fi
token="$(node -e 'const j=JSON.parse(require("fs").readFileSync(process.argv[1],"utf8")); process.stdout.write(j.token ?? "")' "$login_out")"
[[ -n "$token" && "$token" != "null" ]] || { echo "FAILED: no token in the login response"; exit 1; }

# ---------------------------------------------------------------- playwright client
# The prebuilt image bakes the BROWSERS (/ms-playwright) but not the npm client, so the
# driver still needs `playwright-core` on disk. That is a ~3 MB pure-JS package with no
# postinstall - nothing like the ~4-minute `playwright install --with-deps chromium`
# the review probe paid on a bare node image. Cached between runs so a local iteration
# does not hit the registry at all. Mounted at /node_modules: ESM resolution from
# /smoke/smoke.mjs walks up to the root, so a bare `playwright-core` import just works.
if [[ ! -d "$pw_cache/node_modules/playwright-core" ]]; then
  echo "==> caching playwright-core@$PW_VERSION in $pw_cache"
  mkdir -p "$pw_cache"
  docker run --rm -u "$(id -u):$(id -g)" -e HOME=/tmp -v "$pw_cache:/pw" -w /pw "$PW_IMAGE" \
    npm install --no-save --no-audit --no-fund "playwright-core@$PW_VERSION"
fi
[[ -d "$pw_cache/node_modules/playwright-core" ]] || { echo "FAILED: playwright-core is not in $pw_cache"; exit 1; }

# ---------------------------------------------------------------- drive it
echo "==> driving the SPA in $PW_IMAGE"
set +e
docker run --rm --network host --shm-size=1g \
  -u "$(id -u):$(id -g)" \
  -e HOME=/tmp \
  -e BASE="$base" \
  -e OUT=/out \
  -e SMOKE_USER="$ADMIN_USER" \
  -e SMOKE_PASS="$ADMIN_PASS" \
  -e SMOKE_CALLSIGN="$CALLSIGN" \
  -e SMOKE_TOKEN="$token" \
  -e BOOT_PORT_ID="$BOOT_PORT_ID" \
  -e NEW_PORT_ID="$NEW_PORT_ID" \
  -e NEW_PORT_HOST=127.0.0.1 \
  -e NEW_PORT_TCP="$sink_b" \
  -v "$here:/smoke:ro" \
  -v "$pw_cache/node_modules:/node_modules:ro" \
  -v "$out:/out" \
  "$PW_IMAGE" node /smoke/smoke.mjs
driver_rc=$?
set -e

# The node log is evidence for anything the driver saw, so print it on failure.
if [[ "$driver_rc" -ne 0 ]]; then
  dump_node_log
  echo "LIVE SMOKE FAILED (driver exit $driver_rc). Artifacts in $out"
  # Keep the node log next to the screenshots so CI can upload it.
  cp -f "$node_log" "$out/node.log" 2>/dev/null || true
  exit "$driver_rc"
fi

cp -f "$node_log" "$out/node.log" 2>/dev/null || true
echo "LIVE SMOKE PASSED"
