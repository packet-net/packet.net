using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// The cheap, cached read the <c>GET /api/v1/system/info</c> handler consumes: the running node
/// version plus the last-known <see cref="SystemUpdateAvailability"/>. The actual per-channel probe
/// (apt-cache / GitHub API / latest.json) is potentially slow + networked, so <c>/info</c> must
/// never block on it — this service serves a cached snapshot and refreshes it out of band on a TTL.
/// </summary>
public interface ISystemVersionService
{
    /// <summary>The running node version (assembly informational version, build-metadata stripped).</summary>
    string Version { get; }

    /// <summary>The last-resolved update availability. Returns the cached snapshot immediately
    /// (the safe default — <see cref="SystemUpdateAvailability.None"/> — before the first refresh
    /// completes) and, if the cache is older than the TTL, kicks off a non-blocking background
    /// refresh for the next caller. NEVER blocks on the network.</summary>
    SystemUpdateAvailability GetAvailabilitySnapshot();
}

/// <summary>
/// The default <see cref="ISystemVersionService"/>: caches the probe result behind a TTL and
/// refreshes it in the background (fire-and-forget, single-flight) so <c>/info</c> stays an
/// in-memory read. The probe itself is total (never throws), so a failed refresh simply leaves the
/// last snapshot (or the safe default) in place.
/// </summary>
public sealed partial class SystemVersionService : ISystemVersionService
{
    /// <summary>How long a cached availability result is served before a background refresh is
    /// triggered. Short enough that "update available" appears within minutes of a release; long
    /// enough that <c>/info</c> polling never hammers the GitHub API (its own client also caches).</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    private readonly IUpdateAvailabilityProbe probe;
    private readonly TimeProvider clock;
    private readonly ILogger<SystemVersionService> log;
    private readonly TimeSpan ttl;

    // The snapshot is published via a reference field (a struct can't be `volatile`); readers see a
    // fully-constructed value-or the safe default. Boxed once per refresh — refreshes are infrequent.
    private volatile object snapshotBox = SystemUpdateAvailability.None;
    private long lastRefreshTicks = long.MinValue; // DateTimeOffset.UtcTicks of the last refresh START.
    private int refreshing; // 0 = idle, 1 = a background refresh is in flight (single-flight).

    private SystemUpdateAvailability Snapshot
    {
        get => (SystemUpdateAvailability)snapshotBox;
        set => snapshotBox = value;
    }

    public SystemVersionService(
        string version,
        IUpdateAvailabilityProbe probe,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        Version = version;
        this.probe = probe;
        this.clock = clock;
        log = loggerFactory.CreateLogger<SystemVersionService>();
        this.ttl = ttl ?? DefaultTtl;
    }

    /// <inheritdoc/>
    public string Version { get; }

    /// <inheritdoc/>
    public SystemUpdateAvailability GetAvailabilitySnapshot()
    {
        var now = clock.GetUtcNow();
        var last = Interlocked.Read(ref lastRefreshTicks);
        bool stale = last == long.MinValue || now.UtcTicks - last >= ttl.Ticks;
        if (stale)
        {
            TriggerBackgroundRefresh(now);
        }
        return Snapshot;
    }

    /// <summary>Run a refresh and await it — for tests + an optional eager warm-up at boot. Updates
    /// the snapshot in place. Single-flight with the background refresh.</summary>
    public async Task<SystemUpdateAvailability> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
        {
            return Snapshot; // another refresh is already running.
        }
        try
        {
            Interlocked.Exchange(ref lastRefreshTicks, clock.GetUtcNow().UtcTicks);
            var result = await probe.CheckAsync(Version, cancellationToken).ConfigureAwait(false);
            Snapshot = result;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The probe is documented total, but guard the service too: keep the prior snapshot.
            LogRefreshFault(ex);
            return Snapshot;
        }
        finally
        {
            Interlocked.Exchange(ref refreshing, 0);
        }
    }

    private void TriggerBackgroundRefresh(DateTimeOffset now)
    {
        if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
        {
            return; // single-flight: a refresh is already running.
        }
        // Stamp the refresh start NOW so concurrent callers see it as fresh and don't re-trigger.
        Interlocked.Exchange(ref lastRefreshTicks, now.UtcTicks);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await probe.CheckAsync(Version, CancellationToken.None).ConfigureAwait(false);
                Snapshot = result;
            }
            catch (Exception ex)
            {
                LogRefreshFault(ex);
            }
            finally
            {
                Interlocked.Exchange(ref refreshing, 0);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Update-availability refresh failed; keeping the prior snapshot.")]
    private partial void LogRefreshFault(Exception ex);
}
