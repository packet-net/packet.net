using FsCheck;
using FsCheck.Xunit;
using Xunit;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Phase A1 — adversarial generative testing over the conformance harness.
/// FsCheck generates loss patterns; the invariant oracle judges. A failure is
/// a shrinkable counterexample, automatically triaged engine-vs-figure by the
/// Strict-vs-Default companion (future A2). This first property fuzzes the
/// #231-class: a connected-mode transfer with a single dropped I-frame must
/// always recover (everything delivered in order, windows empty) once the
/// channel clears — for both REJ go-back-N and SREJ selective recovery.
/// </summary>
public class LossRecoveryProperties
{
    [Property(MaxTest = 300)]
    public bool A_single_dropped_iframe_always_recovers(int seedN, int seedDrop, bool srej, bool extended)
    {
        int n       = 1 + Mod(seedN, 6);   // 1..6 I-frames
        int dropPos = Mod(seedDrop, n);    // drop the frame with N(s)=dropPos, once
        int k       = Math.Max(4, n);

        // The `extended` parameter fuzzes the SAME recovery space at both modulos
        // (mod-8 and mod-128, V4a): the dropped-frame N(S) match below reads the
        // frame's mode-aware Ns, so the property holds in the 7-bit space too.
        var h = TwoStationHarness.Build(srej: srej, k: k, extended: extended);
        h.Connect();

        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped) return false;
            if (!f.Source.Callsign.Equals(h.A.Context.Local)) return false;
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (f.Ns != dropPos) return false;   // mode-aware: 3-bit mod-8 / 7-bit mod-128
            dropped = true;
            return true;
        };

        for (byte i = 0; i < n; i++) h.Submit(h.A, i);

        // Channel is clean once the single drop is consumed; drive T1 recovery
        // until the link converges (bounded — non-convergence is the bug we hunt).
        for (int r = 0; r < 40 && !Converged(h); r++) h.AdvanceT1();

        h.AssertConverged();  // throws InvariantViolationException → shrinkable counterexample
        return true;
    }

    [Property(MaxTest = 400)]
    public bool A_finite_bidirectional_loss_burst_recovers(int seedN, int seedBudget, int seedPattern, bool srej, bool extended)
    {
        int n      = 1 + Mod(seedN, 6);        // 1..6 I-frames
        int budget = Mod(seedBudget, n + 1);   // 0..n total drops — finite, so the channel always clears
        int k      = Math.Max(4, n);
        var rng    = new Random(seedPattern);

        // Both go-back-N (REJ) and selective (SREJ) recovery, fuzzed across budgets
        // and both modes. This sweep was REJ-only until the three figc4.x SREJ
        // recovery defects were closed: #242 (out-of-window duplicate discard),
        // #241 (SRT/T1V overflow), and #246 (SREJ requested the just-arrived frame
        // instead of the gap). With all three quirks on (default) selective-reject
        // converges under arbitrary finite loss too. N2 generous so the link
        // doesn't give up before the finite loss clears. `extended` fuzzes both the
        // mod-8 and mod-128 sequence spaces (V4a).
        var h = TwoStationHarness.Build(srej: srej, k: k, n2: 40, extended: extended);
        h.Connect();

        // Drop up to `budget` frames in EITHER direction (lost I-frames, acks,
        // SREJs, retransmits), then the channel is clean. The finite budget is
        // the "disruption prefix"; recovery must complete on the clean tail.
        int dropsLeft = budget;
        h.Link.Drop = _ =>
        {
            if (dropsLeft > 0 && rng.NextDouble() < 0.5) { dropsLeft--; return true; }
            return false;
        };

        for (byte i = 0; i < n; i++) h.Submit(h.A, i);
        for (int r = 0; r < 80 && !Converged(h); r++) h.AdvanceT1();

        h.AssertConverged();
        return true;
    }

    // Regression for the ax25spec#40 SREJ livelock (packet-net/packet.net#242). A
    // multi-frame bidirectional SREJ burst like this used to spin to the pump's
    // 256-round bound: B SREJ'd out-of-window duplicates (figc4.4 has no
    // receive-window guard), A re-sent, the re-send was again out-of-window,
    // repeat forever. With Ax25Spec40DiscardOutOfWindowIFrames on (default), B
    // discards out-of-window frames instead of SREJ'ing them, so this moderate
    // selective-reject burst converges. (#242 regression. A heavier burst is still
    // blocked by #246 — see the skipped Srej_heavy_bidirectional_loss_burst_recovers
    // below — so keep this budget moderate.)
    [Fact]
    public void Srej_bidirectional_loss_burst_recovers_with_window_guard()
    {
        var h = TwoStationHarness.Build(srej: true, k: 6, n2: 40);
        h.Connect();
        var rng = new Random(2);
        var dropsLeft = 2;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.5) { dropsLeft--; return true; } return false; };
        for (byte i = 0; i < 6; i++) h.Submit(h.A, i);
        for (int r = 0; r < 60 && !Converged(h); r++) h.AdvanceT1();
        h.AssertConverged();
    }

    // Convergence regression for the full SREJ recovery stack: this heavy burst
    // needed all three figc4.x fixes — the ax25spec#40 duplicate-SREJ livelock
    // (window guard / #242), the ax25spec#41 SRT overflow (Karn guard / #241), and
    // ax25spec#42 (#246): figc4.4's multi-frame SREJ requested the just-arrived
    // frame (N(r):=N(s)) instead of the gap, so the receiver SREJ'd a frame it held
    // and the real gap was never re-requested. With Ax25Spec42SrejTargetsGap on
    // (default) the SREJ targets V(R), so heavy multi-frame selective-reject
    // recovery converges.
    [Fact]
    public void Srej_heavy_bidirectional_loss_burst_recovers()
    {
        var h = TwoStationHarness.Build(srej: true, k: 6, n2: 40);
        h.Connect();
        var rng = new Random(2);
        var dropsLeft = 5;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.5) { dropsLeft--; return true; } return false; };
        for (byte i = 0; i < 6; i++) h.Submit(h.A, i);
        for (int r = 0; r < 60 && !Converged(h); r++) h.AdvanceT1();
        h.AssertConverged();
    }

    // ─── n ≥ 8 sequence-ring-wrap recovery (the mod-8 SREJ data-integrity bug) ──
    //
    // The properties above cap n at 1..6 from a single station and settle after
    // every Submit, so N(S) never wraps the 0–7 ring mid-recovery. That hid a
    // data-integrity bug: under mod-8 SREJ, a bulk transfer of ≥ 8 frames flying
    // together (so V(S) wraps 7→0) plus a loss pattern made the receiver re-deliver
    // already-delivered frames and desync permanently. Reference repro from
    // ax25-ts (mod-8 SREJ, k=7, 8 frames): a single drop yielded delivered
    // [1..8, 1, 2] — frames 1 and 2 delivered twice. Root cause: the recovery path
    // replayed I-frames from the sent-frame store even after they were
    // acknowledged; once V(R) wrapped past those numbers, the receiver took the
    // stale retransmits for new data. Fixed by gating selective replay on the live
    // send window [V(a), V(s)) and to once-per-recovery-cycle (ActionDispatcher /
    // Ax25SessionContext). These pin the fix and guard the regression.

    /// <summary>The headline minimal repro: the exact ax25-ts shape. mod-8 SREJ,
    /// k=7, station A bursts 8 frames, drop the second one (N(S)=1) once, then a
    /// clean channel. Before the fix this delivered payloads [1..8, 1, 2] (1 and 2
    /// twice) and never reconverged; it must deliver 1..8 exactly once, in order,
    /// and the link must converge.</summary>
    [Fact]
    public void Mod8_srej_wrapping_burst_with_one_drop_does_not_re_deliver()
    {
        var h = TwoStationHarness.Build(srej: true, k: 7, n2: 40);
        h.Connect();

        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped) return false;
            if (!f.Source.Callsign.Equals(h.A.Context.Local)) return false;
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (f.Ns != 1) return false;          // drop the second frame, once
            dropped = true;
            return true;
        };

        h.SubmitBurst(h.A, 1, 2, 3, 4, 5, 6, 7, 8);
        for (int r = 0; r < 60 && !Converged(h); r++) h.AdvanceT1();

        Assert.Equal(Enumerable.Range(1, 8), h.B.Delivered.Select(p => (int)p[0]));
        h.AssertConverged();
    }

    /// <summary>Every drop position across an 8-frame mod-8 SREJ burst at k=7 (the
    /// worst case: k = modulus − 1, the whole ring in flight). Each must deliver
    /// 1..8 exactly once and converge — no re-delivery, no desync, at any wrap
    /// offset.</summary>
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void Mod8_srej_wrapping_burst_recovers_at_every_drop_position(int dropNs)
    {
        var h = TwoStationHarness.Build(srej: true, k: 7, n2: 40);
        h.Connect();

        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped) return false;
            if (!f.Source.Callsign.Equals(h.A.Context.Local)) return false;
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (f.Ns != dropNs) return false;
            dropped = true;
            return true;
        };

        h.SubmitBurst(h.A, 1, 2, 3, 4, 5, 6, 7, 8);
        for (int r = 0; r < 60 && !Converged(h); r++) h.AdvanceT1();

        Assert.Equal(Enumerable.Range(1, 8), h.B.Delivered.Select(p => (int)p[0]));
        h.AssertConverged();
    }

    /// <summary>Generative sweep of the ring-wrap regime: n ≥ 8 frames bursting
    /// together (so the sequence ring wraps) with a single drop, at BOTH modulos
    /// and BOTH reject schemes. mod-8 caps k at 7 (the 3-bit window maximum); the
    /// extended space exercises a much larger n through the same wrap machinery.
    /// The oracle judges: exactly-once in-order delivery + convergence.</summary>
    [Property(MaxTest = 300)]
    public bool Wrapping_burst_with_one_drop_recovers(int seedN, int seedDrop, bool srej, bool extended)
    {
        int k = extended ? 16 : 7;             // mod-8 max window is 7
        int n = k + 1 + Mod(seedN, 3 * k);     // ≥ k+1 ⇒ the ring necessarily wraps
        int dropPos = Mod(seedDrop, n);

        var h = TwoStationHarness.Build(srej: srej, k: k, n2: 80, extended: extended);
        h.Connect();

        // Drop the dropPos-th distinct I-frame A puts on the wire (by submission
        // order, not by N(S), since N(S) repeats across the wrap), once.
        int seen = -1;
        var dropped = false;
        h.Link.Drop = f =>
        {
            if (dropped) return false;
            if (!f.Source.Callsign.Equals(h.A.Context.Local)) return false;
            if (Ax25FrameClassifier.Classify(f) is not IFrameReceived) return false;
            if (++seen != dropPos) return false;
            dropped = true;
            return true;
        };

        var payloads = Enumerable.Range(0, n).Select(i => (byte)(i & 0xFF)).ToArray();
        h.SubmitBurst(h.A, payloads);
        for (int r = 0; r < 200 && !Converged(h); r++) h.AdvanceT1();

        h.AssertConverged();   // exactly-once in-order delivery + empty windows
        return true;
    }

    /// <summary>Bidirectional / simultaneous wrapping bursts: both stations submit
    /// ≥ 8 frames at once (each ring wraps) and the channel drops a finite budget in
    /// either direction, then clears. Both directions must recover to exactly-once
    /// in-order delivery and converge — the two-way analogue of the ring-wrap
    /// regime, mod-8 SREJ included.</summary>
    [Property(MaxTest = 300)]
    public bool Bidirectional_wrapping_bursts_recover(int seedBudget, int seedPattern, bool srej, bool extended)
    {
        int k = extended ? 16 : 7;
        int n = k + 2;                         // > k ⇒ both rings wrap
        int budget = Mod(seedBudget, 5);       // 0..4 finite drops, then clean
        var rng = new Random(seedPattern);

        var h = TwoStationHarness.Build(srej: srej, k: k, n2: 80, extended: extended);
        h.Connect();
        h.CheckAfterEachStep = false;          // both bursts queue before settling

        int dropsLeft = budget;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.5) { dropsLeft--; return true; } return false; };

        // Queue BOTH stations' bursts before settling so the two transfers (and
        // their recoveries) interleave on the shared scheduler.
        for (int i = 0; i < n; i++)
        {
            h.A.Submitted.Add(new[] { (byte)(0x10 + i) });
            h.A.Session.PostEvent(new DlDataRequest(new[] { (byte)(0x10 + i) }));
            h.B.Submitted.Add(new[] { (byte)(0x80 + i) });
            h.B.Session.PostEvent(new DlDataRequest(new[] { (byte)(0x80 + i) }));
        }
        h.Settle();
        for (int r = 0; r < 200 && !Converged(h); r++) h.AdvanceT1();

        h.AssertConverged();
        return true;
    }

    private static bool Converged(TwoStationHarness h) =>
        h.A.Context.VS == h.A.Context.VA &&
        h.B.Context.VS == h.B.Context.VA &&
        h.B.Delivered.Count == h.A.Submitted.Count &&
        h.A.Delivered.Count == h.B.Submitted.Count;

    /// <summary>Non-negative modulo, overflow-safe for int.MinValue.</summary>
    private static int Mod(int v, int m) => (int)(((long)v % m + m) % m);
}
