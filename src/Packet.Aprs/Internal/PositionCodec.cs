using System.Globalization;

namespace Packet.Aprs.Internal;

/// <summary>
/// The position body shared by position reports, objects and items: an uncompressed
/// (APRS12c §6, §8) or compressed (§9) position with symbol, then the data extension (§7) and
/// the comment.
/// </summary>
internal static class PositionCodec
{
    private const double LatitudeScale = 380926;
    private const double LongitudeScale = 190463;

    public static bool TryReadBody(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, out PositionedFields fields)
    {
        fields = new PositionedFields();
        if (pos >= info.Length)
        {
            ctx.Error(AprsDiagnosticCode.Truncated, "position is missing", pos);
            return false;
        }

        byte first = info[pos];
        if (Text.IsDigit(first))
        {
            if (!TryReadUncompressed(info, pos, ctx, fields))
            {
                return false;
            }

            pos += 19;
            return TryReadExtensionAndComment(info, pos, ctx, fields);
        }

        if (first is (byte)'/' or (byte)'\\' || Text.IsUpper(first) || first is >= (byte)'a' and <= (byte)'j')
        {
            if (!TryReadCompressed(info, pos, ctx, fields, out bool csIsWind))
            {
                return false;
            }

            pos += 13;
            return TryReadCompressedTail(info, pos, ctx, fields, csIsWind);
        }

        ctx.Error(AprsDiagnosticCode.InvalidPosition, $"'{(char)first}' cannot start a position: expected a latitude digit or a compressed-position symbol table", pos);
        return false;
    }

    // ---------------------------------------------------------------- uncompressed

    private static bool TryReadUncompressed(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, PositionedFields fields)
    {
        if (info.Length < pos + 19)
        {
            ctx.Error(AprsDiagnosticCode.Truncated, "uncompressed position needs 19 characters (latitude, table, longitude, symbol)", pos);
            return false;
        }

        if (!TryReadLatitude(info.Slice(pos, 8), pos, ctx, out double latitude, out int ambiguity))
        {
            return false;
        }

        char table = (char)info[pos + 8];
        if (!AprsSymbol.IsValidTable(table))
        {
            ctx.Error(AprsDiagnosticCode.InvalidSymbolTable, $"'{table}' is not a symbol table or overlay (/ \\ 0-9 A-Z)", pos + 8);
            return false;
        }

        if (!TryReadLongitude(info.Slice(pos + 9, 9), pos + 9, ambiguity, ctx, out double longitude))
        {
            return false;
        }

        char code = (char)info[pos + 18];
        if (code is < '!' or > '~')
        {
            ctx.Error(AprsDiagnosticCode.InvalidSymbolCode, $"byte 0x{(int)code:X2} is not a symbol code (! to ~)", pos + 18);
            return false;
        }

        fields.Position = new AprsPosition(latitude, longitude, ambiguity);
        fields.Symbol = new AprsSymbol(table, code);
        return true;
    }

    /// <summary><c>DDMM.hhN</c>, trailing digits optionally blanked for ambiguity.</summary>
    private static bool TryReadLatitude(ReadOnlySpan<byte> s, int offset, DecodeContext ctx, out double latitude, out int ambiguity)
    {
        latitude = 0;
        ambiguity = 0;
        if (!Text.IsDigit(s[0]) || !Text.IsDigit(s[1]) || s[4] != (byte)'.')
        {
            ctx.Error(AprsDiagnosticCode.InvalidLatitude, $"'{Text.Latin1(s)}' is not a latitude: expected DDMM.hhN (APRS12c ch. 6)", offset);
            return false;
        }

        if (!TryReadMinuteDigits(s[2], s[3], s[5], s[6], maxAmbiguity: 4, out double minutes, out ambiguity))
        {
            ctx.Error(AprsDiagnosticCode.InvalidLatitude, $"'{Text.Latin1(s)}' has invalid latitude minutes (digits, with only trailing spaces for ambiguity)", offset);
            return false;
        }

        if (!TryHemisphere(s[7], (byte)'N', (byte)'S', offset + 7, ctx, out bool negative))
        {
            return false;
        }

        int degrees = Text.ParseDigits(s[..2]);
        latitude = degrees + (minutes / 60);
        if (latitude > 90)
        {
            ctx.Error(AprsDiagnosticCode.InvalidLatitude, $"latitude {Text.Latin1(s)} is beyond 90 degrees", offset);
            return false;
        }

        latitude = negative ? -latitude : latitude;
        return true;
    }

    /// <summary><c>DDDMM.hhW</c>. Ambiguity comes from the latitude; blanked digits here are optional.</summary>
    private static bool TryReadLongitude(ReadOnlySpan<byte> s, int offset, int ambiguity, DecodeContext ctx, out double longitude)
    {
        longitude = 0;
        if (!Text.IsDigit(s[0]) || !Text.IsDigit(s[1]) || !Text.IsDigit(s[2]) || s[5] != (byte)'.')
        {
            ctx.Error(AprsDiagnosticCode.InvalidLongitude, $"'{Text.Latin1(s)}' is not a longitude: expected DDDMM.hhW (APRS12c ch. 6)", offset);
            return false;
        }

        // Digits the latitude's ambiguity covers may be anything numeric or a space; the rest must be digits.
        Span<byte> digits = [s[3], s[4], s[6], s[7]];
        for (int i = 0; i < 4; i++)
        {
            bool masked = i >= 4 - ambiguity;
            if (masked)
            {
                if (digits[i] == (byte)' ' || Text.IsDigit(digits[i]))
                {
                    digits[i] = (byte)'0';
                    continue;
                }
            }
            else if (Text.IsDigit(digits[i]))
            {
                continue;
            }

            ctx.Error(AprsDiagnosticCode.InvalidLongitude, $"'{Text.Latin1(s)}' has invalid longitude minutes", offset);
            return false;
        }

        if (!TryReadMinuteDigits(digits[0], digits[1], digits[2], digits[3], maxAmbiguity: 0, out double minutes, out _))
        {
            ctx.Error(AprsDiagnosticCode.InvalidLongitude, $"'{Text.Latin1(s)}' has longitude minutes of 60 or more", offset);
            return false;
        }

        minutes += AmbiguityCentre(ambiguity);
        if (!TryHemisphere(s[8], (byte)'E', (byte)'W', offset + 8, ctx, out bool negative))
        {
            return false;
        }

        longitude = Text.ParseDigits(s[..3]) + (minutes / 60);
        if (longitude > 180)
        {
            ctx.Error(AprsDiagnosticCode.InvalidLongitude, $"longitude {Text.Latin1(s)} is beyond 180 degrees", offset);
            return false;
        }

        longitude = negative ? -longitude : longitude;
        return true;
    }

    /// <summary>Reads MM.hh from four digit positions (tens, units, tenths, hundredths). Trailing spaces
    /// up to <paramref name="maxAmbiguity"/> are ambiguity; the result is the centre of the range.</summary>
    private static bool TryReadMinuteDigits(byte tens, byte units, byte tenths, byte hundredths, int maxAmbiguity, out double minutes, out int ambiguity)
    {
        minutes = 0;
        ReadOnlySpan<byte> d = [tens, units, tenths, hundredths];
        ambiguity = 0;
        for (int i = 3; i >= 0 && d[i] == (byte)' '; i--)
        {
            ambiguity++;
        }

        if (ambiguity > maxAmbiguity)
        {
            return false;
        }

        for (int i = 0; i < 4 - ambiguity; i++)
        {
            if (!Text.IsDigit(d[i]))
            {
                return false;
            }
        }

        double value = 0;
        double[] weights = [10, 1, 0.1, 0.01];
        for (int i = 0; i < 4 - ambiguity; i++)
        {
            value += (d[i] - '0') * weights[i];
        }

        minutes = value + AmbiguityCentre(ambiguity);
        return minutes < 60;
    }

    /// <summary>Half the resolution left by an ambiguity level, in minutes, so an ambiguous
    /// position decodes to the centre of its range (docs/aprs-spec-interpretations.md).</summary>
    private static double AmbiguityCentre(int ambiguity) => ambiguity switch
    {
        0 => 0,
        1 => 0.05,
        2 => 0.5,
        3 => 5,
        _ => 30,
    };

    private static bool TryHemisphere(byte b, byte positive, byte negative, int offset, DecodeContext ctx, out bool isNegative)
    {
        isNegative = false;
        if (b == positive || b == negative)
        {
            isNegative = b == negative;
            return true;
        }

        byte upper = (byte)(b & ~0x20);
        if ((upper == positive || upper == negative) && Text.IsLower(b))
        {
            isNegative = upper == negative;
            return ctx.Tolerate(
                ctx.Options.AllowLowercaseHemisphere,
                AprsDiagnosticCode.LowercaseHemisphere,
                $"hemisphere '{(char)b}' must be upper case (UAP 5.9)",
                offset);
        }

        ctx.Error(
            positive == (byte)'N' ? AprsDiagnosticCode.InvalidLatitude : AprsDiagnosticCode.InvalidLongitude,
            $"'{(char)b}' is not a hemisphere: expected {(char)positive} or {(char)negative}",
            offset);
        return false;
    }

    // ---------------------------------------------------------------- compressed

    private static bool TryReadCompressed(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, PositionedFields fields, out bool csIsWind)
    {
        csIsWind = false;
        if (info.Length < pos + 13)
        {
            ctx.Error(AprsDiagnosticCode.Truncated, "compressed position needs 13 characters", pos);
            return false;
        }

        ReadOnlySpan<byte> c = info.Slice(pos, 13);
        char table = (char)c[0];
        if (table is >= 'a' and <= 'j')
        {
            table = (char)('0' + (table - 'a'));
        }

        if (!Base91.TryDecode(c.Slice(1, 4), out long y) || !Base91.TryDecode(c.Slice(5, 4), out long x))
        {
            ctx.Error(AprsDiagnosticCode.InvalidCompressedPosition, "compressed latitude/longitude must be base-91 characters ! to { (APRS12c ch. 9)", pos + 1);
            return false;
        }

        double latitude = 90 - (y / LatitudeScale);
        double longitude = -180 + (x / LongitudeScale);
        if (latitude < -90 || longitude > 180)
        {
            ctx.Error(AprsDiagnosticCode.InvalidCompressedPosition, "compressed position is outside the globe", pos + 1);
            return false;
        }

        char code = (char)c[9];
        if (code is < '!' or > '~')
        {
            ctx.Error(AprsDiagnosticCode.InvalidSymbolCode, $"byte 0x{(int)code:X2} is not a symbol code (! to ~)", pos + 9);
            return false;
        }

        fields.IsCompressed = true;
        fields.Position = new AprsPosition(latitude, longitude);
        fields.Symbol = new AprsSymbol(table, code);

        byte cByte = c[10];
        byte sByte = c[11];
        byte tByte = c[12];
        if (cByte == (byte)' ')
        {
            // No course/speed, range or altitude; the s and T bytes are filler (APRS12c §9).
            return true;
        }

        if (!Base91.IsDigit(cByte) || !Base91.IsDigit(sByte) || !Base91.IsDigit(tByte))
        {
            ctx.Error(AprsDiagnosticCode.InvalidCompressedPosition, "compressed cs and T bytes must be base-91 characters", pos + 10);
            return false;
        }

        int cv = cByte - 33;
        int sv = sByte - 33;
        int tv = tByte - 33;
        if (tv > 63)
        {
            if (!ctx.Tolerate(
                    ctx.Options.AllowCompressionTypeReservedBits,
                    AprsDiagnosticCode.CompressionTypeReservedBits,
                    "compression type byte sets its unused high bits (APRS12c ch. 9); ignored",
                    pos + 12))
            {
                return false;
            }

            tv &= 0x3F;
        }

        var type = AprsCompressionType.FromBits(tv);
        fields.CompressionType = type;
        if (fields.Symbol.IsWeatherStation && cByte != (byte)'{' && type.Source != AprsNmeaSource.Gga)
        {
            csIsWind = true;
            fields.Weather = new AprsWeather
            {
                WindDirectionDegrees = cv * 4,
                WindSpeedMph = Units.KnotsToMph(Math.Pow(1.08, sv) - 1),
            };
        }
        else if (type.Source == AprsNmeaSource.Gga)
        {
            fields.AltitudeFeet = Math.Pow(1.002, (cv * 91) + sv);
        }
        else if (cByte == (byte)'{')
        {
            fields.RadioRangeMiles = 2 * Math.Pow(1.08, sv);
        }
        else if (cv <= 89)
        {
            // The compressed course is 0-356 in 4-degree steps with no "unknown" value, so 0 is
            // north: reported as 360, the convention for north (as Ham::APRS::FAP does).
            fields.CourseDegrees = cv == 0 ? 360 : cv * 4;
            fields.SpeedKnots = Math.Pow(1.08, sv) - 1;
        }
        else
        {
            ctx.Error(AprsDiagnosticCode.InvalidCompressedPosition, "compressed course byte is out of range", pos + 10);
            return false;
        }

        return true;
    }

    private static bool TryReadCompressedTail(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, PositionedFields fields, bool csIsWind)
    {
        if (fields.Symbol.IsWeatherStation)
        {
            var weather = fields.Weather ?? new AprsWeather();

            // LoRa trackers send the uncompressed DDD/SSS wind extension after a compressed
            // position, whose cs bytes already carry wind (UAP §5.33).
            if (info.Length >= pos + 7 && IsCourseSpeed(info.Slice(pos, 7)))
            {
                if (!ctx.Tolerate(
                        ctx.Options.AllowWindExtensionAfterCompressed,
                        AprsDiagnosticCode.WindExtensionAfterCompressed,
                        "an uncompressed DDD/SSS wind extension follows a compressed position (UAP 5.33)",
                        pos)
                    || !WeatherCodec.TryReadWindExtension(info, ref pos, ctx, ref weather))
                {
                    return false;
                }

                // Re-encoding puts this wind in the cs bytes, which then need a compression type.
                if (weather.WindDirectionDegrees is not null || weather.WindSpeedMph is not null)
                {
                    fields.CompressionType ??= AprsCompressionType.Default;
                }
            }

            if (!WeatherCodec.TryReadFields(info, ref pos, ctx, ref weather, positionless: false))
            {
                return false;
            }

            fields.Weather = weather;
            return CommentCodec.TryReadWeatherTail(info, pos, ctx, fields);
        }

        _ = csIsWind;
        return CommentCodec.TryRead(info, pos, ctx, fields);
    }

    // ---------------------------------------------------------------- data extension

    private static bool TryReadExtensionAndComment(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, PositionedFields fields)
    {
        if (fields.Symbol.IsWeatherStation)
        {
            var weather = new AprsWeather();
            if (!WeatherCodec.TryReadWind(info, ref pos, ctx, ref weather, out bool windAsFields) ||
                !WeatherCodec.TryReadFields(info, ref pos, ctx, ref weather, positionless: false, windAsFields))
            {
                return false;
            }

            fields.Weather = weather;
            return CommentCodec.TryReadWeatherTail(info, pos, ctx, fields);
        }

        if (!TryReadDataExtension(info, ref pos, ctx, fields))
        {
            return false;
        }

        return CommentCodec.TryRead(info, pos, ctx, fields);
    }

    internal static bool TryReadDataExtension(ReadOnlySpan<byte> info, ref int pos, DecodeContext ctx, PositionedFields fields)
    {
        if (info.Length < pos + 7)
        {
            return true;
        }

        ReadOnlySpan<byte> ext = info.Slice(pos, 7);
        if (fields.Symbol is { Table: '\\', Code: 'l' } && TryReadAreaObject(ext, out AprsAreaObject area))
        {
            fields.AreaObject = area;
            pos += 7;
            return true;
        }

        if (IsCourseSpeed(ext))
        {
            int at = pos;
            pos += 7;
            if (!TryCourseSpeedValues(ext, at, ctx, out int? course, out int? speed))
            {
                return false;
            }

            fields.CourseDegrees = course;
            fields.SpeedKnots = speed;
            if (fields.Symbol is { Table: '/', Code: '\\' } && info.Length >= pos + 8 && TryReadBearing(info.Slice(pos, 8), out AprsDfBearing bearing))
            {
                fields.DfBearing = bearing;
                pos += 8;
            }
            else if (fields.Symbol.Code == '@' && StormCodec.TryRead(info, ref pos, out AprsStorm? storm))
            {
                fields.Storm = storm;
            }

            return true;
        }

        if (ext.StartsWith("PHG"u8) && Text.IsDigit(ext[3]) && ext[4] >= (byte)'0' && ext[4] <= (byte)'~' && Text.IsDigit(ext[5]) && Text.IsDigit(ext[6]))
        {
            int? rate = null;
            if (info.Length >= pos + 9 && info[pos + 8] == (byte)'/' && BeaconRate(info[pos + 7]) is { } r)
            {
                rate = r;
            }

            fields.Phg = new AprsPhg(ext[3] - '0', ext[4] - '0', ext[5] - '0', ext[6] - '0', rate);
            pos += rate is null ? 7 : 9;
            return true;
        }

        if (ext.StartsWith("RNG"u8) && Text.AllDigits(ext[3..]))
        {
            fields.RadioRangeMiles = Text.ParseDigits(ext[3..]);
            pos += 7;
            return true;
        }

        if (ext.StartsWith("DFS"u8) && Text.IsDigit(ext[3]) && ext[4] >= (byte)'0' && ext[4] <= (byte)'~' && Text.IsDigit(ext[5]) && Text.IsDigit(ext[6]))
        {
            fields.DfSignalStrength = new AprsDfSignalStrength(ext[3] - '0', ext[4] - '0', ext[5] - '0', ext[6] - '0');
            pos += 7;
            return true;
        }

        return true;
    }

    /// <summary>PHGR beacon rate character: 1-9, then A = 10 up to Z = 35.</summary>
    private static int? BeaconRate(byte b) => b switch
    {
        >= (byte)'1' and <= (byte)'9' => b - '0',
        >= (byte)'A' and <= (byte)'Z' => b - 'A' + 10,
        _ => null,
    };

    /// <summary>Three characters, <c>/</c>, three characters, where each group is all digits, all dots or all spaces.</summary>
    internal static bool IsCourseSpeed(ReadOnlySpan<byte> ext) =>
        ext.Length >= 7 && ext[3] == (byte)'/' && IsValueGroup(ext[..3]) && IsValueGroup(ext.Slice(4, 3));

    internal static bool IsValueGroup(ReadOnlySpan<byte> g) =>
        Text.AllDigits(g) || g.SequenceEqual("..."u8) || g.SequenceEqual("   "u8);

    private static bool TryCourseSpeedValues(ReadOnlySpan<byte> ext, int offset, DecodeContext ctx, out int? course, out int? speed)
    {
        course = Text.AllDigits(ext[..3]) ? Text.ParseDigits(ext[..3]) : null;
        speed = Text.AllDigits(ext.Slice(4, 3)) ? Text.ParseDigits(ext.Slice(4, 3)) : null;
        if (course > 360)
        {
            course = null;
            return ctx.Tolerate(ctx.Options.AllowOutOfRangeValues, AprsDiagnosticCode.OutOfRangeValue, "course is over 360 degrees; ignored", offset);
        }

        return true;
    }

    private static bool TryReadBearing(ReadOnlySpan<byte> s, out AprsDfBearing bearing)
    {
        bearing = default;
        if (s[0] != (byte)'/' || s[4] != (byte)'/' || !Text.AllDigits(s.Slice(1, 3)) || !Text.AllDigits(s.Slice(5, 3)))
        {
            return false;
        }

        int brg = Text.ParseDigits(s.Slice(1, 3));
        if (brg > 360)
        {
            return false;
        }

        bearing = new AprsDfBearing(brg, s[5] - '0', s[6] - '0', s[7] - '0');
        return true;
    }

    /// <summary><c>Tyy/Cxx</c>, where <c>/C</c> is <c>/0</c>-<c>/9</c> or <c>10</c>-<c>15</c> for colours 10-15.</summary>
    private static bool TryReadAreaObject(ReadOnlySpan<byte> s, out AprsAreaObject area)
    {
        area = default;
        if (!Text.IsDigit(s[0]) || !Text.AllDigits(s.Slice(1, 2)) || !Text.AllDigits(s.Slice(5, 2)) || !Text.IsDigit(s[4]))
        {
            return false;
        }

        int color;
        if (s[3] == (byte)'/')
        {
            color = s[4] - '0';
        }
        else if (s[3] == (byte)'1' && s[4] <= (byte)'5')
        {
            color = 10 + (s[4] - '0');
        }
        else
        {
            return false;
        }

        area = new AprsAreaObject((AprsAreaShape)(s[0] - '0'), (AprsAreaColor)color, Text.ParseDigits(s.Slice(1, 2)), Text.ParseDigits(s.Slice(5, 2)));
        return true;
    }

    // ---------------------------------------------------------------- encoding

    public static void WriteBody(InfoWriter w, AprsPositionedData d)
    {
        d.Position.Validate(nameof(d.Position));
        d.Symbol.Validate(nameof(d.Symbol));
        if (d.IsCompressed)
        {
            WriteCompressed(w, d);
        }
        else
        {
            WriteUncompressed(w, d.Position, d.Symbol, d.Dao);
            WriteDataExtension(w, d);
        }

        if (d.Weather is { } weather)
        {
            if (!d.Symbol.IsWeatherStation)
            {
                throw new ArgumentException("weather data needs a weather-station symbol (_) (APRS12c ch. 12)", nameof(d.Weather));
            }

            WeatherCodec.WriteFields(w, weather, positionless: false);
            if (d.Comment.Length > 0)
            {
                throw new ArgumentException("a weather report has no comment field (APRS12c ch. 12, UAP 2.7.1)", nameof(d.Comment));
            }

            CommentCodec.WriteTrailer(w, d);
            return;
        }

        CommentCodec.Write(w, d, altitudeInCs: AltitudeFitsInCs(d));
    }

    /// <summary>
    /// With a GGA compression type the cs bytes carry altitude, but only to 0.2% (1.002^cs feet).
    /// An altitude that is not exactly representable there is also written as <c>/A=</c>, which
    /// the decoder prefers, so it round-trips.
    /// </summary>
    internal static bool AltitudeFitsInCs(AprsPositionedData d)
    {
        if (!d.IsCompressed || d.CompressionType?.Source != AprsNmeaSource.Gga || d.AltitudeFeet is not { } alt || alt < 1)
        {
            return false;
        }

        long cs = (long)Math.Round(Math.Log(alt) / Math.Log(1.002));
        return Math.Abs(Math.Pow(1.002, cs) - alt) <= 1e-9 * alt;
    }

    internal static void WriteUncompressed(InfoWriter w, AprsPosition position, AprsSymbol symbol, AprsDao? dao)
    {
        (string lat, string lon) = FormatUncompressed(position, dao);
        w.Ascii(lat).Char(symbol.Table).Ascii(lon).Char(symbol.Code);
    }

    /// <summary>Formats DDMM.hhN / DDDMM.hhW with ambiguity blanks. With a DAO the position is
    /// rounded to the DAO's finer unit first so the DAO digits and these digits stay consistent.</summary>
    internal static (string Latitude, string Longitude) FormatUncompressed(AprsPosition position, AprsDao? dao)
    {
        string lat = FormatCoordinate(Math.Abs(position.Latitude), 2, dao) + (double.IsNegative(position.Latitude) ? "S" : "N");
        string lon = FormatCoordinate(Math.Abs(position.Longitude), 3, dao) + (double.IsNegative(position.Longitude) ? "W" : "E");
        return (Blank(lat, position.Ambiguity), Blank(lon, position.Ambiguity));

        static string Blank(string coordinate, int ambiguity)
        {
            // Blank from the hundredths backwards, skipping the decimal point: hh then mm.
            char[] c = coordinate.ToCharArray();
            int[] order = [c.Length - 2, c.Length - 3, c.Length - 5, c.Length - 6];
            for (int i = 0; i < ambiguity; i++)
            {
                c[order[i]] = ' ';
            }

            return new string(c);
        }
    }

    internal static string FormatCoordinate(double degrees, int degreeDigits, AprsDao? dao)
    {
        long hundredths = DaoCodec.SplitForDao(degrees, dao, out _);
        long whole = hundredths / 6000;
        long rem = hundredths % 6000;
        return whole.ToString(CultureInfo.InvariantCulture).PadLeft(degreeDigits, '0')
            + (rem / 100).ToString("00", CultureInfo.InvariantCulture) + "."
            + (rem % 100).ToString("00", CultureInfo.InvariantCulture);
    }

    private static void WriteCompressed(InfoWriter w, AprsPositionedData d)
    {
        if (d.Position.Ambiguity != 0)
        {
            throw new ArgumentException("a compressed position cannot carry position ambiguity (APRS12c ch. 9)", nameof(d.Position));
        }

        char table = d.Symbol.Table is >= '0' and <= '9' ? (char)('a' + (d.Symbol.Table - '0')) : d.Symbol.Table;
        long y = (long)Math.Round((90 - d.Position.Latitude) * LatitudeScale);
        long x = (long)Math.Round((180 + d.Position.Longitude) * LongitudeScale);
        w.Char(table).Base91(Math.Min(y, Base91.MaxValue(4)), 4).Base91(Math.Min(x, Base91.MaxValue(4)), 4).Char(d.Symbol.Code);

        if (d.Phg is not null || d.DfSignalStrength is not null || d.AreaObject is not null || d.DfBearing is not null || d.Storm is not null)
        {
            throw new ArgumentException("a compressed position cannot carry a 7-byte data extension (PHG, DFS, area, DF bearing, storm) (APRS12c ch. 9)");
        }

        var type = d.CompressionType;
        if (type?.Source != AprsNmeaSource.Gga && d.Weather is { } wx && d.Symbol.IsWeatherStation && (wx.WindDirectionDegrees is not null || wx.WindSpeedMph is not null))
        {
            int c = (int)Math.Round((wx.WindDirectionDegrees ?? 0) / 4.0) % 90;
            int s = SpeedCode(Units.MphToKnots(wx.WindSpeedMph ?? 0));
            w.Byte((byte)(c + 33)).Byte((byte)(s + 33));
            WriteType(w, type ?? AprsCompressionType.Default);
        }
        else if (type?.Source == AprsNmeaSource.Gga && d.AltitudeFeet is { } alt)
        {
            if (d.CourseDegrees is not null || d.SpeedKnots is not null || d.RadioRangeMiles is not null)
            {
                throw new ArgumentException("with a GGA compression type the cs bytes carry altitude, so course/speed and range cannot also be sent");
            }

            long cs = alt <= 1 ? 0 : (long)Math.Round(Math.Log(alt) / Math.Log(1.002));
            if (cs > (91 * 91) - 1)
            {
                throw new ArgumentOutOfRangeException(nameof(d.AltitudeFeet), "altitude too high for compressed form");
            }

            w.Base91(cs, 2);
            WriteType(w, type.Value);
        }
        else if (type?.Source == AprsNmeaSource.Gga && d.AltitudeFeet is null)
        {
            throw new ArgumentException("a GGA compression type means the cs bytes carry altitude; set AltitudeFeet or choose another source");
        }
        else if (d.CourseDegrees is not null || d.SpeedKnots is not null)
        {
            if (d.RadioRangeMiles is not null)
            {
                throw new ArgumentException("a compressed position carries either course/speed or radio range, not both (APRS12c ch. 9)");
            }

            if (d.CourseDegrees is not { } course || course is < 1 or > 360)
            {
                throw new ArgumentOutOfRangeException(nameof(d.CourseDegrees), "a compressed course/speed needs a course 1-360; compressed form has no 'unknown' course (APRS12c ch. 9)");
            }

            w.Byte((byte)(((int)Math.Round(course / 4.0) % 90) + 33)).Byte((byte)(SpeedCode(d.SpeedKnots ?? 0) + 33));
            WriteType(w, type ?? AprsCompressionType.Default);
        }
        else if (d.RadioRangeMiles is { } range)
        {
            int s = range <= 2 ? 0 : (int)Math.Round(Math.Log(range / 2) / Math.Log(1.08));
            if (s > 90)
            {
                throw new ArgumentOutOfRangeException(nameof(d.RadioRangeMiles), "range too large for compressed form");
            }

            w.Char('{').Byte((byte)(s + 33));
            WriteType(w, type ?? AprsCompressionType.Default);
        }
        else
        {
            // No cs data: a space, then filler (the spec's example uses "sT").
            w.Ascii(" sT");
        }
    }

    private static void WriteType(InfoWriter w, AprsCompressionType type) => w.Byte((byte)(type.Bits + 33));

    private static int SpeedCode(double knots)
    {
        int s = knots <= 0 ? 0 : (int)Math.Round(Math.Log(knots + 1) / Math.Log(1.08));
        return s > 90 ? throw new ArgumentOutOfRangeException(nameof(knots), "speed too high for compressed form") : s;
    }

    private static void WriteDataExtension(InfoWriter w, AprsPositionedData d)
    {
        bool courseSpeed = d.CourseDegrees is not null || d.SpeedKnots is not null || d.DfBearing is not null || d.Storm is not null;
        int count = (courseSpeed ? 1 : 0) + (d.Phg is null ? 0 : 1) + (d.RadioRangeMiles is null ? 0 : 1)
            + (d.DfSignalStrength is null ? 0 : 1) + (d.AreaObject is null ? 0 : 1);
        if (count > 1)
        {
            throw new ArgumentException("only one data extension (course/speed, PHG, RNG, DFS or area object) fits in a report (APRS12c ch. 7)");
        }

        if (d.Weather is not null)
        {
            if (count > 0)
            {
                throw new ArgumentException("in a weather report the data extension carries wind; course/speed, PHG, RNG, DFS and area cannot be sent");
            }

            WeatherCodec.WriteWind(w, d.Weather);
            return;
        }

        if (d.AreaObject is { } area)
        {
            area.Validate(nameof(d.AreaObject));
            if (d.Symbol is not { Table: '\\', Code: 'l' })
            {
                throw new ArgumentException("an area object needs the \\l symbol (APRS12c ch. 11)", nameof(d.AreaObject));
            }

            int color = (int)area.Color;
            w.Digits((int)area.Shape, 1).Digits(area.LatitudeOffsetCode, 2);
            _ = color < 10 ? w.Char('/').Digits(color, 1) : w.Digits(color, 2);
            w.Digits(area.LongitudeOffsetCode, 2);
        }
        else if (courseSpeed)
        {
            WriteCourseSpeed(w, d.CourseDegrees, d.SpeedKnots);
            if (d.DfBearing is { } bearing)
            {
                bearing.Validate(nameof(d.DfBearing));
                if (d.Symbol is not { Table: '/', Code: '\\' })
                {
                    throw new ArgumentException("a DF bearing needs the /\\ DF symbol (APRS12c ch. 8)", nameof(d.DfBearing));
                }

                w.Char('/').Digits(bearing.BearingDegrees, 3).Char('/').Digits(bearing.Number, 1).Digits(bearing.Range, 1).Digits(bearing.Quality, 1);
            }

            if (d.Storm is { } storm)
            {
                if (d.Symbol.Code != '@')
                {
                    throw new ArgumentException("storm data needs a hurricane symbol (@) (APRS12c ch. 12)", nameof(d.Storm));
                }

                StormCodec.Write(w, storm);
            }
        }
        else if (d.Phg is { } phg)
        {
            WritePhg(w, phg);
        }
        else if (d.RadioRangeMiles is { } range)
        {
            if (range != Math.Floor(range) || range is < 0 or > 9999)
            {
                throw new ArgumentOutOfRangeException(nameof(d.RadioRangeMiles), "RNG carries whole miles 0-9999");
            }

            w.Ascii("RNG").Digits((int)range, 4);
        }
        else if (d.DfSignalStrength is { } dfs)
        {
            dfs.Validate(nameof(d.DfSignalStrength));
            w.Ascii("DFS").Digits(dfs.StrengthCode, 1).Char((char)('0' + dfs.HeightCode)).Digits(dfs.GainCode, 1).Digits(dfs.DirectivityCode, 1);
        }
    }

    internal static void WritePhg(InfoWriter w, AprsPhg phg)
    {
        phg.Validate(nameof(phg));
        w.Ascii("PHG").Digits(phg.PowerCode, 1).Char((char)('0' + phg.HeightCode)).Digits(phg.GainCode, 1).Digits(phg.DirectivityCode, 1);
        if (phg.BeaconsPerHour is { } rate)
        {
            w.Char(rate <= 9 ? (char)('0' + rate) : (char)('A' + rate - 10)).Char('/');
        }
    }

    internal static void WriteCourseSpeed(InfoWriter w, int? course, double? speed)
    {
        if (course is < 0 or > 360)
        {
            throw new ArgumentOutOfRangeException(nameof(course), "course must be 0-360");
        }

        if (speed is { } s && (s < 0 || s > 999 || s != Math.Floor(s)))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), "an uncompressed speed is whole knots 0-999");
        }

        _ = course is { } c ? w.Digits(c, 3) : w.Ascii("...");
        w.Char('/');
        _ = speed is { } v ? w.Digits((int)v, 3) : w.Ascii("...");
    }
}
