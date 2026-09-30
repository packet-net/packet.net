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
/// timers; a reset with nothing of ours outstanding is carried through. The session counts
/// what a reset threw away (<see cref="DataLinkResetIndication"/>): frames still queued as
/// well as the sent, unacknowledged window.
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

        public bool NodeSentA(Ax25FrameType type)
        {
            lock (NodeSent)
            {
                return NodeSent.Any(b => Ax25Frame.TryParse(b, Ax25ParseOptions.Lenient, out var f) && f.FrameType == type);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await Node.DisposeAsync();
            await Peer.DisposeAsync();
        }
    }

    private static async Task WriteUnacknowledgedAsync(Rig rig, string line)
    {
        rig.DropNodeIFrames = true;
        await rig.Connection.WriteAsync(System.Text.Encoding.ASCII.GetBytes(line));
        SpinWait.SpinUntil(() => rig.NodeSession.Context.VS != rig.NodeSession.Context.VA, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the line is on the air and unacknowledged");
    }

    [Fact]
    public async Task The_peers_reset_with_our_data_unacknowledged_ends_the_connection_and_says_why()
    {
        await using var rig = await Rig.ConnectAsync();
        await WriteUnacknowledgedAsync(rig, "hello\r");

        // The peer resets the link (a DL-CONNECT request on its live link, figc4.4 t07, sends its
        // SABM; on our up link that is figc4.4 t14's V(s) != V(a) branch).
        rig.PeerSession.PostEvent(new DlConnectRequest());

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Connection.EndReason.Should().Be("link reset by G7XYZ-7 with 1 frame(s) of ours undelivered (1 sent and unacknowledged, 0 queued)");
        (await rig.Connection.ReadAsync()).IsEmpty.Should().BeTrue("the owner reads EOF");

        // The owner's close then disconnects the link at once: it already lost data, and a
        // graceful drain would have nothing to drain.
        await rig.Connection.DisposeAsync();
        SpinWait.SpinUntil(() => rig.NodeSentA(Ax25FrameType.Disc), TimeSpan.FromSeconds(5)).Should().BeTrue("DISC goes out now");
    }

    [Fact]
    public async Task Frames_still_queued_count_too()
    {
        // The peer is busy (RNR), so what the owner writes stays queued, never sent. A reset
        // discards that queue inside Clear Exception Conditions, before the figures raise any
        // indication, so a V(s) / V(a) check would miss it; the session's count does not.
        await using var rig = await Rig.ConnectAsync();
        rig.PeerSession.PostEvent(new DlFlowOffRequest());
        SpinWait.SpinUntil(() => rig.NodeSession.Context.PeerReceiverBusy, TimeSpan.FromSeconds(5)).Should().BeTrue("the peer's RNR reached us");

        await rig.Connection.WriteAsync("queued\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.Context.IFrameQueue.Count == 1, TimeSpan.FromSeconds(5)).Should().BeTrue("the line waits in the queue");
        rig.NodeSession.Context.VS.Should().Be(rig.NodeSession.Context.VA, "nothing went on the air");

        rig.PeerSession.PostEvent(new DlConnectRequest());

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Connection.EndReason.Should().Be("link reset by G7XYZ-7 with 1 frame(s) of ours undelivered (0 sent and unacknowledged, 1 queued)");
    }

    [Fact]
    public async Task The_peers_reset_with_nothing_of_ours_outstanding_is_carried_through()
    {
        await using var rig = await Rig.ConnectAsync();

        await rig.Connection.WriteAsync("hello\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.AllSentDataAcknowledged, TimeSpan.FromSeconds(5)).Should().BeTrue("the peer acknowledged it");

        rig.PeerSession.PostEvent(new DlConnectRequest());
        SpinWait.SpinUntil(() => rig.NodeSentA(Ax25FrameType.Ua), TimeSpan.FromSeconds(5)).Should().BeTrue("we answered the peer's SABM");

        rig.Connection.Completion.IsCompleted.Should().BeFalse("nothing of ours was lost, so the stream carries on");
        rig.Connection.EndReason.Should().BeNull();

        // And it does carry on: the next line goes over the re-established link from N(S) = 0.
        await rig.Connection.WriteAsync("again\r"u8.ToArray());
        SpinWait.SpinUntil(() => rig.NodeSession.AllSentDataAcknowledged && rig.NodeSession.Context.VS == 1, TimeSpan.FromSeconds(5))
            .Should().BeTrue("the line was sent from N(S) = 0 on the reset link and acknowledged");
    }

    [Fact]
    public async Task Our_own_re_establishment_with_data_unacknowledged_ends_the_connection()
    {
        await using var rig = await Rig.ConnectAsync();
        await WriteUnacknowledgedAsync(rig, "hello\r");

        // An FRMR from the peer (figc4.4 t16): DL-ERROR K, Establish Data Link. The queue goes
        // in Clear Exception Conditions; the sent, unacknowledged frame goes when the peer's UA
        // to our SABM zeroes V(s) and V(a).
        var frmr = Ax25Frame.Frmr(NodeCall, PeerCall, new byte[] { 0x00, 0x00, 0x00 }, finalBit: true);
        rig.NodeSession.PostEvent(new FrmrReceived(frmr));

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Connection.EndReason.Should().Be("link re-established after an unexpected UA or FRMR with 1 frame(s) of ours undelivered (1 sent and unacknowledged, 0 queued)");
    }

    [Fact]
    public async Task A_frame_error_reset_with_data_unacknowledged_ends_the_connection()
    {
        // figc4.4 t09: a control field error re-establishes the link (DL-ERROR L). Same loss,
        // a code the DL-ERROR letters alone would have missed.
        await using var rig = await Rig.ConnectAsync();
        await WriteUnacknowledgedAsync(rig, "hello\r");

        rig.NodeSession.PostEvent(new ControlFieldError());

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Connection.EndReason.Should().StartWith("link re-established after a frame error with 1 frame(s) of ours undelivered");
    }

    [Fact]
    public async Task Acknowledgements_across_the_sequence_wrap_are_not_a_reset()
    {
        // At the wrap V(s) and V(a) both come back to 0 on an ordinary acknowledgement; the
        // window count must key on the peer's SABM(E), not on the zeroes (found in review).
        await using var rig = await Rig.ConnectAsync();

        for (int i = 0; i < 9; i++)
        {
            await rig.Connection.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"line {i}\r"));
            SpinWait.SpinUntil(() => rig.NodeSession.AllSentDataAcknowledged, TimeSpan.FromSeconds(5))
                .Should().BeTrue($"line {i} is acknowledged");
        }

        rig.NodeSession.Context.VS.Should().Be(1, "nine frames on a mod-8 link wrapped once");
        rig.Connection.Completion.IsCompleted.Should().BeFalse("nothing was lost");
        rig.Connection.EndReason.Should().BeNull();
        rig.NodeSentA(Ax25FrameType.Disc).Should().BeFalse();
    }

    [Fact]
    public async Task A_peers_restart_taken_by_a_fresh_owner_is_not_disconnected_by_the_old_owners_close()
    {
        // The #850 hand-over, on the reset's own signal: the caller started the link over
        // while we had output unacknowledged, so this connection ends, and if the supervisor
        // finds the restarted link an owner (a console, an accept, a waiting dial), this
        // owner's close must not DISC the link that is now theirs.
        await using var rig = await Rig.ConnectAsync();
        int offered = 0;
        rig.Connection.PeerRestartedAfterClose = () => { Interlocked.Increment(ref offered); return true; };
        await WriteUnacknowledgedAsync(rig, "hello\r");

        rig.PeerSession.PostEvent(new DlConnectRequest());

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        offered.Should().Be(1, "the restarted link was offered to a fresh owner");
        await rig.Connection.DisposeAsync();
        await Task.Delay(300);
        rig.NodeSentA(Ax25FrameType.Disc).Should().BeFalse("the link belongs to its new owner");
        rig.NodeSession.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task An_old_owner_cannot_write_onto_the_link_the_reset_took_from_it()
    {
        // Between the reset signal and its own dispose the old owner may still write (a console
        // finishing a command, say); that must not go out on the restarted link, which may be a
        // fresh owner's now.
        await using var rig = await Rig.ConnectAsync();
        rig.Connection.PeerRestartedAfterClose = () => true;
        await WriteUnacknowledgedAsync(rig, "hello\r");

        rig.PeerSession.PostEvent(new DlConnectRequest());
        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        rig.DropNodeIFrames = false;
        int sentBefore;
        lock (rig.NodeSent) { sentBefore = rig.NodeSent.Count; }

        await rig.Connection.WriteAsync(System.Text.Encoding.ASCII.GetBytes("stale tail\r"));
        await Task.Delay(300);

        rig.NodeSession.Context.VS.Should().Be(0, "nothing of the old owner's went out on the restarted link");
        lock (rig.NodeSent)
        {
            rig.NodeSent.Skip(sentBefore).Any(b => Ax25Frame.TryParse(b, Ax25ParseOptions.Lenient, out var f) && f.FrameType == Ax25FrameType.I)
                .Should().BeFalse();
        }
    }

    [Fact]
    public async Task A_peers_restart_nobody_takes_is_disconnected_by_the_old_owners_close()
    {
        await using var rig = await Rig.ConnectAsync();
        rig.Connection.PeerRestartedAfterClose = () => false;
        await WriteUnacknowledgedAsync(rig, "hello\r");

        rig.PeerSession.PostEvent(new DlConnectRequest());

        await rig.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Connection.DisposeAsync();
        SpinWait.SpinUntil(() => rig.NodeSentA(Ax25FrameType.Disc), TimeSpan.FromSeconds(5)).Should().BeTrue("nobody took it, so it ends");
    }

    [Fact]
    public async Task An_interlink_NET_ROM_is_using_rides_the_reset_out()
    {
        await using var rig = await Rig.ConnectAsync();
        rig.Connection.KeepOnLinkReset = () => true;
        await WriteUnacknowledgedAsync(rig, "hello\r");

        rig.PeerSession.PostEvent(new DlConnectRequest());
        SpinWait.SpinUntil(() => rig.NodeSentA(Ax25FrameType.Ua), TimeSpan.FromSeconds(5)).Should().BeTrue("we answered the peer's SABM");

        rig.Connection.Completion.IsCompleted.Should().BeFalse("NET/ROM recovers its own frames; the console's connection stays");
        rig.Connection.EndReason.Should().BeNull();
    }
}
