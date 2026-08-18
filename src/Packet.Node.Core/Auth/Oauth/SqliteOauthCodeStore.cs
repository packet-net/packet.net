using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// SQLite-backed <see cref="IOauthCodeStore"/> on <c>pdn.db</c>. The single-use guarantee is
/// the atomic <c>DELETE … RETURNING</c> in <see cref="Consume"/> - SQLite serialises it, so
/// two concurrent redemptions of the same code cannot both succeed. Same resilient discipline
/// as the sibling stores (a fault logs + degrades, never throws into the auth path).
/// </summary>
public sealed partial class SqliteOauthCodeStore : IOauthCodeStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS oauth_code (
            code           TEXT PRIMARY KEY,
            client_id      TEXT NOT NULL,
            redirect_uri   TEXT NOT NULL,
            code_challenge TEXT NOT NULL,
            scope          TEXT NOT NULL,
            resource       TEXT NOT NULL,
            username       TEXT NOT NULL,
            expires_utc    TEXT NOT NULL);
        """;

    private const string Columns =
        "code AS Code, client_id AS ClientId, redirect_uri AS RedirectUri, code_challenge AS CodeChallenge, " +
        "scope AS Scope, resource AS Resource, username AS Username, expires_utc AS ExpiresUtcRaw";

    private readonly string connectionString;
    private readonly ILogger<SqliteOauthCodeStore> logger;

    public SqliteOauthCodeStore(string dbPath, ILogger<SqliteOauthCodeStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteOauthCodeStore>.Instance;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public void Issue(OauthCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        try
        {
            using var conn = Open();
            conn.Execute(
                """
                INSERT INTO oauth_code (code, client_id, redirect_uri, code_challenge, scope, resource, username, expires_utc)
                VALUES (@Code, @ClientId, @RedirectUri, @CodeChallenge, @Scope, @Resource, @Username, @Expires);
                """,
                new
                {
                    code.Code,
                    code.ClientId,
                    code.RedirectUri,
                    code.CodeChallenge,
                    code.Scope,
                    code.Resource,
                    code.Username,
                    Expires = SqliteStamps.Stamp(code.ExpiresUtc),
                });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
        }
    }

    /// <inheritdoc />
    public OauthCode? Consume(string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        try
        {
            using var conn = Open();
            // Atomic single-use: delete the row and return what it held, in one statement.
            var row = conn.QuerySingleOrDefault<Row>(
                $"DELETE FROM oauth_code WHERE code = @Code RETURNING {Columns};", new { Code = code });
            if (row is null)
            {
                return null;
            }
            var consumed = row.ToCode();
            // Expired codes are consumed-and-rejected (so they can't linger or be retried).
            return consumed.ExpiresUtc <= now ? null : consumed;
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc />
    public int PruneExpired(DateTimeOffset now)
    {
        try
        {
            using var conn = Open();
            // Mirrors SqliteRefreshTokenStore.PruneExpired: a plain lexical range delete, which is
            // only sound because every stamp is written UTC-normalised by SqliteStamps.
            return conn.Execute(
                "DELETE FROM oauth_code WHERE expires_utc < @Now;",
                new { Now = SqliteStamps.Stamp(now) });
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return 0;
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

    private sealed record Row(
        string Code, string ClientId, string RedirectUri, string CodeChallenge,
        string Scope, string Resource, string Username, string ExpiresUtcRaw)
    {
        public OauthCode ToCode() => new(
            Code, ClientId, RedirectUri, CodeChallenge, Scope, Resource, Username,
            SqliteStamps.ParseStamp(ExpiresUtcRaw));
    }

    [LoggerMessage(EventId = 4211, Level = LogLevel.Warning,
        Message = "oauth code store: schema init failed ({Db}); the OAuth authorize flow is unavailable this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4212, Level = LogLevel.Warning,
        Message = "oauth code store: write failed ({Db}).")]
    private partial void LogWriteFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4213, Level = LogLevel.Warning,
        Message = "oauth code store: consume failed ({Db}).")]
    private partial void LogReadFailed(Exception ex, string db);
}
