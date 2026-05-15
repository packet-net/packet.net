using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// Connected-mode interop against the real LinBPQ container with net-sim
/// providing the RF link between us. Our <see cref="Ax25Session"/> attaches
/// to net-sim's KISS-TCP on port 8100 (node a); LinBPQ dials net-sim's
/// other listener on 8102 (node c) from inside docker, configured by its
/// second PORT block in <c>docker/linbpq/bpq32.cfg</c>. The afsk1200 sim
/// bridges frames between the two endpoints.
/// </summary>
/// <remarks>
/// <para>
/// This is the first interop test with a third-party AX.25 stack as the
/// remote peer rather than another instance of our own session. The
/// assertion is intentionally minimal — handshake only — to keep the
/// blast radius small as we add more BPQ-driven scenarios later. The
/// test sends SABM addressed to PN0TST (BPQ's NODECALL), expects BPQ to
/// reply UA and put us in Connected; then sends DISC and expects UA back
/// + Disconnected. AGW / telnet probes of BPQ's heard-list or per-port
/// counters are deferred to a follow-up.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// Compose's <c>depends_on netsim healthy</c> ensures BPQ's KISS dial
/// finds the listener on first attempt; BPQ retries regardless if
/// startup races.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class LinbpqViaNetsimConnectedMode
{
    private const string Host             = "127.0.0.1";
    private const int    OurKissPort      = 8100;
    private static readonly Callsign OurCall = new("PNTEST", 0);
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Connect_Then_Disconnect_Against_Linbpq_Across_Netsim()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: BpqCall, kiss: kiss);

        var pumps = new[]
        {
            Task.Run(() => InboundPump(rig, cts.Token), cts.Token),
        };

        // Brief settle so net-sim's per-port TX queue is ready before
        // we fire SABM. BPQ's KISS dial may still be racing in but its
        // retry behaviour absorbs that — we don't need to wait for it.
        await Task.Delay(500, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rig.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps, cts.Token);
        connectConfirm.Should().NotBeNull("LinBPQ must accept SABM addressed to its NODECALL (PN0TST) and reply UA, taking us to Connected");
        rig.Session.CurrentState.Should().Be("Connected");

        // ─── Disconnect ─────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());

        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps, cts.Token);
        disconnectConfirm.Should().NotBeNull("LinBPQ must reply UA to our DISC, taking us to Disconnected");
        rig.Session.CurrentState.Should().Be("Disconnected");

        cts.Cancel();
        try { await Task.WhenAll(pumps); } catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Beyond the handshake: drive the figc4.4 Connected state's data path
    /// end-to-end against BPQ's node prompt. After SABM/UA, BPQ emits a
    /// welcome banner as one or more I-frames; we collect at least one
    /// DataLinkDataIndication, then send "P\r" (ports command) as an
    /// outbound I-frame and wait for BPQ's response indication. Finally
    /// DISC/UA to close the link cleanly. This exercises t18 (DL-DATA
    /// push), t19/t20 (I-frame TX with V(s)++ and T1 management), t14-t16
    /// (I-frame RX with N(s)/N(r) accounting), the upward data plumbing
    /// via sendUpward, and BPQ's own ack/RR handling on both directions.
    /// </summary>
    /// <remarks>
    /// Assertion is intentionally tolerant of BPQ's exact wording — only
    /// "non-empty payload from BPQ" is checked, not specific text — so
    /// the test survives BPQ upstream banner changes. Pid is asserted as
    /// 0xF0 (no layer 3) which is what BPQ's node prompt uses; any other
    /// pid would surface a real protocol mismatch worth investigating.
    /// </remarks>
    [Fact]
    public async Task Connected_IFrame_RoundTrip_Against_Linbpq_Node_Prompt()
    {
        var totalBudget = ConnectBudget
            + TimeSpan.FromSeconds(15)   // banner wait
            + TimeSpan.FromSeconds(15)   // command response wait
            + DisconnectBudget
            + TimeSpan.FromSeconds(15);  // slack
        using var cts = new CancellationTokenSource(totalBudget);

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        var rig = BuildRig(local: OurCall, remote: BpqCall, kiss: kiss);

        var pumps = new[]
        {
            Task.Run(() => InboundPump(rig, cts.Token), cts.Token),
        };

        await Task.Delay(500, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps, cts.Token);
        connectConfirm.Should().NotBeNull("must complete handshake before any data exchange");

        // ─── Banner from BPQ ────────────────────────────────────────
        // BPQ emits its node-prompt welcome banner as one or more
        // I-frames immediately after sending UA. Some BPQ versions
        // split it across multiple frames — we only require the first
        // to arrive; later frames will queue up behind it but we
        // don't gate on them.
        var banner = await WaitForSignal<DataLinkDataIndication>(rig.Signals, TimeSpan.FromSeconds(15), pumps, cts.Token);
        banner.Should().NotBeNull("BPQ must emit its node-prompt welcome banner as an I-frame after the handshake completes");
        banner!.Info.Length.Should().BeGreaterThan(0, "banner payload should not be empty");
        banner.Pid.Should().Be(Ax25Frame.PidNoLayer3, "BPQ's node prompt uses PID 0xF0 (no layer 3)");

        // Drain any follow-up banner frames so they don't surface as
        // false matches when we wait for the command response below.
        await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
        DrainIndications(rig.Signals);

        // ─── Outbound command ───────────────────────────────────────
        // "P\r" is BPQ's "Ports" command at the node prompt — short,
        // deterministically non-empty response, no side effects on
        // BPQ's state.
        rig.Session.PostEvent(new DlDataRequest(System.Text.Encoding.ASCII.GetBytes("P\r"), Ax25Frame.PidNoLayer3));

        var response = await WaitForSignal<DataLinkDataIndication>(rig.Signals, TimeSpan.FromSeconds(15), pumps, cts.Token);
        response.Should().NotBeNull("BPQ must reply to our ports command with at least one I-frame");
        response!.Info.Length.Should().BeGreaterThan(0, "response payload should not be empty");
        response.Pid.Should().Be(Ax25Frame.PidNoLayer3);

        rig.Session.CurrentState.Should().Be("Connected", "link must survive the data round-trip");

        // ─── Disconnect ─────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps, cts.Token);
        disconnectConfirm.Should().NotBeNull("clean DISC/UA close after data exchange");
        rig.Session.CurrentState.Should().Be("Disconnected");

        cts.Cancel();
        try { await Task.WhenAll(pumps); } catch (OperationCanceledException) { }
    }

    private static void DrainIndications(ConcurrentQueue<DataLinkSignal> signals)
    {
        var keep = new List<DataLinkSignal>();
        while (signals.TryDequeue(out var s))
        {
            if (s is not DataLinkDataIndication) keep.Add(s);
        }
        foreach (var s in keep) signals.Enqueue(s);
    }

    // ─── Rig ────────────────────────────────────────────────────────
    //
    // Identical shape to NetsimConnectedModeScenarios.BuildRig. Kept
    // duplicated here rather than lifted into a shared helper because
    // the two tests are likely to diverge as BPQ-specific quirks
    // surface (e.g. BPQ's PACLEN clamping, ACKMODE handling, link
    // recovery behaviour) — premature sharing would obscure those.

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

                // net-sim's afsk1200 channel is broadcast — every node
                // on the channel hears every TX. We only react to
                // frames addressed to our local callsign.
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

    private static void ThrowIfAnyFaulted(IReadOnlyList<Task> tasks)
    {
        foreach (var t in tasks)
        {
            if (t.IsFaulted)
            {
                throw t.Exception?.GetBaseException()
                    ?? new InvalidOperationException("background task faulted with no exception attached");
            }
        }
    }
}
