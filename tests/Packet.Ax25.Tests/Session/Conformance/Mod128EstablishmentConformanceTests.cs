using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// v2.2 arc V2 — SABME establishment + version negotiation.
///
/// figc4.2 routes the Disconnected DL-CONNECT-request unconditionally to
/// AwaitingConnection (no version branch — confirmed against the authoritative
/// graphml). The figc4.7 Establish_Data_Link subroutine *does* send a SABME when
/// mod_128, so a v2.2-preferred connect emits the right first frame but parks in
/// the mod-8 establishment state, whose T1 retry downgrades to SABM and which has
/// no FRMR handler. The Ax25Spec44Mod128ConnectRoutesToV22 quirk (default on)
/// rewrites that single transition's target to AwaitingV22Connection (figc4.6),
/// which resends SABME on retry and handles the §975 FRMR/DM fallbacks.
///
/// These tests drive the full handshake end-to-end through the two-station
/// harness with the real dispatcher, asserting the figc4.6 transitions fire and
/// the fallbacks behave — closing the AwaitingV22Connection (0/25) behavioural
/// coverage gap that existed because nothing could drive a SABME connect.
/// </summary>
public class Mod128EstablishmentConformanceTests
{
    // U-frame base control octets (P/F masked out) — what the receiver sees.
    private const int SabmeBase = 0x6F;
    private const int SabmBase  = 0x2F;

    private static int UBase(Ax25Frame f) => f.Control & 0xEF;
    private static bool IsSabme(Ax25Frame f) => UBase(f) == SabmeBase;
    private static bool IsSabm(Ax25Frame f)  => UBase(f) == SabmBase;

    /// <summary>1 — a mod-128 connect routes the initiator through
    /// AwaitingV22Connection (figc4.6), and both stations reach Connected with
    /// IsExtended. Proven on the live FiredTransitions ledger.</summary>
    [Fact]
    public void Mod128_connect_routes_the_initiator_through_AwaitingV22Connection()
    {
        var h = TwoStationHarness.Build(extended: true, k: 8);

        // Drive the connect directly (Connect() would assert convergence; here we
        // want to observe the path, then assert it ourselves).
        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();

        // The initiator transited the figc4.6 AwaitingV22Connection state: its
        // DL-CONNECT left Disconnected via t03, and the connection completed via
        // the figc4.6 UA-received transition — a transition that exists ONLY in
        // AwaitingV22Connection, so its firing proves the route.
        h.FiredTransitions.Should().Contain(("Disconnected", "t03_dl_connect_request"),
            "the connect leaves Disconnected via figc4.2 t03 (Establish Data Link → SABME)");
        h.FiredTransitions.Should().Contain(("AwaitingV22Connection", "t12_ua_received_yes_yes"),
            "the redirect parks the initiator in AwaitingV22Connection, whose figc4.6 UA handler completes the mod-128 connect");
        h.FiredTransitions.Should().NotContain(("AwaitingConnection", "t04_ua_received_yes_yes"),
            "a mod-128 connect must NOT complete through the mod-8 figc4.2 AwaitingConnection state");

        h.A.State.Should().Be("Connected");
        h.B.State.Should().Be("Connected");
        h.A.Context.IsExtended.Should().BeTrue("the initiator negotiated mod-128 via SABME");
        h.B.Context.IsExtended.Should().BeTrue("the responder adopted mod-128 on receiving SABME (figc4.1 t14)");

        // The first frame the responder saw was a SABME, not a SABM.
        h.B.ReceivedFromPeer.Should().Contain(f => IsSabme(f), "the initiator's first frame is a SABME");
        h.B.ReceivedFromPeer.Should().NotContain(f => IsSabm(f), "no SABM is sent on a v2.2-preferred connect");
    }

    /// <summary>2 — when the initial SABME is lost, the T1 retry resends a SABME
    /// (figc4.6 t13_t1_expiry_no), NOT a SABM. The mis-routed figc4.2 path would
    /// downgrade the link to mod-8 on the first retry; the redirect prevents
    /// that.</summary>
    [Fact]
    public void Lost_SABME_is_retried_as_SABME_not_downgraded_to_SABM()
    {
        var h = TwoStationHarness.Build(extended: true, k: 8);

        // Drop the initial SABME on the channel so the responder never answers and
        // the initiator must retry from AwaitingV22Connection. (Drop SABMEs from A
        // only; leave everything else flowing.)
        var dropped = 0;
        h.Link.Drop = f =>
        {
            if (IsSabme(f) && f.Source.Callsign.Equals(h.A.Context.Local) && dropped == 0)
            {
                dropped++;
                return true;
            }
            return false;
        };

        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();

        h.A.State.Should().Be("AwaitingV22Connection", "the dropped SABME left the initiator awaiting the v2.2 connection");
        dropped.Should().Be(1, "exactly the first SABME was dropped");
        h.B.ReceivedFromPeer.Should().BeEmpty("the only frame sent so far (the SABME) was dropped");

        // T1 fires → figc4.6 t13_t1_expiry_no: RC++, resend SABME (P=1), restart T1.
        h.AdvanceT1();

        h.FiredTransitions.Should().Contain(("AwaitingV22Connection", "t13_t1_expiry_no"),
            "the T1 retry runs the figc4.6 resend-SABME transition, not the figc4.2 resend-SABM one");
        h.B.ReceivedFromPeer.Should().NotBeEmpty("the retry was sent (and this time delivered)");
        h.B.ReceivedFromPeer.Should().OnlyContain(f => IsSabme(f),
            "every establishment frame the responder sees is a SABME — the link did NOT downgrade to mod-8");
        h.B.ReceivedFromPeer.Should().NotContain(f => IsSabm(f), "no SABM downgrade on retry");

        // And it converges to a mod-128 connection.
        h.Settle();
        h.A.State.Should().Be("Connected");
        h.B.State.Should().Be("Connected");
        h.A.Context.IsExtended.Should().BeTrue();
        h.B.Context.IsExtended.Should().BeTrue();
    }

    /// <summary>3 — a pre-v2.2 peer answers a SABME with FRMR (§975): the initiator
    /// falls back to v2.0 and re-establishes with a <b>SABM</b>, completing a mod-8
    /// connection. figc4.6 t14_frmr_received forces Version 2.0 and re-establishes;
    /// the #44 redirect is what makes that handler reachable (figc4.2's
    /// AwaitingConnection, where a mod-128 connect used to land, has no FRMR handler
    /// at all). The re-establish-as-SABM half depends on the second fix below.</summary>
    /// <remarks>
    /// figc4.6 t14 draws <c>Establish Data Link</c> BEFORE <c>Set Version 2.0</c>
    /// (confirmed in awaiting_v22_connection.sdl.yaml). Because figc4.7
    /// <c>Establish_Data_Link</c> branches on <c>mod_128</c>, the figure as drawn
    /// re-establishes with a <b>SABME</b> while still extended — useless against the
    /// pre-v2.2 peer that just FRMR'd. <see cref="Ax25SessionQuirks.Ax25Spec45FrmrFallbackReestablishesV20"/>
    /// (default on; ax25spec#45) forces Version 2.0 before the t14 actions run, so the
    /// re-establish is a SABM — matching direwolf's <c>frmr_frame</c> case state_5,
    /// which runs <c>set_version_2_0</c> ("Erratum: Need to force v2.0. This is not in
    /// flow chart") before <c>establish_data_link</c>. This test therefore drops only
    /// the initiator's SABME (so the v2.2 peer never adopts mod-128) and lets the SABM
    /// re-establish through, asserting the complete v2.0 fallback.
    /// </remarks>
    [Fact]
    public void FRMR_to_a_SABME_falls_back_to_v20_and_reestablishes_with_SABM()
    {
        var h = TwoStationHarness.Build(extended: true, k: 8);

        // Drop only the initiator's SABME, so the v2.2 peer never sees it (never
        // adopts mod-128); the SABM the fallback re-establishes with passes through
        // and completes a mod-8 connection. We inject the FRMR a pre-v2.2 peer would
        // have sent in response to that (dropped-here) SABME.
        h.Link.Drop = f => IsSabme(f) && f.Source.Callsign.Equals(h.A.Context.Local);

        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();
        h.A.State.Should().Be("AwaitingV22Connection", "initiator is awaiting the v2.2 connection (its SABME was swallowed by the channel)");
        h.A.Context.IsExtended.Should().BeTrue("still v2.2 until the FRMR arrives");

        // The pre-v2.2 peer rejects the SABME.
        h.Inject(h.A, new FrmrReceived(
            Ax25Frame.Frmr(h.A.Context.Local, h.A.Context.Remote, info: stackalloc byte[] { 0x00, 0x00, 0x00 })));

        h.FiredTransitions.Should().Contain(("AwaitingV22Connection", "t14_frmr_received"),
            "the FRMR runs the figc4.6 §975 fallback transition — reachable only because the #44 redirect parked the connect here");
        // ax25spec#45 fix: Version 2.0 forced before Establish, so the re-establish is a SABM.
        h.B.ReceivedFromPeer.Should().Contain(f => IsSabm(f),
            "the fallback re-establishes with SABM (v2.0), not SABME — ax25spec#45");
        h.B.ReceivedFromPeer.Should().NotContain(f => IsSabme(f),
            "no SABME reaches the peer: the initial one was dropped and the re-establish is a SABM");
        h.A.Context.IsExtended.Should().BeFalse("fell back to mod-8");
        h.A.State.Should().Be("Connected", "the v2.0 SABM re-establish completed the connection");
        h.B.State.Should().Be("Connected");
        h.B.Context.IsExtended.Should().BeFalse("the peer adopted mod-8 from the SABM (figc4.1 SABM-received → Set Version 2.0)");
    }

    /// <summary>4 — a not-capable peer answers a SABME with DM (§975 DM case).
    /// figc4.6 t11_dm_received_yes tears the connect attempt down to Disconnected
    /// and indicates DL-DISCONNECT.</summary>
    [Fact]
    public void DM_to_a_SABME_tears_the_connect_attempt_down_to_Disconnected()
    {
        var h = TwoStationHarness.Build(extended: true, k: 8);

        // Swallow the SABME so the v2.2 peer never auto-UAs; inject the DM(F=1) a
        // not-capable peer would send.
        h.Link.Drop = f => IsSabme(f) && f.Source.Callsign.Equals(h.A.Context.Local);

        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();
        h.A.State.Should().Be("AwaitingV22Connection");

        h.Inject(h.A, new DmReceived(
            Ax25Frame.Dm(h.A.Context.Local, h.A.Context.Remote, finalBit: true)));

        h.FiredTransitions.Should().Contain(("AwaitingV22Connection", "t11_dm_received_yes"),
            "a DM with F=1 runs the figc4.6 teardown transition");
        h.A.State.Should().Be("Disconnected", "the not-capable DM abandons the connect");
        h.A.Signals.Should().Contain(s => s is DataLinkDisconnectIndication,
            "teardown indicates DL-DISCONNECT to the upper layer");
    }

    /// <summary>StrictlyFaithful reproduces the figure defect: a mod-128 connect
    /// parks in AwaitingConnection (the mod-8 state) and its retry downgrades to
    /// SABM. Pins the quirk's off-behaviour so the redirect stays a deliberate,
    /// named deviation.</summary>
    [Fact]
    public void StrictlyFaithful_reproduces_the_figc4_2_defect_parks_in_AwaitingConnection()
    {
        var h = TwoStationHarness.BuildStrictlyFaithful(extended: true, k: 8);

        // Drop the first SABME so we can observe the retry's frame type.
        var dropped = 0;
        h.Link.Drop = f =>
        {
            if (IsSabme(f) && f.Source.Callsign.Equals(h.A.Context.Local) && dropped == 0) { dropped++; return true; }
            return false;
        };

        h.A.Session.PostEvent(new DlConnectRequest());
        h.Settle();

        // Faithful figure: the redirect is off, so the initiator parks in the
        // mod-8 establishment state despite having sent a SABME.
        h.A.State.Should().Be("AwaitingConnection",
            "with the quirk off the figc4.2 figure routes a mod-128 connect to the mod-8 state (the defect)");

        // And its T1 retry downgrades to SABM (the second consequence of the defect).
        h.AdvanceT1();
        h.B.ReceivedFromPeer.Should().Contain(f => IsSabm(f),
            "the figc4.2 AwaitingConnection retry sends a hardcoded SABM — the mod-8 downgrade the redirect prevents");
    }
}
