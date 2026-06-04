using System.Threading.Channels;
using Packet.Ax25;
using Packet.Ax25.Session;

namespace Packet.Node.Core.Console;

/// <summary>
/// Wraps an <see cref="Ax25Session"/> (plus its owning <see cref="Ax25Listener"/>)
/// as an <see cref="INodeConnection"/>: inbound <c>DL-DATA indication</c>s become
/// readable bytes, <see cref="WriteAsync"/> goes out through the listener's
/// segmentation-aware <see cref="Ax25Listener.SendData"/>, and a disconnect
/// (indication or confirm) completes the connection. This is the over-the-air
/// service path — the bridge between the AX.25 engine and the transport-agnostic
/// console.
/// </summary>
/// <remarks>
/// The adapter subscribes to the session's <see cref="Ax25Session.DataLinkSignalEmitted"/>
/// stream (the same seam axcall's <c>SessionRelay</c> uses). It must be
/// constructed and subscribed before data flows — for inbound sessions the
/// listener's <c>ConfigureSession</c> hook is the right place; for outbound, the
/// session returned by <see cref="Ax25Listener.ConnectAsync"/> is already
/// connected, so any data arriving between connect and subscribe is a non-issue
/// in slice 1 (the remote waits for our banner before sending).
/// </remarks>
public sealed class Ax25NodeConnection : INodeConnection
{
    private readonly Ax25Listener listener;
    private readonly Ax25Session session;
    private readonly Channel<ReadOnlyMemory<byte>> inbound =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly TaskCompletionSource completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int disposed;

    public Ax25NodeConnection(Ax25Listener listener, Ax25Session session)
    {
        this.listener = listener ?? throw new ArgumentNullException(nameof(listener));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        session.DataLinkSignalEmitted += OnSignal;
    }

    /// <inheritdoc/>
    public string PeerId => session.Context.Remote.ToString();

    /// <inheritdoc/>
    public NodeTransportKind TransportKind => NodeTransportKind.Ax25;

    /// <inheritdoc/>
    public Task Completion => completion.Task;

    /// <summary>The wrapped session — exposed so the AX.25 adapter source
    /// (listener wiring) can correlate, and for tests.</summary>
    public Ax25Session Session => session;

    private void OnSignal(object? sender, DataLinkSignal sig)
    {
        switch (sig)
        {
            case DataLinkDataIndication di:
                // The console carries node-text data (PID 0xF0 / no-Layer-3). A
                // session may ALSO carry NET/ROM (PID 0xCF interlink datagrams),
                // which the NetRomService taps separately — those must not leak into
                // the console as garbage text, so filter them out here.
                if (di.Pid != Ax25Frame.PidNetRom)
                {
                    inbound.Writer.TryWrite(di.Info);
                }
                break;
            case DataLinkDisconnectIndication:
            case DataLinkDisconnectConfirm:
                Complete();
                break;
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
                inbound.Reader.TryRead(out var chunk))
            {
                return chunk;
            }
        }
        catch (ChannelClosedException)
        {
            // disconnected — fall through to EOF
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    /// <inheritdoc/>
    public ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return ValueTask.CompletedTask;
        }
        // Segmentation-aware send through the listener. SendData throws if the
        // session is no longer owned by the listener (evicted / torn down) — treat
        // that as a closed connection rather than letting it escape the console.
        try
        {
            listener.SendData(session, bytes);
        }
        catch (ArgumentException)
        {
            Complete();
        }
        catch (ObjectDisposedException)
        {
            Complete();
        }
        return ValueTask.CompletedTask;
    }

    private void Complete()
    {
        completion.TrySetResult();
        inbound.Writer.TryComplete();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        session.DataLinkSignalEmitted -= OnSignal;

        // If still connected, ask the link to disconnect cleanly. The session's
        // disconnect confirm will fire Complete via the (now-removed) handler? No
        // — we unsubscribed, so complete locally.
        try
        {
            if (session.CurrentState is "Connected" or "TimerRecovery" or "AwaitingConnection" or "AwaitingV22Connection")
            {
                session.PostEvent(new DlDisconnectRequest());
            }
        }
        catch
        {
            // Best-effort teardown; never throw from dispose.
        }

        Complete();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
