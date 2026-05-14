using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Packet.Aprs;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Differential test: walk every line with DTI <c>!</c> or <c>=</c> in
/// the supplied corpus (joined to <c>direwolf_decoded</c> for the
/// reference), run our <see cref="AprsPositionDecoder"/> on the same
/// payload, and tally agreements / disagreements / rejections.
/// </summary>
/// <remarks>
/// <para>
/// Walks corpus → builds a histogram of outcomes:
/// <list type="bullet">
///   <item><c>both_ok_match</c> — both decoders produced lat/lon and they
///   agree within 1e-4° (about 11 m at equator).</item>
///   <item><c>both_ok_mismatch</c> — both decoded but lat/lon differ
///   beyond tolerance. Surfaces real bugs.</item>
///   <item><c>only_us</c> — we decoded, direwolf reported an error.
///   Either we're over-permissive or direwolf flagged a spec-divergent
///   frame we silently accepted.</item>
///   <item><c>only_direwolf</c> — direwolf decoded, we rejected.
///   Surfaces under-strict cases we should accept.</item>
///   <item><c>both_failed</c> — both gave up. Expected for the
///   long-tail of malformed firmware-bug frames.</item>
/// </list>
/// </para>
/// <para>
/// Writes a Markdown report to <c>OutDir/differential.md</c> with the
/// per-bucket count, percentage, and a few example payloads.
/// </para>
/// </remarks>
public static class DifferentialMode
{
    private const double LatLonToleranceDegrees = 1e-4; // ~11 m
    private const int MaxExamplesPerBucket = 5;

    public static async Task<int> RunAsync(Options opts)
    {
        var dbs = ResolveDatabases(opts);
        if (dbs.Count == 0)
        {
            Console.Error.WriteLine($"# no SQLite files matched {opts.Db} / {opts.DataDir}");
            return 1;
        }

        Directory.CreateDirectory(opts.OutDir);

        var stats = new BucketStats();
        long processed = 0;
        foreach (var dbPath in dbs)
        {
            Console.WriteLine($"# scanning {dbPath} …");
            await using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT l.info, d.latitude, d.longitude, d.has_error, d.decoded_type
                FROM lines l JOIN direwolf_decoded d ON d.line_id = l.id
                WHERE hex(substr(l.info, 1, 1)) IN
                    ('21',  -- '!' no-timestamp, no-msg
                     '3D',  -- '=' no-timestamp, msg-capable
                     '40',  -- '@' timestamped, msg-capable
                     '2F')  -- '/' timestamped, no-msg
            """;

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                var info = (byte[])rdr.GetValue(0);
                double? dwLat = rdr.IsDBNull(1) ? null : rdr.GetDouble(1);
                double? dwLon = rdr.IsDBNull(2) ? null : rdr.GetDouble(2);
                long hasError = rdr.IsDBNull(3) ? 0 : rdr.GetInt64(3);
                string decodedType = rdr.IsDBNull(4) ? "" : rdr.GetString(4);

                bool usOk = AprsPositionDecoder.TryDecode(info, out var ours);
                // dwOk = direwolf produced a position. has_error can be set
                // for non-fatal warnings (e.g. lowercase callsign in the
                // source, non-standard frequency in comment); we only use
                // it to disambiguate true rejections from warnings if
                // lat/lon is absent altogether.
                bool dwOk = dwLat is not null && dwLon is not null;

                var bucket = ClassifyOutcome(usOk, dwOk, ours, dwLat, dwLon);
                stats.Record(bucket, info, ours, dwLat, dwLon);

                processed++;
                if (processed % 50_000 == 0)
                {
                    Console.WriteLine($"# … {processed:N0} rows, current bucket counts:");
                    stats.WriteShortSummary(Console.Out);
                }

                if (opts.Limit > 0 && processed >= opts.Limit) break;
            }
            if (opts.Limit > 0 && processed >= opts.Limit) break;
        }

        var reportPath = Path.Combine(opts.OutDir, "differential.md");
        await File.WriteAllTextAsync(reportPath, stats.RenderMarkdown(processed));
        Console.WriteLine();
        Console.WriteLine($"# scanned {processed:N0} rows");
        stats.WriteShortSummary(Console.Out);
        Console.WriteLine($"# report → {reportPath}");
        return 0;
    }

    private static Bucket ClassifyOutcome(bool usOk, bool dwOk, AprsPosition ours, double? dwLat, double? dwLon)
    {
        if (usOk && dwOk)
        {
            double dlat = Math.Abs(ours.Latitude  - dwLat!.Value);
            double dlon = Math.Abs(ours.Longitude - dwLon!.Value);
            return (dlat <= LatLonToleranceDegrees && dlon <= LatLonToleranceDegrees)
                ? Bucket.BothOkMatch
                : Bucket.BothOkMismatch;
        }
        if (usOk && !dwOk) return Bucket.OnlyUs;
        if (!usOk && dwOk) return Bucket.OnlyDirewolf;
        return Bucket.BothFailed;
    }

    private static List<string> ResolveDatabases(Options opts)
    {
        if (!string.IsNullOrEmpty(opts.Db) && File.Exists(opts.Db))
        {
            return [opts.Db];
        }
        if (Directory.Exists(opts.DataDir))
        {
            return Directory.EnumerateFiles(opts.DataDir, "*.sqlite")
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }
        return [];
    }

    private enum Bucket { BothOkMatch, BothOkMismatch, OnlyUs, OnlyDirewolf, BothFailed }

    private sealed class BucketStats
    {
        private readonly Dictionary<Bucket, long> counts = new();
        private readonly Dictionary<Bucket, List<string>> examples = new();

        public void Record(Bucket bucket, byte[] info, AprsPosition ours, double? dwLat, double? dwLon)
        {
            counts.TryGetValue(bucket, out var c);
            counts[bucket] = c + 1;
            if (!examples.TryGetValue(bucket, out var list))
            {
                list = new List<string>();
                examples[bucket] = list;
            }
            if (list.Count < MaxExamplesPerBucket)
            {
                var infoStr = Encoding.Latin1.GetString(info, 0, Math.Min(info.Length, 80));
                list.Add(bucket switch
                {
                    Bucket.BothOkMatch    => $"`{Escape(infoStr)}` ours=({ours.Latitude:F4},{ours.Longitude:F4})",
                    Bucket.BothOkMismatch => $"`{Escape(infoStr)}` ours=({ours.Latitude:F4},{ours.Longitude:F4}) dw=({dwLat:F4},{dwLon:F4})",
                    Bucket.OnlyUs         => $"`{Escape(infoStr)}` ours=({ours.Latitude:F4},{ours.Longitude:F4}) dw=error",
                    Bucket.OnlyDirewolf   => $"`{Escape(infoStr)}` dw=({dwLat:F4},{dwLon:F4}) ours=rejected",
                    Bucket.BothFailed     => $"`{Escape(infoStr)}`",
                    _ => infoStr,
                });
            }
        }

        public void WriteShortSummary(TextWriter w)
        {
            long total = counts.Values.Sum();
            if (total == 0) { w.WriteLine("#   (no rows yet)"); return; }
            foreach (var (bucket, count) in counts.OrderByDescending(kv => kv.Value))
            {
                w.WriteLine($"#   {bucket,-18} {count,10:N0}  {100.0 * count / total,5:F1}%");
            }
        }

        public string RenderMarkdown(long total)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# APRS position decoder — differential vs direwolf");
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Scanned **{total:N0}** lines with DTI `!` or `=` from the APRS-IS corpus.");
            sb.AppendLine();
            sb.AppendLine("## Bucket breakdown");
            sb.AppendLine();
            sb.AppendLine("| Bucket | Count | % |");
            sb.AppendLine("|---|---:|---:|");
            foreach (var (bucket, count) in counts.OrderByDescending(kv => kv.Value))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{bucket}` | {count:N0} | {100.0 * count / total:F2}% |");
            }
            sb.AppendLine();
            sb.AppendLine("## Examples per bucket");
            sb.AppendLine();
            foreach (var bucket in new[] { Bucket.BothOkMismatch, Bucket.OnlyUs, Bucket.OnlyDirewolf, Bucket.BothFailed, Bucket.BothOkMatch })
            {
                if (!examples.TryGetValue(bucket, out var list) || list.Count == 0) continue;
                sb.AppendLine(CultureInfo.InvariantCulture, $"### `{bucket}`");
                foreach (var ex in list)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {ex}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private static string Escape(string s)
            => s.Replace("`", "\\`", StringComparison.Ordinal)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
    }
}
