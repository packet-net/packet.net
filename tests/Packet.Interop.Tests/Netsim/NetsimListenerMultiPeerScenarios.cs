using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// Multi-peer interop scenarios driven through <see cref="Ax25Listener"/>,
/// exercising the per-peer session cache + multi-session routing across
/// the live net-sim AFSK1200 channel.
/// </summary>
/// <remarks>
/// <para>
/// The net-sim stack publishes node a's KISS-TCP on port 8100 (the
/// shared "ours" endpoint) and node b's on 8101. We use 8100 as the
/// listener-under-test and dial 8101 as the connecting peer in tests
/// that need a second peer on the air. The remaining nodes (c/d/e) are
/// bound to LinBPQ / XRouter / rax25 and have their own KISS-TCP ports
/// (8102/8103/8104) but those aren't published on the host — they're
/// reachable only inside docker. For listener-multi-peer-from-the-host
/// coverage 8100+8101 is enough.
/// </para>
/// <para>
/// The companion <see cref="NetsimListenerScenarios"/> tests the
/// single-peer connect/disconnect path end-to-end. This file focuses
/// on the multi-peer and reconnect-preserves-state behaviours.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class NetsimListenerMultiPeerScenarios
{
    private const string Host          = "127.0.0.1";
    private const int    ListenerPort  = 8100;
    private const int    PeerPort      = 8101;
    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // Headroom for a local state mutation / SessionAccepted callback to
    // settle after the remote signal implying it (e.g. the listener-side
    // session reaching Connected once the peer's ConnectAsync has returned,
    // or SessionAccepted having fired). Near-instant on an idle host;
    // matters only under CPU contention. WaitUntil returns as soon as the
    // predicate holds, so a generous budget costs nothing.
    private static readonly TimeSpan StateSettleBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Listener on node a accepts inbound from one peer on node b, then
    /// from a second peer on node b at a different callsign. Both
    /// SessionAccepted events fire and the listener emerges with two
    /// distinct cached sessions.
    /// </summary>
    /// <remarks>
    /// Net-sim's afsk1200 channel is shared — both fake peers ride the
    /// same KISS-TCP socket on 8101. The two peer sessions are
    /// distinguished by their source callsign only; net-sim faithfully
    /// broadcasts each frame to every linked node on the channel.
    /// </remarks>
    [Fact]
    public async Task Listener_Two_Peers_Both_Connect_To_Listener()
    {
        var listenerCall = new Callsign("PNLSTN", 0);
        var peer1Call    = new Callsign("PNPEER", 1);
        var peer2Call    = new Callsign("PNPEER", 2);

        using var cts = new CancellationTokenSource(ConnectBudget * 2 + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kissListener = await KissTcpClient.ConnectAsync(Host, ListenerPort, cts.Token);
        await using var kissPeer1    = await KissTcpClient.ConnectAsync(Host, PeerPort, cts.Token);
        await using var kissPeer2    = await KissTcpClient.ConnectAsync(Host, PeerPort, cts.Token);

        await using var listener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissListener), new Ax25ListenerOptions
        {
            MyCall = listenerCall,
        });
        await using var peer1Listener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissPeer1), new Ax25ListenerOptions
        {
            MyCall = peer1Call,
        });
        await using var peer2Listener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissPeer2), new Ax25ListenerOptions
        {
            MyCall = peer2Call,
        });

        // Listener-under-test accumulates accepted sessions by remote.
        var acceptedFromPeer1 = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedFromPeer2 = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.SessionAccepted += (_, e) =>
        {
            if (e.Session.Context.Remote.Equals(peer1Call)) acceptedFromPeer1.TrySetResult(e.Session);
            if (e.Session.Context.Remote.Equals(peer2Call)) acceptedFromPeer2.TrySetResult(e.Session);
        };

        await listener.StartAsync(cts.Token);
        await peer1Listener.StartAsync(cts.Token);
        await peer2Listener.StartAsync(cts.Token);

        // Settle so both inbound pumps are subscribed before SABM flies.
        await Task.Delay(300, cts.Token);

        // ─── Peer 1 connects ────────────────────────────────────────
        var sessionFromPeer1 = await peer1Listener.ConnectAsync(listenerCall, cts.Token);
        sessionFromPeer1.CurrentState.Should().Be("Connected");
        var listenerSidePeer1 = await acceptedFromPeer1.Task.WaitAsync(cts.Token);
        await WaitUntil(() => listenerSidePeer1.CurrentState == "Connected", TimeSpan.FromSeconds(5), cts.Token);

        // ─── Peer 2 connects ────────────────────────────────────────
        var sessionFromPeer2 = await peer2Listener.ConnectAsync(listenerCall, cts.Token);
        sessionFromPeer2.CurrentState.Should().Be("Connected");
        var listenerSidePeer2 = await acceptedFromPeer2.Task.WaitAsync(cts.Token);
        await WaitUntil(() => listenerSidePeer2.CurrentState == "Connected", TimeSpan.FromSeconds(5), cts.Token);

        listenerSidePeer1.Should().NotBeSameAs(listenerSidePeer2,
            "the listener must hold distinct Ax25Session instances per peer callsign");

        // ─── Clean disconnect on both ───────────────────────────────
        sessionFromPeer1.PostEvent(new DlDisconnectRequest());
        sessionFromPeer2.PostEvent(new DlDisconnectRequest());

        await WaitUntil(() => sessionFromPeer1.CurrentState == "Disconnected", DisconnectBudget, cts.Token);
        await WaitUntil(() => sessionFromPeer2.CurrentState == "Disconnected", DisconnectBudget, cts.Token);
    }

    /// <summary>
    /// Listener accepts inbound from peer, peer disconnects, peer
    /// reconnects. SessionAccepted fires twice; the second cached
    /// session is the same instance (per-peer cache preserved).
    /// </summary>
    [Fact]
    public async Task Listener_Reconnect_Preserves_Cached_Session()
    {
        var listenerCall = new Callsign("PNLSTN", 5);
        var peerCall     = new Callsign("PNPEER", 5);

        using var cts = new CancellationTokenSource(
            ConnectBudget * 2 + DisconnectBudget * 2 + TimeSpan.FromSeconds(20));

        await using var kissListener = await KissTcpClient.ConnectAsync(Host, ListenerPort, cts.Token);
        await using var kissPeer     = await KissTcpClient.ConnectAsync(Host, PeerPort, cts.Token);

        await using var listener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissListener), new Ax25ListenerOptions
        {
            MyCall = listenerCall,
        });
        await using var peerListener = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissPeer), new Ax25ListenerOptions
        {
            MyCall = peerCall,
        });

        var accepted = new List<Ax25Session>();
        var gate = new object();
        listener.SessionAccepted += (_, e) =>
        {
            lock (gate) accepted.Add(e.Session);
        };

        await listener.StartAsync(cts.Token);
        await peerListener.StartAsync(cts.Token);
        await Task.Delay(300, cts.Token);

        // ─── First connect ─────────────────────────────────────────
        var sessionFromPeer1 = await peerListener.ConnectAsync(listenerCall, cts.Token);
        sessionFromPeer1.CurrentState.Should().Be("Connected");
        await WaitUntil(() => { lock (gate) return accepted.Count >= 1; }, TimeSpan.FromSeconds(5), cts.Token);
        Ax25Session firstListenerSide;
        lock (gate) firstListenerSide = accepted[0];

        // Stamp the listener-side context with a probe value the SDL
        // doesn't reset on reconnect — SentIFrames is not touched by
        // t14's Clear_Exception_Conditions. (T1V is reset by t14.)
        firstListenerSide.Context.SentIFrames[7] =
            (new ReadOnlyMemory<byte>(new byte[] { 0xDE, 0xAD }), Ax25Frame.PidNoLayer3);

        // ─── Disconnect ────────────────────────────────────────────
        sessionFromPeer1.PostEvent(new DlDisconnectRequest());
        await WaitUntil(() => sessionFromPeer1.CurrentState == "Disconnected", DisconnectBudget, cts.Token);
        await WaitUntil(() => firstListenerSide.CurrentState == "Disconnected", DisconnectBudget, cts.Token);

        // ─── Reconnect ─────────────────────────────────────────────
        // ConnectAsync on the same listener with the same remote
        // reuses the cached session. The listener-under-test sees a
        // second SABM and re-fires SessionAccepted with the cached
        // instance.
        var sessionFromPeer2 = await peerListener.ConnectAsync(listenerCall, cts.Token);
        sessionFromPeer2.CurrentState.Should().Be("Connected");
        await WaitUntil(() => { lock (gate) return accepted.Count >= 2; }, TimeSpan.FromSeconds(5), cts.Token);

        Ax25Session secondListenerSide;
        lock (gate) secondListenerSide = accepted[1];

        secondListenerSide.Should().BeSameAs(firstListenerSide,
            "the listener's per-peer cache must hand back the same Ax25Session on reconnect");

        // The probe entry survived the disconnect/reconnect — the
        // session context really kept its state.
        secondListenerSide.Context.SentIFrames.Should().ContainKey((byte)7,
            "context state outside the SDL t14 reset list must persist across disconnect/reconnect");

        // Clean teardown.
        sessionFromPeer2.PostEvent(new DlDisconnectRequest());
        await WaitUntil(() => sessionFromPeer2.CurrentState == "Disconnected", DisconnectBudget, cts.Token);
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return;
            try { await Task.Delay(50, cts.Token); } catch (OperationCanceledException) { return; }
        }
    }
}
