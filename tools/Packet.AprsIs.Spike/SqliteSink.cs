using Microsoft.Data.Sqlite;

namespace Packet.AprsIs.Spike;

/// <summary>
/// Persists captured APRS-IS TNC2 lines to daily-rotated SQLite files.
/// </summary>
/// <remarks>
/// APRS-IS firehose-style traffic can run hundreds of lines per second; we
/// batch inserts in explicit transactions (commit every N lines or every M
/// milliseconds, whichever comes first) to keep WAL writes amortised.
/// Schema mirrors the MQTT collector's shape: one row per line, raw text
/// preserved verbatim, denormalised top-level fields populated on ingest so
/// queries don't have to re-parse the line.
///
/// Not thread-safe - single producer (the consumer task in
/// <see cref="CollectMode"/>) serialises calls.
/// </remarks>
public sealed class SqliteSink : IAsyncDisposable
{
    readonly string _dataDir;
    readonly string _filenamePrefix;
    readonly string _clientId;
    readonly string _filter;
    readonly int _batchSize;
    readonly TimeSpan _batchTimeout;

    SqliteConnection? _conn;
    SqliteCommand? _insertCmd;
    SqliteTransaction? _tx;
    DateTime _txOpenedAt;
    int _txPending;
    string? _currentDateUtc;
    long _runId;
    long _totalLines;

    public SqliteSink(
        string dataDir,
        string filenamePrefix,
        string clientId,
        string filter,
        int batchSize = 100,
        int batchTimeoutMs = 500)
    {
        _dataDir = dataDir;
        _filenamePrefix = filenamePrefix;
        _clientId = clientId;
        _filter = filter;
        _batchSize = batchSize;
        _batchTimeout = TimeSpan.FromMilliseconds(batchTimeoutMs);
        Directory.CreateDirectory(_dataDir);
    }

    public long TotalLines => _totalLines;

    public async Task WriteAsync(CapturedLine line, CancellationToken ct)
    {
        string dateUtc = DateTimeOffset
            .FromUnixTimeMilliseconds(line.TimestampUtcUs / 1000)
            .UtcDateTime
            .ToString("yyyy-MM-dd");

        if (_currentDateUtc != dateUtc)
        {
            await RotateToAsync(dateUtc, ct);
        }

        await EnsureTransactionAsync(ct);

        var cmd = _insertCmd!;
        cmd.Parameters["$ts"].Value = line.TimestampUtcUs;
        cmd.Parameters["$source"].Value = (object?)line.Source ?? DBNull.Value;
        cmd.Parameters["$dest"].Value = (object?)line.Destination ?? DBNull.Value;
        cmd.Parameters["$digi_path"].Value = (object?)line.DigiPath ?? DBNull.Value;
        cmd.Parameters["$digi_count"].Value = line.DigiCount;
        cmd.Parameters["$info_len"].Value = line.Info.Length;
        cmd.Parameters["$info"].Value = line.Info;
        cmd.Parameters["$raw_line"].Value = line.RawLine;
        await cmd.ExecuteNonQueryAsync(ct);

        _totalLines++;
        _txPending++;

        if (_txPending >= _batchSize || (DateTime.UtcNow - _txOpenedAt) >= _batchTimeout)
        {
            await CommitAsync(ct);
        }
    }

    /// <summary>Force a commit (used on shutdown / rotation boundaries / idle).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        if (_tx is not null && _txPending > 0)
        {
            await CommitAsync(ct);
        }
    }

    async Task EnsureTransactionAsync(CancellationToken ct)
    {
        if (_tx is not null)
        {
            return;
        }

        _tx = (SqliteTransaction)await _conn!.BeginTransactionAsync(ct);
        _insertCmd!.Transaction = _tx;
        _txOpenedAt = DateTime.UtcNow;
        _txPending = 0;
    }

    async Task CommitAsync(CancellationToken ct)
    {
        if (_tx is null)
        {
            return;
        }

        await _tx.CommitAsync(ct);
        await _tx.DisposeAsync();
        _tx = null;
        _txPending = 0;
        if (_insertCmd is not null)
        {
            _insertCmd.Transaction = null;
        }
    }

    async Task RotateToAsync(string dateUtc, CancellationToken ct)
    {
        if (_conn is not null)
        {
            try { await CommitAsync(ct); } catch { }
            _insertCmd?.Dispose();
            await _conn.DisposeAsync();
            _conn = null;
            _insertCmd = null;
        }

        var path = Path.Combine(_dataDir, $"{_filenamePrefix}-{dateUtc}.sqlite");
        Console.Error.WriteLine($"# rotating SQLite → {path}");

        var connString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        _conn = new SqliteConnection(connString);
        await _conn.OpenAsync(ct);

        using (var pragma = _conn.CreateCommand())
        {
            pragma.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous  = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                """;
            await pragma.ExecuteNonQueryAsync(ct);
        }

        using (var schema = _conn.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS lines (
                  id           INTEGER PRIMARY KEY,
                  ts_utc_us    INTEGER NOT NULL,
                  source       TEXT,
                  destination  TEXT,
                  digi_path    TEXT,
                  digi_count   INTEGER NOT NULL DEFAULT 0,
                  info_len     INTEGER NOT NULL DEFAULT 0,
                  info         BLOB,
                  raw_line     TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_lines_ts          ON lines(ts_utc_us);
                CREATE INDEX IF NOT EXISTS idx_lines_source      ON lines(source);
                CREATE INDEX IF NOT EXISTS idx_lines_destination ON lines(destination);

                CREATE TABLE IF NOT EXISTS run_meta (
                  run_id          INTEGER PRIMARY KEY AUTOINCREMENT,
                  started_at_us   INTEGER NOT NULL,
                  ended_at_us     INTEGER,
                  client_id       TEXT,
                  filter          TEXT,
                  line_count      INTEGER NOT NULL DEFAULT 0,
                  reconnect_count INTEGER NOT NULL DEFAULT 0
                );
                """;
            await schema.ExecuteNonQueryAsync(ct);
        }

        using (var meta = _conn.CreateCommand())
        {
            meta.CommandText = """
                INSERT INTO run_meta (started_at_us, client_id, filter, line_count)
                VALUES ($now, $client, $filter, 0)
                RETURNING run_id;
                """;
            meta.Parameters.AddWithValue("$now", NowUs());
            meta.Parameters.AddWithValue("$client", _clientId);
            meta.Parameters.AddWithValue("$filter", _filter);
            var runIdObj = await meta.ExecuteScalarAsync(ct);
            _runId = Convert.ToInt64(runIdObj);
        }

        using (var trg = _conn.CreateCommand())
        {
            trg.CommandText = $"""
                DROP TRIGGER IF EXISTS trg_lines_meta;
                CREATE TRIGGER trg_lines_meta
                AFTER INSERT ON lines
                BEGIN
                  UPDATE run_meta
                  SET line_count    = line_count + 1,
                      ended_at_us   = NEW.ts_utc_us
                  WHERE run_id = {_runId};
                END;
                """;
            await trg.ExecuteNonQueryAsync(ct);
        }

        _insertCmd = _conn.CreateCommand();
        _insertCmd.CommandText = """
            INSERT INTO lines
              (ts_utc_us, source, destination, digi_path, digi_count, info_len, info, raw_line)
            VALUES
              ($ts, $source, $dest, $digi_path, $digi_count, $info_len, $info, $raw_line);
            """;
        _insertCmd.Parameters.Add("$ts", SqliteType.Integer);
        _insertCmd.Parameters.Add("$source", SqliteType.Text);
        _insertCmd.Parameters.Add("$dest", SqliteType.Text);
        _insertCmd.Parameters.Add("$digi_path", SqliteType.Text);
        _insertCmd.Parameters.Add("$digi_count", SqliteType.Integer);
        _insertCmd.Parameters.Add("$info_len", SqliteType.Integer);
        _insertCmd.Parameters.Add("$info", SqliteType.Blob);
        _insertCmd.Parameters.Add("$raw_line", SqliteType.Text);

        _currentDateUtc = dateUtc;
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            try { await CommitAsync(CancellationToken.None); } catch { }
            _insertCmd?.Dispose();
            await _conn.DisposeAsync();
            _conn = null;
            _insertCmd = null;
        }
    }

    public static long NowUs() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000
        + (DateTimeOffset.UtcNow.Ticks % TimeSpan.TicksPerMillisecond) / 10;
}

/// <summary>One APRS-IS line captured for persistence.</summary>
public sealed record CapturedLine(
    long TimestampUtcUs,
    string? Source,
    string? Destination,
    string? DigiPath,
    int DigiCount,
    byte[] Info,
    string RawLine);
