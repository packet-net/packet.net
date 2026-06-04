using System.Text;
using Packet.Core;
using Packet.NetRom.Transport;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// Loss-recovery + flow-control tests for the NET/ROM L4 circuit: selective-NAK
/// retransmission on a sequence gap, and choke backpressure. Driven through the
/// deterministic <see cref="CircuitPairHarness"/>.
/// </summary>
public sealed class NetRomCircuitRecoveryTests
{
    private static readonly Callsign User = new("M0LTE", 0);

    [Fact]
    public void A_sequence_gap_triggers_a_NAK_and_selective_retransmit()
    {
        // Window 4, three frames sent together. Drop the FIRST (seq 0) on the wire;
        // B sees seq 1 out of order, NAKs seq 0, A retransmits from seq 0, and all
        // three deliver in order.
        var opts = new NetRomCircuitOptions { WindowSize = 4, RetransmitTimeout = TimeSpan.FromSeconds(30) };
        var h = new CircuitPairHarness(opts);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();

        h.DropNextAToB();   // drop the first Information (seq 0)
        a.Circuit.Send(new byte[] { 10 });
        a.Circuit.Send(new byte[] { 20 });
        a.Circuit.Send(new byte[] { 30 });
        h.Pump();   // B receives seq 1,2 out of order → NAK seq 0 → A retransmits

        accepted[0].Received.Select(r => r[0]).Should().Equal(
            new byte[] { 10, 20, 30 },
            "the NAK-driven selective retransmit recovered the dropped frame, in order");
    }

    [Fact]
    public void Choke_stops_the_sender_until_released()
    {
        // B self-chokes after one undelivered frame (ChokeThreshold=1) and only
        // drains (releasing choke) when the test calls OnDeliveryDrained. A must
        // hold its second frame back while choked, then send it once released.
        var opts = new NetRomCircuitOptions { WindowSize = 8, ChokeThreshold = 1, RetransmitTimeout = TimeSpan.FromSeconds(30) };
        var h = new CircuitPairHarness(opts);

        CircuitPairHarness.Captured? bCap = null;
        h.B.IncomingCircuit += (_, e) =>
        {
            bCap = new CircuitPairHarness.Captured(e.Circuit);
            CircuitManager.AcceptIncoming(e);
        };

        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();
        bCap.Should().NotBeNull();

        // First frame: B receives it and, because ChokeThreshold=1, asserts choke on
        // its ack. A learns it is choked.
        a.Circuit.Send(Encoding.ASCII.GetBytes("one"));
        h.Pump();
        bCap!.Received.Should().HaveCount(1);
        a.Circuit.PeerChoked.Should().BeTrue("B asserted choke after the first undelivered frame");

        // Second frame while choked: A must NOT put it on the wire.
        a.Circuit.Send(Encoding.ASCII.GetBytes("two"));
        h.Pump();
        bCap.Received.Should().HaveCount(1, "A is choked, so the second frame is held");

        // B drains its backlog → releases choke → A resumes and the second frame
        // arrives. (With ChokeThreshold=1, B will re-choke the moment "two" lands;
        // the point under test is that the gate opened and the held frame got
        // through — draining again then clears the re-choke.)
        bCap.Circuit.OnDeliveryDrained();
        h.Pump();
        bCap.Received.Should().HaveCount(2, "the held frame went out once choke was released");
        Encoding.ASCII.GetString(bCap.Received[1]).Should().Be("two");

        bCap.Circuit.OnDeliveryDrained();   // drain "two" → release the re-choke
        h.Pump();
        a.Circuit.PeerChoked.Should().BeFalse("once B has fully drained, the sender is un-choked");
    }
}
