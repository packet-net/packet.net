using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Tests.Hosting;

/// <summary>
/// Example-based tests pinning the reconcile-delta classification: each kind of
/// config edit lands in exactly the right bucket of the <see cref="ReconcilePlan"/>.
/// The marquee invariant (a delta disrupts only the ports whose restart-class
/// fields changed) is property-tested separately; these nail the specific arms.
/// </summary>
public class ReconcilePlannerTests
{
    private static NodeConfig Config(string callsign, params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = callsign },
        Ports = ports,
    };

    private static PortConfig Tcp(string id, int port = 8001, bool enabled = true,
        KissParams? kiss = null, Ax25PortParams? ax25 = null) => new()
        {
            Id = id,
            Enabled = enabled,
            Transport = new KissTcpTransport { Host = "127.0.0.1", Port = port },
            Kiss = kiss,
            Ax25 = ax25,
        };

    [Fact]
    public void Same_config_twice_is_a_noop()
    {
        var c = Config("M0LTE-1", Tcp("a"), Tcp("b", 8002));
        var plan = ReconcilePlanner.Plan(c, c);
        plan.IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void Added_enabled_port_brings_up_only_it()
    {
        var before = Config("M0LTE-1", Tcp("a"));
        var to = Config("M0LTE-1", Tcp("a"), Tcp("b", 8002));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ToBringUp.Select(p => p.Id).Should().Equal("b");
        plan.ToTearDown.Should().BeEmpty();
        plan.ToRestart.Should().BeEmpty();
    }

    [Fact]
    public void Removed_port_tears_down_only_it()
    {
        var before = Config("M0LTE-1", Tcp("a"), Tcp("b", 8002));
        var to = Config("M0LTE-1", Tcp("a"));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ToTearDown.Should().Equal("b");
        plan.ToBringUp.Should().BeEmpty();
    }

    [Fact]
    public void Transport_change_is_a_single_port_restart()
    {
        var before = Config("M0LTE-1", Tcp("a", 8001), Tcp("b", 8002));
        var to = Config("M0LTE-1", Tcp("a", 9999), Tcp("b", 8002));   // a's port changed
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ToRestart.Select(p => p.Id).Should().Equal("a");
        plan.ToBringUp.Should().BeEmpty();
        plan.ToTearDown.Should().BeEmpty();
        plan.KissParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void Enabled_toggle_off_disables_and_on_enables()
    {
        var up = Config("M0LTE-1", Tcp("a", enabled: true));
        var down = Config("M0LTE-1", Tcp("a", enabled: false));

        ReconcilePlanner.Plan(up, down).ToDisable.Should().Equal("a");
        ReconcilePlanner.Plan(down, up).ToEnable.Select(p => p.Id).Should().Equal("a");
    }

    [Fact]
    public void Kiss_param_change_only_is_hot_no_restart()
    {
        var before = Config("M0LTE-1", Tcp("a", kiss: new KissParams { TxDelay = 30 }));
        var to = Config("M0LTE-1", Tcp("a", kiss: new KissParams { TxDelay = 50 }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.KissParamsChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.Ax25ParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void Ackmode_toggle_is_a_single_port_restart_not_a_live_kiss_apply()
    {
        // kiss.ackMode decides whether the modem is wrapped in the PacingKissModem
        // decorator at construction time, so it cannot be applied live like the other
        // KISS knobs — toggling it must RESTART the port so the change takes effect,
        // not land in the (no-op-for-ackmode) live KissParamsChanged bucket.
        var before = Config("M0LTE-1", Tcp("a", kiss: new KissParams { AckMode = false }));
        var to = Config("M0LTE-1", Tcp("a", kiss: new KissParams { AckMode = true }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ToRestart.Select(p => p.Id).Should().Equal("a");
        plan.KissParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void T1FromTxComplete_toggle_is_a_single_port_restart_not_a_live_kiss_apply()
    {
        // kiss.t1FromTxComplete changes how the listener sends (decided at listener
        // construction), so like ackMode it needs the restart to take effect.
        var before = Config("M0LTE-1", Tcp("a", kiss: new KissParams { AckMode = true }));
        var to = Config("M0LTE-1", Tcp("a", kiss: new KissParams { AckMode = true, T1FromTxComplete = true }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ToRestart.Select(p => p.Id).Should().Equal("a");
        plan.KissParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void Radio_attachment_change_is_a_single_port_restart()
    {
        // port.radio is construction-time: the radio control channel is opened and the
        // RSSI-tagging transport wrap decided at bring-up. Adding, removing, or
        // re-pointing it must restart the port — never a (no-op) hot apply.
        static PortConfig Serial(string id, PortRadioConfig? radio) => new()
        {
            Id = id,
            Transport = new SerialKissTransport { Device = "/dev/ttyACM0" },
            Radio = radio,
        };
        var tait = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0", Baud = 28800 };

        var without = Config("M0LTE-1", Serial("a", null));
        var with = Config("M0LTE-1", Serial("a", tait));
        var moved = Config("M0LTE-1", Serial("a", tait with { Port = "/dev/ttyUSB1" }));

        // Added, removed, and re-pointed — each is a restart of exactly port a.
        foreach (var (from, to) in new[] { (without, with), (with, without), (with, moved) })
        {
            var plan = ReconcilePlanner.Plan(from, to);
            plan.ToRestart.Select(p => p.Id).Should().Equal("a");
            plan.KissParamsChanged.Should().BeEmpty();
            plan.Ax25ParamsChanged.Should().BeEmpty();
        }

        // Identical radio blocks (value equality) — a no-op.
        ReconcilePlanner.Plan(with, Config("M0LTE-1", Serial("a", tait with { }))).IsNoOp.Should().BeTrue();
    }

    [Fact]
    public void Ackmode_unchanged_with_other_kiss_change_stays_a_live_apply()
    {
        // ackMode steady (here: both on); only TXDELAY moved → still the hot live path.
        var before = Config("M0LTE-1", Tcp("a", kiss: new KissParams { AckMode = true, TxDelay = 30 }));
        var to = Config("M0LTE-1", Tcp("a", kiss: new KissParams { AckMode = true, TxDelay = 50 }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.KissParamsChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
    }

    [Fact]
    public void Ax25_param_change_only_is_hot_no_restart()
    {
        // The AX.25-params-only change is HOT: classified into Ax25ParamsChanged
        // with no restart class. The supervisor live-reseeds the running listener
        // (UpdateSessionParameters) so new sessions pick up the params while
        // existing sessions are untouched — see ReconfigDeltaIntegrationTests.
        var before = Config("M0LTE-1", Tcp("a", ax25: new Ax25PortParams { N2 = 10 }));
        var to = Config("M0LTE-1", Tcp("a", ax25: new Ax25PortParams { N2 = 12 }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.Ax25ParamsChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.KissParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void N1_paclen_change_only_is_a_hot_ax25_reseed()
    {
        // N1 (PACLEN) rides the Ax25PortParams record, so an N1-only edit is the same
        // HOT live-reseed as any other AX.25 param — no restart, new sessions only.
        var before = Config("M0LTE-1", Tcp("a", ax25: new Ax25PortParams { N1 = 256 }));
        var to = Config("M0LTE-1", Tcp("a", ax25: new Ax25PortParams { N1 = 80 }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.Ax25ParamsChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.NetRomQualityChanged.Should().BeEmpty();
    }

    [Fact]
    public void NetRom_quality_change_only_is_hot_no_restart()
    {
        // A per-port NET/ROM QUALITY edit is the lightest hot class — applied by swapping
        // the port's NET/ROM attachment quality (read-only awareness, never touches a
        // session). It must not trip a restart or an AX.25 reseed.
        var before = Config("M0LTE-1", Tcp("a") with { NetRomQuality = 192 });
        var to = Config("M0LTE-1", Tcp("a") with { NetRomQuality = 191 });
        var plan = ReconcilePlanner.Plan(before, to);

        plan.NetRomQualityChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.Ax25ParamsChanged.Should().BeEmpty();
        plan.KissParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void NetRom_minquality_change_only_is_hot_no_restart()
    {
        // A per-port MINQUAL edit is the same lightest hot class as QUALITY — read-only
        // route-keep awareness, never a session. It joins NetRomQualityChanged for the
        // light-touch hot-apply.
        var before = Config("M0LTE-1", Tcp("a") with { NetRomMinQuality = 0 });
        var to = Config("M0LTE-1", Tcp("a") with { NetRomMinQuality = 100 });
        var plan = ReconcilePlanner.Plan(before, to);

        plan.NetRomQualityChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.Ax25ParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void Nodespaclen_change_only_is_hot_no_restart()
    {
        // A per-port NODESPACLEN edit only changes how the next NODES broadcast is framed
        // (outbound advertisement) — never a session — so it is the same light-touch hot class.
        var before = Config("M0LTE-1", Tcp("a"));   // NodesPaclen null (no cap)
        var to = Config("M0LTE-1", Tcp("a") with { NodesPaclen = 160 });
        var plan = ReconcilePlanner.Plan(before, to);

        plan.NetRomQualityChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.Ax25ParamsChanged.Should().BeEmpty();
    }

    [Fact]
    public void Compat_change_only_is_hot_no_restart()
    {
        // A compat-profile-only change is HOT: classified into CompatChanged with
        // no restart class. The supervisor live-reseeds the running listener so
        // parse options apply from the next inbound frame and quirks seed new
        // sessions — existing sessions untouched.
        var before = Config("M0LTE-1", Tcp("a"));
        var to = Config("M0LTE-1", Tcp("a") with { Compat = new PortCompatConfig { Preset = "bpq" } });
        var plan = ReconcilePlanner.Plan(before, to);

        plan.CompatChanged.Select(p => p.Id).Should().Equal("a");
        plan.ToRestart.Should().BeEmpty();
        plan.Ax25ParamsChanged.Should().BeEmpty();
        plan.KissParamsChanged.Should().BeEmpty();
        plan.IsNoOp.Should().BeFalse();
    }

    [Fact]
    public void Callsign_change_is_a_node_wide_reset()
    {
        var before = Config("M0LTE-1", Tcp("a"), Tcp("b", 8002));
        var to = Config("G7XYZ-7", Tcp("a"), Tcp("b", 8002));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.NodeWideReset.Should().BeTrue();
        plan.ToBringUp.Select(p => p.Id).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void Telnet_change_restarts_only_telnet()
    {
        var before = Config("M0LTE-1", Tcp("a"));
        var to = before with { Management = new ManagementConfig { Telnet = new TelnetConfig { Port = 9999 } } };
        var plan = ReconcilePlanner.Plan(before, to);

        plan.TelnetChanged.Should().BeTrue();
        plan.ToRestart.Should().BeEmpty();
        plan.ToBringUp.Should().BeEmpty();
    }

    [Fact]
    public void Services_change_is_a_reference_swap_only()
    {
        var before = Config("M0LTE-1", Tcp("a"));
        var to = before with { Services = new ServicesConfig { Banner = "new banner" } };
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ServicesChanged.Should().BeTrue();
        plan.ToRestart.Should().BeEmpty();
        plan.IsNoOp.Should().BeFalse();
    }

    [Fact]
    public void Transport_change_subsumes_a_simultaneous_kiss_change_no_double_action()
    {
        // A port that changes BOTH transport and kiss params restarts (which
        // re-applies kiss on bring-up) — it must not ALSO appear in KissParamsChanged.
        var before = Config("M0LTE-1", Tcp("a", 8001, kiss: new KissParams { TxDelay = 10 }));
        var to = Config("M0LTE-1", Tcp("a", 9002, kiss: new KissParams { TxDelay = 20 }));
        var plan = ReconcilePlanner.Plan(before, to);

        plan.ToRestart.Select(p => p.Id).Should().Equal("a");
        plan.KissParamsChanged.Should().BeEmpty();
    }
}
