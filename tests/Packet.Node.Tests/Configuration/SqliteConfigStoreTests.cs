using System.Text.Json.Nodes;
using FsCheck.Xunit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;
using Packet.Node.Tests.Support;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Tests for <see cref="SqliteConfigStore"/> — the JSON-blob singleton row in pdn.db.
/// The marquee is the full-tree round-trip property: save → load returns a
/// <see cref="NodeConfig"/> EQUAL to the input across the whole tree, incl. the
/// polymorphic transport union and the sequence-equality collection records
/// (WebAuthn.AllowedOrigins, Tailscale.Tags). A reference-equality regression in any
/// list/dict member would make a reconcile diff see spurious changes — this catches it.
/// </summary>
public sealed class SqliteConfigStoreTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;

    public SqliteConfigStoreTests()
    {
        dir = TestPaths.NewPath("pdn-cfgstore");
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
    }

    private SqliteConfigStore NewStore() => new(dbPath, new FakeTimeProvider());

    [Property(Arbitrary = [typeof(NodeConfigArbitraries)], MaxTest = 200)]
    public void Save_then_load_round_trips_the_full_tree(NodeConfig config)
    {
        // Each property case gets its own db so the singleton row can't collide.
        var path = Path.Combine(dir, "rt-" + Guid.NewGuid().ToString("N") + ".db");
        var store = new SqliteConfigStore(path, new FakeTimeProvider());

        store.Save(config).Should().BeTrue();
        var loaded = store.Load();

        loaded.Should().NotBeNull();
        var (got, schemaVer) = loaded!.Value;
        schemaVer.Should().Be(config.SchemaVersion);

        // Compare the pieces explicitly: NodeConfig's record equality compares the Ports /
        // Applications / Apps lists by REFERENCE, so a structural reparse is never reference-
        // equal. The sub-records (Tailscale, Management incl. WebAuthn) override Equals with
        // sequence-equality, so those compare by value directly.
        got.Identity.Should().Be(config.Identity);
        got.Ports.Should().Equal(config.Ports, "the polymorphic transport union must round-trip");
        got.Services.Should().Be(config.Services);
        got.Management.Should().Be(config.Management);
        got.NetRom.Should().Be(config.NetRom);
        got.Traffic.Should().Be(config.Traffic);
        got.Tailscale.Should().Be(config.Tailscale, "the tailscale tags list must round-trip by value");

        // And the canonical JSON is stable: re-serialising the loaded config equals the
        // serialisation of the input — one canonical form, byte-for-byte.
        NodeConfigJson.Serialize(got).Should().Be(NodeConfigJson.Serialize(config));
    }

    [Fact]
    public void Load_on_a_fresh_db_returns_null()
    {
        var store = NewStore();
        store.Load().Should().BeNull("an absent row is the first-boot migration signal");
    }

    [Fact]
    public void Save_is_an_upsert_keeping_exactly_one_row()
    {
        var store = NewStore();
        var a = new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } };
        var b = new NodeConfig { Identity = new Identity { Callsign = "G0ABC-2" } };

        store.Save(a).Should().BeTrue();
        store.Save(b).Should().BeTrue();   // ON CONFLICT(id=1) DO UPDATE — replaces, not appends

        var loaded = store.Load();
        loaded!.Value.Config.Identity.Callsign.Should().Be("G0ABC-2");
    }

    [Fact]
    public void AllowedOrigins_and_Tags_round_trip_by_value()
    {
        // The two explicit sequence-equality lists in the tree — pin them directly.
        var store = NewStore();
        var config = new NodeConfig
        {
            Identity = new Identity { Callsign = "M0LTE-1" },
            Management = new ManagementConfig
            {
                Auth = new AuthConfig
                {
                    WebAuthn = new WebAuthnConfig { AllowedOrigins = ["https://a.example", "https://b.example"] },
                },
            },
            Tailscale = new TailscaleConfig { Tags = ["tag:server", "tag:packetnet"] },
        };

        store.Save(config).Should().BeTrue();
        var got = store.Load()!.Value.Config;

        got.Management.Auth.WebAuthn.AllowedOrigins.Should().Equal("https://a.example", "https://b.example");
        got.Tailscale.Tags.Should().Equal("tag:server", "tag:packetnet");
        got.Management.Auth.WebAuthn.Should().Be(config.Management.Auth.WebAuthn);
        got.Tailscale.Should().Be(config.Tailscale);
    }

    // --- Forward schema migration (#488) ---

    /// <summary>Write a raw node_config row at an arbitrary <paramref name="schemaVer"/> and
    /// payload, as a pre-existing DB at some schema would have on disk. Bypasses Save (which
    /// always stamps the running version) so a test can synthesise an older/newer blob.</summary>
    private static void WriteRawRow(string dbPath, int schemaVer, string payload)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS node_config (
                id INTEGER PRIMARY KEY CHECK (id = 1), schema_ver INTEGER NOT NULL,
                format TEXT NOT NULL, payload TEXT NOT NULL, updated_utc TEXT NOT NULL);
            INSERT INTO node_config (id, schema_ver, format, payload, updated_utc)
            VALUES (1, $ver, 'json', $payload, '2026-01-01T00:00:00.0000000+00:00')
            ON CONFLICT(id) DO UPDATE SET schema_ver = $ver, payload = $payload;
            """;
        cmd.Parameters.AddWithValue("$ver", schemaVer);
        cmd.Parameters.AddWithValue("$payload", payload);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Load_at_current_schema_reports_the_current_version_and_does_not_migrate()
    {
        var store = NewStore();
        store.Save(new NodeConfig { Identity = new Identity { Callsign = "M0LTE-1" } }).Should().BeTrue();

        var loaded = store.Load();
        loaded!.Value.SchemaVer.Should().Be(NodeConfig.CurrentSchemaVersion);
        loaded.Value.Config.Identity.Callsign.Should().Be("M0LTE-1");
    }

    [Fact]
    public void Load_of_a_GREATER_future_schema_throws_the_boot_fail_safe()
    {
        // A v2 blob written by a newer build, then this (v1) build boots onto it: never run
        // on, nor clobber, a future schema. The store throws (it propagates to boot fail).
        var path = Path.Combine(dir, "future.db");
        WriteRawRow(path, schemaVer: NodeConfig.CurrentSchemaVersion + 1,
            payload: "{\"schemaVersion\":2,\"identity\":{\"callsign\":\"M0LTE-1\"}}");
        var store = new SqliteConfigStore(path, new FakeTimeProvider());

        var act = () => store.Load();
        act.Should().Throw<NodeConfigSchemaException>().WithMessage("*NEWER than this build understands*");
    }

    [Fact]
    public void Load_of_an_OLDER_blob_runs_the_registered_migration_loads_and_logs_the_rendered_line()
    {
        // current = 1 with no real migration yet, so drive the store with a SYNTHETIC current
        // (v2) + a v1→v2 registry to prove the load-time migrate-and-log path end-to-end. The
        // migration adds a grid that the loaded NodeConfig must then carry.
        var path = Path.Combine(dir, "older.db");
        WriteRawRow(path, schemaVer: 1,
            payload: "{\"schemaVersion\":1,\"identity\":{\"callsign\":\"M0LTE-1\"}}");

        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [1] = root => { root["identity"]!.AsObject()["grid"] = "IO91wm"; return root; },
        };
        var log = new CapturingStoreLogger();
        var store = new SqliteConfigStore(path, targetSchemaVersion: 2, registry, new FakeTimeProvider(), log);

        var loaded = store.Load();

        loaded.Should().NotBeNull();
        loaded!.Value.SchemaVer.Should().Be(2, "the loaded config has been brought forward to the target");
        loaded.Value.Config.Identity.Callsign.Should().Be("M0LTE-1");
        loaded.Value.Config.Identity.Grid.Should().Be("IO91wm", "the v1→v2 migration's edit is present in the loaded config");

        // Capturing-logger discipline: assert the RENDERED migration line, not just behaviour
        // (a LoggerMessage arg-swap would still load but render a garbled v→v line).
        log.Messages.Should().ContainSingle(m =>
            m.Level == LogLevel.Information && m.Text.Contains("migrated the persisted config schema v1→v2"));
    }

    [Fact]
    public void Load_at_the_target_with_a_registry_present_is_idempotent_no_migration_log()
    {
        // A blob already at the (synthetic) target v2 must NOT re-run the v1→v2 migration on a
        // subsequent load — the migration is not re-applied once at current.
        var path = Path.Combine(dir, "already-current.db");
        WriteRawRow(path, schemaVer: 2,
            payload: "{\"schemaVersion\":2,\"identity\":{\"callsign\":\"M0LTE-1\"}}");

        var ran = 0;
        var registry = new Dictionary<int, NodeConfigSchemaMigrations.Migration>
        {
            [1] = root => { ran++; return root; },
        };
        var log = new CapturingStoreLogger();
        var store = new SqliteConfigStore(path, targetSchemaVersion: 2, registry, new FakeTimeProvider(), log);

        var loaded = store.Load();

        loaded!.Value.SchemaVer.Should().Be(2);
        ran.Should().Be(0, "a blob already at the target is not re-migrated");
        log.Messages.Should().NotContain(m => m.Text.Contains("migrated the persisted config schema"));
    }

    [Fact]
    public void A_corrupt_blob_still_degrades_to_null_not_a_schema_throw()
    {
        // The migration seam must not change the corrupt-blob degrade path: malformed JSON is
        // still surfaced as null (re-seed), distinct from the schema fail-safe (throw).
        var path = Path.Combine(dir, "corrupt.db");
        WriteRawRow(path, schemaVer: NodeConfig.CurrentSchemaVersion, payload: "{ not json");
        var store = new SqliteConfigStore(path, new FakeTimeProvider());

        store.Load().Should().BeNull("a corrupt blob degrades to a re-seed, it does not throw");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>An in-memory ILogger over the store, recording the rendered message + level so
    /// a test can assert the RENDERED "migrated schema" line (capturing-logger discipline).</summary>
    private sealed class CapturingStoreLogger : ILogger<SqliteConfigStore>
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
