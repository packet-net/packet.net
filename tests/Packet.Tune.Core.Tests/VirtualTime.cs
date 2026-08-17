using Microsoft.Extensions.Time.Testing;

namespace Packet.Tune.Core.Tests;

/// <summary>
/// Drives work that is parked on an injected <see cref="FakeTimeProvider"/> forward on
/// virtual time, so a test can prove a timeout/guard fires without waiting for it
/// (plan.md §2.7: no real-time waits in test code).
/// </summary>
internal static class VirtualTime
{
    /// <summary>Wall-clock backstop for a hung test. A passing run never reaches it.</summary>
    private static readonly TimeSpan Backstop = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Run <paramref name="work"/> to completion on virtual time. Code under test arms its
    /// next timer only once the previous one has fired, so the clock is hopped forward in
    /// <paramref name="hop"/> steps rather than in one jump. Between hops the loop hands the
    /// scheduler back (racing the work against a 1 ms tick) so the continuations the hop
    /// released actually run and re-arm; the progress comes from the hops, not from that
    /// tick, and a spin-yield instead of it would burn a core for the whole run.
    /// </summary>
    /// <param name="clock">The clock the code under test was given.</param>
    /// <param name="work">The operation waiting on that clock.</param>
    /// <param name="hop">How far to move virtual time per step; the shortest interval the
    /// code under test waits on is the natural choice.</param>
    public static async Task AdvanceUntilAsync(FakeTimeProvider clock, Task work, TimeSpan hop)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(work);
        long deadline = Environment.TickCount64 + (long)Backstop.TotalMilliseconds;
        while (!work.IsCompleted)
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("the work parked on the fake clock never completed");
            }
            clock.Advance(hop);
            await Task.WhenAny(work, Task.Delay(TimeSpan.FromMilliseconds(1))).ConfigureAwait(false);
        }
        await work.ConfigureAwait(false);
    }

    /// <inheritdoc cref="AdvanceUntilAsync(FakeTimeProvider, Task, TimeSpan)"/>
    /// <typeparam name="T">The operation's result type.</typeparam>
    /// <returns>The completed operation's result.</returns>
    public static async Task<T> AdvanceUntilAsync<T>(FakeTimeProvider clock, Task<T> work, TimeSpan hop)
    {
        await AdvanceUntilAsync(clock, (Task)work, hop).ConfigureAwait(false);
        return await work.ConfigureAwait(false);
    }
}
