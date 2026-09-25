namespace Packet.Aprs;

/// <summary>How a <c>!DAO!</c> extension adds precision (APRS12c §5 Precision and Datum Option).</summary>
public enum AprsDaoPrecision
{
    /// <summary>Datum only; the A and O bytes are spaces and add no precision.</summary>
    None,

    /// <summary>Upper-case datum, one extra decimal digit of minutes each for latitude and longitude
    /// (thousandths of a minute, about 2 m).</summary>
    Thousandths,

    /// <summary>Lower-case datum, one base-91 character each: about two more decimal digits of
    /// minutes (about 20 cm).</summary>
    Base91,
}

/// <summary>
/// The <c>!DAO!</c> precision-and-datum extension (APRS12c §5). The extra precision is already
/// folded into <see cref="AprsPosition"/>; this records the datum and which precision form was
/// used, so the encoder can write it back.
/// </summary>
/// <param name="Datum">The datum identifier: <c>W</c> WGS84, <c>N</c> NAD27, <c>O</c> OSGB36, other
/// letters for other predefined datums, or <c>0</c>-<c>9</c> for locally defined ones. Held in
/// upper case; the precision form decides the case written on air.</param>
/// <param name="Precision">Which precision form is used.</param>
public readonly record struct AprsDao(char Datum, AprsDaoPrecision Precision)
{
    /// <summary>WGS84 with base-91 precision, the most common form (e.g. <c>!wAb!</c>).</summary>
    public static AprsDao Wgs84Base91 => new('W', AprsDaoPrecision.Base91);

    internal void Validate(string paramName)
    {
        if (Datum is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9')) || !Enum.IsDefined(Precision))
        {
            throw new ArgumentOutOfRangeException(paramName, this, "DAO datum must be A-Z or 0-9");
        }

        if (Precision != AprsDaoPrecision.None && Datum is >= '0' and <= '9')
        {
            throw new ArgumentException("a numeric (local) datum cannot signal a precision form; use AprsDaoPrecision.None", paramName);
        }
    }
}
