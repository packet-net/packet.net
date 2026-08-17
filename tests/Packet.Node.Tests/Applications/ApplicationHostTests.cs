using Packet.Node.Core.Applications;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Applications;

/// <summary>
/// The <see cref="ApplicationHost"/>: verb resolution (enabled-only, case-insensitive, exact,
/// read live from config so a hot edit applies to the next launch) and the total run contract
/// (a spawn failure is reported to the user, never thrown).
/// </summary>
[Trait("Category", "Node")]
public sealed class ApplicationHostTests
{
    private static NodeConfig WithApps(params ApplicationConfig[] apps) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Applications = apps,
    };

    private static ApplicationConfig App(string id, string match, bool enabled = true, string command = "/bin/cat") =>
        new() { Id = id, Command = match, Enabled = enabled, Executable = command };

    [Fact]
    public void Resolve_matches_an_enabled_app_case_insensitively_and_exactly()
    {
        var cfg = new TestConfigProvider(WithApps(App("wall", "WALL")));
        var host = new ApplicationHost(cfg);

        (host.Resolve("WALL")?.Id).Should().Be("wall");
        (host.Resolve("wall")?.Id).Should().Be("wall");     // case-insensitive
        (host.Resolve(" WALL ")?.Id).Should().Be("wall");   // trimmed
        host.Resolve("WAL").Should().BeNull();              // exact, not a prefix
        host.Resolve("WALLY").Should().BeNull();
        host.Resolve("nope").Should().BeNull();
        host.Resolve("").Should().BeNull();
    }

    [Fact]
    public void Resolve_ignores_a_disabled_app()
    {
        var host = new ApplicationHost(new TestConfigProvider(WithApps(App("wall", "WALL", enabled: false))));
        host.Resolve("WALL").Should().BeNull();
    }

    [Fact]
    public void Resolve_reads_config_live()
    {
        var cfg = new TestConfigProvider(WithApps(App("wall", "WALL")));
        var host = new ApplicationHost(cfg);
        host.Resolve("WALL").Should().NotBeNull();

        // Hot edit: disable wall, add guest. The next resolve reflects it — no reconcile needed.
        cfg.Apply(WithApps(App("wall", "WALL", enabled: false), App("guest", "GUEST")));
        host.Resolve("WALL").Should().BeNull();
        (host.Resolve("GUEST")?.Id).Should().Be("guest");
    }

    [Fact]
    public async Task RunAsync_reports_a_spawn_failure_to_the_user_and_does_not_throw()
    {
        var bad = App("ghost", "GHOST", command: "/no/such/binary-xyzzy");
        var host = new ApplicationHost(new TestConfigProvider(WithApps(bad)));
        var conn = new DriveableConnection("M0LTE-7", NodeTransportKind.Ax25);

        var ctx = new NodeAppContext { Callsign = "M0LTE-7", Transport = NodeTransportKind.Ax25 };
        await host.RunAsync(bad, conn, ctx);   // must not throw

        conn.Output.Should().ContainEquivalentOf("unavailable");
    }
}
