using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end smoke test for figc4.3 — drives the codegen-emitted
/// <see cref="DataLink_AwaitingRelease"/> transition table through
/// <see cref="Ax25Session"/> with a recording dispatcher and stubbed
/// decision predicates, asserting each of the 20 transitions lands on
/// its declared next state with its declared action sequence.
/// </summary>
/// <remarks>
/// figc4.3 has no SDL Save shape, so unlike figc4.2 there's no
/// page-level save-and-replay caveat for this test.
///
/// Decision bindings needed: <c>P_eq_1</c>, <c>F_eq_1</c> (referenced
/// by both <c>ua_f_eq_1</c> and <c>dm_f_eq_1</c> diamonds), and
/// <c>RC_eq_N2</c>.
/// </remarks>
public class DataLinkAwaitingReleaseSmokeTests
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
        bool rcEqN2 = false)
    {
        var time = new FakeTimeProvider();
        var scheduler = new SystemTimerScheduler(time);
        var ctx = new Ax25SessionContext
        {
            Local  = new Callsign("M0LTE", 0),
            Remote = new Callsign("G7XYZ", 7),
        };
        var bindings = new Dictionary<string, Func<bool>>(Ax25SessionBindings.CreateDefault(ctx, scheduler), StringComparer.Ordinal)
        {
            ["P_eq_1"]   = () => pEq1,
            ["F_eq_1"]   = () => fEq1,
            ["RC_eq_N2"] = () => rcEqN2,
        };
        var guards = new GuardEvaluator(bindings);
        var recorder = new RecordingActionDispatcher();
        var session = new Ax25Session(
            ctx, scheduler, recorder, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["AwaitingRelease"] = DataLink_AwaitingRelease.Transitions,
                ["Disconnected"]    = DataLink_Disconnected.Transitions,
            },
            initialState: "AwaitingRelease");
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
        bool rcEqN2 = false)
    {
        var (s, r) = NewSession(pEq1: pEq1, fEq1: fEq1, rcEqN2: rcEqN2);
        var expected = DataLink_AwaitingRelease.Transitions.Single(x => x.Id == transitionId);

        s.PostEvent(evt);

        s.CurrentState.Should().Be(expected.Next, $"transition '{transitionId}' should land on '{expected.Next}'");
        r.Recorded.Should().Equal(
            expected.Actions.Select(a => (a.Verb, a.Kind)).ToArray(),
            $"transition '{transitionId}' actions should fire in order");
    }

    // ─── Tests, one per transition (20 total) ──────────────────────────

    [Fact] public void t01_dl_disconnect_request() =>
        AssertTransitionFires("t01_dl_disconnect_request", new DlDisconnectRequest());

    [Fact] public void t02_t1_expiry_rc_eq_n2() =>
        AssertTransitionFires("t02_t1_expiry_rc_eq_n2", new T1Expiry(), rcEqN2: true);

    [Fact] public void t03_t1_expiry_rc_neq_n2() =>
        AssertTransitionFires("t03_t1_expiry_rc_neq_n2", new T1Expiry(), rcEqN2: false);

    [Fact] public void t04_ua_received_f_eq_1() =>
        AssertTransitionFires("t04_ua_received_f_eq_1", new UaReceived(Frame()), fEq1: true);

    [Fact] public void t05_ua_received_not_f_eq_1() =>
        AssertTransitionFires("t05_ua_received_not_f_eq_1", new UaReceived(Frame()), fEq1: false);

    [Fact] public void t06_all_other_primitives_from_upper_layer() =>
        AssertTransitionFires("t06_all_other_primitives_from_upper_layer", new AllOtherPrimitivesFromUpperLayer());

    [Fact] public void t07_dl_unit_data_request() =>
        AssertTransitionFires("t07_dl_unit_data_request", new DlUnitDataRequest("x"u8.ToArray()));

    [Fact] public void t08_all_other_primitives_from_lower_layer() =>
        AssertTransitionFires("t08_all_other_primitives_from_lower_layer", new AllOtherPrimitivesFromLowerLayer());

    [Fact] public void t09_control_field_error() =>
        AssertTransitionFires("t09_control_field_error", new ControlFieldError());

    [Fact] public void t10_info_not_permitted_in_frame() =>
        AssertTransitionFires("t10_info_not_permitted_in_frame", new InfoNotPermittedInFrame());

    [Fact] public void t11_u_or_s_frame_length_error() =>
        AssertTransitionFires("t11_u_or_s_frame_length_error", new UOrSFrameLengthError());

    [Fact] public void t12_sabm_received() =>
        AssertTransitionFires("t12_sabm_received", new SabmReceived(Frame()));

    [Fact] public void t13_sabme_received() =>
        AssertTransitionFires("t13_sabme_received", new SabmeReceived(Frame()));

    [Fact] public void t14_disc_received() =>
        AssertTransitionFires("t14_disc_received", new DiscReceived(Frame()));

    [Fact] public void t15_dm_received_f_eq_1() =>
        AssertTransitionFires("t15_dm_received_f_eq_1", new DmReceived(Frame()), fEq1: true);

    [Fact] public void t16_dm_received_not_f_eq_1() =>
        AssertTransitionFires("t16_dm_received_not_f_eq_1", new DmReceived(Frame()), fEq1: false);

    [Fact] public void t17_ui_received_p_eq_1() =>
        AssertTransitionFires("t17_ui_received_p_eq_1", new UiReceived(Frame()), pEq1: true);

    [Fact] public void t18_ui_received_not_p_eq_1() =>
        AssertTransitionFires("t18_ui_received_not_p_eq_1", new UiReceived(Frame()), pEq1: false);

    [Fact] public void t19_i_or_s_command_p_eq_1() =>
        AssertTransitionFires("t19_i_or_s_command_p_eq_1", new IOrSCommandReceived(Frame()), pEq1: true);

    [Fact] public void t20_i_or_s_command_not_p_eq_1() =>
        AssertTransitionFires("t20_i_or_s_command_not_p_eq_1", new IOrSCommandReceived(Frame()), pEq1: false);
}
