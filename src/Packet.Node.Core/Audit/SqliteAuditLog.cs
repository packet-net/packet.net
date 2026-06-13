using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Audit;

/// <summary>
/// The SQLite-backed <see cref="IAuditLog"/>: persists privileged-action records to
/// the same consolidated <c>pdn.db</c> the users + NET/ROM routing table live in. Raw
/// SQL via Dapper (no EF — the node host's persistence decision), mirroring
/// <c>SqliteUserStore</c> / <c>SqliteNetRomRoutingStore</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resilient.</b> A schema/open failure logs and leaves the node running (auditing
/// is simply unavailable); <see cref="Record"/> swallows + logs any write fault and
/// never throws, so an action path can never be taken down by the audit sink.
/// <see cref="Recent"/> returns empty on fault. WAL mode, a fresh pooled connection per
/// call — the same discipline as the sibling stores.
/// </para>
/// <para>
/// <b>Bounded.</b> The table is capped at <see cref="RowCap"/> rows: each insert prunes
/// anything older than the newest cap, so the audit log can't grow without limit on a
/// long-lived node. Audit volume is low (operator write actions), so the per-insert
/// prune is cheap.
/// </para>
/// <para>
/// <b>No secrets.</b> Callers pass a summarised/ hashed detail line — the store persists
/// it verbatim and never inspects it. It also emits a structured log line per record so
/// auditing is visible in the node's normal logs as well as the DB.
/// </para>
/// </remarks>
public sealed partial class SqliteAuditLog : IAuditLog
{
    /// <summary>Maximum rows retained; older rows are pruned on insert.</summary>
    public const int RowCap = 20_000;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS audit_log (
            id        INTEGER PRIMARY KEY AUTOINCREMENT,
            ts_utc    TEXT NOT NULL,
            actor     TEXT NOT NULL,
            source    TEXT NOT NULL,
            action    TEXT NOT NULL,
            target    TEXT NOT NULL,
            outcome   TEXT NOT NULL,
            detail    TEXT NOT NULL,
            client_ip TEXT NULL);
        CREATE INDEX IF NOT EXISTS ix_audit_log_id_desc ON audit_log (id DESC);
        """;

    private const string SelectColumns =
        "id AS Id, ts_utc AS TimestampUtcRaw, actor AS Actor, source AS Source, " +
        "action AS Action, target AS Target, outcome AS Outcome, detail AS Detail, client_ip AS ClientIp";

    private readonly string connectionString;
    private readonly ILogger<SqliteAuditLog> logger;
    private readonly int rowCap;

    /// <summary>Open (creating if absent) the audit log at <paramref name="dbPath"/> and
    /// ensure its schema. A schema/open failure is logged, not thrown — the node still
    /// boots, just without persisted auditing. <paramref name="rowCap"/> bounds retained
    /// rows (defaults to <see cref="RowCap"/>; lower values are for tests).</summary>
    public SqliteAuditLog(string dbPath, ILogger<SqliteAuditLog>? logger = null, int rowCap = RowCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowCap, 1);
        this.logger = logger ?? NullLogger<SqliteAuditLog>.Instance;
        this.rowCap = rowCap;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Visible in the node's normal logs regardless of the DB outcome.
        LogAudit(entry.Actor, entry.Source, entry.Action, entry.Target, entry.Outcome, entry.Detail);

        try
        {
            using var conn = Open();
            conn.Execute(
                """
                INSERT INTO audit_log (ts_utc, actor, source, action, target, outcome, detail, client_ip)
                VALUES (@Ts, @Actor, @Source, @Action, @Target, @Outcome, @Detail, @ClientIp);
                """,
                new
                {
                    Ts = entry.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                    entry.Actor,
                    entry.Source,
                    entry.Action,
                    entry.Target,
                    entry.Outcome,
                    entry.Detail,
                    entry.ClientIp,
                });

            // Prune to the cap (cheap at audit volume): drop everything below the newest cap.
            conn.Execute(
                "DELETE FROM audit_log WHERE id <= (SELECT MAX(id) FROM audit_log) - @Cap;",
                new { Cap = rowCap });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEntry> Recent(int limit)
    {
        int n = Math.Clamp(limit, 1, RowCap);
        try
        {
            using var conn = Open();
            var rows = conn.Query<Row>(
                $"SELECT {SelectColumns} FROM audit_log ORDER BY id DESC LIMIT @N;", new { N = n });
            return rows.Select(r => r.ToEntry()).ToList();
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    private void EnsureSchema()
    {
        try
        {
            using var conn = Open();
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute(SchemaSql);
        }
        catch (SqliteException ex)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    // Dapper projection row (ts parsed back to DateTimeOffset off the round-trip "O" string).
    private sealed record Row(
        long Id, string TimestampUtcRaw, string Actor, string Source,
        string Action, string Target, string Outcome, string Detail, string? ClientIp)
    {
        public AuditEntry ToEntry() => new(
            Id,
            DateTimeOffset.Parse(TimestampUtcRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Actor, Source, Action, Target, Outcome, Detail, ClientIp);
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information,
        Message = "audit: actor={Actor} source={Source} action={Action} target={Target} outcome={Outcome} {Detail}")]
    private partial void LogAudit(string actor, string source, string action, string target, string outcome, string detail);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning,
        Message = "audit log: schema init failed ({Db}); auditing is not persisted this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning,
        Message = "audit log: write failed ({Db}); the entry was logged but not persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Warning,
        Message = "audit log: read failed ({Db}); returning no entries.")]
    private partial void LogReadFailed(Exception ex, string db);
}
