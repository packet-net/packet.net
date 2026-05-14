using System.Text;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS status report (DTI <c>&gt;</c>) per APRS101 §16.
/// </summary>
/// <remarks>
/// <para>
/// Layout:
/// <code>
///   &gt;status text                           (without timestamp)
///   &gt;DDHHMMz status text                   (with DHM-zulu timestamp)
/// </code>
/// Per spec §16, status timestamps are restricted to DHM-zulu format
/// (7 bytes ending in <c>z</c>). DHM-local (<c>/</c>) and HMS
/// (<c>h</c>) are NOT valid in status reports — those formats appear
/// in position and object reports but not here.
/// </para>
/// <para>
/// Status text is decoded as UTF-8 with the trailing <c>\r</c> /
/// <c>\n</c> stripped. The spec says ASCII only, but real-world
/// frames contain non-ASCII bytes (e.g. Chinese-station beacons),
/// so we accept any byte sequence on receive.
/// </para>
/// </remarks>
public static class AprsStatusDecoder
{
    /// <summary>
    /// Try to decode an APRS status report from <paramref name="info"/>.
    /// </summary>
    /// <param name="info">Info bytes, optionally prefixed with DTI byte <c>&gt;</c>.</param>
    /// <param name="status">On success, the decoded status.</param>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsStatus status)
    {
        status = default;
        if (info.IsEmpty) return false;

        // Strip DTI byte if present.
        if (info[0] == (byte)'>') info = info[1..];

        // Detect a DHM-zulu timestamp prefix: 6 digits + 'z'. APRS101 §16
        // restricts status to this format specifically.
        string? timestamp = null;
        if (info.Length >= 7 && info[6] == (byte)'z' && AllDigits(info[..6]))
        {
            timestamp = Encoding.ASCII.GetString(info[..7]);
            info = info[7..];
        }

        // Remainder is the status text. Spec says ASCII, real corpus has
        // UTF-8 (and worse); decode permissively with the replacement
        // character for invalid bytes so callers see something rather
        // than throwing.
        string text = Encoding.UTF8.GetString(info).TrimEnd('\r', '\n', ' ');
        status = new AprsStatus(timestamp, text);
        return true;
    }

    private static bool AllDigits(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b < (byte)'0' || b > (byte)'9') return false;
        }
        return true;
    }
}
