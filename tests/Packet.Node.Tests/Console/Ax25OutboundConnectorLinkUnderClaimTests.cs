using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Console;

/// <summary>
/// packet.net#862: a dial that finds the link to the peer already up. If the peer's call
/// brought it up under the dial's claim (its accept was suppressed, nobody holds it), the dial
/// takes it as its own, crossed, with no SABM(E) of ours; if the link reached its owner before
/// the dial began, the dial is refused rather than resetting it.
/// </summary>
[Trait("Category", "Node")]
public sealed class Ax25OutboundConnectorLinkUnderClaimTests
{
    private static readonly Callsign NodeCall = new("N0BBB", 3);
    private static readonly Callsign PeerCall = new("N0AAA", 3);

    // A claim in the supervisor's shape: taken when the dial starts, told of the session the
    // peer's call brought up under it.
    private sealed class Claim : IOutboundClaim
    {
        public Ax25Session? LinkUnderClaim { get; set; }
        public bool LeavesALinkNobodyHolds => false;
        public bool Delivered { get; private set; }
        public void MarkDelivered() => Delivered = true;
        public void Dispose() { }
    }

    private static List<Ax25Frame> Sent(List<byte[]> frames) =>
        frames.Select(b => Ax25Frame.TryParse(b, Ax25ParseOptions.Lenient, out var f) ? f : null).Where(f => f is not null).Select(f => f!).ToList();

    [Fact]
    public async Task A_link_the_peers_call_brought_up_under_the_claim_is_the_dials_link()
    {
        var bus = new SharedRadioBus();
        var peerSent = new List<byte[]>();
        var nodeWire = bus.Attach();
        var peerWire = bus.Attach(loseOutbound: f => { lock (peerSent) { peerSent.Add(f.ToArray()); } return false; });
        var listener = new Ax25Listener(nodeWire, new Ax25ListenerOptions { MyCall = NodeCall, T1V = TimeSpan.FromSeconds(1) });
        await listener.StartAsync();
        await using var _ = listener;

        Ax25Session? accepted = null;
        listener.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);
        var nodeSent = new List<byte[]>();
        listener.FrameTraced += (_, e) => { if (e.Direction == FrameDirection.Transmitted) { lock (nodeSent) { nodeSent.Add(e.Frame.ToBytes()); } } };

        // The claim factory runs when the dial starts. The peer's call lands right then, the
        // listener answers it (figc4.1), and the claim records the session it brought up, as the
        // supervisor's SuppressIfOutbound does when it drops the accept under a claim.
        var claim = new Claim();
        var connector = new Ax25OutboundConnector("p1", listener, claim: _ =>
        {
            peerWire.SendAsync(Ax25Frame.Sabme(NodeCall, PeerCall).ToBytes()).GetAwaiter().GetResult();
            SpinWait.SpinUntil(() => Volatile.Read(ref accepted) is { CurrentState: "Connected" }, TimeSpan.FromSeconds(5))
                .Should().BeTrue("the peer's call is accepted");
            claim.LinkUnderClaim = Volatile.Read(ref accepted);
            return claim;
        });

        var connection = await connector.ConnectAsync(PeerCall, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        connection.Crossed.Should().BeTrue("the two calls crossed before ours went out");
        claim.Delivered.Should().BeTrue("the dial holds the link now");
        Sent(nodeSent).Count(f => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme).Should().Be(0,
            "the dial took the link the peer's call brought up instead of resetting it");
        Volatile.Read(ref accepted)!.CurrentState.Should().Be("Connected");
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task A_link_held_by_someone_else_is_not_dialled_onto()
    {
        var bus = new SharedRadioBus();
        var nodeWire = bus.Attach();
        var peerWire = bus.Attach();
        var listener = new Ax25Listener(nodeWire, new Ax25ListenerOptions { MyCall = NodeCall, T1V = TimeSpan.FromSeconds(1) });
        await listener.StartAsync();
        await using var _ = listener;
        Ax25Session? accepted = null;
        listener.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);
        var nodeSent = new List<byte[]>();
        listener.FrameTraced += (_, e) => { if (e.Direction == FrameDirection.Transmitted) { lock (nodeSent) { nodeSent.Add(e.Frame.ToBytes()); } } };

        // The peer's call reached its owner (an accept nobody suppressed) before any dial.
        await peerWire.SendAsync(Ax25Frame.Sabme(NodeCall, PeerCall).ToBytes());
        SpinWait.SpinUntil(() => Volatile.Read(ref accepted) is { CurrentState: "Connected" }, TimeSpan.FromSeconds(5)).Should().BeTrue();
        int sabmesBefore = Sent(nodeSent).Count(f => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme);

        var claim = new Claim();   // taken after the link was up: nothing under it
        var connector = new Ax25OutboundConnector("p1", listener, claim: _ => claim);

        var act = () => connector.ConnectAsync(PeerCall, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var ex = await act.Should().ThrowAsync<LinkAlreadyUpException>();
        ex.Which.Remote.Should().Be(PeerCall);
        ex.Which.Message.Should().Be("Already connected to N0AAA-3 from N0BBB-3.");
        Volatile.Read(ref accepted)!.CurrentState.Should().Be("Connected", "the owner's link is untouched");
        Sent(nodeSent).Count(f => f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme).Should().Be(sabmesBefore,
            "no SABM(E) went onto the owner's link");
        claim.Delivered.Should().BeFalse();
    }
}
