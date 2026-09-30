using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// v2.2 arc V4b (and the V4 headline exit criterion) - the <b>wired</b>
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
            if (dropped)
            {
                return false;
            }

            if (!f.Source.Callsign.Equals(h.A.Context.Local))
            {
                return false;
            }

            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived)
            {
                return false;
            }

            if (f.Ns != 2)
            {
                return false;            // mode-aware 7-bit N(S)
            }

            dropped = true;
            return true;
        };

        h.SubmitLarge(h.A, payload);
        for (int r = 0; r < 40 && h.B.Delivered.Count == 0; r++)
        {
            h.AdvanceT1();
        }

        dropped.Should().BeTrue("the scenario must actually have dropped a segment for the test to mean anything");
        h.A.ReceivedFromPeer.Any(f => Ax25FrameClassifier.Classify(f) is SrejReceived).Should().BeTrue(
            "the lost segment must be recovered SELECTIVELY — B must have put an SREJ on the wire (not merely a T1-timeout go-back-N)");
        h.B.Delivered.Should().ContainSingle(
            "after SREJ recovers the lost segment, the receiver reassembles exactly ONE payload");
        h.B.Delivered[0].Should().Equal(payload,
            "the reassembled payload must be byte-for-byte the original, despite the dropped-then-recovered segment");
        h.AssertConverged();
    }

    // Same, but REJ go-back-N recovery (SREJ off) - the lost segment is recovered
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
            if (dropped)
            {
                return false;
            }

            if (!f.Source.Callsign.Equals(h.A.Context.Local))
            {
                return false;
            }

            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived)
            {
                return false;
            }

            if (f.Ns != 1)
            {
                return false;
            }

            dropped = true;
            return true;
        };

        h.SubmitLarge(h.A, payload);
        for (int r = 0; r < 40 && h.B.Delivered.Count == 0; r++)
        {
            h.AdvanceT1();
        }

        dropped.Should().BeTrue();
        h.B.Delivered.Should().ContainSingle();
        h.B.Delivered[0].Should().Equal(payload);
        h.AssertConverged();
    }

    [Fact]
    public void Over_N1_byte_stream_on_a_session_without_the_negotiated_segmenter_goes_as_N1_sized_I_frames()
    {
        // v2.0 / not-negotiated (packet.net#808): a byte-stream payload over N1 is split at
        // N1, so every I frame stays within what the peer accepts, and the peer sees the same
        // bytes in order across two indications. Before, one oversize I frame went out and the
        // peer re-established the link (figc4.4 t26, DL-ERROR N), losing the data.
        var h = TwoStationHarness.Build(k: 8, segmenter: false);
        h.Connect();

        int n1 = h.A.Context.N1;
        var payload = Enumerable.Range(0, n1 + 50).Select(i => (byte)i).ToArray();
        h.Submit(h.A, payload[..n1]);
        h.Submit(h.A, payload[n1..]);
        h.FlushAcks();

        h.B.Delivered.Should().HaveCount(2, "two I frames, N1 bytes and the remainder");
        h.B.Delivered.SelectMany(d => d).Should().Equal(payload);
        h.A.State.Should().Be("Connected", "no oversize frame, so no re-establishment");
        h.AssertConverged();
    }

    [Fact]
    public void Over_N1_byte_stream_posted_raw_is_split_by_the_session_itself()
    {
        // The raw path (a DlDataRequest posted straight to the session, as axcall did in
        // packet.net#808) gets the same rule, so no caller can put an oversize I frame on the air.
        var h = TwoStationHarness.Build(k: 8, segmenter: false);
        h.Connect();

        int n1 = h.A.Context.N1;
        var payload = Enumerable.Range(0, n1 + 50).Select(i => (byte)(i * 7)).ToArray();
        h.A.Submitted.Add(payload[..n1]);
        h.A.Submitted.Add(payload[n1..]);
        h.A.Session.PostEvent(new DlDataRequest(payload));
        h.FlushAcks();

        h.B.Delivered.Should().HaveCount(2);
        h.B.Delivered.SelectMany(d => d).Should().Equal(payload);
        h.A.State.Should().Be("Connected");
        h.AssertConverged();
    }

    [Fact]
    public void Over_N1_datagram_on_a_session_without_the_negotiated_segmenter_is_rejected()
    {
        // A Layer-3 datagram over N1 cannot be split without breaking it and cannot be sent
        // whole, so it is rejected cleanly at the shim and at the session alike.
        var h = TwoStationHarness.Build(k: 8, segmenter: false);
        h.Connect();

        var payload = new byte[h.A.Context.N1 + 50];
        var viaShim = () => h.SubmitLarge(h.A, payload, Ax25Frame.PidNetRom);
        viaShim.Should().Throw<InvalidOperationException>().WithMessage("*segmenter/reassembler has not been negotiated*");

        var raw = () => h.A.Session.PostEvent(new DlDataRequest(payload, Ax25Frame.PidNetRom));
        raw.Should().Throw<InvalidOperationException>().WithMessage("*segmenter/reassembler has not been negotiated*");
        h.A.State.Should().Be("Connected", "nothing went on the air");
    }

    // Default format (SegmentFirstCarriesL3Pid on) - the wired round-trip must
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

    // StrictlyFaithful (SegmentFirstCarriesL3Pid off) - the wired round-trip uses
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
