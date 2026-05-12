namespace Packet.Ax25.Sdl;

/// <summary>
/// One SDL transition: when in <see cref="From"/> and we receive event
/// <see cref="On"/> while <see cref="Guard"/> holds, run <see cref="Actions"/>
/// in order and move to <see cref="Next"/>.
/// </summary>
/// <remarks>
/// Guard and action strings are opaque to the codegen — they describe spec
/// intent. The orchestrator in Packet.Ax25 maps each <see cref="ActionStep.Verb"/>
/// to concrete C# behaviour at runtime. <see cref="ActionStep.Kind"/> is
/// metadata recording which figc1.1 shape class produced the action, so the
/// figure can be redrawn from the YAML alone.
/// </remarks>
public sealed record TransitionSpec(
    string Id,
    string From,
    string On,
    string? Guard,
    IReadOnlyList<ActionStep> Actions,
    string Next,
    string? Notes,
    IReadOnlyList<ImplementationReference> References);

/// <summary>
/// One cross-reference citation for a transition — either a pointer into
/// the AX.25 specification prose (<see cref="Source"/> = <c>"spec_prose"</c>),
/// or a pointer to specific code in a pinned implementation repo.
/// </summary>
/// <remarks>
/// For code citations, the page-level <c>pinned_refs:</c> table binds the
/// <see cref="Source"/> name to a repo URL and a specific commit. Line
/// numbers in <see cref="Line"/> are valid at that commit; the
/// <see cref="Function"/> name is the primary durable anchor.
/// </remarks>
public sealed record ImplementationReference(
    string Source,
    string? Cite,
    string? Quote,
    string? Path,
    string? Function,
    int? Line,
    string? Note);

/// <summary>One step in a transition's action chain — a verb + the SDL shape class that produced it.</summary>
public sealed record ActionStep(string Verb, ActionKind Kind);

/// <summary>
/// The five SDL action shape-classes from figc1.1 that produce entries in a
/// transition's action chain. Other shapes (State, Test, Save, Subroutine
/// start/return, Input variants) are not actions and live elsewhere in the
/// generated representation.
/// </summary>
public enum ActionKind
{
    /// <summary>Signal generation to upper layer (left-pointer parallelogram). DL-* indications/confirms.</summary>
    SignalUpper,

    /// <summary>Signal generation to lower layer (right-pointer parallelogram). Frames to transmit (UI, SABM, RR, …).</summary>
    SignalLower,

    /// <summary>Processing description (plain rectangle). Internal tasks: variable assignments, timer ops, flag mutations.</summary>
    Processing,

    /// <summary>Subroutine call (rectangle with sidebars). Named reference into a subroutine page (figc4.7-style).</summary>
    Subroutine,

    /// <summary>Internal Signal Generation. Posts to an internal queue consumed elsewhere in this machine.</summary>
    InternalOut,
}

/// <summary>
/// Provenance for a generated state machine page — which spec figure it came
/// from. Surfaced in generated code so a reader can trace any transition back
/// to its source diagram.
/// </summary>
public sealed record SdlSource(
    string Spec,
    string Figure,
    string? Url);
