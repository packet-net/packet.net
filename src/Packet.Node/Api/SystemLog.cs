using Microsoft.Extensions.Logging;

namespace Packet.Node.Api;

/// <summary>
/// Structured audit-log lines for the system/update endpoints, via the
/// <see cref="LoggerMessage"/> source generator (allocation-free, repo logging rule).
/// Logged under the <c>Packet.Node.System</c> category. No secrets cross this surface —
/// only a username, a source IP, the install channel, and an outcome.
/// </summary>
internal static partial class SystemLog
{
    [LoggerMessage(EventId = 5001, Level = LogLevel.Warning,
        Message = "system: update REQUESTED channel={Channel} user={User} ip={Ip}")]
    public static partial void UpdateRequested(ILogger logger, string channel, string user, string ip);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information,
        Message = "system: update STARTED via apt (detached) — node will restart; user={User} ip={Ip}")]
    public static partial void UpdateStarted(ILogger logger, string user, string ip);

    [LoggerMessage(EventId = 5003, Level = LogLevel.Warning,
        Message = "system: update DECLINED channel={Channel} reason={Reason} user={User} ip={Ip}")]
    public static partial void UpdateDeclined(ILogger logger, string channel, string reason, string user, string ip);

    [LoggerMessage(EventId = 5004, Level = LogLevel.Error,
        Message = "system: update FAILED to launch channel={Channel} detail={Detail} user={User} ip={Ip}")]
    public static partial void UpdateLaunchFailed(ILogger logger, string channel, string detail, string user, string ip);
}
