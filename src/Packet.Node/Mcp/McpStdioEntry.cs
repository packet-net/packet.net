using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Packet.Mcp;
using Packet.Mcp.Tools;

namespace Packet.Node.Mcp;

/// <summary>
/// The <c>pdn mcp</c> subcommand: an MCP server over <b>stdio</b> for local clients
/// (Claude Code, etc.). It bridges to the running node's loopback REST API via
/// <see cref="RestNodeMcpBackend"/> — a stdio process can't share the live node's
/// in-proc state, so it talks to <c>127.0.0.1</c>. The caller is the OS-trusted local
/// user (<see cref="McpCaller.LocalStdio"/>, all scopes). See docs/mcp-design.md.
/// </summary>
/// <remarks>
/// <para>
/// <b>The bridge carries a bearer token.</b> The node's control API is auth-gated by default
/// (<c>management.auth.enabled</c> defaults to true) and there is no loopback exemption, so a
/// token-less bridge 401s on every tool call. The token is resolved once at start from, in
/// order: <c>--token &lt;value&gt;</c>, <c>PDN_NODE_TOKEN</c>, or the contents of the file named
/// by <c>PDN_NODE_TOKEN_FILE</c>; mint one with <c>POST /api/v1/mcp/token</c>. It is attached as
/// the client's default <c>Authorization: Bearer</c> header. With no token the bridge still
/// starts (an auth-off node works fine) and any 401/403 is reported as a plain "node requires
/// auth" message rather than a raw <c>HttpRequestException</c> (review item C061, #694).
/// </para>
/// </remarks>
public static class McpStdioEntry
{
    /// <summary>The default node base URL (the web listener's loopback default).</summary>
    public const string DefaultNodeUrl = "http://127.0.0.1:8080";

    /// <summary>The env var holding the bearer token for the node's control API.</summary>
    public const string TokenEnvVar = "PDN_NODE_TOKEN";

    /// <summary>The env var naming a file whose contents are the bearer token (so the token
    /// need not appear in a process environment dump or an MCP client's config JSON).</summary>
    public const string TokenFileEnvVar = "PDN_NODE_TOKEN_FILE";

    /// <summary>
    /// Run the stdio MCP server until stdin closes. <paramref name="args"/> is the full
    /// process argv (the first element is <c>mcp</c>); <c>--node-url &lt;url&gt;</c> or the
    /// <c>PDN_NODE_URL</c> env var override the node base URL, and <c>--token &lt;jwt&gt;</c>,
    /// <c>PDN_NODE_TOKEN</c> or <c>PDN_NODE_TOKEN_FILE</c> supply the bearer token.
    /// </summary>
    public static async Task RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = Host.CreateApplicationBuilder(args);

        // stdout is the MCP protocol stream — every log MUST go to stderr.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        var baseUrl = ResolveNodeUrl(args);
        var token = ResolveToken(args, Environment.GetEnvironmentVariable, ReadTokenFile);
        builder.Services.AddSingleton<IMcpCallerAccessor, LocalStdioCallerAccessor>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddHttpClient<INodeMcpBackend, RestNodeMcpBackend>(
            c => ConfigureClient(c, baseUrl, token));
        builder.Services.AddTransient<ReadTools>();
        builder.Services.AddTransient<WriteTools>();

        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<DiagnosticTools>()
            .WithTools<ReadTools>()
            .WithTools<WriteTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
    }

    /// <summary>Point the bridge's <see cref="HttpClient"/> at the node and attach the bearer
    /// token when there is one (no token = unauthenticated, which only works on an auth-off
    /// node). Factored out so a test can assert the header without booting the host.</summary>
    internal static void ConfigureClient(HttpClient client, string baseUrl, string? token)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.BaseAddress = new Uri(baseUrl, UriKind.Absolute);
        if (!string.IsNullOrWhiteSpace(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    /// <summary>
    /// Resolve the node bearer token: <c>--token &lt;value&gt;</c> wins, then
    /// <see cref="TokenEnvVar"/>, then the contents of the file named by
    /// <see cref="TokenFileEnvVar"/>. Null when none is set. The lookups are injected so the
    /// resolution order is testable without mutating process environment.
    /// </summary>
    internal static string? ResolveToken(
        string[] args, Func<string, string?> env, Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(readFile);

        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--token" && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1].Trim();
            }
        }

        var direct = env(TokenEnvVar);
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct.Trim();
        }

        var path = env(TokenFileEnvVar);
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        var fromFile = readFile(path);
        return string.IsNullOrWhiteSpace(fromFile) ? null : fromFile.Trim();
    }

    // An unreadable / absent token file is not fatal: the bridge starts token-less and the
    // first tool call reports the auth requirement plainly (better than a startup stack trace
    // on a stdio server whose stdout is the protocol stream).
    private static string? ReadTokenFile(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string ResolveNodeUrl(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--node-url")
            {
                return args[i + 1];
            }
        }
        var env = Environment.GetEnvironmentVariable("PDN_NODE_URL");
        return string.IsNullOrWhiteSpace(env) ? DefaultNodeUrl : env;
    }
}
