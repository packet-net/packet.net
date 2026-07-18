using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Tests.Hosting;

/// <summary>
/// Example-based tests pinning the operator-facing <see cref="ReconcilePreview"/>:
/// each kind of edit lands in the right disruption bucket (apply live · restart a
/// port · reset the node). The field-level <see cref="ReconcilePlan"/> classification
/// is pinned next door in <see cref="ReconcilePlannerTests"/>; these assert the
/// preview grouping the config editor actually shows.
/// </summary>
public class ReconcilePreviewBuilderTests
{
    private static NodeConfig Base() => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1", Alias = "LONDON", Grid = "IO91wm" },
        Ports = [],
    };

    private static PortConfig Tcp(string id, int port = 8001) => new()
    {
        Id = id,
        Enabled = true,
        Transport = new KissTcpTransport { Host = "127.0.0.1", Port = port },
    };

    [Fact]
    public void A_callsign_change_is_a_node_reset()
    {
        var baseline = Base();
        var to = baseline with { Identity = baseline.Identity with { Callsign = "G7XYZ-7" } };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        preview.NodeReset.Should().ContainSingle()
            .Which.Path.Should().Be("identity.callsign");
    }

    [Fact]
    public void Adding_an_enabled_port_is_a_port_restart()
    {
        var baseline = Base();
        var to = baseline with { Ports = [Tcp("vhf")] };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        preview.PortRestart.Should().ContainSingle()
            .Which.Path.Should().Be("ports.vhf");
    }

    [Fact]
    public void A_netrom_change_is_a_node_reset_with_the_restart_note()
    {
        var baseline = Base();
        var to = baseline with { NetRom = baseline.NetRom with { Broadcast = !baseline.NetRom.Broadcast } };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        var change = preview.NodeReset.Should().ContainSingle().Subject;
        change.Path.Should().Be("netRom");
        change.Summary.Should().Contain("after a node restart");
    }

    [Fact]
    public void An_ardop_change_is_itemised_as_a_port_restart()
    {
        var baseline = Base();
        var to = baseline with { Ardop = baseline.Ardop with { Enabled = true, Device = "flex:mock" } };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        var change = preview.PortRestart.Should().ContainSingle().Subject;
        change.Path.Should().Be("ardop");
        change.Summary.Should().Contain("ARDOP");
    }

    [Fact]
    public void A_paging_change_is_itemised_as_a_port_restart()
    {
        var baseline = Base();
        var to = baseline with { Paging = baseline.Paging with { Enabled = true, Baud = 512 } };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        var change = preview.PortRestart.Should().ContainSingle().Subject;
        change.Path.Should().Be("paging");
        change.Summary.Should().Contain("POCSAG");
    }

    [Fact]
    public void A_grid_change_is_a_live_change()
    {
        var baseline = Base();
        var to = baseline with { Identity = baseline.Identity with { Grid = "JO01" } };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        preview.Live.Should().ContainSingle()
            .Which.Path.Should().Be("identity.grid");
    }

    [Fact]
    public void A_compat_profile_change_is_a_live_change()
    {
        var baseline = Base() with { Ports = [Tcp("vhf")] };
        var to = baseline with
        {
            Ports = [Tcp("vhf") with { Compat = new PortCompatConfig { Preset = "strict" } }],
        };

        var preview = ReconcilePreviewBuilder.Build(baseline, to);

        preview.Live.Should().ContainSingle()
            .Which.Path.Should().Be("ports.vhf.compat");
        preview.PortRestart.Should().BeEmpty();
        preview.NodeReset.Should().BeEmpty();
    }

    [Fact]
    public void An_identical_config_yields_no_changes()
    {
        var c = Base();

        var preview = ReconcilePreviewBuilder.Build(c, c);

        preview.Live.Should().BeEmpty();
        preview.PortRestart.Should().BeEmpty();
        preview.NodeReset.Should().BeEmpty();
    }
}
