using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The optional <c>If-Match</c> compare-and-swap on the config read-modify-write endpoints
/// (review item C065, #694), end to end through the composition root: <c>GET /config</c> serves
/// the document version as an <c>ETag</c>, a stale <c>If-Match</c> is a <c>412</c> that changes
/// nothing, a fresh one applies, and no header at all keeps the historical last-writer-wins.
/// </summary>
[Trait("Category", "Node")]
public sealed class ConfigCasApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = NodeConfigJson.CreateOptions();
    private readonly string dir;

    public ConfigCasApiTests()
    {
        dir = TestPaths.NewPath("packetnet-casapi");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "node.yaml");
        File.WriteAllText(configPath, """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
            ports: []
            management:
              auth:
                enabled: false
              telnet:
                enabled: false
              http:
                bind: 127.0.0.1
                port: 8080
            """);
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
        Environment.SetEnvironmentVariable("PACKETNET_DB", Path.Combine(dir, "pdn.db"));
    }

    private sealed class NodeAppFactory : WebApplicationFactory<Program>;

    private static async Task<(NodeConfig Config, string ETag)> GetConfigAsync(HttpClient client)
    {
        var resp = await client.GetAsync("/api/v1/config");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = resp.Headers.ETag?.ToString();
        etag.Should().NotBeNullOrWhiteSpace("GET /config publishes the document version to send back as If-Match");
        var config = JsonSerializer.Deserialize<NodeConfig>(await resp.Content.ReadAsStringAsync(), Web);
        return (config!, etag!);
    }

    private static HttpRequestMessage Put(string url, NodeConfig body, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body, options: Web) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        return request;
    }

    [Fact]
    public async Task A_matching_if_match_applies_and_answers_with_the_new_version()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var (current, etag) = await GetConfigAsync(client);
        using var request = Put("/api/v1/config", current with { Identity = current.Identity with { Grid = "IO91wm" } }, etag);

        var resp = await client.SendAsync(request);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.ETag?.ToString().Should().NotBe(etag, "the applied document has a new version");
        (await GetConfigAsync(client)).Config.Identity.Grid.Should().Be("IO91wm");
    }

    [Fact]
    public async Task A_stale_if_match_is_a_412_and_changes_nothing()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var (current, etag) = await GetConfigAsync(client);

        // Somebody else's edit lands first, so the version the client holds is stale.
        using var first = Put("/api/v1/config", current with { Identity = current.Identity with { Grid = "JO01aa" } }, etag);
        (await client.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var second = Put("/api/v1/config", current with { Identity = current.Identity with { Grid = "IO92ab" } }, etag);
        var resp = await client.SendAsync(second);

        resp.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await GetConfigAsync(client)).Config.Identity.Grid
            .Should().Be("JO01aa", "the loser's whole-document write was refused, not merged or clobbered");
    }

    [Fact]
    public async Task No_if_match_is_last_writer_wins_exactly_as_before()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var (current, _) = await GetConfigAsync(client);
        using var request = Put("/api/v1/config", current with { Identity = current.Identity with { Grid = "IO92ab" } }, ifMatch: null);

        (await client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetConfigAsync(client)).Config.Identity.Grid.Should().Be("IO92ab");
    }

    [Fact]
    public async Task A_port_add_honours_a_stale_if_match_too()
    {
        await using var factory = new NodeAppFactory();
        using var client = factory.CreateClient();

        var (current, etag) = await GetConfigAsync(client);

        // Land an unrelated config edit so the port editor's version is stale.
        using var other = Put("/api/v1/config", current with { Identity = current.Identity with { Grid = "JO01aa" } }, etag);
        (await client.SendAsync(other)).StatusCode.Should().Be(HttpStatusCode.OK);

        using var add = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ports")
        {
            Content = JsonContent.Create(
                new PortConfig
                {
                    Id = "vhf",
                    Enabled = false,
                    Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8101 },
                },
                options: Web),
        };
        add.Headers.TryAddWithoutValidation("If-Match", etag);

        var resp = await client.SendAsync(add);

        resp.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        (await GetConfigAsync(client)).Config.Ports.Should().BeEmpty("the stale port add was refused");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
