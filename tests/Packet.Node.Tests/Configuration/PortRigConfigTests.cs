using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The port-scoped <c>rig:</c> (CAT/station-control) attachment block: YAML round-trip, kind
/// defaults, and validation (known kind, non-empty host, sane port + cadences).
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRigConfigTests
{
    private static readonly NodeConfigValidator Validator = new();

    private static NodeConfig WithRig(PortRigConfig rig) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports =
        [
            new PortConfig
            {
                Id = "hf",
                Enabled = true,
                Transport = new KissTcpTransport { Host = "127.0.0.1", Port = 8100 },
                Rig = rig,
            },
        ],
    };

    [Fact]
    public void A_hamlib_rig_with_cadences_round_trips_through_yaml()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            ports:
              - id: hf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8100
                rig:
                  kind: hamlib
                  host: 10.0.0.5
                  port: 4534
                  pollIntervalSeconds: 10
                  meterIntervalSeconds: 2
            """;

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(NodeConfigYaml.Parse(yaml)));

        var rig = reparsed.Ports[0].Rig!;
        rig.Kind.Should().Be("hamlib");
        rig.Host.Should().Be("10.0.0.5");
        rig.Port.Should().Be(4534);
        rig.PollIntervalSeconds.Should().Be(10);
        rig.MeterIntervalSeconds.Should().Be(2);
    }

    [Fact]
    public void A_minimal_rig_block_defaults_to_loopback_and_the_kind_port()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            ports:
              - id: hf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8100
                rig:
                  kind: flrig
            """;

        var rig = NodeConfigYaml.Parse(yaml).Ports[0].Rig!;

        rig.Host.Should().Be("127.0.0.1");
        rig.Port.Should().BeNull();
        RigKinds.DefaultPort(rig.Kind).Should().Be(12345);
        RigKinds.DefaultPort("hamlib").Should().Be(4532);
    }

    [Fact]
    public void A_port_without_a_rig_block_parses_to_null()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            ports:
              - id: hf
                enabled: true
                transport:
                  kind: kiss-tcp
                  host: 127.0.0.1
                  port: 8100
            """;

        NodeConfigYaml.Parse(yaml).Ports[0].Rig.Should().BeNull();
    }

    [Theory]
    [InlineData("hamlib", true)]
    [InlineData("flrig", true)]
    [InlineData("HAMLIB", true)]      // kind matching is case-insensitive
    [InlineData("ham_lib", true)]     // and hyphen/underscore-insensitive
    [InlineData("omnirig", false)]    // researched, deliberately not implemented
    [InlineData("", false)]           // a rig block must say what it speaks
    public void Rig_kind_must_be_known(string kind, bool valid)
    {
        var result = Validator.Validate(WithRig(new PortRigConfig { Kind = kind }));
        result.IsValid.Should().Be(valid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(4532, true)]
    [InlineData(65535, true)]
    [InlineData(0, false)]
    [InlineData(65536, false)]
    public void Rig_port_must_be_a_sane_tcp_port_when_set(int port, bool valid)
    {
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Port = port }))
            .IsValid.Should().Be(valid);
    }

    [Fact]
    public void Rig_host_must_not_be_blanked_out()
    {
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Host = "  " }))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(5, true)]
    public void Rig_cadences_must_be_positive_when_set(int seconds, bool valid)
    {
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", PollIntervalSeconds = seconds }))
            .IsValid.Should().Be(valid);
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", MeterIntervalSeconds = seconds }))
            .IsValid.Should().Be(valid);
    }

    [Fact]
    public void A_rig_is_valid_on_any_transport_kind()
    {
        // Unlike radio: (which needs a serial modem beside it), a rig is a TCP daemon that can
        // sit beside any port — the HF kiss-tcp case is the motivating one.
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Ports =
            [
                new PortConfig
                {
                    Id = "axudp",
                    Enabled = true,
                    Transport = new AxudpTransport { Host = "example.net", Port = 10093, LocalPort = 10093 },
                    Rig = new PortRigConfig { Kind = "hamlib" },
                },
            ],
        };

        var result = Validator.Validate(config);
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
    }
}
