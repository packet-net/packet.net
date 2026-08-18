using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Wire;

/// <summary>
/// Round-trip, spec hex-vector, recognition, and totality tests for the INP3
/// L3RTT codec (<see cref="Inp3L3RttFrame"/>). Vectors are taken verbatim from
/// <c>docs/netrom-inp3-i1-wire-spec.md</c> §1.5 and become shared cross-stack
/// golden vectors (the C# reference is authoritative; TS and Rust mirror it 1:1).
/// </summary>
public sealed class Inp3L3RttTests
{
    private static readonly Callsign M0Lte = new("M0LTE", 0);

    // From §1.5: origin M0LTE-0, dest L3RTT-0, TTL 0x19, transport 00 00 00 00 02.
    private static readonly byte[] HeaderPrefix =
    [
        0x9A, 0x60, 0x98, 0xA8, 0x8A, 0x40, 0x60,   // origin M0LTE-0
        0x98, 0x66, 0xA4, 0xA8, 0xA8, 0x40, 0x60,   // dest L3RTT-0
        0x19,                                        // TTL = 25
        0x00, 0x00, 0x00, 0x00, 0x02,               // transport: opcode 0x02, no flags
    ];

    // Vector L3RTT-A - probe advertising plain INP3 ("$N      "), length 28.
    private static readonly byte[] VectorA =
    [
        .. HeaderPrefix,
        0x24, 0x4E, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20,   // "$N" + 6 spaces
    ];

    // Vector L3RTT-B - probe advertising INP3 + IPv4 ("$N$I4   "), length 28.
    private static readonly byte[] VectorB =
    [
        .. HeaderPrefix,
        0x24, 0x4E, 0x24, 0x49, 0x34, 0x20, 0x20, 0x20,   // "$N$I4" + 3 spaces
    ];

    // Vector L3RTT-C - reflection: byte-identical echo of Vector A.
    private static readonly byte[] VectorC = VectorA;

    [Fact]
    public void Build_plain_inp3_probe_matches_spec_vector_A()
    {
        var frame = Inp3L3RttFrame.Build(M0Lte);

        frame.ToBytes().Should().Equal(VectorA);
        frame.ToBytes().Length.Should().Be(28);
        frame.Inp3Capable.Should().BeTrue();
        frame.IpAccept.Should().BeNull();
        frame.CapabilityText.Should().Be("$N      ", "default width 8: $N right-padded with six spaces");

        // The frame IS a NetRomPacket with the canonical L3RTT shape.
        frame.Packet.Network.Origin.Should().Be(M0Lte);
        frame.Packet.Network.Destination.Should().Be(new Callsign("L3RTT", 0));
        frame.Packet.Network.TimeToLive.Should().Be(NetRomNetworkHeader.DefaultTimeToLive).And.Be((byte)25);
        ((byte)frame.Packet.Transport.Opcode & NetRomTransportHeader.OpcodeMask).Should().Be((byte)0x02);
        frame.Packet.Transport.Flags.Should().Be(NetRomTransportFlags.None);
    }

    [Fact]
    public void Build_inp3_plus_ipv4_probe_matches_spec_vector_B()
    {
        var frame = Inp3L3RttFrame.Build(M0Lte, ipAccept: 4);

        frame.ToBytes().Should().Equal(VectorB);
        frame.ToBytes().Length.Should().Be(28);
        frame.Inp3Capable.Should().BeTrue();
        frame.IpAccept.Should().Be(4);
        frame.CapabilityText.Should().Be("$N$I4   ");
    }

    [Fact]
    public void Parse_vector_A_extracts_plain_inp3_capability()
    {
        Inp3L3RttFrame.TryParse(VectorA, out var frame).Should().BeTrue();
        frame!.Inp3Capable.Should().BeTrue();
        frame.IpAccept.Should().BeNull();
        frame.Packet.Network.Origin.Should().Be(M0Lte);
        frame.Packet.Network.Destination.Base.Should().Be("L3RTT");
    }

    [Fact]
    public void Parse_vector_B_extracts_inp3_and_ipv4()
    {
        Inp3L3RttFrame.TryParse(VectorB, out var frame).Should().BeTrue();
        frame!.Inp3Capable.Should().BeTrue();
        frame.IpAccept.Should().Be(4, "the $I4 token advertises IPv4 acceptance");
    }

    [Fact]
    public void Parse_vector_C_recognised_as_our_own_reflection_by_origin()
    {
        // Verbatim echo: a returning frame keeps the original prober's origin, so
        // the prober recognises its own probe by Origin == self (§1.4).
        Inp3L3RttFrame.TryParse(VectorC, out var frame).Should().BeTrue();
        frame!.IsReflectionOf(M0Lte).Should().BeTrue("origin came back unchanged as M0LTE-0");
        frame.IsReflectionOf(new Callsign("GB7RDG", 0)).Should().BeFalse("a different node's probe is not ours");
    }

    [Fact]
    public void Build_then_parse_round_trips_through_bytes()
    {
        foreach (int? ip in new int?[] { null, 0, 4, 6, 9 })
        {
            var built = Inp3L3RttFrame.Build(M0Lte, ipAccept: ip);
            Inp3L3RttFrame.TryParse(built.ToBytes(), out var parsed).Should().BeTrue();
            parsed!.Inp3Capable.Should().BeTrue();
            parsed.IpAccept.Should().Be(ip);
            parsed.Packet.Network.Origin.Should().Be(M0Lte);
            parsed.ToBytes().Should().Equal(built.ToBytes());
        }
    }

    [Fact]
    public void Capability_text_parse_is_width_independent()
    {
        // The recogniser scans $-tokens regardless of pad width / contiguity.
        var wide = Inp3L3RttFrame.Build(M0Lte, ipAccept: 4, capabilityTextWidth: 40);
        wide.ToBytes()[20..].Length.Should().Be(40, "the payload was padded to the requested width");
        Inp3L3RttFrame.TryParse(wide.ToBytes(), out var parsed).Should().BeTrue();
        parsed!.Inp3Capable.Should().BeTrue();
        parsed.IpAccept.Should().Be(4);
    }

    [Fact]
    public void Capability_text_shorter_than_width_is_not_truncated()
    {
        // A width smaller than the tokens leaves them intact (no truncation, no pad).
        var frame = Inp3L3RttFrame.Build(M0Lte, ipAccept: 4, capabilityTextWidth: 0);
        frame.CapabilityText.Should().Be("$N$I4");
        Inp3L3RttFrame.TryParse(frame.ToBytes(), out var parsed).Should().BeTrue();
        parsed!.Inp3Capable.Should().BeTrue();
        parsed.IpAccept.Should().Be(4);
    }

    [Fact]
    public void Unknown_dollar_tokens_are_ignored_but_known_ones_still_parse()
    {
        // Forward-compat: an unknown $-capability between $N and $I4 must not break
        // recognition of the tokens we do understand.
        var packet = new NetRomPacket
        {
            Network = new NetRomNetworkHeader
            {
                Origin = M0Lte,
                Destination = new Callsign("L3RTT", 0),
                TimeToLive = 25,
            },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 0,
                CircuitId = 0,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = (NetRomOpcode)0x02,
                Flags = NetRomTransportFlags.None,
            },
            Payload = "$N$Z9$I4 "u8.ToArray(),
        };

        Inp3L3RttFrame.TryFrom(packet, out var frame).Should().BeTrue();
        frame!.Inp3Capable.Should().BeTrue();
        frame.IpAccept.Should().Be(4);
    }

    [Fact]
    public void A_packet_without_dollar_N_is_l3rtt_but_not_inp3_capable()
    {
        // Absence of $N means fall back to vanilla NODES (§1.3) - still an L3RTT
        // frame by destination+opcode, just not advertising INP3.
        var packet = new NetRomPacket
        {
            Network = new NetRomNetworkHeader
            {
                Origin = M0Lte,
                Destination = new Callsign("L3RTT", 0),
                TimeToLive = 25,
            },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 0,
                CircuitId = 0,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = (NetRomOpcode)0x02,
                Flags = NetRomTransportFlags.None,
            },
            Payload = "        "u8.ToArray(),
        };

        Inp3L3RttFrame.IsL3Rtt(packet).Should().BeTrue();
        Inp3L3RttFrame.TryFrom(packet, out var frame).Should().BeTrue();
        frame!.Inp3Capable.Should().BeFalse();
        frame.IpAccept.Should().BeNull();
    }

    [Fact]
    public void Non_l3rtt_destination_is_not_recognised()
    {
        // A real Connect Acknowledge (opcode 0x02) to a normal node must NOT be
        // mistaken for L3RTT - the destination is the discriminator, not the opcode.
        var connectAck = new NetRomPacket
        {
            Network = new NetRomNetworkHeader
            {
                Origin = M0Lte,
                Destination = new Callsign("GB7RDG", 0),
                TimeToLive = 25,
            },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 1,
                CircuitId = 1,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = NetRomOpcode.ConnectAcknowledge,
                Flags = NetRomTransportFlags.None,
            },
        };

        Inp3L3RttFrame.IsL3Rtt(connectAck).Should().BeFalse("opcode 0x02 alone is not L3RTT");
        Inp3L3RttFrame.TryFrom(connectAck, out _).Should().BeFalse();
    }

    [Fact]
    public void L3rtt_destination_with_wrong_opcode_is_not_recognised()
    {
        var packet = new NetRomPacket
        {
            Network = new NetRomNetworkHeader
            {
                Origin = M0Lte,
                Destination = new Callsign("L3RTT", 0),
                TimeToLive = 25,
            },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 0,
                CircuitId = 0,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = NetRomOpcode.Information,
                Flags = NetRomTransportFlags.None,
            },
        };

        Inp3L3RttFrame.IsL3Rtt(packet).Should().BeFalse("opcode nibble must be 0x02");
        Inp3L3RttFrame.TryFrom(packet, out _).Should().BeFalse();
    }

    [Fact]
    public void Parse_is_total_on_empty_and_truncated_input()
    {
        Inp3L3RttFrame.TryParse(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
        Inp3L3RttFrame.TryParse(new byte[19], out _).Should().BeFalse("a datagram needs the full 20-byte header");
        Inp3L3RttFrame.TryParse(new byte[20], out _).Should()
            .BeFalse("an all-zero callsign slot is not a decodable callsign, so the packet itself fails to parse");

        // Truncate Vector A at every length below full - none should throw or
        // succeed past the point the header decodes to a valid L3RTT packet.
        for (int len = 0; len < VectorA.Length; len++)
        {
            // Must never throw.
            Inp3L3RttFrame.TryParse(VectorA.AsSpan(0, len), out _);
        }
        // A header-only (payload-empty) L3RTT still parses: no $N → not capable.
        Inp3L3RttFrame.TryParse(VectorA.AsSpan(0, 20), out var headerOnly).Should().BeTrue();
        headerOnly!.Inp3Capable.Should().BeFalse();
    }

    [Fact]
    public void Parse_is_total_on_garbage()
    {
        var rng = new Random(20260607);
        for (int trial = 0; trial < 20_000; trial++)
        {
            var buf = new byte[rng.Next(0, 64)];
            rng.NextBytes(buf);
            // The contract: never throws. Whether it recognises is incidental.
            Inp3L3RttFrame.TryParse(buf, out _);
        }
    }

    [Fact]
    public void Build_rejects_out_of_range_ip_accept()
    {
        var ex1 = Record.Exception(() => Inp3L3RttFrame.Build(M0Lte, ipAccept: 10));
        ex1.Should().BeOfType<ArgumentOutOfRangeException>("IP version must be a single decimal digit");
        var ex2 = Record.Exception(() => Inp3L3RttFrame.Build(M0Lte, ipAccept: -1));
        ex2.Should().BeOfType<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Build_honours_custom_ttl()
    {
        var frame = Inp3L3RttFrame.Build(M0Lte, timeToLive: 1);
        frame.Packet.Network.TimeToLive.Should().Be((byte)1, "any TTL >= 1 works for the single-hop probe");
    }
}
