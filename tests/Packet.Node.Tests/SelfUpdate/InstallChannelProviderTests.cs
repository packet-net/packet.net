using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="RuntimeInstallChannelProvider"/>: there is no build stamp - the channel is
/// resolved entirely at runtime from what's on the box (<c>docs/node-self-update-design.md</c>
/// § Channel detection). Every external probe (<c>dpkg-query</c>, <c>apt-cache</c>) goes
/// through the injected <see cref="IProcessRunner"/> seam - a <see cref="FakeProcessRunner"/>
/// simulates owned/not-owned + repo/no-repo + missing-executable outcomes WITHOUT shelling
/// out for real - so these tests assert the resolution order, that anything dpkg doesn't own
/// lands on <see cref="InstallChannel.Unknown"/> (an unpacked release archive, a container,
/// a source build), and the safe fallbacks on a dpkg-less / apt-less host.
/// </summary>
[Trait("Category", "Node")]
public sealed class InstallChannelProviderTests : IDisposable
{
    // A path that stands in for the resolved /proc/self/exe - the dpkg-ownership probe target.
    private const string Binary = "/opt/packetnet/app/Packet.Node";

    public InstallChannelProviderTests() =>
        // Tests must not inherit a developer's ambient override.
        Environment.SetEnvironmentVariable(RuntimeInstallChannelProvider.EnvOverride, null);

    public void Dispose() =>
        Environment.SetEnvironmentVariable(RuntimeInstallChannelProvider.EnvOverride, null);

    // --- dpkg ownership of the running binary, then apt's upgrade source ---------------

    [Fact]
    public void Owned_with_a_repo_origin_resolves_Apt()
    {
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet: {Binary}\n"),
            AptCache = ProcessRunResult.Ran(0, AptPolicyWithRepo),
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Apt);
        runner.Ran("dpkg-query").Should().BeTrue();
        runner.Ran("apt-cache").Should().BeTrue();
    }

    [Fact]
    public void Owned_but_no_repo_origin_resolves_Github()
    {
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet: {Binary}\n"),
            AptCache = ProcessRunResult.Ran(0, AptPolicyInstalledOnly),
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Github);
    }

    [Fact]
    public void An_arch_qualified_owner_still_counts_as_ours()
    {
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet:arm64: {Binary}\n"),
            AptCache = ProcessRunResult.Ran(0, AptPolicyWithRepo),
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Apt);
    }

    // --- nothing owns it → Unknown (the archive / container / source-build case) --------

    [Fact]
    public void Binary_not_owned_resolves_Unknown()
    {
        var runner = new FakeProcessRunner
        {
            // dpkg-query ran but exits 1 with "no path found matching pattern".
            DpkgQuery = ProcessRunResult.Ran(1, $"dpkg-query: no path found matching pattern {Binary}\n"),
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Unknown);
        runner.Ran("apt-cache").Should().BeFalse("not-owned must short-circuit before the apt probe");
    }

    [Fact]
    public void Owned_by_a_DIFFERENT_package_resolves_Unknown()
    {
        var runner = new FakeProcessRunner
        {
            // Some other package claims the path - we must require OUR package, not any.
            DpkgQuery = ProcessRunResult.Ran(0, $"some-other-pkg: {Binary}\n"),
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Unknown);
    }

    // --- the missing-executable safe fallbacks (non-Debian / dpkg-less / apt-less host) -

    [Fact]
    public void dpkg_query_absent_resolves_Unknown()
    {
        var runner = new FakeProcessRunner
        {
            // The executable could not be launched (not on PATH) → the safe-fallback signal.
            DpkgQuery = ProcessRunResult.NotLaunched,
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Unknown);
        runner.Ran("apt-cache").Should().BeFalse("a dpkg-less host never reaches the apt probe");
    }

    [Fact]
    public void apt_cache_absent_resolves_Github()
    {
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet: {Binary}\n"),
            // dpkg owns us, but apt-cache isn't installed → conservative fall to Github.
            AptCache = ProcessRunResult.NotLaunched,
        };

        Resolve(runner).Channel.Should().Be(InstallChannel.Github);
    }

    // --- the PDN_INSTALL_CHANNEL override --------------------------------------------

    [Theory]
    [InlineData("apt", InstallChannel.Apt)]
    [InlineData("github", InstallChannel.Github)]
    [InlineData("unknown", InstallChannel.Unknown)]
    [InlineData("  GITHUB \n", InstallChannel.Github)]
    [InlineData("nonsense", InstallChannel.Unknown)]
    public void The_env_override_wins_and_probes_nothing(string token, InstallChannel expected)
    {
        Environment.SetEnvironmentVariable(RuntimeInstallChannelProvider.EnvOverride, token);
        var runner = new FakeProcessRunner(); // would throw if any probe ran (none expected)

        new RuntimeInstallChannelProvider(runner, Binary).Channel.Should().Be(expected);
        runner.Calls.Should().BeEmpty("the override short-circuits all detection");
    }

    [Fact]
    public void ParseOverride_maps_the_full_channel_set()
    {
        RuntimeInstallChannelProvider.ParseOverride("apt").Should().Be(InstallChannel.Apt);
        RuntimeInstallChannelProvider.ParseOverride("github").Should().Be(InstallChannel.Github);
        RuntimeInstallChannelProvider.ParseOverride("unknown").Should().Be(InstallChannel.Unknown);
        // The withdrawn self-contained channel is no longer a channel - it must not resurrect.
        RuntimeInstallChannelProvider.ParseOverride("selfcontained").Should().Be(InstallChannel.Unknown);
        RuntimeInstallChannelProvider.ParseOverride("rpm").Should().Be(InstallChannel.Unknown);
    }

    // --- harness ----------------------------------------------------------------------

    private static RuntimeInstallChannelProvider Resolve(IProcessRunner runner) =>
        new(runner, Binary);

    // A real `apt-cache policy packetnet` table for a package available from an http repo.
    private const string AptPolicyWithRepo = """
        packetnet:
          Installed: 0.9.0
          Candidate: 0.9.0
          Version table:
         *** 0.9.0 500
                500 https://repo.oarc.uk/debian bookworm/main arm64 Packages
                100 /var/lib/dpkg/status
        """;

    // A `apt-cache policy` table for a dpkg -i'd package with NO repo - only the dpkg status.
    private const string AptPolicyInstalledOnly = """
        packetnet:
          Installed: 0.9.0
          Candidate: 0.9.0
          Version table:
         *** 0.9.0 100
                100 /var/lib/dpkg/status
        """;

    /// <summary>
    /// A fake <see cref="IProcessRunner"/> that returns canned results per probe and records
    /// which executables were invoked. By default every probe is configured to throw if run,
    /// so a test asserting "zero probes" (the override) is enforced by construction. Set
    /// <see cref="DpkgQuery"/> / <see cref="AptCache"/> to opt a probe in.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        public List<string> Calls { get; } = new();

        public ProcessRunResult? DpkgQuery { get; init; }
        public ProcessRunResult? AptCache { get; init; }

        public bool Ran(string exe) => Calls.Contains(exe);

        public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add(fileName);
            return fileName switch
            {
                "dpkg-query" => DpkgQuery ?? throw new InvalidOperationException("unexpected dpkg-query probe"),
                "apt-cache" => AptCache ?? throw new InvalidOperationException("unexpected apt-cache probe"),
                _ => throw new InvalidOperationException($"unexpected probe: {fileName}"),
            };
        }
    }
}
