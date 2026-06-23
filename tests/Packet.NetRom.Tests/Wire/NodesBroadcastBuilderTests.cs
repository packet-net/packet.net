using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Wire;

/// <summary>
/// Tests for the L3-origination <see cref="NodesBroadcastBuilder"/>: the bytes it
/// emits must parse back through the production <see cref="NodesBroadcast"/> parser
/// to the same entries (no hand-rolled-encoder tautology — the parser is the
/// oracle), and a table larger than 11 entries must chunk into multiple frames.
/// </summary>
public sealed class NodesBroadcastBuilderTests
{
    private static NodesBroadcastBuilder.Entry Entry(string dest, string alias, string via, byte q)
        => new(new Callsign(dest), alias, new Callsign(via), q);

    [Fact]
    public void Build_then_parse_round_trips_the_entries()
    {
        var entries = new[]
        {
            Entry("GB7SOT", "SOT", "GB7XYZ", 200),
            Entry("GB7PYB", "PYB", "GB7XYZ", 156),
        };

        var frames = NodesBroadcastBuilder.Build("RDGBPQ", entries);
        frames.Should().ContainSingle();

        NodesBroadcast.TryParse(frames[0], NetRomParseOptions.Strict, out var parsed).Should().BeTrue();
        parsed!.SenderAlias.Should().Be("RDGBPQ");
        parsed.Entries.Should().HaveCount(2);
        parsed.Entries[0].Destination.Should().Be(new Callsign("GB7SOT"));
        parsed.Entries[0].DestinationAlias.Should().Be("SOT");
        parsed.Entries[0].BestNeighbour.Should().Be(new Callsign("GB7XYZ"));
        parsed.Entries[0].BestQuality.Should().Be((byte)200);
        parsed.Entries[1].Destination.Should().Be(new Callsign("GB7PYB"));
        parsed.Entries[1].BestQuality.Should().Be((byte)156);
    }

    [Fact]
    public void Empty_table_emits_a_single_header_only_frame()
    {
        var frames = NodesBroadcastBuilder.Build("RDGBPQ", []);
        frames.Should().ContainSingle();
        frames[0].Length.Should().Be(7, "0xFF signature + 6-byte alias, no entries");

        // The lenient parser accepts a header-only broadcast (a node announcing itself).
        NodesBroadcast.TryParse(frames[0], out var parsed).Should().BeTrue();
        parsed!.SenderAlias.Should().Be("RDGBPQ");
        parsed.Entries.Should().BeEmpty();
    }

    [Fact]
    public void A_table_over_11_entries_chunks_into_multiple_frames()
    {
        // 25 destinations → 11 + 11 + 3 = 3 frames.
        var entries = Enumerable.Range(0, 25)
            .Select(i => Entry($"GB7N{i:D2}", $"N{i:D2}", "GB7HUB", (byte)(200 - i)))
            .ToArray();

        var frames = NodesBroadcastBuilder.Build("HUBBPQ", entries);
        frames.Should().HaveCount(3);

        var reassembled = new List<NodesRoutingEntry>();
        foreach (var frame in frames)
        {
            NodesBroadcast.TryParse(frame, NetRomParseOptions.Strict, out var parsed).Should().BeTrue();
            parsed!.Entries.Count.Should().BeLessThanOrEqualTo(NodesBroadcast.MaxEntriesPerFrame);
            reassembled.AddRange(parsed.Entries);
        }

        reassembled.Should().HaveCount(25);
        reassembled.Select(e => e.Destination).Should().Equal(entries.Select(e => e.Destination));
    }

    [Fact]
    public void Built_frames_carry_the_signature_and_are_dest_NODES_ready()
    {
        var frames = NodesBroadcastBuilder.Build("RDGBPQ", [Entry("GB7SOT", "SOT", "GB7XYZ", 200)]);
        frames[0][0].Should().Be(NodesBroadcast.Signature);
        // 0xFF + 6 alias + one 21-byte entry.
        frames[0].Length.Should().Be(7 + 21);
    }

    // ─── NODESPACLEN: per-port frame-size cap (segmentation) ───

    [Fact]
    public void A_null_paclen_segments_exactly_as_the_unlimited_default()
    {
        // 5 entries — well under the structural 11-per-frame cap — must yield ONE frame,
        // byte-identical to the parameterless overload. The default-off guarantee.
        var entries = Enumerable.Range(0, 5)
            .Select(i => Entry($"GB7N{i:D2}", $"N{i:D2}", "GB7HUB", (byte)(200 - i)))
            .ToArray();

        var unlimited = NodesBroadcastBuilder.Build("HUBBPQ", entries);
        var nullCap = NodesBroadcastBuilder.Build("HUBBPQ", entries, maxFrameBytes: null);

        nullCap.Should().ContainSingle();
        nullCap.Should().HaveCount(unlimited.Count);
        nullCap[0].Should().Equal(unlimited[0]);
    }

    [Fact]
    public void A_paclen_cap_fragments_a_large_table_into_more_smaller_frames()
    {
        // 5 entries that would otherwise ride in one frame. NODESPACLEN=50 admits
        // (50 - 7 header) / 21 per-entry = 2 whole entries per frame → 2 + 2 + 1 = 3 frames,
        // each ≤ 50 octets, every entry preserved in order.
        var entries = Enumerable.Range(0, 5)
            .Select(i => Entry($"GB7N{i:D2}", $"N{i:D2}", "GB7HUB", (byte)(200 - i)))
            .ToArray();

        var frames = NodesBroadcastBuilder.Build("HUBBPQ", entries, maxFrameBytes: 50);
        frames.Should().HaveCount(3);

        var reassembled = new List<NodesRoutingEntry>();
        foreach (var frame in frames)
        {
            frame.Length.Should().BeLessThanOrEqualTo(50, "each frame honours the NODESPACLEN cap");
            frame[0].Should().Be(NodesBroadcast.Signature, "each fragment is a self-contained broadcast");
            NodesBroadcast.TryParse(frame, NetRomParseOptions.Strict, out var parsed).Should().BeTrue();
            parsed!.SenderAlias.Should().Be("HUBBPQ", "every fragment repeats the full header");
            parsed.Entries.Count.Should().BeLessThanOrEqualTo(2);
            reassembled.AddRange(parsed.Entries);
        }

        reassembled.Should().HaveCount(5);
        reassembled.Select(e => e.Destination).Should().Equal(entries.Select(e => e.Destination));
    }

    [Fact]
    public void GB7RDGs_nodespaclen_160_caps_each_frame_at_seven_entries()
    {
        // GB7RDG's bpq32.cfg NODESPACLEN=160. (160 - 7 header) / 21 per-entry = 7 entries
        // per frame. 20 entries → 7 + 7 + 6 = 3 frames, each ≤ 160 octets.
        var entries = Enumerable.Range(0, 20)
            .Select(i => Entry($"GB7N{i:D2}", $"N{i:D2}", "GB7HUB", (byte)(200 - i)))
            .ToArray();

        var frames = NodesBroadcastBuilder.Build("RDGBPQ", entries, maxFrameBytes: 160);
        frames.Should().HaveCount(3);
        frames.Select(f => f.Length).Should().AllSatisfy(len => len.Should().BeLessThanOrEqualTo(160));
        // 7 + 7 + 6 split: 7*21 + 7 = 154 octets for a full frame.
        frames[0].Length.Should().Be(7 + (7 * 21));
        frames[2].Length.Should().Be(7 + (6 * 21));
    }

    [Fact]
    public void A_paclen_too_small_for_a_whole_entry_still_emits_one_entry_per_frame()
    {
        // A cap below 28 (header 7 + one 21-octet entry) can't fit a whole entry, but we never
        // drop an entry on the floor: it degrades to one entry per frame (a best-effort soft cap).
        var entries = new[]
        {
            Entry("GB7SOT", "SOT", "GB7XYZ", 200),
            Entry("GB7PYB", "PYB", "GB7XYZ", 156),
        };

        var frames = NodesBroadcastBuilder.Build("RDGBPQ", entries, maxFrameBytes: 10);
        frames.Should().HaveCount(2, "one entry per frame when the cap is below a whole entry");
        foreach (var frame in frames)
        {
            NodesBroadcast.TryParse(frame, NetRomParseOptions.Strict, out var parsed).Should().BeTrue();
            parsed!.Entries.Should().ContainSingle();
        }
    }

    [Fact]
    public void An_empty_table_with_a_paclen_cap_still_emits_one_header_only_frame()
    {
        var frames = NodesBroadcastBuilder.Build("RDGBPQ", [], maxFrameBytes: 160);
        frames.Should().ContainSingle();
        frames[0].Length.Should().Be(7);
    }
}
