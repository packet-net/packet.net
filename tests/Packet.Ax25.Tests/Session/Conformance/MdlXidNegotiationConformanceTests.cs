using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Xid;
using Xunit;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// v2.2 arc V3 part 2 - the MDL (XID parameter-negotiation) runtime.
///
/// Drives the management data-link FSM (figc5.1 Ready / figc5.2 Negotiating,
/// prose-bootstrap, verification_pending) through the two-station harness. The
/// data-link figc4.6 UA-received path raises MDL-NEGOTIATE Request on a v2.2
/// connect; the MDL driver (<see cref="Ax25ManagementDataLink"/>) then runs the
/// single XID command/response exchange and applies the §6.3.2 reverts-to merge,
/// replacing the forced establishment defaults with negotiated values.
///
/// Mirrors <see cref="Mod128EstablishmentConformanceTests"/> rigor: asserts on the
/// negotiated values landing in both contexts, the MDL state transitions, the MDL
/// signals raised, and the version-2.0 fallback / error paths.
/// </summary>
public class MdlXidNegotiationConformanceTests
{
    private const int XidBase = 0xAF;   // XID U-frame control, P/F masked out
    private static bool IsXid(Ax25Frame f) => (f.Control & 0xEF) == XidBase;

    /// <summary>A full mod-128 + SREJ XID offer (half-duplex, k=32, N1=256,
    /// T1=3000ms, N2=10) - the v2.2 baseline a station advertises. Tests vary one
    /// field via <c>with</c> to pin a specific reverts-to outcome.</summary>
    private static readonly XidParameters Mod128Offer = new()
    {
        ClassesOfProcedures = ClassesOfProcedures.HalfDuplexDefault,
        HdlcOptionalFunctions = new HdlcOptionalFunctions { Reject = RejectMode.SelectiveReject, Modulo128 = true },
        IFieldLengthRxBits = XidParameters.OctetsToBits(256),
        WindowSizeRx = 32,
        AckTimerMillis = 3000,
        Retries = 10,
    };

    // ─── Happy-path negotiation (two v2.2 stations) ─────────────────────────

    /// <summary>1 - two v2.2 stations connect; the initiator's figc4.6 UA path
    /// fires MDL-NEGOTIATE Request, the MDL exchanges XID command/response, and
    /// both stations end in MDL Ready having confirmed the negotiation. An XID
    /// command and response actually crossed the wire.</summary>
    [Fact]
    public void V22_connect_runs_the_XID_exchange_and_confirms_on_both_sides()
    {
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8);
        h.Connect();

        // Both MDL machines settle back in Ready (the exchange completed).
        h.A.MdlState.Should().Be("Ready");
        h.B.MdlState.Should().Be("Ready");

        // The initiator (A) confirmed negotiation to its Layer 3.
        h.A.MdlSignals.OfType<MdlNegotiateConfirmSignal>().Should().ContainSingle(
            "the initiator's figc5.2 XID-response path raises MDL-NEGOTIATE Confirm");

        // An XID command (from A) and an XID response (from B) crossed the link.
        h.B.ReceivedFromPeer.Should().Contain(f => IsXid(f) && f.IsCommand,
            "the initiator sent an XID command");
        h.A.ReceivedFromPeer.Should().Contain(f => IsXid(f) && f.IsResponse,
            "the responder replied with an XID response");
    }

    /// <summary>2 - the negotiated reject scheme reverts to the LESSER of the two
    /// offers (§6.3.2 ¶1426): SREJ only survives if both sides offer it. Here the
    /// responder offers only implicit reject, so both converge on REJ even though
    /// the initiator offered SREJ.</summary>
    [Fact]
    public void Reject_scheme_reverts_to_the_lesser_REJ_when_one_side_offers_only_REJ()
    {
        // B offers implicit-reject (still mod-128); the merge must drop SREJ on
        // both sides while keeping mod-128.
        var offerB = Mod128Offer with
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { Reject = RejectMode.ImplicitReject, Modulo128 = true },
        };
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8, xidOfferA: Mod128Offer, xidOfferB: offerB);

        h.Connect();

        h.A.Context.SrejEnabled.Should().BeFalse("SREJ reverts to the lesser — REJ wins when B offers only REJ");
        h.B.Context.SrejEnabled.Should().BeFalse();
        h.A.Context.ImplicitReject.Should().BeTrue();
        h.B.Context.ImplicitReject.Should().BeTrue();
        // Modulo survives (both offered mod-128).
        h.A.Context.IsExtended.Should().BeTrue("both offered mod-128, so it survives the merge");
        h.B.Context.IsExtended.Should().BeTrue();
    }

    /// <summary>3 - window k reverts to notification/min (§6.3.2 ¶1430): each side
    /// adopts the smaller of the two advertised Rx windows. The peer advertising a
    /// smaller k pulls both down to it.</summary>
    [Fact]
    public void Window_k_converges_on_the_minimum_advertised()
    {
        // A offers k=32; B offers a smaller k=10. Both must converge on 10.
        var offerB = Mod128Offer with { WindowSizeRx = 10 };
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 32, xidOfferA: Mod128Offer, xidOfferB: offerB);

        h.Connect();

        h.A.Context.K.Should().Be(10, "k reverts to the min of the two advertised Rx windows");
        h.B.Context.K.Should().Be(10);
    }

    /// <summary>4 - T1 reverts to the greater and N2 reverts to the greater
    /// (§6.3.2 ¶1432/¶1434). The slower / more-patient values win on both sides.
    /// Uses explicit XID offers (a connect would reset T1V/N2 via the figc4.x
    /// establishment path before negotiation, masking the negotiated values).</summary>
    [Fact]
    public void T1_and_N2_revert_to_the_greater_offered()
    {
        // A offers a fast/impatient T1+N2; B offers a slow/persistent one. Both
        // must converge on the greater (B's) values.
        var offerA = Mod128Offer with { AckTimerMillis = 1000, Retries = 8 };
        var offerB = Mod128Offer with { AckTimerMillis = 4000, Retries = 15 };
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8, xidOfferA: offerA, xidOfferB: offerB);

        h.Connect();

        h.A.Context.T1V.Should().Be(System.TimeSpan.FromMilliseconds(4000), "T1 reverts to the greater of the two offers");
        h.B.Context.T1V.Should().Be(System.TimeSpan.FromMilliseconds(4000));
        h.A.Context.N2.Should().Be(15, "N2 reverts to the greater of the two offers");
        h.B.Context.N2.Should().Be(15);
    }

    /// <summary>5 - modulo reverts to the lesser (§6.3.2 ¶1426): if one side offers
    /// only mod-8 in the XID, both converge on mod-8. Uses explicit offers (the
    /// SABME establishment resets B to mod-128 before negotiation, so a context
    /// mutation wouldn't survive - the XID offer is the controllable input).</summary>
    [Fact]
    public void Modulo_reverts_to_mod8_when_one_side_offers_only_mod8()
    {
        var offerB = Mod128Offer with
        {
            HdlcOptionalFunctions = new HdlcOptionalFunctions { Reject = RejectMode.SelectiveReject, Modulo128 = false },
        };
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8, xidOfferA: Mod128Offer, xidOfferB: offerB);

        h.Connect();

        h.A.Context.IsExtended.Should().BeFalse("modulo reverts to the lesser (mod-8) when B offers only mod-8");
        h.B.Context.IsExtended.Should().BeFalse();
    }

    // ─── v2.0 fallback (pre-v2.2 peer FRMRs the XID command) ────────────────

    /// <summary>6 - a pre-v2.2 peer answers the XID command with FRMR (§6.3.2 ¶1):
    /// the MDL applies the FULL §1436 version-2.0 default set (half-duplex,
    /// implicit reject, mod-8, N1=256, k=7, T1=3000ms, N2=10), confirms, and the
    /// link is usable at mod-8.</summary>
    [Fact]
    public void FRMR_of_XID_command_applies_the_full_v20_defaults()
    {
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8);

        // Swallow the XID command so the (modelled pre-v2.2) peer never auto-
        // responds; we inject the FRMR it would have sent. Start the negotiation
        // directly (a connect would have B auto-respond as a v2.2 peer).
        h.Link.Drop = f => IsXid(f) && f.IsCommand && f.Source.Callsign.Equals(h.A.Context.Local);
        h.StartNegotiation(h.A);

        h.A.MdlState.Should().Be("Negotiating", "the XID command was swallowed, so A awaits a reply");

        // The pre-v2.2 peer rejects the XID command with a FRMR-of-XID - routed
        // to the MDL machine (the listener routes a FRMR to the MDL while it
        // negotiates; see Ax25Listener.DispatchInbound).
        h.A.Mdl.OnFrmrReceived(
            Ax25Frame.Frmr(h.A.Context.Local, h.A.Context.Remote, info: stackalloc byte[] { 0x00, 0x00, 0x00 }));

        // Full §1436 version-2.0 default set applied to A's link context.
        h.A.Context.IsExtended.Should().BeFalse("v2.0 fallback ⇒ modulo 8");
        h.A.Context.SrejEnabled.Should().BeFalse("v2.0 fallback ⇒ implicit reject (no SREJ)");
        h.A.Context.ImplicitReject.Should().BeTrue();
        h.A.Context.N1.Should().Be(256, "v2.0 N1 = 2048 bits = 256 octets (§1436)");
        h.A.Context.K.Should().Be(7, "v2.0 Window Size Receive = 7 (§1436)");
        h.A.Context.N2.Should().Be(10, "v2.0 Retries = 10 (§1436)");
        h.A.Context.HalfDuplex.Should().BeTrue("v2.0 ⇒ half duplex (§1436)");
        h.A.Context.T1V.Should().Be(System.TimeSpan.FromMilliseconds(3000), "v2.0 Acknowledge Timer = 3000 ms (§1436)");
        h.A.Context.SegmenterReassemblerEnabled.Should().BeFalse("segmenter is a v2.2-only capability");

        // MDL confirms completion (a v2.0 connection is made) and returns to Ready.
        // (The MDL applies parameters; it does not manage the data-link connection
        // state - that stays under the figc4.x data-link machine.)
        h.A.MdlState.Should().Be("Ready");
        h.A.MdlSignals.OfType<MdlNegotiateConfirmSignal>().Should().ContainSingle(
            "the figc5.2 FRMR path raises MDL-NEGOTIATE Confirm (a v2.0 connection is made)");
    }

    // ─── TM201 retry / NM201 exhaustion (error C) ───────────────────────────

    /// <summary>7 - when no reply comes, TM201 retransmits the XID command up to
    /// NM201 times, then gives up with MDL-ERROR Indicate (C) ("management retry
    /// limit exceeded", §C5.3).</summary>
    [Fact]
    public void TM201_exhaustion_gives_up_with_MDL_ERROR_C()
    {
        // NM201 defaults to the data-link N2 (here 3 for a short test). Drop ALL
        // of A's XID commands so the peer never replies; A must retry then fail.
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8, n2: 3);
        h.Link.Drop = f => IsXid(f) && f.Source.Callsign.Equals(h.A.Context.Local);

        h.StartNegotiation(h.A);
        h.A.MdlState.Should().Be("Negotiating");

        // Retransmit cycles: each TM201 expiry with RC < NM201 bumps RC and
        // resends. NM201 == 3, so after 3 retries RC == NM201 and the next expiry
        // gives up. Advance enough TM201 intervals to exhaust.
        for (int i = 0; i < 4; i++)
        {
            h.AdvanceTm201();
        }

        h.A.MdlState.Should().Be("Ready", "after NM201 retries the MDL gives up and returns to Ready");
        h.A.MdlSignals.OfType<MdlErrorIndicateSignal>().Select(s => s.Code).Should().Contain("C",
            "retry-limit exhaustion raises MDL-ERROR Indicate (C)");
    }

    // ─── Error D (XID response without F=1) ─────────────────────────────────

    /// <summary>8 - an XID response without F=1 is the error-D condition (§C5.3):
    /// MDL-ERROR Indicate (D) is raised, and the MDL stays in Negotiating (TM201
    /// still running) rather than completing.</summary>
    [Fact]
    public void XID_response_without_F1_raises_MDL_ERROR_D_and_stays_negotiating()
    {
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8);

        // Swallow A's XID command so the v2.2 peer doesn't auto-respond; inject a
        // crafted XID *response* with F=0.
        h.Link.Drop = f => IsXid(f) && f.IsCommand && f.Source.Callsign.Equals(h.A.Context.Local);
        h.StartNegotiation(h.A);
        h.A.MdlState.Should().Be("Negotiating");

        var info = XidInfoField.Encode(new XidParameters
        {
            HdlcOptionalFunctions = HdlcOptionalFunctions.Default,
            WindowSizeRx = 4,
        });
        var xidRespNoFinal = Ax25Frame.Xid(
            h.A.Context.Local, h.A.Context.Remote, info, isCommand: false, pollFinal: false);

        h.A.Mdl.OnXidReceived(xidRespNoFinal);

        h.A.MdlSignals.OfType<MdlErrorIndicateSignal>().Select(s => s.Code).Should().Contain("D",
            "an XID response without F=1 raises MDL-ERROR Indicate (D)");
        h.A.MdlState.Should().Be("Negotiating", "error D leaves the MDL in Negotiating (TM201 still running)");
    }

    // ─── Error B (unexpected XID response in Ready) ─────────────────────────

    /// <summary>9 - an XID response arriving with no negotiation outstanding is the
    /// error-B condition (§C5.3 "unexpected XID response"): MDL-ERROR Indicate (B),
    /// staying in Ready.</summary>
    [Fact]
    public void Unexpected_XID_response_in_Ready_raises_MDL_ERROR_B()
    {
        var h = TwoStationHarness.Build(extended: true, srej: true, k: 8);
        h.A.MdlState.Should().Be("Ready", "no negotiation has started");

        var info = XidInfoField.Encode(new XidParameters { WindowSizeRx = 4 });
        var xidResp = Ax25Frame.Xid(
            h.A.Context.Local, h.A.Context.Remote, info, isCommand: false, pollFinal: true);

        h.A.Mdl.OnXidReceived(xidResp);

        h.A.MdlSignals.OfType<MdlErrorIndicateSignal>().Select(s => s.Code).Should().Contain("B",
            "an XID response with no command outstanding is the unexpected-response error B");
        h.A.MdlState.Should().Be("Ready");
    }
}
