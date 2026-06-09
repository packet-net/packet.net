using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Api;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Tests.Telemetry;

/// <summary>
/// Unit tests for <see cref="NodeTelemetry"/> driven directly via
/// <see cref="NodeTelemetry.Observe"/> with synthetic frames — no modem, no live
/// listener. The live <c>FrameTraced → Observe</c> wiring is covered by the SSE
/// integration test; this file pins the counter math, the per-link byte/REJ/SREJ
/// rollup, the FirstSeen/LastActivity timestamps, and the SSE subscription
/// fan-out + teardown.
/// </summary>
public sealed class NodeTelemetryTests
{
    private const string Port = "vhf-1";
    private static readonly Callsign Local = Callsign.Parse("M0LTE-1");
    private static readonly Callsign Peer = Callsign.Parse("G7XYZ-2");

    private static Ax25FrameEventArgs Rx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Received, Timestamp = at };

    private static Ax25FrameEventArgs Tx(Ax25Frame frame, DateTimeOffset at)
        => new() { Frame = frame, Direction = FrameDirection.Transmitted, Timestamp = at };

    private static DateTimeOffset At(int seconds)
        => new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);

    [Fact]
    public void PortFrames_totals_in_and_out_separately()
    {
        var t = new NodeTelemetry();

        // Two RX, one TX. Peer is the source on RX, the dest on TX.
        t.Observe(Port, Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "ab"u8), At(0)));
        t.Observe(Port, Rx(Ax25Frame.Rr(Local, Peer, nr: 1, isCommand: false), At(1)));
        t.Observe(Port, Tx(Ax25Frame.I(Peer, Local, nr: 1, ns: 0, "cde"u8), At(2)));

        var (framesIn, framesOut) = t.PortFrames(Port);
        framesIn.Should().Be(2);
        framesOut.Should().Be(1);
    }

    [Fact]
    public void PortFrames_is_zero_for_an_unseen_port()
        => new NodeTelemetry().PortFrames("nope").Should().Be((0L, 0L));

    [Fact]
    public void RecentFrames_is_empty_before_any_frame()
        => new NodeTelemetry().RecentFrames(250).Should().BeEmpty();

    [Fact]
    public void RecentFrames_returns_the_last_N_oldest_first()
    {
        var t = new NodeTelemetry();
        for (int i = 0; i < 10; i++)
        {
            t.Observe(Port, Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: (byte)(i % 8), "x"u8), At(i)));
        }

        var recent = t.RecentFrames(3);
        recent.Should().HaveCount(3);
        // Monotonic seq, oldest → newest: the last three observed frames (seq 8, 9, 10).
        recent.Select(f => f.Seq).Should().BeInAscendingOrder().And.Equal(8, 9, 10);
    }

    [Fact]
    public void RecentFrames_returns_all_when_fewer_than_the_limit_and_clamps_a_nonpositive_limit()
    {
        var t = new NodeTelemetry();
        t.Observe(Port, Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "x"u8), At(0)));
        t.Observe(Port, Tx(Ax25Frame.I(Peer, Local, nr: 0, ns: 1, "y"u8), At(1)));

        t.RecentFrames(250).Should().HaveCount(2);
        t.RecentFrames(0).Should().BeEmpty();
        t.RecentFrames(-5).Should().BeEmpty();
    }

    [Fact]
    public void RecentFrames_is_bounded_to_the_ring_capacity()
    {
        var t = new NodeTelemetry();
        // Observe well past the 250-frame ring; only the most recent 250 survive.
        for (int i = 0; i < 400; i++)
        {
            t.Observe(Port, Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: (byte)(i % 8), "x"u8), At(i)));
        }

        var recent = t.RecentFrames(1000);
        recent.Should().HaveCount(250);
        // The oldest retained is seq 151 (frames 1..150 fell off), newest is 400.
        recent[0].Seq.Should().Be(151);
        recent[^1].Seq.Should().Be(400);
    }

    [Fact]
    public void Link_rolls_up_frames_bytes_and_rej_srej_per_peer()
    {
        var t = new NodeTelemetry();

        // RX from Peer: I-frame carries a 2-byte info field; the peer key is the source.
        t.Observe(Port, Rx(Ax25Frame.I(Local, Peer, nr: 0, ns: 0, "ab"u8), At(0)));
        // TX to Peer: I-frame carries 3 bytes; the peer key is the dest.
        t.Observe(Port, Tx(Ax25Frame.I(Peer, Local, nr: 0, ns: 0, "cde"u8), At(1)));
        // An inbound REJ and SREJ from the peer (S-frames carry no info → +0 bytes).
        t.Observe(Port, Rx(Ax25Frame.Rej(Local, Peer, nr: 1, isCommand: false), At(2)));
        t.Observe(Port, Rx(Ax25Frame.Srej(Local, Peer, nr: 2, isCommand: false), At(3)));

        var link = t.Link(Port, Peer.ToString());
        link.Should().NotBeNull();
        link!.FramesIn.Should().Be(3);   // I + REJ + SREJ
        link.FramesOut.Should().Be(1);   // the TX I-frame
        link.BytesIn.Should().Be(2);     // only the inbound I-frame's info field
        link.BytesOut.Should().Be(3);    // only the outbound I-frame's info field
        link.RejCount.Should().Be(1);
        link.SrejCount.Should().Be(1);
    }

    [Fact]
    public void Link_first_seen_is_set_once_and_last_activity_advances()
    {
        var t = new NodeTelemetry();

        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "x"u8), At(10)));
        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "y"u8), At(40)));

        var link = t.Link(Port, Peer.ToString());
        link.Should().NotBeNull();
        link!.FirstSeen.Should().Be(At(10));     // earliest frame, never overwritten
        link.LastActivity.Should().Be(At(40));   // advances to the most recent
    }

    [Fact]
    public void Link_is_null_for_an_unseen_peer()
        => new NodeTelemetry().Link(Port, "NOBODY").Should().BeNull();

    [Fact]
    public void Links_returns_every_known_peer()
    {
        var t = new NodeTelemetry();
        var other = Callsign.Parse("G0ABC");

        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "x"u8), At(0)));
        t.Observe(Port, Rx(Ax25Frame.Ui(Local, other, "y"u8), At(1)));

        t.Links().Select(l => l.Peer).Should().BeEquivalentTo([Peer.ToString(), other.ToString()]);
    }

    [Fact]
    public void DetachPort_clears_that_ports_counters()
    {
        var t = new NodeTelemetry();
        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "x"u8), At(0)));

        t.PortFrames(Port).Should().Be((1L, 0L));
        t.Link(Port, Peer.ToString()).Should().NotBeNull();

        t.DetachPort(Port);

        t.PortFrames(Port).Should().Be((0L, 0L));
        t.Link(Port, Peer.ToString()).Should().BeNull();
    }

    [Fact]
    public async Task Subscribe_delivers_observed_events_to_the_reader()
    {
        var t = new NodeTelemetry();
        using var sub = t.Subscribe(out var reader);
        t.SubscriberCount.Should().Be(1);

        t.Observe(Port, Rx(Ax25Frame.Ui(Local, Peer, "hi"u8), At(0)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var evt = await reader.ReadAsync(cts.Token);
        evt.PortId.Should().Be(Port);
        evt.Type.Should().Be("UI");
        evt.Source.Should().Be(Peer.ToString());
    }

    [Fact]
    public void Disposing_a_subscription_drops_the_subscriber_count()
    {
        var t = new NodeTelemetry();
        var sub = t.Subscribe(out _);
        t.SubscriberCount.Should().Be(1);

        sub.Dispose();

        t.SubscriberCount.Should().Be(0);
    }
}
