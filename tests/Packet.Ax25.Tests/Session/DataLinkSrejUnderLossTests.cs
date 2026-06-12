using System.Collections.Concurrent;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
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

    // NOTE: this variant pumps each recovery cycle to quiescence with Settle(),
    // which has NO T1 pacing — so the many T1-spaced retransmits of a real link
    // collapse into one instantaneous burst and the trace looks like a retransmit
    // storm (m0lte/packet.net#233). It still recovers correctly. The T1-PACED
    // companion Srej_under_loss_converges_under_T1_pacing_no_storm proves the
    // storm is a Settle artifact, not a recovery defect; and
    // Srej_response_drives_single_frame_selective_retransmit_on_the_wire proves
    // the #38 quirk does single-frame selective (not go-back-N) on the wire.
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

    // m0lte/packet.net#233 (a): the SREJ-selective quirk genuinely ENGAGES
    // end-to-end. Drive A into TimerRecovery, deliver EXACTLY ONE SREJ(nr=1)
    // response, and swallow A's reply before B can feed back more SREJs, so we
    // observe only the frames A puts on the wire in *direct* response to that one
    // SREJ. With the quirk on (default) A emits the single requested frame N(s)=1
    // — NOT go-back-N (which would put 1,2,3 on the wire). This is the on-the-wire
    // proof that complements the verb-level Ax25SessionQuirksTests: the figc4.5
    // SREJ-received transition (t24_srej_received_yes_yes_*_no, the push-bearing
    // response paths) really does route through the quirk's selective redirect in
    // a live two-session exchange.
    [Fact]
    public void Srej_response_drives_single_frame_selective_retransmit_on_the_wire()
    {
        var rig = BuildPair();
        DriveIntoTimerRecovery(rig);

        var iFramesFromA = CaptureAOutboundIFramesSwallowed(rig);
        var srej = Ax25Frame.Srej(destination: rig.A.Context.Local, source: rig.A.Context.Remote, nr: 1, isCommand: false, pollFinal: true);
        rig.A.Session.PostEvent(Ax25FrameClassifier.Classify(srej));
        Settle(rig);

        Log("after one SREJ(nr=1) response", rig);
        iFramesFromA.Should().Equal(new byte[] { 1 },
            "an SREJ response must trigger SELECTIVE single-frame retransmit of N(r)=1 only — go-back-N would have put N(s)=1,2,3 on the wire");
        rig.A.Context.VA.Should().Be((byte)1, "the SREJ N(r)=1 acks through frame 0; V(a) advances to 1");
    }

    // m0lte/packet.net#234: SREJ-as-COMMAND under Default retransmits nothing.
    // §4.3.2.4 ("The SREJ frame is only sent as a response") makes SREJ
    // response-only; direwolf omits the command path entirely (src/ax25_link.c
    // srej_frame: "Command path has been omitted because SREJ can only be
    // response.") and linbpq gates resend on `if (MSGFLAG & RESP)` (L2Code.c
    // SFRAME). So nobody sends SREJ as a command and nobody acts on receiving
    // one. The figc4.5 response:No paths (t24_srej_received_no_yes_*) carry only
    // a go-back-N "Invoke Retransmission" (no push verb); the #38 selective quirk
    // skips that go-back-N and has nothing to redirect, so the command form is a
    // no-op retransmit. This test PINS that documented behaviour: the response
    // form selectively retransmits, the command form does not — matching the
    // de-facto stacks. If a future spec revision resurrects an actionable SREJ
    // command, revisit this together with packethacking/ax25spec#38.
    [Theory]
    [InlineData(true,  new byte[] { 1 })] // response (F=1): selective retransmit of N(r)=1
    [InlineData(false, new byte[0])]      // command  (P=1): no retransmit (SREJ is response-only)
    public void Srej_command_does_not_retransmit_under_default(bool asResponse, byte[] expectedNs)
    {
        var rig = BuildPair();
        DriveIntoTimerRecovery(rig);

        var iFramesFromA = CaptureAOutboundIFramesSwallowed(rig);
        var srej = Ax25Frame.Srej(destination: rig.A.Context.Local, source: rig.A.Context.Remote, nr: 1, isCommand: !asResponse, pollFinal: true);
        rig.A.Session.PostEvent(Ax25FrameClassifier.Classify(srej));
        Settle(rig);

        _out.WriteLine($"asResponse={asResponse}: A emitted I-frames N(s)=[{string.Join(",", iFramesFromA)}], VA={rig.A.Context.VA}");
        iFramesFromA.Should().Equal(expectedNs,
            asResponse
                ? "an SREJ RESPONSE selectively retransmits N(r)=1"
                : "an SREJ COMMAND is response-only per §4.3.2.4 — Default retransmits nothing (matches direwolf/linbpq)");
    }

    // m0lte/packet.net#233 (b)+(c): the same recovery as
    // Srej_under_loss_recovers_from_TimerRecovery_with_default_quirk, but driven
    // with T1 PACING — advance the FakeTimeProvider one T1 interval per round and
    // process only the frames already on the wire (DrainOnce), instead of pumping
    // every cascade to quiescence. This is the realistic-link model: each
    // retransmit is one T1 apart. The unpaced Settle() collapses many T1 rounds
    // into one instant, which is what makes the #231 trace look like a
    // "retransmit storm". Under pacing the link CONVERGES in a bounded number of
    // rounds — proving the storm is a Settle artifact, not a recovery defect. (A
    // secondary, smaller inefficiency remains: the figc4.4/figc4.5 Connected
    // I-frame table has no out-of-window duplicate-discard guard — direwolf adds
    // one per X.25 §2.4.6.4(a), the SDL figures don't — so duplicate retransmits
    // briefly draw extra SREJs. That is a spec-efficiency gap, flagged upstream;
    // it does not stop convergence and is NOT the quirk.)
    [Fact]
    public void Srej_under_loss_converges_under_T1_pacing_no_storm()
    {
        var rig = BuildPair();
        DriveIntoTimerRecovery(rig);

        int rounds = 0;
        const int maxRounds = 20;
        for (; rounds < maxRounds && rig.A.Session.CurrentState != "Connected"; rounds++)
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 20));
            DrainOnce(rig);
            Log($"paced round {rounds}", rig);
        }

        rig.A.Session.CurrentState.Should().Be("Connected",
            "with realistic T1 pacing the link converges back to Connected — the unpaced 'storm' was a Settle artifact, not a recovery failure");
        rounds.Should().BeLessThan(maxRounds, "convergence must happen in a bounded number of T1 rounds, not run away");
        rig.B.Signals.OfType<DataLinkDataIndication>().Select(s => s.Info.ToArray()[0]).Should()
            .Equal(new byte[] { 0, 1, 2, 3 }, "all four payloads must reach B in order");
        rig.A.Context.VA.Should().Be((byte)4, "every I-frame must end acknowledged");
    }

    // #214 — a BULK multi-frame transfer survives scripted loss across sequence-ring
    // wraps and recovers, for BOTH go-back-N (SREJ off) and Selective Repeat (SREJ on).
    // The deterministic in-process proof of the Phase-2 §5.2 "10 kB transfer with
    // 0–30 % scripted loss" criterion that the hardware matrix (HardwareLoop10KBTransfer,
    // [SkippableFact]) can only check on real TNCs. The broader exploration is
    // tools/Packet.LinkBench's `--loss`/`--srej` sweep over a modelled-airtime channel;
    // this distils it to a CI-deterministic regression. Driven with T1 PACING (advance
    // the FakeTimeProvider one T1 interval per round, then drain what's on the wire) so
    // it models a real link rather than the unpaced-Settle "storm" (#233): the first
    // transmission of every third frame is dropped — recurring through every mod-8 ring
    // cycle — so the gap→recover→advance loop is exercised dozens of times, including at
    // the V(R) wrap where #393's stale-stored-frame bug lived. Asserts every payload
    // byte reaches B exactly once, in order, and the link ends Connected + fully acked.
    [Theory]
    [InlineData(false, 4)]   // go-back-N, default window
    [InlineData(true, 4)]    // Selective Repeat, default window (= modulus/2, the SREJ-safe cap)
    [InlineData(false, 2)]   // narrower window
    [InlineData(true, 2)]
    public void Bulk_transfer_survives_scripted_loss_across_ring_wraps(bool srej, int k)
    {
        const int frames = 24;   // 3 full mod-8 ring cycles
        var rig = BuildPair();
        rig.A.Context.K = k;
        rig.B.Context.K = k;
        Connect(rig);
        rig.A.Context.SrejEnabled = srej;
        rig.B.Context.SrejEnabled = srej;

        // Drop the FIRST transmission of every third payload index (recurs through
        // each ring cycle). Keyed by the payload byte = the frame's index, so a
        // retransmit of the same frame is let through and the link always converges.
        var droppedOnce = new HashSet<int>();
        rig.Link.Drop = f =>
        {
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (f.Info.Length < 1) return false;
            int idx = f.Info.Span[0];
            if (idx % 3 == 2 && droppedOnce.Add(idx)) return true;   // drop once, then deliver
            return false;
        };

        for (int i = 0; i < frames; i++) rig.A.Session.PostEvent(new DlDataRequest(new[] { (byte)i }));
        DrainOnce(rig);   // the first window flows out and is processed

        // Paced rounds, the proven rhythm (Srej_under_loss_converges_under_T1_pacing_no_storm):
        // advance one T1 interval, then process one batch of what's on the wire.
        const int maxRounds = 400;
        int rounds = 0;
        for (; rounds < maxRounds; rounds++)
        {
            if (rig.B.Signals.OfType<DataLinkDataIndication>().Count() >= frames) break;
            rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 20));   // T1 tick → retransmits
            DrainOnce(rig);
        }
        // Settle the final acks (the channel is clean of pending drops by now).
        for (int i = 0; i < 64 && (rig.A.Inbound.Count + rig.B.Inbound.Count) > 0; i++) DrainOnce(rig);

        rounds.Should().BeLessThan(maxRounds, "the transfer must converge, not run away under loss");
        var delivered = rig.B.Signals.OfType<DataLinkDataIndication>()
            .Select(s => (int)s.Info.ToArray()[0]).ToArray();
        delivered.Should().Equal(Enumerable.Range(0, frames).ToArray(),
            "every payload byte must reach B exactly once, in order — no loss, no duplicate, no reorder across the ring wrap");
        rig.A.Context.VA.Should().Be((byte)(frames % 8), "every I-frame must end acknowledged");
    }

    // Shared setup: connect, enable SREJ both ends, drop A's gap frame N(s)=1 and
    // starve A of all B→A traffic so A times out into TimerRecovery (where the
    // figc4.5 SREJ paths live). Leaves the channel CLEAN (phase1 off) on return.
    private static void DriveIntoTimerRecovery(Pair rig)
    {
        Connect(rig);
        rig.A.Context.SrejEnabled = true;
        rig.B.Context.SrejEnabled = true;

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
        rig.Time.Advance(TimeSpan.FromMilliseconds(FastT1V + 20));
        Settle(rig);
        rig.A.Session.CurrentState.Should().Be("TimerRecovery",
            "starved of acks, A's T1 must expire and drive it into TimerRecovery");

        phase1[0] = false;
        rig.A.ReceivedFromPeer.Clear();
        rig.B.ReceivedFromPeer.Clear();
    }

    // Re-arm the Drop predicate to SWALLOW every I-frame A emits (recording its
    // N(s)) and drop all other inbound to A, so a single injected SREJ probes
    // A's direct retransmit response without B feeding back further frames.
    private static List<byte> CaptureAOutboundIFramesSwallowed(Pair rig)
    {
        var iFramesFromA = new List<byte>();
        rig.Link.Drop = f =>
        {
            bool fromA = f.Source.Callsign.Equals(rig.A.Context.Local);
            if (fromA && Ax25FrameClassifier.Classify(f) is IFrameReceived)
            {
                iFramesFromA.Add(f.GetIFrameNs(8)!.Value);
                return true; // swallow so B can't react and feed back more SREJs
            }
            return !fromA; // drop everything TO A (we inject the probe directly)
        };
        return iFramesFromA;
    }

    // Process exactly the events currently queued on both endpoints — ONE pass,
    // no pump-to-quiescence. Models a single T1-spaced round: each side reacts
    // to what is already on the wire; any frame it emits in response lands in
    // the peer's queue but is not processed until the NEXT advance/drain.
    private static void DrainOnce(Pair rig)
    {
        int an = rig.A.Inbound.Count, bn = rig.B.Inbound.Count;
        for (int i = 0; i < an && rig.A.Inbound.TryDequeue(out var e); i++) rig.A.Session.PostEvent(e);
        for (int i = 0; i < bn && rig.B.Inbound.TryDequeue(out var e); i++) rig.B.Session.PostEvent(e);
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
