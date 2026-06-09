using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Auth;

/// <summary>
/// The SQLite-backed <see cref="IWebAuthnCredentialStore"/>: persists enrolled passkey
/// credentials to the same consolidated <c>pdn.db</c> the users + refresh tokens +
/// routing table live in. Raw SQL via Dapper, mirroring <see cref="SqliteUserStore"/>
/// and <see cref="SqliteRefreshTokenStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Schema</b> is created with <c>CREATE TABLE IF NOT EXISTS</c> — the same
/// meta-row approach the other auth stores use, so it doesn't fight the routing store
/// over <c>PRAGMA user_version</c>. One table, <c>webauthn_credential</c>, keyed by the
/// raw credential id (a BLOB), with an index on <c>username</c> for the by-user lookup.
/// </para>
/// <para>
/// <b>Resilient.</b> Every operation is wrapped: a schema/open failure logs and leaves
/// the node running (passkeys simply can't be used); a lookup returns null/empty on
/// fault, a write returns false. Persistence can never take the node down. WAL mode, a
/// fresh pooled connection per call — the same discipline as the sibling stores — so it
/// is safe to share across the request threads.
/// </para>
/// <para>
/// <b><see cref="WebAuthnCredentialRecord.SignCount"/> is stored as a SQLite INTEGER</b>
/// (a signed 64-bit value) but is a WebAuthn unsigned 32-bit counter; the round-trip
/// casts through <see cref="long"/> losslessly (a uint always fits in a signed long).
/// </para>
/// </remarks>
public sealed partial class SqliteWebAuthnCredentialStore : IWebAuthnCredentialStore
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS webauthn_credential (
            credential_id BLOB PRIMARY KEY,
            username      TEXT NOT NULL,
            public_key    BLOB NOT NULL,
            sign_count    INTEGER NOT NULL,
            cred_type     TEXT,
            transports    TEXT,
            aaguid        BLOB,
            created_utc   TEXT NOT NULL,
            last_used_utc TEXT);
        CREATE INDEX IF NOT EXISTS ix_webauthn_credential_username ON webauthn_credential (username);
        """;

    private readonly string connectionString;
    private readonly ILogger<SqliteWebAuthnCredentialStore> logger;

    /// <summary>Open (creating if absent) the credential store at <paramref name="dbPath"/>
    /// and ensure its schema. A schema/open failure is logged, not thrown — the node
    /// still boots, just without passkeys.</summary>
    public SqliteWebAuthnCredentialStore(string dbPath, ILogger<SqliteWebAuthnCredentialStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.logger = logger ?? NullLogger<SqliteWebAuthnCredentialStore>.Instance;
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
        }
        catch (SqliteException ex)
        {
            LogSchemaFailed(ex, connectionString);
        }
    }

    /// <inheritdoc/>
    public bool Add(WebAuthnCredentialRecord credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT INTO webauthn_credential " +
                "(credential_id, username, public_key, sign_count, cred_type, transports, aaguid, created_utc, last_used_utc) " +
                "VALUES (@id, @u, @pk, @sc, @ct, @tr, @ag, @c, @l);",
                new
                {
                    id = credential.CredentialId,
                    u = credential.Username,
                    pk = credential.PublicKey,
                    sc = (long)credential.SignCount,
                    ct = credential.CredType,
                    tr = credential.Transports,
                    ag = credential.AaGuid,
                    c = Stamp(credential.CreatedUtc),
                    l = credential.LastUsedUtc is { } when ? Stamp(when) : null,
                });
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // SQLITE_CONSTRAINT (19) — the credential id already exists. A normal
            // "already enrolled" outcome, not a fault: return false without log noise.
            return false;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<WebAuthnCredentialRecord> GetByUser(string username)
    {
        ArgumentNullException.ThrowIfNull(username);
        try
        {
            using var conn = Open();
            var rows = conn.Query<CredentialRow>(
                "SELECT credential_id AS CredentialId, username AS Username, public_key AS PublicKey, " +
                "sign_count AS SignCount, cred_type AS CredType, transports AS Transports, aaguid AS AaGuid, " +
                "created_utc AS CreatedUtc, last_used_utc AS LastUsedUtc " +
                "FROM webauthn_credential WHERE username = @u ORDER BY created_utc;",
                new { u = username }).ToList();
            return Project(rows);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    /// <inheritdoc/>
    public WebAuthnCredentialRecord? GetByCredentialId(byte[] credentialId)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        try
        {
            using var conn = Open();
            var row = conn.QuerySingleOrDefault<CredentialRow>(
                "SELECT credential_id AS CredentialId, username AS Username, public_key AS PublicKey, " +
                "sign_count AS SignCount, cred_type AS CredType, transports AS Transports, aaguid AS AaGuid, " +
                "created_utc AS CreatedUtc, last_used_utc AS LastUsedUtc " +
                "FROM webauthn_credential WHERE credential_id = @id;",
                new { id = credentialId });
            return row is null ? null : ToRecord(row);
        }
        catch (Exception ex) when (ex is SqliteException or FormatException)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<byte[]> GetAllCredentialIds()
    {
        try
        {
            using var conn = Open();
            // Dapper maps a single BLOB column to byte[] directly.
            return conn.Query<byte[]>("SELECT credential_id FROM webauthn_credential;").ToList();
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return [];
        }
    }

    /// <inheritdoc/>
    public void UpdateSignCount(byte[] credentialId, uint newCount, DateTimeOffset whenUtc)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        try
        {
            using var conn = Open();
            conn.Execute(
                "UPDATE webauthn_credential SET sign_count = @sc, last_used_utc = @l WHERE credential_id = @id;",
                new { id = credentialId, sc = (long)newCount, l = Stamp(whenUtc) });
        }
        catch (SqliteException ex)
        {
            // Best-effort: a failed counter/last-used stamp must never fail the assertion
            // itself (the security-relevant counter check ran in the verify path already).
            LogWriteFailed(ex, connectionString);
        }
    }

    /// <inheritdoc/>
    public bool Delete(byte[] credentialId, string username)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(username);
        try
        {
            using var conn = Open();
            // The username predicate is the ownership guard: a caller can only delete
            // their OWN credential, never someone else's by id.
            int rows = conn.Execute(
                "DELETE FROM webauthn_credential WHERE credential_id = @id AND username = @u;",
                new { id = credentialId, u = username });
            return rows > 0;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    private static List<WebAuthnCredentialRecord> Project(List<CredentialRow> rows)
    {
        var list = new List<WebAuthnCredentialRecord>(rows.Count);
        foreach (var row in rows)
        {
            list.Add(ToRecord(row));
        }
        return list;
    }

    private static WebAuthnCredentialRecord ToRecord(CredentialRow row) => new(
        row.CredentialId,
        row.Username,
        row.PublicKey,
        unchecked((uint)row.SignCount),
        row.CredType,
        row.Transports,
        row.AaGuid,
        ParseStamp(row.CreatedUtc),
        string.IsNullOrEmpty(row.LastUsedUtc) ? null : ParseStamp(row.LastUsedUtc));

    private static string Stamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // Dapper row DTO (mutable so Dapper's setter mapping binds it).
    private sealed class CredentialRow
    {
        public byte[] CredentialId { get; set; } = [];
        public string Username { get; set; } = string.Empty;
        public byte[] PublicKey { get; set; } = [];
        public long SignCount { get; set; }
        public string? CredType { get; set; }
        public string? Transports { get; set; }
        public byte[]? AaGuid { get; set; }
        public string CreatedUtc { get; set; } = string.Empty;
        public string? LastUsedUtc { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WebAuthn credential store: could not initialise the schema ({Db}); passkeys are unavailable for this run.")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WebAuthn credential store: a read failed ({Db}); treating as empty/absent.")]
    private partial void LogReadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "WebAuthn credential store: a write failed ({Db}); the change was not persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);
}
