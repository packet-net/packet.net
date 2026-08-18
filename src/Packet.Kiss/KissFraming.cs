namespace Packet.Kiss;

/// <summary>
/// KISS framing constants - the SLIP-style escape bytes that delimit and
/// transparently encode KISS frames on a serial / TCP stream.
/// </summary>
/// <remarks>
/// See "KISS TNC Protocol" (https://github.com/packethacking/ax25spec/blob/main/doc/kiss-tnc-protocol.md).
/// </remarks>
public static class KissFraming
{
    /// <summary>Frame End delimiter.</summary>
    public const byte Fend = 0xC0;

    /// <summary>Frame Escape - enter escape mode.</summary>
    public const byte Fesc = 0xDB;

    /// <summary>Transposed Frame End - escaped form of FEND.</summary>
    public const byte Tfend = 0xDC;

    /// <summary>Transposed Frame Escape - escaped form of FESC.</summary>
    public const byte Tfesc = 0xDD;

    /// <summary>
    /// The Exit-KISS-mode command. Sent as a single byte 0xFF (no FEND
    /// framing required for this command on most TNCs).
    /// </summary>
    public const byte ExitKissMode = 0xFF;
}
