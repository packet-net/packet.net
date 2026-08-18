using Packet.Kiss;

namespace Packet.Kiss.Tests;

public class KissEncoderTests
{
    [Fact]
    public void Empty_Data_Frame_Is_FEND_CMD_FEND()
    {
        var encoded = KissEncoder.Encode(port: 0, KissCommand.Data, ReadOnlySpan<byte>.Empty);
        encoded.Should().Equal(new byte[] { 0xC0, 0x00, 0xC0 });
    }

    [Fact]
    public void Port_Is_Encoded_In_Upper_Nibble()
    {
        var encoded = KissEncoder.Encode(port: 1, KissCommand.Data, ReadOnlySpan<byte>.Empty);
        encoded[1].Should().Be((byte)0x10);

        var encoded5 = KissEncoder.Encode(port: 5, KissCommand.TxDelay, new byte[] { 0x32 });
        encoded5[1].Should().Be((byte)0x51);
    }

    [Fact]
    public void Encode_Escapes_FEND_In_Payload()
    {
        var encoded = KissEncoder.Encode(port: 0, KissCommand.Data, new byte[] { 0xC0 });
        encoded.Should().Equal(new byte[] { 0xC0, 0x00, 0xDB, 0xDC, 0xC0 });
    }

    [Fact]
    public void Encode_Escapes_FESC_In_Payload()
    {
        var encoded = KissEncoder.Encode(port: 0, KissCommand.Data, new byte[] { 0xDB });
        encoded.Should().Equal(new byte[] { 0xC0, 0x00, 0xDB, 0xDD, 0xC0 });
    }

    [Fact]
    public void Encode_Passes_Through_Non_Special_Bytes()
    {
        var encoded = KissEncoder.Encode(port: 0, KissCommand.Data, new byte[] { 0x01, 0x02, 0x03 });
        encoded.Should().Equal(new byte[] { 0xC0, 0x00, 0x01, 0x02, 0x03, 0xC0 });
    }

    [Fact]
    public void Encode_Rejects_Port_Greater_Than_15()
    {
        ((Action)(() => KissEncoder.Encode(port: 16, KissCommand.Data, ReadOnlySpan<byte>.Empty)))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    public void Encoded_Length_Within_MaxEncodedLength(int payloadLen)
    {
        var payload = new byte[payloadLen];
        Array.Fill(payload, (byte)0xC0); // every byte needs escaping
        var encoded = KissEncoder.Encode(0, KissCommand.Data, payload);
        encoded.Length.Should().BeLessThanOrEqualTo(KissEncoder.MaxEncodedLength(payloadLen));
    }

    [Fact]
    public void Encodes_When_Command_Byte_Is_FEND()
    {
        // port=12, cmd=Data → command byte = 0xC0 (FEND). This is the
        // multi-drop / port-nibble pothole the spec text overlooks.
        // The encoder must escape the command byte so the frame decodes.
        var encoded = KissEncoder.Encode(port: 12, KissCommand.Data, ReadOnlySpan<byte>.Empty);
        encoded.Should().Equal(new byte[] { 0xC0, 0xDB, 0xDC, 0xC0 });
    }

    [Fact]
    public void Encodes_When_Command_Byte_Is_FESC()
    {
        // port=13 + cmd=0xB would give 0xDB (FESC). 0xB isn't a defined
        // KissCommand value but the encoder must still produce decodable
        // output - receivers shouldn't be able to derail us on a hostile
        // or future command code.
        var encoded = KissEncoder.Encode(port: 13, (KissCommand)0xB, ReadOnlySpan<byte>.Empty);
        encoded.Should().Equal(new byte[] { 0xC0, 0xDB, 0xDD, 0xC0 });
    }
}
