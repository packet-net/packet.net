using System.Collections.Concurrent;
using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Ax25Event = Packet.Ax25.Session.Ax25Event;
using Packet.Core;
using Packet.Interop.Tests.Netsim;
using Packet.Kiss;
using Xunit;

namespace Packet.Interop.Tests.Xrouter;

/// <summary>
/// Connected-mode interop against the XRouter container with net-sim
/// providing the RF link between us. Our <see cref="Ax25Session"/>
/// attaches to net-sim's KISS-TCP on port 8100 (node a); XRouter dials
/// net-sim's other listener on 8103 (node d) from inside docker, configured
/// by its INTERFACE=2 / PORT=2 blocks in <c>docker/xrouter/XROUTER.CFG</c>.
/// The afsk1200 sim bridges frames between the two endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>LinbpqViaNetsimConnectedMode.Connect_Then_Disconnect_Against_Linbpq_Across_Netsim</c>
/// — minimum useful assertion (SABM/UA connect, DISC/UA disconnect) against
/// a second third-party AX.25 stack as the remote peer. Data-path / I-frame
/// round-trip is deferred to a follow-up once we've confirmed XRouter accepts
/// connects on its NODECALL without further node-prompt configuration.
/// </para>
/// <para>
/// Topology gotcha: XRouter splits the KISS-TCP attach into two directives
/// (INTERFACE TYPE=TCP PROTOCOL=KISS … + PORT INTERFACENUM=N CHANNEL=B …)
/// whereas LinBPQ uses a single PORT block. Wire-level is plain KISS over
/// TCP for both — only the config syntax differs.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// Compose's <c>depends_on netsim healthy</c> ensures XRouter's KISS dial
/// finds the listener on first attempt.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class XrouterViaNetsimConnectedMode
{
    private const string Host             = "127.0.0.1";
    private const int    OurKissPort      = 8100;
    private static readonly Callsign OurCall    = new("PNTEST", 0);
    private static readonly Callsign XrouterCall = new("PN0XRT", 0);

    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // net-sim's afsk1200 channel is a shared half-duplex medium with
    // collision_mode: silence (docker/netsim/network.yaml). Shorten our
    // ack timer so our RR-acks turn the channel around quickly, reducing
    // the window where our TX overlaps XRouter's and both get silenced.
    // T1/T3 keep spec defaults — retransmit recovery is unchanged. See the
    // longer note in NetsimConnectedModeScenarios.
    private static readonly TimeSpan AckTimer = TimeSpan.FromMilliseconds(600);

    [Fact]
    public async Task Connect_Then_Disconnect_Against_Xrouter_Across_Netsim()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: XrouterCall, kiss: kiss);

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));

        // Brief settle so net-sim's per-port TX queue is ready before
        // we fire SABM. XRouter's KISS dial may still be racing in;
        // XRouter retries the dial so we don't gate on its connect.
        await Task.Delay(500, cts.Token);

        rig.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("XRouter must accept SABM addressed to its NODECALL (PN0XRT) and reply UA, taking us to Connected");
        rig.Session.CurrentState.Should().Be("Connected");

        rig.Session.PostEvent(new DlDisconnectRequest());

        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("XRouter must reply UA to our DISC, taking us to Disconnected");
        rig.Session.CurrentState.Should().Be("Disconnected");
    }

    /// <summary>
    /// Beyond the handshake: drive the figc4.4 Connected state's data path
    /// end-to-end against XRouter's node prompt. After SABM/UA we send
    /// "?\r" (help-summary command) as an outbound I-frame and wait for
    /// XRouter's response indication, then DISC/UA to close the link
    /// cleanly. Mirrors
    /// <c>LinbpqViaNetsimConnectedMode.Connected_IFrame_RoundTrip_Against_Linbpq_Node_Prompt</c>
    /// against the second third-party AX.25 stack. This exercises t18
    /// (DL-DATA push), t19/t20 (I-frame TX with V(s)++ and T1 management),
    /// t14-t16 (I-frame RX with N(s)/N(r) accounting), the upward data
    /// plumbing via sendUpward, and XRouter's own ack/RR handling on
    /// both directions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike LinBPQ, XRouter does NOT emit a welcome banner on connects
    /// to its NODECALL — its CTEXT block is documented as "sent to
    /// anyone connecting to the node alias" (see /data/XROUTER.CFG.example
    /// in the container image), so NODECALL connects engage the node
    /// prompt silently. We therefore skip the banner-wait step here and
    /// drive the command exchange directly. A short settle after UA
    /// lets the post-handshake RR exchange flow before the queue drain.
    /// </para>
    /// <para>
    /// Assertion is intentionally tolerant of XRouter's exact wording —
    /// only "non-empty payload from XRouter" is checked, not specific
    /// text — so the test survives XRouter upstream wording changes. Pid
    /// is asserted as 0xF0 (no layer 3) which is what XRouter's node
    /// prompt uses; any other pid would surface a real protocol mismatch
    /// worth investigating.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Connected_IFrame_RoundTrip_Against_Xrouter_Node_Prompt()
    {
        var responseWait = TimeSpan.FromSeconds(30);
        var totalBudget = ConnectBudget
            + TimeSpan.FromSeconds(15)   // post-handshake quiescence settle
            + responseWait               // command response wait
            + DisconnectBudget
            + TimeSpan.FromSeconds(15);  // slack
        using var cts = new CancellationTokenSource(totalBudget);

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        var rig = BuildRig(local: OurCall, remote: XrouterCall, kiss: kiss);

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));

        await Task.Delay(500, cts.Token);

        // ─── Connect ────────────────────────────────────────────────
        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete handshake before any data exchange");

        // ─── Settle ─────────────────────────────────────────────────
        // Unlike LinBPQ, XRouter's NODECALL connect path does not emit a
        // CTEXT welcome banner (CTEXT is alias-only — see XROUTER.CFG
        // and /data/XROUTER.CFG.example: "Connection text, sent to anyone
        // connecting to the node alias"). The node prompt is engaged for
        // NODECALL connects, but it's silent until the first command
        // arrives — there's no banner to gate on. Instead of a fixed sleep,
        // wait for our own link to go quiescent (the post-UA RR exchange
        // settled: V(s) == V(a), no ack pending) so the channel is idle
        // before we drive the command round-trip.
        await WaitForQuiescence(rig.Session, TimeSpan.FromSeconds(15), pumps.Tasks, cts.Token);
        DrainIndications(rig.Signals);

        // ─── Outbound command ───────────────────────────────────────
        // "?\r" is the help-summary command at XRouter's node prompt —
        // short, deterministically non-empty response, no side effects
        // on XRouter's state.
        rig.Session.PostEvent(new DlDataRequest(System.Text.Encoding.ASCII.GetBytes("?\r"), Ax25Frame.PidNoLayer3));

        var response = await WaitForSignal<DataLinkDataIndication>(rig.Signals, responseWait, pumps.Tasks, cts.Token);
        response.Should().NotBeNull("XRouter must reply to our help command with at least one I-frame");
        response!.Info.Length.Should().BeGreaterThan(0, "response payload should not be empty");
        response.Pid.Should().Be(Ax25Frame.PidNoLayer3);

        (rig.Session.CurrentState is "Connected" or "TimerRecovery").Should().BeTrue(
            $"link must survive the data round-trip (was {rig.Session.CurrentState}; TimerRecovery is a transient post-ack recovery state under load, not a teardown)");

        // ─── Disconnect ─────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("clean DISC/UA close after data exchange");
        rig.Session.CurrentState.Should().Be("Disconnected");
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
    // Same shape as LinbpqViaNetsimConnectedMode.BuildRig. Kept
    // duplicated rather than lifted into a shared helper because the
    // tests are likely to diverge as XRouter-specific quirks surface
    // (e.g. PACLEN clamping, idle timeouts, the AXUDP-vs-KISS path
    // selection at L2).

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
            subroutines:   subroutines)
        {
            // Faster RR-ack turnaround on the shared half-duplex channel —
            // see AckTimer remarks. T1/T3 keep spec defaults.
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
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;
                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

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

    /// <summary>
    /// Wait until our connected-mode link is quiescent — every I-frame we
    /// sent is acknowledged (V(s) == V(a)) and we owe no pending ack — so
    /// the shared half-duplex channel is idle before we transmit next.
    /// Lock-free, eventually-consistent reads against the inbound pump
    /// thread; a stale read just costs one extra poll iteration.
    /// </summary>
    private static async Task WaitForQuiescence(
        Ax25Session session,
        TimeSpan budget,
        IReadOnlyList<Task> backgroundTasks,
        CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(backgroundTasks);
            if (session.Context.VS == session.Context.VA && !session.Context.AcknowledgePending) return;
            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
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
