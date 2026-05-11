namespace Packet.Core;

/// <summary>
/// One 7-octet address slot in an AX.25 frame header.
/// </summary>
/// <remarks>
/// <para>
/// Per AX.25 v2.2 §3.12, each address slot is 7 octets:
/// </para>
/// <list type="number">
///   <item>Octets 1–6: callsign chars, each left-shifted by 1 (so bit 0 of each byte is reserved for HDLC frame-end signalling).</item>
///   <item>Octet 7 (SSID byte): bit layout depends on whether this slot is destination, source, or repeater.</item>
/// </list>
/// <para>SSID byte layout:</para>
/// <code>
/// bit:  7    6    5    4..1       0
///       C/H  R    R    SSID(4b)   E
/// </code>
/// <list type="bullet">
///   <item><c>C/H</c>: command/response bit on destination &amp; source slots (§3.12.2 / §3.12.3); has-been-repeated bit on repeater slots (§3.12.4).</item>
///   <item><c>R</c>: reserved bits. v2.2 specifies "11" by default.</item>
///   <item><c>SSID</c>: 4-bit station identifier.</item>
///   <item><c>E</c>: end-of-address bit. Set on the LAST address octet of the whole address field.</item>
/// </list>
/// <para>
/// We model the C/H bit as <see cref="CrhBit"/> (caller knows which role this slot has).
/// Reserved bits are read back faithfully; on write we follow the v2.2 default of "11".
/// </para>
/// </remarks>
public readonly record struct Ax25Address(Callsign Callsign, bool CrhBit, bool ExtensionBit)
{
    /// <summary>Number of octets one address slot occupies on the wire.</summary>
    public const int EncodedLength = 7;

    /// <summary>Parse one 7-octet slot.</summary>
    /// <exception cref="ArgumentException">Span is too short, or the encoded callsign is malformed.</exception>
    public static Ax25Address Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < EncodedLength)
        {
            throw new ArgumentException($"address slot needs {EncodedLength} bytes (got {source.Length})", nameof(source));
        }

        // First 6 bytes: callsign chars, each in upper 7 bits (the encoded
        // value is the ASCII char shifted left by 1). Trailing spaces (0x20
        // shifted = 0x40) are padding.
        Span<char> baseChars = stackalloc char[6];
        int baseLen = 0;
        for (int i = 0; i < 6; i++)
        {
            byte b = source[i];
            char c = (char)(b >> 1);
            if (c == ' ')
            {
                // padding — but anything after a space must also be padding
                continue;
            }
            if (i > baseLen)
            {
                // We had a space then a non-space — malformed
                throw new ArgumentException($"address octet {i} contains non-space after padding", nameof(source));
            }
            baseChars[baseLen++] = c;
        }

        var ssidByte = source[6];
        byte ssid = (byte)((ssidByte >> 1) & 0x0F);
        bool crh = (ssidByte & 0x80) != 0;
        bool ext = (ssidByte & 0x01) != 0;

        return new Ax25Address(new Callsign(new string(baseChars[..baseLen]), ssid), crh, ext);
    }

    /// <summary>Encode this address slot into 7 bytes.</summary>
    /// <exception cref="ArgumentException">Span is too short.</exception>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"address slot needs {EncodedLength} bytes of room (got {destination.Length})", nameof(destination));
        }

        string @base = Callsign.Base;
        for (int i = 0; i < 6; i++)
        {
            char c = i < @base.Length ? @base[i] : ' ';
            destination[i] = (byte)(c << 1);
        }

        // SSID byte: C/H | R | R | SSID(4) | E
        // R bits default to "11" per v2.2.
        byte ssidByte = (byte)(0x60 | ((Callsign.Ssid & 0x0F) << 1));
        if (CrhBit) ssidByte |= 0x80;
        if (ExtensionBit) ssidByte |= 0x01;
        destination[6] = ssidByte;
    }
}
