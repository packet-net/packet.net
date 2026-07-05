using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Mqtt;

/// <summary>
/// Tests for <see cref="MqttConfigValidator"/> and its composition into
/// <see cref="NodeConfigValidator"/>: the enabled-requires-host rule, and the always-on shape checks
/// (broker port, QoS) that keep a disabled-but-edited block from holding junk that detonates on enable.
/// </summary>
public sealed class MqttConfigValidatorTests
{
    private static readonly MqttConfigValidator Validator = new();

    [Fact]
    public void Enabled_without_a_broker_host_is_rejected()
    {
        var result = Validator.Validate(new MqttConfig { Enabled = true, BrokerHost = "" });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(MqttConfig.BrokerHost));
    }

    [Fact]
    public void Enabled_with_a_broker_host_passes()
    {
        var result = Validator.Validate(new MqttConfig { Enabled = true, BrokerHost = "broker.example" });

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Disabled_without_a_host_is_fine()
    {
        // A disabled block need not name a broker.
        var result = Validator.Validate(new MqttConfig { Enabled = false, BrokerHost = "" });

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Qos_out_of_range_is_rejected_even_when_disabled(int qos)
    {
        var result = Validator.Validate(new MqttConfig { Enabled = false, Qos = qos });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(MqttConfig.Qos));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void Broker_port_out_of_range_is_rejected_even_when_disabled(int port)
    {
        var result = Validator.Validate(new MqttConfig { Enabled = false, BrokerPort = port });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(MqttConfig.BrokerPort));
    }

    [Fact]
    public void NodeConfigValidator_composes_the_mqtt_block()
    {
        // A node whose only defect is an enabled MQTT block with no host must fail the top-level
        // validator — proving MqttConfigValidator is wired into NodeConfigValidator.
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Mqtt = new MqttConfig { Enabled = true, BrokerHost = "" },
        };

        var result = new NodeConfigValidator().Validate(config);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("mqtt.brokerHost", StringComparison.Ordinal));
    }

    [Fact]
    public void Default_mqtt_block_is_valid()
    {
        // The stock default (everything off) must pass — a fresh node config validates.
        new MqttConfigValidator().Validate(new MqttConfig()).IsValid.Should().BeTrue();
    }
}
