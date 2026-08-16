#!/usr/bin/env bash
# check-ci-test-matrix.sh - fail if ci.yml's hand-listed test matrix has drifted from tests/.
#
#   scripts/check-ci-test-matrix.sh
#
# ci.yml runs one matrix leg per test project, by name. Nothing tied that list to the
# directory it mirrors, so `tests/Packet.Rig.Tests`, `tests/Packet.Rig.Flrig.Tests` and
# `tests/Packet.Rig.Hamlib.Tests` sat there for a month never being run by any workflow -
# 103 tests with no CI signal (packet.net#699 / C017), the second time the same omission
# happened. This is the tie: every tests/*/*.csproj must have a leg, and every leg must have
# a project. Run it locally before touching the matrix; ci.yml runs it on every push.
#
# The per-project matrix is deliberate (parallelism across the self-hosted runners), so the
# fix for drift is to add the leg, not to collapse the matrix into one solution-wide run.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
workflow="$root/.github/workflows/ci.yml"
[ -f "$workflow" ] || { echo "no such workflow: $workflow" >&2; exit 2; }

# Test projects that are deliberately NOT in the ci matrix. Each needs a reason here.
#   Packet.Interop.Tests - every test in it carries Category=Interop or =HardwareLoop, both of
#     which ci.yml's filter excludes, so the leg executed zero tests (C113). interop.yml owns
#     that suite and Release-builds the whole solution, so no coverage is lost.
excluded=(Packet.Interop.Tests)

# The matrix list: the items under the `project:` key, up to the first line that is not a
# plain list entry.
mapfile -t matrix < <(awk '
    /^[[:space:]]*project:[[:space:]]*$/ { inlist = 1; next }
    inlist {
        if ($0 ~ /^[[:space:]]*-[[:space:]]+[A-Za-z0-9._]+[[:space:]]*$/) {
            sub(/^[[:space:]]*-[[:space:]]+/, "")
            sub(/[[:space:]]+$/, "")
            print
            next
        }
        exit
    }
' "$workflow")

if [ "${#matrix[@]}" -lt 10 ]; then
    echo "could not parse the ci.yml test matrix (found ${#matrix[@]} entries) - has its shape changed?" >&2
    exit 2
fi

# The test projects on disk: tests/<name>/<name>.csproj.
on_disk=()
for csproj in "$root"/tests/*/*.csproj; do
    [ -f "$csproj" ] || continue
    name="$(basename "$csproj" .csproj)"
    dir="$(basename "$(dirname "$csproj")")"
    # Only the one-project-per-directory convention; anything else is reported, not guessed at.
    [ "$name" = "$dir" ] || { echo "unexpected layout: $csproj (project name != directory)" >&2; exit 2; }
    on_disk+=("$name")
done

contains() {
    local needle="$1"; shift
    local item
    for item in "$@"; do
        [ "$item" = "$needle" ] && return 0
    done
    return 1
}

rc=0

for name in "${on_disk[@]}"; do
    contains "$name" "${excluded[@]}" && continue
    if ! contains "$name" "${matrix[@]}"; then
        echo "MISSING from the ci.yml test matrix: $name (tests/$name/$name.csproj exists but no workflow runs it)"
        rc=1
    fi
done

for name in "${matrix[@]}"; do
    if ! contains "$name" "${on_disk[@]}"; then
        echo "STALE ci.yml test matrix entry: $name (no tests/$name/$name.csproj)"
        rc=1
    fi
done

for name in "${excluded[@]}"; do
    if contains "$name" "${matrix[@]}"; then
        echo "EXCLUDED project is in the ci.yml matrix: $name (see the exclusion list in $0)"
        rc=1
    fi
done

if [ "$rc" -ne 0 ]; then
    if [ -n "${GITHUB_ACTIONS:-}" ]; then
        echo "::error::ci.yml's test matrix has drifted from tests/ - see the lines above."
    fi
    echo "TEST_MATRIX_FAIL"
    exit 1
fi

echo "ok: ${#matrix[@]} matrix legs cover every tests/*/*.csproj (excluding: ${excluded[*]})"
echo "TEST_MATRIX_PASS"
