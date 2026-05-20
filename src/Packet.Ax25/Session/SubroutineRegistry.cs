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
    /// Legacy subroutine names that remain referenced by older
    /// state-machine YAML pages (e.g. <c>connected.sdl.yaml</c>'s
    /// <c>Enquiry_Response_F_0</c> / <c>_F_1</c>) but aren't separate
    /// subroutines in the redrawn <c>figc4.7</c> — both call the same
    /// <c>Enquiry_Response</c> body, with the F-bit choice arising
    /// naturally from the spec's first-decision predicate
    /// <c>F == 1 &amp; (Frame==RR || Frame==RNR || Frame==I)?</c>.
    /// </summary>
    /// <remarks>
    /// Each entry maps the legacy alias name to the canonical spec
    /// name to walk. After <see cref="Wire"/>, invoking the legacy
    /// alias runs the canonical body; the figure-faithful caller-side
    /// name in <c>connected.sdl.yaml</c> stays unchanged.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> LegacyAliases =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Enquiry_Response_F_0"] = "Enquiry_Response",
            ["Enquiry_Response_F_1"] = "Enquiry_Response",
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
    /// Hand-coded subroutines not (yet) present in
    /// <see cref="DataLink_Subroutines.Subroutines"/> but still referenced
    /// by other state-machine pages and so must be invocable. Currently only
    /// <c>Establish_Data_Link</c> and <c>Establish_Extended_Data_Link</c> —
    /// the figc4.7 graphml has an authoring bug (n50 missing outgoing edge,
    /// m0lte/ax25sdl#11) that causes the walker to skip them. Once that's
    /// fixed and a new Packet.Ax25.Sdl release ships, this list collapses to
    /// empty and the entries appear in <see cref="DataLink_Subroutines.Subroutines"/>
    /// naturally.
    /// </summary>
    private static readonly IReadOnlyList<string> HandCodedSubroutines = new[]
    {
        "Establish_Data_Link",
        "Establish_Extended_Data_Link",
    };

    /// <summary>
    /// Canonical names of every subroutine the transcribed pages reference.
    /// Sourced from the generated <see cref="DataLink_Subroutines.Subroutines"/>
    /// list plus the legacy aliases plus the hand-coded fallbacks for
    /// figc4.7 subroutines the walker currently skips.
    /// </summary>
    public static IReadOnlyList<string> KnownSubroutines { get; } =
        DataLink_Subroutines.Subroutines.Select(s => s.Name)
            .Concat(LegacyAliases.Keys)
            .Concat(HandCodedSubroutines)
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
        // Legacy aliases (e.g. Enquiry_Response_F_0 / _F_1, Select_T1_Value)
        // — referenced by older transcriptions or by paths that called the
        // longer historic name; resolved to the same body as their
        // canonical target once Wire() runs.
        foreach (var alias in LegacyAliases.Keys)
        {
            subroutines[alias] = _ => { /* no-op until Wire() is called */ };
        }
        // `Establish_Data_Link` and `Establish_Extended_Data_Link` aren't
        // in the v0.5.0 DataLink_Subroutines because the source graphml is
        // missing an outgoing edge on n50 (raised against the spec authors
        // at m0lte/ax25sdl#11). Until that graphml is fixed, supply the
        // hand-coded equivalents so figc4.x state-machine pages that call
        // them have a working implementation rather than hitting "unknown
        // SDL subroutine".
        //
        // `Establish_Data_Link` itself picks SABM vs SABME at runtime based
        // on the session's IsExtended flag (figc4.7's `mod_128?` decision):
        // not-extended → SABM (mod-8), extended → SABME (mod-128). Matches
        // the pre-redraw walker behaviour the existing tests expect.
        // `Establish_Extended_Data_Link` unconditionally sends SABME (used
        // by figc4.6's t08 / t09 transitions where mod-128 is already the
        // chosen path). Both follow the §C4.7 spec body: clear exception
        // conditions, RC := 0, send SABM/SABME(P=1), stop T3, start T1.
        subroutines["Establish_Data_Link"]          = tx => EstablishDataLink(tx, extended: EvaluateMod128(tx));
        subroutines["Establish_Extended_Data_Link"] = tx => EstablishDataLink(tx, extended: true);
    }

    /// <summary>
    /// Evaluate the <c>mod_128</c> predicate via the wired
    /// <see cref="GuardEvaluator"/> if available, falling back to
    /// <see cref="Ax25SessionContext.IsExtended"/> if the registry hasn't
    /// been wired. Used by the hand-coded Establish_Data_Link to choose
    /// SABM vs SABME at runtime — mirrors the figc4.7 redraw's first
    /// decision diamond. Wraps in try / catch so tests passing a guard
    /// evaluator that throws on unbound predicates degrade gracefully
    /// (no-match → false → SABM path).
    /// </summary>
    private bool EvaluateMod128(TransitionContext tx)
    {
        if (wiredGuards is null) return tx.Session.IsExtended;
        try { return wiredGuards.Evaluate("mod_128"); }
        catch (GuardEvaluationException) { return false; }
    }

    private void EstablishDataLink(TransitionContext tx, bool extended)
    {
        if (wiredDispatcher is null) return;
        var steps = new ActionStep[]
        {
            new("Clear_Exception_Conditions", ActionKind.Subroutine),
            new(extended ? "SABME" : "SABM", ActionKind.SignalLower),
            new("stop_T3", ActionKind.Processing),
            new("start_T1", ActionKind.Processing),
        };
        // Spec body: RC starts at 0 and the SABM/SABME transmission counts
        // as the first attempt — net result RC = 1 after the call. The
        // pre-redraw figc4.7 SubroutineSpec did this implicitly via the
        // composite "RC := 1" action; the figc4.7 redraw decomposes the
        // step but the post-condition is the same. Tests assert RC == 1.
        tx.Session.RC = 1;
        tx.Pending.PfBit = true;
        wiredDispatcher.Execute(steps, tx);
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
