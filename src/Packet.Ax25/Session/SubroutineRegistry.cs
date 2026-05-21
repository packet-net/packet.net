using Packet.Ax25.Sdl;

namespace Packet.Ax25.Session;

/// <summary>
/// Looks up and invokes SDL subroutine action chains by canonical name.
/// The dispatcher routes every <c>kind: subroutine</c> verb (e.g.
/// <c>Establish_Data_Link</c>, <c>UI_Check</c>, <c>Select_T1_Value</c>)
/// through this interface; production wires this against figc4.7's
/// subroutine transcriptions, tests can register custom recorders.
/// </summary>
public interface ISubroutineRegistry
{
    /// <summary>
    /// Invoke the subroutine identified by <paramref name="name"/> in
    /// the supplied transition context. Implementations decide how to
    /// handle unknown names — the default registry throws, so a
    /// transcription typo doesn't silently no-op.
    /// </summary>
    void Invoke(string name, TransitionContext tx);
}

/// <summary>
/// Pre-populated subroutine registry. Defaults each known subroutine name
/// to a walker that executes the figc4.7-generated <see cref="SubroutineSpec"/>
/// data from <see cref="DataLink_Subroutines"/>, then lets tests
/// override individual entries with hand-written delegates via
/// <see cref="Register"/>.
/// </summary>
/// <remarks>
/// <para>
/// The registry resolves a subroutine call at <see cref="Invoke"/> time
/// by looking up the name in its internal delegate table. The
/// constructor populates that table from the supplied
/// <see cref="IReadOnlyList{SubroutineSpec}"/> (defaults to
/// <see cref="DataLink_Subroutines.Subroutines"/>): each name maps to a
/// walker that evaluates each path's guard via the supplied
/// <see cref="GuardEvaluator"/>, takes the first match, and executes
/// the path's <see cref="ActionStep"/> chain via the supplied
/// <see cref="IActionDispatcher"/>.
/// </para>
/// <para>
/// To override a subroutine for testing, use <see cref="Register"/>:
/// <code>
/// var registry = new DefaultSubroutineRegistry();
/// registry.Wire(dispatcher, guards);   // optional — without this, subroutines no-op
/// registry.Register("Establish_Data_Link", tx => { /* record */ });
/// </code>
/// </para>
/// </remarks>
public sealed class DefaultSubroutineRegistry : ISubroutineRegistry
{
    private readonly Dictionary<string, Action<TransitionContext>> subroutines = new(StringComparer.Ordinal);
    private readonly HashSet<string> userOverridden = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<SubroutineSpec> specs;
    private IActionDispatcher? wiredDispatcher;
    private GuardEvaluator? wiredGuards;

    /// <summary>
    /// Legacy subroutine names — pure name rewrites where the alias walks
    /// the canonical body unchanged. Used when a YAML page calls a
    /// subroutine under a name that the redrawn figc4.7 doesn't emit
    /// directly.
    /// </summary>
    /// <remarks>
    /// After <see cref="Wire"/>, invoking the legacy alias runs the
    /// canonical body; the figure-faithful caller-side name in the
    /// state-machine YAMLs stays unchanged.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> LegacyAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Packet.Ax25.Sdl v0.5.0 names the figc4.7 subroutine `Select_T1`
            // (matching the figure heading); earlier transcriptions called
            // it `Select_T1_Value`. Keep the longer historic name working.
            ["Select_T1_Value"]      = "Select_T1",
            // figc4.7 emits `Check_Need_for_Response` (lowercase 'for'); the
            // calling pages spell the action verb `Check Need For Response`
            // which normalises to `Check_Need_For_Response`. Alias the
            // capital-F form so action-verb dispatch finds the body.
            ["Check_Need_For_Response"] = "Check_Need_for_Response",
        };

    /// <summary>
    /// Context-binding aliases — alias names that mutate the trigger
    /// context before walking the canonical body. Used where the SDL
    /// figure's call-site annotation on a subroutine implies an
    /// out-of-band binding that the canonical body alone doesn't
    /// produce.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Concrete case: figc4.7b page 102 draws <c>Check Need for Response</c>'s
    /// Yes branch as <c>Enquiry Response (F = 1)</c>. The <c>(F = 1)</c>
    /// annotation isn't explained in the spec prose (§C1.2 covers the
    /// standard SDL symbols only); plausible readings include a formal
    /// parameter binding, a frame-emission annotation analogous to the
    /// <c>DM (F = 1)</c> shorthand used elsewhere, or a documentation
    /// reminder. The <em>wire contract</em> is unambiguous either way:
    /// AX.25 v2.2 §4.3 prose states *"the reply to this poll is indicated
    /// by setting the response (final) bit in the appropriate frame"*, so
    /// every response taking this code path must go out with F=1. The
    /// canonical encoding of the annotation in the yaml DSL is the open
    /// question — tracked at
    /// <see href="https://github.com/M0LTE/ax25sdl/issues/45">m0lte/ax25sdl#45</see>.
    /// </para>
    /// <para>
    /// The walker / codegen currently surfaces the annotation as a
    /// name-suffix alias (<c>Enquiry_Response_F_1</c> / <c>_F_0</c>) of
    /// the canonical <c>Enquiry_Response</c> body. Until the upstream
    /// encoding settles, this dictionary mutates <see cref="PendingFrame.PfBit"/>
    /// before walking the canonical body, so the response-emitting verbs
    /// (<c>RR Response</c>, <c>RNR Response</c>, <c>SREJ</c>) emit frames
    /// with the right F bit on the wire. Without the binding, polls
    /// received by a Connected peer get responses with F=0, the polling
    /// side's TimerRecovery guard <c>response_and_F_eq_1</c> never matches,
    /// and recovery-to-Connected is unreachable.
    /// </para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, (string Canonical, Action<TransitionContext> Bind)> ContextBindingAliases =
        new Dictionary<string, (string, Action<TransitionContext>)>(StringComparer.Ordinal)
        {
            ["Enquiry_Response_F_1"] = ("Enquiry_Response", tx => tx.Pending.PfBit = true),
            ["Enquiry_Response_F_0"] = ("Enquiry_Response", tx => tx.Pending.PfBit = false),
        };

    /// <summary>
    /// Canonical names of every subroutine the transcribed pages reference.
    /// Sourced from the generated <see cref="DataLink_Subroutines.Subroutines"/>
    /// list plus the legacy aliases.
    /// </summary>
    public static IReadOnlyList<string> KnownSubroutines { get; } =
        DataLink_Subroutines.Subroutines.Select(s => s.Name)
            .Concat(LegacyAliases.Keys)
            .Concat(ContextBindingAliases.Keys)
            .ToList();

    /// <summary>
    /// Construct a registry pre-populated with no-op stubs for every name
    /// in <see cref="KnownSubroutines"/>. Call <see cref="Wire"/> to upgrade
    /// the stubs to actual SubroutineSpec walkers (needs a dispatcher +
    /// guard evaluator to execute path actions and evaluate path guards).
    /// </summary>
    public DefaultSubroutineRegistry()
        : this(DataLink_Subroutines.Subroutines)
    {
    }

    /// <summary>
    /// Construct a registry from an explicit subroutine-spec list.
    /// Primarily for tests that want to substitute a smaller / different
    /// list; production code should use the parameterless constructor.
    /// </summary>
    public DefaultSubroutineRegistry(IReadOnlyList<SubroutineSpec> specs)
    {
        ArgumentNullException.ThrowIfNull(specs);
        this.specs = specs;
        foreach (var spec in specs)
        {
            subroutines[spec.Name] = _ => { /* no-op until Wire() is called */ };
        }
        // Legacy aliases (e.g. Select_T1_Value, Check_Need_For_Response with
        // capital F) — referenced by older transcriptions or by paths that
        // called the longer historic name; resolved to the same body as
        // their canonical target once Wire() runs.
        foreach (var alias in LegacyAliases.Keys)
        {
            subroutines[alias] = _ => { /* no-op until Wire() is called */ };
        }
        // Context-binding aliases (e.g. Enquiry_Response_F_0/F_1) — resolved
        // to the canonical body, but with a context-mutation applied first
        // so the body's frame-emission verbs see the right Pending state.
        foreach (var alias in ContextBindingAliases.Keys)
        {
            subroutines[alias] = _ => { /* no-op until Wire() is called */ };
        }
    }

    /// <summary>
    /// Bind this registry to a dispatcher + guard evaluator. After this
    /// call, every <see cref="SubroutineSpec"/> name not previously
    /// overridden via <see cref="Register"/> maps to a walker that
    /// evaluates each path's guard, takes the first match, and executes
    /// its action chain.
    /// </summary>
    /// <remarks>
    /// Order-independent w.r.t. <see cref="Register"/>: names that have
    /// been explicitly registered keep their caller-supplied delegate;
    /// names whose entries are still default no-op stubs are replaced
    /// with walkers.
    /// </remarks>
    public void Wire(IActionDispatcher dispatcher, GuardEvaluator guards)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(guards);
        wiredDispatcher = dispatcher;
        wiredGuards = guards;
        var specsByName = specs.ToDictionary(s => s.Name, s => s, StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            if (userOverridden.Contains(spec.Name)) continue;
            var captured = spec;   // close-over copy
            subroutines[spec.Name] = tx => WalkSubroutine(captured, tx);
        }
        // Each legacy alias walks the canonical spec body it maps to.
        // Existing tests that Register a recorder under the legacy name
        // continue to win (userOverridden check).
        foreach (var (alias, canonicalName) in LegacyAliases)
        {
            if (userOverridden.Contains(alias)) continue;
            if (!specsByName.TryGetValue(canonicalName, out var spec)) continue;
            var captured = spec;
            subroutines[alias] = tx => WalkSubroutine(captured, tx);
        }
        // Each context-binding alias mutates the trigger context then walks
        // the canonical body — see ContextBindingAliases doc for why the
        // mutation is needed and the open encoding question at
        // m0lte/ax25sdl#45.
        foreach (var (alias, (canonicalName, bind)) in ContextBindingAliases)
        {
            if (userOverridden.Contains(alias)) continue;
            if (!specsByName.TryGetValue(canonicalName, out var spec)) continue;
            var captured = spec;
            var capturedBind = bind;
            subroutines[alias] = tx =>
            {
                capturedBind(tx);
                WalkSubroutine(captured, tx);
            };
        }
    }

    /// <summary>
    /// Register a custom implementation for the named subroutine. Replaces
    /// any existing entry (including the default walker if Wire has been
    /// called) so tests can observe / mock subroutine calls. The override
    /// is "sticky" — a subsequent <see cref="Wire"/> call will NOT
    /// re-replace this name with a walker.
    /// </summary>
    public void Register(string name, Action<TransitionContext> implementation)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(implementation);
        subroutines[name] = implementation;
        userOverridden.Add(name);
    }

    /// <inheritdoc/>
    public void Invoke(string name, TransitionContext tx)
    {
        if (!subroutines.TryGetValue(name, out var impl))
        {
            throw new InvalidOperationException(
                $"unknown SDL subroutine: '{name}'. " +
                "Known subroutines are populated from DataLink_Subroutines.Subroutines — " +
                "either the figc4.7 YAML doesn't declare this name, or this is a transcription typo.");
        }
        impl(tx);
    }

    private void WalkSubroutine(SubroutineSpec spec, TransitionContext tx)
    {
        // Wire() must have run before walker fires. If a name still has its
        // no-op stub, the user never called Wire — the call no-ops silently,
        // matching the pre-figc4.7-codegen behaviour.
        if (wiredDispatcher is null || wiredGuards is null) return;

        foreach (var path in spec.Paths)
        {
            bool guardHolds;
            try
            {
                guardHolds = wiredGuards.Evaluate(path.Guard);
            }
            catch (GuardEvaluationException)
            {
                // Predicate isn't bound yet (frame-aware bindings are item 5
                // of the interop arc and lag behind figc4.7's path
                // requirements). Treat this path as not-matching — the
                // subroutine call degrades to no-op rather than crash the
                // calling state-machine transition. When the predicate is
                // bound, the walker starts following the path automatically.
                continue;
            }
            if (!guardHolds) continue;
            wiredDispatcher.Execute(path.Actions, tx);
            return;
        }
        // No matching path — silently no-op.
    }

}
