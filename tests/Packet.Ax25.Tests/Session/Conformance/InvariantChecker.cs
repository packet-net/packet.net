namespace Packet.Ax25.Tests.Session.Conformance;

/// <summary>
/// The conformance oracle. Encodes the invariants every correct AX.25 exchange
/// must satisfy, independent of the scenario that drove the harness there. Run
/// after every step (safety) and at the end of a converging run (liveness).
/// A violation throws <see cref="InvariantViolationException"/> with a precise message -
/// in happy-path tests that fails the test; under generative testing it's a
/// shrinkable counterexample.
/// </summary>
/// <remarks>
/// Build and trust this on known-answer (happy-path) scenarios first - a
/// fuzzer is only as good as its oracle (see <c>docs/conformance-harness-plan.md</c>).
/// </remarks>
public static class InvariantChecker
{
    private static readonly HashSet<string> KnownStates = new(StringComparer.Ordinal)
    {
        "Disconnected", "AwaitingConnection", "AwaitingV22Connection",
        "Connected", "AwaitingRelease", "TimerRecovery",
    };

    // ─── Safety (must hold after every step) ────────────────────────────

    public static void CheckSafety(TwoStationHarness h)
    {
        CheckDefinedState(h.A);
        CheckDefinedState(h.B);
        CheckSequenceSanity(h.A);
        CheckSequenceSanity(h.B);
        // A delivers what B submitted; B delivers what A submitted.
        CheckReliableDelivery(receiver: h.A, sender: h.B);
        CheckReliableDelivery(receiver: h.B, sender: h.A);
    }

    private static void CheckDefinedState(TwoStationHarness.Endpoint e)
    {
        if (!KnownStates.Contains(e.State))
        {
            throw new InvariantViolationException($"[{e.Name}] is in undefined state '{e.State}'");
        }
    }

    /// <summary>Window invariant: V(s), V(a), V(r) are valid sequence numbers and
    /// the count of outstanding (unacked) I-frames never exceeds the window k.</summary>
    private static void CheckSequenceSanity(TwoStationHarness.Endpoint e)
    {
        int n = e.Context.Modulus;
        int vs = e.Context.VS, va = e.Context.VA, vr = e.Context.VR, k = e.Context.K;

        if (vs < 0 || vs >= n)
        {
            throw new InvariantViolationException($"[{e.Name}] V(s)={vs} out of range [0,{n})");
        }

        if (va < 0 || va >= n)
        {
            throw new InvariantViolationException($"[{e.Name}] V(a)={va} out of range [0,{n})");
        }

        if (vr < 0 || vr >= n)
        {
            throw new InvariantViolationException($"[{e.Name}] V(r)={vr} out of range [0,{n})");
        }

        int outstanding = ((vs - va) % n + n) % n;
        if (outstanding > k)
        {
            throw new InvariantViolationException(
                $"[{e.Name}] window exceeded: V(s)={vs} V(a)={va} ⇒ {outstanding} outstanding > k={k} (state={e.State})");
        }
    }

    /// <summary>Reliable, in-order, gap-free, duplicate-free delivery: the
    /// payloads <paramref name="receiver"/> surfaced upward must be an exact
    /// in-order prefix of what <paramref name="sender"/> submitted.</summary>
    private static void CheckReliableDelivery(TwoStationHarness.Endpoint receiver, TwoStationHarness.Endpoint sender)
    {
        var delivered = receiver.Delivered;
        var submitted = sender.Submitted;

        if (delivered.Count > submitted.Count)
        {
            throw new InvariantViolationException(
                $"[{receiver.Name}] delivered {delivered.Count} payloads but [{sender.Name}] only submitted {submitted.Count} " +
                "— duplicate or spurious delivery");
        }

        for (int i = 0; i < delivered.Count; i++)
        {
            if (!delivered[i].AsSpan().SequenceEqual(submitted[i]))
            {
                throw new InvariantViolationException(
                    $"[{receiver.Name}] delivery #{i} = [{Hex(delivered[i])}] does not match " +
                    $"[{sender.Name}] submission #{i} = [{Hex(submitted[i])}] — reorder/corruption/gap");
            }
        }
    }

    // ─── Liveness (must hold once a finite disruption has ceased) ───────

    /// <summary>Both windows empty (everything sent is acknowledged) and every
    /// submitted payload delivered, in order, in both directions.</summary>
    public static void AssertConverged(TwoStationHarness h)
    {
        CheckSafety(h);
        AssertWindowEmpty(h.A);
        AssertWindowEmpty(h.B);
        AssertFullyDelivered(receiver: h.A, sender: h.B);
        AssertFullyDelivered(receiver: h.B, sender: h.A);
    }

    private static void AssertWindowEmpty(TwoStationHarness.Endpoint e)
    {
        if (e.Context.VS != e.Context.VA)
        {
            throw new InvariantViolationException(
                $"[{e.Name}] not converged: V(s)={e.Context.VS} ≠ V(a)={e.Context.VA} (unacked frames remain, state={e.State})");
        }
    }

    private static void AssertFullyDelivered(TwoStationHarness.Endpoint receiver, TwoStationHarness.Endpoint sender)
    {
        if (receiver.Delivered.Count != sender.Submitted.Count)
        {
            throw new InvariantViolationException(
                $"[{receiver.Name}] delivered {receiver.Delivered.Count} of [{sender.Name}]'s {sender.Submitted.Count} submitted payloads — not fully delivered");
        }
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b);
}
