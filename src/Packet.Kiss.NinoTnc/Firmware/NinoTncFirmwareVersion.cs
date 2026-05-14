using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Packet.Kiss.NinoTnc.Firmware;

/// <summary>
/// A NinoTNC firmware version in Nino's two-component form, e.g.
/// <c>3.44</c> or <c>4.44</c>. The major component encodes the chip
/// variant (<c>3</c> = dsPIC33EP256GP, <c>4</c> = dsPIC33EP512GP) per
/// the convention adopted from firmware 2.90 onward.
/// </summary>
/// <param name="Major">Chip-variant indicator. 3 or 4 for current
///   hardware; older firmware (pre-2.90) used a single sequence (1.x,
///   2.x) without the chip-variant overload.</param>
/// <param name="Minor">Sequential release number within the major.
///   E.g. 44 in <c>3.44</c>.</param>
public readonly record struct NinoTncFirmwareVersion(int Major, int Minor) : IComparable<NinoTncFirmwareVersion>
{
    /// <summary>The chip variant this firmware version implies.</summary>
    public NinoTncChipVariant ChipVariant => Major switch
    {
        3 => NinoTncChipVariant.Dspic33Ep256,
        4 => NinoTncChipVariant.Dspic33Ep512,
        _ => NinoTncChipVariant.Unknown,
    };

    /// <summary>
    /// Parse a firmware version string. Accepts <c>"3.44"</c>,
    /// <c>"4.44"</c>, and the legacy <c>"2.71"</c> shape. Whitespace
    /// trimmed; otherwise strict.
    /// </summary>
    public static NinoTncFirmwareVersion Parse(string text)
    {
        if (TryParse(text, out var version))
        {
            return version;
        }
        throw new FormatException($"Not a NinoTNC firmware version: '{text}'");
    }

    /// <summary>Non-throwing variant of <see cref="Parse"/>.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out NinoTncFirmwareVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var trimmed = text.Trim();
        int dot = trimmed.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == trimmed.Length - 1)
        {
            return false;
        }
        if (!int.TryParse(trimmed.AsSpan(0, dot), NumberStyles.Integer, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(trimmed.AsSpan(dot + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor))
        {
            return false;
        }
        if (major < 0 || minor < 0)
        {
            return false;
        }
        version = new NinoTncFirmwareVersion(major, minor);
        return true;
    }

    /// <inheritdoc/>
    public int CompareTo(NinoTncFirmwareVersion other)
    {
        // Comparison only makes sense within the same chip variant — a
        // "3.44" is not "less than" or "greater than" a "4.44"; they're
        // for different chips. Callers should filter by ChipVariant
        // before ranking. We still expose a total order so the type can
        // be sorted in collections, with major as primary key.
        var byMajor = Major.CompareTo(other.Major);
        return byMajor != 0 ? byMajor : Minor.CompareTo(other.Minor);
    }

    public static bool operator <(NinoTncFirmwareVersion left, NinoTncFirmwareVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(NinoTncFirmwareVersion left, NinoTncFirmwareVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(NinoTncFirmwareVersion left, NinoTncFirmwareVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(NinoTncFirmwareVersion left, NinoTncFirmwareVersion right) => left.CompareTo(right) >= 0;

    /// <inheritdoc/>
    public override string ToString() => $"{Major}.{Minor:D2}";
}
