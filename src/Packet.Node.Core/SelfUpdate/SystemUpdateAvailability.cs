namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// The outcome of the per-channel "is a newer version available?" check that feeds the shared
/// <c>GET /api/v1/system/info</c> contract (<c>{ …, updateAvailable, latestVersion }</c>) the web
/// UI's update banner consumes.
/// </summary>
/// <param name="UpdateAvailable">A strictly-newer version is known to be installable on this
/// channel.</param>
/// <param name="LatestVersion">The latest known version string when <paramref name="UpdateAvailable"/>,
/// else <c>null</c>. (Only populated when there is genuinely something newer — an up-to-date node
/// reports <c>null</c>, matching the contract.)</param>
public readonly record struct SystemUpdateAvailability(bool UpdateAvailable, string? LatestVersion)
{
    /// <summary>The safe default: nothing known to be available (offline, missing tool, API error,
    /// already up to date, or a channel that can't self-update). NEVER reports a downgrade.</summary>
    public static SystemUpdateAvailability None { get; } = new(UpdateAvailable: false, LatestVersion: null);

    /// <summary>A newer version is available.</summary>
    public static SystemUpdateAvailability Available(string latestVersion) =>
        new(UpdateAvailable: true, LatestVersion: latestVersion);
}

/// <summary>
/// Resolves whether a newer node version is available, for the running install channel. The single
/// implementation dispatches per channel (apt: <c>apt-cache policy</c>; github: the GitHub Releases
/// API; selfcontained: the <c>latest.json</c> feed) and is <b>total</b>: every external / network
/// path is guarded so an offline host, a missing tool, or an API error yields
/// <see cref="SystemUpdateAvailability.None"/> — it never throws and never reports a downgrade.
/// Results are cached behind <see cref="ISystemVersionService"/> so <c>/info</c> stays cheap.
/// </summary>
public interface IUpdateAvailabilityProbe
{
    /// <summary>Check whether a version newer than <paramref name="runningVersion"/> is available.
    /// Returns <see cref="SystemUpdateAvailability.None"/> on any failure (the load-bearing
    /// guarantee — see the interface remarks).</summary>
    Task<SystemUpdateAvailability> CheckAsync(string runningVersion, CancellationToken cancellationToken = default);
}
