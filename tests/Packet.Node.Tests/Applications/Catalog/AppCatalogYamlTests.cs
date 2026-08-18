using System.Text.RegularExpressions;
using Packet.Node.Core.Applications.Catalog;

namespace Packet.Node.Tests.Applications.Catalog;

public partial class AppCatalogYamlTests
{
    private static readonly string GoodSha = new('a', 64);

    // The three runtime ids every shipped app must publish an artifact for.
    private static readonly string[] AllRids = ["linux-x64", "linux-arm64", "linux-arm"];

    // A "semver-ish" version pin: digits-and-dots, e.g. "0.34.2" or "1.0". Deliberately loose -
    // we assert the SHAPE of a pin, not the value, so a routine bump never fails this test.
    [GeneratedRegex(@"^[0-9]+(\.[0-9]+)*$")]
    private static partial Regex SemverIshRegex();

    // Exactly 64 lowercase hex chars - the catalog pin spelling.
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256HexRegex();

    /// <summary>
    /// The real shipped catalog parses, carries all three artifact KINDs and the vetted set, and
    /// every pin is VALID - but this test pins no version, sha, or url VALUE. A routine
    /// version/sha bump (the thing that broke this test twice on #475) keeps it green; only a
    /// structural regression (a missing app/kind/rid, a malformed pin) turns it red. Exact-value
    /// coverage lives in <see cref="The_real_catalog_validates_clean"/> against the live file.
    /// </summary>
    [Fact]
    public void Parses_the_real_shipped_catalog_with_all_three_kinds_and_valid_pins()
    {
        var doc = AppCatalogYaml.Parse(CatalogTestSupport.RealCatalogYaml());

        doc.Catalog.Should().Be(1);

        // The vetted set (docs/app-catalog.md) - by id, version-agnostic.
        doc.Apps.Select(a => a.Id).Should().Contain(["dapps", "bpqchat", "convers", "bbs"]);

        // dapps is the assets-kind flagship; bbs/bpqchat/convers are deb-kind - so the shipped
        // catalog exercises both real kinds. (pdnapp's parse is covered by a dedicated test.)
        var dapps = doc.Apps.Single(a => a.Id == "dapps");
        dapps.Artifact!.Kind.Should().Be(ArtifactKind.Assets);
        dapps.Name.Should().Be("DAPPS");
        // The catalog ships the transport-accurate `packet` spelling (the rename from `network`).
        dapps.Capabilities.Should().Contain(["packet", "web"]);
        dapps.Artifact.Assets!.Binaries["linux-x64"].Dest.Should().Be("dapps");

        foreach (var id in new[] { "bpqchat", "convers", "bbs" })
        {
            doc.Apps.Single(a => a.Id == id).Artifact!.Kind.Should().Be(ArtifactKind.Deb);
        }

        // Every entry: a valid semver-ish version + a structurally sound, all-RID, well-formed,
        // sha-pinned artifact whose deb/binary urls embed the app id + the entry's version.
        foreach (var app in doc.Apps)
        {
            app.Version.Should().NotBeNullOrWhiteSpace($"'{app.Id}' must pin a version");
            app.Version.Should().MatchRegex(SemverIshRegex(), $"'{app.Id}' version must be semver-ish");

            AssertArtifactPinsValid(app);
        }
    }

    /// <summary>Structure + pin-VALIDITY assertions for one catalog entry, version/sha-agnostic:
    /// every RID is present, every url is https and embeds the id + version, every sha256 is
    /// 64-char lowercase hex.</summary>
    private static void AssertArtifactPinsValid(AppCatalogEntry app)
    {
        var artifact = app.Artifact!;
        switch (artifact.Kind)
        {
            case ArtifactKind.Assets:
                var assets = artifact.Assets!;
                AssertSha(app.Id, "manifest", assets.Manifest.Sha256);
                assets.Manifest.Url.Should().StartWith("https://");
                assets.Binaries.Keys.Should().Contain(AllRids, $"'{app.Id}' must publish every RID");
                foreach (var (rid, bin) in assets.Binaries)
                {
                    AssertUrl(app.Id, rid, bin.Url, app.Version!);
                    AssertSha(app.Id, rid, bin.Sha256);
                    bin.Dest.Should().NotBeNullOrWhiteSpace($"'{app.Id}/{rid}' must name a dest");
                }
                break;

            case ArtifactKind.Deb:
                var debs = artifact.Deb!.Debs;
                debs.Keys.Should().Contain(AllRids, $"'{app.Id}' must publish every RID");
                foreach (var (rid, @ref) in debs)
                {
                    AssertUrl(app.Id, rid, @ref.Url, app.Version!);
                    AssertSha(app.Id, rid, @ref.Sha256);
                }
                break;

            default:
                throw new Xunit.Sdk.XunitException(
                    $"'{app.Id}' has unexpected artifact kind {artifact.Kind} in the shipped catalog.");
        }
    }

    private static void AssertUrl(string id, string rid, string url, string version)
    {
        url.Should().StartWith("https://", $"'{id}/{rid}' url must be https");
        Uri.TryCreate(url, UriKind.Absolute, out _).Should().BeTrue($"'{id}/{rid}' url must be well-formed");
        url.Should().Contain(id, $"'{id}/{rid}' url should embed the app id");
        url.Should().Contain(version, $"'{id}/{rid}' url should embed the pinned version");
    }

    private static void AssertSha(string id, string rid, string sha) =>
        sha.Should().MatchRegex(Sha256HexRegex(), $"'{id}/{rid}' sha256 must be 64-char lowercase hex");

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
