using System.Text;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS item report (DTI <c>)</c>) per APRS101 §11.
/// </summary>
/// <remarks>
/// <para>
/// Layout (DTI optional):
/// <code>
///   )NAME!POSITION_DATAcomment   (live item, name 3–9 chars)
///   )NAME_POSITION_DATAcomment   (killed item)
/// </code>
/// The name is variable-length (3–9 characters) and is terminated by
/// the indicator byte <c>!</c> (live) or <c>_</c> (killed). Per spec
/// neither character may appear inside the name, so the first
/// occurrence in the right offset range is unambiguous. The position
/// bytes follow exactly as for an object — uncompressed (§8) or
/// compressed (§9), with no separating timestamp.
/// </para>
/// </remarks>
public static class AprsItemDecoder
{
    private const int MinNameLen = 3;
    private const int MaxNameLen = 9;

    /// <summary>
    /// Try to decode an APRS item report from <paramref name="info"/>.
    /// </summary>
    /// <param name="info">Info bytes, optionally prefixed with the DTI byte <c>)</c>.</param>
    /// <param name="item">On success, the decoded item.</param>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsItem item)
    {
        item = default;
        if (info.IsEmpty) return false;

        // Strip DTI byte if present.
        if (info[0] == (byte)')') info = info[1..];

        // Find the indicator byte ('!' or '_') in the valid name-length
        // window. The spec restricts names to 3-9 chars and forbids these
        // two characters from appearing inside the name, so the first
        // match in window [MinNameLen, MaxNameLen] is the terminator.
        int indicatorIdx = -1;
        int upper = Math.Min(info.Length, MaxNameLen);
        for (int i = MinNameLen; i <= upper; i++)
        {
            if (i >= info.Length) break;
            byte b = info[i];
            if (b == (byte)'!' || b == (byte)'_')
            {
                indicatorIdx = i;
                break;
            }
        }
        if (indicatorIdx < 0) return false;

        string name = Encoding.ASCII.GetString(info[..indicatorIdx]);
        bool alive = info[indicatorIdx] == (byte)'!';

        // Position follows directly. Minimum payload: 13 bytes (compressed)
        // or 19 bytes (uncompressed); TryDecodePayload will reject too-short.
        if (!AprsPositionDecoder.TryDecodePayload(info[(indicatorIdx + 1)..], out var pos)) return false;

        item = new AprsItem(name, alive, pos);
        return true;
    }
}
