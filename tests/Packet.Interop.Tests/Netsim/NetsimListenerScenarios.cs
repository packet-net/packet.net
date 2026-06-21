using AwesomeAssertions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// Listener-driven connected-mode interop across the net-sim AFSK1200
/// RF simulator. Mirrors <see cref="NetsimConnectedModeScenarios"/> but
/// exercises the <see cref="Ax25Listener"/> path on both ends instead
/// of hand-rolling the inbound pump / dispatcher rig per-node.
/// </summary>
/// <remarks>
/// <para>
/// Topology: node A on KISS-TCP port 8100 runs an
/// <see cref="Ax25Listener"/> with <c>PNNODA-1</c>; node B on KISS-TCP
/// port 8101 runs one with <c>PNNODB-2</c>. Node A's listener kicks an
/// outbound <see cref="Ax25Listener.ConnectAsync"/> at node B; the
/// AFSK1200 sim carries SABM over the RF channel; node B's listener
/// observes inbound SABM, accepts (fires <c>SessionAccepted</c>), and
/// emits UA back. Node A's <see cref="Ax25Listener.ConnectAsync"/>
/// resolves once DL-CONNECT-confirm arrives.
/// </para>
/// <para>
/// The pre-listener scenario (<see cref="NetsimConnectedModeScenarios"/>)
/// remains as the canonical low-level rig reference — useful for tests
/// that need to drive specific transitions or interrogate session state
/// at points the listener hides.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class NetsimListenerScenarios
{
    private const string Host         = "127.0.0.1";
    private const int    NodeAKissPort = 8100;
    private const int    NodeBKissPort = 8101;
    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // Headroom for a local state mutation to settle after the remote signal
    // implying it has already been observed (e.g. the accepting side
    // reaching Connected once ConnectAsync — which only resolves on
    // DL-CONNECT-confirm — has returned). Resolves near-instantly on an idle
    // host; the budget only bites under CPU contention. WaitUntil returns as
    // soon as the predicate holds, so a generous value costs nothing.
    private static readonly TimeSpan StateSettleBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Listener_Connect_Then_Disconnect_Across_AFSK1200_Sim()
    {
        var nodeA = new Callsign("PNNODA", 1);
        var nodeB = new Callsign("PNNODB", 2);

        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kissA = await KissTcpClient.ConnectAsync(Host, NodeAKissPort, cts.Token);
        await using var kissB = await KissTcpClient.ConnectAsync(Host, NodeBKissPort, cts.Token);

        await using var listenerA = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissA), new Ax25ListenerOptions { MyCall = nodeA });
        await using var listenerB = new Ax25Listener(new Packet.Kiss.KissModemTransport(kissB), new Ax25ListenerOptions { MyCall = nodeB });

        Ax25Session? acceptedOnB = null;
        var bAccepted = new TaskCompletionSource<Ax25Session>(TaskCreationOptions.RunContinuationsAsynchronously);
        listenerB.SessionAccepted += (_, e) =>
        {
            acceptedOnB = e.Session;
            bAccepted.TrySetResult(e.Session);
        };

        await listenerA.StartAsync(cts.Token);
        await listenerB.StartAsync(cts.Token);

        // Brief settle so the listeners' pumps are subscribed before
        // we fire SABM.
        await Task.Delay(200, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        var sessionA = await listenerA.ConnectAsync(nodeB, cts.Token);
        sessionA.CurrentState.Should().Be("Connected",
            "ConnectAsync resolves only after DL-CONNECT-confirm — session must be Connected by then");

        // The accepting side runs concurrently; await its SessionAccepted.
        var sessionB = await bAccepted.Task.WaitAsync(cts.Token);
        await WaitUntil(() => sessionB.CurrentState == "Connected", TimeSpan.FromSeconds(5), cts.Token);
        sessionB.CurrentState.Should().Be("Connected");

        // ─── Disconnect ─────────────────────────────────────────────
        var bDisconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionB.DataLinkSignalEmitted += (_, sig) =>
        {
            if (sig is DataLinkDisconnectIndication or DataLinkDisconnectConfirm)
            {
                bDisconnected.TrySetResult(true);
            }
        };
        var aDisconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionA.DataLinkSignalEmitted += (_, sig) =>
        {
            if (sig is DataLinkDisconnectConfirm or DataLinkDisconnectIndication)
            {
                aDisconnected.TrySetResult(true);
            }
        };

        sessionA.PostEvent(new DlDisconnectRequest());

        await aDisconnected.Task.WaitAsync(DisconnectBudget, cts.Token);
        sessionA.CurrentState.Should().Be("Disconnected");

        await bDisconnected.Task.WaitAsync(DisconnectBudget, cts.Token);
        await WaitUntil(() => sessionB.CurrentState == "Disconnected", TimeSpan.FromSeconds(5), cts.Token);
        sessionB.CurrentState.Should().Be("Disconnected");
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
