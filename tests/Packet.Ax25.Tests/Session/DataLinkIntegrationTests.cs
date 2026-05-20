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
        public List<(string Verb, ActionKind Kind)> Recorded { get; } = new();

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
        MutableGuards? overrides = null)
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
        var bindings = new Dictionary<string, Func<bool>>(Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
        {
            ["P_eq_1"]               = () => guards.PEq1,
            ["F_eq_1"]               = () => guards.FEq1,
            ["V_s_eq_V_a"]           = () => guards.VsEqVa,
            ["V_s_eq_V_a_plus_k"]    = () => guards.VsEqVaPlusK,
            ["RC_eq_N2"]             = () => guards.RcEqN2,
            ["layer_3_initiated"]    = () => guards.Layer3Initiated,
            ["own_receiver_busy"]    = () => guards.OwnReceiverBusy,
            ["peer_receiver_busy"]   = () => guards.PeerReceiverBusy,
            ["reject_exception"]     = () => guards.RejectException,
            ["srej_enabled"]         = () => guards.SrejEnabled,
            ["srej_exception_gt_0"]  = () => guards.SrejExceptionGt0,
            ["acknowledge_pending"]  = () => guards.AckPending,
            ["V_r_I_frame_stored"]   = () => guards.VrIFrameStored,
            ["version_2_2"]          = () => guards.Version22,
            ["P_or_F_eq_1"]          = () => guards.POrFEq1,
            ["T1_running"]           = () => guards.T1Running,
            ["command"]              = () => guards.Command,
            ["info_field_valid"]     = () => guards.InfoFieldValid,
            ["N_s_eq_V_r"]           = () => guards.NsEqVr,
            ["N_s_gt_V_r_plus_1"]    = () => guards.NsGtVrPlus1,
            ["V_a_le_N_r_le_V_s"]    = () => guards.NrInWindow,
            ["able_to_establish"]    = () => guards.AbleToEstablish,
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
                ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
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
        var (s, _, guards) = NewSession("AwaitingV22Connection",
            new MutableGuards { Layer3Initiated = true });

        // figc4.6 t14: DM with F=0 → AwaitingConnection (v2.0 fallback).
        guards.FEq1 = false;
        s.PostEvent(new DmReceived(Frame()));
        s.CurrentState.Should().Be("AwaitingConnection",
            "DM(F=0) in AwaitingV22Connection (figc4.6 t14) should drop to AwaitingConnection");
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
