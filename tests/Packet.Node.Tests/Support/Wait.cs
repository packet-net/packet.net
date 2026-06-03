namespace Packet.Node.Tests.Support;

/// <summary>
/// Real-time polling helper for the integration tests. The AX.25 listener pump
/// and the console run on their own tasks against <c>TimeProvider.System</c>, so
/// these tests are inherently real-time (mirroring the existing
/// <c>Ax25Listener</c> tests' <c>WaitFor</c>); they poll a condition with a
/// timeout rather than sleeping a fixed duration. The deterministic
/// <c>FakeTimeProvider</c> path is used by the config / reconcile-delta tests
/// where the component under test takes an injectable clock.
/// </summary>
public static class Wait
{
    public static async Task ForAsync(Func<bool> condition, string because, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
        throw new TimeoutException($"condition not met within {timeoutMs} ms: {because}");
    }
}
