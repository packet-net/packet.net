using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The SQLite-backed <see cref="ISqliteConfigStore"/>: the node's live config persisted
/// as a SINGLE versioned JSON-blob row in the consolidated <c>pdn.db</c> the routing /
/// users / heard / capability stores share. Raw SQL via Dapper, mirroring
/// <see cref="Heard.SqliteHeardStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Single blob, not shredded.</b> <see cref="NodeConfig"/> is a deep, polymorphic,
/// frequently-evolving record tree (a kind-discriminated transport union, many additive
/// sub-records). Shredding it into structured tables would duplicate the whole shape in
/// DDL, demand a migration on every field add, and re-implement the polymorphic union in
/// SQL. A single JSON blob round-trips the EXACT model the provider produces — provably
/// zero behaviour change. The blob is the canonical management-API JSON
/// (<see cref="NodeConfigJson"/>), so the structured <c>PUT /config</c> body and the
/// persisted bytes are identical.
/// </para>
/// <para>
/// <b>Schema</b> is created with <c>CREATE TABLE IF NOT EXISTS</c> — the meta-less
/// pattern the heard / capability stores use, so it does NOT fight the routing store over
/// <c>PRAGMA user_version</c>. One table, <c>node_config</c>, a singleton row pinned by a
/// <c>CHECK (id = 1)</c>. Writes are an upsert (<c>ON CONFLICT(id) DO UPDATE</c>).
/// </para>
/// <para>
/// <b>Resilient.</b> WAL mode, a fresh pooled connection per call, every op wrapped: a
/// schema/open failure logs and leaves the node running (the provider boots on its
/// in-memory current / seed); a read returns <c>null</c> on fault, a write returns
/// <c>false</c>. Persistence can never take the node down — the same discipline as every
/// other store.
/// </para>
/// </remarks>
public sealed partial class SqliteConfigStore : ISqliteConfigStore
{
    /// <summary>The persisted blob format discriminator. Today only <c>json</c>; carried
    /// in the row so a future format switch is a value, not a schema change.</summary>
    public const string JsonFormat = "json";

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS node_config (
            id          INTEGER PRIMARY KEY CHECK (id = 1),
            schema_ver  INTEGER NOT NULL,
            format      TEXT NOT NULL,
            payload     TEXT NOT NULL,
            updated_utc TEXT NOT NULL);
        """;

    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly ILogger<SqliteConfigStore> logger;

    // The schema-migration policy: the version the loader targets and the registry of
    // vN→vN+1 transforms. Production pins these to the running code's single source of truth
    // (NodeConfig.CurrentSchemaVersion + NodeConfigSchemaMigrations.Registry). The internal
    // ctor lets tests drive the load-time migrate-and-log path with a synthetic current
    // (e.g. v2) + a synthetic registry, because CurrentSchemaVersion is 1 with no real
    // migration registered yet — so the seam is proven, not stubbed.
    private readonly int targetSchemaVersion;
    private readonly IReadOnlyDictionary<int, NodeConfigSchemaMigrations.Migration> migrations;

    /// <summary>Open (creating if absent) the config store at <paramref name="dbPath"/>
    /// and ensure its schema. A schema/open failure is logged, not thrown — the node
    /// still boots, the provider just runs on its seed/in-memory config for the run.</summary>
    public SqliteConfigStore(string dbPath, TimeProvider? clock = null, ILogger<SqliteConfigStore>? logger = null)
        : this(dbPath, NodeConfig.CurrentSchemaVersion, NodeConfigSchemaMigrations.Registry, clock, logger)
    {
    }

    /// <summary>Test seam: construct with an explicit migration target + registry so the
    /// load-time <c>schema_ver &lt; current</c> migrate-and-log path can be exercised while
    /// the running code's <see cref="NodeConfig.CurrentSchemaVersion"/> is still 1.</summary>
    internal SqliteConfigStore(
        string dbPath,
        int targetSchemaVersion,
        IReadOnlyDictionary<int, NodeConfigSchemaMigrations.Migration> migrations,
        TimeProvider? clock = null,
        ILogger<SqliteConfigStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<SqliteConfigStore>.Instance;
        this.targetSchemaVersion = targetSchemaVersion;
        this.migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
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
    public (NodeConfig Config, int SchemaVer)? Load()
    {
        ConfigRow? row;
        try
        {
            using var conn = Open();
            row = conn.QuerySingleOrDefault<ConfigRow>(
                "SELECT schema_ver AS SchemaVer, format AS Format, payload AS Payload FROM node_config WHERE id = 1;");
        }
        catch (SqliteException ex)
        {
            LogReadFailed(ex, connectionString);
            return null;
        }

        if (row is null)
        {
            return null;   // absent row — the provider's first-boot migration/seed path
        }

        if (!string.Equals(row.Format, JsonFormat, StringComparison.Ordinal))
        {
            // An unknown blob format (no second format exists yet, but be honest about it)
            // — treat as unusable so the provider re-seeds rather than crashing.
            LogUnknownFormat(row.Format, connectionString);
            return null;
        }

        var storedVer = (int)row.SchemaVer;

        try
        {
            if (storedVer == targetSchemaVersion)
            {
                // Common path: the blob is already at the running schema — deserialise as-is.
                return (NodeConfigJson.Deserialize(row.Payload), storedVer);
            }

            // The blob predates (or postdates) the running schema. Transform the raw JSON to
            // the current shape FIRST — so a renamed/restructured field is handled without the
            // old typed model — then deserialise through the current type. A GREATER (future)
            // schema makes Migrate throw NodeConfigSchemaException: the boot-fails-on-unknown-
            // config fail-safe, which propagates (it is NOT a degrade-to-reseed).
            var migrated = NodeConfigSchemaMigrations.Migrate(
                NodeConfigJson.ParseObject(row.Payload), storedVer, targetSchemaVersion, migrations);
            var config = NodeConfigJson.Deserialize(migrated);
            LogMigratedSchema(storedVer, targetSchemaVersion, connectionString);
            // Report the CURRENT version: the loaded config has been brought forward, and the
            // provider persists it at current on the next write (lazy upgrade of the stored row).
            return (config, targetSchemaVersion);
        }
        catch (JsonException ex)
        {
            // A corrupt/unreadable blob: degrade like every other store fault. The
            // provider re-seeds (and a subsequent valid write overwrites the bad row).
            LogDeserializeFailed(ex, connectionString);
            return null;
        }
    }

    /// <inheritdoc/>
    public bool Save(NodeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Belt-and-braces with the validator's 1..CurrentSchemaVersion bound: the store
        // stamps the version IT targets, never the caller's. A row is only ever written by
        // this build, so it is by construction readable by this build; a stale or typo'd
        // caller stamp can no longer be persisted and brick the next boot. The payload is
        // normalised to the same number so the blob and the schema_ver column agree.
        var stamped = config.SchemaVersion == targetSchemaVersion
            ? config
            : config with { SchemaVersion = targetSchemaVersion };

        string payload;
        try
        {
            payload = NodeConfigJson.Serialize(stamped);
        }
        catch (JsonException ex)
        {
            // Serialising a valid NodeConfig should never fail; if it somehow does, don't
            // persist a half-blob — surface it as a failed write (Current won't advance).
            LogSerializeFailed(ex, connectionString);
            return false;
        }

        try
        {
            using var conn = Open();
            conn.Execute(
                "INSERT INTO node_config (id, schema_ver, format, payload, updated_utc) " +
                "VALUES (1, @ver, @fmt, @payload, @updated) " +
                "ON CONFLICT(id) DO UPDATE SET " +
                "schema_ver = @ver, format = @fmt, payload = @payload, updated_utc = @updated;",
                new
                {
                    ver = targetSchemaVersion,
                    fmt = JsonFormat,
                    payload,
                    updated = Stamp(clock.GetUtcNow()),
                });
            return true;
        }
        catch (SqliteException ex)
        {
            LogWriteFailed(ex, connectionString);
            return false;
        }
    }

    private static string Stamp(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    // Dapper row DTO (mutable so Dapper's setter mapping binds it).
    private sealed class ConfigRow
    {
        public long SchemaVer { get; set; }
        public string Format { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Config store: could not initialise the schema ({Db}); the node runs on its in-memory config this run (no config persistence).")]
    private partial void LogSchemaFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Config store: a read failed ({Db}); treating as no persisted config.")]
    private partial void LogReadFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Config store: the persisted blob has an unknown format '{Format}' ({Db}); treating as no persisted config.")]
    private partial void LogUnknownFormat(string format, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Config store: the persisted blob did not deserialise ({Db}); treating as no persisted config.")]
    private partial void LogDeserializeFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Config store: migrated the persisted config schema v{FromVer}->v{ToVer} ({Db}); the upgraded shape persists on the next config write.")]
    private partial void LogMigratedSchema(int fromVer, int toVer, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Config store: a write failed ({Db}); the config change was NOT persisted.")]
    private partial void LogWriteFailed(Exception ex, string db);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Config store: the config could not be serialised ({Db}); the change was NOT persisted.")]
    private partial void LogSerializeFailed(Exception ex, string db);
}
