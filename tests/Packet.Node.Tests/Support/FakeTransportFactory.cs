using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.Transports;

namespace Packet.Node.Tests.Support;

/// <summary>
/// A test <see cref="ITransportFactory"/> that hands out pre-supplied in-memory
/// transports instead of opening real hardware/sockets — so the
/// <see cref="Packet.Node.Core.Hosting.PortSupervisor"/> can bring up real
/// <c>Ax25Listener</c>s over the in-memory radio. Transports are registered per port
/// id; each port-id can be given a sequence of transports so a restart (tear down +
/// bring up) gets a fresh one.
/// </summary>
public sealed class FakeTransportFactory : ITransportFactory
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<IAx25Transport>> byEndpoint = new();
    private readonly ConcurrentDictionary<string, Exception> faults = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> stalls = new();
    private readonly HashSet<string> exclusive = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> leased = new(StringComparer.Ordinal);
    private int stalledOpens;

    /// <summary>Supply the transport(s) an endpoint will receive, in order.
    /// The key is the transport's <c>DescribeEndpoint()</c> (e.g.
    /// <c>kiss-tcp:mem:1</c>), since the supervisor only passes the
    /// <see cref="TransportConfig"/>.</summary>
    public FakeTransportFactory Provide(string endpoint, params IAx25Transport[] transports)
    {
        var q = byEndpoint.GetOrAdd(endpoint, _ => new ConcurrentQueue<IAx25Transport>());
        foreach (var m in transports)
        {
            q.Enqueue(m);
        }

        return this;
    }

    /// <summary>Make a transport endpoint fault on bring-up (models a device that
    /// won't open), to test per-port fault isolation.</summary>
    public FakeTransportFactory Fault(string endpoint, Exception? ex = null)
    {
        faults[endpoint] = ex ?? new IOException($"fake transport for '{endpoint}' refused to open");
        return this;
    }

    /// <summary>Stop faulting an endpoint (models the head-end coming back up — the bring-up
    /// retry loop's recovery case, #576). Pair with <see cref="Provide"/> for the next open.</summary>
    public FakeTransportFactory ClearFault(string endpoint)
    {
        faults.TryRemove(endpoint, out _);
        return this;
    }

    /// <summary>
    /// Make an endpoint's open BLOCK until <see cref="Release"/> (or the caller's token is
    /// cancelled) - a blackholing head-end: a DROP firewall or a dead Pi, where the dial
    /// neither connects nor refuses. The point is what the rest of the node can still do while
    /// one port is stuck in there (#722).
    /// </summary>
    public FakeTransportFactory Stall(string endpoint)
    {
        stalls.GetOrAdd(endpoint, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return this;
    }

    /// <summary>Let a stalled endpoint's pending (and future) opens proceed.</summary>
    public FakeTransportFactory Release(string endpoint)
    {
        if (stalls.TryRemove(endpoint, out var gate))
        {
            gate.TrySetResult();
        }

        return this;
    }

    /// <summary>Whether an open is parked in <see cref="Stall"/> right now.</summary>
    public bool IsStalling(string endpoint) => Volatile.Read(ref stalledOpens) > 0 && stalls.ContainsKey(endpoint);

    /// <summary>
    /// Model a device only one port can hold at a time (a serial device: the second opener gets
    /// "already open"). The lease is released when the handed-out transport is disposed, so a
    /// port that has been torn down frees its device - which is what makes a two-phase apply
    /// (all teardowns, then all bring-ups) the difference between a working device handover
    /// between two ports and a permanently dead port (#722).
    /// </summary>
    public FakeTransportFactory Exclusive(string endpoint)
    {
        exclusive.Add(endpoint);
        return this;
    }

    /// <summary>The supervisor passes only the TransportConfig, so we key on its
    /// endpoint description which the tests make unique per port.</summary>
    public async Task<IAx25Transport> CreateAsync(
        TransportConfig transport,
        TimeProvider? timeProvider = null,
        HeadEndDeviceResolver? headEndResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var key = transport.DescribeEndpoint();
        if (stalls.TryGetValue(key, out var gate))
        {
            Interlocked.Increment(ref stalledOpens);
            try
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref stalledOpens);
            }
        }
        if (faults.TryGetValue(key, out var fault))
        {
            throw fault;
        }
        if (byEndpoint.TryGetValue(key, out var q) && q.TryDequeue(out var provided))
        {
            if (!exclusive.Contains(key))
            {
                return provided;
            }
            if (!leased.TryAdd(key, 0))
            {
                throw new IOException($"device '{key}' is already open (another port holds it)");
            }
            return new LeasedTransport(provided, () => leased.TryRemove(key, out _));
        }
        throw new InvalidOperationException(
            $"FakeTransportFactory has no transport registered for endpoint '{key}'. " +
            "Register it with Provide(endpoint, transport).");
    }
}

/// <summary>
/// A handed-out transport that holds an exclusive device lease (see
/// <see cref="FakeTransportFactory.Exclusive"/>) and releases it on disposal.
/// </summary>
internal sealed class LeasedTransport(IAx25Transport inner, Action release) : IAx25Transport
{
    private int released;

    /// <inheritdoc/>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        => inner.SendAsync(ax25, cancellationToken);

    /// <inheritdoc/>
    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default)
        => inner.ReceiveAsync(cancellationToken);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            release();
        }

        await inner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A transport whose receive pump can be made to DIE mid-run, the way a real one does when its
/// serial device is unplugged or its socket half-opens: the inbound enumerator faults, the
/// <c>Ax25Listener</c> catches it and marks itself not-running, and the port is dead on the air
/// with nothing in the old code observing it (packet-net/packet.net#722).
/// </summary>
public sealed class KillableTransport(IAx25Transport inner) : IAx25Transport
{
    private readonly Channel<Ax25InboundFrame> rx =
        Channel.CreateUnbounded<Ax25InboundFrame>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private int pumping;

    /// <summary>Fault this transport's inbound stream. The listener's pump sees the exception,
    /// logs it, and marks itself not-running.</summary>
    public void Kill(Exception? ex = null)
        => rx.Writer.TryComplete(ex ?? new IOException("the fake transport's receive pump died"));

    /// <inheritdoc/>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        => inner.SendAsync(ax25, cancellationToken);

    /// <inheritdoc/>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref pumping, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var f in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
                    {
                        rx.Writer.TryWrite(f);
                    }
                    rx.Writer.TryComplete();
                }
                catch (Exception ex)
                {
                    rx.Writer.TryComplete(ex);
                }
            }, CancellationToken.None);
        }

        while (await rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (rx.Reader.TryRead(out var frame))
            {
                yield return frame;
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        rx.Writer.TryComplete();
        return inner.DisposeAsync();
    }
}
