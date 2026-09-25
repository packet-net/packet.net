namespace Packet.Aprs;

/// <summary>Whether a compressed position's fix was current (APRS12c §9 Compression Type byte, bit 5).</summary>
public enum AprsGpsFix
{
    /// <summary>Old (last known) fix.</summary>
    Old = 0,

    /// <summary>Current fix.</summary>
    Current = 1,
}

/// <summary>The NMEA sentence a compressed position came from (T byte bits 3-4).</summary>
public enum AprsNmeaSource
{
    /// <summary>Other or unknown.</summary>
    Other = 0,

    /// <summary>GLL sentence.</summary>
    Gll = 1,

    /// <summary>GGA sentence. With this source the <c>cs</c> bytes carry altitude.</summary>
    Gga = 2,

    /// <summary>RMC sentence.</summary>
    Rmc = 3,
}

/// <summary>What performed the compression (T byte bits 0-2).</summary>
public enum AprsCompressionOrigin
{
    /// <summary>Compressed.</summary>
    Compressed = 0,

    /// <summary>TNC beacon text.</summary>
    TncBeaconText = 1,

    /// <summary>Software (APRSdos, MacAPRS, WinAPRS, APRS+SA).</summary>
    Software = 2,

    /// <summary>Reserved.</summary>
    Reserved3 = 3,

    /// <summary>KPC-3.</summary>
    Kpc3 = 4,

    /// <summary>Pico.</summary>
    Pico = 5,

    /// <summary>Other tracker.</summary>
    OtherTracker = 6,

    /// <summary>Digipeater conversion.</summary>
    DigipeaterConversion = 7,
}

/// <summary>
/// The compression type (<c>T</c>) byte of a compressed position (APRS12c §9). Only present when
/// the compressed <c>cs</c> bytes carry data (the <c>c</c> byte is not a space).
/// </summary>
/// <param name="Fix">Old or current fix.</param>
/// <param name="Source">The NMEA sentence the data came from.</param>
/// <param name="Origin">What compressed it.</param>
public readonly record struct AprsCompressionType(AprsGpsFix Fix, AprsNmeaSource Source, AprsCompressionOrigin Origin)
{
    /// <summary>
    /// Current fix, other source, compressed by software: what the encoder writes when the cs bytes
    /// carry data but <see cref="AprsPositionedData.CompressionType"/> is not set.
    /// </summary>
    public static AprsCompressionType Default => new(AprsGpsFix.Current, AprsNmeaSource.Other, AprsCompressionOrigin.Software);

    internal int Bits => ((int)Fix << 5) | ((int)Source << 3) | (int)Origin;

    internal static AprsCompressionType FromBits(int bits) =>
        new((AprsGpsFix)((bits >> 5) & 1), (AprsNmeaSource)((bits >> 3) & 3), (AprsCompressionOrigin)(bits & 7));
}
