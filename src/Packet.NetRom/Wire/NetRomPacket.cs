using System.Diagnostics.CodeAnalysis;

namespace Packet.NetRom.Wire;

/// <summary>
/// A complete NET/ROM L3 datagram as carried in one connected-mode AX.25
/// interlink I-frame (PID 0xCF): a 15-octet <see cref="NetRomNetworkHeader"/>, a
/// 5-octet <see cref="NetRomTransportHeader"/>, and the transport payload (0..236
/// octets — empty for the control opcodes; user data for Information).
/// </summary>
/// <remarks>
/// <para>
/// This is the unit a node sends/receives/forwards: the AX.25 layer delivers the
/// I-frame's information field, this parses it into header + header + payload, and
/// the circuit layer acts on it. The repo's own BPQ corpus observed PID-0xCF
/// I-frames "always exactly 20 B" — that is exactly this with an empty payload
/// (15 + 5).
/// </para>
/// <para>
/// Parsing is total: arbitrary bytes never throw, they return <c>false</c>.
/// </para>
/// </remarks>
public sealed record NetRomPacket
{
    /// <summary>Maximum transport payload (user data) one datagram carries — the
    /// canonical 256-octet NET/ROM frame minus the 20 octets of L3+L4 header. A
    /// larger logical frame is fragmented across several Information datagrams via
    /// the more-follows flag.</summary>
    public const int MaxPayload = 236;

    /// <summary>Octets of fixed header (network + transport) every datagram carries.</summary>
    public const int HeaderLength = NetRomNetworkHeader.EncodedLength + NetRomTransportHeader.EncodedLength; // 20

    /// <summary>The L3 network header (origin/destination nodes + TTL).</summary>
    public required NetRomNetworkHeader Network { get; init; }

    /// <summary>The L4 transport header (circuit + sequencing + opcode/flags).</summary>
    public required NetRomTransportHeader Transport { get; init; }

    /// <summary>The transport payload (0..236 octets). Empty for control opcodes.</summary>
    public ReadOnlyMemory<byte> Payload { get; init; } = ReadOnlyMemory<byte>.Empty;

    /// <summary>Encode this datagram into the bytes to hand to the AX.25 interlink
    /// (the I-frame information field, sent with PID 0xCF).</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[HeaderLength + Payload.Length];
        Network.Write(buf);
        Transport.Write(buf.AsSpan(NetRomNetworkHeader.EncodedLength));
        Payload.Span.CopyTo(buf.AsSpan(HeaderLength));
        return buf;
    }

    /// <summary>
    /// Try to decode a NET/ROM datagram from an interlink I-frame's information
    /// field. Returns <c>false</c> (never throws) if the field is shorter than the
    /// 20-octet fixed header or either header fails to decode. A payload longer
    /// than <see cref="MaxPayload"/> still parses (the circuit layer decides what
    /// to do with an over-long fragment — being total here keeps a malformed peer
    /// from sinking the parser).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, [NotNullWhen(true)] out NetRomPacket? packet)
    {
        packet = null;
        if (info.Length < HeaderLength)
        {
            return false;
        }

        if (!NetRomNetworkHeader.TryParse(info, out var network))
        {
            return false;
        }
        if (!NetRomTransportHeader.TryParse(info[NetRomNetworkHeader.EncodedLength..], out var transport))
        {
            return false;
        }

        packet = new NetRomPacket
        {
            Network = network!,
            Transport = transport!,
            Payload = info[HeaderLength..].ToArray(),
        };
        return true;
    }
}
