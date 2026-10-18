using Packet.Core;
using Packet.NetRom.Wire;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;

namespace Packet.Node.Tests.Applications.Packages;

/// <summary>
/// The opt-in app NET/ROM advert projection (<see cref="AppNetromAdvert"/>,
/// <c>docs/app-packages.md</c> § Application packet identity): an app advertises an alias only
/// when its owner set <c>netrom.alias</c> (off by default), at the configured quality, pointing
/// the alias at the app's resolved callsign via this node.
/// </summary>
[Trait("Category", "Node")]
public sealed class AppNetromAdvertTests
{
    private static readonly Callsign NodeCall = new("M0LTE", 1);

    private static NodeConfig Node(params ApplicationConfig[] apps) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Applications = apps,
    };

    private static ApplicationConfig App(string id, AppNetromConfig? netrom = null, string? callsign = null) =>
        new()
        {
            Id = id,
            Command = id.ToUpperInvariant(),
            Executable = "/bin/cat",
            Capabilities = ["packet"],
            Callsign = callsign,
            Netrom = netrom,
        };

    [Fact]
    public void No_alias_means_nothing_is_advertised()
    {
        // A packet app with a resolved callsign but NO netrom block contributes nothing.
        var cfg = Node(App("bbs"));
        var entries = AppNetromAdvert.Build(cfg, [], NodeCall);

        entries.Should().BeEmpty();
    }

    [Fact]
    public void An_alias_is_advertised_at_the_resolved_callsign_via_this_node()
    {
        var cfg = Node(App("bbs", new AppNetromConfig { Alias = "RDGBBS", Quality = 200 }, callsign: "-7"));
        var entries = AppNetromAdvert.Build(cfg, [], NodeCall);

        var e = entries.Should().ContainSingle().Subject;
        e.Destination.Should().Be(new Callsign("M0LTE", 7));   // the app's resolved (pinned) callsign
        e.DestinationAlias.Should().Be("RDGBBS");
        e.BestNeighbour.Should().Be(NodeCall);                  // reachable via this node
        e.Quality.Should().Be((byte)200);
    }

    [Fact]
    public void Quality_defaults_to_255_when_unset()
    {
        var cfg = Node(App("bbs", new AppNetromConfig { Alias = "RDGBBS" }, callsign: "-7"));
        var entries = AppNetromAdvert.Build(cfg, [], NodeCall);

        entries.Should().ContainSingle().Which.Quality.Should().Be((byte)AppNetromConfig.DefaultQuality);
    }

    [Fact]
    public void A_disabled_app_advertises_nothing()
    {
        var cfg = Node(App("bbs", new AppNetromConfig { Alias = "RDGBBS" }, callsign: "-7") with { Enabled = false });
        var entries = AppNetromAdvert.Build(cfg, [], NodeCall);

        entries.Should().BeEmpty();
    }

    [Fact]
    public void Two_apps_each_advertise_their_own_alias()
    {
        var cfg = Node(
            App("bbs", new AppNetromConfig { Alias = "RDGBBS" }, callsign: "-7"),
            App("chat", new AppNetromConfig { Alias = "RDGCHAT" }, callsign: "-8"));
        var entries = AppNetromAdvert.Build(cfg, [], NodeCall);

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.DestinationAlias == "RDGBBS" && e.Destination.Equals(new Callsign("M0LTE", 7)));
        entries.Should().Contain(e => e.DestinationAlias == "RDGCHAT" && e.Destination.Equals(new Callsign("M0LTE", 8)));
    }

    [Fact]
    public void A_quality_0_alias_is_still_projected_so_its_callsign_stays_local()
    {
        // packet.net#939: quality 0 keeps the alias out of NODES (NetRomService filters it at
        // broadcast), but the entry is still returned so the node keeps treating the app's
        // callsign as its own, as BPQ does for an APPLCALL at quality 0.
        var cfg = Node(App("telstar", new AppNetromConfig { Alias = "TELSTR", Quality = 0 }, callsign: "-9"));
        var entries = AppNetromAdvert.Build(cfg, [], NodeCall);

        var e = entries.Should().ContainSingle().Subject;
        e.Destination.Should().Be(new Callsign("M0LTE", 9));
        e.Quality.Should().Be((byte)0);
    }
}
