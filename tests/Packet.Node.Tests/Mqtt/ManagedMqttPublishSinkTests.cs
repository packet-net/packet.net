using MQTTnet.Server;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Mqtt;

namespace Packet.Node.Tests.Mqtt;

/// <summary>
/// The managed-client options behind the production publish sink (#582): the pending-publish queue
/// must be BOUNDED with drop-oldest overflow — MQTTnet's default is <c>int.MaxValue</c>, which with
/// the emitter on and the broker down grows two messages per traced frame in RAM indefinitely.
/// Asserted on <see cref="ManagedMqttPublishSink.BuildOptions"/> without starting a client.
/// </summary>
[Trait("Category", "Node")]
public sealed class ManagedMqttPublishSinkTests
{
    private static MqttConfig Cfg() => new()
    {
        Enabled = true,
        BrokerHost = "broker.example",
        NodeName = "gb7rdg-node",
    };

    [Fact]
    public void Pending_queue_is_bounded_with_drop_oldest_overflow()
    {
        var options = ManagedMqttPublishSink.BuildOptions(Cfg(), "gb7rdg-node_pdn_1a2b3c4d");

        options.MaxPendingMessages.Should().Be(ManagedMqttPublishSink.MaxPendingMessages)
            .And.Be(10_000, "the bound keeps broker-down memory in the tens of MB");
        options.PendingMessagesOverflowStrategy.Should().Be(
            MqttPendingMessagesOverflowStrategy.DropOldestQueuedMessage,
            "fresh traffic beats stale backlog, matching the emitter's telemetry subscription policy");
    }

    [Fact]
    public void Client_id_and_broker_endpoint_pass_through()
    {
        var options = ManagedMqttPublishSink.BuildOptions(Cfg(), "gb7rdg-node_pdn_1a2b3c4d");

        options.ClientOptions.ClientId.Should().Be("gb7rdg-node_pdn_1a2b3c4d");
        options.AutoReconnectDelay.Should().Be(TimeSpan.FromSeconds(5));
    }
}
