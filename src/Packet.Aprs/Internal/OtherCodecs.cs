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
        if (!Text.IsAscii(data))
        {
            ctx.Error(AprsDiagnosticCode.InvalidWeather, "raw weather data must be ASCII", skip);
            return null;
        }

        return new AprsRawWeatherReport { Format = format, Data = Text.Latin1(data) };
    }
}

internal static class NmeaCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "raw NMEA sentences are obsolete; trackers should send position reports (UAP 5.20)", 0);
        ReadOnlySpan<byte> body = info[1..];
        if (body.Length < 5 || !Text.IsAscii(body) || body.IndexOfAnyInRange((byte)0, (byte)0x1F) >= 0)
        {
            ctx.Error(AprsDiagnosticCode.InvalidNmea, "NMEA sentence must be printable ASCII starting $TTSSS", 0);
            return null;
        }

        string sentence = Text.Latin1(body);
        string content = sentence;
        bool? checksumValid = null;
        int star = sentence.LastIndexOf('*');
        if (star >= 0 && star == sentence.Length - 3 && byte.TryParse(sentence.AsSpan(star + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte expected))
        {
            content = sentence[..star];
            byte sum = 0;
            foreach (char c in content)
            {
                sum ^= (byte)c;
            }

            checksumValid = sum == expected;
            if (!checksumValid.Value)
            {
                ctx.Warn(AprsDiagnosticCode.NmeaChecksumMismatch, $"NMEA checksum is {expected:X2} but the sentence sums to {sum:X2}", 1 + star);
            }
        }

        string[] f = content.Split(',');
        var report = new AprsNmeaReport { Sentence = sentence, ChecksumValid = checksumValid };
        string type = f[0].Length >= 5 ? f[0][2..5] : "";
        return type switch
        {
            "GGA" when f.Length >= 10 => report with
            {
                Time = ParseTime(f[1]),
                Position = ParsePosition(f[2], f[3], f[4], f[5]),
                FixValid = int.TryParse(f[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int q) ? q > 0 : null,
                AltitudeMetres = ParseDouble(f[9]),
            },
            "RMC" when f.Length >= 9 => report with
            {
                Time = ParseTime(f[1]),
                FixValid = f[2] == "A" ? true : f[2] == "V" ? false : null,
                Position = ParsePosition(f[3], f[4], f[5], f[6]),
                SpeedKnots = ParseDouble(f[7]),
                CourseDegrees = ParseDouble(f[8]),
            },
            "GLL" when f.Length >= 5 => report with
            {
                Position = ParsePosition(f[1], f[2], f[3], f[4]),
                Time = f.Length > 5 ? ParseTime(f[5]) : null,
                FixValid = f.Length > 6 ? (f[6] == "A" ? true : f[6] == "V" ? false : null) : null,
            },
            "VTG" when f.Length >= 6 => report with { CourseDegrees = ParseDouble(f[1]), SpeedKnots = ParseDouble(f[5]) },
            "WPL" when f.Length >= 6 => report with { Position = ParsePosition(f[1], f[2], f[3], f[4]), WaypointName = f[5] },
            _ => report,
        };
    }

    private static double? ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static TimeOnly? ParseTime(string s)
    {
        if (s.Length < 6 || !int.TryParse(s.AsSpan(0, 2), CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(s.AsSpan(2, 2), CultureInfo.InvariantCulture, out int m)
            || !double.TryParse(s.AsSpan(4), NumberStyles.Float, CultureInfo.InvariantCulture, out double sec)
            || h > 23 || m > 59 || sec >= 60)
        {
            return null;
        }

        return new TimeOnly(h, m).Add(TimeSpan.FromSeconds(sec));
    }

    /// <summary>NMEA <c>ddmm.mmmm,N,dddmm.mmmm,W</c>.</summary>
    private static AprsPosition? ParsePosition(string lat, string ns, string lon, string ew)
    {
        if (lat.Length < 4 || lon.Length < 5 || ns is not ("N" or "S") || ew is not ("E" or "W")
            || !double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out double la)
            || !double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out double lo))
        {
            return null;
        }

        double latDeg = Math.Floor(la / 100) + ((la % 100) / 60);
        double lonDeg = Math.Floor(lo / 100) + ((lo % 100) / 60);
        if (latDeg > 90 || lonDeg > 180)
        {
            return null;
        }

        return new AprsPosition(ns == "S" ? -latDeg : latDeg, ew == "W" ? -lonDeg : lonDeg);
    }
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
        if (!type.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
        {
            ctx.Error(AprsDiagnosticCode.InvalidGeneralQuery, "query type must be upper-case letters or digits (APRS12c ch. 15)", 1);
            return null;
        }

        int pos = close + 2;
        AprsQueryFootprint? footprint = null;
        string rest = Text.Latin1(info[pos..]);
        if (rest.Length > 0)
        {
            string[] parts = rest.Split(',');
            if (parts.Length != 3
                || !decimal.TryParse(parts[0].TrimStart(' '), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal lat)
                || !decimal.TryParse(parts[1].TrimStart(' '), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal lon)
                || !int.TryParse(parts[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int radius))
            {
                ctx.Error(AprsDiagnosticCode.InvalidGeneralQuery, "query footprint must be latitude,longitude,radius (APRS12c ch. 15)", pos);
                return null;
            }

            footprint = new AprsQueryFootprint(lat, lon, radius);
        }

        return new AprsGeneralQuery { QueryType = type, Footprint = footprint };
    }

    public static void WriteGeneral(InfoWriter w, AprsGeneralQuery q)
    {
        if (q.QueryType.Length == 0 || !q.QueryType.All(c => c is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
        {
            throw new ArgumentException("query type must be upper-case letters or digits (APRS12c ch. 15)", nameof(q));
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

        var caps = new List<AprsCapability>();
        foreach (string part in text.Split(','))
        {
            string item = part.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            int eq = item.IndexOf('=', StringComparison.Ordinal);
            caps.Add(eq < 0 ? new AprsCapability(item, null) : new AprsCapability(item[..eq].Trim(), item[(eq + 1)..].Trim()));
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

        var inner = new DecodeContext(ctx.Options) { Depth = ctx.Depth + 1 };
        if (!Tnc2Codec.TrySplit(info[1..], inner, out Tnc2Codec.Header header, out int infoStart))
        {
            string why = string.Join("; ", inner.Diagnostics.Select(d => d.Message));
            ctx.Error(AprsDiagnosticCode.InvalidThirdParty, $"third-party header is not SOURCE>DEST,PATH: ({why}) (APRS12c ch. 17)", 1);
            return null;
        }

        AprsPacket packet = AprsPacket.Build(header, info[(1 + infoStart)..], inner);
        return new AprsThirdPartyTraffic { Packet = packet };
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
        if (info.Length < 6 || !Text.AllDigits(info.Slice(1, 3)) || info[4] != (byte)'/' || !Text.IsDigit(info[5]))
        {
            ctx.Error(AprsDiagnosticCode.InvalidAgreloDf, "Agrelo DF report is %bbb/q", 0);
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
