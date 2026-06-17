using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Beacons;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// DI wiring for the node core. The host shell (<c>Packet.Node</c>) calls
/// <see cref="AddPacketNode"/> on its <see cref="IServiceCollection"/> to
/// register the config provider, transport factory, and the
/// <see cref="NodeHostedService"/>.
/// </summary>
public static class NodeServiceCollectionExtensions
{
    /// <summary>
    /// Register the node host services. The <see cref="IConfigProvider"/> is a
    /// <see cref="FileConfigProvider"/> over <paramref name="configPath"/>; the
    /// transport factory and hosted service are registered as singletons.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configPath">Path to the YAML config file.</param>
    /// <param name="dbPath">Path to the SQLite store (<c>pdn.db</c>) for routing-table
    /// persistence; null skips persistence (the table is in-memory only).</param>
    public static IServiceCollection AddPacketNode(this IServiceCollection services, string configPath, string? dbPath = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configPath);

        services.TryAddSingleton<IConfigProvider>(sp => new FileConfigProvider(
            configPath,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILoggerFactory>()?.CreateLogger<FileConfigProvider>()));

        services.TryAddSingleton<ITransportFactory>(TransportFactory.Instance);
        services.TryAddSingleton(TimeProvider.System);

        // The ID-beacon service (a singleton over the live config + clock). The hosted
        // service injects it and passes it to the supervisor; inert until a port whose
        // effective beacon is enabled comes up (default-off).
        services.TryAddSingleton<BeaconService>();

        if (!string.IsNullOrWhiteSpace(dbPath))
        {
            services.TryAddSingleton<INetRomRoutingStore>(sp => new SqliteNetRomRoutingStore(
                dbPath,
                sp.GetService<ILoggerFactory>()?.CreateLogger<SqliteNetRomRoutingStore>()));

            // The per-peer AX.25 capability cache persists to the same pdn.db. Registered here
            // so a dbPath node gets a durable store; without a dbPath the cache below falls back
            // to in-memory (the store resolves to null), so tests/embedders are unaffected.
            services.TryAddSingleton<IPeerCapabilityStore>(sp => new SqlitePeerCapabilityStore(
                dbPath,
                sp.GetService<ILoggerFactory>()?.CreateLogger<SqlitePeerCapabilityStore>()));

            // The MHeard log persists to the same pdn.db (#454). Same pattern as the capability
            // store — a durable store with a dbPath, in-memory otherwise.
            services.TryAddSingleton<Heard.IHeardStore>(sp => new Heard.SqliteHeardStore(
                dbPath,
                sp.GetService<ILoggerFactory>()?.CreateLogger<Heard.SqliteHeardStore>()));
        }

        // The capability cache itself is always available (in-memory when no store is registered).
        services.TryAddSingleton(sp => new PeerCapabilityCache(
            sp.GetService<IPeerCapabilityStore>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        // The MHeard log is always available (in-memory when no store is registered).
        services.TryAddSingleton(sp => new Heard.HeardLog(
            sp.GetService<Heard.IHeardStore>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));

        services.AddHostedService<NodeHostedService>();
        return services;
    }
}
