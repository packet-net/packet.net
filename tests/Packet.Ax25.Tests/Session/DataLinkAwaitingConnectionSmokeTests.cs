using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end smoke test for figc4.2 — drives the codegen-emitted
/// <see cref="DataLink_AwaitingConnection"/> transition table through
/// <see cref="Ax25Session"/> with a recording dispatcher and stubbed
/// decision predicates, asserting each of the 24 transitions lands on
/// its declared next state with its declared action sequence.
/// </summary>
/// <remarks>
/// <para>
/// figc4.2's page-level <c>save: [DL_DISCONNECT_request]</c> is not
/// asserted by this test — the orchestrator does not yet implement
/// save-and-replay semantics. Posting <see cref="DlDisconnectRequest"/>
/// in this state goes to the unhandled-event path, which is the
/// expected behaviour until the save machinery is wired up.
/// </para>
/// <para>
/// Adds bindings for four new decision predicates beyond what the
/// figc4.1 smoke test needs: <c>F_eq_1</c>, <c>V_s_eq_V_a</c>,
/// <c>RC_eq_N2</c>, and reuses <c>P_eq_1</c> + <c>layer_3_initiated</c>.
/// </para>
/// </remarks>
public class DataLinkAwaitingConnectionSmokeTests
{
    private sealed class RecordingActionDispatcher : IActionDispatcher
    {
        public List<(string Verb, ActionKind Kind)> Recorded { get; } = new();

        public void Execute(IEnumerable<ActionStep> actions, Ax25SessionContext context, ITimerScheduler scheduler)
        {
            foreach (var step in actions)
            {
                Recorded.Add((step.Verb, step.Kind));
            }
        }
    }

    private static (Ax25Session session, RecordingActionDispatcher recorder) NewSession(
        bool pEq1 = false,
        bool fEq1 = false,
        bool vsEqVa = false,
        bool rcEqN2 = false,
        bool layer3Initiated = false)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
            Layer3Initiated = layer3Initiated,
        };
        var bindings = new Dictionary<string, Func<bool>>(Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
        {
            ["P_eq_1"]     = () => pEq1,
            ["F_eq_1"]     = () => fEq1,
            ["V_s_eq_V_a"] = () => vsEqVa,
            ["RC_eq_N2"]   = () => rcEqN2,
        };
        var guards = new GuardEvaluator(bindings);
        var recorder = new RecordingActionDispatcher();
        var session = new Ax25Session(
            ctx, scheduler, recorder, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
                ["Disconnected"]         = DataLink_Disconnected.Transitions,
                ["Connected"]            = DataLink_Connected.Transitions,
                ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
            },
            initialState: "AwaitingConnection");
        return (session, recorder);
    }

    private static Ax25Frame Frame() => Ax25Frame.Ui(
        destination: new Callsign("M0LTE", 0),
        source:      new Callsign("G7XYZ", 7),
        info:        "x"u8);

    private static void AssertTransitionFires(
        string transitionId,
        Ax25Event evt,
        bool pEq1 = false,
        bool fEq1 = false,
        bool vsEqVa = false,
        bool rcEqN2 = false,
        bool layer3Initiated = false)
    {
        var (s, r) = NewSession(pEq1: pEq1, fEq1: fEq1, vsEqVa: vsEqVa, rcEqN2: rcEqN2, layer3Initiated: layer3Initiated);
        var expected = DataLink_AwaitingConnection.Transitions.Single(x => x.Id == transitionId);

        s.PostEvent(evt);

        s.CurrentState.Should().Be(expected.Next, $"transition '{transitionId}' should land on '{expected.Next}'");
        r.Recorded.Should().Equal(
            expected.Actions.Select(a => (a.Verb, a.Kind)).ToArray(),
            $"transition '{transitionId}' actions should fire in order");
    }

    // ─── Tests, one per transition (24 total) ──────────────────────────

    [Fact] public void t01_disc_received() =>
        AssertTransitionFires("t01_disc_received", new DiscReceived(Frame()));

    [Fact] public void t02_dm_received_f_eq_1() =>
        AssertTransitionFires("t02_dm_received_f_eq_1", new DmReceived(Frame()), fEq1: true);

    [Fact] public void t03_dm_received_not_f_eq_1() =>
        AssertTransitionFires("t03_dm_received_not_f_eq_1", new DmReceived(Frame()), fEq1: false);

    [Fact] public void t04_ua_received_not_f_eq_1() =>
        AssertTransitionFires("t04_ua_received_not_f_eq_1", new UaReceived(Frame()), fEq1: false);

    [Fact] public void t05_ua_received_f_eq_1_layer_3_initiated() =>
        AssertTransitionFires("t05_ua_received_f_eq_1_layer_3_initiated",
            new UaReceived(Frame()), fEq1: true, layer3Initiated: true);

    [Fact] public void t06_ua_received_f_eq_1_not_layer_3_initiated_vs_eq_va() =>
        AssertTransitionFires("t06_ua_received_f_eq_1_not_layer_3_initiated_vs_eq_va",
            new UaReceived(Frame()), fEq1: true, layer3Initiated: false, vsEqVa: true);

    [Fact] public void t07_ua_received_f_eq_1_not_layer_3_initiated_vs_neq_va() =>
        AssertTransitionFires("t07_ua_received_f_eq_1_not_layer_3_initiated_vs_neq_va",
            new UaReceived(Frame()), fEq1: true, layer3Initiated: false, vsEqVa: false);

    [Fact] public void t08_t1_expiry_rc_eq_n2() =>
        AssertTransitionFires("t08_t1_expiry_rc_eq_n2", new T1Expiry(), rcEqN2: true);

    [Fact] public void t09_t1_expiry_rc_neq_n2() =>
        AssertTransitionFires("t09_t1_expiry_rc_neq_n2", new T1Expiry(), rcEqN2: false);

    [Fact] public void t10_all_other_primitives_from_upper_layer() =>
        AssertTransitionFires("t10_all_other_primitives_from_upper_layer", new AllOtherPrimitivesFromUpperLayer());

    [Fact] public void t11_dl_connect_request() =>
        AssertTransitionFires("t11_dl_connect_request", new DlConnectRequest());

    [Fact] public void t12_dl_unit_data_request() =>
        AssertTransitionFires("t12_dl_unit_data_request", new DlUnitDataRequest("x"u8.ToArray()));

    [Fact] public void t13_dl_data_request_layer_3_initiated() =>
        AssertTransitionFires("t13_dl_data_request_layer_3_initiated",
            new DlDataRequest("x"u8.ToArray()), layer3Initiated: true);

    [Fact] public void t14_dl_data_request_not_layer_3_initiated() =>
        AssertTransitionFires("t14_dl_data_request_not_layer_3_initiated",
            new DlDataRequest("x"u8.ToArray()), layer3Initiated: false);

    [Fact] public void t15_i_frame_pops_off_queue_layer_3_initiated() =>
        AssertTransitionFires("t15_i_frame_pops_off_queue_layer_3_initiated",
            new IFramePopsOffQueue(), layer3Initiated: true);

    [Fact] public void t16_i_frame_pops_off_queue_not_layer_3_initiated() =>
        AssertTransitionFires("t16_i_frame_pops_off_queue_not_layer_3_initiated",
            new IFramePopsOffQueue(), layer3Initiated: false);

    [Fact] public void t17_all_other_primitives_from_lower_layer() =>
        AssertTransitionFires("t17_all_other_primitives_from_lower_layer", new AllOtherPrimitivesFromLowerLayer());

    [Fact] public void t18_ui_received_p_eq_1() =>
        AssertTransitionFires("t18_ui_received_p_eq_1", new UiReceived(Frame()), pEq1: true);

    [Fact] public void t19_ui_received_p_eq_0() =>
        AssertTransitionFires("t19_ui_received_p_eq_0", new UiReceived(Frame()), pEq1: false);

    [Fact] public void t20_control_field_error() =>
        AssertTransitionFires("t20_control_field_error", new ControlFieldError());

    [Fact] public void t21_info_not_permitted_in_frame() =>
        AssertTransitionFires("t21_info_not_permitted_in_frame", new InfoNotPermittedInFrame());

    [Fact] public void t22_u_or_s_frame_length_error() =>
        AssertTransitionFires("t22_u_or_s_frame_length_error", new UOrSFrameLengthError());

    [Fact] public void t23_sabm_received() =>
        AssertTransitionFires("t23_sabm_received", new SabmReceived(Frame()));

    [Fact] public void t24_sabme_received() =>
        AssertTransitionFires("t24_sabme_received", new SabmeReceived(Frame()));
}
