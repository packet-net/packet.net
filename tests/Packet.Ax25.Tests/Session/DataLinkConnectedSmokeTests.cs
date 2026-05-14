using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Packet.Core;

namespace Packet.Ax25.Tests.Session;

/// <summary>
/// End-to-end smoke test for figc4.4 — drives the codegen-emitted
/// <see cref="DataLink_Connected"/> transition table through
/// <see cref="Ax25Session"/> with a recording dispatcher and stubbed
/// decision predicates, asserting each of the 69 transitions lands
/// on its declared next state with its declared action sequence.
/// </summary>
/// <remarks>
/// <para>
/// figc4.4 is the densest page in AX.25 — Connected state has to handle
/// 26 distinct input events, with the I-frame received column alone
/// expanding to 19 paths through 7 chained decisions.
/// </para>
/// <para>
/// Per-test guard predicate config covers every decision identifier on
/// the page: P_eq_1, F_eq_1, command, info_field_valid,
/// V_a_le_N_r_le_V_s, N_s_eq_V_r, N_s_gt_V_r_plus_1, V_s_eq_V_a,
/// V_s_eq_V_a_plus_k, own_receiver_busy, peer_receiver_busy,
/// reject_exception, srej_enabled, srej_exception_gt_0,
/// acknowledge_pending, V_r_I_frame_stored, version_2_2, P_or_F_eq_1,
/// T1_running.
/// </para>
/// <para>
/// t67-t69 (column 2's V(r) I Frame Stored = Yes paths) currently
/// encode one iteration of the figure's stored-frame loop inline.
/// Smoke test still passes (one iteration runs deterministically) but
/// see the YAML's verification_pending notes — real loop support is
/// pending a schema bump.
/// </para>
/// </remarks>
public class DataLinkConnectedSmokeTests
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

    private sealed class Guards
    {
        public bool PEq1 { get; init; }
        public bool FEq1 { get; init; }
        public bool Command { get; init; }
        public bool InfoFieldValid { get; init; }
        public bool NrInWindow { get; init; }
        public bool NsEqVr { get; init; }
        public bool NsGtVrPlus1 { get; init; }
        public bool VsEqVa { get; init; }
        public bool VsEqVaPlusK { get; init; }
        public bool OwnReceiverBusy { get; init; }
        public bool PeerReceiverBusy { get; init; }
        public bool RejectException { get; init; }
        public bool SrejEnabled { get; init; }
        public bool SrejExceptionGt0 { get; init; }
        public bool AckPending { get; init; }
        public bool VrIFrameStored { get; init; }
        public bool Version22 { get; init; }
        public bool POrFEq1 { get; init; }
        public bool T1Running { get; init; }
    }

    private static (Ax25Session session, RecordingActionDispatcher recorder) NewSession(Guards g)
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
            ["P_eq_1"]               = () => g.PEq1,
            ["F_eq_1"]               = () => g.FEq1,
            ["command"]              = () => g.Command,
            ["info_field_valid"]     = () => g.InfoFieldValid,
            ["V_a_le_N_r_le_V_s"]    = () => g.NrInWindow,
            ["N_s_eq_V_r"]           = () => g.NsEqVr,
            ["N_s_gt_V_r_plus_1"]    = () => g.NsGtVrPlus1,
            ["V_s_eq_V_a"]           = () => g.VsEqVa,
            ["V_s_eq_V_a_plus_k"]    = () => g.VsEqVaPlusK,
            ["own_receiver_busy"]    = () => g.OwnReceiverBusy,
            ["peer_receiver_busy"]   = () => g.PeerReceiverBusy,
            ["reject_exception"]     = () => g.RejectException,
            ["srej_enabled"]         = () => g.SrejEnabled,
            ["srej_exception_gt_0"]  = () => g.SrejExceptionGt0,
            ["acknowledge_pending"]  = () => g.AckPending,
            ["V_r_I_frame_stored"]   = () => g.VrIFrameStored,
            ["version_2_2"]          = () => g.Version22,
            ["P_or_F_eq_1"]          = () => g.POrFEq1,
            ["T1_running"]           = () => g.T1Running,
        };
        var guards = new GuardEvaluator(bindings);
        var recorder = new RecordingActionDispatcher();
        var session = new Ax25Session(
            ctx, scheduler, recorder, guards,
            transitionsByState: new Dictionary<string, IReadOnlyList<TransitionSpec>>
            {
                ["Connected"]            = DataLink_Connected.Transitions,
                ["Disconnected"]         = DataLink_Disconnected.Transitions,
                ["AwaitingConnection"]   = DataLink_AwaitingConnection.Transitions,
                ["AwaitingRelease"]      = DataLink_AwaitingRelease.Transitions,
                ["TimerRecovery"]        = Array.Empty<TransitionSpec>(),
                ["AwaitingConnection22"] = DataLink_AwaitingConnection22.Transitions,
            },
            initialState: "Connected");
        return (session, recorder);
    }

    private static Ax25Frame Frame() => Ax25Frame.Ui(
        destination: new Callsign("M0LTE", 0),
        source:      new Callsign("G7XYZ", 7),
        info:        "x"u8);

    private static void AssertTransitionFires(string transitionId, Ax25Event evt, Guards guards)
    {
        var (s, r) = NewSession(guards);
        var expected = DataLink_Connected.Transitions.Single(x => x.Id == transitionId);

        s.PostEvent(evt);

        s.CurrentState.Should().Be(expected.Next, $"transition '{transitionId}' should land on '{expected.Next}'");
        r.Recorded.Should().Equal(
            expected.Actions.Select(a => (a.Verb, a.Kind)).ToArray(),
            $"transition '{transitionId}' actions should fire in order");
    }

    // ─── Column 1 ──────────────────────────────────────────────────────
    [Fact] public void t01_dl_disconnect_request() =>
        AssertTransitionFires("t01_dl_disconnect_request", new DlDisconnectRequest(), new Guards());

    // ─── Column 2 (I received) — 16 paths + 3 stored-frame paths ───────
    [Fact] public void t02_i_received_not_command() =>
        AssertTransitionFires("t02_i_received_not_command", new IFrameReceived(Frame()),
            new Guards { Command = false });

    [Fact] public void t03_i_received_command_info_field_invalid_v22() =>
        AssertTransitionFires("t03_i_received_command_info_field_invalid_v22", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = false, Version22 = true });

    [Fact] public void t04_i_received_command_info_field_invalid_v20() =>
        AssertTransitionFires("t04_i_received_command_info_field_invalid_v20", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = false, Version22 = false });

    [Fact] public void t05_i_received_command_info_valid_nr_out_of_window_v22() =>
        AssertTransitionFires("t05_i_received_command_info_valid_nr_out_of_window_v22", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = false, Version22 = true });

    [Fact] public void t06_i_received_command_info_valid_nr_out_of_window_v20() =>
        AssertTransitionFires("t06_i_received_command_info_valid_nr_out_of_window_v20", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = false, Version22 = false });

    [Fact] public void t07_i_received_own_busy_p_eq_1() =>
        AssertTransitionFires("t07_i_received_own_busy_p_eq_1", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = true, PEq1 = true });

    [Fact] public void t08_i_received_own_busy_p_eq_0() =>
        AssertTransitionFires("t08_i_received_own_busy_p_eq_0", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = true, PEq1 = false });

    [Fact] public void t09_i_received_in_seq_no_stored_p_eq_1() =>
        AssertTransitionFires("t09_i_received_in_seq_no_stored_p_eq_1", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = true, VrIFrameStored = false, PEq1 = true });

    [Fact] public void t10_i_received_in_seq_no_stored_p_eq_0_ack_pending() =>
        AssertTransitionFires("t10_i_received_in_seq_no_stored_p_eq_0_ack_pending", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = true, VrIFrameStored = false, PEq1 = false, AckPending = true });

    [Fact] public void t11_i_received_in_seq_no_stored_p_eq_0_no_ack_pending() =>
        AssertTransitionFires("t11_i_received_in_seq_no_stored_p_eq_0_no_ack_pending", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = true, VrIFrameStored = false, PEq1 = false, AckPending = false });

    [Fact] public void t12_i_received_out_of_seq_reject_exception_p_eq_1() =>
        AssertTransitionFires("t12_i_received_out_of_seq_reject_exception_p_eq_1", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = false, RejectException = true, PEq1 = true });

    [Fact] public void t13_i_received_out_of_seq_reject_exception_p_eq_0() =>
        AssertTransitionFires("t13_i_received_out_of_seq_reject_exception_p_eq_0", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = false, RejectException = true, PEq1 = false });

    [Fact] public void t14_i_received_out_of_seq_srej_enabled_no_excep_in_range() =>
        AssertTransitionFires("t14_i_received_out_of_seq_srej_enabled_no_excep_in_range", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = false, RejectException = false, SrejEnabled = true, SrejExceptionGt0 = false, NsGtVrPlus1 = false });

    [Fact] public void t15_i_received_out_of_seq_srej_enabled_no_excep_far_skip() =>
        AssertTransitionFires("t15_i_received_out_of_seq_srej_enabled_no_excep_far_skip", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = false, RejectException = false, SrejEnabled = true, SrejExceptionGt0 = false, NsGtVrPlus1 = true });

    [Fact] public void t16_i_received_out_of_seq_srej_enabled_existing_excep() =>
        AssertTransitionFires("t16_i_received_out_of_seq_srej_enabled_existing_excep", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = false, RejectException = false, SrejEnabled = true, SrejExceptionGt0 = true });

    [Fact] public void t17_i_received_out_of_seq_srej_disabled() =>
        AssertTransitionFires("t17_i_received_out_of_seq_srej_disabled", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = false, RejectException = false, SrejEnabled = false });

    // ─── Column 3 ──────────────────────────────────────────────────────
    [Fact] public void t18_dl_data_request() =>
        AssertTransitionFires("t18_dl_data_request", new DlDataRequest("x"u8.ToArray()), new Guards());

    // ─── Column 4 (I Frame Pops Off Queue) ─────────────────────────────
    [Fact] public void t19_i_frame_pops_off_queue_send_now_t1_running() =>
        AssertTransitionFires("t19_i_frame_pops_off_queue_send_now_t1_running", new IFramePopsOffQueue(),
            new Guards { PeerReceiverBusy = false, VsEqVaPlusK = false, T1Running = true });

    [Fact] public void t20_i_frame_pops_off_queue_send_now_t1_not_running() =>
        AssertTransitionFires("t20_i_frame_pops_off_queue_send_now_t1_not_running", new IFramePopsOffQueue(),
            new Guards { PeerReceiverBusy = false, VsEqVaPlusK = false, T1Running = false });

    [Fact] public void t21_i_frame_pops_off_queue_window_full() =>
        AssertTransitionFires("t21_i_frame_pops_off_queue_window_full", new IFramePopsOffQueue(),
            new Guards { PeerReceiverBusy = false, VsEqVaPlusK = true });

    [Fact] public void t22_i_frame_pops_off_queue_peer_busy() =>
        AssertTransitionFires("t22_i_frame_pops_off_queue_peer_busy", new IFramePopsOffQueue(),
            new Guards { PeerReceiverBusy = true });

    // ─── Column 5 ──────────────────────────────────────────────────────
    [Fact] public void t23_dl_unit_data_request() =>
        AssertTransitionFires("t23_dl_unit_data_request", new DlUnitDataRequest("x"u8.ToArray()), new Guards());

    // ─── Columns 6 & 7 (Flow control) ──────────────────────────────────
    [Fact] public void t24_dl_flow_off_when_not_busy() =>
        AssertTransitionFires("t24_dl_flow_off_when_not_busy", new DlFlowOffRequest(),
            new Guards { OwnReceiverBusy = true });

    [Fact] public void t25_dl_flow_off_when_already_busy() =>
        AssertTransitionFires("t25_dl_flow_off_when_already_busy", new DlFlowOffRequest(),
            new Guards { OwnReceiverBusy = false });

    [Fact] public void t26_dl_flow_on_when_busy_and_t1_not_running() =>
        AssertTransitionFires("t26_dl_flow_on_when_busy_and_t1_not_running", new DlFlowOnRequest(),
            new Guards { OwnReceiverBusy = true, T1Running = false });

    [Fact] public void t27_dl_flow_on_when_busy_and_t1_running() =>
        AssertTransitionFires("t27_dl_flow_on_when_busy_and_t1_running", new DlFlowOnRequest(),
            new Guards { OwnReceiverBusy = true, T1Running = true });

    [Fact] public void t28_dl_flow_on_when_not_busy() =>
        AssertTransitionFires("t28_dl_flow_on_when_not_busy", new DlFlowOnRequest(),
            new Guards { OwnReceiverBusy = false });

    // ─── Column 8 (DL-CONNECT collision) ───────────────────────────────
    [Fact] public void t29_dl_connect_request_v22() =>
        AssertTransitionFires("t29_dl_connect_request_v22", new DlConnectRequest(), new Guards { Version22 = true });

    [Fact] public void t30_dl_connect_request_v20() =>
        AssertTransitionFires("t30_dl_connect_request_v20", new DlConnectRequest(), new Guards { Version22 = false });

    // ─── Column 9 ──────────────────────────────────────────────────────
    [Fact] public void t31_all_other_primitives_from_lower_layer() =>
        AssertTransitionFires("t31_all_other_primitives_from_lower_layer", new AllOtherPrimitivesFromLowerLayer(), new Guards());

    // ─── Columns 10-12 (frame-format errors) ───────────────────────────
    [Fact] public void t32_control_field_error_v22() =>
        AssertTransitionFires("t32_control_field_error_v22", new ControlFieldError(), new Guards { Version22 = true });

    [Fact] public void t33_control_field_error_v20() =>
        AssertTransitionFires("t33_control_field_error_v20", new ControlFieldError(), new Guards { Version22 = false });

    [Fact] public void t34_info_not_permitted_in_frame_v22() =>
        AssertTransitionFires("t34_info_not_permitted_in_frame_v22", new InfoNotPermittedInFrame(), new Guards { Version22 = true });

    [Fact] public void t35_info_not_permitted_in_frame_v20() =>
        AssertTransitionFires("t35_info_not_permitted_in_frame_v20", new InfoNotPermittedInFrame(), new Guards { Version22 = false });

    [Fact] public void t36_u_or_s_frame_length_error_v22() =>
        AssertTransitionFires("t36_u_or_s_frame_length_error_v22", new UOrSFrameLengthError(), new Guards { Version22 = true });

    [Fact] public void t37_u_or_s_frame_length_error_v20() =>
        AssertTransitionFires("t37_u_or_s_frame_length_error_v20", new UOrSFrameLengthError(), new Guards { Version22 = false });

    // ─── Columns 13-14 (Timer expiries) ────────────────────────────────
    [Fact] public void t38_t1_expiry() =>
        AssertTransitionFires("t38_t1_expiry", new T1Expiry(), new Guards());

    [Fact] public void t39_t3_expiry() =>
        AssertTransitionFires("t39_t3_expiry", new T3Expiry(), new Guards());

    // ─── Columns 15-16 (SABM/SABME collision) ──────────────────────────
    [Fact] public void t40_sabm_received_vs_neq_va() =>
        AssertTransitionFires("t40_sabm_received_vs_neq_va", new SabmReceived(Frame()), new Guards { VsEqVa = false });

    [Fact] public void t41_sabm_received_vs_eq_va() =>
        AssertTransitionFires("t41_sabm_received_vs_eq_va", new SabmReceived(Frame()), new Guards { VsEqVa = true });

    [Fact] public void t42_sabme_received_vs_neq_va() =>
        AssertTransitionFires("t42_sabme_received_vs_neq_va", new SabmeReceived(Frame()), new Guards { VsEqVa = false });

    [Fact] public void t43_sabme_received_vs_eq_va() =>
        AssertTransitionFires("t43_sabme_received_vs_eq_va", new SabmeReceived(Frame()), new Guards { VsEqVa = true });

    // ─── Columns 17-18 (FRMR/UA unexpected) ────────────────────────────
    [Fact] public void t44_frmr_received_v22() =>
        AssertTransitionFires("t44_frmr_received_v22", new FrmrReceived(Frame()), new Guards { Version22 = true });

    [Fact] public void t45_frmr_received_v20() =>
        AssertTransitionFires("t45_frmr_received_v20", new FrmrReceived(Frame()), new Guards { Version22 = false });

    [Fact] public void t46_ua_received_v22() =>
        AssertTransitionFires("t46_ua_received_v22", new UaReceived(Frame()), new Guards { Version22 = true });

    [Fact] public void t47_ua_received_v20() =>
        AssertTransitionFires("t47_ua_received_v20", new UaReceived(Frame()), new Guards { Version22 = false });

    // ─── Column 19 (UI) ────────────────────────────────────────────────
    [Fact] public void t48_ui_received_p_eq_0() =>
        AssertTransitionFires("t48_ui_received_p_eq_0", new UiReceived(Frame()), new Guards { PEq1 = false });

    [Fact] public void t49_ui_received_p_eq_1() =>
        AssertTransitionFires("t49_ui_received_p_eq_1", new UiReceived(Frame()), new Guards { PEq1 = true });

    // ─── Columns 20-21 (DISC/DM) ───────────────────────────────────────
    [Fact] public void t50_disc_received() =>
        AssertTransitionFires("t50_disc_received", new DiscReceived(Frame()), new Guards());

    [Fact] public void t51_dm_received() =>
        AssertTransitionFires("t51_dm_received", new DmReceived(Frame()), new Guards());

    // ─── Columns 22-23 (RR/RNR) ────────────────────────────────────────
    [Fact] public void t52_rr_received_nr_in_window() =>
        AssertTransitionFires("t52_rr_received_nr_in_window", new RrReceived(Frame()), new Guards { NrInWindow = true });

    [Fact] public void t53_rr_received_nr_out_of_window_v22() =>
        AssertTransitionFires("t53_rr_received_nr_out_of_window_v22", new RrReceived(Frame()), new Guards { NrInWindow = false, Version22 = true });

    [Fact] public void t54_rr_received_nr_out_of_window_v20() =>
        AssertTransitionFires("t54_rr_received_nr_out_of_window_v20", new RrReceived(Frame()), new Guards { NrInWindow = false, Version22 = false });

    [Fact] public void t55_rnr_received_nr_in_window() =>
        AssertTransitionFires("t55_rnr_received_nr_in_window", new RnrReceived(Frame()), new Guards { NrInWindow = true });

    [Fact] public void t56_rnr_received_nr_out_of_window_v22() =>
        AssertTransitionFires("t56_rnr_received_nr_out_of_window_v22", new RnrReceived(Frame()), new Guards { NrInWindow = false, Version22 = true });

    [Fact] public void t57_rnr_received_nr_out_of_window_v20() =>
        AssertTransitionFires("t57_rnr_received_nr_out_of_window_v20", new RnrReceived(Frame()), new Guards { NrInWindow = false, Version22 = false });

    // ─── Column 24 (LM-SEIZE Confirm) ──────────────────────────────────
    [Fact] public void t58_lm_seize_confirm_no_ack_pending()
    {
        // LM_SEIZE_confirm has no concrete Ax25Event subtype yet — synthesise an inline one.
        AssertTransitionFires("t58_lm_seize_confirm_no_ack_pending", new LmSeizeConfirm(), new Guards { AckPending = false });
    }

    [Fact] public void t59_lm_seize_confirm_ack_pending() =>
        AssertTransitionFires("t59_lm_seize_confirm_ack_pending", new LmSeizeConfirm(), new Guards { AckPending = true });

    // ─── Columns 25-26 (SREJ/REJ) ──────────────────────────────────────
    [Fact] public void t60_srej_received_nr_in_window_pf_eq_0() =>
        AssertTransitionFires("t60_srej_received_nr_in_window_pf_eq_0", new SrejReceived(Frame()),
            new Guards { NrInWindow = true, POrFEq1 = false });

    [Fact] public void t61_srej_received_nr_in_window_pf_eq_1() =>
        AssertTransitionFires("t61_srej_received_nr_in_window_pf_eq_1", new SrejReceived(Frame()),
            new Guards { NrInWindow = true, POrFEq1 = true });

    [Fact] public void t62_srej_received_nr_out_of_window_v22() =>
        AssertTransitionFires("t62_srej_received_nr_out_of_window_v22", new SrejReceived(Frame()),
            new Guards { NrInWindow = false, Version22 = true });

    [Fact] public void t63_srej_received_nr_out_of_window_v20() =>
        AssertTransitionFires("t63_srej_received_nr_out_of_window_v20", new SrejReceived(Frame()),
            new Guards { NrInWindow = false, Version22 = false });

    [Fact] public void t64_rej_received_nr_in_window() =>
        AssertTransitionFires("t64_rej_received_nr_in_window", new RejReceived(Frame()), new Guards { NrInWindow = true });

    [Fact] public void t65_rej_received_nr_out_of_window_v22() =>
        AssertTransitionFires("t65_rej_received_nr_out_of_window_v22", new RejReceived(Frame()),
            new Guards { NrInWindow = false, Version22 = true });

    [Fact] public void t66_rej_received_nr_out_of_window_v20() =>
        AssertTransitionFires("t66_rej_received_nr_out_of_window_v20", new RejReceived(Frame()),
            new Guards { NrInWindow = false, Version22 = false });

    // ─── Column 2 continued (V(r) I Frame Stored = Yes paths) ──────────
    [Fact] public void t67_i_received_in_seq_stored_p_eq_1() =>
        AssertTransitionFires("t67_i_received_in_seq_stored_p_eq_1", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = true, VrIFrameStored = true, PEq1 = true });

    [Fact] public void t68_i_received_in_seq_stored_p_eq_0_ack_pending() =>
        AssertTransitionFires("t68_i_received_in_seq_stored_p_eq_0_ack_pending", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = true, VrIFrameStored = true, PEq1 = false, AckPending = true });

    [Fact] public void t69_i_received_in_seq_stored_p_eq_0_no_ack_pending() =>
        AssertTransitionFires("t69_i_received_in_seq_stored_p_eq_0_no_ack_pending", new IFrameReceived(Frame()),
            new Guards { Command = true, InfoFieldValid = true, NrInWindow = true, OwnReceiverBusy = false, NsEqVr = true, VrIFrameStored = true, PEq1 = false, AckPending = false });
}
