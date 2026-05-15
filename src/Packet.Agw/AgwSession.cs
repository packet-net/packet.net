using System.Threading.Channels;

namespace Packet.Agw;

/// <summary>
/// One AGW connected-mode session, exposed as a <see cref="Stream"/>.
/// Writes go out as 'D' (Data) frames; reads drain 'D' frames the
/// server has delivered. A 'd' (Disconnect) frame from the server
/// surfaces as EOF on the next read.
/// </summary>
/// <remarks>
/// <para>
/// Don't construct directly — call <see cref="AgwClient.OpenSessionAsync"/>,
/// which sends the SABM ('C') and awaits the connect-ack before
/// returning the session.
/// </para>
/// <para>
/// PID defaults to 0xF0 ("no layer 3"), which is what BPQ's node
/// prompt and most application-layer text protocols use. Override
/// per-write if you need a different PID (e.g. 0xCF for NET/ROM L3).
/// </para>
/// </remarks>
#pragma warning disable CA1710 // The name "AgwSession" is the conventional packet-radio term for one connected-mode endpoint. The fact that we expose it as a Stream is implementation detail; "AgwSessionStream" would be a worse name.
public sealed class AgwSession : Stream
#pragma warning restore CA1710
{
    private readonly AgwClient client;
    private readonly Channel<ReadOnlyMemory<byte>> incoming;
    private readonly TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private byte defaultPid = 0xF0;
    private ReadOnlyMemory<byte> readResidual = ReadOnlyMemory<byte>.Empty;
    private Exception? streamFault;

    internal AgwSession(AgwClient client, string from, string to, byte radioPort)
    {
        this.client = client;
        From = from;
        To = to;
        RadioPort = radioPort;
        incoming = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
    }

    /// <summary>Our callsign for this session.</summary>
    public string From { get; }
    /// <summary>Remote callsign for this session.</summary>
    public string To { get; }
    /// <summary>AGW radio-port number this session uses.</summary>
    public byte RadioPort { get; }

    /// <summary>Default PID used by <see cref="Stream.WriteAsync(byte[], int, int, CancellationToken)"/> overloads. 0xF0 ("no layer 3") by default.</summary>
    public byte DefaultPid
    {
        get => defaultPid;
        set => defaultPid = value;
    }

    /// <summary>Completes when the session has disconnected (server sent 'd' or stream faulted).</summary>
    public Task DisconnectedTask => disconnected.Task;

    // ─── Stream surface ────────────────────────────────────────────

    public override bool CanRead => true;
    public override bool CanWrite => !disconnected.Task.IsCompleted;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { /* no buffer — every Write goes out immediately */ }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;

        // Drain leftover bytes from a previous partial-frame read first.
        if (!readResidual.IsEmpty)
        {
            int take = Math.Min(readResidual.Length, buffer.Length);
            readResidual.Slice(0, take).CopyTo(buffer);
            readResidual = readResidual.Slice(take);
            return take;
        }

        try
        {
            ReadOnlyMemory<byte> chunk = await incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            int take = Math.Min(chunk.Length, buffer.Length);
            chunk.Slice(0, take).CopyTo(buffer);
            if (take < chunk.Length)
            {
                readResidual = chunk.Slice(take);
            }
            return take;
        }
        catch (ChannelClosedException)
        {
            if (streamFault is not null) throw streamFault;
            return 0;   // EOF — server sent 'd' (Disconnect).
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (disconnected.Task.IsCompleted)
        {
            if (streamFault is not null) throw streamFault;
            throw new InvalidOperationException("AGW session is disconnected.");
        }
        if (buffer.IsEmpty) return;

        // AGW servers cap per-frame data size at ~256 bytes by
        // convention (matches the typical PACLEN on the radio side).
        // Larger payloads get split into multiple D-frames so the
        // server doesn't reject or silently truncate.
        const int MaxChunk = 256;
        int offset = 0;
        while (offset < buffer.Length)
        {
            int take = Math.Min(MaxChunk, buffer.Length - offset);
            await client.WriteFrameAsync(new AgwFrame(
                Port: RadioPort,
                Kind: AgwCommandKind.Data,
                Pid: defaultPid,
                From: From,
                To: To,
                Data: buffer.Slice(offset, take)), cancellationToken).ConfigureAwait(false);
            offset += take;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => await WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Send the disconnect ('d') command. The session enters the
    /// disconnected state immediately; <see cref="DisconnectedTask"/>
    /// completes synchronously, and further writes throw.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        if (disconnected.Task.IsCompleted) return;
        try
        {
            await client.WriteFrameAsync(new AgwFrame(
                Port: RadioPort,
                Kind: AgwCommandKind.Disconnect,
                Pid: 0,
                From: From,
                To: To,
                Data: ReadOnlyMemory<byte>.Empty), ct).ConfigureAwait(false);
        }
        finally
        {
            // We complete locally rather than waiting for the server's
            // 'd' ack — the AGW spec doesn't guarantee a reply, and
            // production servers vary on this. The session is "done"
            // from our side the moment we sent the disconnect command.
            FinalizeDisconnect(streamFault: null);
        }
    }

    // ─── Internal frame-arrival hooks ──────────────────────────────

    internal void OnFrame(AgwFrame frame)
    {
        switch (frame.Kind)
        {
            case AgwCommandKind.Data:
                if (!frame.Data.IsEmpty)
                {
                    incoming.Writer.TryWrite(frame.Data);
                }
                break;
            case AgwCommandKind.Disconnect:
                FinalizeDisconnect(streamFault: null);
                break;
            default:
                // Other kinds (monitored frames, port-info, etc.) are
                // not session-relevant; the dispatch loop already
                // handled them at a higher level.
                break;
        }
    }

    internal void OnStreamFault(Exception ex)
    {
        FinalizeDisconnect(ex);
    }

    private void FinalizeDisconnect(Exception? streamFault)
    {
        this.streamFault = streamFault;
        incoming.Writer.TryComplete(streamFault);
        if (streamFault is null)
        {
            disconnected.TrySetResult();
        }
        else
        {
            disconnected.TrySetException(streamFault);
        }
        client.RemoveSession(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disconnected.Task.IsCompleted)
        {
            // Synchronous Dispose path: best-effort fire-and-forget
            // disconnect. Real callers should `await DisposeAsync`.
            try { DisconnectAsync().GetAwaiter().GetResult(); }
            catch { /* swallow — terminating */ }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!disconnected.Task.IsCompleted)
        {
            try { await DisconnectAsync().ConfigureAwait(false); }
            catch { /* swallow — terminating */ }
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
