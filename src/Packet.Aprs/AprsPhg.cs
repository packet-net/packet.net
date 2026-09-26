namespace Packet.Aprs;

/// <summary>
/// Station power, effective antenna height, gain and directivity: the <c>PHGphgd</c> data
/// extension (APRS12c §7), optionally with the APRS 1.2 beacon-rate "probe" suffix
/// <c>PHGphgdr/</c>.
/// </summary>
/// <param name="PowerCode">0-9; power in watts is the code squared.</param>
/// <param name="HeightCode">0 and up (0-9 on the ground; <c>:</c> = 10, <c>;</c> = 11 and so on for
/// balloons and aircraft); height above average terrain is 10 x 2^code feet.</param>
/// <param name="GainCode">0-9, antenna gain in dBi.</param>
/// <param name="DirectivityCode">0 for omni, 1-8 for 45-360 degrees (direction of maximum gain).</param>
/// <param name="BeaconsPerHour">The PHGR rate: beacons per hour, 1-35 (digits then A = 10), or null
/// for the plain 7-byte form.</param>
public readonly record struct AprsPhg(int PowerCode, int HeightCode, int GainCode, int DirectivityCode, int? BeaconsPerHour = null)
{
    /// <summary>Transmitter power in watts.</summary>
    public int PowerWatts => PowerCode * PowerCode;

    /// <summary>Antenna height above average terrain, in feet.</summary>
    public double HeightFeet => 10 * Math.Pow(2, HeightCode);

    /// <summary>Antenna gain in dBi.</summary>
    public int GainDbi => GainCode;

    /// <summary>Direction of maximum gain in degrees, or null for omnidirectional.</summary>
    public int? DirectivityDegrees => DirectivityCode == 0 ? null : DirectivityCode * 45;

    /// <summary>The usable radio range in miles that APRS clients draw as a circle
    /// (APRS12c §7 Range Circle Plot): sqrt(2 x height x sqrt(power/10 x gain/2)).</summary>
    public double RangeMiles
    {
        get
        {
            double gain = Math.Pow(10, GainCode / 10.0);
            return Math.Sqrt(2 * HeightFeet * Math.Sqrt(PowerWatts / 10.0 * (gain / 2)));
        }
    }

    internal void Validate(string paramName)
    {
        if (PowerCode is < 0 or > 9 || GainCode is < 0 or > 9 || DirectivityCode is < 0 or > 9 || HeightCode is < 0 or > 78)
        {
            throw new ArgumentOutOfRangeException(paramName, this, "PHG codes: power, gain and directivity 0-9; height 0 and up");
        }

        if (BeaconsPerHour is < 1 or > 35)
        {
            throw new ArgumentOutOfRangeException(paramName, BeaconsPerHour, "PHGR beacons per hour must be 1-35");
        }
    }
}

/// <summary>
/// Omni-DF signal strength: the <c>DFSshgd</c> data extension (APRS12c §7), used to locate
/// interference by overlapping signal-strength circles.
/// </summary>
/// <param name="StrengthCode">Relative signal strength, 0-9 S-points. 0 means "not heard", which is
/// just as useful as a hit.</param>
/// <param name="HeightCode">Antenna height code as for <see cref="AprsPhg.HeightCode"/>.</param>
/// <param name="GainCode">0-9, antenna gain in dB.</param>
/// <param name="DirectivityCode">0 for omni, 1-8 for 45-360 degrees.</param>
public readonly record struct AprsDfSignalStrength(int StrengthCode, int HeightCode, int GainCode, int DirectivityCode)
{
    /// <summary>Antenna height above average terrain, in feet.</summary>
    public double HeightFeet => 10 * Math.Pow(2, HeightCode);

    /// <summary>Direction of maximum gain in degrees, or null for omnidirectional.</summary>
    public int? DirectivityDegrees => DirectivityCode == 0 ? null : DirectivityCode * 45;

    internal void Validate(string paramName)
    {
        if (StrengthCode is < 0 or > 9 || GainCode is < 0 or > 9 || DirectivityCode is < 0 or > 9 || HeightCode is < 0 or > 78)
        {
            throw new ArgumentOutOfRangeException(paramName, this, "DFS codes: strength, gain and directivity 0-9; height 0 and up");
        }
    }
}

/// <summary>
/// A direction-finding bearing with its Number/Range/Quality: the <c>/BRG/NRQ</c> field that
/// follows course/speed in a DF report (APRS12c §7, §8). Only meaningful with the <c>/\</c> DF symbol.
/// </summary>
/// <param name="BearingDegrees">Bearing to the signal, 0-360.</param>
/// <param name="Number">N: 0 means the NRQ is meaningless, 1-8 is the proportion of samples that
/// got a hit (8 = 100%), 9 means a manual report.</param>
/// <param name="Range">R: range of interest is 2^R miles.</param>
/// <param name="Quality">Q: 0 useless, 1 under 240 degrees beamwidth, halving each step to 9 under 1 degree.</param>
public readonly record struct AprsDfBearing(int BearingDegrees, int Number, int Range, int Quality)
{
    /// <summary>The range of interest in miles (2^R).</summary>
    public double RangeMiles => Math.Pow(2, Range);

    /// <summary>The bearing accuracy as a beamwidth in degrees, or null for quality 0 (useless).</summary>
    public double? BeamwidthDegrees => Quality switch
    {
        0 => null,
        1 => 240,
        2 => 120,
        3 => 64,
        _ => 32 / Math.Pow(2, Quality - 4),
    };

    internal void Validate(string paramName)
    {
        if (BearingDegrees is < 0 or > 360 || Number is < 0 or > 9 || Range is < 0 or > 9 || Quality is < 0 or > 9)
        {
            throw new ArgumentOutOfRangeException(paramName, this, "bearing 0-360; N, R and Q 0-9");
        }
    }
}
