using Microsoft.Extensions.Logging;

namespace Packet.Node.Tests.Support;

/// <summary>
/// The one in-memory <see cref="ILogger{T}"/> the node tests assert against.
/// </summary>
/// <remarks>
/// <para>
/// It records the <b>rendered</b> line, not the state object, on purpose: a
/// <c>LoggerMessage</c> whose arguments are passed in the wrong order still persists the right
/// values and only renders wrongly, so asserting the rendered string is what catches it.
/// </para>
/// <para>
/// Eight near-identical copies of this class had been written across the suite (#700 C115),
/// three of them not thread safe even though the component under test logs from a background
/// pump. This one is thread safe and hands out snapshots.
/// </para>
/// </remarks>
/// <typeparam name="T">The category type, exactly as the component under test asks for it.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object gate = new();
    private readonly List<(LogLevel Level, string Text)> messages = [];

    /// <summary>Everything logged so far, oldest first (a snapshot; safe to enumerate).</summary>
    public IReadOnlyList<(LogLevel Level, string Text)> Messages
    {
        get
        {
            lock (gate)
            {
                return [.. messages];
            }
        }
    }

    /// <summary>The rendered text of everything logged at <see cref="LogLevel.Warning"/> or above.</summary>
    public IReadOnlyList<string> Warnings =>
        [.. Messages.Where(m => m.Level >= LogLevel.Warning).Select(m => m.Text)];

    /// <summary>The rendered text of every line, whatever the level.</summary>
    public IReadOnlyList<string> Lines => [.. Messages.Select(m => m.Text)];

    /// <summary>The rendered text of every line logged at exactly <paramref name="level"/>.</summary>
    public IReadOnlyList<string> Render(LogLevel level) =>
        [.. Messages.Where(m => m.Level == level).Select(m => m.Text)];

    /// <summary>True when a line at <paramref name="level"/> contains <paramref name="fragment"/>.</summary>
    public bool Has(LogLevel level, string fragment) =>
        Messages.Any(m => m.Level == level && m.Text.Contains(fragment, StringComparison.Ordinal));

    /// <inheritdoc/>
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        lock (gate)
        {
            messages.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> / <see cref="ILoggerFactory"/> that captures every category's
/// output, for the tests that need to see what a component logged through a factory rather than
/// through an injected <see cref="ILogger{T}"/>.
/// </summary>
public sealed class CapturingLoggerFactory : ILoggerFactory, ILoggerProvider
{
    private readonly object gate = new();
    private readonly List<(string Category, LogLevel Level, string Text)> messages = [];

    /// <summary>Everything logged so far, oldest first (a snapshot).</summary>
    public IReadOnlyList<(string Category, LogLevel Level, string Text)> Messages
    {
        get
        {
            lock (gate)
            {
                return [.. messages];
            }
        }
    }

    /// <summary>The rendered text of every line, whatever the category or level.</summary>
    public IReadOnlyList<string> Lines => [.. Messages.Select(m => m.Text)];

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);

    /// <inheritdoc/>
    public void AddProvider(ILoggerProvider provider)
    {
        // Nothing to chain: this factory is the sink.
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Nothing to release: the capture list dies with the test.
    }

    private void Add(string category, LogLevel level, string text)
    {
        lock (gate)
        {
            messages.Add((category, level, text));
        }
    }

    private sealed class CategoryLogger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            owner.Add(category, logLevel, formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
