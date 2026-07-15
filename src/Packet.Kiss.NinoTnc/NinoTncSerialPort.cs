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
    private Task? keepAliveLoop;
    private long lastInboundTicks;
    private int ackSequenceCursor;
    private int disposed;
    private int currentMode = -1;

    private NinoTncSerialPort(KissSerialModem modem, TimeProvider? timeProvider)
    {
        this.modem = modem;
        clock = timeProvider ?? TimeProvider.System;
        lastInboundTicks = clock.GetUtcNow().UtcTicks;
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
    /// been set on this connection. This is what *we* commanded — not a DIP-switch or
    /// flash-persisted mode chosen outside this connection.
    /// </summary>
    /// <remarks>
    /// A verified <see cref="SetModeAsync"/> (the default) has proved this against the TNC's own
    /// GETALL readback, and rewrites it to the observed mode — or <c>null</c> — when the mode
    /// refused to take, so it never asserts a mode the TNC contradicted. A fire-and-forget send
    /// (<see cref="NinoTncModeVerification.None"/>) leaves this as the commanded mode alone, which
    /// SETHW does not guarantee (#633).
    /// </remarks>
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
    /// Open a NinoTNC whose USB-CDC serial port is bridged as a raw binary TCP pipe by a remote
    /// head-end (the split-station topology — see
    /// <c>docs/research/split-station-rf-headend.md</c>) and start the read pump. This is the
    /// <b>full-control</b> NinoTNC-over-TCP path — GETVER, mode agility, GETRSSI and ACKMODE
    /// TX-completion all work remotely, distinct from the generic control-less <c>kiss-tcp</c>
    /// transport. NinoTNC baud is fictional over USB-CDC, so there is no baud parameter.
    /// </summary>
    /// <param name="host">Head-end host bridging the serial port.</param>
    /// <param name="port">Head-end TCP port for this TNC's raw byte pipe.</param>
    /// <param name="timeProvider">
    /// Clock used to stamp inbound frames' <see cref="Ax25InboundFrame.ReceivedAt"/> on the
    /// <see cref="IAx25Transport"/> seam. Null uses the system clock.
    /// </param>
    /// <param name="options">Behavioural knobs; null uses defaults — which for the TCP path
    /// includes the GETVER keep-alive poll (#580), so an RF-quiet channel never trips the
    /// socket's 5-min read-idle liveness budget while a dead link still faults on it.</param>
    /// <param name="cancellationToken">Cancels the connect.</param>
    public static async Task<NinoTncSerialPort> OpenTcp(
        string host, int port, TimeProvider? timeProvider = null,
        NinoTncSerialPortOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inner = await KissSerialModem.OpenTcp(host, port, timeProvider, cancellationToken).ConfigureAwait(false);
        return StartDispatching(inner, timeProvider, options ?? new NinoTncSerialPortOptions());
    }

    /// <summary>
    /// Test seam (InternalsVisibleTo <c>Packet.Kiss.NinoTnc.Tests</c>): wrap a pre-built
    /// <see cref="KissSerialModem"/> (typically one over a fake <c>ISerialPortIo</c> via
    /// <c>KissSerialModem.OpenForTest</c>) and start the dispatch loop, without opening a real
    /// serial port. Lets tests drive the ACKMODE TX-completion correlation, frame
    /// classification/fan-out, and dispose-fails-pending-acks teardown deterministically.
    /// </summary>
    internal static NinoTncSerialPort OpenForTest(
        KissSerialModem modem, TimeProvider? timeProvider = null, NinoTncSerialPortOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(modem);
        return StartDispatching(modem, timeProvider, options);
    }

    private static NinoTncSerialPort StartDispatching(
        KissSerialModem modem, TimeProvider? timeProvider, NinoTncSerialPortOptions? options = null)
    {
        var tnc = new NinoTncSerialPort(modem, timeProvider);
        tnc.dispatchLoop = Task.Run(() => tnc.DispatchFramesAsync(tnc.dispatchCts.Token));
        if (options?.KeepAliveInterval is { } interval && interval > TimeSpan.Zero)
        {
            tnc.keepAliveLoop = Task.Run(() => tnc.KeepAliveAsync(interval, tnc.dispatchCts.Token));
        }
        return tnc;
    }

    /// <summary>
    /// The keep-alive poll (#580), mirroring the Tait driver's watchdog: when no inbound frame
    /// has arrived for <paramref name="interval"/>, issue a GETVER. On a healthy-but-RF-quiet
    /// link the reply's bytes reset the TCP pipe's read-idle liveness budget, so the port never
    /// churns through a pointless reconnect cycle; on a dead link the probe goes unanswered (or
    /// the write fails) and the idle budget faults the pump exactly as before — dead links still
    /// fault fast, quiet links stop faulting at all.
    /// </summary>
    private async Task KeepAliveAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var checkEvery = TimeSpan.FromTicks(Math.Max(interval.Ticks / 2, TimeSpan.TicksPerSecond));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(checkEvery, clock, cancellationToken).ConfigureAwait(false);
                bool quiet = clock.GetUtcNow().UtcTicks - Volatile.Read(ref lastInboundTicks) >= interval.Ticks;
                if (!quiet)
                {
                    continue;
                }
                try
                {
                    _ = await GetVersionAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // No reply / link down: liveness policy stays with the transport layer (the
                    // read-idle budget faults a dead pipe); the probe's only job was to generate
                    // bytes on a healthy one.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
    /// The DIP position that means "Set from KISS" rather than naming an
    /// operating mode — see <see cref="SetModeAsync"/> on why it is not verified.
    /// </summary>
    private const byte SetFromKissMode = 15;

    /// <summary>
    /// Set the NinoTNC operating mode via KISS SETHW (command 0x06), and — by
    /// default — verify the TNC actually applied it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SETHW is unacknowledged and <em>does</em> silently fail to apply (#633):
    /// bench-observed twice on firmware 3.44 with DIP 1111, the TNC carrying on
    /// in its previous mode while every downstream measurement scored zero in
    /// both directions — which reads as broken RF, not an ignored command. So
    /// this method sends the SETHW, lets the modem settle, reads the running
    /// mode back with GETALL, retries, and throws
    /// <see cref="NinoTncModeNotAppliedException"/> if the mode never takes.
    /// Verification is the default precisely because the failure it catches is
    /// invisible; pass <see cref="NinoTncModeVerification.None"/> for the old
    /// fire-and-forget send.
    /// </para>
    /// <para>
    /// The readback compares through <see cref="NinoTncCatalog"/>
    /// (<see cref="NinoTncStatusFrame.RunningMode"/>), not raw firmware bytes,
    /// so firmware-specific spellings of a mode — e.g. 3.41 reporting mode 14 as
    /// <c>0x90</c> where 3.44 reports <c>0x23</c> — verify correctly rather than
    /// reading as a mis-set.
    /// </para>
    /// <para>
    /// <b>Mode 15 is never verified.</b> It is the "Set from KISS" escape — a
    /// statement about where the mode comes from, not an operating mode to run —
    /// so there is nothing meaningful to compare a readback against, and the
    /// send is passed straight through.
    /// </para>
    /// </remarks>
    /// <param name="mode">DIP-switch-equivalent mode 0-14, or 15 ("Set from KISS").</param>
    /// <param name="persistToFlash">
    /// When <c>false</c> (default), the +16 non-persist offset is applied so
    /// the change does not touch the TNC's flash. Use <c>true</c> only when
    /// the user wants the choice to survive a reboot.
    /// </param>
    /// <param name="verification">
    /// How hard to work at proving the mode took. <c>null</c> (default) uses
    /// <see cref="NinoTncModeVerification.Default"/>: 1.5 s settle, GETALL
    /// readback, up to 3 attempts.
    /// </param>
    /// <param name="cancellationToken">Cancels the SETHW send, the settle and the readback.</param>
    /// <exception cref="NinoTncModeNotAppliedException">
    /// Verification was enabled and the TNC never reported running
    /// <paramref name="mode"/>.
    /// </exception>
    public async Task SetModeAsync(
        byte mode,
        bool persistToFlash = false,
        NinoTncModeVerification? verification = null,
        CancellationToken cancellationToken = default)
    {
        byte payload = NinoTncSetHardware.BuildPayloadByte(mode, persistToFlash);
        var verify = verification ?? NinoTncModeVerification.Default;

        if (!verify.Enabled || mode == SetFromKissMode)
        {
            await SendSetHardwareAsync(mode, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        NinoTncStatusFrame? lastStatus = null;
        Exception? lastFailure = null;
        int attempts = Math.Max(1, verify.Attempts);

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            await SendSetHardwareAsync(mode, payload, cancellationToken).ConfigureAwait(false);

            if (verify.SettleTime > TimeSpan.Zero)
            {
                await Task.Delay(verify.SettleTime, clock, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                lastStatus = await GetAllAsync(verify.ReadBackTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                // A readback that never lands says nothing about the mode — re-send and re-ask.
                lastFailure = ex;
                lastStatus = null;
                continue;
            }

            if (lastStatus.RunningMode?.Mode == mode)
            {
                return;
            }
        }

        // Don't leave CurrentMode (and CurrentBitRateHz through it) asserting a
        // mode the TNC just told us it isn't running — that is the same lie in a
        // different place. Prefer what it reports; otherwise admit we don't know.
        Volatile.Write(ref currentMode, lastStatus?.RunningMode?.Mode ?? -1);

        throw new NinoTncModeNotAppliedException(
            BuildModeNotAppliedMessage(mode, lastStatus, attempts),
            mode,
            lastStatus?.RunningMode,
            lastStatus?.FirmwareModeByte,
            attempts,
            lastFailure);
    }

    private Task SendSetHardwareAsync(byte mode, byte payload, CancellationToken cancellationToken)
    {
        Volatile.Write(ref currentMode, mode);
        return modem.SendKissAsync(KissCommand.SetHardware, new[] { payload }, cancellationToken);
    }

    private static string BuildModeNotAppliedMessage(byte mode, NinoTncStatusFrame? status, int attempts)
    {
        string requested = NinoTncCatalog.TryGetByMode(mode) is { } wanted
            ? $"mode {mode} ({wanted.Name})"
            : $"mode {mode}";

        string observed = status switch
        {
            null => "no GETALL reply landed",
            { RunningMode: { } running } => $"it is running mode {running.Mode} ({running.Name})",
            { FirmwareModeByte: { } raw } => $"it reports firmware mode byte 0x{raw:X2}, unknown to NinoTncCatalog",
            _ => "its GETALL reply carried no mode register",
        };

        return $"NinoTNC did not apply {requested} after {attempts} SETHW attempt(s) — {observed}. " +
               "SETHW is unacknowledged and silently ignored on occasion; the mode is NOT set, so any " +
               "traffic from here would run in the wrong mode (see packet-net/packet.net#633).";
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
    /// <see cref="NinoTncStatusDelta"/>). Where it exists it is a continuous
    /// post-FM-demod deviation meter — the deviation-tuning assistant probes
    /// for it once per session and samples it during bursts (the fast path).
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
    /// GETSERNO: read the TNC's 8-byte KAUP8R identity register (the value
    /// the periodic status report carries as register 01). Returns the
    /// register as ASCII, or <c>null</c> when it is unset (all zero bytes).
    /// Bench-verified on firmware 3.41 (2026-07-03): the reply is exactly
    /// 8 raw bytes on the 0xE0 reply command byte.
    /// </summary>
    /// <param name="timeout">Maximum wait for the reply. Defaults to 5 s.</param>
    /// <param name="cancellationToken">Cancels the send and the wait.</param>
    /// <exception cref="TimeoutException">No reply within the timeout (older
    /// firmware without GETSERNO gives no reply at all).</exception>
    public async Task<string?> GetSerialNumberAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        byte[] register = await SendAwaitingReplyAsync(
            (KissCommand)NinoTncCommands.GetSerialNumberCommand,
            NinoTncCommands.BuildGetSerialNumberPayload(),
            static frame =>
                NinoTncCommands.IsReply(frame) && frame.Payload.Length == NinoTncCommands.SerialNumberLength
                    ? frame.Payload
                    : null,
            "GETSERNO",
            timeout,
            cancellationToken).ConfigureAwait(false);
        return register.All(static b => b == 0)
            ? null
            : Encoding.ASCII.GetString(register).TrimEnd('\0');
    }

    /// <summary>
    /// SETSERNO: <b>write</b> the TNC's 8-byte KAUP8R identity register.
    /// Fire-and-forget — the firmware sends no acknowledgement; confirm with
    /// <see cref="GetSerialNumberAsync"/>. Upstream tnc-tools prescribes
    /// clearing (<see cref="ClearSerialNumberAsync"/>) before setting.
    /// </summary>
    /// <remarks>
    /// The wire form follows upstream tnc-tools (KISS command 0x0A + 8 ASCII
    /// characters). <b>The write path has not been exercised on this bench's
    /// hardware</b> — only the GETSERNO read has (the rig TNCs' identity
    /// registers are deliberately left untouched).
    /// </remarks>
    /// <param name="serialNumber">Exactly 8 printable-ASCII characters.</param>
    /// <param name="cancellationToken">Cancels the underlying send.</param>
    public Task SetSerialNumberAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        modem.SendKissAsync(
            (KissCommand)NinoTncCommands.SetSerialNumberCommand,
            NinoTncCommands.BuildSetSerialNumberPayload(serialNumber),
            cancellationToken);

    /// <summary>
    /// CLRSERNO: <b>erase</b> the TNC's KAUP8R identity register (SETSERNO
    /// with 8 zero bytes). Fire-and-forget; see the
    /// <see cref="SetSerialNumberAsync"/> remarks — the write path is
    /// bench-unexercised.
    /// </summary>
    /// <param name="cancellationToken">Cancels the underlying send.</param>
    public Task ClearSerialNumberAsync(CancellationToken cancellationToken = default) =>
        modem.SendKissAsync(
            (KissCommand)NinoTncCommands.SetSerialNumberCommand,
            NinoTncCommands.BuildClearSerialNumberPayload(),
            cancellationToken);

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
        Volatile.Write(ref lastInboundTicks, clock.GetUtcNow().UtcTicks);
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
        try
        {
            if (keepAliveLoop is not null)
            {
                await keepAliveLoop.ConfigureAwait(false);
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
