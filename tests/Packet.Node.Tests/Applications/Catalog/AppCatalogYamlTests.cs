using Packet.Node.Core.Applications.Catalog;

namespace Packet.Node.Tests.Applications.Catalog;

public class AppCatalogYamlTests
{
    private static readonly string GoodSha = new('a', 64);

    [Fact]
    public void Parses_the_real_shipped_catalog_with_all_three_kinds_and_pins()
    {
        var doc = AppCatalogYaml.Parse(CatalogTestSupport.RealCatalogYaml());

        doc.Catalog.Should().Be(1);
        doc.Apps.Should().HaveCount(4);

        // dapps — assets kind.
        var dapps = doc.Apps.Single(a => a.Id == "dapps");
        dapps.Name.Should().Be("DAPPS");
        dapps.Version.Should().Be("0.34.2");
        // The catalog ships the transport-accurate `packet` spelling (the rename from `network`).
        dapps.Capabilities.Should().Contain(["packet", "web"]);
        dapps.Artifact!.Kind.Should().Be(ArtifactKind.Assets);
        dapps.Artifact.Assets.Should().NotBeNull();
        dapps.Artifact.Assets!.Manifest.Sha256.Should()
            .Be("80bab3ad2f6b761149a6ac62386d0c66bd27c1e265e10f8111268aea4c90b2ad");
        dapps.Artifact.Assets.Binaries.Should().ContainKeys("linux-x64", "linux-arm64", "linux-arm");
        var x64 = dapps.Artifact.Assets.Binaries["linux-x64"];
        x64.Dest.Should().Be("dapps");
        x64.Mode.Should().Be("0755");
        x64.Sha256.Should().Be("d8dab9f1f48eb2194c80c4318cd6a4706627b8fca730b2d430b35e1cd69ba0ec");
        x64.Url.Should().StartWith("https://");

        // bpqchat + convers — deb kind.
        var bpqchat = doc.Apps.Single(a => a.Id == "bpqchat");
        bpqchat.Artifact!.Kind.Should().Be(ArtifactKind.Deb);
        bpqchat.Artifact.Deb!.Debs.Should().ContainKeys("linux-x64", "linux-arm64", "linux-arm");
        bpqchat.Version.Should().Be("0.1.1");
        bpqchat.Artifact.Deb.Debs["linux-arm64"].Sha256.Should()
            .Be("721462a23ff66f35dbbcaa6acf8f1784fbeb64453171b556a887e98f5dc88e61");

        var convers = doc.Apps.Single(a => a.Id == "convers");
        convers.Artifact!.Kind.Should().Be(ArtifactKind.Deb);
        convers.Version.Should().Be("0.1.3");

        var bbs = doc.Apps.Single(a => a.Id == "bbs");
        bbs.Artifact!.Kind.Should().Be(ArtifactKind.Deb);
        bbs.Version.Should().Be("0.2.34");
        bbs.Artifact.Deb!.Debs.Should().ContainKeys("linux-x64", "linux-arm64", "linux-arm");
    }

    [Fact]
    public void The_real_catalog_validates_clean()
    {
        var doc = AppCatalogYaml.Parse(CatalogTestSupport.RealCatalogYaml());
        foreach (var entry in doc.Apps)
        {
            AppCatalogYaml.Validate(entry).Should().BeEmpty($"'{entry.Id}' should be valid");
        }
    }

    [Fact]
    public void Parses_a_pdnapp_kind_with_a_single_tarball_and_variants()
    {
        var doc = AppCatalogYaml.Parse($"""
            catalog: 1
            apps:
              - id: demo
                version: "1.0.0"
                artifact:
                  kind: pdnapp
                  pdnapp:
                    url: https://example.test/demo.pdnapp
                    sha256: {GoodSha}
                  variants:
                    linux-arm64:
                      url: https://example.test/demo-arm64.pdnapp
                      sha256: {GoodSha}
            """);

        var demo = doc.Apps.Single();
        demo.Artifact!.Kind.Should().Be(ArtifactKind.Pdnapp);
        demo.Artifact.Pdnapp!.Pdnapp!.Url.Should().Be("https://example.test/demo.pdnapp");
        demo.Artifact.Pdnapp.Variants!.Should().ContainKey("linux-arm64");
        AppCatalogYaml.Validate(demo).Should().BeEmpty();
    }

    [Fact]
    public void Empty_document_throws_a_descriptive_exception()
    {
        var act = () => AppCatalogYaml.Parse("# just a comment\n");
        act.Should().Throw<InvalidDataException>().WithMessage("*empty*");
    }

    [Fact]
    public void Malformed_yaml_throws_a_descriptive_exception()
    {
        var act = () => AppCatalogYaml.Parse("apps: [unclosed");
        act.Should().Throw<InvalidDataException>().WithMessage("*not a valid catalog*");
    }

    [Fact]
    public void An_unknown_artifact_kind_throws_naming_the_closed_set()
    {
        var act = () => AppCatalogYaml.Parse("""
            catalog: 1
            apps:
              - id: x
                version: "1"
                artifact:
                  kind: flatpak
            """);

        act.Should().Throw<InvalidDataException>()
            .WithMessage("*flatpak*")
            .WithMessage("*assets*");
    }

    [Fact]
    public void Validate_flags_a_bad_sha256()
    {
        var entry = AssetsEntry(sha: "deadbeef");  // too short
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*sha256*");
    }

    [Fact]
    public void Validate_flags_an_uppercase_sha256()
    {
        var entry = AssetsEntry(sha: new string('A', 64));  // uppercase hex rejected
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*sha256*");
    }

    [Fact]
    public void Validate_flags_a_non_https_url()
    {
        var entry = AssetsEntry(url: "http://example.test/bin");
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*https*");
    }

    [Fact]
    public void Validate_flags_a_missing_kind_sub_object()
    {
        var entry = new AppCatalogEntry
        {
            Id = "x",
            Version = "1",
            Artifact = new ArtifactSpec { Kind = ArtifactKind.Deb, Deb = null },
        };
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*artifact.debs*required*");
    }

    [Fact]
    public void Validate_flags_a_bad_id()
    {
        var entry = AssetsEntry(id: "Bad_Id");
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*id*lowercase*");
    }

    [Fact]
    public void Validate_flags_a_missing_dest_on_an_assets_binary()
    {
        var entry = new AppCatalogEntry
        {
            Id = "x",
            Version = "1",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Assets,
                Assets = new AssetsArtifact
                {
                    Manifest = new ArtifactRef { Url = "https://e.test/m", Sha256 = GoodSha },
                    Binaries = new Dictionary<string, BinaryRef>
                    {
                        ["linux-x64"] = new() { Url = "https://e.test/b", Sha256 = GoodSha, Dest = "" },
                    },
                },
            },
        };
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*dest*required*");
    }

    [Fact]
    public void Validate_flags_a_missing_version()
    {
        var entry = AssetsEntry(version: "");
        AppCatalogYaml.Validate(entry).Should().ContainMatch("*version*required*");
    }

    private static AppCatalogEntry AssetsEntry(
        string id = "x",
        string version = "1.0.0",
        string url = "https://example.test/bin",
        string? sha = null) => new()
        {
            Id = id,
            Version = version,
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Assets,
                Assets = new AssetsArtifact
                {
                    Manifest = new ArtifactRef { Url = "https://example.test/m", Sha256 = GoodSha },
                    Binaries = new Dictionary<string, BinaryRef>
                    {
                        ["linux-x64"] = new() { Url = url, Sha256 = sha ?? GoodSha, Dest = "bin", Mode = "0755" },
                    },
                },
            },
        };
}
