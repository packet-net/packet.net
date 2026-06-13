using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Packet.Mcp;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Mcp;

/// <summary>
/// The <see cref="IMcpCallerAccessor"/> for the in-process SSE transport: derives
/// the <see cref="McpCaller"/> from the authenticated request principal. Mirrors
/// the REST surface's auth behaviour — when <c>management.auth.enabled</c> is off
/// the caller is granted every scope (pass-through), and when it's on the caller's
/// scopes are expanded from its single <c>scope</c> claim under the hierarchical
/// admin ⊃ operate ⊃ read model.
/// </summary>
public sealed class HttpContextMcpCallerAccessor(IHttpContextAccessor http, IConfigProvider config)
    : IMcpCallerAccessor
{
    private readonly IHttpContextAccessor http = http;
    private readonly IConfigProvider config = config;

    public McpCaller Current
    {
        get
        {
            var user = http.HttpContext?.User;
            string actor = user?.Identity?.Name
                ?? user?.FindFirstValue("sub")
                ?? "anonymous";

            var scopes = new HashSet<string>(StringComparer.Ordinal);
            if (!config.Current.Management.Auth.Enabled)
            {
                // Pass-through: auth off ⇒ every scope, exactly like the REST gate.
                scopes.Add(McpScopes.Read);
                scopes.Add(McpScopes.Operate);
                scopes.Add(McpScopes.Admin);
            }
            else
            {
                var granted = user?.FindFirstValue(AuthScopes.ScopeClaim);
                // Expand the single granted scope into the set it satisfies.
                if (AuthScopes.Satisfies(granted, AuthScopes.Read)) scopes.Add(McpScopes.Read);
                if (AuthScopes.Satisfies(granted, AuthScopes.Operate)) scopes.Add(McpScopes.Operate);
                if (AuthScopes.Satisfies(granted, AuthScopes.Admin)) scopes.Add(McpScopes.Admin);
            }

            string? ip = http.HttpContext?.Connection.RemoteIpAddress?.ToString();
            return new McpCaller(actor, "mcp:sse", scopes, ip);
        }
    }
}
