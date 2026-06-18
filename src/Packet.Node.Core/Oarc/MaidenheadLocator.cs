using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Packet.Node.Core.Oarc;

/// <summary>
/// Maidenhead locator helpers (#459). The OARC collector validates a node's locator as a 6-character
/// grid (<c>^[A-R]{2}\d{2}[A-Xa-x]{2}$</c>) and is the node's only position source — pdn's
/// <c>Identity.Grid</c> is free-form, so the reporter validates it here, and (when the operator opts
/// in to exact coordinates) derives the grid-square CENTRE lat/lon to publish alongside it.
/// </summary>
public static class MaidenheadLocator
{
    // Subsquare extent in degrees: longitude 2°/24, latitude 1°/24.
    private const double SubLon = 2.0 / 24.0;
    private const double SubLat = 1.0 / 24.0;

    /// <summary>True if <paramref name="grid"/> is a valid 6-character Maidenhead locator, exactly as
    /// the collector requires: two field letters A–R, two digits, two subsquare letters A–X
    /// (case-insensitive). Surrounding whitespace is ignored.</summary>
    public static bool IsValid([NotNullWhen(true)] string? grid) => TryToLatLon(grid, out _, out _);

    /// <summary>Parse a 6-character Maidenhead grid and return the centre of its subsquare in decimal
    /// degrees. Returns <c>false</c> (and zeroes) for anything that is not a valid 6-char locator.</summary>
    public static bool TryToLatLon([NotNullWhen(true)] string? grid, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (grid is null)
        {
            return false;
        }

        var g = grid.Trim();
        if (g.Length != 6)
        {
            return false;
        }

        var f0 = ToUpper(g[0]);   // longitude field A–R
        var f1 = ToUpper(g[1]);   // latitude field  A–R
        var s0 = ToUpper(g[4]);   // longitude subsquare A–X
        var s1 = ToUpper(g[5]);   // latitude subsquare  A–X

        if (f0 < 'A' || f0 > 'R' || f1 < 'A' || f1 > 'R'
            || !IsDigit(g[2]) || !IsDigit(g[3])
            || s0 < 'A' || s0 > 'X' || s1 < 'A' || s1 > 'X')
        {
            return false;
        }

        // Lower-left corner of the subsquare, then + half a subsquare for the centre.
        longitude = -180.0 + (f0 - 'A') * 20.0 + (g[2] - '0') * 2.0 + (s0 - 'A') * SubLon + SubLon / 2.0;
        latitude = -90.0 + (f1 - 'A') * 10.0 + (g[3] - '0') * 1.0 + (s1 - 'A') * SubLat + SubLat / 2.0;
        return true;

        static char ToUpper(char c) => char.ToUpper(c, CultureInfo.InvariantCulture);
        static bool IsDigit(char c) => c is >= '0' and <= '9';
    }
}
