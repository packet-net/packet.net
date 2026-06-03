using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests for the figc4.5 Timer Recovery state
/// under simulated 100 % loss. Two <see cref="Ax25Session"/> instances
/// run in-process, linked by a controllable bidirectional pipe; a
/// <see cref="FakeTimeProvider"/> drives T1 expiries deterministically.
/// </summary>
/// <remarks>
/// Phase 2 exit criterion ([#168](https://github.com/m0lte/packet.net/issues/168)):
/// *"Timer Recovery entered + exited under scripted net-sim 100 % loss
/// for (T1−1)·N2 then recovery."* These tests exercise the canonical
/// §C4.5 cycle:
/// <list type="number">
/// <item>Connected session sends an I-frame; T1 starts.</item>
/// <item>Loss drops the I-frame (and any subsequent retransmissions). T1
/// expires; session enters TimerRecovery; sends RR Command (P=1); RC=1.</item>
/// <item>T1 expires again. RC=2. And again. RC=3.</item>
/// <item>Either: loss lifts before RC=N2 → next retransmit reaches peer;
/// peer responds with RR (F=1); session re-enters Connected.</item>
/// <item>Or: loss persists. RC reaches N2 → DL-ERROR Indication (I) +
/// DM emitted upstream; session transitions to Disconnected.</item>
/// </list>
/// The in-process link forgoes the KISS round-trip but exercises the
/// full <see cref="Ax25Session"/> → <see cref="ActionDispatcher"/> →
/// frame-bytes → <see cref="Ax25Frame.TryParse"/> → <see cref="Ax25FrameClassifier"/>
/// path, so the figc4.5 transitions fire under real frame plumbing.
/// </remarks>
public class DataLinkTimerRecoveryIntegrationTests
{
    private const int FastT1V = 200;   // ms — tight enough for sub-second tests
    private const int FastN2  = 3;     // retries before give-up

    [Fact]
    public void Connected_DataRequest_Enters_TimerRecovery_When_T1_Expires_Under_Loss()
    {
        var rig = BuildPair();

        Connect(rig);

        // Loss starts. A's I-frame and any retransmits will be dropped.
        rig.Link.LossActive = true;
        rig.A.Session.PostEvent(new DlDataRequest("hello"u8.ToArray()));
        Settle(rig);

        rig.A.Session.CurrentState.Should().Be("Connected",
            "T1 has just been armed; we're still in Connected pending its expiry");
        rig.A.Scheduler.IsRunning("T1").Should().BeTrue(
            "DL-DATA-request in Connected should arm T1 via the I-frame send subroutine");
        rig.A.Context.T1V.Should().Be(TimeSpan.FromMilliseconds(FastT1V),
            "T1V should remain at the FastT1V value after the handshake");

        // T1 expiry → enter TimerRecovery. The TimerRecovery entry path
        // re-arms T1, so we can't assert T1.IsRunning here; instead
        // assert on state + RC.
        rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 10));
        Settle(rig);
        rig.A.Session.CurrentState.Should().Be("TimerRecovery",
            "after T1 expires with no ack, session must enter TimerRecovery");
        rig.A.Context.RC.Should().Be(1, "first retry must increment RC to 1");
    }

    [Fact]
    public void TimerRecovery_Disconnects_When_N2_Retries_Exhausted_Under_Sustained_Loss()
    {
        var rig = BuildPair();
        Connect(rig);

        rig.Link.LossActive = true;
        rig.A.Session.PostEvent(new DlDataRequest("hello"u8.ToArray()));
        Settle(rig);

        // N2+1 T1 cycles total. The first expiry takes Connected → TimerRecovery
        // (figc4.4 t12, RC=1). Each subsequent expiry, while `RC != N2`, fires
        // figc4.5 t21_t1_expiry_no (RC++, stay in TimerRecovery). On the
        // expiry where `RC == N2`, figc4.5 t21_t1_expiry_yes_* transitions to
        // Disconnected.
        for (int i = 0; i <= FastN2; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 10));
            Settle(rig);
        }

        rig.A.Session.CurrentState.Should().Be("Disconnected",
            "after N2 retries with no ack, session must give up and transition to Disconnected");
        rig.A.Signals.OfType<DataLinkDisconnectIndication>().Should().NotBeEmpty(
            "DL-DISCONNECT-indication must surface upstream so callers know the link is gone");
    }

    [Fact]
    public void TimerRecovery_Retransmits_Unacked_IFrame_And_Recovers_When_Loss_Lifts()
    {
        // figc4.5 step 4 (the recovery branch the TimerRecovery docstring
        // promises but no other in-process test exercised): A enters
        // TimerRecovery under loss; loss lifts; A re-polls; B replies RR(F=1);
        // A invokes retransmission of the unacked I-frame (carrying its
        // ORIGINAL N(s), so B accepts it in sequence) and, once everything is
        // re-acked, returns to Connected. In-process mirror of the
        // hardware-loop scripted-loss scenario (#214).
        //
        // This exercises two fixes that both had to land for recovery to work:
        //   - runtime: ActionDispatcher re-emits old frames with the push-time
        //     N(s) (was renumbered off the restored V(s) — pure-RR stall);
        //   - upstream (Packet.Ax25.Sdl 0.7.1, ax25sdl#54): figc4.5's
        //     recovery-complete decision now guards on the post-`V(a):=N(r)`
        //     value (`vs_eq_nr`) instead of the stale pre-action `vs_eq_va`,
        //     so a poll-response that fully re-acks routes to Connected rather
        //     than back into Invoke_Retransmission with nothing to send.
        var rig = BuildPair();
        Connect(rig);

        rig.Link.LossActive = true;
        rig.A.Session.PostEvent(new DlDataRequest("hello"u8.ToArray()));
        Settle(rig);

        // First T1 expiry: Connected → TimerRecovery, poll (RR P=1) emitted
        // but dropped because loss is still active. RC=1.
        rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 10));
        Settle(rig);
        rig.A.Session.CurrentState.Should().Be("TimerRecovery");

        // Loss lifts. Subsequent T1 expiries re-send the poll; B replies
        // RR(F=1); A re-emits the unacked I-frame (N(s)=0) which B accepts,
        // and once V(a) catches up A returns to Connected. A couple of poll
        // cycles are needed (retransmit, then the F=1 poll once all re-acked);
        // loop a bounded number rather than hard-coding the count.
        rig.Link.LossActive = false;
        for (int i = 0; i < FastN2 && rig.A.Session.CurrentState != "Connected"; i++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 10));
            Settle(rig);
        }

        rig.A.Session.CurrentState.Should().Be("Connected",
            "after the poll / RR(F=1) exchange and retransmission, A must exit TimerRecovery back to Connected");
        rig.B.Signals.OfType<DataLinkDataIndication>()
            .Any(d => d.Info.ToArray().AsSpan().SequenceEqual("hello"u8))
            .Should().BeTrue(
                "the unacked I-frame must be retransmitted on recovery, with its original N(s), " +
                "and surface as a DL-DATA-indication on B");
    }

    // ─── Rig: two sessions linked in-process ───────────────────────────

    private sealed class Link
    {
        public bool LossActive { get; set; }
    }

    private sealed record Endpoint(
        Ax25Session Session,
        Ax25SessionContext Context,
        ConcurrentQueue<DataLinkSignal> Signals,
        Queue<Ax25Event> Inbound,
        SystemTimerScheduler Scheduler);

    private sealed record Pair(Endpoint A, Endpoint B, Link Link, FakeTimeProvider Time);

    /// <summary>
    /// Drain queued inbound events on both endpoints until they
    /// stabilise (no new events generated). Posting a frame from A
    /// generates an inbound on B; B's processing may emit a response
    /// that becomes an inbound on A; iterate until quiescent.
    /// </summary>
    private static void Settle(Pair rig)
    {
        // Bound the loop in case of pathological send-loops. AX.25 should
        // never trigger more than a handful of round-trips per stimulus.
        for (int i = 0; i < 64; i++)
        {
            var progress = false;
            while (rig.A.Inbound.TryDequeue(out var evt))
            {
                rig.A.Session.PostEvent(evt);
                progress = true;
            }
            while (rig.B.Inbound.TryDequeue(out var evt))
            {
                rig.B.Session.PostEvent(evt);
                progress = true;
            }
            if (!progress) return;
        }
        throw new InvalidOperationException("link did not settle within 64 round-trips");
    }

    private static Pair BuildPair()
    {
        var nodeA = new Callsign("M0LTEA", 1);
        var nodeB = new Callsign("M0LTEB", 2);
        var time  = new FakeTimeProvider();
        var link  = new Link();

        var a = BuildEndpoint(nodeA, nodeB, time, link, out var aPeer);
        var b = BuildEndpoint(nodeB, nodeA, time, link, out var bPeer);

        // Wire each endpoint's outbound to the peer's inbound *queue*
        // (not its session directly — see Settle for the re-entrancy
        // rationale).
        aPeer.Target = b.Inbound;
        bPeer.Target = a.Inbound;

        return new Pair(a, b, link, time);
    }

    private sealed class Peer
    {
        public Queue<Ax25Event>? Target { get; set; }
        public Callsign? TargetLocal { get; set; }
    }

    private static Endpoint BuildEndpoint(
        Callsign local, Callsign remote,
        FakeTimeProvider time, Link link, out Peer peer)
    {
        peer = new Peer { TargetLocal = remote };
        var peerLocal = peer;
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = local,
            Remote = remote,
            // T1V is initialised here, but the connect handshake re-derives
            // it as `T1V := 2 * SRT`. Set SRT small so the post-handshake
            // T1V matches FastT1V (otherwise it climbs back to 6 s default
            // and the test runs slowly or times out).
            Srt = TimeSpan.FromMilliseconds(FastT1V / 2),
            T1V = TimeSpan.FromMilliseconds(FastT1V),
            N2  = FastN2,
        };
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var inbound = new Queue<Ax25Event>();
        var subroutines = new DefaultSubroutineRegistry();

        Ax25Session? sessionRef = null;

        // sendBytes: parse own frame bytes, deliver to peer's inbound
        // queue unless loss is active. Queued (not direct PostEvent)
        // because the receiving session might be mid-dispatch from a
        // prior frame in the same chain.
        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            if (link.LossActive) return;
            if (!Ax25Frame.TryParse(bytes.Span, out var parsed)) return;
            // Address filter — drop if not for the peer (e.g. UI broadcast).
            if (peerLocal.TargetLocal is { } expected
                && !parsed.Destination.Callsign.Equals(expected)) return;
            peerLocal.Target?.Enqueue(Ax25FrameClassifier.Classify(parsed));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    signals.Enqueue,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines);

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);
        subroutines.Wire(dispatcher, guards);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;
        return new Endpoint(session, ctx, signals, inbound, scheduler);
    }

    private static void Connect(Pair rig)
    {
        rig.A.Session.PostEvent(new DlConnectRequest());
        Settle(rig);

        rig.A.Session.CurrentState.Should().Be("Connected",
            "no loss configured; SABM/UA handshake should complete after Settle drains the round-trip");
        rig.B.Session.CurrentState.Should().Be("Connected");

        // Restore the fast timing values — the handshake runs the
        // `T1V := 2 * SRT` verb (via the Select_T1 subroutine) which
        // overwrites the per-context T1V we set at construction. Apply
        // again after the handshake so the loss-scenario timing is fast.
        rig.A.Context.T1V = TimeSpan.FromMilliseconds(FastT1V);
        rig.B.Context.T1V = TimeSpan.FromMilliseconds(FastT1V);

        // Drain any DL-CONNECT-indication / DL-CONNECT-confirm signals so
        // subsequent assertions only see post-connect activity.
        while (rig.A.Signals.TryDequeue(out _)) { }
        while (rig.B.Signals.TryDequeue(out _)) { }
    }

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]          = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"]             = DataLink_Connected.Transitions,
        ["AwaitingRelease"]       = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]         = DataLink_TimerRecovery.Transitions,
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };
}

internal static class EndpointSignalExtensions
{
    public static IEnumerable<T> OfType<T>(this ConcurrentQueue<DataLinkSignal> queue)
        => queue.Where(s => s is T).Cast<T>();
}
