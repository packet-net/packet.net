using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// SQLite-backed <see cref="IOauthGrantStore"/> on the consolidated <c>pdn.db</c>: raw SQL via
/// Dapper, WAL, a fresh pooled connection per call, resilient on fault, the discipline of the
/// sibling stores. A row per refresh-token family; the family's tokens are the refresh store's.
/// </summary>
public sealed partial class SqliteOauthGrantStore : IOauthGrantStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS oauth_grant (
            family      TEXT PRIMARY KEY,
            client_id   TEXT NOT NULL,
            username    TEXT NOT NULL,
            scope       TEXT NOT NULL,
            created_utc TEXT NOT NULL);
        """;

    private const string SelectSql =
        "SELECT family AS Family, client_id AS ClientId, username AS Username, scope AS Scope, created_utc AS CreatedUtcRaw FROM oauth_grant";

    private readonly string connectionString;
    private readonly ILogger<SqliteOauthGrantStore> logger;

    public SqliteOauthGrantStore(string dbPath, ILogger<SqliteOauthGrantStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteOauthGrantStore>.Instance;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public bool Add(OauthGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT INTO oauth_grant (family, client_id, username, scope, created_utc) VALUES (@Family, @ClientId, @Username, @Scope, @CreatedUtc);",
                new { grant.Family, grant.ClientId, grant.Username, grant.Scope, CreatedUtc = SqliteStamps.Stamp(grant.CreatedUtc) });
            return true;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc />
    public OauthGrant? FindByFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return null;
        }
        try
        {
            using var conn = Open();
            return conn.QuerySingleOrDefault<Row>(SelectSql + " WHERE family = @Family;", new { Family = family })?.ToGrant();
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc />
    public bool Delete(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }
        try
        {
            using var conn = Open();
            return conn.Execute("DELETE FROM oauth_grant WHERE family = @Family;", new { Family = family }) > 0;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OauthGrant> ListByUser(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return [];
        }
        try
        {
            using var conn = Open();
            return conn.Query<Row>(SelectSql + " WHERE username = @Username ORDER BY created_utc DESC;", new { Username = username })
                .Select(r => r.ToGrant()).ToList();
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

    private sealed record Row(string Family, string ClientId, string Username, string Scope, string CreatedUtcRaw)
    {
        public OauthGrant ToGrant() => new(Family, ClientId, Username, Scope, SqliteStamps.ParseStamp(CreatedUtcRaw));
    }

    [LoggerMessage(EventId = 4221, Level = LogLevel.Warning,
        Message = "oauth grant store: schema init failed ({Db}); OAuth refresh tokens are unavailable this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4222, Level = LogLevel.Warning,
        Message = "oauth grant store: write failed ({Db}).")]
    private partial void LogWriteFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4223, Level = LogLevel.Warning,
        Message = "oauth grant store: read failed ({Db}); returning none.")]
    private partial void LogReadFailed(Exception ex, string db);
}
