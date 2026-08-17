using Packet.Node.Core.Configuration;
using Packet.Node.Core.Discovery;
using Xunit;

namespace Packet.Node.Tests.Discovery;

public sealed class MdnsAdvertTests
{
    private static NodeConfig Cfg(
        bool enabled,
        string bind = "0.0.0.0",
        int port = 8080,
        string callsign = "M0LTE-7",
        string? alias = null,
        string? instance = null) =>
        new()
        {
            Identity = new Identity { Callsign = callsign, Alias = alias },
            Management = new ManagementConfig
            {
                Http = new HttpConfig { Bind = bind, Port = port },
                Mdns = new MdnsConfig { Enabled = enabled, InstanceName = instance },
            },
        };

    [Fact]
    public void Disabled_yields_no_plan()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: false), "1.0", out var reason);
        plan.Should().BeNull();
        reason.Should().Contain("enabled");
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.53")]
    [InlineData("::1")]
    [InlineData("localhost")]
    public void Loopback_bind_yields_no_plan(string bind)
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, bind: bind), "1.0", out var reason);
        plan.Should().BeNull();
        reason.Should().Contain("loopback");
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.168.1.10")]
    public void Non_loopback_bind_advertises(string bind)
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, bind: bind), "1.0", out var reason);
        plan.Should().NotBeNull();
        reason.Should().BeNull();
    }

    [Fact]
    public void Plan_carries_callsign_as_instance_and_cs_txt()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, callsign: "M0LTE-7", port: 8080), "0.18.1", out _);
        plan.Should().NotBeNull();
        plan!.Instance.Should().Be("M0LTE-7");
        plan.Port.Should().Be(8080);
        plan.Txt.Should().Contain("cs=M0LTE-7");
    }

    [Fact]
    public void Alias_rides_a_name_txt_but_callsign_stays_the_instance()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, callsign: "M0LTE-7", alias: "RDGBBS"), "0.18.1", out _);
        plan.Should().NotBeNull();
        plan!.Instance.Should().Be("M0LTE-7"); // identity stays the callsign, not the (collidable) alias
        plan.Txt.Should().Contain("name=RDGBBS");
    }

    [Fact]
    public void No_alias_means_no_name_txt()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, alias: null), "0.18.1", out _);
        plan!.Txt.Should().NotContain(t => t.StartsWith("name=", System.StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0.18.1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Version_txt_present_only_when_version_is_set(string? version, bool expected)
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true), version, out _);
        plan!.Txt.Any(t => t.StartsWith("v=", System.StringComparison.Ordinal)).Should().Be(expected);
    }

    [Fact]
    public void Explicit_instance_name_overrides_the_callsign_default()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, callsign: "M0LTE-7", instance: "Hilltop"), "1.0", out _);
        plan!.Instance.Should().Be("Hilltop");
        plan.Txt.Should().Contain("cs=M0LTE-7"); // cs is still the callsign, not the display name
    }

    [Fact]
    public void ToAvahiArgs_is_f_s_endopts_instance_type_port_then_txt()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, callsign: "M0LTE-7", alias: "RDGBBS", port: 8080), "0.18.1", out _);
        var args = plan!.ToAvahiArgs();
        args[0].Should().Be("-f");   // --no-fail: wait for / reattach to avahi-daemon
        args[1].Should().Be("-s");
        args[2].Should().Be("--");   // end options: an instance name can't be read as a flag
        args[3].Should().Be("M0LTE-7");
        args[4].Should().Be("_pdn._tcp");
        args[5].Should().Be("8080");
        args.Should().Contain("cs=M0LTE-7");
        args.Should().Contain("name=RDGBBS");
        args.Should().Contain("v=0.18.1");
    }

    [Fact]
    public void Out_of_range_port_yields_no_plan()
    {
        var plan = MdnsAdvert.Plan(Cfg(enabled: true, port: 0), "1.0", out var reason);
        plan.Should().BeNull();
        reason.Should().Contain("port");
    }
}
