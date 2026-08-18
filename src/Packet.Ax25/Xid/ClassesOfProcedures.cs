namespace Packet.Ax25.Xid;

/// <summary>
/// The XID "Classes of Procedures" parameter (PI=2, PL=2 - a 16-bit field),
/// per AX.25 v2.2 §4.3.3.7 (Figure 4.5) and the negotiation rules in §6.3.2.
/// For AX.25 only the duplex selection is negotiable; the remaining bits are
/// fixed.
/// </summary>
/// <remarks>
/// <para>Bit layout (LSB-first within each octet; octet 0 transmitted first):</para>
/// <list type="bullet">
/// <item>bit 0 - Balanced ABM: always 1.</item>
/// <item>bits 1-4 - Unbalanced NRM/ARM primary/secondary: always 0.</item>
/// <item>bit 5 - Half Duplex.</item>
/// <item>bit 6 - Full Duplex. Exactly one of bit 5 / bit 6 is set.</item>
/// <item>bits 7-15 - Reserved: always 0.</item>
/// </list>
/// <para>
/// Note the spec prose (§6.3.2 ¶1080) talks of "bit 0 always 1" but Figure 4.6
/// encodes PV 0x22 0x00 = ABM(bit1)+half-duplex(bit5). The discrepancy is the
/// spec's off-by-one in its prose bit-numbering vs the Figure 4.5 table /
/// Figure 4.6 bytes; we follow the table + worked example: <c>ABM</c> is the
/// low bit (0x01) and half-duplex is 0x20, which is what reproduces 0x22.
/// </para>
/// </remarks>
public sealed record ClassesOfProcedures
{
    /// <summary>Balanced ABM - bit 0, always set for AX.25.</summary>
    private const int BitAbmBalanced = 0;

    /// <summary>Half-duplex operation - bit 5.</summary>
    private const int BitHalfDuplex = 5;

    /// <summary>Full-duplex operation - bit 6.</summary>
    private const int BitFullDuplex = 6;

    /// <summary>
    /// True for half-duplex, false for full-duplex. The default per §6.3.2 is
    /// half-duplex, and the negotiation "reverts to half-duplex if either TNC
    /// cannot support full-duplex".
    /// </summary>
    public bool HalfDuplex { get; init; } = true;

    /// <summary>Half-duplex Classes of Procedures (the AX.25 default).</summary>
    public static ClassesOfProcedures HalfDuplexDefault { get; } = new() { HalfDuplex = true };

    /// <summary>Full-duplex Classes of Procedures.</summary>
    public static ClassesOfProcedures FullDuplexCapable { get; } = new() { HalfDuplex = false };

    /// <summary>
    /// Encode to the 2-octet PV (octet 0 first). ABM (bit 0) is forced set;
    /// exactly one of half-duplex (bit 5) / full-duplex (bit 6) is set; all
    /// other bits are zero per the Figure 4.5 fixed values.
    /// </summary>
    public byte[] ToOctets()
    {
        int field = (1 << BitAbmBalanced) | (1 << (HalfDuplex ? BitHalfDuplex : BitFullDuplex));
        // LSB-first per octet: octet0 = bits 0-7, octet1 = bits 8-15.
        return new[] { (byte)(field & 0xFF), (byte)((field >> 8) & 0xFF) };
    }

    /// <summary>
    /// Decode from the (up to) 2-octet PV. Duplex is read from bits 5/6; if
    /// neither is set we default to half-duplex (the spec default). All other
    /// bits are ignored on receive - only the duplex selection is meaningful
    /// to AX.25.
    /// </summary>
    public static ClassesOfProcedures FromOctets(byte octet0, byte octet1)
    {
        int field = octet0 | (octet1 << 8);
        bool full = (field & (1 << BitFullDuplex)) != 0;
        bool half = (field & (1 << BitHalfDuplex)) != 0;
        // Half-duplex unless only full-duplex is asserted.
        return new ClassesOfProcedures { HalfDuplex = !(full && !half) };
    }
}
