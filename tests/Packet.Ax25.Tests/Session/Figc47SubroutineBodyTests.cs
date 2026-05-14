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

        d.Execute("Set_Version_2_0", ctx, new SystemTimerScheduler(new FakeTimeProvider()));

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

        d.Execute("Set_Version_2_2", ctx, new SystemTimerScheduler(new FakeTimeProvider()));

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

        d.Execute("Clear_Exception_Conditions", ctx, new SystemTimerScheduler(new FakeTimeProvider()));

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

        d.Execute("Establish_Data_Link", ctx, scheduler);

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

        d.Execute("Establish_Data_Link", ctx, scheduler);

        lastUFrame.Should().NotBeNull();
        lastUFrame!.Value.Type.Should().Be(UFrameType.Sabme);
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

        d.Execute("N_r_Error_Recovery", ctx, scheduler);

        upward.OfType<DataLinkErrorIndication>().Should().ContainSingle().Which.Code.Should().Be("J");
        ctx.Layer3Initiated.Should().BeFalse();
    }
}
