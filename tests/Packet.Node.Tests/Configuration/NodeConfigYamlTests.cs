using Packet.Node.Core.Configuration;

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
              defaultNeighbourQuality: 203
              minQuality: 150
              obsoleteInitial: 5
              sweepIntervalSeconds: 1800
            """;

        var config = NodeConfigYaml.Parse(yaml);

        config.NetRom.Enabled.Should().BeTrue();
        config.NetRom.DefaultNeighbourQuality.Should().Be(203);
        config.NetRom.MinQuality.Should().Be(150);
        config.NetRom.ObsoleteInitial.Should().Be(5);
        config.NetRom.SweepIntervalSeconds.Should().Be(1800);
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
            "axudp" => new AxudpTransport { Host = "peer.local", Port = 10093, LocalPort = 10093, IncludeFcs = true },
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
                  includeFcs: true
            """;

        var config = NodeConfigYaml.Parse(yaml);

        var axudp = config.Ports.Should().ContainSingle().Subject
            .Transport.Should().BeOfType<AxudpTransport>().Subject;
        axudp.Host.Should().Be("10.0.0.2");
        axudp.Port.Should().Be(10093);
        axudp.LocalPort.Should().Be(10093);
        axudp.IncludeFcs.Should().BeTrue();
        axudp.DescribeEndpoint().Should().Be("axudp:10.0.0.2:10093(local:10093)");
    }

    [Fact]
    public void Axudp_localPort_and_includeFcs_default_when_omitted()
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
        // The de-facto interoperable default: every real AXIP/AXUDP peer (LinBPQ BPQAXIP,
        // XRouter, ax25ipd, JNOS, per RFC 1226) REQUIRES the 2-octet FCS, so an
        // out-of-the-box AXUDP port includes it. FCS-less is pdn↔pdn-only and must be
        // opted into explicitly. See docs/strict-vs-pragmatic-audit.md.
        axudp.IncludeFcs.Should().BeTrue("includeFcs defaults to true — the standard RFC-1226 AXIP/AXUDP wire form");
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
}
