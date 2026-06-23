using System.Text;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// Tests for LinBPQ-style negotiated NET/ROM L4 payload compression
/// (<c>L4Compress</c> / <c>L2Compress</c>): the connect-time capability handshake
/// (offer / decline, both directions), the on-wire codec bytes, the zlib payload
/// round-trip, and the end-to-end compressed data path (including fragmentation and
/// recovery), all driven through the deterministic <see cref="CircuitPairHarness"/>.
///
/// The cardinal safety property under test: a compressed frame is only ever sent on a
/// circuit where BOTH ends negotiated compression, so a non-compressing peer never
/// receives a frame it cannot read.
/// </summary>
public sealed class NetRomCompressionTests
{
    private static readonly Callsign User = new("M0LTE", 0);
    private static readonly Callsign NodeA = new("GB7AAA", 0);
    private static readonly Callsign NodeB = new("GB7BBB", 0);

    private static NetRomCircuitOptions On => new() { CompressionEnabled = true };
    private static NetRomCircuitOptions Off => NetRomCircuitOptions.Default;

    // ─── Negotiation ────────────────────────────────────────────────────────

    [Fact]
    public void Both_ends_offering_negotiates_compression_on()
    {
        var h = new CircuitPairHarness(options: On, optionsB: On);
        var accepted = h.AutoAcceptOnB();

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Connected.Should().BeTrue();
        a.Circuit.CompressionNegotiated.Should().BeTrue("both ends advertised compression");
        accepted[0].Circuit.CompressionNegotiated.Should().BeTrue("the responder agreed and mirrored it back");
    }

    [Fact]
    public void Originator_off_means_compression_declined_both_ends()
    {
        // A does not offer; B would accept. Result: OFF — A never sends the extended
        // Connect Request, so B has nothing to mirror, and A never compresses.
        var h = new CircuitPairHarness(options: Off, optionsB: On);
        var accepted = h.AutoAcceptOnB();

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Circuit.CompressionNegotiated.Should().BeFalse("the originator never offered");
        accepted[0].Circuit.CompressionNegotiated.Should().BeFalse("there was no offer to agree to");
    }

    [Fact]
    public void Responder_off_declines_so_originator_does_not_compress()
    {
        // A offers, B has compression off ⇒ B replies with the vanilla ack ⇒ A must
        // NOT compress. This is the BPQ-neighbour-safe case: pdn offers, BPQ-like peer
        // declines, the link runs uncompressed and nothing is corrupted.
        var h = new CircuitPairHarness(options: On, optionsB: Off);
        var accepted = h.AutoAcceptOnB();

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Circuit.CompressionNegotiated.Should().BeFalse("the responder declined, so A keeps sending raw");
        accepted[0].Circuit.CompressionNegotiated.Should().BeFalse();

        // And data still flows correctly, uncompressed.
        var payload = Encoding.ASCII.GetBytes("the quick brown fox");
        a.Circuit.Send(payload);
        h.Pump();
        accepted[0].ReceivedBytes.Should().Equal(payload);
    }

    [Fact]
    public void Neither_end_offering_is_vanilla_netrom()
    {
        var h = new CircuitPairHarness(options: Off, optionsB: Off);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Circuit.CompressionNegotiated.Should().BeFalse();
        accepted[0].Circuit.CompressionNegotiated.Should().BeFalse();
    }

    [Fact]
    public void Declined_circuit_sends_a_canonical_empty_connect_ack()
    {
        // Capture the Connect Acknowledge bytes B emits when compression is NOT agreed:
        // it must be the vanilla empty-info ack (no extension) — byte-for-byte what a
        // non-compressing peer expects.
        NetRomPacket? ack = null;
        var bManager = new CircuitManager(NodeB, Off, new Microsoft.Extensions.Time.Testing.FakeTimeProvider());
        bManager.SendPacket = p =>
        {
            if (p.Transport.Opcode == NetRomOpcode.ConnectAcknowledge)
            {
                ack = p;
            }
        };
        bManager.IncomingCircuit += (_, e) => CircuitManager.AcceptIncoming(e);

        // Feed it a (compression-offering) Connect Request from A.
        var creq = BuildConnectRequest(offerCompression: true);
        bManager.OnPacket(creq);

        ack.Should().NotBeNull();
        ack!.Payload.Length.Should().Be(0, "a non-agreeing Connect Acknowledge carries no extension");
    }

    // ─── Wire codec ─────────────────────────────────────────────────────────

    [Fact]
    public void Extended_connect_request_carries_the_compress_bit_in_the_timer_high_byte()
    {
        var info = ConnectRequestInfo.BuildExtended(4, User, NodeA, timerSeconds: 60, offerCompression: true);

        info.Length.Should().Be(ConnectRequestInfo.ExtendedLength, "15 canonical + 2-octet timer trailer");
        ConnectRequestInfo.OffersCompression(info).Should().BeTrue();

        // The compress bit is the high nibble of the trailing high byte (BPQ 0x40);
        // the timer value survives in the low 12 bits.
        (info[^1] & ConnectRequestInfo.CompressBit).Should().Be(ConnectRequestInfo.CompressBit);
        int timer = info[^2] | ((info[^1] & 0x0F) << 8);
        timer.Should().Be(60, "masking the compress bit off leaves the proposed timer");
    }

    [Fact]
    public void Canonical_connect_request_does_not_offer_compression()
    {
        var info = ConnectRequestInfo.Build(4, User, NodeA);
        ConnectRequestInfo.OffersCompression(info).Should().BeFalse("the 15-octet form has no trailer");

        // And the extended parser of the canonical form still yields window + callsigns.
        ConnectRequestInfo.TryParse(info, out var win, out var u, out var n).Should().BeTrue();
        win.Should().Be((byte)4);
        u.Should().Be(User);
        n.Should().Be(NodeA);
    }

    [Fact]
    public void Connect_ack_codec_round_trips_the_agree_bit()
    {
        var agree = ConnectAckInfo.Build(acceptedWindow: 4, timeToLive: 25, agreeCompression: true);
        agree.Length.Should().Be(ConnectAckInfo.ExtendedLength);
        ConnectAckInfo.AgreesCompression(agree).Should().BeTrue();
        (agree[1] & ConnectAckInfo.CompressBit).Should().Be(ConnectAckInfo.CompressBit);
        (agree[1] & 0x7F).Should().Be((byte)25, "masking the agree bit off leaves the TTL (BPQ L4DATA[1] &= 0x7f)");

        var decline = ConnectAckInfo.Build(4, 25, agreeCompression: false);
        decline.Should().BeEmpty("a declining ack is the vanilla empty-info form");
        ConnectAckInfo.AgreesCompression(decline).Should().BeFalse();
    }

    // ─── zlib payload round-trip + BPQ-format interop ────────────────────────

    [Fact]
    public void Compress_then_decompress_round_trips()
    {
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("CQ CQ CQ de GB7RDG ", 50)));
        var z = NetRomCompression.Compress(data);
        z.Length.Should().BeLessThan(data.Length, "repetitive text compresses");

        NetRomCompression.TryDecompress(z, maxOutput: 65536, out var back).Should().BeTrue();
        back.Should().Equal(data);
    }

    [Fact]
    public void Compressed_payload_is_a_zlib_stream_that_bpq_doinflate_accepts()
    {
        // LinBPQ's doinflate is a plain inflateInit/inflate, i.e. it expects a standard
        // zlib-wrapped stream (RFC 1950): 0x78 header byte (CMF, deflate/32K window) and
        // an Adler-32 trailer. Assert our compressor emits exactly that framing — the
        // format contract that makes the stream decodable by BPQ.
        var data = Encoding.ASCII.GetBytes("interop probe payload, compressed by pdn");
        var z = NetRomCompression.Compress(data);

        z.Length.Should().BeGreaterThan(6, "zlib header (2) + data + Adler-32 (4)");
        z[0].Should().Be((byte)0x78, "zlib CMF: deflate with a 32K window — what inflateInit expects");
        ((z[0] << 8 | z[1]) % 31).Should().Be(0, "the zlib 2-byte header is a valid FCHECK (divisible by 31)");

        // Round-trips through a fresh decompressor (the inflate side of the contract).
        NetRomCompression.TryDecompress(z, 65536, out var back).Should().BeTrue();
        back.Should().Equal(data);
    }

    [Fact]
    public void Corrupt_compressed_payload_fails_closed_not_throws()
    {
        var garbage = new byte[] { 0x78, 0x9C, 0xFF, 0xFF, 0xFF, 0xFF };
        NetRomCompression.TryDecompress(garbage, 65536, out var back).Should().BeFalse();
        back.Should().BeEmpty();
    }

    [Fact]
    public void Decompress_refuses_a_zip_bomb_past_the_cap()
    {
        // A long run of one byte compresses tiny but expands large; cap enforcement
        // must reject it rather than allocate unboundedly.
        var huge = new byte[100_000];
        var z = NetRomCompression.Compress(huge);
        NetRomCompression.TryDecompress(z, maxOutput: 8192, out _).Should().BeFalse("expansion past the cap is rejected");
        NetRomCompression.TryDecompress(z, maxOutput: 200_000, out var ok).Should().BeTrue();
        ok.Should().HaveCount(100_000);
    }

    // ─── End-to-end compressed data path ─────────────────────────────────────

    [Fact]
    public void Negotiated_circuit_compresses_on_the_wire_and_delivers_intact()
    {
        var h = new CircuitPairHarness(options: On, optionsB: On);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        // Highly compressible payload, larger than one fragment so it exercises the
        // compress-then-fragment-then-reassemble-then-inflate path.
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("A", 1000)));
        a.Circuit.Send(payload);
        h.Pump();

        accepted[0].Received.Should().ContainSingle("inflated to one logical frame");
        accepted[0].Received[0].Should().Equal(payload, "the decompressed payload matches exactly");
    }

    [Fact]
    public void Both_directions_compress_independently()
    {
        var h = new CircuitPairHarness(options: On, optionsB: On);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        var toB = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("ping ", 100)));
        a.Circuit.Send(toB);
        h.Pump();
        accepted[0].ReceivedBytes.Should().Equal(toB);

        var toA = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("pong ", 100)));
        accepted[0].Circuit.Send(toA);
        h.Pump();
        a.ReceivedBytes.Should().Equal(toA);
    }

    [Fact]
    public void Compressed_frame_survives_a_dropped_fragment_via_retransmit()
    {
        var h = new CircuitPairHarness(options: On, optionsB: On);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("recover me ", 80)));

        h.DropNextAToB();   // lose the first compressed fragment
        a.Circuit.Send(payload);
        h.Pump();

        // Retransmit timer recovers the lost fragment; the inflated frame still matches.
        h.Advance(TimeSpan.FromSeconds(6));
        h.Pump();

        accepted[0].ReceivedBytes.Should().Equal(payload, "loss recovery keeps the compressed stream intact");
    }

    [Fact]
    public void Incompressible_payload_falls_back_to_raw_and_still_delivers()
    {
        // Random-ish data won't shrink; the circuit sends it raw (no Compressed flag),
        // which the receiver delivers without inflating — the BPQ "complen >= dataLen ⇒
        // just send" fallback. Correctness must hold either way.
        var h = new CircuitPairHarness(options: On, optionsB: On);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        var payload = new byte[64];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 167 + 13) & 0xFF);   // poorly-compressible
        }
        a.Circuit.Send(payload);
        h.Pump();

        accepted[0].ReceivedBytes.Should().Equal(payload);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static NetRomPacket BuildConnectRequest(bool offerCompression)
    {
        var info = offerCompression
            ? ConnectRequestInfo.BuildExtended(4, User, NodeA, 60, offerCompression: true)
            : ConnectRequestInfo.Build(4, User, NodeA);

        return new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = NodeA, Destination = NodeB, TimeToLive = 25 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 7,
                CircuitId = 3,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = NetRomOpcode.ConnectRequest,
                Flags = NetRomTransportFlags.None,
            },
            Payload = info,
        };
    }
}
