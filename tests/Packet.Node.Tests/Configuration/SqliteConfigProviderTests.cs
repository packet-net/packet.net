using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Configuration;

/// <summary>
/// Behavioural tests for <see cref="SqliteConfigProvider"/> — the config-in-DB provider
/// (#473). Covers the NON-NEGOTIABLE first-boot YAML→DB migration (an existing install's
/// hand-tuned /etc YAML is imported unchanged, runs identically, and a second boot reads
/// the DB not the YAML), idempotency, the boot-fails-on-broken-config invariant, the seed
/// fallbacks, and the write path (TryApply persists + raises OnChange once;
/// persist-before-advance: a DB write failure does NOT advance Current).
/// </summary>
public sealed class SqliteConfigProviderTests : IDisposable
{
    private readonly string dir;
    private readonly string dbPath;
    private readonly string yamlPath;

    public SqliteConfigProviderTests()
    {
        dir = Path.Combine(Path.GetTempPath(), "pdn-cfgprovider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        dbPath = Path.Combine(dir, "pdn.db");
        yamlPath = Path.Combine(dir, "packetnet.yaml");
    }

    private const string LabYaml = """
        schemaVersion: 1
        identity:
          callsign: M0LTE-7
          alias: LONDON
          grid: IO91wm
        ports:
          - id: vhf
            enabled: true
            transport:
              kind: kiss-tcp
              host: 127.0.0.1
              port: 8001
        management:
          telnet:
            enabled: true
            bind: 127.0.0.1
            port: 8011
          http:
            bind: 0.0.0.0
            port: 8080
        tailscale:
          enabled: true
          hostname: rdg-pdn
          tags:
            - tag:server
        """;

    private SqliteConfigStore NewStore() => new(dbPath, new FakeTimeProvider());

    private SqliteConfigProvider NewProvider(
        string? configPath = null,
        string? seedPath = null,
        string? templatePath = null,
        CapturingLogger<SqliteConfigProvider>? logger = null,
        ISqliteConfigStore? store = null) =>
        new(store ?? NewStore(),
            configPath,
            seedPath,
            templatePath,
            markerDir: dir,
            new FakeTimeProvider(),
            logger);

    // --- The migration proof (NON-NEGOTIABLE) ---

    [Fact]
    public void First_boot_imports_the_legacy_YAML_into_the_DB_and_a_second_boot_reads_the_DB()
    {
        File.WriteAllText(yamlPath, LabYaml);
        var expected = NodeConfigYaml.Parse(LabYaml);
        var log = new CapturingLogger<SqliteConfigProvider>();

        // FIRST boot: row absent → import the YAML.
        using (var first = NewProvider(configPath: yamlPath, logger: log))
        {
            // (a) the loaded config EQUALS the YAML's, across the whole tree.
            first.Current.Identity.Should().Be(expected.Identity);
            first.Current.Ports.Should().Equal(expected.Ports);
            first.Current.Management.Should().Be(expected.Management);
            first.Current.Tailscale.Should().Be(expected.Tailscale);

            // (b) the migration is announced on the boot log — assert the RENDERED string,
            // not just the persisted value (a LoggerMessage arg-swap would still pass a
            // value-only assert but render garbage).
            log.Messages.Should().ContainSingle(m =>
                m.Level == LogLevel.Information
                && m.Text.Contains("Migrated config from")
                && m.Text.Contains(yamlPath)
                && m.Text.Contains("M0LTE-7"));

            // (c) the marker is written (auditability) but is NOT the idempotency authority.
            File.Exists(Path.Combine(dir, SqliteConfigProvider.MigrationMarkerName)).Should().BeTrue();
        }

        // (d) the DB now holds the imported config (byte-identical canonical JSON).
        var inDb = NewStore().Load();
        inDb.Should().NotBeNull();
        NodeConfigJson.Serialize(inDb!.Value.Config).Should().Be(NodeConfigJson.Serialize(expected));

        // SECOND boot: row present → read the DB, NOT the YAML. Prove it by HAND-EDITING the
        // YAML on disk to a different callsign; the provider must ignore it (no hot file-watch,
        // and the row gates re-import).
        File.WriteAllText(yamlPath, LabYaml.Replace("M0LTE-7", "G0XXX-9"));
        var log2 = new CapturingLogger<SqliteConfigProvider>();
        using var second = NewProvider(configPath: yamlPath, logger: log2);

        second.Current.Identity.Callsign.Should().Be("M0LTE-7", "the second boot loads from the DB, not the edited YAML");
        log2.Messages.Should().NotContain(m => m.Text.Contains("Migrated config from"),
            "the import only runs when the row is absent — it must not re-run");
        log2.Messages.Should().Contain(m => m.Text.Contains("Loaded node config from pdn.db"));
    }

    [Fact]
    public void A_present_but_invalid_legacy_YAML_throws_at_boot()
    {
        // Blank callsign fails validation. The node must NOT silently template over an
        // operator's broken-but-fixable file — it boot-fails loudly.
        File.WriteAllText(yamlPath, "schemaVersion: 1\nidentity:\n  callsign: \"\"\n");

        var act = () => NewProvider(configPath: yamlPath);
        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid*");
    }

    [Fact]
    public void An_invalid_blob_already_in_the_DB_throws_at_boot()
    {
        // Persist an invalid config directly, then boot — the node never runs on broken config.
        var store = NewStore();
        store.Save(new NodeConfig { Identity = new Identity { Callsign = "" } }).Should().BeTrue();

        var act = () => NewProvider(store: store);
        act.Should().Throw<InvalidOperationException>().WithMessage("*pdn.db is invalid*");
    }

    // --- Seed fallbacks ---

    [Fact]
    public void With_no_YAML_and_no_seed_it_seeds_the_in_code_template_and_boots_idle()
    {
        var log = new CapturingLogger<SqliteConfigProvider>();
        using var provider = NewProvider(configPath: yamlPath, logger: log);   // yamlPath does NOT exist

        provider.Current.Identity.Callsign.Should().Be(NodeConfigTemplate.PlaceholderCallsign);
        log.Messages.Should().Contain(m => m.Text.Contains("seeded the starter template"));
        NewStore().Load().Should().NotBeNull("the seeded template is persisted so a second boot reads the DB");
    }

    [Fact]
    public void The_config_path_YAML_wins_over_the_seed_and_template()
    {
        File.WriteAllText(yamlPath, LabYaml);
        var seed = Path.Combine(dir, "seed.yaml");
        File.WriteAllText(seed, LabYaml.Replace("M0LTE-7", "SEED-1"));

        using var provider = NewProvider(configPath: yamlPath, seedPath: seed);
        provider.Current.Identity.Callsign.Should().Be("M0LTE-7", "--config wins over the seed");
    }

    [Fact]
    public void The_seed_is_used_when_no_config_YAML_exists()
    {
        var seed = Path.Combine(dir, "seed.yaml");
        File.WriteAllText(seed, LabYaml.Replace("M0LTE-7", "SEED-1"));

        using var provider = NewProvider(configPath: yamlPath, seedPath: seed);   // yamlPath absent
        provider.Current.Identity.Callsign.Should().Be("SEED-1");
    }

    [Fact]
    public void A_present_but_invalid_seed_throws_rather_than_silently_templating()
    {
        var seed = Path.Combine(dir, "seed.yaml");
        File.WriteAllText(seed, "schemaVersion: 1\nidentity:\n  callsign: \"\"\n");

        var act = () => NewProvider(configPath: yamlPath, seedPath: seed);   // yamlPath absent
        act.Should().Throw<InvalidOperationException>().WithMessage("*invalid*");
    }

    // --- The write path ---

    [Fact]
    public void TryApply_a_valid_candidate_persists_advances_Current_and_raises_OnChange_once()
    {
        File.WriteAllText(yamlPath, LabYaml);
        using var provider = NewProvider(configPath: yamlPath);
        int onChange = 0;
        NodeConfig? observed = null;
        using var _ = provider.OnChange(c => { onChange++; observed = c; });

        var candidate = provider.Current with
        {
            Identity = provider.Current.Identity with { Grid = "JO01aa" },
        };
        provider.TryApply(candidate, out var errors).Should().BeTrue();
        errors.Should().BeEmpty();

        provider.Current.Identity.Grid.Should().Be("JO01aa");
        onChange.Should().Be(1);
        observed!.Identity.Grid.Should().Be("JO01aa");

        // Persisted: a fresh provider over the same DB reads the new value.
        using var reboot = NewProvider(configPath: yamlPath);
        reboot.Current.Identity.Grid.Should().Be("JO01aa");
    }

    [Fact]
    public void TryApply_an_invalid_candidate_is_rejected_atomically_no_event_no_persist()
    {
        File.WriteAllText(yamlPath, LabYaml);
        using var provider = NewProvider(configPath: yamlPath);
        var before = provider.Current;
        int onChange = 0;
        using var _ = provider.OnChange(_ => onChange++);

        var invalid = provider.Current with { Identity = provider.Current.Identity with { Callsign = "" } };
        provider.TryApply(invalid, out var errors).Should().BeFalse();

        errors.Should().NotBeEmpty();
        provider.Current.Should().BeSameAs(before);
        onChange.Should().Be(0);
    }

    [Fact]
    public void A_DB_write_failure_does_NOT_advance_Current_persist_before_advance()
    {
        // A store that boots fine (returns the seed on Load) but fails every Save — the
        // wedged-pdn.db case. TryApply must return false and leave Current untouched: the
        // node never runs on un-persisted config.
        File.WriteAllText(yamlPath, LabYaml);
        var failing = new SaveFailsStore();
        using var provider = NewProvider(store: failing, configPath: yamlPath);
        var before = provider.Current;
        int onChange = 0;
        using var _ = provider.OnChange(_ => onChange++);

        var candidate = provider.Current with { Identity = provider.Current.Identity with { Grid = "JO01aa" } };
        provider.TryApply(candidate, out var errors).Should().BeFalse();

        errors.Should().Contain(e => e.Path == "(store)");
        provider.Current.Should().BeSameAs(before, "a failed persist must NOT advance Current");
        onChange.Should().Be(0, "no event when nothing was persisted");
    }

    [Fact]
    public void A_faulty_OnChange_subscriber_does_not_break_delivery_to_others()
    {
        File.WriteAllText(yamlPath, LabYaml);
        using var provider = NewProvider(configPath: yamlPath);
        int good = 0;
        using var _ = provider.OnChange(_ => throw new InvalidOperationException("boom"));
        using var __ = provider.OnChange(_ => good++);

        var candidate = provider.Current with { Identity = provider.Current.Identity with { Grid = "JO01aa" } };
        provider.TryApply(candidate, out var errs).Should().BeTrue();
        errs.Should().BeEmpty();

        good.Should().Be(1, "a throwing subscriber must not starve the others");
    }

    public void Dispose()
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A store whose row is always absent (so the provider boots via its
    /// migrate-from-YAML path, setting Current to the import) and whose every Save fails —
    /// the wedged-pdn.db case for the persist-before-advance contract.</summary>
    private sealed class SaveFailsStore : ISqliteConfigStore
    {
        public (NodeConfig Config, int SchemaVer)? Load() => null;

        public bool Save(NodeConfig config) => false;   // wedged db: every persist fails
    }

    /// <summary>An in-memory ILogger recording the rendered message + level of every entry,
    /// so a test asserts the RENDERED migration line (capturing-logger discipline), not just
    /// the persisted value.</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
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
