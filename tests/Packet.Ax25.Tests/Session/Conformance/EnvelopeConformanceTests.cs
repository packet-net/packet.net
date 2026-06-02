using AwesomeAssertions;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Phase H — the rest of the normal-operation envelope beyond the basic
/// happy path: mod-128 (extended), segmentation/reassembly, and RNR/busy
/// flow control. Same oracle, same convergence requirement.
/// </summary>
public class EnvelopeConformanceTests
{
    [Fact(Skip = "mod-128 connected-mode data transfer unimplemented (N(S)/N(R) on 2-byte control) — packet.net#239; un-skip when that lands")]
    public void Mod128_extended_window_transfer_converges()
    {
        var h = TwoStationHarness.Build(extended: true, k: 8);
        h.Connect();
        h.A.Context.IsExtended.Should().BeTrue("DL-CONNECT with IsExtended must negotiate mod-128 via SABME");
        h.B.Context.IsExtended.Should().BeTrue("the peer must adopt mod-128 on receiving SABME");

        for (byte i = 0; i < 8; i++) h.Submit(h.A, i);

        h.B.Delivered.Select(p => p[0]).Should().Equal(Enumerable.Range(0, 8).Select(i => (byte)i));
        h.AssertConverged();
    }

    [Fact]
    public void Connect_initiated_by_B_also_works()
    {
        var h = TwoStationHarness.Build();
        h.ConnectFrom(h.B);
        h.A.State.Should().Be("Connected");
        h.B.State.Should().Be("Connected");

        h.Submit(h.B, 0xB0);
        h.A.Delivered.Should().ContainSingle().Which.Should().Equal(new byte[] { 0xB0 });
        h.AssertConverged();
    }

    [Fact]
    public void Segmentation_reassembly_roundtrips_a_large_payload()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // A 300-byte upper-layer payload split by §6.6 segmentation (info-field
        // max 64 → 63 data bytes/segment → 5 segments), each carried as its own
        // I-frame across the data link, then reassembled on the receiver.
        var payload = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var segments = Segmenter.Segment(payload, maxInfoFieldBytes: 64);
        segments.Count.Should().Be(5);

        var sent = 0;
        foreach (var seg in segments)
        {
            h.Submit(h.A, seg);
            if (++sent % 4 == 0) h.FlushAcks();   // reopen the k=4 window mid-series
        }
        h.FlushAcks();

        // Every segment delivered, in order; reassembly reconstructs the payload.
        h.B.Delivered.Should().HaveCount(5);
        var reassembler = new Reassembler();
        byte[]? reassembled = null;
        foreach (var info in h.B.Delivered)
        {
            var done = reassembler.Push(info);
            if (done is not null) reassembled = done;
        }
        reassembled.Should().Equal(payload);
        h.AssertConverged();
    }

    [Fact(Skip = "DL-FLOW-OFF doesn't enter own-receiver-busy from a clean state (figc4.4 Yes/No branch swap) — ax25sdl#60; un-skip when resolved")]
    public void Rnr_flow_control_pauses_then_resumes_the_sender()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        h.Submit(h.A, 0xA0);
        h.B.Delivered.Should().ContainSingle("first frame delivers normally");

        // B goes busy → RNR → A must register peer-busy and stop sending.
        h.SetBusy(h.B);
        h.A.Context.PeerReceiverBusy.Should().BeTrue("A must observe B's RNR (peer busy)");

        h.Submit(h.A, 0xA1);
        h.B.Delivered.Should().ContainSingle("while the peer is busy the second frame must NOT be delivered");

        // B clears busy → RR → A resumes and the queued frame flows.
        h.ClearBusy(h.B);
        h.FlushAcks();

        h.B.Delivered.Select(p => p[0]).Should().Equal(new byte[] { 0xA0, 0xA1 });
        h.AssertConverged();
    }
}
