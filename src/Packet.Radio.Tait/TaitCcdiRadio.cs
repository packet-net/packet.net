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
    private DateTimeOffset? busySince;
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
        // One loop serves both watchdog duties: the quiet-link keep-alive ping and the
        // stale-busy re-validation (#576). Either being enabled starts it.
        bool keepAlive = this.options.KeepAliveInterval is { } ka && ka > TimeSpan.Zero;
        bool staleBusy = this.options.StaleBusyRevalidateAfter is { } sb && sb > TimeSpan.Zero;
        if (keepAlive || staleBusy)
        {
            watchdogLoop = Task.Run(() => WatchdogAsync(pumpCts.Token));
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
    /// <remarks>Reset to <c>null</c> (unknown ⇒ the CSMA gate fails open) when the link faults
    /// (#576) and when a stale latched-busy fails its re-validation probe
    /// (<see cref="TaitCcdiRadioOptions.StaleBusyRevalidateAfter"/>).</remarks>
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

    /// <summary>Unsolicited GET_SDM messages — received SDMs pushed by the radio without a
    /// QUERY, which happens when SDM output-on-reception is enabled
    /// (<see cref="SetSdmOutputOnReceptionAsync"/>). Solicited reads via
    /// <see cref="ReadBufferedSdmAsync"/> do NOT raise this.</summary>
    public event EventHandler<CcdiSdmMessage>? SdmReceived;

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

    /// <summary>
    /// Open a Tait radio whose CCDI serial port is bridged as a raw binary TCP pipe by a remote
    /// head-end (the split-station topology — see <c>docs/research/split-station-rf-headend.md</c>)
    /// and start the read pump. The socket carries the CCDI/PROGRESS byte stream unchanged, so
    /// carrier-sense (DCD) edges, RSSI reads, SDM and every transaction work exactly as over a
    /// local port. Like <see cref="Open"/>, the radio itself is not touched — pair with
    /// <see cref="SetProgressMessagesAsync"/> to turn on DCD events.
    /// </summary>
    /// <param name="host">Head-end host bridging the serial port.</param>
    /// <param name="port">Head-end TCP port for this radio's raw byte pipe.</param>
    /// <param name="baudRate">The CCDI line rate the head-end should clock the physical port at.
    /// Applied at open only when <paramref name="setBaud"/> is supplied (the head-end owns the
    /// clock); with the default null callback the head-end's current clock is trusted as-is.</param>
    /// <param name="setBaud">Async line-control callback the data-plane <c>SetBaudRate</c> routes
    /// to — the out-of-band head-end verb that re-clocks the physical port (the data socket is a
    /// pure binary pipe and cannot carry line-rate changes). <c>null</c> (the default) makes baud
    /// a no-op: a plain raw pipe works today, and the verb lands in a later stage.</param>
    /// <param name="options">Behavioural knobs; null uses defaults.</param>
    /// <param name="timeProvider">Clock (test seam); null uses the system clock.</param>
    /// <param name="cancellationToken">Cancels the connect (and the optional initial re-clock).</param>
    public static async Task<TaitCcdiRadio> OpenTcp(
        string host,
        int port,
        int baudRate = DefaultBaudRate,
        Func<int, CancellationToken, Task>? setBaud = null,
        TaitCcdiRadioOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        var io = await TcpSerialIo.ConnectAsync(
            host, port, setBaud, readTimeout: null, readIdleTimeout: options?.ReadIdleTimeout,
            timeProvider, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (setBaud is not null)
            {
                // Clock the remote port to the requested CCDI rate before any transaction (the
                // head-end owns the physical UART). No callback ⇒ the head-end's clock is trusted.
                await setBaud(baudRate, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            io.Dispose();
            throw;
        }
        return new TaitCcdiRadio(io, options, timeProvider);
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

    /// <summary>FUNCTION 0/1: enable (or disable) CCDI volume control — required before
    /// <see cref="SetVolumeAsync"/> takes effect.</summary>
    public Task SetVolumeControlAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "011" : "010"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 0/2: set the speaker volume, 0 (off) to 25 (loudest). This is the
    /// 0–25 scale of TM8100 v2.06+ and all TM8200 firmware (§1.9.3 footnote c); pre-2.06
    /// TM8100s used 0–9. Enable <see cref="SetVolumeControlAsync"/> first.</summary>
    public Task SetVolumeAsync(int level, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(level);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 25);
        return TransactAsync(
            new CcdiFrame('f', string.Create(CultureInfo.InvariantCulture, $"02{level}")),
            matches: null, minCount: 0, completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    /// <summary>FUNCTION 0/3: enable (or disable) Selcall output — RING messages for incoming
    /// Selcall calls, and the destination-ID prefix on <see cref="CcdiRingMessage.CallerId"/>.</summary>
    public Task SetSelcallRingOutputAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "031" : "030"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 0/4/2–3: enable (or disable, the power-up default) KEYPRESS progress
    /// messages (PROGRESS type 23 — key number + down/up/short/long). TM8200 v2.05+ only
    /// (§1.9.3 footnote d); a TM8100 answers with an error.</summary>
    public Task SetKeypressProgressMessagesAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "042" : "043"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 0/5/0–1: enable (or disable, the power-up default) unsolicited
    /// channel-change PROGRESS messages (type 21) on every retune. Distinct from the solicited
    /// one-shot report of <see cref="QueryCurrentChannelAsync"/> (0/5/2).</summary>
    public Task SetChannelProgressMessagesAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "051" : "050"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 1/0: when enabled, the radio pushes each received SDM to the serial
    /// port as an unsolicited GET_SDM message (surfaced via <see cref="SdmReceived"/>) — no
    /// <see cref="ReadBufferedSdmAsync"/> QUERY required.</summary>
    public Task SetSdmOutputOnReceptionAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "101" : "100"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 1/1: enable (or disable) SDM caller-ID encode — the sender's ID goes
    /// out as a separate SDM before the SDM itself.</summary>
    public Task SetSdmCallerIdEncodeAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "111" : "110"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 1/2: enable (or disable) SDM caller-ID decode — a received caller-ID
    /// SDM is decoded before the incoming SDM it precedes.</summary>
    public Task SetSdmCallerIdDecodeAsync(bool enable, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', enable ? "121" : "120"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 4: user-controls lockout — selectively disable the radio's front
    /// panel while under computer control. The power-up default is
    /// <see cref="TaitUserControls.EnableAll"/>.</summary>
    public Task SetUserControlsAsync(TaitUserControls mode, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', string.Create(CultureInfo.InvariantCulture, $"4{(int)mode}")),
            matches: null, minCount: 0, completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>FUNCTION 7: activate (or deactivate) validation of CTCSS/DCS subaudible
    /// signaling on incoming FFSK data — when active, data is only processed if the subaudible
    /// signaling matches (only effective on channels programmed for it). The power-up default
    /// follows the radio's 'Ignore DCS/CTCSS' programming.</summary>
    public Task SetSubaudibleValidationAsync(bool validate, CancellationToken cancellationToken = default) =>
        TransactAsync(
            new CcdiFrame('f', validate ? "71" : "70"), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);

    /// <summary>
    /// FUNCTION 3: simulate a key press on the radio's front panel.
    /// </summary>
    /// <param name="key">Which key (the PROGRESS type-23 key-number table).</param>
    /// <param name="durationCode">Press duration code: 0 = constantly off (release),
    /// 1–8 = that many eighths of a second, 9 = constantly on (hold until a later 0).</param>
    /// <param name="cancellationToken">Cancels waiting for the radio's acknowledgement.</param>
    public Task SimulateKeyPressAsync(TaitKey key, int durationCode, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(durationCode);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(durationCode, 9);
        return TransactAsync(
            new CcdiFrame('f', string.Create(
                CultureInfo.InvariantCulture, $"3{(byte)key:X2}{durationCode}")),
            matches: null, minCount: 0, completeOnPrompt: true, quietTime: null, cancellationToken);
    }

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
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > PlainSdmMaxLength)
        {
            throw new ArgumentException("plain SDMs are limited to 32 characters (§1.9.8)", nameof(message));
        }
        return SendAdaptableSdmAsync(
            dataMessageId, message, gfi: '2', sfi: "00", leadInDelay, cancellationToken);
    }

    /// <summary>
    /// Extended SDM (§1.9.8, GFI 2 / SFI 04): a text short data message of up to 128 characters
    /// in ONE command — the radio itself splits it into multiple over-air SDMs (continuations
    /// tagged SFI 05) and the <em>receiving</em> radio reassembles them natively: its one-deep
    /// buffer presents the complete message in a single RING /
    /// <see cref="ReadBufferedSdmAsync"/> read.
    /// </summary>
    /// <remarks>
    /// Hardware-verified TM8110↔TM8110 (CCDI 03.02, 2026-07-03): 100- and 128-character
    /// messages each transmitted as two FFSK bursts (two 'FFSK data received' PROGRESS
    /// messages at the receiver), then one RING with the fully reassembled message buffered;
    /// the SDM auto-acknowledge delivery receipt (PROGRESS 1D) covers the whole message.
    /// Reading the buffer mid-reassembly harmlessly returns "no SDM buffered" and does not
    /// disturb the reassembly.
    /// </remarks>
    /// <param name="dataMessageId">8-character destination data identity; '*' wildcards per
    /// character.</param>
    /// <param name="message">Up to 128 characters of text.</param>
    /// <param name="leadInDelay">Carrier lead-in before data, 20 ms granularity, max 5.1 s.
    /// Null uses 100 ms.</param>
    /// <param name="cancellationToken">Cancels waiting for the radio's acknowledgement.</param>
    public Task SendExtendedSdmAsync(
        string dataMessageId, string message, TimeSpan? leadInDelay = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > ExtendedSdmMaxLength)
        {
            throw new ArgumentException("extended SDMs are limited to 128 characters (§1.9.8)", nameof(message));
        }
        return SendAdaptableSdmAsync(
            dataMessageId, message, gfi: '2', sfi: "04", leadInDelay, cancellationToken);
    }

    /// <summary>
    /// Binary SDM (§1.9.8, GFI 1): a short data message of raw bytes. Up to 32 bytes go as a
    /// plain SDM (SFI 00); 33–128 bytes go as an extended SDM (SFI 04 — the radios split and
    /// reassemble natively, see <see cref="SendExtendedSdmAsync"/>). Hardware-verified
    /// TM8110↔TM8110: bytes arrive verbatim in the receiver's buffer, both plain and extended.
    /// </summary>
    /// <remarks>
    /// The bytes <c>0x0D</c> (CR — the CCDI frame terminator), <c>0x0A</c>, <c>0x11</c> and
    /// <c>0x13</c> (XON/XOFF — the link may use software flow control, §1.6.1) are refused:
    /// they would corrupt CCDI line framing on the serial leg. All other values 0x00–0xFF are
    /// allowed.
    /// </remarks>
    /// <param name="dataMessageId">8-character destination data identity; '*' wildcards per
    /// character.</param>
    /// <param name="message">1–128 bytes; see remarks for the four refused values.</param>
    /// <param name="leadInDelay">Carrier lead-in before data, 20 ms granularity, max 5.1 s.
    /// Null uses 100 ms.</param>
    /// <param name="cancellationToken">Cancels waiting for the radio's acknowledgement.</param>
    public Task SendBinarySdmAsync(
        string dataMessageId, ReadOnlyMemory<byte> message, TimeSpan? leadInDelay = null,
        CancellationToken cancellationToken = default)
    {
        if (message.Length > ExtendedSdmMaxLength)
        {
            throw new ArgumentException("binary SDMs are limited to 128 bytes (§1.9.8)", nameof(message));
        }
        var span = message.Span;
        var chars = new char[span.Length];
        for (int i = 0; i < span.Length; i++)
        {
            byte b = span[i];
            if (b is 0x0D or 0x0A or 0x11 or 0x13)
            {
                throw new ArgumentException(
                    $"binary SDM byte 0x{b:X2} at offset {i} would corrupt CCDI line framing " +
                    "(CR/LF terminate frames; XON/XOFF may be software flow control, §1.6.1)",
                    nameof(message));
            }
            chars[i] = (char)b;
        }
        string sfi = message.Length > PlainSdmMaxLength ? "04" : "00";
        return SendAdaptableSdmAsync(
            dataMessageId, new string(chars), gfi: '1', sfi, leadInDelay, cancellationToken);
    }

    /// <summary>
    /// SEND_SDM (§1.9.7): the legacy fixed-format SDM send — kept by the protocol (and this
    /// driver) only for backwards compatibility with pre-adaptable-SDM peers;
    /// <see cref="SendSdmAsync"/> supersedes it. Hardware-verified TM8110↔TM8110: delivered,
    /// RINGed and auto-acknowledged exactly like a plain adaptable SDM.
    /// </summary>
    /// <param name="dataMessageId">8-character destination data identity; '*' wildcards per
    /// character.</param>
    /// <param name="message">Up to 32 characters of text.</param>
    /// <param name="leadInDelay">Carrier lead-in before data, 20 ms granularity, max 5.1 s.
    /// Null uses 100 ms.</param>
    /// <param name="cancellationToken">Cancels waiting for the radio's acknowledgement.</param>
    public Task SendLegacySdmAsync(
        string dataMessageId, string message, TimeSpan? leadInDelay = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDataMessageId(dataMessageId);
        ArgumentNullException.ThrowIfNull(message);
        if (message.Length > PlainSdmMaxLength)
        {
            throw new ArgumentException("SEND_SDM messages are limited to 32 characters (§1.9.7)", nameof(message));
        }
        string parameters = string.Create(
            CultureInfo.InvariantCulture, $"{LeadInUnits(leadInDelay):X2}{dataMessageId}{message}");
        return TransactAsync(
            new CcdiFrame('s', parameters), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    /// <summary>
    /// Build (without sending) a CCR-over-SDM frame (§1.9.8 "CCR SDM", GFI 2 / SFI 03): an
    /// adaptable SDM whose message is stripped and executed as a <b>CCR command</b> by a
    /// receiving radio that supports CCR and is currently in CCR mode. The
    /// <paramref name="ccrCommand"/> travels in its full CCR wire form (ident + size + params +
    /// checksum, no CR — the manual's worked example carries <c>M01D0E</c>).
    /// </summary>
    /// <remarks>
    /// <b>⚠ This is remote radio CONTROL, not messaging</b> — see
    /// <see cref="UnsafeSendCcrOverSdmAsync"/> for the warnings that apply to actually
    /// transmitting one. Unit-tested against the manual's worked example
    /// (<c>a130520312345678M01D0E36</c>).
    /// </remarks>
    /// <param name="dataMessageId">8-character destination data identity; '*' wildcards per
    /// character.</param>
    /// <param name="ccrCommand">The CCR command to execute at the receiver (e.g.
    /// <c>new CcdiFrame('M', "D")</c> = monitor on).</param>
    /// <param name="leadInDelay">Carrier lead-in before data, 20 ms granularity, max 5.1 s.
    /// Null uses 100 ms.</param>
    [System.Diagnostics.CodeAnalysis.Experimental(CcrOverSdmDiagnosticId)]
    public static CcdiFrame UnsafeBuildCcrOverSdmFrame(
        string dataMessageId, CcdiFrame ccrCommand, TimeSpan? leadInDelay = null)
    {
        ValidateDataMessageId(dataMessageId);
        string encodedCcr = ccrCommand.Encode();
        if (encodedCcr.Length > PlainSdmMaxLength)
        {
            throw new ArgumentException(
                "CCR-over-SDM commands are limited to 32 encoded characters (§1.9.8)", nameof(ccrCommand));
        }
        string parameters = string.Create(
            CultureInfo.InvariantCulture, $"{LeadInUnits(leadInDelay):X2}203{dataMessageId}{encodedCcr}");
        return new CcdiFrame('a', parameters);
    }

    /// <summary>
    /// Transmit a CCR command <b>into another radio</b> over the air (§1.9.8 "CCR SDM",
    /// GFI 2 / SFI 03): the addressed radio — which must already be in CCR mode — strips the
    /// SDM's message and executes it as a CCR command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>⚠ This is remote radio CONTROL, not messaging.</b> A CCR command arriving over the
    /// air can retune the receiving radio's frequencies, change its TX power, or key its
    /// transmitter — with no operator at the receiving end and <b>no consent handshake in the
    /// protocol</b>. Only ever aim it at a radio you have explicit authority over; a
    /// consent/capability gate for using this in anything beyond bench tooling is a named
    /// follow-up, and until it exists the method stays
    /// <see cref="System.Diagnostics.CodeAnalysis.ExperimentalAttribute">[Experimental]</see>
    /// with the <c>Unsafe</c> name prefix.
    /// </para>
    /// <para>
    /// Note the receipt semantics: SDM auto-acknowledgement (PROGRESS 1D) reports over-air
    /// <em>delivery</em>; there is no over-air signal that the CCR command was <em>accepted</em>
    /// (the receiving radio's +/− CCR acknowledgement goes to its own serial port, not back
    /// over the air).
    /// </para>
    /// </remarks>
    /// <param name="dataMessageId">8-character destination data identity; '*' wildcards per
    /// character.</param>
    /// <param name="ccrCommand">The CCR command to execute at the receiver.</param>
    /// <param name="leadInDelay">Carrier lead-in before data, 20 ms granularity, max 5.1 s.
    /// Null uses 100 ms.</param>
    /// <param name="cancellationToken">Cancels waiting for the local radio's acknowledgement.</param>
    [System.Diagnostics.CodeAnalysis.Experimental(CcrOverSdmDiagnosticId)]
    public Task UnsafeSendCcrOverSdmAsync(
        string dataMessageId, CcdiFrame ccrCommand, TimeSpan? leadInDelay = null,
        CancellationToken cancellationToken = default)
    {
        var frame = UnsafeBuildCcrOverSdmFrame(dataMessageId, ccrCommand, leadInDelay);
        return TransactAsync(
            frame, matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    /// <summary>The diagnostic id suppressing the CCR-over-SDM experimental warnings.</summary>
    public const string CcrOverSdmDiagnosticId = "PKTTAIT001";

    private Task<IReadOnlyList<CcdiMessage>> SendAdaptableSdmAsync(
        string dataMessageId, string message, char gfi, string sfi, TimeSpan? leadInDelay,
        CancellationToken cancellationToken)
    {
        ValidateDataMessageId(dataMessageId);
        string parameters = string.Create(
            CultureInfo.InvariantCulture, $"{LeadInUnits(leadInDelay):X2}{gfi}{sfi}{dataMessageId}{message}");
        return TransactAsync(
            new CcdiFrame('a', parameters), matches: null, minCount: 0,
            completeOnPrompt: true, quietTime: null, cancellationToken);
    }

    private static void ValidateDataMessageId(string dataMessageId)
    {
        ArgumentNullException.ThrowIfNull(dataMessageId);
        if (dataMessageId.Length != 8)
        {
            throw new ArgumentException("the data message ID is exactly 8 characters (§1.9.8)", nameof(dataMessageId));
        }
    }

    private static int LeadInUnits(TimeSpan? leadInDelay) =>
        (int)Math.Clamp((leadInDelay ?? TimeSpan.FromMilliseconds(100)).TotalMilliseconds / 20, 1, 255);

    /// <summary>Characters a plain SDM can carry (§1.9.8).</summary>
    public const int PlainSdmMaxLength = 32;

    /// <summary>Characters an extended (SFI 04) SDM can carry (§1.9.8).</summary>
    public const int ExtendedSdmMaxLength = 128;

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

    /// <summary>
    /// Re-clock the open serial port to <paramref name="baudRate"/>. The Transparent-mode
    /// transport uses this when the radio's Transparent terminal baud differs from its
    /// Command-mode CCDI baud (§1.8): the <c>t</c> entry command goes out at the command rate,
    /// then the port is re-clocked to the transparent rate for the byte pipe, and back to the
    /// command rate after the escape. On a rig where both rates match (the common case) it is
    /// never called. Not a general runtime knob — CCDI transactions assume the programmed rate.
    /// </summary>
    internal void SetSerialBaudRate(int baudRate) => io.SetBaudRate(baudRate);

    /// <summary>
    /// Run the §1.7.2 escape guard sequence (idle — escape char ×3 — idle) <b>blind</b>: no
    /// mode precondition and no driver-mode mutation. The stale-Transparent recovery primitive
    /// for <see cref="TaitTransparentTransport"/>: a radio whose previous session's pipe died
    /// before teardown is still a Transparent byte pipe while this freshly-opened driver believes
    /// it is in Command mode — <see cref="ExitTransparentModeAsync"/> and
    /// <see cref="EscapeAndVerifyTransparentAsync"/> both refuse that state, so this writes the
    /// escape without asking. The caller proves the outcome (e.g. by retrying the <c>t</c> entry);
    /// on a radio already in Command mode the three characters are harmless CCDI noise, and on a
    /// radio programmed "Ignore Escape Sequence" ON they are transmitted over the air as data.
    /// </summary>
    internal async Task EscapeTransparentBlindAsync(
        char escapeChar, TimeSpan guardTime, CancellationToken cancellationToken = default)
    {
        byte esc = (byte)escapeChar;
        byte[] escape = [esc, esc, esc];
        await Task.Delay(guardTime, clock, cancellationToken).ConfigureAwait(false);
        io.Write(escape, 0, escape.Length);
        await Task.Delay(guardTime, clock, cancellationToken).ConfigureAwait(false);
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
    /// Best-effort <b>verified</b> recovery from Transparent mode — the primitive the
    /// Transparent-readiness doctor uses to detect an "Ignore Escape Sequence" lockout without
    /// wedging blindly. Runs the §1.7.2 escape guard sequence (idle — escape char ×3 — idle) up
    /// to <paramref name="attempts"/> times, and after each escape <b>confirms with a MODEL
    /// query</b> that Command mode was actually regained. Returns <c>true</c> as soon as a MODEL
    /// query answers (the radio is then left in Command mode); returns <c>false</c> if every
    /// attempt's confirmation times out — the radio is genuinely <b>wedged</b> in Transparent
    /// (programmed "Ignore Escape Sequence" ON) and only a power cycle recovers it.
    /// <para>
    /// Unlike <see cref="ExitTransparentModeAsync"/>, which trusts the escape and flips the driver
    /// to Command unconditionally, this reports success only on a proven Command-mode round-trip.
    /// The radio must currently be in Transparent mode.
    /// </para>
    /// </summary>
    /// <param name="attempts">Escape attempts before declaring the radio wedged (≥1; default 2).</param>
    /// <param name="guardTime">The idle guard either side of the escape burst (§1.7.2 wants ~2 s;
    /// default 2.1 s). Tests pass a short value to keep fast.</param>
    /// <param name="verifyTimeout">How long to wait for the confirming MODEL reply per attempt
    /// (default 3 s).</param>
    /// <param name="cancellationToken">Cancels the run. Cancelling mid-recovery can leave the
    /// radio in Transparent mode — the caller owns that risk.</param>
    /// <returns><c>true</c> when Command mode was proven regained (radio left in Command mode);
    /// <c>false</c> when the radio remains wedged in Transparent after every attempt.</returns>
    public async Task<bool> EscapeAndVerifyTransparentAsync(
        int attempts = 2,
        TimeSpan? guardTime = null,
        TimeSpan? verifyTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var guard = guardTime ?? TimeSpan.FromMilliseconds(2100);
        var verify = verifyTimeout ?? TimeSpan.FromSeconds(3);
        byte esc;
        lock (stateGate)
        {
            if (mode != TaitProtocolMode.Transparent)
            {
                throw new InvalidOperationException("the radio is not in Transparent mode");
            }
            esc = (byte)transparentEscape;
        }

        byte[] escape = [esc, esc, esc];
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // §1.7.2 escape dance: idle, escape char ×3, idle. (When the radio honours the escape
            // these bytes are intercepted locally; when it ignores it they are transmitted — the
            // very failure we are probing for, and why this is gated behind the doctor's interrupt.)
            await Task.Delay(guard, clock, cancellationToken).ConfigureAwait(false);
            io.Write(escape, 0, escape.Length);
            await Task.Delay(guard, clock, cancellationToken).ConfigureAwait(false);

            // Optimistically treat the radio as back in Command mode so the pump parses CCDI and
            // the confirming query is permitted, then prove it with a bounded MODEL query.
            lock (stateGate)
            {
                mode = TaitProtocolMode.Command;
            }
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(verify);
            try
            {
                _ = await ExpectOneAsync<CcdiModelMessage>(new CcdiFrame('q', ""), attemptCts.Token)
                    .ConfigureAwait(false);
                return true; // a MODEL query answered → Command mode is proven back
            }
            catch (Exception ex)
                when ((ex is TimeoutException or OperationCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                // No CCDI reply: the escape did not take. Reflect the physical reality (still a
                // Transparent byte pipe) so the next attempt's guard is meaningful, and retry.
                if (attempt < attempts)
                {
                    lock (stateGate)
                    {
                        mode = TaitProtocolMode.Transparent;
                    }
                }
            }
        }

        return false; // wedged in Transparent — a power cycle is the only recovery
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
                        busySince = busy ? clock.GetUtcNow() : null;
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
            case CcdiSdmMessage sdm:
                SdmReceived?.Invoke(this, sdm);
                break;
            default:
                break;
        }
    }

    private async Task WatchdogAsync(CancellationToken cancellationToken)
    {
        TimeSpan? keepAlive = options.KeepAliveInterval is { } ka && ka > TimeSpan.Zero ? ka : null;
        TimeSpan? staleBusy = options.StaleBusyRevalidateAfter is { } sb && sb > TimeSpan.Zero ? sb : null;
        long shortest = Math.Min(keepAlive?.Ticks ?? long.MaxValue, staleBusy?.Ticks ?? long.MaxValue);
        var checkEvery = TimeSpan.FromTicks(Math.Max(shortest / 2, TimeSpan.TicksPerSecond));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(checkEvery, clock, cancellationToken).ConfigureAwait(false);
                if (Mode == TaitProtocolMode.Transparent)
                {
                    continue;
                }

                if (staleBusy is { } staleAfter)
                {
                    await RevalidateStaleBusyAsync(staleAfter, cancellationToken).ConfigureAwait(false);
                }

                if (keepAlive is not { } interval)
                {
                    continue;
                }
                bool quiet;
                lock (stateGate)
                {
                    quiet = clock.GetUtcNow() - lastInboundAt >= interval;
                }
                if (!quiet)
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

    /// <summary>
    /// Stale-DCD re-validation (#576, bench spike items 9–11): a busy state latched for
    /// <paramref name="staleAfter"/> without a clear edge is suspect — the classic cause is a lost
    /// DCD-clear PROGRESS message, whose effect is every subsequent keyup deferring the CSMA
    /// gate's full MaxWait. Probe the radio; if it does not answer, reset busy to <c>null</c>
    /// (unknown ⇒ fail-open) and raise a final carrier-clear edge so event-driven consumers agree
    /// with the cleared state. A responsive radio keeps its state and the staleness timer re-arms.
    /// </summary>
    private async Task RevalidateStaleBusyAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        lock (stateGate)
        {
            if (channelBusy != true || busySince is not { } since || clock.GetUtcNow() - since < staleAfter)
            {
                return;
            }
        }

        bool ok;
        try
        {
            ok = await PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A dead link (write failure) counts as unresponsive; the pump/watchdog fault
            // machinery handles the connection state separately.
            ok = false;
        }

        if (ok)
        {
            MarkHealthy();
            lock (stateGate)
            {
                if (channelBusy == true)
                {
                    busySince = clock.GetUtcNow();   // re-arm: re-validate again a full period later
                }
            }
            return;
        }

        bool cleared = false;
        lock (stateGate)
        {
            if (channelBusy == true)
            {
                channelBusy = null;
                busySince = null;
                cleared = true;
            }
        }
        if (cleared)
        {
            CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(Busy: false, clock.GetUtcNow()));
        }
    }

    private void MarkFaulted(Exception cause)
    {
        bool changed;
        bool busyCleared;
        Transaction? pending;
        lock (stateGate)
        {
            changed = connectionState != TaitConnectionState.Faulted;
            connectionState = TaitConnectionState.Faulted;
            // A faulted link's last DCD report is no longer evidence (#576): a radio that died
            // busy would otherwise latch the CSMA gate into deferring every keyup its full
            // MaxWait. Unknown (null) fails open, which is the correct degraded behaviour.
            busyCleared = channelBusy == true;
            channelBusy = null;
            busySince = null;
            pending = active;
        }
        pending?.Done.TrySetException(cause);
        if (busyCleared)
        {
            // A final carrier-clear edge so event-driven consumers (window trackers etc.)
            // agree with the cleared state before the fault is announced.
            CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(Busy: false, clock.GetUtcNow()));
        }
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
