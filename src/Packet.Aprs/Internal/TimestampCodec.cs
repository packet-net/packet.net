namespace Packet.Aprs.Internal;

internal static class TimestampCodec
{
    /// <summary>True if 7 characters at <paramref name="pos"/> have the shape of a timestamp: 6 digits then z, / or h.</summary>
    public static bool HasShape(ReadOnlySpan<byte> info, int pos) =>
        info.Length >= pos + 7 && Text.AllDigits(info.Slice(pos, 6)) && info[pos + 6] is (byte)'z' or (byte)'/' or (byte)'h';

    /// <summary>Reads a 7-character DHM (<c>z</c> or <c>/</c>) or HMS (<c>h</c>) timestamp at <paramref name="pos"/>.</summary>
    public static bool TryRead(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, out AprsTimestamp timestamp)
    {
        timestamp = default;
        if (info.Length < pos + 7)
        {
            ctx.Error(AprsDiagnosticCode.InvalidTimestamp, "timestamp is truncated", pos);
            return false;
        }

        ReadOnlySpan<byte> digits = info.Slice(pos, 6);
        byte kind = info[pos + 6];
        if (!Text.AllDigits(digits) || kind is not ((byte)'z' or (byte)'/' or (byte)'h'))
        {
            ctx.Error(AprsDiagnosticCode.InvalidTimestamp, $"'{Text.Latin1(info.Slice(pos, 7))}' is not a timestamp: expected 6 digits then z, / or h (APRS12c ch. 6)", pos);
            return false;
        }

        int a = Text.ParseDigits(digits[..2]);
        int b = Text.ParseDigits(digits.Slice(2, 2));
        int c = Text.ParseDigits(digits.Slice(4, 2));
        timestamp = kind switch
        {
            (byte)'h' => AprsTimestamp.Unchecked(AprsTimestampFormat.HoursMinutesSecondsUtc, 0, 0, a, b, c),
            (byte)'z' => AprsTimestamp.Unchecked(AprsTimestampFormat.DayHoursMinutesUtc, 0, a, b, c, 0),
            _ => AprsTimestamp.Unchecked(AprsTimestampFormat.DayHoursMinutesLocal, 0, a, b, c, 0),
        };
        return timestamp.IsValid || ctx.Tolerate(
            ctx.Options.AllowInvalidTimestamp,
            AprsDiagnosticCode.InvalidTimestamp,
            $"timestamp '{Text.Latin1(info.Slice(pos, 7))}' has a field out of range (APRS12c ch. 6)",
            pos);
    }

    /// <summary>Reads an 8-digit MDHM timestamp (positionless weather reports).</summary>
    public static bool TryReadMonthDay(ReadOnlySpan<byte> info, int pos, DecodeContext ctx, out AprsTimestamp timestamp)
    {
        timestamp = default;
        if (info.Length < pos + 8 || !Text.AllDigits(info.Slice(pos, 8)))
        {
            ctx.Error(AprsDiagnosticCode.InvalidTimestamp, "expected an 8-digit MMDDHHMM timestamp (APRS12c ch. 6)", pos);
            return false;
        }

        int month = Text.ParseDigits(info.Slice(pos, 2));
        int day = Text.ParseDigits(info.Slice(pos + 2, 2));
        int hour = Text.ParseDigits(info.Slice(pos + 4, 2));
        int minute = Text.ParseDigits(info.Slice(pos + 6, 2));
        timestamp = AprsTimestamp.Unchecked(AprsTimestampFormat.MonthDayHoursMinutesUtc, month, day, hour, minute, 0);
        return timestamp.IsValid || ctx.Tolerate(
            ctx.Options.AllowInvalidTimestamp,
            AprsDiagnosticCode.InvalidTimestamp,
            $"timestamp '{Text.Latin1(info.Slice(pos, 8))}' has a field out of range (APRS12c ch. 6)",
            pos);
    }

    public static void Write(InfoWriter writer, AprsTimestamp timestamp, bool allowMonthDay)
    {
        if (!timestamp.IsValid)
        {
            throw new ArgumentException($"timestamp {timestamp} has a field out of range", nameof(timestamp));
        }

        if (timestamp.Format == AprsTimestampFormat.MonthDayHoursMinutesUtc && !allowMonthDay)
        {
            throw new ArgumentException("the MMDDHHMM timestamp format is only used in positionless weather reports (APRS12c ch. 6)", nameof(timestamp));
        }

        writer.Ascii(timestamp.ToString());
    }
}
