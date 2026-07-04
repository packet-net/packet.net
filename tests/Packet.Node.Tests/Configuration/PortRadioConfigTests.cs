using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The <c>serial:</c> (CCDI serial, stable binding) + <c>healthIntervalSeconds:</c> extensions to a
/// port's <see cref="PortRadioConfig"/>: YAML round-trip and the "exactly one of port/serial"
/// validation (plus a positive health interval).
/// </summary>
[Trait("Category", "Node")]
public sealed class PortRadioConfigTests
{
    private static readonly NodeConfigValidator Validator = new();

    private static NodeConfig WithRadio(PortRadioConfig radio) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports =
        [
            new PortConfig
            {
                Id = "nino",
                Enabled = true,
                Transport = new NinoTncTransport { Device = "/dev/ttyACM0", Baud = 57600, Mode = 6 },
                Radio = radio,
            },
        ],
    };

    [Fact]
    public void A_serial_bound_radio_with_a_health_interval_round_trips_through_yaml()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            ports:
              - id: nino
                enabled: true
                transport:
                  kind: nino-tnc
                  device: /dev/ttyACM0
                  baud: 57600
                  mode: 6
                radio:
                  kind: tait-ccdi
                  serial: 1G000123
                  baud: 28800
                  healthIntervalSeconds: 30
            """;

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(NodeConfigYaml.Parse(yaml)));

        var radio = reparsed.Ports[0].Radio!;
        radio.Serial.Should().Be("1G000123");
        radio.Port.Should().BeEmpty();
        radio.HealthIntervalSeconds.Should().Be(30);
    }

    [Theory]
    [InlineData("/dev/ttyUSB0", "", true)]           // port only — valid
    [InlineData("", "1G000123", true)]                // serial only — valid
    [InlineData("/dev/ttyUSB0", "1G000123", false)]   // both — ambiguous
    [InlineData("", "", false)]                        // neither — nothing to open
    public void Exactly_one_of_port_or_serial_is_required(string port, string serial, bool valid)
        => Validator.Validate(WithRadio(new PortRadioConfig { Kind = "tait-ccdi", Port = port, Serial = serial }))
            .IsValid.Should().Be(valid);

    [Theory]
    [InlineData(10, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    public void Health_interval_must_be_positive_when_set(int seconds, bool valid)
        => Validator.Validate(WithRadio(new PortRadioConfig
        {
            Kind = "tait-ccdi",
            Port = "/dev/ttyUSB0",
            HealthIntervalSeconds = seconds,
        })).IsValid.Should().Be(valid);
}
