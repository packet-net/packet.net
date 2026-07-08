using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// The duplicate-MqttInstance quirk warning (#586): two ports resolving to the same MQTT
/// <c>{instance}</c> label silently merge their kissproxy topic streams. That can be intentional
/// (multi-port same-band feeding one collector key), so it is a validation-PASSING logged warning —
/// surfaced through the providers' <c>WarnOnConfigQuirks</c> at config load/apply, the same channel
/// the NET/ROM routing resolver warns through — never a hard error.
/// </summary>
[Trait("Category", "Node")]
public sealed class NodeConfigWarningsTests
{
    private static PortConfig Port(string id, string? mqttInstance = null, int tcpPort = 8001) => new()
    {
        Id = id,
        MqttInstance = mqttInstance,
        Transport = new KissTcpTransport { Host = "127.0.0.1", Port = tcpPort },
    };

    private static NodeConfig Config(params PortConfig[] ports) => new()
    {
        Identity = new Identity { Callsign = "M0LTE-1" },
        Ports = ports,
    };

    [Fact]
    public void Two_ports_sharing_an_explicit_mqtt_instance_warn_but_validate()
    {
        var config = Config(Port("2m", "2m", 8001), Port("2m-2", "2m", 8002));

        var warnings = NodeConfigWarnings.DuplicateMqttInstances(config);

        warnings.Should().ContainSingle()
            .Which.Should().Contain("'2m'").And.Contain("'2m-2'").And.Contain("merge");
        new NodeConfigValidator().Validate(config).IsValid.Should().BeTrue("merging streams may be intentional");
    }

    [Fact]
    public void An_explicit_label_colliding_with_another_ports_default_warns_too()
    {
        // Port "vhf" explicitly labels itself "2m"; port "2m" has no label so its id IS its label —
        // the emitter resolves both to {instance}=2m, so they merge just the same.
        var config = Config(Port("2m", tcpPort: 8001), Port("vhf", "2m", 8002));

        NodeConfigWarnings.DuplicateMqttInstances(config).Should().ContainSingle();
    }

    [Fact]
    public void Distinct_labels_produce_no_warning()
    {
        var config = Config(Port("2m", "2m", 8001), Port("70cm", "70cm", 8002), Port("hf", tcpPort: 8003));

        NodeConfigWarnings.DuplicateMqttInstances(config).Should().BeEmpty();
    }

    [Fact]
    public void Each_shared_label_warns_once()
    {
        var config = Config(
            Port("a", "2m", 8001), Port("b", "2m", 8002),
            Port("c", "70cm", 8003), Port("d", "70cm", 8004));

        NodeConfigWarnings.DuplicateMqttInstances(config).Should().HaveCount(2);
    }
}
