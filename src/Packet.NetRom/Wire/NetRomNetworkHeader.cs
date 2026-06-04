using System.Diagnostics.CodeAnalysis;
using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// The NET/ROM L3 network header — 15 octets prepended to every inter-node
/// datagram carried over a connected-mode AX.25 interlink (PID 0xCF). It names
/// the end-to-end origin and destination <em>nodes</em> (not the hop-by-hop AX.25
/// addresses, which are the interlink's own) and carries the hop-limit
/// time-to-live a forwarding node decrements.
/// </summary>
/// <remarks>
/// <para>Layout (canonical NET/ROM appendix), 15 octets:</para>
/// <code>
///   [7] origin node callsign       (AX.25 shifted form)
///   [7] destination node callsign  (AX.25 shifted form)
///   [1] time-to-live               (hop limit; decremented per node; 0 → discard)
/// </code>
/// <para>
/// The 5 octets immediately after this header are the L4
/// <see cref="NetRomTransportHeader"/>; the bytes after that are the transport
/// payload. A full L3 datagram is <see cref="NetRomPacket"/>.
/// </para>
/// </remarks>
public sealed record NetRomNetworkHeader
{
    /// <summary>Octets this header occupies on the wire.</summary>
    public const int EncodedLength =
        NetRomCallsign.ShiftedLength    // 7 origin
        + NetRomCallsign.ShiftedLength  // 7 destination
        + 1;                            // 1 TTL
    // = 15

    /// <summary>The canonical default initial time-to-live (BPQ's
    /// <c>L3TIMETOLIVE</c> default; the Linux <c>network_ttl_initialiser</c>
    /// default is also in this band). Operator-tunable via
    /// <see cref="Packet.NetRom.Transport.NetRomCircuitOptions"/>.</summary>
    public const byte DefaultTimeToLive = 25;

    /// <summary>The end-to-end origin node.</summary>
    public required Callsign Origin { get; init; }

    /// <summary>The end-to-end destination node.</summary>
    public required Callsign Destination { get; init; }

    /// <summary>Hop-limit counter; a forwarding node decrements it and discards
    /// the datagram at 0.</summary>
    public required byte TimeToLive { get; init; }

    /// <summary>
    /// A copy of this header with the time-to-live decremented by one. The caller
    /// checks the result is &gt; 0 before forwarding (a header that arrives at TTL
    /// 1 decrements to 0 and must not be forwarded). Never underflows: a TTL of 0
    /// stays 0.
    /// </summary>
    public NetRomNetworkHeader Decremented() =>
        this with { TimeToLive = TimeToLive == 0 ? (byte)0 : (byte)(TimeToLive - 1) };

    /// <summary>Encode this header into <paramref name="destination"/> (≥ <see cref="EncodedLength"/> octets).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < EncodedLength)
        {
            throw new ArgumentException($"network header needs {EncodedLength} bytes of room (got {destination.Length})", nameof(destination));
        }

        NetRomCallsign.WriteShifted(Origin, destination);
        NetRomCallsign.WriteShifted(Destination, destination[NetRomCallsign.ShiftedLength..]);
        destination[NetRomCallsign.ShiftedLength * 2] = TimeToLive;
    }

    /// <summary>Allocate and return this header's 15-octet encoding.</summary>
    public byte[] ToBytes()
    {
        var buf = new byte[EncodedLength];
        Write(buf);
        return buf;
    }

    /// <summary>
    /// Try to decode a 15-octet network header from the front of
    /// <paramref name="source"/>. Returns <c>false</c> (never throws) if the span
    /// is too short or either callsign field fails to decode.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> source, [NotNullWhen(true)] out NetRomNetworkHeader? header)
    {
        header = null;
        if (source.Length < EncodedLength)
        {
            return false;
        }

        if (!NetRomCallsign.TryReadShifted(source, out var origin))
        {
            return false;
        }
        if (!NetRomCallsign.TryReadShifted(source[NetRomCallsign.ShiftedLength..], out var destination))
        {
            return false;
        }

        header = new NetRomNetworkHeader
        {
            Origin = origin,
            Destination = destination,
            TimeToLive = source[NetRomCallsign.ShiftedLength * 2],
        };
        return true;
    }
}
