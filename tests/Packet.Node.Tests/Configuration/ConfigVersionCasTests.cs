using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Compare-and-swap on the config write seam (review item C065, #694). Every config
/// read-modify-write endpoint rebuilds the WHOLE document from <c>Current</c>, so two
/// overlapping writers silently lost one edit and both got a 200. The provider now compares an
/// expected version INSIDE its write lock.
/// </summary>
[Trait("Category", "Node")]
public sealed class ConfigVersionCasTests : IDisposable
{
    private readonly string dir;
    private readonly string path;

    public ConfigVersionCasTests()
    {
        dir = TestPaths.NewPath("packetnet-cas");
        Directory.CreateDirectory(dir);
        path = Path.Combine(dir, "node.yaml");
        File.WriteAllText(path, NodeConfigYaml.Serialize(Config("M0LTE-1", "IO91wm")));
    }

    private static NodeConfig Config(string callsign, string grid) => new()
    {
        Identity = new Identity { Callsign = callsign, Alias = "LONDON", Grid = grid },
        Management = new ManagementConfig { Telnet = new TelnetConfig { Enabled = false } },
    };

    private FileConfigProvider Provider() => new(path, new FakeTimeProvider(), watch: false);

    [Fact]
    public void The_version_is_a_fingerprint_of_the_document()
    {
        var a = Config("M0LTE-1", "IO91wm");
        var b = Config("M0LTE-1", "IO91wm");
        var c = Config("M0LTE-1", "JO01aa");

        ConfigVersion.Of(a).Should().Be(ConfigVersion.Of(b), "identical documents have the same version");
        ConfigVersion.Of(a).Should().NotBe(ConfigVersion.Of(c));
    }

    [Fact]
    public void An_if_match_header_matches_quoted_weak_and_wildcard_forms()
    {
        const string version = "0123456789abcdef";

        ConfigVersion.Matches(version, version).Should().BeTrue();
        ConfigVersion.Matches($"\"{version}\"", version).Should().BeTrue();
        ConfigVersion.Matches($"W/\"{version}\"", version).Should().BeTrue();
        ConfigVersion.Matches($"\"other\", \"{version}\"", version).Should().BeTrue();
        ConfigVersion.Matches("*", version).Should().BeTrue();
        ConfigVersion.Matches("\"stale\"", version).Should().BeFalse();
        ConfigVersion.Matches(null, version).Should().BeFalse();
    }

    [Fact]
    public void An_apply_on_the_current_version_succeeds_and_advances_it()
    {
        using var provider = Provider();
        var before = provider.CurrentVersion;

        var result = provider.Apply(Config("M0LTE-1", "JO01aa"), before);

        result.Outcome.Should().Be(ConfigApplyOutcome.Applied);
        result.Version.Should().NotBe(before);
        provider.CurrentVersion.Should().Be(result.Version);
        provider.Current.Identity.Grid.Should().Be("JO01aa");
    }

    [Fact]
    public void The_second_of_two_overlapping_writers_is_refused_instead_of_clobbering_the_first()
    {
        using var provider = Provider();

        // Both editors read the same document...
        var baseVersion = provider.CurrentVersion;

        // ... the first lands ...
        provider.Apply(Config("M0LTE-1", "JO01aa"), baseVersion).Outcome
            .Should().Be(ConfigApplyOutcome.Applied);

        // ... and the second, still holding the stale base, is told so.
        var second = provider.Apply(Config("M0LTE-1", "IO92ab"), baseVersion);

        second.Outcome.Should().Be(ConfigApplyOutcome.VersionMismatch);
        second.Version.Should().Be(provider.CurrentVersion, "the 412 carries the version actually in force");
        provider.Current.Identity.Grid.Should().Be("JO01aa", "the first writer's edit survived");
    }

    [Fact]
    public void No_expected_version_is_last_writer_wins_exactly_as_before()
    {
        using var provider = Provider();
        var stale = provider.CurrentVersion;
        provider.Apply(Config("M0LTE-1", "JO01aa"), stale);

        // A client that sends no If-Match keeps the historical behaviour.
        var result = provider.Apply(Config("M0LTE-1", "IO92ab"), expectedVersion: null);

        result.Outcome.Should().Be(ConfigApplyOutcome.Applied);
        provider.Current.Identity.Grid.Should().Be("IO92ab");
    }

    [Fact]
    public void An_invalid_candidate_is_still_a_validation_failure_not_a_version_failure()
    {
        using var provider = Provider();

        var result = provider.Apply(Config("not a callsign!", "IO91wm"), provider.CurrentVersion);

        result.Outcome.Should().Be(ConfigApplyOutcome.Invalid);
        result.Errors.Should().NotBeEmpty();
        provider.Current.Identity.Callsign.Should().Be("M0LTE-1");
    }

    [Fact]
    public void TryApply_still_works_and_is_the_no_cas_path()
    {
        using var provider = Provider();

        provider.TryApply(Config("M0LTE-1", "JO01aa"), out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
        provider.Current.Identity.Grid.Should().Be("JO01aa");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }
}
