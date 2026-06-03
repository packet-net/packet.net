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
/// <para>
/// <b>First-segment inner PID (de-facto-interop format).</b> Figure 6.2's
/// two-octet header carries no field for the original Layer-3 PID, so the
/// figure-literal format loses it across a segmented series. Dire Wolf — the
/// only known v2.2 segmenter — prepends the original PID as an extra octet at
/// the front of the <b>first</b> segment, between the F/X octet and the data, so
/// its reassembler can recover it. Pass <c>innerPid</c> to
/// <see cref="Segment(ReadOnlySpan{byte}, int, byte?)"/> to emit that format
/// (the inner octet counts toward the segment budget — the first segment then
/// holds N1−2 data bytes, subsequent segments N1−1). Pass <c>null</c> for the
/// figure-literal format. The session selects between them via
/// <see cref="Ax25SessionQuirks.SegmentFirstCarriesL3Pid"/>.
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
    /// <c><paramref name="maxInfoFieldBytes"/> − 1</c> bytes of payload
    /// (figure-literal format), or — when <paramref name="innerPid"/> is
    /// supplied — the first segment additionally carries that inner-PID octet
    /// after the F/X byte (Dire Wolf's de-facto format), so its data capacity is
    /// <c>maxInfoFieldBytes − 2</c>.
    /// </summary>
    /// <param name="payload">The upper-layer payload to segment.</param>
    /// <param name="maxInfoFieldBytes">N1 — the max info-field size per I-frame.</param>
    /// <param name="innerPid">When non-<c>null</c>, emit Dire Wolf's format: the
    /// original Layer-3 PID is written as an extra octet on the <i>first</i> segment
    /// (between the F/X octet and the data) and counts toward the segment budget.
    /// When <c>null</c>, emit the figure-literal format (no inner-PID octet).</param>
    /// <exception cref="ArgumentOutOfRangeException">If <paramref name="maxInfoFieldBytes"/>
    /// is &lt; 2 (figure-literal) or &lt; 3 (inner-PID format — the first segment needs
    /// room for the F/X octet, the inner-PID octet, and at least one data byte).</exception>
    /// <exception cref="ArgumentException">If <paramref name="payload"/> would need
    /// more than <see cref="MaxSegments"/> segments.</exception>
    public static IReadOnlyList<byte[]> Segment(ReadOnlySpan<byte> payload, int maxInfoFieldBytes, byte? innerPid = null)
    {
        if (innerPid is null)
            return SegmentFigureLiteral(payload, maxInfoFieldBytes);
        return SegmentWithInnerPid(payload, maxInfoFieldBytes, innerPid.Value);
    }

    /// <summary>
    /// Figure-literal format: every segment info field is
    /// <c>[F/X octet][≤ N1−1 payload bytes]</c>.
    /// </summary>
    private static byte[][] SegmentFigureLiteral(ReadOnlySpan<byte> payload, int maxInfoFieldBytes)
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

    /// <summary>
    /// Dire Wolf's de-facto format: the first segment info field is
    /// <c>[F/X octet][inner-PID octet][≤ N1−2 payload bytes]</c>; subsequent
    /// segments are <c>[F/X octet][≤ N1−1 payload bytes]</c>. The inner-PID octet
    /// counts toward the budget — Dire Wolf computes the segment count as
    /// <c>ceil((len + 1) / (N1 − 1))</c> ("+1 for the original PID";
    /// <c>ax25_link.c</c> <c>dl_data_request</c>).
    /// </summary>
    private static byte[][] SegmentWithInnerPid(ReadOnlySpan<byte> payload, int maxInfoFieldBytes, byte innerPid)
    {
        if (maxInfoFieldBytes < 3)
            throw new ArgumentOutOfRangeException(nameof(maxInfoFieldBytes),
                "must be at least 3 for the inner-PID format (1 byte F/X control + 1 byte inner PID + at least 1 byte of payload on the first segment)");

        var perSegment = maxInfoFieldBytes - 1;               // subsequent-segment data capacity (F/X + data)
        var firstSegmentCapacity = maxInfoFieldBytes - 2;     // first-segment data capacity (F/X + inner PID + data)

        // Count of segments, treating the inner-PID octet as one extra payload
        // byte that consumes a data slot — mirrors Dire Wolf's
        // DIVROUNDUP(len + 1, N1 - 1). For an empty payload, one segment still
        // carries the inner PID.
        var budget = payload.Length + 1;
        var segmentCount = (budget + perSegment - 1) / perSegment;
        if (segmentCount < 1) segmentCount = 1;
        if (segmentCount > MaxSegments)
            throw new ArgumentException(
                $"payload of {payload.Length} bytes (plus 1 inner-PID octet) would need {segmentCount} segments at N1={maxInfoFieldBytes}; max is {MaxSegments}",
                nameof(payload));

        var result = new byte[segmentCount][];
        var offset = 0;
        for (int i = 0; i < segmentCount; i++)
        {
            var remaining = (byte)(segmentCount - 1 - i);
            if (i == 0)
            {
                var thisLen = Math.Min(firstSegmentCapacity, payload.Length - offset);
                var segment = new byte[2 + thisLen];
                segment[0] = (byte)(FirstBit | (remaining & CountMask));
                segment[1] = innerPid;
                if (thisLen > 0)
                    payload.Slice(offset, thisLen).CopyTo(segment.AsSpan(2));
                result[i] = segment;
                offset += thisLen;
            }
            else
            {
                var thisLen = Math.Min(perSegment, payload.Length - offset);
                var segment = new byte[1 + thisLen];
                segment[0] = (byte)(remaining & CountMask);
                if (thisLen > 0)
                    payload.Slice(offset, thisLen).CopyTo(segment.AsSpan(1));
                result[i] = segment;
                offset += thisLen;
            }
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
/// <para>
/// One <see cref="Reassembler"/> handles one in-flight multi-segment
/// payload at a time. A new "First" segment discards any previously
/// accumulated partial state — matching the spec's behaviour when a
/// fresh packet arrives mid-way through a prior series.
/// </para>
/// <para>
/// <b>Inner PID (de-facto-interop format).</b> When constructed with
/// <c>expectInnerPid: true</c>, the reassembler reads an inner-PID octet off the
/// front of the first segment's data (Dire Wolf's format — see
/// <see cref="Segmenter"/>) and exposes it via <see cref="LastRecoveredPid"/>, so
/// the reassembled payload can be delivered with its original Layer-3 PID. When
/// constructed with <c>expectInnerPid: false</c> (figure-literal), there is no
/// inner PID and <see cref="LastRecoveredPid"/> stays <c>null</c>.
/// </para>
/// </remarks>
public sealed class Reassembler
{
    private readonly List<byte[]> accumulated = new();
    private int expectedRemaining = -1;   // -1 = waiting for a "First" segment
    private readonly bool expectInnerPid;
    private byte? pendingPid;              // inner PID seen on the current series' first segment

    /// <summary>Construct a reassembler for the figure-literal format (no inner PID).</summary>
    public Reassembler() : this(expectInnerPid: false) { }

    /// <summary>
    /// Construct a reassembler.
    /// </summary>
    /// <param name="expectInnerPid">When <c>true</c>, read an inner-PID octet off the
    /// first segment (Dire Wolf's format) and surface it via
    /// <see cref="LastRecoveredPid"/>. When <c>false</c>, the figure-literal format.</param>
    public Reassembler(bool expectInnerPid)
    {
        this.expectInnerPid = expectInnerPid;
    }

    /// <summary>
    /// The original Layer-3 PID recovered from the inner-PID octet of the most
    /// recently <i>completed</i> series, when this reassembler expects the inner-PID
    /// format. <c>null</c> for the figure-literal format (no inner PID is carried),
    /// or before any series has completed.
    /// </summary>
    public byte? LastRecoveredPid { get; private set; }

    /// <summary>
    /// Push the info-field bytes of one segment. Returns the completed
    /// payload when the last segment of a series arrives (remaining
    /// count == 0). Returns <c>null</c> when more segments are expected.
    /// On completion, <see cref="LastRecoveredPid"/> holds the inner PID
    /// (inner-PID format) or <c>null</c> (figure-literal format).
    /// </summary>
    /// <exception cref="ArgumentException">If <paramref name="infoField"/> is empty,
    /// or — for the inner-PID format — a first segment lacks the inner-PID octet.</exception>
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

        byte[] data;
        if (isFirst && expectInnerPid)
        {
            if (infoField.Length < 2)
                throw new ArgumentException(
                    "first segment of an inner-PID series must be at least 2 bytes (the F/X control byte + the inner-PID octet)",
                    nameof(infoField));
            pendingPid = infoField[1];
            data = infoField.Slice(2).ToArray();
        }
        else
        {
            data = infoField.Slice(1).ToArray();
        }

        if (isFirst)
        {
            accumulated.Clear();
            expectedRemaining = remaining;
            if (!expectInnerPid) pendingPid = null;
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
        LastRecoveredPid = pendingPid;
        pendingPid = null;
        return result;
    }
}
