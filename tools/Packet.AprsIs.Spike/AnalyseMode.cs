using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Packet.Ax25;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Offline analyser: replays the accumulated corpus through the AX.25
/// reconstruct + round-trip pipeline and produces a structured report.
/// </summary>
/// <remarks>
/// <para>
/// Reads from one or more SQLite files produced by <see cref="CollectMode"/>.
/// Doesn't touch the network. Idempotent - running twice over the same
/// corpus produces the same report.
/// </para>
/// <para>
/// The corpus columns are denormalised on ingest (source / destination /
/// digi_path) but we re-parse from <c>raw_line</c> here because that's the
/// canonical record and the only one that round-trips losslessly through
/// <see cref="Tnc2Parser"/>.
/// </para>
/// </remarks>
public static class AnalyseMode
{
    public static async Task<int> RunAsync(Options opts)
    {
        var dbs = ResolveDatabases(opts);
        if (dbs.Count == 0)
        {
            Console.Error.WriteLine("# no SQLite files found. Pass --db <path> or --data-dir <dir>.");
            return 1;
        }

        Directory.CreateDirectory(opts.OutDir);
        string failuresPath = Path.Combine(opts.OutDir, "failures.jsonl");
        string statsPath = Path.Combine(opts.OutDir, "stats.md");

        Console.Error.WriteLine($"# analyse → out-dir: {opts.OutDir}");
        Console.Error.WriteLine($"# {dbs.Count} sqlite file(s) to scan");

        var counters = new AnalyseCounters();
        using var failuresFile = File.CreateText(failuresPath);

        foreach (var db in dbs)
        {
            await ScanOneAsync(db, counters, failuresFile, opts);
        }

        await File.WriteAllTextAsync(statsPath, counters.RenderMarkdown(dbs, opts));

        Console.Error.WriteLine();
        Console.Error.WriteLine($"# stats:     {statsPath}");
        Console.Error.WriteLine($"# failures:  {failuresPath} ({counters.TotalFailures} entries)");
        Console.Error.WriteLine();
        Console.Error.WriteLine(counters.RenderShortSummary());

        return 0;
    }

    static List<string> ResolveDatabases(Options opts)
    {
        if (!string.IsNullOrEmpty(opts.Db))
        {
            return File.Exists(opts.Db) ? [opts.Db] : [];
        }
        return Directory.Exists(opts.DataDir)
            ? Directory.GetFiles(opts.DataDir, "*.sqlite").OrderBy(p => p, StringComparer.Ordinal).ToList()
            : [];
    }

    static async Task ScanOneAsync(string dbPath, AnalyseCounters c, StreamWriter failuresFile, Options opts)
    {
        Console.Error.WriteLine($"# scanning {dbPath}");
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT raw_line FROM lines ORDER BY id"
                        + (opts.Limit > 0 ? $" LIMIT {opts.Limit}" : "");

        using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
        {
            string raw = rdr.GetString(0);
            c.Total++;

            if (!Tnc2Parser.TryParse(raw, out var parsed))
            {
                c.Tnc2ParseFailures++;
                await LogFailureAsync(failuresFile, "tnc2_parse_failed", raw, null, null, dbPath);
                continue;
            }

            // Bucket by APRS payload type — works regardless of whether the
            // AX.25 envelope reconstructs, so we get a complete view of what
            // the corpus contains.
            c.PayloadType(AprsPayloadType.Classify(parsed.Info.Span));

            if (!FrameReconstruct.TryReconstruct(parsed, out var frame, out string? recErr))
            {
                c.ReconstructFailures++;
                string bucket = FrameReconstruct.BucketReconstructError(recErr ?? "unknown");
                c.ReconstructBucket(bucket);
                c.TallyOffender(bucket, parsed.Source, parsed.Destination, parsed.Digipeaters);
                await LogFailureAsync(failuresFile, "reconstruct_failed", raw, recErr, parsed, dbPath);
                continue;
            }

            byte[] bytes = frame.ToBytes();
            if (!Ax25Frame.TryParse(bytes, out var decoded))
            {
                c.RoundTripFailures++;
                await LogFailureAsync(failuresFile, "roundtrip_parse_failed", raw,
                    $"TryParse rejected {bytes.Length}-byte encoding", parsed, dbPath);
                continue;
            }
            if (!FrameReconstruct.StructurallyEqual(frame, decoded!))
            {
                c.RoundTripFailures++;
                await LogFailureAsync(failuresFile, "roundtrip_mismatch", raw,
                    $"decoded != built (dest={decoded!.Destination.Callsign} src={decoded.Source.Callsign} info={decoded.Info.Length}B)",
                    parsed, dbPath);
                continue;
            }

            c.RoundTripSuccesses++;
        }
    }

    static async Task LogFailureAsync(
        StreamWriter writer,
        string kind,
        string raw,
        string? reason,
        Tnc2Parser.Tnc2Line? parsed,
        string sourceDb)
    {
        var rec = new
        {
            kind,
            source_db = Path.GetFileName(sourceDb),
            raw,
            reason,
            parsed = parsed is null ? null : new
            {
                source = parsed.Source,
                destination = parsed.Destination,
                digipeaters = parsed.Digipeaters,
                info_length = parsed.Info.Length,
            },
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(rec));
    }
}

sealed class AnalyseCounters
{
    public int Total;
    public int Tnc2ParseFailures;
    public int ReconstructFailures;
    public int RoundTripFailures;
    public int RoundTripSuccesses;

    public Dictionary<string, int> ReconstructBuckets { get; } = new(StringComparer.Ordinal);

    // top-N offender callsigns per failure bucket
    public Dictionary<string, Dictionary<string, int>> OffendersByBucket { get; } = new(StringComparer.Ordinal);

    // APRS payload-type histogram (independent of reconstruct success)
    public Dictionary<string, int> PayloadTypes { get; } = new(StringComparer.Ordinal);

    public int TotalFailures => Tnc2ParseFailures + ReconstructFailures + RoundTripFailures;

    public void ReconstructBucket(string bucket)
    {
        ReconstructBuckets.TryGetValue(bucket, out int n);
        ReconstructBuckets[bucket] = n + 1;
    }

    public void PayloadType(string label)
    {
        PayloadTypes.TryGetValue(label, out int n);
        PayloadTypes[label] = n + 1;
    }

    public void TallyOffender(string bucket, string source, string destination, IReadOnlyList<Tnc2Parser.DigipeaterEntry> digis)
    {
        if (!OffendersByBucket.TryGetValue(bucket, out var inner))
        {
            inner = new Dictionary<string, int>(StringComparer.Ordinal);
            OffendersByBucket[bucket] = inner;
        }
        string offender = bucket switch
        {
            "invalid_source" => source,
            "invalid_destination" => destination,
            "invalid_digipeater" => digis.FirstOrDefault(d => d.Callsign is not null)?.Callsign ?? "?",
            _ => "?",
        };
        inner.TryGetValue(offender, out int n);
        inner[offender] = n + 1;
    }

    public string RenderShortSummary()
    {
        double pct(int x) => Total == 0 ? 0 : 100.0 * x / Total;
        return $@"  total parsed       : {Total}
  tnc2 parse fail    : {Tnc2ParseFailures} ({pct(Tnc2ParseFailures):F2}%)
  reconstruct fail   : {ReconstructFailures} ({pct(ReconstructFailures):F2}%)
  round-trip fail    : {RoundTripFailures} ({pct(RoundTripFailures):F2}%)
  round-trip success : {RoundTripSuccesses} ({pct(RoundTripSuccesses):F2}%)";
    }

    public string RenderMarkdown(IReadOnlyList<string> dbs, Options opts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# APRS-IS corpus analysis — stats");
        sb.AppendLine();
        sb.AppendLine($"- **Run at**: {DateTime.UtcNow:O}");
        sb.AppendLine($"- **Sources**: {dbs.Count} sqlite file(s)");
        foreach (var db in dbs)
        {
            sb.AppendLine($"  - `{Path.GetFileName(db)}`");
        }

        if (opts.Limit > 0)
        {
            sb.AppendLine($"- **Per-file limit**: {opts.Limit}");
        }

        sb.AppendLine();

        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine("| Metric | Count | % of total |");
        sb.AppendLine("|---|--:|--:|");
        double pct(int x) => Total == 0 ? 0 : 100.0 * x / Total;
        sb.AppendLine($"| Lines processed | {Total} | 100.00% |");
        sb.AppendLine($"| TNC2 parse failures | {Tnc2ParseFailures} | {pct(Tnc2ParseFailures):F2}% |");
        sb.AppendLine($"| Reconstruct failures | {ReconstructFailures} | {pct(ReconstructFailures):F2}% |");
        sb.AppendLine($"| Round-trip failures | {RoundTripFailures} | {pct(RoundTripFailures):F2}% |");
        sb.AppendLine($"| **Round-trip successes** | **{RoundTripSuccesses}** | **{pct(RoundTripSuccesses):F2}%** |");
        sb.AppendLine();

        if (ReconstructBuckets.Count > 0)
        {
            sb.AppendLine("## Reconstruct failures — buckets");
            sb.AppendLine();
            sb.AppendLine("| Kind | Count | % of recon-fail |");
            sb.AppendLine("|---|--:|--:|");
            double rpct(int x) => ReconstructFailures == 0 ? 0 : 100.0 * x / ReconstructFailures;
            foreach (var kv in ReconstructBuckets.OrderByDescending(x => x.Value))
            {
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} | {rpct(kv.Value):F2}% |");
            }
            sb.AppendLine();
        }

        foreach (var (bucket, inner) in OffendersByBucket.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"### Top 20 offenders — `{bucket}`");
            sb.AppendLine();
            sb.AppendLine("| Callsign | Count |");
            sb.AppendLine("|---|--:|");
            foreach (var kv in inner.OrderByDescending(x => x.Value).Take(20))
            {
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} |");
            }
            sb.AppendLine();
        }

        if (PayloadTypes.Count > 0)
        {
            int classifiedTotal = PayloadTypes.Values.Sum();
            double tpct(int x) => classifiedTotal == 0 ? 0 : 100.0 * x / classifiedTotal;
            sb.AppendLine("## APRS payload-type breakdown");
            sb.AppendLine();
            sb.AppendLine("Classified by the first information-field byte (APRS101 §5 DTI). Counts every line whose TNC2 envelope parsed, regardless of whether the AX.25 envelope reconstructed.");
            sb.AppendLine();
            sb.AppendLine("| Type | Count | % of classified |");
            sb.AppendLine("|---|--:|--:|");
            foreach (var kv in PayloadTypes.OrderByDescending(x => x.Value))
            {
                sb.AppendLine($"| `{kv.Key}` | {kv.Value} | {tpct(kv.Value):F2}% |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Notes");
        sb.AppendLine();
        sb.AppendLine("- This run replays the corpus exactly as it was captured. AX.25 parser changes change these numbers.");
        sb.AppendLine("- See `failures.jsonl` for every failure with raw TNC2 line + reason + originating SQLite file.");
        return sb.ToString();
    }
}
