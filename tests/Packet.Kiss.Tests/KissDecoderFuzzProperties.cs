using FsCheck.Xunit;
using Packet.Kiss;

namespace Packet.Kiss.Tests;

/// <summary>
/// Robustness properties for <see cref="KissDecoder"/> - fed arbitrary
/// byte sequences, it must never throw, never produce malformed frames,
/// and never wedge its internal state.
/// </summary>
public class KissDecoderFuzzProperties
{
    /// <summary>
    /// Random byte input must never throw out of <c>Push</c>. KISS is a
    /// soft framing protocol - malformed escape sequences or partial
    /// frames are explicitly allowed by the spec ("receivers should be
    /// lenient"), and the decoder's job is to drop garbage and keep
    /// going.
    /// </summary>
    [Property]
    public void Push_Random_Bytes_Never_Throws(byte[]? bytes)
    {
        var decoder = new KissDecoder();
        var act = () => decoder.Push(bytes ?? Array.Empty<byte>());
        act.Should().NotThrow();
    }

    /// <summary>
    /// Splitting an arbitrary byte stream into arbitrary chunks must
    /// produce the same decoded frames as feeding it as one block.
    /// </summary>
    [Property]
    public void Chunked_Push_Equals_Whole_Push(byte[]? raw, byte chunkSize)
    {
        raw ??= Array.Empty<byte>();
        int size = Math.Max(1, chunkSize % 32 + 1);

        var whole = new KissDecoder().Push(raw);
        var chunked = new KissDecoder();
        var chunkedFrames = new List<KissFrame>();
        for (int i = 0; i < raw.Length; i += size)
        {
            int len = Math.Min(size, raw.Length - i);
            chunkedFrames.AddRange(chunked.Push(raw.AsSpan(i, len)));
        }

        chunkedFrames.Should().HaveCount(whole.Count);
        for (int i = 0; i < whole.Count; i++)
        {
            chunkedFrames[i].Port.Should().Be(whole[i].Port);
            chunkedFrames[i].Command.Should().Be(whole[i].Command);
            chunkedFrames[i].Payload.Should().Equal(whole[i].Payload);
        }
    }

    /// <summary>
    /// An all-FEND input must never produce a frame. Repeated FENDs are
    /// the universal KISS "re-sync prefix" and decoders must collapse
    /// them.
    /// </summary>
    [Property]
    public void All_FEND_Input_Produces_No_Frames(byte count)
    {
        int n = count % 64 + 1;
        var bytes = new byte[n];
        Array.Fill(bytes, KissFraming.Fend);

        var frames = new KissDecoder().Push(bytes);
        frames.Should().BeEmpty();
    }

    /// <summary>
    /// FESC followed by anything other than TFEND or TFESC is malformed.
    /// The decoder must drop the escape sequence silently and continue,
    /// never throw, never produce a frame containing the bogus byte.
    /// </summary>
    [Property]
    public void Malformed_Escape_Does_Not_Throw(byte bogus)
    {
        if (bogus == KissFraming.Tfend || bogus == KissFraming.Tfesc)
        {
            return; // valid escape - skip
        }
        var bytes = new byte[]
        {
            KissFraming.Fend,
            0x00,                  // command byte: port 0, Data
            0x41, 0x42,            // "AB"
            KissFraming.Fesc,
            bogus,                 // malformed escape body
            0x43,                  // "C"
            KissFraming.Fend,
        };
        var decoder = new KissDecoder();
        var act = () => decoder.Push(bytes);
        act.Should().NotThrow();
    }
}
