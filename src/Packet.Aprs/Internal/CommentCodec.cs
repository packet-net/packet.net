using System.Globalization;

namespace Packet.Aprs.Internal;

/// <summary>
/// Structured elements carried in the comment of a positioned report: altitude, <c>!DAO!</c>,
/// base-91 telemetry, voice frequency, signpost / corridor braces, and (when enabled) a data
/// extension found later in the comment.
/// </summary>
internal static class CommentCodec
{
    /// <summary>Decodes a position, object or item comment starting at <paramref name="pos"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, PositionedFields f)
    {
        var comment = new List<byte>(info[pos..].ToArray());
        return TryExtract(comment, pos, ctx, f, frequencyAtStart: true) && TryFinish(comment, pos, ctx, f);
    }

    /// <summary>Whatever follows the weather fields: ideally only the software and unit type.</summary>
    public static bool TryReadWeatherTail(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, PositionedFields f)
    {
        ReadOnlySpan<byte> tail = info[pos..];
        if (tail.Length == 0)
        {
            return true;
        }

        if (IsSoftwareAndUnit(tail))
        {
            f.Weather = f.Weather! with { SoftwareType = (char)tail[0], UnitType = Text.Latin1(tail[1..]) };
            return true;
        }

        var comment = new List<byte>(tail.ToArray());
        if (!TryExtractTrailer(comment, pos, ctx, f))
        {
            return false;
        }

        if (comment.Count == 0)
        {
            return true;
        }

        if (!ctx.Tolerate(
                ctx.Options.AllowWeatherComment,
                AprsDiagnosticCode.WeatherComment,
                "a weather report has no comment field; text after the weather data kept as a comment (APRS12c ch. 12, UAP 2.7.1)",
                pos))
        {
            return false;
        }

        return TryFinish(comment, pos, ctx, f);
    }

    /// <summary>
    /// One software-type letter then a 2-4 character unit type, e.g. <c>wRSW</c>, <c>xDvs</c>, <c>tU2k</c>.
    /// A letter followed only by digits is a malformed weather field (e.g. a 4-digit <c>b0990</c>), not this.
    /// </summary>
    internal static bool IsSoftwareAndUnit(ReadOnlySpan<byte> tail)
    {
        if (tail.Length is < 3 or > 5 || !(Text.IsUpper(tail[0]) || Text.IsLower(tail[0])) || Text.AllDigits(tail[1..]))
        {
            return false;
        }

        foreach (byte b in tail)
        {
            if (!Text.IsAlphanumeric(b) && b is not ((byte)'-' or (byte)'_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Lifts structured elements out of the comment bytes, in the order that avoids one being
    /// mistaken for another: DAO and telemetry from the end, then altitude, braces, a late data
    /// extension, and finally a voice frequency at the start.
    /// </summary>
    internal static bool TryExtract(List<byte> c, int offset, DecodeContext ctx, PositionedFields f, bool frequencyAtStart)
    {
        if (!TryExtractTrailer(c, offset, ctx, f))
        {
            return false;
        }

        ExtractAltitude(c, f);
        ExtractBraces(c, f);
        if (f.Phg is null && f.RadioRangeMiles is null && f.DfSignalStrength is null)
        {
            ExtractLateExtension(c, offset, ctx, f);
        }

        if (frequencyAtStart)
        {
            ExtractFrequency(c, f);
        }

        return true;
    }

    /// <summary>DAO then base-91 telemetry, both expected at the end of the comment (APRS12c §13).</summary>
    internal static bool TryExtractTrailer(List<byte> c, int offset, DecodeContext ctx, PositionedFields f)
    {
        // Telemetry is located first so that a DAO-shaped run of base-91 characters inside it
        // (e.g. "|!m2n!Q[P|") is not mistaken for a !DAO!; the spec puts the DAO after it.
        (int tStart, int tEnd) = FindTelemetry(c);
        int daoAt = ExtractDao(c, offset, ctx, f, tStart, tEnd, out bool ok);
        if (!ok)
        {
            return false;
        }

        if (daoAt >= 0 && daoAt < tStart)
        {
            tStart -= 5;
            tEnd -= 5;
        }

        if (tStart >= 0)
        {
            ExtractTelemetry(c, f, tStart, tEnd);
        }

        return true;
    }

    internal static bool TryFinish(List<byte> c, int offset, DecodeContext ctx, PositionedFields f)
    {
        // "Any free text field that begins with [a space or a slash] ... they can be ignored and the
        // beginning of the text field begins after them" (APRS12c §18): once the structured elements
        // are lifted out, one leading delimiter is not part of the comment.
        if (c.Count > 0 && c[0] is (byte)' ' or (byte)'/')
        {
            c.RemoveAt(0);
        }

        if (!Text.TryDecode(c.ToArray(), ctx, offset, out string text))
        {
            return false;
        }

        f.Comment = text;
        return true;
    }

    private static int ExtractDao(List<byte> c, int offset, DecodeContext ctx, PositionedFields f, int skipFrom, int skipTo, out bool ok)
    {
        ok = true;
        Span<byte> window = stackalloc byte[5];
        for (int i = c.Count - 5; i >= 0; i--)
        {
            if (c[i] != (byte)'!' || c[i + 4] != (byte)'!' || (skipFrom >= 0 && i + 4 >= skipFrom && i <= skipTo))
            {
                continue;
            }

            for (int k = 0; k < 5; k++)
            {
                window[k] = c[i + k];
            }

            if (!DaoCodec.TryParse(window, out AprsDao dao, out double lat, out double lon))
            {
                continue;
            }

            f.Dao = dao;
            if (dao.Precision != AprsDaoPrecision.None && !f.IsCompressed)
            {
                if (f.Position.Ambiguity == 0)
                {
                    f.Position = DaoCodec.Apply(f.Position, lat, lon);
                }
                else if (!ctx.Tolerate(
                             ctx.Options.AllowDaoWithAmbiguity,
                             AprsDiagnosticCode.DaoWithAmbiguity,
                             "!DAO! adds precision to an ambiguous position; the extra precision is ignored",
                             offset + i))
                {
                    ok = false;
                    return -1;
                }
            }

            c.RemoveRange(i, 5);
            return i;
        }

        return -1;
    }

    /// <summary>Where the last <c>|ss11..|</c> block is: 2 to 7 base-91 pairs between bars (APRS12c §13), or (-1, -1).</summary>
    private static (int Start, int End) FindTelemetry(List<byte> c)
    {
        int end = c.LastIndexOf((byte)'|');
        if (end < 5)
        {
            return (-1, -1);
        }

        int start = c.LastIndexOf((byte)'|', end - 1);
        int length = end - start - 1;
        if (start < 0 || length is < 4 or > 14 || length % 2 != 0)
        {
            return (-1, -1);
        }

        for (int i = start + 1; i < end; i++)
        {
            if (!Base91.IsDigit(c[i]))
            {
                return (-1, -1);
            }
        }

        return (start, end);
    }

    private static void ExtractTelemetry(List<byte> c, PositionedFields f, int start, int end)
    {
        var values = new List<int>();
        for (int i = start + 1; i < end; i += 2)
        {
            values.Add(((c[i] - 33) * 91) + (c[i + 1] - 33));
        }

        f.Telemetry = new AprsCommentTelemetry
        {
            Sequence = values[0],
            Analog = values.Skip(1).Take(5).ToArray(),
            Digital = values.Count == 7 ? (byte)(values[6] & 0xFF) : null,
        };
        c.RemoveRange(start, end - start + 1);
    }

    /// <summary><c>/A=nnnnnn</c> anywhere in the comment (APRS12c §6), or the de facto <c>/A=-nnnnn</c>.</summary>
    private static void ExtractAltitude(List<byte> c, PositionedFields f)
    {
        for (int i = 0; i + 9 <= c.Count; i++)
        {
            if (c[i] != (byte)'/' || c[i + 1] != (byte)'A' || c[i + 2] != (byte)'=')
            {
                continue;
            }

            bool negative = c[i + 3] == (byte)'-';
            int digitsFrom = negative ? i + 4 : i + 3;
            int value = 0;
            bool ok = true;
            for (int k = digitsFrom; k < i + 9; k++)
            {
                if (!Text.IsDigit(c[k]))
                {
                    ok = false;
                    break;
                }

                value = (value * 10) + (c[k] - '0');
            }

            if (!ok)
            {
                continue;
            }

            f.AltitudeFeet = negative ? -value : value;
            c.RemoveRange(i, 9);
            return;
        }
    }

    /// <summary>Signpost text on <c>\m</c>, or a line corridor width on <c>\l</c> lines (APRS12c §11).</summary>
    private static void ExtractBraces(List<byte> c, PositionedFields f)
    {
        bool signpost = f.Symbol is { Table: '\\', Code: 'm' };
        bool corridor = f.AreaObject is { Shape: AprsAreaShape.LineDownRight or AprsAreaShape.LineDownLeft };
        if (!signpost && !corridor)
        {
            return;
        }

        int open = c.IndexOf((byte)'{');
        int close = open < 0 ? -1 : c.IndexOf((byte)'}', open);
        int length = close - open - 1;
        if (open < 0 || close < 0 || length is < 1 or > 3)
        {
            return;
        }

        byte[] inner = c.GetRange(open + 1, length).ToArray();
        if (signpost && inner.All(Text.IsPrintableAscii))
        {
            f.SignpostText = Text.Latin1(inner);
        }
        else if (corridor && Text.AllDigits(inner))
        {
            f.AreaObject = f.AreaObject!.Value with { CorridorWidthMiles = Text.ParseDigits(inner) };
        }
        else
        {
            return;
        }

        c.RemoveRange(open, close - open + 1);
    }

    /// <summary>
    /// PHG / RNG / DFS that appear after other comment text instead of straight after the symbol.
    /// Strictly that is free text (UAP §5.15); recognising it is a named tolerance.
    /// </summary>
    private static void ExtractLateExtension(List<byte> c, int offset, DecodeContext ctx, PositionedFields f)
    {
        if (!ctx.Options.RecognizeDataExtensionInComment)
        {
            return;
        }

        byte[] bytes = [.. c];
        ReadOnlySpan<byte> s = bytes;
        foreach (string tagText in (string[])["PHG", "RNG", "DFS"])
        {
            ReadOnlySpan<byte> tag = System.Text.Encoding.ASCII.GetBytes(tagText);
            int at = s.IndexOf(tag);
            if (at < 0 || at + 7 > s.Length)
            {
                continue;
            }

            ReadOnlySpan<byte> ext = s.Slice(at, 7);
            if (!Text.IsDigit(ext[3]) || !Text.IsDigit(ext[5]) || !Text.IsDigit(ext[6]) || ext[4] is < (byte)'0' or > (byte)'~')
            {
                continue;
            }

            if (tag[0] == (byte)'R')
            {
                if (!Text.AllDigits(ext[3..]))
                {
                    continue;
                }

                f.RadioRangeMiles = Text.ParseDigits(ext[3..]);
            }
            else if (tag[0] == (byte)'D')
            {
                f.DfSignalStrength = new AprsDfSignalStrength(ext[3] - '0', ext[4] - '0', ext[5] - '0', ext[6] - '0');
            }
            else
            {
                f.Phg = new AprsPhg(ext[3] - '0', ext[4] - '0', ext[5] - '0', ext[6] - '0');
            }

            ctx.Warn(
                AprsDiagnosticCode.DataExtensionInComment,
                $"{Text.Latin1(tag)} found later in the comment; the spec puts data extensions straight after the symbol (UAP 5.15)",
                offset + at);
            c.RemoveRange(at, 7);
            return;
        }
    }

    /// <summary>
    /// A voice frequency at the start of the comment (APRS12c §18): <c>FFF.FFFMHz</c> or
    /// <c>FFF.FF MHz</c>, then optional space-separated tone, offset and range fields.
    /// A single leading space or slash delimiter is allowed before it.
    /// </summary>
    private static void ExtractFrequency(List<byte> c, PositionedFields f)
    {
        byte[] bytes = [.. c];
        ReadOnlySpan<byte> s = bytes;
        int start = s.Length > 0 && s[0] is (byte)' ' or (byte)'/' ? 1 : 0;
        if (!FrequencyCodec.TryRead(s[start..], out AprsVoiceFrequency? frequency, out int consumed))
        {
            return;
        }

        f.Frequency = frequency;
        int remove = start + consumed;
        if (remove < c.Count && c[remove] == (byte)' ')
        {
            remove++;
        }

        c.RemoveRange(0, remove);
    }

    // ---------------------------------------------------------------- encoding

    /// <summary>Writes altitude, frequency, free text, braces, telemetry and DAO in canonical order.</summary>
    public static void Write(InfoWriter w, AprsPositionedData d, bool altitudeInCs)
    {
        RequireCleanComment(d);
        if (d.AltitudeFeet is { } alt && !altitudeInCs)
        {
            WriteAltitude(w, alt);
        }

        if (d.Frequency is { } freq)
        {
            // Straight after a 7-byte extension, separate the frequency with '/' as the spec's first
            // form does ("$PHGphgd/FFF.FFFMHz", APRS12c §18); a PHGR extension already ends in '/'.
            bool afterExtension = !d.IsCompressed && d.AltitudeFeet is null && d.Weather is null
                && (d.CourseDegrees is not null || d.SpeedKnots is not null || d.RadioRangeMiles is not null
                    || d.DfSignalStrength is not null || d.AreaObject is not null || d.Phg is { BeaconsPerHour: null });
            if (afterExtension)
            {
                w.Char('/');
            }

            FrequencyCodec.WriteWithComment(w, freq, d.Comment);
        }

        if (d.Frequency is null)
        {
            bool extensionJustWritten = !d.IsCompressed && d.AltitudeFeet is null
                && (d.CourseDegrees is not null || d.SpeedKnots is not null || d.RadioRangeMiles is not null || d.DfSignalStrength is not null
                    || d.AreaObject is not null || d.DfBearing is not null || d.Storm is not null || d.Phg is not null);
            bool nothingBefore = d.AltitudeFeet is null && !extensionJustWritten;
            if (NeedsDelimiter(d.Comment, extensionJustWritten ? d.Phg : null) || (nothingBefore && !d.IsCompressed && LooksLikeExtension(d.Comment, d)))
            {
                w.Char('/');
            }

            w.Utf8(d.Comment);
        }

        WriteBraces(w, d);
        WriteTrailer(w, d, includeWeatherSoftware: false);
    }

    /// <summary>The end of a report: weather software/unit (weather reports), telemetry, DAO.</summary>
    public static void WriteTrailer(InfoWriter w, AprsPositionedData d) => WriteTrailer(w, d, includeWeatherSoftware: true);

    private static void WriteTrailer(InfoWriter w, AprsPositionedData d, bool includeWeatherSoftware)
    {
        if (includeWeatherSoftware && (d.Telemetry is not null || d.Frequency is not null || d.SignpostText is not null
            || (d.AltitudeFeet is not null && !PositionCodec.AltitudeFitsInCs(d))))
        {
            throw new ArgumentException("a weather report has no comment, so it cannot carry telemetry, frequency, signpost or /A= altitude (APRS12c ch. 12)");
        }

        if (d.Telemetry is { } t)
        {
            t.Validate(nameof(d.Telemetry));
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

        if (d.Dao is { } dao)
        {
            DaoCodec.Write(w, d.Position, dao);
        }
    }

    /// <summary>
    /// True when comment text would be read back differently without a delimiter in front: a leading
    /// space or slash (the decoder drops one as a delimiter), or, straight after plain PHG, a
    /// character and slash that would read as the PHGR beacon rate.
    /// </summary>
    internal static bool NeedsDelimiter(string comment, AprsPhg? phg) =>
        comment.Length > 0 && (comment[0] is ' ' or '/'
            || (phg is { BeaconsPerHour: null } && comment.Length >= 2 && comment[1] == '/' && comment[0] is (>= '1' and <= '9') or (>= 'A' and <= 'Z')));

    internal static void WriteAltitude(InfoWriter w, double feet)
    {
        int alt = (int)Math.Round(feet);
        if (alt is < -99999 or > 999999)
        {
            throw new ArgumentOutOfRangeException(nameof(feet), "/A= altitude must fit in 6 digits (or - and 5 digits)");
        }

        w.Ascii("/A=");
        _ = alt < 0 ? w.Char('-').Digits(-alt, 5) : w.Digits(alt, 6);
    }

    private static void WriteBraces(InfoWriter w, AprsPositionedData d)
    {
        if (d.SignpostText is { } sign)
        {
            if (d.Symbol is not { Table: '\\', Code: 'm' } || sign.Length is < 1 or > 3 || sign.Any(ch => ch is < ' ' or > '~' or '{' or '}'))
            {
                throw new ArgumentException("signpost text is 1-3 printable characters on the \\m symbol (APRS12c ch. 11)", nameof(d.SignpostText));
            }

            w.Char('{').Ascii(sign).Char('}');
        }

        if (d.AreaObject is { CorridorWidthMiles: { } corridor } area)
        {
            if (area.Shape is not (AprsAreaShape.LineDownRight or AprsAreaShape.LineDownLeft))
            {
                throw new ArgumentException("a corridor width only applies to line area objects (APRS12c ch. 11)", nameof(d.AreaObject));
            }

            w.Char('{').Ascii(corridor.ToString(CultureInfo.InvariantCulture)).Char('}');
        }
    }

    /// <summary>
    /// The encoder refuses comment text that the decoder would read back as a structured element,
    /// so every encoded report decodes to the same data. Set the matching property instead.
    /// </summary>
    internal static void RequireCleanComment(AprsPositionedData d)
    {
        string comment = d.Comment ?? throw new ArgumentNullException(nameof(d.Comment));
        Text.RequireNoLineBreaks(comment, nameof(d.Comment));
        var probe = new PositionedFields { Position = d.Position, Symbol = d.Symbol, AreaObject = d.AreaObject, IsCompressed = d.IsCompressed, Phg = d.Phg, RadioRangeMiles = d.RadioRangeMiles, DfSignalStrength = d.DfSignalStrength };
        var bytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes(comment));
        var ctx = new DecodeContext(AprsParseOptions.Lenient);
        TryExtract(bytes, 0, ctx, probe, frequencyAtStart: d.Frequency is null);
        if (probe.Dao is not null || probe.Telemetry is not null || probe.AltitudeFeet is not null || probe.SignpostText is not null
            || probe.Frequency is not null || probe.Phg != d.Phg || probe.RadioRangeMiles != d.RadioRangeMiles
            || probe.DfSignalStrength != d.DfSignalStrength || probe.AreaObject != d.AreaObject)
        {
            throw new ArgumentException(
                "comment text contains something that decodes as a structured element (altitude, !DAO!, |telemetry|, frequency, PHG/RNG/DFS or braces); set the property instead",
                nameof(d.Comment));
        }


    }

    /// <summary>
    /// Mic-E status text must not start with something read back as altitude or a data extension,
    /// or end with a device suffix, unless the matching property is set.
    /// </summary>
    internal static void RequireCleanMicEComment(AprsMicEReport r)
    {
        string comment = r.Comment ?? throw new ArgumentNullException(nameof(r));
        Text.RequireNoLineBreaks(comment, nameof(r.Comment));
        var bytes = new List<byte>(System.Text.Encoding.UTF8.GetBytes(comment));
        var probe = new PositionedFields { Position = r.Position, Symbol = r.Symbol, Phg = r.Phg, RadioRangeMiles = r.RadioRangeMiles, DfSignalStrength = r.DfSignalStrength };
        TryExtract(bytes, 0, new DecodeContext(AprsParseOptions.Lenient), probe, frequencyAtStart: r.Frequency is null);
        byte[] c = System.Text.Encoding.UTF8.GetBytes(comment);
        bool startsWithAltitude = r.AltitudeFeet is null && r.Phg is null && r.RadioRangeMiles is null && r.DfSignalStrength is null && r.Frequency is null
            && c.Length >= 4 && Base91.IsDigit(c[0]) && Base91.IsDigit(c[1]) && Base91.IsDigit(c[2]) && c[3] == (byte)'}';
        int pos = 0;
        bool startsWithExtension = r.Phg is null && r.RadioRangeMiles is null && r.DfSignalStrength is null && r.Frequency is null
            && !PositionCodec.IsCourseSpeed(c) && PositionCodec.TryReadDataExtension(c, ref pos, new DecodeContext(AprsParseOptions.Lenient), new PositionedFields { Position = r.Position, Symbol = r.Symbol }) && pos > 0;
        bool endsWithSuffix = r.DeviceSuffix.Length == 0 && r.Telemetry is null && r.Dao is null && r.TypeCode is { } t
            && (t is '`' or '\'' ? AprsDeviceIdentification.MicESuffixes : t is '>' or ']' ? AprsDeviceIdentification.MicELegacySuffixes(t) : [])
                .Any(s => comment.EndsWith(s, StringComparison.Ordinal));
        if (probe.Dao is not null || probe.Telemetry is not null || probe.AltitudeFeet is not null || probe.Frequency is not null
            || probe.Phg != r.Phg || probe.RadioRangeMiles != r.RadioRangeMiles || probe.DfSignalStrength != r.DfSignalStrength
            || startsWithAltitude || startsWithExtension || endsWithSuffix || comment.Contains('\xFF', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Mic-E status text contains something that decodes as a structured element (altitude, extension, !DAO!, |telemetry|, frequency or device suffix); set the property instead",
                nameof(r));
        }
    }

    /// <summary>True if text written straight after the symbol (or after course/speed) would be read back as an extension.</summary>
    private static bool LooksLikeExtension(string comment, AprsPositionedData d)
    {
        byte[] c = System.Text.Encoding.UTF8.GetBytes(comment);
        bool hasCourseSpeed = d.CourseDegrees is not null || d.SpeedKnots is not null || d.DfBearing is not null || d.Storm is not null;
        bool hasExtension = hasCourseSpeed || d.Phg is not null || d.RadioRangeMiles is not null || d.DfSignalStrength is not null || d.AreaObject is not null;
        if (d.AltitudeFeet is not null || d.Frequency is not null)
        {
            return false;
        }

        if (!hasExtension)
        {
            var probe = new PositionedFields { Position = d.Position, Symbol = d.Symbol };
            int pos = 0;
            PositionCodec.TryReadDataExtension(c, ref pos, new DecodeContext(AprsParseOptions.Lenient), probe);
            return pos > 0;
        }

        if (hasCourseSpeed && d.DfBearing is null && d.Storm is null)
        {
            int pos = 0;
            bool bearing = d.Symbol is { Table: '/', Code: '\\' } && c.Length >= 8 && c[0] == (byte)'/' && c[4] == (byte)'/'
                && Text.AllDigits(c.AsSpan(1, 3)) && Text.AllDigits(c.AsSpan(5, 3));
            return bearing || (d.Symbol.Code == '@' && StormCodec.TryRead(c, ref pos, out _));
        }

        return false;
    }
}
