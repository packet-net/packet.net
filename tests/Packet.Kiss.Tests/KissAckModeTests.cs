using Packet.Kiss;

namespace Packet.Kiss.Tests;

public class KissAckModeTests
{
    [Fact]
    public void Build_Send_Frame_Has_AckMode_Command_And_Sequence_Prefix()
    {
        // tag 0xA5B6, payload "AB" → wire: FEND, 0x0C, 0xA5, 0xB6, 'A', 'B', FEND
        var wire = KissAckMode.BuildSendFrame(port: 0, sequenceTag: 0xA5B6, ax25Payload: new byte[] { 0x41, 0x42 });
        wire.Should().Equal(new byte[] { 0xC0, 0x0C, 0xA5, 0xB6, 0x41, 0x42, 0xC0 });
    }

    [Fact]
    public void Build_Send_Frame_Encodes_Port_In_Upper_Nibble()
    {
        // port 5, ackmode command → command byte (5 << 4) | 0x0C = 0x5C
        var wire = KissAckMode.BuildSendFrame(port: 5, sequenceTag: 0x0001, ax25Payload: ReadOnlySpan<byte>.Empty);
        wire.Should().Equal(new byte[] { 0xC0, 0x5C, 0x00, 0x01, 0xC0 });
    }

    [Fact]
    public void Build_Send_Frame_Escapes_Sequence_Bytes_When_They_Are_FEND()
    {
        // seqHi = 0xC0 (FEND) → must escape to FESC TFEND
        var wire = KissAckMode.BuildSendFrame(port: 0, sequenceTag: 0xC0DB, ax25Payload: ReadOnlySpan<byte>.Empty);
        wire.Should().Equal(new byte[] { 0xC0, 0x0C, 0xDB, 0xDC, 0xDB, 0xDD, 0xC0 });
    }

    [Fact]
    public void TryParseAcknowledgement_Recovers_The_Sequence_Tag_From_A_Two_Byte_Payload()
    {
        var frame = new KissFrame(0, KissCommand.AckMode, new byte[] { 0x12, 0x34 });
        KissAckMode.TryParseAcknowledgement(frame, out var tag).Should().BeTrue();
        tag.Should().Be((ushort)0x1234);
    }

    [Fact]
    public void TryParseAcknowledgement_Rejects_Non_AckMode_Commands()
    {
        var frame = new KissFrame(0, KissCommand.Data, new byte[] { 0x12, 0x34 });
        KissAckMode.TryParseAcknowledgement(frame, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseAcknowledgement_Rejects_Wrong_Payload_Length()
    {
        // 3-byte payload = a data frame (seq + 1 byte of AX.25), not an echo.
        var withData = new KissFrame(0, KissCommand.AckMode, new byte[] { 0x12, 0x34, 0x99 });
        KissAckMode.TryParseAcknowledgement(withData, out _).Should().BeFalse();

        var empty = new KissFrame(0, KissCommand.AckMode, Array.Empty<byte>());
        KissAckMode.TryParseAcknowledgement(empty, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParseDataFrame_Splits_Sequence_From_Payload()
    {
        var frame = new KissFrame(0, KissCommand.AckMode, new byte[] { 0x12, 0x34, 0x41, 0x42, 0x43 });
        KissAckMode.TryParseDataFrame(frame, out var tag, out var data).Should().BeTrue();
        tag.Should().Be((ushort)0x1234);
        data.ToArray().Should().Equal(new byte[] { 0x41, 0x42, 0x43 });
    }

    [Fact]
    public void TryParseDataFrame_Rejects_The_Two_Byte_Echo()
    {
        // A 2-byte payload is the TX-complete echo, not a data frame.
        var frame = new KissFrame(0, KissCommand.AckMode, new byte[] { 0x12, 0x34 });
        KissAckMode.TryParseDataFrame(frame, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Round_Trip_Send_Frame_Then_Decode_Recovers_Tag_And_Payload()
    {
        byte[] payload = { 0xA8, 0x8A, 0xA6, 0xC0, 0xDB, 0x03, 0xF0, 0x68, 0x69 };
        var wire = KissAckMode.BuildSendFrame(port: 0, sequenceTag: 0xBEEF, ax25Payload: payload);

        var decoder = new KissDecoder();
        var frames = decoder.Push(wire);
        frames.Should().HaveCount(1);

        var decoded = frames[0];
        decoded.Command.Should().Be(KissCommand.AckMode);
        KissAckMode.TryParseDataFrame(decoded, out var tag, out var roundTripPayload).Should().BeTrue();
        tag.Should().Be((ushort)0xBEEF);
        roundTripPayload.ToArray().Should().Equal(payload);
    }
}
