using FsCheck.Xunit;
using Packet.Ax25.Session;

namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// Phase A - adversarial channel perturbations beyond loss. The medium no longer
/// just drops frames; it also <b>duplicates</b> them (a digipeater echo, or a
/// retransmit arriving alongside the original it was meant to replace). The
/// invariant oracle judges: a duplicate must never be delivered upward twice (the
/// safety invariant - reliable, gap-free, duplicate-free delivery) and the link
/// must still converge (liveness). This stresses the figc4.4 receive path and the
/// <c>Ax25Spec40</c> out-of-window discard guard, which is exactly what drops an
/// in-flight duplicate once V(R) has moved past it. Failures shrink to a minimal
/// counterexample and triage engine-vs-figure via the Strict-vs-Default companion.
/// </summary>
public class AdversarialChannelProperties
{
    [Property(MaxTest = 300)]
    public bool A_duplicating_channel_never_double_delivers(int seedN, int seedPattern)
    {
        int n = 1 + Mod(seedN, 6);     // 1..6 I-frames
        int k = System.Math.Max(4, n);
        var rng = new System.Random(seedPattern);

        var h = TwoStationHarness.Build(k: k);
        h.Connect();

        // After the link is up, duplicate ~half of every frame in flight (I-frames
        // and acks, both directions). No drops - duplication is the only
        // perturbation, so every submitted payload must still arrive exactly once.
        h.Link.Duplicate = _ => rng.NextDouble() < 0.5;

        for (byte i = 0; i < n; i++)
        {
            h.Submit(h.A, i);
        }

        for (int r = 0; r < 40 && !Converged(h); r++)
        {
            h.AdvanceT1();
        }

        // Oracle: in-order, gap-free, DUPLICATE-FREE delivery both ways + windows empty.
        h.AssertConverged();
        return true;
    }

    [Property(MaxTest = 400)]
    public bool A_lossy_and_duplicating_channel_recovers_cleanly(
        int seedN, int seedBudget, int seedPattern, bool srej)
    {
        int n = 1 + Mod(seedN, 6);        // 1..6 I-frames
        int budget = Mod(seedBudget, n + 1);   // 0..n finite drops - channel then clears
        int k = System.Math.Max(4, n);
        var rng = new System.Random(seedPattern);

        var h = TwoStationHarness.Build(srej: srej, k: k, n2: 40);
        h.Connect();

        // A mean channel: a finite drop budget AND ~30% duplication, both
        // directions. Recovery (for the drops) and dedup (for the duplicates) must
        // compose - every payload delivered exactly once, the link converges once
        // the finite loss clears. Both REJ and SREJ recovery are fuzzed.
        int dropsLeft = budget;
        h.Link.Drop = _ => { if (dropsLeft > 0 && rng.NextDouble() < 0.5) { dropsLeft--; return true; } return false; };
        h.Link.Duplicate = _ => rng.NextDouble() < 0.3;

        for (byte i = 0; i < n; i++)
        {
            h.Submit(h.A, i);
        }

        for (int r = 0; r < 80 && !Converged(h); r++)
        {
            h.AdvanceT1();
        }

        h.AssertConverged();
        return true;
    }

    private static bool Converged(TwoStationHarness h) =>
        h.A.Context.VS == h.A.Context.VA &&
        h.B.Context.VS == h.B.Context.VA &&
        h.B.Delivered.Count == h.A.Submitted.Count &&
        h.A.Delivered.Count == h.B.Submitted.Count;

    private static int Mod(int v, int m) => (int)(((long)v % m + m) % m);
}
