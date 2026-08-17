using System.Text.Json;
using Packet.Node.Core.SelfUpdate;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="SystemctlUpdateLauncher"/> stages the per-channel request to the spool dir
/// (<c>/run/packetnet</c>, overridden here via <c>PDN_UPDATE_SPOOL</c>) BEFORE triggering the
/// privileged oneshot - so the root <c>packetnet-update</c> dispatcher can read the runtime-resolved
/// channel (and, for github, the validated download request) that only the running box can determine.
/// We don't run real <c>systemctl</c> here (the host has none, so NotSupported); the assertion is on
/// the spool the launcher writes, which is the load-bearing handoff to the helper.
/// </summary>
[Trait("Category", "Node")]
public sealed class SystemctlUpdateLauncherTests : IDisposable
{
    private readonly string spool;

    public SystemctlUpdateLauncherTests()
    {
        spool = TestPaths.NewPath("pdn-spool");
        Environment.SetEnvironmentVariable("PDN_UPDATE_SPOOL", spool);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PDN_UPDATE_SPOOL", null);
        try { Directory.Delete(spool, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task Writes_the_channel_file_for_a_deb_channel()
    {
        var launcher = new SystemctlUpdateLauncher();
        await launcher.StartUpdateAsync(new SystemUpdateRequest("apt"));

        File.Exists(SystemctlUpdateLauncher.ChannelFile).Should().BeTrue();
        File.ReadAllText(SystemctlUpdateLauncher.ChannelFile).Trim().Should().Be("apt");
        // No github request file on a non-github channel.
        File.Exists(SystemctlUpdateLauncher.GithubRequestFile).Should().BeFalse();
    }

    [Fact]
    public async Task Writes_the_github_request_file_then_the_channel_file()
    {
        var launcher = new SystemctlUpdateLauncher();
        var req = new GithubUpdateRequest("0.9.0", "amd64",
            "https://github.com/packet-net/packet.net/releases/download/node-v0.9.0/packetnet_0.9.0_amd64.deb",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            "http://127.0.0.1:9090/healthz");

        await launcher.StartUpdateAsync(new SystemUpdateRequest("github", req));

        File.ReadAllText(SystemctlUpdateLauncher.ChannelFile).Trim().Should().Be("github");
        File.Exists(SystemctlUpdateLauncher.GithubRequestFile).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(SystemctlUpdateLauncher.GithubRequestFile));
        var root = doc.RootElement;
        root.GetProperty("targetVersion").GetString().Should().Be("0.9.0");
        root.GetProperty("arch").GetString().Should().Be("amd64");
        root.GetProperty("debUrl").GetString().Should().Contain("packetnet_0.9.0_amd64.deb");
        root.GetProperty("sha256").GetString().Should().HaveLength(64);
        // The health URL rides the spool so the root helper gates on the port this node actually
        // serves, instead of the hard-coded :8080 that rolled back good installs (#699 / C101).
        root.GetProperty("healthUrl").GetString().Should().Be("http://127.0.0.1:9090/healthz");
    }

    [Fact]
    public async Task Writes_a_null_health_url_rather_than_omitting_the_field()
    {
        // An older request (or one the node could not resolve a bind for) must still produce the
        // key: the helper reads the spool with sed, and a missing key is what its fallback to
        // http://127.0.0.1:8080/healthz + `systemctl is-active` exists for.
        var launcher = new SystemctlUpdateLauncher();
        var req = new GithubUpdateRequest("0.9.0", "amd64",
            "https://github.com/packet-net/packet.net/releases/download/node-v0.9.0/packetnet_0.9.0_amd64.deb",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");

        await launcher.StartUpdateAsync(new SystemUpdateRequest("github", req));

        using var doc = JsonDocument.Parse(File.ReadAllText(SystemctlUpdateLauncher.GithubRequestFile));
        doc.RootElement.TryGetProperty("healthUrl", out var health).Should().BeTrue();
        health.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Clears_a_stale_github_request_when_switching_to_a_deb_channel()
    {
        Directory.CreateDirectory(spool);
        File.WriteAllText(SystemctlUpdateLauncher.GithubRequestFile, "{\"targetVersion\":\"stale\"}");

        var launcher = new SystemctlUpdateLauncher();
        await launcher.StartUpdateAsync(new SystemUpdateRequest("apt"));

        File.Exists(SystemctlUpdateLauncher.GithubRequestFile).Should().BeFalse(
            "a leftover github request must never be picked up by a non-github helper");
    }
}
