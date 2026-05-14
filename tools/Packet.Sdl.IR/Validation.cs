namespace Packet.Sdl.IR;

/// <summary>
/// Structural validation + lints for loaded YAML pages. Runs before
/// <see cref="Resolver"/>; emitter backends never need to inspect raw
/// YAML models. All errors are accumulated into the caller-supplied list
/// so a single codegen run surfaces every transcription bug at once.
/// </summary>
public static class Validation
{
    private static readonly HashSet<string> ValidActionKinds = new(
        new[] { "signal_upper", "signal_lower", "processing", "subroutine", "internal_out" },
        StringComparer.Ordinal);

    private static readonly char[] GuardTokenSeparators = { ' ', '\t' };

    // ─── Catalogue normalisation ──────────────────────────────────────

    /// <summary>
    /// Walks a page's transitions and substitutes every alias-spelling
    /// action verb with its canonical name. No-op when the catalog is
    /// empty (passthrough mode — no actions.yaml present). Records errors
    /// when a canonical verb is drawn with a different <c>kind:</c> than
    /// the catalog declared.
    /// </summary>
    public static void NormaliseActionVerbs(SdlPage page, ActionCatalog catalog, List<string> errors)
    {
        if (catalog.CanonicalLookup.Count == 0) return;
        foreach (var t in page.Transitions)
        {
            if (t.Path is null) continue;
            NormalisePathSteps(page.SourcePath, t.Id, "path", t.Path, catalog, errors);
        }
    }

    /// <summary>Alias-normalisation for subroutine pages. Same semantics as <see cref="NormaliseActionVerbs"/>.</summary>
    public static void NormaliseSubroutineActionVerbs(SubroutinePage page, ActionCatalog catalog, List<string> errors)
    {
        if (catalog.CanonicalLookup.Count == 0) return;
        foreach (var sub in page.Subroutines)
        {
            foreach (var p in sub.Paths)
            {
                NormalisePathSteps(page.SourcePath, p.Id, "path", p.Path, catalog, errors);
            }
        }
    }

    private static void NormalisePathSteps(
        string loc, string transitionId, string contextLabel,
        List<SdlPathStep> path,
        ActionCatalog catalog,
        List<string> errors)
    {
        for (int i = 0; i < path.Count; i++)
        {
            var step = path[i];
            if (!string.IsNullOrWhiteSpace(step.Action) && catalog.CanonicalLookup.TryGetValue(step.Action!, out var canonical))
            {
                if (catalog.CanonicalKind.TryGetValue(canonical, out var expectedKind)
                    && !string.IsNullOrWhiteSpace(step.Kind)
                    && !string.Equals(step.Kind, expectedKind, StringComparison.Ordinal))
                {
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] action `{step.Action}` (canonical `{canonical}`) " +
                               $"is drawn with kind `{step.Kind}` but spec-sdl/actions.yaml declares it as `{expectedKind}`. " +
                               "Either the YAML's kind is wrong, or the catalog is wrong.");
                }
                // Track alias usage so we can warn about dead aliases at the
                // end of the run. An alias is "used" if at least one YAML
                // verb literally matched it (i.e. step.Action wasn't already
                // the canonical name).
                if (!string.Equals(step.Action, canonical, StringComparison.Ordinal))
                {
                    catalog.SeenAliases.Add(step.Action!);
                }
                step.Action = canonical;
            }
            if (step.Body is not null)
            {
                NormalisePathSteps(loc, transitionId, contextLabel + $"[{i}].body", step.Body, catalog, errors);
            }
        }
    }

    // ─── Page validation ──────────────────────────────────────────────

    /// <summary>
    /// Structural validation for state-machine pages: required fields,
    /// decision well-formedness, transition path step shape, references,
    /// and the decision-branch / guard-overlap lints.
    /// </summary>
    public static void ValidatePage(SdlPage page, HashSet<string> events, List<string> errors)
    {
        var loc = page.SourcePath;
        if (string.IsNullOrWhiteSpace(page.Machine))   errors.Add($"{loc}: missing `machine`");
        if (string.IsNullOrWhiteSpace(page.State))     errors.Add($"{loc}: missing `state`");
        if (page.Source is null)                       errors.Add($"{loc}: missing `source`");
        if (page.Transitions.Count == 0)               errors.Add($"{loc}: at least one transition required");

        var decisionsById = new Dictionary<string, SdlDecision>(StringComparer.Ordinal);
        foreach (var d in page.Decisions)
        {
            if (string.IsNullOrWhiteSpace(d.Id)) { errors.Add($"{loc}: decision missing `id`"); continue; }
            if (!decisionsById.TryAdd(d.Id, d))  errors.Add($"{loc}: duplicate decision id `{d.Id}`");
            if (string.IsNullOrWhiteSpace(d.Question))  errors.Add($"{loc}: decision `{d.Id}` missing `question`");
            if (string.IsNullOrWhiteSpace(d.Predicate)) errors.Add($"{loc}: decision `{d.Id}` missing `predicate`");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in page.Transitions)
        {
            if (string.IsNullOrWhiteSpace(t.Id))   errors.Add($"{loc}: transition missing `id`");
            else if (!ids.Add(t.Id))               errors.Add($"{loc}: duplicate transition id `{t.Id}`");

            if (string.IsNullOrWhiteSpace(t.On))   errors.Add($"{loc}: transition `{t.Id}` missing `on`");
            else if (events.Count > 0 && !events.Contains(t.On))
                errors.Add($"{loc}: transition `{t.Id}` references unknown event `{t.On}`. Add it to /spec-sdl/events.yaml.");

            if (string.IsNullOrWhiteSpace(t.Next)) errors.Add($"{loc}: transition `{t.Id}` missing `next`");

            if (t.Path is null)
            {
                errors.Add($"{loc}: transition `{t.Id}` missing `path`. Use `path: []` for a no-op column (input → state with no intermediate boxes).");
                continue;
            }

            // Empty path is allowed: figc4.1's "All Other Primitives" (from
            // lower layer) is drawn as input → state with no intermediate
            // boxes, and that's a valid SDL pattern.

            ValidatePathSteps(loc, t.Id, "path", t.Path, decisionsById, errors);
        }

        LintDecisionBranchCompleteness(page, decisionsById, errors);
        LintGuardOverlap(page, decisionsById, errors);
        LintReferences(page, errors);
    }

    /// <summary>
    /// Structural validation for subroutine pages. Enforces the same
    /// decision-branch / path-ID rules as state-machine page validation,
    /// adapted for subroutines (no <c>on:</c> event, no <c>next:</c> state).
    /// </summary>
    public static void ValidateSubroutinePage(SubroutinePage page, List<string> errors)
    {
        var loc = page.SourcePath;
        if (string.IsNullOrWhiteSpace(page.Machine)) errors.Add($"{loc}: missing `machine`");
        if (page.Source is null)                     errors.Add($"{loc}: missing `source`");
        if (page.Subroutines.Count == 0)             errors.Add($"{loc}: at least one subroutine required");

        var subNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in page.Subroutines)
        {
            if (string.IsNullOrWhiteSpace(sub.Name)) { errors.Add($"{loc}: subroutine missing `name`"); continue; }
            if (!subNames.Add(sub.Name)) errors.Add($"{loc}: duplicate subroutine name `{sub.Name}`");

            var decisionsById = new Dictionary<string, SdlDecision>(StringComparer.Ordinal);
            foreach (var d in sub.Decisions)
            {
                if (string.IsNullOrWhiteSpace(d.Id)) { errors.Add($"{loc}: {sub.Name}: decision missing `id`"); continue; }
                if (!decisionsById.TryAdd(d.Id, d))  errors.Add($"{loc}: {sub.Name}: duplicate decision id `{d.Id}`");
                if (string.IsNullOrWhiteSpace(d.Question))  errors.Add($"{loc}: {sub.Name}: decision `{d.Id}` missing `question`");
                if (string.IsNullOrWhiteSpace(d.Predicate)) errors.Add($"{loc}: {sub.Name}: decision `{d.Id}` missing `predicate`");
            }

            var pathIds = new HashSet<string>(StringComparer.Ordinal);
            if (sub.Paths.Count == 0) errors.Add($"{loc}: {sub.Name}: at least one path required");
            foreach (var p in sub.Paths)
            {
                if (string.IsNullOrWhiteSpace(p.Id)) { errors.Add($"{loc}: {sub.Name}: path missing `id`"); continue; }
                if (!pathIds.Add(p.Id)) errors.Add($"{loc}: {sub.Name}: duplicate path id `{p.Id}`");

                if (p.Path is null || p.Path.Count == 0)
                {
                    // A decision-free, action-free path is fine (silent return).
                    continue;
                }

                ValidatePathSteps(loc, p.Id, "path", p.Path, decisionsById, errors);
            }
        }
    }

    /// <summary>
    /// Validates a path or loop body. Each step must be exactly one of:
    /// (decision + branch), (action + kind), or (loop_while + body).
    /// </summary>
    private static void ValidatePathSteps(
        string loc, string transitionId, string contextLabel,
        List<SdlPathStep> path,
        Dictionary<string, SdlDecision> decisionsById,
        List<string> errors)
    {
        for (int i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var hasDecision = !string.IsNullOrWhiteSpace(step.Decision);
            var hasAction   = !string.IsNullOrWhiteSpace(step.Action);
            var hasLoop     = !string.IsNullOrWhiteSpace(step.LoopWhile);
            var kindsSet = (hasDecision ? 1 : 0) + (hasAction ? 1 : 0) + (hasLoop ? 1 : 0);
            if (kindsSet != 1)
            {
                errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}]: must specify exactly one of `decision:`, `action:`, or `loop_while:`");
                continue;
            }

            if (hasDecision)
            {
                if (!decisionsById.ContainsKey(step.Decision!))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] references undefined decision `{step.Decision}`. Add it to the page-level decisions[].");
                if (step.Branch is not "Yes" and not "No")
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] branch must be 'Yes' or 'No' (was `{step.Branch}`)");
            }
            else if (hasAction)
            {
                if (string.IsNullOrWhiteSpace(step.Kind))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] action `{step.Action}` missing `kind:` (one of signal_upper, signal_lower, processing, subroutine, internal_out)");
                else if (!ValidActionKinds.Contains(step.Kind))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] action `{step.Action}` has unknown kind `{step.Kind}`. Valid: {string.Join(", ", ValidActionKinds)}.");
            }
            else // hasLoop
            {
                if (!decisionsById.ContainsKey(step.LoopWhile!))
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] loop_while references undefined decision `{step.LoopWhile}`. Add it to the page-level decisions[].");
                if (step.Body is null || step.Body.Count == 0)
                {
                    errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}] loop_while missing or empty `body:`. Loop bodies must have at least one step.");
                }
                else
                {
                    // Restrict body to action steps only — nested decisions
                    // and nested loops require richer runtime semantics that
                    // we'd rather face when a real figure needs them.
                    for (int j = 0; j < step.Body.Count; j++)
                    {
                        var b = step.Body[j];
                        if (string.IsNullOrWhiteSpace(b.Action))
                        {
                            errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}].body[{j}] must be an action step. Nested decisions and loops inside a loop body are not supported by the current codegen; refactor as a subroutine if needed.");
                        }
                        else if (string.IsNullOrWhiteSpace(b.Kind) || !ValidActionKinds.Contains(b.Kind))
                        {
                            errors.Add($"{loc}: transition `{transitionId}` {contextLabel}[{i}].body[{j}] action `{b.Action}` has invalid or missing `kind:`.");
                        }
                    }
                }
            }
        }
    }

    // ─── Reference lints ──────────────────────────────────────────────

    /// <summary>
    /// Validates per-transition references against the page's
    /// <c>pinned_refs</c> table. Every code citation must have a matching
    /// source pinned at the page level (repo URL + commit hash) so line
    /// numbers have a stable reference point. spec_prose references are
    /// validated for shape: cite required, quote optional.
    /// </summary>
    private static void LintReferences(SdlPage page, List<string> errors)
    {
        var pinnedSources = page.PinnedRefs?.Keys.ToHashSet(StringComparer.Ordinal)
                            ?? new HashSet<string>(StringComparer.Ordinal);

        foreach (var pin in page.PinnedRefs ?? new())
        {
            if (string.IsNullOrWhiteSpace(pin.Value.Repo))
                errors.Add($"{page.SourcePath}: pinned_refs[{pin.Key}] missing `repo`.");
            if (string.IsNullOrWhiteSpace(pin.Value.Commit))
                errors.Add($"{page.SourcePath}: pinned_refs[{pin.Key}] missing `commit`. Pin to a specific commit hash so line numbers stay valid.");
        }

        foreach (var t in page.Transitions)
        {
            foreach (var (r, i) in (t.References ?? new()).Select((r, i) => (r, i)))
            {
                if (string.IsNullOrWhiteSpace(r.Source))
                {
                    errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] missing `source`.");
                    continue;
                }

                if (r.Source == "spec_prose")
                {
                    if (string.IsNullOrWhiteSpace(r.Cite))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (spec_prose) missing `cite` (e.g. '§6.3.5 ¶1').");
                    if (!string.IsNullOrWhiteSpace(r.Path) || !string.IsNullOrWhiteSpace(r.Function) || r.Line is not null)
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (spec_prose) should not have path/function/line — those are for code citations.");
                }
                else
                {
                    if (!pinnedSources.Contains(r.Source))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] source `{r.Source}` is not declared in pinned_refs. Add an entry to the page-level pinned_refs table with the repo URL and a pinned commit hash.");
                    if (string.IsNullOrWhiteSpace(r.Path))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (source={r.Source}) missing `path`.");
                    if (string.IsNullOrWhiteSpace(r.Function))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (source={r.Source}) missing `function` (primary anchor; line numbers drift, function names survive refactors).");
                    if (!string.IsNullOrWhiteSpace(r.Cite) || !string.IsNullOrWhiteSpace(r.Quote))
                        errors.Add($"{page.SourcePath}: transition `{t.Id}` references[{i}] (source={r.Source}) has cite/quote — those are for spec_prose references.");
                }
            }
        }
    }

    // ─── Lints ────────────────────────────────────────────────────────

    /// <summary>
    /// Every decision defined on a page must appear with both "Yes" and
    /// "No" branches across some transition pair. A decision used with
    /// only one branch means the figure's other branch is missing from
    /// the transcription — almost always a slip rather than an
    /// intentional one-armed diamond.
    /// </summary>
    private static void LintDecisionBranchCompleteness(
        SdlPage page,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> errors)
    {
        var branchUses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var t in page.Transitions)
        {
            foreach (var step in t.Path ?? new())
            {
                if (string.IsNullOrWhiteSpace(step.Decision)) continue;
                if (!branchUses.TryGetValue(step.Decision!, out var seen))
                {
                    seen = new HashSet<string>(StringComparer.Ordinal);
                    branchUses[step.Decision!] = seen;
                }
                if (!string.IsNullOrWhiteSpace(step.Branch))
                {
                    seen.Add(step.Branch!);
                }
            }
        }

        foreach (var id in decisionsById.Keys)
        {
            if (!branchUses.TryGetValue(id, out var seen))
            {
                errors.Add($"{page.SourcePath}: decision `{id}` is declared but never referenced in any transition path.");
                continue;
            }
            if (!seen.Contains("Yes"))
                errors.Add($"{page.SourcePath}: decision `{id}` missing 'Yes' branch in any transition (seen: {string.Join(", ", seen.OrderBy(s => s, StringComparer.Ordinal))}). Add the missing column or mark `coverage: partial` with a verification_pending note.");
            if (!seen.Contains("No"))
                errors.Add($"{page.SourcePath}: decision `{id}` missing 'No' branch in any transition (seen: {string.Join(", ", seen.OrderBy(s => s, StringComparer.Ordinal))}). Add the missing column or mark `coverage: partial` with a verification_pending note.");
        }
    }

    /// <summary>
    /// Two transitions sharing the same <c>on:</c> event for one state
    /// must have provably-disjoint guards — otherwise the orchestrator
    /// silently picks the first match and the second is dead code.
    /// Disjointness here is decided by literal contradiction: if guard A
    /// contains <c>X</c> positively and guard B contains <c>X</c>
    /// negatively (or vice versa), they're disjoint. Guards with
    /// <c>or</c> are skipped (out of scope for this lint).
    /// </summary>
    private static void LintGuardOverlap(
        SdlPage page,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> errors)
    {
        var compiled = page.Transitions
            .Where(t => t.Path is not null)
            .Select(t => (t, lits: CompileGuardLiterals(t, decisionsById)))
            .ToList();

        var byEvent = compiled.GroupBy(x => x.t.On, StringComparer.Ordinal);
        foreach (var grp in byEvent)
        {
            var list = grp.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (list[i].lits is null || list[j].lits is null) continue;
                    if (!AreDisjoint(list[i].lits!, list[j].lits!))
                    {
                        errors.Add($"{page.SourcePath}: transitions `{list[i].t.Id}` and `{list[j].t.Id}` both fire on event `{grp.Key}` with non-disjoint guards. The orchestrator will silently pick the first match. Add a contradicting decision branch on one path, or merge the transitions.");
                    }
                }
            }
        }
    }

    private static HashSet<(string Ident, bool Positive)>? CompileGuardLiterals(
        SdlTransition t,
        IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        var lits = new HashSet<(string, bool)>();
        foreach (var step in t.Path ?? new())
        {
            if (string.IsNullOrWhiteSpace(step.Decision)) continue;
            if (!decisionsById.TryGetValue(step.Decision!, out var decision)) continue;
            var predicate = decision.Predicate ?? string.Empty;
            if (predicate.Contains(" or ", StringComparison.Ordinal))
            {
                return null;
            }
            var tokens = predicate.Split(GuardTokenSeparators, StringSplitOptions.RemoveEmptyEntries);
            int idx = 0;
            while (idx < tokens.Length)
            {
                if (tokens[idx] == "and") { idx++; continue; }
                bool positive = true;
                if (tokens[idx] == "not") { positive = false; idx++; }
                if (idx >= tokens.Length) break;
                var ident = tokens[idx++];
                if (step.Branch == "No") positive = !positive;
                lits.Add((ident, positive));
            }
        }
        return lits;
    }

    private static bool AreDisjoint(
        HashSet<(string Ident, bool Positive)> a,
        HashSet<(string Ident, bool Positive)> b)
    {
        // Two conjunctive literal sets are disjoint iff one literal in a
        // has its negation in b (or vice versa). Empty sets (unguarded)
        // are not disjoint with anything — they always fire.
        if (a.Count == 0 || b.Count == 0) return false;
        foreach (var lit in a)
        {
            if (b.Contains((lit.Ident, !lit.Positive))) return true;
        }
        return false;
    }
}
