using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;
using MQTTnet.Protocol;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Mqtt;

/// <summary>
/// The production <see cref="IMqttPublishSink"/>: a started MQTTnet <c>ManagedMqttClient</c>. The
/// managed client owns auto-reconnect (~5 s) and an internal publish queue, so a slow or unreachable
/// broker never blocks the caller — publishes are fire-and-forget enqueues at the configured QoS with
/// retain=false (matching kissproxy). Connection parameters (host/port/TLS/credentials) are captured
/// at construction; plain TCP unless <see cref="MqttConfig.UseTls"/>.
/// </summary>
internal sealed class ManagedMqttPublishSink : IMqttPublishSink
{
    private readonly IManagedMqttClient client;

    private ManagedMqttPublishSink(IManagedMqttClient client) => this.client = client;

    /// <summary>Build + start a managed client for <paramref name="cfg"/> under
    /// <paramref name="clientId"/>. <c>StartAsync</c> configures the managed connection loop and
    /// returns promptly; the actual connect (and any reconnect) happens in the background.</summary>
    public static async ValueTask<IMqttPublishSink> CreateAsync(MqttConfig cfg, string clientId, CancellationToken ct)
    {
        var factory = new MqttFactory();
        var client = factory.CreateManagedMqttClient();

        var clientOptions = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(cfg.BrokerHost, cfg.BrokerPort);

        if (cfg.UseTls)
        {
            clientOptions = clientOptions.WithTlsOptions(o => o.UseTls(true));
        }
        if (!string.IsNullOrEmpty(cfg.Username))
        {
            clientOptions = clientOptions.WithCredentials(cfg.Username, cfg.Password ?? "");
        }

        var managedOptions = new ManagedMqttClientOptionsBuilder()
            .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
            .WithClientOptions(clientOptions.Build())
            .Build();

        await client.StartAsync(managedOptions).ConfigureAwait(false);
        return new ManagedMqttPublishSink(client);
    }

    public async ValueTask PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .WithRetainFlag(retain)
            .Build();
        await client.EnqueueAsync(message).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await client.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown on shutdown — a broker already gone must never throw out of dispose.
        }
        client.Dispose();
    }
}
