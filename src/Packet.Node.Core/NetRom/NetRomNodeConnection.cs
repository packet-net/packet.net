using System.Threading.Channels;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// Wraps a NET/ROM L4 <see cref="NetRomCircuit"/> as an <see cref="INodeConnection"/>
/// so the transport-agnostic node console runs over a network-routed circuit
/// exactly as it does over an AX.25 session or a telnet socket: the circuit's
/// reassembled <c>DataReceived</c> frames become readable bytes,
/// <see cref="WriteAsync"/> hands data to <see cref="NetRomCircuit.Send"/>, and a
/// circuit close completes the connection.
/// </summary>
/// <remarks>
/// Used both ways: for an <em>inbound</em> circuit (a user routed to us via NET/ROM
/// reaching the node prompt), and as the far side of <c>connect &lt;alias&gt;</c>
/// (the outbound circuit the console relays the dialling user against).
/// </remarks>
public sealed class NetRomNodeConnection : INodeConnection
{
    private readonly NetRomCircuit circuit;
    private readonly Channel<ReadOnlyMemory<byte>> inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int disposed;

    public NetRomNodeConnection(NetRomCircuit circuit, Callsign peer)
    {
        this.circuit = circuit ?? throw new ArgumentNullException(nameof(circuit));
        PeerId = peer.ToString();
        circuit.DataReceived += OnData;
        circuit.Closed += OnClosed;
    }

    /// <inheritdoc/>
    public string PeerId { get; }

    /// <inheritdoc/>
    public NodeTransportKind TransportKind => NodeTransportKind.NetRom;

    /// <inheritdoc/>
    public Task Completion => completion.Task;

    private void OnData(ReadOnlyMemory<byte> data) => inbound.Writer.TryWrite(data);

    private void OnClosed(NetRomCircuitCloseReason reason) => Complete();

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
                inbound.Reader.TryRead(out var chunk))
            {
                // Tell the circuit a frame has been consumed so it can release choke
                // if it had asserted backpressure.
                circuit.OnDeliveryDrained();
                return chunk;
            }
        }
        catch (ChannelClosedException)
        {
            // circuit closed - fall through to EOF
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            circuit.Send(bytes);
        }
        return ValueTask.CompletedTask;
    }

    private void Complete()
    {
        completion.TrySetResult();
        inbound.Writer.TryComplete();
    }

    /// <summary>
    /// The ordinary close (the relay finished, the console or an app ended the session): stop
    /// taking writes, end the local side at once, and close the circuit once everything already
    /// written has been acknowledged by the far end (<see cref="NetRomCircuit.DisconnectWhenDrained"/>,
    /// packet.net#850). Does not wait for the far end.
    /// </summary>
    public ValueTask DisposeAsync() => Close(immediate: false);

    /// <summary>Close the circuit at once, abandoning anything not yet acknowledged: a sysop
    /// kill and shutdown.</summary>
    public ValueTask AbortAsync()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            // Already closing gracefully: cut the wait short.
            circuit.Disconnect();
            return ValueTask.CompletedTask;
        }
        return Close(immediate: true);
    }

    private ValueTask Close(bool immediate)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }
        circuit.DataReceived -= OnData;
        circuit.Closed -= OnClosed;
        if (immediate)
        {
            circuit.Disconnect();
        }
        else
        {
            circuit.DisconnectWhenDrained();
        }
        Complete();
        return ValueTask.CompletedTask;
    }
}
