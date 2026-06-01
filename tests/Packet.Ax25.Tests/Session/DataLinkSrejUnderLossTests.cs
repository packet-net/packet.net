using System.Collections.Concurrent;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit.Abstractions;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end deterministic reproduction of connected-mode recovery under
/// scripted single-frame loss — the in-process, FakeTimeProvider-driven
/// analogue of the #214 30%-loss hardware row. Two real <see cref="Ax25Session"/>
/// instances exchange frames over a controllable link.
/// </summary>
/// <remarks>
/// These were the tests that first surfaced m0lte/packet.net#231: retransmitted
/// I-frames were renumbered with a fresh N(s) (the drained queue assigned
/// N(s):=V(s)), so the peer never recognised the resend and no loss was
/// recoverable. With the fix (retransmits emit with their original N(s)) a lost
/// frame is recovered — selectively for SREJ, go-back-N for REJ — and the link
/// stays up.
/// </remarks>
public class DataLinkSrejUnderLossTests
{
    private const int FastT1V = 200;
    private const int FastN2  = 12;
    private readonly ITestOutputHelper _out;

    public DataLinkSrejUnderLossTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Minimal_single_frame_retransmit_should_preserve_Ns_and_deliver()
    {
        var rig = BuildPair();
        Connect(rig);

        // Drop A's first I-frame (N(s)=0) exactly once, then clean channel.
        int drops = 0;
        rig.Link.Drop = f =>
        {
            bool fromA = f.Source.Callsign.Equals(rig.A.Context.Local);
            bool isI0  = Ax25FrameClassifier.Classify(f) is IFrameReceived && f.GetIFrameNs(8) == 0;
            if (fromA && isI0 && drops == 0) { drops++; return true; }
            return false;
        };

        rig.A.Session.PostEvent(new DlDataRequest(new byte[] { 0xAA }));
        Settle(rig);
        for (int c = 0; c < 4 && rig.A.Context.VA == 0; c++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 20));
            Settle(rig);
        }

        Log("FINAL", rig);
        rig.B.Signals.OfType<DataLinkDataIndication>().Select(s => s.Info.ToArray()[0]).Should()
            .Equal(new byte[] { 0xAA }, "B must receive the single payload once A retransmits frame 0 with its ORIGINAL N(s)=0");
        rig.A.Context.VS.Should().Be((byte)1, "exactly one I-frame exists; a retransmit must NOT mint a fresh sequence number");
    }

    [Fact]
    public void Srej_under_loss_recovers_from_TimerRecovery_with_default_quirk()
    {
        var rig = BuildPair();
        Connect(rig);
        rig.A.Context.SrejEnabled = true;
        rig.B.Context.SrejEnabled = true;

        // Phase 1: drop A's gap frame N(s)=1 AND starve A of all B-to-A traffic,
        // so it gets no ack/SREJ in Connected and instead times out into
        // TimerRecovery (where the figc4.5 SREJ defect lives). Phase 2 clears
        // the channel; recovery must complete from TimerRecovery.
        var phase1 = new[] { true };
        rig.Link.Drop = f =>
        {
            if (!phase1[0]) return false;
            bool fromA = f.Source.Callsign.Equals(rig.A.Context.Local);
            bool toA   = f.Destination.Callsign.Equals(rig.A.Context.Local);
            bool isI1  = Ax25FrameClassifier.Classify(f) is IFrameReceived && f.GetIFrameNs(8) == 1;
            return (fromA && isI1) || toA;
        };

        for (byte i = 0; i < 4; i++) { rig.A.Session.PostEvent(new DlDataRequest(new[] { i })); Settle(rig); }
        Log("after 4 sends (drop N(s)=1, starve acks)", rig);

        rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 20));
        Settle(rig);
        Log("after timeout to TimerRecovery", rig);
        rig.A.Session.CurrentState.Should().Be("TimerRecovery",
            "starved of acks, A's T1 must expire and drive it into TimerRecovery");

        phase1[0] = false;  // clean channel
        for (int c = 0; c < 6 && rig.A.Session.CurrentState != "Connected"; c++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 20));
            Try(() => Settle(rig), rig);
            Log($"recovery cycle #{c + 1}", rig);
        }

        Log("FINAL", rig);
        rig.B.Signals.OfType<DataLinkDataIndication>().Select(s => s.Info.ToArray()[0]).Should()
            .Equal(new byte[] { 0, 1, 2, 3 }, "all four payloads must reach B in order once the gap frame is retransmitted with its correct N(s) from TimerRecovery");
        rig.A.Session.CurrentState.Should().Be("Connected", "A must recover the link, not disconnect");
        rig.A.Context.VA.Should().Be((byte)4, "every I-frame must end acknowledged");
    }

    private void Try(Action a, Pair rig)
    {
        try { a(); }
        catch (Exception ex) { _out.WriteLine($"  !! threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Log(string label, Pair rig)
    {
        string bDelivered = string.Join(",", rig.B.Signals.OfType<DataLinkDataIndication>().Select(s => (int)s.Info.ToArray()[0]));
        string aRx = string.Join(" ", rig.A.ReceivedFromPeer.Select(Tag));
        string bRx = string.Join(" ", rig.B.ReceivedFromPeer.Select(Tag));
        _out.WriteLine(
            $"[{label}] A={rig.A.Session.CurrentState}(VS{rig.A.Context.VS}/VA{rig.A.Context.VA}/VR{rig.A.Context.VR}) " +
            $"B={rig.B.Session.CurrentState}(VR{rig.B.Context.VR}) Bdelivered=[{bDelivered}] | A.rx=[{aRx}] | B.rx=[{bRx}]");
    }

    private static string Tag(Ax25Frame f)
    {
        var c = Ax25FrameClassifier.Classify(f);
        var ns = f.GetIFrameNs(8);
        var name = c.GetType().Name.Replace("Received", "");
        return ns is byte n ? $"{name}{n}" : name;
    }

    // --- Rig: two sessions linked in-process (Drop filter + Rx log + fast T1) ---

    private sealed class Link { public Func<Ax25Frame, bool>? Drop { get; set; } }

    private sealed record Endpoint(
        Ax25Session Session, Ax25SessionContext Context,
        ConcurrentQueue<DataLinkSignal> Signals, Queue<Ax25Event> Inbound,
        List<Ax25Frame> ReceivedFromPeer);

    private sealed record Pair(Endpoint A, Endpoint B, Link Link, FakeTimeProvider Time);

    private static void Settle(Pair rig)
    {
        for (int i = 0; i < 128; i++)
        {
            var progress = false;
            while (rig.A.Inbound.TryDequeue(out var evt)) { rig.A.Session.PostEvent(evt); progress = true; }
            while (rig.B.Inbound.TryDequeue(out var evt)) { rig.B.Session.PostEvent(evt); progress = true; }
            if (!progress) return;
        }
        throw new InvalidOperationException("link did not settle within 128 round-trips (storm?)");
    }

    private static Pair BuildPair()
    {
        var nodeA = new Callsign("M0LTEA", 1);
        var nodeB = new Callsign("M0LTEB", 2);
        var time  = new FakeTimeProvider();
        var link  = new Link();
        var a = BuildEndpoint(nodeA, nodeB, time, link, out var aPeer);
        var b = BuildEndpoint(nodeB, nodeA, time, link, out var bPeer);
        aPeer.Target = b.Inbound;  bPeer.Target = a.Inbound;
        aPeer.RxLog  = b.ReceivedFromPeer;  bPeer.RxLog = a.ReceivedFromPeer;
        return new Pair(a, b, link, time);
    }

    private sealed class Peer
    {
        public Queue<Ax25Event>? Target { get; set; }
        public List<Ax25Frame>? RxLog { get; set; }
        public Callsign? TargetLocal { get; set; }
    }

    private static Endpoint BuildEndpoint(
        Callsign local, Callsign remote, FakeTimeProvider time, Link link, out Peer peer)
    {
        peer = new Peer { TargetLocal = remote };
        var peerLocal = peer;
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local = local, Remote = remote,
            Srt = TimeSpan.FromMilliseconds(FastT1V / 2),
            T1V = TimeSpan.FromMilliseconds(FastT1V),
            N2  = FastN2,
        };
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var inbound = new Queue<Ax25Event>();
        var rxLog = new List<Ax25Frame>();
        var subroutines = new DefaultSubroutineRegistry();
        Ax25Session? sessionRef = null;

        void SendBytes(ReadOnlyMemory<byte> bytes)
        {
            if (!Ax25Frame.TryParse(bytes.Span, out var parsed)) return;
            if (link.Drop?.Invoke(parsed) == true) return;
            if (peerLocal.TargetLocal is { } expected && !parsed.Destination.Callsign.Equals(expected)) return;
            peerLocal.RxLog?.Add(parsed);
            peerLocal.Target?.Enqueue(Ax25FrameClassifier.Classify(parsed));
        }

        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame: spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward: signals.Enqueue,
            sendLinkMux: _ => { },
            sendInternal: _ => { },
            subroutines: subroutines);

        var guards = new GuardEvaluator(
            Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger));
        subroutines.Wire(dispatcher, guards);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards, TransitionMap(), "Disconnected");
        sessionRef = session;
        return new Endpoint(session, ctx, signals, inbound, rxLog);
    }

    private static void Connect(Pair rig)
    {
        rig.A.Session.PostEvent(new DlConnectRequest());
        Settle(rig);
        rig.A.Session.CurrentState.Should().Be("Connected");
        rig.B.Session.CurrentState.Should().Be("Connected");
        rig.A.Context.T1V = TimeSpan.FromMilliseconds(FastT1V);
        rig.B.Context.T1V = TimeSpan.FromMilliseconds(FastT1V);
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
