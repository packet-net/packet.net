using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Packet.Ax25.Transport;

namespace Packet.Kiss.Serial;

/// <summary>
/// A generic serial-port KISS modem implementing the neutral
/// <see cref="IAx25Transport"/> seam (plus the <see cref="ICsmaChannelParams"/> capability).
/// Opens a serial port, runs a background read pump, and surfaces inbound
/// KISS frames through <see cref="ReadFramesAsync"/> /
/// <see cref="ReceiveAsync"/> and <see cref="FrameReceived"/>. Writes are serialised through an
/// internal semaphore. Suitable for any TNC that speaks standard KISS over a serial
/// (USB-CDC) connection.
/// </summary>
/// <remarks>
/// Plain serial KISS has no ACKMODE TX-completion signal, so this transport deliberately does
/// NOT implement <see cref="ITxCompletionTransport"/> — a consumer probing
/// <c>is ITxCompletionTransport</c> correctly skips it and falls back to fire-and-forget sends.
/// For NinoTNC-specific features (ACKMODE TX-completion correlation,
/// SETHW mode switching, TX-Test frame classification), use
/// <c>Packet.Kiss.NinoTnc.NinoTncSerialPort</c> instead.
/// </remarks>
public sealed class KissSerialModem : IAx25Transport, ICsmaChannelParams, IAsyncDisposable, IDisposable
{
    public const int DefaultBaudRate = 57600;

    private readonly ISerialPortIo serial;
    private readonly KissDecoder decoder = new();
    private readonly Channel<KissFrame> inbound;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource pumpCts = new();
    private readonly TimeProvider clock;
    private Task? readPump;
    private int disposed;

    private const byte KissPort = 0;

    private KissSerialModem(ISerialPortIo serial, TimeProvider? timeProvider)
    {
        this.serial = serial;
        clock = timeProvider ?? TimeProvider.System;
        inbound = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
    }

    /// <summary>The port name the connection was opened on (e.g. "COM6" or "/dev/ttyACM0").</summary>
    public string PortName => serial.PortName;

    /// <summary>
    /// Fired for every inbound KISS frame after framing/unescaping.
    /// Subscribers run on the read-pump task — keep handlers fast and
    /// non-blocking. Use <see cref="ReadFramesAsync"/> if you'd rather
    /// pull frames on your own task.
    /// </summary>
    public event EventHandler<KissFrame>? FrameReceived;

    /// <summary>
    /// Open the named serial port and start the background read pump.
    /// </summary>
    /// <param name="portName">The serial port to open (e.g. "COM6" or "/dev/ttyACM0").</param>
    /// <param name="baudRate">The serial baud rate.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp inbound frames' <see cref="Ax25InboundFrame.ReceivedAt"/> on the
    /// <see cref="IAx25Transport"/> seam. Null uses the system clock.
    /// </param>
    public static KissSerialModem Open(string portName, int baudRate = DefaultBaudRate, TimeProvider? timeProvider = null)
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
        return StartPumping(new SystemSerialPortIo(serial), timeProvider);
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo <c>Packet.Kiss.Serial.Tests</c>): build a modem
    /// over a substitute <see cref="ISerialPortIo"/> and start the read pump, without
    /// touching a real serial port. Lets tests drive the read pump, KISS encoding, and
    /// dispose ordering deterministically.
    /// </summary>
    internal static KissSerialModem OpenForTest(ISerialPortIo io, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(io);
        return StartPumping(io, timeProvider);
    }

    private static KissSerialModem StartPumping(ISerialPortIo io, TimeProvider? timeProvider)
    {
        var modem = new KissSerialModem(io, timeProvider);
        modem.readPump = Task.Factory.StartNew(
            () => modem.PumpReadsBlocking(modem.pumpCts.Token),
            TaskCreationOptions.LongRunning);
        return modem;
    }

    /// <summary>
    /// Asynchronously stream every inbound KISS frame until the connection
    /// is disposed or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public IAsyncEnumerable<KissFrame> ReadFramesAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// <see cref="IAx25Transport"/>: send one AX.25 frame body (KISS Data, cmd 0x00).
    /// Same fire-and-forget path as <see cref="SendFrameAsync"/>.
    /// </summary>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
        SendFrameAsync(ax25, cancellationToken);

    /// <summary>
    /// <see cref="IAx25Transport"/>: async stream of inbound AX.25 frames, pre-filtered to KISS
    /// Data (non-Data KISS commands are dropped here so the consumer never sees them).
    /// </summary>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var kiss in ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (kiss.Command != KissCommand.Data) continue;
            yield return new Ax25InboundFrame(kiss.Payload, kiss.Port, clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Send a plain KISS Data frame (command 0x00). Returns once the bytes
    /// have been handed to the underlying stream.
    /// </summary>
    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default) =>
        SendKissAsync(KissCommand.Data, ax25Bytes, cancellationToken);

    /// <summary>
    /// Send an arbitrary KISS command frame on port 0. The
    /// <paramref name="payload"/> is KISS-encoded (escaped + FEND-framed)
    /// and written to the serial port under the write lock.
    /// </summary>
    public Task SendKissAsync(KissCommand command, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var encoded = KissEncoder.Encode(KissPort, command, payload.Span);
        return WriteAsync(encoded, cancellationToken);
    }

    /// <summary>Send a KISS TXDELAY (0x01) command. Units are 10 ms.</summary>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.TxDelay, tenMsUnits, cancellationToken);

    /// <summary>Send a KISS PERSISTENCE (0x02) command (0-255).</summary>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.Persistence, value, cancellationToken);

    /// <summary>Send a KISS SLOTTIME (0x03) command. Units are 10 ms.</summary>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.SlotTime, tenMsUnits, cancellationToken);

    /// <summary>Send a KISS TXTAIL (0x04) command. Units are 10 ms.</summary>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.TxTail, tenMsUnits, cancellationToken);

    /// <summary>Send a KISS FULLDUPLEX (0x05) command.</summary>
    public Task SetFullDuplexAsync(bool fullDuplex, CancellationToken cancellationToken = default) =>
        SendParameterAsync(KissCommand.FullDuplex, fullDuplex ? (byte)1 : (byte)0, cancellationToken);

    private Task SendParameterAsync(KissCommand command, byte value, CancellationToken cancellationToken)
    {
        var encoded = KissEncoder.Encode(KissPort, command, [value]);
        return WriteAsync(encoded, cancellationToken);
    }

    private async Task WriteAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                    inbound.Writer.TryWrite(frame);
                    FrameReceived?.Invoke(this, frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (Volatile.Read(ref disposed) != 0)
        {
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

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        // Dispose the SerialPort *first* — on Windows, SerialPort.BaseStream.ReadAsync
        // does not honour cancellation tokens reliably. Closing the underlying
        // handle is what actually unblocks the pending read so the pump can exit.
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
