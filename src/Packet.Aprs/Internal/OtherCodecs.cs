using System.Globalization;

namespace Packet.Aprs.Internal;

internal static class RawWeatherCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        (AprsRawWeatherFormat format, int skip) = info[0] switch
        {
            (byte)'#' => (AprsRawWeatherFormat.PeetBrosHash, 1),
            (byte)'*' => (AprsRawWeatherFormat.PeetBrosStar, 1),
            (byte)'$' => (AprsRawWeatherFormat.UltimeterPacket, 5),
            _ => (AprsRawWeatherFormat.UltimeterLogging, 2),
        };
        ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "raw weather station data is not recommended; senders should send a complete weather report (APRS12c ch. 12, UAP 5.19)", 0);
        ReadOnlySpan<byte> data = info[skip..];
        if (data.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) >= 0)
        {
            // Station output is printable text; a control byte means the packet was corrupted.
            ctx.Error(AprsDiagnosticCode.InvalidWeather, "raw weather data must be printable ASCII", skip);
            return null;
        }

        return new AprsRawWeatherReport { Format = format, Data = Text.Latin1(data) };
    }
}

internal static class NmeaCodec
{
    /// <summary>
    /// Finds a <c>*hh</c> checksum that ends <paramref name="sentence"/>; <paramref name="sum"/> is
    /// the XOR of everything before the <c>*</c>, which is what it should equal.
    /// </summary>
    public static bool TryReadChecksum(string sentence, out int star, out byte expected, out byte sum)
    {
        sum = 0;
        expected = 0;
        star = sentence.Length - 3;
        if (star < 0 || sentence[star] != '*' || !IsHex(sentence[star + 1]) || !IsHex(sentence[star + 2]))
        {
            return false;
        }

        expected = (byte)((HexValue(sentence[star + 1]) << 4) | HexValue(sentence[star + 2]));
        foreach (char c in sentence.AsSpan(0, star))
        {
            sum ^= (byte)c;
        }

        return true;
    }

    /// <summary>
    /// The structure of an NMEA 0183 sentence (vectors interpretations.md, "What $ text is an NMEA
    /// sentence"): an address field of five upper-case letters or digits, or <c>P</c> and three or
    /// more of them, then at least one field, all in printable ASCII with no <c>$</c>. The sentence
    /// ends at its first <c>*</c>, which must be followed by two hex digits (the checksum); a
    /// <c>*</c> that is not is a reserved character in a field. Returns the length of the sentence
    /// (up to and including any checksum), or -1 when the text is not a sentence.
    /// </summary>
    public static int SentenceLength(ReadOnlySpan<byte> s)
    {
        int comma = s.IndexOf((byte)',');
        if (comma < 0 || !IsAddress(s[..comma]))
        {
            return -1;
        }

        for (int i = comma + 1; i < s.Length; i++)
        {
            byte b = s[i];
            if (b == (byte)'*')
            {
                return i + 2 < s.Length && IsHex((char)s[i + 1]) && IsHex((char)s[i + 2]) ? i + 3 : -1;
            }

            if (!Text.IsPrintableAscii(b) || b == (byte)'$')
            {
                return -1;
            }
        }

        return s.Length;
    }

    /// <summary>Five upper-case letters or digits (a talker and sentence formatter, or a query), or
    /// <c>P</c> and three or more of them (a proprietary sentence).</summary>
    private static bool IsAddress(ReadOnlySpan<byte> address)
    {
        foreach (byte b in address)
        {
            if (!Text.IsUpper(b) && !Text.IsDigit(b))
            {
                return false;
            }
        }

        return address.Length == 5 || (address.Length >= 4 && address[0] == (byte)'P');
    }

    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "raw NMEA sentences are obsolete; trackers should send position reports (UAP 5.20)", 0);
        ReadOnlySpan<byte> body = info[1..];
        int length = SentenceLength(body);
        if (length < 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidNmea, "not an NMEA 0183 sentence: an address field (5 upper-case letters or digits, or P and 3 or more), then comma-separated fields in printable ASCII, with * only before a 2-digit hex checksum", 1);
            return null;
        }

        string sentence = Text.Latin1(body[..length]);
        string content = sentence;
        if (TryReadChecksum(sentence, out int star, out byte expected, out byte sum))
        {
            // A checksum that doesn't match means the sentence was corrupted on the way, so none
            // of its fields can be trusted (NMEA 0183; Ham::APRS::FAP rejects it too).
            if (sum != expected)
            {
                ctx.Error(AprsDiagnosticCode.NmeaChecksumMismatch, $"NMEA checksum is {expected:X2} but the sentence sums to {sum:X2}; the sentence is corrupt", 1 + star);
                return null;
            }

            content = sentence[..star];
        }

        // Text after the checksum is a comment (TinyTrack, FreeTrak), as any APRS packet may carry (APRS12c ch. 5).
        if (!Text.TryDecode(body[length..], ctx, 1 + length, out string comment))
        {
            return null;
        }

        string[] f = content.Split(',');
        string? Field(int i) => i < f.Length ? f[i] : null;
        var report = new AprsNmeaReport { Sentence = sentence, Comment = comment };

        // Only an approved five-character address has a sentence formatter; a proprietary
        // sentence (P...) is laid out as its manufacturer defines, so it is kept as text.
        string type = f[0].Length == 5 && f[0][0] != 'P' ? f[0][2..] : "";
        return type switch
        {
            "GGA" => WithTime(report, Field(1)) with
            {
                Position = ParsePosition(Field(2), Field(3), Field(4), Field(5)),
                FixValid = Field(6) is [>= '0' and <= '9'] q ? q != "0" : null,
                AltitudeMetres = ParseNumber(Field(9)),
            },
            "RMC" => WithTime(report, Field(1)) with
            {
                FixValid = Status(Field(2)),
                Position = ParsePosition(Field(3), Field(4), Field(5), Field(6)),
                SpeedKnots = ParseNumber(Field(7)),
                CourseDegrees = ParseNumber(Field(8)),
            },
            "GLL" => WithTime(report, Field(5)) with
            {
                Position = ParsePosition(Field(1), Field(2), Field(3), Field(4)),
                FixValid = Status(Field(6)),
            },
            "VTG" => report with { CourseDegrees = ParseNumber(Field(1)), SpeedKnots = ParseNumber(Field(5)) },
            "WPL" => report with { Position = ParsePosition(Field(1), Field(2), Field(3), Field(4)), WaypointName = Field(5) is { Length: > 0 } name ? name : null },
            _ => report,
        };
    }

    private static bool? Status(string? s) => s switch
    {
        "A" => true,
        "V" => false,
        _ => null,
    };

    /// <summary>An optional <c>-</c>, then digits with an optional <c>.</c> and fraction.</summary>
    private static double? ParseNumber(string? s)
    {
        if (s is null)
        {
            return null;
        }

        ReadOnlySpan<char> digits = s.StartsWith('-') ? s.AsSpan(1) : s;
        int dot = digits.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? digits : digits[..dot];
        ReadOnlySpan<char> fraction = dot < 0 ? [] : digits[(dot + 1)..];
        return whole.Length + fraction.Length > 0 && AllDigits(whole) && AllDigits(fraction)
            ? double.Parse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// <c>hhmmss</c> with an optional fraction of a second, kept as sent in
    /// <see cref="AprsNmeaReport.TimeText"/>; <see cref="AprsNmeaReport.Time"/> holds it to
    /// TimeOnly's 100 ns.
    /// </summary>
    private static AprsNmeaReport WithTime(AprsNmeaReport report, string? s)
    {
        if (s is null || s.Length < 6 || !AllDigits(s.AsSpan(0, 6)) || (s.Length > 6 && (s[6] != '.' || !AllDigits(s.AsSpan(7)))))
        {
            return report;
        }

        int h = int.Parse(s.AsSpan(0, 2), CultureInfo.InvariantCulture);
        int m = int.Parse(s.AsSpan(2, 2), CultureInfo.InvariantCulture);
        int sec = int.Parse(s.AsSpan(4, 2), CultureInfo.InvariantCulture);
        if (h > 23 || m > 59 || sec > 59)
        {
            return report;
        }

        string fraction = s.Length > 7 ? s[7..] : "";
        long ticks = fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(7, '0').AsSpan(0, 7), CultureInfo.InvariantCulture);
        return report with { Time = new TimeOnly(h, m, sec).Add(TimeSpan.FromTicks(ticks)), TimeText = s };
    }

    /// <summary>
    /// NMEA <c>ddmm.mm,N,dddmm.mm,W</c>. The degrees are whatever digits come before the two minute
    /// digits, since a real sender drops leading zeros, but there must be at least one; the minutes
    /// are below 60, and the value within 90 or 180 degrees. A position needs both coordinates.
    /// </summary>
    private static AprsPosition? ParsePosition(string? lat, string? ns, string? lon, string? ew)
    {
        double? latitude = ParseCoordinate(lat, 90);
        double? longitude = ParseCoordinate(lon, 180);
        if (latitude is not { } la || longitude is not { } lo || ns is not ("N" or "S") || ew is not ("E" or "W"))
        {
            return null;
        }

        return new AprsPosition(ns == "S" ? -la : la, ew == "W" ? -lo : lo);
    }

    private static double? ParseCoordinate(string? s, int limit)
    {
        if (s is null)
        {
            return null;
        }

        int dot = s.IndexOf('.', StringComparison.Ordinal);
        int whole = dot < 0 ? s.Length : dot;
        if (whole < 3 || !AllDigits(s.AsSpan(0, whole)) || (dot >= 0 && !AllDigits(s.AsSpan(dot + 1))))
        {
            return null;
        }

        int degrees = int.TryParse(s.AsSpan(0, whole - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int d) ? d : int.MaxValue;
        double minutes = double.Parse(s.AsSpan(whole - 2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
        double value = degrees + (minutes / 60);
        return minutes < 60 && value <= limit ? value : null;
    }

    private static bool AllDigits(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'A' and <= 'F') or (>= 'a' and <= 'f');

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}

internal static class MaidenheadBeaconCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "the [ Maidenhead beacon is obsolete (APRS12c ch. 5)", 0);
        int close = info.IndexOf((byte)']');
        if (close is not (5 or 7) || !Maidenhead.IsLocator(info[1..close]))
        {
            ctx.Error(AprsDiagnosticCode.InvalidLocator, "expected [ then a 4 or 6 character locator then ]", 1);
            return null;
        }

        return Text.TryDecode(info[(close + 1)..], ctx, close + 1, out string comment)
            ? new AprsMaidenheadBeacon { Locator = Text.Latin1(info[1..close]).ToUpperInvariant(), Comment = comment }
            : null;
    }
}

internal static class QueryCodec
{
    public static AprsData? DecodeGeneral(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        int close = info[1..].IndexOf((byte)'?');
        if (close < 1)
        {
            ctx.Error(AprsDiagnosticCode.InvalidGeneralQuery, "a general query is ? then the query type then ? (APRS12c ch. 15, UAP 5.18)", 1);
            return null;
        }

        string type = Text.Latin1(info.Slice(1, close));
        if (!IsQueryType(type))
        {
            ctx.Error(AprsDiagnosticCode.InvalidGeneralQuery, "a query type is upper-case letters (APRS12c ch. 15)", 1);
            return null;
        }

        int pos = close + 2;
        AprsQueryFootprint? footprint = null;
        string rest = Text.Latin1(info[pos..]);
        if (rest.Length > 0)
        {
            string[] parts = rest.Split(',');
            if (parts.Length != 3
                || Coordinate(parts[0], 90) is not { } lat
                || Coordinate(parts[1], 180) is not { } lon
                || parts[2].Length != 4 || !parts[2].All(char.IsAsciiDigit))
            {
                ctx.Error(AprsDiagnosticCode.InvalidGeneralQuery, "a query footprint is latitude and longitude in degrees (at most 90 and 180) and a radius of exactly 4 digits (APRS12c ch. 15)", pos);
                return null;
            }

            footprint = new AprsQueryFootprint(lat, lon, int.Parse(parts[2], CultureInfo.InvariantCulture));
        }

        return new AprsGeneralQuery { QueryType = type, Footprint = footprint };
    }

    /// <summary>Upper-case letters (APRS12c ch. 15; vectors interpretations.md, "Query types are upper-case letters").</summary>
    internal static bool IsQueryType(string type) => type.Length > 0 && type.All(char.IsAsciiLetterUpper);

    /// <summary>Decimal degrees: an optional <c>-</c> (a positive value may have one leading space instead), digits and an optional fraction.</summary>
    private static decimal? Coordinate(string s, int limit)
    {
        ReadOnlySpan<char> t = s.StartsWith(' ') ? s.AsSpan(1) : s;
        ReadOnlySpan<char> digits = t.StartsWith("-") && !s.StartsWith(' ') ? t[1..] : t;
        int dot = digits.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? digits : digits[..dot];
        ReadOnlySpan<char> fraction = dot < 0 ? [] : digits[(dot + 1)..];
        if (whole.Length + fraction.Length == 0 || whole.ContainsAnyExceptInRange('0', '9') || fraction.ContainsAnyExceptInRange('0', '9')
            || !decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v))
        {
            return null;
        }

        return Math.Abs(v) <= limit ? v : null;
    }

    public static void WriteGeneral(InfoWriter w, AprsGeneralQuery q)
    {
        if (!IsQueryType(q.QueryType))
        {
            throw new ArgumentException("query type must be upper-case letters (APRS12c ch. 15)", nameof(q));
        }

        w.Char('?').Ascii(q.QueryType).Char('?');
        if (q.Footprint is { } f)
        {
            if (f.RadiusMiles is < 0 or > 9999 || f.Latitude is < -90 or > 90 || f.Longitude is < -180 or > 180)
            {
                throw new ArgumentOutOfRangeException(nameof(q), "footprint latitude, longitude or radius out of range");
            }

            // Positive coordinates carry a leading space in place of the sign (APRS12c §15).
            w.Ascii(Signed(f.Latitude)).Char(',').Ascii(Signed(f.Longitude)).Char(',').Digits(f.RadiusMiles, 4);
        }

        static string Signed(decimal v) => v < 0 ? v.ToString(CultureInfo.InvariantCulture) : " " + v.ToString(CultureInfo.InvariantCulture);
    }
}

internal static class CapabilitiesCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        if (!Text.TryDecode(info[1..], ctx, 1, out string text))
        {
            return null;
        }

        // Spaces (U+0020 only) around an item, a token or a value are padding (vectors
        // interpretations.md, "Station capabilities: items, tokens and values").
        var caps = new List<AprsCapability>();
        foreach (string part in text.Split(','))
        {
            string item = part.Trim(' ');
            if (item.Length == 0)
            {
                continue;
            }

            int eq = item.IndexOf('=', StringComparison.Ordinal);
            caps.Add(eq < 0 ? new AprsCapability(item, null) : new AprsCapability(item[..eq].Trim(' '), item[(eq + 1)..].Trim(' ')));
        }

        if (caps.Count == 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidCapabilities, "a capabilities report lists at least one capability (APRS12c ch. 15)", 1);
            return null;
        }

        // Each capability is a TOKEN or TOKEN=VALUE (APRS12c ch. 15). A "token" with spaces in it is
        // free text: a beacon sent with the wrong data type identifier.
        if (caps.Any(c => c.Token.Length == 0 || c.Token.Any(ch => ch is <= ' ' or '\x7F') || (c.Value is { } v && v.Any(ch => ch is < ' ' or '\x7F')))
            && !ctx.Tolerate(
                ctx.Options.AllowFreeTextCapabilities,
                AprsDiagnosticCode.FreeTextCapabilities,
                "station capabilities are TOKEN or TOKEN=VALUE items separated by commas; this is free text (APRS12c ch. 15)",
                1))
        {
            return null;
        }

        return new AprsStationCapabilities { Capabilities = caps };
    }
}

internal static class ThirdPartyCodec
{
    private const int MaxDepth = 4;

    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        if (ctx.Depth >= MaxDepth)
        {
            ctx.Error(AprsDiagnosticCode.InvalidThirdParty, "third-party packets nested too deeply", 0);
            return null;
        }

        // A defect the inner header may tolerate is decoded leniently, with its warning on the inner
        // packet; strict options reject it here, so the packet is invalid-third-party.
        var inner = new DecodeContext(ctx.Options) { Depth = ctx.Depth + 1 };
        if (!Tnc2Codec.TrySplit(info[1..], inner, out Tnc2Codec.Header header, out int infoStart, thirdParty: true))
        {
            string why = string.Join("; ", inner.Diagnostics.Select(d => d.Message));
            ctx.Error(AprsDiagnosticCode.InvalidThirdParty, $"third-party header is not SOURCE>DEST,PATH: ({why}) (APRS12c ch. 17)", 1);
            return null;
        }

        AprsPacket packet = AprsPacket.Build(header, info[(1 + infoStart)..], inner);
        return new AprsThirdPartyTraffic { Packet = packet };
    }

    /// <summary>
    /// For the encoder: whether <paramref name="packet"/> can be written inside a third-party packet
    /// and read back the same. Its header must be one a strict decoder reads, and it must not have
    /// had a defect of its own tolerated when it was decoded: that defect is part of its data
    /// (diagnostics included), so no clean form reproduces it.
    /// </summary>
    public static bool CanWrite(AprsPacket packet, byte[] tnc2)
    {
        if (packet.Diagnostics.Any(d => d.Code is AprsDiagnosticCode.EmptyDestination or AprsDiagnosticCode.EmptyPathEntry or AprsDiagnosticCode.MultipleUsedMarkers
            or AprsDiagnosticCode.NulPaddedAddress or AprsDiagnosticCode.InvalidAx25AddressCharacters))
        {
            return false;
        }

        var ctx = new DecodeContext(AprsParseOptions.Strict);
        return Tnc2Codec.TrySplit(tnc2, ctx, out _, out _, thirdParty: true);
    }
}

internal static class UserDefinedCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        if (info.Length < 3)
        {
            ctx.Error(AprsDiagnosticCode.InvalidUserDefined, "user-defined data needs { then a user ID and a packet type (APRS12c ch. 19)", 0);
            return null;
        }

        return new AprsUserDefinedData { UserId = (char)info[1], PacketType = (char)info[2], Data = info[3..].ToArray() };
    }
}

internal static class TestDataCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx) =>
        Text.TryDecode(info[1..], ctx, 1, out string data) ? new AprsTestData { Data = data } : null;
}

internal static class AgreloCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        // Exactly %, three bearing digits, / and one quality digit (APRS12c Appendix 1): the format has no comment.
        if (info.Length != 6 || !Text.AllDigits(info.Slice(1, 3)) || info[4] != (byte)'/' || !Text.IsDigit(info[5]))
        {
            ctx.Error(AprsDiagnosticCode.InvalidAgreloDf, "Agrelo DF report is exactly %bbb/q", 0);
            return null;
        }

        int bearing = Text.ParseDigits(info.Slice(1, 3));
        if (bearing > 360)
        {
            ctx.Error(AprsDiagnosticCode.InvalidAgreloDf, "Agrelo bearing is over 360 degrees", 1);
            return null;
        }

        return new AprsAgreloDfReport { BearingDegrees = bearing, Quality = info[5] - '0' };
    }
}
