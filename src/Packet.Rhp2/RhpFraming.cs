using System.Buffers.Binary;

namespace Packet.Rhp2;

/// <summary>
/// RHPv2 framing over a byte stream: each frame is a 2-byte big-endian
/// unsigned length followed by exactly that many bytes of UTF-8 JSON.
/// </summary>
/// <remarks>
/// The 16-bit length field caps a single message at 65535 bytes — RHPv2
/// has no continuation mechanism, so larger payloads are a caller error
/// (split the data across multiple <c>send</c> messages instead). A
/// zero-length frame (<c>00 00</c>) is legal on the wire and yields an
/// empty payload rather than an error, because a conforming reader must
/// not lose framing sync over it.
/// </remarks>
public static class RhpFraming
{
    /// <summary>Largest payload expressible in the 2-byte length prefix.</summary>
    public const int MaxPayloadLength = 0xFFFF;

    /// <summary>
    /// Writes one length-prefixed frame and flushes the stream.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The payload exceeds <see cref="MaxPayloadLength"/>.
    /// </exception>
    public static async Task WriteFrameAsync(Stream output, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfOversize(payload.Length, nameof(payload));

        // Header and payload are written separately rather than coalesced
        // into one buffer: payloads are typically small JSON objects and
        // the underlying transport (NetworkStream / pipe) coalesces for us.
        var header = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)payload.Length);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        await output.WriteAsync(payload, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous counterpart of <see cref="WriteFrameAsync"/> — handy in
    /// tests that build wire fixtures into a <see cref="MemoryStream"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The payload exceeds <see cref="MaxPayloadLength"/>.
    /// </exception>
    public static void WriteFrame(Stream output, ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(output);
        ThrowIfOversize(payload.Length, nameof(payload));

        Span<byte> header = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(header, (ushort)payload.Length);
        output.Write(header);
        output.Write(payload);
        output.Flush();
    }

    /// <summary>
    /// Reads one length-prefixed frame.
    /// </summary>
    /// <returns>
    /// The payload bytes (possibly empty for a zero-length frame), or
    /// <see langword="null"/> if the stream ended cleanly before any header
    /// byte arrived — the peer hung up between frames, which is the normal
    /// way an RHP conversation ends.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// The stream ended part-way through a header or body — the peer hung
    /// up mid-frame, which is always abnormal.
    /// </exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Distinguish "no more frames" (clean close, return null) from
        // "frame cut short" (protocol violation, throw): only a zero-byte
        // first read counts as clean.
        var header = new byte[2];
        var got = await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        if (got < header.Length)
        {
            throw new EndOfStreamException("Stream ended inside an RHP frame header.");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(header);
        if (length == 0)
        {
            return [];
        }

        var payload = new byte[length];
        try
        {
            await input.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new EndOfStreamException($"Stream ended inside an RHP frame body of {length} bytes.", ex);
        }

        return payload;
    }

    private static void ThrowIfOversize(int length, string paramName)
    {
        if (length > MaxPayloadLength)
        {
            throw new ArgumentException(
                $"RHP payload of {length} bytes exceeds the 16-bit length prefix maximum ({MaxPayloadLength}).",
                paramName);
        }
    }
}
