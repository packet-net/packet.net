using System.Globalization;

namespace Packet.Aprs.Internal;

/// <summary>Status reports (APRS12c §16).</summary>
internal static class StatusCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        int pos = 1;
        AprsTimestamp? timestamp = null;
        string? locator = null;
        AprsSymbol? symbol = null;

        if (info.Length >= 8 && Text.AllDigits(info.Slice(1, 6)) && info[7] == (byte)'z')
        {
            if (!TimestampCodec.TryRead(info, 1, ctx, out AprsTimestamp ts))
            {
                return null;
            }

            timestamp = ts;
            pos = 8;
        }
        else if (TryLocatorWithSymbol(info[1..], out int locatorLength))
        {
            // Letters may be received in either case but are sent in upper case (APRS12c §16).
            locator = Text.Latin1(info.Slice(1, locatorLength)).ToUpperInvariant();
            symbol = new AprsSymbol((char)info[1 + locatorLength], (char)info[2 + locatorLength]);
            pos = 3 + locatorLength;
            if (pos < info.Length)
            {
                if (info[pos] == (byte)' ')
                {
                    pos++;
                }
                else if (!ctx.Tolerate(
                             ctx.Options.AllowMissingSpaceAfterLocator,
                             AprsDiagnosticCode.MissingSpaceAfterLocator,
                             "status text after a grid locator must start with a space (APRS12c ch. 16, UAP 5.17)",
                             pos))
                {
                    return null;
                }
            }
        }

        ReadOnlySpan<byte> text = info[pos..];
        AprsBeamHeading? beam = null;
        if (text.Length >= 3 && text[^3] == (byte)'^')
        {
            var candidate = new AprsBeamHeading((char)text[^2], (char)text[^1]);
            if (candidate.IsValid)
            {
                beam = candidate;
                text = text[..^3];
            }
        }

        if (!Text.TryDecode(text, ctx, pos, out string decoded))
        {
            return null;
        }

        return new AprsStatusReport
        {
            Timestamp = timestamp,
            Text = decoded,
            MaidenheadLocator = locator,
            Symbol = symbol,
            BeamHeading = beam,
        };
    }

    /// <summary>A 4 or 6 character locator then a symbol table and code, then end or a space
    /// (or, tolerated, other text). Letters may be received in either case (APRS12c §16).</summary>
    private static bool TryLocatorWithSymbol(ReadOnlySpan<byte> s, out int length)
    {
        // ">JN55VD Powered by..." is a bare 6-character locator: plain text, not "JN55" with symbol "VD".
        bool sixCharacterLocator = s.Length >= 6 && Maidenhead.IsLocator(s[..6]);
        foreach (int candidate in sixCharacterLocator ? (int[])[6] : [6, 4])
        {
            length = candidate;
            if (s.Length >= candidate + 2 && Maidenhead.IsLocator(s[..candidate]) && AprsSymbol.IsValidTable((char)s[candidate])
                && s[candidate + 1] is >= (byte)'!' and <= (byte)'~')
            {
                return true;
            }
        }

        length = 0;
        return false;
    }

    public static void Write(InfoWriter w, AprsStatusReport r)
    {
        Text.RequireNoLineBreaks(r.Text, nameof(r.Text));
        w.Char('>');
        if (r.MaidenheadLocator is { } locator)
        {
            if (r.Timestamp is not null)
            {
                throw new ArgumentException("a status report with a grid locator cannot have a timestamp (APRS12c ch. 16)", nameof(r));
            }

            if (r.Symbol is not { } symbol)
            {
                throw new ArgumentException("a grid locator status needs a symbol", nameof(r));
            }

            if (!Maidenhead.IsLocator(System.Text.Encoding.ASCII.GetBytes(locator)) || locator.Length is not (4 or 6))
            {
                throw new ArgumentException($"'{locator}' is not a 4 or 6 character Maidenhead locator", nameof(r));
            }

            symbol.Validate(nameof(r.Symbol));
            w.Ascii(locator.ToUpperInvariant()).Char(symbol.Table).Char(symbol.Code);
            if (r.Text.Length > 0 || r.BeamHeading is not null)
            {
                w.Char(' ');
            }
        }
        else if (r.Symbol is not null)
        {
            throw new ArgumentException("a status symbol only goes with a grid locator", nameof(r));
        }
        else if (r.Timestamp is { } ts)
        {
            if (ts.Format != AprsTimestampFormat.DayHoursMinutesUtc)
            {
                throw new ArgumentException("a status report timestamp must be DHM UTC (APRS12c ch. 16)", nameof(r));
            }

            w.Ascii(ts.ToString());
        }

        byte[] text = System.Text.Encoding.UTF8.GetBytes(r.Text);
        if (r.MaidenheadLocator is null && r.Timestamp is null && TryLocatorWithSymbol(text, out _))
        {
            throw new ArgumentException("status text starts with what reads as a grid locator and symbol; set MaidenheadLocator instead", nameof(r));
        }

        if (r.MaidenheadLocator is null && r.Timestamp is null && text.Length >= 7 && Text.AllDigits(text.AsSpan(0, 6)) && text[6] == (byte)'z')
        {
            throw new ArgumentException("status text starts with what reads as a timestamp; set Timestamp instead", nameof(r));
        }

        w.Bytes(text);
        if (r.BeamHeading is { } beam)
        {
            if (!beam.IsValid)
            {
                throw new ArgumentException("beam heading code must be 0-9 or A-Z and power code 1-K", nameof(r));
            }

            w.Char('^').Char(beam.HeadingCode).Char(beam.PowerCode);
        }
        else if (text.Length >= 3 && text[^3] == (byte)'^' && new AprsBeamHeading((char)text[^2], (char)text[^1]).IsValid)
        {
            throw new ArgumentException("status text ends with what reads as ^HP beam heading/power; set BeamHeading instead", nameof(r));
        }
    }
}

/// <summary>Maidenhead locator syntax: field A-R, square 0-9, optional subsquare A-X (either case).</summary>
internal static class Maidenhead
{
    public static bool IsLocator(ReadOnlySpan<byte> s)
    {
        if (s.Length is not (4 or 6))
        {
            return false;
        }

        bool field = Upper(s[0]) is >= (byte)'A' and <= (byte)'R' && Upper(s[1]) is >= (byte)'A' and <= (byte)'R';
        bool square = Text.IsDigit(s[2]) && Text.IsDigit(s[3]);
        bool sub = s.Length == 4 || (Upper(s[4]) is >= (byte)'A' and <= (byte)'X' && Upper(s[5]) is >= (byte)'A' and <= (byte)'X');
        return field && square && sub;
    }

    private static byte Upper(byte b) => Text.IsLower(b) ? (byte)(b - 32) : b;
}

/// <summary>Telemetry reports, data type <c>T</c> (APRS12c §13).</summary>
internal static class TelemetryCodec
{
    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        if (info.Length < 3 || info[1] != (byte)'#')
        {
            ctx.Error(AprsDiagnosticCode.InvalidTelemetry, "telemetry report must start T# (APRS12c ch. 13)", 1);
            return null;
        }

        int pos = 2;
        string sequence;
        if (info[2..].StartsWith("MIC"u8))
        {
            sequence = "MIC";
            pos = 5;
            if (pos < info.Length && info[pos] == (byte)',')
            {
                pos++;
            }
        }
        else
        {
            int comma = info[2..].IndexOf((byte)',');
            if (comma < 1)
            {
                ctx.Error(AprsDiagnosticCode.InvalidTelemetry, "telemetry sequence number is not followed by ','", 2);
                return null;
            }

            sequence = Text.Latin1(info.Slice(2, comma));
            if (!sequence.All(char.IsAsciiLetterOrDigit))
            {
                ctx.Error(AprsDiagnosticCode.InvalidTelemetry, "telemetry sequence number must be letters or digits", 2);
                return null;
            }

            pos = 2 + comma + 1;
        }

        var analog = new List<decimal?>(5);
        while (analog.Count < 5)
        {
            int end = pos;
            while (end < info.Length && info[end] != (byte)',')
            {
                end++;
            }

            ReadOnlySpan<byte> field = info[pos..end];
            if (field.Length == 0)
            {
                analog.Add(null);
            }
            else if (decimal.TryParse(Text.Latin1(field), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal v))
            {
                analog.Add(v);
            }
            else
            {
                ctx.Error(AprsDiagnosticCode.InvalidTelemetry, $"telemetry value '{Text.Latin1(field)}' is not a number", pos);
                return null;
            }

            pos = end;
            if (pos >= info.Length)
            {
                break;
            }

            pos++; // the comma
        }

        byte? digital = null;
        if (analog.Count == 5 && pos < info.Length)
        {
            if (info.Length >= pos + 8 && info.Slice(pos, 8).IndexOfAnyExcept((byte)'0', (byte)'1') < 0)
            {
                byte bits = 0;
                for (int i = 0; i < 8; i++)
                {
                    if (info[pos + i] == (byte)'1')
                    {
                        bits |= (byte)(1 << i);
                    }
                }

                digital = bits;
                pos += 8;
            }
        }

        if (analog.Count < 5 || digital is null)
        {
            if (!ctx.Tolerate(
                    ctx.Options.AllowIncompleteTelemetry,
                    AprsDiagnosticCode.InvalidTelemetry,
                    "telemetry report should carry 5 analog values and 8 digital bits (APRS12c ch. 13)",
                    2))
            {
                return null;
            }
        }

        if (!Text.TryDecode(info[pos..], ctx, pos, out string comment))
        {
            return null;
        }

        return new AprsTelemetryReport { Sequence = sequence, Analog = analog, Digital = digital, Comment = comment };
    }

    public static void Write(InfoWriter w, AprsTelemetryReport r)
    {
        if (r.Sequence.Length == 0 || !r.Sequence.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("telemetry sequence is letters or digits", nameof(r));
        }

        if (r.Analog.Count != 5 || r.Digital is null)
        {
            throw new ArgumentException("a telemetry report carries 5 analog values and the 8 digital bits (APRS12c ch. 13)", nameof(r));
        }

        Text.RequireNoLineBreaks(r.Comment, nameof(r.Comment));
        w.Ascii("T#");
        w.Ascii(r.Sequence);
        for (int i = 0; i < r.Analog.Count; i++)
        {
            decimal? v = r.Analog[i];

            // "MIC may or may not be followed by a comma" (APRS12c §13); the spec's example has none.
            if (i > 0 || r.Sequence != "MIC")
            {
                w.Char(',');
            }

            if (v is { } value)
            {
                bool canonical = value == decimal.Truncate(value) && value is >= 0 and <= 999 && value.Scale == 0;
                w.Ascii(canonical ? ((int)value).ToString("000", CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture));
            }
        }

        w.Char(',');
        for (int i = 0; i < 8; i++)
        {
            w.Char((r.Digital.Value & (1 << i)) != 0 ? '1' : '0');
        }

        w.Utf8(r.Comment);
    }
}

/// <summary>Positionless weather reports, data type <c>_</c> (APRS12c §12).</summary>
internal static class WeatherReportCodec
{

    public static AprsData? Decode(ReadOnlySpan<byte> info, DecodeContext ctx)
    {
        ctx.Info(AprsDiagnosticCode.ObsoleteFormat, "positionless weather reports are not recommended; senders should use a position report with the weather symbol (APRS12c ch. 12)", 0);
        if (!TimestampCodec.TryReadMonthDay(info, 1, ctx, out AprsTimestamp timestamp))
        {
            return null;
        }

        int pos = 9;
        var weather = new AprsWeather();
        if (!WeatherCodec.TryReadFields(info, ref pos, ctx, ref weather, positionless: true))
        {
            return null;
        }

        ReadOnlySpan<byte> tail = info[pos..];
        string comment = "";
        if (CommentCodec.IsSoftwareAndUnit(tail))
        {
            weather = weather with { SoftwareType = (char)tail[0], UnitType = Text.Latin1(tail[1..]) };
        }
        else if (tail.Length > 0)
        {
            if (!ctx.Tolerate(ctx.Options.AllowWeatherComment, AprsDiagnosticCode.WeatherComment, "text after the weather data kept as a comment (APRS12c ch. 12)", pos)
                || !Text.TryDecode(tail, ctx, pos, out comment))
            {
                return null;
            }
        }

        return new AprsWeatherReport { Timestamp = timestamp, Weather = weather, Comment = comment };
    }
}
