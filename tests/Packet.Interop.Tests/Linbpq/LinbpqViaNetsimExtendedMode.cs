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

namespace Packet.Interop.Tests.Linbpq;

/// <summary>
/// v2.2 arc V5b — extended-mode (mod-128 / SABME) interop against the real
/// LinBPQ container over the net-sim AFSK1200 link. Our <see cref="Ax25Session"/>
/// attaches to net-sim's KISS-TCP on 8100 (node a); LinBPQ dials net-sim's 8102
/// (node c) from inside docker. Same transport as the mod-8
/// <see cref="LinbpqViaNetsimConnectedMode"/>; the difference is our session is
/// built with <c>IsExtended = true</c>, so a DL-CONNECT initiates a SABME
/// (figc4.7 <c>Establish_Data_Link</c> branches on <c>mod_128</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline finding (verified against LinBPQ source + on the wire).</b>
/// LinBPQ 6.0.25.23 — the pinned container — <b>does not implement mod-128 at
/// the AX.25 L2 layer</b>. Its frame handler (<c>L2Code.c</c> ~line 687)
/// reads, verbatim:
/// <code>
///   if (CTLlessPF == SABME)
///   {
///       // Although some say V2.2 requires SABME I don't agree!
///       // Reject until we support Mod 128
///       L2SENDINVALIDCTRL(PORT, Buffer, ADJBUFFER, CTL);
///       return;
///   }
/// </code>
/// — an inbound SABME is answered with an FRMR ("invalid control field",
/// W-bit set). Outbound, BPQ only ever sends <c>SABM | PFBIT</c> (it has no
/// SABME-send path), and its XID handler is clamped to "Mod 8 and 256 Byte
/// frames". The <c>MAXFRAME=1..127</c> the docs advertise sets only the mod-8
/// window cap; the framing stays mod-8. So there is <b>no</b> BPQ config that
/// makes it accept a SABME and run mod-128 — the limitation is in the compiled
/// L2 code, not a tunable. (Empirically: sending a SABME to BPQ over the
/// net-sim KISS path returns control <c>0x97</c> = <c>FRMR|PF</c> with info
/// <c>7F 00 01</c> — the rejected SABME control byte, then the SDINVC flag.)
/// </para>
/// <para>
/// That makes the §7.1 "SABME connect / mod-128 windowed transfer / mod-128
/// loss recovery / segmentation-vs-BPQ-mod-128" rows <b>unreachable against
/// this peer</b> (they need a mod-128 link BPQ won't establish). What it
/// <i>does</i> exercise — and what this test asserts — is the §7.1
/// <b>version-negotiation fallback</b> row: our extended-preferred connect
/// sends a SABME, BPQ rejects it with FRMR (§975), and our figc4.6
/// <c>t14_frmr_received</c> path (<see cref="Ax25SessionQuirks.Ax25Spec45FrmrFallbackReestablishesV20"/>,
/// default on) sets version 2.0 and re-establishes with a SABM, completing a
/// mod-8 connection. The §7.1 fallback row explicitly names "a BPQ port pinned
/// v2.0" as a valid peer for this case — BPQ <i>is</i> that peer for L2
/// connect purposes, and it actively FRMRs the SABME, which is the cleanest
/// possible trigger for the fallback. This is real end-to-end exercise of the
/// v2.2 version-negotiation machinery against a third-party stack.
/// </para>
/// <para>
/// Bring the stack up with
/// <c>docker compose -f docker/compose.interop.yml up -d --wait</c>.
/// </para>
/// </remarks>
[Trait("Category", "Interop")]
[Collection(NetsimCollection.Name)]
public class LinbpqViaNetsimExtendedMode
{
    private const string Host        = "127.0.0.1";
    private const int    OurKissPort = 8100;

    // Distinct from the mod-8 test's PNTEST so a freshly-torn-down BPQ link
    // table from a prior test in the same (serialised) collection can't be
    // confused with this session. BPQ's NODECALL is the connect target.
    private static readonly Callsign OurCall = new("PNT128", 0);
    private static readonly Callsign BpqCall = new("PN0TST", 0);

    private static readonly TimeSpan ConnectBudget    = TimeSpan.FromSeconds(40);
    private static readonly TimeSpan DisconnectBudget = TimeSpan.FromSeconds(30);

    // Faster RR-ack turnaround on the shared half-duplex channel — same
    // rationale as LinbpqViaNetsimConnectedMode. T1/T3 keep spec defaults so
    // the SABME→FRMR→SABM retransmit recovery is unchanged.
    private static readonly TimeSpan AckTimer = TimeSpan.FromMilliseconds(600);

    // U-frame base control octets (P/F masked out), for wire assertions.
    private const int SabmeBase = 0x6F;
    private const int FrmrBase  = 0x87;

    private static int UBase(Ax25Frame f) => f.Control & 0xEF;

    /// <summary>
    /// Our extended-preferred DL-CONNECT against LinBPQ: we emit SABME, BPQ
    /// answers FRMR (it does not implement mod-128 — see class remarks), and
    /// our figc4.6 FRMR-fallback drops us to v2.0 and re-establishes with a
    /// SABM, completing a <b>mod-8</b> connection. Then DISC/UA tears it down.
    /// Asserts the full negotiation on the live FRMR the real BPQ sends, not a
    /// synthesised one — the conformance suite proves the in-process state
    /// machine; this proves it fires against a real peer's FRMR over the RF sim.
    /// </summary>
    [Fact]
    public async Task ExtendedConnect_FallsBackToMod8_OnLinbpqFrmr_ThenDisconnects()
    {
        using var cts = new CancellationTokenSource(ConnectBudget + DisconnectBudget + TimeSpan.FromSeconds(15));

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);

        var rig = BuildRig(local: OurCall, remote: BpqCall, kiss: kiss);
        rig.Session.Context.IsExtended.Should().BeTrue("the rig must start extended so DL-CONNECT initiates a SABME");

        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));

        // Brief settle so net-sim's per-port TX queue is ready before SABME.
        await Task.Delay(500, cts.Token);

        // ─── Extended connect → fallback → mod-8 Connected ──────────────
        rig.Session.PostEvent(new DlConnectRequest());

        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull(
            "after BPQ FRMRs our SABME, the figc4.6 FRMR-fallback must re-establish with SABM and complete a mod-8 connection");
        rig.Session.CurrentState.Should().Be("Connected");
        rig.Session.Context.IsExtended.Should().BeFalse(
            "the FRMR fallback forces version 2.0 — the completed link is mod-8, not mod-128 (BPQ cannot do mod-128)");

        // The wire evidence: we sent a SABME, and BPQ answered with an FRMR.
        // (The SABM the fallback re-establishes with is what BPQ actually UAs;
        // that's covered by reaching Connected above.)
        rig.Observed.Should().Contain(f => UBase(f) == FrmrBase,
            "BPQ must reject our SABME with an FRMR (invalid control field) — its documented mod-128 response");

        // ─── Disconnect ─────────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("LinBPQ must reply UA to our DISC, taking us to Disconnected");
        rig.Session.CurrentState.Should().Be("Disconnected");
    }

    /// <summary>
    /// Beyond the fallback handshake: prove the link the FRMR-fallback
    /// produced is a fully functional mod-8 data link by round-tripping a
    /// command against BPQ's node prompt. We connect extended (→ FRMR →
    /// mod-8), drain BPQ's welcome banner, send "P\r" (ports command), and
    /// require a non-empty response — then DISC/UA. Asserts the fallback
    /// leaves a usable connection, not merely a Connected state.
    /// </summary>
    [Fact]
    public async Task ExtendedConnect_FallbackLink_CarriesData_AgainstLinbpqNodePrompt()
    {
        var bannerWait   = TimeSpan.FromSeconds(30);
        var responseWait = TimeSpan.FromSeconds(30);
        var totalBudget = ConnectBudget
            + bannerWait
            + TimeSpan.FromSeconds(15)   // banner-drain settle
            + responseWait
            + DisconnectBudget
            + TimeSpan.FromSeconds(15);
        using var cts = new CancellationTokenSource(totalBudget);

        await using var kiss = await KissTcpClient.ConnectAsync(Host, OurKissPort, cts.Token);
        var rig = BuildRig(local: OurCall, remote: BpqCall, kiss: kiss);
        // `await using` so the pump is cancelled + awaited on EVERY exit path
        // (pass, assertion-failure, throw, timeout), not just at the happy-path end
        // — declared after `cts` so it disposes first. See InboundPumpScope.
        await using var pumps = InboundPumpScope.Start(cts.Token, ct => InboundPump(rig, ct));

        await Task.Delay(500, cts.Token);

        // ─── Extended connect → fallback → mod-8 Connected ──────────────
        rig.Session.PostEvent(new DlConnectRequest());
        var connectConfirm = await WaitForSignal<DataLinkConnectConfirm>(rig.Signals, ConnectBudget, pumps.Tasks, cts.Token);
        connectConfirm.Should().NotBeNull("must complete the FRMR-fallback handshake before any data exchange");
        rig.Session.Context.IsExtended.Should().BeFalse("fell back to mod-8");

        // ─── Banner from BPQ ────────────────────────────────────────────
        var banner = await WaitForSignal<DataLinkDataIndication>(rig.Signals, bannerWait, pumps.Tasks, cts.Token);
        banner.Should().NotBeNull("BPQ must emit its node-prompt welcome banner as an I-frame after the link is up");
        banner!.Info.Length.Should().BeGreaterThan(0, "banner payload should not be empty");
        banner.Pid.Should().Be(Ax25Frame.PidNoLayer3, "BPQ's node prompt uses PID 0xF0 (no layer 3)");

        await DrainIndicationsUntilQuiet(rig.Signals,
            quietFor: TimeSpan.FromSeconds(1.5),
            budget:   TimeSpan.FromSeconds(15), pumps.Tasks, cts.Token);

        // ─── Outbound command → response ────────────────────────────────
        rig.Session.PostEvent(new DlDataRequest(System.Text.Encoding.ASCII.GetBytes("P\r"), Ax25Frame.PidNoLayer3));

        var response = await WaitForSignal<DataLinkDataIndication>(rig.Signals, responseWait, pumps.Tasks, cts.Token);
        response.Should().NotBeNull("BPQ must reply to our ports command with at least one I-frame");
        response!.Info.Length.Should().BeGreaterThan(0, "response payload should not be empty");
        response.Pid.Should().Be(Ax25Frame.PidNoLayer3);

        rig.Session.CurrentState.Should().Be("Connected", "link must survive the data round-trip");

        // ─── Disconnect ─────────────────────────────────────────────────
        rig.Session.PostEvent(new DlDisconnectRequest());
        var disconnectConfirm = await WaitForSignal<DataLinkDisconnectConfirm>(rig.Signals, DisconnectBudget, pumps.Tasks, cts.Token);
        disconnectConfirm.Should().NotBeNull("clean DISC/UA close after data exchange");
        rig.Session.CurrentState.Should().Be("Disconnected");
    }

    // ─── Rig ────────────────────────────────────────────────────────────
    //
    // Same shape as LinbpqViaNetsimConnectedMode.BuildRig, plus: IsExtended =
    // true on the context (so DL-CONNECT initiates SABME) and an Observed
    // frame log (so the test can assert BPQ's FRMR appeared on the wire). Kept
    // duplicated rather than shared for the same reason the mod-8 rig is — the
    // two are likely to diverge as more BPQ-specific extended quirks surface.

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
        // which sends SABME when mod_128. The default-on Ax25Spec44 quirk routes
        // the connect through AwaitingV22Connection (figc4.6), whose FRMR handler
        // (Ax25Spec45, also default-on) is what fires when BPQ rejects the SABME.
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

                // The link's modulo is whatever ctx currently says: while we
                // are extended (pre-FRMR) we parse extended; after the fallback
                // forces v2.0 the context is mod-8. U-frames (SABME/UA/DM/FRMR)
                // are one control octet in both modes, so the handshake frames
                // parse the same either way — only the post-connect I/S frames
                // depend on the modulo, by which point ctx is mod-8.
                if (!Ax25Frame.TryParse(f.Payload, Ax25ParseOptions.Lenient,
                        rig.Session.Context.IsExtended, out var parsed))
                    continue;

                // net-sim's afsk1200 channel is broadcast — only react to
                // frames addressed to our local callsign.
                if (!parsed.Destination.Callsign.Equals(rig.Session.Context.Local)) continue;

                rig.Observed.Enqueue(parsed);
                rig.Session.PostEvent(Ax25FrameClassifier.Classify(parsed));
            }
        }
    }

    // ─── Helpers (mirror LinbpqViaNetsimConnectedMode) ────────────────────

    private static async Task DrainIndicationsUntilQuiet(
        ConcurrentQueue<DataLinkSignal> signals,
        TimeSpan quietFor,
        TimeSpan budget,
        IReadOnlyList<Task> backgroundTasks,
        CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(budget);
        var lastActivity = DateTime.UtcNow;
        while (!cts.IsCancellationRequested)
        {
            ThrowIfAnyFaulted(backgroundTasks);

            var sawData = false;
            var keep = new List<DataLinkSignal>();
            while (signals.TryDequeue(out var s))
            {
                if (s is DataLinkDataIndication) sawData = true;
                else keep.Add(s);
            }
            foreach (var s in keep) signals.Enqueue(s);

            if (sawData) lastActivity = DateTime.UtcNow;
            else if (DateTime.UtcNow - lastActivity >= quietFor) return;

            try { await Task.Delay(50, cts.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

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
