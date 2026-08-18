using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// The <see cref="Ax25SessionQuirks.Ax25Spec9AckProgressResetsRc"/> session quirk
/// (packethacking/ax25spec#9). The figures only reset the retry counter RC on the
/// Timer-Recovery fully-acked checkpoint (V(S)=V(A) → Connected), so a sustained
/// transfer that lives in Timer Recovery with frames always in flight ratchets RC
/// across a <i>working</i> link and dies (DL-ERROR I → DM) at the N2'th lifetime
/// T1 hiccup - reproduced by tools/Packet.LinkBench over net-sim. With the quirk
/// on (default), a T1 expiry that follows V(A)-advancing progress clamps RC to 1
/// before the RC=N2 guard runs: the peer acking new data is the proof of life RC
/// exists to test, so RC counts <i>consecutive</i> recovery failures. The clamp
/// happens at expiry time (not eagerly at ack time) because RC==0 doubles as
/// Select_T1's Karn sampling signal. With it off (StrictlyFaithful) the figures
/// run as drawn.
/// </summary>
public class Ax25Spec9RcResetQuirkTests
{
    private static Ax25Session NewTimerRecoverySession(Ax25SessionQuirks quirks, out Ax25SessionContext ctx)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var context = new Ax25SessionContext
        {
            Local = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Quirks = quirks,
        };
        // Mid-recovery shape: three I-frames in flight, several T1 hiccups on the clock.
        context.VS = 3;
        context.VA = 0;
        context.RC = 7;
        for (byte ns = 0; ns < 3; ns++)
        {
            context.SentIFrames[ns] = (new byte[] { ns }, Ax25Frame.PidNoLayer3);
        }

        var registry = new DefaultSubroutineRegistry();
        Ax25Session? sessionRef = null;
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendUFrame: _ => { },
            sendUiFrame: _ => { },
            sendIFrame: _ => { },
            sendUpward: _ => { },
            sendLinkMux: _ => { },
            sendInternal: _ => { },
            subroutines: registry);
        var bindings = Ax25SessionBindings.CreateDefault(
            context, scheduler, currentTrigger: () => sessionRef?.CurrentTrigger);
        var session = new Ax25Session(
            context, scheduler, dispatcher, new GuardEvaluator(bindings),
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Connected"] = DataLink_Connected.Transitions,
                ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
            },
            initialState: "TimerRecovery");
        sessionRef = session;
        ctx = context;
        return session;
    }

    /// <summary>Inbound mod-8 RR response addressed to us, N(R)=<paramref name="nr"/>, F=0.</summary>
    private static Ax25Frame RrResponse(byte nr)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: false, ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: true, ExtensionBit: true).Write(bytes.AsSpan(7, 7));
        bytes[14] = (byte)((nr << 5) | 0x01);
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public void Quirk_on_a_T1_expiry_after_ack_progress_clamps_RC_to_1()
    {
        var session = NewTimerRecoverySession(Ax25SessionQuirks.Default, out var ctx);

        // An ack advances V(A) (progress - the link is alive) …
        session.PostEvent(new RrReceived(RrResponse(nr: 1)));
        ctx.VA.Should().Be(1, "the RR acknowledged frame 0");
        ctx.RC.Should().Be(7, "RC is untouched at ack time — RC==0 is Select_T1's Karn sampling signal, so the clamp waits for the next T1 expiry");

        // … so the NEXT T1 expiry starts a fresh consecutive-failure run.
        session.PostEvent(new T1Expiry());
        ctx.RC.Should().Be(2, "the expiry clamps RC to 1 BEFORE the RC=N2 guard, then the figure's own RC:=RC+1 runs (ax25spec#9)");
        session.CurrentState.Should().Be("TimerRecovery", "with RC clamped below N2 the link re-polls instead of dying");
    }

    [Fact]
    public void Quirk_on_a_T1_expiry_with_no_progress_keeps_ratcheting_so_a_dead_link_still_exhausts_N2()
    {
        var session = NewTimerRecoverySession(Ax25SessionQuirks.Default, out var ctx);

        // A duplicate ack (N(R)=V(A)) acknowledges nothing new - no progress.
        session.PostEvent(new RrReceived(RrResponse(nr: 0)));
        ctx.VA.Should().Be(0, "N(R)=V(A) acknowledges nothing new");

        session.PostEvent(new T1Expiry());
        ctx.RC.Should().Be(8, "no forward progress since the last expiry — the consecutive-failure ratchet continues toward N2");
    }

    [Fact]
    public void Quirk_on_progress_then_silence_dies_after_N2_consecutive_failures_not_before()
    {
        var session = NewTimerRecoverySession(Ax25SessionQuirks.Default, out var ctx);

        // Progress resets the run …
        session.PostEvent(new RrReceived(RrResponse(nr: 1)));

        // … then the peer goes silent: N2 consecutive unanswered expiries must
        // still kill the link (the watchdog is weakened only against hiccups on
        // a progressing link, never against a genuinely dead one).
        for (var i = 0; i < ctx.N2; i++)
        {
            session.CurrentState.Should().Be("TimerRecovery", $"expiry #{i} is within the N2 budget");
            session.PostEvent(new T1Expiry());
        }

        session.CurrentState.Should().Be("Disconnected", "RC reached N2 with no intervening progress — genuine link failure");
    }

    [Fact]
    public void StrictlyFaithful_runs_the_figure_as_drawn_RC_ratchets_across_a_working_link()
    {
        var session = NewTimerRecoverySession(Ax25SessionQuirks.StrictlyFaithful, out var ctx);

        session.PostEvent(new RrReceived(RrResponse(nr: 1)));
        ctx.VA.Should().Be(1, "the figure's ack processing is untouched");

        session.PostEvent(new T1Expiry());
        ctx.RC.Should().Be(8, "as drawn, progress never clamps RC — only the fully-acked checkpoint path resets it");
    }
}
