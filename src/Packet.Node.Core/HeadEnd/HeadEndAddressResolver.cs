using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.HeadEnd;

/// <summary>
/// The production <see cref="IHeadEndAddressResolver"/>: config address wins; else a bounded mDNS
/// browse resolves the instance id to a single live address. Two or more discovered advertisers
/// sharing one instance id (with no config address to disambiguate) is a loud conflict — logged and
/// resolved to <c>null</c>, never guessed.
/// </summary>
public sealed partial class HeadEndAddressResolver : IHeadEndAddressResolver
{
    /// <summary>Default mDNS browse budget when the config address is blank. Short — bring-up only
    /// pays this for head-ends configured in discover mode (a blank address), never for pinned ones.</summary>
    public static readonly TimeSpan DefaultDiscoveryTimeout = TimeSpan.FromSeconds(2);

    private readonly IReadOnlyList<HeadEndConfig> headEnds;
    private readonly IHeadEndDiscovery discovery;
    private readonly TimeSpan discoveryTimeout;
    private readonly ILogger<HeadEndAddressResolver> logger;

    /// <summary>Build a resolver over the configured fleet + a discovery backend.</summary>
    public HeadEndAddressResolver(
        IReadOnlyList<HeadEndConfig> headEnds,
        IHeadEndDiscovery discovery,
        ILoggerFactory? loggerFactory = null,
        TimeSpan? discoveryTimeout = null)
    {
        this.headEnds = headEnds ?? [];
        this.discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        this.discoveryTimeout = discoveryTimeout is { } t && t > TimeSpan.Zero ? t : DefaultDiscoveryTimeout;
        logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<HeadEndAddressResolver>();
    }

    /// <inheritdoc/>
    public async Task<Uri?> ResolveBaseAddressAsync(string headEndId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headEndId);

        // Config address wins — an operator who typed a host:port has resolved the address by hand
        // (the routed / VLAN / Tailscale fallback), so no browse, and no discovery duplicate can
        // override it.
        var configured = headEnds.FirstOrDefault(h => string.Equals(h.Id, headEndId, StringComparison.Ordinal));
        if (configured is not null && !string.IsNullOrWhiteSpace(configured.Address)
            && HeadEndAddress.TryParse(configured.Address, out var host, out var port))
        {
            return new Uri($"http://{host}:{port}/", UriKind.Absolute);
        }

        // No pinned address — fall back to an mDNS browse and match the instance id.
        var discovered = await discovery.DiscoverAsync(discoveryTimeout, cancellationToken).ConfigureAwait(false);
        var matches = discovered
            .Where(d => string.Equals(d.InstanceId, headEndId, StringComparison.Ordinal))
            .ToList();

        var distinct = matches.Select(m => m.Address).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0)
        {
            return null; // not discovered (and no config address) — caller surfaces the open failure
        }
        if (distinct.Count > 1)
        {
            // Two advertisers claim the same stable id and no config address disambiguates: refuse to
            // guess. The scan surface reports this as a conflict entry; here we simply don't bind.
            LogDuplicateInstanceId(headEndId, string.Join(", ", distinct));
            return null;
        }

        var only = matches[0];
        return new Uri($"http://{only.Host}:{only.HttpPort}/", UriKind.Absolute);
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "mDNS head-end conflict: instance id '{InstanceId}' is advertised at more than one address ({Addresses}); refusing to bind. Pin the address in config to resolve it.")]
    private partial void LogDuplicateInstanceId(string instanceId, string addresses);
}
