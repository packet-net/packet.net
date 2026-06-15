using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="SystemVersionService"/> serves <c>GET /api/v1/system/info</c> a cheap cached snapshot
/// and refreshes it out of band on a TTL — so <c>/info</c> never blocks on the network. Asserts the
/// snapshot is published after a refresh, that the cache is served within the TTL, and that the
/// service is total (a throwing probe leaves the prior snapshot, never propagates).
/// </summary>
[Trait("Category", "Node")]
public sealed class SystemVersionServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Version_is_exposed_verbatim()
    {
        var svc = new SystemVersionService("0.8.0", new StubProbe(SystemUpdateAvailability.None),
            new FakeTimeProvider(T0), NullLoggerFactory.Instance);
        svc.Version.Should().Be("0.8.0");
    }

    [Fact]
    public void Snapshot_is_the_safe_default_before_the_first_refresh()
    {
        var probe = new StubProbe(SystemUpdateAvailability.Available("0.9.0"));
        var svc = new SystemVersionService("0.8.0", probe, new FakeTimeProvider(T0), NullLoggerFactory.Instance);

        // The synchronous snapshot read returns the default immediately (it only KICKS OFF a
        // background refresh — it never awaits one), so /info is never blocked.
        svc.GetAvailabilitySnapshot().Should().Be(SystemUpdateAvailability.None);
    }

    [Fact]
    public async Task RefreshAsync_publishes_the_probe_result()
    {
        var probe = new StubProbe(SystemUpdateAvailability.Available("0.9.0"));
        var svc = new SystemVersionService("0.8.0", probe, new FakeTimeProvider(T0), NullLoggerFactory.Instance);

        var result = await svc.RefreshAsync();
        result.Should().Be(SystemUpdateAvailability.Available("0.9.0"));
        svc.GetAvailabilitySnapshot().Should().Be(SystemUpdateAvailability.Available("0.9.0"));
        probe.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Snapshot_serves_the_cache_within_the_ttl()
    {
        var probe = new StubProbe(SystemUpdateAvailability.Available("0.9.0"));
        var clock = new FakeTimeProvider(T0);
        var svc = new SystemVersionService("0.8.0", probe, clock, NullLoggerFactory.Instance, ttl: TimeSpan.FromMinutes(15));

        await svc.RefreshAsync();
        probe.Calls.Should().Be(1);

        // Still within the TTL → the snapshot read must NOT trigger another probe.
        clock.Advance(TimeSpan.FromMinutes(5));
        svc.GetAvailabilitySnapshot().Should().Be(SystemUpdateAvailability.Available("0.9.0"));
        probe.Calls.Should().Be(1, "a snapshot read within the TTL serves the cache, no re-probe");
    }

    [Fact]
    public async Task A_throwing_probe_keeps_the_prior_snapshot_and_never_propagates()
    {
        var probe = new ThrowingProbe();
        var svc = new SystemVersionService("0.8.0", probe, new FakeTimeProvider(T0), NullLoggerFactory.Instance);

        // RefreshAsync must swallow the probe fault (the service is total) and return the default.
        var result = await svc.RefreshAsync();
        result.Should().Be(SystemUpdateAvailability.None);
        svc.GetAvailabilitySnapshot().Should().Be(SystemUpdateAvailability.None);
    }

    private sealed class StubProbe(SystemUpdateAvailability result) : IUpdateAvailabilityProbe
    {
        public int Calls { get; private set; }

        public Task<SystemUpdateAvailability> CheckAsync(string runningVersion, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingProbe : IUpdateAvailabilityProbe
    {
        public Task<SystemUpdateAvailability> CheckAsync(string runningVersion, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
    }
}
