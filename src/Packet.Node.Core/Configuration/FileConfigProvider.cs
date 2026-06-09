using FluentValidation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Configuration;

/// <summary>
/// The slice-1 <see cref="IConfigProvider"/>: loads a YAML config file, watches
/// it, and on each change re-parses + validates + atomically swaps
/// <see cref="Current"/>, raising <see cref="OnChange"/>. If the file is absent
/// on construction it writes the commented first-start template and starts on
/// it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Atomic apply.</b> A candidate is fully parsed and validated before
/// <see cref="Current"/> is touched. On any failure (malformed YAML, unknown
/// transport kind, validation error) the provider logs and keeps the existing
/// <see cref="Current"/>, raising no <see cref="OnChange"/> — rollback by
/// construction.
/// </para>
/// <para>
/// <b>Debounce.</b> Editors fire a flurry of filesystem events per save; the
/// watcher's raw events are coalesced behind a debounce timer driven by the
/// injected <see cref="TimeProvider"/> (no wall-clock), so a save triggers a
/// single reload. Reloads are serialised on a lock so two near-simultaneous
/// saves can't interleave a swap.
/// </para>
/// <para>
/// A future <c>SqliteConfigProvider</c> implements the same seam and raises the
/// same <see cref="OnChange"/> from its web-edit path; the reconcile consumer is
/// identical.
/// </para>
/// </remarks>
public sealed partial class FileConfigProvider : IWritableConfigProvider, IDisposable
{
    private readonly string path;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<FileConfigProvider> logger;
    private readonly IValidator<NodeConfig> validator;
    private readonly object gate = new();
    private readonly List<Subscription> subscriptions = new();
    private readonly TimeSpan debounce;

    private FileSystemWatcher? watcher;
    private ITimer? debounceTimer;
    private NodeConfig current;
    private bool disposed;

    // The exact text we last wrote/loaded. The watcher fires on our own
    // TryApply write; when its debounced reload reads back these same bytes we
    // skip it (no double-swap, no echo OnChange). A genuine external edit reads
    // different bytes and is processed normally.
    private string lastText = string.Empty;

    /// <summary>
    /// Construct the provider over <paramref name="configPath"/>. Performs the
    /// first load (writing the template if the file is absent) before returning,
    /// so <see cref="Current"/> is valid immediately. Throws if the initial
    /// config is invalid — a node should not boot on a broken config.
    /// </summary>
    /// <param name="configPath">Absolute or relative path to the YAML config file.</param>
    /// <param name="timeProvider">Time source for the debounce timer.</param>
    /// <param name="logger">Optional logger; defaults to a null logger.</param>
    /// <param name="validator">Optional validator; defaults to <see cref="NodeConfigValidator"/>.</param>
    /// <param name="watch">Whether to install the filesystem watcher (tests that
    /// drive reloads manually set this false).</param>
    /// <param name="debounce">Debounce window for coalescing watcher events.</param>
    public FileConfigProvider(
        string configPath,
        TimeProvider? timeProvider = null,
        ILogger<FileConfigProvider>? logger = null,
        IValidator<NodeConfig>? validator = null,
        bool watch = true,
        TimeSpan? debounce = null)
    {
        path = Path.GetFullPath(configPath ?? throw new ArgumentNullException(nameof(configPath)));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<FileConfigProvider>.Instance;
        this.validator = validator ?? new NodeConfigValidator();
        this.debounce = debounce ?? TimeSpan.FromMilliseconds(250);

        current = LoadInitial();

        if (watch)
        {
            InstallWatcher();
        }
    }

    /// <inheritdoc/>
    public NodeConfig Current
    {
        get { lock (gate) return current; }
    }

    /// <inheritdoc/>
    public IDisposable OnChange(Action<NodeConfig> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        var sub = new Subscription(this, listener);
        lock (gate) subscriptions.Add(sub);
        return sub;
    }

    /// <summary>
    /// Force a synchronous reload now (parse + validate + atomic swap + raise).
    /// The watcher path funnels here after debouncing; tests with
    /// <c>watch: false</c> call it directly. Returns true if a new valid config
    /// was applied, false if the candidate was rejected (and <see cref="Current"/>
    /// left unchanged).
    /// </summary>
    public bool Reload()
    {
        NodeConfig applied;
        lock (gate)
        {
            if (disposed) return false;
            if (!TryLoadCandidate(out var candidate, out var text))
            {
                return false;   // rejected — Current unchanged, no event
            }
            if (string.Equals(text, lastText, StringComparison.Ordinal))
            {
                // The bytes on disk are exactly what we last applied/loaded — our
                // own TryApply write echoing back through the watcher, or a touch
                // with no content change. Nothing to do.
                return false;
            }
            current = candidate;
            lastText = text;
            applied = candidate;
        }
        RaiseOnChange(applied);
        return true;
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

        var text = NodeConfigYaml.Serialize(candidate);
        lock (gate)
        {
            if (disposed) return false;
            // Persist atomically (write a sibling temp + rename) so a reader — the
            // watcher, or a fresh boot — never sees a half-written file. Record the
            // exact bytes so the watcher's echo of this very write is skipped.
            WriteAtomic(text);
            lastText = text;
            current = candidate;
        }
        LogApplied(path, candidate.Identity.Callsign, candidate.Ports.Count);
        RaiseOnChange(candidate);
        return true;
    }

    private void WriteAtomic(string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);   // atomic rename on the same filesystem
    }

    private static ConfigValidationError[] ToErrors(FluentValidation.Results.ValidationResult result)
        => result.IsValid
            ? []
            : result.Errors.Select(e => new ConfigValidationError(e.PropertyName, e.ErrorMessage)).ToArray();

    private NodeConfig LoadInitial()
    {
        if (!File.Exists(path))
        {
            WriteTemplate();
        }

        var text = File.ReadAllText(path);
        var candidate = NodeConfigYaml.Parse(text);
        var result = validator.Validate(candidate);
        if (!result.IsValid)
        {
            throw new InvalidOperationException(
                $"the config at '{path}' is invalid:{Environment.NewLine}{FormatErrors(result.Errors)}");
        }
        lastText = text;
        LogLoaded(path, candidate.Identity.Callsign, candidate.Ports.Count);
        return candidate;
    }

    private void WriteTemplate()
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, NodeConfigTemplate.Yaml);
        LogWroteTemplate(path, NodeConfigTemplate.PlaceholderCallsign);
    }

    private bool TryLoadCandidate(out NodeConfig candidate, out string text)
    {
        candidate = current;
        text = string.Empty;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogReadFailed(ex, path);
            return false;
        }

        NodeConfig parsed;
        try
        {
            parsed = NodeConfigYaml.Parse(text);
        }
        catch (Exception ex)
        {
            LogParseFailed(ex, path);
            return false;
        }

        var result = validator.Validate(parsed);
        if (!result.IsValid)
        {
            LogValidationFailed(path, FormatErrors(result.Errors));
            return false;
        }

        candidate = parsed;
        return true;
    }

    private void InstallWatcher()
    {
        var dir = Path.GetDirectoryName(path);
        var file = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }

        debounceTimer = timeProvider.CreateTimer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        watcher = new FileSystemWatcher(dir, file)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        watcher.Changed += OnFileEvent;
        watcher.Created += OnFileEvent;
        watcher.Renamed += OnFileEvent;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Coalesce the editor's burst of events into one reload: (re)arm the
        // one-shot debounce timer. The timer callback runs Reload() once the
        // burst goes quiet.
        lock (gate)
        {
            if (disposed) return;
            debounceTimer?.Change(debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseOnChange(NodeConfig applied)
    {
        Subscription[] snapshot;
        lock (gate) snapshot = subscriptions.ToArray();
        foreach (var sub in snapshot)
        {
            try
            {
                sub.Invoke(applied);
            }
            catch (Exception ex)
            {
                // A faulty subscriber must not break config delivery to others
                // or wedge the watcher.
                LogSubscriberThrew(ex);
            }
        }
    }

    private static string FormatErrors(IEnumerable<FluentValidation.Results.ValidationFailure> errors) =>
        string.Join(Environment.NewLine, errors.Select(e => $"  - {e.ErrorMessage}"));

    // Source-generated logging (CA1848 — high-performance LoggerMessage). The
    // node host is a long-lived process; these compile to cached delegates.
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Loaded node config from {Path} (callsign {Callsign}, {PortCount} port(s)).")]
    private partial void LogLoaded(string path, string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Applied edited config to {Path} (callsign {Callsign}, {PortCount} port(s)).")]
    private partial void LogApplied(string path, string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No config found at {Path}; wrote a starter template (callsign {Placeholder}, no ports). Edit it to bring the node online.")]
    private partial void LogWroteTemplate(string path, string placeholder);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read config at {Path}; keeping the running config.")]
    private partial void LogReadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config at {Path} did not parse; keeping the running config.")]
    private partial void LogParseFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config at {Path} is invalid; keeping the running config:\n{Errors}")]
    private partial void LogValidationFailed(string path, string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "A config-change subscriber threw; continuing.")]
    private partial void LogSubscriberThrew(Exception ex);

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        if (watcher is not null)
        {
            watcher.Changed -= OnFileEvent;
            watcher.Created -= OnFileEvent;
            watcher.Renamed -= OnFileEvent;
            watcher.Dispose();
        }
        debounceTimer?.Dispose();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly FileConfigProvider owner;
        private readonly Action<NodeConfig> listener;

        public Subscription(FileConfigProvider owner, Action<NodeConfig> listener)
        {
            this.owner = owner;
            this.listener = listener;
        }

        public void Invoke(NodeConfig config) => listener(config);

        public void Dispose()
        {
            lock (owner.gate) owner.subscriptions.Remove(this);
        }
    }
}
