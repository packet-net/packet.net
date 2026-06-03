using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
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
[Collection(NetsimCollection.Name)]
public class NetsimConnectedModeScenarios
{
    private const string Host         = "127.0.0.1";
    private const int    NodeAKissPort = 8100;
    private const int    NodeBKissPort = 8101;
    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // net-sim's afsk1200 channel is a single shared half-duplex medium with
    // collision_mode: silence (see docker/netsim/network.yaml) — if both
    // ends key up at once, BOTH transmissions are lost and recovery waits
    // for a T1 retransmit cycle. The spec-default ack timer (T2 = 1500 ms)
    // and retransmit timer (T1V = 6 s after connect) make each lost-frame
    // recovery slow, so under host-CPU contention a couple of unlucky
    // collisions can blow the per-direction budget before a frame lands.
    // We shorten the ack timer here so the receiver turns the channel
    // around quickly (smaller contention window, faster quiescence) without
    // changing protocol semantics — a real TNC on a quiet channel runs a
    // short FRACK/RESPTIME too. Retransmit (T1) recovery still happens via
    // the normal SDL path; we just don't rely on burning many 6 s cycles.
    private static readonly TimeSpan AckTimer = TimeSpan.FromMilliseconds(600);

    // Headroom for a *local* state mutation to settle after the remote
    // signal that implies it has already been observed (e.g. B reaching
    // Connected once A has its DL-CONNECT-confirm — B's UA must already be
    // on the wire). These resolve near-instantly when the host is idle; the
    // budget only matters under heavy CPU contention. WaitUntil returns as
    // soon as the predicate holds, so a generous budget costs nothing.
    private static readonly TimeSpan StateSettleBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Connect_Then_Disconnect_Across_AFSK1200_Sim()
    {
        var nodeA = new Callsign("PNNODA", 1);
        var nodeB = new Callsign("PNNODB", 2);

        // Outer token must exceed the sum of the inner step budgets so it
        // can't cut an inner wait short and turn a slow-but-OK run into a
        // confusing cancellation: connect + 2× state-settle + disconnect.
        using var cts = new CancellationTokenSource(
            ConnectBudget + DisconnectBudget + StateSettleBudget + StateSettleBudget + TimeSpan.FromSeconds(15));

        await using var kissA = await KissTcpClient.ConnectAsync(Host, NodeAKissPort, cts.Token);
        await using var kissB = await KissTcpClient.ConnectAsync(Host, NodeBKissPort, cts.Token);

        // Both sessions: real walker, real subroutine registry, frame-
        // aware bindings, recorders that capture upward DL signals so
        // the test can await DataLinkConnectConfirm / DisconnectConfirm.
        var rigA = BuildRig(local: nodeA, remote: nodeB, kiss: kissA);
        var rigB = BuildRig(local: nodeB, remote: nodeA, kiss: kissB);

        // Inbound pumps: every KISS frame from net-sim becomes an
        // Ax25Event into the matching session. The pump tasks must be
        // observed by the wait helpers below — if a pump throws (e.g.
        // GuardEvaluationException from an unbound predicate), we want
        // the test to fail immediately with the real exception, not
        // sit for ConnectBudget seconds waiting for a signal that can
        // never arrive.
        var pumps = new[]
        {
            Task.Run(() => InboundPump(rigA, cts.Token), cts.Token),
            Task.Run(() => InboundPump(rigB, cts.Token), cts.Token),
        };

        // Brief settle so net-sim's per-port TX queue is ready before
        // we fire SABM.
        await Task.Delay(200, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rigA.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rigA.Signals, ConnectBudget, pumps, cts.Token);
        connectConfirm.Should().NotBeNull("node A must observe UA(F=1) from node B and emit DL-CONNECT-confirm");
        rigA.Session.CurrentState.Should().Be("Connected");

        // Node B saw SABM, replied UA, and transitioned. Give the
        // post-state mutations a beat to settle before asserting.
        await WaitUntil(() => rigB.Session.CurrentState == "Connected",
            StateSettleBudget, pumps, cts.Token);
        rigB.Session.CurrentState.Should().Be("Connected");

        // ─── Disconnect ─────────────────────────────────────────────
        rigA.Session.PostEvent(new DlDisconnectRequest());

        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rigA.Signals, DisconnectBudget, pumps, cts.Token);
        disconnectConfirm.Should().NotBeNull("node A must observe UA(F=1) to its DISC and emit DL-DISCONNECT-confirm");
        rigA.Session.CurrentState.Should().Be("Disconnected");

        await WaitUntil(() => rigB.Session.CurrentState == "Disconnected",
            StateSettleBudget, pumps, cts.Token);
        rigB.Session.CurrentState.Should().Be("Disconnected");

        cts.Cancel();
        try { await Task.WhenAll(pumps); } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task IFrame_Round_Trip_Across_AFSK1200_Sim()
    {
        // Connected-mode data round-trip between two of our sessions over
        // the netsim RF channel. Closes the gap left by the connect/disconnect
        // test above — that asserts the handshake state machine; this asserts
        // I-frame TX/RX with V(s)/V(r)/V(a) bookkeeping flowing end-to-end.
        //
        // SP-005 force-multiplier: any divergence between V(s) on the sender
        // and V(r) on the receiver across a real RF channel surfaces a state-
        // machine bug we'd miss with single-session tests.
        var nodeA = new Callsign("PNIFRA", 1);
        var nodeB = new Callsign("PNIFRB", 2);

        // dataBudget bounds each *frame-arrival* wait (the I-frame must
        // cross the RF channel, possibly after a collision + T1 retransmit).
        // The quiescence gates are local-state convergence checks that
        // complete within one ack round-trip, so they get the smaller
        // StateSettleBudget. The outer token is sized to exceed the sum of
        // every inner step so it can never cut one short: connect + settle +
        // two frame waits + four quiescence gates + disconnect + settle.
        var dataBudget = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(
            ConnectBudget
            + dataBudget + dataBudget                          // two I-frame arrivals
            + StateSettleBudget + StateSettleBudget            // post-connect + post-disconnect settle
            + StateSettleBudget + StateSettleBudget            // four quiescence gates…
            + StateSettleBudget + StateSettleBudget            // …(2 after each direction)
            + DisconnectBudget
            + TimeSpan.FromSeconds(20));

        await using var kissA = await KissTcpClient.ConnectAsync(Host, NodeAKissPort, cts.Token);
        await using var kissB = await KissTcpClient.ConnectAsync(Host, NodeBKissPort, cts.Token);

        var rigA = BuildRig(local: nodeA, remote: nodeB, kiss: kissA);
        var rigB = BuildRig(local: nodeB, remote: nodeA, kiss: kissB);

        var pumps = new[]
        {
            Task.Run(() => InboundPump(rigA, cts.Token), cts.Token),
            Task.Run(() => InboundPump(rigB, cts.Token), cts.Token),
        };

        await Task.Delay(200, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rigA.Session.PostEvent(new DlConnectRequest());
        (await WaitForSignal<DataLinkConnectConfirm>(rigA.Signals, ConnectBudget, pumps, cts.Token))
            .Should().NotBeNull("connect handshake should complete");
        await WaitUntil(() => rigB.Session.CurrentState == "Connected",
            StateSettleBudget, pumps, cts.Token);

        // On a shared half-duplex channel (collision_mode: silence) we drive
        // the two directions strictly one at a time, and between them wait
        // for the *sending* side's link to go quiescent — every sent I-frame
        // acknowledged (V(s) == V(a)) and no ack of its own still pending —
        // before the other side keys up. That keeps the two endpoints from
        // transmitting fresh I-frames into each other (which would collide
        // and be silenced, then need slow T1 recovery). It also strengthens
        // the test: reaching quiescence proves the V(s)/V(r)/V(a) bookkeeping
        // actually converged end-to-end, not merely that one frame arrived.

        // ─── I-frame A → B ──────────────────────────────────────────
        // Drain any signals queued during connect (e.g. DL-CONNECT-indication
        // on rigB) so the WaitForSignal<DataLinkDataIndication> below isn't
        // satisfied by an unrelated leftover.
        while (rigB.Signals.TryDequeue(out _)) { }

        var payloadAB = System.Text.Encoding.ASCII.GetBytes("hello from A");
        rigA.Session.PostEvent(new DlDataRequest(payloadAB, Pid: Ax25Frame.PidNoLayer3));

        var indicationAB = await WaitForSignal<DataLinkDataIndication>(rigB.Signals, dataBudget, pumps, cts.Token);
        indicationAB.Should().NotBeNull("node B must observe the I-frame from node A as DL-DATA-indication");
        indicationAB!.Info.ToArray().Should().Equal(payloadAB);
        indicationAB.Pid.Should().Be(Ax25Frame.PidNoLayer3);

        // Let the A→B exchange settle: A's I-frame must be acknowledged by B
        // and any RR B owes must have flushed, so the channel is idle before
        // B transmits in the other direction.
        await WaitForQuiescence(rigA.Session, StateSettleBudget, pumps, cts.Token);
        await WaitForQuiescence(rigB.Session, StateSettleBudget, pumps, cts.Token);

        // ─── I-frame B → A ──────────────────────────────────────────
        while (rigA.Signals.TryDequeue(out _)) { }

        var payloadBA = System.Text.Encoding.ASCII.GetBytes("ack from B");
        rigB.Session.PostEvent(new DlDataRequest(payloadBA, Pid: Ax25Frame.PidNoLayer3));

        var indicationBA = await WaitForSignal<DataLinkDataIndication>(rigA.Signals, dataBudget, pumps, cts.Token);
        indicationBA.Should().NotBeNull("node A must observe the I-frame from node B");
        indicationBA!.Info.ToArray().Should().Equal(payloadBA);

        // Settle the B→A exchange before tearing down so the DISC doesn't
        // race a still-in-flight RR/I-frame on the channel.
        await WaitForQuiescence(rigB.Session, StateSettleBudget, pumps, cts.Token);
        await WaitForQuiescence(rigA.Session, StateSettleBudget, pumps, cts.Token);

        // ─── Disconnect ─────────────────────────────────────────────
        rigA.Session.PostEvent(new DlDisconnectRequest());
        (await WaitForSignal<DataLinkDisconnectConfirm>(rigA.Signals, DisconnectBudget, pumps, cts.Token))
            .Should().NotBeNull("disconnect handshake should complete");
        await WaitUntil(() => rigB.Session.CurrentState == "Disconnected",
            StateSettleBudget, pumps, cts.Token);

        cts.Cancel();
        try { await Task.WhenAll(pumps); } catch (OperationCanceledException) { }
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
            subroutines:   subroutines)
        {
            // Shorten the ack timer so RR-acks turn the half-duplex channel
            // around quickly — see AckTimer remarks. T1/T3 keep spec defaults.
            T2Duration = AckTimer,
        };

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
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
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"]            = DataLink_Connected.Transitions,
        ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]        = DataLink_TimerRecovery.Transitions,
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
        IReadOnlyList<Task> backgroundTasks,
        CancellationToken outer) where T : DataLinkSignal
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(backgroundTasks);
            while (signals.TryDequeue(out var sig))
            {
                if (sig is T match) return match;
            }
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    private static async Task WaitUntil(
        Func<bool> condition,
        TimeSpan budget,
        IReadOnlyList<Task> backgroundTasks,
        CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(backgroundTasks);
            if (condition()) return;
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Wait until <paramref name="session"/>'s connected-mode link is
    /// quiescent: every I-frame it has sent is acknowledged
    /// (V(s) == V(a)) and it owes no pending acknowledgement of its own
    /// (<see cref="Ax25SessionContext.AcknowledgePending"/> is clear). On
    /// the shared half-duplex sim this is the signal that the side has
    /// finished talking and the channel is free for the other end — used
    /// to serialise the two I-frame directions so they don't collide.
    /// Reads are lock-free and eventually-consistent against the inbound
    /// pump thread, which is fine for a poll: a stale read just costs one
    /// extra 50 ms iteration.
    /// </summary>
    private static Task WaitForQuiescence(
        Ax25Session session,
        TimeSpan budget,
        IReadOnlyList<Task> backgroundTasks,
        CancellationToken outer)
        => WaitUntil(
            () => session.Context.VS == session.Context.VA
                  && !session.Context.AcknowledgePending,
            budget, backgroundTasks, outer);

    /// <summary>
    /// If any of the supplied background tasks has faulted, rethrow
    /// its exception. Lets a wait-helper turn a backgrounded crash
    /// (e.g. an unbound predicate throwing inside the rx pump) into
    /// an immediate test failure with the real stack trace, rather
    /// than a budget timeout that hides the cause.
    /// </summary>
    private static void ThrowIfAnyFaulted(IReadOnlyList<Task> tasks)
    {
        foreach (var t in tasks)
        {
            if (t.IsFaulted)
            {
                // GetBaseException unwraps the AggregateException so
                // the assertion stack trace points at the real
                // throwing line, not the task plumbing.
                throw t.Exception?.GetBaseException()
                    ?? new InvalidOperationException("background task faulted with no exception attached");
            }
        }
    }
}
