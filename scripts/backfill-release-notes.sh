#!/usr/bin/env bash
#
# Rewrite the body of every existing GitHub Release with the notes
# scripts/release-notes.sh generates for that tag.
#
# Why: releases cut before the change-notes work all carried the same fixed
# paragraph of install instructions, so the release list said nothing about what
# any given version changed. The generator is deterministic from git history, so
# an old release can be given exactly the notes it would have got had the
# workflow always worked this way.
#
# Every body it replaces is saved to <backup-dir>/<tag>.md first, so the sweep is
# reversible: `gh release edit <tag> --notes-file <backup-dir>/<tag>.md`.
#
# Usage:
#   scripts/backfill-release-notes.sh [--dry-run] [--tag TAG]... [--backup-dir DIR]
#
#   --dry-run      print the generated notes, change nothing
#   --tag          restrict to one tag (repeatable); default is every node-v* and
#                  headend-v* release the repo has
#   --backup-dir   where the old bodies go (default: .release-notes-backup, which
#                  is untracked; delete it once you are happy)
#
# Needs `gh` authenticated with write access to the repo, and a full clone
# (history + tags) for the generator.

set -euo pipefail

dry_run=0
backup_dir=".release-notes-backup"
tags=()

while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run)    dry_run=1; shift ;;
    --tag)        tags+=("$2"); shift 2 ;;
    --backup-dir) backup_dir="$2"; shift 2 ;;
    -h|--help)    sed -n '2,26p' "$0"; exit 0 ;;
    *) echo "backfill-release-notes.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
done

root="$(git rev-parse --show-toplevel)"
gen="${root}/scripts/release-notes.sh"
[ -x "$gen" ] || { echo "backfill: $gen not found or not executable" >&2; exit 2; }

repo="${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner -q .nameWithOwner)}"

if [ "${#tags[@]}" -eq 0 ]; then
  mapfile -t tags < <(gh release list --repo "$repo" --limit 500 --json tagName -q '.[].tagName')
fi

[ "$dry_run" -eq 1 ] || mkdir -p "$backup_dir"

ok=0; skipped=0; failed=0
for tag in "${tags[@]}"; do
  case "$tag" in
    node-v*)    series=node;    version="${tag#node-v}" ;;
    headend-v*) series=headend; version="${tag#headend-v}" ;;
    *) echo "skip  $tag (not a release series this generator covers)"; skipped=$((skipped + 1)); continue ;;
  esac

  notes="$("$gen" --series "$series" --version "$version" --repo "$repo")" || {
    echo "FAIL  $tag (generator)" >&2; failed=$((failed + 1)); continue
  }

  if [ "$dry_run" -eq 1 ]; then
    printf '===== %s =====\n%s\n\n' "$tag" "$notes"
    ok=$((ok + 1))
    continue
  fi

  gh release view "$tag" --repo "$repo" --json body -q .body > "${backup_dir}/${tag}.md" || {
    echo "FAIL  $tag (backup)" >&2; failed=$((failed + 1)); continue
  }

  if printf '%s\n' "$notes" | gh release edit "$tag" --repo "$repo" --notes-file - >/dev/null; then
    echo "done  $tag"
    ok=$((ok + 1))
  else
    echo "FAIL  $tag (edit)" >&2
    failed=$((failed + 1))
  fi
done

echo "backfill: ${ok} ok, ${skipped} skipped, ${failed} failed"
[ "$failed" -eq 0 ]
