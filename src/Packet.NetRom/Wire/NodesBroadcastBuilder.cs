using Packet.Core;

namespace Packet.NetRom.Wire;

/// <summary>
/// Builds the information field(s) of one or more NET/ROM NODES routing
/// broadcasts — the L3-origination counterpart to <see cref="NodesBroadcast"/>'s
/// parser. The caller transmits each returned byte array as a UI frame (PID 0xCF,
/// AX.25 destination the literal text callsign <c>NODES</c>).
/// </summary>
/// <remarks>
/// <para>
/// The wire format is the canonical one the parser already documents: a 0xFF
/// signature, the sender's 6-octet alias, then the destination entries packed
/// <see cref="NodesBroadcast.MaxEntriesPerFrame"/> (11) per frame. A routing table
/// with more than 11 advertisable destinations is dumped across several frames,
/// each a self-contained broadcast (the receiver merges them by destination, so
/// frame boundaries don't matter).
/// </para>
/// <para>
/// Construction stays strict (we never emit a frame that violates the canonical
/// format — the outbound path is always spec-faithful, per CLAUDE.md), even
/// though the parser tolerates real-world divergences inbound.
/// </para>
/// </remarks>
public static class NodesBroadcastBuilder
{
    private const int HeaderLength = 1 + NetRomCallsign.AliasLength;   // 0xFF + 6-byte alias

    /// <summary>
    /// One destination entry to advertise: the destination node + its alias, the
    /// best-neighbour we forward through, and the quality we advertise for it.
    /// </summary>
    /// <param name="Destination">The destination node's callsign.</param>
    /// <param name="DestinationAlias">The destination node's alias / mnemonic (may be empty).</param>
    /// <param name="BestNeighbour">The neighbour we forward through to reach it.</param>
    /// <param name="Quality">The quality to advertise (0..255).</param>
    public readonly record struct Entry(Callsign Destination, string DestinationAlias, Callsign BestNeighbour, byte Quality);

    /// <summary>
    /// Build the NODES broadcast frames advertising <paramref name="entries"/> from
    /// a node whose alias is <paramref name="senderAlias"/>. Returns one info-field
    /// byte array per UI frame (entries chunked 11 per frame). An empty
    /// <paramref name="entries"/> yields a single header-only frame (the node
    /// announcing its presence with nothing to advertise yet).
    /// </summary>
    /// <param name="senderAlias">The broadcasting node's alias / mnemonic.</param>
    /// <param name="entries">The destinations to advertise, best-first within the table.</param>
    /// <returns>One info field per UI frame to transmit.</returns>
    public static IReadOnlyList<byte[]> Build(string senderAlias, IReadOnlyList<Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(senderAlias);
        ArgumentNullException.ThrowIfNull(entries);

        var frames = new List<byte[]>();

        // Header-only broadcast when there's nothing to advertise — a node still
        // announces itself. (The receiver creates a neighbour entry for us from
        // the UI frame's source callsign regardless of the entry list.)
        int frameCount = entries.Count == 0
            ? 1
            : (entries.Count + NodesBroadcast.MaxEntriesPerFrame - 1) / NodesBroadcast.MaxEntriesPerFrame;

        for (int frame = 0; frame < frameCount; frame++)
        {
            int start = frame * NodesBroadcast.MaxEntriesPerFrame;
            int take = Math.Min(NodesBroadcast.MaxEntriesPerFrame, entries.Count - start);
            if (take < 0)
            {
                take = 0;
            }

            var buf = new byte[HeaderLength + (take * NodesRoutingEntry.EncodedLength)];
            buf[0] = NodesBroadcast.Signature;
            NetRomCallsign.WriteAlias(senderAlias, buf.AsSpan(1));

            int offset = HeaderLength;
            for (int i = 0; i < take; i++)
            {
                var e = entries[start + i];
                var span = buf.AsSpan(offset);
                NetRomCallsign.WriteShifted(e.Destination, span);
                NetRomCallsign.WriteAlias(e.DestinationAlias, span[NetRomCallsign.ShiftedLength..]);
                NetRomCallsign.WriteShifted(e.BestNeighbour, span[(NetRomCallsign.ShiftedLength + NetRomCallsign.AliasLength)..]);
                buf[offset + NodesRoutingEntry.EncodedLength - 1] = e.Quality;
                offset += NodesRoutingEntry.EncodedLength;
            }

            frames.Add(buf);
        }

        return frames;
    }
}
