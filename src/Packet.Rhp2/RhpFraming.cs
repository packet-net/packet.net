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
    /// Reads one length-prefixed frame, with no time limit on a stalled peer.
    /// Equivalent to <see cref="ReadFrameAsync(Stream, TimeSpan, CancellationToken)"/>
    /// with <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>.
    /// </summary>
    public static Task<byte[]?> ReadFrameAsync(Stream input, CancellationToken ct = default)
        => ReadFrameAsync(input, System.Threading.Timeout.InfiniteTimeSpan, ct);

    /// <summary>
    /// Reads one length-prefixed frame, bounding how long a peer may take
    /// <em>once a frame has started</em>.
    /// </summary>
    /// <param name="input">The stream to read from.</param>
    /// <param name="inFrameTimeout">
    /// Maximum time the rest of a frame may take to arrive after its first byte.
    /// A peer may sit idle between frames indefinitely (the multiplexed RHP
    /// connection legitimately waits on async pushes), but once it starts sending
    /// a frame it must finish within this window — a peer that sends a length
    /// prefix and then dribbles or stalls (a slowloris) is dropped rather than
    /// pinning the connection forever. <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
    /// disables the bound.
    /// </param>
    /// <param name="ct">Cancellation for the whole read.</param>
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
    /// <exception cref="TimeoutException">
    /// The peer started a frame but did not finish it within
    /// <paramref name="inFrameTimeout"/>.
    /// </exception>
    public static Task<byte[]?> ReadFrameAsync(Stream input, TimeSpan inFrameTimeout, CancellationToken ct = default)
        => ReadFrameAsync(input, inFrameTimeout, TimeProvider.System, ct);

    /// <summary>
    /// Reads one length-prefixed frame, bounding how long a peer may take
    /// <em>once a frame has started</em>, against an injected clock.
    /// </summary>
    /// <param name="input">The stream to read from.</param>
    /// <param name="inFrameTimeout">
    /// Maximum time the rest of a frame may take to arrive after its first byte;
    /// see <see cref="ReadFrameAsync(Stream, TimeSpan, CancellationToken)"/>.
    /// </param>
    /// <param name="timeProvider">
    /// The clock the in-frame deadline runs on. Production passes
    /// <see cref="TimeProvider.System"/>; a test passes a fake provider and advances
    /// it, so the slowloris drop is asserted without a real wait.
    /// </param>
    /// <param name="ct">Cancellation for the whole read.</param>
    /// <returns>
    /// The payload bytes (possibly empty for a zero-length frame), or
    /// <see langword="null"/> at a clean end of stream between frames.
    /// </returns>
    /// <exception cref="EndOfStreamException">
    /// The stream ended part-way through a header or body.
    /// </exception>
    /// <exception cref="TimeoutException">
    /// The peer started a frame but did not finish it within
    /// <paramref name="inFrameTimeout"/>.
    /// </exception>
    public static async Task<byte[]?> ReadFrameAsync(Stream input, TimeSpan inFrameTimeout, TimeProvider timeProvider, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // First byte of the header is NOT time-bounded: an idle multiplexed
        // connection may wait here arbitrarily long. A zero-byte read is the
        // clean "no more frames" close; anything else means a frame has begun.
        var header = new byte[2];
        var got = await input.ReadAtLeastAsync(header.AsMemory(0, 1), 1, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        if (got == 0)
        {
            return null;
        }

        // A frame has started. From here the rest of the header and the whole
        // body must arrive within inFrameTimeout. The deadline runs on the injected
        // clock (docs/plan.md §2.7). Two sources rather than CancelAfter, so the
        // catch below can still tell "the deadline fired" from "the caller cancelled".
        CancellationTokenSource? timeoutCts = null;
        CancellationTokenSource? linkedCts = null;
        var readCt = ct;
        if (inFrameTimeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            timeoutCts = new CancellationTokenSource(inFrameTimeout, timeProvider);
            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            readCt = linkedCts.Token;
        }

        try
        {
            try
            {
                await input.ReadExactlyAsync(header.AsMemory(1, 1), readCt).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new EndOfStreamException("Stream ended inside an RHP frame header.", ex);
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(header);
            if (length == 0)
            {
                return [];
            }

            var payload = new byte[length];
            try
            {
                await input.ReadExactlyAsync(payload, readCt).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new EndOfStreamException($"Stream ended inside an RHP frame body of {length} bytes.", ex);
            }

            return payload;
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            // The timeout fired, not the caller's token: a peer stalled mid-frame.
            throw new TimeoutException($"RHP peer stalled part-way through a frame (exceeded {inFrameTimeout}).");
        }
        finally
        {
            linkedCts?.Dispose();
            timeoutCts?.Dispose();
        }
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
