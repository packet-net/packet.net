using Microsoft.Extensions.Time.Testing;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end smoke tests for figc4.7 subroutine bodies. Drives each
/// subroutine through the registry's walker (post-Wire) and asserts
/// the session-context mutations / outbound signals match what the
/// figure prescribes.
/// </summary>
/// <remarks>
/// These tests exist to prove the bindings + walker integration works
/// end-to-end on the simplest cases. Subroutines with frame-aware
/// predicates need a triggering frame to be set up on the
/// <see cref="TransitionContext"/>; the constant-action subroutines
/// (Set_Version_2_0 / _2_2, Clear_Exception_Conditions) need none.
/// </remarks>
public class Figc47SubroutineBodyTests
{
    private static (Ax25SessionContext ctx, ActionDispatcher d, FakeTimeProvider time, SystemTimerScheduler scheduler) Rig()
    {
        var local  = new Callsign("M0LTE", 1);
        var remote = new Callsign("WB2OSZ", 0);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    _ => { },
            sendUFrame:    _ => { },
            sendUiFrame:   _ => { },
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            sendIFrame:    _ => { });
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);
        var guards   = new GuardEvaluator(bindings);
        if (d.Subroutines is DefaultSubroutineRegistry reg)
        {
            reg.Wire(d, guards);
        }
        return (ctx, d, time, scheduler);
    }

    [Fact]
    public void Set_Version_2_0_Sets_All_V20_Defaults()
    {
        var (ctx, d, _, _) = Rig();
        // Pre-set values to non-defaults to prove the subroutine resets them.
        ctx.IsExtended = true;
        ctx.K = 1;
        ctx.SrejEnabled = true;
        ctx.HalfDuplex = false;

        d.Subroutines.Invoke("Set_Version_2_0", new TransitionContext(ctx, new SystemTimerScheduler(new FakeTimeProvider()), new DlConnectRequest()));

        ctx.HalfDuplex.Should().BeTrue();
        ctx.ImplicitReject.Should().BeTrue();
        ctx.SrejEnabled.Should().BeFalse();
        ctx.IsExtended.Should().BeFalse();      // mod 8
        ctx.N1.Should().Be(2048);
        ctx.K.Should().Be(8);
        ctx.T2.TotalMilliseconds.Should().Be(3000);
        ctx.N2.Should().Be(10);
    }

    [Fact]
    public void Set_Version_2_2_Sets_All_V22_Defaults()
    {
        var (ctx, d, _, _) = Rig();
        ctx.IsExtended = false;
        ctx.K = 4;
        ctx.SrejEnabled = false;

        d.Subroutines.Invoke("Set_Version_2_2", new TransitionContext(ctx, new SystemTimerScheduler(new FakeTimeProvider()), new DlConnectRequest()));

        ctx.HalfDuplex.Should().BeTrue();
        ctx.ImplicitReject.Should().BeFalse();
        ctx.SrejEnabled.Should().BeTrue();
        ctx.IsExtended.Should().BeTrue();       // mod 128
        ctx.N1.Should().Be(2048);
        ctx.K.Should().Be(32);
        ctx.T2.TotalMilliseconds.Should().Be(3000);
        ctx.N2.Should().Be(10);
    }

    [Fact]
    public void Clear_Exception_Conditions_Clears_All_Six_Flags_And_Queue()
    {
        var (ctx, d, _, _) = Rig();
        ctx.PeerReceiverBusy = true;
        ctx.OwnReceiverBusy  = true;
        ctx.RejectException  = true;
        ctx.SelectiveRejectException = true;
        ctx.SrejExceptionCount = 3;
        ctx.AcknowledgePending = true;
        ctx.IFrameQueue.Enqueue((new byte[] { 1, 2, 3 }, Ax25Frame.PidNoLayer3));

        d.Subroutines.Invoke("Clear_Exception_Conditions", new TransitionContext(ctx, new SystemTimerScheduler(new FakeTimeProvider()), new DlConnectRequest()));

        ctx.PeerReceiverBusy.Should().BeFalse();
        ctx.OwnReceiverBusy.Should().BeFalse();
        ctx.RejectException.Should().BeFalse();
        ctx.SelectiveRejectException.Should().BeFalse();
        ctx.SrejExceptionCount.Should().Be(0);
        ctx.AcknowledgePending.Should().BeFalse();
        ctx.IFrameQueue.Count.Should().Be(0);
    }

    [Fact]
    public void Establish_Data_Link_Mod8_Sends_SABM_And_Starts_T1()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 1),
            Remote = new Callsign("WB2OSZ", 0),
            IsExtended = false,   // mod-8 → SABM
        };
        UFrameSpec? lastUFrame = null;
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    _ => { },
            sendUFrame:    spec => lastUFrame = spec,
            sendUiFrame:   _ => { },
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            sendIFrame:    _ => { });
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);
        var guards   = new GuardEvaluator(bindings);
        if (d.Subroutines is DefaultSubroutineRegistry reg) reg.Wire(d, guards);

        // Pre-state: pretend we're in a flagged exception state so the
        // Clear_Exception_Conditions chained inside Establish_Data_Link
        // visibly clears something.
        ctx.AcknowledgePending = true;
        ctx.RC = 0;

        d.Subroutines.Invoke("Establish_Data_Link", new TransitionContext(ctx, scheduler, new DlConnectRequest()));

        lastUFrame.Should().NotBeNull();
        lastUFrame!.Value.Type.Should().Be(UFrameType.Sabm);
        ctx.RC.Should().Be(1);
        ctx.AcknowledgePending.Should().BeFalse();   // cleared by the chained Clear_Exception_Conditions
        scheduler.IsRunning("T1").Should().BeTrue();
        scheduler.IsRunning("T3").Should().BeFalse();
    }

    [Fact]
    public void Establish_Data_Link_Mod128_Sends_SABME()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 1),
            Remote = new Callsign("WB2OSZ", 0),
            IsExtended = true,    // mod-128 → SABME
        };
        UFrameSpec? lastUFrame = null;
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    _ => { },
            sendUFrame:    spec => lastUFrame = spec,
            sendUiFrame:   _ => { },
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            sendIFrame:    _ => { });
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);
        var guards   = new GuardEvaluator(bindings);
        if (d.Subroutines is DefaultSubroutineRegistry reg) reg.Wire(d, guards);

        d.Subroutines.Invoke("Establish_Data_Link", new TransitionContext(ctx, scheduler, new DlConnectRequest()));

        lastUFrame.Should().NotBeNull();
        lastUFrame!.Value.Type.Should().Be(UFrameType.Sabme);
    }

    [Fact]
    public void Enquiry_Response_F_1_Sets_F_Bit_On_Outbound_Response()
    {
        // figc4.7b page 102 draws Check_Need_for_Response's Yes branch as
        // `Enquiry Response (F = 1)`. The "(F = 1)" annotation isn't
        // documented in §C1.2; canonical encoding tracked at packet-net/ax25sdl#45.
        // The wire contract is unambiguous either way — §4.3 prose: "the
        // reply to this poll is indicated by setting the response (final)
        // bit in the appropriate frame". DefaultSubroutineRegistry honours
        // that via ContextBindingAliases: invoking the F_1 alias sets
        // tx.Pending.PfBit=true before walking the canonical body, so the
        // body's "RR Response" verb emits the frame with F=1.
        var local  = new Callsign("M0LTE", 1);
        var remote = new Callsign("WB2OSZ", 0);
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };

        SupervisoryFrameSpec? lastS = null;
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    s => lastS = s,
            sendUFrame:    _ => { },
            sendUiFrame:   _ => { },
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            sendIFrame:    _ => { });

        // The trigger is what figc4.7's `F=1 & Frame=RR/RNR/I?` predicate
        // reads — an RR Command with the P bit set (PollFinal=true). The
        // PfBit binding mirrors that onto Pending so the response inherits
        // F=1 when it goes out the wire.
        var triggerFrame = Ax25Frame.Rr(
            destination: local, source: remote,
            nr: 0, isCommand: true, pollFinal: true);
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => new RrReceived(triggerFrame));
        var guards = new GuardEvaluator(bindings);
        if (d.Subroutines is DefaultSubroutineRegistry reg) reg.Wire(d, guards);

        var tx = new TransitionContext(ctx, scheduler, new RrReceived(triggerFrame));
        d.Subroutines.Invoke("Enquiry_Response_F_1", tx);

        lastS.Should().NotBeNull("Enquiry_Response must emit an S-frame response");
        lastS!.Value.Type.Should().Be(SupervisoryFrameType.Rr);
        lastS.Value.IsCommand.Should().BeFalse("response form (not command)");
        lastS.Value.PfBit.Should().BeTrue(
            "the (F = 1) parameter binding on the Enquiry_Response_F_1 alias must put F=1 on the wire — without this, the polling side's TimerRecovery guard `response_and_F_eq_1` never matches");
    }

    [Fact]
    public void Enquiry_Response_F_0_Clears_F_Bit_On_Outbound_Response()
    {
        // Symmetric variant: Enquiry_Response_F_0 binds F=0 onto the
        // outbound response. Less common at call sites but it's the
        // natural-pair version of _F_1; both aliases share the canonical
        // body and only differ in the bound F-bit value.
        var local  = new Callsign("M0LTE", 1);
        var remote = new Callsign("WB2OSZ", 0);
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };

        SupervisoryFrameSpec? lastS = null;
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    s => lastS = s,
            sendUFrame:    _ => { },
            sendUiFrame:   _ => { },
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            sendIFrame:    _ => { });

        var triggerFrame = Ax25Frame.Rr(
            destination: local, source: remote,
            nr: 0, isCommand: true, pollFinal: true);
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => new RrReceived(triggerFrame));
        var guards = new GuardEvaluator(bindings);
        if (d.Subroutines is DefaultSubroutineRegistry reg) reg.Wire(d, guards);

        var tx = new TransitionContext(ctx, scheduler, new RrReceived(triggerFrame));
        d.Subroutines.Invoke("Enquiry_Response_F_0", tx);

        lastS.Should().NotBeNull();
        lastS!.Value.PfBit.Should().BeFalse(
            "the (F = 0) parameter binding on the Enquiry_Response_F_0 alias must put F=0 on the wire");
    }

    [Fact]
    public void N_r_Error_Recovery_Emits_J_DL_Error_And_Clears_Layer3_Initiated()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 1),
            Remote = new Callsign("WB2OSZ", 0),
            Layer3Initiated = true,
        };
        var upward = new List<DataLinkSignal>();
        var d = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    _ => { },
            sendUFrame:    _ => { },
            sendUiFrame:   _ => { },
            sendUpward:    upward.Add,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            sendIFrame:    _ => { });
        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler);
        var guards   = new GuardEvaluator(bindings);
        if (d.Subroutines is DefaultSubroutineRegistry reg) reg.Wire(d, guards);

        d.Subroutines.Invoke("N_r_Error_Recovery", new TransitionContext(ctx, scheduler, new DlConnectRequest()));

        upward.OfType<DataLinkErrorIndication>().Should().ContainSingle().Which.Code.Should().Be("J");
        ctx.Layer3Initiated.Should().BeFalse();
    }
}
