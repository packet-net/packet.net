namespace Packet.Sdl.IR;

/// <summary>
/// Converts loaded-and-validated YAML pages into the language-neutral
/// <see cref="ResolvedPage"/> / <see cref="ResolvedSubroutinesPage"/> IR
/// that emitters consume. Run after <see cref="Validation"/>; assumes the
/// page structure is well-formed (every decision id referenced exists,
/// every action has a kind, etc.).
/// </summary>
public static class Resolver
{
    public static ResolvedPage Resolve(SdlPage page)
    {
        var decisionsById = page.Decisions.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
        return new ResolvedPage
        {
            Machine      = page.Machine,
            State        = page.State,
            Coverage     = page.Coverage,
            SourcePath   = page.SourcePath,
            SourceSpec   = page.Source!.Spec,
            SourceFigure = page.Source.Figure,
            SourceUrl    = page.Source.Url,
            Transitions  = page.Transitions.Select(t => ResolveTransition(t, decisionsById)).ToList(),
        };
    }

    public static ResolvedSubroutinesPage Resolve(SubroutinePage page) => new()
    {
        Machine      = page.Machine,
        SourcePath   = page.SourcePath,
        SourceSpec   = page.Source!.Spec,
        SourceFigure = page.Source.Figure,
        SourceUrl    = page.Source.Url,
        Subroutines  = page.Subroutines.Select(ResolveSubroutine).ToList(),
    };

    private static ResolvedSubroutine ResolveSubroutine(SubroutineYamlEntry s)
    {
        var decisionsById = s.Decisions.ToDictionary(d => d.Id, d => d, StringComparer.Ordinal);
        return new ResolvedSubroutine
        {
            Name       = s.Name,
            Notes      = s.Notes,
            Paths      = s.Paths.Select(p => ResolveSubroutinePath(p, decisionsById)).ToList(),
            References = Array.Empty<ResolvedReference>(),  // subroutine-level refs not currently surfaced (path-level only)
        };
    }

    private static ResolvedSubroutinePath ResolveSubroutinePath(
        SubroutinePathYaml p,
        IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        var predicates = new List<string>();
        var actions    = new List<ResolvedAction>();
        var loops      = new List<ResolvedLoop>();
        WalkPath(p.Path, decisionsById, predicates, actions, loops);
        return new ResolvedSubroutinePath
        {
            Id         = p.Id,
            Guard      = predicates.Count == 0 ? null : string.Join(" and ", predicates),
            Notes      = p.Notes,
            Actions    = actions,
            Loops      = loops,
            References = MapReferences(p.References),
        };
    }

    private static ResolvedTransition ResolveTransition(
        SdlTransition t,
        IReadOnlyDictionary<string, SdlDecision> decisionsById)
    {
        var predicates = new List<string>();
        var actions    = new List<ResolvedAction>();
        var loops      = new List<ResolvedLoop>();
        WalkPath(t.Path, decisionsById, predicates, actions, loops);
        return new ResolvedTransition
        {
            Id         = t.Id,
            On         = t.On,
            Guard      = predicates.Count == 0 ? null : string.Join(" and ", predicates),
            Next       = t.Next,
            Notes      = t.Notes,
            Actions    = actions,
            Loops      = loops,
            References = MapReferences(t.References),
        };
    }

    private static IReadOnlyList<ResolvedReference> MapReferences(IEnumerable<SdlReference>? refs)
    {
        if (refs is null) return Array.Empty<ResolvedReference>();
        return refs.Select(r => new ResolvedReference(
            r.Source, r.Cite, r.Quote, r.Path, r.Function, r.Line, r.Note)).ToList();
    }

    /// <summary>
    /// Walks a path, accumulating guard predicates, flattening loop bodies
    /// into the action list, and recording loop ranges. Decision branches
    /// contribute a predicate (negated for the No branch). Loop bodies are
    /// action-only (validator enforces).
    /// </summary>
    private static void WalkPath(
        List<SdlPathStep> path,
        IReadOnlyDictionary<string, SdlDecision> decisionsById,
        List<string> predicates,
        List<ResolvedAction> actions,
        List<ResolvedLoop> loops)
    {
        foreach (var step in path)
        {
            if (!string.IsNullOrWhiteSpace(step.Decision))
            {
                var decision = decisionsById[step.Decision!];
                predicates.Add(step.Branch == "Yes" ? decision.Predicate : "not " + decision.Predicate);
            }
            else if (!string.IsNullOrWhiteSpace(step.LoopWhile))
            {
                var loopGuard = decisionsById[step.LoopWhile!];
                var startIndex = actions.Count;
                foreach (var body in step.Body!)
                {
                    if (!string.IsNullOrWhiteSpace(body.Action))
                    {
                        actions.Add(new ResolvedAction(body.Action!, ParseKind(body.Kind!)));
                    }
                }
                loops.Add(new ResolvedLoop(startIndex, actions.Count - startIndex, loopGuard.Predicate));
            }
            else
            {
                actions.Add(new ResolvedAction(step.Action!, ParseKind(step.Kind!)));
            }
        }
    }

    public static ResolvedActionKind ParseKind(string kind) => kind switch
    {
        "signal_upper" => ResolvedActionKind.SignalUpper,
        "signal_lower" => ResolvedActionKind.SignalLower,
        "processing"   => ResolvedActionKind.Processing,
        "subroutine"   => ResolvedActionKind.Subroutine,
        "internal_out" => ResolvedActionKind.InternalOut,
        _ => throw new InvalidOperationException($"unknown action kind '{kind}'"),
    };
}
