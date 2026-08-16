using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// A throwing <see cref="Ax25Session.DataLinkSignalEmitted"/> subscriber must not
/// be able to rewrite link state. The signals are raised from the dispatcher's
/// <c>sendUpward</c>, which runs inside the transition's action chain - so an
/// escaping exception hit the #225 rollback and restored the pre-transition state
/// and timers <em>after</em> the transition's frames had gone out: the peer saw a
/// UA and considered itself connected while this end sat Disconnected and silent
/// (packet-net/packet.net#696). Handlers are now invoked one at a time, in
/// isolation, exactly like <c>SessionAccepted</c> / <c>FrameTraced</c> on the
/// listener.
/// </summary>
public class Ax25SessionSignalHandlerIsolationTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    [Fact]
    public async Task A_throwing_subscriber_leaves_the_session_connected_after_sabm()
    {
        var modem = new LoopbackModem();
        var seen = 0;
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            // ConfigureSession is the attach-before-events hook, so this handler is
            // live for the inbound connect's DL-CONNECT-indication.
            ConfigureSession = session =>
            {
                session.DataLinkSignalEmitted += (_, _) => throw new InvalidOperationException("buggy consumer");
                session.DataLinkSignalEmitted += (_, _) => Interlocked.Increment(ref seen);
            },
        });

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
        await modem.SentFrames.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(
            () => listener.ActiveSessions.Count == 1, TimeSpan.FromSeconds(2), "the session should be cached");

        var live = listener.ActiveSessions.Single();
        live.CurrentState.Should().Be("Connected",
            "the UA is already on the wire - a consumer fault must not roll the transition back");
        Volatile.Read(ref seen).Should().BeGreaterThan(0,
            "a fault in one subscriber must not suppress the others");

        // ...and the link keeps working: the peer's I-frame is processed and acked.
        modem.InjectInbound(Ax25Frame.I(
            destination: LocalCall, source: PeerCall, nr: 0, ns: 0, info: "hello"u8, pollBit: true));
        await modem.SentFrames.WaitForCountAsync(2, TimeSpan.FromSeconds(3));
        live.CurrentState.Should().Be("Connected");
        live.Context.VR.Should().Be(1, "the received I-frame advanced V(R)");
    }

    [Fact]
    public void A_throwing_subscriber_does_not_suppress_the_signal_for_others()
    {
        // Direct unit check on the raise path, independent of the listener.
        var session = TestSession();
        var second = 0;
        session.DataLinkSignalEmitted += (_, _) => throw new InvalidOperationException("buggy consumer");
        session.DataLinkSignalEmitted += (_, _) => second++;

        var raise = () => session.RaiseDataLinkSignal(new DataLinkConnectIndication());

        raise.Should().NotThrow("the raise path isolates each subscriber");
        second.Should().Be(1);
    }

    private static Ax25Session TestSession()
    {
        var ctx = new Ax25SessionContext { Local = LocalCall, Remote = PeerCall };
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendUFrame: _ => { },
            sendUiFrame: _ => { },
            sendIFrame: _ => { },
            sendUpward: _ => { });
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler, () => null));
        return new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<Packet.Ax25.Sdl.TransitionSpec>>
            {
                ["Disconnected"] = Packet.Ax25.Sdl.DataLink_Disconnected.Transitions,
            },
            initialState: "Disconnected");
    }
}
