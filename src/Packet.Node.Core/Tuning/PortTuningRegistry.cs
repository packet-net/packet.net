using System.Collections.Concurrent;

namespace Packet.Node.Core.Tuning;

/// <summary>
/// The set of live <see cref="IPortTuningSession"/>s (deviation tuning, TXDELAY
/// minimisation…), keyed by port id — at most one per port, whatever the flavour. Split
/// out from <see cref="PortTuningService"/> so the "one session per port" and "stop-all-on-shutdown"
/// bookkeeping is unit-testable without a live node host. A port stays claimed (a second
/// <see cref="TryAdd"/> fails) until its session removes itself, which the service does only after
/// the port has been restored.
/// </summary>
public sealed class PortTuningRegistry : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IPortTuningSession> sessions =
        new(StringComparer.Ordinal);
    private int disposed;

    /// <summary>The live session on a port, or <c>null</c> when none is active.</summary>
    public IPortTuningSession? Get(string portId) =>
        sessions.TryGetValue(portId, out var session) ? session : null;

    /// <summary>Whether a session is currently claimed on a port.</summary>
    public bool IsActive(string portId) => sessions.ContainsKey(portId);

    /// <summary>
    /// Claim the port for <paramref name="session"/>. Returns <c>false</c> (the caller maps to 409)
    /// when a session already holds the port.
    /// </summary>
    public bool TryAdd(IPortTuningSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return sessions.TryAdd(session.PortId, session);
    }

    /// <summary>
    /// Release the port, but only if it still maps to <paramref name="session"/> (never evicts a
    /// newer session that replaced it).
    /// </summary>
    public bool Remove(IPortTuningSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return sessions.TryRemove(new KeyValuePair<string, IPortTuningSession>(session.PortId, session));
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        foreach (var session in sessions.Values.ToArray())
        {
            // StopAsync drives the session to a clean stop and restores its port; a session removes
            // itself from this registry as part of that.
            await session.StopAsync().ConfigureAwait(false);
        }
    }
}
