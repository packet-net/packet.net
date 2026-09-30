using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Mqtt;

/// <summary>
/// The production <see cref="IMqttPublishSink"/>: an MQTTnet client behind a bounded publish
/// queue and a reconnect loop of its own. MQTTnet 4's ManagedClient extension did both and was
/// dropped in v5 (#882), so this keeps the contract the emitter was built on: a publish is a
/// fire-and-forget enqueue that never blocks the caller, the queue holds at most
/// <see cref="MaxPendingMessages"/> and drops the oldest past that (fresh traffic beats stale
/// backlog, the emitter's own telemetry policy), and a broker that is down or unreachable is
/// retried every <see cref="ReconnectDelay"/> with the queue intact. Messages go out in order at
/// the configured QoS with retain=false (matching kissproxy). Connection parameters
/// (host/port/TLS/credentials) are captured at construction; plain TCP unless
/// <see cref="MqttConfig.UseTls"/>.
/// </summary>
internal sealed class ManagedMqttPublishSink : IMqttPublishSink
{
    /// <summary>The bound on the publish queue. With the broker down the emitter enqueues two
    /// messages per traced frame, forever (#582); 10k messages is hours of typical channel
    /// traffic while keeping worst-case memory in the tens of MB.</summary>
    internal const int MaxPendingMessages = 10_000;

    /// <summary>How long the pump waits after a failed connect or publish before trying again.</summary>
    internal static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly IMqttClient client;
    private readonly MqttClientOptions options;
    private readonly TimeProvider clock;
    private readonly Channel<MqttApplicationMessage> queue;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task pump;
    private int inFlight;

    internal ManagedMqttPublishSink(IMqttClient client, MqttClientOptions options, TimeProvider? clock = null, int maxPending = MaxPendingMessages)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPending, 1);
        this.client = client;
        this.options = options;
        this.clock = clock ?? TimeProvider.System;
        queue = Channel.CreateBounded<MqttApplicationMessage>(new BoundedChannelOptions(maxPending)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        pump = Task.Run(PumpAsync);
    }

    /// <summary>Build a sink for <paramref name="cfg"/> under <paramref name="clientId"/>. Returns
    /// at once; the connect (and every reconnect) happens on the pump.</summary>
    public static ValueTask<IMqttPublishSink> CreateAsync(MqttConfig cfg, string clientId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        return new ValueTask<IMqttPublishSink>(
            new ManagedMqttPublishSink(new MqttClientFactory().CreateMqttClient(), BuildOptions(cfg, clientId)));
    }

    /// <summary>The client options, split out (internal) so they can be asserted without a client.</summary>
    internal static MqttClientOptions BuildOptions(MqttConfig cfg, string clientId)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(cfg.BrokerHost, cfg.BrokerPort);
        if (cfg.UseTls)
        {
            builder = builder.WithTlsOptions(o => o.UseTls(true));
        }
        if (!string.IsNullOrEmpty(cfg.Username))
        {
            builder = builder.WithCredentials(cfg.Username, cfg.Password ?? "");
        }
        return builder.Build();
    }

    /// <inheritdoc/>
    public long PendingMessageCount => queue.Reader.Count + Volatile.Read(ref inFlight);

    /// <inheritdoc/>
    public ValueTask PublishAsync(string topic, byte[] payload, int qos, bool retain, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)qos)
            .WithRetainFlag(retain)
            .Build();
        // DropOldest: the write always succeeds, evicting the oldest queued message when full.
        queue.Writer.TryWrite(message);
        return ValueTask.CompletedTask;
    }

    // One message at a time, in order. A failed connect or publish keeps the message and
    // retries after the delay; only a stop lets go of it.
    private async Task PumpAsync()
    {
        try
        {
            await foreach (var message in queue.Reader.ReadAllAsync(stopping.Token).ConfigureAwait(false))
            {
                Volatile.Write(ref inFlight, 1);
                while (true)
                {
                    try
                    {
                        if (!client.IsConnected)
                        {
                            await client.ConnectAsync(options, stopping.Token).ConfigureAwait(false);
                        }
                        await client.PublishAsync(message, stopping.Token).ConfigureAwait(false);
                        break;
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Broker down, refused, or the connection dropped mid-publish: wait and go
                        // again with this same message. The emitter's own log line covers start
                        // faults; here a retry is the whole point and would only be noise.
                        await Task.Delay(ReconnectDelay, clock, stopping.Token).ConfigureAwait(false);
                    }
                }
                Volatile.Write(ref inFlight, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The pump ends on the stop; anything else is a fault we are already discarding.
        }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.DisconnectAsync(new MqttClientDisconnectOptions(), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort teardown on shutdown - a broker already gone must never throw out of dispose.
        }
        client.Dispose();
        stopping.Dispose();
    }
}
