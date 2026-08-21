using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Packet.Node.Core.SelfUpdate;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Integration;

/// <summary>
/// The Phase-7 system/self-update API: <c>GET /api/v1/system/info</c> (version + install
/// channel + the cached update-availability snapshot) and the channel-aware
/// <c>POST /api/v1/system/update</c> - apt and github both launch the privileged helper and 202
/// (with the right <c>via</c> + a per-channel <see cref="SystemUpdateRequest"/>); an unknown
/// channel 409s; a launch fault 503s; the trigger is admin-gated when auth is on. The
/// seams (<see cref="IInstallChannelProvider"/> / <see cref="ISystemUpdateLauncher"/> /
/// <see cref="IGitHubReleaseClient"/> / <see cref="IUpdateAvailabilityProbe"/>) so the real
/// systemd / apt / GitHub-API mechanisms are never touched here.
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
        dir = TestPaths.NewPath("pdn-sysupd");
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
        // The shared API contract also carries the update-availability fields the web UI's banner
        // consumes. The availability service serves a cached snapshot - by default (no update
        // probed yet) the safe default (no update known).
        body.GetProperty("updateAvailable").GetBoolean().Should().BeFalse();
        body.GetProperty("latestVersion").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Info_surfaces_an_available_update_from_the_version_service()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Github);
        // Override the availability service with one already holding an "update available" snapshot.
        factory.Availability = new StubVersionService("0.8.0", SystemUpdateAvailability.Available("0.9.0"));
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/v1/system/info");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("version").GetString().Should().Be("0.8.0");
        body.GetProperty("channel").GetString().Should().Be("github");
        body.GetProperty("updateAvailable").GetBoolean().Should().BeTrue();
        body.GetProperty("latestVersion").GetString().Should().Be("0.9.0");
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
        factory.Launcher.LastRequest!.Channel.Should().Be("apt");
        factory.Launcher.LastRequest!.GithubRequest.Should().BeNull("apt carries no github download request");
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

    // A release attaches its .deb under a version-free name (`packetnet_<arch>.deb`) so that
    // /releases/latest/download/ is a permanent link. That is the only name resolved.
    [Fact]
    public async Task Update_on_the_github_channel_resolves_the_version_free_asset_then_launches_with_a_request()
    {
        var arch = GithubUpdateRequestBuilder.CurrentDebArch ?? "amd64";
        await AssertGithubUpdateResolvesAsync($"packetnet_{arch}.deb", arch);
    }

    // Releases up to node-v0.43.0 named the asset `packetnet_<ver>_<arch>.deb`. Reading that name
    // was deliberately dropped, so such a release must be declined outright rather than installed
    // - and it must fail closed (409, helper untouched), not by guessing a URL.
    [Fact]
    public async Task Update_on_the_github_channel_declines_a_release_that_only_carries_the_legacy_asset_name()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Github);
        var arch = GithubUpdateRequestBuilder.CurrentDebArch ?? "amd64";
        const string baseUrl = "https://github.com/packet-net/packet.net/releases/download/node-v0.9.0";
        var legacy = $"packetnet_0.9.0_{arch}.deb";
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        factory.Github = new FakeGithub(
            release: new GitHubRelease("node-v0.9.0", new Dictionary<string, Uri>
            {
                [legacy] = new($"{baseUrl}/{legacy}"),
                ["SHA256SUMS"] = new($"{baseUrl}/SHA256SUMS"),
            }),
            sha256Sums: $"{sha}  {legacy}\n");
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Launcher.Calls.Should().Be(0, "no recognisable asset means nothing to install");
    }

    /// <summary>
    /// Drives a real POST /api/v1/system/update on the github channel against a fake release that
    /// attaches the .deb as <paramref name="debName"/>, and asserts the launcher received a
    /// request resolved to that asset and its checksum.
    /// </summary>
    private async Task AssertGithubUpdateResolvesAsync(string debName, string arch)
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Github);
        const string baseUrl = "https://github.com/packet-net/packet.net/releases/download/node-v0.9.0";
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var assets = new Dictionary<string, Uri>
        {
            [debName] = new($"{baseUrl}/{debName}"),
            ["SHA256SUMS"] = new($"{baseUrl}/SHA256SUMS"),
        };
        // A fake release the real GithubUpdateRequestBuilder resolves into a download request.
        factory.Github = new FakeGithub(
            release: new GitHubRelease("node-v0.9.0", assets), sha256Sums: $"{sha}  {debName}\n");
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        factory.Launcher.Calls.Should().Be(1, "the github channel must trigger the update helper too");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(Web);
        body.GetProperty("status").GetString().Should().Be("started");
        body.GetProperty("via").GetString().Should().Be("github");

        // The launcher must receive a VALIDATED github request resolved from the release.
        var req = factory.Launcher.LastRequest!.GithubRequest;
        req.Should().NotBeNull("the github channel must pass a download request, not trust the caller");
        req!.TargetVersion.Should().Be("0.9.0", "the version comes from the tag, not the filename");
        req.Arch.Should().Be(arch);
        req.DebUrl.Should().Be($"{baseUrl}/{debName}");
        req.Sha256.Should().Be(sha, "the checksum must be the one listed against the asset actually chosen");
    }

    [Fact]
    public async Task Update_on_the_github_channel_declines_409_when_no_release_can_be_resolved()
    {
        WriteConfig(authEnabled: false);
        await using var factory = Factory(InstallChannel.Github);
        // No release available (offline / API error) → the builder yields null → 409, no launch.
        factory.Github = new FakeGithub(release: null, sha256Sums: null);
        using var client = factory.CreateClient();

        var resp = await client.PostAsync("/api/v1/system/update", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        factory.Launcher.Calls.Should().Be(0, "with nothing to install, the helper must not run");
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

        // Optional overrides - when null, the real DI registration is used.
        public ISystemVersionService? Availability { get; set; }
        public IGitHubReleaseClient? Github { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("PACKETNET_CONFIG", configPath);
            Environment.SetEnvironmentVariable("PACKETNET_DB", dbPath);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IInstallChannelProvider>(new StubChannel(channel));
                services.AddSingleton<ISystemUpdateLauncher>(Launcher);
                if (Availability is not null)
                {
                    services.AddSingleton(Availability);
                }
                if (Github is not null)
                {
                    services.AddSingleton(Github);
                }
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
        public SystemUpdateRequest? LastRequest { get; private set; }
        public UpdateLaunchResult Result { get; set; } = UpdateLaunchResult.Started;

        public Task<UpdateLaunchResult> StartUpdateAsync(SystemUpdateRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class StubVersionService(string version, SystemUpdateAvailability availability) : ISystemVersionService
    {
        public string Version => version;
        public SystemUpdateAvailability GetAvailabilitySnapshot() => availability;
    }

    private sealed class FakeGithub(GitHubRelease? release, string? sha256Sums) : IGitHubReleaseClient
    {
        public Task<GitHubRelease?> GetLatestNodeReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(release);

        public Task<string?> GetTextAssetAsync(Uri url, CancellationToken cancellationToken = default) =>
            Task.FromResult(sha256Sums);
    }
}
