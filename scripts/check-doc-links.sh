#!/usr/bin/env bash
# Fail if a markdown link in the living docs points at a file that is not there.
#
# Why: the 2026-05-17 repo split and the config-in-DB move left a trail of links to
# files that had moved to a sibling repo or been deleted, and nothing noticed for
# months (review item C107). Relative links are cheap to check, so check them.
#
# Scope, and the one deliberate exemption: docs/plan.md is checked only up to its
# §17 amendment log. The log is a RECORD of what was true when each entry was
# written, and an entry that cites a path which has since moved is not a broken
# doc, it is history. Everything above §17 is the living document and must resolve.
#
# Only relative links are checked. http(s):, mailto: and bare #anchors are not.
set -euo pipefail

cd "$(dirname "$0")/.."

fail=0
checked=0

# Files whose links are not ours to keep current: the vendored spec markdown (a
# verbatim third-party document, with its own image tree upstream) and the design
# handoff bundle (a delivered artefact, frozen).
skip_re='^(ax\.25\.2\.2\.4_Oct_25\.md|design_handoff_pdn_control_panel/)'

while IFS= read -r file; do
  [[ "$file" =~ $skip_re ]] && continue

  body=$(cat -- "$file")
  if [[ "$file" == "docs/plan.md" ]]; then
    body=$(awk '/^## 17[.] Amendment log/{exit} {print}' "$file")
  fi

  dir=$(dirname -- "$file")

  # Markdown inline links: ](target). Reference definitions ([id]: target) are not
  # used by these docs, so inline is the whole surface.
  while IFS= read -r target; do
    [[ -z "$target" ]] && continue
    case "$target" in
      http://*|https://*|mailto:*|'#'*|'<'*) continue ;;
      # An absolute path is a dev-box filesystem reference (the reference shelf
      # cites local clones), not a repo file. Not ours to resolve.
      /*) continue ;;
    esac
    # Drop any anchor or query, and percent-decode a space.
    path=${target%%#*}
    path=${path%%\?*}
    path=${path//%20/ }
    [[ -z "$path" ]] && continue

    checked=$((checked + 1))
    resolved="$dir/$path"
    if [[ ! -e "$resolved" ]]; then
      echo "BROKEN  $file -> $target" >&2
      fail=1
    fi
  done < <(printf '%s\n' "$body" | grep -oP '\]\(\K[^)\s]+' || true)
done < <(git ls-files '*.md')

if [[ "$fail" -ne 0 ]]; then
  echo >&2
  echo "Broken relative links above. Fix the link, or move the claim into docs/plan.md §17" >&2
  echo "if it is a historical statement rather than a live pointer." >&2
  exit 1
fi

echo "doc links ok ($checked relative links resolve)"
