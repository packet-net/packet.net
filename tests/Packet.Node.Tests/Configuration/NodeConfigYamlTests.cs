using Packet.Node.Core.Configuration;
using Packet.NetRom.Wire;

namespace Packet.Node.Tests.Configuration;

public class NodeConfigYamlTests
{
    [Fact]
    public void Parses_a_full_config_with_all_three_transport_kinds()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
              alias: LONDON
              grid: IO91wm
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                ax25:
                  t1Ms: 3000
                  n2: 10
                  windowSize: 4
                kiss:
                  txDelay: 30
                  persistence: 63
              - id: hf
                enabled: false
                transport:
                  kind: serial-kiss
                  device: /dev/ttyACM0
                  baud: 57600
              - id: nino
                enabled: true
                transport:
                  kind: nino-tnc
                  device: /dev/ttyACM1
                  baud: 921600
                  mode: 6
            services:
              banner: "Hi {node}"
              prompt: "{call}> "
            management:
              telnet:
                enabled: true
                bind: 127.0.0.1
                port: 8011
              http:
                bind: 0.0.0.0
                port: 8080
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.Identity.Callsign.Should().Be("M0LTE-1");
        config.Identity.Alias.Should().Be("LONDON");
        config.Ports.Should().HaveCount(3);

        var vhf = config.Ports[0];
        vhf.Id.Should().Be("vhf");
        vhf.Enabled.Should().BeTrue();
        vhf.Transport.Should().BeOfType<KissTcpTransport>()
            .Which.Port.Should().Be(8001);
        vhf.Ax25!.T1Ms.Should().Be(3000);
        vhf.Kiss!.TxDelay.Should().Be((byte)30);

        config.Ports[1].Transport.Should().BeOfType<SerialKissTransport>()
            .Which.Device.Should().Be("/dev/ttyACM0");

        var nino = config.Ports[2].Transport.Should().BeOfType<NinoTncTransport>().Subject;
        nino.Mode.Should().Be(6);
        nino.Baud.Should().Be(921600);

        config.Management.Telnet.Port.Should().Be(8011);
        config.Management.Http.Bind.Should().Be("0.0.0.0");

        // No netrom: block → the default (enabled, canonical knobs).
        config.NetRom.Enabled.Should().BeTrue();
        config.NetRom.MinQuality.Should().BeNull();
    }

    [Fact]
    public void Parses_a_netrom_block_with_overridden_knobs()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              broadcast: true
              connect: true
              alias: NODE
              defaultNeighbourQuality: 203
              minQuality: 150
              obsoleteInitial: 5
              obsoleteMinimum: 3
              sweepIntervalSeconds: 1800
              window: 7
              transportTimeoutSeconds: 8
              transportRetries: 5
              timeToLive: 30
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.NetRom.Enabled.Should().BeTrue();
        config.NetRom.Broadcast.Should().BeTrue();
        config.NetRom.Connect.Should().BeTrue();
        config.NetRom.Alias.Should().Be("NODE");
        config.NetRom.DefaultNeighbourQuality.Should().Be(203);
        config.NetRom.MinQuality.Should().Be(150);
        config.NetRom.ObsoleteInitial.Should().Be(5);
        config.NetRom.ObsoleteMinimum.Should().Be(3);
        config.NetRom.SweepIntervalSeconds.Should().Be(1800);
        config.NetRom.Window.Should().Be(7);
        config.NetRom.TransportTimeoutSeconds.Should().Be(8);
        config.NetRom.TransportRetries.Should().Be(5);
        config.NetRom.TimeToLive.Should().Be(30);
    }

    [Fact]
    public void Netrom_broadcast_and_connect_default_off()
    {
        // TX-bearing NET/ROM is opt-in: a stock node hears but does not transmit
        // NODES or open circuits until the operator turns them on.
        var config = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");
        config.NetRom.Broadcast.Should().BeFalse();
        config.NetRom.Connect.Should().BeFalse();
    }

    [Fact]
    public void Netrom_can_be_disabled()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: false
            """;

        NodeConfigYaml.Parse(yaml).NetRom.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Management_auth_defaults_off_and_round_trips_when_enabled()
    {
        // Default-off: an absent management.auth block leaves auth disabled — the
        // no-regression contract for the auth foundation.
        var defaulted = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");
        defaulted.Management.Auth.Enabled.Should().BeFalse();
        defaulted.Management.Auth.AccessTokenMinutes.Should().BeNull();

        // And an explicit on round-trips through serialise→parse.
        const string yaml = """
            identity:
              callsign: M0LTE-1
            management:
              auth:
                enabled: true
                accessTokenMinutes: 30
            """;
        var parsed = NodeConfigYaml.Parse(yaml);
        parsed.Management.Auth.Enabled.Should().BeTrue();
        parsed.Management.Auth.AccessTokenMinutes.Should().Be(30);

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(parsed));
        reparsed.Management.Auth.Should().Be(parsed.Management.Auth);
    }

    [Theory]
    [InlineData("serial-kiss")]
    [InlineData("nino-tnc")]
    [InlineData("kiss-tcp")]
    [InlineData("axudp")]
    public void Round_trips_each_transport_kind_through_serialise_then_parse(string kind)
    {
        TransportConfig transport = kind switch
        {
            "serial-kiss" => new SerialKissTransport { Device = "/dev/ttyUSB0", Baud = 115200 },
            "nino-tnc" => new NinoTncTransport { Device = "/dev/ttyACM3", Baud = 57600, Mode = 9 },
            "axudp" => new AxudpTransport { Host = "peer.local", Port = 10093, LocalPort = 10093 },
            _ => new KissTcpTransport { Host = "modem.local", Port = 8100 },
        };
        var original = new NodeConfig
        {
            Identity = new Identity { Callsign = "G7XYZ-7" },
            Ports = [new PortConfig { Id = "p1", Enabled = true, Transport = transport }],
        };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.Ports.Should().HaveCount(1);
        reparsed.Ports[0].Transport.Should().Be(transport);
        reparsed.Identity.Callsign.Should().Be("G7XYZ-7");
    }

    [Fact]
    public void Parses_an_axudp_transport_with_all_fields()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: tunnel
                enabled: true
                transport:
                  kind: axudp
                  host: 10.0.0.2
                  port: 10093
                  localPort: 10093
            """;

        var config = NodeConfigYaml.Parse(yaml);

        var axudp = config.Ports.Should().ContainSingle().Subject
            .Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.Host.Should().Be("10.0.0.2");
        axudp.Port.Should().Be(10093);
        axudp.LocalPort.Should().Be(10093);
        axudp.DescribeEndpoint().Should().Be("axudp:10.0.0.2:10093(local:10093)");
    }

    [Fact]
    public void Axudp_localPort_defaults_to_ephemeral_when_omitted()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: tunnel
                transport:
                  kind: axudp
                  host: peer.example
                  port: 10093
            """;

        var axudp = NodeConfigYaml.Parse(yaml).Ports[0].Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.LocalPort.Should().Be(0, "localPort defaults to 0 (ephemeral) when omitted");
    }

    [Fact]
    public void Axudp_tolerates_a_stale_includeFcs_key_from_a_pre_removal_config()
    {
        // 'includeFcs' was removed (AXUDP always carries the FCS — the FCS-less
        // opt-out interoperated with nothing; see docs/strict-vs-pragmatic-audit.md).
        // A config carrying the now-defunct key must still load: the transport
        // converter reads only the fields it knows, so a stale 'includeFcs:' is
        // ignored, not a parse error.
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: tunnel
                transport:
                  kind: axudp
                  host: 10.0.0.2
                  port: 10093
                  localPort: 10093
                  includeFcs: false
            """;

        var act = () => NodeConfigYaml.Parse(yaml);
        act.Should().NotThrow("a stale includeFcs key is ignored, so a pre-removal config still loads");
        var axudp = act().Ports[0].Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.Host.Should().Be("10.0.0.2");
        axudp.Port.Should().Be(10093);
        axudp.LocalPort.Should().Be(10093);
    }

    [Fact]
    public void Unknown_transport_kind_throws_a_clear_parse_error()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE
            ports:
              - id: bad
                transport:
                  kind: smoke-signals
                  host: 10.0.0.1
                  port: 10093
            """;

        var act = () => NodeConfigYaml.Parse(yaml);
        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain("smoke-signals");
    }

    [Fact]
    public void Parses_and_round_trips_a_ports_channel_profile()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                profile: slow-afsk1200
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
            """;

        var config = NodeConfigYaml.Parse(yaml);
        config.Ports[0].Profile.Should().Be("slow-afsk1200");

        // Round-trip: the profile survives serialise → parse.
        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.Ports[0].Profile.Should().Be("slow-afsk1200");
    }

    [Fact]
    public void Empty_or_comment_only_document_parses_to_a_default_shape_not_null()
    {
        var config = NodeConfigYaml.Parse("# just a comment\n");
        config.Should().NotBeNull();
        config.Ports.Should().BeEmpty();
    }

    [Fact]
    public void Netrom_inp3_defaults_to_disabled_when_no_inp3_block()
    {
        // The default-off proof at the config layer: a config with no inp3: block at
        // all yields Inp3 == NetRomInp3Options.Default ⇒ Enabled == false, so the
        // host creates no Inp3Host and behaves byte-for-byte as today.
        var config = NodeConfigYaml.Parse("identity:\n  callsign: M0LTE-1\n");
        config.NetRom.Inp3.Should().NotBeNull();
        config.NetRom.Inp3.Enabled.Should().BeFalse();
        config.NetRom.Inp3.Should().Be(NetRomInp3Options.Default);
    }

    [Fact]
    public void Parses_a_netrom_inp3_block_with_all_knobs()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            netRom:
              enabled: true
              inp3:
                enabled: true
                preferInp3Routes: true
                snttGainShift: 4
                probeUnknownCapability: false
                advertiseIpAccept: 4
                capabilityTextWidth: 12
                hopLimit: 20
                worsenThresholdMs: 750
                l3RttInterval: 00:01:30
                l3RttResetWindow: 00:05:00
                rifInterval: 00:10:00
                positiveDebounce: 00:00:03
            """;

        var inp3 = NodeConfigYaml.Parse(yaml).NetRom.Inp3;

        inp3.Enabled.Should().BeTrue();
        inp3.PreferInp3Routes.Should().BeTrue();
        inp3.SnttGainShift.Should().Be(4);
        inp3.ProbeUnknownCapability.Should().BeFalse();
        inp3.AdvertiseIpAccept.Should().Be(4);
        inp3.CapabilityTextWidth.Should().Be(12);
        inp3.HopLimit.Should().Be(20);
        inp3.WorsenThresholdMs.Should().Be(750);
        inp3.L3RttInterval.Should().Be(TimeSpan.FromSeconds(90));
        inp3.L3RttResetWindow.Should().Be(TimeSpan.FromMinutes(5));
        inp3.RifInterval.Should().Be(TimeSpan.FromMinutes(10));
        inp3.PositiveDebounce.Should().Be(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Round_trips_a_netrom_inp3_block_through_serialise_then_parse()
    {
        // A populated inp3: overlay survives serialise → parse intact — including the
        // TimeSpan-typed duration knobs (via YamlDotNet's built-in TimeSpan converter)
        // and the nullable advertiseIpAccept.
        var original = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            NetRom = new NetRomConfig
            {
                Enabled = true,
                Inp3 = new NetRomInp3Options
                {
                    Enabled = true,
                    PreferInp3Routes = true,
                    SnttGainShift = 5,
                    ProbeUnknownCapability = false,
                    AdvertiseIpAccept = 6,
                    CapabilityTextWidth = 10,
                    HopLimit = 15,
                    WorsenThresholdMs = 1500,
                    L3RttInterval = TimeSpan.FromSeconds(45),
                    L3RttResetWindow = TimeSpan.FromSeconds(200),
                    RifInterval = TimeSpan.FromSeconds(600),
                    PositiveDebounce = TimeSpan.FromSeconds(7),
                },
            },
        };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.NetRom.Inp3.Should().Be(original.NetRom.Inp3,
            "the whole inp3 overlay should round-trip\nYAML:\n{0}", yaml);
    }

    [Fact]
    public void Round_trips_a_default_disabled_inp3_overlay()
    {
        // The common case: INP3 left at its default (disabled). Serialising the
        // default config and parsing it back must still yield the disabled default —
        // OmitNull drops advertiseIpAccept, and the rest is the record default.
        var original = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };

        var yaml = NodeConfigYaml.Serialize(original);
        var reparsed = NodeConfigYaml.Parse(yaml);

        reparsed.NetRom.Inp3.Should().Be(NetRomInp3Options.Default,
            "the default disabled overlay should round-trip\nYAML:\n{0}", yaml);
        reparsed.NetRom.Inp3.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Parses_a_traffic_block()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            traffic:
              enabled: false
              path: /tmp/frames.db
              retentionDays: 7
              maxMb: 64
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.Traffic.Should().Be(new TrafficConfig
        {
            Enabled = false,
            Path = "/tmp/frames.db",
            RetentionDays = 7,
            MaxMb = 64,
        });
    }

    [Fact]
    public void An_absent_traffic_block_means_the_on_by_default_record()
    {
        // Existing configs have no traffic: key — they must come up logging with
        // the default bounds (enabled is the whole point of the feature).
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports: []
            """);

        config.Traffic.Should().Be(new TrafficConfig());
        config.Traffic.Enabled.Should().BeTrue();
        config.Traffic.RetentionDays.Should().Be(14);
        config.Traffic.MaxMb.Should().Be(512);
        config.Traffic.Path.Should().BeNull("null = traffic.db beside pdn.db");
    }

    [Fact]
    public void Template_ships_the_default_traffic_block_and_round_trips()
    {
        // The first-start template documents the block with its defaults spelled
        // out; parsing it must yield exactly the record defaults, and the parsed
        // config must serialise→parse back to the same traffic block.
        var fromTemplate = NodeConfigYaml.Parse(NodeConfigTemplate.Yaml);
        fromTemplate.Traffic.Should().Be(new TrafficConfig(), "the template's traffic block must match the record defaults");

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(fromTemplate));
        reparsed.Traffic.Should().Be(fromTemplate.Traffic);
    }

    [Fact]
    public void Kiss_ackMode_parses_and_round_trips_when_set_true()
    {
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                kiss:
                  txDelay: 30
                  ackMode: true
            """);

        config.Ports[0].Kiss!.AckMode.Should().BeTrue();
        config.Ports[0].Kiss!.TxDelay.Should().Be((byte)30);

        // The flag survives a serialise→parse round-trip unchanged.
        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(config));
        reparsed.Ports[0].Kiss!.AckMode.Should().BeTrue();
    }

    [Fact]
    public void Kiss_ackMode_defaults_false_when_absent()
    {
        // A port with KISS knobs set but no ackMode key must default the flag off —
        // the no-regression contract (a pre-feature config blasts fire-and-forget).
        var config = NodeConfigYaml.Parse("""
            schemaVersion: 1
            identity:
              callsign: M0LTE-1
            ports:
              - id: vhf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8001
                kiss:
                  txDelay: 30
            """);

        config.Ports[0].Kiss!.AckMode.Should().BeFalse();
    }
}
