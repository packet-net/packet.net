using System.Net.Sockets;
using System.Threading.Channels;
using Packet.Ax25.Transport;
using Packet.Kiss;

namespace Packet.Radio.Tait;

/// <summary>
/// An <see cref="IAx25Transport"/> whose modem <b>is</b> a Tait TM8100/TM8200 radio in
/// Transparent mode — no external TNC. The radio's own FFSK modem is an 8-bit-clean byte pipe
/// (hardware-proven: all 256 byte values, including XON/XOFF, round-trip identical), so this
/// transport frames AX.25 with <b>KISS SLIP framing</b> (FEND-delimited, reusing
/// <see cref="KissEncoder"/>/<see cref="KissDecoder"/>) over that pipe and de-frames the inbound
/// byte stream back into whole AX.25 frames. The radio fragments to ≤46-byte over-air blocks and
/// reassembles them transparently, so the host sends and receives whole byte streams and does no
/// chunking of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>Timing metadata.</b> This transport <em>owns</em> the transmission, so it times it
/// directly. Every send raises <see cref="TxTiming"/> with the queue instant, the estimated
/// on-air start (queue + <see cref="TaitTransparentTransportOptions.LeadIn"/>), the estimated
/// on-air end (start + airtime), and the airtime itself; <see cref="SendAwaitingCompletionAsync"/>
/// additionally waits out that modelled airtime and returns a <see cref="TxCompletion"/> so the
/// AX.25 engine can re-arm T1 / pace on real transmit duration. Inbound frames carry a precise
/// <see cref="Ax25InboundFrame.ReceivedAt"/> (the delivery instant of the completing byte chunk)
/// and <see cref="RadioMetadata.EstimatedAirtime"/>, from which the AX.25 engine derives on-air
/// start as <c>ReceivedAt − EstimatedAirtime</c>. Airtime is <c>on-air bytes × 8 ÷
/// <see cref="TaitTransparentTransportOptions.FfskBaud"/></c>; "on-air bytes" is the SLIP-framed
/// length (what is actually clocked through the FFSK modem), so it is symmetric TX↔RX.
/// </para>
/// <para>
/// <b>Signal-telemetry trade-off (inherent).</b> In Transparent mode the serial port is a byte
/// pipe: CCDI is unavailable, so there is <em>no</em> RSSI, SNR, noise-floor, carrier-rise (DCD)
/// or burst attribution — those fields on <see cref="RadioMetadata"/> stay <c>null</c>. This is
/// the deliberate cost of a <b>TNC-less</b> link (one device, no audio wiring), versus the
/// NinoTNC-plus-CCDI arrangement (<c>RssiTaggingTransport</c>) which has full per-frame signal
/// telemetry but needs two devices (a TNC for the modem and the radio's CCDI control channel).
/// </para>
/// <para>
/// <b>Exit / recovery WARNING.</b> <see cref="DisposeAsync"/> escapes Transparent mode with the
/// <c>+++</c> guard sequence (§1.7.2) and restores the command baud, returning the radio to
/// Command mode — a port left in Transparent is deaf to CCDI. <b>If the radio is programmed with
/// "Ignore Escape Sequence" ON, the <c>+++</c> escape does nothing and there is no software way
/// out: recovery is a power cycle.</b> That is exactly the lockout observed on the bench before
/// the option was turned off. Program the radio with the escape sequence honoured before running
/// this transport unattended.
/// </para>
/// </remarks>
public sealed class TaitTransparentTransport : ITxCompletionTransport, IAsyncDisposable
{
    // KISS SLIP framing on a single-channel byte pipe: port 0, Data command. The 1-byte
    // command overhead is negligible and lets us reuse the tested KISS codec verbatim.
    private const byte SlipPort = 0;

    private readonly TaitCcdiRadio radio;
    private readonly TaitTransparentTransportOptions options;
    private readonly TimeProvider clock;
    private readonly bool ownsRadio;
    private readonly KissDecoder decoder = new();
    private readonly Channel<Ax25InboundFrame> inbound =
        Channel.CreateUnbounded<Ax25InboundFrame>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly SemaphoreSlim txGate = new(1, 1);
    private int entered;
    private int disposed;

    /// <summary>
    /// Wrap an already-open <paramref name="radio"/>. The transport does not enter Transparent
    /// mode in the constructor — call <see cref="EnterTransparentModeAsync"/> (or use
    /// <see cref="OpenAsync"/>, which does both). When <paramref name="ownsRadio"/> is
    /// <c>true</c> (the default), <see cref="DisposeAsync"/> disposes the radio too.
    /// </summary>
    public TaitTransparentTransport(
        TaitCcdiRadio radio,
        TaitTransparentTransportOptions? options = null,
        TimeProvider? timeProvider = null,
        bool ownsRadio = true)
    {
        ArgumentNullException.ThrowIfNull(radio);
        this.radio = radio;
        this.options = options ?? new TaitTransparentTransportOptions();
        clock = timeProvider ?? TimeProvider.System;
        this.ownsRadio = ownsRadio;
        radio.TransparentDataReceived += OnTransparentData;
        // A dead serial link / dropped head-end pipe faults the radio's read pump; complete the
        // inbound channel so ReceiveAsync ENDS instead of going silent forever — end-of-stream is
        // the drop signal a reconnect supervisor (the node's ReconnectingKissModem) keys on,
        // mirroring how KissTcpClient surfaces a dead socket.
        radio.ConnectionStateChanged += OnConnectionState;
    }

    /// <summary>Raised on every send with the transmission's timing envelope (queue instant,
    /// estimated on-air start and end, airtime, lead-in, on-air byte count) — the TX-side
    /// counterpart of the inbound <see cref="RadioMetadata.EstimatedAirtime"/>, for the AX.25
    /// engine / airtime estimator.</summary>
    public event EventHandler<TaitTransparentTxTiming>? TxTiming;

    /// <summary>The serial port the radio is attached to (e.g. <c>/dev/ttyUSB0</c>).</summary>
    public string PortName => radio.PortName;

    /// <summary>
    /// Open the named serial port as a Tait radio and enter Transparent mode, returning a
    /// ready-to-use transport that owns the radio. The <c>t</c> entry command is issued at
    /// <see cref="TaitTransparentTransportOptions.CommandBaud"/>; if
    /// <see cref="TaitTransparentTransportOptions.TransparentBaud"/> differs, the port is then
    /// re-clocked to it for the byte pipe.
    /// </summary>
    public static async Task<TaitTransparentTransport> OpenAsync(
        string portName,
        TaitTransparentTransportOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new TaitTransparentTransportOptions();
        // The keep-alive watchdog probes with CCDI queries — meaningless (and impossible) while
        // the port is a Transparent byte pipe, so disable it for this radio.
        var radio = TaitCcdiRadio.Open(
            portName, opts.CommandBaud, new TaitCcdiRadioOptions { KeepAliveInterval = null }, timeProvider);
        return await EnterOwningAsync(radio, opts, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Open a Tait radio whose serial port is bridged as a raw binary TCP pipe by a split-station
    /// head-end and enter Transparent mode, returning a ready-to-use transport that owns the
    /// radio — the remote twin of <see cref="OpenAsync"/>. The <c>t</c> entry command is issued at
    /// <see cref="TaitTransparentTransportOptions.CommandBaud"/>, clocked onto the physical UART
    /// via <paramref name="setBaud"/> at open; when
    /// <see cref="TaitTransparentTransportOptions.TransparentBaud"/> differs, the enter/exit
    /// re-clocks also route through <paramref name="setBaud"/> (the data socket is a pure binary
    /// pipe — line rate travels out-of-band through the head-end's line verb).
    /// </summary>
    /// <remarks>
    /// The pipe's <b>read-idle liveness budget is disabled</b> here: in Transparent mode any
    /// in-band probe byte would be transmitted over the air, so an RF-quiet channel is
    /// indistinguishable from a dead link by bytes alone — the 5-minute default would tear a
    /// healthy port down every quiet stretch (the same failure #580 fixed for nino-tnc-tcp,
    /// which CAN probe in-band with GETVER). Drop detection relies on OS TCP keepalive and a FIN,
    /// both of which fault the pump and end <see cref="ReceiveAsync"/>.
    /// </remarks>
    /// <param name="host">Head-end host bridging the radio's serial port.</param>
    /// <param name="port">Head-end TCP port for the radio's raw byte pipe.</param>
    /// <param name="setBaud">Line-control callback (the head-end's <c>POST /ports/{id}/line</c>
    /// verb); null trusts the head-end's current clock and makes re-clocks no-ops.</param>
    /// <param name="options">Transport options; null uses defaults.</param>
    /// <param name="timeProvider">Clock (test seam); null uses the system clock.</param>
    /// <param name="cancellationToken">Cancels the connect / mode entry.</param>
    public static async Task<TaitTransparentTransport> OpenTcpAsync(
        string host,
        int port,
        Func<int, CancellationToken, Task>? setBaud = null,
        TaitTransparentTransportOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new TaitTransparentTransportOptions();
        var radio = await TaitCcdiRadio.OpenTcp(
            host, port, opts.CommandBaud, setBaud,
            new TaitCcdiRadioOptions
            {
                // No CCDI watchdog (a byte pipe can't answer queries) and no in-band read-idle
                // budget (no probe is possible without transmitting) — see the remarks.
                KeepAliveInterval = null,
                ReadIdleTimeout = Timeout.InfiniteTimeSpan,
            },
            timeProvider, cancellationToken).ConfigureAwait(false);
        return await EnterOwningAsync(radio, opts, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    // Shared open tail: wrap the freshly-opened radio, enter Transparent (with the
    // stale-Transparent recovery), and dispose the whole stack on failure.
    private static async Task<TaitTransparentTransport> EnterOwningAsync(
        TaitCcdiRadio radio,
        TaitTransparentTransportOptions opts,
        TimeProvider? timeProvider,
        CancellationToken cancellationToken)
    {
        var transport = new TaitTransparentTransport(radio, opts, timeProvider, ownsRadio: true);
        try
        {
            await transport.EnterTransparentModeAsync(cancellationToken).ConfigureAwait(false);
            return transport;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Enter Transparent mode: issue the CCDI <c>t</c> command at the command baud, then — if the
    /// transparent baud differs — re-clock the open port to it. Idempotent-safe: a second call is
    /// a no-op.
    /// </summary>
    /// <remarks>
    /// <b>Stale-Transparent recovery.</b> A radio whose previous session ended without a clean
    /// teardown — the node crashed, or (the split-station case) the head-end pipe died before the
    /// escape could be delivered — is still a Transparent byte pipe: the <c>t</c> entry command is
    /// then transmitted over the air as data and no prompt ever comes back. When the entry times
    /// out, this presumes exactly that state and recovers: re-clock to the transparent baud (where
    /// the stale pipe is clocked), run the §1.7.2 escape blind, re-clock back, and retry the entry
    /// once. A radio that was merely off/unreachable fails the retry the same way it failed the
    /// first attempt (the blind escape is harmless noise to a Command-mode radio); a radio wedged
    /// by "Ignore Escape Sequence" ON still fails — that misconfiguration has no software recovery
    /// (see the class-level WARNING and the Transparent-readiness doctor).
    /// </remarks>
    public async Task EnterTransparentModeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref entered, 1, 0) != 0)
        {
            return;
        }
        try
        {
            try
            {
                await radio.EnterTransparentModeAsync(
                    options.EscapeChar, thsd: false, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No CCDI answer at the command baud — presume a stale Transparent pipe from a
                // session that never got to exit (see remarks). Escape it and retry once.
                if (options.TransparentBaud != options.CommandBaud)
                {
                    radio.SetSerialBaudRate(options.TransparentBaud);
                }
                await radio.EscapeTransparentBlindAsync(
                    options.EscapeChar, options.EscapeGuard, cancellationToken).ConfigureAwait(false);
                if (options.TransparentBaud != options.CommandBaud)
                {
                    radio.SetSerialBaudRate(options.CommandBaud);
                }
                await radio.EnterTransparentModeAsync(
                    options.EscapeChar, thsd: false, cancellationToken).ConfigureAwait(false);
            }
            if (options.TransparentBaud != options.CommandBaud)
            {
                radio.SetSerialBaudRate(options.TransparentBaud);
            }
        }
        catch
        {
            Interlocked.Exchange(ref entered, 0);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
        await SendFramedAsync(ax25, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    /// <remarks>
    /// Transparent mode gives no hardware transmit-completion signal, so completion is
    /// <em>modelled</em>: the frame is SLIP-framed and handed to the radio, then this waits out
    /// the lead-in plus the estimated airtime and returns <see cref="TxCompletion"/> with
    /// <see cref="TxCompletion.Completed"/> at the modelled end-of-air. It never throws
    /// <see cref="TimeoutException"/> (there is no external signal to miss). <paramref name="timeout"/>
    /// is accepted for interface conformance and ignored.
    /// </remarks>
    public async Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        _ = timeout;
        var timing = await SendFramedAsync(ax25, cancellationToken).ConfigureAwait(false);
        var remaining = timing.OnAirEnd - clock.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, clock, cancellationToken).ConfigureAwait(false);
        }
        return new TxCompletion(timing.Queued, timing.OnAirEnd);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inbound.Reader.ReadAllAsync(cancellationToken);

    private async Task<TaitTransparentTxTiming> SendFramedAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken)
    {
        byte[] framed = KissEncoder.Encode(SlipPort, KissCommand.Data, ax25.Span);
        DateTimeOffset queued;
        await txGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Serialise sends: the radio is a half-duplex byte pipe. The write is synchronous.
            queued = clock.GetUtcNow();
            await radio.SendTransparentAsync(framed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            txGate.Release();
        }

        var onAirStart = queued + options.LeadIn;
        var airtime = EstimateAirtime(framed.Length);
        var timing = new TaitTransparentTxTiming(
            queued, onAirStart, onAirStart + airtime, airtime, options.LeadIn, framed.Length);
        TxTiming?.Invoke(this, timing);
        return timing;
    }

    private void OnTransparentData(object? sender, ReadOnlyMemory<byte> data)
    {
        // Fired on the radio's single read-pump thread, so the decoder is touched serially.
        // Stamp delivery time once for this chunk — the instant the bytes that complete the
        // frame(s) arrived off the wire.
        var receivedAt = clock.GetUtcNow();
        foreach (var frame in decoder.Push(data.Span))
        {
            if (frame.Command != KissCommand.Data || frame.Payload.Length == 0)
            {
                continue;
            }
            // On-air size = the exact SLIP-framed byte count this frame was carried as
            // (SLIP encoding is deterministic from content, so re-encoding recovers it) —
            // symmetric with the TX-side airtime.
            var airtime = EstimateAirtime(KissEncoder.Encode(SlipPort, KissCommand.Data, frame.Payload).Length);
            var inboundFrame = new Ax25InboundFrame(
                frame.Payload,
                PortId: 0,
                ReceivedAt: receivedAt,
                // RSSI / SNR / noise-floor / carrier-rise / burst index are unavailable in
                // Transparent mode (no CCDI control channel); only the airtime is known.
                Radio: new RadioMetadata(EstimatedAirtime: airtime));
            inbound.Writer.TryWrite(inboundFrame);
        }
    }

    // A faulted radio (dead serial link / dropped head-end pipe — the pump broke on a hard IO
    // failure) can never deliver another byte: end the inbound stream so consumers see the drop.
    private void OnConnectionState(object? sender, TaitConnectionState state)
    {
        if (state == TaitConnectionState.Faulted)
        {
            inbound.Writer.TryComplete();
        }
    }

    private TimeSpan EstimateAirtime(int onAirByteCount) =>
        TimeSpan.FromSeconds(onAirByteCount * 8.0 / options.FfskBaud);

    /// <summary>
    /// Escape Transparent mode (restoring Command mode and the command baud) and, when this
    /// transport owns the radio, dispose it. See the class-level WARNING: if the radio's "Ignore
    /// Escape Sequence" is programmed on, the escape cannot succeed and recovery is a power cycle.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        radio.TransparentDataReceived -= OnTransparentData;
        radio.ConnectionStateChanged -= OnConnectionState;
        inbound.Writer.TryComplete();

        if (Interlocked.Exchange(ref entered, 0) == 1)
        {
            try
            {
                await radio.ExitTransparentModeAsync().ConfigureAwait(false);
                if (options.TransparentBaud != options.CommandBaud)
                {
                    radio.SetSerialBaudRate(options.CommandBaud);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or SocketException)
            {
                // Best-effort exit — the radio may already be gone / power-cycled, or the
                // head-end pipe already dead (SocketException / IOException from the socket or
                // the line-verb callback). Dispose the radio below regardless.
            }
        }

        if (ownsRadio)
        {
            await radio.DisposeAsync().ConfigureAwait(false);
        }
        txGate.Dispose();
    }
}

/// <summary>
/// Options for <see cref="TaitTransparentTransport"/>: the two serial rates (Command vs
/// Transparent terminal baud — equal on rigs where the radio is programmed the same both ways,
/// which needs no re-clock), the FFSK over-air baud used for airtime estimation, and the
/// transmit lead-in.
/// </summary>
public sealed record TaitTransparentTransportOptions
{
    /// <summary>The CCDI Command-mode serial rate — the rate the <c>t</c> entry / <c>+++</c> exit
    /// commands are clocked at. Default <see cref="TaitCcdiRadio.DefaultBaudRate"/> (28800).</summary>
    public int CommandBaud { get; init; } = TaitCcdiRadio.DefaultBaudRate;

    /// <summary>The Transparent-mode terminal serial rate (§1.8). When it differs from
    /// <see cref="CommandBaud"/> the port is re-clocked on enter and restored on exit; when equal
    /// (the common bench case) no re-clock happens. Default 28800.</summary>
    public int TransparentBaud { get; init; } = TaitCcdiRadio.DefaultBaudRate;

    /// <summary>
    /// The FFSK <b>over-air</b> baud used to estimate frame airtime (<c>on-air bytes × 8 ÷
    /// this</c>). Default 2400 — the TM8110's internal FFSK modem raw rate. Note effective
    /// throughput is lower (~1.8 kbit/s) because the radio adds per-≤46-byte-block framing this
    /// figure ignores, so airtime computed here is a floor; set it to a measured effective rate
    /// if a tighter estimate is wanted.
    /// </summary>
    public int FfskBaud { get; init; } = 2400;

    /// <summary>The modelled transmit lead-in: on-air start ≈ submit instant + this (radio
    /// key-up + FFSK preamble before data). A configurable estimate; default 100 ms. Calibrate
    /// per rig if the AX.25 engine needs tight on-air-start timestamps.</summary>
    public TimeSpan LeadIn { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>The Transparent-mode escape character (§1.7.2) — the byte sent ×3 (guarded by
    /// idle time) to leave Transparent. Default <c>'+'</c> (the <c>+++</c> sequence).</summary>
    public char EscapeChar { get; init; } = '+';

    /// <summary>The §1.7.2 idle guard either side of the escape burst when the enter path runs
    /// the stale-Transparent recovery (see <see cref="TaitTransparentTransport.EnterTransparentModeAsync"/>).
    /// Default 2.1 s (the protocol minimum). Tests pass a short value to keep fast.</summary>
    public TimeSpan EscapeGuard { get; init; } = TimeSpan.FromMilliseconds(2100);
}

/// <summary>
/// The timing envelope of one Transparent-mode transmission, raised on
/// <see cref="TaitTransparentTransport.TxTiming"/>. The transport owns the transmission, so these
/// are measured/derived at the source: <see cref="Queued"/> is the submit instant,
/// <see cref="OnAirStart"/> ≈ <see cref="Queued"/> + lead-in, and <see cref="OnAirEnd"/> ≈
/// <see cref="OnAirStart"/> + <see cref="EstimatedAirtime"/>.
/// </summary>
/// <param name="Queued">When the frame was handed to the radio (submit instant).</param>
/// <param name="OnAirStart">Estimated instant the data began leaving the antenna
/// (<see cref="Queued"/> + <see cref="LeadIn"/>).</param>
/// <param name="OnAirEnd">Estimated instant the data finished leaving the antenna
/// (<see cref="OnAirStart"/> + <see cref="EstimatedAirtime"/>).</param>
/// <param name="EstimatedAirtime">On-air duration: <see cref="OnAirByteCount"/> × 8 ÷ the FFSK
/// over-air baud.</param>
/// <param name="LeadIn">The lead-in applied (radio key-up + FFSK preamble).</param>
/// <param name="OnAirByteCount">The SLIP-framed byte count clocked through the modem (excludes the
/// radio's own per-block framing overhead, which the host cannot observe).</param>
public readonly record struct TaitTransparentTxTiming(
    DateTimeOffset Queued,
    DateTimeOffset OnAirStart,
    DateTimeOffset OnAirEnd,
    TimeSpan EstimatedAirtime,
    TimeSpan LeadIn,
    int OnAirByteCount);
