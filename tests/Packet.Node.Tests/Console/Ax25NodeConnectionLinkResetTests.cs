using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;
using Xunit;

namespace Packet.Node.Tests.Console;

/// <summary>
/// packet.net#885: a link reset that discards frames of ours ends the connection, so the
/// owner (an RHPv2 client, a console) hears of it at once instead of waiting on its own
/// timers; a reset with nothing of ours outstanding is carried through.
/// </summary>
[Trait("Category", "Node")]
public sealed class Ax25NodeConnectionLinkResetTests
{
    private static readonly Callsign NodeCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    private sealed class Rig : IAsyncDisposable
    {
        private int dropNodeIFrames;
        public Ax25Listener Node { get; private set; } = null!;
        public Ax25Listener Peer { get; private set; } = null!;
        public Ax25Session NodeSession { get; private set; } = null!;
        public Ax25Session PeerSession { get; private set; } = null!;
        public Ax25NodeConnection Connection { get; private set; } = null!;
        public List<byte[]> NodeSent { get; } = new();

        /// <summary>While set, the node's I frames are lost on the air, so what it writes stays unacknowledged.</summary>
        public bool DropNodeIFrames { set => Volatile.Write(ref dropNodeIFrames, value ? 1 : 0); }

        public static async Task<Rig> ConnectAsync()
        {
            var rig = new Rig();
            var bus = new SharedRadioBus();
            var nodeWire = bus.Attach(loseOutbound: bytes =>
            {
                lock (rig.NodeSent) { rig.NodeSent.Add(bytes.ToArray()); }
                return Volatile.Read(ref rig.dropNodeIFrames) != 0
                    && Ax25Frame.TryParse(bytes.Span, Ax25ParseOptions.Lenient, out var f)
                    && f.FrameType == Ax25FrameType.I;
            });
            var peerWire = bus.Attach();
            var t1 = TimeSpan.FromMilliseconds(400);
            rig.Node = new Ax25Listener(nodeWire, new Ax25ListenerOptions { MyCall = NodeCall, T1V = t1 });
            rig.Peer = new Ax25Listener(peerWire, new Ax25ListenerOptions { MyCall = PeerCall, T1V = t1 });
            Ax25Session? accepted = null;
            rig.Peer.SessionAccepted += (_, e) => Volatile.Write(ref accepted, e.Session);
            await rig.Node.StartAsync();
            await rig.Peer.StartAsync();
            rig.NodeSession = await rig.Node.ConnectAsync(PeerCall, NodeCall, extended: false, preConnectXidNegotiatesSrej: false)
                .WaitAsync(TimeSpan.FromSeconds(10));
            SpinWait.SpinUntil(() => Volatile.Read(ref accepted) is not null, TimeSpan.FromSeconds(5)).Should().BeTrue("the peer accepted the link");
            rig.PeerSession = Volatile.Read(ref accepted)!;
            rig.Connection = new Ax25NodeConnection(rig.Node, rig.NodeSession);
            return rig;
        }

        public int NodeSabms()
        {
            lock (NodeSent)
            {
                return NodeSent.Count(b => Ax25Frame.TryParse(b, Ax25ParseOptions.Lenient, out var f) && f.FrameType is Ax25FrameType.Sabm or Ax25FrameType.Sabme);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Node.DisposeAsync();
            await Peer.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_peers_reset_with_our_data_unacknowledged_ends_the_connection_and_says_why()
    {
        await using var rig = await Rig.ConnectAsync();

        // Our data is on the air but never reaches the peer, so it stays unacknowledged...
        rig.DropNodeIFrames = true;
        await rig.Connection.WriteAsync("hello\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.Context.VS != rig.NodeSession.Context.VA, TimeSpan.FromSeconds(5)).Should().BeTrue();

        // ...when the peer resets the link (a DL-CONNECT request on its live link, figc4.4 t07,
        // sends its SABM; on our up link that is figc4.4 t14's V(s) != V(a) branch: DL-ERROR F,
        // the queue discarded, DL-CONNECT indication).
        rig.PeerSession.PostEvent(new DlConnectRequest());

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Connection.EndReason.Should().Be("link reset by G7XYZ-7 with 1 frame(s) of ours unacknowledged");
        (await rig.Connection.ReadAsync()).IsEmpty.Should().BeTrue("the owner reads EOF");
    }

    [Fact]
    public async Task The_peers_reset_with_nothing_of_ours_outstanding_is_carried_through()
    {
        await using var rig = await Rig.ConnectAsync();

        await rig.Connection.WriteAsync("hello\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.AllSentDataAcknowledged, TimeSpan.FromSeconds(5)).Should().BeTrue("the peer acknowledged it");

        rig.PeerSession.PostEvent(new DlConnectRequest());
        SpinWait.SpinUntil(() => rig.NodeSent.Any(b => Ax25Frame.TryParse(b, Ax25ParseOptions.Lenient, out var f) && f.FrameType == Ax25FrameType.Ua), TimeSpan.FromSeconds(5))
            .Should().BeTrue("we answered the peer's SABM");

        rig.Connection.Completion.IsCompleted.Should().BeFalse("nothing of ours was lost, so the stream carries on");
        rig.Connection.EndReason.Should().BeNull();

        // And it does carry on: the next line goes over the re-established link.
        await rig.Connection.WriteAsync("again\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.AllSentDataAcknowledged && rig.NodeSession.Context.VS == 1, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the line was sent from N(S) = 0 on the reset link and acknowledged");
    }

    [Fact]
    public async Task Our_own_re_establishment_with_data_unacknowledged_ends_the_connection()
    {
        await using var rig = await Rig.ConnectAsync();

        rig.DropNodeIFrames = true;
        await rig.Connection.WriteAsync("hello\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.Context.VS != rig.NodeSession.Context.VA, TimeSpan.FromSeconds(5)).Should().BeTrue();

        // An FRMR from the peer (figc4.4 t16): DL-ERROR K, Establish Data Link; our unacknowledged
        // frame is gone with it.
        var frmr = Ax25Frame.Frmr(NodeCall, PeerCall, new byte[] { 0x00, 0x00, 0x00 }, finalBit: true);
        rig.NodeSession.PostEvent(new FrmrReceived(frmr));

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Connection.EndReason.Should().Be("link re-established after an unexpected UA or FRMR with 1 frame(s) of ours unacknowledged");
    }
}
