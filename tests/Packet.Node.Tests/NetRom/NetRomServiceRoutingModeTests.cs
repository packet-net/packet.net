using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.NetRom;

namespace Packet.Node.Tests.NetRom;

/// <summary>
/// <see cref="NetRomService"/> gates its connected-mode behaviours on the resolved
/// <see cref="NetRomRouting"/> role:
/// <list type="bullet">
/// <item><see cref="NetRomRouting.None"/> - passive: no interlinks (no
/// <see cref="NetRomService.Circuits"/>), no transit forwarding.</item>
/// <item><see cref="NetRomRouting.Endpoint"/> - interlinks/circuits are built (our own
/// <c>connect &lt;alias&gt;</c>), but transit forwarding is OFF.</item>
/// <item><see cref="NetRomRouting.Transit"/> - both interlinks AND transit forwarding.</item>
/// </list>
/// These are the same gates the old <c>connect</c>/<c>forward</c> bools drove, expressed
/// against the new single knob (and verified to hold via the legacy back-compat mapping too).
/// </summary>
[Trait("Category", "Node")]
public sealed class NetRomServiceRoutingModeTests
{
    private static NetRomService Build(NetRomConfig cfg)
        => new(cfg, TimeProvider.System, NullLogger<NetRomService>.Instance);

    [Fact]
    public void None_has_no_interlinks_and_no_transit()
    {
        using var svc = Build(new NetRomConfig { Enabled = true, Routing = NetRomRouting.None });
        svc.Enabled.Should().BeTrue("the node still hears NODES");
        svc.ConnectEnabled.Should().BeFalse("None opens no interlinks");
        svc.ForwardEnabled.Should().BeFalse("None relays no transit");
        svc.Circuits.Should().BeNull("the circuit manager is not constructed without interlinks");
    }

    [Fact]
    public void Endpoint_has_interlinks_but_no_transit()
    {
        using var svc = Build(new NetRomConfig { Enabled = true, Routing = NetRomRouting.Endpoint });
        svc.ConnectEnabled.Should().BeTrue("Endpoint opens interlinks for our own connect <alias>");
        svc.ForwardEnabled.Should().BeFalse("Endpoint is endpoint-only — it does not relay transit");
        svc.Circuits.Should().NotBeNull("the L4 circuit manager is built for our own circuits");
    }

    [Fact]
    public void Transit_has_both_interlinks_and_transit()
    {
        using var svc = Build(new NetRomConfig { Enabled = true, Routing = NetRomRouting.Transit });
        svc.ConnectEnabled.Should().BeTrue("Transit opens interlinks");
        svc.ForwardEnabled.Should().BeTrue("Transit relays third-party transit datagrams");
        svc.Circuits.Should().NotBeNull();
    }

    // The /metrics forwarding bucket (#457): a freshly-built service has forwarded nothing, so
    // every counter is zero. (The increment paths are wire-driven and covered through the
    // ForwardDatagram path / the exporter's all-zero endpoint assertion.)
    [Fact]
    public void ForwardingStats_start_at_zero()
    {
        using var svc = Build(new NetRomConfig { Enabled = true, Routing = NetRomRouting.Transit });
        var stats = svc.ForwardingStats;
        stats.ForwardedFrames.Should().Be(0);
        stats.ForwardedBytes.Should().Be(0);
        stats.DroppedTtlExpired.Should().Be(0);
        stats.DroppedLooped.Should().Be(0);
        stats.DroppedNoRoute.Should().Be(0);
        stats.DroppedTotal.Should().Be(0);
    }

    [Fact]
    public void Disabled_node_never_routes_even_with_transit_requested()
    {
        // Enabled:false short-circuits every routing behaviour (the validator also rejects
        // this combo, but the service must be safe regardless).
        using var svc = Build(new NetRomConfig { Enabled = false, Routing = NetRomRouting.Transit });
        svc.Enabled.Should().BeFalse();
        svc.ConnectEnabled.Should().BeFalse();
        svc.ForwardEnabled.Should().BeFalse();
        svc.Circuits.Should().BeNull();
    }

    // ── Legacy back-compat: the old bools drive the same gates via ResolveRouting ──
    [Fact]
    public void Legacy_connect_true_forward_default_behaves_as_Transit()
    {
        using var svc = Build(new NetRomConfig { Enabled = true, Connect = true });
        svc.ConnectEnabled.Should().BeTrue();
        svc.ForwardEnabled.Should().BeTrue("connect:true with forward defaulted on is the full router");
        svc.Circuits.Should().NotBeNull();
    }

    [Fact]
    public void Legacy_connect_true_forward_false_behaves_as_Endpoint()
    {
        using var svc = Build(new NetRomConfig { Enabled = true, Connect = true, Forward = false });
        svc.ConnectEnabled.Should().BeTrue();
        svc.ForwardEnabled.Should().BeFalse("connect:true,forward:false is endpoint-only");
        svc.Circuits.Should().NotBeNull();
    }

    [Fact]
    public void Legacy_forward_true_without_connect_behaves_as_None()
    {
        // The contradictory combo that was always inert: it must not become a router.
        using var svc = Build(new NetRomConfig { Enabled = true, Forward = true });
        svc.ConnectEnabled.Should().BeFalse();
        svc.ForwardEnabled.Should().BeFalse();
        svc.Circuits.Should().BeNull();
    }
}
