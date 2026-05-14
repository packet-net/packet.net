using Packet.AprsIs.Spike;

// SP-001b: APRS-IS UI-frame ingestion spike.
//
// Two modes:
//   oneshot — original behaviour: connect, read N frames, run each through
//             the TNC2 parser → reconstruct → round-trip pipeline, persist
//             failures to JSONL, write a summary markdown. Good for short
//             experimentation.
//
//   collect — long-running daemon: persists every TNC2 line to per-day
//             SQLite files (gb7rdg-YYYY-MM-DD.sqlite or whatever
//             --filename-prefix you pass). Daily rotation at UTC midnight,
//             exponential-backoff reconnect, graceful SIGTERM shutdown.
//             Designed to run forever in a VM / on a server.
//
// Usage:
//   dotnet run -- oneshot [--max-frames N] [--out-dir dir] [other opts...]
//   dotnet run -- collect [--out-dir dir] [--filename-prefix prefix] [other opts...]
//
// Common opts:
//   --server <host[:port]>     default rotate.aprs2.net:14580
//   --callsign <CALL[-SSID]>   default N0CALL
//   --filter <filter>          default "t/poimqstuc" (all APRS payload types)
//   --quiet                    (oneshot) suppress per-frame stdout

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: dotnet run --project tools/Packet.AprsIs.Spike -- <oneshot|collect> [opts]");
    return 1;
}

var opts = ParseArgs(args);

return opts.Mode switch
{
    "oneshot"      => await OneshotMode.RunAsync(opts),
    "collect"      => await CollectMode.RunAsync(opts),
    "analyse"      => await AnalyseMode.RunAsync(opts),
    "direwolf"     => await DirewolfMode.RunAsync(opts),
    "differential" => await DifferentialMode.RunAsync(opts),
    _              => Fail($"unknown mode: {opts.Mode}"),
};

static int Fail(string msg) { Console.Error.WriteLine($"# {msg}"); return 1; }

static Options ParseArgs(string[] args)
{
    string mode = args[0];
    var opts = new Options
    {
        Mode = mode,
        Host = "rotate.aprs2.net",
        Port = 14580,
        Callsign = "N0CALL",
        Filter = "t/poimqstuc",
        MaxFrames = 1000,
        OutDir = mode switch
        {
            "collect" => Path.Combine("data", "aprs-is"),
            "analyse" => Path.Combine("artifacts", "aprs-is-analysis", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")),
            _         => Path.Combine("artifacts", "aprs-is-spike", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")),
        },
        FilenamePrefix = "aprs-is",
        Quiet = false,
        DataDir = "/home/tf/aprs-is-data",
        Db = "",
        Limit = 0,
        DirewolfBin = "/usr/bin/decode_aprs",
        BatchSize = 1000,
        Reprocess = false,
    };

    for (int i = 1; i < args.Length; i++)
    {
        string a = args[i];
        string? next() => i + 1 < args.Length ? args[++i] : null;
        switch (a)
        {
            case "--server":
                var sv = next() ?? throw new ArgumentException("--server requires host:port");
                var parts = sv.Split(':');
                opts.Host = parts[0];
                opts.Port = parts.Length > 1 ? int.Parse(parts[1]) : 14580;
                break;
            case "--callsign":
                opts.Callsign = next() ?? throw new ArgumentException("--callsign requires value");
                break;
            case "--filter":
                opts.Filter = next() ?? throw new ArgumentException("--filter requires value");
                break;
            case "--max-frames":
                opts.MaxFrames = int.Parse(next() ?? throw new ArgumentException("--max-frames requires value"));
                break;
            case "--out-dir":
                opts.OutDir = next() ?? throw new ArgumentException("--out-dir requires value");
                break;
            case "--filename-prefix":
                opts.FilenamePrefix = next() ?? throw new ArgumentException("--filename-prefix requires value");
                break;
            case "--quiet":
                opts.Quiet = true;
                break;
            case "--db":
                opts.Db = next() ?? throw new ArgumentException("--db requires path");
                break;
            case "--data-dir":
                opts.DataDir = next() ?? throw new ArgumentException("--data-dir requires path");
                break;
            case "--limit":
                opts.Limit = int.Parse(next() ?? throw new ArgumentException("--limit requires value"));
                break;
            case "--direwolf-bin":
                opts.DirewolfBin = next() ?? throw new ArgumentException("--direwolf-bin requires path");
                break;
            case "--batch-size":
                opts.BatchSize = int.Parse(next() ?? throw new ArgumentException("--batch-size requires value"));
                break;
            case "--reprocess":
                opts.Reprocess = true;
                break;
            default:
                throw new ArgumentException($"unknown arg: {a}");
        }
    }
    return opts;
}

namespace Packet.AprsIs.Spike
{
    public sealed class Options
    {
        public string Mode { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Callsign { get; set; } = "";
        public string Filter { get; set; } = "";
        public int MaxFrames { get; set; }
        public string OutDir { get; set; } = "";
        public string FilenamePrefix { get; set; } = "";
        public bool Quiet { get; set; }
        // analyse mode:
        public string Db { get; set; } = "";
        public string DataDir { get; set; } = "";
        public int Limit { get; set; }
        // direwolf mode:
        public string DirewolfBin { get; set; } = "";
        public int BatchSize { get; set; }
        public bool Reprocess { get; set; }
    }
}
