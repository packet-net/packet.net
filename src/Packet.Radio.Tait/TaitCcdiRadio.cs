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

    private static readonly TimeSpan DefaultTransactionTimeout = TimeSpan.FromSeconds(2);

    private readonly ISerialIo io;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim commandGate = new(1, 1);
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Thread pumpThread;
    private readonly object stateGate = new();

    private Transaction? active;
    private bool? channelBusy;
    private bool weKeyedTransmitter;
    private int disposed;

    private TaitCcdiRadio(ISerialIo io, TimeProvider? timeProvider)
    {
        this.io = io;
        clock = timeProvider ?? TimeProvider.System;
        pumpThread = new Thread(PumpReads) { IsBackground = true, Name = $"tait-ccdi:{io.PortName}" };
        pumpThread.Start();
    }

    /// <summary>The serial port this radio is attached to (e.g. <c>/dev/ttyUSB0</c>).</summary>
    public string PortName => io.PortName;

    /// <inheritdoc/>
    public RadioCapabilities Capabilities =>
        RadioCapabilities.RssiRead | RadioCapabilities.CarrierSense | RadioCapabilities.TransmitterControl;

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

    /// <summary>
    /// Open the named serial port (8N1, no flow control) and start the read pump. The radio is
    /// not touched — pair with <see cref="SetProgressMessagesAsync"/> to turn on DCD events.
    /// </summary>
    public static TaitCcdiRadio Open(string portName, int baudRate = DefaultBaudRate, TimeProvider? timeProvider = null)
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
        return new TaitCcdiRadio(new SystemSerialIo(serial), timeProvider);
    }

    /// <summary>Test seam (InternalsVisibleTo <c>Packet.Radio.Tait.Tests</c>): drive the
    /// transaction engine and demux over a scripted <see cref="ISerialIo"/>.</summary>
    internal static TaitCcdiRadio OpenForTest(ISerialIo io, TimeProvider? timeProvider = null) =>
        new(io, timeProvider);

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

    private async Task<CcdiQueryResultMessage> ExpectCctmAsync(string cctm, CancellationToken cancellationToken)
    {
        int command = int.Parse(cctm, System.Globalization.CultureInfo.InvariantCulture);
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

    private async Task<IReadOnlyList<CcdiMessage>> TransactAsync(
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

                await txn.Done.Task.WaitAsync(DefaultTransactionTimeout, clock, cancellationToken)
                    .ConfigureAwait(false);

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
                    $"no CCDI response to '{encoded}' within {DefaultTransactionTimeout.TotalSeconds:0.#}s on {PortName}");
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
            catch (Exception)
            {
                // Port closed under us (dispose path) or hard IO failure: stop pumping.
                break;
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
            default:
                break;
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
        lock (stateGate)
        {
            mustUnkey = weKeyedTransmitter;
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

/// <summary>One transmitter keying edge reported by the radio (PROGRESS PTT
/// activated/deactivated).</summary>
/// <param name="Transmitting"><c>true</c> = transmitter keyed; <c>false</c> = unkeyed.</param>
/// <param name="At">When the radio reported the edge, stamped from the driver's clock.</param>
public readonly record struct TransmitterStateChange(bool Transmitting, DateTimeOffset At);
