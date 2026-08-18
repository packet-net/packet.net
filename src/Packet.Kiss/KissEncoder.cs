using System.Buffers;

namespace Packet.Kiss;

/// <summary>
/// Encode a KISS frame for transmission on a serial / TCP stream.
/// </summary>
public static class KissEncoder
{
    /// <summary>
    /// Encode a single KISS frame:
    /// <c>FEND | (port&lt;&lt;4)|cmd | (escaped payload) | FEND</c>.
    /// </summary>
    /// <param name="port">Multi-drop port (0-15).</param>
    /// <param name="command">KISS command code.</param>
    /// <param name="payload">Payload bytes, unescaped.</param>
    /// <returns>The wire bytes ready to send to the TNC.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Port outside 0-15.</exception>
    public static byte[] Encode(byte port, KissCommand command, ReadOnlySpan<byte> payload)
    {
        if (port > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "KISS port must be 0–15");
        }

        // Worst case: every payload byte becomes 2 escaped bytes.
        // 2 FENDs + 1 command byte + 2*payload.
        int worstCase = 3 + (payload.Length * 2);
        var buffer = ArrayPool<byte>.Shared.Rent(worstCase);
        try
        {
            int len = WriteFrame(buffer, port, command, payload);
            return buffer.AsSpan(0, len).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Encode directly into a caller-provided buffer. Returns the number of
    /// bytes written. Throws if <paramref name="destination"/> is too small
    /// - call <see cref="MaxEncodedLength"/> first to size.
    /// </summary>
    public static int Encode(Span<byte> destination, byte port, KissCommand command, ReadOnlySpan<byte> payload)
    {
        if (port > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "KISS port must be 0–15");
        }
        int needed = MaxEncodedLength(payload.Length);
        if (destination.Length < needed)
        {
            throw new ArgumentException($"destination too short (need ≤{needed} bytes, got {destination.Length})", nameof(destination));
        }
        return WriteFrame(destination, port, command, payload);
    }

    /// <summary>
    /// The maximum possible encoded length for a given payload length. Two
    /// FEND delimiters, plus a worst-case-escaped command byte (2 bytes),
    /// plus worst-case-escaped payload (2 bytes per input byte).
    /// </summary>
    public static int MaxEncodedLength(int payloadLength) => 4 + (payloadLength * 2);

    private static int WriteFrame(Span<byte> destination, byte port, KissCommand command, ReadOnlySpan<byte> payload)
    {
        int i = 0;
        destination[i++] = KissFraming.Fend;

        // Escape the command byte too. Without this, port=12 + Data (0xC0)
        // and similar combinations would produce undecodable streams. The
        // KISS spec text says the command byte is not escaped, but real
        // implementations (e.g. direwolf's kiss_frame.c) escape it like any
        // other byte - and receivers handle that universally because the
        // first non-FEND post-FEND byte (after applying any escape) is by
        // definition the command byte.
        byte commandByte = (byte)(((port & 0x0F) << 4) | ((byte)command & 0x0F));
        i += WriteEscaped(destination[i..], commandByte);

        for (int j = 0; j < payload.Length; j++)
        {
            i += WriteEscaped(destination[i..], payload[j]);
        }

        destination[i++] = KissFraming.Fend;
        return i;
    }

    private static int WriteEscaped(Span<byte> destination, byte b)
    {
        switch (b)
        {
            case KissFraming.Fend:
                destination[0] = KissFraming.Fesc;
                destination[1] = KissFraming.Tfend;
                return 2;
            case KissFraming.Fesc:
                destination[0] = KissFraming.Fesc;
                destination[1] = KissFraming.Tfesc;
                return 2;
            default:
                destination[0] = b;
                return 1;
        }
    }
}
