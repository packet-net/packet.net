using System.Net.Sockets;

namespace Packet.Kiss;

/// <summary>
/// A simple TCP client that speaks KISS to a peer (typically a TNC or a
/// node like LinBPQ that exposes a KISS-over-TCP listener). Handles framing
/// in both directions so callers deal in <see cref="KissFrame"/>s, not
/// escape sequences.
/// </summary>
public sealed class KissTcpClient : IKissModem, IDisposable, IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;
    private readonly KissDecoder decoder = new();
    private readonly byte[] readBuffer = new byte[4096];

    private KissTcpClient(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    /// <summary>
    /// Connect to a KISS-over-TCP listener at <paramref name="host"/>:<paramref name="port"/>.
    /// </summary>
    public static async Task<KissTcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        return new KissTcpClient(tcp);
    }

    /// <summary>
    /// Send a KISS frame to the peer.
    /// </summary>
    public async Task SendAsync(byte port, KissCommand command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var encoded = KissEncoder.Encode(port, command, payload.Span);
        await stream.WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Read available bytes from the socket and return any KISS frames that
    /// have now completed. Returns an empty list if the socket has data
    /// pending but no frame finished yet — callers should loop.
    /// </summary>
    public async Task<IReadOnlyList<KissFrame>> ReadAvailableAsync(CancellationToken cancellationToken = default)
    {
        int bytesRead = await stream.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
        if (bytesRead == 0)
        {
            throw new IOException("KISS-TCP peer closed the connection");
        }
        return decoder.Push(readBuffer.AsSpan(0, bytesRead));
    }

    /// <summary>
    /// Read until at least one frame has been received, or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<IReadOnlyList<KissFrame>> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var frames = await ReadAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (frames.Count > 0)
            {
                return frames;
            }
        }
    }

    /// <summary>
    /// <see cref="IKissModem"/> shape: write a KISS-Data frame on
    /// port 0. Delegates to <see cref="SendAsync"/>.
    /// </summary>
    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
        => SendAsync(port: 0, KissCommand.Data, ax25Bytes, cancellationToken);

    /// <summary>
    /// <see cref="IKissModem"/> shape: async stream of every inbound
    /// KISS frame until the socket closes or the token fires. Loops
    /// internally over <see cref="ReceiveAsync"/>.
    /// </summary>
    public async IAsyncEnumerable<KissFrame> ReadFramesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<KissFrame> frames;
            try { frames = await ReceiveAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            catch (IOException) { yield break; /* peer closed */ }
            foreach (var f in frames) yield return f;
        }
    }

    Task<AckModeReceipt> IKissModem.SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes, TimeSpan? timeout, ushort? sequenceTag, CancellationToken cancellationToken)
        => throw new NotSupportedException("KissTcpClient does not implement KISS ACKMODE; use SendFrameAsync for fire-and-forget transmission.");
    Task IKissModem.SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken)        => SendAsync(0, KissCommand.TxDelay,    new[] { tenMsUnits }, cancellationToken);
    Task IKissModem.SetPersistenceAsync(byte value, CancellationToken cancellationToken)         => SendAsync(0, KissCommand.Persistence, new[] { value },      cancellationToken);
    Task IKissModem.SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken)       => SendAsync(0, KissCommand.SlotTime,   new[] { tenMsUnits }, cancellationToken);
    Task IKissModem.SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken)         => SendAsync(0, KissCommand.TxTail,     new[] { tenMsUnits }, cancellationToken);

    /// <inheritdoc/>
    public void Dispose()
    {
        stream.Dispose();
        tcp.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync().ConfigureAwait(false);
        tcp.Dispose();
    }
}
