using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The SHIPPED manifests — <c>examples/wall/pdn-app.yaml</c>, <c>examples/lobby/pdn-app.yaml</c>,
/// and <c>packaging/dapps/pdn-app.yaml</c>, staged by <c>scripts/build-deb.sh</c> into
/// <c>/usr/share/packetnet/apps/&lt;id&gt;</c> — parsed and validated mechanically, so an
/// edit that breaks parsing (or drifts the wiring the two halves of an app agree on)
/// fails CI instead of failing on a deployed node.
/// </summary>
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

    // ---- DAPPS ----------------------------------------------------------------------

    [Fact]
    public void Dapps_manifest_parses_with_the_expected_blocks()
    {
        // packaging/dapps/pdn-app.yaml — pdn carries it interim; the binary beside it is
        // the published m0lte/dapps release artifact, fetched + sha256-pinned by
        // build-deb.sh (never built from source — public interfaces only).
        var m = AppPackageManifestYaml.Parse(ReadDappsManifest());

        m.Manifest.Should().Be(1);
        m.Id.Should().Be("dapps");
        m.Name.Should().Be("DAPPS");
        m.Version.Should().NotBeNullOrWhiteSpace();
        m.Description.Should().Contain("Distributed Asynchronous Packet Pub/Sub");
        m.Icon.Should().Be("inbox");
        m.Capabilities.Should().Equal("network", "web");

        // No packet-plane console verb: DAPPS binds its own callsigns over RHPv2.
        m.Session.Should().BeNull("DAPPS speaks RHPv2, not the pdn-app/1 console wire");

        // The supervised daemon: the staged release binary, package-dir-relative.
        m.Service.Should().NotBeNull();
        m.Service!.Command.Should().Be("./dapps");
        m.Service.Args.Should().BeEmpty();
        m.Service.Managed.Should().Be(AppServiceManaged.Pdn);
        m.Service.WorkingDirectory.Should().BeNull(
            "cwd must default to the state dir so the cwd-relative dapps.db lands in /var/lib/packetnet/apps/dapps");

        // The env map seeds DAPPS's first-start config: RHPv2 to the local node, no
        // self-update (pdn's deb owns updates), MQTT off the well-known 1883.
        m.Service.Environment.Should().Contain("DAPPS_NODE_BEARER", "rhpv2");
        m.Service.Environment.Should().Contain("DAPPS_NODE_HOST", "127.0.0.1");
        m.Service.Environment.Should().Contain("DAPPS_RHP_PORT", "9000");
        m.Service.Environment.Should().Contain("DAPPS_DEFAULT_BEARER_PORT", "0");
        m.Service.Environment.Should().Contain("DAPPS_UPDATE_CHECK_ENABLED", "false");
        m.Service.Environment.Should().Contain("DAPPS_MQTT_PORT", "18831");
        m.Service.Environment.Should().NotContainKey("DAPPS_CALLSIGN",
            "there is no sensible default callsign — the owner supplies it via the apps: override");

        m.Ui.Should().NotBeNull();
        m.Ui!.Name.Should().Be("DAPPS");
        m.Ui.Icon.Should().Be("inbox");
    }

    [Fact]
    public void Dapps_aspnetcore_urls_matches_the_ui_upstream()
    {
        // DAPPS serves its web UI where ASPNETCORE_URLS says; the gateway proxies to
        // ui.upstream. The two MUST name the same loopback origin or the tile 502s.
        var m = AppPackageManifestYaml.Parse(ReadDappsManifest());

        var upstream = new Uri(m.Ui!.Upstream!, UriKind.Absolute);
        upstream.IsLoopback.Should().BeTrue("an app web server must bind loopback only");
        m.Service!.Environment.Should().Contain("ASPNETCORE_URLS",
            $"{upstream.Scheme}://{upstream.Host}:{upstream.Port}");
    }

    [Fact]
    public void Dapps_manifest_version_matches_the_build_deb_pin()
    {
        // build-deb.sh stages the release binary pinned as dapps_version=vX.Y.Z; the
        // manifest's informational version must be the same release or the UI lies.
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build-deb.sh"));
        var match = System.Text.RegularExpressions.Regex.Match(
            script, @"^dapps_version=""v([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline);
        match.Success.Should().BeTrue("build-deb.sh must pin dapps_version=\"vX.Y.Z\"");

        var m = AppPackageManifestYaml.Parse(ReadDappsManifest());
        m.Version.Should().Be(match.Groups[1].Value);
    }

    // ---- the catalog's view ------------------------------------------------------------

    [Fact]
    public void Catalog_discovers_all_shipped_packages_as_valid_and_disabled_by_default()
    {
        // examples/ + packaging/ together are exactly the layout build-deb.sh stages into
        // /usr/share/packetnet/apps (packaging/ holds only the dapps package dir — the
        // catalog skips directories without a manifest), so pointing the catalog at them
        // exercises the full contract validation (id = dir name, per-kind required fields,
        // verb collisions) against what we actually ship.
        var catalog = new AppPackageCatalog(NullLoggerFactory.Instance);
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            AppPackageRoots =
            [
                Path.Combine(RepoRoot(), "examples"),
                Path.Combine(RepoRoot(), "packaging"),
            ],
        };

        var found = catalog.Discover(config);

        found.Select(p => p.Id).Should().BeEquivalentTo(["wall", "lobby", "dapps"]);
        foreach (var package in found)
        {
            package.Error.Should().BeNull("a shipped package must validate clean");
            package.Enabled.Should().BeFalse("discovered is not enabled — the owner opts in");
        }
    }

    // ---- plumbing ----------------------------------------------------------------------

    private static string ReadManifest(string id) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "examples", id, AppPackageCatalog.ManifestFileName));

    private static string ReadDappsManifest() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "packaging", "dapps", AppPackageCatalog.ManifestFileName));

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
