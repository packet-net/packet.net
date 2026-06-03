using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using SdlEvent = Packet.Ax25.Sdl.Ax25Event;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

public class Ax25SessionTests
{
    /// <summary>
    /// Hand-rolled fixture covering the four columns of figc4.4a's flow-control
    /// behaviour from the Connected state. Mirrors what a codegen-emitted
    /// transcription would produce, but inline here so the orchestrator's
    /// behaviour can be exercised without depending on any one *.sdl.yaml.
    /// </summary>
    private static readonly IReadOnlyList<TransitionSpec> ConnectedFlowControlTransitions = new TransitionSpec[]
    {
        new(
            Id: "t01_dl_flow_off_when_own_receiver_busy",
            From: "Connected",
            On: SdlEvent.DLFLOWOFFRequest,
            Guard: new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, false) },
            Actions: new ActionStep[]
            {
                new(Ax25ActionVerb.SetOwnReceiverBusy,      ActionKind.Processing),
                new(Ax25ActionVerb.RNRResponse,                ActionKind.SignalLower),
                new(Ax25ActionVerb.ClearAcknowledgePending,   ActionKind.Processing),
            },
            Next: "Connected",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>()),
        new(
            Id: "t02_dl_flow_off_when_own_receiver_not_busy",
            From: "Connected",
            On: SdlEvent.DLFLOWOFFRequest,
            Guard: new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, true) },
            Actions: Array.Empty<ActionStep>(),
            Next: "Connected",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>()),
        new(
            Id: "t03_dl_flow_on_when_own_receiver_not_busy",
            From: "Connected",
            On: SdlEvent.DLFLOWONRequest,
            Guard: new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, true) },
            Actions: Array.Empty<ActionStep>(),
            Next: "Connected",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>()),
        new(
            Id: "t04_dl_flow_on_when_busy_and_T1_not_running",
            From: "Connected",
            On: SdlEvent.DLFLOWONRequest,
            Guard: new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, false), new(Ax25Guard.T1Running, true) },
            Actions: new ActionStep[]
            {
                new(Ax25ActionVerb.ClearOwnReceiverBusy,    ActionKind.Processing),
                new(Ax25ActionVerb.RRCommand,                  ActionKind.SignalLower),
                new(Ax25ActionVerb.ClearAcknowledgePending,   ActionKind.Processing),
                new(Ax25ActionVerb.StopT3,                     ActionKind.Processing),
                new(Ax25ActionVerb.StartT1,                    ActionKind.Processing),
            },
            Next: "Connected",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>()),
        new(
            Id: "t05_dl_flow_on_when_busy_and_T1_running",
            From: "Connected",
            On: SdlEvent.DLFLOWONRequest,
            Guard: new GuardTerm[] { new(Ax25Guard.OwnReceiverBusy, false), new(Ax25Guard.T1Running, false) },
            Actions: new ActionStep[]
            {
                new(Ax25ActionVerb.ClearOwnReceiverBusy,    ActionKind.Processing),
                new(Ax25ActionVerb.RRCommand,                  ActionKind.SignalLower),
                new(Ax25ActionVerb.ClearAcknowledgePending,   ActionKind.Processing),
            },
            Next: "Connected",
            Notes: null,
            References: Array.Empty<ImplementationReference>(),
            Loops: Array.Empty<LoopRange>()),
    };

    private static (Ax25Session session,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    FakeTimeProvider time,
                    List<SupervisoryFrameSpec> sFrames,
                    List<Ax25Event> unhandled) NewConnectedSession()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var sFrames = new List<SupervisoryFrameSpec>();
        var unhandled = new List<Ax25Event>();
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { /* session would normally re-post as event */ },
            sendSFrame: sFrames.Add);
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler));
        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Connected"]       = ConnectedFlowControlTransitions,
                ["AwaitingRelease"] = Array.Empty<TransitionSpec>(),
            },
            initialState: "Connected",
            onUnhandledEvent: unhandled.Add);
        return (session, ctx, scheduler, time, sFrames, unhandled);
    }

    [Fact]
    public void Starts_In_The_Configured_Initial_State()
    {
        var (s, _, _, _, _, _) = NewConnectedSession();
        s.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public void DL_FLOW_OFF_When_Own_Receiver_Busy_Sends_RNR_And_Stays_Connected()
    {
        // Yes branch: actions fire when own_receiver_busy is already set.
        var (s, ctx, _, _, sFrames, _) = NewConnectedSession();
        ctx.OwnReceiverBusy = true;
        ctx.AcknowledgePending = true;

        s.PostEvent(new DlFlowOffRequest());

        s.CurrentState.Should().Be("Connected");
        ctx.OwnReceiverBusy.Should().BeTrue("set_own_receiver_busy was in the action chain");
        ctx.AcknowledgePending.Should().BeFalse("clear_acknowledge_pending fired");
        sFrames.Should().ContainSingle()
            .Which.Should().Be(new SupervisoryFrameSpec(SupervisoryFrameType.Rnr, IsCommand: false, Nr: ctx.VR, PfBit: false));
    }

    [Fact]
    public void DL_FLOW_OFF_When_Not_Busy_Is_A_No_Op()
    {
        var (s, ctx, _, _, sFrames, _) = NewConnectedSession();
        ctx.OwnReceiverBusy = false;
        ctx.AcknowledgePending = true;

        s.PostEvent(new DlFlowOffRequest());

        s.CurrentState.Should().Be("Connected");
        ctx.AcknowledgePending.Should().BeTrue("the no-op branch shouldn't touch the flag");
        sFrames.Should().BeEmpty();
    }

    [Fact]
    public void DL_FLOW_ON_When_Busy_And_T1_Not_Running_Sends_RR_And_Arms_T1()
    {
        var (s, ctx, scheduler, _, sFrames, _) = NewConnectedSession();
        ctx.OwnReceiverBusy = true;
        ctx.AcknowledgePending = true;
        scheduler.IsRunning("T1").Should().BeFalse("guard requires T1 not running");
        scheduler.IsRunning("T3").Should().BeFalse();

        s.PostEvent(new DlFlowOnRequest());

        s.CurrentState.Should().Be("Connected");
        ctx.OwnReceiverBusy.Should().BeFalse("clear_own_receiver_busy fired");
        ctx.AcknowledgePending.Should().BeFalse("clear_acknowledge_pending fired");
        sFrames.Should().ContainSingle()
            .Which.Should().Be(new SupervisoryFrameSpec(SupervisoryFrameType.Rr, IsCommand: true, Nr: ctx.VR, PfBit: false));
        scheduler.IsRunning("T1").Should().BeTrue("start_T1 fired");
        scheduler.IsRunning("T3").Should().BeFalse("stop_T3 fired");
    }

    [Fact]
    public void DL_FLOW_ON_When_Busy_And_T1_Running_Sends_RR_But_Leaves_Timers_Alone()
    {
        var (s, ctx, scheduler, _, sFrames, _) = NewConnectedSession();
        ctx.OwnReceiverBusy = true;
        scheduler.Arm("T1", TimeSpan.FromSeconds(1), () => { });
        scheduler.Arm("T3", TimeSpan.FromSeconds(1), () => { });

        s.PostEvent(new DlFlowOnRequest());

        sFrames.Should().ContainSingle()
            .Which.Type.Should().Be(SupervisoryFrameType.Rr);
        scheduler.IsRunning("T1").Should().BeTrue("we don't touch T1 when it's already running");
        scheduler.IsRunning("T3").Should().BeTrue();
        ctx.OwnReceiverBusy.Should().BeFalse();
    }

    [Fact]
    public void DL_FLOW_ON_When_Not_Busy_Is_A_No_Op()
    {
        var (s, ctx, _, _, sFrames, _) = NewConnectedSession();
        ctx.OwnReceiverBusy = false;

        s.PostEvent(new DlFlowOnRequest());

        s.CurrentState.Should().Be("Connected");
        sFrames.Should().BeEmpty();
    }

    [Fact]
    public void Unhandled_Event_Is_Reported_And_State_Is_Unchanged()
    {
        var (s, _, _, _, _, unhandled) = NewConnectedSession();
        var sabm = new SabmReceived(Ax25Frame.Ui(
            destination: new Callsign("M0LTE", 0),
            source:      new Callsign("G7XYZ", 7),
            info:        "x"u8));

        s.PostEvent(sabm);

        s.CurrentState.Should().Be("Connected");
        unhandled.Should().ContainSingle().Which.Should().BeSameAs(sabm);
    }

    [Fact]
    public void Unknown_Initial_State_Throws()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        var dispatcher = new ActionDispatcher(_ => { }, _ => { });
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler));

        var act = () => new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Connected"] = ConnectedFlowControlTransitions,
            },
            initialState: "NoSuchState");
        act.Should().Throw<ArgumentException>().WithMessage("*NoSuchState*");
    }
}
