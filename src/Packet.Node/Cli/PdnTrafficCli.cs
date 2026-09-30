using System.Globalization;
using Packet.Node.Core.Traffic;

namespace Packet.Node.Cli;

/// <summary>
/// The <c>pdn traffic</c> subcommand family (SP-003, #178): <c>export</c> writes the node's
/// persisted traffic log as a portable JSON-lines capture, and <c>replay</c> runs a capture back
/// through the AX.25 parser and the link observer and prints what a third party would have
/// read, frame by frame, with the parse verdict of every frame. Both short-circuit before the
/// web host like the other verbs: export opens the traffic database read-only and nothing
/// else; replay opens nothing but the capture file.
/// </summary>
public static class PdnTrafficCli
{
    public static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length < 2)
        {
            return Task.FromResult(Usage("(none)"));
        }
        return Task.FromResult(args[1] switch
        {
            "export" => Export(args),
            "replay" => Replay(args),
            var other => Usage(other),
        });
    }

    private static int Usage(string verb)
    {
        Console.Error.WriteLine($"pdn traffic: unknown subcommand '{verb}' (want export or replay).");
        Console.Error.WriteLine("  pdn traffic export [--traffic-db <traffic.db>] [--db <pdn.db>] [--port <id>] [--since <utc>] [--until <utc>] [--limit <n>] [--out <file>]");
        Console.Error.WriteLine("      write the traffic log as JSON lines, oldest first (default: the newest 10000 frames, to stdout)");
        Console.Error.WriteLine("  pdn traffic replay <capture.jsonl> [--port <id>]");
        Console.Error.WriteLine("      run a capture through the AX.25 parser and the link observer and print the reading");
        return 2;
    }

    private static int Export(string[] args)
    {
        var trafficDb = Option(args, "--traffic-db") ?? DefaultTrafficDbPath(args);
        if (trafficDb is null || !File.Exists(trafficDb))
        {
            Console.Error.WriteLine("pdn traffic export: no traffic database found. Name it with --traffic-db, or --db <pdn.db> for the traffic.db beside it.");
            return 1;
        }
        int limit = 10_000;
        if (Option(args, "--limit") is { } lim && (!int.TryParse(lim, NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit <= 0))
        {
            Console.Error.WriteLine("pdn traffic export: --limit wants a positive number.");
            return 2;
        }
        DateTimeOffset? since = null, until = null;
        if (Option(args, "--since") is { } s)
        {
            if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                Console.Error.WriteLine("pdn traffic export: --since wants an ISO 8601 time (e.g. 2026-09-30T20:00:00Z).");
                return 2;
            }
            since = parsed;
        }
        if (Option(args, "--until") is { } u)
        {
            if (!DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                Console.Error.WriteLine("pdn traffic export: --until wants an ISO 8601 time.");
                return 2;
            }
            until = parsed;
        }

        var store = new SqliteTrafficStore(trafficDb);
        // The store answers newest-first; a capture is oldest-first so it replays in order.
        var frames = store.Query(Option(args, "--port"), since, until, limit).Reverse().Select(TrafficCapture.FromTrafficFrame);

        if (Option(args, "--out") is { } outPath)
        {
            using var writer = new StreamWriter(outPath, append: false);
            int n = WriteAll(writer, frames);
            Console.Error.WriteLine($"pdn traffic export: {n} frames from {trafficDb} to {outPath}");
        }
        else
        {
            WriteAll(Console.Out, frames);
        }
        return 0;
    }

    private static int WriteAll(TextWriter writer, IEnumerable<TrafficCaptureFrame> frames)
    {
        int n = 0;
        foreach (var frame in frames)
        {
            TrafficCapture.WriteFrame(writer, frame);
            n++;
        }
        writer.Flush();
        return n;
    }

    private static int Replay(string[] args)
    {
        var path = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : null;
        if (path is null)
        {
            Console.Error.WriteLine("usage: pdn traffic replay <capture.jsonl> [--port <id>]");
            return 2;
        }
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"pdn traffic replay: no such capture: {path}");
            return 1;
        }
        TrafficReplay.ReplayReport report;
        try
        {
            using var reader = new StreamReader(path);
            report = TrafficReplay.Run(TrafficCapture.Read(reader), Option(args, "--port"));
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"pdn traffic replay: {ex.Message}");
            return 1;
        }
        Console.Out.Write(TrafficReplay.Render(report));
        return 0;
    }

    // traffic.db beside the resolved pdn.db: the node's default (Program.ResolveTrafficDbPath)
    // when traffic.path is unset. An operator with traffic.path set names it with --traffic-db.
    private static string? DefaultTrafficDbPath(string[] args)
    {
        var pdnDb = NodeStatePaths.ResolveExistingDbPath(args);
        return pdnDb is null ? null : Path.Combine(Path.GetDirectoryName(pdnDb) ?? ".", "traffic.db");
    }

    private static string? Option(string[] args, string name)
    {
        for (int i = 2; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
