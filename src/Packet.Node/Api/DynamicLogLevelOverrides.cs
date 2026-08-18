using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Packet.Node.Api;

/// <summary>
/// A runtime, restart-free log-level override store for the node host. Holds a thread-safe
/// map of <em>category prefix → minimum <see cref="LogLevel"/></em> that an operator can
/// mutate live (via <c>PUT /api/v1/system/loglevel</c>) to raise (or lower) the verbosity of
/// a logging category without touching <c>appsettings.json</c> (read-only under
/// <c>ProtectSystem=strict</c>) and without a restart (which would drop every session).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this takes effect on already-created loggers.</b> Microsoft.Extensions.Logging has
/// no built-in runtime level switch: a logger's effective filter rules are computed once from
/// <see cref="LoggerFilterOptions"/> and cached. The robust hook is the options pipeline -
/// this class is registered as BOTH an <see cref="IConfigureOptions{TOptions}"/> of
/// <see cref="LoggerFilterOptions"/> (it appends one <see cref="LoggerFilterRule"/> per active
/// override) AND an <see cref="IOptionsChangeTokenSource{TOptions}"/>. MEL's
/// <c>LoggerFactory</c> subscribes to that change token; when a mutation fires it, the factory
/// re-runs every <c>IConfigureOptions&lt;LoggerFilterOptions&gt;</c> (including this one) and
/// re-applies the rebuilt rules to <b>every cached logger</b> - so a category created at
/// startup picks up the new level immediately, no restart. (Proven by a focused test that
/// flips a category and observes <c>ILogger.IsEnabled</c> change.)
/// </para>
/// <para>
/// <b>Category matching = longest-prefix wins</b>, which is exactly MEL's own
/// <see cref="LoggerFilterRule"/> selection semantics - a rule whose category name is the
/// longest prefix of the logger's category is selected. Our rules are appended <em>after</em>
/// the config-bound rules (appsettings), so an override of equal specificity wins over the
/// configured value. Removing an override drops its rule, restoring the configured behaviour.
/// </para>
/// <para>
/// <b>Default state = no overrides.</b> With the map empty this contributes zero rules, so
/// logging behaves exactly as configured by <c>appsettings.json</c> - the capability is
/// strictly additive and behaviour-preserving until an operator uses it.
/// </para>
/// </remarks>
public sealed class DynamicLogLevelOverrides
    : IConfigureOptions<LoggerFilterOptions>, IOptionsChangeTokenSource<LoggerFilterOptions>, IDisposable
{
    // Ordinal: logging category names are case-sensitive .NET type/namespace names.
    private readonly ConcurrentDictionary<string, LogLevel> overrides = new(StringComparer.Ordinal);

    // The change token MEL's LoggerFactory watches. Swapped + signalled on every mutation so
    // the factory re-runs Configure(...) and refreshes all cached loggers' filter rules.
    private ChangeTokenHolder token = new();

    /// <summary>The options instance name this source/configurator targets - the default
    /// (unnamed) <see cref="LoggerFilterOptions"/>, which is what <c>LoggerFactory</c> reads.</summary>
    public string Name => Options.DefaultName;

    /// <summary>Set (or replace) the override for <paramref name="category"/> to
    /// <paramref name="level"/> and refresh all loggers live.</summary>
    public void Set(string category, LogLevel level)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        overrides[category] = level;
        FireChanged();
    }

    /// <summary>Remove the override for <paramref name="category"/> (restoring the configured
    /// level) and refresh all loggers live. No-op if no such override exists, but always
    /// signals so a redundant clear is harmless.</summary>
    public void Clear(string category)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        overrides.TryRemove(category, out _);
        FireChanged();
    }

    /// <summary>A point-in-time snapshot of the active overrides (category → level), ordered
    /// by category for a stable API response.</summary>
    public IReadOnlyList<KeyValuePair<string, LogLevel>> Snapshot() =>
        overrides
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToList();

    /// <inheritdoc />
    /// <remarks>Appends one filter rule per active override. Called by the options machinery
    /// at logger-pipeline build time AND on every change-token signal (the refresh path).</remarks>
    public void Configure(LoggerFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var kv in overrides)
        {
            // providerName: null → applies across all log providers (console etc.).
            // filter: null → a pure minimum-level rule. MEL selects the rule whose
            // categoryName is the longest matching prefix of the logger's category.
            options.Rules.Add(new LoggerFilterRule(
                providerName: null,
                categoryName: kv.Key,
                logLevel: kv.Value,
                filter: null));
        }
    }

    /// <inheritdoc />
    public IChangeToken GetChangeToken() => token;

    /// <summary>Dispose the current change-token holder. Called by the DI container when the
    /// singleton is torn down at host shutdown.</summary>
    public void Dispose() => token.Dispose();

    private void FireChanged()
    {
        // Swap in a fresh token THEN signal the old one, so a callback that re-reads the
        // current token during the refresh sees the new (un-signalled) one. Cancel runs the
        // registered callbacks synchronously, so once Signal returns the old token's work is
        // done and its CTS can be disposed.
        var previous = Interlocked.Exchange(ref token, new ChangeTokenHolder());
        previous.Signal();
        previous.Dispose();
    }

    // A one-shot change token. MEL re-registers a fresh callback after each signal, so we hand
    // out a new holder per mutation (the standard ConfigurationReloadToken pattern).
    private sealed class ChangeTokenHolder : IChangeToken, IDisposable
    {
        private readonly CancellationTokenSource cts = new();

        public bool ActiveChangeCallbacks => true;

        public bool HasChanged => cts.IsCancellationRequested;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) =>
            cts.Token.Register(callback, state);

        public void Signal() => cts.Cancel();

        public void Dispose() => cts.Dispose();
    }
}
