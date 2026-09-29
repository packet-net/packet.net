using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
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
/// packet.net#850: closing an app's or the console's AX.25 connection must not throw away what
/// it wrote just before. <see cref="Ax25NodeConnection.DisposeAsync"/> ends the local side at
/// once but sends DISC only when the I-frame queue is empty and every I-frame is acknowledged;
/// <see cref="Ax25NodeConnection.AbortAsync"/> (sysop kill, shutdown) still disconnects at once.
/// Two real listeners talk over an in-memory channel the tests can hold (a slow link) or make
/// lossy, with short timers so recovery runs in well under a second per retry.
/// </summary>
public sealed class Ax25NodeConnectionGracefulCloseTests
{
    private static readonly Callsign NodeCall = new("M0LTE", 0);
    private static readonly Callsign PeerCall = new("G7XYZ", 7);

    // K=2 so a few lines are enough to leave data queued behind a full window.
    private static readonly TimeSpan T1 = TimeSpan.FromMilliseconds(300);
    private const int N2 = 6;

    private static readonly string[] Lines =
        Enumerable.Range(1, 6).Select(i => $"line {i} of the tail the app wrote before it closed\r").ToArray();

    private static byte[] Tail => Encoding.ASCII.GetBytes(string.Concat(Lines));

    [Fact]
    public async Task Dispose_on_a_slow_link_delivers_the_whole_tail_before_DISC()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        rig.Wire.HoldNodeToPeer = true;   // everything the node sends sits on the air for now
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.DisposeAsync();

        conn.Completion.IsCompleted.Should().BeTrue("the local side ends at once");
        rig.NodeSession.CurrentState.Should().Be("Connected", "the link waits for the tail to be acknowledged");
        rig.NodeSession.Context.IFrameQueue.Should().NotBeEmpty("K=2, so most of the tail is still queued");
        rig.Wire.NodeSent.Should().NotContain(f => IsDisc(f), "no DISC while data is undelivered");

        // Writes after the close are refused, not queued.
        var queued = rig.NodeSession.Context.IFrameQueue.Count;
        await conn.WriteAsync(Encoding.ASCII.GetBytes("too late\r"));
        rig.NodeSession.Context.IFrameQueue.Should().HaveCount(queued);

        rig.Wire.HoldNodeToPeer = false;

        await Wait.ForAsync(() => rig.PeerDisconnected, "the link ends once the tail is delivered");
        rig.PeerReceived.Should().Equal(Tail, "every byte written before the close reached the peer, in order");
        rig.PeerDisconnectedByDisc.Should().BeTrue("the node ended the link with DISC, not a failure");
        AssertDiscAfterLastIFrame(rig);
        await Wait.ForAsync(() => rig.NodeSession.CurrentState == "Disconnected", "the node side settles");
    }

    [Fact]
    public async Task Dispose_on_a_lossy_link_retransmits_the_tail_then_sends_DISC()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        // Lose the first transmission of every odd-numbered I-frame, and the first two RRs back.
        var seen = new ConcurrentDictionary<int, int>();
        int rrsDropped = 0;
        rig.Wire.DropNodeToPeer = f =>
            IsIFrame(f, out var ns) && ns % 2 == 1 && seen.AddOrUpdate(ns, 1, (_, n) => n + 1) == 1;
        rig.Wire.DropPeerToNode = f =>
            Ax25FrameClassifier.Classify(f) is RrReceived && Interlocked.Increment(ref rrsDropped) <= 2;

        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.DisposeAsync();

        await Wait.ForAsync(() => rig.PeerDisconnected, "the link ends once the tail is recovered and delivered");
        rig.PeerReceived.Should().Equal(Tail, "the lost frames were retransmitted before the DISC");
        rig.PeerDisconnectedByDisc.Should().BeTrue();
        AssertDiscAfterLastIFrame(rig);
    }

    [Fact]
    public async Task Dispose_in_timer_recovery_waits_for_recovery_then_sends_DISC()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        // Starve the node of acknowledgements until its T1 runs out and it is in Timer Recovery.
        rig.Wire.DropPeerToNode = _ => true;
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await Wait.ForAsync(() => rig.NodeSession.CurrentState == "TimerRecovery", "T1 expires with I-frames outstanding");

        await conn.DisposeAsync();
        rig.Wire.NodeSent.Should().NotContain(f => IsDisc(f));

        rig.Wire.DropPeerToNode = null;
        await Wait.ForAsync(() => rig.PeerDisconnected, "recovery completes, the tail goes, then DISC");
        rig.PeerReceived.Should().Equal(Tail);
        rig.PeerDisconnectedByDisc.Should().BeTrue();
        AssertDiscAfterLastIFrame(rig);
    }

    [Fact]
    public async Task Dispose_while_the_peer_is_busy_waits_until_it_clears()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        // The peer's receiver goes busy (it sends RNR), so the node must hold its data.
        rig.PeerSession!.PostEvent(new DlFlowOffRequest());
        await Wait.ForAsync(() => rig.NodeSession.Context.PeerReceiverBusy, "the node hears the RNR");

        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        int rnrBefore = rig.Wire.PeerSent.Count(IsPollAnsweredWithRnr);
        await conn.DisposeAsync();

        // The node polls the busy peer and it answers RNR: the close is waiting for it, not
        // cutting it off (well inside the retry budget a busy peer gets).
        await Wait.ForAsync(() => rig.Wire.PeerSent.Count(IsPollAnsweredWithRnr) > rnrBefore,
            "the busy peer answers a poll with RNR");
        rig.Wire.NodeSent.Should().NotContain(f => IsDisc(f), "a busy peer is waited for, not cut off");
        rig.NodeSession.CurrentState.Should().BeOneOf("Connected", "TimerRecovery");
        rig.PeerDisconnected.Should().BeFalse();

        rig.PeerSession.PostEvent(new DlFlowOnRequest());
        await Wait.ForAsync(() => rig.PeerDisconnected, "the peer clears, the tail goes, then DISC");
        rig.PeerReceived.Should().Equal(Tail);
        rig.PeerDisconnectedByDisc.Should().BeTrue();
        AssertDiscAfterLastIFrame(rig);
    }

    [Fact]
    public async Task A_peer_still_busy_after_the_retry_budget_gets_DISC_and_a_redial_then_proceeds()
    {
        await using var rig = await Rig.ConnectAsync();
        var connector = new Ax25OutboundConnector("p1", rig.Node);
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        rig.PeerSession!.PostEvent(new DlFlowOffRequest());   // busy, and it never clears
        await Wait.ForAsync(() => rig.NodeSession.Context.PeerReceiverBusy, "the node hears the RNR");
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }

        // The budget a silent peer would get: N2 retries of T1 with its backoff.
        var budget = Ax25GracefulClose.BusyBudget(rig.NodeSession.Context);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await conn.DisposeAsync();
        var redial = connector.ConnectAsync(PeerCall);

        await Wait.ForAsync(() => rig.Wire.NodeSent.Exists(IsDisc), "the DISC goes once the budget is spent");
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(budget - TimeSpan.FromMilliseconds(50),
            "the busy peer had the whole budget first");
        rig.Wire.PeerSent.Should().Contain(f => IsPollAnsweredWithRnr(f), "the peer was still answering RNR");
        rig.PeerReceived.Length.Should().BeLessThan(Tail.Length, "what the busy peer would not take is discarded");

        // The redial waited behind the close and now goes ahead.
        await using var second = await redial.WaitAsync(Wait.DefaultBudget);
        rig.NodeSession.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Dispose_when_the_peer_has_gone_ends_via_the_link_retry_limit()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        // The peer vanishes: nothing the node sends is heard, nothing comes back.
        rig.Wire.DropNodeToPeer = _ => true;
        rig.Wire.DropPeerToNode = _ => true;
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }

        var disposed = conn.DisposeAsync();
        disposed.IsCompleted.Should().BeTrue("the close never waits on the peer");
        await disposed;

        // T1 x N2 of polling, then the link gives up on its own (no extra timer of ours).
        await Wait.ForAsync(() => rig.NodeSession.CurrentState == "Disconnected",
            "T1 running out N2 times ends the link", TimeSpan.FromSeconds(30));
        rig.Wire.NodeSent.Should().NotContain(f => IsDisc(f),
            "nothing was ever acknowledged, so the close never got as far as sending DISC");
    }

    [Fact]
    public async Task Dispose_with_nothing_outstanding_sends_DISC_straight_away()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        await conn.WriteAsync(Encoding.ASCII.GetBytes(Lines[0]));
        await Wait.ForAsync(() => rig.NodeSession.AllSentDataAcknowledged, "the one line is acknowledged");

        await conn.DisposeAsync();
        await Wait.ForAsync(() => rig.PeerDisconnected, "DISC goes at once");
        rig.PeerReceived.Should().Equal(Encoding.ASCII.GetBytes(Lines[0]));
    }

    [Fact]
    public async Task Abort_disconnects_at_once_and_discards_the_queue()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        rig.Wire.HoldNodeToPeer = true;
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.AbortAsync();

        conn.Completion.IsCompleted.Should().BeTrue();
        await Wait.ForAsync(() => rig.Wire.NodeSent.Exists(IsDisc), "a kill sends DISC now");
        rig.NodeSession.Context.IFrameQueue.Should().BeEmpty("the DL-DISCONNECT request discards the queue");

        rig.Wire.HoldNodeToPeer = false;
        await Wait.ForAsync(() => rig.PeerDisconnected, "the peer gets the DISC");
        rig.PeerReceived.Length.Should().BeLessThan(Tail.Length, "the queued tail was discarded");
    }

    [Fact]
    public async Task Abort_after_dispose_cuts_the_pending_close_short()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        rig.Wire.HoldNodeToPeer = true;
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.DisposeAsync();
        rig.Wire.NodeSent.Should().NotContain(f => IsDisc(f));

        await conn.AbortAsync();
        await Wait.ForAsync(() => rig.Wire.NodeSent.Exists(IsDisc), "the abort sends the DISC the close was waiting to send");
        rig.NodeSession.CurrentState.Should().Be("AwaitingRelease");
    }

    [Fact]
    public async Task Port_teardown_sends_the_DISC_for_a_pending_close()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        rig.Wire.HoldNodeToPeer = true;
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.DisposeAsync();

        Ax25GracefulClose.DisconnectAllNow(rig.Node);
        await Wait.ForAsync(() => rig.Wire.NodeSent.Exists(IsDisc), "shutdown does not wait for the tail");
        rig.NodeSession.CurrentState.Should().Be("AwaitingRelease");
    }

    [Fact]
    public async Task A_new_dial_to_the_same_peer_waits_for_the_pending_close_and_is_not_ended_by_it()
    {
        await using var rig = await Rig.ConnectAsync();
        var connector = new Ax25OutboundConnector("p1", rig.Node);
        var first = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        rig.Wire.HoldNodeToPeer = true;
        foreach (var line in Lines)
        {
            await first.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await first.DisposeAsync();

        int sentBeforeDial = rig.Wire.NodeSent.Count;
        var dial = connector.ConnectAsync(PeerCall);

        // The old link times out waiting for its (held) acks and polls: time has passed with the
        // dial started, and it has neither completed nor sent a SABM of its own.
        await Wait.ForAsync(() => rig.Wire.NodeSent.Skip(sentBeforeDial).Any(IsPoll), "the old link polls for its acks");
        dial.IsCompleted.Should().BeFalse("the dial waits for the old link's tail and DISC");
        rig.Wire.NodeSent.Skip(sentBeforeDial).Should().NotContain(f => IsSabm(f), "the dial has not started");

        rig.Wire.HoldNodeToPeer = false;
        await using var second = await dial.WaitAsync(Wait.DefaultBudget);

        rig.PeerReceived.Should().Equal(Tail, "the old link's tail was delivered before the new dial reset anything");
        rig.PeerDisconnectedByDisc.Should().BeTrue("the old link ended with its DISC");

        // The new link stays up: the old close is finished and does not DISC it.
        await second.WriteAsync(Encoding.ASCII.GetBytes("second\r"));
        await Wait.ForAsync(() => rig.NodeSession.AllSentDataAcknowledged, "the new link carries data");
        // A stale close would have posted its DISC inside the very dispatch that acknowledged the
        // data, and AllSentDataAcknowledged is read under that dispatch's lock, so the state is
        // already settled here.
        rig.NodeSession.CurrentState.Should().Be("Connected", "the old close is finished and leaves the new link alone");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_close_during_a_link_reset_takes_effect_once_the_reset_settles(bool abort)
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);

        // An unexpected UA resets the link: the node sends SABM and waits in Awaiting Connection.
        // Hold the SABM on the air so the close lands mid-reset.
        rig.Wire.HoldNodeToPeer = true;
        rig.Wire.InjectToNode(Ax25Frame.Ua(NodeCall, PeerCall, finalBit: false));
        await Wait.ForAsync(() => rig.NodeSession.CurrentState is "AwaitingConnection" or "AwaitingV22Connection",
            "the unexpected UA starts a reset");

        if (abort)
        {
            await conn.AbortAsync();
        }
        else
        {
            await conn.DisposeAsync();
        }

        rig.Wire.HoldNodeToPeer = false;

        // The peer answers the SABM, the link is Connected again, and the close then sends DISC
        // rather than leaving a link nobody owns.
        await Wait.ForAsync(() => rig.Wire.NodeSent.Exists(IsDisc), "the close sends DISC once the reset settles");
        await Wait.ForAsync(() => rig.NodeSession.CurrentState == "Disconnected", "the link ends");
    }

    [Fact]
    public async Task A_peer_restart_during_the_drain_goes_to_a_fresh_owner_not_a_DISC()
    {
        await using var rig = await Rig.ConnectAsync();
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession)
        {
            PeerRestartedAfterClose = () => restarted.TrySetResult(),
        };

        // None of the node's I-frames get through, so the close is still waiting...
        rig.Wire.DropNodeToPeer = f => IsIFrame(f, out _);
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.DisposeAsync();
        rig.Wire.DropNodeToPeer = null;

        // ...when the peer starts the link over.
        rig.PeerSession!.PostEvent(new DlConnectRequest());
        await restarted.Task.WaitAsync(Wait.DefaultBudget);

        // A fresh owner (a new console, in the node) takes the link, and it carries data.
        var fresh = new Ax25NodeConnection(rig.Node, rig.NodeSession);
        await Wait.ForAsync(() => rig.NodeSession.CurrentState == "Connected" && rig.PeerSession.CurrentState == "Connected",
            "both ends are connected again");
        await fresh.WriteAsync(Encoding.ASCII.GetBytes("welcome back\r"));
        await Wait.ForAsync(() => rig.NodeSession.AllSentDataAcknowledged && rig.PeerReceived.Length > 0,
            "the new owner's data is delivered");
        Encoding.ASCII.GetString(rig.PeerReceived).Should().EndWith("welcome back\r");
        rig.Wire.NodeSent.Should().NotContain(f => IsDisc(f), "the old close did not end the peer's new link");
        rig.NodeSession.CurrentState.Should().Be("Connected");
    }

    [Fact]
    public async Task Without_a_fresh_owner_a_peer_restart_during_the_drain_ends_with_DISC()
    {
        await using var rig = await Rig.ConnectAsync();
        var conn = new Ax25NodeConnection(rig.Node, rig.NodeSession);   // an outbound link: no hand-on

        rig.Wire.DropNodeToPeer = f => IsIFrame(f, out _);
        foreach (var line in Lines)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes(line));
        }
        await conn.DisposeAsync();
        rig.Wire.DropNodeToPeer = null;

        rig.PeerSession!.PostEvent(new DlConnectRequest());
        await Wait.ForAsync(() => rig.Wire.NodeSent.Exists(IsDisc), "nobody else owns it, so the close still ends it");
    }

    private static bool IsDisc(Ax25Frame f) => Ax25FrameClassifier.Classify(f) is DiscReceived;

    private static bool IsSabm(Ax25Frame f) => Ax25FrameClassifier.Classify(f) is SabmReceived or SabmeReceived;

    // A supervisory command with P=1: the node asking the peer for its state (T1 or T3 expiry).
    private static bool IsPoll(Ax25Frame f) =>
        f.IsCommand && f.PollFinal && Ax25FrameClassifier.Classify(f) is RrReceived or RnrReceived or RejReceived;

    private static bool IsPollAnsweredWithRnr(Ax25Frame f) =>
        !f.IsCommand && f.PollFinal && Ax25FrameClassifier.Classify(f) is RnrReceived;

    private static bool IsIFrame(Ax25Frame f, out int ns)
    {
        ns = -1;
        if (Ax25FrameClassifier.Classify(f) is not IFrameReceived)
        {
            return false;
        }
        ns = (f.Control >> 1) & 0x07;   // mod-8: the rig never negotiates v2.2
        return true;
    }

    // The DISC went out after the last I-frame the node sent, and after the peer's last
    // acknowledgement reached the node: the close waited for delivery, not just transmission.
    private static void AssertDiscAfterLastIFrame(Rig rig)
    {
        var sent = rig.Wire.NodeSent;
        int disc = sent.FindIndex(IsDisc);
        disc.Should().BeGreaterThanOrEqualTo(0, "the node sent DISC");
        int lastI = sent.FindLastIndex(f => IsIFrame(f, out _));
        disc.Should().BeGreaterThan(lastI, "no I-frame follows the DISC");
    }

    /// <summary>A node listener and a peer listener over a <see cref="ControlledWire"/>, with the
    /// node's link to the peer up and the peer side recording what it receives.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<byte> received = new();
        private int disconnected;
        private int disconnectedByDisc;

        private Rig(Ax25Listener node, Ax25Listener peer, ControlledWire wire)
        {
            Node = node;
            Peer = peer;
            Wire = wire;
        }

        public Ax25Listener Node { get; }
        public Ax25Listener Peer { get; }
        public ControlledWire Wire { get; }
        public Ax25Session NodeSession { get; private set; } = null!;
        public Ax25Session? PeerSession { get; private set; }

        public byte[] PeerReceived => received.ToArray();
        public bool PeerDisconnected => Volatile.Read(ref disconnected) != 0;
        public bool PeerDisconnectedByDisc => Volatile.Read(ref disconnectedByDisc) != 0;

        public static async Task<Rig> ConnectAsync()
        {
            var wire = new ControlledWire();
            var options = (Callsign call) => new Ax25ListenerOptions
            {
                MyCall = call,
                T1V = T1,
                T3 = TimeSpan.FromMilliseconds(600),
                N2 = N2,
                K = 2,
                PreferExtendedConnect = false,
                PreConnectXidNegotiatesSrej = false,
            };
            var node = new Ax25Listener(wire.NodeEnd, options(NodeCall));
            var peer = new Ax25Listener(wire.PeerEnd, options(PeerCall));
            var rig = new Rig(node, peer, wire);

            peer.SessionAccepted += (_, e) =>
            {
                // A second accept (the re-dial test) re-fires for the same cached peer session,
                // which is already attached.
                Volatile.Write(ref rig.disconnected, 0);
                if (!ReferenceEquals(rig.PeerSession, e.Session))
                {
                    rig.PeerSession = e.Session;
                    e.Session.AttachConsumerWithReplay(rig.OnPeerSignal);
                }
            };
            await node.StartAsync();
            await peer.StartAsync();
            peer.AcceptIncoming = true;

            rig.NodeSession = await node.ConnectAsync(PeerCall, NodeCall).WaitAsync(Wait.DefaultBudget);
            await Wait.ForAsync(() => rig.PeerSession is not null, "the peer accepted the link");
            return rig;
        }

        private void OnPeerSignal(object? sender, DataLinkSignal signal)
        {
            switch (signal)
            {
                case DataLinkDataIndication di:
                    foreach (var b in di.Info.Span)
                    {
                        received.Enqueue(b);
                    }
                    break;
                case DataLinkDisconnectIndication:
                    // Raised on the peer by the node's DISC (figc4.4 DISC received) and on the
                    // peer's own give-up; tell them apart by whether a DISC crossed the wire.
                    if (Wire.NodeSent.Exists(IsDisc))
                    {
                        Volatile.Write(ref disconnectedByDisc, 1);
                    }
                    Volatile.Write(ref disconnected, 1);
                    break;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            await Peer.DisposeAsync();
        }
    }

    /// <summary>
    /// Two in-memory transports joined back to back. Node-to-peer frames can be held (a slow
    /// channel: they wait, then go in order) or dropped by predicate; peer-to-node frames can be
    /// dropped by predicate. Everything the node transmits is logged, held or not.
    /// </summary>
    private sealed class ControlledWire
    {
        private readonly object gate = new();
        private readonly List<Ax25Frame> nodeSent = new();
        private readonly List<Ax25Frame> peerSent = new();
        private readonly Queue<byte[]> held = new();
        private bool holdNodeToPeer;

        public ControlledWire()
        {
            NodeEnd = new End(this, fromNode: true);
            PeerEnd = new End(this, fromNode: false);
        }

        public End NodeEnd { get; }
        public End PeerEnd { get; }

        public Func<Ax25Frame, bool>? DropNodeToPeer { get; set; }
        public Func<Ax25Frame, bool>? DropPeerToNode { get; set; }

        public bool HoldNodeToPeer
        {
            set
            {
                lock (gate)
                {
                    holdNodeToPeer = value;
                    if (!value)
                    {
                        while (held.TryDequeue(out var frame))
                        {
                            PeerEnd.Deliver(frame);
                        }
                    }
                }
            }
        }

        public List<Ax25Frame> PeerSent
        {
            get
            {
                lock (gate)
                {
                    return peerSent.ToList();
                }
            }
        }

        /// <summary>Put a frame on the air towards the node as if the peer had sent it.</summary>
        public void InjectToNode(Ax25Frame frame) => NodeEnd.Deliver(frame.ToBytes());

        public List<Ax25Frame> NodeSent
        {
            get
            {
                lock (gate)
                {
                    return nodeSent.ToList();
                }
            }
        }

        private void Carry(bool fromNode, byte[] bytes)
        {
            if (!Ax25Frame.TryParse(bytes, out var frame) || frame is null)
            {
                return;
            }

            lock (gate)
            {
                if (fromNode)
                {
                    nodeSent.Add(frame);
                    if (DropNodeToPeer?.Invoke(frame) == true)
                    {
                        return;
                    }
                    if (holdNodeToPeer)
                    {
                        held.Enqueue(bytes);
                        return;
                    }
                    PeerEnd.Deliver(bytes);
                }
                else
                {
                    peerSent.Add(frame);
                    if (DropPeerToNode?.Invoke(frame) == true)
                    {
                        return;
                    }
                    NodeEnd.Deliver(bytes);
                }
            }
        }

        public sealed class End(ControlledWire wire, bool fromNode) : IAx25Transport, ICsmaChannelParams
        {
            private readonly Channel<Ax25InboundFrame> rx =
                Channel.CreateUnbounded<Ax25InboundFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            internal void Deliver(byte[] bytes) => rx.Writer.TryWrite(new Ax25InboundFrame(bytes, 0, DateTimeOffset.UtcNow));

            public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
            {
                wire.Carry(fromNode, ax25.ToArray());
                return Task.CompletedTask;
            }

            public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                while (await rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (rx.Reader.TryRead(out var f))
                    {
                        yield return f;
                    }
                }
            }

            public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) => Task.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
