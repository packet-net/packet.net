namespace Packet.Aprs;

/// <summary>
/// A decoded APRS position report. Latitude and longitude are decimal
/// degrees with WGS-84 reference (positive north / east). Symbol table +
/// symbol code identify the station's icon per APRS12c §20 (symbol table
/// page). Comment carries any trailing free-form text from the info field.
/// </summary>
/// <param name="Latitude">Decimal-degree latitude, positive north, range [-90, 90].</param>
/// <param name="Longitude">Decimal-degree longitude, positive east, range [-180, 180].</param>
/// <param name="SymbolTable">
/// Symbol table identifier: <c>/</c> = primary, <c>\</c> = alternate, or
/// an overlay character (<c>0–9</c>, <c>A–Z</c>) for the alternate table
/// with a single-character overlay per APRS12c §20.
/// </param>
/// <param name="SymbolCode">
/// Symbol code byte from APRS12c §20's table — e.g. <c>&gt;</c> for car,
/// <c>b</c> for bicycle, <c>_</c> for weather station.
/// </param>
/// <param name="Comment">
/// Free-form trailing text after the position fields. May be empty.
/// </param>
/// <param name="Format">
/// Which of the two on-wire formats the report used: uncompressed
/// (20-byte fixed fields) or compressed (base-91, 13 bytes).
/// </param>
public readonly record struct AprsPosition(
    double Latitude,
    double Longitude,
    char SymbolTable,
    char SymbolCode,
    string Comment,
    AprsPositionFormat Format);

/// <summary>
/// Which on-wire encoding produced an <see cref="AprsPosition"/>.
/// </summary>
public enum AprsPositionFormat
{
    /// <summary>Uncompressed DDMM.mmN / DDDMM.mmW fixed-width form per APRS12c §8.</summary>
    Uncompressed,

    /// <summary>Base-91 compressed form per APRS12c §9 (13 bytes).</summary>
    Compressed,
}
