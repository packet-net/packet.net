namespace Packet.Aprs;

/// <summary>The three APRS timestamp layouts (APRS12c §6 Time Formats), with the DHM local-time variant.</summary>
public enum AprsTimestampFormat
{
    /// <summary><c>DDHHMMz</c>: day of month, hours, minutes, UTC. The recommended form.</summary>
    DayHoursMinutesUtc,

    /// <summary><c>DDHHMM/</c>: day of month, hours, minutes, sender's local time. Not recommended.</summary>
    DayHoursMinutesLocal,

    /// <summary><c>HHMMSSh</c>: hours, minutes, seconds, UTC.</summary>
    HoursMinutesSecondsUtc,

    /// <summary><c>MMDDHHMM</c>: month, day, hours, minutes, UTC. Only in positionless weather reports.</summary>
    MonthDayHoursMinutesUtc,
}

/// <summary>
/// A timestamp as carried in an APRS packet. APRS timestamps are partial (no year, and some
/// have no month or day), so they are kept as their fields and resolved against a reference time
/// with <see cref="Resolve"/>.
/// </summary>
public readonly record struct AprsTimestamp
{
    private AprsTimestamp(AprsTimestampFormat format, int month, int day, int hour, int minute, int second)
    {
        Format = format;
        Month = month;
        Day = day;
        Hour = hour;
        Minute = minute;
        Second = second;
    }

    /// <summary>Which of the layouts this timestamp uses.</summary>
    public AprsTimestampFormat Format { get; }

    /// <summary>Month 1-12, or 0 when the format has no month.</summary>
    public int Month { get; }

    /// <summary>Day of month 1-31, or 0 when the format has no day.</summary>
    public int Day { get; }

    /// <summary>Hour 0-23.</summary>
    public int Hour { get; }

    /// <summary>Minute 0-59.</summary>
    public int Minute { get; }

    /// <summary>Second 0-59, or 0 when the format has no seconds.</summary>
    public int Second { get; }

    /// <summary>
    /// False for a timestamp decoded with out-of-range fields (e.g. day 0 or hour 41), which is only
    /// possible when <see cref="AprsParseOptions.AllowInvalidTimestamp"/> tolerated it. An invalid
    /// timestamp does not <see cref="Resolve"/> and cannot be encoded.
    /// </summary>
    public bool IsValid => Format switch
    {
        AprsTimestampFormat.HoursMinutesSecondsUtc => Hour <= 23 && Minute <= 59 && Second <= 59,
        AprsTimestampFormat.MonthDayHoursMinutesUtc => Month is >= 1 and <= 12 && Day is >= 1 and <= 31 && Hour <= 23 && Minute <= 59,
        _ => Day is >= 1 and <= 31 && Hour <= 23 && Minute <= 59,
    };

    /// <summary>
    /// True for <c>111111z</c>, the pseudo-timestamp that marks a permanent object which only its
    /// originator may replace (APRS12c §18 Object Name Permanence).
    /// </summary>
    public bool IsPermanentObjectMarker =>
        Format == AprsTimestampFormat.DayHoursMinutesUtc && Day == 11 && Hour == 11 && Minute == 11;

    /// <summary>A <c>DDHHMMz</c> (or <c>DDHHMM/</c> local) timestamp.</summary>
    public static AprsTimestamp DayHoursMinutes(int day, int hour, int minute, bool utc = true)
    {
        Check(day, 1, 31, nameof(day));
        Check(hour, 0, 23, nameof(hour));
        Check(minute, 0, 59, nameof(minute));
        return new(utc ? AprsTimestampFormat.DayHoursMinutesUtc : AprsTimestampFormat.DayHoursMinutesLocal, 0, day, hour, minute, 0);
    }

    /// <summary>A <c>HHMMSSh</c> timestamp.</summary>
    public static AprsTimestamp HoursMinutesSeconds(int hour, int minute, int second)
    {
        Check(hour, 0, 23, nameof(hour));
        Check(minute, 0, 59, nameof(minute));
        Check(second, 0, 59, nameof(second));
        return new(AprsTimestampFormat.HoursMinutesSecondsUtc, 0, 0, hour, minute, second);
    }

    /// <summary>A <c>MMDDHHMM</c> timestamp (positionless weather reports).</summary>
    public static AprsTimestamp MonthDayHoursMinutes(int month, int day, int hour, int minute)
    {
        Check(month, 1, 12, nameof(month));
        Check(day, 1, 31, nameof(day));
        Check(hour, 0, 23, nameof(hour));
        Check(minute, 0, 59, nameof(minute));
        return new(AprsTimestampFormat.MonthDayHoursMinutesUtc, month, day, hour, minute, 0);
    }

    /// <summary>The timestamp for a UTC instant in the given format (seconds are dropped where the
    /// format has none).</summary>
    public static AprsTimestamp FromDateTime(DateTime utc, AprsTimestampFormat format = AprsTimestampFormat.DayHoursMinutesUtc) => format switch
    {
        AprsTimestampFormat.DayHoursMinutesUtc => DayHoursMinutes(utc.Day, utc.Hour, utc.Minute),
        AprsTimestampFormat.DayHoursMinutesLocal => DayHoursMinutes(utc.Day, utc.Hour, utc.Minute, utc: false),
        AprsTimestampFormat.HoursMinutesSecondsUtc => HoursMinutesSeconds(utc.Hour, utc.Minute, utc.Second),
        AprsTimestampFormat.MonthDayHoursMinutesUtc => MonthDayHoursMinutes(utc.Month, utc.Day, utc.Hour, utc.Minute),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Resolves the timestamp to a full date and time: the candidate closest to
    /// <paramref name="reference"/> (normally the time the packet was received). Returns null
    /// if no such date exists (for example 31 February). For <see cref="AprsTimestampFormat.DayHoursMinutesLocal"/>
    /// the result is in the sender's unknown local time zone and has <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    public DateTime? Resolve(DateTime reference)
    {
        if (!IsValid)
        {
            return null;
        }

        var kind = Format == AprsTimestampFormat.DayHoursMinutesLocal ? DateTimeKind.Unspecified : DateTimeKind.Utc;
        DateTime? best = null;
        for (int step = -1; step <= 1; step++)
        {
            DateTime? candidate = Format switch
            {
                AprsTimestampFormat.HoursMinutesSecondsUtc => reference.Date.AddDays(step).Add(new TimeSpan(Hour, Minute, Second)),
                AprsTimestampFormat.MonthDayHoursMinutesUtc => Make(reference.Year + step, Month),
                _ => MakeInMonth(new DateTime(reference.Year, reference.Month, 1).AddMonths(step)),
            };

            if (candidate is { } c && (best is null || Math.Abs((c - reference).Ticks) < Math.Abs((best.Value - reference).Ticks)))
            {
                best = c;
            }
        }

        return best is { } b ? DateTime.SpecifyKind(b, kind) : null;
    }

    private DateTime? MakeInMonth(DateTime month) => Make(month.Year, month.Month);

    private DateTime? Make(int year, int month) =>
        Day <= DateTime.DaysInMonth(year, month) ? new DateTime(year, month, Day, Hour, Minute, 0) : null;

    /// <summary>Formats as it appears on air, e.g. <c>092345z</c>.</summary>
    public override string ToString() => Format switch
    {
        AprsTimestampFormat.DayHoursMinutesUtc => $"{Day:00}{Hour:00}{Minute:00}z",
        AprsTimestampFormat.DayHoursMinutesLocal => $"{Day:00}{Hour:00}{Minute:00}/",
        AprsTimestampFormat.HoursMinutesSecondsUtc => $"{Hour:00}{Minute:00}{Second:00}h",
        _ => $"{Month:00}{Day:00}{Hour:00}{Minute:00}",
    };

    /// <summary>Builds a timestamp from on-air fields without range checks (decoder only).</summary>
    internal static AprsTimestamp Unchecked(AprsTimestampFormat format, int month, int day, int hour, int minute, int second) =>
        new(format, month, day, hour, minute, second);

    private static void Check(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, value, $"must be {min} to {max}");
        }
    }
}
