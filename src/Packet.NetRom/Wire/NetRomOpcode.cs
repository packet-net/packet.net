namespace Packet.NetRom.Wire;

/// <summary>
/// The six NET/ROM L4 (transport) message types — the low nibble of the
/// transport header's opcode-and-flags byte. The high bits of that byte are the
/// independent flags <see cref="NetRomTransportFlags"/> (choke / NAK /
/// more-follows), so always mask with <see cref="NetRomTransportHeader.OpcodeMask"/> before comparing.
/// </summary>
/// <remarks>
/// <para>
/// Values are the canonical NET/ROM appendix opcodes (the "Structure of
/// Inter-Node HDLC Frames" transport table). They are the de-facto wire numbers
/// every implementation (BPQ, XRouter, the Linux <c>netrom</c> family) agrees on
/// — unlike the routing quality maths, the opcode set does not diverge.
/// </para>
/// </remarks>
public enum NetRomOpcode : byte
{
    /// <summary>Connect Request (0x01): open a circuit. Info carries the
    /// originating-user + originating-node callsigns; the header proposes a
    /// window size.</summary>
    ConnectRequest = 0x01,

    /// <summary>Connect Acknowledge (0x02): accept (or, with the choke/bit-7 flag
    /// set, refuse) a circuit. Carries the accepted window size (≤ proposed).</summary>
    ConnectAcknowledge = 0x02,

    /// <summary>Disconnect Request (0x03): tear a circuit down.</summary>
    DisconnectRequest = 0x03,

    /// <summary>Disconnect Acknowledge (0x04): confirm a disconnect.</summary>
    DisconnectAcknowledge = 0x04,

    /// <summary>Information (0x05): up to 236 bytes of user data, piggybacking an
    /// ack via the RX-sequence field; the more-follows flag marks a fragment of a
    /// larger logical frame.</summary>
    Information = 0x05,

    /// <summary>Information Acknowledge (0x06): a standalone ack (RX sequence), and
    /// the carrier of the choke / NAK flow-control flags.</summary>
    InformationAcknowledge = 0x06,
}

/// <summary>
/// The independent flag bits packed into the high bits of the transport header's
/// opcode-and-flags byte, above the <see cref="NetRomOpcode"/> nibble.
/// </summary>
/// <remarks>
/// On a Connect Acknowledge, the choke bit (bit 7) is overloaded to mean
/// <em>refused</em> (the canonical "connection refused" encoding), since a
/// refused circuit has no flow to choke.
/// </remarks>
[Flags]
public enum NetRomTransportFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Compressed (bit 4): a <b>BPQ-specific</b> extension flag marking an
    /// Information message whose payload is a (zlib / RFC 1950) compressed stream
    /// rather than raw user data — see LinBPQ <c>L4COMP</c> in <c>asmstrucs.h</c>.
    /// Only ever set on a circuit where both ends negotiated compression at connect
    /// time (the <c>L4Compress</c> capability handshake), so a peer that did not
    /// agree never receives it. Reassemble all the <see cref="MoreFollows"/>
    /// fragments first, then inflate the concatenation as one stream.</summary>
    Compressed = 0x10,

    /// <summary>More-follows (bit 5): this Information message is a non-final
    /// fragment of a logical frame larger than one 236-byte payload.</summary>
    MoreFollows = 0x20,

    /// <summary>NAK (bit 6): request selective retransmission of the frame named
    /// by the RX-sequence field.</summary>
    Nak = 0x40,

    /// <summary>Choke (bit 7): tell the far end to stop sending Information until
    /// further notice — the flow-control backpressure signal. On a Connect
    /// Acknowledge this same bit instead means the circuit was refused.</summary>
    Choke = 0x80,
}
