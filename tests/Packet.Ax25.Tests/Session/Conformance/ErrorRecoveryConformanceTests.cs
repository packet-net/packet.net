using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Behavioural (real-dispatcher, real-codec) coverage of the inbound
/// error-recovery frames — <b>FRMR</b> and an unsolicited <b>DM</b> — received in
/// the Connected and TimerRecovery states. These frames arrive on a station's
/// radio that the peer session would never emit during normal operation, so the
/// two-station harness reaches them via <see cref="TwoStationHarness.InjectFrameBytes"/>:
/// a real <see cref="Ax25Frame.Frmr"/> / <see cref="Ax25Frame.Dm"/> is serialised,
/// parsed, and classified through the production <see cref="Ax25FrameClassifier"/>,
/// then dispatched through the real <see cref="ActionDispatcher"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: FRMR_received had <em>no</em> behavioural coverage anywhere —
/// only the stub-dispatcher smoke test (which records verb names, runs nothing)
/// and the classifier unit test (bytes → event). Its figc4.4/figc4.5 transitions
/// run <c>Establish_Data_Link</c> (re-establish the link with a fresh SABM), the
/// most consequential recovery a received frame can trigger, and nothing exercised
/// that path with the real dispatcher on the wire. DM_received in Connected /
/// TimerRecovery tears the link down; covered here over the wire with the peer
/// present.
/// </para>
/// <para>
/// The error-input events (<c>control_field_error</c>, <c>info_not_permitted_in_frame</c>,
/// <c>u_or_s_frame_length_error</c>) are <em>not</em> covered here: the first
/// already has behavioural coverage in the single-session
/// <c>DataLink*EndToEndTests</c>, and the latter two are never synthesised from a
/// frame by the runtime receive path (only the classifier's
/// <c>ControlFieldError</c> fallback and the listener's Disconnected
/// <c>AllOtherCommands</c> reclassification exist) — a runtime reachability gap
/// tracked separately, not a harness gap.
/// </para>
/// </remarks>
public class ErrorRecoveryConformanceTests
{
    /// <summary>A FRMR addressed to <paramref name="target"/> (i.e. as if its peer
    /// sent it), serialised to the on-air byte form the receive path parses.</summary>
    private static byte[] FrmrTo(TwoStationHarness.Endpoint target) =>
        Ax25Frame.Frmr(
            destination: target.Context.Local,
            source:      target.Context.Remote,
            info:        ReadOnlySpan<byte>.Empty).ToBytes();

    /// <summary>A DM addressed to <paramref name="target"/>.</summary>
    private static byte[] DmTo(TwoStationHarness.Endpoint target) =>
        Ax25Frame.Dm(
            destination: target.Context.Local,
            source:      target.Context.Remote).ToBytes();

    private static bool SawSabmFrom(TwoStationHarness.Endpoint peerThatReceives) =>
        peerThatReceives.ReceivedFromPeer.Any(f => Ax25FrameClassifier.Classify(f) is SabmReceived);

    // ─── FRMR → re-establish (Establish_Data_Link) ──────────────────────

    [Fact]
    public void Frmr_received_in_Connected_reestablishes_the_link()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // A receives a FRMR (its peer rejected a frame as unrecoverable). figc4.4
        // t16_frmr_received_no (mod-8): DL-ERROR(K) up, Establish_Data_Link (fresh
        // SABM), Clear Layer 3 Initiated → AwaitingConnection. The SABM reaches B,
        // B re-syncs with a UA, and A returns to Connected — the link re-establishes.
        h.InjectFrameBytes(h.A, FrmrTo(h.A));

        SawSabmFrom(h.B).Should().BeTrue("FRMR must drive Establish_Data_Link → a fresh SABM on the wire");
        h.A.Signals.OfType<DataLinkErrorIndication>().Should().NotBeEmpty("FRMR raises a DL-ERROR indication upward");
        h.A.State.Should().Be("Connected", "the link re-establishes after the FRMR-triggered SABM/UA exchange");
        h.B.State.Should().Be("Connected");
    }

    [Fact]
    public void Frmr_received_in_TimerRecovery_reestablishes_the_link()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // Drive A into TimerRecovery: drop its I-frames so the submitted frame is
        // never acked, then let T1 expire (Transmit_Enquiry → TimerRecovery).
        h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0;
        h.Submit(h.A, 0x01);
        h.AdvanceT1();
        h.A.State.Should().Be("TimerRecovery", "the dropped I-frame's T1 must expire A into TimerRecovery");

        // Clear the channel, then deliver a FRMR. figc4.5 t15_frmr_received:
        // DL-ERROR(K), set_version_2_0, Establish_Data_Link, Clear Layer 3
        // Initiated → AwaitingConnection. The undelivered I-frame is abandoned by
        // the reset (so the link does NOT converge — that's the point of FRMR).
        h.Link.Drop = null;
        h.B.ReceivedFromPeer.Clear();
        h.InjectFrameBytes(h.A, FrmrTo(h.A));

        SawSabmFrom(h.B).Should().BeTrue("FRMR in TimerRecovery must also re-establish via a fresh SABM");
        h.A.State.Should().Be("Connected", "re-establishment completes back to Connected");
        h.B.State.Should().Be("Connected");
    }

    // ─── DM → tear down to Disconnected ─────────────────────────────────

    [Fact]
    public void Dm_received_in_Connected_tears_down_to_disconnected()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // figc4.4 t20_dm_received: DL-ERROR(E), DL-DISCONNECT indication,
        // discard_I_frame_queue, Stop T1, Stop T3 → Disconnected. No frame emitted;
        // A simply tears down (the peer claimed it is no longer connected).
        h.InjectFrameBytes(h.A, DmTo(h.A));

        h.A.State.Should().Be("Disconnected", "an unsolicited DM tears the connection down");
        h.A.Signals.OfType<DataLinkDisconnectIndication>().Should().NotBeEmpty("DM raises a DL-DISCONNECT indication upward");
    }

    [Fact]
    public void Dm_received_in_TimerRecovery_tears_down_to_disconnected()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0;
        h.Submit(h.A, 0x01);
        h.AdvanceT1();
        h.A.State.Should().Be("TimerRecovery");

        h.Link.Drop = null;
        // figc4.5 t12_dm_received: same teardown as Connected → Disconnected.
        h.InjectFrameBytes(h.A, DmTo(h.A));

        h.A.State.Should().Be("Disconnected", "DM in TimerRecovery also tears the connection down");
        h.A.Signals.OfType<DataLinkDisconnectIndication>().Should().NotBeEmpty();
    }

    // ─── Information not permitted (DL-ERROR M) over the wire ───────────

    [Fact]
    public void Info_bearing_supervisory_frame_in_Connected_is_info_not_permitted_and_reestablishes()
    {
        var h = TwoStationHarness.Build(k: 4);
        h.Connect();

        // A bare DM tears the link down (t20). A DM *carrying an information field*
        // is instead the "information not permitted in frame" error (DL-ERROR M) —
        // the classifier surfaces InfoNotPermittedInFrame, and figc4.4
        // t10_info_not_permitted_in_frame (mod-8) runs Establish_Data_Link → a
        // fresh SABM → re-establishment. Same frame type, opposite outcome, decided
        // purely by the forbidden info field: end-to-end proof the M detection fires
        // through the real codec + classifier + dispatcher on the wire.
        var dmWithInfo = DmTo(h.A).Concat(new byte[] { 0xDE, 0xAD }).ToArray();
        h.InjectFrameBytes(h.A, dmWithInfo);

        SawSabmFrom(h.B).Should().BeTrue("info-not-permitted in Connected re-establishes via a fresh SABM, not a teardown");
        h.A.State.Should().Be("Connected", "the link re-establishes (contrast a bare DM, which disconnects)");
        h.B.State.Should().Be("Connected");
    }
}
