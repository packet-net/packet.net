using System.Text;
using System.Text.Json;
using Packet.Ax25;
using Packet.Core;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Original spike behaviour - connect, read N frames, run each through the
/// TNC2 parser → reconstruct → round-trip pipeline, persist failures to
/// JSONL, write a summary markdown.
/// </summary>
/// <remarks>
/// Useful for short-window experimentation. For sustained capture, see
/// <see cref="CollectMode"/>.
/// </remarks>
public static class OneshotMode
{
    public static async Task<int> RunAsync(Options opts)
    {
        Directory.CreateDirectory(opts.OutDir);
        string failuresPath = Path.Combine(opts.OutDir, "failures.jsonl");
        string statsPath = Path.Combine(opts.OutDir, "stats.md");

        Console.Error.WriteLine($"# oneshot → out-dir: {opts.OutDir}");
        Console.Error.WriteLine($"# server: {opts.Host}:{opts.Port}  callsign: {opts.Callsign}  filter: {opts.Filter}");
        Console.Error.WriteLine($"# max-frames: {(opts.MaxFrames == 0 ? "unlimited" : opts.MaxFrames.ToString())}");

        var stats = new Stats();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("# ^C — finishing up...");
            cts.Cancel();
        };

        using var failuresFile = File.CreateText(failuresPath);

        await using var client = new AprsIsClient();
        try
        {
            await client.ConnectAsync(opts.Host, opts.Port, opts.Callsign, -1, opts.Filter, cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"# connect failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        DateTime startedAt = DateTime.UtcNow;

        await foreach (var line in client.ReadLinesAsync(cts.Token))
        {
            stats.LinesReceived++;
            if (line.Length == 0 || line[0] == '#')
            {
                stats.NonFrameLines++;
                continue;
            }

            if (!Tnc2Parser.TryParse(line, out var parsed))
            {
                stats.Tnc2ParseFailures++;
                await LogFailureAsync(failuresFile, "tnc2_parse_failed", line, null, null);
                continue;
            }

            stats.FramesParsed++;

            if (!FrameReconstruct.TryReconstruct(parsed, out var frame, out string? reconstructError))
            {
                stats.ReconstructFailures++;
                stats.TallyReconstructError(reconstructError ?? "unknown");
                await LogFailureAsync(failuresFile, "reconstruct_failed", line, reconstructError, parsed);
                continue;
            }

            byte[] bytes = frame.ToBytes();
            if (!Ax25Frame.TryParse(bytes, out var decoded))
            {
                stats.RoundTripFailures++;
                await LogFailureAsync(failuresFile, "roundtrip_parse_failed", line,
                    $"TryParse rejected {bytes.Length}-byte encoding", parsed);
                continue;
            }
            if (!FrameReconstruct.StructurallyEqual(frame, decoded!))
            {
                stats.RoundTripFailures++;
                await LogFailureAsync(failuresFile, "roundtrip_mismatch", line,
                    $"decoded didn't match: dest={decoded!.Destination.Callsign} src={decoded.Source.Callsign} info={decoded.Info.Length}B",
                    parsed);
                continue;
            }

            stats.RoundTripSuccesses++;
            if (!opts.Quiet)
            {
                Console.WriteLine($"✓ {parsed.Source} > {parsed.Destination} ({parsed.Info.Length}B)");
            }
            if (opts.MaxFrames > 0 && stats.FramesParsed >= opts.MaxFrames)
            {
                Console.Error.WriteLine($"# reached --max-frames {opts.MaxFrames}, stopping");
                break;
            }
        }

        DateTime endedAt = DateTime.UtcNow;
        await File.WriteAllTextAsync(statsPath, stats.RenderMarkdown(opts, startedAt, endedAt));

        Console.Error.WriteLine();
        Console.Error.WriteLine($"# summary written to {statsPath}");
        Console.Error.WriteLine($"# failures jsonl: {failuresPath} ({stats.TotalFailures} entries)");
        Console.Error.WriteLine();
        Console.Error.WriteLine(stats.RenderShortSummary());

        return 0;
    }

    static async Task LogFailureAsync(StreamWriter failuresFile, string kind, string raw, string? reason, Tnc2Parser.Tnc2Line? parsed)
    {
        var record = new
        {
            kind,
            raw,
            reason,
            parsed = parsed is null ? null : new
            {
                source = parsed.Source,
                destination = parsed.Destination,
                digipeaters = parsed.Digipeaters,
                info_first_bytes = HexFirstN(parsed.Info, 16),
                info_length = parsed.Info.Length,
            },
            at = DateTime.UtcNow,
        };
        await failuresFile.WriteLineAsync(JsonSerializer.Serialize(record));
        await failuresFile.FlushAsync();
    }

    static string HexFirstN(ReadOnlyMemory<byte> data, int n)
    {
        int len = Math.Min(data.Length, n);
        var sb = new StringBuilder(len * 2);
        var span = data.Span;
        for (int i = 0; i < len; i++)
        {
            sb.Append(span[i].ToString("x2"));
        }

        return sb.ToString();
    }

}

sealed class Stats
{
    public int LinesReceived { get; set; }
    public int NonFrameLines { get; set; }
    public int FramesParsed { get; set; }
    public int Tnc2ParseFailures { get; set; }
    public int ReconstructFailures { get; set; }
    public int RoundTripFailures { get; set; }
    public int RoundTripSuccesses { get; set; }

    public Dictionary<string, int> ReconstructErrorKinds { get; } = new(StringComparer.Ordinal);

    public int TotalFailures => Tnc2ParseFailures + ReconstructFailures + RoundTripFailures;

    public void TallyReconstructError(string err)
    {
        string kind =
            err.StartsWith("invalid source callsign", StringComparison.Ordinal) ? "invalid_source" :
            err.StartsWith("invalid destination callsign", StringComparison.Ordinal) ? "invalid_destination" :
            err.StartsWith("invalid digipeater callsign", StringComparison.Ordinal) ? "invalid_digipeater" :
            "other";
        ReconstructErrorKinds.TryGetValue(kind, out int n);
        ReconstructErrorKinds[kind] = n + 1;
    }

    public string RenderShortSummary()
    {
        double pct(int x) => FramesParsed == 0 ? 0 : 100.0 * x / FramesParsed;
        return $@"  lines received     : {LinesReceived}
  non-frame lines    : {NonFrameLines}
  frames parsed      : {FramesParsed}
  tnc2 parse failures: {Tnc2ParseFailures}
  reconstruct failed : {ReconstructFailures} ({pct(ReconstructFailures):F1}% of parsed)
  round-trip failed  : {RoundTripFailures} ({pct(RoundTripFailures):F1}% of parsed)
  round-trip succeed : {RoundTripSuccesses} ({pct(RoundTripSuccesses):F1}% of parsed)";
    }

    public string RenderMarkdown(Options opts, DateTime started, DateTime ended)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# APRS-IS spike — stats");
        sb.AppendLine();
        sb.AppendLine($"- **Server**: `{opts.Host}:{opts.Port}`");
        sb.AppendLine($"- **Callsign**: `{opts.Callsign}` (read-only)");
        sb.AppendLine($"- **Filter**: `{opts.Filter}`");
        sb.AppendLine($"- **Started**: {started:O}");
        sb.AppendLine($"- **Ended**: {ended:O}");
        sb.AppendLine($"- **Duration**: {ended - started:c}");
        sb.AppendLine();
        sb.AppendLine("## Counts");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("|---|--:|");
        sb.AppendLine($"| Lines received | {LinesReceived} |");
        sb.AppendLine($"| Non-frame lines (server messages, keepalives) | {NonFrameLines} |");
        sb.AppendLine($"| Frames parsed (TNC2-level) | {FramesParsed} |");
        sb.AppendLine($"| TNC2 parse failures | {Tnc2ParseFailures} |");
        sb.AppendLine($"| Reconstruct (TNC2 → Ax25Frame.Ui) failures | {ReconstructFailures} |");
        sb.AppendLine($"| Round-trip (encode → decode → equal) failures | {RoundTripFailures} |");
        sb.AppendLine($"| Round-trip successes | {RoundTripSuccesses} |");

        if (ReconstructErrorKinds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Reconstruct failure breakdown");
            sb.AppendLine();
            sb.AppendLine("| Kind | Count |");
            sb.AppendLine("|---|--:|");
            foreach (var kv in ReconstructErrorKinds.OrderByDescending(x => x.Value))
            {
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine("- Q-construct pseudo-digipeaters (`qAR`/`qAS`/`qAo`/…) are dropped before reconstruct attempt.");
        sb.AppendLine("- Reconstruct failures are *interesting* — they're real-world data our parser/builder can't represent. See `failures.jsonl` for the raw lines.");
        sb.AppendLine("- This spike doesn't validate the APRS payload itself, only that the AX.25 envelope round-trips.");

        return sb.ToString();
    }
}
