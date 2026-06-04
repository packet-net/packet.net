using System.Text;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// Tests for <see cref="CircuitManager"/>: the circuit-table demultiplex (two
/// concurrent circuits stay independent), inbound-connect minting + refusal, and
/// tolerance of stray datagrams.
/// </summary>
public sealed class CircuitManagerTests
{
    private static readonly Callsign User = new("M0LTE", 0);

    [Fact]
    public void Two_concurrent_circuits_demultiplex_independently()
    {
        var h = new CircuitPairHarness();
        var accepted = h.AutoAcceptOnB();

        var c1 = h.OpenFromA();
        c1.Circuit.Connect(User);
        h.Pump();
        var c2 = h.OpenFromA();
        c2.Circuit.Connect(new Callsign("G0ABC"));
        h.Pump();

        accepted.Should().HaveCount(2);

        // Each carries its own data; the manager routes by the (index,id) key.
        c1.Circuit.Send(Encoding.ASCII.GetBytes("circuit one"));
        c2.Circuit.Send(Encoding.ASCII.GetBytes("circuit two"));
        h.Pump();

        // Match accepted circuits to senders by what they received (order of accept
        // matches order of connect).
        Encoding.ASCII.GetString(accepted[0].ReceivedBytes).Should().Be("circuit one");
        Encoding.ASCII.GetString(accepted[1].ReceivedBytes).Should().Be("circuit two");
    }

    [Fact]
    public void Closed_circuits_are_removed_from_the_table()
    {
        var h = new CircuitPairHarness();
        h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();
        h.A.Circuits.Should().ContainSingle();

        a.Circuit.Disconnect();
        h.Pump();
        h.A.Circuits.Should().BeEmpty("a disconnected circuit deregisters from the manager");
        h.B.Circuits.Should().BeEmpty();
    }

    [Fact]
    public void A_retransmitted_connect_request_does_not_mint_a_duplicate_inbound_circuit()
    {
        // Drop B's first Connect Acknowledge so A retransmits its Connect Request.
        // The retransmit's header names A's circuit (not B's), so it can't match B's
        // local-key table — B must dedup it by the peer identity and re-ack, NOT mint
        // a second inbound circuit.
        var opts = new NetRomCircuitOptions { RetransmitTimeout = TimeSpan.FromSeconds(5), MaxRetries = 3 };
        var h = new CircuitPairHarness(opts);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();

        h.DropNextBToA();   // lose B's first Connect Acknowledge
        a.Circuit.Connect(User);
        h.Pump();
        a.Connected.Should().BeFalse("the connect-ack was dropped");
        h.B.Circuits.Should().ContainSingle("B minted exactly one inbound circuit");

        h.Advance(TimeSpan.FromSeconds(6));   // A retransmits the Connect Request
        a.Connected.Should().BeTrue("the re-ack from the deduped circuit completes the connect");
        h.B.Circuits.Should().ContainSingle("the retransmit re-acked the existing circuit, no duplicate");
        accepted.Should().ContainSingle("IncomingCircuit fired exactly once");
    }

    [Fact]
    public void An_inbound_connect_with_no_listener_is_refused()
    {
        // No IncomingCircuit handler subscribed → the manager refuses rather than
        // leaving a dangling half-open circuit.
        var h = new CircuitPairHarness();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Connected.Should().BeFalse();
        a.Closed.Should().ContainSingle().Which.Should().Be(NetRomCircuitCloseReason.Refused);
        h.B.Circuits.Should().BeEmpty("the refused inbound circuit was deregistered");
    }

    [Fact]
    public void A_stray_datagram_for_an_unknown_circuit_is_dropped_without_throwing()
    {
        var manager = new CircuitManager(new Callsign("GB7XXX"));
        var sent = new List<NetRomPacket>();
        manager.SendPacket = sent.Add;

        // An Information datagram naming a circuit that does not exist.
        var stray = new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = new Callsign("GB7YYY"), Destination = new Callsign("GB7XXX"), TimeToLive = 10 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 99, CircuitId = 99, TxSequence = 0, RxSequence = 0,
                Opcode = NetRomOpcode.Information, Flags = NetRomTransportFlags.None,
            },
            Payload = new byte[] { 1, 2, 3 },
        };

        var act = () => manager.OnPacket(stray);
        act.Should().NotThrow();
        sent.Should().BeEmpty("a stray Information datagram is silently dropped");
    }

    [Fact]
    public void A_disconnect_for_an_unknown_circuit_is_courteously_acknowledged()
    {
        var manager = new CircuitManager(new Callsign("GB7XXX"));
        var sent = new List<NetRomPacket>();
        manager.SendPacket = sent.Add;

        var disc = new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = new Callsign("GB7YYY"), Destination = new Callsign("GB7XXX"), TimeToLive = 10 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 5, CircuitId = 5, TxSequence = 0, RxSequence = 0,
                Opcode = NetRomOpcode.DisconnectRequest, Flags = NetRomTransportFlags.None,
            },
        };
        manager.OnPacket(disc);

        sent.Should().ContainSingle();
        sent[0].Transport.Opcode.Should().Be(NetRomOpcode.DisconnectAcknowledge,
            "a half-open peer's disconnect is acked so it stops retransmitting");
    }
}
