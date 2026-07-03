using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.Serial;

namespace Packet.Kiss.NinoTnc;

/// <summary>
/// A NinoTNC USB-CDC serial connection that speaks KISS, the neutral
/// <see cref="IAx25Transport"/> seam (with both the <see cref="ITxCompletionTransport"/> and
/// <see cref="ICsmaChannelParams"/> capabilities), and the NinoTNC-flavoured
/// SETHW command. Built on top of <see cref="KissSerialModem"/> for the generic
/// serial-port plumbing; this class adds ACKMODE TX-completion correlation,
/// NinoTNC frame classification, and SETHW mode switching. Inbound KISS frames
/// are surfaced through <see cref="ReadFramesAsync"/> / <see cref="ReceiveAsync"/> and
/// <see cref="FrameReceived"/>; ACKMODE transmit-completion echoes are correlated
/// through <see cref="SendFrameWithAckAsync"/> / <see cref="SendAwaitingCompletionAsync"/>.
/// </summary>
public sealed class NinoTncSerialPort : IAx25Transport, ITxCompletionTransport, ICsmaChannelParams, IAsyncDisposable, IDisposable
{
    /// <summary>The NinoTNC's documented USB-serial baud rate.</summary>
    public const int DefaultBaudRate = 57600;

    private readonly KissSerialModem modem;
    private readonly Channel<KissFrame> inbound;
    private readonly ConcurrentDictionary<ushort, TaskCompletionSource<DateTimeOffset>> pendingAcks = new();
    private readonly CancellationTokenSource dispatchCts = new();
    private readonly TimeProvider clock;

    private Task? dispatchLoop;
    private int ackSequenceCursor;
    private int disposed;
    private int currentMode = -1;

    private NinoTncSerialPort(KissSerialModem modem, TimeProvider? timeProvider)
    {
        this.modem = modem;
        clock = timeProvider ?? TimeProvider.System;
        inbound = Channel.CreateUnbounded<KissFrame>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });
    }

    /// <summary>The port name the connection was opened on (e.g. "COM6" or "/dev/ttyACM0").</summary>
    public string PortName => modem.PortName;

    /// <summary>
    /// The last mode this driver set via <see cref="SetModeAsync"/>, or <c>null</c> if none has
    /// been set on this connection. KISS has no mode read-back, so this reflects what *we*
    /// commanded — not a DIP-switch or flash-persisted mode chosen outside this connection.
    /// </summary>
    public byte? CurrentMode
    {
        get
        {
            int mode = Volatile.Read(ref currentMode);
            return mode >= 0 ? (byte)mode : null;
        }
    }

    /// <summary>
    /// The over-air bit rate of <see cref="CurrentMode"/> per <see cref="NinoTncCatalog"/>, or
    /// <c>null</c> when no mode has been set (or the mode has no fixed rate, e.g. 15 "Set from
    /// KISS"). Feed this to <c>RssiTaggingOptions.BitRateHzProvider</c> (Packet.Radio) for
    /// per-frame airtime / pre-data-carrier estimation.
    /// </summary>
    public int? CurrentBitRateHz =>
        CurrentMode is { } m && NinoTncCatalog.TryGetByMode(m) is { BitRateHz: > 0 } entry
            ? entry.BitRateHz
            : null;

    /// <summary>
    /// Fired for every inbound KISS frame after framing/unescaping, in
    /// its raw form. Subscribers run on the dispatch task — keep handlers
    /// fast and non-blocking. Use <see cref="ReadFramesAsync"/> if you'd
    /// rather pull frames on your own task.
    /// </summary>
    /// <remarks>
    /// Subscribers that want shape-typed events (AX.25 frames, TX-Test
    /// diagnostics, ACKMODE-Data) should subscribe to
    /// <see cref="InboundEvent"/> instead. Both fire for every inbound
    /// frame; pick whichever fits your dispatch model.
    /// </remarks>
    public event EventHandler<KissFrame>? FrameReceived;

    /// <summary>
    /// Fired for every inbound KISS frame after framing/unescaping, *and*
    /// after the driver has classified the shape (AX.25, TX-Test diagnostic,
    /// ACKMODE-Data, or unknown). Strongly typed for ergonomic handler
    /// dispatch. Subscribers run on the dispatch task.
    /// </summary>
    public event EventHandler<KissInboundEvent>? InboundEvent;

    /// <summary>
    /// Open the named serial port at 57 600 8N1 and start the background read pump.
    /// </summary>
    /// <param name="portName">The serial port to open (e.g. "COM6" or "/dev/ttyACM0").</param>
    /// <param name="baudRate">The serial baud rate.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp inbound frames' <see cref="Ax25InboundFrame.ReceivedAt"/> on the
    /// <see cref="IAx25Transport"/> seam. Null uses the system clock.
    /// </param>
    public static NinoTncSerialPort Open(string portName, int baudRate = DefaultBaudRate, TimeProvider? timeProvider = null)
    {
        var inner = KissSerialModem.Open(portName, baudRate, timeProvider);
        return StartDispatching(inner, timeProvider);
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo <c>Packet.Kiss.NinoTnc.Tests</c>): wrap a pre-built
    /// <see cref="KissSerialModem"/> (typically one over a fake <c>ISerialPortIo</c> via
    /// <c>KissSerialModem.OpenForTest</c>) and start the dispatch loop, without opening a real
    /// serial port. Lets tests drive the ACKMODE TX-completion correlation, frame
    /// classification/fan-out, and dispose-fails-pending-acks teardown deterministically.
    /// </summary>
    internal static NinoTncSerialPort OpenForTest(KissSerialModem modem, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        return StartDispatching(modem, timeProvider);
    }

    private static NinoTncSerialPort StartDispatching(KissSerialModem modem, TimeProvider? timeProvider)
    {
        var tnc = new NinoTncSerialPort(modem, timeProvider);
        tnc.dispatchLoop = Task.Run(() => tnc.DispatchFramesAsync(tnc.dispatchCts.Token));
        return tnc;
    }

    /// <summary>
    /// Asynchronously stream every inbound KISS frame until the connection is
    /// disposed or <paramref name="cancellationToken"/> fires.
    /// </summary>
    public IAsyncEnumerable<KissFrame> ReadFramesAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// <see cref="IAx25Transport"/>: async stream of inbound AX.25 frames, pre-filtered to KISS
    /// Data (non-Data KISS commands — TX-Test echoes, diagnostics — are dropped here).
    /// </summary>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var kiss in ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (kiss.Command != KissCommand.Data)
            {
                continue;
            }

            yield return new Ax25InboundFrame(kiss.Payload, kiss.Port, clock.GetUtcNow());
        }
    }

    /// <summary>
    /// Send a plain KISS data frame (command 0x00). Returns once the bytes
    /// have been handed to the underlying stream; transmission over-the-air
    /// has *not* happened yet. Use <see cref="SendFrameWithAckAsync"/> when
    /// you need to know when the modem has finished keying.
    /// </summary>
    public Task SendFrameAsync(ReadOnlyMemory<byte> ax25Bytes, CancellationToken cancellationToken = default) =>
        modem.SendFrameAsync(ax25Bytes, cancellationToken);

    /// <summary>
    /// <see cref="IAx25Transport"/>: send one AX.25 frame body (KISS Data, cmd 0x00),
    /// fire-and-forget. Same path as <see cref="SendFrameAsync"/>.
    /// </summary>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
        SendFrameAsync(ax25, cancellationToken);

    /// <summary>
    /// <see cref="ITxCompletionTransport"/>: send an AX.25 frame in ACKMODE and await its
    /// TX-completion as the neutral <see cref="TxCompletion"/> (the 16-bit sequence tag is
    /// auto-assigned and stays an internal wire artefact). NotSupported / Timeout propagate
    /// unchanged.
    /// </summary>
    public Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => SendFrameWithAckAsync(ax25, timeout, sequenceTag: null, cancellationToken);

    /// <summary>
    /// Send an ACKMODE frame (command 0x0C) and await the TNC's TX-completion
    /// echo, resolving with a <see cref="TxCompletion"/> that times the round
    /// trip. If <paramref name="sequenceTag"/> is <c>null</c>, an internal
    /// counter assigns a unique value. This is the KISS-specific entry point
    /// that lets a caller pin the 16-bit sequence tag;
    /// <see cref="SendAwaitingCompletionAsync"/> is the neutral
    /// <see cref="ITxCompletionTransport"/> form that auto-assigns it.
    /// </summary>
    /// <param name="ax25Bytes">AX.25 frame to transmit.</param>
    /// <param name="timeout">Maximum time to wait for the echo. Defaults to 30 s.</param>
    /// <param name="sequenceTag">Caller-supplied 16-bit tag, or <c>null</c> to auto-assign.</param>
    /// <param name="cancellationToken">Cancels the wait (does not un-queue the frame at the TNC).</param>
    public async Task<TxCompletion> SendFrameWithAckAsync(
        ReadOnlyMemory<byte> ax25Bytes,
        TimeSpan? timeout = null,
        ushort? sequenceTag = null,
        CancellationToken cancellationToken = default)
    {
        ushort tag = sequenceTag ?? NextSequenceTag();
        var tcs = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingAcks.TryAdd(tag, tcs))
        {
            throw new InvalidOperationException($"sequence tag 0x{tag:X4} already has a pending ACK; pick a unique tag");
        }

        var payload = new byte[ax25Bytes.Length + 2];
        payload[0] = (byte)((tag >> 8) & 0xFF);
        payload[1] = (byte)(tag & 0xFF);
        ax25Bytes.Span.CopyTo(payload.AsSpan(2));

        var queuedAt = DateTimeOffset.UtcNow;
        try
        {
            await modem.SendKissAsync(KissCommand.AckMode, payload, cancellationToken).ConfigureAwait(false);
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

        var completedAt = await tcs.Task.ConfigureAwait(false);
        return new TxCompletion(queuedAt, completedAt);
    }

    /// <summary>
    /// Set the NinoTNC operating mode via KISS SETHW (command 0x06).
    /// </summary>
    /// <param name="mode">DIP-switch-equivalent mode 0-14, or 15 ("Set from KISS").</param>
    /// <param name="persistToFlash">
    /// When <c>false</c> (default), the +16 non-persist offset is applied so
    /// the change does not touch the TNC's flash. Use <c>true</c> only when
    /// the user wants the choice to survive a reboot.
    /// </param>
    /// <param name="cancellationToken">Cancels the underlying KISS SETHW send.</param>
    public Task SetModeAsync(byte mode, bool persistToFlash = false, CancellationToken cancellationToken = default)
    {
        byte payload = NinoTncSetHardware.BuildPayloadByte(mode, persistToFlash);
        Volatile.Write(ref currentMode, mode);
        return modem.SendKissAsync(KissCommand.SetHardware, new[] { payload }, cancellationToken);
    }

    /// <summary>Send a KISS TXDELAY (0x01) command. Units are 10 ms.</summary>
    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        modem.SetTxDelayAsync(tenMsUnits, cancellationToken);

    /// <summary>Send a KISS PERSISTENCE (0x02) command (0-255).</summary>
    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) =>
        modem.SetPersistenceAsync(value, cancellationToken);

    /// <summary>Send a KISS SLOTTIME (0x03) command. Units are 10 ms.</summary>
    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        modem.SetSlotTimeAsync(tenMsUnits, cancellationToken);

    /// <summary>Send a KISS FULLDUPLEX (0x05) command.</summary>
    public Task SetFullDuplexAsync(bool fullDuplex, CancellationToken cancellationToken = default) =>
        modem.SetFullDuplexAsync(fullDuplex, cancellationToken);

    /// <summary>Send a KISS TXTAIL (0x04) command. Units are 10 ms.</summary>
    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        modem.SetTxTailAsync(tenMsUnits, cancellationToken);

    /// <summary>Default wait for a firmware query reply. Bench-measured replies land within ~150 ms.</summary>
    private static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// GETALL: request a full diagnostic-register snapshot and await the
    /// reply as a <see cref="NinoTncStatusFrame"/>.
    /// </summary>
    /// <remarks>
    /// Firmware 3.41 answers GETALL with the labelled <c>=FirmwareVr:</c>
    /// diagnostic (mapped through <see cref="NinoTncStatusFrame.FromDiagnostic"/>,
    /// so the registers with no labelled counterpart — PTT-on, DCD-on,
    /// RX/TX bytes, FEC-corrected bytes — come back <c>null</c>); newer
    /// firmware documents the numeric <c>=II:</c> report, which is matched
    /// directly. A periodic status frame arriving during the wait also
    /// satisfies it — the content is the same snapshot.
    /// </remarks>
    /// <param name="timeout">Maximum wait for the reply. Defaults to 5 s.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    /// <exception cref="TimeoutException">No reply within the timeout.</exception>
    public Task<NinoTncStatusFrame> GetAllAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendAwaitingReplyAsync(
            (KissCommand)NinoTncCommands.GetAllCommand,
            NinoTncCommands.BuildGetAllPayload(),
            static frame =>
            {
                if (NinoTncStatusFrame.TryParse(frame, out var status))
                {
                    return status;
                }
                if (NinoTncTxTestFrame.TryParse(frame, out var diagnostic) && diagnostic is not null)
                {
                    return NinoTncStatusFrame.FromDiagnostic(diagnostic);
                }
                return null;
            },
            "GETALL",
            timeout,
            cancellationToken);

    /// <summary>
    /// GETVER: request the firmware version and await the ASCII reply
    /// (e.g. <c>"3.41"</c>). Parse it further with
    /// <see cref="Firmware.NinoTncFirmwareVersion.TryParse(string?, out Firmware.NinoTncFirmwareVersion)"/>
    /// if the two-component form is needed.
    /// </summary>
    /// <param name="timeout">Maximum wait for the reply. Defaults to 5 s.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    /// <exception cref="TimeoutException">No reply within the timeout.</exception>
    public Task<string> GetVersionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendAwaitingReplyAsync(
            (KissCommand)NinoTncCommands.GetVersionCommand,
            NinoTncCommands.BuildGetVersionPayload(),
            static frame => TryReadVersionReply(frame),
            "GETVER",
            timeout,
            cancellationToken);

    /// <summary>
    /// GETRSSI: request an RX-audio level reading and await the reply.
    /// The value is the RMS level of the TNC's receive audio in dB, not an
    /// RF dBm figure — see <see cref="NinoTncRssiReading"/>.
    /// </summary>
    /// <remarks>
    /// <b>Firmware 3.41 only — REMOVED in firmware 3.44.</b> GETRSSI was an
    /// undocumented 3.41 feature; on 3.44 the query gets no reply at all
    /// (bench-verified 2026-07-02 after flashing both rig TNCs), so this
    /// call ends in <see cref="TimeoutException"/>. Callers must catch it
    /// and degrade — e.g. meter deviation by decoded-frame counts, IL2P
    /// FEC-corrected-byte deltas and the lost-ADC counter instead (see
    /// <see cref="NinoTncStatusDelta"/>).
    /// </remarks>
    /// <param name="timeout">Maximum wait for the reply. Defaults to 5 s.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    /// <exception cref="TimeoutException">No reply within the timeout — including always on firmware 3.44+, which removed the query.</exception>
    public async Task<float> GetRssiAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var reading = await SendAwaitingReplyAsync(
            (KissCommand)NinoTncCommands.ExtendedCommand,
            NinoTncCommands.BuildGetRssiPayload(),
            static frame => NinoTncRssiReading.TryParse(frame, out var parsed) ? parsed : null,
            "GETRSSI",
            timeout,
            cancellationToken).ConfigureAwait(false);
        return reading.LevelDb;
    }

    /// <summary>
    /// STOPTX: tell the TNC to abort any transmission in progress.
    /// Fire-and-forget — the firmware sends no acknowledgement.
    /// </summary>
    /// <remarks>
    /// Bench note (firmware 3.41, 2026-07-02): STOPTX sent mid-tone did
    /// <b>not</b> cut short an in-progress CQBEEP responder tone — the tone
    /// ran its full N seconds. Treat it as a queue/normal-TX abort;
    /// whether newer firmware also stops the beep generator is unverified.
    /// </remarks>
    public Task StopTxAsync(CancellationToken cancellationToken = default) =>
        modem.SendKissAsync((KissCommand)NinoTncCommands.ExtendedCommand, NinoTncCommands.BuildStopTxPayload(), cancellationToken);

    /// <summary>
    /// SETBCNINT: set the interval of the periodic status report
    /// (<see cref="NinoTncStatusFrame"/>) in minutes. Fire-and-forget —
    /// the firmware sends no acknowledgement.
    /// </summary>
    /// <param name="minutes">Reporting interval in minutes.</param>
    /// <param name="cancellationToken">Cancels the underlying send.</param>
    public Task SetBeaconIntervalAsync(byte minutes, CancellationToken cancellationToken = default) =>
        modem.SendKissAsync((KissCommand)NinoTncCommands.ExtendedCommand, NinoTncCommands.BuildSetBeaconIntervalPayload(minutes), cancellationToken);

    /// <summary>
    /// Arm this TNC's CQBEEP tone responder by transmitting a
    /// <c>[TARPNstat</c> status frame through it (see
    /// <see cref="NinoTncCqBeep.BuildArmingFrame"/>). The frame goes out
    /// over the air like any UI frame. Arming is volatile — re-arm after
    /// the TNC resets.
    /// </summary>
    /// <param name="source">Source callsign for the arming frame.</param>
    /// <param name="cancellationToken">Cancels the underlying send.</param>
    public Task ArmCqBeepResponderAsync(Callsign source, CancellationToken cancellationToken = default) =>
        SendFrameAsync(NinoTncCqBeep.BuildArmingFrame(source).ToBytes(), cancellationToken);

    /// <summary>
    /// Transmit a CQBEEP-N beep request through this TNC: any armed NinoTNC
    /// that hears it transmits <paramref name="seconds"/> seconds of 440 Hz
    /// tone (see <see cref="NinoTncCqBeep.BuildBeepRequest"/>).
    /// </summary>
    /// <param name="source">Source callsign for the request frame.</param>
    /// <param name="seconds">Seconds of tone to request, 1–15.</param>
    /// <param name="cancellationToken">Cancels the underlying send.</param>
    public Task SendCqBeepRequestAsync(Callsign source, int seconds, CancellationToken cancellationToken = default) =>
        SendFrameAsync(NinoTncCqBeep.BuildBeepRequest(source, seconds).ToBytes(), cancellationToken);

    private static string? TryReadVersionReply(KissFrame frame)
    {
        // The GETVER reply is a short bare-ASCII string ("3.41") on the 0xE0
        // reply command byte. Field-style replies (labelled/numeric dumps)
        // start '=' and the GETRSSI reply contains ':' — exclude both so a
        // concurrent query cannot be mistaken for the version.
        if (!NinoTncCommands.IsReply(frame) || frame.Payload.Length is 0 or > 16)
        {
            return null;
        }
        foreach (byte b in frame.Payload)
        {
            if (b is < 0x20 or > 0x7E || b == (byte)':' || b == (byte)'=')
            {
                return null;
            }
        }
        return Encoding.ASCII.GetString(frame.Payload);
    }

    private async Task<T> SendAwaitingReplyAsync<T>(
        KissCommand command,
        byte[] payload,
        Func<KissFrame, T?> matcher,
        string commandName,
        TimeSpan? timeout,
        CancellationToken cancellationToken) where T : class
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, KissFrame frame)
        {
            if (matcher(frame) is { } match)
            {
                tcs.TrySetResult(match);
            }
        }

        FrameReceived += Handler;
        try
        {
            await modem.SendKissAsync(command, payload, cancellationToken).ConfigureAwait(false);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout ?? DefaultReplyTimeout);
            await using var registration = cts.Token.Register(() => tcs.TrySetException(
                cancellationToken.IsCancellationRequested
                    ? new OperationCanceledException(cancellationToken)
                    : new TimeoutException($"NinoTNC did not answer {commandName} within {timeout ?? DefaultReplyTimeout}"))).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            FrameReceived -= Handler;
        }
    }

    private async Task DispatchFramesAsync(CancellationToken cancellationToken)
    {
        Exception? terminal = null;
        try
        {
            await foreach (var frame in modem.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
            {
                DispatchFrame(frame);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
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
            // Complete the waiter with the echo-arrival instant; the sender
            // pairs it with the real submit time to build the TxCompletion.
            tcs.TrySetResult(DateTimeOffset.UtcNow);
            inbound.Writer.TryWrite(frame);
            FrameReceived?.Invoke(this, frame);
            return;
        }

        var typed = NinoTncFrameClassifier.Classify(frame);
        inbound.Writer.TryWrite(frame);
        FrameReceived?.Invoke(this, frame);
        InboundEvent?.Invoke(this, typed);
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
        await dispatchCts.CancelAsync().ConfigureAwait(false);
        await modem.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (dispatchLoop is not null)
            {
                await dispatchLoop.ConfigureAwait(false);
            }
        }
        catch
        {
        }
        dispatchCts.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
