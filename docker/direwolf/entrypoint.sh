#!/bin/sh
# Launch the Dire Wolf mod-128 interop peer:
#   1. PulseAudio with a null sink = the shared simulated-RF (AFSK1200) channel.
#   2. direwolf-resp — connected-mode engine, AGW server on 8000.
#   3. direwolf-gw   — transparent KISS-TCP modem on 8001 (our side's radio).
#   4. agw_echo.py   — registers the called callsign(s), accepts inbound
#                      connects, echoes connected data.
#
# All four run as the unprivileged `dw` user. See docker/direwolf/Dockerfile
# for why a two-direwolf audio loopback is the architecture.
set -eu

export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/1000}"
export HOME="${HOME:-/home/dw}"

log() { echo "[entrypoint] $*"; }

# ─── PulseAudio: one null sink, set as default sink + source ────────
log "starting PulseAudio (null sink 'rf')"
pulseaudio \
    --start \
    --exit-idle-time=-1 \
    -n \
    -L "module-native-protocol-unix" \
    -L "module-null-sink sink_name=rf" \
    -L "module-always-sink" \
    >/tmp/pulseaudio.log 2>&1

# Wait for the server to accept clients, then pin defaults.
for _ in $(seq 1 20); do
    if pactl info >/dev/null 2>&1; then break; fi
    sleep 0.5
done
pactl set-default-sink rf
pactl set-default-source rf.monitor
log "PulseAudio ready: $(pactl list short sinks | tr '\n' ' ')"

# ─── Dire Wolf instances ────────────────────────────────────────────
# -t 0 disables ANSI colour so the logs are readable in `docker logs`.
log "starting direwolf-resp (connected-mode engine, AGW 8000)"
direwolf -t 0 -c /etc/direwolf-resp.conf >/tmp/direwolf-resp.log 2>&1 &
RESP_PID=$!

log "starting direwolf-gw (KISS modem, KISS 8001)"
direwolf -t 0 -c /etc/direwolf-gw.conf >/tmp/direwolf-gw.log 2>&1 &
GW_PID=$!

# Wait until the responder's AGW server is listening before the echo app dials.
for _ in $(seq 1 30); do
    if grep -q "Ready to accept AGW client" /tmp/direwolf-resp.log 2>/dev/null; then
        break
    fi
    sleep 0.5
done
log "direwolf-resp AGW server up"

# ─── AGW echo helper ────────────────────────────────────────────────
log "starting agw_echo.py (register=${AGW_REGISTER:-N0RESP})"
python3 /usr/local/bin/agw_echo.py >/tmp/agw-echo.log 2>&1 &
ECHO_PID=$!

# ─── Supervise ──────────────────────────────────────────────────────
# Stream each component's log to stdout so `docker logs`/compose shows
# everything in one place, and exit if any core process dies.
tail -n +1 -F /tmp/direwolf-resp.log /tmp/direwolf-gw.log /tmp/agw-echo.log &
TAIL_PID=$!

term() {
    log "shutting down"
    kill "$RESP_PID" "$GW_PID" "$ECHO_PID" "$TAIL_PID" 2>/dev/null || true
    exit 0
}
trap term TERM INT

while true; do
    for pid in "$RESP_PID" "$GW_PID" "$ECHO_PID"; do
        if ! kill -0 "$pid" 2>/dev/null; then
            log "component pid $pid exited; tearing down"
            term
        fi
    done
    sleep 2
done
