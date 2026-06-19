using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Regression coverage for packet-net/packet.net#385 — the §6.7.1.2 T2 acknowledge
/// delay in the production listener wiring.
///
/// Off-air capture of the lab node receiving a sustained I-frame stream from
/// a real LinBPQ at 1200 baud half duplex showed the pre-#385 receive path
/// emitting one RR keyup per received I-frame (five frames → five RRs at
/// ~120 ms spacing). That TX occupancy left the port deaf to the peer's next
/// window, so the F=1 answer to the peer's checkpoint poll carried a stale
/// V(R); BPQ rolled back and retransmitted the whole window, looping until
/// its retry counter exhausted and it sent DISC.
///
/// The fix defers the LM-SEIZE grant by the session's T2: the first
/// in-sequence I-frame of a burst arms T2 (figc4.4 t26 requests the seize
/// only while no ack is pending), follow-on frames just advance V(R), and the
/// confirm at expiry emits ONE cumulative RR (Enquiry_Response reads
/// N(R):=V(R) at dispatch time). Any other N(R)-bearing transmission — a
/// poll/enquiry response, a piggybacking I-frame, an REJ — runs the SDL's
/// Clear Acknowledge Pending, which also cancels the armed T2, so a stale
/// ack can never follow it onto the wire. T2 = 0 restores ack-per-frame.
/// </summary>
public class Ax25ListenerT2CoalescedAckTests
{
    private static readonly Callsign LocalCall = new("M9YYY", 0);
    private static readonly Callsign PeerCall  = new("GB7BPQ", 1);
    private static readonly TimeSpan Budget    = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The marquee #385 repro: a five-frame burst followed by the peer's
    /// checkpoint poll (RR P=1), with no ack flushed in between. The poll
    /// must be answered F=1 with V(R) current as of ALL frames received
    /// before it — covering the whole burst — and that F=1 must be the ONLY
    /// ack on the wire: no per-frame RR dribble before it (the five keyups
    /// that deafened the port) and no stale-N(R) ack after it (the pending
    /// delayed ack is cancelled by the poll response). Red before the fix:
    /// five separate RRs nr=1..5 precede the F=1 answer.
    /// </summary>
    [Fact]
    public async Task Burst_then_poll_answers_F1_with_current_VR_and_nothing_else()
    {
        await using var rig = await Rig.Connect();
        var (modem, time, session) = (rig.Modem, rig.Time, rig.Session);

        // Peer streams five in-sequence I-frames, P=0, back to back.
        for (byte ns = 0; ns < 5; ns++)
        {
            modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: ns,
                info: new[] { ns }, pollBit: false));
        }
        // ... then polls: RR command, P=1 — the v2.2 checkpoint enquiry.
        modem.InjectInbound(Ax25Frame.Rr(LocalCall, PeerCall, nr: 0, isCommand: true, pollFinal: true));

        // The poll answer must reach the wire; nothing else may precede it.
        await modem.SentFrames.WaitForCountAsync(2, Budget);
        var transcript = ParseAll(modem);
        transcript.Should().HaveCount(2,
            "the wire transcript must be exactly [UA, RR F=1] — no per-frame RR dribble (#385)");

        var checkpoint = transcript[1];
        IsRr(checkpoint).Should().BeTrue("the poll is answered with an RR");
        checkpoint.IsResponse.Should().BeTrue("a poll answer is a response frame");
        checkpoint.PollFinal.Should().BeTrue("an inbound P=1 must be answered F=1");
        checkpoint.Nr.Should().Be(5,
            "the F=1 checkpoint answer must carry V(R) current as of every frame received before the poll");

        // The poll response superseded the pending delayed ack: T2 expiry
        // must NOT emit a further (now redundant) RR. Under FakeTimeProvider
        // an armed timer fires synchronously inside Advance, so the count
        // check right after is deterministic.
        time.Advance(session.Context.T2 + TimeSpan.FromMilliseconds(1));
        modem.SentFrames.Count.Should().Be(2,
            "no ack with any N(R) — let alone a stale one — may follow the F=1 checkpoint answer");
        session.Context.VR.Should().Be(5);
    }

    /// <summary>
    /// Coalescing: N in-sequence P=0 I-frames produce exactly ONE RR, sent
    /// when T2 expires, carrying the cumulative N(R). (The two-frame variant
    /// lives in <see cref="Ax25ListenerDelayedAckTests"/>; this is the
    /// full-burst shape from the #385 capture.)
    /// </summary>
    [Fact]
    public async Task Five_frame_burst_coalesces_into_one_cumulative_RR_after_T2()
    {
        await using var rig = await Rig.Connect();
        var (modem, time, session) = (rig.Modem, rig.Time, rig.Session);

        for (byte ns = 0; ns < 5; ns++)
        {
            modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: ns,
                info: new[] { ns }, pollBit: false));
        }

        // Gate the clock advance on the delayed ack being *armed*, not merely on
        // V(R) having advanced. The Connected in-sequence-I-frame transition
        // (SDL t26_i_received_yes_yes_yes_no_yes_no_no) runs its actions in the
        // order: V(R):=V(R)+1 … LM-SEIZE Request (this arms T2 via the listener's
        // sendLinkMux grant) … Set Acknowledge Pending (last). So V(R) is observable
        // from the test thread *before* T2 is armed — advancing the FakeTimeProvider
        // in that window sets the timer's due-time against the already-advanced clock,
        // and it then never fires (no further advance) → the coalesced RR never lands.
        // AcknowledgePending is the final action, so observing it true guarantees the
        // preceding LM-SEIZE/arm has run. (#385 flake — caught on a 2026-06-19 CI run.)
        await ListenerTestSupport.WaitFor(
            () => session.Context.VR == 5 && session.Context.AcknowledgePending, Budget,
            "all five I-frames must be processed and the delayed ack (T2) armed");
        modem.SentFrames.Count.Should().Be(1, "no ack before T2 expires — only the connect UA is on the wire");

        time.Advance(session.Context.T2 + TimeSpan.FromMilliseconds(1));
        await modem.SentFrames.WaitForCountAsync(2, Budget);

        var transcript = ParseAll(modem);
        transcript.Should().HaveCount(2, "five frames coalesce into exactly one ack");
        var ack = transcript[1];
        IsRr(ack).Should().BeTrue();
        ack.Nr.Should().Be(5, "the single RR is cumulative — N(R)=V(R) read at T2 expiry");
        ack.IsResponse.Should().BeTrue();
        ack.PollFinal.Should().BeFalse("a delayed ack is F=0");

        // And the cycle re-arms cleanly: the next frame starts a new burst.
        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 5,
            info: "again"u8.ToArray(), pollBit: false));
        // This is the window the CI flake actually hit: frame ns=5 is the FIRST frame
        // of the new burst, so the single transition that brings V(R) to 6 is also the
        // one that arms T2 — and it sets V(R) before it arms. Gating on V(R)==6 alone
        // races the arm; gate on AcknowledgePending (set last) so the arm is guaranteed
        // done before we advance the clock.
        await ListenerTestSupport.WaitFor(
            () => session.Context.VR == 6 && session.Context.AcknowledgePending, Budget,
            "the next burst's first frame must be processed and its delayed ack (T2) armed");
        time.Advance(session.Context.T2 + TimeSpan.FromMilliseconds(1));
        await modem.SentFrames.WaitForCountAsync(3, Budget);
        var second = ParseAll(modem)[2];
        IsRr(second).Should().BeTrue();
        second.Nr.Should().Be(6, "the next burst gets its own coalesced ack");
    }

    /// <summary>
    /// Piggyback cancellation: if an outgoing I-frame (which carries
    /// N(R)=V(R)) goes out while a delayed ack is pending, the pending RR is
    /// superseded — T2 expiry must not emit a redundant ack. The SDL's
    /// I-command chain runs Clear Acknowledge Pending; the dispatcher hooks
    /// that verb to cancel the armed T2.
    /// </summary>
    [Fact]
    public async Task Outgoing_I_frame_piggyback_cancels_the_pending_delayed_ack()
    {
        await using var rig = await Rig.Connect();
        var (modem, time, session, listener) = (rig.Modem, rig.Time, rig.Session, rig.Listener);

        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 0,
            info: "PING"u8.ToArray(), pollBit: false));
        await ListenerTestSupport.WaitFor(() => session.Context.AcknowledgePending, Budget,
            "the received I-frame must set Ack-Pending");

        // Local reply: the outgoing I-frame piggybacks N(R)=1.
        listener.SendData(session, "PONG"u8.ToArray());
        await modem.SentFrames.WaitForCountAsync(2, Budget);

        var reply = ParseAll(modem)[1];
        IsIFrame(reply).Should().BeTrue("the reply is an I-frame");
        reply.Nr.Should().Be(1, "the reply piggybacks the acknowledgement");

        // T2 expiry must now be a no-op: the ack already went out on the
        // I-frame. (Synchronous under FakeTimeProvider — see above.)
        time.Advance(session.Context.T2 + TimeSpan.FromMilliseconds(1));
        modem.SentFrames.Count.Should().Be(2, "the piggybacked N(R) superseded the delayed ack — no RR follows");
        ParseAll(modem).Count(f => IsRr(f)).Should().Be(0, "no standalone RR was ever needed");
    }

    /// <summary>
    /// T2 = 0 restores the legacy ack-per-frame behaviour (what the SDL
    /// figures draw, and the wire shape before #385): every received
    /// in-sequence I-frame elicits its own immediate RR.
    /// </summary>
    [Fact]
    public async Task T2_zero_restores_ack_per_frame()
    {
        await using var rig = await Rig.Connect(t2: TimeSpan.Zero);
        var (modem, time, session) = (rig.Modem, rig.Time, rig.Session);

        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 0,
            info: "ONE"u8.ToArray(), pollBit: false));
        modem.InjectInbound(Ax25Frame.I(LocalCall, PeerCall, nr: 0, ns: 1,
            info: "TWO"u8.ToArray(), pollBit: false));

        // Two immediate RRs — no time advance needed.
        await modem.SentFrames.WaitForCountAsync(3, Budget);
        var transcript = ParseAll(modem);
        var rrs = transcript.Where(IsRr).ToList();
        rrs.Should().HaveCount(2, "T2=0 means one RR per received I-frame");
        rrs[0].Nr.Should().Be(1);
        rrs[1].Nr.Should().Be(2);
        session.Context.VR.Should().Be(2);
        _ = time; // virtual clock never advanced: the acks were immediate
    }

    // ─── Fixture plumbing ────────────────────────────────────────────────

    /// <summary>A started listener with one inbound-accepted Connected session,
    /// on a virtual clock. Disposing tears the listener (and its pump) down.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public required LoopbackModem Modem { get; init; }
        public required FakeTimeProvider Time { get; init; }
        public required Ax25Session Session { get; init; }
        public required Ax25Listener Listener { get; init; }

        public static async Task<Rig> Connect(TimeSpan? t2 = null)
        {
            var modem = new LoopbackModem();
            var time = new FakeTimeProvider();
            var listener = new Ax25Listener(modem, new Ax25ListenerOptions
            {
                MyCall = LocalCall,
                T2 = t2,
            }, time);

            var accepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.SessionAccepted += (_, e) => accepted.TrySetResult(e.Session);
            await listener.StartAsync();

            modem.InjectInbound(Ax25Frame.Sabm(LocalCall, PeerCall));
            var session = await accepted.Task.WithTimeout(Budget);
            await modem.SentFrames.WaitForCountAsync(1, Budget);   // the UA
            await ListenerTestSupport.WaitFor(() => session.CurrentState == "Connected", Budget);
            return new Rig { Modem = modem, Time = time, Session = session, Listener = listener };
        }

        public ValueTask DisposeAsync() => Listener.DisposeAsync();
    }

    private static List<Ax25Frame> ParseAll(LoopbackModem modem)
        => modem.SentFrames.SnapshotList()
            .Select(b =>
            {
                Ax25Frame.TryParse(b.Span, out var f).Should().BeTrue("every sent frame must parse");
                return f!;
            })
            .ToList();

    /// <summary>S-frame RR test, mod-8: low nibble 0x01.</summary>
    private static bool IsRr(Ax25Frame frame) => (frame.Control & 0x0F) == 0x01;

    /// <summary>I-frame test, mod-8: low control bit 0.</summary>
    private static bool IsIFrame(Ax25Frame frame) => (frame.Control & 0x01) == 0x00;
}
