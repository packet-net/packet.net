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
    /// Try to decode an APRS status report from <paramref name="info"/>,
    /// using the default lenient parser options.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsStatus status)
        => TryDecode(info, AprsParseOptions.Lenient, out status);

    /// <summary>
    /// Try to decode an APRS status report from <paramref name="info"/>,
    /// applying the supplied <see cref="AprsParseOptions"/>.
    /// </summary>
    /// <param name="info">Info bytes, optionally prefixed with DTI byte <c>&gt;</c>.</param>
    /// <param name="options">Strict-vs-lenient parser knobs.</param>
    /// <param name="status">On success, the decoded status.</param>
    /// <remarks>
    /// Strict mode rejects any byte outside printable ASCII (32–126,
    /// minus <c>|</c> and <c>~</c> per §16) anywhere in the status
    /// text. Trailing CR / LF / space are still tolerated since they
    /// get trimmed before the value is exposed.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> info, AprsParseOptions options, out AprsStatus status)
    {
        ArgumentNullException.ThrowIfNull(options);
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

        // Strict §16 check: each byte must be printable ASCII (32–126),
        // not | (124) or ~ (126). CR / LF tolerated as they get trimmed.
        if (!options.AllowNonAsciiStatusText)
        {
            foreach (var b in info)
            {
                bool ok = (b >= 32 && b <= 126 && b != 0x7C && b != 0x7E)
                          || b == 0x0D || b == 0x0A;
                if (!ok) return false;
            }
        }

        // Decode. Lenient: UTF-8 with replacement chars for invalid bytes
        // (Chinese-station beacons rely on this). Strict path already
        // proved everything is ASCII so UTF-8 = ASCII here.
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
