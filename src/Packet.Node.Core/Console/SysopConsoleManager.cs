using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Packet.Node.Core.Console;

/// <summary>
/// Holds the operator-initiated connect-out sessions (the web Sessions screen's
/// "sysop connect") and turns each into an interactive console the browser can read
/// and type into: a per-connection read pump drains the peer's output into a bounded
/// backlog and fans it out to any number of SSE subscribers, while
/// <see cref="WriteAsync"/> sends typed input back and <see cref="CloseAsync"/> tears
/// the connection (and its DISC) down.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the web connect-out <em>not blind</em>: a connect-out hands its
/// <see cref="INodeConnection"/> here (keyed by the session id <c>"{portId}:{peer}"</c>)
/// instead of discarding it, so the bytes the peer sends (a LinBPQ banner/prompt, a
/// command response) are captured and streamed to the browser rather than buffering
/// unread. The node's own inbound consoles are unaffected — only operator-initiated
/// outbound connections are adopted here.
/// </para>
/// <para>
/// <b>Concurrency.</b> One read-pump <see cref="Task"/> per connection is the sole
/// reader of that connection (no contention on <see cref="INodeConnection.ReadAsync"/>);
/// the backlog is guarded by a per-session lock; subscribers are a concurrent set of
/// bounded channel writers, and a slow browser drops its oldest output chunks
/// (<see cref="BoundedChannelFullMode.DropOldest"/>) rather than stalling the pump. The
/// pump ends — completing every subscriber — when the peer goes away
/// (<see cref="INodeConnection.ReadAsync"/> returns empty) or on
/// <see cref="CloseAsync"/>.
/// </para>
/// <para>
/// <b>The idle reaper.</b> A node command console adopted here (an <c>Open(..., reapWhenIdle:
/// true)</c> session, i.e. every <c>console:&lt;guid&gt;</c> the browser opens) keeps a node
/// command service and a loopback pair alive for as long as it is managed. A browser that is
/// closed, crashes, or loses the network never sends the DELETE, so before #694 (review item
/// C062) those sessions leaked until host shutdown. A <see cref="TimeProvider"/>-driven sweep
/// now closes any reapable session that has had NO subscriber and NO typed input for the idle
/// timeout (<c>management.console.idleTimeoutMinutes</c>, default 30; 0 disables the reaper).
/// An attached SSE subscriber counts as activity for the whole time it is attached, so a
/// watched-but-quiet console is never reaped. Adopted connect-out sessions
/// (<c>{portId}:{peer}</c>) are deliberately NOT reaped: those are real RF links an operator
/// may leave up on purpose, and the peer-gone path already tears them down when the far end
/// drops.
/// </para>
/// </remarks>
public sealed partial class SysopConsoleManager : IAsyncDisposable
{
    // Keep a bounded tail of recent output so a browser opening the stream mid-session
    // sees context (the banner/prompt it missed), without unbounded memory growth.
    private const int BacklogCap = 16 * 1024;

    /// <summary>The idle timeout used when the host doesn't configure one.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(30);

    // How often the reaper looks: half the timeout, but never more often than every 5 s and
    // never less often than once a minute. A sweep is a walk of a handful of dictionary
    // entries, so the cost is nil either way.
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxSweepInterval = TimeSpan.FromMinutes(1);

    private readonly ILogger<SysopConsoleManager> logger;
    private readonly ConcurrentDictionary<string, ConsoleSession> sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly TimeSpan idleTimeout;
    private readonly ITimer? reaper;
    private int disposed;

    /// <summary>
    /// Construct the manager. <paramref name="idleTimeout"/> is how long a reapable session may
    /// sit with no SSE subscriber and no typed input before it is closed; null uses
    /// <see cref="DefaultIdleTimeout"/>, and zero (or negative) disables the reaper entirely.
    /// The sweep rides <paramref name="clock"/>, never the wall clock (repo rule 2.7).
    /// </summary>
    public SysopConsoleManager(
        ILogger<SysopConsoleManager>? logger = null,
        TimeProvider? clock = null,
        TimeSpan? idleTimeout = null)
    {
        this.logger = logger ?? NullLogger<SysopConsoleManager>.Instance;
        this.clock = clock ?? TimeProvider.System;
        this.idleTimeout = idleTimeout ?? DefaultIdleTimeout;

        if (this.idleTimeout > TimeSpan.Zero)
        {
            var sweep = SweepInterval(this.idleTimeout);
            reaper = this.clock.CreateTimer(_ => ReapIdle(), null, sweep, sweep);
        }
    }

    private static TimeSpan SweepInterval(TimeSpan timeout)
    {
        var half = timeout / 2;
        return half < MinSweepInterval ? MinSweepInterval
            : half > MaxSweepInterval ? MaxSweepInterval
            : half;
    }

    /// <summary>
    /// Adopt an operator-initiated connection under the session id and start draining
    /// its output. If the id is already managed (or the manager is disposed) the new
    /// connection is disposed instead (no duplicate adoption).
    /// <paramref name="reapWhenIdle"/> opts the session into the idle reaper: true for the
    /// browser's node command consoles (a lost browser must not leak a command service),
    /// false (the default) for adopted AX.25 connect-outs, which are real links the operator
    /// owns. See the type remarks.
    /// </summary>
    public void Open(string id, INodeConnection connection, bool reapWhenIdle = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(connection);

        var session = new ConsoleSession(connection, reapWhenIdle, clock.GetUtcNow());
        if (Volatile.Read(ref disposed) != 0 || !sessions.TryAdd(id, session))
        {
            DisposeInBackground(connection);
            return;
        }
        session.StartPump(() => OnPeerGone(id), logger);
        LogOpened(id, connection.PeerId);
    }

    /// <summary>True if an interactive console is managed for this session id.</summary>
    public bool IsManaged(string id) => sessions.ContainsKey(id);

    /// <summary>Send typed input to the peer (UTF-8 as supplied — the caller decides
    /// any line terminator). No-op if the id is not managed.</summary>
    public async ValueTask WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        if (sessions.TryGetValue(id, out var session))
        {
            // Typed input is activity: it holds the idle reaper off even with no SSE
            // subscriber attached (a client that posts input without streaming).
            session.MarkActive(clock.GetUtcNow());
            await session.Connection.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Subscribe to a managed session's output: <paramref name="backlog"/> is the recent
    /// tail (so the browser has context), and the returned reader yields each new output
    /// chunk as it arrives. The <see cref="IDisposable"/> unsubscribes. Returns null (and
    /// an empty backlog) if the id isn't managed.
    /// </summary>
    public IDisposable? Subscribe(string id, out string backlog, out ChannelReader<string>? reader)
    {
        backlog = string.Empty;
        reader = null;
        return sessions.TryGetValue(id, out var session)
            ? session.Subscribe(clock, out backlog, out reader)
            : null;
    }

    /// <summary>
    /// Close every reapable session that has had no subscriber and no input for the idle
    /// timeout. Driven by the <see cref="TimeProvider"/> timer; public so a test (or a future
    /// admin endpoint) can sweep deterministically instead of racing the timer.
    /// </summary>
    public void ReapIdle()
    {
        if (idleTimeout <= TimeSpan.Zero || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var now = clock.GetUtcNow();
        foreach (var (id, session) in sessions)
        {
            if (!session.IsIdle(now, idleTimeout))
            {
                continue;
            }
            if (sessions.TryRemove(id, out var removed))
            {
                DisposeInBackground(removed);
                LogReaped(id, (long)idleTimeout.TotalMinutes);
            }
        }
    }

    /// <summary>Close a managed session: stop its pump and dispose the connection (which
    /// posts the DISC). No-op if not managed.</summary>
    public async ValueTask CloseAsync(string id)
    {
        if (sessions.TryRemove(id, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            LogClosed(id);
        }
    }

    private void OnPeerGone(string id)
    {
        // The peer dropped the link (ReadAsync returned empty). Remove + dispose so the
        // connection's resources are released and subscribers see the stream end.
        if (sessions.TryRemove(id, out var session))
        {
            DisposeInBackground(session);
            LogPeerGone(id);
        }
    }

    // Fire-and-forget dispose of an IAsyncDisposable from a synchronous path (Open's
    // duplicate-adoption reject, the peer-gone callback running in the pump's finally).
    // Wrapped in a Task (not a bare discarded ValueTask — CA2012) and swallows faults.
    private static void DisposeInBackground(IAsyncDisposable resource)
    {
        _ = Quietly(resource);
        static async Task Quietly(IAsyncDisposable r)
        {
            try { await r.DisposeAsync().ConfigureAwait(false); }
            catch { /* best-effort teardown */ }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        if (reaper is not null)
        {
            await reaper.DisposeAsync().ConfigureAwait(false);
        }
        foreach (var id in sessions.Keys.ToArray())
        {
            if (sessions.TryRemove(id, out var session))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sysop console: adopted connect-out session {Id} to {Peer}.")]
    private partial void LogOpened(string id, string peer);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sysop console: closed session {Id}.")]
    private partial void LogClosed(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sysop console: session {Id} ended (peer went away).")]
    private partial void LogPeerGone(string id);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Sysop console: reaped idle session {Id} (no subscriber and no input for {Minutes} min).")]
    private partial void LogReaped(string id, long minutes);

    // One adopted connection: its read pump, bounded backlog, and SSE subscribers.
    private sealed class ConsoleSession : IAsyncDisposable
    {
        // High-perf logging (CA1848): the pump runs in this nested type, which can't reach
        // the outer LoggerMessage partials, so define the one message it needs here.
        private static readonly Action<ILogger, Exception?> LogPumpFault =
            LoggerMessage.Define(LogLevel.Warning, new EventId(1, "SysopConsolePumpFault"),
                "Sysop console read pump faulted.");

        public INodeConnection Connection { get; }

        /// <summary>Whether the idle reaper may close this session (see the type remarks).</summary>
        public bool ReapWhenIdle { get; }

        private readonly StringBuilder backlog = new();
        private readonly object backlogGate = new();
        private readonly ConcurrentDictionary<Guid, ChannelWriter<string>> subscribers = new();
        private readonly CancellationTokenSource cts = new();
        private Task? pump;
        private int disposed;

        // Last activity as UTC ticks (Interlocked so the reaper thread never reads a torn
        // value): the later of "adopted", "input typed" and "last subscriber left".
        private long lastActivityTicks;

        public ConsoleSession(INodeConnection connection, bool reapWhenIdle, DateTimeOffset opened)
        {
            Connection = connection;
            ReapWhenIdle = reapWhenIdle;
            lastActivityTicks = opened.UtcTicks;
        }

        /// <summary>Stamp activity (typed input, or a subscriber arriving/leaving).</summary>
        public void MarkActive(DateTimeOffset now) => Interlocked.Exchange(ref lastActivityTicks, now.UtcTicks);

        /// <summary>
        /// True when this session may be reaped: opted in, nobody watching, and no activity for
        /// <paramref name="timeout"/>. An attached subscriber is activity in itself, so a quiet
        /// console someone is watching never goes idle.
        /// </summary>
        public bool IsIdle(DateTimeOffset now, TimeSpan timeout)
        {
            if (!ReapWhenIdle || !subscribers.IsEmpty)
            {
                return false;
            }
            var last = new DateTimeOffset(Interlocked.Read(ref lastActivityTicks), TimeSpan.Zero);
            return now - last >= timeout;
        }

        public void StartPump(Action onPeerGone, ILogger logger)
            => pump = Task.Run(() => PumpAsync(onPeerGone, logger));

        private async Task PumpAsync(Action onPeerGone, ILogger logger)
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var chunk = await Connection.ReadAsync(cts.Token).ConfigureAwait(false);
                    if (chunk.IsEmpty)
                    {
                        break;   // far end gone
                    }
                    var text = Encoding.UTF8.GetString(chunk.Span);
                    Append(text);
                    Broadcast(text);
                }
            }
            catch (OperationCanceledException)
            {
                // CloseAsync / dispose — normal teardown.
            }
            catch (Exception ex)
            {
                LogPumpFault(logger, ex);
            }
            finally
            {
                CompleteSubscribers();
                if (!cts.IsCancellationRequested)
                {
                    onPeerGone();   // peer-initiated end (not our CloseAsync)
                }
            }
        }

        private void Append(string text)
        {
            lock (backlogGate)
            {
                backlog.Append(text);
                if (backlog.Length > BacklogCap)
                {
                    backlog.Remove(0, backlog.Length - BacklogCap);
                }
            }
        }

        private void Broadcast(string text)
        {
            foreach (var writer in subscribers.Values)
            {
                writer.TryWrite(text);
            }
        }

        public IDisposable Subscribe(TimeProvider clock, out string backlogSnapshot, out ChannelReader<string>? reader)
        {
            lock (backlogGate)
            {
                backlogSnapshot = backlog.ToString();
            }
            var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
            var id = Guid.NewGuid();
            subscribers[id] = channel.Writer;
            reader = channel.Reader;
            // Subscribing is activity, and so is unsubscribing: the idle countdown starts when
            // the LAST subscriber goes away, not when the session was opened.
            MarkActive(clock.GetUtcNow());
            return new Unsubscriber(this, id, channel.Writer, clock);
        }

        private void CompleteSubscribers()
        {
            foreach (var writer in subscribers.Values)
            {
                writer.TryComplete();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);   // posts the DISC
            }
            catch
            {
                // Best-effort teardown.
            }
            CompleteSubscribers();
            cts.Dispose();
        }

        private sealed class Unsubscriber(ConsoleSession owner, Guid id, ChannelWriter<string> writer, TimeProvider clock)
            : IDisposable
        {
            private int gone;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref gone, 1) != 0)
                {
                    return;
                }
                owner.subscribers.TryRemove(id, out _);
                writer.TryComplete();
                // Restart the idle countdown from the moment the watcher left.
                owner.MarkActive(clock.GetUtcNow());
            }
        }
    }
}
