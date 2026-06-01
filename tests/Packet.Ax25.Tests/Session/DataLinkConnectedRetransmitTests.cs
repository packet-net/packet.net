using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end integration tests for the figc4.4 REJ / SREJ paths in
/// the Connected state. Two <see cref="Ax25Session"/> instances run
/// in-process, linked by a controllable bidirectional pipe with a
/// frame-aware drop filter; Phase 2 exit criterion is *"REJ and SREJ
/// retransmits observed on the wire"*.
/// </summary>
/// <remarks>
/// The pipe captures every successfully-delivered frame on both
/// endpoints' Received lists so the test can assert on what was seen
/// without inspecting state.
/// </remarks>
public class DataLinkConnectedRetransmitTests
{
    [Fact]
    public void Out_Of_Sequence_I_Frame_Triggers_REJ_Back_To_Sender()
    {
        var rig = BuildPair();
        Connect(rig);

        // Configure the link to drop A's second I-frame (N(s)=1)
        // — the gap forces B's figc4.4 t26 to take the REJ-emitting
        // path (`not ns_eq_vr and not reject_exception and not SREJ_enabled`).
        rig.Link.Drop = frame =>
            frame.Source.Callsign.Equals(rig.A.Context.Local)
            && Ax25FrameClassifier.Classify(frame) is IFrameReceived ifr
            && ifr.Frame.GetIFrameNs(rig.A.Context.Modulus) is byte ns
            && ns == 1;

        rig.A.Session.PostEvent(new DlDataRequest("zero"u8.ToArray()));
        Settle(rig);
        rig.A.Session.PostEvent(new DlDataRequest("one"u8.ToArray()));
        Settle(rig);
        rig.A.Session.PostEvent(new DlDataRequest("two"u8.ToArray()));
        Settle(rig);

        // B must have observed N(s)=0, then a gap, then N(s)=2 → REJ.
        rig.A.ReceivedFromPeer.Any(f =>
                Ax25FrameClassifier.Classify(f) is RejReceived)
            .Should().BeTrue("B's out-of-sequence reception must send REJ back to A");

        // A processes the REJ and stays in Connected (figc4.4 t25_rej_received_yes).
        rig.A.Session.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public void Out_Of_Sequence_I_Frame_With_SREJ_Enabled_Triggers_SREJ_Back_To_Sender()
    {
        var rig = BuildPair();
        Connect(rig);

        // Negotiate SREJ on B (in real protocol via XID; we just set
        // the flag directly). With SREJ enabled and a single-frame
        // gap (N(s) - V(r) == 1), figc4.4 t26 takes the SREJ-emitting
        // path rather than REJ.
        rig.B.Context.SrejEnabled = true;

        rig.Link.Drop = frame =>
            frame.Source.Callsign.Equals(rig.A.Context.Local)
            && Ax25FrameClassifier.Classify(frame) is IFrameReceived
            && frame.GetIFrameNs(rig.A.Context.Modulus) == 1;

        rig.A.Session.PostEvent(new DlDataRequest("zero"u8.ToArray()));
        Settle(rig);
        rig.A.Session.PostEvent(new DlDataRequest("one"u8.ToArray()));
        Settle(rig);
        rig.A.Session.PostEvent(new DlDataRequest("two"u8.ToArray()));
        Settle(rig);

        rig.A.ReceivedFromPeer.Any(f =>
                Ax25FrameClassifier.Classify(f) is SrejReceived)
            .Should().BeTrue("SREJ-enabled peer should request selective retransmission of the missing N(s)=1 frame");
    }

    [Fact]
    public void REJ_Received_In_Connected_Updates_VA_And_Stays_Connected()
    {
        // Direct-injection unit-style test of figc4.4 t25_rej_received_yes.
        // Doesn't need network simulation — just craft the REJ frame and
        // post it. Asserts the runtime applies V(a) := N(r) and stays in
        // Connected (no Invoke_Retransmission semantics asserted here;
        // see m0lte/ax25sdl#44).
        var rig = BuildPair();
        Connect(rig);

        // Pretend A had sent two I-frames (V(s)=2, V(a)=0). We don't
        // exercise the queue here; just nudge the counters so the guard
        // `va_le_nr_le_vs` holds for N(r)=1.
        rig.A.Context.VS = 2;
        rig.A.Context.VA = 0;

        var rej = Ax25Frame.Rej(
            destination: rig.A.Context.Local,
            source: rig.A.Context.Remote,
            nr: 1,
            isCommand: false,
            pollFinal: false);
        rig.A.Session.PostEvent(new RejReceived(rej));

        rig.A.Session.CurrentState.Should().Be("Connected",
            "REJ with N(r) in window stays in Connected (t25_rej_received_yes)");
        rig.A.Context.VA.Should().Be(1,
            "V(a) must catch up to N(r) — the spec's 'V(a) := N(r)' action");
    }

    [Fact]
    public void Invoke_Retransmission_Resends_Every_Unacked_Frame_With_Its_Original_Ns()
    {
        // Regression for ax25sdl#44 (loop recovery) AND m0lte/packet.net#231
        // (retransmit renumbering): the figc4.7 retransmit loop must resend
        // every unacked frame from N(r) up to X (= the saved V(s)) — and each
        // must go out with its ORIGINAL N(s), not a fresh V(s)-derived one.
        // The renumbering bug (drained retransmits got N(s):=V(s)) made every
        // resend unrecognisable to the peer, so a single loss was unrecoverable.
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTEA", 1),
            Remote = new Callsign("M0LTEB", 2),
        };
        var sent = new List<Ax25Frame>();
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: _ => { },
            sendSFrame:    _ => { },
            sendUFrame:    _ => { },
            sendUiFrame:   _ => { },
            sendIFrame:    spec => sent.Add(spec.ToAx25Frame(ctx)),
            sendUpward:    _ => { },
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines);
        var guards = new GuardEvaluator(Ax25SessionBindings.CreateDefault(ctx, scheduler));
        subroutines.Wire(dispatcher, guards);

        // A has sent four I-frames (seq 0..3); V(s)=4, V(a)=0. The peer's REJ
        // asks to go back to N(r)=1, so frames 1, 2 and 3 must be resent
        // (X - N(r) = 4 - 1 = 3 frames), each carrying its own N(s).
        ctx.VS = 4;
        ctx.VA = 0;
        for (byte ns = 0; ns < 4; ns++)
            ctx.SentIFrames[ns] = (new byte[] { ns }, Ax25Frame.PidNoLayer3);

        var rej = Ax25Frame.Rej(
            destination: ctx.Local, source: ctx.Remote,
            nr: 1, isCommand: false, pollFinal: false);
        var tx = new TransitionContext(ctx, scheduler, new RejReceived(rej));

        dispatcher.Execute(new[] { new ActionStep("Invoke Retransmission", ActionKind.Subroutine) }, tx);

        sent.Select(f => f.GetIFrameNs(ctx.Modulus)!.Value).Should().Equal(new byte[] { 1, 2, 3 },
            "go-back-N resends seq 1, 2 and 3 in order, each with its ORIGINAL N(s) — not renumbered to V(s)");
        sent.Select(f => f.Info.ToArray()[0]).Should().Equal(new byte[] { 1, 2, 3 },
            "each resent frame carries the payload originally stored under that sequence number");
        ctx.VS.Should().Be(4,
            "V(s) is restored to X (the saved V(s)) after the retransmit loop completes");
    }

    // ─── Rig: two sessions linked in-process ───────────────────────────

    private sealed class Link
    {
        /// <summary>Returns true if the given frame should be dropped at
        /// the link layer (i.e. never reach the peer's session).</summary>
        public Func<Ax25Frame, bool>? Drop { get; set; }
    }

    private sealed record Endpoint(
        Ax25Session Session,
        Ax25SessionContext Context,
        ConcurrentQueue<DataLinkSignal> Signals,
        Queue<Ax25Event> Inbound,
        List<Ax25Frame> ReceivedFromPeer);

    private sealed record Pair(Endpoint A, Endpoint B, Link Link, FakeTimeProvider Time);

    private static void Settle(Pair rig)
    {
        for (int i = 0; i < 64; i++)
        {
            var progress = false;
            while (rig.A.Inbound.TryDequeue(out var evt)) { rig.A.Session.PostEvent(evt); progress = true; }
            while (rig.B.Inbound.TryDequeue(out var evt)) { rig.B.Session.PostEvent(evt); progress = true; }
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
        aPeer.Target = b.Inbound;     bPeer.Target = a.Inbound;
        // A's outbound (its peer's send-target) is what B receives, so
        // record it in B's ReceivedFromPeer log; and vice versa.
        aPeer.RxLog  = b.ReceivedFromPeer;   bPeer.RxLog = a.ReceivedFromPeer;

        return new Pair(a, b, link, time);
    }

    private sealed class Peer
    {
        public Queue<Ax25Event>? Target { get; set; }
        public List<Ax25Frame>? RxLog { get; set; }
        public Callsign? TargetLocal { get; set; }
    }

    private static Endpoint BuildEndpoint(
        Callsign local, Callsign remote,
        FakeTimeProvider time, Link link, out Peer peer)
    {
        peer = new Peer { TargetLocal = remote };
        var peerLocal = peer;
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var inbound = new Queue<Ax25Event>();
        var rxLog = new List<Ax25Frame>();
        var subroutines = new DefaultSubroutineRegistry();

        Ax25Session? sessionRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            if (!Ax25Frame.TryParse(bytes.Span, out var parsed)) return;
            if (link.Drop?.Invoke(parsed) == true) return;
            if (peerLocal.TargetLocal is { } expected
                && !parsed.Destination.Callsign.Equals(expected)) return;
            // Log on the *receiving* side's RxLog. peerLocal.RxLog is the
            // peer's log (set up via aPeer.RxLog = b.ReceivedFromPeer
            // when wiring), so a frame from A landing on B is recorded
            // in B's log. The test inspects A's ReceivedFromPeer for
            // frames *A* saw arrive — that's frames sent by B.
            peerLocal.RxLog?.Add(parsed);
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
        return new Endpoint(session, ctx, signals, inbound, rxLog);
    }

    private static void Connect(Pair rig)
    {
        rig.A.Session.PostEvent(new DlConnectRequest());
        Settle(rig);
        rig.A.Session.CurrentState.Should().Be("Connected");
        rig.B.Session.CurrentState.Should().Be("Connected");
        // Drain handshake signals so subsequent assertions only see
        // post-connect activity.
        while (rig.A.Signals.TryDequeue(out _)) { }
        while (rig.B.Signals.TryDequeue(out _)) { }
        rig.A.ReceivedFromPeer.Clear();
        rig.B.ReceivedFromPeer.Clear();
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
        _    => throw new InvalidOperationException($"unexpected timer expiry '{name}'"),
    };
}

internal static class IFrameNsExtensions
{
    /// <summary>Decode N(s) from an I-frame's control field. Returns
    /// null for non-I-frames.</summary>
    public static byte? GetIFrameNs(this Ax25Frame frame, int modulus)
    {
        var ctrl = frame.Control;
        // I-frame: low bit is 0.
        if ((ctrl & 0x01) != 0) return null;
        // mod-8: bits 1-3 are N(s). mod-128: second control byte.
        if (modulus == 8) return (byte)((ctrl >> 1) & 0x07);
        // mod-128: control is 16 bits; assume frame.ControlExtended.
        return (byte)((ctrl >> 1) & 0x7F);
    }
}
