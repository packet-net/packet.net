namespace Packet.Aprs;

/// <summary>
/// A latitude and longitude in decimal degrees (WGS84 unless a <see cref="AprsDao"/> says
/// otherwise), with the sender's position ambiguity.
/// </summary>
/// <remarks>
/// <para>
/// Position ambiguity (APRS12c §6) is how many trailing digits of the latitude minutes the
/// sender blanked: 0 none, 1 hundredths, 2 all decimals, 3 units of minutes, 4 all minutes
/// (nearest degree). An ambiguous position decodes to the centre of the range the remaining
/// digits allow; see <c>interpretations.md</c> in <c>packet-net/aprs-vectors</c>.
/// </para>
/// <para>
/// Any extra precision from a <c>!DAO!</c> extension is already applied to
/// <see cref="Latitude"/> and <see cref="Longitude"/>.
/// </para>
/// </remarks>
/// <param name="Latitude">Degrees, positive north, -90 to 90.</param>
/// <param name="Longitude">Degrees, positive east, -180 to 180.</param>
/// <param name="Ambiguity">0 to 4 blanked trailing digits (Mic-E and uncompressed formats only).</param>
public readonly record struct AprsPosition(double Latitude, double Longitude, int Ambiguity = 0)
{
    /// <summary>The "null position" 0N 0W that stations without a fix must send, with the
    /// <c>\.</c> symbol (APRS12c §6 Default Null Position).</summary>
    public static AprsPosition Null => new(0, 0);

    /// <summary>
    /// The approximate radius of uncertainty implied by <see cref="Ambiguity"/>, in nautical
    /// miles: 0.01 (full precision), 0.1, 1, 10, 60 (APRS12c §6 Ambiguity Plots).
    /// </summary>
    public double AmbiguityRadiusNauticalMiles => Ambiguity switch
    {
        0 => 0.01,
        1 => 0.1,
        2 => 1,
        3 => 10,
        _ => 60,
    };

    internal void Validate(string paramName)
    {
        if (!double.IsFinite(Latitude) || Latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(paramName, Latitude, "latitude must be between -90 and 90 degrees");
        }

        if (!double.IsFinite(Longitude) || Longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(paramName, Longitude, "longitude must be between -180 and 180 degrees");
        }

        if (Ambiguity is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(paramName, Ambiguity, "ambiguity must be 0 to 4");
        }
    }
}
