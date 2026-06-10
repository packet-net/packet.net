using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Packet.Rhp2.Tests;

/// <summary>
/// Pins the RHPv2 frame format: 2-byte big-endian length prefix + payload,
/// and the reader's three outcomes (frame / clean-EOF null / truncation throw).
/// </summary>
public class FramingTests
{
    [Fact]
    public async Task WriteFrameAsync_then_ReadFrameAsync_round_trips_payload()
    {
        using var ms = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("""{"type":"auth"}""");
        await RhpFraming.WriteFrameAsync(ms, payload);

        ms.Position = 0;
        var got = await RhpFraming.ReadFrameAsync(ms);

        got.Should().Equal(payload);
    }

    [Fact]
    public void Header_is_big_endian_for_a_300_byte_payload()
    {
        // 300 = 0x012C: proves both header bytes carry length and that the
        // high byte comes first.
        using var ms = new MemoryStream();
        RhpFraming.WriteFrame(ms, new byte[300]);

        var bytes = ms.ToArray();
        bytes[0].Should().Be(0x01);
        bytes[1].Should().Be(0x2C);
        bytes.Length.Should().Be(2 + 300);
    }

    [Fact]
    public async Task ReadFrameAsync_returns_null_at_clean_end_of_stream()
    {
        // Peer hung up between frames — the normal end of a conversation.
        using var empty = new MemoryStream();
        var got = await RhpFraming.ReadFrameAsync(empty);
        got.Should().BeNull();
    }

    [Fact]
    public async Task ReadFrameAsync_round_trips_a_zero_length_frame()
    {
        // 00 00 is a legal frame: empty payload, not an error and not EOF.
        using var ms = new MemoryStream([0x00, 0x00]);
        var got = await RhpFraming.ReadFrameAsync(ms);
        got.Should().NotBeNull();
        got.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadFrameAsync_throws_on_truncated_header()
    {
        using var ms = new MemoryStream([0x01]);
        var act = async () => await RhpFraming.ReadFrameAsync(ms);
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task ReadFrameAsync_throws_on_truncated_body()
    {
        // Header promises 16 bytes; only 2 arrive.
        using var ms = new MemoryStream([0x00, 0x10, (byte)'h', (byte)'i']);
        var act = async () => await RhpFraming.ReadFrameAsync(ms);
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task WriteFrameAsync_rejects_payload_over_65535_bytes()
    {
        using var ms = new MemoryStream();
        var oversize = new byte[RhpFraming.MaxPayloadLength + 1];
        var act = async () => await RhpFraming.WriteFrameAsync(ms, oversize);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void WriteFrame_rejects_payload_over_65535_bytes()
    {
        using var ms = new MemoryStream();
        var act = () => RhpFraming.WriteFrame(ms, new byte[RhpFraming.MaxPayloadLength + 1]);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void WriteFrame_accepts_payload_of_exactly_65535_bytes()
    {
        using var ms = new MemoryStream();
        RhpFraming.WriteFrame(ms, new byte[RhpFraming.MaxPayloadLength]);
        ms.Length.Should().Be(2 + RhpFraming.MaxPayloadLength);
        ms.ToArray()[..2].Should().Equal(0xFF, 0xFF);
    }

    [Fact]
    public async Task ReadFrameAsync_reassembles_across_partial_reads()
    {
        // TCP gives no read-boundary guarantees; a frame may dribble in one
        // byte at a time and the reader must loop until complete.
        using var assembled = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes("""{"type":"close","handle":3}""");
        RhpFraming.WriteFrame(assembled, payload);

        using var trickle = new OneByteAtATimeStream(assembled.ToArray());
        var got = await RhpFraming.ReadFrameAsync(trickle);

        got.Should().Equal(payload);
    }

    /// <summary>Read-only stream yielding a single byte per read, to exercise reassembly loops.</summary>
    private sealed class OneByteAtATimeStream(byte[] bytes) : Stream
    {
        private int position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (position >= bytes.Length)
            {
                return 0;
            }

            buffer[offset] = bytes[position++];
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
