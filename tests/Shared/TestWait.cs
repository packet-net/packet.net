namespace Packet.Tests.Shared;

/// <summary>
/// The repo's single "wait until this becomes true" helper for tests, compiled into every test
/// project by <c>tests/Directory.Build.props</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reach for it only where the thing under test is genuinely real-time: a background pump, a
/// socket, a process. Anything driven by a <c>TimeProvider</c> is tested by advancing a
/// <c>FakeTimeProvider</c> instead, and anything with a completion signal is awaited directly
/// (see docs/plan.md 2.7). This is for the cases where neither applies.
/// </para>
/// <para>
/// <b>Why a PeriodicTimer and a generous budget (the #47 flake fix, now shared).</b> CI runs the
/// test matrix as several <c>dotnet test</c> processes on one self-hosted box. When the CPU-heavy
/// siblings saturate every core, a test's background pump gets scheduling-starved: the work
/// always completes correctly once a core frees up, it was only ever late. Two properties make
/// the wait deterministic enough without weakening any assertion. The budget is a
/// generous-but-bounded 30 s, comfortably over worst-case scheduling latency yet still failing a
/// genuine hang. And the poll is driven by a <see cref="PeriodicTimer"/>, fired from the timer
/// queue rather than chained thread-pool continuations, so the poller itself is not starved by
/// the contention it is waiting out. A passing run is unaffected: the condition is observed as
/// soon as it holds, so fast stays fast.
/// </para>
/// <para>
/// Copies of this loop had been written independently in at least four test projects (#700 C115),
/// several of them with the <c>Stopwatch</c> + <c>Task.Delay</c> shape this one exists to avoid.
/// </para>
/// </remarks>
internal static class TestWait
{
    /// <summary>The default wait budget: over worst-case CI scheduling latency, under a hang.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    /// <summary>Wait until <paramref name="condition"/> holds, or fail with <paramref name="because"/>.</summary>
    public static Task ForAsync(Func<bool> condition, string because = "the awaited condition") =>
        ForAsync(condition, because, DefaultBudget);

    /// <summary>Wait until <paramref name="condition"/> holds within <paramref name="budget"/>.</summary>
    /// <exception cref="TimeoutException">The condition did not hold in time.</exception>
    public static async Task ForAsync(Func<bool> condition, string because, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(condition);

        // Fast path: already satisfied.
        if (condition())
        {
            return;
        }

        var deadline = DateTimeOffset.UtcNow + budget;
        // 15 ms keeps a passing run snappy while leaving the CPU to the work being waited on.
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(15));
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            if (condition())
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"condition not met within {budget.TotalSeconds:0.#}s: {because}");
            }
        }
    }
}
