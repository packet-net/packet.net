using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// <see cref="Ax25Session.PostEvent"/> is posted to from genuinely concurrent
/// threads in production: the listener's inbound pump (frame events),
/// timer-expiry callbacks (fired on TimeProvider timer threads), and
/// upper-layer callers (ConnectAsync / SendData). These tests hammer the
/// dispatch path from multiple threads and assert the run-to-completion
/// machinery survives: no corrupted deferred queue (was: Queue.Enqueue throwing
/// ArgumentException under race) and no silently lost events (was: one thread's
/// deferred enqueue wiped by another's finally-Clear). Found by
/// tools/Packet.LinkBench bulk transfers; see docs/link-bench-plan.md.
/// </summary>
public class PostEventConcurrencyTests
{
    private static (Ax25Session Session, Ax25SessionContext Ctx, List<DataLinkSignal> Upward, object UpwardGate) NewConnectedRig()
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Quirks = Ax25SessionQuirks.Default,
        };

        var upward = new List<DataLinkSignal>();
        var upwardGate = new object();
        var registry = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendUFrame: _ => { },
            sendUiFrame: _ => { },
            sendIFrame: _ => { },
            sendUpward: sig => { lock (upwardGate) { upward.Add(sig); } },
            sendLinkMux: _ => { },
            sendInternal: _ => { },
            subroutines: registry);

        Ax25Session? sessionRef = null;
        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, scheduler, currentTrigger: () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(
            ctx, scheduler, dispatcher, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"] = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"] = DataLink_AwaitingConnection.Transitions,
                ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
                ["Connected"] = DataLink_Connected.Transitions,
                ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
                ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
            },
            initialState: "Connected");
        sessionRef = session;
        return (session, ctx, upward, upwardGate);
    }

    /// <summary>Inbound mod-8 RR response addressed to us, N(R)=<paramref name="nr"/>.</summary>
    private static Ax25Frame RrInbound(byte nr)
    {
        var bytes = new byte[15];
        new Ax25Address(new Callsign("M0LTE", 0), CrhBit: false, ExtensionBit: false).Write(bytes.AsSpan(0, 7));
        new Ax25Address(new Callsign("G7XYZ", 7), CrhBit: true, ExtensionBit: true).Write(bytes.AsSpan(7, 7));
        bytes[14] = (byte)((nr << 5) | 0x01);
        Ax25Frame.TryParse(bytes, out var frame).Should().BeTrue();
        return frame!;
    }

    [Fact]
    public async Task Concurrent_posters_never_corrupt_the_dispatch_machinery()
    {
        // Upper layer streams DL-DATA-requests from one thread while the "link"
        // posts RR receipts from another - the LinkBench bulk-transfer shape.
        // Pre-fix this crashed inside Queue<T>.Enqueue within a few hundred
        // posts; post-fix every event dispatches and the session stays sane.
        var (session, _, _, _) = NewConnectedRig();
        const int N = 25_000;
        var payload = new byte[32];

        using var start = new ManualResetEventSlim(false);
        var sender = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < N; i++)
            {
                session.PostEvent(new DlDataRequest(payload));
            }
        });
        var acker = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < N; i++)
            {
                // RR with N(R) == V(S): "everything you sent is acked", keeping
                // the window open so the I-frame queue keeps draining.
                session.PostEvent(new RrReceived(RrInbound((byte)(session.Context.VS % 8))));
            }
        });

        start.Set();
        var hammer = Task.WhenAll(sender, acker);
        (await Task.WhenAny(hammer, Task.Delay(TimeSpan.FromSeconds(60)))).Should().Be(
            hammer, "concurrent PostEvent must not deadlock");
        await hammer; // surface any exception (pre-fix: ArgumentException from Queue<T>)

        session.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Concurrent_data_requests_are_never_silently_dropped()
    {
        // Several threads each post N DL-DATA-requests against a wide-open
        // window (the sink acks instantly), so every request must reach the
        // I-frame send path exactly once: Posters×N I-frames, no losses.
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Quirks = Ax25SessionQuirks.Default,
            K = 7,
        };

        var sent = 0;
        var registry = new DefaultSubroutineRegistry();
        Ax25Session? sessionRef = null;
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame: _ => { },
            sendUFrame: _ => { },
            sendUiFrame: _ => { },
            sendIFrame: _ =>
            {
                Interlocked.Increment(ref sent);
                // Instant lossless ack: the peer has seen everything we sent,
                // so the window can never close and every data request must
                // eventually hit this sink.
                sessionRef!.Context.VA = sessionRef.Context.VS;
            },
            sendUpward: _ => { },
            sendLinkMux: _ => { },
            sendInternal: _ => { },
            subroutines: registry);
        var bindings = Ax25SessionBindings.CreateDefault(
            ctx, scheduler, currentTrigger: () => sessionRef?.CurrentTrigger);
        var session = new Ax25Session(
            ctx, scheduler, dispatcher, new GuardEvaluator(bindings),
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Connected"] = DataLink_Connected.Transitions,
                ["TimerRecovery"] = DataLink_TimerRecovery.Transitions,
            },
            initialState: "Connected");
        sessionRef = session;

        const int N = 25_000;
        const int Posters = 4;
        var payload = new byte[16];
        using var start = new ManualResetEventSlim(false);
        var posters = Enumerable.Range(0, Posters).Select(_ => Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < N; i++)
            {
                session.PostEvent(new DlDataRequest(payload));
            }
        })).ToArray();

        start.Set();
        var hammer = Task.WhenAll(posters);
        (await Task.WhenAny(hammer, Task.Delay(TimeSpan.FromSeconds(60)))).Should().Be(
            hammer, "concurrent PostEvent must not deadlock");
        await hammer;

        // Pre-fix, the finally-Clear() wiped requests a concurrent poster had
        // deferred - bulk transfers stalled with bytes missing. Every request
        // must reach the wire.
        sent.Should().Be(Posters * N);
    }
}
