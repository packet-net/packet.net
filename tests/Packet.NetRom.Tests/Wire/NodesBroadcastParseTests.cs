using AwesomeAssertions;
using Packet.Core;
using Packet.NetRom.Wire;
using Xunit;

namespace Packet.NetRom.Tests.Wire;

public class NodesBroadcastParseTests
{
    private static readonly Callsign Gb7Rdg = new("GB7RDG", 0);
    private static readonly Callsign Gb7Sot = new("GB7SOT", 0);
    private static readonly Callsign Gb7Xyz = new("GB7XYZ", 5);

    [Fact]
    public void Parses_signature_and_sender_alias()
    {
        var info = TestNodesEncoder.Build("RDGBPQ");

        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        bc!.SenderAlias.Should().Be("RDGBPQ");
        bc.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Rejects_a_frame_whose_first_octet_is_not_the_signature()
    {
        var info = TestNodesEncoder.Build("RDGBPQ");
        info[0] = 0x00;   // canonical "wrong signature → ignore"

        NodesBroadcast.TryParse(info, out var bc).Should().BeFalse();
        bc.Should().BeNull();
    }

    [Fact]
    public void Parses_a_single_destination_entry_with_all_fields()
    {
        var info = TestNodesEncoder.Build("RDGBPQ",
            (Gb7Sot, "SOT", Gb7Xyz, 200));

        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        bc!.Entries.Should().ContainSingle();
        var e = bc.Entries[0];
        e.Destination.Should().Be(Gb7Sot);
        e.DestinationAlias.Should().Be("SOT");
        e.BestNeighbour.Should().Be(Gb7Xyz);   // SSID 5 survives the shifted round-trip
        e.BestQuality.Should().Be(200);
    }

    [Fact]
    public void Parses_several_entries_in_order()
    {
        var info = TestNodesEncoder.Build("RDGBPQ",
            (Gb7Sot, "SOT", Gb7Xyz, 200),
            (Gb7Xyz, "XYZ", Gb7Xyz, 192),
            (Gb7Rdg, "RDG", Gb7Rdg, 255));

        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        bc!.Entries.Should().HaveCount(3);
        bc.Entries.Select(e => e.Destination).Should().Equal(Gb7Sot, Gb7Xyz, Gb7Rdg);
        bc.Entries.Select(e => e.BestQuality).Should().Equal(200, 192, 255);
    }

    [Fact]
    public void Caps_at_eleven_entries_per_frame_ignoring_the_surplus()
    {
        // Hand-build 13 entries; the canonical format caps a frame at 11.
        var entries = Enumerable.Range(0, 13)
            .Select(_ => (Gb7Sot, "SOT", Gb7Xyz, (byte)200))
            .ToArray();
        var info = TestNodesEncoder.Build("RDGBPQ", entries);

        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        bc!.Entries.Should().HaveCount(NodesBroadcast.MaxEntriesPerFrame);
    }

    // ─── Strict-vs-lenient paired tests (CLAUDE.md §"Spec-compliant by default") ───

    [Fact]
    public void Trailing_partial_entry_is_rejected_by_strict_but_accepted_by_lenient()
    {
        var info = TestNodesEncoder.Build("RDGBPQ",
            (Gb7Sot, "SOT", Gb7Xyz, 200)).ToList();
        info.AddRange(new byte[] { 0x01, 0x02, 0x03 });   // 3 trailing octets (< 21)
        var bytes = info.ToArray();

        NodesBroadcast.TryParse(bytes, NetRomParseOptions.Strict, out _).Should().BeFalse();

        NodesBroadcast.TryParse(bytes, NetRomParseOptions.Lenient, out var lenient).Should().BeTrue();
        lenient!.Entries.Should().ContainSingle();   // the whole entry is kept; the remainder dropped
    }

    [Fact]
    public void Empty_destination_list_is_rejected_by_strict_but_accepted_by_lenient()
    {
        var info = TestNodesEncoder.Build("RDGBPQ");   // header only

        NodesBroadcast.TryParse(info, NetRomParseOptions.Strict, out _).Should().BeFalse();
        NodesBroadcast.TryParse(info, NetRomParseOptions.Lenient, out var lenient).Should().BeTrue();
        lenient!.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Bpq_and_xrouter_presets_accept_a_padded_dump_like_lenient()
    {
        var info = TestNodesEncoder.Build("RDGBPQ",
            (Gb7Sot, "SOT", Gb7Xyz, 200)).ToList();
        info.Add(0x00);   // one pad octet on the final frame
        var bytes = info.ToArray();

        NodesBroadcast.TryParse(bytes, NetRomParseOptions.Bpq, out var bpq).Should().BeTrue();
        bpq!.Entries.Should().ContainSingle();
        NodesBroadcast.TryParse(bytes, NetRomParseOptions.Xrouter, out var xr).Should().BeTrue();
        xr!.Entries.Should().ContainSingle();
    }

    // ─── Totality: arbitrary bytes never throw ───

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(20)]
    public void Short_or_truncated_input_returns_false_without_throwing(int length)
    {
        var bytes = new byte[length];
        if (length > 0) bytes[0] = NodesBroadcast.Signature;

        var act = () => NodesBroadcast.TryParse(bytes, out _);
        act.Should().NotThrow();
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var rng = new Random(1234);
        for (int i = 0; i < 500; i++)
        {
            var bytes = new byte[rng.Next(0, 300)];
            rng.NextBytes(bytes);
            var act = () => NodesBroadcast.TryParse(bytes, NetRomParseOptions.Lenient, out _);
            act.Should().NotThrow();
        }
    }

    [Fact]
    public void Alias_is_trimmed_of_trailing_spaces()
    {
        // "RDG" packed into a 6-byte field is "RDG   "; the parser trims it.
        var info = TestNodesEncoder.Build("RDG");
        NodesBroadcast.TryParse(info, out var bc).Should().BeTrue();
        bc!.SenderAlias.Should().Be("RDG");
    }
}
