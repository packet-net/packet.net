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

    // ---- the node-managed shape (device + model → the node spawns rigctld) --------------

    [Fact]
    public void A_node_managed_rig_round_trips_through_yaml()
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
                  device: /dev/serial/by-id/usb-Icom_Inc._IC-7300_02012345-if00-port0
                  model: 3073
                  serialSpeed: 115200
            """;

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(NodeConfigYaml.Parse(yaml)));

        var rig = reparsed.Ports[0].Rig!;
        rig.Device.Should().Be("/dev/serial/by-id/usb-Icom_Inc._IC-7300_02012345-if00-port0");
        rig.Model.Should().Be(3073);
        rig.SerialSpeed.Should().Be(115200);
        rig.IsNodeManaged.Should().BeTrue();
        rig.Port.Should().BeNull("the node allocates the daemon's loopback port itself");
    }

    [Fact]
    public void A_node_managed_hamlib_rig_is_valid()
    {
        var result = Validator.Validate(WithRig(new PortRigConfig
        {
            Kind = "hamlib",
            Device = "/dev/serial/by-id/usb-Icom_Inc._IC-7300_02012345-if00-port0",
            Model = 3073,
        }));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));

        // With the optional CAT serial speed, and with the (harmless) explicit loopback host.
        Validator.Validate(WithRig(new PortRigConfig
        {
            Kind = "hamlib",
            Device = "/dev/ttyUSB0",
            Model = 3073,
            SerialSpeed = 19200,
            Host = "127.0.0.1",
        })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_node_managed_rig_requires_a_model()
    {
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0" }))
            .IsValid.Should().BeFalse("rigctld -m needs the hamlib model number");
    }

    [Fact]
    public void A_node_managed_rig_must_be_hamlib()
    {
        // flrig is a GUI app the node can't sensibly spawn — BYO host/port is its only shape.
        Validator.Validate(WithRig(new PortRigConfig { Kind = "flrig", Device = "/dev/ttyUSB0", Model = 3073 }))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_node_managed_rig_must_not_set_a_port()
    {
        // The node allocates the daemon's loopback port itself — a configured one contradicts it.
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = 3073, Port = 4532 }))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_node_managed_rig_must_not_point_at_a_remote_host()
    {
        // A remote host contradicts a locally-spawned daemon.
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = 3073, Host = "10.0.0.5" }))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void Model_or_serial_speed_without_a_device_is_invalid()
    {
        // They describe the daemon the node spawns; without `device` there is none to describe.
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Model = 3073 }))
            .IsValid.Should().BeFalse();
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", SerialSpeed = 19200 }))
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Model_and_serial_speed_must_be_positive(int bad)
    {
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = bad }))
            .IsValid.Should().BeFalse();
        Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = 3073, SerialSpeed = bad }))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void The_byo_daemon_shape_stays_valid()
    {
        // Regression: host/port (the only flrig shape) is untouched by the node-managed rules.
        var result = Validator.Validate(WithRig(new PortRigConfig { Kind = "hamlib", Host = "10.0.0.5", Port = 4534 }));
        result.IsValid.Should().BeTrue(string.Join("; ", result.Errors));
        Validator.Validate(WithRig(new PortRigConfig { Kind = "flrig" })).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Describe_endpoint_is_honest_for_both_shapes()
    {
        // BYO: host:port with the kind default resolved — byte-for-byte today's string.
        new PortRigConfig { Kind = "hamlib" }.DescribeEndpoint().Should().Be("127.0.0.1:4532");
        new PortRigConfig { Kind = "flrig", Host = "10.0.0.5" }.DescribeEndpoint().Should().Be("10.0.0.5:12345");

        // Node-managed, port not yet allocated (the config-only projection of an unattached rig).
        new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = 3073 }
            .DescribeEndpoint().Should().Be("/dev/ttyUSB0 (managed rigctld)");

        // Node-managed, effective config (the daemon's ClientConfig carries the allocated port).
        new PortRigConfig { Kind = "hamlib", Device = "/dev/ttyUSB0", Model = 3073, Port = 4531 }
            .DescribeEndpoint().Should().Be("/dev/ttyUSB0 (managed rigctld @127.0.0.1:4531)");
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
