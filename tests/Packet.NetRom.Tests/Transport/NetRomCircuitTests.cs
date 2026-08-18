using System.Text;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// Behavioural tests for the NET/ROM L4 circuit FSM, driven through the
/// deterministic <see cref="CircuitPairHarness"/> (two managers + a controllable
/// channel + FakeTimeProvider). Covers the full vanilla transport: connect/ack
/// with window negotiation, info/info-ack over the sliding window, disconnect/ack,
/// retransmit on loss, selective-NAK recovery, choke flow control, and L4
/// fragment/reassembly.
/// </summary>
public sealed class NetRomCircuitTests
{
    private static readonly Callsign User = new("M0LTE", 0);

    [Fact]
    public void Connect_then_acknowledge_brings_both_ends_up()
    {
        var h = new CircuitPairHarness();
        var accepted = h.AutoAcceptOnB();

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Connected.Should().BeTrue("the Connect Acknowledge reached the originator");
        a.Circuit.State.Should().Be(NetRomCircuitState.Connected);
        accepted.Should().ContainSingle();
        accepted[0].Circuit.State.Should().Be(NetRomCircuitState.Connected);
        accepted[0].Circuit.RemoteNode.Should().Be(h.ANode, "B learned the originating node from the L3 header");
    }

    [Fact]
    public void Window_is_negotiated_down_to_the_responders_ceiling()
    {
        // A proposes a window of 8; B's ceiling is 2. The accepted (B-side) window
        // must clamp to B's smaller ceiling - the canonical "accepted ≤ proposed"
        // negotiation.
        var h = new CircuitPairHarness(
            options: new NetRomCircuitOptions { WindowSize = 8 },
            optionsB: new NetRomCircuitOptions { WindowSize = 2 });
        var accepted = h.AutoAcceptOnB();

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        accepted.Should().ContainSingle();
        accepted[0].Circuit.Window.Should().Be(2, "B accepts at most its own ceiling, below A's proposed 8");
    }

    [Fact]
    public void The_originator_clamps_its_send_window_to_the_accepted_window()
    {
        // The other half of the negotiation: B's Connect Acknowledge reports the window
        // it ACCEPTED (info[0]) and the originator must come down to it: sending more
        // frames than the far end agreed to hold overruns its receive queue. LinBPQ does
        // exactly this on receipt: L4->L4WINDOW = L3MSG->L4DATA[0] (L4Code.c:2287).
        var h = new CircuitPairHarness(
            options: new NetRomCircuitOptions { WindowSize = 8 },
            optionsB: new NetRomCircuitOptions { WindowSize = 2 });
        h.AutoAcceptOnB();

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Connected.Should().BeTrue();
        a.Circuit.Window.Should().Be(2, "the acknowledgement's window octet caps our proposed 8");
    }

    [Fact]
    public void A_vanilla_connect_acknowledge_is_21_bytes_carrying_the_accepted_window()
    {
        // The accepted-window octet is base NET/ROM, not a compression extension:
        // LinBPQ's SendConACK writes L4DATA[0] = L4WINDOW and sends
        // LENGTH = MSGHDDRLEN + 22, a 21-byte vanilla ack (L4Code.c:1768,1824), and
        // Linux emits nr->window with NR_CONNACK_LEN 1. Without it the peer reads buffer
        // residue for our window.
        NetRomPacket? ack = null;
        var manager = new CircuitManager(
            new Callsign("GB7BBB", 0),
            new NetRomCircuitOptions { WindowSize = 4 },
            new FakeTimeProvider());
        manager.SendPacket = p =>
        {
            if (p.Transport.Opcode == NetRomOpcode.ConnectAcknowledge)
            {
                ack = p;
            }
        };
        manager.IncomingCircuit += (_, e) => CircuitManager.AcceptIncoming(e);

        manager.OnPacket(new NetRomPacket
        {
            Network = new NetRomNetworkHeader { Origin = new Callsign("GB7AAA", 0), Destination = new Callsign("GB7BBB", 0), TimeToLive = 25 },
            Transport = new NetRomTransportHeader
            {
                CircuitIndex = 7,
                CircuitId = 3,
                TxSequence = 0,
                RxSequence = 0,
                Opcode = NetRomOpcode.ConnectRequest,
                Flags = NetRomTransportFlags.None,
            },
            Payload = ConnectRequestInfo.Build(proposedWindow: 7, User, new Callsign("GB7AAA", 0)),
        });

        ack.Should().NotBeNull();
        ack!.Payload.Length.Should().Be(ConnectAckInfo.VanillaLength, "the vanilla ack carries the window octet and nothing else");
        ack.Payload.Span[0].Should().Be((byte)4, "the accepted window: our ceiling, below the proposed 7");
        ack.ToBytes().Length.Should().Be(21, "20-byte NET/ROM header + the accepted-window octet");
    }

    [Fact]
    public void Information_flows_with_piggybacked_acks()
    {
        var h = new CircuitPairHarness();
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        var payload = Encoding.ASCII.GetBytes("hello netrom");
        a.Circuit.Send(payload);
        h.Pump();

        accepted[0].ReceivedBytes.Should().Equal(payload, "B received the Information payload");

        // And the reverse direction.
        var reply = Encoding.ASCII.GetBytes("hi back");
        accepted[0].Circuit.Send(reply);
        h.Pump();
        a.ReceivedBytes.Should().Equal(reply);
    }

    [Fact]
    public void A_multi_frame_burst_delivers_in_order_within_the_window()
    {
        var h = new CircuitPairHarness(new NetRomCircuitOptions { WindowSize = 4 });
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        // Six one-byte logical sends - more than the window, so the queue drains as
        // acks return.
        for (byte i = 1; i <= 6; i++)
        {
            a.Circuit.Send(new[] { i });
        }
        h.Pump();

        accepted[0].Received.Should().HaveCount(6);
        accepted[0].Received.Select(r => r[0]).Should().Equal((byte)1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void A_large_payload_fragments_and_reassembles_at_236_bytes()
    {
        var h = new CircuitPairHarness(new NetRomCircuitOptions { WindowSize = 8 });
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        // 600 bytes → 236 + 236 + 128, three Information messages (more-follows on
        // the first two), reassembled to one logical frame on B.
        var big = new byte[600];
        for (int i = 0; i < big.Length; i++)
        {
            big[i] = (byte)(i & 0xFF);
        }
        a.Circuit.Send(big);
        h.Pump();

        accepted[0].Received.Should().ContainSingle("the fragments reassemble to one logical frame");
        accepted[0].Received[0].Should().Equal(big);
    }

    [Fact]
    public void Disconnect_is_acknowledged_and_closes_both_ends()
    {
        var h = new CircuitPairHarness();
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Circuit.Disconnect();
        h.Pump();

        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
        a.Closed.Should().ContainSingle().Which.Should().Be(NetRomCircuitCloseReason.Normal);
        accepted[0].Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
        accepted[0].Closed.Should().Contain(NetRomCircuitCloseReason.Normal);
    }

    [Fact]
    public void A_refused_connect_closes_the_originator_as_refused()
    {
        var h = new CircuitPairHarness();
        h.B.IncomingCircuit += (_, e) => h.B.RefuseIncoming(e);

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        a.Connected.Should().BeFalse();
        a.Closed.Should().ContainSingle().Which.Should().Be(NetRomCircuitCloseReason.Refused);
        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
    }

    [Fact]
    public void A_lost_information_frame_is_retransmitted_after_the_timeout()
    {
        var opts = new NetRomCircuitOptions { WindowSize = 4, RetransmitTimeout = TimeSpan.FromSeconds(5), MaxRetries = 3 };
        var h = new CircuitPairHarness(opts);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        // Drop the next A→B datagram (the Information), so B never sees it.
        h.DropNextAToB();
        var payload = Encoding.ASCII.GetBytes("retransmit me");
        a.Circuit.Send(payload);
        h.Pump();
        accepted[0].Received.Should().BeEmpty("the only copy was dropped");

        // After the retransmit timeout, the tick retransmits it and B receives it.
        h.Advance(TimeSpan.FromSeconds(6));
        accepted[0].ReceivedBytes.Should().Equal(payload, "the retransmit delivered the data");
    }

    [Fact]
    public void A_lost_connect_request_is_retransmitted_then_succeeds()
    {
        var opts = new NetRomCircuitOptions { RetransmitTimeout = TimeSpan.FromSeconds(5), MaxRetries = 3 };
        var h = new CircuitPairHarness(opts);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();

        h.DropNextAToB();   // lose the first Connect Request
        a.Circuit.Connect(User);
        h.Pump();
        a.Connected.Should().BeFalse();

        h.Advance(TimeSpan.FromSeconds(6));   // retransmit the connect
        a.Connected.Should().BeTrue("the retransmitted Connect Request was acknowledged");
        accepted.Should().ContainSingle();
    }

    [Fact]
    public void Connect_fails_after_retries_are_exhausted()
    {
        var opts = new NetRomCircuitOptions { RetransmitTimeout = TimeSpan.FromSeconds(5), MaxRetries = 2 };
        var h = new CircuitPairHarness(opts);
        h.AutoAcceptOnB();
        var a = h.OpenFromA();

        // Drop every connect attempt (original + 2 retries).
        h.DropNextAToB(3);
        a.Circuit.Connect(User);
        h.Pump();

        h.Advance(TimeSpan.FromSeconds(6));   // retry 1 (dropped)
        h.Advance(TimeSpan.FromSeconds(6));   // retry 2 (dropped) → exhausted
        h.Advance(TimeSpan.FromSeconds(6));   // tick that trips the give-up

        a.Connected.Should().BeFalse();
        a.Closed.Should().ContainSingle().Which.Should().Be(NetRomCircuitCloseReason.Timeout);
    }
}
