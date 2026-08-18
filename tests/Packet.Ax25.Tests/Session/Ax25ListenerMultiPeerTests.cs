using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Xunit;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Multi-peer / cache-lifecycle coverage for <see cref="Ax25Listener"/>.
/// Verifies the per-peer session cache routes inbound frames correctly
/// across distinct peers, that LRU eviction past
/// <see cref="Ax25ListenerOptions.MaxCachedPeers"/> works, and that
/// <c>DisposeAsync</c> releases everything.
/// </summary>
public class Ax25ListenerMultiPeerTests
{
    private static readonly Callsign LocalCall = new("M0LTE", 0);

    // ─── Category 2: multi-peer as a node ───────────────────────────────

    /// <summary>
    /// While peer A is already Connected, peer B SABMs the listener.
    /// Both sessions must emerge as distinct instances and both fire
    /// SessionAccepted. The pre-existing peer-A session is unaffected.
    /// </summary>
    [Fact]
    public async Task Listener_Accepts_Second_Peer_While_First_Session_Active()
    {
        var peerA = new Callsign("G7AAA", 0);
        var peerB = new Callsign("G7BBB", 0);

        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var sessions = new ConcurrentDictionary<Callsign, Ax25Session>();
        var bothAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            sessions[e.Session.Context.Remote] = e.Session;
            if (sessions.Count >= 2)
            {
                bothAccepted.TrySetResult(true);
            }
        };

        await listener.StartAsync();

        // Peer A connects first.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerA));
        await ListenerTestSupport.WaitFor(() => sessions.ContainsKey(peerA), TimeSpan.FromSeconds(2));
        await ListenerTestSupport.WaitFor(() => sessions[peerA].CurrentState == "Connected", TimeSpan.FromSeconds(2));

        // Now peer B SABMs while A is still Connected.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerB));
        await bothAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));

        sessions[peerA].Should().NotBeSameAs(sessions[peerB], "distinct peers must get distinct sessions");
        sessions[peerA].CurrentState.Should().Be("Connected", "peer A's existing session must not be disturbed by peer B");
        sessions[peerB].CurrentState.Should().Be("Connected");
    }

    /// <summary>
    /// Two distinct peers, both Connected. Inbound I-frame from peer A
    /// must increment peer A's V(r) and post a DL-DATA-indication on
    /// peer A's session, NOT peer B's.
    /// </summary>
    [Fact]
    public async Task Listener_Routes_Frames_To_Correct_Session_With_Multiple_Peers()
    {
        var peerA = new Callsign("G7AAA", 0);
        var peerB = new Callsign("G7BBB", 0);

        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
        });

        var sessions = new ConcurrentDictionary<Callsign, Ax25Session>();
        var bothAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataFromA = new ConcurrentQueue<DataLinkDataIndication>();
        var dataFromB = new ConcurrentQueue<DataLinkDataIndication>();
        listener.SessionAccepted += (_, e) =>
        {
            sessions[e.Session.Context.Remote] = e.Session;
            // Wire per-session DL_DATA listeners so we can prove
            // routing went to the right session.
            if (e.Session.Context.Remote.Equals(peerA))
            {
                e.Session.DataLinkSignalEmitted += (_, sig) =>
                {
                    if (sig is DataLinkDataIndication d)
                    {
                        dataFromA.Enqueue(d);
                    }
                };
            }
            else if (e.Session.Context.Remote.Equals(peerB))
            {
                e.Session.DataLinkSignalEmitted += (_, sig) =>
                {
                    if (sig is DataLinkDataIndication d)
                    {
                        dataFromB.Enqueue(d);
                    }
                };
            }
            if (sessions.Count >= 2)
            {
                bothAccepted.TrySetResult(true);
            }
        };

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerA));
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerB));
        await bothAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));

        // Inject an I-frame from peer A with N(s)=0 (matches V(r)=0 on
        // a fresh session). Payload "HELLO-A". The listener routes via
        // source callsign, so only peer A's session should see it.
        var payloadA = System.Text.Encoding.ASCII.GetBytes("HELLO-A");
        modem.InjectInbound(Ax25Frame.I(LocalCall, peerA, nr: 0, ns: 0, info: payloadA, pollBit: false));

        await ListenerTestSupport.WaitFor(() => !dataFromA.IsEmpty, TimeSpan.FromSeconds(2),
            "I-frame from peer A must surface as DL-DATA-indication on peer A's session");
        dataFromA.TryPeek(out var aData).Should().BeTrue();
        aData!.Info.ToArray().Should().Equal(payloadA);
        dataFromB.Should().BeEmpty("peer B's session must not have received peer A's data");

        // And the reverse - payload from peer B routes to peer B only.
        var payloadB = System.Text.Encoding.ASCII.GetBytes("HELLO-B");
        modem.InjectInbound(Ax25Frame.I(LocalCall, peerB, nr: 0, ns: 0, info: payloadB, pollBit: false));

        await ListenerTestSupport.WaitFor(() => !dataFromB.IsEmpty, TimeSpan.FromSeconds(2));
        dataFromB.TryPeek(out var bData).Should().BeTrue();
        bData!.Info.ToArray().Should().Equal(payloadB);
        dataFromA.Count.Should().Be(1, "peer A's DL-DATA queue must be unchanged by peer B's frame");
    }

    /// <summary>
    /// Each cached session holds its own V(s)/V(a)/V(r). Inbound I-frames
    /// from two peers advance only their respective receive-state
    /// variables; the other peer's counters remain at zero.
    /// </summary>
    [Fact]
    public async Task Listener_Independent_Vs_Vr_Va_Per_Peer()
    {
        var peerA = new Callsign("G7AAA", 0);
        var peerB = new Callsign("G7BBB", 0);

        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var sessions = new ConcurrentDictionary<Callsign, Ax25Session>();
        var bothAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            sessions[e.Session.Context.Remote] = e.Session;
            if (sessions.Count >= 2)
            {
                bothAccepted.TrySetResult(true);
            }
        };

        await listener.StartAsync();
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerA));
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerB));
        await bothAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));

        // Both start at V(r)=0. Peer A sends two I-frames in sequence.
        modem.InjectInbound(Ax25Frame.I(LocalCall, peerA, nr: 0, ns: 0, info: new byte[] { 0x41 }, pollBit: false));
        modem.InjectInbound(Ax25Frame.I(LocalCall, peerA, nr: 0, ns: 1, info: new byte[] { 0x42 }, pollBit: false));

        await ListenerTestSupport.WaitFor(() => sessions[peerA].Context.VR == 2, TimeSpan.FromSeconds(2),
            "peer A's V(r) must advance to 2 after two in-sequence I-frames");
        sessions[peerB].Context.VR.Should().Be(0,
            "peer B's V(r) must remain 0 — frames from peer A must not bleed into peer B's state");
    }

    // ─── Category 3: cache lifecycle ────────────────────────────────────

    /// <summary>
    /// Peer disconnects then reconnects. Listener hands back the same
    /// <see cref="Ax25Session"/> instance both times; per-peer cache
    /// state (e.g. <see cref="Ax25SessionContext.Srt"/>) survives the
    /// disconnect interlude.
    /// </summary>
    [Fact]
    public async Task Listener_Reuses_Session_Across_Peer_Reconnects_Preserving_State()
    {
        var peerA = new Callsign("G7AAA", 0);

        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var accepted = new List<Ax25Session>();
        var gate = new object();
        listener.SessionAccepted += (_, e) =>
        {
            lock (gate)
            {
                accepted.Add(e.Session);
            }
        };

        await listener.StartAsync();

        // Connect 1.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerA));
        await ListenerTestSupport.WaitFor(() => { lock (gate) { return accepted.Count >= 1; } }, TimeSpan.FromSeconds(2));
        var first = accepted[0];
        first.CurrentState.Should().Be("Connected");

        // Stamp a value on the context that the SDL doesn't reset on
        // reconnect. Srt is updated by Select_T1_Value during normal
        // operation; the t14 chain sets `SRT := Initial Default` so
        // checking Srt is unreliable. Instead we use SrejExceptionCount
        // - figc4.1 t14 calls Clear_Exception_Conditions but that
        // resets SrejExceptionCount to 0. So both routes wipe it.
        //
        // The cleanest survivor across the reconnect is the session
        // instance itself (already asserted by the BeSameAs check below)
        // plus the IFrameQueue / SentIFrames / StoredReceivedIFrames
        // dictionaries - they are not reset by t14's
        // Clear_Exception_Conditions either (those are flag/seqvar
        // resets per §C4.3). Stick a probe entry into SentIFrames; on
        // reconnect, t14 does NOT call IFrameQueue.Clear() etc., so
        // the probe should persist.
        first.Context.SentIFrames[42] = (new ReadOnlyMemory<byte>(new byte[] { 0xAA }), Ax25Frame.PidNoLayer3);

        // Peer disconnects.
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, peerA));
        await ListenerTestSupport.WaitFor(() => first.CurrentState == "Disconnected", TimeSpan.FromSeconds(2));

        // Reconnect.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peerA));
        await ListenerTestSupport.WaitFor(() => { lock (gate) { return accepted.Count >= 2; } }, TimeSpan.FromSeconds(2));
        var second = accepted[1];
        second.Should().BeSameAs(first, "the cached session instance must be returned on reconnect");

        // The probe entry survived - the cache really kept the same
        // context, not just the same identity.
        second.Context.SentIFrames.Should().ContainKey((byte)42,
            "session-context state outside the SDL t14 reset list must persist across disconnect/reconnect");
    }

    /// <summary>
    /// LRU eviction: open <c>MaxCachedPeers + 1</c> sessions sequentially
    /// (each disconnects before the next opens). The oldest session
    /// drops out of the cache; reconnecting to that callsign builds a
    /// fresh <see cref="Ax25Session"/> instance (not the original).
    /// </summary>
    [Fact]
    public async Task Listener_Evicts_Oldest_Peer_Past_MaxCachedPeers()
    {
        const int cap = 3;
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            MaxCachedPeers = cap,
        });

        var firstAcceptedByPeer = new ConcurrentDictionary<Callsign, Ax25Session>();
        listener.SessionAccepted += (_, e) =>
        {
            // Capture only the FIRST session instance per peer. A
            // re-fire on reconnect must produce the same instance for
            // a still-cached peer, or a different one if the peer was
            // evicted.
            firstAcceptedByPeer.TryAdd(e.Session.Context.Remote, e.Session);
        };

        await listener.StartAsync();

        var peers = new List<Callsign>();
        for (int i = 0; i < cap + 1; i++)
        {
            peers.Add(new Callsign($"G7P{i:00}", 0));
        }

        // SABM-then-DISC each peer in turn, in order. Eviction policy
        // is LRU on "most-recently-touched" - sequential connects move
        // each new peer to the back of the queue; the oldest (peers[0])
        // gets evicted when the (cap+1)th peer connects.
        for (int i = 0; i < peers.Count; i++)
        {
            modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peers[i]));
            await ListenerTestSupport.WaitFor(
                () => firstAcceptedByPeer.ContainsKey(peers[i]), TimeSpan.FromSeconds(2),
                $"peer {peers[i]} must have triggered SessionAccepted");
            await ListenerTestSupport.WaitFor(
                () => firstAcceptedByPeer[peers[i]].CurrentState == "Connected", TimeSpan.FromSeconds(2));

            modem.InjectInbound(Ax25Frame.Disc(LocalCall, peers[i]));
            await ListenerTestSupport.WaitFor(
                () => firstAcceptedByPeer[peers[i]].CurrentState == "Disconnected", TimeSpan.FromSeconds(2));
        }

        // peers[0] should now be evicted. Reconnect to peers[0] - the
        // listener should build a NEW session instance (not the one
        // first captured).
        var oldFirst = firstAcceptedByPeer[peers[0]];

        // Reset the dict for peer[0] so we can grab the NEW session
        // reference from the re-SABM. (The dict's first-write-wins
        // protected the original capture.)
        firstAcceptedByPeer.TryRemove(peers[0], out _);

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, peers[0]));
        await ListenerTestSupport.WaitFor(
            () => firstAcceptedByPeer.ContainsKey(peers[0]), TimeSpan.FromSeconds(2));

        var newFirst = firstAcceptedByPeer[peers[0]];
        newFirst.Should().NotBeSameAs(oldFirst,
            "after eviction, reconnecting must build a fresh Ax25Session — the cache no longer held the original");
    }

    /// <summary>
    /// Same eviction scenario, focused on the fresh-state side: the
    /// rebuilt session starts with V(s)=V(r)=0, no I-frame queue
    /// contents from the previous incarnation, no stored received
    /// I-frames.
    /// </summary>
    [Fact]
    public async Task Listener_Evicted_Peer_Reconnect_Builds_Fresh_Session()
    {
        const int cap = 2;
        var modem = new LoopbackModem();
        await using var listener = new Ax25Listener(modem, new Ax25ListenerOptions
        {
            MyCall = LocalCall,
            MaxCachedPeers = cap,
        });

        var firstAccepted = new ConcurrentDictionary<Callsign, Ax25Session>();
        var allAccepted = new ConcurrentQueue<Ax25Session>();
        listener.SessionAccepted += (_, e) =>
        {
            firstAccepted.TryAdd(e.Session.Context.Remote, e.Session);
            allAccepted.Enqueue(e.Session);
        };

        await listener.StartAsync();

        var p0 = new Callsign("G7P00", 0);
        var p1 = new Callsign("G7P01", 0);
        var p2 = new Callsign("G7P02", 0);

        // Connect p0.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, p0));
        await ListenerTestSupport.WaitFor(() => firstAccepted.ContainsKey(p0), TimeSpan.FromSeconds(2));
        var s0Original = firstAccepted[p0];
        // Stash a probe entry - confirms freshness after eviction.
        s0Original.Context.SentIFrames[1] = (new ReadOnlyMemory<byte>(new byte[] { 0xCC }), Ax25Frame.PidNoLayer3);
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, p0));
        await ListenerTestSupport.WaitFor(() => s0Original.CurrentState == "Disconnected", TimeSpan.FromSeconds(2));

        // Push p1 and p2 to evict p0.
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, p1));
        await ListenerTestSupport.WaitFor(() => firstAccepted.ContainsKey(p1), TimeSpan.FromSeconds(2));
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, p1));
        await ListenerTestSupport.WaitFor(() => firstAccepted[p1].CurrentState == "Disconnected", TimeSpan.FromSeconds(2));

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, p2));
        await ListenerTestSupport.WaitFor(() => firstAccepted.ContainsKey(p2), TimeSpan.FromSeconds(2));

        // Now we should be at cap (2 cached: p1 + p2). p0 should be
        // evicted. Reconnect p0.
        firstAccepted.TryRemove(p0, out _);
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, p0));
        await ListenerTestSupport.WaitFor(() => firstAccepted.ContainsKey(p0), TimeSpan.FromSeconds(2));

        var s0Fresh = firstAccepted[p0];
        s0Fresh.Should().NotBeSameAs(s0Original);
        s0Fresh.Context.VS.Should().Be(0);
        s0Fresh.Context.VR.Should().Be(0);
        s0Fresh.Context.VA.Should().Be(0);
        s0Fresh.Context.SentIFrames.Should().BeEmpty(
            "the rebuilt session must NOT carry forward SentIFrames from the evicted incarnation");
    }

    /// <summary>
    /// DisposeAsync must tear down every cached session's scheduler so
    /// no timers are left running after the listener is gone.
    /// </summary>
    [Fact]
    public async Task Listener_DisposeAsync_Disposes_All_Cached_Sessions()
    {
        var modem = new LoopbackModem();
        var listener = new Ax25Listener(modem, new Ax25ListenerOptions { MyCall = LocalCall });

        var sessions = new ConcurrentBag<Ax25Session>();
        var twoAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            sessions.Add(e.Session);
            if (sessions.Count >= 2)
            {
                twoAccepted.TrySetResult(true);
            }
        };
        await listener.StartAsync();

        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, new Callsign("G7AAA", 0)));
        modem.InjectInbound(Ax25Frame.Sabm(LocalCall, new Callsign("G7BBB", 0)));
        await twoAccepted.Task.WithTimeout(TimeSpan.FromSeconds(2));

        // Bring both peers cleanly back to Disconnected before dispose
        // so we're not testing "abort an active session" - that's
        // covered separately.
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, new Callsign("G7AAA", 0)));
        modem.InjectInbound(Ax25Frame.Disc(LocalCall, new Callsign("G7BBB", 0)));

        // Dispose should complete promptly even with N cached sessions
        // (scheduler.Dispose() is the heaviest per-session cleanup).
        var disposeTask = listener.DisposeAsync().AsTask();
        var done = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(3)));
        done.Should().BeSameAs(disposeTask, "DisposeAsync must not hang with cached sessions in place");
        await disposeTask;

        // Calling DisposeAsync twice is a no-op.
        await listener.DisposeAsync();
    }
}
