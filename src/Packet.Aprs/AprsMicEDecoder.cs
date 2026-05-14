using System.Text;

namespace Packet.Aprs;

/// <summary>
/// Decode an APRS Mic-E packet per APRS101 §10.
/// </summary>
/// <remarks>
/// <para>
/// Mic-E is the only APRS payload type that splits data across the
/// AX.25 frame header (destination address encodes latitude, message
/// bits, N/S, longitude offset, W/E) and the information field
/// (encodes longitude, speed, course, symbol, optional status). The
/// caller must therefore supply both pieces.
/// </para>
/// <para>
/// DTI is <c>`</c> (current GPS data) or <c>'</c> (old data). Real
/// implementations also use <c>0x1C</c> / <c>0x1D</c> on Rev. 0 beta
/// units — accepted on receive.
/// </para>
/// </remarks>
public static class AprsMicEDecoder
{
    private const int DestLen = 6;
    private const int InfoHeaderLen = 9;   // DTI + d + m + h + SP + DC + SE + symcode + symtbl

    /// <summary>
    /// Try to decode a Mic-E frame.
    /// </summary>
    /// <param name="destinationBase">
    /// 6-character AX.25 destination-address base (callsign portion,
    /// not including SSID). Caller is responsible for trimming trailing
    /// pad-spaces. Must be exactly 6 characters per §10.
    /// </param>
    /// <param name="info">
    /// Information-field bytes including the leading DTI byte
    /// (<c>`</c>, <c>'</c>, <c>0x1C</c>, or <c>0x1D</c>).
    /// </param>
    /// <param name="result">On success, the decoded Mic-E packet.</param>
    public static bool TryDecode(string destinationBase, ReadOnlySpan<byte> info, out AprsMicE result)
        => TryDecode(destinationBase, info, AprsParseOptions.Lenient, out result);

    /// <summary>
    /// Try to decode a Mic-E frame with explicit <see cref="AprsParseOptions"/>.
    /// </summary>
    /// <remarks>
    /// Strict mode (per APRS101 §10) rejects the legacy DTI bytes
    /// <c>0x1C</c> / <c>0x1D</c> — those are "Rev. 0 beta units only"
    /// per spec and effectively a permissive accommodation for early
    /// Kenwood firmware.
    /// </remarks>
    public static bool TryDecode(string destinationBase, ReadOnlySpan<byte> info, AprsParseOptions options, out AprsMicE result)
    {
        ArgumentNullException.ThrowIfNull(options);
        result = default;
        if (destinationBase is null) return false;
        if (destinationBase.Length != DestLen) return false;
        if (info.Length < InfoHeaderLen) return false;

        // DTI check. The spec-canonical DTIs are 0x60 (`) and 0x27 (').
        // 0x1C / 0x1D are Rev. 0 beta legacy bytes — gated by an option.
        byte dti = info[0];
        bool isCanonicalDti = dti == (byte)'`' || dti == (byte)'\'';
        bool isLegacyDti = dti == 0x1C || dti == 0x1D;
        if (!isCanonicalDti && !(isLegacyDti && options.AllowMicELegacyDtiBytes))
        {
            return false;
        }

        // ─── Destination address → lat / N-S / lon-offset / W-E / msg bits ───
        Span<char> latChars = stackalloc char[DestLen];
        int msgBitsA = 0, msgBitsB = 0, msgBitsC = 0;
        int stdHints = 0, customHints = 0;
        bool isNorth = false;
        bool longOffset100 = false;
        bool isWest = false;

        for (int i = 0; i < DestLen; i++)
        {
            char c = destinationBase[i];
            if (!TryDecodeDestinationChar(c, out char digitChar, out int msgBit, out bool stdHint, out bool customHint))
            {
                return false;
            }
            latChars[i] = digitChar;

            switch (i)
            {
                case 0: msgBitsA = msgBit; break;
                case 1: msgBitsB = msgBit; break;
                case 2: msgBitsC = msgBit; break;
                case 3:
                    // N/S indicator: 'L' or '0'..'9' → South; rest → North.
                    isNorth = c is 'P' or 'Q' or 'R' or 'S' or 'T' or 'U' or 'V' or 'W' or 'X' or 'Y' or 'Z'
                              || (c >= 'A' && c <= 'K');
                    break;
                case 4:
                    // Longitude offset: 'P'..'Z' → +100; '0'..'9' or 'L' → +0.
                    longOffset100 = c is 'P' or 'Q' or 'R' or 'S' or 'T' or 'U' or 'V' or 'W' or 'X' or 'Y' or 'Z';
                    break;
                case 5:
                    // W/E indicator: 'P'..'Z' → West; '0'..'9' or 'L' → East.
                    isWest = c is 'P' or 'Q' or 'R' or 'S' or 'T' or 'U' or 'V' or 'W' or 'X' or 'Y' or 'Z';
                    break;
            }

            if (msgBit == 1)
            {
                if (stdHint) stdHints++;
                if (customHint) customHints++;
            }
        }

        if (!TryParseLatitude(latChars, isNorth, out double latitude)) return false;

        // ─── Information field → lon / speed / course / symbol ─────────────
        int d = info[1] - 28;
        int m = info[2] - 28;
        int h = info[3] - 28;
        if (longOffset100) d += 100;
        if (d is >= 180 and <= 189) d -= 80;       // 100–109°
        else if (d is >= 190 and <= 199) d -= 190; // 0–9°
        if (d is < 0 or > 179) return false;
        if (m >= 60) m -= 60;
        if (m is < 0 or > 59) return false;
        if (h is < 0 or > 99) return false;
        double longitude = d + (m + h / 100.0) / 60.0;
        if (isWest) longitude = -longitude;

        // SP+28 (speed tens). Two encodings exist for 0-199 kn: subtract 28
        // and, if the result is ≥ 80, subtract another 80 to collapse the
        // "printable" encoding back to the [0, 79] tens-of-knots index.
        int sp = info[4] - 28;
        if (sp >= 80) sp -= 80;
        if (sp is < 0 or > 79) return false;

        // DC+28 (speed units + course hundreds). Per the §10 worked example
        // the algorithm is: subtract 28, divide by 10 for the units digit,
        // then take the remainder; if the remainder is ≥ 4 subtract 4 to
        // recover the canonical course-hundreds index. This single rule
        // handles both old (non-printable-prefix) and new (printable-prefix)
        // encodings without needing to detect which the sender used.
        int dc = info[5] - 28;
        if (dc < 0) return false;
        int speedUnits = dc / 10;
        int courseHundredsIdx = dc % 10;
        if (courseHundredsIdx >= 4) courseHundredsIdx -= 4;
        if (speedUnits > 9 || courseHundredsIdx > 3) return false;

        // SE+28 (course tens/units). One encoding only: raw - 28 = 0..99.
        int se = info[6] - 28;
        if (se is < 0 or > 99) return false;

        int speed = sp * 10 + speedUnits;
        int course = courseHundredsIdx * 100 + se;
        // Per §10's "speed/course adjustments" — if either overflows the
        // representable range, wrap. In practice this catches the rare
        // sender that uses out-of-table byte combinations.
        if (speed >= 800) speed -= 800;
        if (course >= 400) course -= 400;
        if (course > 360) return false;

        char symbolCode = (char)info[7];
        char symbolTable = (char)info[8];

        // Bytes 9+ → comment (status / altitude / Maidenhead / telemetry).
        string comment = info.Length > InfoHeaderLen
            ? Encoding.UTF8.GetString(info[InfoHeaderLen..]).TrimEnd('\r', '\n')
            : string.Empty;

        var msgType = DecodeMessageType(msgBitsA, msgBitsB, msgBitsC, stdHints, customHints);

        result = new AprsMicE(
            Latitude: latitude,
            Longitude: longitude,
            SpeedKnots: speed,
            CourseDegrees: course,
            SymbolTable: symbolTable,
            SymbolCode: symbolCode,
            MessageType: msgType,
            Comment: comment);
        return true;
    }

    // ─── Destination-address character decoding (APRS101 §10) ──────────────
    //
    // Each of the 6 chars maps to:
    //   - one latitude digit (or ASCII space, meaning position-ambiguity)
    //   - one message-bit value (0 or 1)
    //   - for bit=1, whether the hint is "Standard 1" or "Custom 1"
    //   - for bytes 3/4/5, the N/S, long-offset and W/E flags (computed
    //     separately by the caller — they only depend on which char-class
    //     the byte falls in).

    private static bool TryDecodeDestinationChar(char c, out char digit, out int msgBit, out bool stdHint, out bool customHint)
    {
        digit = '0';
        msgBit = 0;
        stdHint = false;
        customHint = false;

        if (c >= '0' && c <= '9') { digit = c; msgBit = 0; return true; }
        if (c >= 'A' && c <= 'J') { digit = (char)('0' + (c - 'A')); msgBit = 1; customHint = true; return true; }
        if (c == 'K')             { digit = ' '; msgBit = 1; customHint = true; return true; }
        if (c == 'L')             { digit = ' '; msgBit = 0; return true; }
        if (c >= 'P' && c <= 'Y') { digit = (char)('0' + (c - 'P')); msgBit = 1; stdHint = true; return true; }
        if (c == 'Z')             { digit = ' '; msgBit = 1; stdHint = true; return true; }
        return false;
    }

    private static bool TryParseLatitude(ReadOnlySpan<char> digits, bool isNorth, out double latitude)
    {
        // Layout: DDMM.HH (6 digits + 2 hundredths). Spaces are
        // position-ambiguity markers — treat as zero per §10.
        latitude = 0;
        Span<int> ds = stackalloc int[6];
        for (int i = 0; i < 6; i++)
        {
            char ch = digits[i];
            if (ch == ' ') ds[i] = 0;
            else if (ch >= '0' && ch <= '9') ds[i] = ch - '0';
            else return false;
        }
        int deg = ds[0] * 10 + ds[1];
        if (deg > 90) return false;
        int minWhole = ds[2] * 10 + ds[3];
        int minFrac  = ds[4] * 10 + ds[5];
        if (minWhole >= 60) return false;
        latitude = deg + (minWhole + minFrac / 100.0) / 60.0;
        if (!isNorth) latitude = -latitude;
        return true;
    }

    private static MicEMessageType DecodeMessageType(int bitA, int bitB, int bitC, int stdHints, int customHints)
    {
        int bits = (bitA << 2) | (bitB << 1) | bitC;

        if (bits == 0b000) return MicEMessageType.Emergency;

        bool isStandard = stdHints > 0 && customHints == 0;
        bool isCustom   = customHints > 0 && stdHints == 0;
        if (!isStandard && !isCustom) return MicEMessageType.Unknown;

        // Bits high-to-low: A | B | C
        //   111 → M0 / C0
        //   110 → M1 / C1
        //   101 → M2 / C2
        //   100 → M3 / C3
        //   011 → M4 / C4
        //   010 → M5 / C5
        //   001 → M6 / C6
        int idx = 7 - bits;  // 7 → M0, 6 → M1, ..., 1 → M6
        return (isStandard, idx) switch
        {
            (true, 0)  => MicEMessageType.StandardM0OffDuty,
            (true, 1)  => MicEMessageType.StandardM1EnRoute,
            (true, 2)  => MicEMessageType.StandardM2InService,
            (true, 3)  => MicEMessageType.StandardM3Returning,
            (true, 4)  => MicEMessageType.StandardM4Committed,
            (true, 5)  => MicEMessageType.StandardM5Special,
            (true, 6)  => MicEMessageType.StandardM6Priority,
            (false, 0) => MicEMessageType.CustomC0,
            (false, 1) => MicEMessageType.CustomC1,
            (false, 2) => MicEMessageType.CustomC2,
            (false, 3) => MicEMessageType.CustomC3,
            (false, 4) => MicEMessageType.CustomC4,
            (false, 5) => MicEMessageType.CustomC5,
            (false, 6) => MicEMessageType.CustomC6,
            _ => MicEMessageType.Unknown,
        };
    }
}
