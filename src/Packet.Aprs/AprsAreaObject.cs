namespace Packet.Aprs;

/// <summary>The shape of an area object (APRS12c §11 Area Objects, type <c>T</c>).</summary>
public enum AprsAreaShape
{
    /// <summary>Open circle.</summary>
    OpenCircle = 0,

    /// <summary>Line, offset down and to the right.</summary>
    LineDownRight = 1,

    /// <summary>Open ellipse.</summary>
    OpenEllipse = 2,

    /// <summary>Open triangle.</summary>
    OpenTriangle = 3,

    /// <summary>Open box.</summary>
    OpenBox = 4,

    /// <summary>Colour-filled circle.</summary>
    FilledCircle = 5,

    /// <summary>Line, offset down and to the left.</summary>
    LineDownLeft = 6,

    /// <summary>Colour-filled ellipse.</summary>
    FilledEllipse = 7,

    /// <summary>Colour-filled triangle.</summary>
    FilledTriangle = 8,

    /// <summary>Colour-filled box.</summary>
    FilledBox = 9,
}

/// <summary>The colour of an area object (APRS12c §11, <c>/C</c>): eight colours at high and low intensity.</summary>
public enum AprsAreaColor
{
    /// <summary>Black, high intensity.</summary>
    Black = 0,

    /// <summary>Blue, high intensity.</summary>
    Blue = 1,

    /// <summary>Green, high intensity.</summary>
    Green = 2,

    /// <summary>Cyan, high intensity.</summary>
    Cyan = 3,

    /// <summary>Red, high intensity.</summary>
    Red = 4,

    /// <summary>Violet, high intensity.</summary>
    Violet = 5,

    /// <summary>Yellow, high intensity.</summary>
    Yellow = 6,

    /// <summary>Grey, high intensity.</summary>
    Gray = 7,

    /// <summary>Black, low intensity.</summary>
    BlackLow = 8,

    /// <summary>Blue, low intensity.</summary>
    BlueLow = 9,

    /// <summary>Green, low intensity.</summary>
    GreenLow = 10,

    /// <summary>Cyan, low intensity.</summary>
    CyanLow = 11,

    /// <summary>Red, low intensity.</summary>
    RedLow = 12,

    /// <summary>Violet, low intensity.</summary>
    VioletLow = 13,

    /// <summary>Yellow, low intensity.</summary>
    YellowLow = 14,

    /// <summary>Grey, low intensity.</summary>
    GrayLow = 15,
}

/// <summary>
/// An area object descriptor: the <c>Tyy/Cxx</c> data extension that follows the <c>\l</c>
/// symbol (APRS12c §11 Area Objects).
/// </summary>
/// <remarks>
/// The offsets are carried as <c>yy</c> and <c>xx</c>, the square root of 1500 times the offset
/// in degrees (the 1500 scaling is the APRS 1.1 correction; the original spec said 100). For
/// line shapes an optional corridor half-width in miles follows in the comment as <c>{nnn}</c>.
/// </remarks>
/// <param name="Shape">What to draw.</param>
/// <param name="Color">Fill or line colour.</param>
/// <param name="LatitudeOffsetCode">yy, 0-99.</param>
/// <param name="LongitudeOffsetCode">xx, 0-99.</param>
/// <param name="CorridorWidthMiles">For lines, the corridor half-width from the <c>{nnn}</c> comment
/// field, or null.</param>
public readonly record struct AprsAreaObject(
    AprsAreaShape Shape,
    AprsAreaColor Color,
    int LatitudeOffsetCode,
    int LongitudeOffsetCode,
    int? CorridorWidthMiles = null)
{
    /// <summary>The latitude offset in degrees: yy squared / 1500.</summary>
    public double LatitudeOffsetDegrees => LatitudeOffsetCode * LatitudeOffsetCode / 1500.0;

    /// <summary>The longitude offset in degrees: xx squared / 1500.</summary>
    public double LongitudeOffsetDegrees => LongitudeOffsetCode * LongitudeOffsetCode / 1500.0;

    internal void Validate(string paramName)
    {
        if (!Enum.IsDefined(Shape) || !Enum.IsDefined(Color) || LatitudeOffsetCode is < 0 or > 99 || LongitudeOffsetCode is < 0 or > 99
            || CorridorWidthMiles is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(paramName, this, "area object: defined shape and colour, offsets 0-99, corridor 0-999");
        }
    }
}
