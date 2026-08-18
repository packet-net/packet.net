using System.Text;
using MQTTnet;
using Packet.Mqtt.Spike;

// SP-001 spike: LinBPQ MQTT frame feed ingestion.
//
// Tom's LinBPQ node publishes every AX.25 frame sent/received across its 4 RF
// ports to mqtt.lan under the PACKETNODE topic. We don't yet know the exact
// topic structure or payload format - this tool starts in `probe` mode to
// observe, then `monitor` mode will parse the frames structurally.
//
// Args:
//   probe   [--seconds <N>]
//     Subscribe to PACKETNODE/# and dump topic + first-64-byte hex preview of
//     every received message. Default 30 s. Output to artifacts/mqtt-probe/<ts>/.
//
//   monitor [--max-messages <N>]
//     Structured ingestion: parses frames via Packet.Kiss / Packet.Ax25, builds
//     per-port stats, writes failures.jsonl + stats.md. (TODO: lands after
//     `probe` has clarified the wire format.)
//
//   --broker <host[:port]>     default 10.45.0.70:1883
//   --topic   <topic>          default PACKETNODE/#
//   --out-dir <dir>            default artifacts/mqtt-{probe,monitor}/<ts>/

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/Packet.Mqtt.Spike -- <probe|monitor> [opts]");
    return 1;
}

var opts = ParseArgs(args);

return opts.Mode switch
{
    "probe" => await ProbeMode.RunAsync(opts),
    "monitor" => await MonitorMode.RunAsync(opts),
    "collect" => await CollectMode.RunAsync(opts),
    _ => Fail($"unknown mode: {opts.Mode}"),
};

static int Fail(string msg) { Console.Error.WriteLine($"# {msg}"); return 1; }

static Options ParseArgs(string[] args)
{
    var opts = new Options
    {
        Mode = args[0],
        Broker = "10.45.0.70",
        Port = 1883,
        Topic = "PACKETNODE/#",
        Seconds = 30,
        MaxMessages = 0,
        OutDir = args[0] == "collect"
            ? Path.Combine("data", "mqtt")
            : Path.Combine("artifacts", $"mqtt-{args[0]}", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")),
        FilenamePrefix = "gb7rdg",
    };
    for (int i = 1; i < args.Length; i++)
    {
        string a = args[i];
        string? next() => i + 1 < args.Length ? args[++i] : null;
        switch (a)
        {
            case "--broker":
                var b = next() ?? throw new ArgumentException("--broker requires host[:port]");
                var parts = b.Split(':');
                opts.Broker = parts[0];
                opts.Port = parts.Length > 1 ? int.Parse(parts[1]) : 1883;
                break;
            case "--topic":
                opts.Topic = next() ?? throw new ArgumentException("--topic requires value");
                break;
            case "--seconds":
                opts.Seconds = int.Parse(next() ?? throw new ArgumentException("--seconds requires value"));
                break;
            case "--max-messages":
                opts.MaxMessages = int.Parse(next() ?? throw new ArgumentException("--max-messages requires value"));
                break;
            case "--out-dir":
                opts.OutDir = next() ?? throw new ArgumentException("--out-dir requires value");
                break;
            case "--filename-prefix":
                opts.FilenamePrefix = next() ?? throw new ArgumentException("--filename-prefix requires value");
                break;
            default:
                throw new ArgumentException($"unknown arg: {a}");
        }
    }
    return opts;
}

public sealed class Options
{
    public string Mode { get; set; } = "";
    public string Broker { get; set; } = "";
    public int Port { get; set; }
    public string Topic { get; set; } = "";
    public int Seconds { get; set; }
    public int MaxMessages { get; set; }
    public string OutDir { get; set; } = "";
    public string FilenamePrefix { get; set; } = "";
}
