using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Packet.AprsIs.Spike;

/// <summary>
/// For corpus lines where direwolf rejected the AX.25 envelope
/// (typically <c>"Bad source address"</c> for letter SSIDs like D-Star
/// <c>-D</c> / <c>-B</c>), rewrite the envelope to an AX.25-valid form
/// via <see cref="Tnc2Parser.TryRewriteForAx25"/> and feed the modified
/// frame through <c>decode_aprs</c>. Results go to a new
/// <c>direwolf_decoded_rewrite</c> table keyed by <c>line_id</c>.
/// </summary>
/// <remarks>
/// <para>
/// The info-field payload is preserved verbatim - only the
/// source / destination / digipeater callsigns get rewritten. So this
/// is a legitimate A/B test of the *payload* decoder: if direwolf still
/// produces wrong coordinates after the envelope is fixed, the bug
/// can't be blamed on the envelope rejection masking the real issue.
/// </para>
/// <para>
/// Strategy: D-Star letter SSIDs (<c>-D</c>, <c>-B</c>, <c>-T</c>) get
/// mapped to <c>-1</c>. Lowercase bases get uppercased. Bases longer
/// than 6 chars get truncated. Q-constructs (<c>qAR</c>, <c>qAS</c>)
/// are preserved verbatim - direwolf strips them anyway.
/// </para>
/// </remarks>
public static class DirewolfRewriteMode
{
    public static async Task<int> RunAsync(Options opts)
    {
        var dbs = ResolveDatabases(opts);
        if (dbs.Count == 0)
        {
            Console.Error.WriteLine($"# no SQLite files matched {opts.Db} / {opts.DataDir}");
            return 1;
        }

        foreach (var dbPath in dbs)
        {
            await ProcessOneAsync(dbPath, opts);
        }
        return 0;
    }

    private static async Task ProcessOneAsync(string dbPath, Options opts)
    {
        Console.Error.WriteLine($"# rewriting {dbPath}");
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        await conn.OpenAsync();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA busy_timeout = 30000;";
            await pragma.ExecuteNonQueryAsync();
        }

        // Ensure target table.
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS direwolf_decoded_rewrite (
                  line_id      INTEGER PRIMARY KEY,
                  rewritten    TEXT NOT NULL,
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
                CREATE INDEX IF NOT EXISTS idx_dwrw_err ON direwolf_decoded_rewrite(has_error);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // SELECT lines where direwolf had a bad-source-address error and
        // we haven't yet rewritten them.
        string sql = """
            SELECT l.id, l.raw_line
            FROM lines l
            JOIN direwolf_decoded d ON d.line_id = l.id
            LEFT JOIN direwolf_decoded_rewrite r ON r.line_id = l.id
            WHERE d.has_error = 1
              AND d.error_first LIKE '%Bad source address%'
              AND r.line_id IS NULL
            ORDER BY l.id
            """;
        if (opts.Limit > 0)
        {
            sql += $" LIMIT {opts.Limit}";
        }

        var batch = new List<(long id, string original, string rewritten)>(opts.BatchSize);
        long total = 0;
        long rewriteFailed = 0;
        var sw = Stopwatch.StartNew();

        using (var reader = conn.CreateCommand())
        {
            reader.CommandText = sql;
            using var rdr = await reader.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                long id = rdr.GetInt64(0);
                string raw = rdr.GetString(1);

                string? rewritten = Tnc2Parser.TryRewriteForAx25(raw);
                if (rewritten is null)
                {
                    rewriteFailed++;
                    continue;
                }
                batch.Add((id, raw, rewritten));
                if (batch.Count >= opts.BatchSize)
                {
                    await FlushBatchAsync(batch, conn, opts);
                    total += batch.Count;
                    batch.Clear();
                    Console.Error.WriteLine($"# {total} processed ({total / sw.Elapsed.TotalSeconds:F0}/sec), {rewriteFailed} unparseable");
                }
            }
        }

        if (batch.Count > 0)
        {
            await FlushBatchAsync(batch, conn, opts);
            total += batch.Count;
        }

        await conn.CloseAsync();
        Console.Error.WriteLine($"# {dbPath}: {total} rewrites in {sw.Elapsed.TotalSeconds:F1}s ({rewriteFailed} unparseable)");
    }

    private static async Task FlushBatchAsync(
        List<(long id, string original, string rewritten)> batch,
        SqliteConnection conn,
        Options opts)
    {
        var rewrittenLines = batch.Select(x => x.rewritten).ToList();
        var rows = await DirewolfPipeline.DecodeLinesAsync(rewrittenLines, opts.DirewolfBin);

        using var tx = (SqliteTransaction)await conn.BeginTransactionAsync();
        using var insertCmd = conn.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT OR REPLACE INTO direwolf_decoded_rewrite
              (line_id, rewritten, decoded_type, latitude, longitude, altitude_m, comment, has_error, error_first, raw_output)
            VALUES
              ($id, $rewritten, $type, $lat, $lon, $alt, $comment, $err, $errf, $raw);
            """;
        insertCmd.Parameters.Add("$id", SqliteType.Integer);
        insertCmd.Parameters.Add("$rewritten", SqliteType.Text);
        insertCmd.Parameters.Add("$type", SqliteType.Text);
        insertCmd.Parameters.Add("$lat", SqliteType.Real);
        insertCmd.Parameters.Add("$lon", SqliteType.Real);
        insertCmd.Parameters.Add("$alt", SqliteType.Real);
        insertCmd.Parameters.Add("$comment", SqliteType.Text);
        insertCmd.Parameters.Add("$err", SqliteType.Integer);
        insertCmd.Parameters.Add("$errf", SqliteType.Text);
        insertCmd.Parameters.Add("$raw", SqliteType.Text);

        for (int i = 0; i < Math.Min(batch.Count, rows.Count); i++)
        {
            var (id, _, rewritten) = batch[i];
            var row = rows[i];
            insertCmd.Parameters["$id"].Value = id;
            insertCmd.Parameters["$rewritten"].Value = rewritten;
            insertCmd.Parameters["$type"].Value = (object?)row.DecodedType ?? DBNull.Value;
            insertCmd.Parameters["$lat"].Value = row.Latitude.HasValue ? (object)row.Latitude.Value : DBNull.Value;
            insertCmd.Parameters["$lon"].Value = row.Longitude.HasValue ? (object)row.Longitude.Value : DBNull.Value;
            insertCmd.Parameters["$alt"].Value = row.AltitudeM.HasValue ? (object)row.AltitudeM.Value : DBNull.Value;
            insertCmd.Parameters["$comment"].Value = (object?)row.Comment ?? DBNull.Value;
            insertCmd.Parameters["$err"].Value = row.HasError ? 1 : 0;
            insertCmd.Parameters["$errf"].Value = (object?)row.FirstError ?? DBNull.Value;
            insertCmd.Parameters["$raw"].Value = row.RawOutput;
            await insertCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
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
}
