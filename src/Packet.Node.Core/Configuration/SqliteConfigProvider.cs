using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The Slice-2 <see cref="IWritableConfigProvider"/>: the node's live config lives in a
/// single versioned JSON-blob row in <c>pdn.db</c> (behind <see cref="ISqliteConfigStore"/>),
/// not in a watched YAML file. It implements the SAME seam as
/// <see cref="FileConfigProvider"/> — <see cref="Current"/> + <see cref="OnChange"/> +
/// <see cref="Validate"/> + <see cref="TryApply"/> — so every consumer
/// (<see cref="Hosting.NodeHostedService"/>, the control API, the reconcile path) is
/// unchanged; only where config is stored differs.
/// </summary>
/// <remarks>
/// <para>
/// <b>Strictly simpler than the file provider.</b> With the DB as the single writer there
/// is no out-of-band edit to watch and no echo of the provider's own write to suppress, so
/// the <see cref="FileSystemWatcher"/>, the debounce timer, and the <c>lastText</c>
/// byte-equality machinery are GONE. A config write (the API) goes: validate → persist →
/// advance <see cref="Current"/> → raise <see cref="OnChange"/>, raising the same signal
/// the file watcher used to. There is no clock dependency on the apply path (no debounce).
/// </para>
/// <para>
/// <b>Persist-before-advance.</b> On <see cref="TryApply"/> the candidate is persisted to
/// the DB <em>before</em> <see cref="Current"/> advances. A DB write failure therefore does
/// NOT advance <see cref="Current"/> — the node never runs on un-persisted config; the
/// edit surfaces as a failed apply instead. This is stricter than the file provider (which
/// advanced even when the on-disk write was best-effort) and correct.
/// </para>
/// <para>
/// <b>First-boot migration / seed.</b> When the DB row is ABSENT the ctor resolves a config
/// in priority order — the <c>--config</c> YAML (the lab carry-over of a hand-tuned
/// <c>/etc/packetnet/packetnet.yaml</c>), then <c>PACKETNET_CONFIG_SEED</c>, then the
/// <c>/usr/share/packetnet/packetnet.yaml.example</c> bootstrap template, then the in-code
/// <see cref="NodeConfigTemplate"/> so a node ALWAYS boots even with no files at all — and
/// imports it into the row. A present-but-invalid source THROWS (a node never boots on
/// broken config). The import is idempotent <em>structurally</em>: it only runs when the row
/// is absent, so a re-run / restart / downgrade-then-upgrade cannot double-import. After an
/// import a sibling marker (<c>.config-migrated</c>) is written for auditability; the node
/// does NOT depend on it (the row is the authority). The source YAML is never deleted.
/// </para>
/// </remarks>
public sealed partial class SqliteConfigProvider : IWritableConfigProvider, IDisposable
{
    /// <summary>The marker file name written beside <c>pdn.db</c> after a first-boot import,
    /// recording the source + timestamp so the migration is auditable. Informational only —
    /// the DB row is the idempotency authority, never this file.</summary>
    public const string MigrationMarkerName = ".config-migrated";

    private readonly ISqliteConfigStore store;
    private readonly string? configPath;
    private readonly string? seedPath;
    private readonly string? exampleTemplatePath;
    private readonly string? markerDir;
    private readonly TimeProvider clock;
    private readonly ILogger<SqliteConfigProvider> logger;
    private readonly IValidator<NodeConfig> validator;
    private readonly object gate = new();
    private readonly List<Subscription> subscriptions = new();

    private NodeConfig current;
    private bool disposed;

    /// <summary>
    /// Construct the provider over <paramref name="store"/>. Performs the synchronous
    /// load-or-migrate-or-seed before returning, so <see cref="Current"/> is valid
    /// immediately (the host reads the Kestrel bind off it before start). Throws if the
    /// resolved initial config is invalid — a node should not boot on a broken config.
    /// </summary>
    /// <param name="store">The config row store (<c>pdn.db</c>).</param>
    /// <param name="configPath">The resolved <c>--config</c> path — the legacy YAML to
    /// import on first boot if it exists (the lab carry-over). May be null/absent.</param>
    /// <param name="seedPath">Optional explicit seed-file path
    /// (<c>PACKETNET_CONFIG_SEED</c>), consulted on first boot only after the
    /// <paramref name="configPath"/> YAML. May be null.</param>
    /// <param name="exampleTemplatePath">The bootstrap template
    /// (<c>/usr/share/packetnet/packetnet.yaml.example</c>) consulted after the seed; the
    /// in-code <see cref="NodeConfigTemplate"/> is the ultimate fallback. May be null.</param>
    /// <param name="markerDir">Directory to write the <see cref="MigrationMarkerName"/>
    /// marker into after an import (the writable state dir beside <c>pdn.db</c>). Null skips
    /// the marker.</param>
    /// <param name="clock">Time source for the marker stamp; defaults to system.</param>
    /// <param name="logger">Optional logger; defaults to a null logger.</param>
    /// <param name="validator">Optional validator; defaults to <see cref="NodeConfigValidator"/>.</param>
    public SqliteConfigProvider(
        ISqliteConfigStore store,
        string? configPath,
        string? seedPath = null,
        string? exampleTemplatePath = null,
        string? markerDir = null,
        TimeProvider? clock = null,
        ILogger<SqliteConfigProvider>? logger = null,
        IValidator<NodeConfig>? validator = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.configPath = configPath;
        this.seedPath = seedPath;
        this.exampleTemplatePath = exampleTemplatePath;
        this.markerDir = markerDir;
        this.clock = clock ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<SqliteConfigProvider>.Instance;
        this.validator = validator ?? new NodeConfigValidator();

        current = LoadOrMigrateOrSeed();
    }

    /// <inheritdoc/>
    public NodeConfig Current
    {
        get { lock (gate)
            {
                return current;
            }
        }
    }

    /// <inheritdoc/>
    public IDisposable OnChange(Action<NodeConfig> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var sub = new Subscription(this, listener);
        lock (gate)
        {
            subscriptions.Add(sub);
        }

        return sub;
    }

    /// <inheritdoc/>
    public IReadOnlyList<ConfigValidationError> Validate(NodeConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return ToErrors(validator.Validate(candidate));
    }

    /// <inheritdoc/>
    public bool TryApply(NodeConfig candidate, out IReadOnlyList<ConfigValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var result = validator.Validate(candidate);
        if (!result.IsValid)
        {
            errors = ToErrors(result);
            return false;   // rejected — nothing persisted, Current unchanged, no event
        }
        errors = [];

        NodeConfig applied;
        lock (gate)
        {
            if (disposed)
            {
                return false;
            }

            // Persist-before-advance: a DB write failure must NOT advance Current (the node
            // never runs on un-persisted config). Surface it as a failed apply.
            if (!store.Save(candidate))
            {
                errors = [new ConfigValidationError("(store)",
                    "the config could not be persisted to pdn.db; the running config is unchanged.")];
                return false;
            }
            current = candidate;
            applied = candidate;
        }
        LogApplied(candidate.Identity.Callsign, candidate.Ports.Count);
        WarnOnConfigQuirks(applied);
        RaiseOnChange(applied);
        return true;
    }

    private static ConfigValidationError[] ToErrors(FluentValidation.Results.ValidationResult result)
        => result.IsValid
            ? []
            : result.Errors.Select(e => new ConfigValidationError(e.PropertyName, e.ErrorMessage)).ToArray();

    /// <summary>
    /// The ctor load path. If the singleton row is present, load + validate + run on it
    /// (THROW on invalid — a node never boots on broken config). If absent, resolve a source
    /// (the first-boot migration/seed) and import it.
    /// </summary>
    private NodeConfig LoadOrMigrateOrSeed()
    {
        var loaded = store.Load();
        if (loaded is { } row)
        {
            var result = validator.Validate(row.Config);
            if (!result.IsValid)
            {
                throw new InvalidOperationException(
                    $"the config in pdn.db is invalid:{Environment.NewLine}{FormatErrors(result.Errors)}");
            }
            LogLoaded(row.SchemaVer, row.Config.Identity.Callsign, row.Config.Ports.Count);
            WarnOnConfigQuirks(row.Config);
            return row.Config;
        }

        return MigrateOrSeed();
    }

    /// <summary>
    /// First-boot source resolution (row absent). Priority:
    /// <c>--config</c> YAML → <c>PACKETNET_CONFIG_SEED</c> → the
    /// <c>/usr/share/packetnet/packetnet.yaml.example</c> bootstrap template → the in-code
    /// <see cref="NodeConfigTemplate"/>. The first source that EXISTS is parsed + validated
    /// (THROW on invalid) + saved into the row. The in-code template is the ultimate fallback
    /// so the node can never fail to boot for lack of a file.
    /// </summary>
    private NodeConfig MigrateOrSeed()
    {
        // 1) The legacy --config YAML (the lab carry-over). A present-but-broken file throws.
        if (configPath is { Length: > 0 } cfg && File.Exists(cfg))
        {
            var config = ParseValidateOrThrow(cfg, "the legacy config");
            PersistImported(config, cfg, MigrationKind.Migrated);
            return config;
        }

        // 2) An explicit seed file (headless image bootstrap, never touching /etc).
        if (seedPath is { Length: > 0 } seed && File.Exists(seed))
        {
            var config = ParseValidateOrThrow(seed, "the seed config");
            PersistImported(config, seed, MigrationKind.Seeded);
            return config;
        }

        // 3) The packaged bootstrap template (/usr/share/packetnet/packetnet.yaml.example).
        if (exampleTemplatePath is { Length: > 0 } example && File.Exists(example))
        {
            var config = ParseValidateOrThrow(example, "the bootstrap template");
            PersistImported(config, example, MigrationKind.Seeded);
            return config;
        }

        // 4) The in-code template — the ultimate fallback so the node ALWAYS boots idle on
        // the placeholder callsign even with no files at all. This must parse + validate (it
        // is curated), so a throw here is a genuine bug, not an operator error.
        var fromTemplate = NodeConfigYaml.Parse(NodeConfigTemplate.Yaml);
        var templateResult = validator.Validate(fromTemplate);
        if (!templateResult.IsValid)
        {
            throw new InvalidOperationException(
                $"the in-code config template is invalid (this is a bug):{Environment.NewLine}{FormatErrors(templateResult.Errors)}");
        }
        store.Save(fromTemplate);   // best-effort; an unwritable DB still boots on this in-memory config
        LogSeededTemplate(NodeConfigTemplate.PlaceholderCallsign);
        WarnOnConfigQuirks(fromTemplate);
        return fromTemplate;
    }

    private NodeConfig ParseValidateOrThrow(string path, string what)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // We checked File.Exists; a read fault here is real (perms). Boot-fail loudly
            // rather than silently template over a config the operator put there.
            throw new InvalidOperationException($"could not read {what} at '{path}': {ex.Message}", ex);
        }

        NodeConfig parsed;
        try
        {
            parsed = NodeConfigYaml.Parse(text);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{what} at '{path}' did not parse: {ex.Message}", ex);
        }

        var result = validator.Validate(parsed);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(
                $"{what} at '{path}' is invalid:{Environment.NewLine}{FormatErrors(result.Errors)}");
        }
        return parsed;
    }

    private void PersistImported(NodeConfig config, string sourcePath, MigrationKind kind)
    {
        store.Save(config);   // best-effort persist; an unwritable DB still boots on the import
        if (kind == MigrationKind.Migrated)
        {
            LogMigrated(sourcePath, config.Identity.Callsign, config.Ports.Count);
        }
        else
        {
            LogSeededFromFile(sourcePath, config.Identity.Callsign, config.Ports.Count);
        }
        WriteMigrationMarker(sourcePath, kind);
        WarnOnConfigQuirks(config);
    }

    /// <summary>Write the informational <see cref="MigrationMarkerName"/> marker recording the
    /// source path + UTC stamp. Best-effort: a failure to write it never affects boot (the row
    /// is the authority). The source YAML is intentionally left in place (rollback floor).</summary>
    private void WriteMigrationMarker(string sourcePath, MigrationKind kind)
    {
        // Only write into a directory that ALREADY exists — never create one. The marker is
        // purely informational (the DB row is the idempotency authority), and creating the
        // dir here would have a side effect: in the unwritable-db case (the state dir absent)
        // it would materialise the dir and make pdn.db writable, defeating the degrade path.
        if (markerDir is not { Length: > 0 } || !Directory.Exists(markerDir))
        {
            return;
        }
        try
        {
            var marker = Path.Combine(markerDir, MigrationMarkerName);
            var stamp = clock.GetUtcNow().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            var verb = kind == MigrationKind.Migrated ? "migrated" : "seeded";
            File.WriteAllText(marker,
                $"# pdn config {verb} into pdn.db on first boot — informational only.{Environment.NewLine}" +
                $"# The DB row is the authority; this file is safe to delete.{Environment.NewLine}" +
                $"# The source below is left in place (do not delete on rollback grounds).{Environment.NewLine}" +
                $"source: {sourcePath}{Environment.NewLine}" +
                $"timestamp: {stamp}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMarkerFailed(ex, markerDir);
        }
    }

    // Surface non-fatal config warnings at load/apply, on the boot log — parity with
    // FileConfigProvider.WarnOnConfigQuirks. Pure read of the already-resolved config.
    private void WarnOnConfigQuirks(NodeConfig config)
    {
        var (_, warnings) = config.NetRom.ResolveRouting();
        foreach (var warning in warnings.Concat(NodeConfigWarnings.DuplicateMqttInstances(config)))
        {
            LogConfigWarning(warning);
        }
    }

    private void RaiseOnChange(NodeConfig applied)
    {
        Subscription[] snapshot;
        lock (gate)
        {
            snapshot = subscriptions.ToArray();
        }

        foreach (var sub in snapshot)
        {
            try
            {
                sub.Invoke(applied);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not break config delivery to others.
                LogSubscriberThrew(ex);
            }
        }
    }

    private static string FormatErrors(IEnumerable<FluentValidation.Results.ValidationFailure> errors) =>
        string.Join(Environment.NewLine, errors.Select(e => $"  - {e.ErrorMessage}"));

    private enum MigrationKind { Migrated, Seeded }

    // Source-generated logging (CA1848). The node host is a long-lived process; these
    // compile to cached delegates.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Loaded node config from pdn.db (schema v{SchemaVer}, callsign {Callsign}, {PortCount} port(s)).")]
    private partial void LogLoaded(int schemaVer, string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Applied edited config to pdn.db (callsign {Callsign}, {PortCount} port(s)).")]
    private partial void LogApplied(string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Migrated config from {Path} into pdn.db (callsign {Callsign}, {PortCount} port(s)). The source file is left in place and is now vestigial; edit config via the web UI / API / `pdn config import`.")]
    private partial void LogMigrated(string path, string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Seeded config into pdn.db from {Path} (callsign {Callsign}, {PortCount} port(s)).")]
    private partial void LogSeededFromFile(string path, string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No config in pdn.db and no config file found; seeded the starter template (callsign {Placeholder}, no ports). Edit it via the web UI / API to bring the node online.")]
    private partial void LogSeededTemplate(string placeholder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config note: {Warning}")]
    private partial void LogConfigWarning(string warning);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write the config-migration marker in {Dir} (informational only; boot is unaffected).")]
    private partial void LogMarkerFailed(Exception ex, string dir);

    [LoggerMessage(Level = LogLevel.Error, Message = "A config-change subscriber threw; continuing.")]
    private partial void LogSubscriberThrew(Exception ex);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }
        // No watcher / timer to tear down — the strictly-simpler DB provider holds no OS
        // resources of its own (the store opens a fresh pooled connection per call).
    }

    private sealed class Subscription : IDisposable
    {
        private readonly SqliteConfigProvider owner;
        private readonly Action<NodeConfig> listener;

        public Subscription(SqliteConfigProvider owner, Action<NodeConfig> listener)
        {
            this.owner = owner;
            this.listener = listener;
        }

        public void Invoke(NodeConfig config) => listener(config);

        public void Dispose()
        {
            lock (owner.gate)
            {
                owner.subscriptions.Remove(this);
            }
        }
    }
}
