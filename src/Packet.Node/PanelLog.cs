using Microsoft.Extensions.Logging;

namespace Packet.Node;

/// <summary>
/// The startup lines that tell a headless operator where the control panel is, via the
/// <see cref="LoggerMessage"/> source generator (allocation-free, repo logging rule).
/// journalctl -u packetnet is the first place someone looks after an install, and
/// Kestrel's own "Now listening on" is filtered out (Microsoft.AspNetCore at Warning),
/// so the node names its concrete URLs itself.
/// </summary>
internal static partial class PanelLog
{
    [LoggerMessage(EventId = 5401, Level = LogLevel.Information,
        Message = "Control panel: {Urls}")]
    public static partial void PanelUp(ILogger logger, string urls);

    [LoggerMessage(EventId = 5402, Level = LogLevel.Information,
        Message = "First-run setup pending - open the control panel from a browser to create the admin login and claim this node.")]
    public static partial void SetupPending(ILogger logger);
}
