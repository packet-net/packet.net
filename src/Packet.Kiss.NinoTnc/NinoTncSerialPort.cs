using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading.Channels;
using Packet.Kiss;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// A NinoTNC USB-CDC serial connection that speaks KISS and the NinoTNC-flavoured
/// SETHW command. Reads run on a background pump; writes are serialised through
/// an internal semaphore. Inbound KISS frames are surfaced through
/// <see cref="ReadFramesAsync"/> and <see cref="FrameReceived"/>; ACKMODE
/// transmit-completion echoes are correlated through
/// <see cref="SendFrameWithAckAsync"/>.
/// </summary>
public sealed class NinoTncSerialPort : IAsyncDisposable, IDisposable
{
    /// <summary>The NinoTNC's documented USB-serial baud rate.</summary>
    public const int DefaultBaudRate = 57600;

    private readonly SerialPort serial;
    private readonly KissDecoder decoder = new();
    private readonly Channel<KissFrame> inbound;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<AckModeReceipt>> pendingAcks = new();
    private readonly CancellationTokenSource pumpCts = new();

    private Task? readPump;
    private int ackSequenceCursor;
    private int disposed;

    private NinoTncSerialPort(SerialPort serial)
    {
        this.serial = serial;
        inbound = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
    }

    /// <summary>The port name the connection was opened on (e.g. "COM6" or "/dev/ttyACM0").</summary>
    public string PortName => serial.PortName;

    /// <summary>The KISS port nibble used for write helpers when none is specified.</summary>
    public byte KissPort { get; init; }

    /// <summary>
    /// Fired for every inbound KISS frame after framing/unescaping. Subscribers
    /// run on the read-pump task — keep handlers fast and non-blocking.
    /// Use <see cref="ReadFramesAsync"/> if you'd rather pull frames on your own task.
    /// </summary>
    public event EventHandler<KissFrame>? FrameReceived;

    /// <summary>
    /// Open the named serial port at 57 600 8N1 and start the background read pump.
    /// </summary>
    public static NinoTncSerialPort Open(string portName, int baudRate = DefaultBaudRate, byte kissPort = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(portName);
        var serial = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            // 100 ms slot for the pump loop. SerialPort.BaseStream.ReadAsync
            // does not honour cancellation on Windows, and SerialPort.DataReceived
            // is famously unreliable; the only pattern that survives both is a
            // foreground thread doing finite-timeout synchronous reads.
            ReadTimeout = 100,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
        serial.Open();
        var tnc = new NinoTncSerialPort(serial) { KissPort = kissPort };
        tnc.readPump = Task.Factory.StartNew(
            () => tnc.PumpReadsBlocking(tnc.pumpCts.Token),
            TaskCreationOptions.LongRunning);
        return tnc;
    }

    /// <summary>
    /// Asynchronously stream every inbound KISS frame until the connection is
    /// disposed or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public IAsyncEnumerable<KissFrame> ReadFramesAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Send a plain KISS data frame (command 0x00). Returns once the bytes
    /// have been handed to the underlying stream; transmission over-the-air
    /// has *not* happened yet. Use <see cref="SendFrameWithAckAsync"/> when
    /// you need to know when the modem has finished keying.
    /// </summary>
    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default) =>
        SendFrameAsync(KissPort, ax25Bytes, cancellationToken);

    /// <inheritdoc cref="SendFrameAsync(System.ReadOnlyMemory{byte},System.Threading.CancellationToken)"/>
    public async Task SendFrameAsync(byte port, ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        var encoded = KissEncoder.Encode(port, KissCommand.Data, ax25Bytes.Span);
        await WriteAsync(encoded, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Send an ACKMODE frame (command 0x0C) and await the TNC's TX-completion
    /// echo. If <paramref name="sequenceTag"/> is <c>null</c>, an internal
    /// counter assigns a unique value.
    /// </summary>
    /// <param name="ax25Bytes">AX.25 frame to transmit.</param>
    /// <param name="timeout">Maximum time to wait for the echo. Defaults to 30 s.</param>
    /// <param name="sequenceTag">Caller-supplied 16-bit tag, or <c>null</c> to auto-assign.</param>
    /// <param name="cancellationToken">Cancels the wait (does not un-queue the frame at the TNC).</param>
    public Task<AckModeReceipt> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes,
        TimeSpan? timeout = null,
        ushort? sequenceTag = null,
        CancellationToken cancellationToken = default) =>
        SendFrameWithAckAsync(KissPort, ax25Bytes, timeout, sequenceTag, cancellationToken);

    /// <inheritdoc cref="SendFrameWithAckAsync(System.ReadOnlyMemory{byte},System.Nullable{System.TimeSpan},System.Nullable{ushort},System.Threading.CancellationToken)"/>
    public async Task<AckModeReceipt> SendFrameWithAckAsync(
        byte port,
        ReadOnlyMemory<byte> ax25Bytes,
        TimeSpan? timeout = null,
        ushort? sequenceTag = null,
        CancellationToken cancellationToken = default)
    {
        ushort tag = sequenceTag ?? NextSequenceTag();
        var tcs = new TaskCompletionSource<AckModeReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingAcks.TryAdd(tag, tcs))
        {
            throw new InvalidOperationException($"sequence tag 0x{tag:X4} already has a pending ACK; pick a unique tag");
        }

        var wire = KissAckMode.BuildSendFrame(port, tag, ax25Bytes.Span);
        var queuedAt = DateTimeOffset.UtcNow;
        try
        {
            await WriteAsync(wire, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pendingAcks.TryRemove(tag, out _);
            throw;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(30));
        await using var registration = cts.Token.Register(() =>
        {
            if (pendingAcks.TryRemove(tag, out var pending))
            {
                pending.TrySetException(cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException($"NinoTNC did not echo ACKMODE tag 0x{tag:X4} within {timeout ?? TimeSpan.FromSeconds(30)}"));
            }
        }).ConfigureAwait(false);

        var receipt = await tcs.Task.ConfigureAwait(false);
        return receipt with { Queued = queuedAt };
    }

    /// <summary>
    /// Set the NinoTNC operating mode via KISS SETHW (command 0x06).
    /// </summary>
    /// <param name="mode">DIP-switch-equivalent mode 0–14, or 15 ("Set from KISS").</param>
    /// <param name="persistToFlash">
    /// When <c>false</c> (default), the +16 non-persist offset is applied so
    /// the change does not touch the TNC's flash. Use <c>true</c> only when
    /// the user wants the choice to survive a reboot.
    /// </param>
    public Task SetModeAsync(byte mode, bool persistToFlash = false, CancellationToken cancellationToken = default)
    {
        var frame = NinoTncSetHardware.BuildKissFrame(mode, persistToFlash, KissPort);
        return WriteAsync(frame, cancellationToken);
    }

    /// <summary>Send a KISS TXDELAY (0x01) command. Units are 10 ms.</summary>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.TxDelay, tenMsUnits, cancellationToken);

    /// <summary>Send a KISS PERSISTENCE (0x02) command (0–255).</summary>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.Persistence, value, cancellationToken);

    /// <summary>Send a KISS SLOTTIME (0x03) command. Units are 10 ms.</summary>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.SlotTime, tenMsUnits, cancellationToken);

    /// <summary>Send a KISS FULLDUPLEX (0x05) command.</summary>
    public Task SetFullDuplexAsync(bool fullDuplex, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.FullDuplex, fullDuplex ? (byte)1 : (byte)0, cancellationToken);

    private Task SendParameterAsync(KissCommand command, byte value, CancellationToken cancellationToken)
    {
        var frame = KissEncoder.Encode(KissPort, command, new[] { value });
        return WriteAsync(frame, cancellationToken);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Use the synchronous Write path. SerialPort.BaseStream.WriteAsync
            // is reportedly unreliable on some Windows configurations; the
            // synchronous path (which BaseStream.WriteAsync wraps via Task.Run
            // anyway under the hood for non-async-IO ports) matches what the
            // spike does and is what the hardware loop has been observed to
            // round-trip.
            serial.Write(bytes, 0, bytes.Length);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private void PumpReadsBlocking(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        Exception? terminal = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = serial.Read(buffer, 0, buffer.Length);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                if (read <= 0)
                {
                    continue;
                }
                foreach (var frame in decoder.Push(buffer.AsSpan(0, read)))
                {
                    DispatchFrame(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (Volatile.Read(ref disposed) != 0)
        {
            // SerialPort throws on Dispose() while a read is in flight; swallow it.
            terminal = ex;
        }
        catch (Exception ex)
        {
            terminal = ex;
        }
        finally
        {
            inbound.Writer.TryComplete(terminal);
            FailPendingAcks(terminal ?? new ObjectDisposedException(nameof(NinoTncSerialPort)));
        }
    }

    private void DispatchFrame(KissFrame frame)
    {
        if (KissAckMode.TryParseAcknowledgement(frame, out var tag) &&
            pendingAcks.TryRemove(tag, out var tcs))
        {
            tcs.TrySetResult(new AckModeReceipt(tag, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        inbound.Writer.TryWrite(frame);
        FrameReceived?.Invoke(this, frame);
    }

    private void FailPendingAcks(Exception cause)
    {
        foreach (var key in pendingAcks.Keys.ToArray())
        {
            if (pendingAcks.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(cause);
            }
        }
    }

    private ushort NextSequenceTag()
    {
        // 0x0000 is a legitimate tag but it's also the natural value for an
        // accidentally-uninitialised tag from the user's side. Start at 1 and
        // wrap around 0xFFFF → 1 to avoid confusion.
        while (true)
        {
            int next = Interlocked.Increment(ref ackSequenceCursor) & 0xFFFF;
            if (next != 0)
            {
                return (ushort)next;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        // Dispose the SerialPort *first* — on Windows, SerialPort.BaseStream.ReadAsync
        // does not honour cancellation tokens reliably (the Win32 ReadFile IO is
        // not abandoned when the token fires). Closing the underlying handle is
        // what actually unblocks the pending read so the pump task can exit.
        try
        {
            serial.Dispose();
        }
        catch
        {
            // Best-effort; the pump's catch block handles the resulting exception.
        }
        await pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (readPump is not null)
            {
                await readPump.ConfigureAwait(false);
            }
        }
        catch
        {
            // The pump's own try/catch already surfaced any terminal exception
            // through the inbound channel; we don't need to re-raise here.
        }
        writeLock.Dispose();
        pumpCts.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

/// <summary>
/// What you get back from <see cref="NinoTncSerialPort.SendFrameWithAckAsync(System.ReadOnlyMemory{byte},System.Nullable{System.TimeSpan},System.Nullable{ushort},System.Threading.CancellationToken)"/>:
/// the sequence tag the host chose (or the driver auto-assigned), the moment
/// the frame was handed to the wire, and the moment the TNC echoed back.
/// </summary>
/// <param name="SequenceTag">The 16-bit ACKMODE tag round-tripped by the TNC.</param>
/// <param name="Queued">When the host wrote the frame to the serial stream.</param>
/// <param name="Acknowledged">When the TNC's echo arrived back.</param>
public readonly record struct AckModeReceipt(ushort SequenceTag, DateTimeOffset Queued, DateTimeOffset Acknowledged)
{
    /// <summary>Wall-clock time between submission and TX-completion echo.</summary>
    public TimeSpan Elapsed => Acknowledged - Queued;
}
