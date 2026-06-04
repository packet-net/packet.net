using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

public class ActionDispatcherTests
{
    private static (ActionDispatcher dispatcher,
                    Ax25SessionContext ctx,
                    SystemTimerScheduler scheduler,
                    FakeTimeProvider time,
                    List<string> timerExpiries,
                    List<SupervisoryFrameSpec> sFrames,
                    List<UFrameSpec> uFrames,
                    List<UiFrameSpec> uiFrames,
                    List<DataLinkSignal> upward,
                    List<LinkMultiplexerSignal> linkMux,
                    List<InternalSignal> internalSignals) NewRig()
    {
        var timerExpiries = new List<string>();
        var sFrames = new List<SupervisoryFrameSpec>();
        var uFrames = new List<UFrameSpec>();
        var uiFrames = new List<UiFrameSpec>();
        var upward = new List<DataLinkSignal>();
        var linkMux = new List<LinkMultiplexerSignal>();
        var internalSignals = new List<InternalSignal>();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: timerExpiries.Add,
            sendSFrame: sFrames.Add,
            sendUFrame: uFrames.Add,
            sendUiFrame: uiFrames.Add,
            sendUpward: upward.Add,
            sendLinkMux: linkMux.Add,
            sendInternal: internalSignals.Add);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        return (dispatcher, ctx, scheduler, time, timerExpiries, sFrames, uFrames, uiFrames, upward, linkMux, internalSignals);
    }

    // ─── Flag mutations ────────────────────────────────────────────────

    [Fact]
    public void Set_Own_Receiver_Busy_Sets_The_Flag()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.OwnReceiverBusy.Should().BeFalse();
        d.Execute(Ax25ActionVerb.SetOwnReceiverBusy, ctx, s);
        ctx.OwnReceiverBusy.Should().BeTrue();
    }

    [Fact]
    public void Clear_Own_Receiver_Busy_Clears_The_Flag()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.OwnReceiverBusy = true;
        d.Execute(Ax25ActionVerb.ClearOwnReceiverBusy, ctx, s);
        ctx.OwnReceiverBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(Ax25ActionVerb.SetAcknowledgePending,   nameof(Ax25SessionContext.AcknowledgePending), true)]
    [InlineData(Ax25ActionVerb.ClearAcknowledgePending, nameof(Ax25SessionContext.AcknowledgePending), false)]
    [InlineData(Ax25ActionVerb.SetLayer3Initiated,     nameof(Ax25SessionContext.Layer3Initiated),    true)]
    [InlineData(Ax25ActionVerb.ClearLayer3Initiated,   nameof(Ax25SessionContext.Layer3Initiated),    false)]
    [InlineData(Ax25ActionVerb.SetPeerReceiverBusy,    nameof(Ax25SessionContext.PeerReceiverBusy),   true)]
    [InlineData(Ax25ActionVerb.ClearPeerReceiverBusy,  nameof(Ax25SessionContext.PeerReceiverBusy),   false)]
    public void Flag_Verbs_Mutate_The_Right_Field(Ax25ActionVerb action, string fieldName, bool expectedValue)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        // Set the opposite to make the change observable
        typeof(Ax25SessionContext).GetProperty(fieldName)!.SetValue(ctx, !expectedValue);

        d.Execute(action, ctx, s);

        typeof(Ax25SessionContext).GetProperty(fieldName)!.GetValue(ctx).Should().Be(expectedValue);
    }

    // ─── Timer operations ──────────────────────────────────────────────

    // Only T1 and T3 have canonical SDL verbs (StartT1/StopT1/StartT3/StopT3);
    // T2 has no Ax25ActionVerb member — no SDL figure arms or cancels it, so the
    // dispatcher exposes no T2 timer verb to test.
    [Theory]
    [InlineData(Ax25ActionVerb.StartT1, "T1")]
    [InlineData(Ax25ActionVerb.StartT3, "T3")]
    public void Start_Timer_Arms_The_Named_Timer(Ax25ActionVerb action, string timerName)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        d.Execute(action, ctx, s);
        s.IsRunning(timerName).Should().BeTrue();
    }

    [Theory]
    [InlineData(Ax25ActionVerb.StartT1, Ax25ActionVerb.StopT1, "T1")]
    [InlineData(Ax25ActionVerb.StartT3, Ax25ActionVerb.StopT3, "T3")]
    public void Stop_Timer_Cancels_The_Named_Timer(Ax25ActionVerb startAction, Ax25ActionVerb stopAction, string timerName)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        // Arm first so cancel has something to clear
        d.Execute(startAction, ctx, s);
        s.IsRunning(timerName).Should().BeTrue();

        d.Execute(stopAction, ctx, s);
        s.IsRunning(timerName).Should().BeFalse();
    }

    [Fact]
    public void Timer_Expiry_Calls_The_Configured_Callback_With_The_Timer_Name()
    {
        var (d, ctx, s, time, expiries, _, _, _, _, _, _) = NewRig();
        d.Execute(Ax25ActionVerb.StartT1, ctx, s);

        // start_T1 reads ctx.T1V (default 6s) — not dispatcher.T1Duration.
        time.Advance(ctx.T1V);

        expiries.Should().ContainSingle().Which.Should().Be("T1");
    }

    [Fact]
    public void Start_T1_Uses_Per_Session_T1V_Not_Dispatcher_Default()
    {
        var (d, ctx, s, time, expiries, _, _, _, _, _, _) = NewRig();
        // Set a custom T1V via the SDL verb chain that figc4.1 t03 fires.
        ctx.Srt = TimeSpan.FromMilliseconds(1500);
        d.Execute(Ax25ActionVerb.T1VAssign2TimesSRT, ctx, s);

        d.Execute(Ax25ActionVerb.StartT1, ctx, s);

        ctx.T1V.Should().Be(TimeSpan.FromMilliseconds(3000));
        s.IsRunning("T1").Should().BeTrue();
        // Advance just shy of expiry — should still be running.
        time.Advance(TimeSpan.FromMilliseconds(2999));
        expiries.Should().BeEmpty();
        time.Advance(TimeSpan.FromMilliseconds(1));
        expiries.Should().ContainSingle().Which.Should().Be("T1");
    }

    [Fact]
    public void Default_Timer_Durations_Match_The_Spec_Defaults()
    {
        var (d, _, _, _, _, _, _, _, _, _, _) = NewRig();
        // T1 default 3000 ms (XID PI=9 default), T3 default chosen per §6.7.1.3.
        d.T1Duration.Should().Be(TimeSpan.FromMilliseconds(3000));
        d.T2Duration.Should().Be(TimeSpan.FromMilliseconds(1500));
        d.T3Duration.Should().Be(TimeSpan.FromMilliseconds(30000));
    }

    // ─── Queue operations ──────────────────────────────────────────────

    [Fact]
    public void Discard_I_Frame_Queue_Empties_The_Queue()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.IFrameQueue.Enqueue((new byte[] { 1 }, Ax25Frame.PidNoLayer3));
        ctx.IFrameQueue.Enqueue((new byte[] { 2 }, Ax25Frame.PidNoLayer3));
        ctx.IFrameQueue.Should().HaveCount(2);

        d.Execute(Ax25ActionVerb.DiscardIFrameQueue, ctx, s);

        ctx.IFrameQueue.Should().BeEmpty();
    }

    // ─── Supervisory-frame transmissions ───────────────────────────────

    [Theory]
    [InlineData(Ax25ActionVerb.RRCommand,   SupervisoryFrameType.Rr,   true)]
    [InlineData(Ax25ActionVerb.RR,           SupervisoryFrameType.Rr,   false)]
    [InlineData(Ax25ActionVerb.RNRResponse, SupervisoryFrameType.Rnr,  false)]
    [InlineData(Ax25ActionVerb.REJ,          SupervisoryFrameType.Rej,  false)]
    [InlineData(Ax25ActionVerb.SREJ,         SupervisoryFrameType.Srej, false)]
    public void Supervisory_Verbs_Signal_Outgoing_Frame_With_Right_Type_And_Role(
        Ax25ActionVerb action, SupervisoryFrameType expectedType, bool expectedIsCommand)
    {
        var (d, ctx, s, _, _, sFrames, _, _, _, _, _) = NewRig();
        d.Execute(action, ctx, s);

        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(expectedType);
        sFrames[0].IsCommand.Should().Be(expectedIsCommand);
    }

    [Fact]
    public void Supervisory_Verb_Consumes_Pending_Nr_And_PfBit()
    {
        var (d, ctx, s, _, _, sFrames, _, _, _, _, _) = NewRig();
        ctx.VR = 4;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        // Mimic a figc4.4-style RR response chain: set F, set N(r), emit RR.
        d.Execute(new[] { Ax25ActionVerb.FAssign1, Ax25ActionVerb.NRAssignVR, Ax25ActionVerb.RR }, tx);

        sFrames.Should().ContainSingle();
        sFrames[0].Should().Be(new SupervisoryFrameSpec(
            SupervisoryFrameType.Rr, IsCommand: false, Nr: 4, PfBit: true));
    }

    [Fact]
    public void Supervisory_Verb_Defaults_Nr_To_VR_And_PfBit_False_When_Pending_Unset()
    {
        // figc4.4 t23 (DL_FLOW_OFF, own_receiver_busy=Yes) draws bare
        // `set_own_receiver_busy; RNR_response; clear_acknowledge_pending`
        // with no N(R) or F-bit setup before the frame. The dispatcher
        // applies the spec-implicit defaults: Nr = V(R), PfBit = false.
        var (d, ctx, s, _, _, sFrames, _, _, _, _, _) = NewRig();
        ctx.VR = 5;
        d.Execute(Ax25ActionVerb.RNRResponse, ctx, s);

        sFrames.Should().ContainSingle();
        sFrames[0].Should().Be(new SupervisoryFrameSpec(
            SupervisoryFrameType.Rnr, IsCommand: false, Nr: 5, PfBit: false));
    }

    // ─── Unnumbered-frame transmissions ────────────────────────────────

    [Theory]
    [InlineData(Ax25ActionVerb.UA,              UFrameType.Ua,    false, false, false)]
    [InlineData(Ax25ActionVerb.DM,              UFrameType.Dm,    false, false, false)]
    [InlineData(Ax25ActionVerb.ExpeditedUA,    UFrameType.Ua,    false, false, true)]
    [InlineData(Ax25ActionVerb.ExpeditedDM,    UFrameType.Dm,    false, false, true)]
    [InlineData(Ax25ActionVerb.DMFEq1,      UFrameType.Dm,    false, true,  false)]
    [InlineData(Ax25ActionVerb.SABMPEqEq1,   UFrameType.Sabm,  true,  true,  false)]
    [InlineData(Ax25ActionVerb.SABMEPEq1,   UFrameType.Sabme, true,  true,  false)]
    [InlineData(Ax25ActionVerb.DISCPEq1,    UFrameType.Disc,  true,  true,  false)]
    public void Unnumbered_Verbs_Signal_Outgoing_Frame_With_Right_Type_Role_PfBit_And_Expedite(
        Ax25ActionVerb action, UFrameType expectedType, bool expectedIsCommand, bool expectedPfBit, bool expectedExpedited)
    {
        var (d, ctx, s, _, _, _, uFrames, _, _, _, _) = NewRig();
        d.Execute(action, ctx, s);

        uFrames.Should().ContainSingle();
        uFrames[0].Should().Be(new UFrameSpec(
            expectedType,
            IsCommand: expectedIsCommand,
            PfBit: expectedPfBit,
            IsExpedited: expectedExpedited));
    }

    [Fact]
    public void Bare_UA_Consumes_Pending_PfBit()
    {
        var (d, ctx, s, _, _, _, uFrames, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new SabmReceived(
            Ax25Frame.Ui(
                destination: new Callsign("M0LTE", 0),
                source:      new Callsign("G7XYZ", 7),
                info:        "x"u8)));

        // figc4.1 t14: SABM received, "able to establish" Yes branch:
        //   F := P; <other actions>; UA
        d.Execute(new[] { Ax25ActionVerb.FAssignP, Ax25ActionVerb.UA }, tx);

        uFrames.Should().ContainSingle();
        // The Ui factory leaves the P/F bit clear (PollFinal=false), so the
        // pending PfBit ends up false. The point is just that the verb reads
        // tx.Pending.PfBit rather than forcing a value.
        uFrames[0].PfBit.Should().BeFalse();
    }

    [Fact]
    public void Explicit_Qualifier_Overrides_Pending_PfBit()
    {
        var (d, ctx, s, _, _, _, uFrames, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        // Even with Pending.PfBit pre-set to false, an explicit (P == 1)
        // forces the value true.
        d.Execute(new[] { Ax25ActionVerb.FAssign0, Ax25ActionVerb.SABMPEqEq1 }, tx);

        uFrames.Should().ContainSingle();
        uFrames[0].PfBit.Should().BeTrue();
    }

    // ─── UI-frame transmissions ────────────────────────────────────────

    [Fact]
    public void UI_command_Reads_Payload_And_Pid_From_DlUnitDataRequest()
    {
        var (d, ctx, s, _, _, _, _, uiFrames, _, _, _) = NewRig();
        var payload = "hello world"u8.ToArray();
        var tx = new TransitionContext(ctx, s, new DlUnitDataRequest(payload, Pid: 0xCF));

        d.Execute(Ax25ActionVerb.UICommand, tx);

        uiFrames.Should().ContainSingle();
        var spec = uiFrames[0];
        spec.IsCommand.Should().BeTrue();
        spec.PfBit.Should().BeFalse("no preceding F := * verb populated Pending");
        spec.Pid.Should().Be((byte)0xCF);
        spec.Info.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void UI_command_Uses_Default_Pid_When_Primitive_Has_Default()
    {
        var (d, ctx, s, _, _, _, _, uiFrames, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlUnitDataRequest("x"u8.ToArray()));

        d.Execute(Ax25ActionVerb.UICommand, tx);

        uiFrames.Should().ContainSingle();
        uiFrames[0].Pid.Should().Be(Ax25Frame.PidNoLayer3);
    }

    [Fact]
    public void UI_command_Consumes_Pending_PfBit_When_Set()
    {
        var (d, ctx, s, _, _, _, _, uiFrames, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlUnitDataRequest("x"u8.ToArray()));

        d.Execute(new[] { Ax25ActionVerb.FAssign1, Ax25ActionVerb.UICommand }, tx);

        uiFrames[0].PfBit.Should().BeTrue();
    }

    [Fact]
    public void UI_command_Throws_When_Trigger_Is_Not_DlUnitDataRequest()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        var act = () => d.Execute(Ax25ActionVerb.UICommand, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*UI_command*requires the trigger*DL_UNIT_DATA_request*DL_CONNECT_request*");
    }

    // ─── I-frame emission ──────────────────────────────────────────────

    [Fact]
    public void I_Command_Builds_IFrameSpec_From_Pending_And_Trigger_Stashes_For_Retransmit()
    {
        var iFrames = new List<IFrameSpec>();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendIFrame: iFrames.Add);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            VS = 4,
            VR = 7,
        };
        var payload = "i-payload"u8.ToArray();
        var tx = new TransitionContext(ctx, scheduler, new IFramePopsOffQueue(payload, Pid: 0xCC));

        // Replicate figc4.4 t20's chain: N(s):=V(s); N(r):=V(r); p:=0; I_command.
        dispatcher.Execute(new[] { Ax25ActionVerb.NSAssignVS, Ax25ActionVerb.NRAssignVR, Ax25ActionVerb.PAssign0, Ax25ActionVerb.ICommand }, tx);

        iFrames.Should().ContainSingle();
        var f = iFrames[0];
        f.IsCommand.Should().BeTrue();
        f.Ns.Should().Be((byte)4);
        f.Nr.Should().Be((byte)7);
        f.PBit.Should().BeFalse("p := 0 set Pending.PfBit = false");
        f.Info.ToArray().Should().Equal(payload);
        f.Pid.Should().Be((byte)0xCC);

        ctx.SentIFrames.Should().ContainKey((byte)4);
        ctx.SentIFrames[4].Data.ToArray().Should().Equal(payload);
        ctx.SentIFrames[4].Pid.Should().Be((byte)0xCC);
    }

    [Fact]
    public void I_Command_Throws_When_Trigger_Is_Not_IFramePopsOffQueue()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        var act = () => d.Execute(Ax25ActionVerb.ICommand, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*I_command*requires the trigger*I_frame_pops_off_queue*");
    }

    // ─── DL upper-layer signals ────────────────────────────────────────

    [Theory]
    [InlineData(Ax25ActionVerb.DLCONNECTIndication,    typeof(DataLinkConnectIndication))]
    [InlineData(Ax25ActionVerb.DLCONNECTConfirm,       typeof(DataLinkConnectConfirm))]
    [InlineData(Ax25ActionVerb.DLDISCONNECTIndication, typeof(DataLinkDisconnectIndication))]
    [InlineData(Ax25ActionVerb.DLDISCONNECTConfirm,    typeof(DataLinkDisconnectConfirm))]
    public void Simple_DL_Signals_Are_Raised_With_No_Payload(Ax25ActionVerb action, Type expectedRecordType)
    {
        var (d, ctx, s, _, _, _, _, _, upward, _, _) = NewRig();
        d.Execute(action, ctx, s);

        upward.Should().ContainSingle();
        upward[0].Should().BeOfType(expectedRecordType);
    }

    [Theory]
    [InlineData(Ax25ActionVerb.DLERRORIndicationCD, "C_D")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationD,   "D")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationE,   "E")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationF,   "F")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationG,   "G")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationK,   "K")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationL,   "L")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationM,   "M")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationN,   "N")]
    [InlineData(Ax25ActionVerb.DLERRORIndicationO,   "O")]
    public void DL_Error_Indication_Letters_Are_Raised_With_The_Code(Ax25ActionVerb action, string expectedCode)
    {
        var (d, ctx, s, _, _, _, _, _, upward, _, _) = NewRig();
        d.Execute(action, ctx, s);

        upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkErrorIndication>()
              .Which.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void DL_Data_Indication_Reads_Info_And_Pid_From_Incoming_I_Frame()
    {
        var (d, ctx, s, _, _, _, _, _, upward, _, _) = NewRig();
        // Build a mod-8 I-frame with N(R)=0, N(S)=0, P=0, info="payload", PID=0xF0
        var info = "payload"u8.ToArray();
        var bytes = new byte[7 + 7 + 1 + 1 + info.Length];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = 0x00;  // I-frame: low bit 0, no N(R)/N(S)/P set
        bytes[15] = Ax25Frame.PidNoLayer3;
        info.CopyTo(bytes.AsSpan(16));
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();

        var tx = new TransitionContext(ctx, s, new IFrameReceived(frame!));

        d.Execute(Ax25ActionVerb.DLDATAIndication, tx);

        upward.Should().ContainSingle();
        var sig = upward[0].Should().BeOfType<DataLinkDataIndication>().Subject;
        sig.Info.ToArray().Should().Equal(info);
        sig.Pid.Should().Be(Ax25Frame.PidNoLayer3);
    }

    [Fact]
    public void DL_UnitData_Indication_From_UI_Frame_Raises_UnitDataIndication()
    {
        // Regression (m0lte/packet.net#258): UI_Check's figure verb
        // "DL-UNIT-DATA Indication" used to be reconciled to a snake_case
        // canonical via the runtime ActionVerbAliases map, but the dispatcher's
        // case label was left in display form so the normalised verb fell through
        // to the default throw and every UI reception crashed. The verb is now the
        // typed Ax25ActionVerb.DLUNITDATAIndication — codegen is the sole
        // canonicaliser and the exhaustive switch guarantees a handler exists.
        var (d, ctx, s, _, _, _, _, _, upward, _, _) = NewRig();
        var info = "ui-payload"u8.ToArray();
        var frame = Ax25Frame.Ui(new Callsign("M0LTE", 0), new Callsign("G7XYZ", 7), info: info);
        var tx = new TransitionContext(ctx, s, new UiReceived(frame));

        d.Execute(Ax25ActionVerb.DLUNITDATAIndication, tx);

        var sig = upward.Should().ContainSingle().Which.Should().BeOfType<DataLinkUnitDataIndication>().Subject;
        sig.Info.ToArray().Should().Equal(info);
    }

    [Fact]
    public void DL_Data_Indication_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new T1Expiry());

        var act = () => d.Execute(Ax25ActionVerb.DLDATAIndication, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*DL_DATA_indication*requires an incoming frame*T1_expiry*");
    }

    // ─── Sequence-variable assignments ─────────────────────────────────

    [Theory]
    [InlineData(Ax25ActionVerb.RCAssign0, 7, 0)]
    [InlineData(Ax25ActionVerb.RCAssign1, 7, 1)]
    [InlineData(Ax25ActionVerb.RCAssignRCPlus1, 4, 5)]
    public void RC_Assignment_Verbs_Mutate_RC(Ax25ActionVerb action, int initial, int expected)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.RC = initial;
        d.Execute(action, ctx, s);
        ctx.RC.Should().Be(expected);
    }

    [Fact]
    public void VS_Set_To_Zero_Resets_The_Field()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VS = 5;
        d.Execute(Ax25ActionVerb.VSAssign0, ctx, s);
        ctx.VS.Should().Be((byte)0);
    }

    [Fact]
    public void VS_Increment_Wraps_At_Mod8_Modulus()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VS = 7;
        d.Execute(Ax25ActionVerb.VSAssignVSPlus1, ctx, s);
        ctx.VS.Should().Be((byte)0, "mod-8 by default; 7 + 1 wraps to 0");
    }

    [Fact]
    public void VS_Increment_Wraps_At_Mod128_Modulus_When_Extended()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.IsExtended = true;
        ctx.VS = 127;
        d.Execute(Ax25ActionVerb.VSAssignVSPlus1, ctx, s);
        ctx.VS.Should().Be((byte)0, "mod-128 in extended mode; 127 + 1 wraps to 0");
    }

    [Fact]
    public void VR_Set_To_Zero_Resets_The_Field()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VR = 5;
        d.Execute(Ax25ActionVerb.VRAssign0, ctx, s);
        ctx.VR.Should().Be((byte)0);
    }

    [Fact]
    public void VR_Increment_Wraps_At_Modulus()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VR = 7;
        d.Execute(Ax25ActionVerb.VRAssignVRPlus1, ctx, s);
        ctx.VR.Should().Be((byte)0);
    }

    [Fact]
    public void VA_Set_To_Zero_Resets_The_Field()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VA = 5;
        d.Execute(Ax25ActionVerb.VAAssign0, ctx, s);
        ctx.VA.Should().Be((byte)0);
    }

    // ─── Bulk execute + error path ─────────────────────────────────────

    [Fact]
    public void Bulk_Execute_Runs_The_Whole_Action_Chain_In_Order()
    {
        // The actual t01_dl_flow_off_when_own_receiver_busy chain from
        // figc4.4a col 5 (Yes branch).
        var (d, ctx, s, _, _, sFrames, _, _, _, _, _) = NewRig();
        d.Execute(
            new[] { Ax25ActionVerb.SetOwnReceiverBusy, Ax25ActionVerb.RNRResponse, Ax25ActionVerb.ClearAcknowledgePending },
            ctx, s);

        ctx.OwnReceiverBusy.Should().BeTrue();
        ctx.AcknowledgePending.Should().BeFalse();
        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(SupervisoryFrameType.Rnr);
        sFrames[0].IsCommand.Should().BeFalse();
    }

    // (The former "unknown action throws" test is gone: Verb is now the closed
    // Ax25ActionVerb enum and the dispatcher's exhaustive switch handles every
    // member, so there is no runtime "unknown verb" path to exercise — a missing
    // or renamed verb is caught at compile time via CS8509 instead.)

    // ─── Reads from incoming frame ─────────────────────────────────────

    /// <summary>Build a mod-8 RR command frame with the supplied N(R) and P bit.</summary>
    private static Ax25Frame BuildRrCommand(byte nr, bool pollBit = false)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        // RR control (mod-8): bits 7..5 = N(R), bit 4 = P, bits 3..0 = 0001
        bytes[14] = (byte)(((nr & 0x07) << 5) | (pollBit ? 0x10 : 0) | 0x01);
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void VA_Assign_From_Nr_Reads_N_R_From_Incoming_Frame(byte nr)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var frame = BuildRrCommand(nr);
        var tx = new TransitionContext(ctx, s, new RrReceived(frame));

        d.Execute(Ax25ActionVerb.VAAssignNR, tx);

        ctx.VA.Should().Be(nr);
    }

    [Fact]
    public void VA_Assign_From_Nr_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        // DlConnectRequest is an upper-layer primitive — no attached frame.
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        var act = () => d.Execute(Ax25ActionVerb.VAAssignNR, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*DL_CONNECT_request*");
    }

    [Fact]
    public void VA_Assign_From_Nr_Reads_7bit_Nr_On_Extended_Frame()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.IsExtended = true;
        // An extended (mod-128) RR carries a 7-bit N(R) in its second control
        // octet (Fig 4.3b). N(R) = 100 doesn't fit mod-8's 3 bits, so a correct
        // result proves the extraction is genuinely 7-bit. Extraction is driven
        // by the frame's own control width (ControlExtension), not ctx.
        var frame = Ax25Frame.Rr(new Callsign("M0LTE", 0), new Callsign("G7XYZ", 7),
            nr: 100, isCommand: true, extended: true);
        var tx = new TransitionContext(ctx, s, new RrReceived(frame));

        d.Execute(Ax25ActionVerb.VAAssignNR, tx);

        ctx.VA.Should().Be(100);
    }

    // ─── Pending-frame field assignments (write side) ──────────────────

    /// <summary>Build a mod-8 I-frame with the supplied N(R), N(S), P bit.</summary>
    private static Ax25Frame BuildIFrame(byte nr, byte ns, bool pollBit, byte[] info)
    {
        // body: dest(7) + src(7) + control(1) + pid(1) + info
        var bytes = new byte[7 + 7 + 1 + 1 + info.Length];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        // I-frame control (mod-8): bit 0 = 0, bits 7..5 = N(R), bit 4 = P, bits 3..1 = N(S)
        bytes[14] = (byte)(((nr & 0x07) << 5) | (pollBit ? 0x10 : 0) | ((ns & 0x07) << 1));
        bytes[15] = Ax25Frame.PidNoLayer3;
        info.CopyTo(bytes.AsSpan(16));
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public void Nr_Assign_From_VR_Writes_Into_Pending()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VR = 5;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute(Ax25ActionVerb.NRAssignVR, tx);

        tx.Pending.Nr.Should().Be((byte)5);
        tx.Pending.Ns.Should().BeNull();
        tx.Pending.PfBit.Should().BeNull();
    }

    [Fact]
    public void Ns_Assign_From_VS_Writes_Into_Pending()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VS = 6;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute(Ax25ActionVerb.NSAssignVS, tx);

        tx.Pending.Ns.Should().Be((byte)6);
    }

    [Fact]
    public void Nr_Assign_From_Ns_Reads_Incoming_I_Frame_NS_When_StrictlyFaithful()
    {
        // Raw figc4.4 verb (ax25spec#42 quirk off): N(r) := N(s) extracts N(S).
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful;
        var frame = BuildIFrame(nr: 2, ns: 4, pollBit: false, info: "hello"u8.ToArray());
        var tx = new TransitionContext(ctx, s, new IFrameReceived(frame));

        d.Execute(Ax25ActionVerb.NRAssignNS, tx);

        tx.Pending.Nr.Should().Be((byte)4);
    }

    [Fact]
    public void Nr_Assign_From_Ns_Retargets_To_Vr_By_Default_Ax25Spec42()
    {
        // ax25spec#42 / Ax25Spec42SrejTargetsGap (default on): on an I_received
        // trigger the SREJ must request the gap (V(R)), not the just-arrived N(S),
        // so the figure's `N(r) := N(s)` is retargeted to V(R).
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VR = 1;
        var frame = BuildIFrame(nr: 2, ns: 4, pollBit: false, info: "hello"u8.ToArray());
        var tx = new TransitionContext(ctx, s, new IFrameReceived(frame));

        d.Execute(Ax25ActionVerb.NRAssignNS, tx);

        tx.Pending.Nr.Should().Be((byte)1, "retargeted from N(S)=4 to V(R)=1 — the missing gap");
    }

    [Fact]
    public void Nr_Assign_From_Ns_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new T1Expiry());

        var act = () => d.Execute(Ax25ActionVerb.NRAssignNS, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*T1_expiry*");
    }

    [Theory]
    [InlineData(Ax25ActionVerb.FAssign0, false)]
    [InlineData(Ax25ActionVerb.FAssign1, true)]
    [InlineData(Ax25ActionVerb.PAssign0, false)]
    public void F_And_P_Bit_Constant_Assignments_Write_Pending_PfBit(Ax25ActionVerb action, bool expected)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute(action, tx);

        tx.Pending.PfBit.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F_Assign_From_P_Echoes_Incoming_Poll_Bit(bool pollBit)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var frame = BuildRrCommand(3, pollBit: pollBit);
        var tx = new TransitionContext(ctx, s, new RrReceived(frame));

        d.Execute(Ax25ActionVerb.FAssignP, tx);

        tx.Pending.PfBit.Should().Be(pollBit);
    }

    [Fact]
    public void F_Assign_From_P_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        var act = () => d.Execute(Ax25ActionVerb.FAssignP, tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*DL_CONNECT_request*");
    }

    [Fact]
    public void Pending_Frame_Builder_Accumulates_Multiple_Writes_In_One_Chain()
    {
        // The whole point of Pending: a chain of processing verbs accumulates
        // fields, then a signal_lower (future PR) reads them as a unit.
        // For now we just prove the accumulation works.
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.VR = 5;
        ctx.VS = 6;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute(new[] { Ax25ActionVerb.NSAssignVS, Ax25ActionVerb.NRAssignVR, Ax25ActionVerb.FAssign1 }, tx);

        tx.Pending.Ns.Should().Be((byte)6);
        tx.Pending.Nr.Should().Be((byte)5);
        tx.Pending.PfBit.Should().BeTrue();
    }

    // ─── Link-multiplexer signals ──────────────────────────────────────

    [Theory]
    [InlineData(Ax25ActionVerb.LMSeizeRequest,   typeof(LinkMultiplexerSeizeRequest))]
    [InlineData(Ax25ActionVerb.LMReleaseRequest, typeof(LinkMultiplexerReleaseRequest))]
    [InlineData(Ax25ActionVerb.LMDataRequest,    typeof(LinkMultiplexerDataRequest))]
    public void Link_Multiplexer_Verbs_Raise_The_Right_Signal(Ax25ActionVerb action, Type expectedType)
    {
        var (d, ctx, s, _, _, _, _, _, _, linkMux, _) = NewRig();
        d.Execute(action, ctx, s);

        linkMux.Should().ContainSingle();
        linkMux[0].Should().BeOfType(expectedType);
    }

    // ─── Internal signals ──────────────────────────────────────────────

    [Fact]
    public void MDL_Negotiate_Request_Raises_Internal_Signal()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, internalSignals) = NewRig();
        d.Execute(Ax25ActionVerb.MDLNEGOTIATERequest, ctx, s);

        internalSignals.Should().ContainSingle()
            .Which.Should().BeOfType<MdlNegotiateRequestSignal>();
    }

    [Theory]
    [InlineData(Ax25ActionVerb.PushOnIFrameQueue)]
    [InlineData(Ax25ActionVerb.PushFrameOnQueue)]
    public void Push_On_I_Frame_Queue_Enqueues_Trigger_Payload(Ax25ActionVerb verb)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, internalSignals) = NewRig();
        var payload = "hello"u8.ToArray();
        var tx = new TransitionContext(ctx, s, new DlDataRequest(payload));

        d.Execute(verb, tx);

        ctx.IFrameQueue.Should().HaveCount(1);
        ctx.IFrameQueue.Peek().Data.ToArray().Should().Equal(payload);
        internalSignals.Should().ContainSingle().Which.Should().BeOfType<PushIFrameQueueSignal>();
    }

    [Fact]
    public void Push_Old_I_Frame_N_R_On_Queue_Retransmits_Stored_Frame_With_Its_Original_Ns()
    {
        // push_old_I_frame_N_r_on_queue retransmits the stored frame whose N(S)
        // equals the incoming N(R). It must go out with its ORIGINAL N(s) — not
        // be enqueued for the fresh-frame drain (which would renumber it to V(s)
        // and break the peer's gap-fill). See m0lte/packet.net#231.
        var sentI = new List<IFrameSpec>();
        var time = new FakeTimeProvider();
        var s = new SystemTimerScheduler(time);
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { }, sendSFrame: _ => { }, sendUFrame: _ => { },
            sendUiFrame: _ => { }, sendIFrame: sentI.Add, sendUpward: _ => { },
            sendLinkMux: _ => { }, sendInternal: _ => { });
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            // Frame 3 must be genuinely OUTSTANDING for a selective replay to fire:
            // V(a)=3, V(s)=4 ⇒ the single unacked frame is N(S)=3. A peer only ever
            // requests an outstanding frame; replaying a non-outstanding (already-
            // acked) one is the mod-8 SREJ ring-wrap bug the guard now blocks.
            VA = 3,
            VS = 4,
        };
        var oldPayload = "retransmit-me"u8.ToArray();
        // Pretend we sent an I-frame with N(S) = 3 (still outstanding, see above).
        ctx.SentIFrames[3] = (oldPayload, Ax25Frame.PidNoLayer3);

        // Now an RR comes in with N(R) = 3 — meaning "I want frame 3 again".
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        bytes[14] = (3 << 5) | 0x01;
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        var tx = new TransitionContext(ctx, s, new RrReceived(frame!));

        d.Execute(Ax25ActionVerb.PushOldIFrameNROnQueue, tx);

        sentI.Should().ContainSingle("the stored frame is retransmitted directly");
        sentI[0].Ns.Should().Be((byte)3, "retransmitted with its ORIGINAL N(s)=3, not renumbered to V(s)");
        sentI[0].Info.ToArray().Should().Equal(oldPayload);
    }

    // ─── Queue/exception/flag processing verbs ─────────────────────────

    [Theory]
    [InlineData(Ax25ActionVerb.DiscardFrameQueue)]
    [InlineData(Ax25ActionVerb.DiscardQueue)]
    [InlineData(Ax25ActionVerb.DiscardIFrameQueue)]
    public void Discard_Queue_Verbs_All_Clear_IFrameQueue(Ax25ActionVerb verb)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.IFrameQueue.Enqueue((new byte[] { 1 }, Ax25Frame.PidNoLayer3));
        ctx.IFrameQueue.Enqueue((new byte[] { 2 }, Ax25Frame.PidNoLayer3));

        d.Execute(verb, ctx, s);

        ctx.IFrameQueue.Should().BeEmpty();
    }

    [Theory]
    [InlineData(Ax25ActionVerb.DiscardIFrame)]
    [InlineData(Ax25ActionVerb.DiscardContentsOfIFrame)]
    [InlineData(Ax25ActionVerb.DiscardPrimitive)]
    public void No_Op_Discard_Verbs_Do_Not_Throw(Ax25ActionVerb verb)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var act = () => d.Execute(verb, ctx, s);
        act.Should().NotThrow();
    }

    [Fact]
    public void Set_And_Clear_Reject_Exception_Mutate_The_Flag()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        d.Execute(Ax25ActionVerb.SetRejectException, ctx, s);
        ctx.RejectException.Should().BeTrue();
        d.Execute(Ax25ActionVerb.ClearRejectException, ctx, s);
        ctx.RejectException.Should().BeFalse();
    }

    [Fact]
    public void Increment_Srej_Exception_Bumps_Count_And_Sets_Flag()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.SrejExceptionCount.Should().Be(0);
        ctx.SelectiveRejectException.Should().BeFalse();

        d.Execute(Ax25ActionVerb.IncrementSrejectException, ctx, s);

        ctx.SrejExceptionCount.Should().Be(1);
        ctx.SelectiveRejectException.Should().BeTrue();

        d.Execute(Ax25ActionVerb.IncrementSrejectException, ctx, s);
        ctx.SrejExceptionCount.Should().Be(2);
    }

    [Fact]
    public void Decrement_Srej_Exception_If_GT_0_Bottoms_At_Zero_And_Clears_Flag()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.SrejExceptionCount = 2;
        ctx.SelectiveRejectException = true;

        d.Execute(Ax25ActionVerb.DecrementSrejectExceptionIf0, ctx, s);
        ctx.SrejExceptionCount.Should().Be(1);
        ctx.SelectiveRejectException.Should().BeTrue("count > 0 still");

        d.Execute(Ax25ActionVerb.DecrementSrejectExceptionIf0, ctx, s);
        ctx.SrejExceptionCount.Should().Be(0);
        ctx.SelectiveRejectException.Should().BeFalse("count reached 0 — flag cleared");

        d.Execute(Ax25ActionVerb.DecrementSrejectExceptionIf0, ctx, s);
        ctx.SrejExceptionCount.Should().Be(0, "no negative values");
    }

    [Theory]
    [InlineData(Ax25ActionVerb.SetVersion20, false)]
    [InlineData(Ax25ActionVerb.SetVersion22, true)]
    public void Set_Version_Verbs_Toggle_IsExtended(Ax25ActionVerb verb, bool expectedExtended)
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.IsExtended = !expectedExtended;

        d.Execute(verb, ctx, s);

        ctx.IsExtended.Should().Be(expectedExtended);
    }

    [Fact]
    public void SRT_Assign_Initial_Default_Sets_3_Seconds()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.Srt = TimeSpan.FromMinutes(99);

        d.Execute(Ax25ActionVerb.SRTAssignInitialDefault, ctx, s);

        ctx.Srt.Should().Be(TimeSpan.FromMilliseconds(3000));
    }

    [Fact]
    public void T1V_Assign_2_Times_SRT_Doubles_Current_SRT()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.Srt = TimeSpan.FromMilliseconds(2500);
        ctx.T1V = TimeSpan.FromMinutes(99);

        d.Execute(Ax25ActionVerb.T1VAssign2TimesSRT, ctx, s);

        ctx.T1V.Should().Be(TimeSpan.FromMilliseconds(5000));
    }

    // ─── Establishment link-param seeds (the #292/#300 clobber class) ───
    //
    // figc4.7's Set_Version_2_0 / Set_Version_2_2 bodies carry `N2 := 10`,
    // `T2 := 3000`, and (mod-8) `k := 8`. Mirroring the InitialSrt (#292) /
    // InitialN2 (#300) pattern, each writes a CONFIGURABLE dispatcher seed
    // defaulting to the spec value — so a per-port configured N2/T2/k survives
    // establishment if a future SDL ever runs Set_Version on the connect path,
    // and the default stays exactly the spec constant. The mod-128 `k := 32`
    // stays the spec mod-128 default (XID-negotiated), intentionally un-seeded.

    [Fact]
    public void Establishment_Seed_Defaults_Match_The_Spec_Constants()
    {
        var (d, _, _, _, _, _, _, _, _, _, _) = NewRig();
        d.InitialN2.Should().Be(10, "§6.7.1.3 N2 default");
        d.InitialT2.Should().Be(TimeSpan.FromMilliseconds(3000), "§6.7.1.1 T2 default");
        d.InitialK.Should().Be(8, "§6.7.1.4 mod-8 k default");
        ActionDispatcher.DefaultInitialN2.Should().Be(10);
        ActionDispatcher.DefaultInitialT2.Should().Be(TimeSpan.FromMilliseconds(3000));
        ActionDispatcher.DefaultInitialK.Should().Be(8);
    }

    [Fact]
    public void N2_Assign_10_Writes_The_Spec_Default_When_Unseeded()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.N2 = 99;
        d.Execute(Ax25ActionVerb.N2Assign10, ctx, s);
        ctx.N2.Should().Be(10, "an unseeded dispatcher reproduces the spec N2 default");
    }

    [Fact]
    public void N2_Assign_10_Reproduces_The_Configured_InitialN2()
    {
        var (_, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var d = NewSeededDispatcher(initialN2: 4);
        ctx.N2 = 99;
        d.Execute(Ax25ActionVerb.N2Assign10, ctx, s);
        ctx.N2.Should().Be(4, "the verb must reproduce the configured N2, not hard-code 10 (#300)");
    }

    [Fact]
    public void T2_Assign_3000_Writes_The_Spec_Default_When_Unseeded()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.T2 = TimeSpan.FromMinutes(99);
        d.Execute(Ax25ActionVerb.T2Assign3000, ctx, s);
        ctx.T2.Should().Be(TimeSpan.FromMilliseconds(3000), "an unseeded dispatcher reproduces the spec T2 default");
    }

    [Fact]
    public void T2_Assign_3000_Reproduces_The_Configured_InitialT2()
    {
        var (_, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var d = NewSeededDispatcher(initialT2: TimeSpan.FromMilliseconds(4000));
        ctx.T2 = TimeSpan.FromMinutes(99);
        d.Execute(Ax25ActionVerb.T2Assign3000, ctx, s);
        ctx.T2.Should().Be(TimeSpan.FromMilliseconds(4000), "the verb must reproduce the configured T2, not hard-code 3000 ms");
    }

    [Fact]
    public void K_Assign_8_Writes_The_Spec_Mod8_Default_When_Unseeded()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.K = 99;
        d.Execute(Ax25ActionVerb.KAssign8, ctx, s);
        ctx.K.Should().Be(8, "an unseeded dispatcher reproduces the spec mod-8 k default");
    }

    [Fact]
    public void K_Assign_8_Reproduces_The_Configured_InitialK()
    {
        var (_, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var d = NewSeededDispatcher(initialK: 6);
        ctx.K = 99;
        d.Execute(Ax25ActionVerb.KAssign8, ctx, s);
        ctx.K.Should().Be(6, "the mod-8 k verb must reproduce the configured WindowSize, not hard-code 8");
    }

    [Fact]
    public void K_Assign_32_Stays_The_Spec_Mod128_Default_Even_When_Seeded()
    {
        // mod-128 k is the spec 32, then XID-negotiated — it is NOT the operator's
        // mod-8 WindowSize knob, so the configured InitialK must not leak into it.
        var (_, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var d = NewSeededDispatcher(initialK: 6);
        ctx.K = 99;
        d.Execute(Ax25ActionVerb.KAssign32, ctx, s);
        ctx.K.Should().Be(32, "mod-128 k stays the spec default (XID-negotiated), independent of the mod-8 seed");
    }

    private static ActionDispatcher NewSeededDispatcher(
        int? initialN2 = null, TimeSpan? initialT2 = null, int? initialK = null)
        => new(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendUFrame: _ => { },
            sendUiFrame: _ => { },
            sendUpward: _ => { },
            sendLinkMux: _ => { },
            sendInternal: _ => { })
        {
            InitialN2 = initialN2 ?? ActionDispatcher.DefaultInitialN2,
            InitialT2 = initialT2 ?? ActionDispatcher.DefaultInitialT2,
            InitialK = initialK ?? ActionDispatcher.DefaultInitialK,
        };

    // ─── SRT IIR formula (figc4.7 Select_T1_Value) ─────────────────────

    [Fact]
    public void Stop_T1_Captures_Remaining_Time_Before_Cancel()
    {
        var (d, ctx, s, time, _, _, _, _, _, _, _) = NewRig();
        ctx.T1V = TimeSpan.FromMilliseconds(6000);
        d.Execute(Ax25ActionVerb.StartT1, ctx, s);

        time.Advance(TimeSpan.FromMilliseconds(2300));
        d.Execute(Ax25ActionVerb.StopT1, ctx, s);

        // The sample stashed onto the context should be what was left
        // on T1 at the moment of cancel, not zero (which is what
        // TimeRemaining would return after cancel).
        ctx.T1RemainingWhenLastStopped.Should().Be(TimeSpan.FromMilliseconds(3700));
        s.IsRunning("T1").Should().BeFalse();
    }

    [Fact]
    public void Select_T1_Value_IIR_Uses_Real_Sample_When_T1_Stopped_Early()
    {
        // SRT update on a happy ack: T1 armed for 6s, stopped after
        // 2s elapsed → remaining = 4s → sample = T1V - remaining = 2s.
        // Spec formula: SRT' = 7/8 * 3000 + 1/8 * 2000 = 2625 + 250 = 2875.
        var (d, ctx, s, time, _, _, _, _, _, _, _) = NewRig();
        ctx.Srt = TimeSpan.FromMilliseconds(3000);
        ctx.T1V = TimeSpan.FromMilliseconds(6000);
        d.Execute(Ax25ActionVerb.StartT1, ctx, s);
        time.Advance(TimeSpan.FromMilliseconds(2000));
        d.Execute(Ax25ActionVerb.StopT1, ctx, s);

        d.Execute(Ax25ActionVerb.SRTAssign7SRT8PlusT18RemainingTimeOnT1WhenLastStopped8, ctx, s);

        ctx.Srt.Should().Be(TimeSpan.FromMilliseconds(2875));
        ctx.T1RemainingWhenLastStopped.Should().Be(TimeSpan.Zero, "consumed by the IIR — next call starts fresh");
    }

    [Fact]
    public void Select_T1_Value_IIR_Skips_Unmeasured_RoundTrip_By_Default_Karn()
    {
        // ax25spec#41 / Karn's algorithm (Ax25Spec41KarnSrtSampling, default on):
        // when T1 expired (remaining = 0) there is no clean round-trip to measure,
        // so SRT is left unchanged rather than folding the full T1V back in. The
        // unguarded fold (3375 below) is self-amplifying and diverges under loss
        // (m0lte/packet.net#241). T1V still backs off via the RC term.
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.Srt = TimeSpan.FromMilliseconds(3000);
        ctx.T1V = TimeSpan.FromMilliseconds(6000);
        ctx.T1RemainingWhenLastStopped = TimeSpan.Zero;   // T1 expired — no measurement

        d.Execute(Ax25ActionVerb.SRTAssign7SRT8PlusT18RemainingTimeOnT1WhenLastStopped8, ctx, s);

        ctx.Srt.Should().Be(TimeSpan.FromMilliseconds(3000), "Karn: no RTT sample is taken from a timed-out round-trip");
    }

    [Fact]
    public void Select_T1_Value_IIR_Folds_Full_T1V_On_Expiry_When_StrictlyFaithful()
    {
        // Quirk off → figc4.7 as drawn: remaining = 0 ⇒ sample = T1V ⇒
        // SRT' = 7/8 * 3000 + 1/8 * 6000 = 3375. This is the divergent behaviour
        // (ax25spec#41) preserved for strict conformance study.
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful;
        ctx.Srt = TimeSpan.FromMilliseconds(3000);
        ctx.T1V = TimeSpan.FromMilliseconds(6000);
        ctx.T1RemainingWhenLastStopped = TimeSpan.Zero;

        d.Execute(Ax25ActionVerb.SRTAssign7SRT8PlusT18RemainingTimeOnT1WhenLastStopped8, ctx, s);

        ctx.Srt.Should().Be(TimeSpan.FromMilliseconds(3375));
    }

    [Fact]
    public void Select_T1_Value_IIR_Converges_To_Actual_Rtt_Over_Multiple_Samples()
    {
        // Drive the IIR with a steady "real RTT = 800ms" signal and
        // confirm SRT smooths toward 800ms. We model a peer that ACKs
        // 800ms after every SABM-like exchange. T1V stays at 6s (the
        // armed duration); each round-trip leaves 5200ms on the clock
        // when stop_T1 fires.
        var (d, ctx, s, time, _, _, _, _, _, _, _) = NewRig();
        ctx.Srt = TimeSpan.FromMilliseconds(3000);  // initial default
        ctx.T1V = TimeSpan.FromMilliseconds(6000);

        for (int i = 0; i < 50; i++)
        {
            d.Execute(Ax25ActionVerb.StartT1, ctx, s);
            time.Advance(TimeSpan.FromMilliseconds(800));
            d.Execute(Ax25ActionVerb.StopT1, ctx, s);
            d.Execute(Ax25ActionVerb.SRTAssign7SRT8PlusT18RemainingTimeOnT1WhenLastStopped8, ctx, s);
        }

        // 50 iterations of (7/8)^n decay: residual = 2200 * 0.875^50 ≈ 3ms.
        // Well within 10ms of 800ms — i.e. essentially converged.
        ctx.Srt.Should().BeCloseTo(TimeSpan.FromMilliseconds(800), TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void Save_Contents_Of_I_Frame_Stashes_Trigger_Frame_Keyed_By_N_S()
    {
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var info = "out-of-order"u8.ToArray();
        var bytes = new byte[7 + 7 + 1 + 1 + info.Length];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: true,  ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: false, ExtensionBit: true ).Write(bytes.AsSpan(7, 7));
        // I-frame mod-8: N(R)=0, P=0, N(S)=4, low bit 0
        bytes[14] = (byte)((4 & 0x07) << 1);
        bytes[15] = Ax25Frame.PidNoLayer3;
        info.CopyTo(bytes.AsSpan(16));
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        var tx = new TransitionContext(ctx, s, new IFrameReceived(frame!));

        d.Execute(Ax25ActionVerb.SaveContentsOfIFrame, tx);

        ctx.StoredReceivedIFrames.Should().ContainKey((byte)4);
        ctx.StoredReceivedIFrames[4].Info.ToArray().Should().Equal(info);
        ctx.StoredReceivedIFrames[4].Pid.Should().Be(Ax25Frame.PidNoLayer3);
    }

    [Fact]
    public void Retrieve_Stored_V_R_I_Frame_Stages_It_For_The_Following_DL_DATA_Indication()
    {
        // figc4.4 / figc4.5 draw the stored-frame drain as two separate body
        // actions: "Retrieve Stored V(r) I Frame" then "DL-DATA Indication".
        // Retrieval consumes the stored frame and stages it; the following
        // DL-DATA Indication delivers the staged frame (not the trigger).
        var (d, ctx, s, _, _, _, _, _, upward, _, _) = NewRig();
        ctx.VR = 5;
        ctx.StoredReceivedIFrames[5] = ("delayed-payload"u8.ToArray(), Ax25Frame.PidNoLayer3);

        var tx = new TransitionContext(ctx, s, new DlConnectRequest());
        d.Execute(new[] { Ax25ActionVerb.RetrieveStoredVRIFrame, Ax25ActionVerb.DLDATAIndication }, tx);

        ctx.StoredReceivedIFrames.Should().NotContainKey((byte)5, "retrieval consumes the stored frame");
        upward.Should().ContainSingle("the staged frame is delivered by the following DL-DATA Indication");
        var sig = upward[0].Should().BeOfType<DataLinkDataIndication>().Subject;
        sig.Info.ToArray().Should().Equal("delayed-payload"u8.ToArray());
    }

    [Fact]
    public void Retrieve_Stored_V_R_I_Frame_Is_A_No_Op_When_No_Match()
    {
        var (d, ctx, s, _, _, _, _, _, upward, _, _) = NewRig();
        ctx.VR = 5;
        // No stored frame for V(R) == 5.

        d.Execute(Ax25ActionVerb.RetrieveStoredVRIFrame, ctx, s);

        upward.Should().BeEmpty();
    }

    // ─── Subroutine calls ──────────────────────────────────────────────

    // KnownSubroutines is now derived from the figc4.7 redraw. The
    // _F_0 / _F_1 names from the prior hand-stubbed list don't exist
    // any more — Enquiry_Response collapses both into one subroutine.
    // New names from the redraw: Establish_Extended_Data_Link,
    // Set_Version_2_0, Set_Version_2_2.
    [Theory]
    [InlineData("Establish_Data_Link")]
    [InlineData("Establish_Extended_Data_Link")]
    [InlineData("Clear_Exception_Conditions")]
    [InlineData("UI_Check")]
    [InlineData("Select_T1_Value")]
    [InlineData("Check_I_Frame_Acknowledged")]
    [InlineData("Check_Need_For_Response")]
    [InlineData("Transmit_Enquiry")]
    [InlineData("Invoke_Retransmission")]
    [InlineData("N_r_Error_Recovery")]
    [InlineData("Enquiry_Response")]
    [InlineData("Set_Version_2_0")]
    [InlineData("Set_Version_2_2")]
    // Legacy aliases — kept as no-op stubs for back-compat with
    // pre-redraw YAML pages.
    [InlineData("Enquiry_Response_F_0")]
    [InlineData("Enquiry_Response_F_1")]
    public void Known_Subroutine_Verbs_Are_Stubbed_NoOp_By_Default(string verb)
    {
        // Subroutines are dispatched by name through the registry (the
        // dispatcher's subroutine-call verbs route there); on an un-wired
        // registry every known name resolves to a no-op stub.
        var (d, ctx, s, _, _, _, _, _, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());
        var act = () => d.Subroutines.Invoke(verb, tx);
        act.Should().NotThrow();
    }

    [Fact]
    public void Custom_Subroutine_Registry_Receives_The_Invocation()
    {
        var registry = new DefaultSubroutineRegistry();
        var called = new List<string>();
        registry.Register("Establish_Data_Link", _ => called.Add("Establish_Data_Link"));

        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            subroutines: registry);
        var ctx = new Ax25SessionContext { Local = new Callsign("M0LTE", 0), Remote = new Callsign("G7XYZ", 7) };
        var tx = new TransitionContext(ctx, scheduler, new DlConnectRequest());

        // Establish_Data_Link reaches the registry via the EstablishDataLink verb.
        dispatcher.Execute(Ax25ActionVerb.EstablishDataLink, tx);

        called.Should().ContainSingle().Which.Should().Be("Establish_Data_Link");
    }

    [Fact]
    public void Subroutine_Registry_Has_Access_To_TransitionContext()
    {
        var registry = new DefaultSubroutineRegistry();
        TransitionContext? captured = null;
        registry.Register("Select_T1_Value", tx => captured = tx);

        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            subroutines: registry);
        var ctx = new Ax25SessionContext { Local = new Callsign("M0LTE", 0), Remote = new Callsign("G7XYZ", 7) };
        var tx = new TransitionContext(ctx, scheduler, new DlConnectRequest());

        // Select_T1_Value reaches the registry via the SelectT1Value verb.
        dispatcher.Execute(Ax25ActionVerb.SelectT1Value, tx);

        captured.Should().NotBeNull();
        captured!.Trigger.Should().BeOfType<DlConnectRequest>();
    }

    [Fact]
    public void Subroutine_Registry_Throws_On_Unknown_Name()
    {
        var registry = new DefaultSubroutineRegistry();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext { Local = new Callsign("M0LTE", 0), Remote = new Callsign("G7XYZ", 7) };
        var tx = new TransitionContext(ctx, scheduler, new DlConnectRequest());

        var act = () => registry.Invoke("Doesnt_Exist", tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*unknown SDL subroutine*Doesnt_Exist*");
    }
}
