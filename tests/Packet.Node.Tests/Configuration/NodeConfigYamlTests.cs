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
    }

    [Theory]
    [InlineData("serial-kiss")]
    [InlineData("nino-tnc")]
    [InlineData("kiss-tcp")]
    public void Round_trips_each_transport_kind_through_serialise_then_parse(string kind)
    {
        TransportConfig transport = kind switch
        {
            "serial-kiss" => new SerialKissTransport { Device = "/dev/ttyUSB0", Baud = 115200 },
            "nino-tnc" => new NinoTncTransport { Device = "/dev/ttyACM3", Baud = 57600, Mode = 9 },
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
    public void Unknown_transport_kind_throws_a_clear_parse_error()
    {
        const string yaml = """
            schemaVersion: 1
            identity:
              callsign: M0LTE
            ports:
              - id: bad
                transport:
                  kind: axudp
                  host: 10.0.0.1
                  port: 10093
            """;

        var act = () => NodeConfigYaml.Parse(yaml);
        act.Should().Throw<Exception>()
            .Which.Message.Should().Contain("axudp");
    }

    [Fact]
    public void Empty_or_comment_only_document_parses_to_a_default_shape_not_null()
    {
        var config = NodeConfigYaml.Parse("# just a comment\n");
        config.Should().NotBeNull();
        config.Ports.Should().BeEmpty();
    }
}
