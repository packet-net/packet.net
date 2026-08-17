using Packet.Kiss;

namespace Packet.Kiss.Tests;

public class KissDecoderTests
{
    [Fact]
    public void Decodes_Empty_Data_Frame()
    {
        var d = new KissDecoder();
        var frames = d.Push(new byte[] { 0xC0, 0x00, 0xC0 });
        frames.Count.Should().Be(1);
        frames[0].Port.Should().Be((byte)0);
        frames[0].Command.Should().Be(KissCommand.Data);
        frames[0].Payload.Should().BeEmpty();
    }

    [Fact]
    public void Decodes_Frame_With_Payload()
    {
        var d = new KissDecoder();
        var frames = d.Push(new byte[] { 0xC0, 0x10, 0x01, 0x02, 0x03, 0xC0 });
        frames.Count.Should().Be(1);
        frames[0].Port.Should().Be((byte)1);
        frames[0].Command.Should().Be(KissCommand.Data);
        frames[0].Payload.Should().Equal(new byte[] { 0x01, 0x02, 0x03 });
    }

    [Fact]
    public void Decodes_Escaped_FEND()
    {
        var d = new KissDecoder();
        var frames = d.Push(new byte[] { 0xC0, 0x00, 0xDB, 0xDC, 0xC0 });
        frames.Count.Should().Be(1);
        frames[0].Payload.Should().Equal(new byte[] { 0xC0 });
    }

    [Fact]
    public void Decodes_Escaped_FESC()
    {
        var d = new KissDecoder();
        var frames = d.Push(new byte[] { 0xC0, 0x00, 0xDB, 0xDD, 0xC0 });
        frames.Count.Should().Be(1);
        frames[0].Payload.Should().Equal(new byte[] { 0xDB });
    }

    [Fact]
    public void Drops_Empty_Inter_Frame_FENDs()
    {
        var d = new KissDecoder();
        var frames = d.Push(new byte[] { 0xC0, 0xC0, 0xC0, 0x00, 0xAA, 0xC0, 0xC0 });
        frames.Count.Should().Be(1);
        frames[0].Payload.Should().Equal(new byte[] { 0xAA });
    }

    [Fact]
    public void Reassembles_Across_Chunks()
    {
        var d = new KissDecoder();
        var f1 = d.Push(new byte[] { 0xC0, 0x00, 0x01 });
        var f2 = d.Push(new byte[] { 0x02, 0xDB });
        var f3 = d.Push(new byte[] { 0xDC, 0x03, 0xC0 });

        f1.Count.Should().Be(0);
        f2.Count.Should().Be(0);
        f3.Count.Should().Be(1);
        f3[0].Payload.Should().Equal(new byte[] { 0x01, 0x02, 0xC0, 0x03 });
    }

    [Fact]
    public void Decodes_Multiple_Frames_In_One_Push()
    {
        var d = new KissDecoder();
        var frames = d.Push(new byte[] { 0xC0, 0x00, 0xAA, 0xC0, 0xC0, 0x10, 0xBB, 0xC0 });
        frames.Count.Should().Be(2);
        frames[0].Payload.Should().Equal(new byte[] { 0xAA });
        frames[1].Port.Should().Be((byte)1);
        frames[1].Payload.Should().Equal(new byte[] { 0xBB });
    }

    [Fact]
    public void An_oversize_partial_frame_is_dropped_and_the_stream_resynchronises()
    {
        // KISS has no length field, so a peer that never sends a FEND (wrong baud
        // rate, a raw-serial peer, noise) used to grow the buffer without limit
        // (packet-net/packet.net#696).
        var d = new KissDecoder(maxFrameLength: 64);
        var flood = new byte[8192];
        Array.Fill(flood, (byte)0x41);

        var frames = d.Push(flood);

        frames.Should().BeEmpty();
        d.OversizeFramesDropped.Should().Be(1, "the run past the bound is counted once, not per byte");

        // Everything up to the next FEND is discarded, including the tail of the
        // oversize run; the frame after it decodes normally.
        d.Push(new byte[] { 0x42, 0x43 }).Should().BeEmpty();
        var recovered = d.Push(new byte[] { 0xC0, 0x10, 0xBB, 0xC0 });
        recovered.Should().HaveCount(1);
        recovered[0].Port.Should().Be((byte)1);
        recovered[0].Payload.Should().Equal(new byte[] { 0xBB });
        d.OversizeFramesDropped.Should().Be(1);
    }

    [Fact]
    public void A_frame_exactly_at_the_bound_still_decodes()
    {
        var d = new KissDecoder(maxFrameLength: 64);
        var payload = new byte[63];          // + the command byte = 64 decoded octets
        Array.Fill(payload, (byte)0x5A);

        var frames = d.Push(KissEncoder.Encode(port: 0, KissCommand.Data, payload));

        frames.Should().HaveCount(1);
        frames[0].Payload.Should().Equal(payload);
        d.OversizeFramesDropped.Should().Be(0);
    }

    [Fact]
    public void The_default_bound_admits_any_ax25_frame()
    {
        // A maximum-size AX.25 frame (8 digis + header + N1 = 256) is ~330 octets.
        var d = new KissDecoder();
        var payload = new byte[400];
        Array.Fill(payload, (byte)0x7E);

        var frames = d.Push(KissEncoder.Encode(port: 0, KissCommand.Data, payload));

        frames.Should().HaveCount(1);
        frames[0].Payload.Should().Equal(payload);
    }

    [Fact]
    public void Reset_Discards_Partial_Frame()
    {
        var d = new KissDecoder();
        d.Push(new byte[] { 0xC0, 0x00, 0xAA });
        d.Reset();
        var frames = d.Push(new byte[] { 0xC0, 0x10, 0xBB, 0xC0 });
        frames.Count.Should().Be(1);
        frames[0].Port.Should().Be((byte)1);
        frames[0].Payload.Should().Equal(new byte[] { 0xBB });
    }
}
