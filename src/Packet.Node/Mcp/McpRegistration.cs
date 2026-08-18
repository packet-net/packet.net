using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Packet.Mcp;
using Packet.Mcp.Tools;
using Packet.Node.Api;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Mcp;

/// <summary>
/// Composition glue for the in-process MCP server (Phase 8). Registers the tool
/// surface + the live backend, and - when <c>mcp.enabled</c> and <c>mcp.sse.enabled</c>
/// - mounts the Streamable-HTTP transport on the web listener at the configured
/// path, gated by the MCP-audience policy (read scope on an MCP-audience token;
/// pass-through when auth is off, like the REST API). stdio is the separate
/// <c>pdn mcp</c> subcommand. See docs/mcp-design.md.
/// </summary>
public static class McpRegistration
{
    /// <summary>Register the MCP tool surface + the live backend in DI.</summary>
    public static void AddPdnMcp(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<INodeMcpBackend, LiveNodeMcpBackend>();
        services.AddScoped<IMcpCallerAccessor, HttpContextMcpCallerAccessor>();
        services.AddScoped<ReadTools>();
        services.AddScoped<WriteTools>();

        services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<DiagnosticTools>()
            .WithTools<ReadTools>()
            .WithTools<WriteTools>();
    }

    /// <summary>Mount the MCP HTTP transport when the config asks for it.</summary>
    public static void MapPdnMcp(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var mcp = app.Services.GetRequiredService<IConfigProvider>().Current.Mcp;
        if (mcp.Enabled && mcp.Sse.Enabled)
        {
            app.MapMcp(mcp.Sse.Path).RequireAuthorization(PdnAuthPolicies.Mcp);
        }
    }
}
