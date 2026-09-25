namespace Packet.Aprs;

/// <summary>Tropical storm category (APRS12c §12 Storm Data).</summary>
public enum AprsStormType
{
    /// <summary><c>TS</c>: tropical storm.</summary>
    TropicalStorm,

    /// <summary><c>HC</c>: hurricane.</summary>
    Hurricane,

    /// <summary><c>TD</c>: tropical depression.</summary>
    TropicalDepression,
}

/// <summary>
/// Storm data that follows course/speed on a hurricane symbol (<c>\@</c> current, <c>/@</c>
/// predicted): <c>/ST/www^GGG/pppp&gt;RRR&amp;rrr%ggg</c> (APRS12c §12 Storm Data).
/// </summary>
public sealed record AprsStorm
{
    /// <summary>Storm category.</summary>
    public required AprsStormType Type { get; init; }

    /// <summary>Sustained wind speed in knots, or null if unknown.</summary>
    public int? SustainedWindKnots { get; init; }

    /// <summary>Peak gusts in knots, or null if unknown.</summary>
    public int? GustKnots { get; init; }

    /// <summary>Central pressure in millibars (hPa), or null if unknown.</summary>
    public int? CentralPressureMillibars { get; init; }

    /// <summary>Radius of hurricane-force winds in nautical miles, or null if unknown.</summary>
    public int? HurricaneWindRadiusNauticalMiles { get; init; }

    /// <summary>Radius of tropical-storm-force winds in nautical miles, or null if unknown.</summary>
    public int? TropicalStormWindRadiusNauticalMiles { get; init; }

    /// <summary>Radius of whole-gale (50 knot) winds in nautical miles, or null if not sent (optional field).</summary>
    public int? WholeGaleRadiusNauticalMiles { get; init; }
}
