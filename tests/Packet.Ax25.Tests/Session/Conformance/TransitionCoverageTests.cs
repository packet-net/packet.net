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
/// one: of the 243 transitions, which ones does the real runtime actually run when
/// driven through realistic traffic? The miss-list (written to test output) is the
/// map of where behavioural coverage still has gaps — some are genuinely
/// unreachable end-to-end (documented in plan §17: the never-produced error inputs,
/// mod-128 connected-mode data), the rest are future scenario work.
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
        // AwaitingV22Connection is intentionally absent: the harness can't yet
        // drive mod-128 connection establishment (0/25 — see the report + §17). ──
        (string State, string Id)[] mustHit =
        {
            ("Disconnected",       "t03_dl_connect_request"),   // A initiates a connect
            ("Disconnected",       "t13_sabm_received_yes"),     // B accepts an incoming SABM
            ("AwaitingConnection", "t04_ua_received_yes_yes"),   // connect completes
            ("Connected",          "t02_dl_data_request"),       // upper layer sends data
            ("Connected",          "t21_rr_received_yes"),        // an RR acks
            ("AwaitingRelease",    "t03_ua_received_yes"),        // disconnect completes
            ("TimerRecovery",      "t15_frmr_received"),          // FRMR during recovery
        };
        foreach (var (state, id) in mustHit)
        {
            fired.Should().Contain((state, id),
                $"the battery should behaviourally exercise {state}/{id}");
        }

        // ── Assert: a floor on total behavioural coverage (regression guard) ──
        hit.Should().BeGreaterThanOrEqualTo(45,
            "the scenario battery should behaviourally exercise a substantial share of the 243 transitions; " +
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
        TwoStationHarness New(bool srej = false, int k = 4, bool extended = false, int n2 = 12)
        {
            var h = TwoStationHarness.Build(srej: srej, k: k, extended: extended, n2: n2);
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

        // 10. mod-128 (extended) connect.
        { var h = New(extended: true); h.Connect(); h.Disconnect(h.A); Collect(h); }

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
}
