using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Tests.SelfUpdate;

/// <summary>
/// <see cref="RuntimeInstallChannelProvider"/>: the build stamp encodes <c>deb</c> vs
/// <c>selfcontained</c> ONLY, and the apt-vs-github split is resolved at runtime
/// (<c>docs/node-self-update-design.md</c> § Channel detection). Every external probe
/// (<c>dpkg-query</c>, <c>apt-cache</c>) goes through the injected <see cref="IProcessRunner"/>
/// seam — a <see cref="FakeProcessRunner"/> simulates owned/not-owned + repo/no-repo +
/// missing-executable outcomes WITHOUT shelling out for real — so these tests assert the
/// resolution order, the zero-probe guarantee for the self-contained stamp, and the safe
/// fallbacks on a dpkg-less / apt-less host.
/// </summary>
[Trait("Category", "Node")]
public sealed class InstallChannelProviderTests : IDisposable
{
    // A path that stands in for the resolved /proc/self/exe — the dpkg-ownership probe target.
    private const string Binary = "/opt/packetnet/app/Packet.Node";

    private readonly string dir;

    public InstallChannelProviderTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Tests must not inherit a developer's ambient override.
        Environment.SetEnvironmentVariable(RuntimeInstallChannelProvider.EnvOverride, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeInstallChannelProvider.EnvOverride, null);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    // --- the stamp now means deb vs selfcontained ONLY --------------------------------

    [Fact]
    public void Stamp_selfcontained_resolves_SelfContained_with_zero_process_probes()
    {
        var runner = new FakeProcessRunner(); // would throw if any probe ran (none expected)
        var provider = Resolve(stamp: "selfcontained", runner);

        provider.Channel.Should().Be(InstallChannel.SelfContained);
        runner.Calls.Should().BeEmpty("the self-contained stamp must NOT touch dpkg/apt at all");
    }

    [Theory]
    [InlineData("selfcontained")]
    [InlineData("self-contained")]
    [InlineData("  SELFCONTAINED \n")]
    public void Stamp_selfcontained_is_case_and_whitespace_insensitive_and_probe_free(string stamp)
    {
        var runner = new FakeProcessRunner();
        Resolve(stamp, runner).Channel.Should().Be(InstallChannel.SelfContained);
        runner.Calls.Should().BeEmpty();
    }

    // --- stamp deb / absent → dpkg ownership of the running binary ---------------------

    [Fact]
    public void Stamp_deb_owned_with_a_repo_origin_resolves_Apt()
    {
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet: {Binary}\n"),
            AptCache = ProcessRunResult.Ran(0, AptPolicyWithRepo),
        };

        Resolve(stamp: "deb", runner).Channel.Should().Be(InstallChannel.Apt);
        runner.Ran("dpkg-query").Should().BeTrue();
        runner.Ran("apt-cache").Should().BeTrue();
    }

    [Fact]
    public void Stamp_deb_owned_but_no_repo_origin_resolves_Github()
    {
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet: {Binary}\n"),
            AptCache = ProcessRunResult.Ran(0, AptPolicyInstalledOnly),
        };

        Resolve(stamp: "deb", runner).Channel.Should().Be(InstallChannel.Github);
    }

    [Fact]
    public void Stamp_deb_but_binary_not_owned_resolves_SelfContained()
    {
        var runner = new FakeProcessRunner
        {
            // dpkg-query ran but exits 1 with "no path found matching pattern".
            DpkgQuery = ProcessRunResult.Ran(1, $"dpkg-query: no path found matching pattern {Binary}\n"),
        };

        Resolve(stamp: "deb", runner).Channel.Should().Be(InstallChannel.SelfContained);
        runner.Ran("apt-cache").Should().BeFalse("not-owned must short-circuit before the apt probe");
    }

    [Fact]
    public void Stamp_deb_owned_by_a_DIFFERENT_package_resolves_SelfContained()
    {
        var runner = new FakeProcessRunner
        {
            // Some other package claims the path — we must require OUR package, not any.
            DpkgQuery = ProcessRunResult.Ran(0, $"some-other-pkg: {Binary}\n"),
        };

        Resolve(stamp: "deb", runner).Channel.Should().Be(InstallChannel.SelfContained);
    }

    [Fact]
    public void An_absent_stamp_is_treated_like_deb_and_probes_dpkg()
    {
        // No marker file at all → fall to the dpkg probe (NOT straight to Unknown).
        var runner = new FakeProcessRunner
        {
            DpkgQuery = ProcessRunResult.Ran(0, $"packetnet:arm64: {Binary}\n"), // arch-qualified owner
            AptCache = ProcessRunResult.Ran(0, AptPolicyWithRepo),
        };

        var missing = Path.Combine(dir, "does-not-exist");
        new RuntimeInstallChannelProvider(missing, runner, Binary).Channel.Should().Be(InstallChannel.Apt);
        runner.Ran("dpkg-query").Should().BeTrue();
    }

    // --- the missing-executable safe fallbacks (non-Debian / dpkg-less / apt-less host) -

    [Fact]
    public void dpkg_query_absent_resolves_SelfContained()
    {
        var runner = new FakeProcessRunner
        {
            // The executable could not be launched (not on PATH) → the safe-fallback signal.
            DpkgQuery = ProcessRunResult.NotLaunched,
        };

        Resolve(stamp: "deb", runner).Channel.Should().Be(InstallChannel.SelfContained);
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

        Resolve(stamp: "deb", runner).Channel.Should().Be(InstallChannel.Github);
    }

    // --- the PDN_INSTALL_CHANNEL override --------------------------------------------

    [Theory]
    [InlineData("apt", InstallChannel.Apt)]
    [InlineData("github", InstallChannel.Github)]
    [InlineData("selfcontained", InstallChannel.SelfContained)]
    [InlineData("self-contained", InstallChannel.SelfContained)]
    [InlineData("unknown", InstallChannel.Unknown)]
    [InlineData("  GITHUB \n", InstallChannel.Github)]
    [InlineData("nonsense", InstallChannel.Unknown)]
    public void The_env_override_wins_and_probes_nothing(string token, InstallChannel expected)
    {
        Environment.SetEnvironmentVariable(RuntimeInstallChannelProvider.EnvOverride, token);
        var runner = new FakeProcessRunner(); // would throw if any probe ran (none expected)

        // Even a `deb` marker present on disk must not override the env.
        var marker = Path.Combine(dir, "install-channel");
        File.WriteAllText(marker, "deb");

        new RuntimeInstallChannelProvider(marker, runner, Binary).Channel.Should().Be(expected);
        runner.Calls.Should().BeEmpty("the override short-circuits all detection");
    }

    [Fact]
    public void ParseOverride_maps_the_full_channel_set()
    {
        RuntimeInstallChannelProvider.ParseOverride("apt").Should().Be(InstallChannel.Apt);
        RuntimeInstallChannelProvider.ParseOverride("github").Should().Be(InstallChannel.Github);
        RuntimeInstallChannelProvider.ParseOverride("selfcontained").Should().Be(InstallChannel.SelfContained);
        RuntimeInstallChannelProvider.ParseOverride("unknown").Should().Be(InstallChannel.Unknown);
        RuntimeInstallChannelProvider.ParseOverride("rpm").Should().Be(InstallChannel.Unknown);
    }

    // --- harness ----------------------------------------------------------------------

    private RuntimeInstallChannelProvider Resolve(string stamp, IProcessRunner runner)
    {
        var marker = Path.Combine(dir, "install-channel");
        File.WriteAllText(marker, stamp);
        return new RuntimeInstallChannelProvider(marker, runner, Binary);
    }

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

    // A `apt-cache policy` table for a dpkg -i'd package with NO repo — only the dpkg status.
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
    /// so a test asserting "zero probes" (the self-contained stamp / the override) is enforced
    /// by construction. Set <see cref="DpkgQuery"/> / <see cref="AptCache"/> to opt a probe in.
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
