using System.Diagnostics.CodeAnalysis;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS position report (DTI <c>!</c>, <c>=</c>, <c>/</c>,
/// <c>@</c>) per APRS12c.pdf §8 (uncompressed) and §9 (compressed).
/// </summary>
/// <remarks>
/// <para>
/// Strict decoder: invalid characters in fixed-position fields
/// (lat/lon digits, hemisphere indicators, symbol table id, compressed
/// base-91 range) cause <see cref="TryDecode"/> to return <c>false</c>.
/// This matches direwolf's "Position" warnings — see the corpus
/// findings in <c>tools/Packet.AprsIs.Spike/findings.md</c> for the
/// classes of malformed frames in the wild.
/// </para>
/// <para>
/// The decoder strips a leading DTI byte if present; callers may
/// equivalently pass the info field with or without the DTI. For
/// <c>@</c> / <c>/</c> reports (timestamped variants), the caller must
/// strip the 7-byte timestamp before passing in — this decoder handles
/// only the lat-lon-symbol portion.
/// </para>
/// </remarks>
public static class AprsPositionDecoder
{
    /// <summary>
    /// Try to decode an APRS position from <paramref name="info"/>.
    /// </summary>
    /// <param name="info">
    /// Info-field bytes, optionally prefixed with the DTI byte. Handles
    /// the four DTI variants:
    /// <list type="bullet">
    ///   <item><c>!</c> — no timestamp, no message capability</item>
    ///   <item><c>=</c> — no timestamp, message-capable</item>
    ///   <item><c>/</c> — timestamped, no message capability</item>
    ///   <item><c>@</c> — timestamped, message-capable</item>
    /// </list>
    /// For the timestamped variants the 7-byte timestamp prefix
    /// (or 8 bytes for MDHM) is stripped before the position bytes are
    /// parsed. The timestamp itself isn't exposed by this decoder; a
    /// future <c>AprsPositionWithTimestamp</c> overload could surface
    /// it when callers need the time.
    /// </param>
    /// <param name="position">
    /// On success, the decoded position. On failure, the default value.
    /// </param>
    /// <returns>
    /// <c>true</c> if the input parsed as a valid position; <c>false</c>
    /// for any structural defect (length, hemisphere indicator, digit
    /// range, base-91 range, timestamp format, …).
    /// </returns>
    public static bool TryDecode(ReadOnlySpan<byte> info, out AprsPosition position)
    {
        position = default;
        if (info.IsEmpty) return false;

        switch (info[0])
        {
            case (byte)'!':
            case (byte)'=':
                info = info[1..];
                break;
            case (byte)'@':
            case (byte)'/':
                // Strip DTI + timestamp.
                if (info.Length < 8) return false;
                int tsLen = TimestampLength(info[1..8]);
                if (tsLen == 0) return false;
                if (info.Length < 1 + tsLen) return false;
                info = info[(1 + tsLen)..];
                break;
            default:
                // No DTI byte — assume caller already stripped it; fall
                // through to format detection.
                break;
        }

        if (info.IsEmpty) return false;

        // Compressed format starts with the symbol table identifier
        // (which can be / \ 0-9 A-Z a-j). Uncompressed format starts
        // with a latitude digit 0-9. So the discriminator is "first
        // byte is a digit" → uncompressed.
        return IsAsciiDigit(info[0])
            ? TryDecodeUncompressed(info, out position)
            : TryDecodeCompressed(info, out position);
    }

    /// <summary>
    /// Determine the length of an APRS timestamp prefix per APRS12c §6.
    /// Returns 0 for unrecognised formats (caller should reject).
    /// </summary>
    /// <remarks>
    /// Four formats per spec:
    /// <list type="bullet">
    ///   <item>DHM zulu: <c>DDHHMMz</c> — 7 bytes, terminator <c>z</c></item>
    ///   <item>DHM local: <c>DDHHMM/</c> — 7 bytes, terminator <c>/</c></item>
    ///   <item>HMS: <c>HHMMSSh</c> — 7 bytes, terminator <c>h</c></item>
    ///   <item>MDHM: <c>MMDDHHMM</c> — 8 bytes, all digits, no terminator</item>
    /// </list>
    /// The 7-byte forms have their format byte at position 6 (the 7th
    /// byte). The 8-byte MDHM has no terminator — distinguished by all
    /// 8 bytes being digits.
    /// </remarks>
    private static int TimestampLength(ReadOnlySpan<byte> candidate7)
    {
        if (candidate7.Length < 7) return 0;
        for (int i = 0; i < 6; i++)
        {
            if (!IsAsciiDigit(candidate7[i])) return 0;
        }
        byte t = candidate7[6];
        if (t == (byte)'z' || t == (byte)'/' || t == (byte)'h') return 7;
        // MDHM (8 bytes, all digits) — also covers a final digit at
        // position 6 followed by another digit at position 7 in the
        // caller's buffer; we caught the first 7 here and need the 8th.
        if (IsAsciiDigit(t)) return 8;
        return 0;
    }

    // ─── Uncompressed (APRS12c §8) ─────────────────────────────────────
    //
    // Layout (20 bytes minimum + optional comment):
    //   DDMM.mmN/S  (8 bytes)
    //   <symtbl>    (1 byte)  — '/' or '\' or overlay
    //   DDDMM.mmE/W (9 bytes)
    //   <symcode>   (1 byte)
    //   <comment>   (0..N bytes)

    private static bool TryDecodeUncompressed(ReadOnlySpan<byte> info, [NotNullWhen(true)] out AprsPosition position)
    {
        position = default;
        if (info.Length < 19) return false;  // 8 + 1 + 9 + 1, comment optional

        // Latitude: DDMM.mmN/S
        if (!TryParseLatitude(info[..8], out double lat)) return false;
        char symbolTable = (char)info[8];
        if (!IsValidSymbolTable(symbolTable)) return false;

        // Longitude: DDDMM.mmE/W
        if (!TryParseLongitude(info.Slice(9, 9), out double lon)) return false;
        char symbolCode = (char)info[18];

        string comment = info.Length > 19
            ? System.Text.Encoding.UTF8.GetString(info[19..]).TrimEnd('\r', '\n')
            : string.Empty;

        position = new AprsPosition(lat, lon, symbolTable, symbolCode, comment, AprsPositionFormat.Uncompressed);
        return true;
    }

    private static bool TryParseLatitude(ReadOnlySpan<byte> field, out double degrees)
    {
        degrees = 0;
        // DDMM.mmN/S — 8 chars
        if (field.Length != 8) return false;
        if (field[4] != (byte)'.') return false;
        if (!IsAsciiDigit(field[0]) || !IsAsciiDigit(field[1])) return false;
        if (!IsAsciiDigit(field[2]) && field[2] != (byte)' ') return false;
        if (!IsAsciiDigit(field[3]) && field[3] != (byte)' ') return false;
        if (!IsAsciiDigit(field[5]) && field[5] != (byte)' ') return false;
        if (!IsAsciiDigit(field[6]) && field[6] != (byte)' ') return false;
        if (field[7] != (byte)'N' && field[7] != (byte)'S') return false;

        int deg = (field[0] - '0') * 10 + (field[1] - '0');
        if (deg > 90) return false;
        int minWhole = ((field[2] == (byte)' ' ? 0 : field[2] - '0') * 10)
                      + (field[3] == (byte)' ' ? 0 : field[3] - '0');
        int minFrac  = ((field[5] == (byte)' ' ? 0 : field[5] - '0') * 10)
                      + (field[6] == (byte)' ' ? 0 : field[6] - '0');

        if (minWhole >= 60) return false;

        double minutes = minWhole + (minFrac / 100.0);
        degrees = deg + (minutes / 60.0);
        if (degrees > 90) return false;
        if (field[7] == (byte)'S') degrees = -degrees;
        return true;
    }

    private static bool TryParseLongitude(ReadOnlySpan<byte> field, out double degrees)
    {
        degrees = 0;
        // DDDMM.mmE/W — 9 chars
        if (field.Length != 9) return false;
        if (field[5] != (byte)'.') return false;
        if (!IsAsciiDigit(field[0]) || !IsAsciiDigit(field[1]) || !IsAsciiDigit(field[2])) return false;
        if (!IsAsciiDigit(field[3]) && field[3] != (byte)' ') return false;
        if (!IsAsciiDigit(field[4]) && field[4] != (byte)' ') return false;
        if (!IsAsciiDigit(field[6]) && field[6] != (byte)' ') return false;
        if (!IsAsciiDigit(field[7]) && field[7] != (byte)' ') return false;
        if (field[8] != (byte)'E' && field[8] != (byte)'W') return false;

        int deg = (field[0] - '0') * 100 + (field[1] - '0') * 10 + (field[2] - '0');
        if (deg > 180) return false;
        int minWhole = ((field[3] == (byte)' ' ? 0 : field[3] - '0') * 10)
                      + (field[4] == (byte)' ' ? 0 : field[4] - '0');
        int minFrac  = ((field[6] == (byte)' ' ? 0 : field[6] - '0') * 10)
                      + (field[7] == (byte)' ' ? 0 : field[7] - '0');

        if (minWhole >= 60) return false;

        double minutes = minWhole + (minFrac / 100.0);
        degrees = deg + (minutes / 60.0);
        if (degrees > 180) return false;
        if (field[8] == (byte)'W') degrees = -degrees;
        return true;
    }

    // ─── Compressed (APRS12c §9) ───────────────────────────────────────
    //
    // Layout (13 bytes + optional comment):
    //   <symtbl>   (1 byte)
    //   yyyy       (4 bytes lat,  base-91 + 33 offset)
    //   xxxx       (4 bytes lon,  base-91 + 33 offset)
    //   <symcode>  (1 byte)
    //   csT        (3 bytes course/speed/range/altitude)
    //   <comment>  (0..N bytes)
    //
    // Base-91 range: characters '!' (33) through '{' (123) — see §9.

    private static bool TryDecodeCompressed(ReadOnlySpan<byte> info, [NotNullWhen(true)] out AprsPosition position)
    {
        position = default;
        if (info.Length < 13) return false;

        char symbolTable = (char)info[0];
        if (!IsValidSymbolTable(symbolTable)) return false;

        // Lat: 4 base-91 chars → integer Y, then lat = 90 - Y/380926
        if (!TryDecodeBase91(info.Slice(1, 4), out long yRaw)) return false;
        double lat = 90.0 - (yRaw / 380926.0);
        if (lat is < -90 or > 90) return false;

        // Lon: 4 base-91 chars → integer X, then lon = -180 + X/190463
        if (!TryDecodeBase91(info.Slice(5, 4), out long xRaw)) return false;
        double lon = -180.0 + (xRaw / 190463.0);
        if (lon is < -180 or > 180) return false;

        char symbolCode = (char)info[9];
        // info[10..13] = csT (course/speed/range/altitude) — not parsed
        // here; would land in a separate decoder for compressed extensions.

        string comment = info.Length > 13
            ? System.Text.Encoding.UTF8.GetString(info[13..]).TrimEnd('\r', '\n')
            : string.Empty;

        position = new AprsPosition(lat, lon, symbolTable, symbolCode, comment, AprsPositionFormat.Compressed);
        return true;
    }

    /// <summary>
    /// Decode 1..4 base-91 characters per APRS12c §9. Valid range is
    /// <c>!</c> (33) through <c>{</c> (123); each character contributes
    /// (c - 33) × 91^position.
    /// </summary>
    private static bool TryDecodeBase91(ReadOnlySpan<byte> field, out long value)
    {
        value = 0;
        long mul = 1;
        for (int i = field.Length - 1; i >= 0; i--)
        {
            byte b = field[i];
            if (b < (byte)'!' || b > (byte)'{') return false;
            value += (b - 33) * mul;
            mul *= 91;
        }
        return true;
    }

    private static bool IsAsciiDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';

    /// <summary>
    /// Valid symbol-table identifiers per APRS12c §20: primary table
    /// (<c>/</c>), alternate table (<c>\</c>), or a single overlay
    /// character drawn from <c>0–9</c>, <c>A–Z</c>, or <c>a–j</c>
    /// (the alternate table with overlay).
    /// </summary>
    private static bool IsValidSymbolTable(char c)
    {
        if (c == '/' || c == '\\') return true;
        if (c >= '0' && c <= '9') return true;
        if (c >= 'A' && c <= 'Z') return true;
        if (c >= 'a' && c <= 'j') return true;
        return false;
    }
}
