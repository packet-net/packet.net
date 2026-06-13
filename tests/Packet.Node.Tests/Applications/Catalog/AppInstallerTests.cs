using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Applications.Catalog;

namespace Packet.Node.Tests.Applications.Catalog;

public class AppInstallerTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 13, 12, 0, 0, TimeSpan.Zero);

    // ---- assets kind -----------------------------------------------------------------------

    [Fact]
    public async Task Assets_install_places_manifest_and_binary_at_dest_with_mode_and_records_a_marker()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var manifest = CatalogTestSupport.ManifestYaml("dapps", "0.34.1");
        var manifestFixture = CatalogTestSupport.WriteFixture(fix, "pdn-app.yaml", manifest);
        var binFixture = CatalogTestSupport.WriteFixture(fix, "dapps-bin", "#!/bin/sh\necho hi\n");

        var manifestUrl = "https://example.test/pdn-app.yaml";
        var binUrl = "https://example.test/dapps-linux-x64";
        var fetcher = new FakeArtifactFetcher()
            .Add(manifestUrl, manifestFixture)
            .Add(binUrl, binFixture);

        var entry = new AppCatalogEntry
        {
            Id = "dapps",
            Version = "0.34.1",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Assets,
                Assets = new AssetsArtifact
                {
                    Manifest = new ArtifactRef { Url = manifestUrl, Sha256 = CatalogTestSupport.Sha256Hex(manifestFixture) },
                    Binaries = new Dictionary<string, BinaryRef>
                    {
                        ["linux-x64"] = new()
                        {
                            Url = binUrl,
                            Sha256 = CatalogTestSupport.Sha256Hex(binFixture),
                            Dest = "dapps",
                            Mode = "0755",
                        },
                    },
                },
            },
        };

        var installer = NewInstaller(fetcher, apps.Path);
        var outcome = await installer.InstallFromCatalogAsync(entry, "linux-x64", default);

        outcome.Ok.Should().BeTrue(outcome.Error);
        outcome.Version.Should().Be("0.34.1");

        var dir = Path.Combine(apps.Path, "dapps");
        File.Exists(Path.Combine(dir, "pdn-app.yaml")).Should().BeTrue();
        var placedBin = Path.Combine(dir, "dapps");
        File.Exists(placedBin).Should().BeTrue();
        if (!OperatingSystem.IsWindows())
        {
            ((int)File.GetUnixFileMode(placedBin) & 0b111_111_111)
                .Should().Be(Convert.ToInt32("0755", 8));
        }

        var marker = ReadMarker(dir);
        marker.Id.Should().Be("dapps");
        marker.Source.Should().Be("catalog");
        marker.Kind.Should().Be("assets");
        marker.Version.Should().Be("0.34.1");
        marker.InstalledUtc.Should().Be(Now);
        marker.Payload.Should().BeEquivalentTo(["pdn-app.yaml", "dapps"]);
        marker.Sha256s.Should().ContainKeys("manifest", "binary");
    }

    [Fact]
    public async Task A_sha_mismatch_is_refused_and_nothing_is_staged()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var manifestFixture = CatalogTestSupport.WriteFixture(fix, "pdn-app.yaml", CatalogTestSupport.ManifestYaml("dapps", "0.34.1"));
        var binFixture = CatalogTestSupport.WriteFixture(fix, "dapps-bin", "binary-bytes");

        var fetcher = new FakeArtifactFetcher()
            .Add("https://example.test/m", manifestFixture)
            .Add("https://example.test/b", binFixture);

        var entry = new AppCatalogEntry
        {
            Id = "dapps",
            Version = "0.34.1",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Assets,
                Assets = new AssetsArtifact
                {
                    Manifest = new ArtifactRef { Url = "https://example.test/m", Sha256 = CatalogTestSupport.Sha256Hex(manifestFixture) },
                    Binaries = new Dictionary<string, BinaryRef>
                    {
                        // Deliberately wrong (but well-formed) sha for the binary.
                        ["linux-x64"] = new() { Url = "https://example.test/b", Sha256 = new string('0', 64), Dest = "dapps", Mode = "0755" },
                    },
                },
            },
        };

        var installer = NewInstaller(fetcher, apps.Path);
        var outcome = await installer.InstallFromCatalogAsync(entry, "linux-x64", default);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("sha256 mismatch");
        Directory.Exists(Path.Combine(apps.Path, "dapps")).Should().BeFalse("nothing should be staged on mismatch");
    }

    // ---- deb kind (real dpkg-deb) ----------------------------------------------------------

    [SkippableFact]
    public async Task Deb_install_extracts_the_app_subtree_and_records_a_marker()
    {
        Skip.IfNot(DpkgDebAvailable(), "dpkg-deb is not on PATH");

        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var debPath = BuildFixtureDeb(fix, id: "bpqchat", version: "0.1.0", arch: "amd64");
        var debUrl = "https://example.test/pdn-bpqchat_0.1.0_amd64.deb";
        var fetcher = new FakeArtifactFetcher().Add(debUrl, debPath);

        var entry = new AppCatalogEntry
        {
            Id = "bpqchat",
            Version = "0.1.0",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Deb,
                Deb = new DebArtifact
                {
                    Debs = new Dictionary<string, ArtifactRef>
                    {
                        ["linux-x64"] = new() { Url = debUrl, Sha256 = CatalogTestSupport.Sha256Hex(debPath) },
                    },
                },
            },
        };

        var installer = NewInstaller(fetcher, apps.Path);
        var outcome = await installer.InstallFromCatalogAsync(entry, "linux-x64", default);

        outcome.Ok.Should().BeTrue(outcome.Error);

        var dir = Path.Combine(apps.Path, "bpqchat");
        File.Exists(Path.Combine(dir, "pdn-app.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "bpqchat")).Should().BeTrue("the binary from the deb subtree landed");

        var marker = ReadMarker(dir);
        marker.Kind.Should().Be("deb");
        marker.Sha256s.Should().ContainKey("deb");
        marker.Payload.Should().Contain("pdn-app.yaml");
    }

    // ---- pdnapp / upload kind --------------------------------------------------------------

    [Fact]
    public async Task Pdnapp_install_unpacks_the_tarball()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var pdnapp = CatalogTestSupport.BuildPdnapp(fix, "demo.pdnapp", new Dictionary<string, string>
        {
            ["pdn-app.yaml"] = CatalogTestSupport.ManifestYaml("demo", "2.0.0"),
            ["extra.txt"] = "hello",
        });
        var url = "https://example.test/demo.pdnapp";
        var fetcher = new FakeArtifactFetcher().Add(url, pdnapp);

        var entry = new AppCatalogEntry
        {
            Id = "demo",
            Version = "2.0.0",
            Artifact = new ArtifactSpec
            {
                Kind = ArtifactKind.Pdnapp,
                Pdnapp = new PdnappArtifact
                {
                    Pdnapp = new ArtifactRef { Url = url, Sha256 = CatalogTestSupport.Sha256Hex(pdnapp) },
                },
            },
        };

        var installer = NewInstaller(fetcher, apps.Path);
        var outcome = await installer.InstallFromCatalogAsync(entry, "linux-x64", default);

        outcome.Ok.Should().BeTrue(outcome.Error);
        var dir = Path.Combine(apps.Path, "demo");
        File.Exists(Path.Combine(dir, "pdn-app.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(dir, "extra.txt")).Should().BeTrue();
        ReadMarker(dir).Payload.Should().BeEquivalentTo(["extra.txt", "pdn-app.yaml"]);
    }

    [Fact]
    public async Task Upload_install_unpacks_a_pdnapp_stream()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var pdnapp = CatalogTestSupport.BuildPdnapp(fix, "up.pdnapp", new Dictionary<string, string>
        {
            ["pdn-app.yaml"] = CatalogTestSupport.ManifestYaml("uploaded", "9.9.9"),
        });

        var installer = NewInstaller(new FakeArtifactFetcher(), apps.Path);
        await using var stream = File.OpenRead(pdnapp);
        var outcome = await installer.InstallFromUploadAsync(stream, default);

        outcome.Ok.Should().BeTrue(outcome.Error);
        outcome.Id.Should().Be("uploaded");
        outcome.Version.Should().Be("9.9.9");

        var dir = Path.Combine(apps.Path, "uploaded");
        File.Exists(Path.Combine(dir, "pdn-app.yaml")).Should().BeTrue();
        var marker = ReadMarker(dir);
        marker.Source.Should().Be("upload");
        marker.Sha256s.Should().BeEmpty("an operator upload has no pin");
    }

    [Fact]
    public async Task A_path_traversal_entry_in_a_pdnapp_is_rejected_and_nothing_is_staged()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var pdnapp = CatalogTestSupport.BuildPdnapp(fix, "evil.pdnapp",
            new Dictionary<string, string> { ["pdn-app.yaml"] = CatalogTestSupport.ManifestYaml("evil", "1.0.0") },
            traversalEntryName: "../evil");

        var installer = NewInstaller(new FakeArtifactFetcher(), apps.Path);
        await using var stream = File.OpenRead(pdnapp);
        var outcome = await installer.InstallFromUploadAsync(stream, default);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("escapes");
        Directory.Exists(Path.Combine(apps.Path, "evil")).Should().BeFalse();
        File.Exists(Path.Combine(apps.Path, "..", "evil")).Should().BeFalse();
    }

    // ---- uninstall -------------------------------------------------------------------------

    [Fact]
    public async Task Uninstall_removes_only_recorded_payload_and_leaves_app_state()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var pdnapp = CatalogTestSupport.BuildPdnapp(fix, "stateful.pdnapp", new Dictionary<string, string>
        {
            ["pdn-app.yaml"] = CatalogTestSupport.ManifestYaml("stateful", "1.0.0"),
        });
        var url = "https://example.test/stateful.pdnapp";
        var fetcher = new FakeArtifactFetcher().Add(url, pdnapp);
        var entry = PdnappEntry("stateful", "1.0.0", url, CatalogTestSupport.Sha256Hex(pdnapp));

        var installer = NewInstaller(fetcher, apps.Path);
        (await installer.InstallFromCatalogAsync(entry, "linux-x64", default)).Ok.Should().BeTrue();

        // The app creates a state file in its own dir, exactly as a real daemon would.
        var dir = Path.Combine(apps.Path, "stateful");
        var stateFile = Path.Combine(dir, "stateful.db");
        File.WriteAllText(stateFile, "app state");

        var outcome = await installer.UninstallAsync("stateful", default);

        outcome.Ok.Should().BeTrue(outcome.Error);
        File.Exists(Path.Combine(dir, "pdn-app.yaml")).Should().BeFalse("the payload is removed");
        File.Exists(Path.Combine(dir, AppInstaller.MarkerFileName)).Should().BeFalse("the marker is removed");
        File.Exists(stateFile).Should().BeTrue("app-created state is left behind");
    }

    [Fact]
    public async Task Uninstall_of_a_marker_less_directory_is_refused()
    {
        using var apps = new TempDir("apps");
        var dir = Path.Combine(apps.Path, "sideloaded");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pdn-app.yaml"), CatalogTestSupport.ManifestYaml("sideloaded", "1.0.0"));

        var installer = NewInstaller(new FakeArtifactFetcher(), apps.Path);
        var outcome = await installer.UninstallAsync("sideloaded", default);

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("no install marker");
        File.Exists(Path.Combine(dir, "pdn-app.yaml")).Should().BeTrue("we never delete files we did not place");
    }

    // ---- update --------------------------------------------------------------------------

    [Fact]
    public async Task Reinstalling_a_newer_version_replaces_the_payload_and_keeps_app_state()
    {
        using var apps = new TempDir("apps");
        using var fix = new TempDir("fix");

        var v1 = CatalogTestSupport.BuildPdnapp(fix, "v1.pdnapp", new Dictionary<string, string>
        {
            ["pdn-app.yaml"] = CatalogTestSupport.ManifestYaml("upd", "1.0.0"),
            ["old-only.txt"] = "v1",
        });
        var v2 = CatalogTestSupport.BuildPdnapp(fix, "v2.pdnapp", new Dictionary<string, string>
        {
            ["pdn-app.yaml"] = CatalogTestSupport.ManifestYaml("upd", "2.0.0"),
            ["new-only.txt"] = "v2",
        });

        var fetcher = new FakeArtifactFetcher()
            .Add("https://example.test/v1", v1)
            .Add("https://example.test/v2", v2);

        var installer = NewInstaller(fetcher, apps.Path);
        (await installer.InstallFromCatalogAsync(
            PdnappEntry("upd", "1.0.0", "https://example.test/v1", CatalogTestSupport.Sha256Hex(v1)), "linux-x64", default))
            .Ok.Should().BeTrue();

        var dir = Path.Combine(apps.Path, "upd");
        var stateFile = Path.Combine(dir, "upd.db");
        File.WriteAllText(stateFile, "state");

        var outcome = await installer.InstallFromCatalogAsync(
            PdnappEntry("upd", "2.0.0", "https://example.test/v2", CatalogTestSupport.Sha256Hex(v2)), "linux-x64", default);

        outcome.Ok.Should().BeTrue(outcome.Error);
        outcome.Version.Should().Be("2.0.0");
        File.Exists(Path.Combine(dir, "old-only.txt")).Should().BeFalse("the previous payload was removed");
        File.Exists(Path.Combine(dir, "new-only.txt")).Should().BeTrue("the new payload was placed");
        File.Exists(stateFile).Should().BeTrue("app-created state survives an update");
        ReadMarker(dir).Version.Should().Be("2.0.0");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static AppInstaller NewInstaller(IArtifactFetcher fetcher, string appsRoot) =>
        new(fetcher, new DpkgDebExtractor(NullLoggerFactory.Instance),
            new FakeTimeProvider(Now), NullLoggerFactory.Instance, appsRoot);

    private static AppCatalogEntry PdnappEntry(string id, string version, string url, string sha) => new()
    {
        Id = id,
        Version = version,
        Artifact = new ArtifactSpec
        {
            Kind = ArtifactKind.Pdnapp,
            Pdnapp = new PdnappArtifact { Pdnapp = new ArtifactRef { Url = url, Sha256 = sha } },
        },
    };

    private static InstallMarker ReadMarker(string dir)
    {
        var json = File.ReadAllText(Path.Combine(dir, AppInstaller.MarkerFileName));
        return System.Text.Json.JsonSerializer.Deserialize(json, InstallMarkerJsonContext.Default.InstallMarker)!;
    }

    private static bool DpkgDebAvailable() =>
        File.Exists("/usr/bin/dpkg-deb") || File.Exists("/bin/dpkg-deb");

    /// <summary>Build a tiny but real <c>.deb</c> at test time with <c>dpkg-deb --build</c>
    /// over a constructed <c>usr/share/packetnet/apps/&lt;id&gt;/{pdn-app.yaml,&lt;bin&gt;}</c>
    /// tree. Returns the .deb path.</summary>
    private static string BuildFixtureDeb(TempDir fix, string id, string version, string arch)
    {
        var stage = Path.Combine(fix.Path, $"deb-{id}");
        var debianDir = Path.Combine(stage, "DEBIAN");
        var appDir = Path.Combine(stage, "usr", "share", "packetnet", "apps", id);
        Directory.CreateDirectory(debianDir);
        Directory.CreateDirectory(appDir);

        File.WriteAllText(Path.Combine(debianDir, "control"), $"""
            Package: pdn-{id}
            Version: {version}
            Architecture: {arch}
            Maintainer: test <test@example.test>
            Description: fixture package for the installer test

            """.ReplaceLineEndings("\n"));

        File.WriteAllText(Path.Combine(appDir, "pdn-app.yaml"), CatalogTestSupport.ManifestYaml(id, version));
        File.WriteAllText(Path.Combine(appDir, id), "#!/bin/sh\necho fixture\n");

        var debPath = Path.Combine(fix.Path, $"pdn-{id}_{version}_{arch}.deb");
        var psi = new ProcessStartInfo("dpkg-deb")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--build");
        psi.ArgumentList.Add(stage);
        psi.ArgumentList.Add(debPath);

        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dpkg-deb --build failed: {stderr}");
        }
        return debPath;
    }
}
