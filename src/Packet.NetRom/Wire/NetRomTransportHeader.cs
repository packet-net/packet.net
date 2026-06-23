using System.Diagnostics.CodeAnalysis;

namespace Packet.NetRom.Wire;

/// <summary>
/// The NET/ROM L4 transport header — 5 octets immediately following the
/// <see cref="NetRomNetworkHeader"/> in an inter-node datagram. It identifies the
/// circuit, carries the sliding-window sequence numbers, and names the message
/// type + flow-control flags.
/// </summary>
/// <remarks>
/// <para>Layout (canonical NET/ROM appendix), 5 octets:</para>
/// <code>
///   [1] circuit index      (slot in the far end's circuit table — "your" index)
///   [1] circuit ID         (serial qualifying the index — "your" id)
///   [1] TX sequence number (this message's send-sequence; 8-bit, mod 256)
///   [1] RX sequence number (the next send-sequence we expect; the piggybacked ack)
///   [1] opcode &amp; flags     (low nibble = NetRomOpcode; high bits = NetRomTransportFlags)
/// </code>
/// <para>
/// The index/ID pair are <em>the receiver's</em> identifiers (the values it gave
/// us in its Connect (Acknowledge)) so it can demultiplex the datagram to the
/// right circuit without parsing the callsigns — which is why on a Connect
/// Request the index/ID name the <em>sender's</em> own circuit (the receiver
/// learns them and echoes them back).
/// </para>
/// <para>
/// Several fields are overloaded per opcode (e.g. Connect Request carries the
/// proposed window size in the TX-sequence slot, Connect Acknowledge the accepted
/// window). This type models the raw 5 octets faithfully; the per-opcode meaning
/// is applied by the circuit layer.
/// </para>
/// </remarks>
public sealed record NetRomTransportHeader
{
    /// <summary>Octets this header occupies on the wire.</summary>
    public const int EncodedLength = 5;

    /// <summary>The low nibble of the opcode-and-flags byte (the message type).</summary>
    public const byte OpcodeMask = 0x0F;

    /// <summary>The high bits of the opcode-and-flags byte (the flow-control flags).</summary>
    public const byte FlagsMask = 0xF0;

    /// <summary>The far end's circuit-table slot ("your index").</summary>
    public required byte CircuitIndex { get; init; }

    /// <summary>The serial number qualifying <see cref="CircuitIndex"/> ("your id").</summary>
    public required byte CircuitId { get; init; }

    /// <summary>This message's send sequence (8-bit, wraps mod 256).</summary>
    public required byte TxSequence { get; init; }

    /// <summary>The next send sequence we expect from the peer — the piggybacked ack.</summary>
    public required byte RxSequence { get; init; }

    /// <summary>The message type (low nibble of the opcode-and-flags byte).</summary>
    public required NetRomOpcode Opcode { get; init; }

    /// <summary>The flow-control flags (high bits of the opcode-and-flags byte).</summary>
    public required NetRomTransportFlags Flags { get; init; }

    /// <summary>True if the choke flag (bit 7) is set. On a Connect Acknowledge
    /// this instead signals refusal — see <see cref="NetRomTransportFlags.Choke"/>.</summary>
    public bool Choke => (Flags & NetRomTransportFlags.Choke) != 0;

    /// <summary>True if the NAK flag (bit 6) is set (selective-retransmit request).</summary>
    public bool Nak => (Flags & NetRomTransportFlags.Nak) != 0;

    /// <summary>True if the more-follows flag (bit 5) is set (a non-final fragment).</summary>
    public bool MoreFollows => (Flags & NetRomTransportFlags.MoreFollows) != 0;

    /// <summary>True if the BPQ compressed flag (bit 4) is set — the Information
    /// payload is a zlib stream (only on a compression-negotiated circuit). See
    /// <see cref="NetRomTransportFlags.Compressed"/>.</summary>
    public bool Compressed => (Flags & NetRomTransportFlags.Compressed) != 0;

    /// <summary>The raw opcode-and-flags byte (opcode nibble OR-ed with the flag bits).</summary>
    public byte OpcodeAndFlags => (byte)(((byte)Opcode & OpcodeMask) | ((byte)Flags & FlagsMask));

    /// <summary>Encode this header into <paramref name="destination"/> (≥ <see cref="EncodedLength"/> octets).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"transport header needs {EncodedLength} bytes of room (got {destination.Length})", nameof(destination));
        }

        destination[0] = CircuitIndex;
        destination[1] = CircuitId;
        destination[2] = TxSequence;
        destination[3] = RxSequence;
        destination[4] = OpcodeAndFlags;
    }

    /// <summary>Allocate and return this header's 5-octet encoding.</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[EncodedLength];
        Write(buf);
        return buf;
    }

    /// <summary>
    /// Try to decode a 5-octet transport header from the front of
    /// <paramref name="source"/>. Returns <c>false</c> only if the span is too
    /// short — any opcode-nibble value parses (an unknown opcode is surfaced as
    /// its raw <see cref="NetRomOpcode"/> value for the circuit layer to reject).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> source, [NotNullWhen(true)] out NetRomTransportHeader? header)
    {
        header = null;
        if (source.Length < EncodedLength)
        {
            return false;
        }

        byte opByte = source[4];
        header = new NetRomTransportHeader
        {
            CircuitIndex = source[0],
            CircuitId = source[1],
            TxSequence = source[2],
            RxSequence = source[3],
            Opcode = (NetRomOpcode)(opByte & OpcodeMask),
            Flags = (NetRomTransportFlags)(opByte & FlagsMask),
        };
        return true;
    }
}
