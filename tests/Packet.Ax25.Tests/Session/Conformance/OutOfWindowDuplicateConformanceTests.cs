using AwesomeAssertions;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Behavioural guard for the ax25spec#40 out-of-window duplicate disposal, drawn
/// in figc4.4 and figc4.5 since Packet.Ax25.Sdl 0.11.0 as the decision
/// "V(r) &lt; N(s) &lt; V(r) + k?" on the out-of-sequence I-frame arm.
/// </summary>
/// <remarks>
/// <para>
/// This is the fault seen on air on 2026-09-09, axcall (M0LTE) connected over RF
/// to GB7RDG (LinBPQ 6.0.24.79). The peer's checkpoint retransmission, driven by
/// a stale F=1 N(R), redelivered an I-frame this end had already acknowledged.
/// Without a receive-window guard the figure treated it as a fresh out-of-sequence
/// frame and answered with an SREJ; with an empty sender queue that SREJ names a
/// frame that will never exist, so nothing clears the exception and §4.4.4 re-sends
/// it on every poll. The link livelocked.
/// </para>
/// <para>
/// X.25 §2.4.6.4(a) has always disposed of such a frame, and direwolf implements it
/// (<c>is_ns_in_window</c>); the AX.25 figures simply never drew it. Between #242 and
/// this the behaviour was supplied by the <c>Ax25Spec40DiscardOutOfWindowIFrames</c>
/// quirk; that quirk is retired and the disposal now comes from the tables, so these
/// assertions hold under <see cref="Ax25SessionQuirks.StrictlyFaithful"/> too, which
/// is exactly what the retirement bought.
/// </para>
/// </remarks>
public class OutOfWindowDuplicateConformanceTests
{
    // B sends two frames, A acknowledges them, so A's V(r) reaches 2. With k = 4
    // the granted window is then the open interval (2, 6): N(s) = 1 is a frame A
    // has already received and acknowledged, which is the duplicate case.
    private const byte AlreadyAcknowledgedNs = 1;

    private static TwoStationHarness ConnectedWithVrAtTwo(bool srej, bool strictlyFaithful = false)
    {
        var h = strictlyFaithful
            ? TwoStationHarness.BuildStrictlyFaithful(srej: srej, k: 4)
            : TwoStationHarness.Build(srej: srej, k: 4);
        h.Connect();
        h.Submit(h.B, 0x01);
        h.Submit(h.B, 0x02);
        h.Settle();
        h.FlushAcks();
        h.A.Context.VR.Should().Be((byte)2, "the two delivered frames must have advanced A's receive state variable");
        return h;
    }

    private static void InjectDuplicate(TwoStationHarness h, bool poll)
        => h.InjectFrameBytes(h.A, Ax25Frame.I(
            destination: h.A.Context.Local,
            source: h.A.Context.Remote,
            nr: 0,
            ns: AlreadyAcknowledgedNs,
            info: new byte[] { 0x99 },
            pollBit: poll).ToBytes());

    [Theory]
    [InlineData(true)]   // SREJ negotiated: the arm that livelocked on air
    [InlineData(false)]  // REJ mode: the same decision precedes the srej_enabled split
    public void A_duplicate_behind_Vr_raises_no_reject_exception(bool srej)
    {
        var h = ConnectedWithVrAtTwo(srej);
        int deliveredBefore = h.A.Signals.OfType<DataLinkDataIndication>().Count();

        InjectDuplicate(h, poll: false);
        h.Settle();

        h.B.ReceivedFromPeer.Where(f => Ax25FrameClassifier.Classify(f) is SrejReceived).Should().BeEmpty(
            "no frame is missing, so there is no receive sequence number an SREJ could coherently name");
        h.B.ReceivedFromPeer.Where(f => Ax25FrameClassifier.Classify(f) is RejReceived).Should().BeEmpty(
            "an N(s) outside the receive window is a duplicate, not a gap, so it establishes no reject exception");
        h.A.Context.VR.Should().Be((byte)2, "a discarded duplicate must not move the receive state variable");
        h.A.Signals.OfType<DataLinkDataIndication>().Count().Should().Be(deliveredBefore,
            "the duplicate's information field is discarded, never delivered a second time");
    }

    [Fact]
    public void A_duplicate_behind_Vr_is_answered_only_when_it_polls()
    {
        var h = ConnectedWithVrAtTwo(srej: true);
        int quiescent = h.B.ReceivedFromPeer.Count;

        InjectDuplicate(h, poll: false);
        h.Settle();
        int afterUnpolled = h.B.ReceivedFromPeer.Count;
        afterUnpolled.Should().Be(quiescent,
            "a duplicate that does not poll draws no response at all - not an SREJ, not an RR");

        InjectDuplicate(h, poll: true);
        h.Settle();

        var answers = h.B.ReceivedFromPeer.Skip(afterUnpolled).ToList();
        answers.Should().ContainSingle("a discarded duplicate draws exactly one response, and only because P=1");
        Ax25FrameClassifier.Classify(answers[0]).Should().BeOfType<RrReceived>(
            "the figure answers the poll with RR, not with a reject of any kind");
        answers[0].PollFinal.Should().BeTrue("the response to a P=1 command carries F=1");
        answers[0].Nr.Should().Be((byte)2, "the RR reports V(r), which the duplicate did not move");
    }

    [Fact]
    public void The_disposal_survives_the_strictly_faithful_preset()
    {
        // The point of retiring the Ax25Spec40 quirk: this is no longer a
        // workaround the faithful preset switches off, it is what the figure draws.
        var h = ConnectedWithVrAtTwo(srej: true, strictlyFaithful: true);

        InjectDuplicate(h, poll: false);
        h.Settle();

        h.B.ReceivedFromPeer.Where(f => Ax25FrameClassifier.Classify(f) is SrejReceived or RejReceived).Should().BeEmpty(
            "figc4.4 itself carries the window guard now, so strict conformance discards the duplicate too");
        h.A.Context.VR.Should().Be((byte)2);
    }
}
