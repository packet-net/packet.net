using System.Text;
using Packet.Core;
using Packet.NetRom.Transport;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// packet.net#850 at L4: <see cref="NetRomCircuit.DisconnectWhenDrained"/> sends the Disconnect
/// Request only once the send queue is empty and every Information message is acknowledged
/// (LinBPQ's DISCPENDING), where <see cref="NetRomCircuit.Disconnect"/> abandons whatever is
/// still queued. A peer that has gone is ended by the Information retry limit; a choked peer
/// with nothing in flight is probed after the retransmit timeout, as LinBPQ does.
/// </summary>
public sealed class NetRomCircuitDrainTests
{
    private static readonly Callsign User = new("M0LTE", 0);
    private static readonly TimeSpan Rto = TimeSpan.FromSeconds(5);

    private static readonly string[] Lines =
        Enumerable.Range(1, 6).Select(i => $"line {i} of the tail\r").ToArray();

    private static byte[] Tail => Encoding.ASCII.GetBytes(string.Concat(Lines));

    private static (CircuitPairHarness H, CircuitPairHarness.Captured A, CircuitPairHarness.Captured B) Connected(
        NetRomCircuitOptions? optsA = null, NetRomCircuitOptions? optsB = null)
    {
        var h = new CircuitPairHarness(optsA ?? new NetRomCircuitOptions { WindowSize = 2, RetransmitTimeout = Rto }, optsB);
        var accepted = h.AutoAcceptOnB();
        var a = h.OpenFromA();
        a.Circuit.Connect(User);
        h.Pump();
        accepted.Should().ContainSingle();
        return (h, a, accepted[0]);
    }

    private static void SendTail(CircuitPairHarness.Captured a)
    {
        foreach (var line in Lines)
        {
            a.Circuit.Send(Encoding.ASCII.GetBytes(line));
        }
    }

    [Fact]
    public void The_tail_is_delivered_before_the_Disconnect_Request()
    {
        var (h, a, b) = Connected();
        // B's data at the moment its end closes: the Disconnect Request must come after all of it.
        byte[]? receivedWhenClosed = null;
        b.Circuit.Closed += _ => receivedWhenClosed = b.ReceivedBytes;

        SendTail(a);   // window 2: two in flight, four queued (nothing pumped yet)
        a.Circuit.DisconnectWhenDrained();
        a.Circuit.State.Should().Be(NetRomCircuitState.Connected, "data is still queued");

        h.Pump();

        receivedWhenClosed.Should().Equal(Tail, "every byte arrived before the Disconnect Request");
        a.Closed.Should().Equal(NetRomCircuitCloseReason.Normal);
        b.Closed.Should().Equal(NetRomCircuitCloseReason.Normal);
    }

    [Fact]
    public void Plain_Disconnect_still_abandons_the_queue()
    {
        var (h, a, b) = Connected();
        SendTail(a);
        a.Circuit.Disconnect();
        h.Pump();

        b.ReceivedBytes.Length.Should().BeLessThan(Tail.Length, "Disconnect does not wait (the sysop kill / shutdown path)");
        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
    }

    [Fact]
    public void Lost_information_is_retransmitted_before_the_disconnect()
    {
        var (h, a, b) = Connected();
        byte[]? receivedWhenClosed = null;
        b.Circuit.Closed += _ => receivedWhenClosed = b.ReceivedBytes;

        h.DropNextAToB(2);   // the first window's worth is lost on the air
        SendTail(a);
        a.Circuit.DisconnectWhenDrained();
        h.Pump();
        a.Circuit.State.Should().Be(NetRomCircuitState.Connected, "nothing is acknowledged yet");

        for (int i = 0; i < 4 && a.Circuit.State != NetRomCircuitState.Disconnected; i++)
        {
            h.Advance(Rto + TimeSpan.FromSeconds(1));
        }

        receivedWhenClosed.Should().Equal(Tail);
        a.Closed.Should().Equal(NetRomCircuitCloseReason.Normal);
    }

    [Fact]
    public void A_peer_that_has_gone_is_ended_by_the_retry_limit()
    {
        var (h, a, _) = Connected();
        h.DropNextAToB(1000);   // the far node has vanished
        SendTail(a);
        a.Circuit.DisconnectWhenDrained();
        h.Pump();

        for (int i = 0; i < 10 && a.Circuit.State != NetRomCircuitState.Disconnected; i++)
        {
            h.Advance(Rto + TimeSpan.FromSeconds(1));
        }

        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
        a.Closed.Should().Equal(NetRomCircuitCloseReason.Timeout);
    }

    [Fact]
    public void A_choked_peer_is_waited_for_and_probed_then_the_tail_goes()
    {
        // B chokes after one undelivered frame and never drains on its own.
        var optsA = new NetRomCircuitOptions { WindowSize = 4, RetransmitTimeout = Rto };
        var optsB = optsA with { ChokeThreshold = 1 };
        var (h, a, b) = Connected(optsA, optsB);
        byte[]? receivedWhenClosed = null;
        b.Circuit.Closed += _ => receivedWhenClosed = b.ReceivedBytes;

        a.Circuit.Send(Encoding.ASCII.GetBytes(Lines[0]));
        h.Pump();
        a.Circuit.PeerChoked.Should().BeTrue();

        foreach (var line in Lines.Skip(1))
        {
            a.Circuit.Send(Encoding.ASCII.GetBytes(line));
        }
        a.Circuit.DisconnectWhenDrained();
        h.Pump();
        a.Circuit.State.Should().Be(NetRomCircuitState.Connected, "the choked peer holds the rest back");

        // Each retransmit timeout the stale choke is cancelled and one more frame is tried; B,
        // still choked, takes it and chokes again, so the tail trickles through and the close
        // completes without B ever draining.
        for (int i = 0; i < 20 && a.Circuit.State != NetRomCircuitState.Disconnected; i++)
        {
            h.Advance(Rto + TimeSpan.FromSeconds(1));
        }

        receivedWhenClosed.Should().Equal(Tail);
        a.Closed.Should().Equal(NetRomCircuitCloseReason.Normal);
    }

    [Fact]
    public void A_choked_peer_that_has_gone_is_ended_by_the_retry_limit()
    {
        var optsA = new NetRomCircuitOptions { WindowSize = 4, RetransmitTimeout = Rto };
        var optsB = optsA with { ChokeThreshold = 1 };
        var (h, a, _) = Connected(optsA, optsB);

        a.Circuit.Send(Encoding.ASCII.GetBytes(Lines[0]));
        h.Pump();
        a.Circuit.PeerChoked.Should().BeTrue();
        foreach (var line in Lines.Skip(1))
        {
            a.Circuit.Send(Encoding.ASCII.GetBytes(line));
        }
        a.Circuit.DisconnectWhenDrained();

        h.DropNextAToB(1000);   // and then B vanishes
        for (int i = 0; i < 20 && a.Circuit.State != NetRomCircuitState.Disconnected; i++)
        {
            h.Advance(Rto + TimeSpan.FromSeconds(1));
        }

        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected, "a choke must not keep a dead circuit waiting for ever");
        a.Closed.Should().Equal(NetRomCircuitCloseReason.Timeout);
    }

    [Fact]
    public void Disconnect_cuts_a_pending_drain_short()
    {
        var (h, a, b) = Connected();
        h.DropNextAToB(2);
        SendTail(a);
        a.Circuit.DisconnectWhenDrained();
        h.Pump();
        a.Circuit.State.Should().Be(NetRomCircuitState.Connected);

        a.Circuit.Disconnect();
        h.Pump();
        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
        b.Closed.Should().Equal(NetRomCircuitCloseReason.Normal);
    }

    [Fact]
    public void With_nothing_outstanding_the_disconnect_goes_at_once()
    {
        var (h, a, b) = Connected();
        a.Circuit.Send(Encoding.ASCII.GetBytes(Lines[0]));
        h.Pump();

        a.Circuit.DisconnectWhenDrained();
        h.Pump();
        a.Circuit.State.Should().Be(NetRomCircuitState.Disconnected);
        b.ReceivedBytes.Should().Equal(Encoding.ASCII.GetBytes(Lines[0]));
    }
}
