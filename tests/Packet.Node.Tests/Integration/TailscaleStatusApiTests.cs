using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.Tailscale;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The Tailscale status read API: <c>GET /api/v1/system/tailscale</c> returns the live
/// <see cref="ITailscaleStatus"/> snapshot in the camelCase shape the web panel consumes
/// (<c>enabled / state / fqdn / authUrl / funnel</c>), is read-gated (401 without a token when
/// auth is on), and reports the default disabled status when nothing has run. The status holder
/// is seeded with a known snapshot AFTER the host has started — the default config is disabled,
/// so the real sidecar never launches; seeding post-start avoids racing the supervisor's
/// startup reconcile (which legitimately sets a disabled-config status to "disabled").
/// </summary>
[Trait("Category", "Node")]
public sealed class TailscaleStatusApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    public TailscaleStatusApiTests()
    {
        dir = TestPaths.NewPath("pdn-tsstatus");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "packetnet.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task Reports_the_current_status_shape()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();   // host started → the startup reconcile ran
        // Seed the live holder the API reads (the supervisor's write seam), post-start so the
        // disabled-config startup reconcile can't clobber it.
        factory.Services.GetRequiredService<ITailscaleStatus>().Update(new TailscaleStatusSnapshot(
            Enabled: true, State: "running", Fqdn: "pdn.test.ts.net",
            AuthUrl: null, Error: "should-not-leak", Funnel: true));

        var resp = await client.GetAsync("/api/v1/system/tailscale");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("enabled").GetBoolean().Should().BeTrue();
        body.GetProperty("state").GetString().Should().Be("running");
        body.GetProperty("fqdn").GetString().Should().Be("pdn.test.ts.net");
        body.GetProperty("funnel").GetBoolean().Should().BeTrue();
        body.TryGetProperty("authUrl", out var authUrl).Should().BeTrue();
        authUrl.ValueKind.Should().Be(JsonValueKind.Null);
        // The internal error string is NOT part of the read shape (the panel doesn't show it).
        body.TryGetProperty("error", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_disabled_by_default()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/system/tailscale");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("enabled").GetBoolean().Should().BeFalse();
        body.GetProperty("state").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task Is_read_gated_when_auth_is_on()
    {
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // No bearer token → the read gate rejects.
        var resp = await client.GetAsync("/api/v1/system/tailscale");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- harness ---------------------------------------------------------------

    private void WriteConfig(bool authEnabled) => File.WriteAllText(configPath, $"""
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
        """);

    private TailscaleFactory Factory() => new(configPath, dbPath);

    private sealed class TailscaleFactory(string configPath, string dbPath)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }
}
