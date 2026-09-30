using System.Net;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// Whether the MCP OAuth consent page, which takes a username and password, is reachable
/// over plain HTTP from beyond this host. Not refused (the panel's own login has the same
/// exposure, and a hard block would break the reverse-proxy TLS case), only worth one line
/// at startup so an operator who enabled the connector on a LAN-facing HTTP bind knows what
/// they did (security review M2, #427). The listener's own TLS or the Tailscale sidecar in
/// front of it each settle the question; a pinned issuer on https does not, since it says
/// nothing about what the node itself is bound to.
/// </summary>
public static class McpOauthCleartext
{
    /// <summary>True when <c>mcp.oauth.enabled</c> and the only listener is plain HTTP on a
    /// bind other than loopback, with no Tailscale sidecar.</summary>
    public static bool Exposed(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var management = config.Management;
        return config.Mcp.Oauth.Enabled
            && !management.Https.Enabled
            && !config.Tailscale.Enabled
            && !IsLoopback(management.Http.Bind);
    }

    private static bool IsLoopback(string bind) =>
        IPAddress.TryParse(bind, out var address)
            ? IPAddress.IsLoopback(address)
            : string.Equals(bind, "localhost", StringComparison.OrdinalIgnoreCase);
}
