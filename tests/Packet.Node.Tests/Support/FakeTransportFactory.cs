using System.Collections.Concurrent;
using Packet.Kiss;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A test <see cref="ITransportFactory"/> that hands out pre-supplied in-memory
/// modems instead of opening real hardware/sockets — so the
/// <see cref="Packet.Node.Core.Hosting.PortSupervisor"/> can bring up real
/// <c>Ax25Listener</c>s over the in-memory radio. Modems are registered per port
/// id; each port-id can be given a sequence of modems so a restart (tear down +
/// bring up) gets a fresh one.
/// </summary>
public sealed class FakeTransportFactory : ITransportFactory
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<IKissModem>> byEndpoint = new();
    private readonly ConcurrentDictionary<string, Exception> faults = new();

    /// <summary>Supply the modem(s) a transport endpoint will receive, in order.
    /// The key is the transport's <c>DescribeEndpoint()</c> (e.g.
    /// <c>kiss-tcp:mem:1</c>), since the supervisor only passes the
    /// <see cref="TransportConfig"/>.</summary>
    public FakeTransportFactory Provide(string endpoint, params IKissModem[] modems)
    {
        var q = byEndpoint.GetOrAdd(endpoint, _ => new ConcurrentQueue<IKissModem>());
        foreach (var m in modems) q.Enqueue(m);
        return this;
    }

    /// <summary>Make a transport endpoint fault on bring-up (models a device that
    /// won't open), to test per-port fault isolation.</summary>
    public FakeTransportFactory Fault(string endpoint, Exception? ex = null)
    {
        faults[endpoint] = ex ?? new IOException($"fake transport for '{endpoint}' refused to open");
        return this;
    }

    /// <summary>The supervisor passes only the TransportConfig, so we key on its
    /// endpoint description which the tests make unique per port.</summary>
    public Task<IKissModem> CreateAsync(
        TransportConfig transport,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var key = transport.DescribeEndpoint();
        if (faults.TryGetValue(key, out var fault))
        {
            throw fault;
        }
        if (byEndpoint.TryGetValue(key, out var q) && q.TryDequeue(out var modem))
        {
            return Task.FromResult(modem);
        }
        throw new InvalidOperationException(
            $"FakeTransportFactory has no modem registered for endpoint '{key}'. " +
            "Register it with Provide(endpoint, modem).");
    }
}
