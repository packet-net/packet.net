using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Unit tests for the <see cref="SegmentationLayer"/> shim — the AX.25 v2.2
/// §2.4 / §6.6 segmentation-reassembly boundary process. Cover the send-side
/// decision (segment / pass-through / reject), the receive-side reassembly,
/// the PID handling, and the gating on the negotiated segmenter flag.
/// </summary>
public class SegmentationLayerTests
{
    private static Ax25SessionContext Ctx(int n1, bool segmenterEnabled, Ax25SessionQuirks? quirks = null) => new()
    {
        Local = new Callsign("M0LTEA", 1),
        Remote = new Callsign("M0LTEB", 2),
        N1 = n1,
        SegmenterReassemblerEnabled = segmenterEnabled,
        Quirks = quirks ?? Ax25SessionQuirks.Default,
    };

    [Fact]
    public void Send_passes_a_payload_within_N1_through_unchanged()
    {
        var seg = new SegmentationLayer(Ctx(n1: 256, segmenterEnabled: true));
        var payload = new byte[100];

        var requests = seg.BuildSendRequests(payload, Ax25Frame.PidNoLayer3);

        requests.Should().ContainSingle("a within-N1 payload is one unsegmented request");
        requests[0].Pid.Should().Be(Ax25Frame.PidNoLayer3, "pass-through preserves the Layer-3 PID");
        requests[0].Data.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Send_passes_an_exactly_N1_payload_through_unchanged()
    {
        var seg = new SegmentationLayer(Ctx(n1: 256, segmenterEnabled: true));
        var payload = new byte[256];   // exactly N1 — fits one info field, no segment byte

        var requests = seg.BuildSendRequests(payload);

        requests.Should().ContainSingle("a payload of exactly N1 octets fits a single info field");
        requests[0].Pid.Should().Be(Ax25Frame.PidNoLayer3);
    }

    [Fact]
    public void Send_segments_an_over_N1_payload_into_PID_0x08_requests()
    {
        var seg = new SegmentationLayer(Ctx(n1: 64, segmenterEnabled: true));
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

        var requests = seg.BuildSendRequests(payload);

        // 300 bytes, 63 payload bytes/segment (N1−1) ⇒ ceil(300/63) = 5 segments.
        requests.Should().HaveCount(5);
        requests.Should().OnlyContain(r => r.Pid == Ax25Frame.PidSegmented,
            "each segment is carried as an I-frame with the segmented PID 0x08");
        requests.Should().OnlyContain(r => r.Data.Length <= 64,
            "no segment info field may exceed N1");
    }

    [Fact]
    public void Send_rejects_an_over_N1_payload_when_the_segmenter_is_not_negotiated()
    {
        var seg = new SegmentationLayer(Ctx(n1: 256, segmenterEnabled: false));
        var payload = new byte[300];   // > N1, segmenter off

        var act = () => seg.BuildSendRequests(payload);

        act.Should().Throw<InvalidOperationException>(
            "an over-N1 payload cannot be sent without segmentation; it must be rejected cleanly, " +
            "never truncated or sent as an oversize frame")
            .WithMessage("*segmenter/reassembler has not been negotiated*");
    }

    [Fact]
    public void Send_passes_a_within_N1_payload_even_when_the_segmenter_is_off()
    {
        var seg = new SegmentationLayer(Ctx(n1: 256, segmenterEnabled: false));
        var payload = new byte[200];

        var requests = seg.BuildSendRequests(payload);

        requests.Should().ContainSingle("a within-N1 payload never needs the segmenter — pass through regardless");
    }

    [Fact]
    public void Receive_passes_a_non_segment_indication_through_unchanged()
    {
        var seg = new SegmentationLayer(Ctx(n1: 256, segmenterEnabled: true));
        var ind = new DataLinkDataIndication(new byte[] { 1, 2, 3 }, Ax25Frame.PidNoLayer3);

        var delivered = seg.OnDataIndication(ind);

        delivered.Should().BeSameAs(ind, "a non-0x08 indication is returned unchanged");
    }

    [Fact]
    public void Receive_reassembles_a_segmented_series_and_delivers_on_the_last_segment()
    {
        var n1 = 64;
        var send = new SegmentationLayer(Ctx(n1, segmenterEnabled: true));
        var recv = new SegmentationLayer(Ctx(n1, segmenterEnabled: true));
        var payload = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();

        var segments = send.BuildSendRequests(payload);

        DataLinkDataIndication? final = null;
        var deliveredBeforeLast = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            // The segment travels as an I-frame; on the receive side it surfaces as
            // a DL-DATA indication with PID 0x08 and the segment info field.
            var ind = new DataLinkDataIndication(segments[i].Data, segments[i].Pid);
            var result = recv.OnDataIndication(ind);
            if (i < segments.Count - 1)
            {
                result.Should().BeNull("no payload is delivered until the final segment arrives");
                if (result is not null) deliveredBeforeLast++;
            }
            else
            {
                final = result;
            }
        }

        deliveredBeforeLast.Should().Be(0);
        final.Should().NotBeNull("the last segment completes the series");
        final!.Info.ToArray().Should().Equal(payload, "reassembly reconstructs the original payload exactly");
    }

    [Fact]
    public void Default_quirk_preserves_the_original_L3_PID_through_a_segmented_series()
    {
        // Default (SegmentFirstCarriesL3Pid on): the first segment carries the
        // original L3 PID after the F/X byte (Dire Wolf's format), so the
        // reassembler recovers it and delivers the reassembled payload with that
        // ORIGINAL PID — not PidNoLayer3. This both interoperates with Dire Wolf
        // and fixes the figure-literal PID-loss limitation. Pin that contract.
        var n1 = 16;
        var send = new SegmentationLayer(Ctx(n1, segmenterEnabled: true));   // default quirks
        var recv = new SegmentationLayer(Ctx(n1, segmenterEnabled: true));
        var payload = new byte[40];

        var segments = send.BuildSendRequests(payload, Ax25Frame.PidNetRom);
        DataLinkDataIndication? final = null;
        foreach (var s in segments)
            final = recv.OnDataIndication(new DataLinkDataIndication(s.Data, s.Pid)) ?? final;

        final.Should().NotBeNull();
        final!.Pid.Should().Be(Ax25Frame.PidNetRom,
            "with the inner-PID quirk on (default) the original L3 PID is carried on the first segment and recovered on reassembly");
        final.Info.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void StrictlyFaithful_uses_the_figure_literal_format_and_delivers_PidNoLayer3()
    {
        // StrictlyFaithful (SegmentFirstCarriesL3Pid off): Figure 6.2 literally —
        // no inner-PID octet, so the original L3 PID cannot be recovered and the
        // reassembled payload is delivered as PidNoLayer3. The first segment's
        // info field is [F/X][data] (no inner-PID octet between them). Pin the
        // strict figure-literal contract alongside the default.
        var n1 = 16;
        var quirks = Ax25SessionQuirks.StrictlyFaithful;
        var send = new SegmentationLayer(Ctx(n1, segmenterEnabled: true, quirks));
        var recv = new SegmentationLayer(Ctx(n1, segmenterEnabled: true, quirks));
        var payload = new byte[40];

        var segments = send.BuildSendRequests(payload, Ax25Frame.PidNetRom);

        // The first segment's second byte is the start of PAYLOAD (figure-literal),
        // NOT the inner PID. (payload[0] == 0, distinct from PidNetRom 0xCF.)
        ((segments[0].Data.Span[0] & Segmenter.FirstBit) != 0).Should().BeTrue("segment 0 is the First segment");
        segments[0].Data.Span[1].Should().Be((byte)0,
            "figure-literal: byte after the F/X octet is the first payload byte, not an inner-PID octet");

        DataLinkDataIndication? final = null;
        foreach (var s in segments)
            final = recv.OnDataIndication(new DataLinkDataIndication(s.Data, s.Pid)) ?? final;

        SegmentationLayer.FigureLiteralReassembledPid.Should().Be(Ax25Frame.PidNoLayer3);
        final.Should().NotBeNull();
        final!.Pid.Should().Be(Ax25Frame.PidNoLayer3,
            "the figure-literal format carries no inner L3 PID, so reassembled data is delivered as PidNoLayer3");
        final.Info.ToArray().Should().Equal(payload);
    }
}
