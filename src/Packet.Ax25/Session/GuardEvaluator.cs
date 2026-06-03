using Packet.Ax25.Sdl;

namespace Packet.Ax25.Session;

/// <summary>
/// Evaluates an SDL transition <c>guard:</c> — now a typed conjunction of
/// optionally-negated guard atoms (<see cref="GuardTerm"/>) rather than a
/// string expression. A guard holds when every term holds; an empty / null
/// term list means the transition is unguarded (always fires).
/// </summary>
/// <remarks>
/// <para>
/// Each <see cref="GuardTerm.Atom"/> is resolved against an externally-supplied
/// binding table keyed by the typed <see cref="Ax25Guard"/> closed-set member
/// (e.g. <see cref="Ax25Guard.OwnReceiverBusy"/> → a lambda reading
/// <c>ctx.OwnReceiverBusy</c>). The binding table is built exhaustively by
/// <see cref="Ax25SessionBindings.CreateDefault"/> — every <see cref="Ax25Guard"/>
/// member is bound — so a renamed or typo'd atom is a compile error in the
/// codegen-emitted tables, not an unbound-identifier thrown at runtime.
/// </para>
/// <para>
/// The composed guard the codegen emits is a pure conjunction
/// (<c>not peer_receiver_busy and vs_eq_va_plus_k</c> ⇒ two terms). A top-level
/// <c>or</c> never appears in the figures (atoms whose <em>name</em> contains
/// <c>_or_</c> are single opaque atoms); the package's <c>GuardExpression</c>
/// parser refuses one, so the runtime never has to evaluate a disjunction.
/// </para>
/// </remarks>
public sealed class GuardEvaluator
{
    private readonly IReadOnlyDictionary<Ax25Guard, Func<bool>> bindings;

    /// <summary>
    /// Build an evaluator over <paramref name="bindings"/>. Each entry maps an
    /// <see cref="Ax25Guard"/> atom to a thunk that returns the atom's current
    /// boolean value. Thunks are evaluated at each <c>Evaluate</c> call, so
    /// capturing mutable state via closure is the normal pattern.
    /// </summary>
    public GuardEvaluator(IReadOnlyDictionary<Ax25Guard, Func<bool>> bindings)
    {
        this.bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    /// <summary>
    /// Evaluate a guard expressed as a conjunction of <see cref="GuardTerm"/>s
    /// (the shape <see cref="TransitionSpec.Guard"/> / <see cref="SubroutinePath.Guard"/>
    /// carry). Returns <c>true</c> when every term holds; a <c>null</c> or empty
    /// list is treated as <c>true</c> (no guard).
    /// </summary>
    /// <exception cref="GuardEvaluationException">
    /// Thrown when a term references an atom with no binding.
    /// </exception>
    public bool Evaluate(IReadOnlyList<GuardTerm>? guard)
    {
        if (guard is null || guard.Count == 0)
        {
            return true;
        }

        foreach (var term in guard)
        {
            if (!EvaluateTerm(term))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Evaluate a single <see cref="GuardTerm"/> — the shape
    /// <see cref="LoopRange.Predicate"/> carries (the loop's continue condition,
    /// already negated where the figure's continuing edge is the decision's No
    /// branch).
    /// </summary>
    /// <exception cref="GuardEvaluationException">
    /// Thrown when the term references an atom with no binding.
    /// </exception>
    public bool Evaluate(GuardTerm term)
    {
        ArgumentNullException.ThrowIfNull(term);
        return EvaluateTerm(term);
    }

    private bool EvaluateTerm(GuardTerm term)
    {
        if (!bindings.TryGetValue(term.Atom, out var predicate))
        {
            throw new GuardEvaluationException(
                $"guard atom '{term.Atom}' has no binding — every Ax25Guard member must be bound " +
                "(see Ax25SessionBindings.CreateDefault).");
        }
        var value = predicate();
        return term.Negate ? !value : value;
    }
}

/// <summary>Thrown when a guard atom has no binding (or a bound closure signals it can't evaluate).</summary>
public sealed class GuardEvaluationException : Exception
{
    public GuardEvaluationException(string message) : base(message) { }
}
