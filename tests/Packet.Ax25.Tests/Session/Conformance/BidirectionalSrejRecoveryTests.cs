using FsCheck;
using FsCheck.Xunit;
using Xunit;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// SIMULTANEOUS bidirectional SREJ recovery — the residual defects surfaced when
/// porting the #285 mod-8 ring-wrap fix to <c>packet-net/ax25-ts</c> (#35). #285 fixed
/// the UNIDIRECTIONAL ring-wrap duplicate (the sender replaying acked I-frames);
/// these are distinct cases that only appear when BOTH stations are concurrently a
/// sender-in-recovery AND a receiver under SREJ.
/// </summary>
/// <remarks>
/// <para>
/// <b>R1 — figc4.5 Timer-Recovery stored-frame drain decremented V(R)
/// (packethacking/ax25spec#47).</b> figc4.5's in-sequence <c>I_received</c> stored-frame
/// drain loop body drew <c>V(r) := V(r) - 1</c> where the structurally-identical
/// figc4.4 (Connected) handler uses <c>V(r) := V(r) + 1</c>. The drain delivers
/// each consecutively-stored (SREJ-gap-filled) frame and must <i>advance</i> V(R)
/// past it; the decrement made V(R) net-stationary the moment one stored frame was
/// drained, so a station recovering in Timer Recovery delivered the gap-filled
/// frames but left V(R) pointing at an already-delivered sequence number — and the
/// peer's next genuine (still-unacknowledged) window retransmit was then taken for
/// new data and <b>re-delivered</b>. Reproduced identically on this C# reference
/// and on ax25-ts at the merged #285 commit. Fixed by
/// <see cref="Ax25SessionQuirks.Ax25Spec47TimerRecoveryDrainAdvancesVR"/> (default
/// on, off under <see cref="Ax25SessionQuirks.StrictlyFaithful"/>). The same fix is
/// mirrored to ax25-ts.
/// </para>
/// <para>
/// <b>R2 — simultaneous-bidirectional ring-wrap recovers via selective replay.</b>
/// On a simultaneous bidirectional wrapping burst (each station bursts k+2 frames
/// so both rings wrap) under a small drop budget, this C# reference recovers the
/// whole exchange through SREJ selective retransmission while the link is still in
/// <c>Connected</c> — it never falls through to the Timer-Recovery go-back-N
/// (<c>Invoke_Retransmission</c>) path, and never needs a T1 timeout. (ax25-ts on
/// the same deterministic seeds diverged into go-back-N, which cannot disambiguate
/// at a wrapped receive window — that divergence is being aligned to this C#
/// reference behaviour, which the seeds below pin.)
/// </para>
/// </remarks>
public class BidirectionalSrejRecoveryTests
{
    /// <summary>Deterministic LCG → [0,1), byte-identical to the ax25-ts
    /// conformance suite's, so a seeded drop pattern replays the exact same way on
    /// both runtimes (the cross-runtime reproducer contract).</summary>
    private static Func<double> Lcg(uint seed)
    {
        uint state = seed;
        return () =>
        {
            state = unchecked(state * 1664525u + 1013904223u);
            return state / 4294967296.0;   // 0x1_0000_0000
        };
    }

    /// <summary>A finite drop filter from an LCG seed + budget: drops frames in
    /// either direction until the budget is spent, then the channel is clean.
    /// Byte-identical to ax25-ts <c>finiteDrops</c>.</summary>
    private static Func<Ax25Frame, bool> FiniteDrops(uint seed, int budget)
    {
        var next = Lcg(seed);
        int left = budget;
        return _ =>
        {
            if (left > 0 && next() < 0.5) { left--; return true; }
            return false;
        };
    }

    private static bool Converged(TwoStationHarness h) =>
        h.A.Context.VS == h.A.Context.VA &&
        h.B.Context.VS == h.B.Context.VA &&
        h.B.Delivered.Count == h.A.Submitted.Count &&
        h.A.Delivered.Count == h.B.Submitted.Count;

    /// <summary>Submit <paramref name="payloadsA"/> from A and
    /// <paramref name="payloadsB"/> from B interleaved, before any settle, so both
    /// transfers (and their recoveries) are in flight together on the shared clock —
    /// the simultaneous-bidirectional regime neither <see cref="TwoStationHarness.Submit"/>
    /// (pumps after each frame) nor <see cref="TwoStationHarness.SubmitBurst"/>
    /// (one direction) reaches.</summary>
    private static void SubmitSimultaneous(TwoStationHarness h, byte[] payloadsA, byte[] payloadsB)
    {
        int n = Math.Max(payloadsA.Length, payloadsB.Length);
        for (int i = 0; i < n; i++)
        {
            if (i < payloadsA.Length)
            {
                h.A.Submitted.Add(new[] { payloadsA[i] });
                h.A.Session.PostEvent(new DlDataRequest(new[] { payloadsA[i] }));
            }
            if (i < payloadsB.Length)
            {
                h.B.Submitted.Add(new[] { payloadsB[i] });
                h.B.Session.PostEvent(new DlDataRequest(new[] { payloadsB[i] }));
            }
        }
    }

    // ─── R1: figc4.5 drain decrement (ax25spec#47) ───────────────────────────

    /// <summary>The headline R1 minimal repro, the exact ax25-ts case-1 shape:
    /// mod-8 SREJ, k=4, A submits one payload while B submits two, finite LCG(1)
    /// drop budget of 3. Before the fix A delivered B's two-frame stream twice
    /// (<c>[0x80,0x81,0x80,0x81]</c>) and the link never reconverged; with the
    /// figc4.5-drain quirk on (default) each stream is delivered exactly once and the
    /// link converges.</summary>
    [Fact]
    public void Bidirectional_mod8_srej_lowN_delivers_each_stream_once()
    {
        var h = TwoStationHarness.Build(srej: true, k: 4, n2: 80);
        h.Connect();
        h.CheckAfterEachStep = false;   // both directions' frames queue before settling
        h.Link.Drop = FiniteDrops(1, 3);

        h.Submit(h.A, 0x01);
        h.Submit(h.B, 0x80);
        h.Submit(h.B, 0x81);
        for (int r = 0; r < 160 && !Converged(h); r++) h.AdvanceT1();

        Assert.Equal(new[] { 0x80, 0x81 }, h.A.Delivered.Select(p => (int)p[0]));
        Assert.Equal(new[] { 0x01 }, h.B.Delivered.Select(p => (int)p[0]));
        h.AssertConverged();
    }

    /// <summary>Quirk-isolation tripwire: with <i>only</i>
    /// <see cref="Ax25SessionQuirks.Ax25Spec47TimerRecoveryDrainAdvancesVR"/> turned
    /// off (every other correction still on), the same scenario reproduces the
    /// figc4.5-drain defect exactly — A delivers B's stream twice and the link does
    /// not converge. This proves the figc4.5 decrement is the sole cause of R1 (not
    /// some other quirk) and that the default-on quirk is what closes it. If this
    /// stops reproducing, the figure has likely been corrected upstream and the quirk
    /// can be retired.</summary>
    [Fact]
    public void Bidirectional_mod8_srej_lowN_reproduces_defect_with_only_spec47_off()
    {
        var quirks = Ax25SessionQuirks.Default with { Ax25Spec47TimerRecoveryDrainAdvancesVR = false };
        var h = TwoStationHarness.Build(srej: true, k: 4, n2: 80, quirks: quirks);
        h.Connect();
        h.CheckAfterEachStep = false;
        h.Link.Drop = FiniteDrops(1, 3);

        h.Submit(h.A, 0x01);
        h.Submit(h.B, 0x80);
        h.Submit(h.B, 0x81);
        for (int r = 0; r < 160 && !Converged(h); r++) h.AdvanceT1();

        // The faithful-decrement defect: B's two payloads delivered to A twice, and
        // the link never reconverges (V(R) was left under-advanced).
        Assert.Equal(new[] { 0x80, 0x81, 0x80, 0x81 }, h.A.Delivered.Select(p => (int)p[0]));
        Assert.False(Converged(h));
    }

    /// <summary>Generative sweep of the simultaneous-bidirectional low-n SREJ regime
    /// (the broader R1 class), at BOTH modulos and BOTH reject schemes: A and B each
    /// submit 1..k frames at once under a finite LCG drop budget, then the channel
    /// clears. Both directions must recover to exactly-once in-order delivery and
    /// converge. This is the two-way analogue of #285's
    /// <c>A_finite_bidirectional_loss_burst_recovers</c> (which submits from A only) —
    /// the gap that hid R1.</summary>
    [Property(MaxTest = 400)]
    public bool Simultaneous_bidirectional_lowN_srej_recovers(
        int seedNa, int seedNb, int seedBudget, int seedPattern, bool extended)
    {
        int k  = extended ? 16 : 7;
        int na = 1 + Mod(seedNa, k);
        int nb = 1 + Mod(seedNb, k);
        int budget = Mod(seedBudget, na + nb + 1);   // finite, so the channel always clears

        var h = TwoStationHarness.Build(srej: true, k: k, n2: 80, extended: extended);
        h.Connect();
        h.CheckAfterEachStep = false;
        h.Link.Drop = FiniteDrops((uint)seedPattern, budget);

        var a = Enumerable.Range(0, na).Select(i => (byte)(0x10 + i)).ToArray();
        var b = Enumerable.Range(0, nb).Select(i => (byte)(0x80 + i)).ToArray();
        SubmitSimultaneous(h, a, b);
        h.Settle();
        for (int r = 0; r < 200 && !Converged(h); r++) h.AdvanceT1();

        h.AssertConverged();   // exactly-once in-order delivery (both ways) + empty windows
        return true;
    }

    // ─── R2: simultaneous-bidirectional ring-wrap (C# reference behaviour) ───

    /// <summary>R2, the exact ax25-ts case-2 deterministic seeds. Both stations
    /// burst k+2 frames simultaneously (each ring wraps) under a finite LCG budget;
    /// on these specific patterns ax25-ts diverged into go-back-N and failed to
    /// converge, while this C# reference recovers cleanly. Pins the C# behaviour the
    /// ax25-ts mirror is being aligned to: every frame delivered exactly once, both
    /// windows empty.</summary>
    [Theory]
    [InlineData(false, 1828421821u)]   // mod-8  (3-bit ring), ax25-ts pinned seed
    [InlineData(true, 4203678057u)]    // mod-128 (7-bit ring), ax25-ts pinned seed
    public void Bidirectional_srej_ringwrap_converges_on_pinned_seeds(bool extended, uint patternSeed)
    {
        int k = extended ? 16 : 7;
        int n = k + 2;   // > k ⇒ both rings wrap

        var h = TwoStationHarness.Build(srej: true, k: k, n2: 80, extended: extended);
        h.Connect();
        h.CheckAfterEachStep = false;
        h.Link.Drop = FiniteDrops(patternSeed, 4);

        var a = Enumerable.Range(0, n).Select(i => (byte)((0x10 + i) & 0xFF)).ToArray();
        var b = Enumerable.Range(0, n).Select(i => (byte)((0x80 + i) & 0xFF)).ToArray();
        SubmitSimultaneous(h, a, b);
        h.Settle();
        for (int r = 0; r < 200 && !Converged(h); r++) h.AdvanceT1();

        h.AssertConverged();
        Assert.Equal(a.Select(x => (int)x), h.B.Delivered.Select(p => (int)p[0]));
        Assert.Equal(b.Select(x => (int)x), h.A.Delivered.Select(p => (int)p[0]));
    }

    /// <summary>Non-negative modulo, overflow-safe for int.MinValue.</summary>
    private static int Mod(int v, int m) => (int)(((long)v % m + m) % m);
}
