using Packet.Tests.Shared;

namespace Packet.Node.Tests.Support;

/// <summary>
/// Real-time polling helper for the node integration tests.
/// </summary>
/// <remarks>
/// The AX.25 listener pump and the console run on their own background tasks against
/// <c>TimeProvider.System</c>, so they are inherently real-time and a <c>FakeTimeProvider</c>
/// cannot make their continuations deterministic (it controls timer expiry, not thread-pool
/// scheduling). These tests therefore poll a condition with a bounded budget; the deterministic
/// <c>FakeTimeProvider</c> path is used by the config / reconcile-delta unit tests, whose
/// components take an injectable clock. The implementation, and the reasoning behind the 30 s
/// budget and the <c>PeriodicTimer</c> poll (the #47 flake fix), live in the shared
/// <see cref="TestWait"/>; this stays as the name the node tests already call.
/// </remarks>
public static class Wait
{
    /// <summary>The default wait budget. Generous (covers worst-case scheduling
    /// latency on a saturated CI runner) but bounded (a genuine hang still fails
    /// the test instead of hanging the job).</summary>
    public static readonly TimeSpan DefaultBudget = TestWait.DefaultBudget;

    public static Task ForAsync(Func<bool> condition, string because) =>
        TestWait.ForAsync(condition, because);

    public static Task ForAsync(Func<bool> condition, string because, TimeSpan budget) =>
        TestWait.ForAsync(condition, because, budget);
}
