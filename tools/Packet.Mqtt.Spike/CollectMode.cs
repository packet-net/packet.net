using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Client;

namespace Packet.Mqtt.Spike;

/// <summary>
/// Long-running collector - subscribes to the MQTT broker and persists every
/// message to daily-rotated SQLite files. Designed to run as a systemd-style
/// service on the LinBPQ host.
/// </summary>
/// <remarks>
/// <para>
/// Architecture: MQTT receive callback enqueues to an unbounded Channel; a
/// single consumer task drains it and writes to <see cref="SqliteSink"/>.
/// This serialises SQLite access (SqliteConnection is not thread-safe) and
/// lets the MQTT thread return quickly even if a write stalls.
/// </para>
/// <para>
/// Robustness: reconnects on broker disconnect with exponential backoff (max
/// 60 s). On SIGINT/SIGTERM, drains the channel and closes the SQLite file
/// cleanly so the WAL gets checkpointed.
/// </para>
/// <para>
/// Topic-parsing for the LinBPQ feed:
/// <code>
///   PACKETNODE/&lt;format&gt;/&lt;node&gt;/&lt;direction&gt;/&lt;port&gt;
///   e.g. PACKETNODE/kiss/GB7RDG/rcvd/2
///        PACKETNODE/ax25/trace/bpqformat/GB7RDG/sent/1
/// </code>
/// The format component can have slashes (the bpqformat one is three slugs
/// deep), so we anchor on the trailing <c>direction/port</c> rather than the
/// leading segments.
/// </para>
/// </remarks>
public static class CollectMode
{
    static long _reconnectCount;

    public static async Task<int> RunAsync(Options opts)
    {
        Directory.CreateDirectory(opts.OutDir);

        string clientId = $"packet-mqtt-collector-{Environment.MachineName}-{Random.Shared.Next():x}";
        Console.Error.WriteLine($"# collect: broker={opts.Broker}:{opts.Port} topic={opts.Topic}");
        Console.Error.WriteLine($"# out-dir: {opts.OutDir}  client-id: {clientId}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("# ^C — initiating graceful shutdown");
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Console.Error.WriteLine("# SIGTERM — initiating graceful shutdown");
            cts.Cancel();
        };

        var channel = Channel.CreateUnbounded<CapturedMessage>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        await using var sink = new SqliteSink(opts.OutDir, opts.FilenamePrefix, clientId);

        // Consumer: drain channel → SQLite.
        var consumer = Task.Run(async () =>
        {
            var lastHeartbeat = DateTime.UtcNow;
            try
            {
                await foreach (var msg in channel.Reader.ReadAllAsync(cts.Token))
                {
                    await sink.WriteAsync(msg, cts.Token);

                    if ((DateTime.UtcNow - lastHeartbeat).TotalSeconds >= 30)
                    {
                        await sink.HeartbeatAsync(cts.Token);
                        lastHeartbeat = DateTime.UtcNow;
                        Console.Error.WriteLine($"# heartbeat: {sink.TotalMessages} messages persisted");
                    }
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
            finally
            {
                try { await sink.HeartbeatAsync(CancellationToken.None); } catch { }
            }
        }, CancellationToken.None);

        var factory = new MqttFactory();
        var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += async e =>
        {
            var msg = ParseToCaptured(e.ApplicationMessage);
            await channel.Writer.WriteAsync(msg, cts.Token);
        };

        client.DisconnectedAsync += async e =>
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            Interlocked.Increment(ref _reconnectCount);
            Console.Error.WriteLine($"# disconnected ({e.Reason}); reconnect attempt #{_reconnectCount}");
            await Task.CompletedTask;  // actual reconnect loop is below
        };

        var connectOptions = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(opts.Broker, opts.Port)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();

        // Reconnect loop with exponential backoff (capped at 60 s).
        int backoffMs = 1000;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                Console.Error.WriteLine("# connecting...");
                await client.ConnectAsync(connectOptions, cts.Token);
                await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(opts.Topic))
                    .Build(), cts.Token);
                Console.Error.WriteLine($"# connected + subscribed to {opts.Topic}");

                backoffMs = 1000;  // reset after a successful connection

                // Block until disconnect (the receive callback handles incoming msgs;
                // we just poll IsConnected here).
                while (client.IsConnected && !cts.IsCancellationRequested)
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"# connection error: {ex.GetType().Name}: {ex.Message}");
            }

            if (cts.IsCancellationRequested)
            {
                break;
            }

            Console.Error.WriteLine($"# reconnecting in {backoffMs} ms...");
            try { await Task.Delay(backoffMs, cts.Token); } catch { break; }
            backoffMs = Math.Min(backoffMs * 2, 60_000);
        }

        Console.Error.WriteLine("# shutting down");
        try { await client.DisconnectAsync(); } catch { }
        channel.Writer.Complete();
        await consumer;

        Console.Error.WriteLine($"# total messages persisted: {sink.TotalMessages}");
        return 0;
    }

    static CapturedMessage ParseToCaptured(MqttApplicationMessage am)
    {
        var topic = am.Topic;
        var payload = am.Payload.ToArray();
        var (format, node, direction, port) = ParseTopic(topic);
        return new CapturedMessage(SqliteSink.NowUs(), topic, format, node, direction, port, payload);
    }

    /// <summary>
    /// Parse a PACKETNODE/* topic into its denormalised components. Returns
    /// nulls for any component we can't pull out so unknown topic shapes
    /// still get logged faithfully (just without the speed-of-query benefit
    /// of indexed columns).
    /// </summary>
    /// <remarks>
    /// Expected shapes:
    /// <code>
    ///   PACKETNODE/kiss/&lt;node&gt;/&lt;direction&gt;/&lt;port&gt;
    ///   PACKETNODE/ax25/trace/bpqformat/&lt;node&gt;/&lt;direction&gt;/&lt;port&gt;
    /// </code>
    /// Anchor on the last three slugs (node, direction, port) so a deeper
    /// format prefix still parses.
    /// </remarks>
    internal static (string? format, string? node, string? direction, int? port) ParseTopic(string topic)
    {
        var parts = topic.Split('/');
        if (parts.Length < 5 || parts[0] != "PACKETNODE")
        {
            return (null, null, null, null);
        }
        // Last three are: node, direction, port. Format = parts[1..^3] joined.
        string node = parts[^3];
        string direction = parts[^2];
        if (!int.TryParse(parts[^1], out int port))
        {
            return (string.Join('/', parts[1..^3]), node, direction, null);
        }
        string format = string.Join('/', parts[1..^3]);
        return (format, node, direction, port);
    }
}
