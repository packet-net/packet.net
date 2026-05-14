using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Integration tests: drive <see cref="Ax25Session"/> with the real
/// <see cref="ActionDispatcher"/> (not the recording stub from the smoke
/// tests) against synthetic transition tables that only use action verbs
/// the dispatcher currently implements.
/// </summary>
/// <remarks>
/// <para>
/// The figc4.x SDL transitions reference many verbs not yet wired —
/// subroutines (<c>Establish_Data_Link</c>), DL-layer signals
/// (<c>DL_CONNECT_indication</c>), outgoing U-frames (<c>UA</c>, <c>DM</c>),
/// and link-parameter assignments (<c>SRT</c>, <c>T1V</c>). Driving a
/// real-figure transition end-to-end through the real dispatcher will
/// remain blocked until those land.
/// </para>
/// <para>
/// What these tests prove now:
/// </para>
/// <list type="bullet">
///   <item><see cref="Ax25Session.PostEvent"/> dispatches to the real
///   <see cref="ActionDispatcher"/>, not just a recording stub.</item>
///   <item>Action verbs in <see cref="TransitionSpec.Actions"/> are
///   executed in order, with their side effects observable in
///   <see cref="Ax25SessionContext"/>.</item>
///   <item>Sequence-variable verbs wrap at the active modulus.</item>
///   <item>Timer verbs arm and cancel real timers via the scheduler.</item>
///   <item>Guards parse and gate transitions against context flags.</item>
/// </list>
/// </remarks>
public class Ax25SessionIntegrationTests
{
    private static (Ax25Session session,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    FakeTimeProvider time,
                    List<SupervisoryFrameSpec> sFrames) NewSession(
        IReadOnlyDictionary<string, IReadOnlyList<TransitionSpec>> transitionsByState,
        string initialState)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        var sFrames = new List<SupervisoryFrameSpec>();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { /* not driven in these tests */ },
            sendSFrame: sFrames.Add);
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);
        var guards = new GuardEvaluator(bindings);
        var session = new Ax25Session(ctx, scheduler, dispatcher, guards, transitionsByState, initialState);
        return (session, ctx, scheduler, time, sFrames);
    }

    private static TransitionSpec Tr(
        string id,
        string from,
        string on,
        string next,
        params (string verb, ActionKind kind)[] actions) =>
        new(
            Id: id,
            From: from,
            On: on,
            Guard: null,
            Actions: actions.Select(a => new ActionStep(a.verb, a.kind)).ToArray(),
            Next: next,
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>());

    /// <summary>
    /// Build a one-state transition table whose only event-handler is the
    /// supplied transition. Used to drive the dispatcher through a single
    /// transition without needing a full figc4.x machine.
    /// </summary>
    private static Dictionary<string, IReadOnlyList<TransitionSpec>> SingleStateMap(
        string state, params TransitionSpec[] transitions) =>
        new(StringComparer.Ordinal)
        {
            [state] = transitions,
        };

    [Fact]
    public void Transition_actions_mutate_context_in_order()
    {
        var t = Tr(
            id: "t01_synth",
            from: "S1",
            on: "DL_CONNECT_request",
            next: "S1",
            ("V(s) := 0",           ActionKind.Processing),
            ("V(r) := 0",           ActionKind.Processing),
            ("V(a) := 0",           ActionKind.Processing),
            ("RC := 0",             ActionKind.Processing),
            ("set_layer_3_initiated", ActionKind.Processing));

        var (session, ctx, _, _, _) = NewSession(SingleStateMap("S1", t), "S1");

        // Pre-seed with non-zero values to make the resets observable.
        ctx.VS = 5;
        ctx.VR = 6;
        ctx.VA = 7;
        ctx.RC = 9;
        ctx.Layer3Initiated = false;

        session.PostEvent(new DlConnectRequest());

        ctx.VS.Should().Be((byte)0);
        ctx.VR.Should().Be((byte)0);
        ctx.VA.Should().Be((byte)0);
        ctx.RC.Should().Be(0);
        ctx.Layer3Initiated.Should().BeTrue();
        session.CurrentState.Should().Be("S1");
    }

    [Fact]
    public void Increment_verbs_wrap_at_modulus_through_orchestrator()
    {
        var t = Tr(
            id: "t01_inc",
            from: "S1",
            on: "DL_CONNECT_request",
            next: "S1",
            ("V(s) := V(s) + 1", ActionKind.Processing),
            ("V(r) := V(r) + 1", ActionKind.Processing));

        var (session, ctx, _, _, _) = NewSession(SingleStateMap("S1", t), "S1");
        ctx.VS = 7;
        ctx.VR = 7;

        session.PostEvent(new DlConnectRequest());

        ctx.VS.Should().Be((byte)0, "mod-8 wrap on V(s)");
        ctx.VR.Should().Be((byte)0, "mod-8 wrap on V(r)");
    }

    [Fact]
    public void Timer_and_frame_actions_take_effect_through_orchestrator()
    {
        var t = Tr(
            id: "t01_send_rr",
            from: "S1",
            on: "DL_CONNECT_request",
            next: "S1",
            ("RC := 0",      ActionKind.Processing),
            ("start_T1",     ActionKind.Processing),
            ("RR_command",   ActionKind.SignalLower));

        var (session, ctx, scheduler, _, sFrames) = NewSession(SingleStateMap("S1", t), "S1");
        ctx.RC = 9;

        session.PostEvent(new DlConnectRequest());

        ctx.RC.Should().Be(0);
        scheduler.IsRunning("T1").Should().BeTrue();
        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(SupervisoryFrameType.Rr);
        sFrames[0].IsCommand.Should().BeTrue();
    }

    [Fact]
    public void Guard_gates_transition_against_context_flag()
    {
        // Two transitions on the same event, gated by `layer_3_initiated`.
        // The orchestrator's guard evaluator should pick the one whose
        // guard matches the current context state.
        var tYes = new TransitionSpec(
            Id: "t01_yes",
            From: "S1",
            On: "DL_CONNECT_request",
            Guard: "layer_3_initiated",
            Actions: new[] { new ActionStep("RC := 1", ActionKind.Processing) },
            Next: "S1",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>());

        var tNo = new TransitionSpec(
            Id: "t02_no",
            From: "S1",
            On: "DL_CONNECT_request",
            Guard: "not layer_3_initiated",
            Actions: new[] { new ActionStep("RC := 0", ActionKind.Processing) },
            Next: "S1",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>());

        // Layer-3 flag set → tYes should fire (RC = 1).
        {
            var (session, ctx, _, _, _) = NewSession(SingleStateMap("S1", tYes, tNo), "S1");
            ctx.Layer3Initiated = true;
            session.PostEvent(new DlConnectRequest());
            ctx.RC.Should().Be(1, "layer_3_initiated guard matched → tYes fired");
        }

        // Layer-3 flag clear → tNo should fire (RC = 0).
        {
            var (session, ctx, _, _, _) = NewSession(SingleStateMap("S1", tYes, tNo), "S1");
            ctx.Layer3Initiated = false;
            ctx.RC = 9;  // baseline
            session.PostEvent(new DlConnectRequest());
            ctx.RC.Should().Be(0, "not layer_3_initiated guard matched → tNo fired");
        }
    }

    [Fact]
    public void V_a_Assign_From_N_r_Reads_Incoming_Frame_Through_Orchestrator()
    {
        // End-to-end: an RR_received event carries an incoming frame whose
        // N(R) is 5. The transition's `V(a) := N(r)` action should pull
        // that 5 from the frame and stash it into ctx.VA.
        var t = Tr(
            id: "t01_va_from_nr",
            from: "S1",
            on: "RR_received",
            next: "S1",
            ("V(a) := N(r)", ActionKind.Processing));

        var (session, ctx, _, _, _) = NewSession(SingleStateMap("S1", t), "S1");

        // Build a mod-8 RR command with N(R)=5.
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = (5 << 5) | 0x01;
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();

        session.PostEvent(new RrReceived(frame!));

        ctx.VA.Should().Be((byte)5);
    }
}
