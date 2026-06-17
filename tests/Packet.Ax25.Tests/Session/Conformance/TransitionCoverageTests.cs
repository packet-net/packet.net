using AwesomeAssertions;
using Packet.Ax25;
using Packet.Ax25.Sdl;
using Packet.Ax25.Session;
using Xunit.Abstractions;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Behavioural transition-coverage ledger. Runs a battery of representative
/// scenarios through the two-station harness with the <b>real</b> dispatcher and
/// records, via <see cref="Ax25Session.TransitionFired"/>, which
/// <c>(state, transition-id)</c> pairs actually execute. Reports per-state
/// coverage against the live <c>Packet.Ax25.Sdl</c> tables and asserts that a
/// curated set of high-value transitions across every state is behaviourally
/// exercised, plus a floor on the total — so behavioural coverage is measurable
/// and can't silently regress.
/// </summary>
/// <remarks>
/// This complements the per-state <c>DataLink&lt;State&gt;SmokeTests</c>, which
/// prove every transition is <i>structurally</i> reachable (stub dispatcher), and
/// the scenario suites (happy-path / loss-recovery / error-recovery /
/// timer-recovery), which assert correctness. Here the question is the orthogonal
/// one: of the 250 tracked transitions (the six data-link states plus the
/// management_data_link Ready/Negotiating machine — added in v2.2 arc V5a), which
/// ones does the real runtime actually run when driven through realistic traffic?
/// The battery runs both mod-8 and mod-128 (extended) scenarios — bidirectional
/// data incl. a 127→0 window-wrap, REJ/SREJ loss recovery, RNR flow, T3 keepalive,
/// the full Connected / TimerRecovery / establishment / release receive columns by
/// frame-injection (every command-vs-response × P/F × N(R)-window arm, both moduli),
/// the figc-input error/catch-all columns by direct event injection, XID
/// negotiation, and segmentation over a mod-128 link. After the W3 coverage
/// campaign the battery exercises 238/250 transitions — every one that is reachable
/// end-to-end. The remaining 12 are genuinely unreachable through this runtime and
/// are documented at the foot of the battery (block 41) with the exact mechanism
/// for each: the IFramePopsOffQueue branches the queue-drain synthesiser cannot emit
/// (it only pops in Connected/TimerRecovery, and only when the peer is not busy, the
/// window is not full, and — in TimerRecovery — T1 is running, which it always is
/// there) and the layer_3_initiated=No data arms of the initiator-only establishment
/// states (the responder goes straight Disconnected→Connected on SABM/SABME and
/// never parks there). The miss-list (written to test output) is the live map of
/// that residue.
/// </remarks>
public class TransitionCoverageTests
{
    private readonly ITestOutputHelper output;

    public TransitionCoverageTests(ITestOutputHelper output) => this.output = output;

    private static readonly (string State, IReadOnlyList<TransitionSpec> Table)[] Tables =
    {
        ("Disconnected",          DataLink_Disconnected.Transitions),
        ("AwaitingConnection",    DataLink_AwaitingConnection.Transitions),
        ("AwaitingV22Connection", DataLink_AwaitingV22Connection.Transitions),
        ("Connected",             DataLink_Connected.Transitions),
        ("AwaitingRelease",       DataLink_AwaitingRelease.Transitions),
        ("TimerRecovery",         DataLink_TimerRecovery.Transitions),
        // The management_data_link (MDL / XID-negotiation) machine. Its Ready /
        // Negotiating transitions register on the same ledger via the harness's
        // forwarding of Ax25ManagementDataLink.TransitionFired (V5a). Its state
        // names don't collide with the data-link states above.
        ("Ready",                 ManagementDataLink_Ready.Transitions),
        ("Negotiating",           ManagementDataLink_Negotiating.Transitions),
    };

    [Fact]
    public void Behavioural_coverage_meets_the_curated_floor_and_is_reported()
    {
        var fired = RunBatteryAndCollectFired();

        // ── Report (per-state hit/total + the misses) ──────────────────
        int total = 0, hit = 0;
        foreach (var (state, table) in Tables)
        {
            var ids = table.Select(t => t.Id).ToList();
            var covered = ids.Where(id => fired.Contains((state, id))).ToList();
            total += ids.Count;
            hit += covered.Count;
            output.WriteLine($"{state,-22} {covered.Count,3}/{ids.Count,-3} behavioural");
            var misses = ids.Where(id => !fired.Contains((state, id))).ToList();
            if (misses.Count > 0)
                output.WriteLine($"    miss: {string.Join(", ", misses)}");
        }
        output.WriteLine($"\nTOTAL {hit}/{total} transitions behaviourally exercised by the battery");

        // ── Assert: each reachable state is behaviourally exercised (a curated,
        // robust must-hit — confirmed-fired ids the battery is built to drive).
        // The W3 lift (this PR) walks every REACHABLE receive / primitive /
        // catch-all column across all six data-link states, both moduli, so
        // Disconnected and AwaitingRelease are now fully exercised (17/17, 20/20),
        // AwaitingConnection / AwaitingV22Connection sit at 22/25 (only the
        // genuinely-unreachable residue left — see block 41), Connected at 64/66
        // and TimerRecovery at 86/90 (each minus the unreachable IFramePopsOffQueue
        // / T1-not-running branches). The MDL (management_data_link) machine is on
        // the ledger too (Ready / Negotiating, both fully exercised). See the
        // report, block 41, and §17. The must-hit below is a representative,
        // deterministically-driven slice across that whole space (it does NOT list
        // every covered id — the floor below guards the total). ──
        (string State, string Id)[] mustHit =
        {
            ("Disconnected",          "t03_dl_connect_request"),    // A initiates a connect
            ("Disconnected",          "t13_sabm_received_yes"),      // B accepts an incoming SABM
            ("Disconnected",          "t13_sabm_received_no"),       // W3: SABM refused (able_to_establish=No) → DM
            ("Disconnected",          "t05_all_other_commands"),     // W3: stray command at a sessionless station → DM
            ("Disconnected",          "t11_ui_received_yes"),        // W3: UI with P=1
            ("AwaitingConnection",    "t04_ua_received_yes_yes"),    // connect completes
            ("AwaitingConnection",    "t04_ua_received_no"),         // W3: UA with F=0 → DL-ERROR D
            ("AwaitingConnection",    "t16_sabm_received"),          // W3: SABM collision while connecting
            ("AwaitingConnection",    "t17_sabme_received"),         // W3: SABME arrives → AwaitingV22Connection
            ("AwaitingV22Connection", "t12_ua_received_yes_yes"),    // mod-128 connect completes (figc4.6)
            ("AwaitingV22Connection", "t13_t1_expiry_no"),           // lost SABME retried as SABME
            ("AwaitingV22Connection", "t14_frmr_received"),          // §975 v2.0 fallback
            ("AwaitingV22Connection", "t11_dm_received_yes"),        // §975 DM teardown
            ("AwaitingV22Connection", "t07_control_field_error"),    // V5a: malformed frame while v2.2-pending
            ("AwaitingV22Connection", "t08_info_not_permitted_in_frame"),       // W3: info-not-permitted while v2.2-pending
            ("AwaitingV22Connection", "t06_all_other_primitives__from_upper_layer"), // V5a: catch-all upper
            ("AwaitingV22Connection", "t18_all_other_primitives__from_lower_layer"), // V5a: catch-all lower
            ("Connected",             "t02_dl_data_request"),        // upper layer sends data
            ("Connected",             "t21_rr_received_yes"),         // an RR acks
            ("Connected",             "t13_t3_expiry"),              // V5a: idle keepalive poll
            ("Connected",             "t16_frmr_received_yes"),      // V5a: extended-link FRMR → re-establish (v2.2 branch)
            ("Connected",             "t18_ui_received_yes"),        // V5a: connectionless UI on an established link
            ("Connected",             "t26_i_received_yes_no_yes"),  // V5a: over-N1 I-frame (info too long, v2.2 branch)
            ("Connected",             "t26_i_received_no"),          // W3: I arriving as a response (command=No) error column
            ("Connected",             "t21_rr_received_no_no"),      // W3: mod-8 RR N(R)-out-of-window → re-establish (v2.0)
            ("Connected",             "t07_dl_connect_request_no"),  // W3: redundant connect on a v2.0 link
            ("Connected",             "t26_i_received_yes_yes_yes_yes_yes"), // W3: in-seq I while own-busy, P=1
            ("AwaitingRelease",       "t03_ua_received_yes"),         // disconnect completes
            ("AwaitingRelease",       "t02_t1_expiry_yes"),          // W3: DISC retried to N2 exhaustion → Disconnected
            ("AwaitingRelease",       "t15_i_or_s_command_received_yes"), // W3: grouped I/S-command column, P=1
            ("AwaitingRelease",       "t13_dm_received_no"),         // W3: DM with F=0 → stay
            ("TimerRecovery",         "t15_frmr_received"),           // FRMR during recovery
            ("TimerRecovery",         "t18_rr_received_yes_yes_yes"), // V5a: poll/final RR completes mod-128 recovery
            ("TimerRecovery",         "t24_srej_received_yes_yes_yes_yes"), // V5a: SREJ selective recovery (7-bit)
            ("TimerRecovery",         "t12_dm_received"),            // V5a: DM teardown while recovering
            ("TimerRecovery",         "t20_lm_seize_confirm_yes"),   // V5a: LM-SEIZE-confirm with ack pending
            ("TimerRecovery",         "t14_sabme_received_no"),      // V5a: SABME collision while recovering
            ("TimerRecovery",         "t22_i_received_no"),          // W3: I arriving as a response (command=No) while recovering
            ("TimerRecovery",         "t18_rr_received_yes_no_no"),  // W3: RR response F=1, N(R)-out-of-window, mod-8 → re-establish
            ("TimerRecovery",         "t23_rej_received_yes_yes_yes"), // W3: REJ response F=1 completes recovery
            ("TimerRecovery",         "t13_sabm_received_no"),       // W3: SABM collision (V(s)≠V(a)) → resync to Connected
            ("TimerRecovery",         "t21_t1_expiry_yes_yes_no"),   // W3: idle recovery N2 exhaustion, peer not busy → Disconnected
            // MDL (management_data_link) — XID negotiation FSM, on the ledger via V5a.
            ("Ready",                 "t01_mdl_negotiate_request"),  // XID command sent on a v2.2 connect
            ("Negotiating",           "t01_xid_response_received_yes"), // negotiation completes (F=1)
            ("Negotiating",           "t02_frmr_received"),          // pre-v2.2 peer FRMRs → v2.0 fallback
            ("Negotiating",           "t03_tm201_expiry_yes"),       // NM201 retry limit → MDL-ERROR C
        };
        foreach (var (state, id) in mustHit)
        {
            fired.Should().Contain((state, id),
                $"the battery should behaviourally exercise {state}/{id}");
        }

        // ── Assert: a floor on total behavioural coverage (regression guard) ──
        // Raised 45 → 60 (v2.2 arc V2: AwaitingV22Connection 0 → 15, total 64),
        // then 60 → 122 (v2.2 arc V5a: extended-mode data/loss/recovery + the MDL
        // machine + segmentation folded in lift the battery to 127/250 — Connected
        // 21 → 38, TimerRecovery 6 → 40, AwaitingV22Connection 15 → 20, plus the
        // MDL Ready 2/2 + Negotiating 5/5). The denominator grew 243 → 250 when the
        // MDL machine joined the tracked set.
        //
        // Then 122 → 238 (W3 test-coverage campaign, this PR): the battery now
        // drives every REACHABLE receive / primitive / catch-all column across all
        // six data-link states at both moduli — Disconnected 7 → 17 (full),
        // AwaitingConnection 9 → 22, AwaitingV22Connection 20 → 22, Connected
        // 38 → 64, AwaitingRelease 6 → 20 (full), TimerRecovery 40 → 86. The 12
        // still-uncovered transitions are GENUINELY unreachable end-to-end through
        // this runtime (documented in block 41 with the exact mechanism for each):
        // the IFramePopsOffQueue branches the synthesiser can't emit (no pop outside
        // Connected/TimerRecovery; no pop when peer-busy / window-full / T1-stopped),
        // and the layer_3_initiated=No data arms of the initiator-only establishment
        // states. 238 + those 12 = 250, i.e. full coverage of the reachable set.
        hit.Should().BeGreaterThanOrEqualTo(238,
            "the scenario battery should behaviourally exercise every reachable one of the 250 transitions " +
            "(the 12 remaining are documented-unreachable in block 41); " +
            "if this drops, a scenario regressed or a path stopped being reached");
    }

    // ─── The scenario battery ───────────────────────────────────────────

    private static HashSet<(string, string)> RunBatteryAndCollectFired()
    {
        var fired = new HashSet<(string, string)>();
        void Collect(TwoStationHarness h) { foreach (var t in h.FiredTransitions) fired.Add(t); }

        // Coverage measurement only — correctness is asserted by the dedicated
        // conformance suites, so suspend the per-step oracle (injection scenarios
        // post frames outside the submitted/delivered model).
        TwoStationHarness New(bool srej = false, int k = 4, bool extended = false, int n2 = 12,
            bool segmenter = false, int? n1 = null)
        {
            var h = TwoStationHarness.Build(srej: srej, k: k, extended: extended, n2: n2, segmenter: segmenter, n1: n1);
            h.CheckAfterEachStep = false;
            return h;
        }

        // 1. Connect (from A) + clean disconnect.
        { var h = New(); h.Connect(); h.Disconnect(h.A); Collect(h); }

        // 2. Connect initiated by B.
        { var h = New(); h.ConnectFrom(h.B); h.Disconnect(h.B); Collect(h); }

        // 3. Bidirectional data transfer + delayed-ack flush.
        {
            var h = New(); h.Connect();
            h.Submit(h.A, 0xA0); h.Submit(h.B, 0xB0); h.Submit(h.A, 0xA1); h.Submit(h.B, 0xB1);
            h.Settle(); h.FlushAcks(); Collect(h);
        }

        // 4. Window-full transfer that wraps the modulus.
        {
            var h = New(k: 4); h.Connect();
            for (byte i = 0; i < 12; i++) h.Submit(h.A, i);
            h.FlushAcks(); Collect(h);
        }

        // 5. Single-drop REJ recovery (Connected → TimerRecovery → recover).
        {
            var h = New(k: 4); h.Connect();
            var dropped = false;
            h.Link.Drop = f => { if (!dropped && f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0) { dropped = true; return true; } return false; };
            for (byte i = 0; i < 4; i++) h.Submit(h.A, i);
            for (int r = 0; r < 30 && !Converged(h); r++) h.AdvanceT1();
            Collect(h);
        }

        // 6. SREJ recovery under intermittent loss.
        {
            var h = New(srej: true, k: 4); h.Connect();
            var budget = 2;
            h.Link.Drop = f => { if (budget > 0 && f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0) { budget--; return true; } return false; };
            for (byte i = 0; i < 6; i++) h.Submit(h.A, i);
            for (int r = 0; r < 40 && !Converged(h); r++) h.AdvanceT1();
            Collect(h);
        }

        // 7. RNR flow control (own-receiver-busy → RNR → resume).
        {
            var h = New(k: 4); h.Connect();
            h.Submit(h.A, 0x01);
            h.SetBusy(h.B); h.Submit(h.A, 0x02); h.ClearBusy(h.B); h.FlushAcks();
            Collect(h);
        }

        // 8. Sustained loss → N2 exhaustion → disconnect from TimerRecovery.
        {
            var h = New(k: 4); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0;
            h.Submit(h.A, 0x01);
            for (int r = 0; r < 20 && h.A.State != "Disconnected"; r++) h.AdvanceT1();
            Collect(h);
        }

        // 9. FRMR + DM + info-not-permitted received in Connected and TimerRecovery.
        {
            var h = New(k: 4); h.Connect();
            h.InjectFrameBytes(h.A, FrmrTo(h.A));        // → re-establish
            h.InjectFrameBytes(h.A, DmTo(h.A));          // → teardown
            Collect(h);
        }
        {
            var h = New(k: 4); h.Connect();
            // DM carrying info → info_not_permitted (M) → re-establish.
            h.InjectFrameBytes(h.A, DmTo(h.A).Concat(new byte[] { 0xDE }).ToArray());
            Collect(h);
        }
        {
            // Drive into TimerRecovery, then inject the receive-column frames.
            var h = New(k: 4); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0;
            h.Submit(h.A, 0x00); h.AdvanceT1();
            h.Link.Drop = null;
            h.InjectFrameBytes(h.A, RejTo(h.A, 0));
            Collect(h);
        }
        {
            var h = New(k: 4); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && (f.Control & 0x01) == 0;
            h.Submit(h.A, 0x00); h.AdvanceT1();
            h.InjectFrameBytes(h.A, FrmrTo(h.A));        // FRMR in TimerRecovery
            Collect(h);
        }

        // 10. mod-128 (extended) establishment — the figc4.6 AwaitingV22Connection
        // column. The Ax25Spec44 redirect (default on) routes a v2.2-preferred
        // connect here instead of figc4.2's mod-8 AwaitingConnection, so this is the
        // battery that lifts AwaitingV22Connection off 0/25. Each block drives a
        // different figc4.6 path.

        // 10a. Happy path: SABME → UA → Connected (mod-128), data, clean disconnect.
        { var h = New(extended: true); h.Connect(); h.Submit(h.A, 0xC0); h.FlushAcks(); h.Disconnect(h.A); Collect(h); }

        // 10b. Lost SABME → T1 retry RESENDS SABME (t13_t1_expiry_no), then converges.
        {
            var h = New(extended: true);
            var dropped = 0;
            h.Link.Drop = f => { if ((f.Control & 0xEF) == 0x6F && f.Source.Callsign.Equals(h.A.Context.Local) && dropped == 0) { dropped++; return true; } return false; };
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            h.AdvanceT1();          // t13_t1_expiry_no → resend SABME → UA → Connected
            Collect(h);
        }

        // 10c. §975 FRMR fallback (t14_frmr_received): peer rejects SABME → set
        // version 2.0, re-establish, fall to AwaitingConnection. Swallow the
        // initiator's establishment frames so it parks where the fallback leaves it.
        {
            var h = New(extended: true);
            h.Link.Drop = f => ((f.Control & 0xEF) == 0x6F || (f.Control & 0xEF) == 0x2F) && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
                h.Inject(h.A, new FrmrReceived(Ax25Frame.Frmr(h.A.Context.Local, h.A.Context.Remote, info: ReadOnlySpan<byte>.Empty)));
            Collect(h);
        }

        // 10d. Receive-column odds that STAY in AwaitingV22Connection: DL-UNIT-DATA
        // (t03), UI received (t10_no / t10_yes), a UA with F=0 (t12_ua_received_no →
        // DL-ERROR D), a SABME-while-pending collision (t15_sabme_received → UA), a
        // DISC (t17_disc_received), and a redundant DL-CONNECT (t02). All keep A
        // parked, so they can share one rig. (Establishment frames are swallowed so
        // the harness peer never UAs us out of the state.)
        {
            var h = New(extended: true);
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.Link.Drop = f => ((f.Control & 0xEF) == 0x6F || (f.Control & 0xEF) == 0x2F) && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
            {
                h.A.Session.PostEvent(new DlConnectRequest());                              // t02 → discard queue, set L3 init
                h.A.Session.PostEvent(new DlUnitDataRequest(new byte[] { 0x01 }));          // t03 → UI command
                h.A.Session.PostEvent(new DlDataRequest(new byte[] { 0x02 }));              // t04_yes (layer_3_initiated) → no-op buffer
                h.Settle();
                h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "y"u8).ToBytes());       // t10_ui_received_no
                h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "z"u8, pollFinal: true).ToBytes()); // t10_ui_received_yes
                h.InjectFrameBytes(h.A, Ax25Frame.Ua(la, re, finalBit: false).ToBytes());   // t12_ua_received_no → DL-ERROR D, stay
                h.InjectFrameBytes(h.A, Ax25Frame.Sabme(la, re).ToBytes());                 // t15_sabme_received → UA, stay
                h.InjectFrameBytes(h.A, Ax25Frame.Disc(la, re).ToBytes());                  // t17_disc_received → stay
            }
            Collect(h);
        }

        // 10d-i. DM(F=1) tears the v2.2 connect down (t11_dm_received_yes →
        // Disconnected). This is the FIGURE-LITERAL path, which only fires with
        // Ax25Spec48 OFF now: by default Ax25Spec48DmRejectionDegradesToV20 rewrites
        // a DM to the FRMR-fallback (t14_frmr_received) so a DM-ing peer (XRouter)
        // degrades to v2.0 instead of failing. Turn off ONLY Spec48 (keep Spec44 on
        // so the connect still reaches AwaitingV22Connection — full StrictlyFaithful
        // would park it in the mod-8 AwaitingConnection state). Fresh rig.
        {
            var h = TwoStationHarness.Build(extended: true,
                quirks: Ax25SessionQuirks.Default with { Ax25Spec48DmRejectionDegradesToV20 = false });
            h.CheckAfterEachStep = false;
            h.Link.Drop = f => (f.Control & 0xEF) == 0x6F && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
                h.InjectFrameBytes(h.A, Ax25Frame.Dm(h.A.Context.Local, h.A.Context.Remote, finalBit: true).ToBytes());
            Collect(h);
        }

        // 10d-i-bis. DM(F=1) DEGRADES to v2.0 by default (Ax25Spec48): the same DM
        // that the StrictlyFaithful rig above tore down now runs t14_frmr_received
        // (force v2.0 → Establish via SABM → AwaitingConnection). Default-on rig.
        {
            var h = New(extended: true);
            h.Link.Drop = f => (f.Control & 0xEF) == 0x6F && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
                h.InjectFrameBytes(h.A, Ax25Frame.Dm(h.A.Context.Local, h.A.Context.Remote, finalBit: true).ToBytes());
            Collect(h);
        }

        // 10d-ii. DM(F=0) drops to the mod-8 AwaitingConnection state
        // (t11_dm_received_no → AwaitingConnection). Figure-literal path — with
        // Ax25Spec48 off (by default Ax25Spec48 degrades this to t14 too). Keep
        // Spec44 on so the connect reaches AwaitingV22Connection. Fresh rig.
        {
            var h = TwoStationHarness.Build(extended: true,
                quirks: Ax25SessionQuirks.Default with { Ax25Spec48DmRejectionDegradesToV20 = false });
            h.CheckAfterEachStep = false;
            h.Link.Drop = f => (f.Control & 0xEF) == 0x6F && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
                h.InjectFrameBytes(h.A, Ax25Frame.Dm(h.A.Context.Local, h.A.Context.Remote, finalBit: false).ToBytes());
            Collect(h);
        }

        // 10d-iii. SABM(v2.0) received while awaiting v2.2 → UA, set version 2.0,
        // drop to AwaitingConnection (t16_sabm_received). Fresh rig.
        {
            var h = New(extended: true);
            h.Link.Drop = f => (f.Control & 0xEF) == 0x6F && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
                h.InjectFrameBytes(h.A, Ax25Frame.Sabm(h.A.Context.Local, h.A.Context.Remote).ToBytes());
            Collect(h);
        }

        // 10e. N2 exhaustion while awaiting the v2.2 connection (t13_t1_expiry_yes):
        // drop every SABME so the retries never get a UA, until RC == N2 → DL-ERROR
        // G + DL-DISCONNECT → Disconnected.
        {
            var h = New(extended: true, n2: 2);
            h.Link.Drop = f => (f.Control & 0xEF) == 0x6F && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            for (int r = 0; r < 6 && h.A.State == "AwaitingV22Connection"; r++) h.AdvanceT1();
            Collect(h);
        }

        // 11. Disconnected-state receive column: deliver assorted frames to a
        // station that has no session up (exercises figc4.1's receive handling —
        // a UI (→ DL-UNIT-DATA indication via UI_Check), DISC→DM, spurious UA, and
        // an info-bearing frame → info_not_permitted).
        {
            var h = New();
            var local = h.A.Context.Local; var remote = h.A.Context.Remote;
            h.InjectFrameBytes(h.A, Ax25Frame.Ui(local, remote, info: "x"u8).ToBytes());
            h.InjectFrameBytes(h.A, Ax25Frame.Disc(local, remote).ToBytes());
            h.InjectFrameBytes(h.A, Ax25Frame.Ua(local, remote).ToBytes());
            // DM carrying info → info_not_permitted (M) in Disconnected.
            h.InjectFrameBytes(h.A, Ax25Frame.Dm(local, remote).ToBytes().Concat(new byte[] { 0x01 }).ToArray());
            Collect(h);
        }

        // 12. AwaitingConnection receive column: hold A there (drop B's UA), then
        // walk the non-terminal receives (DM F=0, DISC, a queued DL-DATA, a T1
        // retransmit of the SABM) and finish by abandoning on a DM(F=1).
        {
            var h = New();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63; // B's UA
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingConnection")
            {
                h.InjectFrameBytes(h.A, Ax25Frame.Dm(la, re, finalBit: false).ToBytes());  // DM F=0 → stay
                h.InjectFrameBytes(h.A, Ax25Frame.Disc(la, re).ToBytes());                 // DISC → stay
                h.A.Session.PostEvent(new DlDataRequest(new byte[] { 0x01 }));             // buffered while connecting (t09)
                h.Settle();
                h.AdvanceT1();                                                             // T1 → retransmit SABM
                h.InjectFrameBytes(h.A, Ax25Frame.Dm(la, re, finalBit: true).ToBytes());   // DM F=1 → Disconnected
            }
            Collect(h);
        }
        // 12b. AwaitingConnection T1 → N2 exhaustion (give up → Disconnected).
        {
            var h = New(n2: 2);
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63;
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            for (int r = 0; r < 6 && h.A.State == "AwaitingConnection"; r++) h.AdvanceT1();
            Collect(h);
        }

        // 13. AwaitingRelease receive column: hold A there (drop B's UA to the
        // DISC), walk the non-terminal receives (UA F=0, DISC, SABM, a T1
        // retransmit of the DISC) and finish on a UA(F=1).
        {
            var h = New(); h.Connect();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63; // B's UA
            h.A.Session.PostEvent(new DlDisconnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingRelease")
            {
                h.InjectFrameBytes(h.A, Ax25Frame.Ua(la, re, finalBit: false).ToBytes());  // UA F=0 → stay
                h.InjectFrameBytes(h.A, Ax25Frame.Disc(la, re).ToBytes());                 // DISC → stay
                h.InjectFrameBytes(h.A, Ax25Frame.Sabm(la, re).ToBytes());                 // SABM → stay
                h.AdvanceT1();                                                             // T1 → retransmit DISC
                h.InjectFrameBytes(h.A, Ax25Frame.Ua(la, re, finalBit: true).ToBytes());   // UA F=1 → Disconnected
            }
            Collect(h);
        }

        // ──────────────────────────────────────────────────────────────────
        // v2.2 arc V5a — extended-mode (mod-128) behavioural coverage. Every
        // block below runs on an extended link (`New(extended: true)`), routed
        // through AwaitingV22Connection by the figc4.1 Ax25Spec44 redirect, so
        // the Connected / TimerRecovery N(S)/N(R)/N(R)-window paths execute in
        // the 7-bit sequence space. Logical transition ids are mode-independent,
        // so these lift coverage by reaching receive-column paths the mod-8
        // battery never drives — and prove they hold at modulo-128.
        // ──────────────────────────────────────────────────────────────────

        // 14. mod-128 bidirectional data transfer + delayed-ack flush. Both
        // directions carry I-frames, so the in-sequence I-received paths fire
        // on both ends (ack-pending and not), the LM-SEIZE-confirm delayed-ack
        // flush runs, and RRs ack in the extended space.
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.Submit(h.A, 0xA0); h.Submit(h.B, 0xB0); h.Submit(h.A, 0xA1); h.Submit(h.B, 0xB1);
            h.Settle(); h.FlushAcks(); Collect(h);
        }

        // 15. mod-128 window-full transfer that WRAPS the 127→0 boundary. Seed
        // both ends near the top of the 7-bit ring (a valid "already sent 124
        // frames" state) and transfer a burst across the wrap — exercises the
        // extended send-window/V(S)/V(R) arithmetic over the modulus lap.
        {
            var h = New(extended: true, k: 8); h.Connect();
            const byte seed = 124;
            h.A.Context.VS = h.A.Context.VA = seed; h.A.Context.VR = seed;
            h.B.Context.VS = h.B.Context.VA = seed; h.B.Context.VR = seed;
            for (byte i = 0; i < 8; i++) h.Submit(h.A, (byte)(0x40 + i));   // N(S)=124..127,0..3
            h.FlushAcks(); Collect(h);
        }

        // 16. mod-128 single-drop REJ recovery (Connected → TimerRecovery →
        // recover). Drives the TimerRecovery RR/REJ/I receive columns and the
        // poll/final ack that completes recovery, all in the 7-bit space.
        {
            var h = New(extended: true, k: 8); h.Connect();
            var dropped = false;
            h.Link.Drop = f => { if (!dropped && f.Source.Callsign.Equals(h.A.Context.Local) && Ax25FrameClassifier.Classify(f) is IFrameReceived && f.Ns == 1) { dropped = true; return true; } return false; };
            for (byte i = 0; i < 5; i++) h.Submit(h.A, i);
            for (int r = 0; r < 40 && !Converged(h); r++) h.AdvanceT1();
            Collect(h);
        }

        // 17. mod-128 SREJ recovery under intermittent loss (multi-frame). Hits
        // the extended SREJ-received + out-of-sequence I-received SREJ paths in
        // Connected and TimerRecovery.
        {
            var h = New(extended: true, srej: true, k: 8, n2: 40); h.Connect();
            var rng = new Random(7);
            var dropsLeft = 3;
            h.Link.Drop = f => { if (dropsLeft > 0 && f.Source.Callsign.Equals(h.A.Context.Local) && Ax25FrameClassifier.Classify(f) is IFrameReceived && rng.NextDouble() < 0.6) { dropsLeft--; return true; } return false; };
            for (byte i = 0; i < 8; i++) h.Submit(h.A, i);
            for (int r = 0; r < 60 && !Converged(h); r++) h.AdvanceT1();
            Collect(h);
        }

        // 18. mod-128 bidirectional loss recovery: both directions carry data
        // AND lose frames, so a station receives peer I-frames / supervisory
        // frames WHILE itself recovering — the TimerRecovery I-received and
        // RR/RNR receive columns (the "data both ways under loss" paths).
        {
            var h = New(extended: true, srej: true, k: 8, n2: 40); h.Connect();
            var aDrops = 2; var bDrops = 2;
            h.Link.Drop = f =>
            {
                if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
                if (aDrops > 0 && f.Source.Callsign.Equals(h.A.Context.Local) && f.Ns == 1) { aDrops--; return true; }
                if (bDrops > 0 && f.Source.Callsign.Equals(h.B.Context.Local) && f.Ns == 1) { bDrops--; return true; }
                return false;
            };
            for (byte i = 0; i < 4; i++) { h.Submit(h.A, (byte)(0xA0 + i)); h.Submit(h.B, (byte)(0xB0 + i)); }
            for (int r = 0; r < 60 && !Converged(h); r++) h.AdvanceT1();
            Collect(h);
        }

        // 18b. mod-128 TimerRecovery receive columns — fold the proven injection
        // technique (TimerRecoveryConformanceTests) into the ledger. Drive A into
        // TimerRecovery with N unacked extended I-frames (drop A's I-frames, expire
        // T1), then inject a crafted supervisory / I frame "from B" the well-behaved
        // peer wouldn't produce on cue — reaching the figc4.5 RR/RNR/REJ/SREJ/I
        // receive branches in the 7-bit space. Each fresh rig keeps the V(s)/V(a)
        // state known so a specific branch is hit; the oracle is off (coverage only).
        TwoStationHarness InTimerRecovery128(byte outstanding, bool srej = false)
        {
            var h = New(extended: true, srej: srej, k: 8, n2: 40); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && Ax25FrameClassifier.Classify(f) is IFrameReceived;
            for (byte i = 0; i < outstanding; i++) h.Submit(h.A, i);
            h.AdvanceT1();                 // unacked I-frame's T1 → poll → TimerRecovery
            h.Link.Drop = null;
            return h;
        }
        // RR response, F=1, N(R)=V(s) (everything acked) → completes recovery to Connected.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, RrExt(h.A, nr: 1, isCmd: false, pf: true)); Collect(h); }
        // RR command, P=1, in-window (an enquiry) → A responds, stays recovering.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RrExt(h.A, nr: 1, isCmd: true, pf: true)); Collect(h); }
        // RR response, F=0 (neither P nor F), in-window → bare ack, stays.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RrExt(h.A, nr: 1, isCmd: false, pf: false)); Collect(h); }
        // RNR command, P=1, in-window → peer-busy + enquiry response, stays.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RnrExt(h.A, nr: 1, isCmd: true, pf: true)); Collect(h); }
        // RNR response, F=1, N(R)=V(s) → peer-busy, everything acked → Connected.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, RnrExt(h.A, nr: 1, isCmd: false, pf: true)); Collect(h); }
        // REJ command, P=1, N(R)=V(s) → retransmit + complete.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, RejExt(h.A, nr: 1, isCmd: true, pf: true)); Collect(h); }
        // REJ response, F=1, in-window, V(s)≠N(R) (partial) → stays recovering.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RejExt(h.A, nr: 1, isCmd: false, pf: true)); Collect(h); }
        // In-sequence I command (N(S)=V(R)), P=0 → deliver peer data while recovering.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, IExt(h.A, nr: 0, ns: 0, payload: 0xBB, pf: false)); Collect(h); }
        // In-sequence I command, P=1 → deliver + enquiry response.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, IExt(h.A, nr: 0, ns: 0, payload: 0xCC, pf: true)); Collect(h); }
        // Out-of-sequence I command (N(S)=V(R)+2, a gap) → REJ/SREJ the gap, stays.
        { var h = InTimerRecovery128(1, srej: true); h.InjectFrameBytes(h.A, IExt(h.A, nr: 0, ns: 2, payload: 0xDD, pf: false)); Collect(h); }
        // SREJ response, in-window, F=1, V(s)=N(R) → selective retransmit + complete.
        { var h = InTimerRecovery128(1, srej: true); h.InjectFrameBytes(h.A, SrejExt(h.A, nr: 1, isCmd: false, pf: true)); Collect(h); }
        // SREJ response, in-window, F=0, V(s)=V(a)-shape → selective retransmit, stays.
        { var h = InTimerRecovery128(2, srej: true); h.InjectFrameBytes(h.A, SrejExt(h.A, nr: 0, isCmd: false, pf: false)); Collect(h); }
        // DISC received while recovering → teardown to Disconnected.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, Ax25Frame.Disc(h.A.Context.Local, h.A.Context.Remote).ToBytes()); Collect(h); }
        // SABME collision while recovering (vs_eq_va false here) → resync to Connected.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, Ax25Frame.Sabme(h.A.Context.Local, h.A.Context.Remote).ToBytes()); Collect(h); }
        // UI received while recovering (P=0 and P=1) → connectionless delivery, stays.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, Ax25Frame.Ui(h.A.Context.Local, h.A.Context.Remote, info: "r"u8).ToBytes()); Collect(h); }
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, Ax25Frame.Ui(h.A.Context.Local, h.A.Context.Remote, info: "s"u8, pollFinal: true).ToBytes()); Collect(h); }
        // DL primitives while recovering: redundant connect (t07), data submit (t02),
        // unit-data (t04), flow-off/on (t05/t06), and a catch-all upper-layer primitive.
        {
            var h = InTimerRecovery128(1);
            h.A.Session.PostEvent(new DlConnectRequest());                   // t07 → stay (discard queue, set L3 init)
            h.A.Session.PostEvent(new DlUnitDataRequest(new byte[] { 0x01 })); // t04 → UI command
            h.A.Session.PostEvent(new DlFlowOffRequest());                   // t05 → own busy
            h.A.Session.PostEvent(new DlFlowOnRequest());                    // t06 → clear busy
            h.A.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());   // t26 → catch-all upper, stay
            h.Inject(h.A, new AllOtherPrimitivesFromLowerLayer());           // t25 → catch-all lower, stay
            h.Inject(h.A, new ControlFieldError());                          // t08 → control-field error, stay
            Collect(h);
        }
        // N2 exhaustion from TimerRecovery with peer busy registered (the
        // RC_eq_N2 ∧ vs_eq_va ∧ peer_busy branch): mark peer busy, then starve.
        {
            var h = New(extended: true, k: 8, n2: 2); h.Connect();
            h.InjectFrameBytes(h.A, RnrExt(h.A, nr: 0, isCmd: true, pf: true));   // peer busy
            h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local);       // starve everything from A
            h.Submit(h.A, 0x01);
            for (int r = 0; r < 8 && h.A.State != "Disconnected"; r++) h.AdvanceT1();
            Collect(h);
        }

        // 18c. More mod-128 TimerRecovery receive branches reachable by injection.
        // DM received while recovering → teardown.
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, Ax25Frame.Dm(h.A.Context.Local, h.A.Context.Remote, finalBit: true).ToBytes()); Collect(h); }
        // LM-SEIZE-confirm in TimerRecovery, both ACK-pending branches (inject the
        // signal directly — models the link multiplexer granting the medium).
        { var h = InTimerRecovery128(1); h.A.Context.AcknowledgePending = true;  h.Inject(h.A, new LmSeizeConfirm()); Collect(h); }
        { var h = InTimerRecovery128(1); h.A.Context.AcknowledgePending = false; h.Inject(h.A, new LmSeizeConfirm()); Collect(h); }
        // REJ command (P=1) variants: in-window-not-complete and a fresh out-of-
        // window N(R) (the re-establish branch) — the t23 command columns.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RejExt(h.A, nr: 1, isCmd: true, pf: true)); Collect(h); }
        { var h = InTimerRecovery128(1); h.InjectFrameBytes(h.A, RejExt(h.A, nr: 1, isCmd: true, pf: false)); Collect(h); }
        // SREJ command (P=1) → the t24 not-response columns.
        { var h = InTimerRecovery128(2, srej: true); h.InjectFrameBytes(h.A, SrejExt(h.A, nr: 0, isCmd: true, pf: true)); Collect(h); }
        // RR command (P=1) with TWO outstanding, N(R)=0 (no ack) — the
        // not-response/command branch with nothing newly acked.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RrExt(h.A, nr: 0, isCmd: true, pf: true)); Collect(h); }
        // RNR command (P=1), N(R)=0 — peer busy, no ack.
        { var h = InTimerRecovery128(2); h.InjectFrameBytes(h.A, RnrExt(h.A, nr: 0, isCmd: true, pf: true)); Collect(h); }
        // Out-of-sequence I (no SREJ) → REJ go-back-N branch (not srej_enabled).
        { var h = InTimerRecovery128(1, srej: false); h.InjectFrameBytes(h.A, IExt(h.A, nr: 0, ns: 2, payload: 0xEE, pf: false)); Collect(h); }
        // Out-of-sequence I, P=1 → REJ + enquiry response.
        { var h = InTimerRecovery128(1, srej: false); h.InjectFrameBytes(h.A, IExt(h.A, nr: 0, ns: 2, payload: 0xEF, pf: true)); Collect(h); }

        // 18d. TimerRecovery entered IDLE (via T3 expiry with V(s)=V(a)) so the
        // vs_eq_va SABM/SABME-received branches are reachable, plus DL-DATA /
        // I-frame-pops / flow paths in the empty-window recovery state. Drop B's
        // supervisory reply so A's T3-poll gets no answer and STAYS in
        // TimerRecovery with V(s)=V(a) (otherwise B's RR(F=1) completes recovery
        // and bounces A straight back to Connected).
        TwoStationHarness IdleInTimerRecovery128()
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && Ax25FrameClassifier.Classify(f) is RrReceived or RnrReceived;
            h.Inject(h.A, new T3Expiry());          // idle poll → TimerRecovery, V(s)=V(a)
            h.Link.Drop = null;
            return h;
        }
        {
            var h = IdleInTimerRecovery128();
            if (h.A.State == "TimerRecovery")
            {
                h.A.Session.PostEvent(new DlDataRequest(new byte[] { 0x77 }));   // t02 → I-frame pops (window open, T1 running)
                h.A.Session.PostEvent(new DlFlowOffRequest());                   // t05 → own busy
                h.A.Session.PostEvent(new DlFlowOnRequest());                    // t06 → clear busy (own busy, T1 running)
                h.Settle();
            }
            Collect(h);
        }
        {
            var h = IdleInTimerRecovery128();
            if (h.A.State == "TimerRecovery")
                h.InjectFrameBytes(h.A, Ax25Frame.Sabm(h.A.Context.Local, h.A.Context.Remote).ToBytes());   // t13_sabm_received_yes (vs_eq_va)
            Collect(h);
        }
        {
            var h = IdleInTimerRecovery128();
            if (h.A.State == "TimerRecovery")
                h.InjectFrameBytes(h.A, Ax25Frame.Sabme(h.A.Context.Local, h.A.Context.Remote).ToBytes());  // t14_sabme_received_yes (vs_eq_va)
            Collect(h);
        }

        // 18e. More mod-128 Connected receive branches.
        // FRMR received on an extended link → figc4.4 t16_frmr_received_yes
        // (version_2_2) → re-establish, routed to AwaitingV22Connection.
        { var h = New(extended: true, k: 8); h.Connect(); h.InjectFrameBytes(h.A, FrmrTo(h.A)); Collect(h); }
        // In-sequence I command with P=1 while Connected (the enquiry-response
        // I-received branch) — inject directly so P=1 is guaranteed.
        { var h = New(extended: true, k: 8); h.Connect(); h.InjectFrameBytes(h.A, IExt(h.A, nr: 0, ns: 0, payload: 0x5A, pf: true)); Collect(h); }
        // LM-SEIZE-confirm in Connected with NO ack pending (t23_lm_seize_confirm_no).
        { var h = New(extended: true, k: 8); h.Connect(); h.A.Context.AcknowledgePending = false; h.Inject(h.A, new LmSeizeConfirm()); Collect(h); }
        // An over-N1 I-frame (info field too long) → the info_field_length error
        // branch of I-received (t26_i_received_yes_no_yes, version_2_2).
        {
            var h = New(extended: true, k: 8); h.Connect();
            var big = new byte[h.A.Context.N1 + 8];
            h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 0, info: big, pollBit: false, extended: true).ToBytes());
            Collect(h);
        }
        // An I-frame with N(R) out of the send window → t26_i_received_yes_yes_no_yes
        // (version_2_2 re-establish branch). N(R)=5 with V(a)=V(s)=0 is out of window.
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.InjectFrameBytes(h.A, IExt(h.A, nr: 5, ns: 0, payload: 0x33, pf: false));
            Collect(h);
        }

        // 19. mod-128 RNR flow control: B goes busy mid-transfer (RNR), A holds,
        // B resumes (RR) — the peer-busy RNR-received path in Connected and the
        // own-busy DL-FLOW-ON resume with/without T1 running.
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.Submit(h.A, 0x01);
            h.SetBusy(h.B); h.Submit(h.A, 0x02); h.Submit(h.A, 0x03); h.ClearBusy(h.B); h.FlushAcks();
            Collect(h);
        }

        // 20. mod-128 T3 idle keepalive: a quiescent connected station's T3
        // expiry polls the peer (figc4.4 t13 → TimerRecovery), then the RR(F=1)
        // response settles it back to Connected.
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.Inject(h.A, new T3Expiry());          // t13_t3_expiry → poll, → TimerRecovery
            h.AdvanceT1();                          // let the poll/response cycle settle
            Collect(h);
        }

        // 21. Connected receive-column odds reachable only by injection (mode-
        // independent, but unexercised by the well-behaved battery): a UI frame
        // (P=0 / P=1), and SABM / SABME collisions arriving on an established
        // link (the peer re-establishing) — each handled in place, link stays up.
        {
            var h = New(extended: true, k: 8); h.Connect();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "u"u8).ToBytes());                    // t18_ui_received_no
            h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "v"u8, pollFinal: true).ToBytes());    // t18_ui_received_yes
            h.InjectFrameBytes(h.A, Ax25Frame.Sabme(la, re).ToBytes());                               // t15_sabme_received (vs_eq_va)
            Collect(h);
        }
        // 21b. SABM collision on a connected link with frames outstanding
        // (not vs_eq_va), so the not-equal branch of SABM/SABME-received fires.
        {
            var h = New(extended: true, k: 8); h.Connect();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            // Wedge one I-frame outstanding (drop B's acks) so V(s) != V(a).
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && Ax25FrameClassifier.Classify(f) is RrReceived or RnrReceived;
            h.Submit(h.A, 0x01);
            if (h.A.Context.VS != h.A.Context.VA)
            {
                h.Link.Drop = null;
                h.InjectFrameBytes(h.A, Ax25Frame.Sabm(la, re).ToBytes());                            // t14_sabm_received_no
            }
            Collect(h);
        }

        // 22. AwaitingV22Connection — push past the establishment column. Hold A
        // in the v2.2-pending state (swallow its establishment frames) and drive
        // the catch-all input columns + a control-field error that all keep it
        // parked: t06 (other upper-layer primitive), t18 (other lower-layer
        // primitive), t07 (control-field error). The genuinely-unreachable
        // misses (t08/t09 never-produced error inputs; the not-layer-3-initiated
        // t04_no/t05/t12_no branches — the responder never parks here, it goes
        // straight Disconnected→Connected on SABME) remain documented misses.
        {
            var h = New(extended: true);
            h.Link.Drop = f => ((f.Control & 0xEF) == 0x6F || (f.Control & 0xEF) == 0x2F) && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
            {
                h.A.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());   // t06 — catch-all upper-layer, stay
                h.Settle();
                h.Inject(h.A, new AllOtherPrimitivesFromLowerLayer());           // t18 — catch-all lower-layer, stay
                h.Inject(h.A, new ControlFieldError());                          // t07 — control-field error, stay
            }
            Collect(h);
        }

        // ──────────────────────────────────────────────────────────────────
        // MDL (management_data_link) machine — Ready / Negotiating. The MDL
        // driver runs its own Ax25Session; the harness forwards its
        // TransitionFired, so these register on the SAME ledger (its Ready /
        // Negotiating state names don't collide with the data-link states).
        // Drives every figc5.1/5.2 path the prose-bootstrap encodes.
        // ──────────────────────────────────────────────────────────────────

        // 23. Happy-path XID negotiation between two v2.2 stations: the figc4.6
        // UA path raises MDL-NEGOTIATE Request → XID command/response exchange →
        // both confirm. Ready t01 (negotiate) + Negotiating t01_yes (F=1 success).
        {
            var h = New(extended: true, srej: true, k: 8); h.Connect(); Collect(h);
        }

        // 24. MDL error B — an unexpected XID response arriving in Ready (no
        // command outstanding). Ready t02_xid_response_received.
        {
            var h = New(extended: true, srej: true, k: 8);
            var info = Packet.Ax25.Xid.XidInfoField.Encode(new Packet.Ax25.Xid.XidParameters { WindowSizeRx = 4 });
            h.A.Mdl.OnXidReceived(Ax25Frame.Xid(h.A.Context.Local, h.A.Context.Remote, info, isCommand: false, pollFinal: true));
            h.Settle(); Collect(h);
        }

        // 25. MDL error D — an XID response without F=1 while Negotiating (stays
        // Negotiating, TM201 still running). Negotiating t01_xid_response_received_no.
        {
            var h = New(extended: true, srej: true, k: 8);
            h.Link.Drop = f => (f.Control & 0xEF) == 0xAF && f.IsCommand && f.Source.Callsign.Equals(h.A.Context.Local);
            h.StartNegotiation(h.A);
            if (h.A.MdlState == "Negotiating")
            {
                var info = Packet.Ax25.Xid.XidInfoField.Encode(new Packet.Ax25.Xid.XidParameters { WindowSizeRx = 4 });
                h.A.Mdl.OnXidReceived(Ax25Frame.Xid(h.A.Context.Local, h.A.Context.Remote, info, isCommand: false, pollFinal: false));
                h.Settle();
            }
            Collect(h);
        }

        // 26. MDL v2.0 fallback — a pre-v2.2 peer FRMRs the XID command (figc5.2
        // t02_frmr_received → full §1436 v2.0 defaults, confirm, → Ready).
        {
            var h = New(extended: true, srej: true, k: 8);
            h.Link.Drop = f => (f.Control & 0xEF) == 0xAF && f.IsCommand && f.Source.Callsign.Equals(h.A.Context.Local);
            h.StartNegotiation(h.A);
            if (h.A.MdlState == "Negotiating")
            {
                h.A.Mdl.OnFrmrReceived(Ax25Frame.Frmr(h.A.Context.Local, h.A.Context.Remote, info: ReadOnlySpan<byte>.Empty));
                h.Settle();
            }
            Collect(h);
        }

        // 27. MDL TM201 retry + NM201 exhaustion (error C): drop every XID
        // command so no reply comes; TM201 retries (t03_tm201_expiry_no) then
        // gives up at RC==NM201 (t03_tm201_expiry_yes → MDL-ERROR C, → Ready).
        {
            var h = New(extended: true, srej: true, k: 8, n2: 2);
            h.Link.Drop = f => (f.Control & 0xEF) == 0xAF && f.Source.Callsign.Equals(h.A.Context.Local);
            h.StartNegotiation(h.A);
            for (int r = 0; r < 5 && h.A.MdlState == "Negotiating"; r++) h.AdvanceTm201();
            Collect(h);
        }

        // ──────────────────────────────────────────────────────────────────
        // Segmentation over a mod-128 link (V4b shim through the wired path).
        // ──────────────────────────────────────────────────────────────────

        // 28. Multi-segment payload over mod-128 with a mid-series drop +
        // selective (SREJ) recovery — the V4 headline path, folded into the
        // ledger so the segment I-frame send/receive + SREJ recovery register.
        {
            var h = New(extended: true, srej: true, k: 16, n2: 40, segmenter: true, n1: 64);
            h.Connect();
            var payload = Enumerable.Range(0, 300).Select(i => (byte)(i * 5 + 2)).ToArray();   // 5 segments
            var dropped = false;
            h.Link.Drop = f => { if (!dropped && f.Source.Callsign.Equals(h.A.Context.Local) && Ax25FrameClassifier.Classify(f) is IFrameReceived && f.Ns == 2) { dropped = true; return true; } return false; };
            h.SubmitLarge(h.A, payload);
            for (int r = 0; r < 40 && h.B.Delivered.Count == 0; r++) h.AdvanceT1();
            Collect(h);
        }

        // ──────────────────────────────────────────────────────────────────
        // Workstream-3 lift — drive the remaining REACHABLE receive / primitive
        // / catch-all columns across every state, by the same injection idiom
        // the blocks above already use (raw-frame injection for receive columns,
        // direct event injection for the figc-input columns the listener/codec
        // synthesises: ControlFieldError, InfoNotPermittedInFrame,
        // UOrSFrameLengthError, AllOtherCommands, IOrSCommandReceived, the two
        // AllOtherPrimitives). The genuinely-unreachable residue is documented at
        // the foot of this method (block 41) rather than forced.
        // ──────────────────────────────────────────────────────────────────

        // 29. Disconnected (figc4.1) receive + primitive + catch-all columns.
        // 29a. Primitives + catch-alls that all keep a session-less station parked.
        {
            var h = New();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.A.Session.PostEvent(new DlDisconnectRequest());                       // t01 → DL-DISCONNECT confirm
            h.A.Session.PostEvent(new DlUnitDataRequest(new byte[] { 0x01 }));      // t02 → UI command
            h.A.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());         // t04 — catch-all upper
            h.Settle();
            h.Inject(h.A, new AllOtherPrimitivesFromLowerLayer());                 // t06 — catch-all lower
            h.Inject(h.A, new ControlFieldError());                               // t07 — control-field error
            h.Inject(h.A, new UOrSFrameLengthError());                            // t09 — U/S length error
            h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "p"u8, pollFinal: true).ToBytes()); // t11_ui_received_yes
            // t05 all_other_commands: the listener reclassifies an RR/I/FRMR/… that
            // arrives at a disconnected station into AllOtherCommands → DM.
            h.Inject(h.A, new AllOtherCommands(Ax25Frame.Rr(la, re, 0, isCommand: true, pollFinal: true)));
            Collect(h);
        }
        // 29b. SABM / SABME arriving when the node won't accept (able_to_establish=No
        // → DM): t13_sabm_received_no, t14_sabme_received_no.
        {
            var h = New();
            h.A.Context.AcceptIncoming = false;
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.InjectFrameBytes(h.A, Ax25Frame.Sabm(la, re).ToBytes());            // t13_sabm_received_no
            h.InjectFrameBytes(h.A, Ax25Frame.Sabme(la, re).ToBytes());           // t14_sabme_received_no
            Collect(h);
        }

        // 30. AwaitingConnection (figc4.2) full column. Hold A there (drop B's UA)
        // and walk every non-terminal receive + primitive + catch-all + error
        // column that keeps it parked, plus the UA-with-F=0 (DL-ERROR D), the SABM
        // collision, and the SABME-while-awaiting (→ AwaitingV22Connection).
        {
            var h = New();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63; // B's UA
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingConnection")
            {
                h.A.Session.PostEvent(new DlConnectRequest());                     // t07 — redundant connect
                h.A.Session.PostEvent(new DlUnitDataRequest(new byte[] { 0x01 }));  // t08 — UI command
                h.A.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());     // t11 — catch-all upper
                h.Settle();
                h.Inject(h.A, new AllOtherPrimitivesFromLowerLayer());             // t06 — catch-all lower
                h.Inject(h.A, new ControlFieldError());                           // t13 — control-field error
                h.Inject(h.A, new InfoNotPermittedInFrame());                     // t14 — info-not-permitted
                h.Inject(h.A, new UOrSFrameLengthError());                        // t15 — U/S length error
                h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "y"u8).ToBytes());                    // t12_ui_received_no
                h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "z"u8, pollFinal: true).ToBytes());    // t12_ui_received_yes
                h.InjectFrameBytes(h.A, Ax25Frame.Ua(la, re, finalBit: false).ToBytes());                 // t04_ua_received_no → DL-ERROR D
                h.InjectFrameBytes(h.A, Ax25Frame.Sabm(la, re).ToBytes());                                // t16_sabm_received
            }
            Collect(h);
        }
        // 30b. DL-DISCONNECT request while connecting (t01 → still AwaitingConnection,
        // it just discards the queue). Fresh rig so 30's terminal SABM doesn't matter.
        {
            var h = New();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63;
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingConnection") h.A.Session.PostEvent(new DlDisconnectRequest());   // t01
            Collect(h);
        }
        // 30c. SABME arriving while in AwaitingConnection → AwaitingV22Connection
        // (t17_sabme_received). Fresh rig — this one leaves the state.
        {
            var h = New();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63;
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingConnection")
                h.InjectFrameBytes(h.A, Ax25Frame.Sabme(h.A.Context.Local, h.A.Context.Remote).ToBytes()); // t17
            Collect(h);
        }

        // 31. AwaitingRelease (figc4.3) full column — extend block 13. Hold A there
        // (drop B's UA to the DISC) and walk the remaining primitive / catch-all /
        // error columns, the SABME / DM(F=0) receives, the UI receives, and the
        // grouped "I, RR, RNR, REJ or SREJ command" column (P=0 and P=1).
        {
            var h = New(); h.Connect();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && (f.Control & 0xEF) == 0x63; // B's UA
            h.A.Session.PostEvent(new DlDisconnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingRelease")
            {
                h.A.Session.PostEvent(new DlDisconnectRequest());                  // t01 — redundant disconnect
                h.A.Session.PostEvent(new DlUnitDataRequest(new byte[] { 0x01 }));  // t05 — UI command
                h.A.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());     // t06 — catch-all upper
                h.Settle();
                h.Inject(h.A, new AllOtherPrimitivesFromLowerLayer());             // t04 — catch-all lower
                h.Inject(h.A, new ControlFieldError());                           // t07 — control-field error
                h.Inject(h.A, new InfoNotPermittedInFrame());                     // t08 — info-not-permitted
                h.Inject(h.A, new UOrSFrameLengthError());                        // t09 — U/S length error
                h.InjectFrameBytes(h.A, Ax25Frame.Sabme(la, re).ToBytes());                              // t11_sabme_received
                h.InjectFrameBytes(h.A, Ax25Frame.Dm(la, re, finalBit: false).ToBytes());                // t13_dm_received_no
                h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "y"u8).ToBytes());                    // t14_ui_received_no
                h.InjectFrameBytes(h.A, Ax25Frame.Ui(la, re, info: "z"u8, pollFinal: true).ToBytes());    // t14_ui_received_yes
                // The grouped "I, RR, RNR, REJ or SREJ Commands" input column (P=0 / P=1).
                h.Inject(h.A, new IOrSCommandReceived(Ax25Frame.Rr(la, re, 0, isCommand: true, pollFinal: false))); // t15_no
                h.Inject(h.A, new IOrSCommandReceived(Ax25Frame.Rr(la, re, 0, isCommand: true, pollFinal: true)));  // t15_yes
            }
            Collect(h);
        }
        // 31b. AwaitingRelease T1 → N2 exhaustion (RC==N2 → Disconnected,
        // t02_t1_expiry_yes). Drop ALL of B's frames so only T1 drives the retry.
        {
            var h = New(n2: 2); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local);
            h.A.Session.PostEvent(new DlDisconnectRequest());
            h.Settle();
            for (int r = 0; r < 8 && h.A.State == "AwaitingRelease"; r++) h.AdvanceT1();   // t02_t1_expiry_no … then _yes
            Collect(h);
        }

        // 32. AwaitingV22Connection error columns reachable only by injection:
        // t08_info_not_permitted_in_frame, t09_u_or_s_frame_length_error. (t07
        // control-field-error is already in the must-hit; these complete the trio.)
        {
            var h = New(extended: true);
            h.Link.Drop = f => ((f.Control & 0xEF) == 0x6F || (f.Control & 0xEF) == 0x2F) && f.Source.Callsign.Equals(h.A.Context.Local);
            h.A.Session.PostEvent(new DlConnectRequest());
            h.Settle();
            if (h.A.State == "AwaitingV22Connection")
            {
                h.Inject(h.A, new InfoNotPermittedInFrame());                     // t08
                h.Inject(h.A, new UOrSFrameLengthError());                        // t09
            }
            Collect(h);
        }

        // 33. Connected (figc4.4) — the mod-8 (version 2.0) receive columns. The
        // extended battery routes every connect through AwaitingV22Connection, so
        // the version_2_2=No branches of the receive columns never fire there. A
        // plain mod-8 connect stays version 2.0, so out-of-window supervisory
        // frames re-establish to AwaitingConnection (the _no_no / _no branches),
        // and the connect/error columns take their version-2.0 arm.
        // 33a. Out-of-window supervisory frames (N(R)=5, V(a)=V(s)=0): each
        // re-establishes via figc4.4 to AwaitingConnection. Fresh rig each (they
        // leave Connected).
        { var h = New(k: 4); h.Connect(); h.InjectFrameBytes(h.A, Rr(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }    // t21_rr_received_no_no
        { var h = New(srej: true, k: 4); h.Connect(); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }  // t22_rnr_received_no_no
        { var h = New(srej: true, k: 4); h.Connect(); h.InjectFrameBytes(h.A, Srej(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); } // t24_srej_received_no_no
        { var h = New(srej: true, k: 4); h.Connect(); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }  // t25_rej_received_no_no
        // 33b. mod-128 out-of-window supervisory → AwaitingV22Connection (the
        // version_2_2=Yes arm of the same columns): t21_no_yes (already covered),
        // t22_no_yes, t24_no_yes, t25_no_yes.
        { var h = New(extended: true, srej: true, k: 8); h.Connect(); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); }  // t22_rnr_received_no_yes
        { var h = New(extended: true, srej: true, k: 8); h.Connect(); h.InjectFrameBytes(h.A, Srej(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); } // t24_srej_received_no_yes
        { var h = New(extended: true, srej: true, k: 8); h.Connect(); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); }  // t25_rej_received_no_yes
        // 33c. mod-8 connect/error/SABME/UA columns (version 2.0 arm) + the
        // unit-data, flow, catch-all-upper, and own-busy I-received columns.
        {
            var h = New(k: 4); h.Connect();
            h.A.Session.PostEvent(new DlUnitDataRequest(new byte[] { 0x01 }));     // t04_dl_unit_data_request
            h.A.Session.PostEvent(new DlFlowOffRequest());                        // t05_yes (own busy on)
            h.A.Session.PostEvent(new DlFlowOffRequest());                        // t05_no (already busy; Spec43-inverted guard)
            h.A.Session.PostEvent(new DlFlowOnRequest());                         // clear busy
            h.A.Session.PostEvent(new DlFlowOnRequest());                         // t06_dl_flow_on_request_no (not busy)
            h.A.Session.PostEvent(new AllOtherPrimitivesFromUpperLayer());        // t08_all_other_primitives__from_upper_layer
            h.Settle();
            h.Inject(h.A, new ControlFieldError());                               // t09_control_field_error_no (v2.0)
            h.Inject(h.A, new UOrSFrameLengthError());                            // t11_u_or_s_frame_length_error_no (v2.0)
            // An I-frame arriving as a RESPONSE (command=No) → t26_i_received_no.
            // (The t15_sabme_received_no V(s)≠V(a) arm is driven in 33d below.)
            h.InjectFrameBytes(h.A, IResponse(h.A, nr: 0, ns: 0, payload: 0x22, pf: false, ext: false)); // t26_i_received_no
            Collect(h);
        }
        // 33d. mod-8 Connected with a frame outstanding (V(s)≠V(a)) then a SABME
        // collision (t15_sabme_received_no) + a redundant DL-CONNECT (t07_no, v2.0).
        {
            var h = New(k: 4); h.Connect();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && Ax25FrameClassifier.Classify(f) is RrReceived or RnrReceived;
            h.Submit(h.A, 0x01);
            if (h.A.Context.VS != h.A.Context.VA)
            {
                h.Link.Drop = null;
                h.InjectFrameBytes(h.A, Ax25Frame.Sabme(la, re).ToBytes());       // t15_sabme_received_no (vs≠va)
            }
            h.A.Session.PostEvent(new DlConnectRequest());                        // t07_dl_connect_request_no (v2.0 → AwaitingConnection)
            Collect(h);
        }
        // 33e. mod-8 Connected, own receiver busy, I-frame received P=1 / P=0
        // (t26_i_received_yes_yes_yes_yes_yes / _yes_no) + a UA(F=0) collision
        // (t17_ua_received_no, v2.0).
        {
            var h = New(k: 4); h.Connect();
            var la = h.A.Context.Local; var re = h.A.Context.Remote;
            h.A.Session.PostEvent(new DlFlowOffRequest());                        // own receiver busy
            h.InjectFrameBytes(h.A, Ax25Frame.I(la, re, nr: 0, ns: 0, info: new byte[] { 0x44 }, pollBit: true).ToBytes());  // t26_…_yes_yes (own busy, P=1)
            h.InjectFrameBytes(h.A, Ax25Frame.I(la, re, nr: 0, ns: 0, info: new byte[] { 0x45 }, pollBit: false).ToBytes()); // t26_…_yes_no (own busy, P=0)
            h.InjectFrameBytes(h.A, Ax25Frame.Ua(la, re, finalBit: false).ToBytes());                                        // t17_ua_received_no
            Collect(h);
        }

        // 34. Connected mod-128 — the info-not-permitted / U-or-S-length columns'
        // version_2_2=Yes arm (t10_info_not_permitted_in_frame_yes,
        // t11_u_or_s_frame_length_error_yes), reachable by direct injection.
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.Inject(h.A, new InfoNotPermittedInFrame());                         // t10_info_not_permitted_in_frame_yes
            h.Inject(h.A, new UOrSFrameLengthError());                            // t11_u_or_s_frame_length_error_yes
            Collect(h);
        }

        // 34b. Connected — the remaining figc4.4 columns the blocks above miss.
        // DL-CONNECT on a v2.2 (extended) link → AwaitingV22Connection (t07_yes).
        { var h = New(extended: true, k: 8); h.Connect(); h.A.Session.PostEvent(new DlConnectRequest()); Collect(h); }
        // DL-FLOW-ON while own-busy AND T1 running (an I-frame outstanding) →
        // t06_dl_flow_on_request_yes_yes. Drop B's acks so V(s)≠V(a) keeps T1 armed.
        {
            var h = New(extended: true, k: 8); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && Ax25FrameClassifier.Classify(f) is RrReceived or RnrReceived;
            h.Submit(h.A, 0x01);                       // one outstanding ⇒ T1 running
            h.A.Session.PostEvent(new DlFlowOffRequest());   // own busy
            h.A.Session.PostEvent(new DlFlowOnRequest());    // own busy ∧ T1 running → t06_yes_yes
            Collect(h);
        }
        // I command, info field too long, mod-8 (version 2.0) → t26_i_received_yes_no_no.
        {
            var h = New(k: 4); h.Connect();
            var big = new byte[h.A.Context.N1 + 8];
            h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 0, info: big, pollBit: false).ToBytes()); // t26_i_received_yes_no_no
            Collect(h);
        }
        // I command, info valid, N(R) out of window, mod-8 (version 2.0) →
        // t26_i_received_yes_yes_no_no (re-establish to AwaitingConnection).
        {
            var h = New(k: 4); h.Connect();
            h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 5, ns: 0, info: new byte[] { 0x33 }, pollBit: false).ToBytes()); // t26_i_received_yes_yes_no_no
            Collect(h);
        }
        // Out-of-sequence I with a reject_exception already pending, P=1 (go-back-N,
        // SREJ off) → t26_i_received_yes_yes_yes_no_no_yes_yes. The first gap frame
        // sets reject_exception; the second (P=1) takes the rejectexc∧P=1 arm.
        {
            var h = New(k: 4); h.Connect();
            h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 2, info: new byte[] { 0x47 }, pollBit: false).ToBytes()); // gap → reject_exception
            h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 3, info: new byte[] { 0x48 }, pollBit: true).ToBytes());  // rejectexc ∧ P=1
            Collect(h);
        }

        // ──────────────────────────────────────────────────────────────────
        // 35–38. TimerRecovery (figc4.5) receive columns, both moduli. The mod-128
        // injection block (18b–18d) already drives many of these; here we fill the
        // command-vs-response × P/F × N(R)-window matrix the well-behaved peer can't
        // produce on cue, including the mod-8 (version 2.0) re-establish arms.
        // ──────────────────────────────────────────────────────────────────

        // Drive A into TimerRecovery holding `outstanding` unacked I-frames, mod-8
        // OR mod-128. Mirrors InTimerRecovery128 but parametric on the modulus, so
        // the version_2_2=No (mod-8) receive arms are reachable.
        TwoStationHarness InTimerRecovery(byte outstanding, bool srej = false, bool extended = false, int k = 4)
        {
            var h = New(srej: srej, k: k, extended: extended, n2: 40); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.A.Context.Local) && Ax25FrameClassifier.Classify(f) is IFrameReceived;
            for (byte i = 0; i < outstanding; i++) h.Submit(h.A, i);
            h.AdvanceT1();                 // unacked I-frame's T1 → poll → TimerRecovery
            h.Link.Drop = null;
            return h;
        }
        // Idle TimerRecovery (V(s)=V(a)) reached via a T3 poll, with B's supervisory
        // reply dropped so A stays recovering with an empty window. Mirrors
        // IdleInTimerRecovery128 but parametric on the modulus.
        TwoStationHarness IdleInTimerRecovery(bool srej = false, bool extended = false, int k = 4)
        {
            var h = New(srej: srej, k: k, extended: extended); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local) && Ax25FrameClassifier.Classify(f) is RrReceived or RnrReceived;
            h.Inject(h.A, new T3Expiry());          // idle poll → TimerRecovery, V(s)=V(a)
            h.Link.Drop = null;
            return h;
        }

        // 35. TimerRecovery RR receive column (figc4.5 t18) — the branches the
        // mod-128 block doesn't reach. N(R)=5 with V(a)=V(s)=0..2 is out of window.
        // bare RR response F=0, N(R) out of window → t18_rr_received_no_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rr(h.A, nr: 5, isCmd: false, pf: false, ext: false)); Collect(h); }
        // RR command P=1, N(R) out of window, mod-8 → re-establish (t18_rr_received_no_yes_no_no).
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rr(h.A, nr: 5, isCmd: true, pf: true, ext: false)); Collect(h); }
        // RR command P=1, N(R) out of window, mod-128 → t18_rr_received_no_yes_no_yes.
        { var h = InTimerRecovery(2, extended: true, k: 8); h.InjectFrameBytes(h.A, Rr(h.A, nr: 5, isCmd: true, pf: true, ext: true)); Collect(h); }
        // RR response F=1, N(R) out of window, mod-8 → t18_rr_received_yes_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rr(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }
        // RR response F=1, N(R) out of window, mod-128 → t18_rr_received_yes_no_yes.
        { var h = InTimerRecovery(2, extended: true, k: 8); h.InjectFrameBytes(h.A, Rr(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); }

        // 36. TimerRecovery RNR receive column (figc4.5 t19).
        // bare RNR response F=0, N(R) in window → t19_rnr_received_no_no_yes.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 1, isCmd: false, pf: false, ext: false)); Collect(h); }
        // bare RNR response F=0, N(R) out of window → t19_rnr_received_no_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: false, pf: false, ext: false)); Collect(h); }
        // RNR command P=1, N(R) out of window, mod-8 → t19_rnr_received_no_yes_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: true, pf: true, ext: false)); Collect(h); }
        // RNR command P=1, N(R) out of window, mod-128 → t19_rnr_received_no_yes_no_yes.
        { var h = InTimerRecovery(2, extended: true, k: 8); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: true, pf: true, ext: true)); Collect(h); }
        // RNR response F=1, N(R) out of window, mod-8 → t19_rnr_received_yes_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }
        // RNR response F=1, N(R) out of window, mod-128 → t19_rnr_received_yes_no_yes.
        { var h = InTimerRecovery(2, extended: true, k: 8); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); }
        // RNR response F=1, N(R) in window but V(s)≠N(R) (partial ack) → t19_rnr_received_yes_yes_no.
        { var h = InTimerRecovery(3); h.InjectFrameBytes(h.A, Rnr(h.A, nr: 1, isCmd: false, pf: true, ext: false)); Collect(h); }

        // 37. TimerRecovery REJ receive column (figc4.5 t23).
        // REJ command P=1, N(R) out of window, mod-128 → t23_rej_received_no_yes_no_yes.
        { var h = InTimerRecovery(2, extended: true, k: 8); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: true, pf: true, ext: true)); Collect(h); }
        // REJ command P=1, N(R) out of window, mod-8 → t23_rej_received_no_yes_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: true, pf: true, ext: false)); Collect(h); }
        // bare REJ response F=0, N(R) in window, V(s)≠N(R) → t23_rej_received_no_no_yes_no.
        { var h = InTimerRecovery(3); h.InjectFrameBytes(h.A, Rej(h.A, nr: 1, isCmd: false, pf: false, ext: false)); Collect(h); }
        // bare REJ response F=0, N(R) out of window, mod-8 → t23_rej_received_no_no_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: false, pf: false, ext: false)); Collect(h); }
        // REJ response F=1, N(R) out of window, mod-128 → t23_rej_received_yes_no_yes.
        { var h = InTimerRecovery(2, extended: true, k: 8); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); }
        // REJ response F=1, N(R) out of window, mod-8 → t23_rej_received_yes_no_no.
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Rej(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }
        // REJ response F=1, N(R) in window, V(s)==N(R) (complete) → t23_rej_received_yes_yes_yes.
        { var h = InTimerRecovery(1); h.InjectFrameBytes(h.A, Rej(h.A, nr: 1, isCmd: false, pf: true, ext: false)); Collect(h); }

        // 38. TimerRecovery SREJ receive column (figc4.5 t24) — the branches the
        // mod-128 block doesn't reach.
        // SREJ command (response=No), N(R) in window, P=1, V(s)==N(R) → t24_srej_received_no_yes_yes_yes.
        { var h = InTimerRecovery(1, srej: true); h.InjectFrameBytes(h.A, Srej(h.A, nr: 1, isCmd: true, pf: true, ext: false)); Collect(h); }
        // SREJ command, N(R) in window, P=0, V(s)≠V(a) → t24_srej_received_no_yes_no_no.
        { var h = InTimerRecovery(2, srej: true); h.InjectFrameBytes(h.A, Srej(h.A, nr: 1, isCmd: true, pf: false, ext: false)); Collect(h); }
        // SREJ command, N(R) out of window, mod-8 → t24_srej_received_no_no_no.
        { var h = InTimerRecovery(2, srej: true); h.InjectFrameBytes(h.A, Srej(h.A, nr: 5, isCmd: true, pf: true, ext: false)); Collect(h); }
        // SREJ command, N(R) out of window, mod-128 → t24_srej_received_no_no_yes.
        { var h = InTimerRecovery(2, srej: true, extended: true, k: 8); h.InjectFrameBytes(h.A, Srej(h.A, nr: 5, isCmd: true, pf: true, ext: true)); Collect(h); }
        // SREJ response, N(R) out of window, mod-8 → t24_srej_received_yes_no_no.
        { var h = InTimerRecovery(2, srej: true); h.InjectFrameBytes(h.A, Srej(h.A, nr: 5, isCmd: false, pf: true, ext: false)); Collect(h); }
        // SREJ response, N(R) out of window, mod-128 → t24_srej_received_yes_no_yes.
        { var h = InTimerRecovery(2, srej: true, extended: true, k: 8); h.InjectFrameBytes(h.A, Srej(h.A, nr: 5, isCmd: false, pf: true, ext: true)); Collect(h); }
        // SREJ response, N(R) in window, F=1, V(s)≠N(R) → t24_srej_received_yes_yes_yes_no.
        { var h = InTimerRecovery(3, srej: true); h.InjectFrameBytes(h.A, Srej(h.A, nr: 1, isCmd: false, pf: true, ext: false)); Collect(h); }
        // SREJ command / response, N(R) in window, P/F=0, V(s)==V(a): need IDLE
        // TimerRecovery (empty window) so vs_eq_va holds → t24_srej_received_no_yes_no_yes
        // (command) and t24_srej_received_yes_yes_no_yes (response).
        { var h = IdleInTimerRecovery(srej: true); if (h.A.State == "TimerRecovery") h.InjectFrameBytes(h.A, Srej(h.A, nr: 0, isCmd: true, pf: false, ext: false)); Collect(h); }
        { var h = IdleInTimerRecovery(srej: true); if (h.A.State == "TimerRecovery") h.InjectFrameBytes(h.A, Srej(h.A, nr: 0, isCmd: false, pf: false, ext: false)); Collect(h); }

        // 39. TimerRecovery I-received column (figc4.5 t22) — the branches the
        // mod-128 block doesn't reach (all driven mod-8 here; ids are mode-independent).
        // I arriving as a RESPONSE (command=No) → t22_i_received_no.
        { var h = InTimerRecovery(1); h.InjectFrameBytes(h.A, IResponse(h.A, nr: 0, ns: 0, payload: 0x22, pf: false, ext: false)); Collect(h); }
        // I command with info field too long (info_field_length invalid) → t22_i_received_yes_no.
        { var h = InTimerRecovery(1); var big = new byte[h.A.Context.N1 + 8]; h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 0, info: big, pollBit: false).ToBytes()); Collect(h); }
        // I command valid, N(R) out of window (V(a)≤N(R)≤V(s) = No) → t22_i_received_yes_yes_no.
        { var h = InTimerRecovery(1); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 5, ns: 0, info: new byte[] { 0x33 }, pollBit: false).ToBytes()); Collect(h); }
        // In-sequence I, own receiver busy, P=1 / P=0 → t22_i_received_yes_yes_yes_yes_yes / _yes_no.
        { var h = InTimerRecovery(1); h.A.Session.PostEvent(new DlFlowOffRequest()); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 0, info: new byte[] { 0x44 }, pollBit: true).ToBytes()); Collect(h); }
        { var h = InTimerRecovery(1); h.A.Session.PostEvent(new DlFlowOffRequest()); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 0, info: new byte[] { 0x45 }, pollBit: false).ToBytes()); Collect(h); }
        // In-sequence I, NOT own-busy, P=0, ACK-pending=No: clear the pending ack
        // first by sending our own data (piggybacks the ack via the LM-SEIZE flush),
        // then deliver a fresh in-sequence I → t22_i_received_yes_yes_yes_no_yes_no_no.
        {
            var h = InTimerRecovery(2);
            h.A.Session.PostEvent(new DlDataRequest(new byte[] { 0x55 }));    // piggyback flushes any pending ack
            if (h.A.State == "TimerRecovery" && !h.A.Context.AcknowledgePending)
                h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 0, info: new byte[] { 0x56 }, pollBit: false).ToBytes());
            Collect(h);
        }
        // Out-of-sequence I with a reject_exception already set, P=1 / P=0 (go-back-N,
        // SREJ off) → t22_i_received_yes_yes_yes_no_no_yes_yes / _no_no_yes_no.
        { var h = InTimerRecovery(1); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 2, info: new byte[] { 0x47 }, pollBit: false).ToBytes()); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 3, info: new byte[] { 0x48 }, pollBit: true).ToBytes()); Collect(h); }
        { var h = InTimerRecovery(1); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 2, info: new byte[] { 0x49 }, pollBit: false).ToBytes()); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 3, info: new byte[] { 0x4A }, pollBit: false).ToBytes()); Collect(h); }
        // Out-of-sequence I, SREJ enabled, srej_exception=0, gap of exactly 1
        // (N(S) ≤ V(R)+1) → t22_i_received_yes_yes_yes_no_no_no_yes_no_no.
        { var h = InTimerRecovery(1, srej: true); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 1, info: new byte[] { 0x4B }, pollBit: false).ToBytes()); Collect(h); }
        // Out-of-sequence I, SREJ enabled, with srej_exception already > 0 → the
        // t22_i_received_yes_yes_yes_no_no_no_yes_yes (SrejectExceptionGt0) branch.
        { var h = InTimerRecovery(1, srej: true); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 1, info: new byte[] { 0x60 }, pollBit: false).ToBytes()); h.InjectFrameBytes(h.A, Ax25Frame.I(h.A.Context.Local, h.A.Context.Remote, nr: 0, ns: 3, info: new byte[] { 0x61 }, pollBit: false).ToBytes()); Collect(h); }

        // 40. TimerRecovery remaining primitive / error / collision / data columns.
        // 40a. DL-DISCONNECT request while recovering → AwaitingRelease (t01).
        { var h = InTimerRecovery(1); h.A.Session.PostEvent(new DlDisconnectRequest()); Collect(h); }
        // 40b. DL-DATA request while recovering, window open → I pops (t02 + the
        // already-covered t03_no_no_yes pop). A fresh rig keeps V(s) known.
        { var h = InTimerRecovery(1); h.A.Session.PostEvent(new DlDataRequest(new byte[] { 0x71 })); Collect(h); }
        // 40c. DL-FLOW-OFF while already own-busy → t05_dl_flow_off_request_no
        // (Spec43-inverted guard: a second flow-off when busy takes the No arm).
        { var h = InTimerRecovery(1); h.A.Session.PostEvent(new DlFlowOffRequest()); h.A.Session.PostEvent(new DlFlowOffRequest()); Collect(h); }
        // 40d. DL-FLOW-ON while NOT own-busy → t06_dl_flow_on_request_no.
        { var h = InTimerRecovery(1); h.A.Session.PostEvent(new DlFlowOnRequest()); Collect(h); }
        // 40e. The three figc-input error columns, each from a fresh rig (some leave
        // TimerRecovery): control_field_error (t08), info_not_permitted (t09),
        // U/S-length (t10).
        { var h = InTimerRecovery(1); h.Inject(h.A, new ControlFieldError()); Collect(h); }
        { var h = InTimerRecovery(1); h.Inject(h.A, new InfoNotPermittedInFrame()); Collect(h); }
        { var h = InTimerRecovery(1); h.Inject(h.A, new UOrSFrameLengthError()); Collect(h); }
        // 40f. SABM collision while recovering with V(s)≠V(a) (vs_eq_va=No branch) →
        // t13_sabm_received_no (resync to Connected).
        { var h = InTimerRecovery(2); h.InjectFrameBytes(h.A, Ax25Frame.Sabm(h.A.Context.Local, h.A.Context.Remote).ToBytes()); Collect(h); }
        // 40g. T1 → N2 exhaustion from an IDLE TimerRecovery (V(s)=V(a)), peer NOT
        // busy → t21_t1_expiry_yes_yes_no. Drop ALL of B's frames so the T3-poll
        // never completes and only T1/RC drive it to give-up.
        {
            var h = New(n2: 2); h.Connect();
            h.Link.Drop = f => f.Source.Callsign.Equals(h.B.Context.Local);
            h.Inject(h.A, new T3Expiry());          // idle poll → TimerRecovery, V(s)=V(a)
            for (int r = 0; r < 8 && h.A.State == "TimerRecovery"; r++) h.AdvanceT1();   // RC→N2 → DL-ERROR + disconnect
            Collect(h);
        }

        // ──────────────────────────────────────────────────────────────────
        // 41. GENUINELY-UNREACHABLE residue (documented, not forced). These 13
        // transitions exist in the figc4.x figures but cannot fire end-to-end
        // through this runtime — driving them would require an artificial poke that
        // contradicts the runtime's own invariants, so they are deliberately left
        // as documented misses rather than papered over:
        //
        //  • The IFramePopsOffQueue synthesiser (Ax25Session.DrainIFrameQueue) only
        //    emits a pop in Connected / TimerRecovery, and only when the peer is
        //    NOT busy and the send window is NOT full (CanTransmitIFrame). So:
        //      AwaitingConnection/t10_i_frame_pops_off_queue_yes, _no       — no pop outside Conn/TR
        //      AwaitingV22Connection/t05_i_frame_pops_off_queue_yes, _no    — ditto
        //      Connected/t03_i_frame_pops_off_queue_yes                     — peer-busy at pop never synthesised
        //      Connected/t03_i_frame_pops_off_queue_no_yes                  — window-full at pop never synthesised
        //      TimerRecovery/t03_i_frame_pops_off_queue_yes                 — peer-busy at pop never synthesised
        //      TimerRecovery/t03_i_frame_pops_off_queue_no_yes              — window-full at pop never synthesised
        //      TimerRecovery/t03_i_frame_pops_off_queue_no_no_no           — T1 is always running in TR, so the
        //                                                                     T1-not-running pop arm can't fire
        //  • Layer3Initiated is set true by the only transition that ENTERS each of
        //    these initiator-only establishment states (figc4.1/4.2's DL-CONNECT
        //    handler runs Set_Layer_3_Initiated; the responder goes straight
        //    Disconnected→Connected on SABM/SABME and never parks here), so the
        //    layer_3_initiated=No data arm is unreachable:
        //      AwaitingConnection/t09_dl_data_request_no
        //      AwaitingV22Connection/t04_dl_data_request_no
        //  • TimerRecovery/t06_dl_flow_on_request_yes_no needs own_receiver_busy ∧
        //    ¬T1_running — but T1 is, by definition, always running while recovering,
        //    so the T1-not-running arm of the flow-on column can't fire there.
        // ──────────────────────────────────────────────────────────────────

        return fired;
    }

    private static bool Converged(TwoStationHarness h) =>
        h.A.Context.VS == h.A.Context.VA && h.B.Context.VS == h.B.Context.VA &&
        h.B.Delivered.Count == h.A.Submitted.Count && h.A.Delivered.Count == h.B.Submitted.Count;

    // ─── Frame builders (addressed to the target, i.e. "from its peer") ──

    private static byte[] FrmrTo(TwoStationHarness.Endpoint t) =>
        Ax25Frame.Frmr(t.Context.Local, t.Context.Remote, info: ReadOnlySpan<byte>.Empty).ToBytes();

    private static byte[] DmTo(TwoStationHarness.Endpoint t) =>
        Ax25Frame.Dm(t.Context.Local, t.Context.Remote).ToBytes();

    private static byte[] RejTo(TwoStationHarness.Endpoint t, byte nr) =>
        Ax25Frame.Rej(t.Context.Local, t.Context.Remote, nr, isCommand: false, pollFinal: true).ToBytes();

    // ─── Extended (mod-128) supervisory / I frame builders (addressed to the
    // target as if its peer sent them, 2-octet control). Used by the
    // TimerRecovery injection block to hit specific figc4.5 receive branches. ──

    private static byte[] RrExt(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf) =>
        Ax25Frame.Rr(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: true).ToBytes();

    private static byte[] RnrExt(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf) =>
        Ax25Frame.Rnr(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: true).ToBytes();

    private static byte[] RejExt(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf) =>
        Ax25Frame.Rej(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: true).ToBytes();

    private static byte[] SrejExt(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf) =>
        Ax25Frame.Srej(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: true).ToBytes();

    private static byte[] IExt(TwoStationHarness.Endpoint t, byte nr, byte ns, byte payload, bool pf) =>
        Ax25Frame.I(t.Context.Local, t.Context.Remote, nr, ns, info: new[] { payload }, pollBit: pf, extended: true).ToBytes();

    // ─── Mode-parametric supervisory / I frame builders (addressed to the target
    // as if its peer sent them). The TimerRecovery / Connected receive columns
    // split on command-vs-response and P/F as well as N(R)-window, so the ledger
    // needs to build each variant explicitly — a well-behaved peer never sends a
    // bare RR-response-F=0, an RR-command-P=1 with N(R) out of window, etc. on
    // cue, but the figc4.4/figc4.5 figures all have a column for it. ──

    private static byte[] Rr(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf, bool ext) =>
        Ax25Frame.Rr(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: ext).ToBytes();

    private static byte[] Rnr(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf, bool ext) =>
        Ax25Frame.Rnr(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: ext).ToBytes();

    private static byte[] Rej(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf, bool ext) =>
        Ax25Frame.Rej(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: ext).ToBytes();

    private static byte[] Srej(TwoStationHarness.Endpoint t, byte nr, bool isCmd, bool pf, bool ext) =>
        Ax25Frame.Srej(t.Context.Local, t.Context.Remote, nr, isCommand: isCmd, pollFinal: pf, extended: ext).ToBytes();

    // An I-frame addressed to the target but encoded as a RESPONSE (C/R bits
    // flipped). §4.3.1 makes I-frames always commands, so the factory hardcodes
    // command=Yes and a conformant peer never sends an I-response — but figc4.4
    // t26_i_received_no / figc4.5 t22_i_received_no are exactly the "I arrived as
    // a response" (command=No) error column, reachable only by crafting the bytes.
    private static byte[] IResponse(TwoStationHarness.Endpoint t, byte nr, byte ns, byte payload, bool pf, bool ext)
    {
        var bytes = Ax25Frame.I(t.Context.Local, t.Context.Remote, nr, ns, info: new[] { payload }, pollBit: pf, extended: ext).ToBytes().ToArray();
        // AX.25 §2.4.1.2 C/R encoding: command = destination C-bit 1 / source C-bit 0;
        // response = destination C-bit 0 / source C-bit 1. The SSID octets are at
        // byte 6 (destination) and byte 13 (source); the C-bit is 0x80.
        bytes[6]  &= 0x7F;   // destination C-bit → 0
        bytes[13] |= 0x80;   // source C-bit      → 1   ⇒ response
        return bytes;
    }
}
