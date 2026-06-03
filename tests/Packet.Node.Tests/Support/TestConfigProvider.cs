using Packet.Node.Core.Configuration;

namespace Packet.Node.Tests.Support;

/// <summary>
/// An in-memory <see cref="IConfigProvider"/> for the host / supervisor tests:
/// set <see cref="Current"/> by calling <see cref="Apply"/>, which swaps the
/// config and raises <see cref="OnChange"/> — exactly the seam a future
/// SqliteConfigProvider's web-edit path would drive. Deterministic, no files, no
/// watcher.
/// </summary>
public sealed class TestConfigProvider : IConfigProvider
{
    private readonly List<Action<NodeConfig>> listeners = new();
    private readonly object gate = new();
    private NodeConfig current;

    public TestConfigProvider(NodeConfig initial) => current = initial;

    public NodeConfig Current { get { lock (gate) return current; } }

    public IDisposable OnChange(Action<NodeConfig> listener)
    {
        lock (gate) listeners.Add(listener);
        return new Unsub(this, listener);
    }

    /// <summary>Swap the current config and notify subscribers — the test's way of
    /// editing the YAML live.</summary>
    public void Apply(NodeConfig next)
    {
        Action<NodeConfig>[] snapshot;
        lock (gate)
        {
            current = next;
            snapshot = listeners.ToArray();
        }
        foreach (var l in snapshot) l(next);
    }

    private sealed class Unsub(TestConfigProvider owner, Action<NodeConfig> listener) : IDisposable
    {
        public void Dispose() { lock (owner.gate) owner.listeners.Remove(listener); }
    }
}
