using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

public class SoundModemConfigTests
{
    private static readonly NodeConfigValidator Validator = new();

    private static NodeConfig Valid(TransportConfig transport) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = [new PortConfig { Id = "sm", Enabled = true, Transport = transport }],
    };

    [Fact]
    public void A_soundmodem_port_round_trips_through_yaml()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            ports:
              - id: sm
                enabled: true
                transport:
                  kind: soundmodem
                  device: plughw:1,0
                  captureRate: 48000
                  mode: bpsk300
                  frequency: 1500
                  ptt: cm108:/dev/hidraw0:3
            """;

        var reparsed = NodeConfigYaml.Parse(NodeConfigYaml.Serialize(NodeConfigYaml.Parse(yaml)));

        var transport = reparsed.Ports[0].Transport.Should().BeOfType<SoundModemTransportConfig>().Subject;
        transport.Device.Should().Be("plughw:1,0");
        transport.CaptureRate.Should().Be(48000);
        transport.Mode.Should().Be("bpsk300");
        transport.Frequency.Should().Be(1500);
        transport.Ptt.Should().Be("cm108:/dev/hidraw0:3");
        transport.DescribeEndpoint().Should().Be("soundmodem:plughw:1,0/bpsk300");
    }

    [Fact]
    public void Defaults_apply_when_only_the_kind_is_given()
    {
        const string yaml = """
            identity:
              callsign: M0LTE-1
            ports:
              - id: sm
                enabled: true
                transport:
                  kind: soundmodem
            """;

        var transport = NodeConfigYaml.Parse(yaml).Ports[0].Transport
            .Should().BeOfType<SoundModemTransportConfig>().Subject;
        transport.Device.Should().Be("default");
        transport.CaptureRate.Should().Be(48000);
        transport.Mode.Should().Be("afsk1200");
        transport.Ptt.Should().Be("");
    }

    [Fact]
    public void A_valid_soundmodem_port_validates()
    {
        Validator.Validate(Valid(new SoundModemTransportConfig
        {
            Mode = "fsk9600-il2p",
            CaptureRate = 48000,
        })).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("afsk1234")]
    [InlineData("il2p")]
    [InlineData("")]
    public void Unknown_modes_are_rejected(string mode)
    {
        var result = Validator.Validate(Valid(new SoundModemTransportConfig { Mode = mode }));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("mode"));
    }

    [Fact]
    public void A_capture_rate_the_decimator_cannot_reach_is_rejected()
    {
        var result = Validator.Validate(Valid(new SoundModemTransportConfig
        {
            Mode = "afsk1200",
            CaptureRate = 44100, // not a multiple of 12000
        }));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("captureRate"));
    }

    [Fact]
    public void Nine_Thousand_Six_Hundred_Requires_A_48k_Multiple()
    {
        var result = Validator.Validate(Valid(new SoundModemTransportConfig
        {
            Mode = "fsk9600",
            CaptureRate = 12000,
        }));
        result.IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("serial:/dev/ttyUSB0")]
    [InlineData("serial:/dev/ttyUSB0:dtr")]
    [InlineData("cm108:/dev/hidraw0")]
    [InlineData("cm108:/dev/hidraw0:3")]
    [InlineData("")]
    public void Valid_ptt_specs_pass(string ptt)
    {
        Validator.Validate(Valid(new SoundModemTransportConfig { Ptt = ptt }))
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("gpio:17")]
    [InlineData("serial:")]
    [InlineData("cm108:/dev/hidraw0:9")]
    [InlineData("serial:/dev/ttyUSB0:cts")]
    public void Invalid_ptt_specs_are_rejected(string ptt)
    {
        Validator.Validate(Valid(new SoundModemTransportConfig { Ptt = ptt }))
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void An_out_of_passband_frequency_is_rejected()
    {
        Validator.Validate(Valid(new SoundModemTransportConfig { Frequency = 5000 }))
            .IsValid.Should().BeFalse();
    }
}
