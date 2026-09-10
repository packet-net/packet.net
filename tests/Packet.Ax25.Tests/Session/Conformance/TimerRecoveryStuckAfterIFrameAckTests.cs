using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using SdlEvent = Packet.Ax25.Sdl.Ax25Event;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Evidence for hypothesis H1 of the two-station model checker
/// (<c>packet-net/ax25sdl</c> <c>docs/explorer.md</c>, "Novel findings"): a
/// station whose Timer Recovery completes on an <b>I frame</b> rather than on an
/// F=1 supervisory response stays in figc4.5 with T1 stopped, nothing
/// outstanding and RC untouched, and figc4.5 has no T3 arm to poll it out again.
/// These tests reproduce the model's shortest trace on the real runtime, in both
/// quirk modes, and pin exactly what happens. Evidence only for H1: no runtime
/// change, no quirk. The acknowledgement defect the same trace exposed (#812,
/// paragraph (3) below) is fixed and pinned here as fixed.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the figure says (the same in both modes).</b> figc4.5 leaves
/// TimerRecovery only through <c>t18/t19/t20_..._yes_yes_yes</c> - an RR / RNR /
/// REJ <em>response</em> with F=1 whose N(r) equals V(s) (Start T3, RC := 0,
/// next Connected). Its <c>I_received</c> arm runs <c>Check_I_Frame_Acknowledged</c>,
/// whose N(r) = V(s) path does V(a) := N(r), Stop T1, Start T3 - and then
/// stays in TimerRecovery. figc4.5 has no <c>T3_expiry</c> arm at all (figc4.4
/// has <c>t13_t3_expiry</c>). Its non-F=1 RR arm (<c>t18_rr_received_no_no_yes</c>)
/// advances V(a) but does not touch T1, and its DL-DATA / I-frame-pops arm
/// starts T1 if it is not running without resetting RC. So once the ack that
/// empties the window rides on an I frame: T1 is stopped, T3 is running into an
/// arm that does not exist, RC keeps the value it had, and the next T1 expiry
/// counts from there. direwolf hit this on air and coded around it
/// (<c>src/ax25_link.c</c> <c>i_frame()</c> at eda1383f: "we sometimes got stuck in
/// state 4 and rc crept up slowly ... Eventually rc could reach the limit and we
/// would get an error"; it adds an exit to state 3 when <c>va == vs</c>).
/// </para>
/// <para>
/// <b>What the runtime adds.</b> (1) An event with no matching transition is
/// handed to <c>onUnhandledEvent</c> or, as here and in the listener, silently
/// dropped with the state unchanged (<see cref="Ax25Session.PostEvent"/>); so the
/// T3 expiry that eventually fires from the scheduler does nothing. (2) The
/// ax25spec#9 quirk <see cref="Ax25SessionQuirks.Ax25Spec9AckProgressResetsRc"/>
/// (default on) clamps RC to 1 at a T1 expiry that follows V(a) progress, which
/// bounds the creep at "one extra retry" per recovery; StrictlyFaithful runs the
/// figure as drawn, RC ratchets across repeated recoveries on a working link,
/// and after N2 lifetime hiccups the link is declared dead. Nothing in the
/// runtime exits TimerRecovery on an I-frame ack or handles T3 there: the
/// transition map is the generated figc4.5 table and the only TimerRecovery
/// special-casing in <see cref="Ax25Session"/> is the I-frame-queue drain gate.
/// (3) Not part of H1 but visible in the same trace, and fixed in #812: figc4.5's
/// partial-ack arm <c>t18_rr_received_yes_yes_no</c> ends with
/// <c>Set Acknowledge Pending</c> and raises no LM-SEIZE. In the figure the
/// retransmitted frames then pop off the queue and <c>t03</c>'s
/// <c>Clear Acknowledge Pending</c> resets the flag; the runtime replays them
/// inline instead (<c>ActionDispatcher.EmitOldIFrame</c>, so a retransmission
/// keeps its original N(s)) and never runs the pops arm. It used to leave the
/// flag set, so when B's I frame arrived figc4.5's in-sequence arm found
/// Acknowledge Pending already set (<c>t22_i_received_yes_yes_yes_no_yes_no_yes</c>)
/// and did nothing, and B's data went unacknowledged until B's own T1 poll. The
/// inline replay now carries the pop's acknowledgement bookkeeping (the frame
/// goes out with N(r) = V(r), the flag clears, and the arm's trailing Set is not
/// applied over it, since in the figure the pops run after the arm), so B's I
/// frame is acknowledged through the ordinary delayed-ack path (Set Acknowledge
/// Pending, LM-SEIZE, RR F=0 on the confirm). Pinned below as fixed; the H1
/// stranding itself is the figure's and is unchanged.
/// </para>
/// <para>
/// <b>Why the ack lands on an I frame here.</b> The harness grants LM-SEIZE at
/// once, so a receiver normally flushes its delayed ack as a bare RR before it
/// could piggyback. Each cycle is therefore stepped with <see cref="TwoStationHarness.DrainOnce"/>
/// so B's DL-DATA is posted after the retransmission is delivered and before the
/// queued LM-SEIZE confirm is processed: figc4.4's I-frame-pops arm runs
/// <c>Clear Acknowledge Pending</c>, the confirm then finds nothing pending, and
/// the ack goes out only on the I frame - which is what a busy bidirectional
/// link does on air whenever the peer has data queued.
/// </para>
/// </remarks>
public class TimerRecoveryStuckAfterIFrameAckTests
{
    private static TwoStationHarness Build(bool strictlyFaithful, int n2 = TwoStationHarness.DefaultN2) =>
        strictlyFaithful
            ? TwoStationHarness.BuildStrictlyFaithful(k: 4, n2: n2)
            : TwoStationHarness.Build(k: 4, n2: n2);

    private static bool IsT1Running(TwoStationHarness.Endpoint e) => e.Scheduler.IsRunning("T1");
    private static bool IsT3Running(TwoStationHarness.Endpoint e) => e.Scheduler.IsRunning("T3");

    /// <summary>Fire A's T1 without pumping, so the poll/final cycle can be
    /// stepped one hop at a time. Advances past the larger live T1V, as
    /// <see cref="TwoStationHarness.AdvanceT1"/> does (Select_T1_Value can grow it).</summary>
    private static void FireT1(TwoStationHarness h)
    {
        var t1 = h.A.Context.T1V > h.B.Context.T1V ? h.A.Context.T1V : h.B.Context.T1V;
        h.Time.Advance(t1 + TimeSpan.FromMilliseconds(20));
    }

    /// <summary>Send two frames from A, losing the second on the wire, then
    /// bring the first half of the recovery to the point where A's retransmission
    /// has just been delivered at B (B has Set Ack Pending, its LM-SEIZE confirm
    /// is still queued). Returns with A in TimerRecovery holding the second
    /// frame unacked.</summary>
    private static void LoseSecondFrameAndRetransmit(TwoStationHarness h, byte first, byte second)
    {
        byte lostNs = (byte)((h.A.Context.VS + 1) % h.A.Context.Modulus);
        int drops = 0;
        h.Link.Drop = f => f.FrameType == Ax25FrameType.I
            && f.Source.Callsign.Equals(h.A.Context.Local)
            && f.Ns == lostNs
            && drops++ == 0;
        h.SubmitBurst(h.A, first, second);
        h.Link.Drop = null;
        drops.Should().Be(1, "exactly one copy of A's second frame is lost");
        h.B.Delivered.Should().Contain(p => p[0] == first, "the first frame gets through");
        h.B.Delivered.Should().NotContain(p => p[0] == second, "the second frame is lost");
        int outstanding = (h.A.Context.VS - h.A.Context.VA + h.A.Context.Modulus) % h.A.Context.Modulus;
        outstanding.Should().Be(1, "B's RR acked the first frame; the lost one is still outstanding");

        FireT1(h);
        h.A.State.Should().Be("TimerRecovery", "the lost frame's T1 expiry polls A into figc4.5");
        h.DrainOnce();   // B answers the poll: RR F=1 acknowledging the first frame only.
        h.DrainOnce();   // A: partial ack (t18_rr_received_yes_yes_no) -> Invoke_Retransmission, stays in TimerRecovery.
        h.A.State.Should().Be("TimerRecovery", "a partial F=1 ack leaves A in TimerRecovery");
        h.B.Inbound.Should().HaveCount(1, "the retransmission is on its way to B");
        h.DrainOnce();   // B delivers the retransmission: Set Ack Pending + LM-SEIZE Request (confirm queued, not yet processed).
        h.B.Delivered.Should().Contain(p => p[0] == second, "the retransmission gets through");
    }

    /// <summary>One full H1 cycle: two frames from A with the second lost, the
    /// F=1 poll answer acking only the first, the retransmission delivered, and
    /// the ack for it carried on a B I frame (<paramref name="peerPayload"/>)
    /// rather than an RR.</summary>
    private static void RecoverWithAckOnIFrame(TwoStationHarness h, byte first, byte second, byte peerPayload)
    {
        LoseSecondFrameAndRetransmit(h, first, second);

        int seenByA = h.A.ReceivedFromPeer.Count;
        // B has data of its own: post it before the LM-SEIZE confirm is processed so
        // the I frame carries the ack and Clear Acknowledge Pending cancels the RR.
        h.B.Submitted.Add(new[] { peerPayload });
        h.B.Session.PostEvent(new DlDataRequest(new[] { peerPayload }));
        h.Settle();

        var fromB = h.A.ReceivedFromPeer.Skip(seenByA).ToList();
        fromB.Should().HaveCount(1, "B's ack of the retransmission travels on exactly one frame");
        fromB[0].FrameType.Should().Be(Ax25FrameType.I, "that frame is B's I frame, not an RR");
        fromB[0].Nr.Should().Be(h.A.Context.VS, "its N(r) acknowledges everything A sent");
        h.A.Delivered.Should().Contain(p => p[0] == peerPayload, "A delivers B's data while recovering");
    }

    private static void AssertStuck(TwoStationHarness h, int expectedRc)
    {
        h.A.State.Should().Be("TimerRecovery", "figc4.5 has no exit on an I-frame ack");
        h.A.Context.VA.Should().Be(h.A.Context.VS, "nothing is outstanding");
        IsT1Running(h.A).Should().BeFalse("Check_I_Frame_Acknowledged's N(r)=V(s) path stops T1");
        IsT3Running(h.A).Should().BeTrue("... and starts T3");
        h.A.Context.RC.Should().Be(expectedRc, "nothing on the I-frame path touches RC");
        h.B.State.Should().Be("Connected", "B saw an ordinary exchange");
    }

    // ─── The figure's arms, as pinned on the runtime table ─────────────────

    [Fact]
    public void Figc4_5_table_has_no_T3_expiry_arm_and_no_I_frame_exit()
    {
        DataLink_TimerRecovery.Transitions.Should().NotContain(t => t.On == SdlEvent.T3Expiry,
            "figc4.5 draws no T3 expiry arm (this is the figure, not a runtime choice)");
        DataLink_Connected.Transitions.Should().Contain(t => t.On == SdlEvent.T3Expiry,
            "figc4.4 does have one (t13_t3_expiry), so the keepalive only works from Connected");
        DataLink_TimerRecovery.Transitions
            .Where(t => t.On == SdlEvent.IReceived && t.Next == "Connected")
            .Select(t => t.Id)
            .Should().Equal(new[] { "t22_i_received_no" },
                "the only I_received arm of figc4.5 that reaches Connected is the DL-ERROR(O) discard of an I frame carried as a response; no acknowledgement path leads out");
    }

    // ─── The stuck state ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ack_on_an_I_frame_leaves_A_in_TimerRecovery_with_T1_stopped_and_T3_running(bool strictlyFaithful)
    {
        var h = Build(strictlyFaithful);
        h.Connect();

        RecoverWithAckOnIFrame(h, 0x00, 0x01, 0xB0);

        AssertStuck(h, expectedRc: 1);
        h.A.Signals.OfType<DataLinkErrorIndication>().Should().BeEmpty("the exchange is error-free from A's point of view");

        // Alongside H1 (runtime, not the figure - see the class remarks, #812): the
        // inline retransmission carried A's acknowledgement, so the partial-ack
        // arm's Set Acknowledge Pending did not outlive it, and B's I frame was
        // acknowledged at once through the ordinary delayed-ack path rather than
        // waiting for B's own T1 poll.
        h.A.Context.AcknowledgePending.Should().BeFalse("the inline retransmission ran the pop arm's Clear Acknowledge Pending, and B's I frame has since been acknowledged");
        var ack = h.B.ReceivedFromPeer.Last();
        ack.FrameType.Should().Be(Ax25FrameType.Rr, "A acknowledged B's I frame with an RR without waiting for B's poll");
        ack.IsCommand.Should().BeFalse("... as a response");
        ack.PollFinal.Should().BeFalse("... with F=0: the delayed ack flushed on LM-SEIZE confirm, not a poll answer");
        ack.Nr.Should().Be(h.B.Context.VS, "... acknowledging B's frame");
        h.B.Context.VS.Should().Be(1);
        h.B.Context.VA.Should().Be(1, "B's I frame is acknowledged");
        IsT1Running(h.B).Should().BeFalse("B has nothing outstanding, so B has no reason to poll");

        // There is nothing left for B's T1 to clear up: advancing past it sends
        // nothing in either direction, and A stays where H1 leaves it.
        int seenByB = h.B.ReceivedFromPeer.Count;
        int seenByA = h.A.ReceivedFromPeer.Count;
        h.AdvanceT1();

        h.B.ReceivedFromPeer.Count.Should().Be(seenByB, "B's window was already closed by the RR, so no poll was needed");
        h.A.ReceivedFromPeer.Count.Should().Be(seenByA, "and B had nothing to send");
        h.B.State.Should().Be("Connected");
        h.B.Context.VA.Should().Be(h.B.Context.VS);
        h.A.State.Should().Be("TimerRecovery", "only receiving an F=1 response is an exit, and B has no reason to send one");
        IsT1Running(h.A).Should().BeFalse();
        h.AssertConverged();
    }

    /// <summary>Control: the same loss, but B has nothing to send, so the ack of
    /// the retransmission arrives on an RR (F=0). figc4.5's non-F=1 RR arm leaves
    /// T1 running, T1 expires, the poll/final cycle completes and A returns to
    /// Connected with RC reset. The only difference from the stuck case is which
    /// frame carried the ack.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Control_ack_on_an_RR_completes_recovery_through_the_poll_cycle(bool strictlyFaithful)
    {
        var h = Build(strictlyFaithful);
        h.Connect();

        LoseSecondFrameAndRetransmit(h, 0x00, 0x01);
        h.Settle();   // B's LM-SEIZE confirm flushes the ack as a bare RR (F=0).

        h.A.Context.VA.Should().Be(h.A.Context.VS, "the RR acknowledges the retransmission");
        h.A.State.Should().Be("TimerRecovery", "a non-F=1 RR does not complete recovery");
        IsT1Running(h.A).Should().BeTrue("t18_rr_received_no_no_yes does not stop T1, so the poll cycle will run");

        h.AdvanceT1();   // T1 expiry -> RR P=1 -> RR F=1 N(r)=V(s) -> Connected.

        h.A.State.Should().Be("Connected", "an F=1 response with N(r)=V(s) is figc4.5's exit");
        h.A.Context.RC.Should().Be(0, "t18_rr_received_yes_yes_yes resets RC");
        IsT3Running(h.A).Should().BeTrue();
        h.AssertConverged();
    }

    // ─── What T3 does there, and what does not release A ───────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T3_expiry_in_TimerRecovery_fires_no_transition_and_emits_nothing(bool strictlyFaithful)
    {
        var h = Build(strictlyFaithful);
        h.Connect();
        RecoverWithAckOnIFrame(h, 0x00, 0x01, 0xB0);
        AssertStuck(h, expectedRc: 1);

        int fired = 0;
        h.A.Session.TransitionFired += (_, _) => fired++;
        int seenByB = h.B.ReceivedFromPeer.Count;
        int signals = h.A.Signals.Count;

        // The same event the scheduler posts when A's T3 runs out (the harness
        // routes timer expiry through the dispatcher's onTimerExpiry hook into
        // PostEvent, exactly as the listener does).
        h.Inject(h.A, new T3Expiry());

        fired.Should().Be(0, "figc4.5 has no T3_expiry arm; Ax25Session drops the event");
        h.B.ReceivedFromPeer.Count.Should().Be(seenByB, "no keepalive poll goes out");
        h.A.Signals.Count.Should().Be(signals, "nothing is raised upward");
        h.A.State.Should().Be("TimerRecovery");
        IsT1Running(h.A).Should().BeFalse("T1 is still stopped: no enquiry was transmitted");
        h.A.Context.RC.Should().Be(1);
    }

    /// <summary>The peer's own keepalive (figc4.4 T3 -> RR command P=1) does
    /// not release A either: figc4.5 answers a command P=1 with an F=1
    /// <em>response</em> (Enquiry Response) and stays put; only an F=1 response
    /// <em>received</em> exits, and B only sends one when A polls, which A can
    /// no longer do with T1 stopped and T3 unarmed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Peer_keepalive_poll_is_answered_but_does_not_release_A(bool strictlyFaithful)
    {
        var h = Build(strictlyFaithful);
        h.Connect();
        RecoverWithAckOnIFrame(h, 0x00, 0x01, 0xB0);
        AssertStuck(h, expectedRc: 1);

        int seenByB = h.B.ReceivedFromPeer.Count;
        var poll = Ax25Frame.Rr(h.A.Context.Local, h.A.Context.Remote, nr: h.B.Context.VR, isCommand: true, pollFinal: true).ToBytes();
        h.InjectFrameBytes(h.A, poll);

        var answer = h.B.ReceivedFromPeer.Skip(seenByB).ToList();
        answer.Should().ContainSingle("A answers the poll");
        answer[0].FrameType.Should().Be(Ax25FrameType.Rr);
        answer[0].IsCommand.Should().BeFalse("Enquiry Response is a response");
        answer[0].PollFinal.Should().BeTrue("with F=1");
        h.A.State.Should().Be("TimerRecovery", "t18_rr_received_no_yes_yes stays in TimerRecovery");
        IsT1Running(h.A).Should().BeFalse();
        h.B.State.Should().Be("Connected");
    }

    // ─── The retry-count creep ─────────────────────────────────────────────

    /// <summary>New data from A restarts T1 (figc4.5 I-frame-pops: Stop T3,
    /// Start T1) with RC still 1, so when that T1 expires it is counted as the
    /// second failure of a recovery that has, in fact, already succeeded. A
    /// station that had returned to Connected would count it as the first
    /// (figc4.4 t12_t1_expiry: RC := 1). Same in both modes: the #9 clamp only
    /// acts on RC &gt; 1.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Next_data_from_A_restarts_T1_with_RC_not_reset(bool strictlyFaithful)
    {
        var h = Build(strictlyFaithful);
        h.Connect();
        RecoverWithAckOnIFrame(h, 0x00, 0x01, 0xB0);
        AssertStuck(h, expectedRc: 1);

        // Lose the new frame so its T1 expiry is observable.
        h.Link.Drop = f => f.FrameType == Ax25FrameType.I && f.Source.Callsign.Equals(h.A.Context.Local);
        h.Submit(h.A, 0x02);
        h.Link.Drop = null;

        h.A.State.Should().Be("TimerRecovery");
        IsT1Running(h.A).Should().BeTrue("t03_i_frame_pops_off_queue_no_no_no starts T1");
        IsT3Running(h.A).Should().BeFalse("... and stops T3");
        h.A.Context.RC.Should().Be(1, "sending data does not reset RC");

        FireT1(h);

        h.A.Context.RC.Should().Be(2, "figc4.5 t21_t1_expiry_no counts on from the stale RC=1, where a Connected station would have set RC := 1");

        h.Settle();
        for (int r = 0; r < 8 && h.A.State != "Connected"; r++)
        {
            h.AdvanceT1();
        }

        h.A.State.Should().Be("Connected", "the poll/final cycle eventually completes on an F=1 RR");
        h.AssertConverged();
    }

    /// <summary>Repeat the cycle on a working link. The figure ratchets RC by one
    /// per cycle; the default ax25spec#9 clamp holds it at 2 (RC &gt; 1 with
    /// progress since the last expiry is clamped to 1 before the increment).
    /// StrictlyFaithful reaches N2 and tears the link down (t21_t1_expiry_yes_no:
    /// DL-ERROR I, DM, Disconnected) even though every frame was getting through.</summary>
    [Fact]
    public void Repeated_cycles_creep_RC_bounded_by_default_unbounded_when_strictly_faithful()
    {
        const int n2 = 3;

        var dflt = Build(strictlyFaithful: false, n2: n2);
        var strict = Build(strictlyFaithful: true, n2: n2);
        dflt.Connect();
        strict.Connect();

        RecoverWithAckOnIFrame(dflt, 0x00, 0x01, 0xB0);
        RecoverWithAckOnIFrame(strict, 0x00, 0x01, 0xB0);
        AssertStuck(dflt, expectedRc: 1);
        AssertStuck(strict, expectedRc: 1);

        RecoverWithAckOnIFrame(dflt, 0x02, 0x03, 0xB1);
        RecoverWithAckOnIFrame(strict, 0x02, 0x03, 0xB1);
        AssertStuck(dflt, expectedRc: 2);
        AssertStuck(strict, expectedRc: 2);

        RecoverWithAckOnIFrame(dflt, 0x04, 0x05, 0xB2);
        RecoverWithAckOnIFrame(strict, 0x04, 0x05, 0xB2);
        AssertStuck(dflt, expectedRc: 2);     // clamped to 1, then +1
        AssertStuck(strict, expectedRc: 3);   // ratchets: now RC == N2

        // Fourth loss: the T1 expiry finds RC == N2 on the faithful figure.
        LoseSecondFrameAndRetransmitOrDie(dflt, 0x06, 0x07);
        dflt.A.State.Should().Be("TimerRecovery", "the #9 clamp keeps RC below N2 on a link that is making progress");
        dflt.A.Context.RC.Should().Be(2);

        LoseSecondFrameAndRetransmitOrDie(strict, 0x06, 0x07);
        strict.A.State.Should().Be("Disconnected", "figc4.5 t21_t1_expiry_yes_no: RC reached N2 across three successful recoveries");
        strict.A.Signals.OfType<DataLinkErrorIndication>().Should().NotBeEmpty("DL-ERROR I");
        strict.A.Signals.OfType<DataLinkDisconnectIndication>().Should().ContainSingle();
        strict.Settle();
        strict.B.State.Should().Be("Disconnected", "B honours A's DM");
    }

    /// <summary>The first half of a cycle up to and including A's T1 expiry -
    /// stops there, so the caller can look at what the expiry did.</summary>
    private static void LoseSecondFrameAndRetransmitOrDie(TwoStationHarness h, byte first, byte second)
    {
        byte lostNs = (byte)((h.A.Context.VS + 1) % h.A.Context.Modulus);
        int drops = 0;
        h.Link.Drop = f => f.FrameType == Ax25FrameType.I
            && f.Source.Callsign.Equals(h.A.Context.Local)
            && f.Ns == lostNs
            && drops++ == 0;
        h.SubmitBurst(h.A, first, second);
        h.Link.Drop = null;
        FireT1(h);
    }
}
