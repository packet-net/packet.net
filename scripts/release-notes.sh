#!/usr/bin/env bash
#
# Generate a GitHub Release body: bullet-point change notes derived from the git
# history since the previous tag in the same series, plus a one-line pointer at
# the install docs.
#
# Why this exists: the release workflows used to hand `gh release create` a fixed
# paragraph of install instructions, so every release read identically and told
# you nothing about what had changed. Install instructions belong in the operating
# guide; a release description should say what moved. This script is the single
# generator for every release series, and it is deterministic from git
# history alone - which is what lets the same script backfill old releases
# (scripts/backfill-release-notes.sh) with exactly the notes they would have got.
#
# Usage:
#   scripts/release-notes.sh --series node --version 0.43.0 [options]
#
#   --series   node | headend             (selects the tag prefix + footer)
#   --version  semver without the tag prefix, e.g. 0.43.0
#   --repo     owner/name for the compare link (default: $GITHUB_REPOSITORY, else
#              derived from the `origin` remote)
#   --prev     override the auto-detected previous tag (use `--prev none` to force
#              "initial release" output)
#   --head     ref to end the range at (default: the series tag if it exists,
#              else HEAD - so a workflow_dispatch run before tagging still works)
#
# Writes markdown to stdout. Needs the full history + tags: a workflow calling
# this MUST checkout with `fetch-depth: 0`.

set -euo pipefail

series=""
version=""
repo="${GITHUB_REPOSITORY:-}"
prev_override=""
head_ref=""

while [ $# -gt 0 ]; do
  case "$1" in
    --series)  series="$2";       shift 2 ;;
    --version) version="$2";      shift 2 ;;
    --repo)    repo="$2";         shift 2 ;;
    --prev)    prev_override="$2"; shift 2 ;;
    --head)    head_ref="$2";     shift 2 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "release-notes.sh: unknown argument: $1" >&2; exit 2 ;;
  esac
done

[ -n "$series" ]  || { echo "release-notes.sh: --series is required" >&2; exit 2; }
[ -n "$version" ] || { echo "release-notes.sh: --version is required" >&2; exit 2; }

case "$series" in
  node)    prefix="node-v" ;;
  headend) prefix="headend-v" ;;
  *) echo "release-notes.sh: unknown series '$series' (node|headend)" >&2; exit 2 ;;
esac

if [ -z "$repo" ]; then
  origin="$(git config --get remote.origin.url || true)"
  repo="$(printf '%s' "$origin" | sed -E 's#^git@[^:]+:##; s#^https?://[^/]+/##; s#\.git$##')"
fi
[ -n "$repo" ] || { echo "release-notes.sh: cannot determine --repo" >&2; exit 2; }

tag="${prefix}${version}"

# End of the range. At tag-push time the tag is checked out; on workflow_dispatch
# it does not exist yet, so fall back to HEAD.
if [ -z "$head_ref" ]; then
  if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then head_ref="$tag"; else head_ref="HEAD"; fi
fi

# Start of the range: the tag in this series that immediately precedes $version.
# Version-sorted, not date-sorted, so 0.13.9 -> 0.13.10 orders correctly and a
# late patch tag on an old line does not become the base for a newer minor.
if [ -n "$prev_override" ]; then
  prev="$prev_override"
  [ "$prev" = "none" ] && prev=""
else
  prev="$(
    { git tag --list "${prefix}*"; printf '%s\n' "$tag"; } \
      | sort -u -V \
      | awk -v t="$tag" '$0 == t { print p; exit } { p = $0 }'
  )"
  # Guard against a base that is not an ancestor (a tag on an unmerged line):
  # a range git cannot walk would silently produce nothing.
  if [ -n "$prev" ] && ! git merge-base --is-ancestor "$prev" "$head_ref" 2>/dev/null; then
    prev=""
  fi
fi

# Buckets, in the order they are printed. Anything whose `type:` prefix is not a
# recognised conventional-commit type (this repo also uses bare subsystem
# prefixes like `catalog:` / `tune:`) lands in "Changed" rather than being
# demoted to internals - those commits are usually the user-visible ones.
declare -A HEADING=(
  [breaking]="Breaking"
  [feat]="New"
  [fix]="Fixed"
  [perf]="Performance"
  [changed]="Changed"
  [docs]="Documentation"
  [internal]="Internal"
)
ORDER=(breaking feat fix perf changed docs internal)
declare -A BULLETS=()
for b in "${ORDER[@]}"; do BULLETS[$b]=""; done

classify() { # $1 = lowercased type token
  case "$1" in
    feat|feature|features)                          echo feat ;;
    fix|fixes|bugfix|hotfix|revert)                 echo fix ;;
    perf)                                           echo perf ;;
    docs|doc|plan|guide|operating|readme)           echo docs ;;
    ci|build|chore|deps|test|tests|refactor|style|\
    interop|fuzz|docker|packaging|spec|spike|\
    fixture|fixtures|licence|license|tooling)       echo internal ;;
    *)                                              echo changed ;;
  esac
}

count=0
if [ -n "$prev" ]; then
  while IFS= read -r subject; do
    [ -n "$subject" ] || continue
    count=$((count + 1))

    # Drop the plan-check override markers - they are instructions to CI, not news.
    subject="${subject//\[skip-plan\]/}"
    subject="${subject//\[no-plan-update\]/}"
    # Old subjects predate the repo's ASCII-punctuation sweep (#743); fold the
    # typographic dashes/ellipsis back to ASCII so the notes read consistently.
    subject="${subject//$'\xe2\x80\x94'/-}"
    subject="${subject//$'\xe2\x80\x93'/-}"
    subject="${subject//$'\xe2\x80\xa6'/...}"
    subject="${subject//$'\xe2\x86\x92'/->}"
    subject="$(printf '%s' "$subject" | sed -E 's/[[:space:]]+/ /g; s/^ //; s/ $//; s/ \)$/)/')"
    [ -n "$subject" ] || continue

    bucket="changed"
    text="$subject"
    if [[ "$subject" =~ ^([A-Za-z][A-Za-z0-9_-]*)(\(([^\)]*)\))?(!)?:[[:space:]]*(.*)$ ]]; then
      type="${BASH_REMATCH[1],,}"
      scope="${BASH_REMATCH[3]}"
      bang="${BASH_REMATCH[4]}"
      rest="${BASH_REMATCH[5]}"
      bucket="$(classify "$type")"
      # A bare subsystem prefix (`catalog: ...`) is a scope, not a type: keep it
      # visible so the bullet still says which part of the node moved.
      if [ "$bucket" = "changed" ] && [ -z "$scope" ]; then scope="$type"; fi
      [ -n "$bang" ] && bucket="breaking"
      text="${rest^}"
      [ -n "$scope" ] && text="**${scope}:** ${rest^}"
    else
      text="${subject^}"
    fi

    BULLETS[$bucket]+="- ${text}"$'\n'
  done < <(git log --no-merges --pretty=tformat:'%s' "${prev}..${head_ref}")
fi

# ---- render -----------------------------------------------------------------

if [ -z "$prev" ]; then
  echo "First \`${prefix}\` release - there is no earlier tag in this series to compare against."
  echo
elif [ "$count" -eq 0 ]; then
  echo "No code changes since [\`${prev}\`](https://github.com/${repo}/releases/tag/${prev}) - re-cut of the same tree."
  echo
else
  for b in "${ORDER[@]}"; do
    [ -n "${BULLETS[$b]}" ] || continue
    echo "### ${HEADING[$b]}"
    echo
    printf '%s' "${BULLETS[$b]}"
    echo
  done
  plural="s"; [ "$count" -eq 1 ] && plural=""
  echo "**Full changelog:** [\`${prev}...${tag}\`](https://github.com/${repo}/compare/${prev}...${tag}) (${count} commit${plural})"
  echo
fi

footer=".github/release-notes/${series}.md"
root="$(git rev-parse --show-toplevel)"
if [ -f "${root}/${footer}" ]; then
  echo "---"
  echo
  sed "s/__VER__/${version}/g; s#__REPO__#${repo}#g" "${root}/${footer}"
fi
