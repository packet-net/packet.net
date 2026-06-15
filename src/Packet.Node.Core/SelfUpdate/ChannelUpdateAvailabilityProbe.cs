using Microsoft.Extensions.Logging;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// The single <see cref="IUpdateAvailabilityProbe"/>: dispatches the "is a newer version
/// available?" check on the resolved <see cref="InstallChannel"/> —
/// <list type="bullet">
/// <item><b>apt</b>: <c>apt-cache policy packetnet</c> — Candidate &gt; Installed (via
/// <see cref="IProcessRunner"/>, the same guarded seam channel detection uses).</item>
/// <item><b>github</b>: the latest <c>node-v*</c> Releases tag vs the running version (via
/// <see cref="IGitHubReleaseClient"/>). A <c>0.1.0+dev…</c> build sorts <em>above</em> the
/// matching release, so it reports up-to-date, never a downgrade (<see cref="NodeVersion"/>).</item>
/// <item><b>selfcontained</b>: the configured feed's <c>latest.json</c> version (via
/// <see cref="ISelfContainedFeedClient"/>).</item>
/// <item><b>unknown</b>: never offers an update.</item>
/// </list>
/// Every branch is total: a missing tool, an offline host, or a parse failure yields
/// <see cref="SystemUpdateAvailability.None"/>. It never throws and never reports a downgrade.
/// </summary>
public sealed partial class ChannelUpdateAvailabilityProbe : IUpdateAvailabilityProbe
{
    private const string PackageName = "packetnet";

    private readonly IInstallChannelProvider channel;
    private readonly IProcessRunner runner;
    private readonly IGitHubReleaseClient github;
    private readonly ISelfContainedFeedClient feed;
    private readonly ILogger<ChannelUpdateAvailabilityProbe> log;

    public ChannelUpdateAvailabilityProbe(
        IInstallChannelProvider channel,
        IProcessRunner runner,
        IGitHubReleaseClient github,
        ISelfContainedFeedClient feed,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(github);
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.channel = channel;
        this.runner = runner;
        this.github = github;
        this.feed = feed;
        log = loggerFactory.CreateLogger<ChannelUpdateAvailabilityProbe>();
    }

    /// <inheritdoc/>
    public async Task<SystemUpdateAvailability> CheckAsync(string runningVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            return channel.Channel switch
            {
                InstallChannel.Apt => CheckApt(runningVersion),
                InstallChannel.Github => await CheckGithubAsync(runningVersion, cancellationToken).ConfigureAwait(false),
                InstallChannel.SelfContained => await CheckSelfContainedAsync(runningVersion, cancellationToken).ConfigureAwait(false),
                _ => SystemUpdateAvailability.None,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Belt-and-braces: each branch is already guarded, but the contract is "never throw".
            LogUnexpected(ex);
            return SystemUpdateAvailability.None;
        }
    }

    // --- apt: apt-cache policy packetnet → Candidate > Installed -------------------------
    private SystemUpdateAvailability CheckApt(string runningVersion)
    {
        var result = runner.Run("apt-cache", ["policy", PackageName]);
        if (!result.Succeeded)
        {
            return SystemUpdateAvailability.None; // apt-cache absent / unknown package → no update known.
        }

        // Parse the `Installed:` and `Candidate:` lines. If apt's candidate is strictly newer than
        // what's installed, an apt upgrade has something to do. We compare the candidate against the
        // RUNNING version too (not just apt's Installed line) so a stale dpkg record can't mask it.
        string? installed = null, candidate = null;
        foreach (var raw in result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("Installed:", StringComparison.Ordinal))
            {
                installed = raw["Installed:".Length..].Trim();
            }
            else if (raw.StartsWith("Candidate:", StringComparison.Ordinal))
            {
                candidate = raw["Candidate:".Length..].Trim();
            }
        }

        if (candidate is null || candidate == "(none)" || !NodeVersion.TryParse(candidate, out var cand))
        {
            return SystemUpdateAvailability.None;
        }

        // The baseline is the newer of {apt's Installed, the running build} — whichever is ahead.
        var baseline = RunningOrInstalled(runningVersion, installed);
        return cand.IsUpdateOver(baseline)
            ? SystemUpdateAvailability.Available(cand.ToString())
            : SystemUpdateAvailability.None;
    }

    private static NodeVersion RunningOrInstalled(string runningVersion, string? installed)
    {
        bool haveRunning = NodeVersion.TryParse(runningVersion, out var run);
        if (installed is not null && installed != "(none)" && NodeVersion.TryParse(installed, out var inst))
        {
            return haveRunning && run.CompareTo(inst) > 0 ? run : inst;
        }
        return haveRunning ? run : default;
    }

    // --- github: latest node-v* release tag vs the running version ----------------------
    private async Task<SystemUpdateAvailability> CheckGithubAsync(string runningVersion, CancellationToken cancellationToken)
    {
        var release = await github.GetLatestNodeReleaseAsync(cancellationToken).ConfigureAwait(false);
        if (release is null || !NodeVersion.TryParse(release.TagName, out var latest))
        {
            return SystemUpdateAvailability.None;
        }

        if (!NodeVersion.TryParse(runningVersion, out var running))
        {
            // We can't read our own version → don't claim an update (could be a downgrade).
            return SystemUpdateAvailability.None;
        }

        // The dev-above-release rule lives in IsUpdateOver: a 0.1.0+dev… running build has the same
        // numeric base as a 0.1.0 release, so latest.IsUpdateOver(running) is false → up to date.
        return latest.IsUpdateOver(running)
            ? SystemUpdateAvailability.Available(latest.ToString())
            : SystemUpdateAvailability.None;
    }

    // --- selfcontained: the feed's latest.json version ---------------------------------
    private async Task<SystemUpdateAvailability> CheckSelfContainedAsync(string runningVersion, CancellationToken cancellationToken)
    {
        var latestStr = await feed.GetLatestVersionAsync(cancellationToken).ConfigureAwait(false);
        if (latestStr is null || !NodeVersion.TryParse(latestStr, out var latest))
        {
            return SystemUpdateAvailability.None;
        }
        if (!NodeVersion.TryParse(runningVersion, out var running))
        {
            return SystemUpdateAvailability.None;
        }
        return latest.IsUpdateOver(running)
            ? SystemUpdateAvailability.Available(latest.ToString())
            : SystemUpdateAvailability.None;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Update-availability check threw; reporting no-update.")]
    private partial void LogUnexpected(Exception ex);
}
