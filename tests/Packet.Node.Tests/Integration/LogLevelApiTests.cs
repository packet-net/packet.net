using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The runtime log-level control API: <c>GET /api/v1/system/loglevel</c> (effective default +
/// active overrides, read scope) and <c>PUT /api/v1/system/loglevel</c> (set/clear an override,
/// admin scope, validated). Proves the endpoint round-trips through the real host's logging
/// pipeline — a set override actually changes <see cref="ILogger.IsEnabled"/> on the host's own
/// logger factory — and that bad input is a 400.
/// </summary>
[Trait("Category", "Node")]
public sealed class LogLevelApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    public LogLevelApiTests()
    {
        dir = TestPaths.NewPath("pdn-loglevel");
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "packetnet.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task Get_reports_the_effective_default_and_no_overrides_by_default()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/system/loglevel");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("effectiveDefault").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("overrides").GetArrayLength().Should().Be(0, "default state = no overrides");
    }

    [Fact]
    public async Task Put_sets_an_override_that_takes_effect_live_then_get_reflects_it()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // A logger from the HOST's own factory, created before the override — proves the live effect.
        var hostFactory = factory.Services.GetRequiredService<ILoggerFactory>();
        var log = hostFactory.CreateLogger("Packet.Ax25.Session");
        log.IsEnabled(LogLevel.Debug).Should().BeFalse();

        var put = await client.PutAsJsonAsync("/api/v1/system/loglevel",
            new { category = "Packet.Ax25", level = "Debug" }, Web);
        put.StatusCode.Should().Be(HttpStatusCode.OK);
        var putBody = await put.Content.ReadFromJsonAsync<JsonElement>(Web);
        putBody.GetProperty("action").GetString().Should().Be("set");
        putBody.GetProperty("level").GetString().Should().Be("Debug");

        // The already-created host logger now logs Debug — live, no restart.
        log.IsEnabled(LogLevel.Debug).Should().BeTrue();

        var get = await client.GetAsync("/api/v1/system/loglevel");
        var getBody = await get.Content.ReadFromJsonAsync<JsonElement>(Web);
        var overrides = getBody.GetProperty("overrides");
        overrides.GetArrayLength().Should().Be(1);
        overrides[0].GetProperty("category").GetString().Should().Be("Packet.Ax25");
        overrides[0].GetProperty("level").GetString().Should().Be("Debug");

        // Clearing restores the prior behaviour live.
        var clear = await client.PutAsJsonAsync("/api/v1/system/loglevel",
            new { category = "Packet.Ax25", level = (string?)null }, Web);
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        (await clear.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("action").GetString().Should().Be("cleared");
        log.IsEnabled(LogLevel.Debug).Should().BeFalse();
    }

    [Theory]
    [InlineData("Packet.Ax25", "Verbose")]   // not a LogLevel name
    [InlineData("Packet.Ax25", "None")]      // None is rejected (not a meaningful verbosity target)
    [InlineData("Packet.Ax25", "7")]         // out-of-range numeric
    public async Task Put_rejects_a_bad_level_with_400(string category, string level)
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/v1/system/loglevel", new { category, level }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_rejects_an_empty_category_with_400()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var resp = await client.PutAsJsonAsync("/api/v1/system/loglevel",
            new { category = "", level = "Debug" }, Web);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_requires_admin_when_auth_is_on()
    {
        WriteConfig(authEnabled: true);
        await using var factory = Factory();
        using var client = factory.CreateClient();

        // No bearer token → the admin gate rejects (401). The GET is read-gated; the PUT is admin.
        var resp = await client.PutAsJsonAsync("/api/v1/system/loglevel",
            new { category = "Packet.Ax25", level = "Debug" }, Web);
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

    private LogLevelFactory Factory() => new(configPath, dbPath);

    private sealed class LogLevelFactory(string configPath, string dbPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
        }
    }
}
