using System.Globalization;

namespace Packet.Aprs.Internal;

/// <summary>Weather data fields (APRS12c §12 Weather Data).</summary>
internal static class WeatherCodec
{
    /// <summary>
    /// Decides the wind where the DIR/SPD extension of an uncompressed complete weather report
    /// belongs, before any field is read (vectors README, The order of checks): the extension
    /// itself; wind sent as <c>c</c>/<c>s</c> fields instead (tolerated), in which case
    /// <paramref name="windAsFields"/> tells the field reader to take them; or no wind at all.
    /// </summary>
    public static bool TryReadWind(ReadOnlySpan<byte> info, ref int pos, DecodeContext ctx, ref AprsWeather weather, out bool windAsFields)
    {
        windAsFields = false;
        if (info.Length >= pos + 7 && PositionCodec.IsCourseSpeed(info.Slice(pos, 7)))
        {
            return TryReadWindExtension(info, ref pos, ctx, ref weather);
        }

        if (WindComesAsFields(info, pos, ctx))
        {
            windAsFields = ctx.Tolerate(
                ctx.Options.AllowWindFieldsInPositionWeather,
                AprsDiagnosticCode.WindFieldsInsteadOfExtension,
                "wind sent as c/s fields instead of the DIR/SPD extension (APRS12c ch. 12)",
                pos);
            return windAsFields;
        }

        return ctx.Tolerate(
            ctx.Options.AllowIncompleteWeather,
            AprsDiagnosticCode.IncompleteWeather,
            "weather report has no wind direction/speed extension (APRS12c ch. 12)",
            pos);
    }

    /// <summary>
    /// Whether the field run at <paramref name="pos"/> carries a wind direction: a <c>c</c> with a
    /// value after it, wherever it comes before the run ends (vectors interpretations.md, "Which
    /// weather field a letter is"). Judged on the fields alone, whatever the options, so that
    /// lenient and strict decoding agree on what the report is.
    /// </summary>
    public static bool WindComesAsFields(ReadOnlySpan<byte> info, int pos, DecodeContext ctx)
    {
        var probe = new DecodeContext(AprsParseOptions.Lenient) { Depth = ctx.Depth };
        var weather = new AprsWeather();
        return TryReadRun(info, ref pos, probe, ref weather, windAsFields: true, positionless: false, out bool direction, out _, out _) && direction;
    }

    /// <summary>Reads a 7-byte <c>DDD/SSS</c> wind extension at <paramref name="pos"/>.</summary>
    public static bool TryReadWindExtension(ReadOnlySpan<byte> info, ref int pos, DecodeContext ctx, ref AprsWeather weather)
    {
        ReadOnlySpan<byte> ext = info.Slice(pos, 7);
        int? dir = Text.AllDigits(ext[..3]) ? Text.ParseDigits(ext[..3]) : null;
        int? speed = Text.AllDigits(ext.Slice(4, 3)) ? Text.ParseDigits(ext.Slice(4, 3)) : null;
        if (dir > 360)
        {
            if (!ctx.Tolerate(ctx.Options.AllowOutOfRangeValues, AprsDiagnosticCode.OutOfRangeValue, "wind direction is over 360 degrees; ignored", pos))
            {
                return false;
            }

            dir = null;
        }

        weather = weather with { WindDirectionDegrees = dir, WindSpeedMph = speed };
        pos += 7;
        return true;
    }

    /// <summary>
    /// Reads letter-plus-value weather fields and checks the report has the ones it must. With
    /// <paramref name="windAsFields"/> (always for a positionless report) the wind comes as fields:
    /// <c>c</c> is the wind direction until it is read, wherever it comes, and <c>s</c> the wind
    /// speed until it is read, then snowfall. A positionless report's wind is fields from the
    /// start; a position, object or item report's only once a <c>c</c> has been read, so an
    /// <c>s</c> before any <c>c</c> there is snowfall. Otherwise the wind is already known (or
    /// missing), so <c>c</c> ends the fields and <c>s</c> is snowfall. With <paramref name="windMustBeComplete"/>
    /// a wind sent as fields needs both <c>c</c> and <c>s</c> (APRS12c ch. 12: the report "must
    /// include at least ... wind direction, wind speed, gust and temperature").
    /// </summary>
    public static bool TryReadFields(ReadOnlySpan<byte> info, ref int pos, DecodeContext ctx, ref AprsWeather weather, bool positionless, bool windAsFields = false, bool windMustBeComplete = false)
    {
        int start = pos;
        if (!TryReadRun(info, ref pos, ctx, ref weather, positionless || windAsFields, positionless, out bool direction, out bool speed, out HashSet<char> seen))
        {
            return false;
        }

        if (positionless)
        {
            return seen.Contains('c') && seen.Contains('s') && seen.Contains('g') && seen.Contains('t')
                || ctx.Tolerate(ctx.Options.AllowIncompleteWeather, AprsDiagnosticCode.IncompleteWeather, "positionless weather report must start with c, s, g and t fields (APRS12c ch. 12)", start);
        }

        if (windAsFields && windMustBeComplete && !(direction && speed) && !ctx.Tolerate(
                ctx.Options.AllowIncompleteWeather,
                AprsDiagnosticCode.IncompleteWeather,
                "wind sent as fields needs both the direction (c) and the speed (s) (APRS12c ch. 12)",
                start))
        {
            return false;
        }

        return seen.Contains('g') && seen.Contains('t')
            || ctx.Tolerate(ctx.Options.AllowIncompleteWeather, AprsDiagnosticCode.IncompleteWeather, "weather report must include gust (g) and temperature (t) fields (APRS12c ch. 12)", start);
    }

    /// <summary>
    /// The fields are one contiguous run, which ends at the first thing that is not a field, at a
    /// defined field already read (<c>L</c> and <c>l</c> are one field, luminosity), and at <c>c</c>
    /// once the wind is known. Extra fields are kept as a list, so a repeated extra letter does not
    /// end it (vectors interpretations.md, "Which weather field a letter is").
    /// </summary>
    private static bool TryReadRun(ReadOnlySpan<byte> info, ref int pos, DecodeContext ctx, ref AprsWeather weather, bool windAsFields, bool positionless, out bool direction, out bool speed, out HashSet<char> seen)
    {
        seen = [];
        direction = false;
        speed = false;
        var additional = new List<AprsWeatherField>();

        decimal? fieldValue = null;
        int fieldLength = 0;
        while (pos < info.Length)
        {
            char letter = (char)info[pos];
            bool snow = letter == 's' && (!windAsFields || speed || !(positionless || direction));
            char key = letter switch
            {
                'l' => 'L',
                's' when snow => 'S',
                _ => letter,
            };
            int width = letter switch
            {
                'c' when windAsFields => 3,
                's' or 'g' or 't' or 'r' or 'p' or 'P' or 'L' or 'l' or '#' => 3,
                'h' => 2,
                'b' => 5,
                _ => 0,
            };

            FieldResult result = width > 0 && !seen.Contains(key)
                ? TryFieldValue(info, pos, width, letter, snow, ctx, out fieldValue, out fieldLength)
                : FieldResult.NotAField;
            if (result == FieldResult.Abort)
            {
                return false;
            }

            if (result == FieldResult.Field)
            {
                seen.Add(key);
                if (!Assign(ref weather, letter, snow, fieldValue, pos, ctx))
                {
                    return false;
                }

                direction |= letter == 'c';
                speed |= letter == 's' && !snow;
                pos += 1 + fieldLength;
                continue;
            }

            if (Text.IsUpper((byte)letter) || Text.IsLower((byte)letter) || letter == '#')
            {
                int end = pos + 1;
                while (end < info.Length && (Text.IsDigit(info[end]) || info[end] is (byte)'.' or (byte)'-'))
                {
                    end++;
                }

                bool looksLikeField = end - pos >= 3 && Text.IsDigit(info[end - 1]) && !IsKnown(letter);
                if (looksLikeField)
                {
                    additional.Add(new AprsWeatherField(letter, Text.Latin1(info[(pos + 1)..end])));
                    pos = end;
                    continue;
                }
            }

            break;
        }

        if (additional.Count > 0)
        {
            weather = weather with { AdditionalFields = additional };
        }

        return true;

        static bool IsKnown(char c) => c is 'c' or 's' or 'g' or 't' or 'r' or 'p' or 'P' or 'h' or 'b' or 'L' or 'l' or '#';
    }

    private enum FieldResult
    {
        NotAField,
        Field,
        Abort,
    }

    /// <summary>
    /// A field value: exactly <paramref name="width"/> characters (APRS12c §12), or, tolerated, a
    /// run of 1 to width+1 digits or dots that ends at a non-digit (<c>t45</c>, <c>h070</c>, <c>b...</c>).
    /// The spec width is tried first, and a spec-width value that runs on into more digits falls
    /// back to it only if no shorter/longer reading fits.
    /// </summary>
    private static FieldResult TryFieldValue(ReadOnlySpan<byte> info, int pos, int width, char letter, bool snow, DecodeContext ctx, out decimal? value, out int consumed)
    {
        value = null;
        consumed = width;
        int avail = info.Length - pos - 1;
        bool exactOk = avail >= width && TryValue(info.Slice(pos + 1, width), letter, snow, out value);
        bool followedByDigit = avail > width && Text.IsDigit(info[pos + 1 + width]);
        if (exactOk && !followedByDigit)
        {
            return FieldResult.Field;
        }

        // Variable width: a run of digits (with a leading '-' for temperature) or of dots.
        int run = 0;
        bool dots = avail > 0 && info[pos + 1] == (byte)'.';
        bool minus = letter == 't' && avail > 0 && info[pos + 1] == (byte)'-';
        int i = pos + 1 + (minus ? 1 : 0);
        while (i < info.Length && (dots ? info[i] == (byte)'.' : Text.IsDigit(info[i])))
        {
            run++;
            i++;
        }

        int length = run + (minus ? 1 : 0);
        if (run >= 1 && length != width && length <= width + 1 && !(snow && !dots))
        {
            if (!ctx.Tolerate(
                    ctx.Options.AllowNonStandardWeatherFieldWidths,
                    AprsDiagnosticCode.NonStandardWeatherFieldWidth,
                    $"weather field '{letter}' has {length} characters instead of {width} (APRS12c ch. 12, UAP 5.31)",
                    pos))
            {
                return FieldResult.Abort;
            }

            consumed = length;
            value = dots ? null : (minus ? -1 : 1) * Text.ParseDigits(info.Slice(pos + 1 + (minus ? 1 : 0), run));
            return FieldResult.Field;
        }

        return exactOk ? FieldResult.Field : FieldResult.NotAField;
    }

    private static bool TryValue(ReadOnlySpan<byte> v, char letter, bool snow, out decimal? value)
    {
        value = null;
        if (v.SequenceEqual("..........."u8[..v.Length]) || v.SequenceEqual("           "u8[..v.Length]))
        {
            return true;
        }

        if (Text.AllDigits(v))
        {
            value = Text.ParseDigits(v);
            return true;
        }

        if (letter == 't' && v[0] == (byte)'-' && Text.AllDigits(v[1..]))
        {
            value = -Text.ParseDigits(v[1..]);
            return true;
        }

        if (snow && v.Count((byte)'.') == 1 && decimal.TryParse(Text.Latin1(v), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal d))
        {
            value = d;
            return true;
        }

        return false;
    }

    private static bool Assign(ref AprsWeather w, char letter, bool snow, decimal? v, int pos, DecodeContext ctx)
    {
        int? i = v is { } d ? (int)d : null;
        switch (letter)
        {
            case 'c':
                if (i > 360)
                {
                    if (!ctx.Tolerate(ctx.Options.AllowOutOfRangeValues, AprsDiagnosticCode.OutOfRangeValue, "wind direction is over 360 degrees; ignored", pos))
                    {
                        return false;
                    }

                    i = null;
                }

                w = w with { WindDirectionDegrees = i };
                break;
            case 's' when !snow:
                w = w with { WindSpeedMph = i };
                break;
            case 's':
                w = w with { SnowfallLast24HoursInches = v };
                break;
            case 'g':
                w = w with { WindGustMph = i };
                break;
            case 't':
                w = w with { TemperatureFahrenheit = i };
                break;
            case 'r':
                w = w with { RainLastHourInches = i / 100.0 };
                break;
            case 'p':
                w = w with { RainLast24HoursInches = i / 100.0 };
                break;
            case 'P':
                w = w with { RainSinceMidnightInches = i / 100.0 };
                break;
            case 'h':
                if (i > 100)
                {
                    if (!ctx.Tolerate(ctx.Options.AllowOutOfRangeValues, AprsDiagnosticCode.OutOfRangeValue, "humidity is over 100%; ignored", pos))
                    {
                        return false;
                    }

                    i = null;
                }

                w = w with { HumidityPercent = i == 0 ? 100 : i };
                break;
            case 'b':
                w = w with { PressureMillibars = i / 10.0 };
                break;
            case 'L':
                w = w with { LuminosityWattsPerSquareMetre = i };
                break;
            case 'l':
                w = w with { LuminosityWattsPerSquareMetre = i + 1000 };
                break;
            case '#':
                w = w with { RainRawCounter = i };
                break;
        }

        return true;
    }

    public static void WriteWind(InfoWriter w, AprsWeather weather)
    {
        weather.Validate(nameof(weather));
        PositionCodec.WriteCourseSpeed(w, weather.WindDirectionDegrees, weather.WindSpeedMph is { } s ? Math.Round(s) : null);
    }

    public static void WriteFields(InfoWriter w, AprsWeather weather, bool positionless)
    {
        weather.Validate(nameof(weather));
        int start = w.Length;
        if (positionless)
        {
            Field(w, 'c', weather.WindDirectionDegrees, 3, always: true);
            Field(w, 's', weather.WindSpeedMph is { } s ? (int)Math.Round(s) : null, 3, always: true);
        }

        Field(w, 'g', weather.WindGustMph, 3, always: true);
        w.Char('t');
        if (weather.TemperatureFahrenheit is { } t)
        {
            _ = t < 0 ? w.Char('-').Digits(-t, 2) : w.Digits(t, 3);
        }
        else
        {
            w.Ascii("...");
        }

        Field(w, 'r', Hundredths(weather.RainLastHourInches), 3);
        Field(w, 'p', Hundredths(weather.RainLast24HoursInches), 3);
        Field(w, 'P', Hundredths(weather.RainSinceMidnightInches), 3);
        Field(w, 'h', weather.HumidityPercent is { } h ? h % 100 : null, 2);
        Field(w, 'b', weather.PressureMillibars is { } b ? (int)Math.Round(b * 10) : null, 5);
        if (weather.LuminosityWattsPerSquareMetre is { } lum)
        {
            _ = lum < 1000 ? w.Char('L').Digits(lum, 3) : w.Char('l').Digits(lum - 1000, 3);
        }

        if (weather.SnowfallLast24HoursInches is { } snow)
        {
            string text = snow == decimal.Truncate(snow)
                ? ((int)snow).ToString("000", CultureInfo.InvariantCulture)
                : snow.ToString(CultureInfo.InvariantCulture).TrimStart('0');
            if (text.Length > 3)
            {
                text = snow.ToString(CultureInfo.InvariantCulture)[..3];
            }

            w.Char('s').Ascii(text.PadLeft(3, '0'));
        }

        Field(w, '#', weather.RainRawCounter, 3);
        foreach (AprsWeatherField extra in weather.AdditionalFields)
        {
            w.Char(extra.Letter).Ascii(extra.Value);
        }

        if (weather.SoftwareType is { } sw)
        {
            int fieldsEnd = w.Length;
            w.Char(sw).Ascii(weather.UnitType ?? "");

            // Written after the fields, the software type and unit must read back as themselves:
            // the field run has to end where they start, and what follows be a letter then a
            // 2-4 character unit (h89b1 would read back as humidity and pressure).
            byte[] written = w.Written[start..].ToArray();
            int pos = 0;
            var probe = new DecodeContext(AprsParseOptions.Lenient);
            var back = new AprsWeather();
            if (!TryReadRun(written, ref pos, probe, ref back, positionless, positionless, out _, out _, out _)
                || pos != fieldsEnd - start || !CommentCodec.IsSoftwareAndUnit(written.AsSpan(pos)))
            {
                throw new ArgumentException($"the software type and unit '{sw}{weather.UnitType}' would read back as weather fields or comment text (APRS12c ch. 12)", nameof(weather));
            }
        }
    }

    private static int? Hundredths(double? inches) => inches is { } i ? (int)Math.Round(i * 100) : null;

    private static void Field(InfoWriter w, char letter, int? value, int width, bool always = false)
    {
        if (value is { } v)
        {
            w.Char(letter).Digits(v, width);
        }
        else if (always)
        {
            w.Char(letter).Ascii(new string('.', width));
        }
    }
}
