using System.IO.Ports;
using System.Threading.Channels;
using Packet.Kiss;

namespace Packet.Term;

// IKissModem in Packet.Kiss; pulled in for the implements clause.

/// <summary>
/// A KISS-over-USB-serial transport. Hardcoded to 57600 8N1 — the only
/// shape <c>Packet.Term</c> supports. Surfaces inbound KISS frames as an
/// <see cref="IAsyncEnumerable{KissFrame}"/> and exposes plain
/// <see cref="SendDataAsync"/> for outbound AX.25 bytes. Not a full
/// <see cref="IKissModem"/> implementation — Packet.Term doesn't use
/// ACKMODE or the parameter-setting verbs.
/// </summary>
/// <remarks>
/// Read pump runs on a long-running task using the synchronous
/// <see cref="SerialPort.Read(byte[], int, int)"/> path with a finite
/// <see cref="SerialPort.ReadTimeout"/>; this is the pattern that
/// survives both .NET's unreliable async read on Windows and the equally-
/// flaky <see cref="SerialPort.DataReceived"/> event. Same shape as
/// <c>Packet.Kiss.NinoTnc.NinoTncSerialPort</c>, just stripped to the
/// pieces Packet.Term actually uses.
/// </remarks>
public sealed class KissSerialModem : IKissModem, IAsyncDisposable, IDisposable
{
    /// <summary>The hard-coded baud rate. Not user-tunable on this transport.</summary>
    public const int BaudRate = 57600;

    private readonly SerialPort serial;
    private readonly KissDecoder decoder = new();
    private readonly Channel<KissFrame> inbound;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource pumpCts = new();
    private Task? readPump;
    private int disposed;

    private KissSerialModem(SerialPort serial)
    {
        this.serial = serial;
        inbound = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
    }

    /// <summary>The port name the connection was opened on.</summary>
    public string PortName => serial.PortName;

    /// <summary>
    /// Fired for every outbound AX.25 frame as soon as the bytes have been
    /// written to the port. Subscribers run on the caller's task.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? FrameTransmitted;

    /// <summary>
    /// Fired for every inbound KISS <see cref="KissCommand.Data"/> frame
    /// the modem reports. Subscribers run on the read-pump task — keep
    /// handlers fast.
    /// </summary>
    public event EventHandler<ReadOnlyMemory<byte>>? FrameReceived;

    /// <summary>
    /// Open <paramref name="portName"/> at 57600 8N1 and start the read pump.
    /// </summary>
    public static KissSerialModem Open(string portName)
    {
        ArgumentException.ThrowIfNullOrEmpty(portName);
        var serial = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
        {
            // 100ms slot for the pump loop. Pattern lifted from
            // NinoTncSerialPort — see its comments for the rationale.
            ReadTimeout = 100,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
        serial.Open();
        var modem = new KissSerialModem(serial);
        modem.readPump = Task.Factory.StartNew(
            () => modem.PumpReadsBlocking(modem.pumpCts.Token),
            TaskCreationOptions.LongRunning);
        return modem;
    }

    /// <summary>
    /// Send the supplied AX.25 frame bytes as a KISS Data frame on port 0.
    /// </summary>
    public async Task SendDataAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default)
    {
        var encoded = KissEncoder.Encode(port: 0, KissCommand.Data, ax25Bytes.Span);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            serial.Write(encoded, 0, encoded.Length);
        }
        finally
        {
            writeLock.Release();
        }
        FrameTransmitted?.Invoke(this, ax25Bytes);
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
                if (read <= 0) continue;

                foreach (var frame in decoder.Push(buffer.AsSpan(0, read)))
                {
                    inbound.Writer.TryWrite(frame);
                    if (frame.Command == KissCommand.Data)
                    {
                        FrameReceived?.Invoke(this, frame.Payload);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex) when (Volatile.Read(ref disposed) != 0)
        {
            // SerialPort can throw on Dispose() while a read is in flight; swallow.
            terminal = ex;
        }
        catch (Exception ex)
        {
            terminal = ex;
        }
        finally
        {
            inbound.Writer.TryComplete(terminal);
        }
    }

    /// <summary>
    /// Async stream of every inbound KISS frame until disposal or
    /// <paramref name="cancellationToken"/> fires.
    /// </summary>
    public IAsyncEnumerable<KissFrame> ReadFramesAsync(CancellationToken cancellationToken = default)
        => inbound.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// <see cref="IKissModem.SendFrameAsync"/> shim: forwards to
    /// <see cref="SendDataAsync"/>. Both spellings exist so the
    /// transport stays compatible with the modem-agnostic
    /// <see cref="Packet.Ax25.Session.Ax25Listener"/> while preserving
    /// the Packet.Term-internal SendDataAsync call sites.
    /// </summary>
    Task IKissModem.SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken)
        => SendDataAsync(ax25Bytes, cancellationToken);

    /// <summary>
    /// ACKMODE not wired in this transport — the TUI doesn't track
    /// per-frame TNC ACKs. Throws so the listener path doesn't silently
    /// fall back to ACKMODE behaviour it can't deliver.
    /// </summary>
    Task<AckModeReceipt> IKissModem.SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes,
        TimeSpan? timeout,
        ushort? sequenceTag,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("KissSerialModem does not implement KISS ACKMODE; use SendFrameAsync for fire-and-forget transmission.");

    // KISS parameter setters — the TUI does not need to retune the modem
    // at runtime; we ship straight-pass-through zero-ops so the interface
    // is satisfied and adaptive controllers can plug in without throwing.
    Task IKissModem.SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken)        => WriteKissParamAsync(KissCommand.TxDelay,    tenMsUnits, cancellationToken);
    Task IKissModem.SetPersistenceAsync(byte value, CancellationToken cancellationToken)         => WriteKissParamAsync(KissCommand.Persistence, value,      cancellationToken);
    Task IKissModem.SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken)       => WriteKissParamAsync(KissCommand.SlotTime,   tenMsUnits, cancellationToken);
    Task IKissModem.SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken)         => WriteKissParamAsync(KissCommand.TxTail,     tenMsUnits, cancellationToken);

    private async Task WriteKissParamAsync(KissCommand cmd, byte value, CancellationToken ct)
    {
        // stackalloc is illegal inside an async body; allocate the 1-byte
        // payload on the heap. The setter path is hit at most a handful
        // of times per session so the allocation cost is irrelevant.
        var payload = new byte[] { value };
        var encoded = KissEncoder.Encode(port: 0, cmd, payload);
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { serial.Write(encoded, 0, encoded.Length); }
        finally { writeLock.Release(); }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { serial.Dispose(); } catch { /* swallowed */ }
        await pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (readPump is not null) await readPump.ConfigureAwait(false);
        }
        catch
        {
            // Pump's own catch already surfaced anything material.
        }
        writeLock.Dispose();
        pumpCts.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
