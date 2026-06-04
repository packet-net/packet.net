using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// One destination entry inside a NET/ROM NODES broadcast — a 21-octet record
/// advertising "I (the broadcasting node) can reach <see cref="Destination"/>
/// (alias <see cref="DestinationAlias"/>) via <see cref="BestNeighbour"/> at
/// quality <see cref="BestQuality"/>."
/// </summary>
/// <remarks>
/// <para>Layout (canonical NET/ROM appendix), 21 octets:</para>
/// <code>
///   [7] destination callsign  (AX.25 shifted form)
///   [6] destination alias     (plain ASCII, space-padded, no SSID)
///   [7] best-neighbour callsign (AX.25 shifted form)
///   [1] best quality          (0 worst … 255 best)
/// </code>
/// <para>
/// This is the <em>advertised</em> quality as the originator sees it. The
/// receiving node combines it multiplicatively with its own path quality to the
/// originator to derive the route quality it stores — see
/// <c>Packet.NetRom.Routing.NetRomQuality</c>.
/// </para>
/// </remarks>
public sealed record NodesRoutingEntry
{
    /// <summary>Octets one entry occupies on the wire.</summary>
    public const int EncodedLength =
        NetRomCallsign.ShiftedLength    // 7  destination callsign
        + NetRomCallsign.AliasLength    // 6  destination alias
        + NetRomCallsign.ShiftedLength  // 7  best-neighbour callsign
        + 1;                            // 1  best quality
    // = 21

    /// <summary>The destination node this entry advertises a route to.</summary>
    public required Callsign Destination { get; init; }

    /// <summary>The destination node's alias / mnemonic (may be empty).</summary>
    public required string DestinationAlias { get; init; }

    /// <summary>
    /// The neighbour the originator forwards through to reach
    /// <see cref="Destination"/> — the originator's own chosen best next hop.
    /// </summary>
    public required Callsign BestNeighbour { get; init; }

    /// <summary>The originator's quality for this route (0 worst … 255 best).</summary>
    public required byte BestQuality { get; init; }

    /// <summary>
    /// Decode one 21-octet entry. Returns <c>false</c> if the span is too short or
    /// either callsign field fails to decode.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> source, out NodesRoutingEntry? entry)
    {
        entry = null;
        if (source.Length < EncodedLength)
        {
            return false;
        }

        int offset = 0;
        if (!NetRomCallsign.TryReadShifted(source[offset..], out var dest))
        {
            return false;
        }
        offset += NetRomCallsign.ShiftedLength;

        string destAlias = NetRomCallsign.ReadAlias(source[offset..]);
        offset += NetRomCallsign.AliasLength;

        if (!NetRomCallsign.TryReadShifted(source[offset..], out var neighbour))
        {
            return false;
        }
        offset += NetRomCallsign.ShiftedLength;

        byte quality = source[offset];

        entry = new NodesRoutingEntry
        {
            Destination = dest,
            DestinationAlias = destAlias,
            BestNeighbour = neighbour,
            BestQuality = quality,
        };
        return true;
    }
}
