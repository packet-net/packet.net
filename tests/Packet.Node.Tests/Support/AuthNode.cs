using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A booted-node harness for the auth / security integration suites: one temp dir carrying
/// this test's <c>node.yaml</c> + <c>pdn.db</c>, a <see cref="WebApplicationFactory{T}"/>
/// bound to them, and the bootstrap dance every auth-ON test needs (claim the node and mint
/// the accounts while auth is still off, flip auth on through the live write seam, then boot
/// a second host over the same db).
/// </summary>
/// <remarks>
/// Extracted from the copy of this scaffolding in <c>AuthApiTests</c> because the review
/// remediation added several more auth-ON suites (actor attribution, user-delete revocation,
/// config-secret redaction, audience segregation, the endpoint sweep) and they all need the
/// same six helpers. Config lives in <c>pdn.db</c> (#473), so flipping auth on is a
/// <c>PUT /config/raw</c> (ungated while auth is off), not a file rewrite.
/// </remarks>
public sealed class AuthNode : IDisposable
{
    /// <summary>STJ options matching the host's web defaults (camelCase).</summary>
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string dir;

    /// <summary>Create a harness in its own temp dir. <paramref name="name"/> only makes the
    /// directory recognisable when a test leaves one behind.</summary>
    public AuthNode(string name)
    {
        dir = Path.Combine(Path.GetTempPath(), $"pdn-{name}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        ConfigPath = Path.Combine(dir, "node.yaml");
        DbPath = Path.Combine(dir, "pdn.db");
    }

    /// <summary>The node.yaml this harness boots from (read once, on the first boot).</summary>
    public string ConfigPath { get; }

    /// <summary>The pdn.db carrying config + users + tokens across this harness's boots.</summary>
    public string DbPath { get; }

    /// <summary>The config text: auth on/off, an optional extra <b>top-level</b> block (e.g.
    /// an <c>mcp:</c> or <c>tailscale:</c> section) and an optional extra block nested under
    /// <c>management:</c> (e.g. <c>https:</c>), both indented by the caller. Telnet is off -
    /// no fixed TCP port under the WAF - and the single port is disabled so nothing dials.</summary>
    public static string ConfigYaml(bool authEnabled, string? extra = null, string? managementExtra = null) => $"""
        schemaVersion: 1
        identity:
          callsign: M0LTE-1
          alias: LONDON
        ports:
          - id: vhf
            enabled: false
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: 8101
        management:
          telnet:
            enabled: false
          http:
            bind: 127.0.0.1
            port: 8080
          auth:
            enabled: {(authEnabled ? "true" : "false")}
        """
        + (managementExtra is null ? string.Empty : "\n" + managementExtra.TrimEnd() + "\n")
        + (extra is null ? string.Empty : "\n" + extra.TrimEnd() + "\n");

    /// <summary>Write the starting config file (only the FIRST boot reads it; after that the
    /// db is the source of truth).</summary>
    public void WriteConfig(bool authEnabled, string? extra = null, string? managementExtra = null) =>
        File.WriteAllText(ConfigPath, ConfigYaml(authEnabled, extra, managementExtra));

    /// <summary>A factory bound to THIS harness's config + db.</summary>
    public NodeAppFactory Factory() => new(ConfigPath, DbPath);

    /// <summary>The WAF, pointing the host at this harness's paths via the env vars the
    /// composition root resolves.</summary>
    public sealed class NodeAppFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }

    // --- bootstrap helpers ----------------------------------------------------

    /// <summary>Claim the node (POST /setup) with an admin account.</summary>
    public static async Task Setup(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/setup", new
        {
            identity = new { callsign = "M0LTE-1", alias = "LONDON" },
            admin = new { username, password },
        }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Log in and return the access token.</summary>
    public static async Task<string> Login(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        return body.GetProperty("token").GetString()!;
    }

    /// <summary>Log in and return the whole body (token + refreshToken + scope).</summary>
    public static async Task<JsonElement> LoginFull(HttpClient client, string username, string password)
    {
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
    }

    /// <summary>Create a user (admin token required once auth is on).</summary>
    public static async Task CreateUser(HttpClient client, string? adminToken, string username, string password, string scope)
    {
        var resp = await Send(client, HttpMethod.Post, "/api/v1/users", adminToken,
            JsonContent.Create(new { username, password, scope }, options: Web));
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>Turn auth ON through the live write seam so the db carries it into the next
    /// boot. Ungated because auth is still off when this runs.</summary>
    public static async Task FlipAuthOn(HttpClient client, string? extra = null, string? managementExtra = null)
    {
        var resp = await client.PutAsync("/api/v1/config/raw",
            new StringContent(ConfigYaml(authEnabled: true, extra, managementExtra)));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- request helpers ------------------------------------------------------

    /// <summary>Send a request with an optional bearer token.</summary>
    public static async Task<HttpResponseMessage> Send(
        HttpClient client, HttpMethod method, string url, string? token, HttpContent? content = null)
    {
        using var req = new HttpRequestMessage(method, url);
        if (content is not null)
        {
            req.Content = content;
        }
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await client.SendAsync(req);
    }

    /// <summary>GET with a bearer token.</summary>
    public static Task<HttpResponseMessage> Get(HttpClient client, string? token, string url) =>
        Send(client, HttpMethod.Get, url, token);

    /// <summary>PUT a JSON body with a bearer token.</summary>
    public static Task<HttpResponseMessage> PutJson(HttpClient client, string? token, string url, object body) =>
        Send(client, HttpMethod.Put, url, token, JsonContent.Create(body, options: Web));

    /// <summary>PUT a raw YAML body with a bearer token.</summary>
    public static Task<HttpResponseMessage> PutYaml(HttpClient client, string? token, string url, string yaml) =>
        Send(client, HttpMethod.Put, url, token, new StringContent(yaml, Encoding.UTF8, "text/plain"));

    /// <summary>POST a JSON body with a bearer token.</summary>
    public static Task<HttpResponseMessage> PostJson(HttpClient client, string? token, string url, object body) =>
        Send(client, HttpMethod.Post, url, token, JsonContent.Create(body, options: Web));

    /// <summary>The claims of a node-issued JWT, decoded without validating (the test only
    /// needs to read <c>sub</c> / <c>aud</c>).</summary>
    public static JsonElement DecodeJwtPayload(string token)
    {
        var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = part.PadRight(part.Length + ((4 - (part.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(padded)).RootElement.Clone();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PACKETNET_CONFIG", null);
        Environment.SetEnvironmentVariable("PACKETNET_DB", null);
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
