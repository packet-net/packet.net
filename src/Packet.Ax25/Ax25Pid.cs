namespace Packet.Ax25;

/// <summary>
/// The layer-3 protocol identifiers of AX.25 v2.2 §3.4 and what to call them.
/// </summary>
public static class Ax25Pid
{
    /// <summary>No layer 3: the information field is the application's own bytes (0xF0).</summary>
    public const byte NoLayer3 = Ax25Frame.PidNoLayer3;

    /// <summary>NET/ROM (0xCF).</summary>
    public const byte NetRom = Ax25Frame.PidNetRom;

    /// <summary>A segment of a larger frame, §3.4.1 (0x08).</summary>
    public const byte Segment = Ax25Frame.PidSegmented;

    /// <summary>ISO 8208 / CCITT X.25 PLP (0x01).</summary>
    public const byte X25Plp = 0x01;

    /// <summary>Compressed TCP/IP, RFC 1144 (0x06).</summary>
    public const byte CompressedTcpIp = 0x06;

    /// <summary>Uncompressed TCP/IP, RFC 1144 (0x07).</summary>
    public const byte UncompressedTcpIp = 0x07;

    /// <summary>TEXNET datagram (0xC3).</summary>
    public const byte Texnet = 0xC3;

    /// <summary>Link Quality Protocol (0xC4).</summary>
    public const byte LinkQuality = 0xC4;

    /// <summary>AppleTalk (0xCA).</summary>
    public const byte AppleTalk = 0xCA;

    /// <summary>AppleTalk ARP (0xCB).</summary>
    public const byte AppleTalkArp = 0xCB;

    /// <summary>ARPA Internet Protocol (0xCC).</summary>
    public const byte Ip = 0xCC;

    /// <summary>ARPA Address Resolution (0xCD).</summary>
    public const byte Arp = 0xCD;

    /// <summary>FlexNet (0xCE).</summary>
    public const byte FlexNet = 0xCE;

    /// <summary>Escape: the next octet holds the PID (0xFF).</summary>
    public const byte Escape = 0xFF;

    /// <summary>
    /// A short name for a PID, as a monitor would print it: "NET/ROM", "IP", "no layer 3".
    /// Unassigned values come back as their hex, "0x42"; the two reserved ranges of §3.4
    /// (bits 5-4 set to 01 or 10) as "layer 3 (0x..)" since the spec says only that some
    /// layer 3 is present.
    /// </summary>
    public static string Name(byte pid) => pid switch
    {
        NoLayer3 => "no layer 3",
        NetRom => "NET/ROM",
        Segment => "segment",
        X25Plp => "X.25 PLP",
        CompressedTcpIp => "TCP/IP (compressed)",
        UncompressedTcpIp => "TCP/IP",
        Texnet => "TEXNET",
        LinkQuality => "link quality",
        AppleTalk => "AppleTalk",
        AppleTalkArp => "AppleTalk ARP",
        Ip => "IP",
        Arp => "ARP",
        FlexNet => "FlexNet",
        Escape => "escape",
        _ when (pid & 0x30) is 0x10 or 0x20 => $"layer 3 (0x{pid:X2})",
        _ => $"0x{pid:X2}",
    };
}
