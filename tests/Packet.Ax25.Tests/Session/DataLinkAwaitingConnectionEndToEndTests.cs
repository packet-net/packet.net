using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests against the real figc4.2 (Awaiting
/// Connection) transition table — same shape as
/// <see cref="DataLinkDisconnectedEndToEndTests"/>.
/// </summary>
public class DataLinkAwaitingConnectionEndToEndTests
{
    private static (Ax25Session session,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    FakeTimeProvider time,
                    List<SupervisoryFrameSpec> sFrames,
                    List<UFrameSpec> uFrames,
                    List<UiFrameSpec> uiFrames,
                    List<DataLinkSignal> upward,
                    List<LinkMultiplexerSignal> linkMux,
                    List<InternalSignal> internalSignals,
                    List<string> subroutineCalls) NewRig(
        bool fEq1 = false,
        bool layer3Initiated = false,
        bool vsEqVa = false,
        bool rcEqN2 = false,
        bool dataLayer3Initiated = false,
        bool pEq1 = false)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };

        var sFrames = new List<SupervisoryFrameSpec>();
        var uFrames = new List<UFrameSpec>();
        var uiFrames = new List<UiFrameSpec>();
        var upward = new List<DataLinkSignal>();
        var linkMux = new List<LinkMultiplexerSignal>();
        var internalSignals = new List<InternalSignal>();
        var subroutineCalls = new List<string>();

        var registry = new DefaultSubroutineRegistry();
        foreach (var name in DefaultSubroutineRegistry.KnownSubroutines)
        {
            var captured = name;
            registry.Register(captured, _ => subroutineCalls.Add(captured));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    sFrames.Add,
            sendUFrame:    uFrames.Add,
            sendUiFrame:   uiFrames.Add,
            sendUpward:    upward.Add,
            sendLinkMux:   linkMux.Add,
            sendInternal:  internalSignals.Add,
            subroutines:   registry);

        var bindings = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
        {
            ["F_eq_1"]            = () => fEq1,
            ["P_eq_1"]            = () => pEq1,
            ["V_s_eq_V_a"]        = () => vsEqVa,
            ["RC_eq_N2"]          = () => rcEqN2,
            // `layer_3_initiated` and several others are bound via the default
            // table to the ctx flag — but the figc4.2 decisions intentionally
            // distinguish "the layer_3_initiated branch of UA decision" vs
            // "the data_layer_3_initiated branch" — both have the same
            // canonical predicate name. Override to make them controllable.
        };
        // The default bindings expose `layer_3_initiated` reading from
        // ctx.Layer3Initiated — set the context flag and the bindings see it.
        ctx.Layer3Initiated = layer3Initiated;
        // `data_layer_3_initiated` decision id uses the same predicate
        // (layer_3_initiated) but figc4.2 has two distinct paths; the YAML's
        // canonical predicate is just `layer_3_initiated`. dataLayer3Initiated
        // parameter is reserved here for symmetry but currently maps to the
        // same flag.
        _ = dataLayer3Initiated;
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]       = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
                ["Connected"]          = DataLink_Connected.Transitions,
            },
            initialState: "AwaitingConnection");

        return (session, ctx, scheduler, time, sFrames, uFrames, uiFrames, upward, linkMux, internalSignals, subroutineCalls);
    }

    private static Ax25Frame DiscFrame(bool pollBit)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = (byte)(0x43 | (pollBit ? 0x10 : 0));  // DISC = 0x43, P bit = 0x10
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    private static Ax25Frame DmOrUaFrame(byte control)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = control;
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public void t01_DISC_Received_Emits_DM_With_F_Equal_Incoming_P()
    {
        var (s, _, _, _, _, uFrames, _, _, _, _, _) = NewRig();

        s.PostEvent(new DiscReceived(DiscFrame(pollBit: true)));

        s.CurrentState.Should().Be("AwaitingConnection");
        uFrames.Should().ContainSingle();
        var u = uFrames[0];
        u.Type.Should().Be(UFrameType.Dm);
        u.PfBit.Should().BeTrue("F := P read P=1 from incoming DISC");
    }

    [Fact]
    public void t02_DM_Received_F_Eq_1_Discards_Queue_Notifies_Upper_Stops_T1_Returns_To_Disconnected()
    {
        var (s, ctx, scheduler, _, _, _, _, upward, _, _, _) = NewRig(fEq1: true);
        ctx.IFrameQueue.Enqueue((new byte[] { 1, 2 }, Ax25Frame.PidNoLayer3));
        scheduler.Arm("T1", TimeSpan.FromSeconds(1), () => { });

        // DM control = 0x0F, F=1 → 0x1F
        s.PostEvent(new DmReceived(DmOrUaFrame(0x1F)));

        s.CurrentState.Should().Be("Disconnected");
        ctx.IFrameQueue.Should().BeEmpty("discard_frame_queue fired");
        upward.Should().ContainSingle()
            .Which.Should().BeOfType<DataLinkDisconnectIndication>();
        scheduler.IsRunning("T1").Should().BeFalse("stop_T1 fired");
    }

    [Fact]
    public void t05_UA_Received_F_Eq_1_Layer_3_Initiated_Transitions_To_Connected()
    {
        var (s, ctx, scheduler, _, _, _, _, upward, _, _, subroutineCalls) = NewRig(
            fEq1: true, layer3Initiated: true);
        scheduler.Arm("T1", TimeSpan.FromSeconds(1), () => { });
        // Pre-corrupt sequence variables so the V(s)/V(r)/V(a) := 0 actions are observable.
        ctx.VS = 5;
        ctx.VR = 5;
        ctx.VA = 5;

        // UA control = 0x63, F=1 → 0x73
        s.PostEvent(new UaReceived(DmOrUaFrame(0x73)));

        s.CurrentState.Should().Be("Connected");
        upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkConnectConfirm>();
        scheduler.IsRunning("T1").Should().BeFalse();
        scheduler.IsRunning("T3").Should().BeTrue();
        ctx.VS.Should().Be((byte)0);
        ctx.VR.Should().Be((byte)0);
        ctx.VA.Should().Be((byte)0);
        subroutineCalls.Should().ContainSingle().Which.Should().Be("Select_T1_Value");
    }

    [Fact]
    public void t08_T1_Expiry_RC_Eq_N2_Gives_Up_And_Returns_To_Disconnected()
    {
        var (s, ctx, _, _, _, _, _, upward, _, _, _) = NewRig(rcEqN2: true);
        ctx.IFrameQueue.Enqueue((new byte[] { 1 }, Ax25Frame.PidNoLayer3));

        s.PostEvent(new T1Expiry());

        s.CurrentState.Should().Be("Disconnected");
        ctx.IFrameQueue.Should().BeEmpty();
        upward.Should().HaveCount(2);
        upward[0].Should().BeOfType<DataLinkErrorIndication>().Which.Code.Should().Be("G");
        upward[1].Should().BeOfType<DataLinkDisconnectIndication>();
    }

    [Fact]
    public void t10_All_Other_Primitives_From_Upper_Layer_Is_No_Op_Stays_AwaitingConnection()
    {
        var (s, _, _, _, _, _, _, _, _, _, _) = NewRig();

        s.PostEvent(new AllOtherPrimitivesFromUpperLayer());

        s.CurrentState.Should().Be("AwaitingConnection");
    }

    [Fact]
    public void t12_DL_UNIT_DATA_request_Emits_UI_Command_While_Awaiting_Connection()
    {
        var (s, _, _, _, _, _, uiFrames, _, _, _, _) = NewRig();
        var payload = "ui-while-connecting"u8.ToArray();

        s.PostEvent(new DlUnitDataRequest(payload, Pid: Ax25Frame.PidNoLayer3));

        s.CurrentState.Should().Be("AwaitingConnection");
        uiFrames.Should().ContainSingle();
        uiFrames[0].Info.ToArray().Should().Equal(payload);
    }
}
