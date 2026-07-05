using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The Stage-3a split-station config surface: a first-class <see cref="HeadEndConfig"/> fleet, the
/// head-end binding mode on a <see cref="PortRadioConfig"/> radio, the <c>nino-tnc-tcp</c> transport,
/// the lifted radio-on-networked-transport rule, and cross-config reference resolution — plus YAML /
/// JSON round-trips. See <c>docs/research/split-station-rf-headend.md</c>.
/// </summary>
[Trait("Category", "Node")]
public sealed class HeadEndConfigTests
{
    private static readonly NodeConfigValidator Validator = new();
    private static readonly HeadEndConfig Pi = new() { Id = "pi", Address = "192.168.1.10:7300" };

    private static NodeConfig Config(PortConfig port, params HeadEndConfig[] headEnds) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [port],
        HeadEnds = headEnds,
    };

    private static PortConfig LocalNinoPort(PortRadioConfig? radio = null) => new()
    {
        Id = "p",
        Transport = new NinoTncTransport { Device = "/dev/ttyACM0", Baud = 57600, Mode = 6 },
        Radio = radio,
    };

    private static PortConfig HeadEndNinoPort(string headEndId, string deviceId, PortRadioConfig? radio = null) => new()
    {
        Id = "p",
        Transport = new NinoTncTcpTransport { HeadEndId = headEndId, DeviceId = deviceId, Mode = 6 },
        Radio = radio,
    };

    // ---- the canonical co-located pair ---------------------------------------------------------

    [Fact]
    public void A_head_end_radio_paired_with_nino_tnc_tcp_on_a_declared_head_end_is_valid()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi", DeviceId = "tait0" };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0", radio), Pi)).IsValid.Should().BeTrue();
    }

    // ---- radio binding modes -------------------------------------------------------------------

    [Theory]
    [InlineData("pi", "tait0", true)]     // both halves — a valid head-end binding
    [InlineData("pi", "", false)]         // only headEndId — incomplete
    [InlineData("", "tait0", false)]      // only deviceId — incomplete
    public void A_head_end_radio_needs_both_head_end_id_and_device_id(string headEndId, string deviceId, bool valid)
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = headEndId, DeviceId = deviceId };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0", radio), Pi)).IsValid.Should().Be(valid);
    }

    [Fact]
    public void A_radio_with_both_a_local_and_a_head_end_binding_is_invalid()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0", HeadEndId = "pi", DeviceId = "tait0" };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0", radio), Pi)).IsValid.Should().BeFalse();
    }

    // ---- the lifted radio-on-networked-transport rule ------------------------------------------

    [Fact]
    public void A_head_end_radio_on_a_kiss_tcp_transport_is_invalid()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi", DeviceId = "tait0" };
        var port = new PortConfig { Id = "p", Transport = new KissTcpTransport { Host = "10.0.0.1", Port = 8001 }, Radio = radio };
        Validator.Validate(Config(port, Pi)).IsValid.Should().BeFalse("a kiss-tcp (LinBPQ) port has no co-located radio");
    }

    [Fact]
    public void A_head_end_radio_on_a_local_nino_tnc_transport_is_invalid()
    {
        // The modem+radio pair is always co-located: a head-end radio needs the head-end's own modem.
        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi", DeviceId = "tait0" };
        Validator.Validate(Config(LocalNinoPort(radio), Pi)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_local_radio_on_a_nino_tnc_tcp_transport_is_invalid()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0" };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0", radio), Pi)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_local_radio_still_pairs_with_a_local_nino_tnc_transport()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0" };
        Validator.Validate(Config(LocalNinoPort(radio))).IsValid.Should().BeTrue("the lift must not weaken the local path");
    }

    [Fact]
    public void A_kiss_tcp_port_still_cannot_carry_a_local_radio()
    {
        var radio = new PortRadioConfig { Kind = "tait-ccdi", Port = "/dev/ttyUSB0" };
        var port = new PortConfig { Id = "p", Transport = new KissTcpTransport { Host = "10.0.0.1", Port = 8001 }, Radio = radio };
        Validator.Validate(Config(port)).IsValid.Should().BeFalse();
    }

    // ---- head-end reference resolution ---------------------------------------------------------

    [Fact]
    public void A_nino_tnc_tcp_transport_referencing_an_undeclared_head_end_is_invalid()
    {
        Validator.Validate(Config(HeadEndNinoPort("ghost", "nino0"))).IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_nino_tnc_tcp_transport_referencing_a_declared_head_end_is_valid()
    {
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0"), Pi)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_head_end_radio_referencing_an_undeclared_head_end_is_invalid()
    {
        // The transport references the declared head-end; the radio references an undeclared one.
        var radio = new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "ghost", DeviceId = "tait0" };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0", radio), Pi)).IsValid.Should().BeFalse();
    }

    // ---- head-end fleet validity ---------------------------------------------------------------

    [Fact]
    public void Head_end_ids_must_be_unique()
    {
        var a = new HeadEndConfig { Id = "pi", Address = "10.0.0.1:7300" };
        var b = new HeadEndConfig { Id = "pi", Address = "10.0.0.2:7300" };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0"), a, b)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("192.168.1.10:7300", true)]
    [InlineData("pi.local:7300", true)]
    [InlineData("http://pi.local:7300", true)]
    [InlineData("", true)]                    // empty tolerated — the Stage-3b mDNS fallback
    [InlineData("192.168.1.10", false)]       // no explicit port
    [InlineData("pi.local", false)]           // no explicit port
    [InlineData("192.168.1.10:0", false)]     // port out of range
    [InlineData("not a host", false)]
    public void A_head_end_address_must_be_host_port_with_an_explicit_port_when_set(string address, bool valid)
    {
        var headEnd = new HeadEndConfig { Id = "pi", Address = address };
        Validator.Validate(Config(HeadEndNinoPort("pi", "nino0"), headEnd)).IsValid.Should().Be(valid);
    }

    // ---- round-trips ---------------------------------------------------------------------------

    [Fact]
    public void A_head_end_pair_round_trips_through_yaml()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            headEnds:
              - id: pi-shack
                address: 192.168.1.10:7300
            ports:
              - id: vhf
                transport:
                  kind: nino-tnc-tcp
                  headEndId: pi-shack
                  deviceId: nino0
                  mode: 6
                radio:
                  kind: tait-ccdi
                  headEndId: pi-shack
                  deviceId: tait0
            """;

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(NodeConfigYaml.Parse(yaml)));

        reparsed.HeadEnds.Should().ContainSingle();
        reparsed.HeadEnds[0].Id.Should().Be("pi-shack");
        reparsed.HeadEnds[0].Address.Should().Be("192.168.1.10:7300");

        var transport = reparsed.Ports[0].Transport.Should().BeOfType<NinoTncTcpTransport>().Subject;
        transport.HeadEndId.Should().Be("pi-shack");
        transport.DeviceId.Should().Be("nino0");
        transport.Mode.Should().Be(6);

        var radio = reparsed.Ports[0].Radio!;
        radio.HeadEndId.Should().Be("pi-shack");
        radio.DeviceId.Should().Be("tait0");
        radio.IsHeadEndBound.Should().BeTrue();

        Validator.Validate(reparsed).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_head_end_pair_round_trips_through_json()
    {
        var config = Config(
            HeadEndNinoPort("pi", "nino0", new PortRadioConfig { Kind = "tait-ccdi", HeadEndId = "pi", DeviceId = "tait0" }),
            Pi);

        var reparsed = NodeConfigJson.Deserialize(NodeConfigJson.Serialize(config));

        reparsed.HeadEnds.Should().ContainSingle();
        reparsed.HeadEnds[0].Address.Should().Be("192.168.1.10:7300");
        reparsed.Ports[0].Transport.Should().BeOfType<NinoTncTcpTransport>();
        reparsed.Ports[0].Radio!.DeviceId.Should().Be("tait0");
        Validator.Validate(reparsed).IsValid.Should().BeTrue();
    }
}
