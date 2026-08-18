using FsCheck.Xunit;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

public class KissRoundTripProperties
{
    /// <summary>
    /// For any port (0-15), any command, and any payload, encoding and
    /// then decoding must return exactly the same frame. This is the
    /// fundamental correctness property for the KISS framing layer.
    /// </summary>
    [Property]
    public void Encode_Then_Decode_Roundtrips(byte port, byte commandRaw, byte[]? payload)
    {
        port = (byte)(port & 0x0F);
        var command = (KissCommand)(commandRaw & 0x0F);
        payload ??= Array.Empty<byte>();

        var encoded = KissEncoder.Encode(port, command, payload);
        var decoder = new KissDecoder();
        var frames = decoder.Push(encoded);

        frames.Count.Should().Be(1, $"port={port} cmd={(byte)command:X} payload={Convert.ToHexString(payload)} encoded={Convert.ToHexString(encoded)}");
        frames[0].Port.Should().Be(port);
        frames[0].Command.Should().Be(command);
        frames[0].Payload.Should().Equal(payload);
    }
}
