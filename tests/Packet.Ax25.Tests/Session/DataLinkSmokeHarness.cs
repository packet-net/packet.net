using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Shared driver for the per-state data-link smoke tests
/// (<c>DataLink&lt;State&gt;SmokeTests</c>). Each of the six data-link states
/// has a thin test class that feeds its codegen-emitted transition table
/// (e.g. <see cref="DataLink_Disconnected.Transitions"/>) through
/// <see cref="AssertTransitionFiresAsDeclared"/> as a
/// <c>[Theory]</c>+<c>[MemberData]</c> over the live <c>TransitionSpec.Id</c>s,
/// so coverage is <i>derived from the table</i> — a transition added or renamed
/// upstream in <c>Packet.Ax25.Sdl</c> auto-appears (or fails) rather than
/// silently slipping past a hand-maintained list.
/// </summary>
/// <remarks>
/// For one transition the driver: derives a guard assignment that satisfies that
/// transition's guard (every other atom on the page bound <c>false</c>), asserts
/// the runtime matches <b>exactly</b> that transition for its event (a collision
/// means the page's guards aren't mutually exclusive under the derivation, or the
/// derivation is too weak), posts the event, and asserts the session lands on the
/// declared <c>Next</c> state having recorded the declared action sequence in
/// order. Decisions/actions are stubbed (a recording dispatcher that mutates
/// nothing, predicates bound to fixed booleans), so this catches orchestrator
/// routing, guard parsing, action order/kind, and next-state — but not whether
/// the YAML transcription faithfully reflects the figure (human review) nor that
/// the actions do the right thing to live context (the behavioural conformance
/// suite). The generalised form of the per-state pattern first written by hand in
/// <c>DataLinkDisconnectedSmokeTests</c> and data-driven in
/// <c>DataLinkTimerRecoverySmokeTests</c>.
/// </remarks>
internal static class DataLinkSmokeHarness
{
    /// <summary>
    /// Drive <paramref name="transitionId"/> from <paramref name="table"/> (the
    /// state whose initial state is <paramref name="initialState"/>) and assert
    /// it fires exactly as the table declares: unique guard match, declared next
    /// state, declared action sequence in order.
    /// </summary>
    public static void AssertTransitionFiresAsDeclared(
        IReadOnlyList<TransitionSpec> table, string initialState, string transitionId)
    {
        var transition = table.Single(t => t.Id == transitionId);

        // Bind every atom mentioned anywhere on the page to false, then apply the
        // target transition's satisfying overrides. Binding the siblings lets the
        // uniqueness check evaluate their guards without unbound-identifier errors.
        var assignment = table
            .SelectMany(t => GuardsThatSatisfy(t.Guard ?? "").Keys)
            .ToHashSet(StringComparer.Ordinal)
            .ToDictionary(a => a, _ => false, StringComparer.Ordinal);
        foreach (var (name, value) in GuardsThatSatisfy(transition.Guard ?? ""))
            assignment[name] = value;

        var (session, recorder, guards) = NewSession(assignment, initialState);

        var matching = table
            .Where(t => t.On == transition.On)
            .Where(t => guards.Evaluate(t.Guard))
            .ToList();
        matching.Should().ContainSingle(
            $"transition '{transitionId}' on event '{transition.On}' with derived guards must match exactly one transition (a collision implies the page's guards are not mutually exclusive under the derivation, or the derivation is too weak)");
        matching[0].Id.Should().Be(transitionId,
            "the matched transition should be the one we're targeting");

        session.PostEvent(EventFor(transition.On));

        session.CurrentState.Should().Be(transition.Next,
            $"transition '{transitionId}' should land on '{transition.Next}'");

        // Loops run against this harness's empty session state, so a head-test
        // (while) loop body executes zero times and is absent from the recorded
        // sequence; a tail-test (do-while) body runs once (= the flat list). Loop
        // iteration is covered behaviourally elsewhere, not in this smoke test.
        var headLoopBody = new HashSet<int>();
        foreach (var loop in transition.Loops.Where(l => !l.TestAtEnd))
            for (int i = loop.Start; i < loop.Start + loop.Length; i++)
                headLoopBody.Add(i);
        var expectedRecorded = transition.Actions
            .Where((_, i) => !headLoopBody.Contains(i))
            .Select(a => (a.Verb, a.Kind))
            .ToArray();

        recorder.Recorded.Should().Equal(expectedRecorded,
            $"transition '{transitionId}' actions should fire in declared order (head-test loop bodies run zero times against empty harness state)");
    }

    // ─── Guard derivation ──────────────────────────────────────────────

    /// <summary>
    /// Parse a guard expression into a <c>name → value</c> map that satisfies it.
    /// The data-link figures only ever compose atoms with <c>and</c> / <c>not</c>
    /// (verified across all six pages — no <c>or</c>, no parentheses), and each
    /// guard string spells out the full root-to-leaf decision path, so setting
    /// exactly its atoms makes only that leaf true.
    /// </summary>
    private static Dictionary<string, bool> GuardsThatSatisfy(string guard)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(guard)) return result;

        foreach (var raw in guard.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            var negated = t.StartsWith("not ", StringComparison.Ordinal);
            var atom = negated ? t.Substring(4).Trim() : t;
            result[atom] = !negated;
        }
        return result;
    }

    // ─── Event factory ─────────────────────────────────────────────────

    /// <summary>
    /// Construct the <see cref="Ax25Event"/> for an <c>on</c> name. The
    /// orchestrator routes by event name and the smoke harness stubs every guard
    /// atom, so frame contents are immaterial — received-frame events all carry a
    /// stand-in UI frame. Covers the union of <c>on</c> names across all six
    /// data-link pages; an unmapped name throws so a new upstream event surfaces.
    /// </summary>
    private static Ax25Event EventFor(string onName) => onName switch
    {
        "DL_DISCONNECT_request"                  => new DlDisconnectRequest(),
        "DL_CONNECT_request"                     => new DlConnectRequest(),
        "DL_DATA_request"                        => new DlDataRequest("x"u8.ToArray()),
        "DL_UNIT_DATA_request"                   => new DlUnitDataRequest("x"u8.ToArray()),
        "DL_FLOW_OFF_request"                    => new DlFlowOffRequest(),
        "DL_FLOW_ON_request"                     => new DlFlowOnRequest(),
        "I_frame_pops_off_queue"                 => new IFramePopsOffQueue("x"u8.ToArray()),
        "I_received"                             => new IFrameReceived(Frame()),
        "RR_received"                            => new RrReceived(Frame()),
        "RNR_received"                           => new RnrReceived(Frame()),
        "REJ_received"                           => new RejReceived(Frame()),
        "SREJ_received"                          => new SrejReceived(Frame()),
        "SABM_received"                          => new SabmReceived(Frame()),
        "SABME_received"                         => new SabmeReceived(Frame()),
        "DISC_received"                          => new DiscReceived(Frame()),
        "UA_received"                            => new UaReceived(Frame()),
        "DM_received"                            => new DmReceived(Frame()),
        "FRMR_received"                          => new FrmrReceived(Frame()),
        "UI_received"                            => new UiReceived(Frame()),
        "i_or_s_command_received"                => new IOrSCommandReceived(Frame()),
        "all_other_commands"                     => new AllOtherCommands(Frame()),
        "LM_SEIZE_confirm"                       => new LmSeizeConfirm(),
        "T1_expiry"                              => new T1Expiry(),
        "T3_expiry"                              => new T3Expiry(),
        "control_field_error"                    => new ControlFieldError(),
        "info_not_permitted_in_frame"            => new InfoNotPermittedInFrame(),
        "u_or_s_frame_length_error"              => new UOrSFrameLengthError(),
        "all_other_primitives__from_lower_layer" => new AllOtherPrimitivesFromLowerLayer(),
        "all_other_primitives__from_upper_layer" => new AllOtherPrimitivesFromUpperLayer(),
        _ => throw new InvalidOperationException($"no event factory for on='{onName}' (add a case)"),
    };

    /// <summary>Stand-in frame for received-frame events; the orchestrator routes
    /// by event name and the harness stubs every guard, so contents don't matter.</summary>
    private static Ax25Frame Frame() => Ax25Frame.Ui(
        destination: new Callsign("M0LTE", 0),
        source:      new Callsign("G7XYZ", 7),
        info:        "x"u8);

    // ─── Session + recorder ────────────────────────────────────────────

    private sealed class RecordingActionDispatcher : IActionDispatcher
    {
        public List<(Ax25ActionVerb Verb, ActionKind Kind)> Recorded { get; } = new();

        public void Execute(IEnumerable<ActionStep> actions, TransitionContext tx)
        {
            foreach (var step in actions)
                Recorded.Add((step.Verb, step.Kind));
        }
    }

    private static (Ax25Session session, RecordingActionDispatcher recorder, GuardEvaluator guards) NewSession(
        Dictionary<string, bool> guardValues, string initialState)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        // Start from the runtime defaults (so timer-state / counter predicates have
        // sensible bindings) then override with the atoms named by this page.
        var bindings = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal);
        foreach (var (name, value) in guardValues)
            bindings[name] = () => value;

        var guards = new GuardEvaluator(bindings);
        var recorder = new RecordingActionDispatcher();
        var session = new Ax25Session(
            ctx, scheduler, recorder, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]          = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
                ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
                ["Connected"]             = DataLink_Connected.Transitions,
                ["AwaitingRelease"]       = DataLink_AwaitingRelease.Transitions,
                ["TimerRecovery"]         = DataLink_TimerRecovery.Transitions,
            },
            initialState: initialState);
        return (session, recorder, guards);
    }
}
