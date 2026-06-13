using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="FileInstallChannelProvider"/>: resolves the install channel from the
/// build-stamped marker file (with a <c>PDN_INSTALL_CHANNEL</c> env override), and fails
/// safe to <see cref="InstallChannel.Unknown"/> when neither is present/recognised.
/// </summary>
[Trait("Category", "Node")]
public sealed class InstallChannelProviderTests : IDisposable
{
    private readonly string dir;

    public InstallChannelProviderTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Tests must not inherit a developer's ambient override.
        Environment.SetEnvironmentVariable(FileInstallChannelProvider.EnvOverride, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FileInstallChannelProvider.EnvOverride, null);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Theory]
    [InlineData("apt", InstallChannel.Apt)]
    [InlineData("apt\n", InstallChannel.Apt)]
    [InlineData("  APT  ", InstallChannel.Apt)]
    [InlineData("selfcontained", InstallChannel.SelfContained)]
    [InlineData("self-contained", InstallChannel.SelfContained)]
    [InlineData("nonsense", InstallChannel.Unknown)]
    [InlineData("", InstallChannel.Unknown)]
    public void Parse_maps_the_token_case_and_whitespace_insensitively(string token, InstallChannel expected) =>
        FileInstallChannelProvider.Parse(token).Should().Be(expected);

    [Fact]
    public void Reads_the_marker_file_when_present()
    {
        var marker = Path.Combine(dir, "install-channel");
        File.WriteAllText(marker, "apt\n");

        new FileInstallChannelProvider(marker).Channel.Should().Be(InstallChannel.Apt);

        File.WriteAllText(marker, "selfcontained");
        new FileInstallChannelProvider(marker).Channel.Should().Be(InstallChannel.SelfContained);
    }

    [Fact]
    public void An_absent_marker_is_Unknown_not_a_crash()
    {
        var missing = Path.Combine(dir, "does-not-exist");
        new FileInstallChannelProvider(missing).Channel.Should().Be(InstallChannel.Unknown);
    }

    [Fact]
    public void The_env_override_wins_over_the_marker_file()
    {
        var marker = Path.Combine(dir, "install-channel");
        File.WriteAllText(marker, "apt");
        Environment.SetEnvironmentVariable(FileInstallChannelProvider.EnvOverride, "selfcontained");

        new FileInstallChannelProvider(marker).Channel.Should().Be(InstallChannel.SelfContained);
    }

    [Fact]
    public void An_unrecognised_marker_is_Unknown()
    {
        var marker = Path.Combine(dir, "install-channel");
        File.WriteAllText(marker, "rpm");
        new FileInstallChannelProvider(marker).Channel.Should().Be(InstallChannel.Unknown);
    }
}
