using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Pipes captured APRS-IS lines through direwolf's <c>decode_aprs</c>
/// utility and persists the structured output back into the corpus
/// SQLite as a sibling table. Acts as a "ground truth" reference: when
/// we eventually grow our own APRS decoder, every line has a direwolf
/// interpretation to diff against.
/// </summary>
/// <remarks>
/// <para>
/// The man page recommends stripping APRS-IS q-construct routing
/// (<c>,qAR,whatever</c>) before feeding lines to decode_aprs because
/// they're internet-side metadata that never appears on the wire and
/// decode_aprs rightly rejects them as invalid digipeaters. We apply
/// the suggested regex (<c>,qA.*:</c> → <c>:</c>) to each line before
/// piping.
/// </para>
/// <para>
/// Output schema in the corpus SQLite (idempotent — re-running just
/// overwrites rows):
/// <code>
///   CREATE TABLE direwolf_decoded (
///     line_id      INTEGER PRIMARY KEY,
///     decoded_type TEXT,
///     latitude     REAL,
///     longitude    REAL,
///     altitude_m   REAL,
///     comment      TEXT,
///     has_error    INTEGER NOT NULL DEFAULT 0,
///     error_first  TEXT,
///     raw_output   TEXT NOT NULL,
///     FOREIGN KEY (line_id) REFERENCES lines(id)
///   );
/// </code>
/// </para>
/// <para>
/// Performance: ~10k lines/sec on a single decode_aprs subprocess on
/// this VM. We bulk-feed in batches of 1000 to amortise the parsing
/// loop on our side; the decode_aprs process itself is reused for the
/// whole run.
/// </para>
/// </remarks>
public static class DirewolfMode
{
    // FIXED 2026-05-14: the previous regex `,qA.*:` was greedy across
    // `:` characters in payload text, over-stripping when the comment
    // contained URLs like `http://`. That over-strip caused direwolf
    // to interpret garbage as compressed positions, producing wildly
    // wrong coordinates that we mis-attributed to direwolf bugs.
    // The current pattern stops at the FIRST `:` after the q-construct.
    static readonly Regex QConstructStripper = new(@",q[A-Z][A-Z]?(,[^:]+)?:", RegexOptions.Compiled);
    // Strip every CSI sequence — direwolf emits more than just SGR (m/K).
    // It also emits cursor-erase (J), which my first pass missed and left
    // a stray "[0J" line that threw line-index parsing off by one.
    static readonly Regex AnsiStripper = new(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);
    // Matches a TNC2 monitor format header: SRC[-SSID]>DEST[-SSID][,VIA...]:...
    // Anchored at the start of a line, used to detect frame boundaries
    // in decode_aprs's output.
    static readonly Regex Tnc2Header = new(@"^[A-Za-z0-9-]+>[A-Za-z0-9-]+[^:]*:", RegexOptions.Compiled);

    public static async Task<int> RunAsync(Options opts)
    {
        var dbs = ResolveDatabases(opts);
        if (dbs.Count == 0)
        {
            Console.Error.WriteLine("# no SQLite files found");
            return 1;
        }

        Console.Error.WriteLine($"# direwolf-decode: {dbs.Count} sqlite file(s)");
        Console.Error.WriteLine($"# decode_aprs:    {opts.DirewolfBin}");

        foreach (var db in dbs)
        {
            await ProcessOneAsync(db, opts);
        }
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

    static async Task ProcessOneAsync(string dbPath, Options opts)
    {
        Console.Error.WriteLine($"# scanning {dbPath}");
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        await conn.OpenAsync();

        // The collector holds the same SQLite file open and is also a
        // writer; without a busy_timeout, our inserts collide immediately.
        // 30 s gives the collector plenty of room to flush a transaction
        // between our batches.
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync();
        }

        // Make sure the sink table exists.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS direwolf_decoded (
                  line_id      INTEGER PRIMARY KEY,
                  decoded_type TEXT,
                  latitude     REAL,
                  longitude    REAL,
                  altitude_m   REAL,
                  comment      TEXT,
                  has_error    INTEGER NOT NULL DEFAULT 0,
                  error_first  TEXT,
                  raw_output   TEXT NOT NULL,
                  FOREIGN KEY (line_id) REFERENCES lines(id)
                );
                CREATE INDEX IF NOT EXISTS idx_direwolf_type ON direwolf_decoded(decoded_type);
                CREATE INDEX IF NOT EXISTS idx_direwolf_err  ON direwolf_decoded(has_error);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Resume logic: skip lines that already have a direwolf row.
        string sql = opts.Reprocess
            ? "SELECT id, raw_line FROM lines ORDER BY id"
            : "SELECT id, raw_line FROM lines WHERE id NOT IN (SELECT line_id FROM direwolf_decoded) ORDER BY id";
        if (opts.Limit > 0) sql += $" LIMIT {opts.Limit}";

        var batch = new List<(long id, string raw)>(opts.BatchSize);
        long total = 0;
        var sw = Stopwatch.StartNew();

        using (var reader = conn.CreateCommand())
        {
            reader.CommandText = sql;
            using var rdr = await reader.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                batch.Add((rdr.GetInt64(0), rdr.GetString(1)));
                if (batch.Count >= opts.BatchSize)
                {
                    await FlushBatchAsync(batch, conn, opts);
                    total += batch.Count;
                    batch.Clear();
                    Console.Error.WriteLine($"# {total} processed ({total / sw.Elapsed.TotalSeconds:F0}/sec)");
                }
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch, conn, opts);
            total += batch.Count;
        }

        await conn.CloseAsync();
        Console.Error.WriteLine($"# {dbPath}: {total} new rows added in {sw.Elapsed.TotalSeconds:F1}s");
    }

    static async Task FlushBatchAsync(List<(long id, string raw)> batch, SqliteConnection conn, Options opts)
    {
        // Spin up a fresh decode_aprs per batch — the process accepts a file
        // via stdin and exits when stdin closes.
        var psi = new ProcessStartInfo
        {
            FileName = opts.DirewolfBin,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // strip TERM so direwolf doesn't try to emit colours
            Environment = { ["TERM"] = "dumb" },
        };
        using var proc = Process.Start(psi) ?? throw new Exception("decode_aprs failed to start");

        var writer = Task.Run(async () =>
        {
            foreach (var (_, raw) in batch)
            {
                await proc.StandardInput.WriteLineAsync(SanitiseForDirewolf(raw));
            }
            proc.StandardInput.Close();
        });

        var output = new StringBuilder();
        var reader = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                output.AppendLine(line);
            }
        });

        await writer;
        await reader;
        await proc.WaitForExitAsync();

        var frames = SplitOutputByFrame(output.ToString());
        if (frames.Count != batch.Count)
        {
            Console.Error.WriteLine($"# warning: fed {batch.Count} lines, got {frames.Count} frames back");
        }

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        using var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT OR REPLACE INTO direwolf_decoded
              (line_id, decoded_type, latitude, longitude, altitude_m, comment, has_error, error_first, raw_output)
            VALUES
              ($id, $type, $lat, $lon, $alt, $comment, $err, $errf, $raw);
            """;
        insertCmd.Parameters.Add("$id", SqliteType.Integer);
        insertCmd.Parameters.Add("$type", SqliteType.Text);
        insertCmd.Parameters.Add("$lat", SqliteType.Real);
        insertCmd.Parameters.Add("$lon", SqliteType.Real);
        insertCmd.Parameters.Add("$alt", SqliteType.Real);
        insertCmd.Parameters.Add("$comment", SqliteType.Text);
        insertCmd.Parameters.Add("$err", SqliteType.Integer);
        insertCmd.Parameters.Add("$errf", SqliteType.Text);
        insertCmd.Parameters.Add("$raw", SqliteType.Text);

        for (int i = 0; i < Math.Min(batch.Count, frames.Count); i++)
        {
            var (id, _) = batch[i];
            var parsed = ParseFrameOutput(frames[i]);
            insertCmd.Parameters["$id"].Value = id;
            insertCmd.Parameters["$type"].Value = (object?)parsed.DecodedType ?? DBNull.Value;
            insertCmd.Parameters["$lat"].Value = parsed.Latitude.HasValue ? (object)parsed.Latitude.Value : DBNull.Value;
            insertCmd.Parameters["$lon"].Value = parsed.Longitude.HasValue ? (object)parsed.Longitude.Value : DBNull.Value;
            insertCmd.Parameters["$alt"].Value = parsed.AltitudeM.HasValue ? (object)parsed.AltitudeM.Value : DBNull.Value;
            insertCmd.Parameters["$comment"].Value = (object?)parsed.Comment ?? DBNull.Value;
            insertCmd.Parameters["$err"].Value = parsed.HasError ? 1 : 0;
            insertCmd.Parameters["$errf"].Value = (object?)parsed.FirstError ?? DBNull.Value;
            insertCmd.Parameters["$raw"].Value = frames[i];
            await insertCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    static string SanitiseForDirewolf(string rawLine)
    {
        // Strip ",qXY,...:" injected by APRS-IS. The man page recommends
        // exactly this transformation.
        return QConstructStripper.Replace(rawLine, ":");
    }

    // Delegate to the shared splitter in DirewolfPipeline. The fixed
    // splitter requires a frame-boundary header to be preceded by a
    // blank line — see DirewolfPipeline.SplitOutputByFrame for the
    // detailed rationale (concatenated-frame alignment bug, 2026-05-14).
    static List<string> SplitOutputByFrame(string output)
        => DirewolfPipeline.SplitOutputByFrame(output);

    static readonly Regex LatLonAlt = new(
        @"([NS])\s+(\d+)\s+(\d+\.\d+),\s+([EW])\s+(\d+)\s+(\d+\.\d+)(?:,\s+alt\s+(-?\d+)\s*m)?",
        RegexOptions.Compiled);

    /// <summary>Parse decode_aprs's multi-line per-frame output into a row.</summary>
    static DirewolfRow ParseFrameOutput(string frame)
    {
        var result = new DirewolfRow();
        var lines = frame.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) return result;

        // First line is the echoed input. Type label is on the next line that
        // doesn't look like an error/warning and doesn't start with a digit
        // or N/S/E/W coord. Heuristic: it's the line after the input with a
        // recognisable prefix.
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            // Error / warning lines.
            if (line.StartsWith("Error", StringComparison.Ordinal) ||
                line.StartsWith("ERROR", StringComparison.Ordinal) ||
                line.StartsWith("Warning", StringComparison.Ordinal) ||
                line.StartsWith("Invalid", StringComparison.Ordinal) ||
                line.StartsWith("Address ", StringComparison.Ordinal) ||
                line.StartsWith("Failed to ", StringComparison.Ordinal) ||
                line.StartsWith("Digi", StringComparison.Ordinal) && line.Contains("Address"))
            {
                result.HasError = true;
                result.FirstError ??= line;
                continue;
            }

            // Coord line.
            var m = LatLonAlt.Match(line);
            if (m.Success)
            {
                double lat = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                           + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) / 60.0;
                if (m.Groups[1].Value == "S") lat = -lat;
                double lon = double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture)
                           + double.Parse(m.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture) / 60.0;
                if (m.Groups[4].Value == "W") lon = -lon;
                result.Latitude = lat;
                result.Longitude = lon;
                if (m.Groups[7].Success)
                {
                    result.AltitudeM = double.Parse(m.Groups[7].Value, System.Globalization.CultureInfo.InvariantCulture);
                }
                continue;
            }

            // Type-label line: short, doesn't contain ":" early, sits before coords.
            if (result.DecodedType is null && !line.Contains("0x") && line.Length < 120)
            {
                result.DecodedType = line;
                continue;
            }

            // Anything else gets treated as the comment if we haven't set one.
            if (result.Comment is null && !line.StartsWith("Use of ", StringComparison.Ordinal)
                && !line.StartsWith("Tell the sender", StringComparison.Ordinal)
                && !line.StartsWith("For most ", StringComparison.Ordinal)
                && !line.StartsWith("\"", StringComparison.Ordinal))
            {
                result.Comment = line;
            }
        }

        return result;
    }

    sealed class DirewolfRow
    {
        public string? DecodedType { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public double? AltitudeM { get; set; }
        public string? Comment { get; set; }
        public bool HasError { get; set; }
        public string? FirstError { get; set; }
    }
}
