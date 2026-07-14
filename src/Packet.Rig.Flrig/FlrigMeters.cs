namespace Packet.Rig.Flrig;

/// <summary>Meter conversions for flrig's 0–100 needle-deflection replies.</summary>
internal static class FlrigMeters
{
    // flrig's get_swrmeter is a 0–100 deflection, not a ratio. This is hamlib flrig.c's
    // interpolation table (swrtbl/interpolateSWR), verbatim: piecewise-linear through
    // (deflection → SWR) anchor points, treating 100 as "infinity" ≈ 10:1, result rounded
    // to 0.1 as hamlib does so the two clients report identical values.
    private static readonly (double Meter, double Swr)[] Table =
    [
        (0.0, 1.0),
        (10.5, 1.5),
        (23.0, 2.0),
        (35.0, 2.5),
        (48.0, 3.0),
        (100.0, 10.0),
    ];

    internal static double InterpolateSwr(double meter)
    {
        for (var i = 0; i < Table.Length - 1; i++)
        {
            if (meter == Table[i].Meter)
            {
                return Table[i].Swr;
            }

            if (meter < Table[i + 1].Meter)
            {
                var slope = (Table[i + 1].Swr - Table[i].Swr) / (Table[i + 1].Meter - Table[i].Meter);

                // AwayFromZero matches C round() — .NET's default banker's rounding would
                // report e.g. 2.2 where hamlib reports 2.3.
                return Math.Round((Table[i].Swr + slope * (meter - Table[i].Meter)) * 10, MidpointRounding.AwayFromZero) / 10.0;
            }
        }

        return 10.0; // off the top of the table — call it infinite
    }
}
