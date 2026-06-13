using System.Threading.Channels;

namespace Packet.Agw;

/// <summary>
/// Low-level frame I/O over a duplex byte stream. Reads frames from
/// the underlying stream into an async queue; serialises writes so
/// concurrent callers can't interleave bytes mid-header.
/// </summary>
/// <remarks>
/// <para>
/// Decoupled from <see cref="System.Net.Sockets.TcpClient"/> so that
/// tests can drive it over a <see cref="System.IO.Pipelines.Pipe"/>
/// or paired <see cref="MemoryStream"/>s without spinning up real
/// sockets. The intended consumer is <see cref="AgwClient"/>, but the
/// type is public so callers wanting raw frame access can use it
/// directly.
/// </para>
/// <para>
/// Inbound frames land in a bounded <see cref="Channel{T}"/>. The
/// read loop is started by the constructor and runs until
/// <see cref="DisposeAsync"/>; it tolerates short reads (typical of
/// network streams) by buffering header bytes until a full header is
/// available, then reading exactly the body length the header
/// advertises.
/// </para>
/// </remarks>
#pragma warning disable CA1711 // The name "AgwFrameStream" describes what it does — wraps a byte stream and emits AGW frames; the "Stream" suffix is the most natural label even though it doesn't subclass System.IO.Stream.
public sealed class AgwFrameStream : IAsyncDisposable
#pragma warning restore CA1711
{
    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly Channel<AgwFrame> inbound;
    private readonly CancellationTokenSource readCts;
    private readonly Task readLoop;
    private readonly SemaphoreSlim writeLock = new(1, 1);

    /// <summary>
    /// Wrap <paramref name="stream"/> and start a background read
    /// loop. Set <paramref name="ownsStream"/> if disposing this
    /// object should also dispose the underlying stream (typical for
    /// a stream we opened ourselves, e.g. a TcpClient.GetStream).
    /// </summary>
    /// <param name="inboundQueueCapacity">
    /// Bounded inbound-queue size. Defaults to 64 — enough to absorb a
    /// burst of frames while the consumer is mid-handler. A backed-up
    /// queue blocks the read loop, which in turn applies TCP back-
    /// pressure on the server.
    /// </param>
    public AgwFrameStream(Stream stream, bool ownsStream = true, int inboundQueueCapacity = 64)
    {
        ArgumentNullException.ThrowIfNull(stream);
        this.stream = stream;
        this.ownsStream = ownsStream;
        this.inbound = Channel.CreateBounded<AgwFrame>(new BoundedChannelOptions(inboundQueueCapacity)
        {
            SingleReader = false,   // Multiple AgwClient methods may concurrently await different frame kinds
            SingleWriter = true,    // Only the read loop writes
            FullMode     = BoundedChannelFullMode.Wait,
        });
        this.readCts = new CancellationTokenSource();
        this.readLoop = Task.Run(() => RunReadLoopAsync(readCts.Token));
    }

    /// <summary>Reader side of the inbound-frame queue.</summary>
    public ChannelReader<AgwFrame> Inbound => inbound.Reader;

    /// <summary>True if the read loop has terminated (stream EOF, error, or disposed).</summary>
    public bool IsClosed => readLoop.IsCompleted;

    /// <summary>
    /// Write one frame to the underlying stream. Concurrent calls are
    /// serialised so the header + body of a frame aren't interleaved
    /// with another sender's bytes.
    /// </summary>
    public async ValueTask WriteAsync(AgwFrame frame, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = frame.ToBytes();
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task RunReadLoopAsync(CancellationToken ct)
    {
        // Allocate header buffer once. Body buffer grows as needed.
        var header = new byte[AgwFrame.HeaderSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, ct).ConfigureAwait(false))
                {
                    // EOF before any header byte → clean shutdown.
                    break;
                }

                if (!AgwFrame.TryReadDataLength(header, out int dataLen))
                {
                    // A full header is in hand (ReadExactAsync returned true), so "too
                    // short" is impossible: the advertised length is unusable (would
                    // overflow Int32). AGW has no frame delimiter to resync on, so a
                    // corrupt length desyncs the stream — surface it and stop.
                    throw new InvalidDataException(
                        "AGW frame advertises an unusable data length (overflows Int32); stream desynced.");
                }

                byte[] body = dataLen == 0
                    ? Array.Empty<byte>()
                    : new byte[dataLen];
                if (dataLen > 0)
                {
                    if (!await ReadExactAsync(body, ct).ConfigureAwait(false))
                    {
                        throw new EndOfStreamException(
                            $"AGW frame body truncated: header advertises {dataLen} bytes, stream ended early.");
                    }
                }

                // Re-parse via the standard path so any future header-
                // field semantics are honoured by one code path only.
                var full = new byte[AgwFrame.HeaderSize + dataLen];
                header.AsSpan().CopyTo(full);
                body.AsSpan().CopyTo(full.AsSpan(AgwFrame.HeaderSize));
                var frame = AgwFrame.Parse(full, out _);

                await inbound.Writer.WriteAsync(frame, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during Dispose; let the channel close cleanly.
        }
        catch (Exception ex)
        {
            inbound.Writer.TryComplete(ex);
            return;
        }
        inbound.Writer.TryComplete();
    }

    /// <summary>
    /// Read exactly <paramref name="buffer"/>.Length bytes, blocking
    /// across multiple underlying-stream reads as necessary. Returns
    /// false if EOF happens on the FIRST read (i.e. before any byte
    /// is delivered) — that's how callers distinguish "stream closed
    /// cleanly between frames" from "stream closed mid-frame". A
    /// short read after the first byte throws via the caller.
    /// </summary>
    private async ValueTask<bool> ReadExactAsync(Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.Slice(totalRead), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (totalRead == 0) return false;
                throw new EndOfStreamException(
                    $"AGW stream ended mid-read: expected {buffer.Length} bytes, got {totalRead}.");
            }
            totalRead += n;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await readCts.CancelAsync().ConfigureAwait(false);
        try { await readLoop.ConfigureAwait(false); } catch { /* swallow — terminating */ }
        readCts.Dispose();
        writeLock.Dispose();
        if (ownsStream)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
