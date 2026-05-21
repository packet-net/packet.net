using AwesomeAssertions;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// AX.25 §6.6 Segmenter / Reassembler round-trip tests. Phase 2 exit
/// criterion: *"Segmenter reassembles a 1500-byte payload across
/// multiple I-frames."*
/// </summary>
public class SegmenterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(254)]     // exactly one segment (255 bytes of payload, with 1-byte header = 256)
    [InlineData(255)]     // exactly one segment, payload fills info field
    [InlineData(256)]     // overflows into a 2nd segment by 1 byte
    [InlineData(1500)]    // Phase 2 exit criterion size
    [InlineData(16320)]   // exactly MaxSegments × (N1-1) bytes at N1=256
    public void Roundtrip_Through_Segmenter_And_Reassembler_Recovers_Original(int payloadSize)
    {
        var payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++) payload[i] = (byte)((i * 31) & 0xFF);  // deterministic non-trivial pattern

        var segments = Segmenter.Segment(payload, maxInfoFieldBytes: 256);

        byte[]? completed = null;
        var reassembler = new Reassembler();
        foreach (var seg in segments)
            completed = reassembler.Push(seg);

        completed.Should().NotBeNull($"the last segment must produce a completed payload for size {payloadSize}");
        completed!.Length.Should().Be(payloadSize);
        completed.Should().Equal(payload);
    }

    [Fact]
    public void First_Segment_Has_First_Bit_Set_And_Remaining_Count_Equals_Segments_After_It()
    {
        // 1500 bytes at N1=256 → per-segment payload 255 bytes → 6 segments.
        var segments = Segmenter.Segment(new byte[1500], maxInfoFieldBytes: 256);
        segments.Should().HaveCount(6);

        // Segment 0: First=1, remaining=5
        ((segments[0][0] & Segmenter.FirstBit) != 0).Should().BeTrue();
        (segments[0][0] & Segmenter.CountMask).Should().Be(5);

        // Last segment: First=0, remaining=0
        ((segments[5][0] & Segmenter.FirstBit) != 0).Should().BeFalse();
        (segments[5][0] & Segmenter.CountMask).Should().Be(0);
    }

    [Fact]
    public void Segment_Throws_If_Payload_Exceeds_Capacity()
    {
        // MaxSegments (64) × (N1-1=255) = 16320 is the limit; one more byte overflows.
        var act = () => Segmenter.Segment(new byte[16321], maxInfoFieldBytes: 256);
        act.Should().Throw<ArgumentException>().WithMessage("*64*");
    }

    [Fact]
    public void Segment_Throws_If_MaxInfoFieldBytes_Too_Small()
    {
        var act = () => Segmenter.Segment(new byte[10], maxInfoFieldBytes: 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Reassembler_Throws_On_Non_First_Segment_Without_Prior_First()
    {
        // Header byte: First=0, remaining=5 (i.e. "I'm segment 1 of 6"
        // but we never saw the "First" segment).
        var reassembler = new Reassembler();
        var stray = new byte[] { 0x05, 1, 2, 3 };

        var act = () => reassembler.Push(stray);
        act.Should().Throw<InvalidOperationException>().WithMessage("*non-First*");
    }

    [Fact]
    public void Reassembler_Throws_On_Out_Of_Sequence_Segments()
    {
        // First says "5 more after me", then we hand it a segment that
        // says "3 more after me" instead of the expected "4 more".
        var reassembler = new Reassembler();
        reassembler.Push(new byte[] { 0x80 | 5, 0xAA });   // First, expects 4 next
        var act = () => reassembler.Push(new byte[] { 3, 0xBB });
        act.Should().Throw<InvalidOperationException>().WithMessage("*out of sequence*");
    }

    [Fact]
    public void New_First_Segment_Mid_Stream_Discards_Partial_State()
    {
        var reassembler = new Reassembler();
        // Start a 6-segment series.
        reassembler.Push(new byte[] { 0x80 | 5, 1, 2 });
        reassembler.Push(new byte[] { 4, 3, 4 });
        // Receive a fresh "First" instead of continuing — the spec's
        // implicit behaviour is to restart the reassembly buffer.
        // Verify by completing the fresh series and checking we got
        // only the fresh bytes, not the prior partials.
        var completed = reassembler.Push(new byte[] { 0x80 | 0, 0xDE, 0xAD });
        completed.Should().Equal(new byte[] { 0xDE, 0xAD });
    }
}
