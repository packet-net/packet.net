using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The SHIPPED bundled manifests — <c>examples/wall/pdn-app.yaml</c> and
/// <c>examples/lobby/pdn-app.yaml</c>, staged by <c>scripts/build-deb.sh</c> into
/// <c>/usr/share/packetnet/apps/&lt;id&gt;</c> — parsed and validated mechanically, so an
/// edit that breaks parsing (or drifts the wiring the two halves of an app agree on)
/// fails CI instead of failing on a deployed node.
/// </summary>
/// <remarks>
/// DAPPS (and bpqchat/convers) are no longer bundled in the deb: they ship in the app
/// CATALOG (<c>catalog/apps.yaml</c>, see <c>docs/app-catalog.md</c>) and are fetched on
/// demand at install time. The catalog file itself is parsed + validated by
/// <c>AppCatalogYamlTests</c>; there is nothing for this class to assert about them.
/// </remarks>
public class ShippedManifestsTests
{
    // ---- WALL -----------------------------------------------------------------------

    [Fact]
    public void Wall_manifest_parses_with_the_expected_blocks()
    {
        var m = AppPackageManifestYaml.Parse(ReadManifest("wall"));

        m.Manifest.Should().Be(1);
        m.Id.Should().Be("wall");
        m.Name.Should().Be("WALL");
        m.Version.Should().NotBeNullOrWhiteSpace();
        m.Description.Should().NotBeNullOrWhiteSpace();
        m.Icon.Should().Be("message-square");
        m.Capabilities.Should().Equal("session", "web");

        // Packet plane: spawn-per-connect wall.py over the stdio wire. The args are
        // package-dir-relative by design — the host resolves them against the package dir.
        m.Session.Should().NotBeNull();
        m.Session!.Match.Should().Be("WALL");
        m.Session.Kind.Should().Be(ApplicationKind.Process);
        m.Session.Command.Should().Be("/usr/bin/python3");
        m.Session.Args.Should().Equal("wall.py");

        // Human plane: the supervised web view.
        m.Service.Should().NotBeNull();
        m.Service!.Command.Should().Be("/usr/bin/python3");
        m.Service.Args.Should().HaveCount(2).And.HaveElementAt(0, "wall_web.py");
        m.Service.Managed.Should().Be(AppServiceManaged.Pdn);

        m.Ui.Should().NotBeNull();
        m.Ui!.Name.Should().Be("WALL");
        m.Ui.Icon.Should().Be("message-square");
    }

    [Fact]
    public void Wall_service_port_argument_matches_the_ui_upstream()
    {
        // wall_web.py takes its loopback port as argv[1]; the gateway proxies to
        // ui.upstream. The two MUST name the same port or the tile 502s.
        var m = AppPackageManifestYaml.Parse(ReadManifest("wall"));

        var upstream = new Uri(m.Ui!.Upstream!, UriKind.Absolute);
        upstream.IsLoopback.Should().BeTrue("an app web server must bind loopback only");
        m.Service!.Args[1].Should().Be(upstream.Port.ToString(CultureInfo.InvariantCulture),
            "wall_web.py serves on argv[1]; the gateway proxies to ui.upstream");
    }

    // ---- LOBBY ----------------------------------------------------------------------

    [Fact]
    public void Lobby_manifest_parses_with_the_expected_blocks()
    {
        var m = AppPackageManifestYaml.Parse(ReadManifest("lobby"));

        m.Manifest.Should().Be(1);
        m.Id.Should().Be("lobby");
        m.Name.Should().Be("LOBBY");
        m.Version.Should().NotBeNullOrWhiteSpace();
        m.Description.Should().NotBeNullOrWhiteSpace();
        m.Icon.Should().Be("users");
        m.Capabilities.Should().Equal("session");

        // Packet plane: connect-per-session to the daemon's Unix socket.
        m.Session.Should().NotBeNull();
        m.Session!.Match.Should().Be("LOBBY");
        m.Session.Kind.Should().Be(ApplicationKind.Socket);
        m.Session.SocketPath.Should().Be("/run/packetnet/lobby.sock");

        // The daemon pdn supervises.
        m.Service.Should().NotBeNull();
        m.Service!.Command.Should().Be("/usr/bin/python3");
        m.Service.Args[0].Should().Be("lobby.py");
        m.Service.Managed.Should().Be(AppServiceManaged.Pdn);

        m.Ui.Should().BeNull("LOBBY has no web view");
    }

    [Fact]
    public void Lobby_service_listen_path_matches_the_session_socket_path()
    {
        // lobby.py listens on the path given as a BARE argv[1] (not a --socket flag);
        // pdn connects sessions to session.socketPath. They MUST be the same path.
        var m = AppPackageManifestYaml.Parse(ReadManifest("lobby"));

        m.Service!.Args.Should().Equal("lobby.py", m.Session!.SocketPath!);
    }

    // ---- the catalog's view ------------------------------------------------------------

    [Fact]
    public void Catalog_discovers_the_example_packages_as_valid_and_disabled_by_default()
    {
        // examples/ is the in-repo half of the layout build-deb.sh stages into
        // /usr/share/packetnet/apps (wall + lobby — the only BUNDLED packages now;
        // DAPPS/bpqchat/convers ship in the app catalog, fetched on demand). The
        // catalog skips directories without a manifest.
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            AppPackageRoots = [Path.Combine(RepoRoot(), "examples")],
        };

        var found = catalog.Discover(config);

        found.Select(p => p.Id).Should().BeEquivalentTo(["wall", "lobby"]);
        foreach (var package in found)
        {
            package.Error.Should().BeNull("a shipped package must validate clean");
            package.Enabled.Should().BeFalse("discovered is not enabled — the owner opts in");
        }
    }

    // ---- plumbing ----------------------------------------------------------------------

    private static string ReadManifest(string id) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "examples", id, AppPackageCatalog.ManifestFileName));

    /// <summary>Walk up from the test assembly to the repo root (the directory that has
    /// <c>examples/wall/pdn-app.yaml</c>) — same approach as <c>WallAppIntegrationTests</c>.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "examples", "wall", AppPackageCatalog.ManifestFileName)))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no examples/wall/pdn-app.yaml above the test assembly).");
    }
}
