using System.Security.Cryptography;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Storage;

namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// SQLite-backed <see cref="IOauthClientStore"/> on the consolidated <c>pdn.db</c>. Raw SQL
/// via Dapper, WAL, a fresh pooled connection per call, resilient on fault - the same
/// discipline as <c>SqliteAuditLog</c> / <c>SqliteUserStore</c>. Redirect URIs are stored as
/// a JSON array in one column (a client has a small fixed set).
/// </summary>
public sealed partial class SqliteOauthClientStore : IOauthClientStore
{
    /// <summary>Maximum registered clients retained; the oldest are pruned on registration.
    /// Registration is open and unauthenticated (RFC 7591, the MCP connector flow), so without
    /// a cap a caller in a loop grows <c>pdn.db</c> without bound (security review M1, #426).
    /// A node has a handful of real clients; a flood past the cap evicts the oldest, and an
    /// evicted real client simply registers again, as the flow already allows.</summary>
    public const int RowCap = 256;

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS oauth_client (
            client_id     TEXT PRIMARY KEY,
            client_name   TEXT NOT NULL,
            redirect_uris TEXT NOT NULL,
            created_utc   TEXT NOT NULL);
        """;

    private readonly string connectionString;
    private readonly ILogger<SqliteOauthClientStore> logger;
    private readonly int rowCap;

    /// <summary>Open (creating if absent) the client store at <paramref name="dbPath"/>.
    /// <paramref name="rowCap"/> bounds retained clients (defaults to <see cref="RowCap"/>;
    /// lower values are for tests).</summary>
    public SqliteOauthClientStore(string dbPath, ILogger<SqliteOauthClientStore>? logger = null, int rowCap = RowCap)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowCap, 1);
        this.logger = logger ?? NullLogger<SqliteOauthClientStore>.Instance;
        this.rowCap = rowCap;
        connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        EnsureSchema();
    }

    /// <inheritdoc />
    public OauthClient? Register(string clientName, IReadOnlyList<string> redirectUris, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(redirectUris);

        // A public client id: 256 bits of CSPRNG, URL-safe. No secret is issued.
        string clientId = "pdn-" + Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var client = new OauthClient(clientId, clientName ?? string.Empty, redirectUris, now);

        try
        {
            using var conn = Open();
            conn.Execute(
                """
                INSERT INTO oauth_client (client_id, client_name, redirect_uris, created_utc)
                VALUES (@ClientId, @ClientName, @RedirectUris, @CreatedUtc);
                """,
                new
                {
                    client.ClientId,
                    client.ClientName,
                    RedirectUris = JsonSerializer.Serialize(redirectUris),
                    CreatedUtc = SqliteStamps.Stamp(now),
                });
            // Keep the newest rowCap clients. The stamp sorts lexically as time; the id breaks a
            // same-instant tie so the prune is deterministic.
            conn.Execute(
                """
                DELETE FROM oauth_client WHERE client_id NOT IN (
                    SELECT client_id FROM oauth_client ORDER BY created_utc DESC, client_id DESC LIMIT @Cap);
                """,
                new { Cap = rowCap });
            return client;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc />
    public OauthClient? Find(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }
        try
        {
            using var conn = Open();
            var row = conn.QuerySingleOrDefault<Row>(
                "SELECT client_id AS ClientId, client_name AS ClientName, redirect_uris AS RedirectUrisJson, created_utc AS CreatedUtcRaw FROM oauth_client WHERE client_id = @Id;",
                new { Id = clientId });
            return row?.ToClient();
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OauthClient> List()
    {
        try
        {
            using var conn = Open();
            var rows = conn.Query<Row>(
                "SELECT client_id AS ClientId, client_name AS ClientName, redirect_uris AS RedirectUrisJson, created_utc AS CreatedUtcRaw FROM oauth_client ORDER BY created_utc DESC;");
            return rows.Select(r => r.ToClient()).ToList();
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    /// <inheritdoc />
    public bool Delete(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }
        try
        {
            using var conn = Open();
            return conn.Execute("DELETE FROM oauth_client WHERE client_id = @Id;", new { Id = clientId }) > 0;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
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

    // URL-safe base64 without padding (RFC 4648 §5) - for the client id token.
    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Row(string ClientId, string ClientName, string RedirectUrisJson, string CreatedUtcRaw)
    {
        public OauthClient ToClient() => new(
            ClientId,
            ClientName,
            JsonSerializer.Deserialize<List<string>>(RedirectUrisJson) ?? [],
            SqliteStamps.ParseStamp(CreatedUtcRaw));
    }

    [LoggerMessage(EventId = 4201, Level = LogLevel.Warning,
        Message = "oauth client store: schema init failed ({Db}); OAuth client registration is unavailable this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4202, Level = LogLevel.Warning,
        Message = "oauth client store: write failed ({Db}).")]
    private partial void LogWriteFailed(Exception ex, string db);

    [LoggerMessage(EventId = 4203, Level = LogLevel.Warning,
        Message = "oauth client store: read failed ({Db}); returning none.")]
    private partial void LogReadFailed(Exception ex, string db);
}
