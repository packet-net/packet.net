using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Api;

namespace Packet.Node.Api;

/// <summary>
/// A bounded in-memory ring of the node's own recent log lines, feeding
/// <c>GET /api/v1/log</c> and the dashboard's "Recent activity" card.
/// </summary>
/// <remarks>
/// <para>
/// Until #694 (review item C008) <c>/log</c> was a permanent empty stub while the OpenAPI doc
/// and the dashboard card presented it as live, so the card was always "No recent activity."
/// on a real node. Nothing in the node produced a <see cref="LogLine"/> at all. This is the
/// smallest honest fix: a logger provider tees whatever MEL already emits (after the
/// configured level filters, including any live override from
/// <see cref="DynamicLogLevelOverrides"/>) into a fixed-size ring, and the endpoint serves the
/// tail newest-first.
/// </para>
/// <para>
/// It is deliberately NOT a journald reader: the node must work identically in a container, a
/// dev run, and under systemd, and it must not need a privileged socket or shell out to
/// <c>journalctl</c>. What it shows is this process's own logging since start, which is what
/// the card actually wants. Bounded at <see cref="Capacity"/> lines, so memory is flat.
/// </para>
/// </remarks>
public sealed class NodeLogRing
{
    /// <summary>How many lines are retained. A few hundred covers "what just happened" (the
    /// card shows a handful) at a trivial, fixed memory cost.</summary>
    public const int Capacity = 500;

    /// <summary>The largest tail <c>GET /log</c> will serve in one response.</summary>
    public const int MaxTail = Capacity;

    /// <summary>The tail served when the caller names no limit.</summary>
    public const int DefaultTail = 100;

    private readonly ConcurrentQueue<LogLine> lines = new();

    /// <summary>Append one rendered line, evicting the oldest when full. Thread-safe and
    /// lock-free: the logging path must never block on the API.</summary>
    public void Add(LogLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        lines.Enqueue(line);
        while (lines.Count > Capacity && lines.TryDequeue(out _))
        {
            // Drop the oldest until back inside the cap.
        }
    }

    /// <summary>The most recent <paramref name="limit"/> lines, newest first (the order the
    /// dashboard renders).</summary>
    public IReadOnlyList<LogLine> Recent(int limit)
    {
        int take = Math.Clamp(limit, 1, MaxTail);
        var snapshot = lines.ToArray();
        var result = new LogLine[Math.Min(take, snapshot.Length)];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = snapshot[snapshot.Length - 1 - i];
        }
        return result;
    }
}

/// <summary>
/// The <see cref="ILoggerProvider"/> that tees MEL output into a <see cref="NodeLogRing"/>.
/// Registered alongside the console provider, so it sees exactly the lines the configured
/// filters let through (no separate level knob to keep in sync).
/// </summary>
public sealed class NodeLogRingProvider(NodeLogRing ring, TimeProvider clock) : ILoggerProvider
{
    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new RingLogger(ring, clock, categoryName);

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: the ring outlives the provider and owns no handles.
    }

    private sealed class RingLogger(NodeLogRing ring, TimeProvider clock, string category) : ILogger
    {
        // The short category the line carries: the last dotted segment, which is the type name
        // ("PortSupervisor"), not the whole namespace - a log card is 80 columns wide.
        private readonly string shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Cheap and honest: the ring takes whatever the configured filters admit. MEL has
        // already applied them by the time Log is called, but IsEnabled is also consulted for
        // the level check, so mirror it rather than claiming to want Trace.
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                // The type + message, never the stack: this feeds a one-line-per-entry card.
                message = $"{message} ({exception.GetType().Name}: {exception.Message})";
            }

            ring.Add(new LogLine(
                T: clock.GetUtcNow().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                Lvl: Severity(logLevel),
                Msg: $"{shortCategory}: {message}"));
        }

        // The UI knows three severities; map the six MEL levels onto them.
        private static string Severity(LogLevel level) => level switch
        {
            LogLevel.Critical or LogLevel.Error => "error",
            LogLevel.Warning => "warn",
            _ => "info",
        };
    }
}
