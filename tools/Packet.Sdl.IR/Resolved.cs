namespace Packet.Sdl.IR;

// ─── Resolved IR ─────────────────────────────────────────────────────
//
// Language-neutral representation of an SDL page after path-walking and
// action-verb canonicalisation. Emitters (C# / Go / …) consume this IR;
// nothing here is C#-specific.
//
// Build via Resolver.Resolve(SdlPage) / Resolver.Resolve(SubroutinePage).

public enum ResolvedActionKind
{
    SignalUpper,
    SignalLower,
    Processing,
    Subroutine,
    InternalOut,
}

/// <summary>One verb in a transition's or subroutine path's action list.</summary>
public sealed record ResolvedAction(string Verb, ResolvedActionKind Kind);

/// <summary>
/// One loop range over the flat <see cref="ResolvedTransition.Actions"/> list.
/// <see cref="Start"/> and <see cref="Length"/> point at the body slice;
/// <see cref="Predicate"/> is the boolean expression gating re-execution.
/// </summary>
public sealed record ResolvedLoop(int Start, int Length, string Predicate);

/// <summary>
/// One cross-reference citation. <see cref="Source"/> is "spec_prose" or
/// the key of a page-level pinned_refs entry. Code citations populate
/// <see cref="Path"/>/<see cref="Function"/>/<see cref="Line"/>;
/// spec_prose citations populate <see cref="Cite"/>/<see cref="Quote"/>.
/// </summary>
public sealed record ResolvedReference(
    string Source,
    string? Cite,
    string? Quote,
    string? Path,
    string? Function,
    int? Line,
    string? Note);

/// <summary>A resolved state-machine page (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 / etc.).</summary>
public sealed class ResolvedPage
{
    public string Machine { get; init; } = "";
    public string State { get; init; } = "";
    public string Coverage { get; init; } = "complete";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public IReadOnlyList<ResolvedTransition> Transitions { get; init; } = Array.Empty<ResolvedTransition>();
}

/// <summary>One resolved transition on a state-machine page.</summary>
public sealed class ResolvedTransition
{
    public string Id { get; init; } = "";
    public string On { get; init; } = "";
    public string? Guard { get; init; }
    public string Next { get; init; } = "";
    public string? Notes { get; init; }
    public IReadOnlyList<ResolvedAction> Actions { get; init; } = Array.Empty<ResolvedAction>();
    public IReadOnlyList<ResolvedLoop> Loops { get; init; } = Array.Empty<ResolvedLoop>();
    public IReadOnlyList<ResolvedReference> References { get; init; } = Array.Empty<ResolvedReference>();
}

/// <summary>A resolved subroutine page (figc4.7).</summary>
public sealed class ResolvedSubroutinesPage
{
    public string Machine { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string SourceSpec { get; init; } = "";
    public string SourceFigure { get; init; } = "";
    public string? SourceUrl { get; init; }
    public IReadOnlyList<ResolvedSubroutine> Subroutines { get; init; } = Array.Empty<ResolvedSubroutine>();
}

/// <summary>One resolved subroutine on a subroutine page.</summary>
public sealed class ResolvedSubroutine
{
    public string Name { get; init; } = "";
    public string? Notes { get; init; }
    public IReadOnlyList<ResolvedSubroutinePath> Paths { get; init; } = Array.Empty<ResolvedSubroutinePath>();
    public IReadOnlyList<ResolvedReference> References { get; init; } = Array.Empty<ResolvedReference>();
}

/// <summary>One resolved path through a subroutine.</summary>
public sealed class ResolvedSubroutinePath
{
    public string Id { get; init; } = "";
    public string? Guard { get; init; }
    public string? Notes { get; init; }
    public IReadOnlyList<ResolvedAction> Actions { get; init; } = Array.Empty<ResolvedAction>();
    public IReadOnlyList<ResolvedLoop> Loops { get; init; } = Array.Empty<ResolvedLoop>();
    public IReadOnlyList<ResolvedReference> References { get; init; } = Array.Empty<ResolvedReference>();
}
