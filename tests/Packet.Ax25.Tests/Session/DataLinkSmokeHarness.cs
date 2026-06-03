using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using SdlEvent = Packet.Ax25.Sdl.Ax25Event;

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
/// routing, guard evaluation, action order/kind, and next-state — but not whether
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
        // uniqueness check evaluate their guards meaningfully.
        var assignment = table
            .SelectMany(t => GuardsThatSatisfy(t.Guard).Keys)
            .ToHashSet()
            .ToDictionary(a => a, _ => false);
        foreach (var (atom, value) in GuardsThatSatisfy(transition.Guard))
            assignment[atom] = value;

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
    /// Reduce a typed guard (a conjunction of <see cref="GuardTerm"/>s) to an
    /// <c>atom → value</c> map that satisfies it: each term's atom must equal
    /// <c>!Negate</c> for the conjunction to hold. The data-link figures only
    /// ever compose atoms with <c>and</c> / <c>not</c> (no top-level <c>or</c>,
    /// which the package's GuardExpression parser refuses anyway), and each guard
    /// spells out the full root-to-leaf decision path, so setting exactly its
    /// atoms makes only that leaf true. A <c>null</c> / empty guard contributes
    /// nothing (unguarded).
    /// </summary>
    private static Dictionary<Ax25Guard, bool> GuardsThatSatisfy(IReadOnlyList<GuardTerm>? guard)
    {
        var result = new Dictionary<Ax25Guard, bool>();
        if (guard is null) return result;
        foreach (var term in guard)
            result[term.Atom] = !term.Negate;
        return result;
    }

    // ─── Event factory ─────────────────────────────────────────────────

    /// <summary>
    /// Construct the runtime <see cref="Ax25Event"/> for a typed
    /// <see cref="SdlEvent"/>. The orchestrator routes by event type and the
    /// smoke harness stubs every guard atom, so frame contents are immaterial —
    /// received-frame events all carry a stand-in UI frame. Covers the union of
    /// <c>on</c> events across all six data-link pages; an unmapped event throws
    /// so a new upstream event surfaces.
    /// </summary>
    private static Ax25Event EventFor(SdlEvent on) => on switch
    {
        SdlEvent.DLDISCONNECTRequest                  => new DlDisconnectRequest(),
        SdlEvent.DLCONNECTRequest                     => new DlConnectRequest(),
        SdlEvent.DLDATARequest                        => new DlDataRequest("x"u8.ToArray()),
        SdlEvent.DLUNITDATARequest                    => new DlUnitDataRequest("x"u8.ToArray()),
        SdlEvent.DLFLOWOFFRequest                     => new DlFlowOffRequest(),
        SdlEvent.DLFLOWONRequest                      => new DlFlowOnRequest(),
        SdlEvent.IFramePopsOffQueue                   => new IFramePopsOffQueue("x"u8.ToArray()),
        SdlEvent.IReceived                            => new IFrameReceived(Frame()),
        SdlEvent.RRReceived                           => new RrReceived(Frame()),
        SdlEvent.RNRReceived                          => new RnrReceived(Frame()),
        SdlEvent.REJReceived                          => new RejReceived(Frame()),
        SdlEvent.SREJReceived                         => new SrejReceived(Frame()),
        SdlEvent.SABMReceived                         => new SabmReceived(Frame()),
        SdlEvent.SABMEReceived                        => new SabmeReceived(Frame()),
        SdlEvent.DISCReceived                         => new DiscReceived(Frame()),
        SdlEvent.UAReceived                           => new UaReceived(Frame()),
        SdlEvent.DMReceived                           => new DmReceived(Frame()),
        SdlEvent.FRMRReceived                         => new FrmrReceived(Frame()),
        SdlEvent.UIReceived                           => new UiReceived(Frame()),
        SdlEvent.IOrSCommandReceived                  => new IOrSCommandReceived(Frame()),
        SdlEvent.AllOtherCommands                     => new AllOtherCommands(Frame()),
        SdlEvent.LMSEIZEConfirm                       => new LmSeizeConfirm(),
        SdlEvent.T1Expiry                             => new T1Expiry(),
        SdlEvent.T3Expiry                             => new T3Expiry(),
        SdlEvent.ControlFieldError                    => new ControlFieldError(),
        SdlEvent.InfoNotPermittedInFrame              => new InfoNotPermittedInFrame(),
        SdlEvent.UOrSFrameLengthError                 => new UOrSFrameLengthError(),
        SdlEvent.AllOtherPrimitivesFromLowerLayer     => new AllOtherPrimitivesFromLowerLayer(),
        SdlEvent.AllOtherPrimitivesFromUpperLayer     => new AllOtherPrimitivesFromUpperLayer(),
        _ => throw new InvalidOperationException($"no event factory for on='{on}' (add a case)"),
    };

    /// <summary>Stand-in frame for received-frame events; the orchestrator routes
    /// by event type and the harness stubs every guard, so contents don't matter.</summary>
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
        Dictionary<Ax25Guard, bool> guardValues, string initialState)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        // Start from the runtime defaults (so timer-state / counter atoms have
        // sensible bindings) then override with the atoms named by this page.
        var bindings = new Dictionary<Ax25Guard, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler));
        foreach (var (atom, value) in guardValues)
            bindings[atom] = () => value;

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
