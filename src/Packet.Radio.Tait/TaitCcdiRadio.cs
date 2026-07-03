using System.Globalization;
using System.IO.Ports;
using System.Text;
using Packet.Radio.Tait.Ccdi;

namespace Packet.Radio.Tait;

/// <summary>
/// A Tait TM8100/TM8200 radio driven over its CCDI serial control channel (Command mode).
/// Surfaces what standard KISS cannot: receiver RSSI in dBm (per-poll, suitable for per-frame
/// attribution via <c>Packet.Radio.RssiTaggingTransport</c>), hardware carrier-sense (DCD)
/// edges as <see cref="CarrierSenseChanged"/> events, transmitter keying, and radio telemetry
/// (PA temperature, forward/reverse power).
/// </summary>
/// <remarks>
/// <para>
/// CCDI is a prompt-disciplined request/response protocol with unsolicited PROGRESS/ERROR
/// messages interleaved: this driver serialises commands, matches solicited responses to the
/// in-flight transaction, and routes everything else to events. Events fire on the read pump —
/// keep handlers fast and non-blocking.
/// </para>
/// <para>
/// Carrier-sense events require the radio to emit PROGRESS messages, which is per-session
/// radio state — call <see cref="SetProgressMessagesAsync"/> after opening. The radio must be
/// in Command mode (its power-up default when so programmed); Transparent-mode escape is not
/// attempted by this driver.
/// </para>
/// <para>
/// Verified against TM8110 (RUTYPE/RUMODEL/RUTIER <c>132</c>, CCDI 3.02, firmware 02.18.00.00).
/// On that firmware the CCDI TX-power set command answers "unsupported command", so
/// <see cref="RadioCapabilities.TxPowerControl"/> is not advertised.
/// </para>
/// </remarks>
public sealed class TaitCcdiRadio : IRadioControl, IDisposable
{
    /// <summary>The CCDI serial rate these radios are commonly programmed for. The radio's
    /// programmed rate wins — 1200 to 115200 are all possible (§1.8).</summary>
    public const int DefaultBaudRate = 28800;

    private readonly ISerialIo io;
    private readonly TaitCcdiRadioOptions options;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Thread pumpThread;
    private readonly object stateGate = new();
    private readonly Task? watchdogLoop;

    private Transaction? active;
    private bool? channelBusy;
    private bool weKeyedTransmitter;
    private char transparentEscape = '+';
    private TaitProtocolMode mode = TaitProtocolMode.Command;
    private TaitConnectionState connectionState = TaitConnectionState.Healthy;
    private DateTimeOffset lastInboundAt;
    private int consecutivePingFailures;
    private int disposed;

    private TaitCcdiRadio(ISerialIo io, TaitCcdiRadioOptions? options, TimeProvider? timeProvider)
    {
        this.io = io;
        this.options = options ?? new TaitCcdiRadioOptions();
        clock = timeProvider ?? TimeProvider.System;
        lastInboundAt = clock.GetUtcNow();
        pumpThread = new Thread(PumpReads) { IsBackground = true, Name = $"tait-ccdi:{io.PortName}" };
        pumpThread.Start();
        if (this.options.KeepAliveInterval is { } interval && interval > TimeSpan.Zero)
        {
            watchdogLoop = Task.Run(() => WatchdogAsync(interval, pumpCts.Token));
        }
    }

    /// <summary>The serial port this radio is attached to (e.g. <c>/dev/ttyUSB0</c>).</summary>
    public string PortName => io.PortName;

    /// <inheritdoc/>
    /// <remarks><see cref="RadioCapabilities.SideChannel"/> advertises the driver machinery
    /// (<see cref="TaitSdmSideChannel"/>); whether SDMs are enabled in the radio's programming
    /// needs a live probe (error 0/06 = disabled — cf. the tuning doctor's SDM probe).</remarks>
    public RadioCapabilities Capabilities =>
        RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense | RadioCapabilities.TransmitterControl |
        RadioCapabilities.SideChannel;

    /// <inheritdoc/>
    public bool? ChannelBusy
    {
        get
        {
            lock (stateGate)
            {
                return channelBusy;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;

    /// <summary>Fired when the radio reports its transmitter keying/unkeying (PROGRESS
    /// PTT activated/deactivated) — whether keyed by CCDI, the microphone, or a data PTT line.</summary>
    public event EventHandler<TransmitterStateChange>? TransmitterStateChanged;

    /// <summary>Every unsolicited PROGRESS message, decoded. The DCD and PTT edges also surface
    /// through <see cref="CarrierSenseChanged"/> / <see cref="TransmitterStateChanged"/>.</summary>
    public event EventHandler<CcdiProgressMessage>? ProgressReceived;

    /// <summary>Unsolicited ERROR messages (system errors outside any transaction).</summary>
    public event EventHandler<CcdiErrorMessage>? ErrorReceived;

    /// <summary>Every decoded inbound message, solicited or not — a diagnostics tap.</summary>
    public event EventHandler<CcdiMessage>? MessageReceived;

    /// <summary>Unsolicited RING messages — incoming Selcall / status / SDM / data calls.</summary>
    public event EventHandler<CcdiRingMessage>? RingReceived;

    /// <summary>
    /// Over-air delivery receipt for a sent SDM (PROGRESS type 1D, requires auto-ack enabled in
    /// both radios' programming): <c>Acknowledged</c> true = the addressed radio confirmed
    /// receipt; false = no acknowledgement within the radio's configured wait time — which a
    /// wrong/absent destination also produces. Hardware-verified both ways on the bench.
    /// </summary>
    public event EventHandler<TaitSdmReceipt>? SdmDeliveryReceipt;

    /// <summary>Health transitions from the built-in watchdog (see
    /// <see cref="TaitCcdiRadioOptions.KeepAliveInterval"/>) and from the read pump: a dead
    /// serial link or an unresponsive radio raises <see cref="TaitConnectionState.Faulted"/>;
    /// a subsequently answered ping raises <see cref="TaitConnectionState.Healthy"/> again.</summary>
    public event EventHandler<TaitConnectionState>? ConnectionStateChanged;

    /// <summary>Raw bytes from the radio while in Transparent mode (the radio's own FFSK/THSD
    /// modem as a byte pipe). Fires on the read pump.</summary>
    public event EventHandler<ReadOnlyMemory<byte>>? TransparentDataReceived;

    /// <summary>Which protocol interpreter the port is currently in: CCDI Command mode,
    /// Transparent (byte pipe), or CCR.</summary>
    public TaitProtocolMode Mode
    {
        get
        {
            lock (stateGate)
            {
                return mode;
            }
        }
    }

    /// <summary>Current health as judged by the watchdog / read pump.</summary>
    public TaitConnectionState ConnectionState
    {
        get
        {
            lock (stateGate)
            {
                return connectionState;
            }
        }
    }

    /// <summary>When the radio last said anything at all on the serial link.</summary>
    public DateTimeOffset LastInboundAt
    {
        get
        {
            lock (stateGate)
            {
                return lastInboundAt;
            }
        }
    }

    /// <summary>
    /// Open the named serial port (8N1, no flow control) and start the read pump. The radio is
    /// not touched — pair with <see cref="SetProgressMessagesAsync"/> to turn on DCD events.
    /// </summary>
    public static TaitCcdiRadio Open(
        string portName,
        int baudRate = DefaultBaudRate,
        TaitCcdiRadioOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        var serial = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 100,
            WriteTimeout = 1000,
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
        };
        serial.Open();
        return new TaitCcdiRadio(new SystemSerialIo(serial), options, timeProvider);
    }

    /// <summary>Test seam (InternalsVisibleTo <c>Packet.Radio.Tait.Tests</c>): drive the
    /// transaction engine and demux over a scripted <see cref="ISerialIo"/>.</summary>
    internal static TaitCcdiRadio OpenForTest(
        ISerialIo io, TaitCcdiRadioOptions? options = null, TimeProvider? timeProvider = null) =>
        new(io, options, timeProvider);

    /// <summary>Query MODEL, RADIO_SERIAL and RADIO_VERSIONS and assemble the radio's identity.</summary>
    public async Task<TaitRadioIdentity> QueryIdentityAsync(CancellationToken cancellationToken = default)
    {
        var model = await ExpectOneAsync<CcdiModelMessage>(
            new CcdiFrame('q', ""), cancellationToken).ConfigureAwait(false);
        var serial = await ExpectOneAsync<CcdiSerialMessage>(
            new CcdiFrame('q', "4"), cancellationToken).ConfigureAwait(false);
        var versions = await TransactAsync(
            new CcdiFrame('q', "3"), m => m is CcdiVersionMessage, minCount: 1,
            completeOnPrompt: false, quietTime: TimeSpan.FromMilliseconds(150), cancellationToken)
            .ConfigureAwait(false);

        return new TaitRadioIdentity(
            model.RuType, model.RuModel, model.RuTier, model.CcdiVersion, serial.SerialNumber,
            versions.OfType<CcdiVersionMessage>().ToDictionary(v => v.RecordNumber, v => v.Version));
    }

    /// <inheritdoc/>
    /// <remarks>CCTM query 064 — instantaneous ("raw") RSSI, resolution 0.1 dB.</remarks>
    public async ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default) =>
        await ReadCctmDecibelsAsync("064", cancellationToken).ConfigureAwait(false);

    /// <summary>CCTM query 063 — the radio's own sliding-average RSSI, resolution 0.1 dB.</summary>
    public async Task<float> ReadAveragedRssiDbmAsync(CancellationToken cancellationToken = default) =>
        await ReadCctmDecibelsAsync("063", cancellationToken).ConfigureAwait(false);

    /// <summary>CCTM query 047 — power-amplifier temperature.</summary>
    public async Task<TaitPaTemperature> ReadPaTemperatureAsync(CancellationToken cancellationToken = default)
    {
        var results = await TransactAsync(
            new CcdiFrame('q', "5047"), m => m is CcdiQueryResultMessage { CctmCommand: 47 }, minCount: 1,
            completeOnPrompt: false, quietTime: TimeSpan.FromMilliseconds(120), cancellationToken)
            .ConfigureAwait(false);
        var values = results.OfType<CcdiQueryResultMessage>().Select(r => r.AsInteger()).ToArray();
        return values.Length switch
        {
            // TM8100-series: temperature then ADC millivolts. TM8200: ADC millivolts only.
            >= 2 => new TaitPaTemperature(values[0], values[1]),
            1 => new TaitPaTemperature(null, values[0]),
            _ => new TaitPaTemperature(null, null),
        };
    }

    /// <summary>CCTM query 318 — forward power detector reading (raw, 0–1200 mV).</summary>
    public Task<int?> ReadForwardPowerAsync(CancellationToken cancellationToken = default) =>
        ReadCctmIntegerAsync("318", cancellationToken);

    /// <summary>CCTM query 319 — reverse power detector reading (raw, 0–1200 mV). Together with
    /// forward power this is a VSWR / antenna-health proxy while transmitting.</summary>
    public Task<int?> ReadReversePowerAsync(CancellationToken cancellationToken = default) =>
        ReadCctmIntegerAsync("319", cancellationToken);

    /// <inheritdoc/>
    /// <remarks>FUNCTION 9 (§1.9.3): CCDI-forced TX does NOT expire with the radio's TX timer,
    /// so this driver unkeys on dispose if the transmitter was left keyed through it.</remarks>
    public async ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        await TransactAsync(
            new CcdiFrame('f', transmit ? "91" : "90"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            weKeyedTransmitter = transmit;
        }
    }

    /// <summary>FUNCTION 0/4: enable (or disable) unsolicited PROGRESS output — required for
    /// <see cref="CarrierSenseChanged"/> and <see cref="TransmitterStateChanged"/> to fire.</summary>
    public Task SetProgressMessagesAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "041" : "040"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 8: activate/deactivate the monitor function (open squelch).</summary>
    public Task SetMonitorAsync(bool on, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', on ? "81" : "80"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 5: request (or cancel) CCDI receive-audio mute.</summary>
    public Task SetRxAudioMuteAsync(bool mute, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', mute ? "51" : "50"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>GO_TO_CHANNEL (§1.9.4): retune to a programmed conventional channel. Denied by
    /// the radio (not-ready error) while in emergency mode.</summary>
    /// <param name="channel">Channel number, 1–4 digits.</param>
    /// <param name="zone">Zone (TM8200 only); TM8100 radios reject a zone-qualified change.</param>
    /// <param name="cancellationToken">Cancels waiting for the radio's acknowledgement.</param>
    public Task GoToChannelAsync(int channel, int? zone = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channel, 9999);
        string parameters = zone is { } z
            ? string.Create(CultureInfo.InvariantCulture, $"{z:00}{channel:0000}")
            : channel.ToString(CultureInfo.InvariantCulture);
        return TransactAsync(
            new CcdiFrame('g', parameters), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    /// <summary>FUNCTION 0/5/2: report the current channel (solicited PROGRESS type 21).</summary>
    public async Task<TaitChannelReport> QueryCurrentChannelAsync(CancellationToken cancellationToken = default)
    {
        var results = await TransactAsync(
            new CcdiFrame('f', "052"),
            m => m is CcdiProgressMessage { Type: CcdiProgressType.UserInitiatedChannelChange },
            minCount: 1, completeOnPrompt: false, quietTime: null, cancellationToken).ConfigureAwait(false);
        var progress = (CcdiProgressMessage)results[0];
        return TaitChannelReport.Parse(progress.Para);
    }

    /// <summary>CANCEL (§1.9.1): abort the current action / clear state.</summary>
    public Task CancelAsync(TaitCancelType type = TaitCancelType.Call, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('c', ((int)type).ToString(CultureInfo.InvariantCulture)), matches: null,
            minCount: 0, completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>DIAL (§1.9.2): initiate Selcall or DTMF dialing on the current channel. Call
    /// progress arrives later as PROGRESS messages.</summary>
    public Task DialAsync(TaitDialType type, string number, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(number);
        if (number.Length > 32)
        {
            throw new ArgumentException("dial strings are limited to 32 digits (§1.9.2)", nameof(number));
        }
        return TransactAsync(
            new CcdiFrame('d', (int)type + number), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    /// <summary>
    /// SEND_ADAPTABLE_SDM (§1.9.8): transmit a short data message radio-to-radio over the air —
    /// no TNC involved; the radio's own FFSK modem carries it. The receiving radio raises
    /// PROGRESS 'FFSK data received' (and a RING for valid addressed SDMs) and buffers the
    /// message for <see cref="ReadBufferedSdmAsync"/>.
    /// </summary>
    /// <param name="dataMessageId">8-character destination data identity; '*' wildcards per
    /// character (e.g. <c>"12**5678"</c>).</param>
    /// <param name="message">Up to 32 characters (ASCII text with the default format).</param>
    /// <param name="leadInDelay">Carrier lead-in before data, 20 ms granularity, max 5.1 s.
    /// Null uses 100 ms.</param>
    /// <param name="cancellationToken">Cancels waiting for the radio's acknowledgement.</param>
    public Task SendSdmAsync(
        string dataMessageId, string message, TimeSpan? leadInDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataMessageId);
        ArgumentNullException.ThrowIfNull(message);
        if (dataMessageId.Length != 8)
        {
            throw new ArgumentException("the data message ID is exactly 8 characters (§1.9.8)", nameof(dataMessageId));
        }
        if (message.Length > 32)
        {
            throw new ArgumentException("plain SDMs are limited to 32 characters (§1.9.8)", nameof(message));
        }
        int leadInUnits = (int)Math.Clamp((leadInDelay ?? TimeSpan.FromMilliseconds(100)).TotalMilliseconds / 20, 1, 255);
        string parameters = string.Create(
            CultureInfo.InvariantCulture, $"{leadInUnits:X2}200{dataMessageId}{message}");
        return TransactAsync(
            new CcdiFrame('a', parameters), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    /// <summary>QUERY type 1 (§1.9.5): fetch the buffered received SDM. The radio's SDM buffer
    /// is one-deep and this read clears it. Returns <c>null</c> when nothing is buffered.</summary>
    public async Task<string?> ReadBufferedSdmAsync(CancellationToken cancellationToken = default)
    {
        var results = await TransactAsync(
            new CcdiFrame('q', "1"), m => m is CcdiSdmMessage, minCount: 1,
            completeOnPrompt: false, quietTime: null, cancellationToken).ConfigureAwait(false);
        string data = ((CcdiSdmMessage)results[0]).Data;
        return data.Length == 0 ? null : data;
    }

    /// <summary>QUERY type 7 (§1.9.5): dump the control-head display as a burst of
    /// <see cref="CcdiDisplayMessage"/> elements (start / text / icon / end). Radios without a
    /// display head answer with a parameter error.</summary>
    public async Task<IReadOnlyList<CcdiDisplayMessage>> QueryDisplayAsync(CancellationToken cancellationToken = default)
    {
        var results = await TransactAsync(
            new CcdiFrame('q', "70"), m => m is CcdiDisplayMessage, minCount: 1,
            completeOnPrompt: false, quietTime: TimeSpan.FromMilliseconds(200), cancellationToken)
            .ConfigureAwait(false);
        return results.OfType<CcdiDisplayMessage>().ToArray();
    }

    /// <summary>
    /// TRANSPARENT (§1.9.9 / §1.7): switch the radio into Transparent mode — the serial port
    /// becomes a byte pipe through the radio's own FFSK (1200/2400 bit/s) or THSD modem. From
    /// then on <see cref="TransparentDataReceived"/> fires for inbound data,
    /// <see cref="SendTransparentAsync"/> transmits, CCDI transactions are unavailable, and the
    /// radio emits no PROGRESS messages until <see cref="ExitTransparentModeAsync"/>.
    /// </summary>
    public async Task EnterTransparentModeAsync(
        char escapeChar = '+', bool thsd = false, CancellationToken cancellationToken = default)
    {
        await TransactAsync(
            new CcdiFrame('t', $"{escapeChar}{(thsd ? "H" : "0")}"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            mode = TaitProtocolMode.Transparent;
            transparentEscape = escapeChar;
        }
    }

    /// <summary>Send raw bytes over the air while in Transparent mode.</summary>
    public Task SendTransparentAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (Mode != TaitProtocolMode.Transparent)
        {
            throw new InvalidOperationException("the radio is not in Transparent mode");
        }
        byte[] buffer = data.ToArray();
        io.Write(buffer, 0, buffer.Length);
        return Task.CompletedTask;
    }

    /// <summary>Escape from Transparent mode (§1.7.2): 2 s idle, escape char ×3, 2 s idle —
    /// then Command mode is back. Takes a little over 4 s by protocol design.</summary>
    public async Task ExitTransparentModeAsync(CancellationToken cancellationToken = default)
    {
        char esc;
        lock (stateGate)
        {
            if (mode != TaitProtocolMode.Transparent)
            {
                throw new InvalidOperationException("the radio is not in Transparent mode");
            }
            esc = transparentEscape;
        }
        await Task.Delay(TimeSpan.FromMilliseconds(2100), clock, cancellationToken).ConfigureAwait(false);
        byte[] escape = [(byte)esc, (byte)esc, (byte)esc];
        io.Write(escape, 0, escape.Length);
        await Task.Delay(TimeSpan.FromMilliseconds(2100), clock, cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            mode = TaitProtocolMode.Command;
        }
    }

    /// <summary>
    /// FUNCTION 0/0 (§2.5.1, TM8100 only): switch the radio into CCR (Computer-Controlled
    /// Radio) mode — the run-time channel-programming interpreter: direct RX/TX frequency in
    /// Hz, TX power, bandwidth, CTCSS/DCS, Selcall, volume. The returned session owns the port
    /// until <see cref="TaitCcrSession.ExitAsync"/> (which soft-resets the radio); CCDI
    /// commands are unavailable meanwhile, and nothing configured in CCR survives the exit.
    /// </summary>
    public async Task<TaitCcrSession> EnterCcrModeAsync(CancellationToken cancellationToken = default)
    {
        // Entry is acknowledged by the unsolicited CCR banner M01R00 (§2.5.3), not a prompt.
        var results = await TransactAsync(
            new CcdiFrame('f', "00"), m => m is CcrNotificationMessage { Kind: 'R' }, minCount: 1,
            completeOnPrompt: false, quietTime: null, cancellationToken).ConfigureAwait(false);
        _ = results;
        lock (stateGate)
        {
            mode = TaitProtocolMode.Ccr;
        }
        return new TaitCcrSession(this);
    }

    /// <summary>
    /// Liveness probe: a lightweight query appropriate to the current mode (RSSI read in
    /// Command mode, pulse in CCR). Returns <c>false</c> on timeout instead of throwing. The
    /// built-in watchdog calls this when the link has been quiet for
    /// <see cref="TaitCcdiRadioOptions.KeepAliveInterval"/>.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            switch (Mode)
            {
                case TaitProtocolMode.Command:
                    await ReadRssiDbmAsync(cancellationToken).ConfigureAwait(false);
                    return true;
                case TaitProtocolMode.Ccr:
                    await TransactCcrAsync(
                        new CcdiFrame('Q', "P"), m => m is CcrPulseResultMessage, cancellationToken)
                        .ConfigureAwait(false);
                    return true;
                default:
                    return true; // Transparent mode has no in-band probe; the pump watches bytes.
            }
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Escape hatch for CCDI commands this driver doesn't model: send
    /// <c>[ident][params]</c> and collect responses until <paramref name="quietTime"/> passes
    /// with nothing new (checksum and framing are handled for you). Unsolicited PROGRESS
    /// messages still route to events, not to the returned list.
    /// </summary>
    public Task<IReadOnlyList<CcdiMessage>> TransactRawAsync(
        char ident, string parameters, TimeSpan? quietTime = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return TransactAsync(
            new CcdiFrame(ident, parameters), m => m is not CcdiProgressMessage, minCount: 0,
            completeOnPrompt: true, quietTime: quietTime ?? TimeSpan.FromMilliseconds(150), cancellationToken);
    }

    private async Task<float> ReadCctmDecibelsAsync(string cctm, CancellationToken cancellationToken)
    {
        var result = await ExpectCctmAsync(cctm, cancellationToken).ConfigureAwait(false);
        return result.AsDecibels()
            ?? throw new FormatException($"CCTM {cctm} result '{result.Value}' is not a 0.1 dB integer");
    }

    private async Task<int?> ReadCctmIntegerAsync(string cctm, CancellationToken cancellationToken)
    {
        var result = await ExpectCctmAsync(cctm, cancellationToken).ConfigureAwait(false);
        return result.AsInteger();
    }

    /// <summary>CCR-mode transaction: send one CCR command and await its matching response (or
    /// a NAK, raised as <see cref="TaitCcrNakException"/>). Used by <see cref="TaitCcrSession"/>.</summary>
    internal async Task<CcdiMessage> TransactCcrAsync(
        CcdiFrame command, Func<CcdiMessage, bool> matches, CancellationToken cancellationToken)
    {
        if (Mode != TaitProtocolMode.Ccr)
        {
            throw new InvalidOperationException("the radio is not in CCR mode");
        }
        var results = await TransactCoreAsync(
            command, m => matches(m) || m is CcrNakMessage, minCount: 1,
            completeOnPrompt: false, quietTime: null, cancellationToken).ConfigureAwait(false);
        if (results[0] is CcrNakMessage nak)
        {
            throw new TaitCcrNakException(command.Encode(), nak);
        }
        return results[0];
    }

    internal void OnCcrExited()
    {
        lock (stateGate)
        {
            mode = TaitProtocolMode.Command;
        }
    }

    private async Task<CcdiQueryResultMessage> ExpectCctmAsync(string cctm, CancellationToken cancellationToken)
    {
        int command = int.Parse(cctm, CultureInfo.InvariantCulture);
        var results = await TransactAsync(
            new CcdiFrame('q', "5" + cctm), m => m is CcdiQueryResultMessage r && r.CctmCommand == command,
            minCount: 1, completeOnPrompt: false, quietTime: null, cancellationToken).ConfigureAwait(false);
        return (CcdiQueryResultMessage)results[0];
    }

    private async Task<T> ExpectOneAsync<T>(CcdiFrame command, CancellationToken cancellationToken)
        where T : CcdiMessage
    {
        var results = await TransactAsync(
            command, m => m is T, minCount: 1, completeOnPrompt: false, quietTime: null, cancellationToken)
            .ConfigureAwait(false);
        return (T)results[0];
    }

    private Task<IReadOnlyList<CcdiMessage>> TransactAsync(
        CcdiFrame command,
        Func<CcdiMessage, bool>? matches,
        int minCount,
        bool completeOnPrompt,
        TimeSpan? quietTime,
        CancellationToken cancellationToken)
    {
        var current = Mode;
        if (current != TaitProtocolMode.Command)
        {
            throw new InvalidOperationException(
                $"CCDI commands are unavailable while the radio is in {current} mode");
        }
        return TransactCoreAsync(command, matches, minCount, completeOnPrompt, quietTime, cancellationToken);
    }

    private async Task<IReadOnlyList<CcdiMessage>> TransactCoreAsync(
        CcdiFrame command,
        Func<CcdiMessage, bool>? matches,
        int minCount,
        bool completeOnPrompt,
        TimeSpan? quietTime,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        string encoded = command.Encode();
        await commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var txn = new Transaction(matches, minCount, completeOnPrompt);
            lock (stateGate)
            {
                active = txn;
            }

            try
            {
                byte[] wire = command.EncodeToBytes();
                io.Write(wire, 0, wire.Length);

                await txn.Done.Task.WaitAsync(options.TransactionTimeout, clock, cancellationToken)
                    .ConfigureAwait(false);

                if (completeOnPrompt && txn.Error is null)
                {
                    // Observed on hardware: the radio prompts BEFORE the ERROR of a rejected
                    // command (a programming-disabled SDM answers ".e03006A2\r." — prompt
                    // first). Give a trailing rejection a beat to arrive before declaring
                    // success; at 28800 baud it lands within ~10 ms.
                    await Task.Delay(options.PromptErrorGrace, clock, cancellationToken).ConfigureAwait(false);
                }

                if (txn.Error is { } error)
                {
                    throw new TaitCcdiException(encoded, error);
                }

                if (quietTime is { } quiet && quiet > TimeSpan.Zero)
                {
                    // Multi-message responses (VERSIONS, PA temperature) have no count-ahead
                    // marker: collect until the radio goes quiet.
                    int seen;
                    do
                    {
                        seen = txn.CollectedCount;
                        await Task.Delay(quiet, clock, cancellationToken).ConfigureAwait(false);
                    }
                    while (txn.CollectedCount != seen);
                }

                return txn.Snapshot();
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"no CCDI response to '{encoded}' within {options.TransactionTimeout.TotalSeconds:0.#}s on {PortName}");
            }
            finally
            {
                lock (stateGate)
                {
                    active = null;
                }
            }
        }
        finally
        {
            commandGate.Release();
        }
    }

    private void PumpReads()
    {
        var buffer = new byte[512];
        var line = new StringBuilder(64);
        while (!pumpCts.IsCancellationRequested)
        {
            int read;
            try
            {
                read = io.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (Exception ex)
            {
                // Port closed under us (dispose path) or hard IO failure: stop pumping. That is
                // an interface hang-up, not a quiet channel — surface it.
                if (Volatile.Read(ref disposed) == 0)
                {
                    MarkFaulted(new IOException($"serial read failed on {PortName}", ex));
                }
                break;
            }

            if (read > 0)
            {
                lock (stateGate)
                {
                    lastInboundAt = clock.GetUtcNow();
                    consecutivePingFailures = 0;
                }
            }

            if (Mode == TaitProtocolMode.Transparent)
            {
                TransparentDataReceived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
                continue;
            }

            for (int i = 0; i < read; i++)
            {
                char c = (char)buffer[i];
                switch (c)
                {
                    case '\r':
                        if (line.Length > 0)
                        {
                            OnLine(line.ToString());
                            line.Clear();
                        }
                        break;
                    case '\n':
                    case '\x11': // XON
                    case '\x13': // XOFF — the radio may use software flow control (§1.6.1)
                        break;
                    case '.' when line.Length == 0:
                        OnPrompt();
                        break;
                    default:
                        line.Append(c);
                        break;
                }
            }
        }
    }

    private void OnLine(string rawLine)
    {
        if (!CcdiFrame.TryParse(rawLine, out var frame))
        {
            return; // line noise / partial line from before open — normal on an async serial link
        }

        var message = CcdiMessage.Decode(frame);
        MessageReceived?.Invoke(this, message);

        bool consumedByTransaction = false;
        lock (stateGate)
        {
            if (active is { } txn)
            {
                if (message is CcdiErrorMessage error)
                {
                    txn.Error = error;
                    txn.Done.TrySetResult(true);
                    consumedByTransaction = true;
                }
                else if (txn.Matches?.Invoke(message) == true)
                {
                    txn.Add(message);
                    consumedByTransaction = true;
                }
            }
        }

        if (!consumedByTransaction)
        {
            RouteUnsolicited(message);
        }
    }

    private void OnPrompt()
    {
        lock (stateGate)
        {
            if (active is { CompleteOnPrompt: true } txn)
            {
                txn.Done.TrySetResult(true);
            }
        }
    }

    private void RouteUnsolicited(CcdiMessage message)
    {
        switch (message)
        {
            case CcdiProgressMessage progress:
                if (progress.Type is CcdiProgressType.ReceiverBusy or CcdiProgressType.ReceiverNotBusy)
                {
                    bool busy = progress.Type == CcdiProgressType.ReceiverBusy;
                    lock (stateGate)
                    {
                        channelBusy = busy;
                    }
                    CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(busy, clock.GetUtcNow()));
                }
                else if (progress.Type == CcdiProgressType.SdmAutoAcknowledge)
                {
                    SdmDeliveryReceipt?.Invoke(this, new TaitSdmReceipt(
                        Acknowledged: progress.Para.StartsWith('1'), At: clock.GetUtcNow()));
                }
                else if (progress.Type is CcdiProgressType.PttActivated or CcdiProgressType.PttDeactivated)
                {
                    TransmitterStateChanged?.Invoke(this, new TransmitterStateChange(
                        progress.Type == CcdiProgressType.PttActivated, clock.GetUtcNow()));
                }
                ProgressReceived?.Invoke(this, progress);
                break;
            case CcdiErrorMessage error:
                ErrorReceived?.Invoke(this, error);
                break;
            case CcdiRingMessage ring:
                RingReceived?.Invoke(this, ring);
                break;
            default:
                break;
        }
    }

    private async Task WatchdogAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var checkEvery = TimeSpan.FromTicks(Math.Max(interval.Ticks / 2, TimeSpan.TicksPerSecond));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(checkEvery, clock, cancellationToken).ConfigureAwait(false);
                bool quiet;
                lock (stateGate)
                {
                    quiet = clock.GetUtcNow() - lastInboundAt >= interval;
                }
                if (!quiet || Mode == TaitProtocolMode.Transparent)
                {
                    continue;
                }

                bool ok = await PingAsync(cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    MarkHealthy();
                    continue;
                }

                int failures;
                lock (stateGate)
                {
                    failures = ++consecutivePingFailures;
                }
                if (failures >= options.FaultAfterConsecutivePingFailures)
                {
                    MarkFaulted(new TimeoutException(
                        $"radio on {PortName} missed {failures} consecutive keep-alive probes"));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // Mode changed under a probe (e.g. CCR entry raced the watchdog) — next tick adapts.
        }
    }

    private void MarkFaulted(Exception cause)
    {
        bool changed;
        Transaction? pending;
        lock (stateGate)
        {
            changed = connectionState != TaitConnectionState.Faulted;
            connectionState = TaitConnectionState.Faulted;
            pending = active;
        }
        pending?.Done.TrySetException(cause);
        if (changed)
        {
            ConnectionStateChanged?.Invoke(this, TaitConnectionState.Faulted);
        }
    }

    private void MarkHealthy()
    {
        bool changed;
        lock (stateGate)
        {
            changed = connectionState != TaitConnectionState.Healthy;
            connectionState = TaitConnectionState.Healthy;
            consecutivePingFailures = 0;
        }
        if (changed)
        {
            ConnectionStateChanged?.Invoke(this, TaitConnectionState.Healthy);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        pumpCts.Cancel();

        bool mustUnkey;
        bool mustExitCcr;
        lock (stateGate)
        {
            mustUnkey = weKeyedTransmitter;
            mustExitCcr = mode == TaitProtocolMode.Ccr;
        }
        if (mustExitCcr)
        {
            // Leaving the radio parked in CCR mode makes it deaf to CCDI until someone resets
            // it; the CCR exit (E) is itself a soft reset, so it also restores channel config.
            try
            {
                byte[] exit = new CcdiFrame('E', "").EncodeToBytes();
                io.Write(exit, 0, exit.Length);
            }
            catch
            {
            }
        }
        if (mustUnkey)
        {
            // A radio latched in TX by FUNCTION 9 stays keyed until told otherwise (§1.9.3
            // note 5) — best-effort unkey is non-negotiable on the way out.
            try
            {
                byte[] unkey = new CcdiFrame('f', "90").EncodeToBytes();
                io.Write(unkey, 0, unkey.Length);
            }
            catch
            {
            }
        }

        io.Dispose();
        if (Thread.CurrentThread != pumpThread)
        {
            pumpThread.Join(TimeSpan.FromSeconds(2));
        }
        pumpCts.Dispose();
        commandGate.Dispose();
    }

    private sealed class Transaction(Func<CcdiMessage, bool>? matches, int minCount, bool completeOnPrompt)
    {
        private readonly List<CcdiMessage> collected = [];

        public Func<CcdiMessage, bool>? Matches { get; } = matches;

        public bool CompleteOnPrompt { get; } = completeOnPrompt;

        public TaskCompletionSource<bool> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CcdiErrorMessage? Error { get; set; }

        public int CollectedCount
        {
            get
            {
                lock (collected)
                {
                    return collected.Count;
                }
            }
        }

        public void Add(CcdiMessage message)
        {
            lock (collected)
            {
                collected.Add(message);
                if (collected.Count >= minCount)
                {
                    Done.TrySetResult(true);
                }
            }
        }

        public IReadOnlyList<CcdiMessage> Snapshot()
        {
            lock (collected)
            {
                return [.. collected];
            }
        }
    }
}

/// <summary>An over-air SDM delivery receipt (PROGRESS 1D).</summary>
/// <param name="Acknowledged"><c>true</c> = the destination radio auto-acknowledged;
/// <c>false</c> = no ack within the configured wait (wrong destination looks the same).</param>
/// <param name="At">When the radio reported it, stamped from the driver's clock.</param>
public readonly record struct TaitSdmReceipt(bool Acknowledged, DateTimeOffset At);

/// <summary>One transmitter keying edge reported by the radio (PROGRESS PTT
/// activated/deactivated).</summary>
/// <param name="Transmitting"><c>true</c> = transmitter keyed; <c>false</c> = unkeyed.</param>
/// <param name="At">When the radio reported the edge, stamped from the driver's clock.</param>
public readonly record struct TransmitterStateChange(bool Transmitting, DateTimeOffset At);
