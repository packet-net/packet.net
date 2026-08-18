using System.Text;
using MQTTnet;
using MQTTnet.Client;

namespace Packet.Mqtt.Spike;

/// <summary>
/// Subscribes to the broker for a fixed window, dumps each received message
/// (topic + first-64-byte hex preview) to stdout and to a structured JSONL
/// file. After the window closes, summarises the topic structure and payload
/// distribution.
/// </summary>
/// <remarks>
/// Output goes to <c>artifacts/mqtt-probe/&lt;ts&gt;/</c>:
/// <list type="bullet">
/// <item><c>messages.jsonl</c> - one line per message with topic, length,
/// hex preview, ts</item>
/// <item><c>summary.md</c> - final topic-frequency table + payload-size
/// distribution</item>
/// </list>
/// </remarks>
public static class ProbeMode
{
    public static async Task<int> RunAsync(Options opts)
    {
        Directory.CreateDirectory(opts.OutDir);
        string messagesPath = Path.Combine(opts.OutDir, "messages.jsonl");
        string summaryPath = Path.Combine(opts.OutDir, "summary.md");

        Console.Error.WriteLine($"# probe: broker={opts.Broker}:{opts.Port} topic={opts.Topic} seconds={opts.Seconds}");
        Console.Error.WriteLine($"# out-dir: {opts.OutDir}");

        var factory = new MqttFactory();
        var client = factory.CreateMqttClient();

        var clientId = $"packetnet-mqtt-probe-{Environment.MachineName}-{Random.Shared.Next():x}";
        var options = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithTcpServer(opts.Broker, opts.Port)
            .WithCleanSession()
            .Build();

        int messageCount = 0;
        var topicCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sizeBuckets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["0"] = 0,
            ["1-31"] = 0,
            ["32-127"] = 0,
            ["128-255"] = 0,
            ["256-511"] = 0,
            ["512+"] = 0,
        };

        var sb = new StringBuilder();
        using var messagesFile = File.CreateText(messagesPath);

        client.ApplicationMessageReceivedAsync += async e =>
        {
            messageCount++;
            string topic = e.ApplicationMessage.Topic;
            var payload = e.ApplicationMessage.Payload;
            int len = (int)payload.Length;

            topicCounts.TryGetValue(topic, out int n);
            topicCounts[topic] = n + 1;
            sizeBuckets[BucketFor(len)] += 1;

            string hex = HexPreview(payload, 64);
            string asciiPreview = AsciiPreview(payload, 64);

            Console.WriteLine($"{messageCount:D5} [{topic}] {len}B  hex={hex}");
            if (asciiPreview.Length > 0)
            {
                Console.WriteLine($"             ascii={asciiPreview}");
            }

            // JSONL row - manual since we don't want a JSON lib dep here.
            sb.Clear();
            sb.Append('{');
            sb.Append("\"seq\":").Append(messageCount).Append(',');
            sb.Append("\"ts\":\"").Append(DateTime.UtcNow.ToString("O")).Append("\",");
            sb.Append("\"topic\":\"").Append(JsonEscape(topic)).Append("\",");
            sb.Append("\"length\":").Append(len).Append(',');
            sb.Append("\"hex_preview\":\"").Append(hex).Append("\",");
            sb.Append("\"ascii_preview\":\"").Append(JsonEscape(asciiPreview)).Append('\"');
            sb.Append('}');
            await messagesFile.WriteLineAsync(sb.ToString());
            await messagesFile.FlushAsync();
        };

        await client.ConnectAsync(options, CancellationToken.None);
        Console.Error.WriteLine("# connected, subscribing...");

        await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(opts.Topic))
            .Build());

        var deadline = DateTime.UtcNow.AddSeconds(opts.Seconds);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            Console.Error.WriteLine("# ^C — stopping early");
            cts.Cancel();
        };

        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            await Task.Delay(500, CancellationToken.None);
        }

        Console.Error.WriteLine($"# window closed — {messageCount} messages received");
        await client.DisconnectAsync();

        await File.WriteAllTextAsync(summaryPath, RenderSummary(opts, messageCount, topicCounts, sizeBuckets));
        Console.Error.WriteLine($"# summary written to {summaryPath}");

        return 0;
    }

    static string HexPreview(ReadOnlyMemory<byte> data, int maxBytes)
    {
        var span = data.Span;
        int n = Math.Min(span.Length, maxBytes);
        var sb = new StringBuilder(n * 2);
        for (int i = 0; i < n; i++)
        {
            sb.Append(span[i].ToString("x2"));
        }

        if (span.Length > maxBytes)
        {
            sb.Append('…');
        }

        return sb.ToString();
    }

    static string AsciiPreview(ReadOnlyMemory<byte> data, int maxBytes)
    {
        var span = data.Span;
        int n = Math.Min(span.Length, maxBytes);
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            byte b = span[i];
            sb.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
        }
        if (span.Length > maxBytes)
        {
            sb.Append('…');
        }

        return sb.ToString();
    }

    static string BucketFor(int len) => len switch
    {
        0 => "0",
        < 32 => "1-31",
        < 128 => "32-127",
        < 256 => "128-255",
        < 512 => "256-511",
        _ => "512+",
    };

    static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append($"\\u{(int)c:x4}");
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }
        return sb.ToString();
    }

    static string RenderSummary(
        Options opts,
        int total,
        Dictionary<string, int> topics,
        Dictionary<string, int> sizes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MQTT probe — summary");
        sb.AppendLine();
        sb.AppendLine($"- **Broker**: `{opts.Broker}:{opts.Port}`");
        sb.AppendLine($"- **Topic filter**: `{opts.Topic}`");
        sb.AppendLine($"- **Probe duration**: {opts.Seconds} s");
        sb.AppendLine($"- **Total messages received**: {total}");
        sb.AppendLine();
        sb.AppendLine("## Topics seen");
        sb.AppendLine();
        sb.AppendLine("| Topic | Count |");
        sb.AppendLine("|---|--:|");
        foreach (var kv in topics.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Payload-size distribution");
        sb.AppendLine();
        sb.AppendLine("| Bucket | Count |");
        sb.AppendLine("|---|--:|");
        foreach (var kv in sizes)
        {
            sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Next steps");
        sb.AppendLine();
        sb.AppendLine("- Inspect `messages.jsonl` for representative payload hex/ASCII.");
        sb.AppendLine("- Figure out whether payloads are KISS-wrapped, raw AX.25, JSON envelopes, or something else.");
        sb.AppendLine("- Map topic structure (likely `PACKETNODE/<port>/<direction>` or similar) to per-port stats in `monitor` mode.");
        return sb.ToString();
    }
}
