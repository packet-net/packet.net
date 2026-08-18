using Packet.Node.Core.Applications;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The session-resolution union in <see cref="ApplicationHost"/>: verbs resolve from the inline
/// <c>applications:</c> list first, then from enabled, error-free app packages with a
/// <c>session:</c> block - mapped onto the <see cref="ApplicationConfig"/> shape the existing
/// run path understands (command/args resolved against the package dir, working dir = the
/// state dir, capabilities from the manifest, no UI). A null catalog is exactly the
/// pre-package host, which the existing <c>ApplicationHostTests</c> keep covering.
/// </summary>
[Trait("Category", "Node")]
public sealed class ApplicationHostPackageUnionTests
{
    private static NodeConfig Cfg(params ApplicationConfig[] inline) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Applications = inline,
    };

    private static ApplicationHost Host(FakeAppPackageCatalog catalog, params ApplicationConfig[] inline) =>
        new(new TestConfigProvider(Cfg(inline)), loggerFactory: null, catalog);

    private static AppPackageManifest SessionManifest(
        string id,
        string match = "LOBBY",
        ApplicationKind kind = ApplicationKind.Process,
        string? command = "/usr/bin/python3",
        IReadOnlyList<string>? args = null,
        string? socketPath = null,
        IReadOnlyList<string>? capabilities = null) => new()
        {
            Manifest = 1,
            Id = id,
            Capabilities = capabilities ?? ["session"],
            Packet = new AppPacketSpec { Command = match },
            Session = new AppSessionSpec
            {
                Kind = kind,
                Command = command,
                Args = args ?? [],
                SocketPath = socketPath,
            },
        };

    [Fact]
    public void Package_session_resolves_and_maps_to_the_run_shape()
    {
        using var pkg = new TempAppPackage("lobby");
        var script = pkg.WriteScript("lobby.py", "# the app\n");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest("lobby", args: ["lobby.py", "--flag"])));
        var host = Host(catalog);

        var resolved = host.Resolve("lobby");   // case-insensitive, like inline verbs

        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be("lobby");
        resolved.Command.Should().Be("LOBBY");
        resolved.Enabled.Should().BeTrue();
        resolved.Kind.Should().Be(ApplicationKind.Process);
        resolved.Executable.Should().Be("/usr/bin/python3");   // absolute → untouched
        resolved.Args[0].Should().Be(script);                  // names a package file → absolute
        resolved.Args[1].Should().Be("--flag");                // a flag passes through
        resolved.WorkingDirectory.Should().Be(pkg.StateDir);
        Directory.Exists(pkg.StateDir).Should().BeTrue("the state dir is created on first use");
        resolved.Capabilities.Should().ContainSingle().Which.Should().Be("session");
        resolved.Ui.Should().BeNull();   // tiles are the gateway's concern
    }

    [Fact]
    public void Relative_command_naming_a_package_file_resolves_against_the_package_dir()
    {
        using var pkg = new TempAppPackage("script");
        var script = pkg.WriteScript("run.sh", "#!/bin/sh\n");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest("script", match: "RUN", command: "run.sh")));
        var host = Host(catalog);

        host.Resolve("RUN")!.Executable.Should().Be(script);
    }

    [Fact]
    public void Disabled_package_does_not_resolve()
    {
        using var pkg = new TempAppPackage("lobby");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest("lobby"), enabled: false));

        Host(catalog).Resolve("LOBBY").Should().BeNull();
    }

    [Fact]
    public void Broken_package_does_not_resolve()
    {
        using var pkg = new TempAppPackage("lobby");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest("lobby"), error: "manifest invalid: id mismatch"));

        Host(catalog).Resolve("LOBBY").Should().BeNull();
    }

    [Fact]
    public void Package_without_a_session_block_does_not_resolve()
    {
        using var pkg = new TempAppPackage("daemon");
        pkg.WriteScript("run.sh", "#!/bin/sh\n");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Service("run.sh"));   // service-only manifest - no console verb

        Host(catalog).Resolve("DAEMON").Should().BeNull();
    }

    [Fact]
    public void Inline_application_beats_a_package_on_the_same_verb()
    {
        using var pkg = new TempAppPackage("lobby");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest("lobby")));
        var inline = new ApplicationConfig { Id = "inline-lobby", Command = "LOBBY", Executable = "/bin/cat" };
        var host = Host(catalog, inline);

        host.Resolve("LOBBY")!.Id.Should().Be("inline-lobby");
    }

    [Fact]
    public void Owner_match_override_replaces_the_manifest_verb()
    {
        using var pkg = new TempAppPackage("lobby");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(
            SessionManifest("lobby"),
            @override: new AppOverrideConfig { Id = "lobby", Enabled = true, Command = "FOYER" }));
        var host = Host(catalog);

        host.Resolve("FOYER")!.Id.Should().Be("lobby");
        host.Resolve("foyer")!.Command.Should().Be("FOYER");
        host.Resolve("LOBBY").Should().BeNull();   // the overridden verb is gone
    }

    [Fact]
    public void Socket_kind_session_maps_socket_path_through()
    {
        using var pkg = new TempAppPackage("lobby");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest(
            "lobby", kind: ApplicationKind.Socket, command: null, socketPath: "/run/packetnet/lobby.sock")));
        var host = Host(catalog);

        var resolved = host.Resolve("LOBBY");
        resolved.Should().NotBeNull();
        resolved!.Kind.Should().Be(ApplicationKind.Socket);
        resolved.SocketPath.Should().Be("/run/packetnet/lobby.sock");
        resolved.Executable.Should().BeNull();
    }

    [Fact]
    public void Package_resolution_reads_the_catalog_live()
    {
        using var pkg = new TempAppPackage("lobby");
        var catalog = new FakeAppPackageCatalog();
        catalog.Set(pkg.Discovered(SessionManifest("lobby"), enabled: false));
        var host = Host(catalog);
        host.Resolve("LOBBY").Should().BeNull();

        catalog.Set(pkg.Discovered(SessionManifest("lobby"), enabled: true));   // the owner enables it
        host.Resolve("LOBBY")!.Id.Should().Be("lobby");
    }
}
