using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// AX.25 §6.6 Segmenter / Reassembler round-trip tests. Phase 2 exit
/// criterion: *"Segmenter reassembles a 1500-byte payload across
/// multiple I-frames."*
/// </summary>
/// <remarks>
/// The <see cref="Segmenter"/>/<see cref="Reassembler"/> support two formats:
/// the figure-literal one (no inner-PID octet — pass <c>innerPid: null</c> /
/// construct <c>new Reassembler()</c>) and Dire Wolf's de-facto one (the first
/// segment carries the original L3 PID after the F/X byte — pass an
/// <c>innerPid</c> / construct <c>new Reassembler(expectInnerPid: true)</c>). The
/// session picks between them via
/// <c>Ax25SessionQuirks.SegmentFirstCarriesL3Pid</c> (default on); these tests
/// pin both formats directly at the utility level.
/// </remarks>
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
    [InlineData(16320)]   // 64 segments at N1=256 (mid-range)
    [InlineData(32640)]   // exactly MaxSegments (128) × (N1-1=255) bytes at N1=256 — the 7-bit boundary
    public void Roundtrip_Figure_Literal_Recovers_Original(int payloadSize)
    {
        var payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++) payload[i] = (byte)((i * 31) & 0xFF);  // deterministic non-trivial pattern

        var segments = Segmenter.Segment(payload, maxInfoFieldBytes: 256);   // innerPid: null = figure-literal

        byte[]? completed = null;
        var reassembler = new Reassembler();
        foreach (var seg in segments)
            completed = reassembler.Push(seg);

        completed.Should().NotBeNull($"the last segment must produce a completed payload for size {payloadSize}");
        completed!.Length.Should().Be(payloadSize);
        completed.Should().Equal(payload);
        reassembler.LastRecoveredPid.Should().BeNull("the figure-literal format carries no inner PID to recover");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(254)]     // exactly one segment with the inner-PID format (F/X + inner-PID + 254 data = 256 = N1)
    [InlineData(255)]     // overflows into a 2nd segment (the inner PID stole the last slot of segment 0)
    [InlineData(1500)]    // Phase 2 exit criterion size
    [InlineData(16320)]   // mid-range
    [InlineData(32639)]   // largest payload at MaxSegments with the inner-PID format: ceil((32639+1)/255) = 128
    public void Roundtrip_Inner_Pid_Recovers_Original_And_The_L3_Pid(int payloadSize)
    {
        var payload = new byte[payloadSize];
        for (int i = 0; i < payloadSize; i++) payload[i] = (byte)((i * 31 + 7) & 0xFF);
        const byte l3Pid = Ax25Frame.PidNetRom;   // a non-default L3 PID, to prove it survives

        var segments = Segmenter.Segment(payload, maxInfoFieldBytes: 256, innerPid: l3Pid);

        byte[]? completed = null;
        var reassembler = new Reassembler(expectInnerPid: true);
        foreach (var seg in segments)
            completed = reassembler.Push(seg);

        completed.Should().NotBeNull($"the last segment must produce a completed payload for size {payloadSize}");
        completed!.Length.Should().Be(payloadSize);
        completed.Should().Equal(payload);
        reassembler.LastRecoveredPid.Should().Be(l3Pid,
            "the inner-PID format carries the original L3 PID on the first segment, recovered on reassembly");
    }

    [Fact]
    public void Inner_Pid_Format_Matches_DireWolfs_Worked_Example_Byte_For_Byte()
    {
        // Dire Wolf's own worked example (ax25_link.c dl_data_request comment block):
        // N1 = 4, payload "ABCDEF", PID = 0xF0 →
        //   seg0 = 0x82 0xF0 'A' 'B'   (First + 2-to-follow, inner PID, N1-2 = 2 data bytes)
        //   seg1 = 0x01 'C' 'D' 'E'    (1-to-follow, N1-1 = 3 data bytes)
        //   seg2 = 0x00 'F'            (0-to-follow, last byte)
        var payload = "ABCDEF"u8.ToArray();
        var segments = Segmenter.Segment(payload, maxInfoFieldBytes: 4, innerPid: Ax25Frame.PidNoLayer3);

        segments.Should().HaveCount(3, "Dire Wolf's ceil((6+1)/(4-1)) = 3 segments");
        segments[0].Should().Equal(new byte[] { 0x82, 0xF0, (byte)'A', (byte)'B' });
        segments[1].Should().Equal(new byte[] { 0x01, (byte)'C', (byte)'D', (byte)'E' });
        segments[2].Should().Equal(new byte[] { 0x00, (byte)'F' });
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
    public void Segment_FigureLiteral_Throws_If_Payload_Exceeds_Capacity()
    {
        // MaxSegments (128) × (N1-1=255) = 32640 is the limit; one more byte overflows.
        var act = () => Segmenter.Segment(new byte[32641], maxInfoFieldBytes: 256);
        act.Should().Throw<ArgumentException>().WithMessage("*128*");
    }

    [Fact]
    public void Segment_InnerPid_Throws_If_Payload_Exceeds_Capacity()
    {
        // With the inner-PID octet stealing one slot, the limit is one byte lower:
        // ceil((32639+1)/255) = 128 is OK; 32640 needs 129 segments.
        var act = () => Segmenter.Segment(new byte[32640], maxInfoFieldBytes: 256, innerPid: 0xF0);
        act.Should().Throw<ArgumentException>().WithMessage("*128*");
    }

    [Fact]
    public void Segment_FigureLiteral_Throws_If_MaxInfoFieldBytes_Too_Small()
    {
        var act = () => Segmenter.Segment(new byte[10], maxInfoFieldBytes: 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Segment_InnerPid_Throws_If_MaxInfoFieldBytes_Below_3()
    {
        // The inner-PID first segment needs room for the F/X octet, the inner-PID
        // octet, and at least one data byte — so N1 must be at least 3.
        var act = () => Segmenter.Segment(new byte[10], maxInfoFieldBytes: 2, innerPid: 0xF0);
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
