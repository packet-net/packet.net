using Packet.Node.Core.HeadEnd;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A scripted <see cref="IHeadEndDiscovery"/> — returns a fixed set of advertisers with no real mDNS
/// socket, and records how many browses were requested. Lets the address resolver, the fleet scanner
/// and the conflict backstop be driven deterministically (a duplicate instance id is just two entries
/// with the same id at different addresses).
/// </summary>
public sealed class FakeHeadEndDiscovery : IHeadEndDiscovery
{
    private readonly IReadOnlyList<DiscoveredHeadEnd> results;

    public FakeHeadEndDiscovery(params DiscoveredHeadEnd[] results) => this.results = results;

    /// <summary>Number of times a browse was requested (asserts a config-pinned address never browses).</summary>
    public int Browses { get; private set; }

    public Task<IReadOnlyList<DiscoveredHeadEnd>> DiscoverAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Browses++;
        return Task.FromResult(results);
    }
}
