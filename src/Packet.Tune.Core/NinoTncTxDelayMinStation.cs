using System.Diagnostics;
using System.Globalization;
using Packet.Core;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Radio;

namespace Packet.Tune.Core;

/// <summary>Tunables for <see cref="NinoTncTxDelayMinStation"/>. The defaults encode the
/// bench-measured NinoTNC keying behaviour (see <see cref="TxDelayControlCheck"/>).</summary>
public sealed record NinoTncTxDelayMinStationOptions
{
    /// <summary>Gap before each probe keying, letting the transmitter unkey first:
    /// back-to-back ACKMODE sends chain into ONE keying train with a single preamble
    /// (bench-observed: chained frames echo in a constant ~430 ms regardless of
    /// TXDELAY), which would measure nothing. Default 750 ms.</summary>
    public TimeSpan UnkeyGap { get; init; } = TimeSpan.FromMilliseconds(750);

    /// <summary>TX-completion echo timeout for the settle frame (its echo is
    /// sporadically absent — the frame still keys, so a timeout is tolerated).
    /// Default 8 s.</summary>
    public TimeSpan SettleTxTimeout { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>TX-completion echo timeout per probe. Default 20 s (covers CSMA +
    /// max TXDELAY + airtime at 300 baud).</summary>
    public TimeSpan ProbeTxTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Plausibility window for a measured pre-data carrier reading — a probe
    /// whose computed lead falls outside (0, this] is discarded rather than skewing the
    /// median (a missed carrier edge or a stale window produces wild values).
    /// Default 10 s.</summary>
    public TimeSpan MaxPlausiblePreData { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The live <see cref="ITxDelayMinStation"/>: a NinoTNC (KISS TXDELAY writes + probe
/// traffic) plus, optionally, a carrier-sensing radio control channel (Tait CCDI via
/// <see cref="IRadioControl"/>) that lets the meter side measure each probe's pre-data
/// carrier time directly.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Settle discipline</b>: the NinoTNC applies a changed KISS parameter from
///     the SECOND frame after the command (bench-observed), so
///     <see cref="SetTxDelayAsync"/> always transmits and discards a settle frame, then
///     lets the transmitter unkey, before returning.</item>
///   <item><b>Separate keyings</b>: <see cref="TransmitProbesAsync"/> gaps every probe
///     by <see cref="NinoTncTxDelayMinStationOptions.UnkeyGap"/> — chained sends share
///     one preamble and would measure nothing.</item>
///   <item><b>Channel-access pinning</b>: persistence 255 / slottime 0 for the
///     measurement (the default p-persistence adds random ~100 ms slot deferrals);
///     restore is the conventional 63 / 10 — KISS has no read-back. Both mirror
///     <see cref="TxDelayControlCheck"/>.</item>
///   <item><b>Pre-data measurement</b> (meter side): the counter tracks the radio's
///     hardware carrier-sense rises and, per decoded probe, computes
///     (arrival − airtime) − carrier-rise — the same attribution
///     <c>RssiTaggingTransport</c> performs, carrying the same constant decode+serial
///     overhead (~40–75 ms at 1200 Bd). Enable PROGRESS messages on the radio or no
///     edges arrive.</item>
/// </list>
/// </remarks>
public sealed class NinoTncTxDelayMinStation : ITxDelayMinStation
{
    private readonly NinoTncSerialPort tnc;
    private readonly Callsign source;
    private readonly IRadioControl? radio;
    private readonly NinoTncTxDelayMinStationOptions options;
    private readonly TimeProvider clock;

    /// <summary>Create over an open TNC (and optionally a carrier-sensing radio for the
    /// meter's pre-data measurement). Lifetimes stay the caller's.</summary>
    /// <param name="tnc">The NinoTNC whose TXDELAY is under control (also carries the probes).</param>
    /// <param name="source">Source callsign for settle/probe frames.</param>
    /// <param name="radio">The paired radio's control channel, when one is attached —
    /// only its carrier-sense events are used, and only by the meter-side counter.</param>
    /// <param name="options">Timing knobs; null = bench defaults.</param>
    /// <param name="timeProvider">Time source for pre-data arithmetic; null = system.</param>
    public NinoTncTxDelayMinStation(
        NinoTncSerialPort tnc,
        Callsign source,
        IRadioControl? radio = null,
        NinoTncTxDelayMinStationOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(tnc);
        this.tnc = tnc;
        this.source = source;
        this.radio = radio;
        this.options = options ?? new NinoTncTxDelayMinStationOptions();
        clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <inheritdoc/>
    public async Task PinChannelAccessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await tnc.SetPersistenceAsync(255, cancellationToken).ConfigureAwait(false);
            await tnc.SetSlotTimeAsync(0, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new TxDelayMinException($"channel-access pin failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task RestoreChannelAccessAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await tnc.SetPersistenceAsync(63, cancellationToken).ConfigureAwait(false);
            await tnc.SetSlotTimeAsync(10, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new TxDelayMinException($"channel-access restore failed: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public async Task SetTxDelayAsync(int txDelayMs, CancellationToken cancellationToken = default)
    {
        byte tenMs = (byte)Math.Clamp(TxDelayMinReport.RoundUpToTen(txDelayMs) / 10, 1, 255);
        try
        {
            await tnc.SetTxDelayAsync(tenMs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            throw new TxDelayMinException($"KISS TXDELAY set failed: {ex.Message}", ex);
        }

        // Settle frame: the changed TXDELAY applies from the SECOND frame after the
        // command — this keying (which still pays the OLD preamble) absorbs that. Its
        // ACKMODE echo is sporadically absent (bench-observed); the frame still keys,
        // so a missing echo is logged and tolerated.
        byte[] settle = TxDelayProbe.BuildSettleFrame(source, txDelayMs).ToBytes();
        try
        {
            await tnc.SendFrameWithAckAsync(settle, options.SettleTxTimeout, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"station: settle frame TX-completion not echoed within {options.SettleTxTimeout.TotalSeconds:0} s (continuing)"));
        }

        // Let the transmitter unkey — the next send must be its own keying.
        await Task.Delay(options.UnkeyGap, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<ModeProbeTxStats> TransmitProbesAsync(
        int stepTag, int count, CancellationToken cancellationToken = default)
    {
        var latencies = new List<double>();
        int confirmed = 0;
        for (int i = 1; i <= count; i++)
        {
            // Gap FIRST: the previous keying (settle frame or probe i−1) must fully
            // unkey, or this probe chains into its train and pays no preamble of its own.
            if (i > 1)
            {
                await Task.Delay(options.UnkeyGap, cancellationToken).ConfigureAwait(false);
            }
            byte[] wire = TxDelayProbe.BuildFrame(source, stepTag, i, count).ToBytes();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await tnc.SendFrameWithAckAsync(wire, options.ProbeTxTimeout, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                confirmed++;
                latencies.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (TimeoutException)
            {
                // No TX-completion echo — the frame may still have keyed (bench-observed
                // sporadic echo absence). The meter's decode count is the verdict.
                Log?.Invoke(string.Create(CultureInfo.InvariantCulture,
                    $"station: probe {i}/{count} TX-completion not echoed within {options.ProbeTxTimeout.TotalSeconds:0} s"));
            }
        }
        return new ModeProbeTxStats(count, confirmed, latencies.Count == 0 ? null : latencies.Average());
    }

    /// <inheritdoc/>
    public ITxDelayProbeCount BeginProbeCount(int stepTag) =>
        new ProbeCounter(tnc, radio, stepTag, options, clock);

    private sealed class ProbeCounter : ITxDelayProbeCount
    {
        private readonly NinoTncSerialPort tnc;
        private readonly IRadioControl? radio;
        private readonly int stepTag;
        private readonly NinoTncTxDelayMinStationOptions options;
        private readonly TimeProvider clock;
        private readonly object gate = new();
        private readonly List<double> preDataMs = [];
        private DateTimeOffset? lastCarrierRise;
        private int count;
        private int disposed;

        public ProbeCounter(
            NinoTncSerialPort tnc, IRadioControl? radio, int stepTag,
            NinoTncTxDelayMinStationOptions options, TimeProvider clock)
        {
            this.tnc = tnc;
            this.radio = radio;
            this.stepTag = stepTag;
            this.options = options;
            this.clock = clock;
            tnc.FrameReceived += OnFrame;
            if (radio is not null && radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
            {
                radio.CarrierSenseChanged += OnCarrierSense;
            }
        }

        public int Count => Volatile.Read(ref count);

        public double? MedianPreDataCarrierMs
        {
            get
            {
                lock (gate)
                {
                    if (preDataMs.Count == 0)
                    {
                        return null;
                    }
                    var sorted = preDataMs.Order().ToList();
                    int mid = sorted.Count / 2;
                    return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            tnc.FrameReceived -= OnFrame;
            if (radio is not null && radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
            {
                radio.CarrierSenseChanged -= OnCarrierSense;
            }
        }

        private void OnCarrierSense(object? sender, CarrierSenseChange e)
        {
            if (e.Busy)
            {
                lock (gate)
                {
                    lastCarrierRise = e.At;
                }
            }
        }

        private void OnFrame(object? sender, KissFrame frame)
        {
            if (!TxDelayProbe.IsProbeFrame(frame, stepTag))
            {
                return;
            }
            Interlocked.Increment(ref count);

            // Pre-data cross-check: probes are SEPARATE keyings, so the most recent
            // carrier rise belongs to this probe's transmission. Lead = (delivery −
            // airtime) − rise; delivery trails end-of-RF by the modem's decode+serial
            // latency, a documented constant overhead the reading carries.
            lock (gate)
            {
                if (lastCarrierRise is not { } rise || tnc.CurrentBitRateHz is not (int rate and > 0))
                {
                    return;
                }
                // Wire bytes + FCS(2) + one flag, ignoring bit-stuffing — the same
                // approximation RssiTaggingTransport uses.
                double airtimeMs = (frame.Payload.Length + 3) * 8.0 * 1000.0 / rate;
                double leadMs = (clock.GetUtcNow() - rise).TotalMilliseconds - airtimeMs;
                if (leadMs > 0 && leadMs <= options.MaxPlausiblePreData.TotalMilliseconds)
                {
                    preDataMs.Add(leadMs);
                }
            }
        }
    }
}
