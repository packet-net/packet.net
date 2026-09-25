namespace Packet.Aprs.Internal;

/// <summary>Mic-E (APRS12c §10).</summary>
internal static class MicECodec
{
    private static readonly AprsMicEMessage[] Standard =
    [
        AprsMicEMessage.Emergency, AprsMicEMessage.Priority, AprsMicEMessage.Special, AprsMicEMessage.Committed,
        AprsMicEMessage.Returning, AprsMicEMessage.InService, AprsMicEMessage.EnRoute, AprsMicEMessage.OffDuty,
    ];

    private static readonly AprsMicEMessage[] Custom =
    [
        AprsMicEMessage.Emergency, AprsMicEMessage.Custom6, AprsMicEMessage.Custom5, AprsMicEMessage.Custom4,
        AprsMicEMessage.Custom3, AprsMicEMessage.Custom2, AprsMicEMessage.Custom1, AprsMicEMessage.Custom0,
    ];

    // ------------------------------------------------------------------ decoding

    public static AprsData? Decode(ReadOnlySpan<byte> info, AprsAddress destination, DecodeContext ctx)
    {
        if (!TryDecodeDestination(destination, ctx, out Destination dest))
        {
            return null;
        }

        if (info.Length < 9)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMicEInformation, "Mic-E information field is shorter than 9 bytes (APRS12c ch. 10)", 0);
            return null;
        }

        byte dti = info[0];
        if (dti is 0x1c or 0x1d)
        {
            ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "Mic-E data type 0x1C/0x1D is from Rev 0 beta units and obsolete (APRS12c ch. 10)", 0);
        }

        int d = info[1] - 28;
        int m = info[2] - 28;
        int h = info[3] - 28;
        if (info[1] is < 38 or > 127 || info[2] is < 38 or > 97 || info[3] is < 28 or > 127)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMicEInformation, "Mic-E longitude bytes are out of range (APRS12c ch. 10)", 1);
            return null;
        }

        if (dest.LongitudeOffset)
        {
            d += 100;
        }

        if (d is >= 180 and <= 189)
        {
            d -= 80;
        }
        else if (d is >= 190 and <= 199)
        {
            d -= 190;
        }

        if (m >= 60)
        {
            m -= 60;
        }

        if (d > 179 || m > 59 || h > 99)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMicEInformation, "Mic-E longitude decodes out of range", 1);
            return null;
        }

        for (int i = 4; i <= 6; i++)
        {
            if (info[i] is < 28 or > 127)
            {
                ctx.Error(AprsDiagnosticCode.InvalidMicEInformation, "Mic-E speed/course bytes are out of range", i);
                return null;
            }
        }

        int sp = info[4] - 28;
        int dc = info[5] - 28;
        int se = info[6] - 28;
        int speed = (sp * 10) + (dc / 10);
        int course = ((dc % 10) * 100) + se;
        if (speed >= 800)
        {
            speed -= 800;
        }

        if (course >= 400)
        {
            course -= 400;
        }

        if (course > 360)
        {
            if (!ctx.Tolerate(ctx.Options.AllowOutOfRangeValues, AprsDiagnosticCode.OutOfRangeValue, "Mic-E course is over 360 degrees; ignored", 5))
            {
                return null;
            }

            course = 0;
        }

        char code = (char)info[7];
        char table = (char)info[8];
        if (!AprsSymbol.IsValidTable(table))
        {
            ctx.Error(AprsDiagnosticCode.InvalidSymbolTable, $"'{table}' is not a symbol table or overlay", 8);
            return null;
        }

        if (code is < '!' or > '~')
        {
            ctx.Error(AprsDiagnosticCode.InvalidSymbolCode, $"byte 0x{(int)code:X2} is not a symbol code", 7);
            return null;
        }

        // Longitude minutes with the latitude's ambiguity applied, centred like any ambiguous position.
        int[] lonDigits = [m / 10, m % 10, h / 10, h % 10];
        double lonMinutes = Minutes(lonDigits, dest.Ambiguity);
        double longitude = d + (lonMinutes / 60);
        if (dest.West)
        {
            longitude = -longitude;
        }

        var fields = new PositionedFields
        {
            Position = new AprsPosition(dest.Latitude, longitude, dest.Ambiguity),
            Symbol = new AprsSymbol(table, code),
            CourseDegrees = course == 0 ? null : course,
            SpeedKnots = speed,
        };

        var report = new AprsMicEReport
        {
            Position = fields.Position,
            Symbol = fields.Symbol,
            Message = dest.Message,
            IsCurrent = dti is (byte)'`' or 0x1c,
            DestinationSsid = destination.NumericSsid ?? 0,
        };

        if (!TryReadStatus(info, ctx, fields, ref report))
        {
            return null;
        }

        return fields.ApplyTo(report);
    }

    private static bool TryReadStatus(ReadOnlySpan<byte> info, DecodeContext ctx, PositionedFields fields, ref AprsMicEReport report)
    {
        var text = new List<byte>(info[9..].ToArray());
        const int offset = 9;
        if (text.Count == 0)
        {
            return true;
        }

        if (text[0] == 0x1d && text.Count >= 6)
        {
            ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "obsolete Mic-E binary telemetry (APRS12c ch. 10)", offset);
            report = report with { LegacyTelemetry = text.GetRange(1, 5).Select(b => (int)b).ToArray() };
            text.RemoveRange(0, 6);
        }

        if (text.Contains(0xFF))
        {
            if (!ctx.Tolerate(ctx.Options.AllowKenwoodFfPadding, AprsDiagnosticCode.KenwoodFfPadding, "0xFF padding removed from Mic-E status text (UAP 5.10)", offset))
            {
                return false;
            }

            text.RemoveAll(b => b == 0xFF);
        }

        char? typeCode = null;
        if (text.Count > 0 && text[0] is (byte)'`' or (byte)'\'' or (byte)'>' or (byte)']' or (byte)' ')
        {
            typeCode = (char)text[0];
            text.RemoveAt(0);
        }
        else if (text.Count > 0)
        {
            ctx.Info(AprsDiagnosticCode.MicEMissingDeviceType, "Mic-E status text has no device type code (UAP 5.4)", offset);
        }

        string suffix = MatchSuffix(typeCode, text);
        if (suffix.Length > 0)
        {
            text.RemoveRange(text.Count - suffix.Length, suffix.Length);
        }

        report = report with { TypeCode = typeCode, DeviceSuffix = suffix };

        // Altitude: xxx} straight after the type code, metres relative to 10 km below sea level.
        int altitudeAt = IsAltitudeAt(text, 0) ? 0 : -1;
        if (altitudeAt < 0 && ctx.Options.AllowMicEAltitudeAnywhere)
        {
            for (int i = 1; i + 4 <= text.Count; i++)
            {
                if (IsAltitudeAt(text, i))
                {
                    altitudeAt = i;
                    ctx.Warn(AprsDiagnosticCode.MicEAltitudeNotFirst, "Mic-E altitude xxx} found after other status text; it should come first (APRS12c ch. 10)", offset + i);
                    break;
                }
            }
        }

        if (altitudeAt >= 0)
        {
            int metres = ((text[altitudeAt] - 33) * 91 * 91) + ((text[altitudeAt + 1] - 33) * 91) + (text[altitudeAt + 2] - 33) - 10000;
            fields.AltitudeFeet = metres * Units.FeetPerMetre;
            text.RemoveRange(altitudeAt, 4);
        }

        // A Maidenhead locator with the /G grid symbol, then a space before any text (APRS12c §10).
        int locatorLength = LocatorLengthAt(text);
        if (locatorLength > 0)
        {
            report = report with { MaidenheadLocator = Text.Latin1(text.GetRange(0, locatorLength).ToArray()).ToUpperInvariant() };
            text.RemoveRange(0, locatorLength + 2);
            if (text.Count > 0)
            {
                if (text[0] == (byte)' ')
                {
                    text.RemoveAt(0);
                }
                else if (!ctx.Tolerate(
                             ctx.Options.AllowMissingSpaceAfterLocator,
                             AprsDiagnosticCode.MissingSpaceAfterLocator,
                             "text after a Mic-E grid locator must start with a space (APRS12c ch. 10)",
                             offset))
                {
                    return false;
                }
            }
        }

        // "The Mic-E text field can contain any normal position comment field too, such as PHG" (APRS 1.2).
        if (text.Count >= 7)
        {
            byte[] head = text.GetRange(0, Math.Min(text.Count, 9)).ToArray();
            int pos = 0;
            var probe = new PositionedFields { Position = fields.Position, Symbol = fields.Symbol };
            if (!PositionCodec.IsCourseSpeed(head) && PositionCodec.TryReadDataExtension(head, ref pos, ctx, probe) && pos > 0)
            {
                fields.Phg = probe.Phg;
                fields.RadioRangeMiles = probe.RadioRangeMiles;
                fields.DfSignalStrength = probe.DfSignalStrength;
                fields.AreaObject = probe.AreaObject;
                text.RemoveRange(0, pos);
            }
        }

        return CommentCodec.TryExtract(text, offset, ctx, fields, frequencyAtStart: true) && CommentCodec.TryFinish(text, offset, ctx, fields);
    }

    /// <summary>6 or 4 when the text starts with a locator of that length followed by the grid
    /// symbol <c>/G</c>; 0 otherwise. A valid 6-character locator is never re-read as 4.</summary>
    internal static int LocatorLengthAt(IReadOnlyList<byte> text)
    {
        foreach (int length in (int[])[6, 4])
        {
            if (text.Count >= length + 2 && text[length] == (byte)'/' && text[length + 1] == (byte)'G'
                && Maidenhead.IsLocator(text.Take(length).ToArray()))
            {
                return length;
            }
        }

        return 0;
    }

    private static bool IsAltitudeAt(List<byte> text, int i) =>
        text.Count >= i + 4 && Base91.IsDigit(text[i]) && Base91.IsDigit(text[i + 1]) && Base91.IsDigit(text[i + 2]) && text[i + 3] == (byte)'}';

    private static string MatchSuffix(char? typeCode, List<byte> text)
    {
        IEnumerable<string> candidates = typeCode switch
        {
            '`' or '\'' => AprsDeviceIdentification.MicESuffixes,
            '>' or ']' => AprsDeviceIdentification.MicELegacySuffixes(typeCode.Value),
            _ => [],
        };

        foreach (string s in candidates.OrderByDescending(s => s.Length))
        {
            if (text.Count < s.Length)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < s.Length; i++)
            {
                if (text[text.Count - s.Length + i] != s[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return s;
            }
        }

        return "";
    }

    private readonly record struct Destination(double Latitude, int Ambiguity, AprsMicEMessage Message, bool LongitudeOffset, bool West);

    private static bool TryDecodeDestination(AprsAddress destination, DecodeContext ctx, out Destination result)
    {
        result = default;
        string call = destination.Base;
        if (call.Length != 6)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMicEDestination, $"Mic-E destination '{destination}' is not 6 characters (APRS12c ch. 10)");
            return false;
        }

        int[] digits = new int[6];
        int ambiguity = 0;
        bool sawDigitAfterSpace = false;
        int[] bits = new int[3];
        bool anyStandard = false;
        bool anyCustom = false;
        for (int i = 0; i < 6; i++)
        {
            char c = call[i];
            int digit;
            bool flag;
            bool custom = false;
            switch (c)
            {
                case >= '0' and <= '9':
                    digit = c - '0';
                    flag = false;
                    break;
                case 'L':
                    digit = -1;
                    flag = false;
                    break;
                case >= 'P' and <= 'Y':
                    digit = c - 'P';
                    flag = true;
                    break;
                case 'Z':
                    digit = -1;
                    flag = true;
                    break;
                case >= 'A' and <= 'J' when i < 3:
                    digit = c - 'A';
                    flag = true;
                    custom = true;
                    break;
                case 'K' when i < 3:
                    digit = -1;
                    flag = true;
                    custom = true;
                    break;
                default:
                    ctx.Error(AprsDiagnosticCode.InvalidMicEDestination, $"'{c}' cannot appear at position {i + 1} of a Mic-E destination (APRS12c ch. 10)");
                    return false;
            }

            if (digit < 0)
            {
                ambiguity++;
            }
            else if (ambiguity > 0)
            {
                sawDigitAfterSpace = true;
            }

            digits[i] = Math.Max(digit, 0);
            if (i < 3)
            {
                bits[i] = flag ? 1 : 0;
                anyStandard |= flag && !custom;
                anyCustom |= custom;
            }
            else if (i == 3)
            {
                result = result with { Latitude = flag ? 1 : -1 };
            }
            else if (i == 4)
            {
                result = result with { LongitudeOffset = flag };
            }
            else
            {
                result = result with { West = flag };
            }
        }

        if (sawDigitAfterSpace || ambiguity > 4)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMicEDestination, "Mic-E latitude ambiguity must blank only trailing digits");
            return false;
        }

        int index = (bits[0] << 2) | (bits[1] << 1) | bits[2];
        AprsMicEMessage message = anyStandard && anyCustom ? AprsMicEMessage.Unknown : anyCustom ? Custom[index] : Standard[index];
        int degrees = (digits[0] * 10) + digits[1];
        double minutes = Minutes([digits[2], digits[3], digits[4], digits[5]], ambiguity);
        double latitude = degrees + (minutes / 60);
        if (latitude > 90)
        {
            ctx.Error(AprsDiagnosticCode.InvalidMicEDestination, "Mic-E latitude is beyond 90 degrees");
            return false;
        }

        result = result with { Latitude = result.Latitude * latitude, Ambiguity = ambiguity, Message = message };
        return true;
    }

    /// <summary>MM.hh from four digits, blanking the last <paramref name="ambiguity"/> and centring the range.</summary>
    private static double Minutes(int[] d, int ambiguity)
    {
        double[] weights = [10, 1, 0.1, 0.01];
        double value = 0;
        for (int i = 0; i < 4 - ambiguity; i++)
        {
            value += d[i] * weights[i];
        }

        return value + ambiguity switch { 0 => 0, 1 => 0.05, 2 => 0.5, 3 => 5, _ => 30 };
    }

    // ------------------------------------------------------------------ encoding

    public static AprsAddress EncodeDestination(AprsMicEReport r)
    {
        r.Position.Validate(nameof(r.Position));
        if (r.DestinationSsid is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(r), "Mic-E destination SSID must be 0-15");
        }

        string lat = PositionCodec.FormatCoordinate(Math.Abs(r.Position.Latitude), 2, r.Dao).Replace(".", "", StringComparison.Ordinal);
        (int[] bits, bool custom) = MessageBits(r.Message);
        int lonDegrees = int.Parse(PositionCodec.FormatCoordinate(Math.Abs(r.Position.Longitude), 3, r.Dao)[..3], System.Globalization.CultureInfo.InvariantCulture);
        bool[] flags =
        [
            bits[0] == 1, bits[1] == 1, bits[2] == 1,
            !double.IsNegative(r.Position.Latitude),
            lonDegrees is < 10 or >= 100,
            double.IsNegative(r.Position.Longitude),
        ];

        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            bool blank = i >= 6 - r.Position.Ambiguity;
            int digit = lat[i] - '0';
            if (!flags[i])
            {
                chars[i] = blank ? 'L' : (char)('0' + digit);
            }
            else if (custom && i < 3)
            {
                chars[i] = blank ? 'K' : (char)('A' + digit);
            }
            else
            {
                chars[i] = blank ? 'Z' : (char)('P' + digit);
            }
        }

        string call = new(chars);
        return AprsAddress.CreateUnchecked(r.DestinationSsid == 0 ? call : $"{call}-{r.DestinationSsid}");
    }

    private static (int[] Bits, bool Custom) MessageBits(AprsMicEMessage message)
    {
        int index = Array.IndexOf(Standard, message);
        bool custom = false;
        if (index < 0)
        {
            index = Array.IndexOf(Custom, message);
            custom = true;
        }

        if (index < 0)
        {
            throw new ArgumentException("an unknown Mic-E message type cannot be encoded", nameof(message));
        }

        return ([(index >> 2) & 1, (index >> 1) & 1, index & 1], custom && message != AprsMicEMessage.Emergency);
    }

    public static void WriteInformation(InfoWriter w, AprsMicEReport r)
    {
        r.Position.Validate(nameof(r.Position));
        r.Symbol.Validate(nameof(r.Symbol));
        if (r.IsCompressed || r.Weather is not null || r.Storm is not null || r.DfBearing is not null || r.AreaObject is not null || r.SignpostText is not null)
        {
            throw new ArgumentException("a Mic-E report cannot carry compression, weather, storm, DF bearing, area object or signpost data");
        }

        string lon = PositionCodec.FormatCoordinate(Math.Abs(r.Position.Longitude), 3, r.Dao);
        int deg = int.Parse(lon[..3], System.Globalization.CultureInfo.InvariantCulture);
        int min = int.Parse(lon.AsSpan(3, 2), System.Globalization.CultureInfo.InvariantCulture);
        int hun = int.Parse(lon.AsSpan(6, 2), System.Globalization.CultureInfo.InvariantCulture);
        int d = deg switch
        {
            < 10 => deg + 90,
            < 100 => deg,
            < 110 => deg - 20,
            _ => deg - 100,
        };

        int speed = r.SpeedKnots is { } s ? (int)Math.Round(s) : 0;
        int course = r.CourseDegrees ?? 0;
        if (speed is < 0 or > 799 || course is < 0 or > 360)
        {
            throw new ArgumentOutOfRangeException(nameof(r), "Mic-E speed must be 0-799 knots and course 0-360");
        }

        // The printable encoding schemes: speeds under 200 knots use SP + 80, and DC always adds 4 to
        // the course hundreds, so only the SE byte can be a control character (APRS12c §10).
        int sp = speed < 200 ? (speed / 10) + 80 : speed / 10;
        int dc = ((speed % 10) * 10) + (course / 100) + 4;
        int se = course % 100;

        w.Char(r.IsCurrent ? '`' : '\'')
            .Byte((byte)(d + 28))
            .Byte((byte)((min < 10 ? min + 60 : min) + 28))
            .Byte((byte)(hun + 28))
            .Byte((byte)(sp + 28))
            .Byte((byte)(dc + 28))
            .Byte((byte)(se + 28))
            .Char(r.Symbol.Code)
            .Char(r.Symbol.Table);

        if (r.LegacyTelemetry is { } legacy)
        {
            throw new ArgumentException("obsolete Mic-E binary telemetry is decode-only", nameof(r));
        }

        if (r.TypeCode is { } type)
        {
            if (type is not ('`' or '\'' or '>' or ']' or ' '))
            {
                throw new ArgumentException("Mic-E type code must be ` ' > ] or space", nameof(r));
            }

            w.Char(type);
        }
        else if (r.DeviceSuffix.Length > 0)
        {
            throw new ArgumentException("a Mic-E device suffix needs a type code", nameof(r));
        }

        // Mic-E carries altitude in whole metres (xxx}); an altitude that is not a whole number of
        // metres, e.g. one that arrived as /A= feet, is written as /A= so it survives unchanged.
        bool altitudeAsFeet = false;
        if (r.AltitudeFeet is { } feet)
        {
            long metres = (long)Math.Round(feet / Units.FeetPerMetre);
            long value = metres + 10000;
            altitudeAsFeet = Math.Abs((metres * Units.FeetPerMetre) - feet) > 1e-6;
            if (!altitudeAsFeet)
            {
                if (value is < 0 or > (91 * 91 * 91) - 1)
                {
                    throw new ArgumentOutOfRangeException(nameof(r), "Mic-E altitude is out of range");
                }

                // Without a type code, an altitude whose first character is itself a type code
                // character would be read back as one.
                char first = (char)((value / (91 * 91)) + 33);
                if (r.TypeCode is null && first is '`' or '\'' or '>' or ']')
                {
                    throw new ArgumentException("without a TypeCode this Mic-E altitude would decode as a device type code; set TypeCode", nameof(r));
                }

                w.Base91(value, 3).Char('}');
            }
        }

        if (r.MaidenheadLocator is { } locator)
        {
            if (locator.Length is not (4 or 6) || !Maidenhead.IsLocator(System.Text.Encoding.ASCII.GetBytes(locator)))
            {
                throw new ArgumentException($"'{locator}' is not a 4 or 6 character Maidenhead locator", nameof(r));
            }

            w.Ascii(locator.ToUpperInvariant()).Ascii("/G");
            bool more = altitudeAsFeet || r.Phg is not null || r.RadioRangeMiles is not null || r.DfSignalStrength is not null
                || r.Frequency is not null || r.Comment.Length > 0 || r.Telemetry is not null || r.Dao is not null;
            if (more)
            {
                w.Char(' ');
            }
        }

        if (r.Phg is { } phg)
        {
            PositionCodec.WritePhg(w, phg);
        }
        else if (r.RadioRangeMiles is { } range)
        {
            w.Ascii("RNG").Digits((int)range, 4);
        }
        else if (r.DfSignalStrength is { } dfs)
        {
            w.Ascii("DFS").Digits(dfs.StrengthCode, 1).Char((char)('0' + dfs.HeightCode)).Digits(dfs.GainCode, 1).Digits(dfs.DirectivityCode, 1);
        }

        CommentCodec.RequireCleanMicEComment(r);
        if (altitudeAsFeet)
        {
            CommentCodec.WriteAltitude(w, r.AltitudeFeet!.Value);
        }

        if (r.Frequency is { } freq)
        {
            FrequencyCodec.WriteWithComment(w, freq, r.Comment);
        }

        bool extensionJustWritten = r.Frequency is null && !altitudeAsFeet && (r.Phg is not null || r.RadioRangeMiles is not null || r.DfSignalStrength is not null);
        bool commentFirst = r.Frequency is null && !altitudeAsFeet && r.Phg is null && r.RadioRangeMiles is null && r.DfSignalStrength is null;
        byte[] commentBytes = System.Text.Encoding.UTF8.GetBytes(r.Comment);
        bool readsAsLocator = commentFirst && r.MaidenheadLocator is null && LocatorLengthAt(commentBytes) > 0;
        if (r.Frequency is null && (CommentCodec.NeedsDelimiter(r.Comment, extensionJustWritten ? r.Phg : null) || readsAsLocator))
        {
            w.Char('/');
        }

        if (r.Frequency is null)
        {
            w.Utf8(r.Comment);
        }

        if (r.Telemetry is { } t)
        {
            t.Validate(nameof(r.Telemetry));
            w.Char('|').Base91(t.Sequence, 2);
            foreach (int v in t.Analog)
            {
                w.Base91(v, 2);
            }

            if (t.Digital is { } bits)
            {
                w.Base91(bits, 2);
            }

            w.Char('|');
        }

        if (r.Dao is { } dao)
        {
            DaoCodec.Write(w, r.Position, dao);
        }

        w.Ascii(r.DeviceSuffix);
    }
}
