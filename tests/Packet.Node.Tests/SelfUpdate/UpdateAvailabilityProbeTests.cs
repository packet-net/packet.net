using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="ChannelUpdateAvailabilityProbe"/>: the per-channel "is a newer version available?"
/// check that feeds <c>GET /api/v1/system/info</c>. Every external probe (apt-cache via
/// <see cref="IProcessRunner"/>; the GitHub Releases API via <see cref="IGitHubReleaseClient"/>; the
/// feed via <see cref="ISelfContainedFeedClient"/>) is mocked — NO real network/process — so these
/// tests assert the per-channel logic AND the load-bearing guarantee: offline / missing-tool /
/// API-error → no update, never an exception, never a downgrade (incl. the dev-above-release case).
/// </summary>
[Trait("Category", "Node")]
public sealed class UpdateAvailabilityProbeTests
{
    // --- github: latest node-v* release vs running ------------------------------------
    [Fact]
    public async Task Github_offers_a_strictly_newer_release()
    {
        var probe = Probe(InstallChannel.Github, github: new FakeGithub(new GitHubRelease("node-v0.9.0", Empty())));
        var result = await probe.CheckAsync("0.8.0");
        result.UpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be("0.9.0");
    }

    [Fact]
    public async Task Github_reports_up_to_date_for_a_dev_build_above_the_release()
    {
        // The dev-above-release case end-to-end: a 0.1.0+dev… node, latest release node-v0.1.0.
        var probe = Probe(InstallChannel.Github, github: new FakeGithub(new GitHubRelease("node-v0.1.0", Empty())));
        var result = await probe.CheckAsync("0.1.0+dev20260614190517.deadbeef");
        result.UpdateAvailable.Should().BeFalse("a dev build sorts above the matching release — no downgrade");
        result.LatestVersion.Should().BeNull();
    }

    [Fact]
    public async Task Github_reports_no_update_when_the_api_is_unreachable()
    {
        var probe = Probe(InstallChannel.Github, github: new FakeGithub(release: null));
        var result = await probe.CheckAsync("0.8.0");
        result.Should().Be(SystemUpdateAvailability.None);
    }

    // --- apt: apt-cache policy Candidate > Installed -----------------------------------
    [Fact]
    public async Task Apt_offers_an_update_when_the_candidate_is_newer()
    {
        var runner = new FakeRunner
        {
            AptCache = ProcessRunResult.Ran(0, """
                packetnet:
                  Installed: 0.8.0
                  Candidate: 0.9.0
                  Version table:
                     0.9.0 500
                """),
        };
        var probe = Probe(InstallChannel.Apt, runner: runner);
        var result = await probe.CheckAsync("0.8.0");
        result.UpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be("0.9.0");
    }

    [Fact]
    public async Task Apt_reports_up_to_date_when_candidate_equals_installed()
    {
        var runner = new FakeRunner
        {
            AptCache = ProcessRunResult.Ran(0, """
                packetnet:
                  Installed: 0.9.0
                  Candidate: 0.9.0
                """),
        };
        var probe = Probe(InstallChannel.Apt, runner: runner);
        (await probe.CheckAsync("0.9.0")).Should().Be(SystemUpdateAvailability.None);
    }

    [Fact]
    public async Task Apt_reports_no_update_when_apt_cache_is_absent()
    {
        var runner = new FakeRunner { AptCache = ProcessRunResult.NotLaunched };
        var probe = Probe(InstallChannel.Apt, runner: runner);
        (await probe.CheckAsync("0.9.0")).Should().Be(SystemUpdateAvailability.None);
    }

    // --- selfcontained: the feed's latest.json version --------------------------------
    [Fact]
    public async Task SelfContained_offers_the_feed_version_when_newer()
    {
        var probe = Probe(InstallChannel.SelfContained, feed: new FakeFeed("0.9.0"));
        var result = await probe.CheckAsync("0.8.0");
        result.UpdateAvailable.Should().BeTrue();
        result.LatestVersion.Should().Be("0.9.0");
    }

    [Fact]
    public async Task SelfContained_reports_no_update_when_no_feed_is_configured()
    {
        var probe = Probe(InstallChannel.SelfContained, feed: new FakeFeed(null));
        (await probe.CheckAsync("0.8.0")).Should().Be(SystemUpdateAvailability.None);
    }

    // --- unknown never offers an update -----------------------------------------------
    [Fact]
    public async Task Unknown_channel_never_offers_an_update()
    {
        var probe = Probe(InstallChannel.Unknown);
        (await probe.CheckAsync("0.8.0")).Should().Be(SystemUpdateAvailability.None);
    }

    // --- the SHA256SUMS extraction the github Apply request relies on ------------------
    [Theory]
    [InlineData("abc123  packetnet_0.9.0_amd64.deb", "packetnet_0.9.0_amd64.deb")]
    [InlineData("abc123 *packetnet_0.9.0_amd64.deb", "packetnet_0.9.0_amd64.deb")]
    [InlineData("abc123  ./artifacts/packetnet_0.9.0_amd64.deb", "packetnet_0.9.0_amd64.deb")]
    public void ExtractSha_finds_the_named_files_hash(string line, string fileName)
    {
        // Use a real 64-hex sha; the placeholders above are for the name-matching shape only.
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var body = line.Replace("abc123", sha) + "\n" + sha + "  other.deb\n";
        GithubUpdateRequestBuilder.ExtractSha(body, fileName).Should().Be(sha);
    }

    [Fact]
    public void ExtractSha_returns_null_when_the_file_is_absent()
    {
        const string sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        GithubUpdateRequestBuilder.ExtractSha($"{sha}  some-other-file\n", "packetnet_0.9.0_amd64.deb")
            .Should().BeNull();
    }

    // --- harness ----------------------------------------------------------------------
    private static ChannelUpdateAvailabilityProbe Probe(
        InstallChannel channel,
        IProcessRunner? runner = null,
        IGitHubReleaseClient? github = null,
        ISelfContainedFeedClient? feed = null) =>
        new(
            new StubChannel(channel),
            runner ?? new FakeRunner(),
            github ?? new FakeGithub(release: null),
            feed ?? new FakeFeed(null),
            NullLoggerFactory.Instance);

    private static Dictionary<string, Uri> Empty() => new(StringComparer.Ordinal);

    private sealed class StubChannel(InstallChannel channel) : IInstallChannelProvider
    {
        public InstallChannel Channel => channel;
    }

    private sealed class FakeRunner : IProcessRunner
    {
        public ProcessRunResult? AptCache { get; init; }

        public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments) => fileName switch
        {
            "apt-cache" => AptCache ?? ProcessRunResult.NotLaunched,
            _ => throw new InvalidOperationException($"unexpected probe: {fileName}"),
        };
    }

    private sealed class FakeGithub(GitHubRelease? release) : IGitHubReleaseClient
    {
        public Task<GitHubRelease?> GetLatestNodeReleaseAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(release);

        public Task<string?> GetTextAssetAsync(Uri url, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }

    private sealed class FakeFeed(string? version) : ISelfContainedFeedClient
    {
        public Task<string?> GetLatestVersionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(version);
    }
}
