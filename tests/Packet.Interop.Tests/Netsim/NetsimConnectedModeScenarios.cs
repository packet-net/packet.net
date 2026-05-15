using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Netsim;

/// <summary>
/// Connected-mode interop scenarios across the net-sim AFSK1200 RF
/// simulator. Two <see cref="Ax25Session"/> instances run end-to-end —
/// one on each net-sim KISS-TCP port — and drive a full
/// connect-handshake / disconnect-handshake over the simulated radio
/// channel. This is the realistic transport for AX.25 (KISS to a TNC
/// modulating an analog RF carrier), not an IP-gateway shortcut.
/// </summary>
/// <remarks>
/// <para>
/// Topology: node A on port 8100 sends SABM, the AFSK1200 sim carries
/// it across the lossy(-ish) RF channel to net-sim's other node which
/// hands it to node B on port 8101. Node B's session observes
/// <see cref="SabmReceived"/>, runs figc4.1 t14, transitions to
/// Connected, emits UA. The UA reverses the path. Node A observes
/// <see cref="UaReceived"/>, runs figc4.2 t05, emits
/// <see cref="DataLinkConnectConfirm"/>, transitions to Connected.
/// </para>
/// <para>
/// Both sides use the real figc4.7 subroutine walker (no recorder
/// stubs). T1 / T3 use wall-clock time. Frame serialisation is via
/// <see cref="Ax25Frame.ToBytes"/> → <see cref="KissTcpClient.SendAsync"/>
/// — no skipped layers.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
public class NetsimConnectedModeScenarios
{
    private const string Host         = "127.0.0.1";
    private const int    NodeAKissPort = 8100;
    private const int    NodeBKissPort = 8101;
    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Connect_Then_Disconnect_Across_AFSK1200_Sim()
    {
        var nodeA = new Callsign("PNNODA", 1);
        var nodeB = new Callsign("PNNODB", 2);

        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kissA = await KissTcpClient.ConnectAsync(Host, NodeAKissPort, cts.Token);
        await using var kissB = await KissTcpClient.ConnectAsync(Host, NodeBKissPort, cts.Token);

        // Both sessions: real walker, real subroutine registry, frame-
        // aware bindings, recorders that capture upward DL signals so
        // the test can await DataLinkConnectConfirm / DisconnectConfirm.
        var rigA = BuildRig(local: nodeA, remote: nodeB, kiss: kissA);
        var rigB = BuildRig(local: nodeB, remote: nodeA, kiss: kissB);

        // Inbound pumps: every KISS frame from net-sim becomes an
        // Ax25Event into the matching session.
        var pumpA = Task.Run(() => InboundPump(rigA, cts.Token), cts.Token);
        var pumpB = Task.Run(() => InboundPump(rigB, cts.Token), cts.Token);

        // Brief settle so net-sim's per-port TX queue is ready before
        // we fire SABM.
        await Task.Delay(200, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rigA.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rigA.Signals, ConnectBudget, cts.Token);
        connectConfirm.Should().NotBeNull("node A must observe UA(F=1) from node B and emit DL-CONNECT-confirm");
        rigA.Session.CurrentState.Should().Be("Connected");

        // Node B saw SABM, replied UA, and transitioned. Give the
        // post-state mutations a beat to settle before asserting.
        await WaitUntil(() => rigB.Session.CurrentState == "Connected",
            TimeSpan.FromSeconds(5), cts.Token);
        rigB.Session.CurrentState.Should().Be("Connected");

        // ─── Disconnect ─────────────────────────────────────────────
        rigA.Session.PostEvent(new DlDisconnectRequest());

        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rigA.Signals, DisconnectBudget, cts.Token);
        disconnectConfirm.Should().NotBeNull("node A must observe UA(F=1) to its DISC and emit DL-DISCONNECT-confirm");
        rigA.Session.CurrentState.Should().Be("Disconnected");

        await WaitUntil(() => rigB.Session.CurrentState == "Disconnected",
            TimeSpan.FromSeconds(5), cts.Token);
        rigB.Session.CurrentState.Should().Be("Disconnected");

        cts.Cancel();
        try { await Task.WhenAll(pumpA, pumpB); } catch (OperationCanceledException) { }
    }

    // ─── Rig ────────────────────────────────────────────────────────

    private sealed record Rig(
        Ax25Session Session,
        SystemTimerScheduler Scheduler,
        ConcurrentQueue<DataLinkSignal> Signals,
        KissTcpClient Kiss);

    private static Rig BuildRig(Callsign local, Callsign remote, KissTcpClient kiss)
    {
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        var ctx = new Ax25SessionContext { Local = local, Remote = remote };
        var signals = new ConcurrentQueue<DataLinkSignal>();

        // sendBytes fans out to a fire-and-forget KISS send. KISS port
        // 0 is the convention for "this radio" on a single-port modem;
        // KissCommand.Data is the plain-frame data type.
        void SendBytes(ReadOnlyMemory<byte> bytes)
            => _ = kiss.SendAsync(port: 0, KissCommand.Data, bytes);

        Ax25Session? sessionRef = null;
        var subroutines = new DefaultSubroutineRegistry();
        var dispatcher = new ActionDispatcher(
            onTimerExpiry: name => sessionRef!.PostEvent(TimerExpiry(name)),
            sendSFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUiFrame:   spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendIFrame:    spec => SendBytes(spec.ToAx25Frame(ctx).ToBytes()),
            sendUpward:    signals.Enqueue,
            sendLinkMux:   _ => { },
            sendInternal:  _ => { },
            subroutines:   subroutines);

        // figc4.1 t14/t15 (SABM_received) gates on `able_to_establish`
        // — "is this peer willing to accept the incoming connection".
        // CreateDefault doesn't ship a binding for it; the production
        // answer would consult node policy (callsign allow-list, link
        // budget, etc.). For an interop test we always accept.
        var defaultBindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var bindings = new Dictionary<string, Func<bool>>(defaultBindings, StringComparer.Ordinal)
        {
            ["able_to_establish"] = () => true,
        };
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;
        return new Rig(session, scheduler, signals, kiss);
    }

    private static async Task InboundPump(Rig rig, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<KissFrame> frames;
            try { frames = await rig.Kiss.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (System.IO.IOException) { return; }   // connection torn down

            foreach (var f in frames)
            {
                if (f.Command != KissCommand.Data) continue;
                if (!Ax25Frame.TryParse(f.Payload, out var parsed)) continue;

                // Address filter: we should only react to frames
                // addressed to our local callsign. net-sim's RF sim
                // is broadcast — both ports hear everything.
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;

                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]         = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
        ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
    };

    private static Ax25Event TimerExpiry(string name) => name switch
    {
        "T1" => new T1Expiry(),
        "T2" => new T2Expiry(),
        "T3" => new T3Expiry(),
        _    => throw new InvalidOperationException($"unexpected timer expiry name '{name}'"),
    };

    private static async Task<T?> WaitForSignal<T>(
        ConcurrentQueue<DataLinkSignal> signals,
        TimeSpan budget,
        CancellationToken outer) where T : DataLinkSignal
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            while (signals.TryDequeue(out var sig))
            {
                if (sig is T match) return match;
            }
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan budget, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return;
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }
}
