using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// Multi-hop end-to-end flows that exercise transitions across more than
/// one Data-Link state machine table. Each test posts a sequence of events
/// and asserts the state name + accumulated action sequence after each
/// hop.
/// </summary>
/// <remarks>
/// <para>
/// These tests would catch cross-table naming drift that per-state smoke
/// tests can't see:
/// </para>
/// <list type="bullet">
/// <item>A transition's <c>next:</c> string typo (e.g. "AwaitingConnection2"
/// instead of "AwaitingV22Connection") only surfaces when the orchestrator
/// actually has to route into that table.</item>
/// <item>Decision-predicate name drift (e.g. <c>F_eq_1</c> vs <c>F_eq_one</c>)
/// in one table is invisible until a transition from another table
/// crosses into it.</item>
/// <item>Event-id consistency between adjacent state machines (e.g. both
/// sides agreeing on <c>UA_received</c> vs <c>Ua_received</c>).</item>
/// </list>
/// <para>
/// Test-time guards are mutated between hops via <see cref="MutableGuards"/>,
/// since the recording dispatcher records action verbs but doesn't actually
/// execute them — context variables stay frozen at their initial values.
/// Tests manually align <see cref="MutableGuards"/> with what the figure
/// would have done at each step.
/// </para>
/// </remarks>
public class DataLinkIntegrationTests
{
    private sealed class RecordingActionDispatcher : IActionDispatcher
    {
        public List<(Ax25ActionVerb Verb, ActionKind Kind)> Recorded { get; } = new();

        public void Execute(IEnumerable<ActionStep> actions, TransitionContext tx)
        {
            foreach (var step in actions)
            {
                Recorded.Add((step.Verb, step.Kind));
            }
        }
    }

    /// <summary>Mutable bag the test can flip between hops to model the
    /// effect of actions that would otherwise mutate session context.</summary>
    private sealed class MutableGuards
    {
        public bool PEq1 { get; set; }
        public bool FEq1 { get; set; }
        public bool VsEqVa { get; set; } = true;            // initial state: V(s)=V(a)=0
        public bool VsEqVaPlusK { get; set; }
        public bool RcEqN2 { get; set; }
        public bool Layer3Initiated { get; set; }
        public bool OwnReceiverBusy { get; set; }
        public bool PeerReceiverBusy { get; set; }
        public bool RejectException { get; set; }
        public bool SrejEnabled { get; set; }
        public bool SrejExceptionGt0 { get; set; }
        public bool AckPending { get; set; }
        public bool VrIFrameStored { get; set; }
        public bool Version22 { get; set; }
        public bool POrFEq1 { get; set; }
        public bool T1Running { get; set; }
        public bool Command { get; set; }
        public bool InfoFieldValid { get; set; } = true;    // valid by default — invalid is the error path
        public bool NsEqVr { get; set; } = true;            // assume in-order receive by default
        public bool NsGtVrPlus1 { get; set; }
        public bool NrInWindow { get; set; } = true;
        public bool AbleToEstablish { get; set; } = true;   // assume able by default
    }

    private static (Ax25Session session, RecordingActionDispatcher recorder, MutableGuards guards) NewSession(
        string initialState,
        MutableGuards? overrides = null,
        Action<Ax25SessionContext>? configureContext = null)
    {
        var guards = overrides ?? new MutableGuards();
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Layer3Initiated = guards.Layer3Initiated,
        };
        configureContext?.Invoke(ctx);
        var bindings = new Dictionary<Ax25Guard, Func<bool>>(Ax25SessionBindings.CreateDefault(ctx, scheduler))
        {
            [Ax25Guard.PEq1]               = () => guards.PEq1,
            [Ax25Guard.FEq1]               = () => guards.FEq1,
            [Ax25Guard.VsEqVa]           = () => guards.VsEqVa,
            [Ax25Guard.VsEqVaPlusK]    = () => guards.VsEqVaPlusK,
            [Ax25Guard.RCEqN2]             = () => guards.RcEqN2,
            [Ax25Guard.Layer3Initiated]    = () => guards.Layer3Initiated,
            [Ax25Guard.OwnReceiverBusy]    = () => guards.OwnReceiverBusy,
            [Ax25Guard.PeerReceiverBusy]   = () => guards.PeerReceiverBusy,
            [Ax25Guard.RejectException]     = () => guards.RejectException,
            [Ax25Guard.SREJEnabled]         = () => guards.SrejEnabled,
            [Ax25Guard.SrejectExceptionGt0]  = () => guards.SrejExceptionGt0,
            [Ax25Guard.AckPending]  = () => guards.AckPending,
            [Ax25Guard.VrIFrameStored]   = () => guards.VrIFrameStored,
            [Ax25Guard.Version22]          = () => guards.Version22,
            [Ax25Guard.POrFEq1]          = () => guards.POrFEq1,
            [Ax25Guard.T1Running]           = () => guards.T1Running,
            [Ax25Guard.Command]              = () => guards.Command,
            [Ax25Guard.InfoFieldLengthLeN1AndContentIsOctetAligned]     = () => guards.InfoFieldValid,
            [Ax25Guard.NsEqVr]           = () => guards.NsEqVr,
            [Ax25Guard.NsGtVrPlus1]    = () => guards.NsGtVrPlus1,
            [Ax25Guard.VaLeNrLeVs]    = () => guards.NrInWindow,
            [Ax25Guard.AbleToEstablish]    = () => guards.AbleToEstablish,
        };
        var guardEvaluator = new GuardEvaluator(bindings);
        var recorder = new RecordingActionDispatcher();
        var session = new Ax25Session(
            ctx, scheduler, recorder, guardEvaluator,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Disconnected"]         = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
                ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
                ["Connected"]            = DataLink_Connected.Transitions,
                ["AwaitingV22Connection"] = DataLink_AwaitingV22Connection.Transitions,
                ["TimerRecovery"]        = DataLink_TimerRecovery.Transitions,
            },
            initialState: initialState);
        return (session, recorder, guards);
    }

    private static Ax25Frame Frame() => Ax25Frame.Ui(
        destination: new Callsign("M0LTE", 0),
        source:      new Callsign("G7XYZ", 7),
        info:        "x"u8);

    // ─── Integration scenarios ────────────────────────────────────────

    [Fact(DisplayName = "L3-initiated v2.0 connect happy path: Disconnected → AwaitingConnection → Connected")]
    public void Happy_path_v20_connect()
    {
        var (s, _, guards) = NewSession("Disconnected");

        // Hop 1: upper layer requests a connection.
        s.PostEvent(new DlConnectRequest());
        s.CurrentState.Should().Be("AwaitingConnection",
            "DL-CONNECT request from Disconnected should arm AwaitingConnection");
        // The action `set_layer_3_initiated` would flip the flag; mirror that.
        guards.Layer3Initiated = true;

        // Hop 2: peer accepts with UA(F=1).
        guards.FEq1 = true;
        s.PostEvent(new UaReceived(Frame()));
        s.CurrentState.Should().Be("Connected",
            "UA(F=1) with layer_3_initiated should resolve AwaitingConnection → Connected");
    }

    [Fact(DisplayName = "Peer-prefers-v2.2 negotiation: AwaitingConnection → AwaitingV22Connection → Connected")]
    public void V22_negotiation_via_sabme_response()
    {
        // We sent SABM (we're in AwaitingConnection); peer replies SABME.
        var (s, _, guards) = NewSession("AwaitingConnection",
            new MutableGuards { Layer3Initiated = true });

        // Hop 1: peer's SABME bumps us into the v2.2 awaiting state.
        s.PostEvent(new SabmeReceived(Frame()));
        s.CurrentState.Should().Be("AwaitingV22Connection",
            "SABME during AwaitingConnection (figc4.2 t24) should hand off to AwaitingV22Connection");

        // Hop 2: peer's UA(F=1) closes out v2.2 establish.
        guards.FEq1 = true;
        s.PostEvent(new UaReceived(Frame()));
        s.CurrentState.Should().Be("Connected",
            "UA(F=1) in AwaitingV22Connection with layer_3_initiated should reach Connected");
    }

    [Fact(DisplayName = "Peer refuses v2.2, drops back to v2.0: AwaitingV22Connection → AwaitingConnection")]
    public void V22_refused_falls_back_to_v20()
    {
        // Mod-8 session (IsExtended=false): the Ax25Spec48 DM-degrade quirk is inert
        // (it is scoped to an extended connect), so this exercises the figure-literal
        // figc4.6 t11_dm_received_no path: DM with F=0 → AwaitingConnection.
        var (s, _, guards) = NewSession("AwaitingV22Connection",
            new MutableGuards { Layer3Initiated = true });

        guards.FEq1 = false;
        s.PostEvent(new DmReceived(Frame()));
        s.CurrentState.Should().Be("AwaitingConnection",
            "DM(F=0) in AwaitingV22Connection (figc4.6 t11_dm_received_no) should drop to AwaitingConnection");
    }

    // ─── Ax25Spec48: DM-to-our-SABME degrades to v2.0 (the load-bearing fix) ───
    //
    // figc4.6's DM-received handler splits on the DM's P/F bit: F=0
    // (t11_dm_received_no) passively drops to AwaitingConnection, F=1
    // (t11_dm_received_yes) treats the DM as a §975 refusal and TEARS DOWN to
    // Disconnected with no fallback. But a peer that DMs our *polled* SABME (P=1)
    // — XRouter does exactly this on the wire — correctly answers F=1, so a real
    // non-v2.2 peer hits the teardown branch and our v2.2-preferred connect dies
    // with IsExtended stuck true. Ax25Spec48DmRejectionDegradesToV20 (default on)
    // makes EITHER F-branch degrade to v2.0 + re-establish via SABM, exactly like
    // the FRMR fallback (Ax25Spec45) — any DM to our SABME means "peer can't do
    // v2.2, retry mod-8", never a refusal.

    [Fact(DisplayName = "Ax25Spec48: DM(F=1) to our SABME degrades to v2.0 + re-establishes via SABM (not Disconnected)")]
    public void V22_dm_f1_degrades_to_v20_via_quirk()
    {
        // Extended connect in flight (IsExtended=true) so the quirk is in scope;
        // default quirks (Ax25Spec48 on).
        var (s, recorder, guards) = NewSession("AwaitingV22Connection",
            new MutableGuards { Layer3Initiated = true },
            ctx => ctx.IsExtended = true);

        // The figure-literal F=1 branch (t11_dm_received_yes) would tear down to
        // Disconnected. The quirk substitutes the FRMR-fallback transition instead.
        guards.FEq1 = true;
        s.PostEvent(new DmReceived(Frame()));

        s.CurrentState.Should().Be("AwaitingConnection",
            "Ax25Spec48 must degrade a DM(F=1) to the v2.0 path, NOT tear down to Disconnected (the figure-literal refusal)");
        s.Context.IsExtended.Should().BeFalse(
            "the degrade forces version 2.0 so the re-establish emits SABM, not SABME");
        recorder.Recorded.Select(r => r.Verb).Should().Contain(Ax25ActionVerb.EstablishDataLink,
            "the degrade must run the FRMR-fallback action chain — Establish_Data_Link re-sends the connect (SABM, now mod-8)");
        recorder.Recorded.Select(r => r.Verb).Should().Contain(Ax25ActionVerb.SetVersion20,
            "the FRMR-fallback action chain sets version 2.0");
    }

    [Fact(DisplayName = "Ax25Spec48: DM(F=0) to our SABME also degrades to v2.0 + re-establishes via SABM")]
    public void V22_dm_f0_degrades_to_v20_via_quirk()
    {
        var (s, recorder, guards) = NewSession("AwaitingV22Connection",
            new MutableGuards { Layer3Initiated = true },
            ctx => ctx.IsExtended = true);

        // F=0 figure-literal would passively drop to AwaitingConnection with no
        // re-establish; the quirk makes it actively re-establish via SABM too.
        guards.FEq1 = false;
        s.PostEvent(new DmReceived(Frame()));

        s.CurrentState.Should().Be("AwaitingConnection",
            "Ax25Spec48 degrades a DM(F=0) the same as F=1 — any DM means the peer can't do v2.2");
        s.Context.IsExtended.Should().BeFalse("the degrade forces version 2.0");
        recorder.Recorded.Select(r => r.Verb).Should().Contain(Ax25ActionVerb.EstablishDataLink,
            "Ax25Spec48 must actively re-establish via SABM, not merely park in AwaitingConnection");
    }

    [Fact(DisplayName = "Ax25Spec48 off (StrictlyFaithful): DM(F=1) reproduces the figure-literal teardown to Disconnected")]
    public void V22_dm_f1_strictly_faithful_tears_down()
    {
        var (s, _, guards) = NewSession("AwaitingV22Connection",
            new MutableGuards { Layer3Initiated = true },
            ctx => { ctx.IsExtended = true; ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful; });

        // figc4.6 t11_dm_received_yes as drawn: DM(F=1) is a §975 refusal → Disconnected.
        guards.FEq1 = true;
        s.PostEvent(new DmReceived(Frame()));

        s.CurrentState.Should().Be("Disconnected",
            "under StrictlyFaithful the figure runs as drawn — DM(F=1) tears the v2.2 connect down to Disconnected");
        s.Context.IsExtended.Should().BeTrue(
            "the figure-literal teardown does not force v2.0 — IsExtended stays as the connect left it");
    }

    [Fact(DisplayName = "Ax25Spec48 off (StrictlyFaithful): DM(F=0) reproduces the figure-literal drop to AwaitingConnection")]
    public void V22_dm_f0_strictly_faithful_drops_passively()
    {
        var (s, recorder, guards) = NewSession("AwaitingV22Connection",
            new MutableGuards { Layer3Initiated = true },
            ctx => { ctx.IsExtended = true; ctx.Quirks = Ax25SessionQuirks.StrictlyFaithful; });

        // figc4.6 t11_dm_received_no as drawn: DM(F=0) drops to AwaitingConnection
        // with NO actions (no re-establish).
        guards.FEq1 = false;
        s.PostEvent(new DmReceived(Frame()));

        s.CurrentState.Should().Be("AwaitingConnection",
            "under StrictlyFaithful the figure runs as drawn — DM(F=0) drops to AwaitingConnection");
        recorder.Recorded.Select(r => r.Verb).Should().NotContain(Ax25ActionVerb.EstablishDataLink,
            "the figure-literal F=0 drop has no actions — it does not re-establish");
    }

    [Fact(DisplayName = "Disconnect from Connected: Connected → AwaitingRelease → Disconnected")]
    public void Disconnect_flow()
    {
        var (s, _, guards) = NewSession("Connected");

        // Hop 1: upper layer requests disconnect from Connected.
        // figc4.4 sends DISC; we land in AwaitingRelease.
        s.PostEvent(new DlDisconnectRequest());
        s.CurrentState.Should().Be("AwaitingRelease",
            "DL-DISCONNECT request from Connected (figc4.4 t01) should arm AwaitingRelease");

        // Hop 2: peer acks the DISC with UA(F=1).
        guards.FEq1 = true;
        s.PostEvent(new UaReceived(Frame()));
        s.CurrentState.Should().Be("Disconnected",
            "UA(F=1) in AwaitingRelease (figc4.3 t04) should land at Disconnected");
    }

    [Fact(DisplayName = "Peer refuses connect: Disconnected → AwaitingConnection → Disconnected via DM(F=1)")]
    public void Connect_refused_by_peer_via_dm()
    {
        var (s, _, guards) = NewSession("Disconnected");

        s.PostEvent(new DlConnectRequest());
        s.CurrentState.Should().Be("AwaitingConnection");

        // figc4.2 t02: DM(F=1) = peer refuses → Disconnected.
        guards.FEq1 = true;
        s.PostEvent(new DmReceived(Frame()));
        s.CurrentState.Should().Be("Disconnected",
            "DM(F=1) during AwaitingConnection (figc4.2 t02) should abandon → Disconnected");
    }

    [Fact(DisplayName = "Cross-DISC during AwaitingRelease: stays in AwaitingRelease (figc4.3 t14)")]
    public void Cross_disc_during_awaiting_release_stays()
    {
        // figc4.3 t14: we sent DISC, peer also sent DISC. Figure says respond UA, stay.
        // (Only direwolf follows this — see SI-08.)
        var (s, _, _) = NewSession("AwaitingRelease");

        s.PostEvent(new DiscReceived(Frame()));
        s.CurrentState.Should().Be("AwaitingRelease",
            "figc4.3 t14: DISC during AwaitingRelease responds UA but stays — figure-authoritative (SI-08)");
    }

    [Fact(DisplayName = "Connection refused on Disconnected SABM: stays Disconnected via able_to_establish=false")]
    public void Sabm_refused_when_unable_to_establish()
    {
        // figc4.1: SABM received when !able_to_establish → DM, stay Disconnected.
        var (s, _, _) = NewSession("Disconnected",
            new MutableGuards { AbleToEstablish = false });

        s.PostEvent(new SabmReceived(Frame()));
        s.CurrentState.Should().Be("Disconnected",
            "SABM with able_to_establish=false (figc4.1) should refuse with DM and stay");
    }

    [Fact(DisplayName = "AwaitingRelease T1 retry exhaustion: → Disconnected (figc4.3 t02)")]
    public void Awaiting_release_t1_retry_exhausted()
    {
        var (s, _, _) = NewSession("AwaitingRelease",
            new MutableGuards { RcEqN2 = true });

        s.PostEvent(new T1Expiry());
        s.CurrentState.Should().Be("Disconnected",
            "T1 expiry with RC==N2 from AwaitingRelease (figc4.3 t02) should give up");
    }

    [Fact(DisplayName = "AwaitingConnection T1 retry exhaustion: → Disconnected (figc4.2 t08)")]
    public void Awaiting_connection_t1_retry_exhausted()
    {
        var (s, _, _) = NewSession("AwaitingConnection",
            new MutableGuards { RcEqN2 = true, Layer3Initiated = true });

        s.PostEvent(new T1Expiry());
        s.CurrentState.Should().Be("Disconnected",
            "T1 expiry with RC==N2 from AwaitingConnection (figc4.2 t08) should give up");
    }

    [Fact(DisplayName = "Full L3 v2.2 connect-then-disconnect round-trip")]
    public void Full_v22_round_trip()
    {
        var (s, _, guards) = NewSession("Disconnected");

        // 1. Local initiates connect.
        s.PostEvent(new DlConnectRequest());
        s.CurrentState.Should().Be("AwaitingConnection");
        guards.Layer3Initiated = true;

        // 2. Peer responds SABME (wants v2.2).
        s.PostEvent(new SabmeReceived(Frame()));
        s.CurrentState.Should().Be("AwaitingV22Connection");

        // 3. Peer's UA(F=1) closes establish.
        guards.FEq1 = true;
        s.PostEvent(new UaReceived(Frame()));
        s.CurrentState.Should().Be("Connected");

        // 4. Local initiates disconnect.
        guards.FEq1 = false;  // reset for the next hop
        s.PostEvent(new DlDisconnectRequest());
        s.CurrentState.Should().Be("AwaitingRelease");

        // 5. Peer acks DISC with UA(F=1).
        guards.FEq1 = true;
        s.PostEvent(new UaReceived(Frame()));
        s.CurrentState.Should().Be("Disconnected",
            "Full L3 v2.2 connect-then-disconnect round-trip should land at Disconnected");
    }
}
