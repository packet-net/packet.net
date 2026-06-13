using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Catalog;

namespace Packet.Node.Tests.Applications.Catalog;

public class EmbeddedAppCatalogTests
{
    [Fact]
    public void Reads_the_real_shipped_catalog()
    {
        var path = Path.Combine(CatalogTestSupport.RepoRoot(), "catalog", "apps.yaml");
        var catalog = new EmbeddedAppCatalog(NullLoggerFactory.Instance, path);

        var apps = catalog.List();

        apps.Should().HaveCount(3);
        apps.Select(a => a.Id).Should().BeEquivalentTo("dapps", "bpqchat", "convers");
    }

    [Fact]
    public void Missing_file_yields_an_empty_list_not_a_throw()
    {
        var catalog = new EmbeddedAppCatalog(NullLoggerFactory.Instance,
            Path.Combine(Path.GetTempPath(), $"no-such-catalog-{Guid.NewGuid():N}.yaml"));

        catalog.List().Should().BeEmpty();
    }

    [Fact]
    public void Drops_invalid_entries_and_keeps_valid_ones()
    {
        using var dir = new TempDir("catalog-read");
        var sha = new string('a', 64);
        var path = dir.Combine("apps.yaml");
        File.WriteAllText(path, $"""
            catalog: 1
            apps:
              - id: good
                version: "1.0.0"
                artifact:
                  kind: pdnapp
                  pdnapp:
                    url: https://example.test/good.pdnapp
                    sha256: {sha}
              - id: bad
                version: "1.0.0"
                artifact:
                  kind: pdnapp
                  pdnapp:
                    url: http://example.test/insecure.pdnapp
                    sha256: {sha}
            """);

        var catalog = new EmbeddedAppCatalog(NullLoggerFactory.Instance, path);

        var apps = catalog.List();

        apps.Should().ContainSingle();
        apps[0].Id.Should().Be("good");
    }

    [Fact]
    public void Unparseable_file_yields_an_empty_list()
    {
        using var dir = new TempDir("catalog-bad");
        var path = dir.Combine("apps.yaml");
        File.WriteAllText(path, "apps: [unclosed");

        new EmbeddedAppCatalog(NullLoggerFactory.Instance, path).List().Should().BeEmpty();
    }
}

public class RuntimeIdsTests
{
    [Fact]
    public void Current_returns_one_of_the_three_expected_rids()
    {
        RuntimeIds.Current().Should().BeOneOf(
            RuntimeIds.LinuxX64, RuntimeIds.LinuxArm64, RuntimeIds.LinuxArm);
    }
}
