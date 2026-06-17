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
/// v2.2 arc — extended-mode (mod-128 / SABME) interop against the real XRouter
/// container over the net-sim AFSK1200 link. Our <see cref="Ax25Session"/>
/// attaches to net-sim's KISS-TCP on 8100 (node a); XRouter dials net-sim's 8103
/// (node d) from inside docker. Same transport as the mod-8
/// <see cref="XrouterViaNetsimConnectedMode"/>; the difference is our session is
/// built with <c>IsExtended = true</c>, so a DL-CONNECT initiates a SABME
/// (figc4.7 <c>Establish_Data_Link</c> branches on <c>mod_128</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline finding (verified on the wire).</b> XRouter rejects our SABME
/// with <b>DM (F=1)</b> — it does not implement mod-128 on the incoming path, and
/// it answers our <i>polled</i> SABME (P=1) with a DM whose F bit is set. figc4.6's
/// <c>AwaitingV22Connection</c> <c>DM received</c> handler routes a DM(F=1)
/// (<c>t11_dm_received_yes</c>) straight to <c>Disconnected</c> as a §975 connection
/// refusal — <b>with no fallback</b> — so a figure-literal v2.2-preferred connect
/// against XRouter dies and leaves <c>IsExtended</c> stuck true. This is the exact
/// gap <see cref="Ax25SessionQuirks.Ax25Spec48DmRejectionDegradesToV20"/> (default
/// on) closes: a DM in <c>AwaitingV22Connection</c> (either F-branch) is treated as
/// "peer can't do v2.2, retry mod-8" — version 2.0 is forced and the link
/// re-establishes via SABM — exactly the DM analogue of the FRMR fallback LinBPQ
/// triggers (<see cref="LinbpqViaNetsimExtendedMode"/>).
/// </para>
/// <para>
/// What this test proves end-to-end against a real third-party stack: our
/// extended-preferred connect sends a SABME, XRouter answers DM(F=1), our figc4.6
/// DM-fallback (Ax25Spec48) drops us to v2.0 and re-establishes with a SABM,
/// completing a <b>mod-8</b> connection that then carries data. Mirrors
/// <see cref="LinbpqViaNetsimExtendedMode"/> for the DM (rather than FRMR) refusal.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class XrouterViaNetsimExtendedMode
{
    private const string Host = "127.0.0.1";
    private const int OurKissPort = 8100;

    // Distinct from the mod-8 test's PNTEST so a freshly-torn-down XRouter link
    // table from a prior test in the same (serialised) collection can't be
    // confused with this session. XRouter's NODECALL is the connect target.
    private static readonly Callsign OurCall = new("PNX128", 0);
    private static readonly Callsign XrouterCall = new("PN0XRT", 0);

    private static readonly TimeSpan ConnectBudget = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // Faster RR-ack turnaround on the shared half-duplex channel — same rationale
    // as the other net-sim tests. T1/T3 keep spec defaults so the
    // SABME→DM→SABM retransmit recovery timing is unchanged.
    private static readonly TimeSpan AckTimer = TimeSpan.FromMilliseconds(600);

    // U-frame base control octet (P/F masked out), for wire assertions. The DM is
    // the inbound frame the test asserts on; the SABME we send is outbound (not in
    // the inbound Observed log), covered by Ax25ListenerPreferV22ConnectTests.
    private const int DmBase = 0x0F;

    private static int UBase(Ax25Frame f) => f.Control & 0xEF;

    /// <summary>
    /// Our extended-preferred DL-CONNECT against XRouter: we emit SABME, XRouter
    /// answers DM(F=1) (it does not implement mod-128 — see class remarks), and our
    /// figc4.6 DM-fallback (Ax25Spec48) drops us to v2.0 and re-establishes with a
    /// SABM, completing a <b>mod-8</b> connection. Then DISC/UA tears it down.
    /// Asserts the full degrade on the live DM the real XRouter sends, not a
    /// synthesised one — this is the on-the-wire proof of the new quirk.
    /// </summary>
    [Fact]
    public async Task ExtendedConnect_FallsBackToMod8_OnXrouterDm_ThenDisconnects()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));
        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: XrouterCall, kiss: kiss);
        rig.Session.Context.IsExtended.Should().BeTrue("the rig must start extended so DL-CONNECT initiates a SABME");

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));

        // Brief settle so net-sim's per-port TX queue is ready before SABME.
        await Task.Delay(500, cts.Token);

        // ─── Extended connect → DM → fallback → mod-8 Connected ─────────
        rig.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull(
            "after XRouter DMs our SABME, the figc4.6 DM-fallback (Ax25Spec48) must re-establish with SABM and complete a mod-8 connection");
        rig.Session.CurrentState.Should().Be("Connected");
        rig.Session.Context.IsExtended.Should().BeFalse(
            "the DM fallback forces version 2.0 — the completed link is mod-8, not mod-128 (XRouter cannot do mod-128)");

        // The wire evidence: XRouter answered our SABME with a DM. (Observed is
        // the INBOUND log — frames addressed to us — so the SABME we transmitted
        // isn't here; the rig starting IsExtended=true plus reaching a mod-8
        // Connected via the DM-fallback is the proof the SABME went out and was
        // rejected. The SABME-on-the-wire emission is covered by the
        // Ax25ListenerPreferV22ConnectTests unit tests.)
        rig.Observed.Should().Contain(f => UBase(f) == DmBase,
            "XRouter must reject our SABME with a DM — its mod-128-incapable response on the incoming path");

        // ─── Disconnect ─────────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("XRouter must reply UA to our DISC, taking us to Disconnected");
        rig.Session.CurrentState.Should().Be("Disconnected");
    }

    /// <summary>
    /// Beyond the fallback handshake: prove the link the DM-fallback produced is a
    /// fully functional mod-8 data link by round-tripping a command against
    /// XRouter's node prompt. We connect extended (→ DM → mod-8), wait for the link
    /// to quiesce, send "?\r" (help-summary command), and require a non-empty
    /// response — then DISC/UA. Asserts the fallback leaves a usable connection,
    /// not merely a Connected state.
    /// </summary>
    [Fact]
    public async Task ExtendedConnect_FallbackLink_CarriesData_AgainstXrouterNodePrompt()
    {
        var responseWait = TimeSpan.FromSeconds(30);
        var totalBudget = ConnectBudget
            + TimeSpan.FromSeconds(15)   // post-handshake quiescence settle
            + responseWait
            + DisconnectBudget
            + TimeSpan.FromSeconds(15);
        using var cts = new CancellationTokenSource(totalBudget);

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        var rig = BuildRig(local: OurCall, remote: XrouterCall, kiss: kiss);

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));

        await Task.Delay(500, cts.Token);

        // ─── Extended connect → DM → fallback → mod-8 Connected ─────────
        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete the DM-fallback handshake before any data exchange");
        rig.Session.Context.IsExtended.Should().BeFalse("fell back to mod-8");

        // ─── Settle ─────────────────────────────────────────────────────
        // XRouter's NODECALL connect path does not emit a CTEXT welcome banner
        // (CTEXT is alias-only — see XrouterViaNetsimConnectedMode). Wait for our
        // own link to go quiescent (post-UA RR exchange settled) before driving
        // the command round-trip.
        await WaitForQuiescence(rig.Session, TimeSpan.FromSeconds(15), pumps.Tasks, cts.Token);
        DrainIndications(rig.Signals);

        // ─── Outbound command → response ────────────────────────────────
        rig.Session.PostEvent(new DlDataRequest(System.Text.Encoding.ASCII.GetBytes("?\r"), Ax25Frame.PidNoLayer3));

        var response = await WaitForSignal<DataLinkDataIndication>(rig.Signals, responseWait, pumps.Tasks, cts.Token);
        response.Should().NotBeNull("XRouter must reply to our help command with at least one I-frame");
        response!.Info.Length.Should().BeGreaterThan(0, "response payload should not be empty");
        response.Pid.Should().Be(Ax25Frame.PidNoLayer3);

        (rig.Session.CurrentState is "Connected" or "TimerRecovery").Should().BeTrue(
            $"link must survive the data round-trip (was {rig.Session.CurrentState}; TimerRecovery is a transient post-ack recovery state under load, not a teardown)");

        // ─── Disconnect ─────────────────────────────────────────────────
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

    // ─── Rig (mirrors LinbpqViaNetsimExtendedMode.BuildRig) ────────────────
    //
    // IsExtended = true on the context (so DL-CONNECT initiates SABME) and an
    // Observed frame log (so the test can assert XRouter's DM appeared on the
    // wire). Kept duplicated rather than shared for the same reason the other
    // rigs are — the tests are likely to diverge as more peer-specific quirks
    // surface.

    private sealed record Rig(
        Ax25Session Session,
        SystemTimerScheduler Scheduler,
        ConcurrentQueue<DataLinkSignal> Signals,
        ConcurrentQueue<Ax25Frame> Observed,
        KissTcpClient Kiss);

    private static Rig BuildRig(Callsign local, Callsign remote, KissTcpClient kiss)
    {
        var scheduler = new SystemTimerScheduler(TimeProvider.System);
        // IsExtended = true: a DL-CONNECT-request runs figc4.7 Establish_Data_Link,
        // which sends SABME when mod_128. The default-on Ax25Spec44 quirk routes the
        // connect through AwaitingV22Connection (figc4.6), whose DM handler
        // (Ax25Spec48, default-on) is what fires when XRouter rejects the SABME.
        var ctx = new Ax25SessionContext { Local = local, Remote = remote, IsExtended = true };
        var signals = new ConcurrentQueue<DataLinkSignal>();
        var observed = new ConcurrentQueue<Ax25Frame>();

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
            T2Duration = AckTimer,
        };

        var bindings = Ax25SessionBindings.CreateDefault(ctx, scheduler, () => sessionRef?.CurrentTrigger);
        var guards = new GuardEvaluator(bindings);

        var session = new Ax25Session(ctx, scheduler, dispatcher, guards,
            transitionsByState: TransitionMap(),
            initialState: "Disconnected");
        sessionRef = session;
        return new Rig(session, scheduler, signals, observed, kiss);
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

                // The link's modulo is whatever ctx currently says: while we are
                // extended (pre-DM) we parse extended; after the fallback forces
                // v2.0 the context is mod-8. U-frames (SABME/UA/DM) are one control
                // octet in both modes, so the handshake frames parse the same either
                // way — only the post-connect I/S frames depend on the modulo, by
                // which point ctx is mod-8.
                if (!Ax25Frame.TryParse(f.Payload, Ax25ParseOptions.Lenient,
                        rig.Session.Context.IsExtended, out var parsed))
                    continue;

                // net-sim's afsk1200 channel is broadcast — only react to frames
                // addressed to our local callsign.
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;

                rig.Observed.Enqueue(parsed);
                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

    // ─── Helpers (mirror XrouterViaNetsimConnectedMode) ────────────────────

    private static Dictionary<string, IReadOnlyList<TransitionSpec>> TransitionMap() => new()
    {
        ["Disconnected"]          = DataLink_Disconnected.Transitions,
        ["AwaitingConnection"]    = DataLink_AwaitingConnection.Transitions,
        ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
        ["Connected"]             = DataLink_Connected.Transitions,
        ["AwaitingRelease"]       = DataLink_AwaitingRelease.Transitions,
        ["TimerRecovery"]         = DataLink_TimerRecovery.Transitions,
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
