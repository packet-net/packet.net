using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// Boots the real <c>Packet.Node</c> composition root with <c>management.auth.enabled</c> ON and
/// opens EVERY Server-Sent-Events feed the control panel subscribes to, presenting the JWT the
/// only way a browser <c>EventSource</c> can: as a <c>?access_token=</c> query parameter.
/// </summary>
/// <remarks>
/// <para>
/// The regression this locks down (review item C001,
/// <see href="https://github.com/packet-net/packet.net/issues/689">#689</see>): the query-token
/// predicate in <c>Program.cs</c> was a hand-kept list of paths that never gained
/// <c>/ports/{id}/tuning/events</c> or <c>/ports/{id}/spectrum/events</c>, so on a stock (auth-on)
/// node the Link Tuner and Waterfall screens got a 401 and rendered the dead stream as
/// "ended"/"unavailable". The predicate is now endpoint metadata
/// (<see cref="Packet.Node.Api.AcceptsQueryAccessToken"/>) applied at each route, and this test is
/// what proves the marker actually reaches the authentication handler, i.e. that routing really
/// does run before authentication in this pipeline.
/// </para>
/// <para>
/// A feed for an id that does not exist answers 404, which is the point: 404 means the request got
/// PAST authentication and authorization to the handler. The negative leg (<c>/status</c> with the
/// same query token gives 401) is what stops the fix from degenerating into "tokens in URLs everywhere".
/// </para>
/// </remarks>
[Trait("Category", "Node")]
public sealed class SseQueryTokenTests : IDisposable
{
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // Every SSE feed the SPA opens with an EventSource (web/packetnet-ui/src/lib/api.ts).
    // The ids are deliberately non-existent: reaching the handler (404) is the assertion.
    private static readonly string[] SseFeeds =
    [
        "/api/v1/events",
        "/api/v1/rigs/events",
        "/api/v1/ports/no-such-port/tuning/events",
        "/api/v1/ports/no-such-port/spectrum/events",
        "/api/v1/sessions/no-such-session/stream",
        "/api/v1/console/console:no-such/stream",
    ];

    public SseQueryTokenTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "packetnet-ssetoken-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "node.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private static string ConfigYaml(bool authEnabled) => $"""
        schemaVersion: 1
        identity:
          callsign: M0LTE-1
          alias: LONDON
        ports: []
        management:
          telnet:
            enabled: false
          http:
            bind: 127.0.0.1
            port: 8080
          auth:
            enabled: {(authEnabled ? "true" : "false")}
        """;

    private sealed class NodeAppFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }

    private NodeAppFactory Factory() => new(configPath, dbPath);

    /// <summary>Bootstrap an admin over an auth-OFF node, then persist the auth-ON config through
    /// the (still ungated) write seam so the next boot comes up enforcing. Returns the admin JWT -
    /// admin satisfies read/operate/admin, so one token opens all six feeds.</summary>
    private async Task<string> BootstrapAndEnableAuthAsync()
    {
        File.WriteAllText(configPath, ConfigYaml(authEnabled: false));

        await using var setupFactory = Factory();
        using var setupClient = setupFactory.CreateClient();

        var setup = await setupClient.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "M0LTE-1", alias = "LONDON" },
            admin = new { username = "sysop", password = "hunter2hunter2" },
        }, Web);
        setup.StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await setupClient.PostAsJsonAsync("/api/v1/auth/login",
            new { username = "sysop", password = "hunter2hunter2" }, Web);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("token").GetString();
        token.Should().NotBeNullOrWhiteSpace();

        var flip = await setupClient.PutAsync("/api/v1/config/raw",
            new StringContent(ConfigYaml(authEnabled: true)));
        flip.StatusCode.Should().Be(HttpStatusCode.OK);

        return token!;
    }

    [Fact]
    public async Task Every_SSE_feed_accepts_the_jwt_as_a_query_parameter_when_auth_is_on()
    {
        var token = await BootstrapAndEnableAuthAsync();

        // Fresh host over the SAME db: the user, the signing key and the auth-on config persist.
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // Sanity: auth really is enforced on this host.
        (await client.GetAsync("/api/v1/status")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        foreach (var feed in SseFeeds)
        {
            var withQuery = await StatusOfAsync(client, $"{feed}?access_token={Uri.EscapeDataString(token)}");
            withQuery.Should().NotBe(
                HttpStatusCode.Unauthorized,
                "{0} is an EventSource feed, and a browser EventSource can only present the token in the query", feed);
            withQuery.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.NotFound]);

            // The query token must be worth exactly as much as the header on these routes - same
            // status either way. (This is what failed for the tuning/spectrum feeds: 401 by query,
            // 404 by header, so the token SOURCE was the gate.)
            var withHeader = await StatusOfAsync(client, feed, token);
            withQuery.Should().Be(withHeader, "the token source must not change the outcome on {0}", feed);
        }
    }

    [Fact]
    public async Task A_non_SSE_endpoint_still_refuses_a_query_parameter_token()
    {
        var token = await BootstrapAndEnableAuthAsync();

        await using var factory = Factory();
        using var client = factory.CreateClient();

        // Tokens in URLs leak (proxy logs, referrers), so the concession is confined to the SSE
        // feeds: a plain read endpoint must ignore ?access_token= entirely.
        var byQuery = await StatusOfAsync(client, $"/api/v1/status?access_token={Uri.EscapeDataString(token)}");
        byQuery.Should().Be(HttpStatusCode.Unauthorized);

        // ... and the same token in the header is fine, so the 401 above is about WHERE the token
        // was, not the token itself.
        var byHeader = await StatusOfAsync(client, "/api/v1/status", token);
        byHeader.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>GET a URL and return only its status, reading headers only and disposing straight
    /// away: an SSE feed that authenticates never ends, so the response body must not be drained.
    /// The timeout keeps a regression a fast failure rather than a hung CI job.</summary>
    private static async Task<HttpStatusCode> StatusOfAsync(HttpClient client, string url, string? bearer = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        return resp.StatusCode;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A best-effort temp-dir clean-up; a locked db file is not a test failure.
        }
    }
}
