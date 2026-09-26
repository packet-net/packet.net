using Packet.Aprs.Internal;

namespace Packet.Aprs;

/// <summary>A weather field this library does not interpret, kept so it is not lost:
/// a letter followed by its value text (APRS12c §12; UAP §2.7.1 says receivers should skip
/// unrecognised fields rather than fail).</summary>
/// <param name="Letter">The field letter, e.g. <c>X</c>.</param>
/// <param name="Value">The characters that followed it, as sent.</param>
public readonly record struct AprsWeatherField(char Letter, string Value);

/// <summary>
/// Weather observations (APRS12c §12 Weather Data), in the units APRS uses on air. Every value is
/// nullable: null means the field was absent or sent as dots / spaces (unknown).
/// </summary>
public sealed record AprsWeather
{
    /// <summary>Wind direction in degrees clockwise from north (<c>c</c>, or the DIR part of the extension).</summary>
    public int? WindDirectionDegrees { get; init; }

    /// <summary>Sustained one-minute wind speed in mph (<c>s</c>, or the SPD part of the extension;
    /// see <c>interpretations.md</c> in <c>packet-net/aprs-vectors</c> on units). Fractional when it came from a compressed position.</summary>
    public double? WindSpeedMph { get; init; }

    /// <summary>Peak wind speed in the last 5 minutes, mph (<c>g</c>).</summary>
    public int? WindGustMph { get; init; }

    /// <summary>Temperature in degrees Fahrenheit (<c>t</c>); may be negative.</summary>
    public int? TemperatureFahrenheit { get; init; }

    /// <summary>Rainfall in the last hour, inches (<c>r</c>, sent in hundredths).</summary>
    public double? RainLastHourInches { get; init; }

    /// <summary>Rainfall in the last 24 hours, inches (<c>p</c>, sent in hundredths).</summary>
    public double? RainLast24HoursInches { get; init; }

    /// <summary>Rainfall since local midnight, inches (<c>P</c>, sent in hundredths).</summary>
    public double? RainSinceMidnightInches { get; init; }

    /// <summary>Relative humidity 1-100 percent (<c>h</c>; <c>h00</c> on air means 100).</summary>
    public int? HumidityPercent { get; init; }

    /// <summary>Barometric pressure in millibars / hPa (<c>b</c>, sent in tenths).</summary>
    public double? PressureMillibars { get; init; }

    /// <summary>Luminosity in W/m^2 (<c>L</c> for 0-999, <c>l</c> for 1000-1999).</summary>
    public int? LuminosityWattsPerSquareMetre { get; init; }

    /// <summary>Snowfall in the last 24 hours, inches (<c>s</c> after the mandatory fields; may have a decimal point).</summary>
    public decimal? SnowfallLast24HoursInches { get; init; }

    /// <summary>Raw rain gauge counter (<c>#</c>).</summary>
    public int? RainRawCounter { get; init; }

    /// <summary>Other letter-plus-value fields, in the order received.</summary>
    public IReadOnlyList<AprsWeatherField> AdditionalFields { get; init => field = EquatableList<AprsWeatherField>.Of(value); } = [];

    /// <summary>The one-character APRS software type after the data, e.g. <c>w</c> WinAPRS (APRS12c §12).</summary>
    public char? SoftwareType { get; init; }

    /// <summary>The 2-4 character weather unit type after the software type, e.g. <c>RSW</c>, <c>Dvs</c>, <c>U2k</c>.</summary>
    public string? UnitType { get; init; }

    internal void Validate(string paramName)
    {
        if (WindDirectionDegrees is < 0 or > 360 || WindSpeedMph is < 0 or > 999 || WindGustMph is < 0 or > 999
            || TemperatureFahrenheit is < -99 or > 999 || HumidityPercent is < 1 or > 100
            || PressureMillibars is < 0 or > 9999.9 || LuminosityWattsPerSquareMetre is < 0 or > 1999
            || RainLastHourInches is < 0 or > 9.99 || RainLast24HoursInches is < 0 or > 9.99 || RainSinceMidnightInches is < 0 or > 9.99
            || SnowfallLast24HoursInches is < 0 or > 999 || RainRawCounter is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(paramName, "a weather value is outside what its fixed-width field can carry");
        }

        if ((SoftwareType is null) != (UnitType is null) && UnitType is not null)
        {
            throw new ArgumentException("a weather unit type needs a software type before it", paramName);
        }

        if (UnitType is { Length: < 2 or > 4 })
        {
            throw new ArgumentException("weather unit type must be 2-4 characters", paramName);
        }
    }
}
