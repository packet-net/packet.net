using Microsoft.Extensions.Logging;

namespace Packet.Node.Api;

/// <summary>
/// Structured log lines for the "Available apps" install/update endpoints, via the
/// <see cref="LoggerMessage"/> source generator (allocation-free, repo logging rule). Logged
/// under the <c>Packet.Node.Api.PdnAvailableAppsApi</c> category. Covers the best-effort
/// post-install restart that swaps a freshly-laid binary in for the still-running old one.
/// </summary>
internal static partial class AvailableAppsLog
{
    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning,
        Message = "available-apps: post-install restart of app '{Id}' failed; the install stands.")]
    public static partial void PostInstallRestartFailed(ILogger logger, Exception ex, string id);
}
