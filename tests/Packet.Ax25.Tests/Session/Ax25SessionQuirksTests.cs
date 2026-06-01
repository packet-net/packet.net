using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The <see cref="Ax25SessionQuirks.Ax25Spec38SrejSelectiveRetransmit"/> session
/// quirk (packethacking/ax25spec#38). figc4.5 draws SREJ-received as the generic
/// fresh-DL-DATA "Push frame onto queue" verb followed by go-back-N "Invoke
/// Retransmission", contradicting §4.3.2.4/§6.4.8, figc4.4, and every
/// implementation. With the quirk on (default) the runtime does single-frame
/// selective retransmit; with it off (StrictlyFaithful) it runs the figure as
/// drawn — which throws on the payload-less push.
/// </summary>
public class Ax25SessionQuirksTests
{
    private const byte Pid = Ax25Frame.PidNoLayer3;

    private static (ActionDispatcher dispatcher, Ax25SessionContext ctx, SystemTimerScheduler scheduler, List<Ax25Frame> sentI)
        NewRig(Ax25SessionQuirks quirks)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTEA", 1),
            Remote = new Callsign("M0LTEB", 2),
            Quirks = quirks,
        };
        // A sent, unacked window: V(s)=3, V(a)=0, frames 0/1/2 still in storage.
        ctx.VS = 3;
        ctx.VA = 0;
        for (byte ns = 0; ns < 3; ns++)
            ctx.SentIFrames[ns] = (new byte[] { ns }, Pid);

        var sentI = new List<Ax25Frame>();
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { }, sendSFrame: _ => { }, sendUFrame: _ => { },
            sendUiFrame: _ => { }, sendIFrame: spec => sentI.Add(spec.ToAx25Frame(ctx)), sendUpward: _ => { },
            sendLinkMux: _ => { }, sendInternal: _ => { }, subroutines: subroutines);
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler));
        subroutines.Wire(dispatcher, guards);
        return (dispatcher, ctx, scheduler, sentI);
    }

    private static TransitionContext SrejTrigger(Ax25SessionContext ctx, SystemTimerScheduler scheduler, byte nr)
    {
        var srej = Ax25Frame.Srej(destination: ctx.Local, source: ctx.Remote, nr: nr, isCommand: false, pollFinal: true);
        return new TransitionContext(ctx, scheduler, new SrejReceived(srej));
    }

    [Fact]
    public void Quirk_on_does_single_frame_selective_retransmit_not_go_back_N()
    {
        var (dispatcher, ctx, scheduler, sentI) = NewRig(Ax25SessionQuirks.Default); // quirk on (default)
        var tx = SrejTrigger(ctx, scheduler, nr: 1);

        // The figc4.5 SREJ-received retransmit verbs as the table draws them.
        dispatcher.Execute(new[]
        {
            new ActionStep("Push Frame Onto Queue", ActionKind.InternalOut),
            new ActionStep("Invoke Retransmission", ActionKind.Subroutine),
        }, tx);

        sentI.Should().ContainSingle(
            "SREJ must selectively retransmit only the single N(r) frame, not go-back-N the whole window");
        sentI[0].GetIFrameNs(ctx.Modulus).Should().Be((byte)1,
            "the resent frame is the requested N(r)=1, carrying its ORIGINAL N(s)");
        sentI[0].Info.ToArray().Should().Equal(new byte[] { 1 },
            "carrying frame 1's stored payload");
    }

    [Fact]
    public void Quirk_off_runs_the_figure_as_drawn_and_throws_on_the_payloadless_push()
    {
        var (dispatcher, ctx, scheduler, _) = NewRig(Ax25SessionQuirks.StrictlyFaithful); // quirk off
        var tx = SrejTrigger(ctx, scheduler, nr: 1);

        var act = () => dispatcher.Execute(
            new[] { new ActionStep("Push Frame Onto Queue", ActionKind.InternalOut) }, tx);

        act.Should().Throw<InvalidOperationException>(
            "with the quirk off the figc4.5 figure runs as drawn — the fresh-DL-DATA push carries no payload on an SREJ trigger and throws (the #38 defect, faithfully reproduced)");
    }
}
