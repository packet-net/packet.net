using Packet.Ax25;

namespace Packet.Ax25.Session;

/// <summary>
/// AX.25 v2.2 §6.6 segmentation — splits a long upper-layer payload
/// into a sequence of I-frame info-field byte arrays, each prefixed
/// with the segment control byte.
/// </summary>
/// <remarks>
/// <para>
/// Segment control byte format (AX.25 v2.2 Figure 6.2 — <c>FXXXXXXX</c>,
/// value <c>F*128+X</c>):
/// <code>
/// bit 7   = First indicator (1 on the first segment of a series)
/// bits 6:0 = X, the 7-bit count of segments still to come
/// </code>
/// </para>
/// <para>
/// With 7 bits of remaining-count, a packet may span at most 128
/// segments. At the default N1=256 the per-segment payload is 255
/// bytes, so the maximum upper-layer payload through the Segmenter is
/// 128 × 255 = 32 640 bytes. (Figure 6.2 makes X a 7-bit field; direwolf
/// masks the count with <c>0x7f</c> — <c>ax25_link.c</c> reassembler — so
/// both spec and de-facto agree the count is 7-bit, not 6.)
/// </para>
/// <para>
/// Layer-3 packets segmented this way travel as I-frames with PID
/// <see cref="Ax25Frame.PidSegmented"/> (0x08). Reassembly is the
/// receiving side's job — see <see cref="Reassembler"/>.
/// </para>
/// </remarks>
public static class Segmenter
{
    /// <summary>First-segment indicator (bit 7 of the segment control byte).</summary>
    public const byte FirstBit = 0x80;

    /// <summary>Seven-bit mask for the remaining-count field (bits 6:0), per Figure 6.2.</summary>
    public const byte CountMask = 0x7F;

    /// <summary>Maximum number of segments a single upper-layer payload may span (7-bit count → 128).</summary>
    public const int MaxSegments = 128;

    /// <summary>
    /// Split a payload into I-frame info fields. Each info field is
    /// prefixed with the segment control byte; the rest is up to
    /// <c><paramref name="maxInfoFieldBytes"/> − 1</c> bytes of payload.
    /// </summary>
    /// <param name="payload">The upper-layer payload to segment.</param>
    /// <param name="maxInfoFieldBytes">N1 — the max info-field size per I-frame.</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="maxInfoFieldBytes"/> is &lt; 2.</exception>
    /// <exception cref="ArgumentException">If <paramref name="payload"/> exceeds
    /// <see cref="MaxSegments"/> × <c>(maxInfoFieldBytes − 1)</c> bytes.</exception>
    public static IReadOnlyList<byte[]> Segment(ReadOnlySpan<byte> payload, int maxInfoFieldBytes)
    {
        if (maxInfoFieldBytes < 2)
            throw new ArgumentOutOfRangeException(nameof(maxInfoFieldBytes),
                "must be at least 2 (1 byte for the segment control byte + at least 1 byte of payload)");

        var perSegment = maxInfoFieldBytes - 1;
        var segmentCount = payload.Length == 0
            ? 1
            : (payload.Length + perSegment - 1) / perSegment;
        if (segmentCount > MaxSegments)
            throw new ArgumentException(
                $"payload of {payload.Length} bytes would need {segmentCount} segments at N1={maxInfoFieldBytes}; max is {MaxSegments}",
                nameof(payload));

        var result = new byte[segmentCount][];
        for (int i = 0; i < segmentCount; i++)
        {
            var remaining = (byte)(segmentCount - 1 - i);
            var firstBit = i == 0 ? FirstBit : (byte)0;
            var header = (byte)(firstBit | (remaining & CountMask));

            var offset = i * perSegment;
            var thisLen = Math.Min(perSegment, payload.Length - offset);
            var segment = new byte[1 + thisLen];
            segment[0] = header;
            if (thisLen > 0)
                payload.Slice(offset, thisLen).CopyTo(segment.AsSpan(1));
            result[i] = segment;
        }
        return result;
    }
}

/// <summary>
/// AX.25 v2.2 §6.6 reassembly — accumulates a sequence of segments
/// (each pushed as the info field of one I-frame with PID 0x08) into
/// a single upper-layer payload.
/// </summary>
/// <remarks>
/// One <see cref="Reassembler"/> handles one in-flight multi-segment
/// payload at a time. A new "First" segment discards any previously
/// accumulated partial state — matching the spec's behaviour when a
/// fresh packet arrives mid-way through a prior series.
/// </remarks>
public sealed class Reassembler
{
    private readonly List<byte[]> accumulated = new();
    private int expectedRemaining = -1;   // -1 = waiting for a "First" segment

    /// <summary>
    /// Push the info-field bytes of one segment. Returns the completed
    /// payload when the last segment of a series arrives (remaining
    /// count == 0). Returns <c>null</c> when more segments are expected.
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="infoField"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">If a non-First segment
    /// arrives without a prior First, or if the remaining count is out
    /// of sequence vs. the prior segment.</exception>
    public byte[]? Push(ReadOnlySpan<byte> infoField)
    {
        if (infoField.Length < 1)
            throw new ArgumentException("segment info field must be at least 1 byte (the control byte)", nameof(infoField));

        var header = infoField[0];
        var isFirst = (header & Segmenter.FirstBit) != 0;
        var remaining = header & Segmenter.CountMask;
        var data = infoField.Slice(1).ToArray();

        if (isFirst)
        {
            accumulated.Clear();
            expectedRemaining = remaining;
        }
        else if (expectedRemaining < 0)
        {
            throw new InvalidOperationException(
                "non-First segment received before any First segment — no in-progress reassembly to attach to");
        }
        else if (remaining != expectedRemaining - 1)
        {
            throw new InvalidOperationException(
                $"segment count out of sequence: expected {expectedRemaining - 1}, got {remaining}");
        }
        else
        {
            expectedRemaining = remaining;
        }

        accumulated.Add(data);

        if (remaining != 0) return null;

        var totalLen = 0;
        foreach (var chunk in accumulated) totalLen += chunk.Length;
        var result = new byte[totalLen];
        var offset = 0;
        foreach (var chunk in accumulated)
        {
            Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }
        accumulated.Clear();
        expectedRemaining = -1;
        return result;
    }
}
