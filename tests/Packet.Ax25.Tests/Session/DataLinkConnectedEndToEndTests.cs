using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests against the real figc4.4 (Connected)
/// transition table. Covers the transitions that don't depend on
/// session-loop machinery for the I-frame queue (the
/// <c>I_frame_pops_off_queue</c> event mechanism is a future PR).
/// </summary>
public class DataLinkConnectedEndToEndTests
{
    private static (Ax25Session session,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    List<SupervisoryFrameSpec> sFrames,
                    List<UFrameSpec> uFrames,
                    List<UiFrameSpec> uiFrames,
                    List<DataLinkSignal> upward,
                    List<LinkMultiplexerSignal> linkMux,
                    List<InternalSignal> internalSignals,
                    List<string> subroutineCalls) NewRig(
        bool ownReceiverBusy = false,
        bool peerReceiverBusy = false,
        bool ackPending = false,
        bool t1Running = false,
        bool pEq1 = false,
        bool fEq1 = false,
        bool pOrFEq1 = false,
        bool command = false,
        bool nrInWindow = true,
        bool nsEqVr = false,
        bool infoFieldValid = true,
        bool vaLeNrLeVs = true)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            OwnReceiverBusy    = ownReceiverBusy,
            PeerReceiverBusy   = peerReceiverBusy,
            AcknowledgePending = ackPending,
        };
        if (t1Running) scheduler.Arm("T1", TimeSpan.FromSeconds(1), () => { });

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
            ["P_eq_1"]            = () => pEq1,
            ["F_eq_1"]             = () => fEq1,
            ["P_or_F_eq_1"]        = () => pOrFEq1,
            ["command"]            = () => command,
            ["nr_in_window"]       = () => nrInWindow,
            ["N_s_eq_V_r"]         = () => nsEqVr,
            ["info_field_valid"]   = () => infoFieldValid,
            ["V_a_le_N_r_le_V_s"]  = () => vaLeNrLeVs,
            ["version_2_2"]        = () => false,
            ["srej_exception_gt_0"] = () => false,
            ["N_s_gt_V_r_plus_1"]  = () => false,
            ["V_s_eq_V_a_plus_k"]  = () => false,
            ["V_s_eq_V_a"]         = () => false,
        };
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]          = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
                ["AwaitingV22Connection"]  = DataLink_AwaitingV22Connection.Transitions,
                ["AwaitingRelease"]       = DataLink_AwaitingRelease.Transitions,
                ["Connected"]             = DataLink_Connected.Transitions,
            },
            initialState: "Connected");

        return (session, ctx, scheduler, sFrames, uFrames, uiFrames, upward, linkMux, internalSignals, subroutineCalls);
    }

    [Fact]
    public void t01_DL_DISCONNECT_request_Drains_Queue_Sends_DISC_Cycles_T1_T3_Transitions_To_AwaitingRelease()
    {
        var (s, ctx, scheduler, _, uFrames, _, _, _, _, _) = NewRig();
        ctx.IFrameQueue.Enqueue((new byte[] { 1, 2 }, Ax25Frame.PidNoLayer3));
        ctx.RC = 9;
        scheduler.Arm("T3", TimeSpan.FromSeconds(1), () => { });

        s.PostEvent(new DlDisconnectRequest());

        s.CurrentState.Should().Be("AwaitingRelease");
        ctx.IFrameQueue.Should().BeEmpty();
        ctx.RC.Should().Be(0);
        uFrames.Should().ContainSingle();
        uFrames[0].Type.Should().Be(UFrameType.Disc);
        uFrames[0].PfBit.Should().BeTrue();
        scheduler.IsRunning("T3").Should().BeFalse();
        scheduler.IsRunning("T1").Should().BeTrue();
    }

    [Fact]
    public void DL_FLOW_OFF_request_When_Busy_Branch_Sends_RNR_Response()
    {
        // figc4.4 t24's branch-Yes path: own_receiver_busy = true →
        // set_own_receiver_busy; RNR_response; clear_acknowledge_pending.
        var (s, ctx, _, sFrames, _, _, _, _, _, _) = NewRig(
            ownReceiverBusy: true, ackPending: true);
        ctx.VR = 4;

        s.PostEvent(new DlFlowOffRequest());

        s.CurrentState.Should().Be("Connected");
        ctx.OwnReceiverBusy.Should().BeTrue();
        ctx.AcknowledgePending.Should().BeFalse();
        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(SupervisoryFrameType.Rnr);
        sFrames[0].IsCommand.Should().BeFalse();
        sFrames[0].Nr.Should().Be((byte)4, "Pending.Nr unset → defaults to ctx.VR");
    }

    [Fact]
    public void DL_FLOW_ON_request_When_Busy_T1_Not_Running_Sends_RR_Command_Starts_T1_Stops_T3()
    {
        // figc4.4 t26: own_receiver_busy && !T1_running →
        // clear_own_receiver_busy; RR_command; clear_acknowledge_pending; stop_T3; start_T1.
        var (s, ctx, scheduler, sFrames, _, _, _, _, _, _) = NewRig(
            ownReceiverBusy: true, ackPending: true, t1Running: false);
        scheduler.Arm("T3", TimeSpan.FromSeconds(1), () => { });

        s.PostEvent(new DlFlowOnRequest());

        s.CurrentState.Should().Be("Connected");
        ctx.OwnReceiverBusy.Should().BeFalse();
        ctx.AcknowledgePending.Should().BeFalse();
        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(SupervisoryFrameType.Rr);
        sFrames[0].IsCommand.Should().BeTrue();
        scheduler.IsRunning("T3").Should().BeFalse();
        scheduler.IsRunning("T1").Should().BeTrue();
    }

    [Fact]
    public void DL_DATA_request_Enqueues_Then_Session_Loop_Pops_And_Emits_I_Frame()
    {
        // figc4.4 t18 enqueues; the session-loop drain pops and posts a
        // synthetic I_frame_pops_off_queue event, which figc4.4 t20 handles
        // (peer_receiver_busy=No, V_s_eq_V_a_plus_k=No, !T1_running) by
        // emitting an I_command and incrementing V(s).
        var (s, ctx, _, _, _, _, _, _, internalSignals, _) = NewRig();
        var payload = "data-to-send"u8.ToArray();
        ctx.VS = 0;
        ctx.VA = 0;
        ctx.VR = 2;  // observable as the emitted N(R)

        s.PostEvent(new DlDataRequest(payload));

        s.CurrentState.Should().Be("Connected");
        ctx.IFrameQueue.Should().BeEmpty("the session loop drained the queue");
        internalSignals.Should().ContainSingle()
            .Which.Should().BeOfType<PushIFrameQueueSignal>();
        ctx.VS.Should().Be((byte)1, "V(s) := V(s) + 1 after I_command");
        ctx.SentIFrames.Should().ContainKey((byte)0,
            "the sent frame is stashed for retransmit, keyed by its N(s) = pre-increment V(s)");
    }

    [Fact]
    public void Control_Field_Error_V20_Branch_Raises_DL_ERROR_L_Discards_Queue_Calls_Establish_Data_Link_Goes_To_AwaitingConnection()
    {
        // figc4.4 t33: control_field_error + version_2_2=No branch →
        // DL_ERROR(L); discard_I_frame_queue; Establish_Data_Link;
        // set_layer_3_initiated; → AwaitingConnection.
        var (s, ctx, _, _, _, _, upward, _, _, subroutineCalls) = NewRig();
        ctx.IFrameQueue.Enqueue((new byte[] { 1 }, Ax25Frame.PidNoLayer3));

        s.PostEvent(new ControlFieldError());

        s.CurrentState.Should().Be("AwaitingConnection");
        upward.Should().ContainSingle()
            .Which.Should().BeOfType<DataLinkErrorIndication>()
            .Which.Code.Should().Be("L");
        ctx.IFrameQueue.Should().BeEmpty();
        subroutineCalls.Should().ContainSingle().Which.Should().Be("Establish_Data_Link");
        ctx.Layer3Initiated.Should().BeTrue();
    }

    [Fact]
    public void Two_DL_DATA_requests_Get_Sent_Sequentially_Through_Session_Loop()
    {
        var (s, ctx, _, _, _, _, _, _, _, _) = NewRig();
        var iFrames = new List<IFrameSpec>();
        // Replace dispatcher with one that captures I-frames (the default
        // rig's NewRig doesn't capture them).
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx2 = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            VR = 3,
        };
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendIFrame: iFrames.Add);
        var bindings = new Dictionary<string, Func<bool>>(
            Ax25SessionBindings.CreateDefault(ctx2, scheduler), StringComparer.Ordinal)
        {
            ["P_eq_1"]            = () => false,
            ["F_eq_1"]             = () => false,
            ["P_or_F_eq_1"]        = () => false,
            ["command"]            = () => false,
            ["nr_in_window"]       = () => true,
            ["N_s_eq_V_r"]         = () => false,
            ["info_field_valid"]   = () => true,
            ["V_a_le_N_r_le_V_s"]  = () => true,
            ["version_2_2"]        = () => false,
            ["srej_exception_gt_0"] = () => false,
            ["N_s_gt_V_r_plus_1"]  = () => false,
            ["V_s_eq_V_a_plus_k"]  = () => false,
            ["V_s_eq_V_a"]         = () => false,
        };
        var guards = new GuardEvaluator(bindings);
        var session = new Ax25Session(
            ctx2, scheduler, dispatcher, guards,
            new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Connected"] = DataLink_Connected.Transitions,
                ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
                ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
            },
            initialState: "Connected");

        session.PostEvent(new DlDataRequest("first"u8.ToArray()));
        session.PostEvent(new DlDataRequest("second"u8.ToArray()));

        iFrames.Should().HaveCount(2);
        iFrames[0].Ns.Should().Be((byte)0);
        iFrames[0].Nr.Should().Be((byte)3, "N(R) = V(R)");
        iFrames[0].Info.ToArray().Should().Equal("first"u8.ToArray());
        iFrames[1].Ns.Should().Be((byte)1, "second I-frame has incremented N(S)");
        iFrames[1].Info.ToArray().Should().Equal("second"u8.ToArray());

        ctx2.VS.Should().Be((byte)2, "two emissions, V(s) advanced twice");
        ctx2.SentIFrames.Should().HaveCount(2, "both frames stashed for retransmit");
    }

    [Fact]
    public void Session_Loop_Stops_Draining_When_Peer_Goes_Busy()
    {
        var (s, ctx, _, _, _, _, _, _, _, _) = NewRig(peerReceiverBusy: true);

        s.PostEvent(new DlDataRequest("blocked"u8.ToArray()));

        ctx.IFrameQueue.Should().HaveCount(1, "peer is busy — queue stays full");
        ctx.VS.Should().Be((byte)0, "no I-frame emitted");
    }

    [Fact]
    public void LM_Seize_Confirm_Ack_Pending_Calls_Enquiry_Response_F_0_Subroutine()
    {
        // figc4.4 t59: LM_SEIZE_confirm + ack_pending=Yes →
        // clear_acknowledge_pending; Enquiry_Response_F_0 subroutine;
        // LM_release_request.
        var (s, ctx, _, _, _, _, _, linkMux, _, subroutineCalls) = NewRig(
            ackPending: true);

        s.PostEvent(new LmSeizeConfirm());

        s.CurrentState.Should().Be("Connected");
        ctx.AcknowledgePending.Should().BeFalse();
        subroutineCalls.Should().ContainSingle().Which.Should().Be("Enquiry_Response_F_0");
        linkMux.Should().ContainSingle().Which.Should().BeOfType<LinkMultiplexerReleaseRequest>();
    }
}
