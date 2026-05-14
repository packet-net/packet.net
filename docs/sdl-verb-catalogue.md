# SDL action-verb catalogue

`spec-sdl/actions.yaml` is the canonical vocabulary for AX.25 SDL action
verbs — the verbs that appear in transition `path:` entries as
`{ action: ..., kind: ... }` and end up driving the dispatcher at
runtime.

## Why this file exists

The AX.25 SDL figures sometimes spell the same semantic verb differently
on different pages — e.g. figc4.2 draws `DM F=1`, figc4.3 draws
`DM F = 1`, figc4.6 draws `DM (F = 1)`. All three labels mean the same
thing: emit a DM response frame with the F bit set.

Two competing constraints:

- **Trust-the-figure** (CLAUDE.md hard rule): the YAML transcription
  must record what the figure draws. We don't "fix" labels.
- **Runtime dispatcher needs one spelling per semantic verb**: the
  dispatcher's switch can't have three arms doing the same thing.

`actions.yaml` is the bridge. It declares a canonical name for each
verb and lists the alternate figure-verbatim spellings as aliases.
The codegen reads it, substitutes aliases for the canonical name, and
emits the canonical into the generated `.g.cs` files. The dispatcher
only ever sees canonical verbs.

## File shape

```yaml
# Grouped by SDL action kind (the figc1.1 shape class). The kind also
# serves as a validation hook — if a YAML page draws the verb with a
# different kind than the catalog declares, codegen flags it.

signal_lower:
  - name: "DM (F = 1)"        # canonical name — what .g.cs emits
    aliases:                  # other spellings observed in figures
      - "DM F=1"              # figc4.2 spelling
      - "DM F = 1"            # figc4.3 spelling

subroutine:
  - name: Establish_Data_Link
    # No aliases — verb only has one spelling so far.
```

Groups are `signal_upper`, `signal_lower`, `processing`, `subroutine`,
`internal_out`. Same five values as the `kind:` field on a transition
step.

## Soft passthrough

If `actions.yaml` is absent the codegen runs with an empty catalog and
every verb passes through verbatim — i.e. exactly the pre-catalog
behaviour. This is deliberate: the catalog is incremental. Add entries
only when a verb is drawn with two or more spellings, or when you want
to force a particular canonical spelling for downstream tools.

When the catalog *is* present, a YAML verb that matches a declared
alias is rewritten to the canonical name during validation. A YAML verb
that matches neither a canonical name nor any alias is passed through
unchanged (still soft). This lets us populate the catalog gradually.

## What triggers an error

Two conditions are hard errors:

1. **Kind mismatch.** A YAML draws an aliased verb with a different
   `kind:` than the catalog declares. Example: catalog says
   `Establish_Data_Link` is `subroutine`; a YAML draws
   `Establish Data Link` with `kind: processing`. Likely a transcription
   slip on the YAML side.

2. **Malformed catalog.** Duplicate canonical names, alias claimed by
   two different canonicals, empty alias string, unknown kind group.

## Workflow when you spot a cross-page variant

1. Confirm both spellings exist in the figures (read the graphml's
   `<y:NodeLabel>` text — that's what's drawn).
2. Decide on the canonical name. Convention so far: prefer the most
   explicit spelling (`DM (F = 1)` over `DM F=1`), prefer snake_case
   for subroutines (`set_version_2_0` over `Set Version 2.0`).
3. Add an entry under the appropriate `kind:` group with the
   canonical `name:` and the variants as `aliases:`.
4. Re-run codegen — the `.g.cs` and generated test files will show the
   canonical verb everywhere.

When in doubt, ask. Different canonical choices have downstream
implications for the dispatcher's switch table; once a verb is
canonicalised it's load-bearing.

## Long-term direction

Today the catalog is sparsely populated — just the obvious
cross-page duplicates. Long-term it should declare every verb the
transcriptions use, so the dispatcher has a fully bounded vocabulary
and the codegen can fail-fast on any verb that isn't declared. That's
the "hard mode" version of OQ-008 (`docs/plan.md` §15).

Get there incrementally:

- PR-by-PR, populate clusters and add aliases as they're discovered.
- When the catalog covers everything used in the YAMLs, flip a
  `strict:` flag on the catalog so unknown verbs become errors.
- At that point the dispatcher's switch arms and the catalog become
  the joint source of truth for the action-verb vocabulary.
