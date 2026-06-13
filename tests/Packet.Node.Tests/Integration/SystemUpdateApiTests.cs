using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The Phase-7 system/self-update API: <c>GET /api/v1/system/info</c> (version + install
/// channel) and the channel-aware <c>POST /api/v1/system/update</c> — apt channel launches
/// the privileged helper and 202s; self-contained 501s; an unknown channel 409s; and the
/// trigger is admin-gated when auth is on. The install channel + update launcher are seamed
/// (<see cref="IInstallChannelProvider"/> / <see cref="ISystemUpdateLauncher"/>) so the real
/// systemd/apt mechanism isn't touched here.
/// </summary>
[Trait("Category", "Node")]
public sealed class SystemUpdateApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private readonly string dir;
    private readonly string configPath;
    private readonly string dbPath;

    public SystemUpdateApiTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-sysupd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        configPath = Path.Combine(dir, "packetnet.yaml");
        dbPath = Path.Combine(dir, "pdn.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task Info_reports_the_version_channel_and_update_mechanism()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Apt);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/system/info");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("version").GetString().Should().NotBeNullOrEmpty();
        body.GetProperty("channel").GetString().Should().Be("apt");
        body.GetProperty("updateMechanism").GetString().Should().Be("apt");
    }

    [Fact]
    public async Task Update_on_the_apt_channel_launches_the_helper_and_returns_202()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Apt);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        factory.Launcher.Calls.Should().Be(1, "the apt channel must trigger the privileged update helper");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("status").GetString().Should().Be("started");
        body.GetProperty("via").GetString().Should().Be("apt");
    }

    [Fact]
    public async Task Update_returns_503_when_the_launch_fails()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Apt);
        factory.Launcher.Result = UpdateLaunchResult.Failed("polkit denied");
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Update_on_the_self_contained_channel_is_501_and_does_not_launch_apt()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.SelfContained);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        factory.Launcher.Calls.Should().Be(0, "the apt launcher must not run on the self-contained channel");
    }

    [Fact]
    public async Task Update_on_an_unknown_channel_is_409()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Unknown);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Launcher.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Update_requires_admin_when_auth_is_on()
    {
        WriteConfig(authEnabled: true);
        await using var factory = Factory(InstallChannel.Apt);
        using var client = factory.CreateClient();

        // No bearer token → the admin gate rejects (401), and the helper never runs.
        var resp = await client.PostAsync("/api/v1/system/update", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        factory.Launcher.Calls.Should().Be(0);
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

    private SystemFactory Factory(InstallChannel channel) => new(configPath, dbPath, channel);

    private sealed class SystemFactory(string configPath, string dbPath, InstallChannel channel)
        : WebApplicationFactory<Program>
    {
        public FakeLauncher Launcher { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IInstallChannelProvider>(new StubChannel(channel));
                services.AddSingleton<ISystemUpdateLauncher>(Launcher);
            });
        }
    }

    private sealed class StubChannel(InstallChannel channel) : IInstallChannelProvider
    {
        public InstallChannel Channel => channel;
    }

    private sealed class FakeLauncher : ISystemUpdateLauncher
    {
        public int Calls { get; private set; }
        public UpdateLaunchResult Result { get; set; } = UpdateLaunchResult.Started;

        public Task<UpdateLaunchResult> StartAptUpdateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
