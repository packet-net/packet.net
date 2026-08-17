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
        packet:
          command: {match}
        session:
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
        alpha.Manifest!.Packet!.Command.Should().Be("LATE");
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
            [new AppOverrideConfig { Id = "alpha", Enabled = true, Command = "TALK" }])).Single();

        entry.EffectiveCommand.Should().Be("TALK");
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
            Command = "OTHER",
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
            [new AppOverrideConfig { Id = "alpha", Enabled = true, Command = "NODES" }])).Single();

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
            Command = "chat",
            Kind = ApplicationKind.Socket,
            SocketPath = "/run/other.sock",
        };

        var entry = catalog.Discover(Config(inline: [inline])).Single();

        entry.Error.Should().Contain("inline application 'other'");
    }

    // ---- forward: block validation (docs/network-access.md) ---------------------------

    private static string ForwardManifest(string id, string forwardYaml) => $"""
        manifest: 1
        id: {id}
        service:
          command: /bin/{id}
        forward:
        {forwardYaml}
        """;

    [Fact]
    public void A_valid_forward_block_is_accepted_and_surfaced()
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 993
                target: 127.0.0.1:1430
                tls: terminate
            """));

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().BeNull();
        entry.Forwards.Should().ContainSingle();
        entry.Forwards[0].Listen.Should().Be(993);
        entry.Forwards[0].Target.Should().Be("127.0.0.1:1430");
        entry.Forwards[0].Tls.Should().Be(ForwardTls.Terminate);
    }

    [Theory]
    [InlineData("::1:993")]
    [InlineData("localhost:993")]
    public void Loopback_hosts_other_than_127_0_0_1_are_accepted(string target)
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", $"""
              - listen: 993
                target: {target}
            """));

        catalog.Discover(Config()).Single().Error.Should().BeNull();
    }

    [Fact]
    public void A_non_loopback_forward_target_is_an_error()
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 993
                target: 10.0.0.5:1430
            """));

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("forward[0].target").And.Contain("loopback");
    }

    [Fact]
    public void A_forward_target_without_a_port_is_an_error()
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 993
                target: 127.0.0.1
            """));

        catalog.Discover(Config()).Single().Error.Should().Contain("forward[0].target");
    }

    [Fact]
    public void A_listen_of_443_is_reserved_for_the_web_proxy_and_is_an_error()
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 443
                target: 127.0.0.1:1430
            """));

        var entry = catalog.Discover(Config()).Single();

        entry.Error.Should().Contain("443").And.Contain("reserved");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void A_listen_outside_1_65535_is_an_error(int listen)
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", $"""
              - listen: {listen}
                target: 127.0.0.1:1430
            """));

        catalog.Discover(Config()).Single().Error.Should().Contain("forward[0].listen");
    }

    [Fact]
    public void Two_packages_claiming_the_same_listen_port_are_both_errors()
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 993
                target: 127.0.0.1:1430
            """));
        WritePackage(rootB, "post", ForwardManifest("post", """
              - listen: 993
                target: 127.0.0.1:1431
            """));

        var found = catalog.Discover(Config());

        found.Should().HaveCount(2);
        found.Single(p => p.Id == "mail").Error.Should().Contain("listen port 993").And.Contain("'post'");
        found.Single(p => p.Id == "post").Error.Should().Contain("listen port 993").And.Contain("'mail'");
    }

    [Fact]
    public void Different_listen_ports_across_packages_are_fine()
    {
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 993
                target: 127.0.0.1:1430
            """));
        WritePackage(rootB, "post", ForwardManifest("post", """
              - listen: 465
                target: 127.0.0.1:1431
            """));

        var found = catalog.Discover(Config());

        found.Should().OnlyContain(p => p.Error == null);
    }

    [Fact]
    public void An_out_of_range_listen_does_not_feed_the_cross_package_dup_check()
    {
        // An out-of-range listen is already its own per-package error; it must not also produce a
        // confusing "collides with" message against a healthy same-(invalid)-port package. The
        // dup-check runs only on listens that passed the range/reserved gate.
        WritePackage(rootA, "mail", ForwardManifest("mail", """
              - listen: 70000
                target: 127.0.0.1:1430
            """));
        WritePackage(rootB, "post", ForwardManifest("post", """
              - listen: 70000
                target: 127.0.0.1:1431
            """));

        var found = catalog.Discover(Config());

        // Each is flagged for the out-of-range listen, but NEITHER for a collision.
        foreach (var p in found)
        {
            p.Error.Should().Contain("forward[0].listen");
            p.Error.Should().NotContain("collides");
        }
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

    // ---- configured, but not installed (#738 item 2) ----------------------------------

    [Fact]
    public void An_apps_entry_naming_a_package_no_root_holds_is_reported_as_not_installed()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));
        var config = Config(
            apps: [
                new AppOverrideConfig { Id = "alpha", Enabled = true },
                new AppOverrideConfig { Id = "ghost", Enabled = true },
                new AppOverrideConfig { Id = "motd", Enabled = true },
            ],
            inline: [new ApplicationConfig { Id = "motd", Command = "MOTD", Executable = "/bin/cat" }]);

        var missing = AppPackageCatalog.ConfiguredButNotInstalled(config, catalog.Discover(config));

        // Only `ghost`: `alpha` is on disk, and `motd` resolves to the inline applications:
        // entry (which IS its own payload - the catalog flags an id collision separately).
        missing.Select(a => a.Id).Should().Equal("ghost");
        AppPackageCatalog.RootsFor(config).Should().Equal(rootA.FullName, rootB.FullName);
    }

    [Fact]
    public void Nothing_is_reported_as_not_installed_when_every_apps_entry_has_its_package()
    {
        WritePackage(rootA, "alpha", SocketManifest("alpha", "ALPHA"));
        var config = Config(apps: [new AppOverrideConfig { Id = "ALPHA", Enabled = false }]);

        // Ids are matched case-insensitively, like every other apps:/package pairing.
        AppPackageCatalog.ConfiguredButNotInstalled(config, catalog.Discover(config)).Should().BeEmpty();
    }

    private string[] Snapshot() =>
        new[] { rootA.FullName, rootB.FullName }
            .SelectMany(r => Directory.EnumerateFileSystemEntries(r, "*", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
