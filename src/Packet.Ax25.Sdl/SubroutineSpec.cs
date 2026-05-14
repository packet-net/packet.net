namespace Packet.Ax25.Sdl;

/// <summary>
/// One subroutine from a figc4.7-style subroutines page. A subroutine
/// is essentially a function body: zero or more decision branches plus
/// an ordered action chain per branch combination.
/// </summary>
/// <remarks>
/// <para>
/// The runtime invokes a subroutine by walking <see cref="Paths"/> in
/// order, evaluating each path's <see cref="SubroutinePath.Guard"/>,
/// and executing the actions of the first path whose guard holds. The
/// guard is the conjunction of the path's decision-branch outcomes
/// (e.g. <c>mod_128 &amp;&amp; !srej_enabled</c>). A subroutine with no
/// decisions has a single path with no guard.
/// </para>
/// <para>
/// Unlike a transition, a subroutine has no <c>On</c> event (it's
/// invoked from a state-machine action chain via
/// <c>kind: subroutine</c>) and no <c>Next</c> state (control returns
/// to the caller after the actions run).
/// </para>
/// </remarks>
public sealed record SubroutineSpec(
    string Name,
    IReadOnlyList<SubroutinePath> Paths,
    string? Notes,
    IReadOnlyList<ImplementationReference> References);

/// <summary>
/// One path through a subroutine — i.e. one combination of
/// decision-branch outcomes plus the action chain that follows from
/// those choices.
/// </summary>
/// <remarks>
/// The guard expression mirrors <see cref="TransitionSpec.Guard"/> in
/// shape: a canonical boolean expression over predicate names, e.g.
/// <c>mod_128</c> or <c>!own_receiver_busy &amp;&amp; srej_enabled</c>.
/// Combined-AND form because all decision outcomes on a path must
/// hold simultaneously for the path to be taken.
/// </remarks>
public sealed record SubroutinePath(
    string Id,
    string? Guard,
    IReadOnlyList<ActionStep> Actions,
    string? Notes,
    IReadOnlyList<ImplementationReference> References,
    IReadOnlyList<LoopRange> Loops);
