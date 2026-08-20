using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Api;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The route inventory in <c>docs/node-api.md</c> is generated from this node's real
/// <see cref="EndpointDataSource"/>, and this test is what keeps it that way.
/// <para>
/// It replaces <c>docs/node-api.yaml</c>, a hand-written OpenAPI draft that was derived
/// from the web UI's typed client rather than from the server. By the 2026-08-16 review
/// it documented ten operations that had no route at all and omitted roughly forty that
/// existed, including the whole OAuth, app-platform, console, audit and system surfaces,
/// and every schema spot-checked was wrong (review item C019,
/// <see href="https://github.com/packet-net/packet.net/issues/701">#701</see>). A second
/// hand-maintained mirror of the server was the root cause, so the fix is to stop keeping
/// one: the doc carries the surface the code actually maps, and a drift fails here.
/// </para>
/// <para>
/// To regenerate after adding or moving a route:
/// <c>scripts/update-node-api.sh</c> (which is this test with
/// <c>PDN_WRITE_ROUTE_TABLE=1</c> set).
/// </para>
/// </summary>
[Trait("Category", "Node")]
public sealed class RouteInventoryTests : IDisposable
{
    private const string BeginMarker = "<!-- BEGIN generated route inventory -->";
    private const string EndMarker = "<!-- END generated route inventory -->";

    private readonly string configPath;

    public RouteInventoryTests()
    {
        // The conditional surfaces have to be switched on or the inventory would document
        // a smaller node than a fully-configured one serves: /mcp is mapped only when
        // mcp.enabled and mcp.sse.enabled are both set (McpRegistration), and OAuth
        // refuses to validate unless management.auth is on. Everything else maps
        // unconditionally, so this config yields the maximal route table.
        var dir = Path.Combine(Path.GetTempPath(), "packetnet-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 2
            identity:
              callsign: M0LTE-1
            ports: []
            management:
              auth:
                enabled: true
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            mcp:
              enabled: true
              sse:
                enabled: true
              oauth:
                enabled: true
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;

    [Fact]
    public async Task Documented_route_inventory_matches_the_endpoints_the_node_maps()
    {
        await using var factory = new NodeAppFactory();
        // Force the host up (the factory builds lazily) so every Map* call has run.
        using var client = factory.CreateClient();

        var live = RenderTable(Inventory(factory.Services.GetRequiredService<EndpointDataSource>()));
        var docPath = Path.Combine(RepoRoot(), "docs", "node-api.md");
        var doc = await File.ReadAllTextAsync(docPath);

        if (Environment.GetEnvironmentVariable("PDN_WRITE_ROUTE_TABLE") == "1")
        {
            await File.WriteAllTextAsync(docPath, Splice(doc, live));
            doc = await File.ReadAllTextAsync(docPath);
        }

        Extract(doc).Should().Be(live,
            "docs/node-api.md documents the route surface this build serves - regenerate it with scripts/update-node-api.sh");
    }

    /// <summary>
    /// Every route the node maps under <c>/api/v1</c> is scope-gated or is one of the
    /// bootstrap endpoints that cannot be (a node with no users has to be able to answer
    /// the setup wizard and a login). A new endpoint that forgets its policy lands here
    /// rather than in production.
    /// </summary>
    [Fact]
    public async Task Every_control_api_route_is_gated_or_is_a_named_bootstrap_endpoint()
    {
        // The always-open set, and why each one is: the SPA has to discover whether the
        // node needs setting up before it holds any credential; /setup itself is one-shot
        // and refuses once a user exists, and /setup/devices carries the same one-shot gate
        // in its own handler (403 the moment a user exists); login and the WebAuthn
        // assertion ceremony are how a credential is obtained in the first place.
        string[] alwaysOpen =
        [
            "/api/v1/setup/state",
            "/api/v1/setup",
            "/api/v1/setup/devices",
            "/api/v1/auth/login",
            "/api/v1/auth/refresh",
            "/api/v1/auth/logout",
            "/api/v1/auth/webauthn/assert/begin",
            "/api/v1/auth/webauthn/assert/complete",
        ];

        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var ungated = Inventory(factory.Services.GetRequiredService<EndpointDataSource>())
            .Where(r => r.Path.StartsWith("/api/v1/", StringComparison.Ordinal))
            .Where(r => r.Policy == "anonymous")
            .Where(r => !alwaysOpen.Contains(r.Path))
            .Select(r => $"{r.Method} {r.Path}")
            .ToList();

        ungated.Should().BeEmpty("a control-API route with no scope policy is reachable by anyone who can reach the panel");
    }

    private static IReadOnlyList<RouteRow> Inventory(EndpointDataSource endpoints) =>
    [
        .. endpoints.Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var path = "/" + e.RoutePattern.RawText?.TrimStart('/');
                var methods = e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
                var policy = Policy(e);
                var sse = e.Metadata.GetMetadata<AcceptsQueryAccessToken>() is not null;
                return (methods is { Count: > 0 } ? methods : ["ANY"])
                    .Select(m => new RouteRow(m, path, policy, sse));
            })
            .OrderBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Method, StringComparer.Ordinal),
    ];

    private static string Policy(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return "anonymous";
        }

        var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.StartsWith("pdn-", StringComparison.Ordinal) ? p[4..] : p)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return policies.Count == 0 ? "anonymous" : string.Join(" + ", policies);
    }

    private static string RenderTable(IReadOnlyList<RouteRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append("| Method | Path | Scope |\n");
        sb.Append("|---|---|---|\n");
        foreach (var r in rows)
        {
            var scope = r.Policy == "anonymous" ? "anonymous" : "`" + r.Policy + "`";
            if (r.AcceptsQueryToken)
            {
                scope += " (SSE, also takes `?access_token=`)";
            }

            sb.Append(CultureInfo.InvariantCulture, $"| {r.Method} | `{r.Path}` | {scope} |\n");
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static string Extract(string doc)
    {
        var start = doc.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = doc.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException(
                $"docs/node-api.md is missing the {BeginMarker} / {EndMarker} pair the generator writes between.");
        }

        return doc[(start + BeginMarker.Length)..end].Trim('\n', '\r', ' ');
    }

    private static string Splice(string doc, string table)
    {
        var start = doc.IndexOf(BeginMarker, StringComparison.Ordinal);
        var end = doc.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start < 0 || end < 0)
        {
            throw new InvalidOperationException(
                $"docs/node-api.md is missing the {BeginMarker} / {EndMarker} pair the generator writes between.");
        }

        return doc[..(start + BeginMarker.Length)] + "\n\n" + table + "\n\n" + doc[end..];
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "packaging", "packetnet.yaml")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Could not locate the repo root (no packaging/packetnet.yaml above the test assembly).");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try
        {
            var dir = Path.GetDirectoryName(configPath);
            if (dir is not null)
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A temp dir we could not remove is not a test failure.
        }
    }

    private sealed record RouteRow(string Method, string Path, string Policy, bool AcceptsQueryToken);
}
