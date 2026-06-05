using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Wire;

/// <summary>
/// Round-trip + totality tests for the NET/ROM L3 network header (15 B), the L4
/// transport header (5 B), and the full <see cref="NetRomPacket"/> datagram — the
/// wire foundation the circuit layer rides on.
/// </summary>
public sealed class NetRomHeaderCodecTests
{
    private static readonly Callsign Origin = new("GB7RDG", 1);
    private static readonly Callsign Dest = new("GB7SOT", 2);

    [Fact]
    public void Network_header_round_trips_through_bytes()
    {
        var header = new NetRomNetworkHeader { Origin = Origin, Destination = Dest, TimeToLive = 25 };

        var bytes = header.ToBytes();
        bytes.Length.Should().Be(NetRomNetworkHeader.EncodedLength).And.Be(15);

        NetRomNetworkHeader.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.Origin.Should().Be(Origin);
        parsed.Destination.Should().Be(Dest);
        parsed.TimeToLive.Should().Be((byte)25);
    }

    [Fact]
    public void Network_header_decrement_reduces_ttl_and_floors_at_zero()
    {
        var header = new NetRomNetworkHeader { Origin = Origin, Destination = Dest, TimeToLive = 1 };
        header.Decremented().TimeToLive.Should().Be((byte)0);
        header.Decremented().Decremented().TimeToLive.Should().Be((byte)0, "TTL never underflows past zero");
    }

    [Fact]
    public void Network_header_parse_is_total_on_short_input()
    {
        NetRomNetworkHeader.TryParse(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
        NetRomNetworkHeader.TryParse(new byte[14], out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(NetRomOpcode.ConnectRequest, NetRomTransportFlags.None)]
    [InlineData(NetRomOpcode.Information, NetRomTransportFlags.MoreFollows)]
    [InlineData(NetRomOpcode.InformationAcknowledge, NetRomTransportFlags.Nak)]
    [InlineData(NetRomOpcode.ConnectAcknowledge, NetRomTransportFlags.Choke)]
    [InlineData(NetRomOpcode.Information, NetRomTransportFlags.Choke | NetRomTransportFlags.MoreFollows)]
    public void Transport_header_round_trips_opcode_and_flags(NetRomOpcode opcode, NetRomTransportFlags flags)
    {
        var header = new NetRomTransportHeader
        {
            CircuitIndex = 7,
            CircuitId = 42,
            TxSequence = 3,
            RxSequence = 9,
            Opcode = opcode,
            Flags = flags,
        };

        var bytes = header.ToBytes();
        bytes.Length.Should().Be(5);

        NetRomTransportHeader.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.CircuitIndex.Should().Be((byte)7);
        parsed.CircuitId.Should().Be((byte)42);
        parsed.TxSequence.Should().Be((byte)3);
        parsed.RxSequence.Should().Be((byte)9);
        parsed.Opcode.Should().Be(opcode);
        parsed.Flags.Should().Be(flags);
    }

    [Fact]
    public void Transport_flag_helpers_reflect_the_high_bits()
    {
        var header = new NetRomTransportHeader
        {
            CircuitIndex = 0, CircuitId = 0, TxSequence = 0, RxSequence = 0,
            Opcode = NetRomOpcode.Information,
            Flags = NetRomTransportFlags.Choke | NetRomTransportFlags.Nak,
        };
        header.Choke.Should().BeTrue();
        header.Nak.Should().BeTrue();
        header.MoreFollows.Should().BeFalse();
        // The opcode nibble and the flag bits coexist in one byte.
        header.OpcodeAndFlags.Should().Be((byte)(0x05 | 0x80 | 0x40));
    }

    [Fact]
    public void Packet_round_trips_header_plus_payload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var packet = new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = Origin, Destination = Dest, TimeToLive = 20 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 1, CircuitId = 1, TxSequence = 0, RxSequence = 0,
                Opcode = NetRomOpcode.Information, Flags = NetRomTransportFlags.None,
            },
            Payload = payload,
        };

        var bytes = packet.ToBytes();
        bytes.Length.Should().Be(NetRomPacket.HeaderLength + payload.Length).And.Be(25);

        NetRomPacket.TryParse(bytes, out var parsed).Should().BeTrue();
        parsed!.Network.Origin.Should().Be(Origin);
        parsed.Transport.Opcode.Should().Be(NetRomOpcode.Information);
        parsed.Payload.ToArray().Should().Equal(payload);
    }

    [Fact]
    public void Empty_control_packet_is_the_observed_20_byte_form()
    {
        // The repo's BPQ corpus saw PID-0xCF I-frames "always exactly 20 B" — the
        // 15-byte network + 5-byte transport header with no payload.
        var packet = new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = Origin, Destination = Dest, TimeToLive = 25 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 1, CircuitId = 1, TxSequence = 0, RxSequence = 0,
                Opcode = NetRomOpcode.ConnectRequest, Flags = NetRomTransportFlags.None,
            },
        };
        packet.ToBytes().Length.Should().Be(20);
    }

    [Fact]
    public void Packet_parse_is_total_on_short_input()
    {
        NetRomPacket.TryParse(new byte[19], out _).Should().BeFalse("a datagram needs the full 20-byte header");
        NetRomPacket.TryParse(ReadOnlySpan<byte>.Empty, out _).Should().BeFalse();
    }

    [Fact]
    public void ConnectRequest_info_round_trips_window_user_and_node()
    {
        var user = new Callsign("M0LTE", 7);
        var node = new Callsign("GB7RDG", 0);

        var info = ConnectRequestInfo.Build(proposedWindow: 6, originatingUser: user, originatingNode: node);
        info.Length.Should().Be(ConnectRequestInfo.Length).And.Be(15, "window byte + two shifted callsigns");
        info[0].Should().Be((byte)6, "the proposed window is the FIRST info octet, not a transport-header field");

        ConnectRequestInfo.TryParse(info, out var win, out var u, out var n).Should().BeTrue();
        win.Should().Be((byte)6);
        u.Should().Be(user);
        n.Should().Be(node);
    }

    [Fact]
    public void ConnectRequest_info_parse_tolerates_trailing_extension_octets()
    {
        // LinBPQ 6.0.25.23 originates a 17-octet Connect Request info field on the
        // wire (verified via the interop stack, #308): [window][user][node] then a
        // 2-octet BPQ extension. We parse the canonical 15 and ignore the rest.
        // These exact bytes are a real PN0TST->PNPROB Connect Request: window 4,
        // user + node both PN0TST, trailing 0x3C 0x00.
        byte[] bpqOnTheWire =
        [
            0x04,                                           // proposed window = 4
            0xA0, 0x9C, 0x60, 0xA8, 0xA6, 0xA8, 0x60,       // originating user  = PN0TST
            0xA0, 0x9C, 0x60, 0xA8, 0xA6, 0xA8, 0x60,       // originating node  = PN0TST
            0x3C, 0x00,                                      // BPQ extension (ignored)
        ];

        ConnectRequestInfo.TryParse(bpqOnTheWire, out var win, out var user, out var node).Should().BeTrue();
        win.Should().Be((byte)4);
        user.Should().Be(new Callsign("PN0TST", 0));
        node.Should().Be(new Callsign("PN0TST", 0));
    }

    [Fact]
    public void ConnectRequest_info_parse_is_total_on_short_input()
    {
        ConnectRequestInfo.TryParse(new byte[14], out _, out _, out _)
            .Should().BeFalse("the canonical Connect Request info field is 15 octets");
        ConnectRequestInfo.TryParse(ReadOnlySpan<byte>.Empty, out _, out _, out _).Should().BeFalse();
    }
}
