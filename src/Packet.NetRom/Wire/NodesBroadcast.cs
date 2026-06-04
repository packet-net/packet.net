using System.Diagnostics.CodeAnalysis;

namespace Packet.NetRom.Wire;

/// <summary>
/// A parsed NET/ROM NODES routing broadcast — the L3 content carried in the
/// information field of a UI frame (PID 0xCF, AX.25 destination the literal text
/// callsign <c>NODES</c>).
/// </summary>
/// <remarks>
/// <para>Information-field layout (canonical NET/ROM appendix):</para>
/// <code>
///   [1]  0xFF signature byte
///   [6]  sender's alias / mnemonic (plain ASCII, space-padded)
///   then up to 11 × 21-octet destination entries (NodesRoutingEntry)
/// </code>
/// <para>
/// A node's full routing table is dumped across as many UI frames as needed,
/// each frame carrying ≤ 11 entries. This type models <em>one</em> such frame's
/// content; a multi-frame dump produces several <see cref="NodesBroadcast"/>
/// instances, all merged into the routing table independently (the table keys on
/// destination, so frame boundaries don't matter to the merge).
/// </para>
/// <para>
/// Parsing is read-only and total: arbitrary bytes never throw, they return
/// <c>false</c>. Divergence tolerance (trailing partial entry, empty list) is
/// gated by <see cref="NetRomParseOptions"/> — strict by default at the byte
/// boundary, lenient on the parameterless overload used for promiscuous ingest.
/// </para>
/// </remarks>
public sealed record NodesBroadcast
{
    /// <summary>The NET/ROM NODES-broadcast signature byte that opens the info field.</summary>
    public const byte Signature = 0xFF;

    /// <summary>The literal AX.25 destination callsign a NODES broadcast is addressed to.</summary>
    public const string NodesDestination = "NODES";

    /// <summary>Maximum destination entries the canonical format packs into one UI frame.</summary>
    public const int MaxEntriesPerFrame = 11;

    private const int HeaderLength = 1 + NetRomCallsign.AliasLength;   // signature + 6-byte alias = 7

    /// <summary>The broadcasting node's alias / mnemonic (may be empty).</summary>
    public required string SenderAlias { get; init; }

    /// <summary>The destination entries carried in this frame (0..11).</summary>
    public required IReadOnlyList<NodesRoutingEntry> Entries { get; init; }

    /// <summary>
    /// Try to parse a NODES broadcast from a UI frame's information field, using
    /// lenient options (the promiscuous-ingest default — see remarks on
    /// <see cref="NetRomParseOptions.Lenient"/>).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, [NotNullWhen(true)] out NodesBroadcast? broadcast)
        => TryParse(info, NetRomParseOptions.Lenient, out broadcast);

    /// <summary>
    /// Try to parse a NODES broadcast from a UI frame's information field,
    /// applying <paramref name="options"/> for the strict-vs-lenient divergence
    /// choices. Returns <c>false</c> (never throws) on any malformed input.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> info, NetRomParseOptions options, [NotNullWhen(true)] out NodesBroadcast? broadcast)
    {
        ArgumentNullException.ThrowIfNull(options);
        broadcast = null;

        // Need at least the signature + 6-byte alias.
        if (info.Length < HeaderLength)
        {
            return false;
        }

        // Signature byte gates the whole frame — a non-0xFF first octet means
        // this is not a NODES broadcast (the canonical "wrong signature → ignore"
        // heuristic).
        if (info[0] != Signature)
        {
            return false;
        }

        string senderAlias = NetRomCallsign.ReadAlias(info[1..]);

        var body = info[HeaderLength..];
        int entryCount = body.Length / NodesRoutingEntry.EncodedLength;
        int remainder = body.Length - (entryCount * NodesRoutingEntry.EncodedLength);

        // A non-zero remainder means the routing region isn't a whole number of
        // 21-byte entries — either trailing pad / a clipped frame, or a malformed
        // dump. Strict rejects; lenient keeps the whole entries it can read.
        if (remainder != 0 && !options.AllowTrailingPartialEntry)
        {
            return false;
        }

        // Cap at the canonical 11-per-frame: a frame claiming more than that is
        // out of spec, so we ignore the surplus rather than trust it.
        int take = Math.Min(entryCount, MaxEntriesPerFrame);

        if (take == 0 && !options.AllowEmptyDestinationList)
        {
            return false;
        }

        var entries = new List<NodesRoutingEntry>(take);
        int offset = 0;
        for (int i = 0; i < take; i++)
        {
            if (!NodesRoutingEntry.TryParse(body.Slice(offset, NodesRoutingEntry.EncodedLength), out var entry))
            {
                // A single undecodable entry shouldn't sink the frame under
                // lenient ingest — skip it and keep parsing the rest. Under
                // strict, a bad entry is a malformed broadcast.
                if (!options.AllowTrailingPartialEntry)
                {
                    return false;
                }
                offset += NodesRoutingEntry.EncodedLength;
                continue;
            }
            entries.Add(entry!);
            offset += NodesRoutingEntry.EncodedLength;
        }

        broadcast = new NodesBroadcast
        {
            SenderAlias = senderAlias,
            Entries = entries,
        };
        return true;
    }
}
