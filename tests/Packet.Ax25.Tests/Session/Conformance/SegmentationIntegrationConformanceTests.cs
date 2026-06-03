using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// v2.2 arc V4b (and the V4 headline exit criterion) — the <b>wired</b>
/// segmentation/reassembly path end-to-end over the two-station harness, as
/// opposed to <see cref="EnvelopeConformanceTests.Segmentation_reassembly_roundtrips_a_large_payload"/>
/// which exercises the standalone <see cref="Segmenter"/>/<see cref="Reassembler"/>
/// utilities by hand. Here the §6.6 shim (<see cref="SegmentationLayer"/>) is
/// wired into the session's DL boundary: <c>SubmitLarge</c> segments on send and
/// the receive-side reassembler surfaces one reassembled
/// <see cref="DataLinkDataIndication"/>, so the convergence oracle compares one
/// logical submission to one logical delivery.
/// </summary>
public class SegmentationIntegrationConformanceTests
{
    [Fact]
    public void Wired_segmentation_roundtrips_a_large_payload_over_a_mod8_link()
    {
        var h = TwoStationHarness.Build(k: 8, segmenter: true, n1: 64);
        h.Connect();
        h.A.Context.SegmenterReassemblerEnabled.Should().BeTrue();

        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        h.SubmitLarge(h.A, payload);
        h.FlushAcks();

        h.B.Delivered.Should().ContainSingle("the five segments reassemble into ONE upper-layer payload");
        h.B.Delivered[0].Should().Equal(payload);
        h.AssertConverged();
    }

    // The V4 headline exit criterion (plan §5.Z V4): a > N1 payload segments on
    // send, reassembles on receive over a MOD-128 link, AND SREJ recovers a lost
    // segment. Combines V4a (SREJ in the 7-bit space) + V4b (segmentation shim).
    [Fact]
    public void Over_N1_payload_segments_reassembles_and_SREJ_recovers_a_lost_segment_mod128()
    {
        // Extended (mod-128) link, SREJ enabled, segmenter negotiated, small N1 so
        // a modest payload spans several segments. k=16 so the whole series fits the
        // send window (the drop is recovered selectively, not by window stall).
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 16, n2: 40, segmenter: true, n1: 64);
        h.Connect();
        h.A.Context.IsExtended.Should().BeTrue("the link must be mod-128");
        h.A.Context.SegmenterReassemblerEnabled.Should().BeTrue("the segmenter must be negotiated");

        var payload = Enumerable.Range(0, 300).Select(i => (byte)(i * 3 + 1)).ToArray();   // 5 segments at N1=64

        // Drop exactly ONE segment in flight (the third I-frame A sends, N(S)=2),
        // then the channel is clean. SREJ must re-request and recover it.
        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped) return false;
            if (!f.Source.Callsign.Equals(h.A.Context.Local)) return false;
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (f.Ns != 2) return false;            // mode-aware 7-bit N(S)
            dropped = true;
            return true;
        };

        h.SubmitLarge(h.A, payload);
        for (int r = 0; r < 40 && h.B.Delivered.Count == 0; r++) h.AdvanceT1();

        dropped.Should().BeTrue("the scenario must actually have dropped a segment for the test to mean anything");
        h.A.ReceivedFromPeer.Any(f => Ax25FrameClassifier.Classify(f) is SrejReceived).Should().BeTrue(
            "the lost segment must be recovered SELECTIVELY — B must have put an SREJ on the wire (not merely a T1-timeout go-back-N)");
        h.B.Delivered.Should().ContainSingle(
            "after SREJ recovers the lost segment, the receiver reassembles exactly ONE payload");
        h.B.Delivered[0].Should().Equal(payload,
            "the reassembled payload must be byte-for-byte the original, despite the dropped-then-recovered segment");
        h.AssertConverged();
    }

    // Same, but REJ go-back-N recovery (SREJ off) — the lost segment is recovered
    // by retransmitting from the gap; reassembly must still be intact.
    [Fact]
    public void Over_N1_payload_segments_reassembles_and_REJ_recovers_a_lost_segment_mod128()
    {
        var h = TwoStationHarness.Build(extended: true, srej: false, k: 16, n2: 40, segmenter: true, n1: 64);
        h.Connect();

        var payload = Enumerable.Range(0, 250).Select(i => (byte)(255 - i)).ToArray();

        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped) return false;
            if (!f.Source.Callsign.Equals(h.A.Context.Local)) return false;
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (f.Ns != 1) return false;
            dropped = true;
            return true;
        };

        h.SubmitLarge(h.A, payload);
        for (int r = 0; r < 40 && h.B.Delivered.Count == 0; r++) h.AdvanceT1();

        dropped.Should().BeTrue();
        h.B.Delivered.Should().ContainSingle();
        h.B.Delivered[0].Should().Equal(payload);
        h.AssertConverged();
    }

    [Fact]
    public void Over_N1_payload_on_a_session_without_the_negotiated_segmenter_is_rejected()
    {
        // v2.0 / not-negotiated: an over-N1 payload must be rejected cleanly at the
        // shim, never truncated or sent oversize.
        var h = TwoStationHarness.Build(k: 8, segmenter: false);
        h.Connect();

        var payload = new byte[h.A.Context.N1 + 50];
        var act = () => h.SubmitLarge(h.A, payload);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*segmenter/reassembler has not been negotiated*");
    }

    // Default format (SegmentFirstCarriesL3Pid on) — the wired round-trip must
    // PRESERVE the original L3 PID through the segmented series (Dire Wolf's
    // first-segment inner-PID format), not flatten it to PidNoLayer3.
    [Fact]
    public void Default_wired_segmentation_preserves_the_original_L3_PID()
    {
        var h = TwoStationHarness.Build(k: 8, segmenter: true, n1: 64);   // default quirks
        h.Connect();

        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        h.SubmitLarge(h.A, payload, Ax25Frame.PidNetRom);   // a non-default L3 PID
        h.FlushAcks();

        h.B.Delivered.Should().ContainSingle("the segments reassemble into ONE upper-layer payload");
        h.B.Delivered[0].Should().Equal(payload);
        h.B.DeliveredPids.Should().ContainSingle().Which.Should().Be(Ax25Frame.PidNetRom,
            "the default inner-PID format carries the original L3 PID on the first segment and recovers it on reassembly");
        h.AssertConverged();
    }

    // StrictlyFaithful (SegmentFirstCarriesL3Pid off) — the wired round-trip uses
    // the figure-literal format: payload still reassembles intact, but the L3 PID
    // is NOT recovered and the reassembled payload is delivered as PidNoLayer3.
    // Pins Figure 6.2 exactly as drawn alongside the default.
    [Fact]
    public void StrictlyFaithful_wired_segmentation_is_figure_literal_and_delivers_PidNoLayer3()
    {
        var h = TwoStationHarness.Build(k: 8, segmenter: true, n1: 64,
            quirks: Ax25SessionQuirks.StrictlyFaithful);
        h.Connect();

        var payload = Enumerable.Range(0, 300).Select(i => (byte)(i * 5 + 2)).ToArray();
        h.SubmitLarge(h.A, payload, Ax25Frame.PidNetRom);   // send a non-default L3 PID …
        h.FlushAcks();

        h.B.Delivered.Should().ContainSingle("the figure-literal segments still reassemble into ONE payload");
        h.B.Delivered[0].Should().Equal(payload);
        h.B.DeliveredPids.Should().ContainSingle().Which.Should().Be(Ax25Frame.PidNoLayer3,
            "… but the figure-literal format carries no inner PID, so it is lost and the payload is delivered as PidNoLayer3");
        h.AssertConverged();
    }
}
