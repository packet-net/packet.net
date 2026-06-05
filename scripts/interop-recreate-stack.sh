#!/usr/bin/env bash
#
# Recreate the interop docker stack from a clean slate: tear everything down
# (containers + named volumes + orphans) and bring it back up, blocking until
# every service is healthy.
#
# Why a *clean* recreate between scenario groups: the stack is a single shared
# LinBPQ daemon plus one net-sim half-duplex channel. Scenarios accumulate
# LinBPQ-side state (learned NODES/routes, open AX.25 sessions) and leave the
# RF channel warm — so a heavyweight group (the NET/ROM L3/L4 scenarios) run
# before a timing-sensitive AX.25 group contaminates it and the AX.25 timing
# tests flake (see docs/plan.md §7.1). `interop.yml` calls this between groups
# so each group starts from a pristine LinBPQ + a quiescent channel. This is
# the interop analog of ci.yml's max-parallel cap for the unit-test contention.
#
# Mirrors the workflow's original `down -v --remove-orphans` + `up -d --wait`
# (same COMPOSE_PROJECT_NAME so a previous run's resources are matched and
# removed). `down` failures are tolerated (nothing to clean up on a fresh
# runner is not an error); `up --wait` failures are fatal.
set -euo pipefail

COMPOSE_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/docker/compose.interop.yml"
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-packetnet-interop}"

echo "interop-recreate-stack: tearing down (clean slate)…"
docker compose -f "$COMPOSE_FILE" down -v --remove-orphans || true

echo "interop-recreate-stack: bringing up + waiting for healthy…"
docker compose -f "$COMPOSE_FILE" up -d --wait

echo "interop-recreate-stack: stack healthy."
