using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.Auth;

/// <summary>
/// The SQLite-backed <see cref="IRefreshTokenStore"/>: persists refresh tokens (by
/// hash only) to the same consolidated <c>pdn.db</c> the users + routing table live
/// in. Raw SQL via Dapper, mirroring <see cref="SqliteUserStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema</b> is created with <c>CREATE TABLE IF NOT EXISTS</c> — the same
/// meta-row approach the user store uses, so it doesn't fight the routing store
/// over <c>PRAGMA user_version</c>. One table, <c>refresh_token</c>, keyed by the
/// token hash, with an index on <c>family</c> for the whole-family revoke.
/// </para>
/// <para>
/// <b>Only the hash is stored</b> (never the opaque token), exactly the way the
/// user store never holds a clear password. The token is a 256-bit CSPRNG value
/// the client holds; we keep its SHA-256 and look up by it.
/// </para>
/// <para>
/// <b>Resilient.</b> Every operation is wrapped: a schema/open failure logs and
/// leaves the node running (refresh simply can't be used); a lookup returns null
/// on fault, a write returns false / 0. Persistence can never take the node down.
/// WAL mode, a fresh pooled connection per call — the same discipline as the user
/// store — so the store is safe to share across the request threads.
/// </para>
/// </remarks>
public sealed partial class SqliteRefreshTokenStore : IRefreshTokenStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS refresh_token (
            token_hash  TEXT PRIMARY KEY,
            username    TEXT NOT NULL,
            family      TEXT NOT NULL,
            issued_utc  TEXT NOT NULL,
            expires_utc TEXT NOT NULL,
            revoked     INTEGER NOT NULL DEFAULT 0,
            revoked_utc TEXT);
        CREATE INDEX IF NOT EXISTS ix_refresh_token_family ON refresh_token (family);
        """;

    private readonly string connectionString;
    private readonly ILogger<SqliteRefreshTokenStore> logger;

    /// <summary>Open (creating if absent) the refresh-token store at
    /// <paramref name="dbPath"/> and ensure its schema. A schema/open failure is
    /// logged, not thrown — the node still boots, just without refresh tokens.</summary>
    public SqliteRefreshTokenStore(string dbPath, ILogger<SqliteRefreshTokenStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteRefreshTokenStore>.Instance;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
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
            EnsureRevokedUtcColumn(conn);
        }
        catch (SqliteException ex)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    // Migrate a pre-existing refresh_token table (created before the reuse-leeway
    // column existed) by adding revoked_utc if it's absent. CREATE TABLE IF NOT
    // EXISTS never alters an existing table, so a node upgraded in place needs this.
    private static void EnsureRevokedUtcColumn(SqliteConnection conn)
    {
        var hasColumn = conn.Query<string>("SELECT name FROM pragma_table_info('refresh_token');")
            .Any(name => string.Equals(name, "revoked_utc", StringComparison.OrdinalIgnoreCase));
        if (!hasColumn)
        {
            conn.Execute("ALTER TABLE refresh_token ADD COLUMN revoked_utc TEXT;");
        }
    }

    /// <inheritdoc/>
    public bool Insert(RefreshTokenRecord token)
    {
        ArgumentNullException.ThrowIfNull(token);
        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT INTO refresh_token (token_hash, username, family, issued_utc, expires_utc, revoked, revoked_utc) " +
                "VALUES (@h, @u, @f, @i, @e, @r, @ru);",
                new
                {
                    h = token.TokenHash,
                    u = token.Username,
                    f = token.Family,
                    i = SqliteStamps.Stamp(token.IssuedUtc),
                    e = SqliteStamps.Stamp(token.ExpiresUtc),
                    r = token.Revoked ? 1 : 0,
                    ru = token.RevokedUtc is { } ru ? SqliteStamps.Stamp(ru) : null,
                });
            return true;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc/>
    public RefreshTokenRecord? FindByHash(string tokenHash)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        try
        {
            using var conn = Open();
            var row = conn.QuerySingleOrDefault<TokenRow>(
                "SELECT token_hash AS TokenHash, username AS Username, family AS Family, " +
                "issued_utc AS IssuedUtc, expires_utc AS ExpiresUtc, revoked AS Revoked, " +
                "revoked_utc AS RevokedUtc " +
                "FROM refresh_token WHERE token_hash = @h;",
                new { h = tokenHash });
            return row is null ? null : ToRecord(row);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc/>
    public bool Revoke(string tokenHash, DateTimeOffset? consumedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        try
        {
            using var conn = Open();
            int rows = conn.Execute(
                "UPDATE refresh_token SET revoked = 1, revoked_utc = @ru WHERE token_hash = @h;",
                new { h = tokenHash, ru = consumedAtUtc is { } ru ? SqliteStamps.Stamp(ru) : null });
            return rows > 0;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc/>
    public bool HasLiveToken(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        try
        {
            using var conn = Open();
            return conn.ExecuteScalar<long>(
                "SELECT EXISTS(SELECT 1 FROM refresh_token WHERE family = @f AND revoked = 0);",
                new { f = family }) != 0;
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return false;   // fail safe — deny the leeway grace on a store fault
        }
    }

    /// <inheritdoc/>
    public int RevokeFamily(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        try
        {
            using var conn = Open();
            return conn.Execute(
                "UPDATE refresh_token SET revoked = 1 WHERE family = @f AND revoked = 0;",
                new { f = family });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    /// <inheritdoc/>
    public int RevokeAllForUser(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        try
        {
            using var conn = Open();
            // Hard kill across every family the user owns, exactly like RevokeFamily: no
            // revoked_utc stamp, so none of them is leeway-eligible afterwards.
            return conn.Execute(
                "UPDATE refresh_token SET revoked = 1 WHERE username = @u AND revoked = 0;",
                new { u = username });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    /// <inheritdoc/>
    public int PruneExpired(DateTimeOffset olderThanUtc)
    {
        try
        {
            using var conn = Open();
            return conn.Execute(
                "DELETE FROM refresh_token WHERE expires_utc < @e;",
                new { e = SqliteStamps.Stamp(olderThanUtc) });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
        }
    }

    private static RefreshTokenRecord ToRecord(TokenRow row) => new(
        row.TokenHash,
        row.Username,
        row.Family,
        SqliteStamps.ParseStamp(row.IssuedUtc),
        SqliteStamps.ParseStamp(row.ExpiresUtc),
        row.Revoked != 0,
        string.IsNullOrEmpty(row.RevokedUtc) ? null : SqliteStamps.ParseStamp(row.RevokedUtc));

    // Dapper row DTO (mutable so Dapper's setter mapping binds it).
    private sealed class TokenRow
    {
        public string TokenHash { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Family { get; set; } = string.Empty;
        public string IssuedUtc { get; set; } = string.Empty;
        public string ExpiresUtc { get; set; } = string.Empty;
        public long Revoked { get; set; }
        public string? RevokedUtc { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refresh-token store: could not initialise the schema ({Db}); refresh tokens are unavailable for this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refresh-token store: a read failed ({Db}); treating as absent.")]
    private partial void LogReadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refresh-token store: a write failed ({Db}); the change was not persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);
}
