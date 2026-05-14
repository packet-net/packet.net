using System.Text;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS object report (DTI <c>;</c>) per APRS12c §11.
/// </summary>
/// <remarks>
/// <para>
/// Layout (DTI optional; fixed-width fields up to the position):
/// <code>
///   ;NNNNNNNNN*DDHHMMzPOSITION_DATAcomment   (live object)
///   ;NNNNNNNNN_DDHHMMzPOSITION_DATAcomment   (killed object)
/// </code>
/// where NNNNNNNNN is a 9-byte name (right-padded with spaces),
/// DDHHMMz is a 7-byte DHM-zulu timestamp (also accepts <c>/</c> for
/// DHM-local per spec), and POSITION_DATA is encoded exactly the same
/// way as an ordinary position report — uncompressed (20 bytes) or
/// compressed (13 bytes) — followed by an optional free-form comment.
/// </para>
/// <para>
/// The position bytes are delegated to <see cref="AprsPositionDecoder.TryDecode"/>
/// without any DTI prefix; that decoder's format-detection (first byte
/// is a digit ⇒ uncompressed, otherwise compressed-with-symbol-table)
/// handles either case.
/// </para>
/// </remarks>
public static class AprsObjectDecoder
{
    /// <summary>
    /// Try to decode an APRS object report from <paramref name="info"/>.
    /// </summary>
    /// <param name="info">
    /// Info-field bytes, optionally prefixed with the DTI byte (<c>;</c>).
    /// </param>
    /// <param name="obj">On success, the decoded object. On failure, default.</param>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsObject obj)
    {
        obj = default;
        if (info.IsEmpty) return false;

        // Strip DTI byte if present.
        if (info[0] == (byte)';')
        {
            info = info[1..];
        }

        // Minimum length: 9-byte name + 1 alive + 7 timestamp + 13 compressed
        // position = 30. (Uncompressed position is 20 bytes, so total ≥ 37.)
        const int NameLen = 9;
        const int TimestampLen = 7;
        const int HeaderLen = NameLen + 1 + TimestampLen;   // = 17
        if (info.Length < HeaderLen + 13) return false;

        // Name: bytes 0–8, fixed-width. Spec doesn't constrain the character
        // set inside the name field — accept any printable ASCII so
        // "439.137JJ", "W4CRL  B", "PP5ADW-R " etc. all round-trip cleanly.
        // Preserve trailing spaces verbatim because the wire form is
        // fixed-width and receivers use the exact name as a dedup key.
        string name = Encoding.ASCII.GetString(info[..NameLen]);

        // Alive / killed indicator.
        bool alive;
        switch (info[NameLen])
        {
            case (byte)'*': alive = true; break;
            case (byte)'_': alive = false; break;
            default: return false;
        }

        // Timestamp: 6 ASCII digits + terminator. APRS101 §11 allows three
        // 7-byte forms for objects (no 8-byte MDHM):
        //   DDHHMMz — DHM zulu
        //   DDHHMM/ — DHM local
        //   HHMMSSh — HMS zulu
        var ts = info.Slice(NameLen + 1, TimestampLen);
        for (int i = 0; i < 6; i++)
        {
            if (!IsAsciiDigit(ts[i])) return false;
        }
        if (ts[6] != (byte)'z' && ts[6] != (byte)'/' && ts[6] != (byte)'h') return false;

        // Position: everything from byte 17 onwards. Use TryDecodePayload so
        // a leading '/' (compressed-symbol-table char) isn't mistaken for the
        // timestamped-position DTI.
        if (!AprsPositionDecoder.TryDecodePayload(info[HeaderLen..], out var pos)) return false;

        obj = new AprsObject(name, alive, pos);
        return true;
    }

    private static bool IsAsciiDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
}
