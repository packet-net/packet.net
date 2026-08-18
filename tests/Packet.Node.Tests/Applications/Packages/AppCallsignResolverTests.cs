using Packet.Core;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The node-as-callsign-authority resolver (<see cref="AppCallsignResolver"/>,
/// <c>docs/app-packages.md</c> § Application packet identity): pins (full + <c>-N</c> suffix),
/// auto-assignment to the lowest free SSID, dedup against the node + other apps, determinism,
/// and the back-compat carve-out (a pure session app gets no callsign).
/// </summary>
[Trait("Category", "Node")]
public sealed class AppCallsignResolverTests
{
    private static NodeConfig Node(string callsign = "M0LTE-1", params ApplicationConfig[] apps) => new()
    {
        Identity = new Identity { Callsign = callsign },
        Applications = apps,
    };

    private static ApplicationConfig PacketApp(string id, string? callsign = null, AppNetromConfig? netrom = null) =>
        new()
        {
            Id = id,
            Command = id.ToUpperInvariant(),
            Executable = "/bin/cat",
            Capabilities = ["packet"],
            Callsign = callsign,
            Netrom = netrom,
        };

    private static DiscoveredAppPackage Package(
        string id, bool enabled = true, IReadOnlyList<string>? caps = null,
        string? pinnedCallsign = null, AppNetromConfig? netrom = null, bool session = false, string? error = null) => new()
        {
            Id = id,
            PackageDir = $"/tmp/{id}",
            StateDir = $"/tmp/{id}/state",
            Enabled = enabled,
            Error = error,
            Manifest = new AppPackageManifest
            {
                Manifest = 1,
                Id = id,
                Capabilities = caps ?? ["packet"],
                Session = session ? new AppSessionSpec { Kind = ApplicationKind.Socket, SocketPath = "/run/x.sock" } : null,
                Service = new AppServiceSpec { Command = "/bin/sh" },
            },
            Override = (pinnedCallsign is null && netrom is null)
                ? new AppOverrideConfig { Id = id, Enabled = enabled }
                : new AppOverrideConfig { Id = id, Enabled = enabled, Callsign = pinnedCallsign, Netrom = netrom },
        };

    [Fact]
    public void A_full_callsign_pin_is_used_verbatim()
    {
        var cfg = Node("M0LTE-1", PacketApp("bbs", callsign: "M9YYY-3"));
        var map = AppCallsignResolver.Resolve(cfg, []);

        map["bbs"].Callsign.Should().Be(new Callsign("M9YYY", 3));
        map["bbs"].Pinned.Should().BeTrue();
    }

    [Fact]
    public void A_bare_ssid_suffix_pin_appends_to_the_node_base()
    {
        var cfg = Node("M0LTE-1", PacketApp("dapps", callsign: "-7"));
        var map = AppCallsignResolver.Resolve(cfg, []);

        map["dapps"].Callsign.Should().Be(new Callsign("M0LTE", 7));
        map["dapps"].Pinned.Should().BeTrue();
    }

    [Fact]
    public void Auto_assignment_picks_the_lowest_free_ssid_skipping_the_node_ssid()
    {
        // Node is M0LTE-1 → SSID 1 is taken; two auto apps get the next lowest (2, then 3).
        var cfg = Node("M0LTE-1", PacketApp("a"), PacketApp("b"));
        var map = AppCallsignResolver.Resolve(cfg, []);

        map["a"].Callsign.Should().Be(new Callsign("M0LTE", 2));
        map["a"].Pinned.Should().BeFalse();
        map["b"].Callsign.Should().Be(new Callsign("M0LTE", 3));
    }

    [Fact]
    public void Auto_assignment_skips_an_ssid_a_pin_already_took()
    {
        // App x is pinned to -2; the auto app y must skip 1 (node) and 2 (pin) → 3.
        var cfg = Node("M0LTE-1", PacketApp("x", callsign: "-2"), PacketApp("y"));
        var map = AppCallsignResolver.Resolve(cfg, []);

        map["x"].Callsign.Should().Be(new Callsign("M0LTE", 2));
        map["y"].Callsign.Should().Be(new Callsign("M0LTE", 3));
    }

    [Fact]
    public void Pins_are_reserved_before_auto_assign_regardless_of_order()
    {
        // The auto app comes FIRST in config order, the pin (-2) second. The auto app must
        // still avoid 2 - pins are reserved in a pass before any auto-assign.
        var cfg = Node("M0LTE-0", PacketApp("auto"), PacketApp("pin", callsign: "-2"));
        var map = AppCallsignResolver.Resolve(cfg, []);

        map["pin"].Callsign.Should().Be(new Callsign("M0LTE", 2));
        // node SSID 0 is taken; 1 is the lowest free (2 reserved by the pin) → auto gets 1.
        map["auto"].Callsign.Should().Be(new Callsign("M0LTE", 1));
    }

    [Fact]
    public void Resolution_is_deterministic_inline_first_then_packages_by_id()
    {
        var cfg = Node("M0LTE-1", PacketApp("inline"));
        var packages = new[] { Package("zeta"), Package("alpha") };

        var map1 = AppCallsignResolver.Resolve(cfg, packages);
        var map2 = AppCallsignResolver.Resolve(cfg, packages);

        map1["inline"].Callsign.Should().Be(map2["inline"].Callsign);
        map1["alpha"].Callsign.Should().Be(map2["alpha"].Callsign);
        map1["zeta"].Callsign.Should().Be(map2["zeta"].Callsign);
        // inline first (SSID 2), then packages by id: alpha (3), zeta (4).
        map1["inline"].Callsign.Should().Be(new Callsign("M0LTE", 2));
        map1["alpha"].Callsign.Should().Be(new Callsign("M0LTE", 3));
        map1["zeta"].Callsign.Should().Be(new Callsign("M0LTE", 4));
    }

    [Fact]
    public void A_pure_session_app_with_no_packet_capability_or_pin_gets_no_callsign()
    {
        // A session-only package (e.g. WALL/LOBBY: caps=[session], no pin/netrom) binds none.
        var cfg = Node("M0LTE-1");
        var packages = new[] { Package("lobby", caps: ["session"], session: true) };

        var map = AppCallsignResolver.Resolve(cfg, packages);

        map.Should().NotContainKey("lobby");
    }

    [Fact]
    public void A_netrom_alias_alone_forces_a_callsign_even_without_packet_capability()
    {
        var cfg = Node("M0LTE-1");
        var packages = new[]
        {
            Package("svc", caps: ["session"], netrom: new AppNetromConfig { Alias = "RDGBBS" }),
        };

        var map = AppCallsignResolver.Resolve(cfg, packages);

        map.Should().ContainKey("svc");
        map["svc"].Callsign.Should().Be(new Callsign("M0LTE", 2));   // node-1 taken → 2.
    }

    [Fact]
    public void A_disabled_or_broken_package_is_not_resolved()
    {
        var cfg = Node("M0LTE-1");
        var packages = new[]
        {
            Package("off", enabled: false),
            Package("broken", error: "bad manifest"),
        };

        var map = AppCallsignResolver.Resolve(cfg, packages);

        map.Should().BeEmpty();
    }

    [Fact]
    public void An_unparsable_pin_drops_the_app_rather_than_auto_assigning_over_owner_intent()
    {
        var cfg = Node("M0LTE-1", PacketApp("bad", callsign: "not a callsign!"));
        var map = AppCallsignResolver.Resolve(cfg, []);

        map.Should().NotContainKey("bad");
    }

    [Theory]
    [InlineData("-7", "M0LTE", 7, true)]
    [InlineData("M9YYY-3", "M9YYY", 3, false)]   // a full callsign off the node base - honoured, not on-base.
    [InlineData("M0LTE-4", "M0LTE", 4, true)]    // a full callsign ON the node base - on-base.
    public void TryResolvePin_handles_suffix_and_full_forms(string pin, string expectBase, int expectSsid, bool onNodeBase)
    {
        AppCallsignResolver.TryResolvePin(pin, "M0LTE", out var call, out var onBase).Should().BeTrue();
        call.Should().Be(new Callsign(expectBase, (byte)expectSsid));
        onBase.Should().Be(onNodeBase);
    }
}
