#!/usr/bin/env bash
# check-ascii-log-output.sh - fail if an operator-facing string in src/ carries a non-ASCII
# character.
#
#   scripts/check-ascii-log-output.sh
#
# Anything a daemon prints comes back as <E2><80><94> in `journalctl`, whose pager runs under a
# C locale on a stock Debian box - so an em dash or a U+2192 arrow in a log template is noise in
# the one place an operator reads it. WP8's C066 swept the node projects by hand and nothing
# held the line: three more had appeared by the GB7RDG upgrade rehearsal (#738 item 3), one of
# them on the config-schema migration line every upgrade prints.
#
# What is checked, in src/ only (tests, comments and docs are free to use whatever they like):
#   - LoggerMessage templates: any line carrying `Message = "`, which is how every
#     [LoggerMessage(...)] in this repo spells its template (one-line or attribute-continuation).
#   - Console output: any line calling Console.Write / Console.WriteLine / Console.Error.* /
#     Console.Out.* (the CLI verbs' operator output).
#
# Not checked: multi-line interpolated strings built up over several lines, and exception
# messages. This is a cheap line-based tripwire for the two surfaces that reach journalctl and a
# terminal, not a full lexer - if it ever needs to be exact, parse with Roslyn instead of
# widening the grep until it false-positives.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"

# The two operator-facing surfaces, as one alternation of line patterns.
patterns='Message = "|Console\.(Write|WriteLine|Error\.|Out\.)'

# grep -P '[^\x00-\x7F]' is the non-ASCII test; the first grep narrows to the lines that carry
# operator-facing text. --include keeps it to C# sources under src/.
hits="$(grep -rnE --include='*.cs' "$patterns" src/ | grep -P '[^\x00-\x7F]' || true)"

if [ -n "$hits" ]; then
    echo "::error::non-ASCII characters in operator-facing output (they render as <E2><80><94> in journalctl)"
    echo "$hits"
    echo
    echo "Use plain ASCII: '->' not an arrow, '-' or ';' not an em dash. Comments may keep theirs."
    exit 1
fi

echo "ok: every LoggerMessage template and Console string in src/ is plain ASCII"
