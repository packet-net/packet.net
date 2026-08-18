using Microsoft.Data.Sqlite;

namespace Packet.Mqtt.Spike;

/// <summary>
/// Writes captured MQTT messages to daily-rotated SQLite files. One file per
/// UTC day; rotation happens lazily when the first insert past midnight fires.
/// </summary>
/// <remarks>
/// Schema is intentionally simple: store the raw payload as a BLOB plus the
/// denormalised topic components (format / node / direction / port) so
/// queries don't have to re-parse the topic string. Parsing the AX.25 frame
/// itself happens offline - we don't want collector behaviour to depend on
/// parser changes.
///
/// Not thread-safe on its own. Use a single producer (the consumer task in
/// <see cref="CollectMode"/>) to serialise inserts.
/// </remarks>
public sealed class SqliteSink : IAsyncDisposable
{
    readonly string _dataDir;
    readonly string _filenamePrefix;
    readonly string _clientId;

    SqliteConnection? _conn;
    SqliteCommand? _insertCmd;
    string? _currentDateUtc;
    long _runId;
    long _messagesInRun;
    long _totalMessages;

    public SqliteSink(string dataDir, string filenamePrefix, string clientId)
    {
        _dataDir = dataDir;
        _filenamePrefix = filenamePrefix;
        _clientId = clientId;
        Directory.CreateDirectory(_dataDir);
    }

    public long TotalMessages => _totalMessages;

    /// <summary>
    /// Insert one captured message. Opens or rotates the day's SQLite file
    /// as needed. Returns the day's file path so the caller can log it.
    /// </summary>
    public async Task WriteAsync(CapturedMessage msg, CancellationToken ct)
    {
        string dateUtc = DateTimeOffset
            .FromUnixTimeMilliseconds(msg.TimestampUtcUs / 1000)
            .UtcDateTime
            .ToString("yyyy-MM-dd");

        if (_currentDateUtc != dateUtc)
        {
            await RotateToAsync(dateUtc, ct);
        }

        var cmd = _insertCmd!;
        cmd.Parameters["$ts"].Value = msg.TimestampUtcUs;
        cmd.Parameters["$topic"].Value = msg.Topic;
        cmd.Parameters["$format"].Value = (object?)msg.Format ?? DBNull.Value;
        cmd.Parameters["$node"].Value = (object?)msg.Node ?? DBNull.Value;
        cmd.Parameters["$direction"].Value = (object?)msg.Direction ?? DBNull.Value;
        cmd.Parameters["$port"].Value = msg.Port.HasValue ? (object)msg.Port.Value : DBNull.Value;
        cmd.Parameters["$payload"].Value = msg.Payload;
        cmd.Parameters["$payload_len"].Value = msg.Payload.Length;
        await cmd.ExecuteNonQueryAsync(ct);

        _messagesInRun++;
        _totalMessages++;
    }

    /// <summary>Update the current run's running message count + last-seen timestamp.</summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        if (_conn is null)
        {
            return;
        }

        using var cmd = _conn.CreateCommand();
        cmd.CommandText =
            "UPDATE run_meta SET ended_at_us = $now, message_count = $n WHERE run_id = $id";
        cmd.Parameters.AddWithValue("$now", NowUs());
        cmd.Parameters.AddWithValue("$n", _messagesInRun);
        cmd.Parameters.AddWithValue("$id", _runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    async Task RotateToAsync(string dateUtc, CancellationToken ct)
    {
        // Finalize the previous run (if any).
        if (_conn is not null)
        {
            await HeartbeatAsync(ct);
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

        // Pragmas first - WAL is the big win on append-heavy workloads, busy
        // timeout cushions accidental concurrent reads.
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

        // Schema (idempotent - rotating into an existing file just adds rows).
        using (var schema = _conn.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE IF NOT EXISTS messages (
                  id           INTEGER PRIMARY KEY,
                  ts_utc_us    INTEGER NOT NULL,
                  topic        TEXT    NOT NULL,
                  format       TEXT,
                  node         TEXT,
                  direction    TEXT,
                  port         INTEGER,
                  payload      BLOB    NOT NULL,
                  payload_len  INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_messages_ts        ON messages(ts_utc_us);
                CREATE INDEX IF NOT EXISTS idx_messages_topic     ON messages(topic);
                CREATE INDEX IF NOT EXISTS idx_messages_node_port ON messages(node, port);
                CREATE INDEX IF NOT EXISTS idx_messages_format    ON messages(format);

                CREATE TABLE IF NOT EXISTS run_meta (
                  run_id          INTEGER PRIMARY KEY AUTOINCREMENT,
                  started_at_us   INTEGER NOT NULL,
                  ended_at_us     INTEGER,
                  client_id       TEXT,
                  message_count   INTEGER NOT NULL DEFAULT 0,
                  reconnect_count INTEGER NOT NULL DEFAULT 0
                );
                """;
            await schema.ExecuteNonQueryAsync(ct);
        }

        // Start a new run row.
        using (var meta = _conn.CreateCommand())
        {
            meta.CommandText = """
                INSERT INTO run_meta (started_at_us, client_id, message_count)
                VALUES ($now, $client, 0)
                RETURNING run_id;
                """;
            meta.Parameters.AddWithValue("$now", NowUs());
            meta.Parameters.AddWithValue("$client", _clientId);
            var runIdObj = await meta.ExecuteScalarAsync(ct);
            _runId = Convert.ToInt64(runIdObj);
            _messagesInRun = 0;
        }

        // Trigger keeps run_meta's message_count + last-seen ts in sync on every
        // insert without an extra SQL round-trip from the consumer. Cleaner than
        // a periodic heartbeat and exact rather than approximate.
        using (var trg = _conn.CreateCommand())
        {
            trg.CommandText = $"""
                DROP TRIGGER IF EXISTS trg_messages_meta;
                CREATE TRIGGER trg_messages_meta
                AFTER INSERT ON messages
                BEGIN
                  UPDATE run_meta
                  SET message_count = message_count + 1,
                      ended_at_us   = NEW.ts_utc_us
                  WHERE run_id = {_runId};
                END;
                """;
            await trg.ExecuteNonQueryAsync(ct);
        }

        // Prepare the per-message insert once per file.
        _insertCmd = _conn.CreateCommand();
        _insertCmd.CommandText = """
            INSERT INTO messages
              (ts_utc_us, topic, format, node, direction, port, payload, payload_len)
            VALUES
              ($ts, $topic, $format, $node, $direction, $port, $payload, $payload_len);
            """;
        _insertCmd.Parameters.Add("$ts", SqliteType.Integer);
        _insertCmd.Parameters.Add("$topic", SqliteType.Text);
        _insertCmd.Parameters.Add("$format", SqliteType.Text);
        _insertCmd.Parameters.Add("$node", SqliteType.Text);
        _insertCmd.Parameters.Add("$direction", SqliteType.Text);
        _insertCmd.Parameters.Add("$port", SqliteType.Integer);
        _insertCmd.Parameters.Add("$payload", SqliteType.Blob);
        _insertCmd.Parameters.Add("$payload_len", SqliteType.Integer);

        _currentDateUtc = dateUtc;
    }

    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            try { await HeartbeatAsync(CancellationToken.None); } catch { /* best-effort */ }
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

/// <summary>
/// One MQTT message captured for persistence. Topic-parsing happens up-front
/// in the consumer so the schema's denormalised columns are populated.
/// </summary>
public sealed record CapturedMessage(
    long TimestampUtcUs,
    string Topic,
    string? Format,
    string? Node,
    string? Direction,
    int? Port,
    byte[] Payload);
