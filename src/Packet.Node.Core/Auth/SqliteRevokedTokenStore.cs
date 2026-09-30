using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.Auth;

/// <summary>
/// SQLite-backed <see cref="IRevokedTokenStore"/> on the consolidated <c>pdn.db</c>, with
/// the live set mirrored in memory: the check runs on every authenticated request, so it
/// must not cost a database round trip, and the set is small (a token is listed only
/// until it expires). Loaded once at construction, written through on revoke, resilient
/// on fault like the sibling stores. A write fault still revokes for the life of this
/// process and is logged; a schema fault at startup leaves the list empty, which is the
/// posture the node had before per-token revocation existed.
/// </summary>
public sealed partial class SqliteRevokedTokenStore : IRevokedTokenStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS revoked_token (
            jti         TEXT PRIMARY KEY,
            expires_utc TEXT NOT NULL,
            revoked_utc TEXT NOT NULL);
        """;

    private readonly string connectionString;
    private readonly ILogger<SqliteRevokedTokenStore> logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> live = new(StringComparer.Ordinal);

    public SqliteRevokedTokenStore(string dbPath, ILogger<SqliteRevokedTokenStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteRevokedTokenStore>.Instance;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Load();
    }

    /// <summary>How many ids are currently listed.</summary>
    public int Count => live.Count;

    /// <inheritdoc />
    public bool IsRevoked(string jti) => !string.IsNullOrEmpty(jti) && live.ContainsKey(jti);

    /// <inheritdoc />
    public bool Revoke(string jti, DateTimeOffset expiresUtc, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);
        live[jti] = expiresUtc;
        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT OR IGNORE INTO revoked_token (jti, expires_utc, revoked_utc) VALUES (@Jti, @Expires, @Revoked);",
                new { Jti = jti, Expires = SqliteStamps.Stamp(expiresUtc), Revoked = SqliteStamps.Stamp(now) });
            return true;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc />
    public int PruneExpired(DateTimeOffset now)
    {
        int removed = 0;
        foreach (var (jti, expires) in live)
        {
            if (expires <= now && live.TryRemove(jti, out _))
            {
                removed++;
            }
        }
        try
        {
            using var conn = Open();
            conn.Execute("DELETE FROM revoked_token WHERE expires_utc <= @Now;", new { Now = SqliteStamps.Stamp(now) });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
        }
        return removed;
    }

    private void Load()
    {
        try
        {
            using var conn = Open();
            conn.Execute("PRAGMA journal_mode=WAL;");
            conn.Execute(SchemaSql);
            foreach (var row in conn.Query<(string Jti, string ExpiresUtc)>("SELECT jti, expires_utc FROM revoked_token;"))
            {
                live[row.Jti] = SqliteStamps.ParseStamp(row.ExpiresUtc);
            }
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        return conn;
    }

    [LoggerMessage(EventId = 4211, Level = LogLevel.Warning,
        Message = "revoked token store: schema init or load failed ({Db}); revocations recorded before this start are not enforced this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4212, Level = LogLevel.Warning,
        Message = "revoked token store: write failed ({Db}); the revocation holds until the node restarts.")]
    private partial void LogWriteFailed(Exception ex, string db);
}
