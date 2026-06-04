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
}
