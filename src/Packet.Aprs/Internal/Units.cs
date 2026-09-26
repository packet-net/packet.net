namespace Packet.Aprs.Internal;

internal static class Units
{
    public const double MphPerKnot = 1.852 / 1.609344;
    public const double FeetPerMetre = 1 / 0.3048;

    public static double KnotsToMph(double knots) => knots * MphPerKnot;

    public static double MphToKnots(double mph) => mph / MphPerKnot;
}
