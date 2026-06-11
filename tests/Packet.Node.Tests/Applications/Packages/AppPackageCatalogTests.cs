using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications.Packages;

public class AppPackageCatalogTests : IDisposable
{
    private readonly DirectoryInfo rootA = Directory.CreateTempSubdirectory("pdn-apps-a-");
    private readonly DirectoryInfo rootB = Directory.CreateTempSubdirectory("pdn-apps-b-");
    private readonly AppPackageCatalog catalog = new(NullLoggerFactory.Instance);

    public void Dispose()
    {
        rootA.Delete(recursive: true);
        rootB.Delete(recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string WritePackage(DirectoryInfo root, string dirName, string manifestYaml)
    {
        var dir = Path.Combine(root.FullName, dirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AppPackageCatalog.ManifestFileName), manifestYaml);
        return dir;
    }

    private static string SocketManifest(string id, string match) => $"""
        manifest: 1
        id: {id}
        session:
          match: {match}
          kind: socket
          socketPath: /run/packetnet/{id}.sock
        """;

    private NodeConfig Config(
        IReadOnlyList<AppOverrideConfig>? apps = null,
        IReadOnlyList<ApplicationConfig>? inline = null) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        AppPackageRoots = [rootA.FullName, rootB.FullName],
        Apps = apps ?? [],
        Applications = inline ?? [],
    };

    // ---- discovery ------------------------------------------------------------------

    [Fact]
    public void Discovers_packages_across_both_roots()
    {
        var dirAlpha = WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));
        var dirBeta = WritePackage(rootB, "beta", SocketManifest("beta", "BETA"));

        var found = catalog.Discover(Config());

        found.Should().HaveCount(2);
        var alpha = found.Single(p => p.Id == "alpha");
        alpha.PackageDir.Should().Be(dirAlpha);
        alpha.Manifest.Should().NotBeNull();
        alpha.Error.Should().BeNull();
        found.Single(p => p.Id == "beta").PackageDir.Should().Be(dirBeta);
    }

    [Fact]
    public void Later_root_wins_on_id_collision()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "EARLY"));
        var winner = WritePackage(rootB, "alpha", SocketManifest("alpha", "LATE"));

        var found = catalog.Discover(Config());

        var alpha = found.Should().ContainSingle().Subject;
        alpha.PackageDir.Should().Be(winner, "the owner-installed root overrides the distro root");
        alpha.Manifest!.Session!.Match.Should().Be("LATE");
        alpha.Error.Should().BeNull();
    }

    [Fact]
    public void Directories_without_a_manifest_are_ignored_silently()
    {
        Directory.CreateDirectory(Path.Combine(rootA.FullName, "not-a-package"));
        File.WriteAllText(Path.Combine(rootA.FullName, "stray-file.txt"), "hi");
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));

        var found = catalog.Discover(Config());

        found.Should().ContainSingle().Which.Id.Should().Be("alpha");
    }

    [Fact]
    public void A_missing_root_is_not_an_error()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));
        var config = Config() with
        {
            AppPackageRoots = [rootA.FullName, Path.Combine(rootB.FullName, "does-not-exist")],
        };

        var found = catalog.Discover(config);

        found.Should().ContainSingle().Which.Id.Should().Be("alpha");
    }

    // ---- the trust switch + override merge ------------------------------------------

    [Fact]
    public void A_discovered_package_is_disabled_until_the_owner_enables_it()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));

        var entry = catalog.Discover(Config()).Single();

        entry.Enabled.Should().BeFalse("discovered is not enabled — the apps: entry is the trust grant");
        entry.Override.Should().BeNull();
    }

    [Fact]
    public void An_apps_entry_flips_the_package_on()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));

        var entry = catalog.Discover(Config(apps: [new AppOverrideConfig { Id = "alpha", Enabled = true }]))
            .Single();

        entry.Enabled.Should().BeTrue();
        entry.Override.Should().NotBeNull();
    }

    [Fact]
    public void The_owner_match_override_wins_over_the_manifest_verb()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "CHAT"));

        var entry = catalog.Discover(Config(apps:
            [new AppOverrideConfig { Id = "alpha", Enabled = true, Match = "TALK" }])).Single();

        entry.EffectiveMatch.Should().Be("TALK");
        entry.Error.Should().BeNull();
    }

    [Fact]
    public void The_owner_environment_merges_over_the_manifest_key_by_key()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: alpha
            service:
              command: /bin/alpha
              environment:
                KEEP: manifest
                CLASH: manifest
            """);

        var entry = catalog.Discover(Config(apps:
        [
            new AppOverrideConfig
            {
                Id = "alpha",
                Enabled = true,
                Environment = new Dictionary<string, string> { ["CLASH"] = "owner", ["ADDED"] = "owner" },
            },
        ])).Single();

        entry.EffectiveEnvironment.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["KEEP"] = "manifest",
            ["CLASH"] = "owner",
            ["ADDED"] = "owner",
        });
    }

    // ---- validation: each rule yields an Error entry, never a throw ------------------

    [Fact]
    public void A_manifest_version_other_than_1_is_an_error_and_forces_disabled()
    {
        WritePackage(rootA, "alpha", """
            manifest: 2
            id: alpha
            ui:
              upstream: http://127.0.0.1:9090
            """);

        var entry = catalog.Discover(Config(apps: [new AppOverrideConfig { Id = "alpha", Enabled = true }]))
            .Single();

        entry.Error.Should().Contain("must be 1");
        entry.Enabled.Should().BeFalse("a broken package never runs, whatever the override says");
    }

    [Fact]
    public void A_blank_id_is_an_error()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: ""
            ui:
              upstream: http://127.0.0.1:9090
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("id");
        entry.Id.Should().Be("alpha", "an error entry is identified by its directory name");
    }

    [Fact]
    public void An_id_outside_lowercase_a_z0_9_hyphen_is_an_error()
    {
        WritePackage(rootA, "Alpha", """
            manifest: 1
            id: Alpha
            ui:
              upstream: http://127.0.0.1:9090
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("lowercase");
    }

    [Fact]
    public void An_id_that_differs_from_the_directory_name_is_an_error()
    {
        WritePackage(rootA, "alpha", SocketManifest("beta", "BETA"));

        var entry = catalog.Discover(Config()).Single();

        entry.Id.Should().Be("alpha");
        entry.Error.Should().Contain("'beta'").And.Contain("'alpha'");
    }

    [Fact]
    public void A_manifest_with_none_of_session_service_ui_is_an_error()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: alpha
            name: Empty
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("at least one of session, service, or ui");
    }

    [Fact]
    public void A_process_session_without_a_command_is_an_error()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: alpha
            session:
              match: ALPHA
              kind: process
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("session.command");
    }

    [Fact]
    public void A_socket_session_without_a_socketPath_is_an_error()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: alpha
            session:
              match: ALPHA
              kind: socket
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("session.socketPath");
    }

    [Fact]
    public void A_service_without_a_command_is_an_error()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: alpha
            service:
              args: [run]
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().ContainEquivalentOf("command");
        entry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void A_ui_upstream_that_is_not_an_absolute_http_url_is_an_error()
    {
        WritePackage(rootA, "alpha", """
            manifest: 1
            id: alpha
            ui:
              upstream: 127.0.0.1:9090
            """);

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("ui.upstream").And.Contain("http");
    }

    [Fact]
    public void A_package_id_colliding_with_an_inline_application_id_is_an_error()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));
        var inline = new ApplicationConfig
        {
            Id = "alpha",
            Match = "OTHER",
            Kind = ApplicationKind.Socket,
            SocketPath = "/run/other.sock",
        };

        var entry = catalog.Discover(Config(inline: [inline])).Single();

        entry.Error.Should().Contain("inline applications");
    }

    // ---- the three verb-collision variants -------------------------------------------

    [Fact]
    public void A_session_verb_colliding_with_a_built_in_console_verb_is_an_error()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "BYE"));

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("built-in console verb");
    }

    [Fact]
    public void The_built_in_check_runs_on_the_effective_verb_so_an_override_can_cause_it()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "CHAT"));

        var entry = catalog.Discover(Config(apps:
            [new AppOverrideConfig { Id = "alpha", Enabled = true, Match = "NODES" }])).Single();

        entry.Error.Should().Contain("built-in console verb");
        entry.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Two_packages_resolving_the_same_effective_verb_are_both_errors()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "CHAT"));
        WritePackage(rootB, "beta", SocketManifest("beta", "chat"));

        var found = catalog.Discover(Config());

        found.Should().HaveCount(2);
        found.Single(p => p.Id == "alpha").Error.Should().Contain("collides with package(s) 'beta'");
        found.Single(p => p.Id == "beta").Error.Should().Contain("collides with package(s) 'alpha'");
    }

    [Fact]
    public void A_session_verb_colliding_with_an_inline_application_match_is_an_error()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "CHAT"));
        var inline = new ApplicationConfig
        {
            Id = "other",
            Match = "chat",
            Kind = ApplicationKind.Socket,
            SocketPath = "/run/other.sock",
        };

        var entry = catalog.Discover(Config(inline: [inline])).Single();

        entry.Error.Should().Contain("inline application 'other'");
    }

    // ---- resilience -------------------------------------------------------------------

    [Fact]
    public void A_broken_manifest_yields_an_error_entry_and_healthy_siblings_still_discover()
    {
        WritePackage(rootA, "broken", "{{{ this is not yaml");
        WritePackage(rootA, "healthy", SocketManifest("healthy", "FINE"));

        var found = catalog.Discover(Config());

        found.Should().HaveCount(2, "a dir containing pdn-app.yaml is never silently dropped");
        var broken = found.Single(p => p.Id == "broken");
        broken.Error.Should().Contain("pdn-app.yaml");
        broken.Manifest.Should().BeNull();
        broken.Enabled.Should().BeFalse();
        found.Single(p => p.Id == "healthy").Error.Should().BeNull();
    }

    // ---- state dir + pure-read guarantees ----------------------------------------------

    [Fact]
    public void StateDir_lives_inside_the_package_dir_when_the_roots_are_overridden()
    {
        var dir = WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));

        var entry = catalog.Discover(Config()).Single();

        entry.StateDir.Should().Be(Path.Combine(dir, "state"),
            "a dev/test scan must never compute paths into /var/lib");
    }

    [Fact]
    public void Discover_is_a_pure_read_and_creates_nothing_on_disk()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));
        var before = Snapshot();

        catalog.Discover(Config(apps: [new AppOverrideConfig { Id = "alpha", Enabled = true }]));

        Snapshot().Should().Equal(before, "directories (state dirs included) are created on use, not on scan");
    }

    private string[] Snapshot() =>
        new[] { rootA.FullName, rootB.FullName }
            .SelectMany(r => Directory.EnumerateFileSystemEntries(r, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
