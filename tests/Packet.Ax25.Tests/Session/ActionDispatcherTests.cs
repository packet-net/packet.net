using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
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
                    List<SupervisoryFrameSpec> sFrames) NewRig()
    {
        var timerExpiries = new List<string>();
        var sFrames = new List<SupervisoryFrameSpec>();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: timerExpiries.Add,
            sendSFrame: sFrames.Add);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        return (dispatcher, ctx, scheduler, time, timerExpiries, sFrames);
    }

    // ─── Flag mutations ────────────────────────────────────────────────

    [Fact]
    public void Set_Own_Receiver_Busy_Sets_The_Flag()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.OwnReceiverBusy.Should().BeFalse();
        d.Execute("set_own_receiver_busy", ctx, s);
        ctx.OwnReceiverBusy.Should().BeTrue();
    }

    [Fact]
    public void Clear_Own_Receiver_Busy_Clears_The_Flag()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.OwnReceiverBusy = true;
        d.Execute("clear_own_receiver_busy", ctx, s);
        ctx.OwnReceiverBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData("set_acknowledge_pending",   nameof(Ax25SessionContext.AcknowledgePending), true)]
    [InlineData("clear_acknowledge_pending", nameof(Ax25SessionContext.AcknowledgePending), false)]
    [InlineData("set_layer_3_initiated",     nameof(Ax25SessionContext.Layer3Initiated),    true)]
    [InlineData("clear_layer_3_initiated",   nameof(Ax25SessionContext.Layer3Initiated),    false)]
    [InlineData("set_peer_receiver_busy",    nameof(Ax25SessionContext.PeerReceiverBusy),   true)]
    [InlineData("clear_peer_receiver_busy",  nameof(Ax25SessionContext.PeerReceiverBusy),   false)]
    public void Flag_Verbs_Mutate_The_Right_Field(string action, string fieldName, bool expectedValue)
    {
        var (d, ctx, s, _, _, _) = NewRig();
        // Set the opposite to make the change observable
        typeof(Ax25SessionContext).GetProperty(fieldName)!.SetValue(ctx, !expectedValue);

        d.Execute(action, ctx, s);

        typeof(Ax25SessionContext).GetProperty(fieldName)!.GetValue(ctx).Should().Be(expectedValue);
    }

    // ─── Timer operations ──────────────────────────────────────────────

    [Theory]
    [InlineData("start_T1", "T1")]
    [InlineData("start_T2", "T2")]
    [InlineData("start_T3", "T3")]
    public void Start_Timer_Arms_The_Named_Timer(string action, string timerName)
    {
        var (d, ctx, s, _, _, _) = NewRig();
        d.Execute(action, ctx, s);
        s.IsRunning(timerName).Should().BeTrue();
    }

    [Theory]
    [InlineData("stop_T1", "T1")]
    [InlineData("stop_T2", "T2")]
    [InlineData("stop_T3", "T3")]
    public void Stop_Timer_Cancels_The_Named_Timer(string action, string timerName)
    {
        var (d, ctx, s, _, _, _) = NewRig();
        // Arm first so cancel has something to clear
        d.Execute("start_" + timerName, ctx, s);
        s.IsRunning(timerName).Should().BeTrue();

        d.Execute(action, ctx, s);
        s.IsRunning(timerName).Should().BeFalse();
    }

    [Fact]
    public void Timer_Expiry_Calls_The_Configured_Callback_With_The_Timer_Name()
    {
        var (d, ctx, s, time, expiries, _) = NewRig();
        d.Execute("start_T1", ctx, s);

        time.Advance(d.T1Duration);

        expiries.Should().ContainSingle().Which.Should().Be("T1");
    }

    [Fact]
    public void Default_Timer_Durations_Match_The_Spec_Defaults()
    {
        var (d, _, _, _, _, _) = NewRig();
        // T1 default 3000 ms (XID PI=9 default), T3 default chosen per §6.7.1.3.
        d.T1Duration.Should().Be(TimeSpan.FromMilliseconds(3000));
        d.T2Duration.Should().Be(TimeSpan.FromMilliseconds(1500));
        d.T3Duration.Should().Be(TimeSpan.FromMilliseconds(30000));
    }

    // ─── Queue operations ──────────────────────────────────────────────

    [Fact]
    public void Discard_I_Frame_Queue_Empties_The_Queue()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.IFrameQueue.Enqueue(new byte[] { 1 });
        ctx.IFrameQueue.Enqueue(new byte[] { 2 });
        ctx.IFrameQueue.Should().HaveCount(2);

        d.Execute("discard_i_frame_queue", ctx, s);

        ctx.IFrameQueue.Should().BeEmpty();
    }

    // ─── Supervisory-frame transmissions ───────────────────────────────

    [Theory]
    [InlineData("RR_command",   SupervisoryFrameType.Rr,   true)]
    [InlineData("RR",           SupervisoryFrameType.Rr,   false)]
    [InlineData("RNR_response", SupervisoryFrameType.Rnr,  false)]
    [InlineData("REJ",          SupervisoryFrameType.Rej,  false)]
    [InlineData("SREJ",         SupervisoryFrameType.Srej, false)]
    public void Supervisory_Verbs_Signal_Outgoing_Frame_With_Right_Type_And_Role(
        string action, SupervisoryFrameType expectedType, bool expectedIsCommand)
    {
        var (d, ctx, s, _, _, sFrames) = NewRig();
        d.Execute(action, ctx, s);

        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(expectedType);
        sFrames[0].IsCommand.Should().Be(expectedIsCommand);
    }

    [Fact]
    public void Supervisory_Verb_Consumes_Pending_Nr_And_PfBit()
    {
        var (d, ctx, s, _, _, sFrames) = NewRig();
        ctx.VR = 4;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        // Mimic a figc4.4-style RR response chain: set F, set N(r), emit RR.
        d.Execute(new[] { "F := 1", "N(r) := V(r)", "RR" }, tx);

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
        var (d, ctx, s, _, _, sFrames) = NewRig();
        ctx.VR = 5;
        d.Execute("RNR_response", ctx, s);

        sFrames.Should().ContainSingle();
        sFrames[0].Should().Be(new SupervisoryFrameSpec(
            SupervisoryFrameType.Rnr, IsCommand: false, Nr: 5, PfBit: false));
    }

    // ─── Sequence-variable assignments ─────────────────────────────────

    [Theory]
    [InlineData("RC := 0", 7, 0)]
    [InlineData("RC := 1", 7, 1)]
    [InlineData("RC := RC + 1", 4, 5)]
    public void RC_Assignment_Verbs_Mutate_RC(string action, int initial, int expected)
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.RC = initial;
        d.Execute(action, ctx, s);
        ctx.RC.Should().Be(expected);
    }

    [Fact]
    public void VS_Set_To_Zero_Resets_The_Field()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VS = 5;
        d.Execute("V(s) := 0", ctx, s);
        ctx.VS.Should().Be((byte)0);
    }

    [Fact]
    public void VS_Increment_Wraps_At_Mod8_Modulus()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VS = 7;
        d.Execute("V(s) := V(s) + 1", ctx, s);
        ctx.VS.Should().Be((byte)0, "mod-8 by default; 7 + 1 wraps to 0");
    }

    [Fact]
    public void VS_Increment_Wraps_At_Mod128_Modulus_When_Extended()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.IsExtended = true;
        ctx.VS = 127;
        d.Execute("V(s) := V(s) + 1", ctx, s);
        ctx.VS.Should().Be((byte)0, "mod-128 in extended mode; 127 + 1 wraps to 0");
    }

    [Fact]
    public void VR_Set_To_Zero_Resets_The_Field()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VR = 5;
        d.Execute("V(r) := 0", ctx, s);
        ctx.VR.Should().Be((byte)0);
    }

    [Fact]
    public void VR_Increment_Wraps_At_Modulus()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VR = 7;
        d.Execute("V(r) := V(r) + 1", ctx, s);
        ctx.VR.Should().Be((byte)0);
    }

    [Fact]
    public void VA_Set_To_Zero_Resets_The_Field()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VA = 5;
        d.Execute("V(a) := 0", ctx, s);
        ctx.VA.Should().Be((byte)0);
    }

    // ─── Bulk execute + error path ─────────────────────────────────────

    [Fact]
    public void Bulk_Execute_Runs_The_Whole_Action_Chain_In_Order()
    {
        // The actual t01_dl_flow_off_when_own_receiver_busy chain from
        // figc4.4a col 5 (Yes branch).
        var (d, ctx, s, _, _, sFrames) = NewRig();
        d.Execute(
            new[] { "set_own_receiver_busy", "RNR_response", "clear_acknowledge_pending" },
            ctx, s);

        ctx.OwnReceiverBusy.Should().BeTrue();
        ctx.AcknowledgePending.Should().BeFalse();
        sFrames.Should().ContainSingle();
        sFrames[0].Type.Should().Be(SupervisoryFrameType.Rnr);
        sFrames[0].IsCommand.Should().BeFalse();
    }

    [Fact]
    public void Unknown_Action_Throws()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        var act = () => d.Execute("transmit_warp_drive", ctx, s);
        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*unknown SDL action*transmit_warp_drive*");
    }

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
        var (d, ctx, s, _, _, _) = NewRig();
        var frame = BuildRrCommand(nr);
        var tx = new TransitionContext(ctx, s, new RrReceived(frame));

        d.Execute("V(a) := N(r)", tx);

        ctx.VA.Should().Be(nr);
    }

    [Fact]
    public void VA_Assign_From_Nr_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        // DlConnectRequest is an upper-layer primitive — no attached frame.
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        var act = () => d.Execute("V(a) := N(r)", tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*DL_CONNECT_request*");
    }

    [Fact]
    public void VA_Assign_From_Nr_Throws_For_Extended_Mode_Until_Wired()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.IsExtended = true;
        var frame = BuildRrCommand(3);
        var tx = new TransitionContext(ctx, s, new RrReceived(frame));

        var act = () => d.Execute("V(a) := N(r)", tx);

        // Mod-128 N(R) lives in a 2-byte control field that Ax25Frame doesn't
        // model yet — fail loudly until that's wired rather than silently
        // returning the wrong value.
        act.Should().Throw<NotSupportedException>()
           .WithMessage("*mod-128*not yet implemented*");
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
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VR = 5;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute("N(r) := V(r)", tx);

        tx.Pending.Nr.Should().Be((byte)5);
        tx.Pending.Ns.Should().BeNull();
        tx.Pending.PfBit.Should().BeNull();
    }

    [Fact]
    public void Ns_Assign_From_VS_Writes_Into_Pending()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VS = 6;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute("N(s) := V(s)", tx);

        tx.Pending.Ns.Should().Be((byte)6);
    }

    [Fact]
    public void Nr_Assign_From_Ns_Reads_Incoming_I_Frame_NS()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        var frame = BuildIFrame(nr: 2, ns: 4, pollBit: false, info: "hello"u8.ToArray());
        var tx = new TransitionContext(ctx, s, new IFrameReceived(frame));

        d.Execute("N(r) := N(s)", tx);

        tx.Pending.Nr.Should().Be((byte)4);
    }

    [Fact]
    public void Nr_Assign_From_Ns_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new T1Expiry());

        var act = () => d.Execute("N(r) := N(s)", tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*T1_expiry*");
    }

    [Theory]
    [InlineData("F := 0", false)]
    [InlineData("F := 1", true)]
    [InlineData("p := 0", false)]
    public void F_And_P_Bit_Constant_Assignments_Write_Pending_PfBit(string action, bool expected)
    {
        var (d, ctx, s, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute(action, tx);

        tx.Pending.PfBit.Should().Be(expected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F_Assign_From_P_Echoes_Incoming_Poll_Bit(bool pollBit)
    {
        var (d, ctx, s, _, _, _) = NewRig();
        var frame = BuildRrCommand(3, pollBit: pollBit);
        var tx = new TransitionContext(ctx, s, new RrReceived(frame));

        d.Execute("F := P", tx);

        tx.Pending.PfBit.Should().Be(pollBit);
    }

    [Fact]
    public void F_Assign_From_P_Throws_When_Trigger_Has_No_Frame()
    {
        var (d, ctx, s, _, _, _) = NewRig();
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        var act = () => d.Execute("F := P", tx);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*requires an incoming frame*DL_CONNECT_request*");
    }

    [Fact]
    public void Pending_Frame_Builder_Accumulates_Multiple_Writes_In_One_Chain()
    {
        // The whole point of Pending: a chain of processing verbs accumulates
        // fields, then a signal_lower (future PR) reads them as a unit.
        // For now we just prove the accumulation works.
        var (d, ctx, s, _, _, _) = NewRig();
        ctx.VR = 5;
        ctx.VS = 6;
        var tx = new TransitionContext(ctx, s, new DlConnectRequest());

        d.Execute(new[] { "N(s) := V(s)", "N(r) := V(r)", "F := 1" }, tx);

        tx.Pending.Ns.Should().Be((byte)6);
        tx.Pending.Nr.Should().Be((byte)5);
        tx.Pending.PfBit.Should().BeTrue();
    }
}
