namespace Packet.Radio.Tait;

/// <summary>
/// Periodic radio-health sampler over a <see cref="TaitCcdiRadio"/>: on every tick it reads the
/// radio's own sliding-average RSSI (CCTM 063) and PA temperature (CCTM 047), plus the raw
/// forward/reverse power detectors (CCTM 318/319) — classified as <i>idle-offset</i> readings
/// while the transmitter is unkeyed and as <i>transmit</i> readings (offset-corrected, with a
/// reverse/forward trend ratio) while the radio reports itself transmitting via
/// <see cref="TaitCcdiRadio.TransmitterStateChanged"/>. Samples surface as
/// <see cref="SampleTaken"/> events; <see cref="Summarize"/> reduces the rolling window to
/// min/median/max per metric. Consumers should alert on <i>change</i> in the trends, not on
/// absolute values.
/// </summary>
/// <remarks>
/// <para>
/// The forward/reverse figures are deliberately never converted to VSWR. Per Tait's service
/// documentation, CCTM 318/319 report the raw DC millivolts of the detector diodes on the
/// directional coupler between PA and antenna port; detector voltage scales as √P (the firmware
/// itself stores √-power calibration constants), the reverse detector exists for mismatch
/// <i>protection</i> (excess reverse power folds the PA back to lowest power), and the service
/// go/no-go figures are specified <b>only at High power</b> (e.g. B1-band 25 W bodies: forward
/// 1100–2000 mV, reverse &lt; 500 mV into a good 50 Ω load). At lower power levels the reverse
/// reading is dominated by detector-diode knee/offset and coupler directivity floor, so an
/// absolute VSWR computed from these numbers is fiction — but their offset-corrected
/// <i>trend per station</i> is a serviceable antenna-system health signal, which is exactly what
/// this monitor exposes. The idle offsets are sampled from the same detectors while the
/// transmitter is unkeyed, as Tait's own troubleshooting procedure suggests.
/// </para>
/// <para>
/// Sources (Tait TM8100/TM8200 service documentation):
/// <see href="https://www.repeater-builder.com/tait/pdf/tait-tm8100-tm8200-service-manual.pdf">
/// TM8100/TM8200 Service Manual (MMA-00005-05)</see> — CCTM command chapter and the High-power
/// forward/reverse go/no-go tables;
/// <see href="https://manuals.repeater-builder.com/2007/TM8000/TM8000%20CCDI%20Protocol%20Manual/TM8000%20CCDI%20Protocol%20Manual%20MMA-00038-02.pdf">
/// TM8000 CCDI Protocol Manual (MMA-00038-02)</see> — QUERY type 5 relaying of CCTM
/// 047/063/064/318/319 (mirrored at
/// <see href="https://wiki.oarc.uk/_media/radios:tm8100-protocol-manual.pdf">wiki.oarc.uk</see>);
/// <see href="https://manuals.repeater-builder.com/2007/TM8000/TM8100%20Calibration_Application_User's_Manual/TM8100_Calibration_Application_User's_Manual.pdf">
/// TM8100 Calibration Application User's Manual</see> — the Tx Power Control test's "Coupler Cal
/// Power" and √-power constants;
/// <see href="https://manuals.repeater-builder.com/2007/TECHNOTE/TM8000/TN-1038b_SR_TM8100%20Firmware%20v2.09%20and%20Programming%20Applicatio.pdf">
/// TN-1038b</see> — exact CCDI wire examples for 318/319/064 and the RSSI usable range;
/// <see href="https://manuals.repeater-builder.com/2007/TECHNOTE/TM8000/TN-1011_Terminal_Application.pdf">
/// TN-1011</see> — the fullest public CCTM command catalogue.
/// </para>
/// <para>
/// The radio's lifetime stays the caller's — disposing the monitor only stops the sampling loop
/// and unhooks the event subscription. Transmit-state tracking needs the radio's PROGRESS
/// messages enabled (<see cref="TaitCcdiRadio.SetProgressMessagesAsync"/>, per-session radio
/// state); without them every sample is classified idle. Individual read failures null the
/// affected fields and the loop carries on; a tick where every read failed emits nothing.
/// </para>
/// </remarks>
public sealed class TaitRadioHealthMonitor : IAsyncDisposable, IDisposable
{
    private readonly TaitCcdiRadio radio;
    private readonly TaitRadioHealthMonitorOptions options;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource cts = new();
    private readonly SemaphoreSlim sampleGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly Queue<int> idleForwardMv = new();
    private readonly Queue<int> idleReverseMv = new();
    private readonly Queue<TaitRadioHealthSample> window = new();
    private readonly CancellationToken stopToken;
    private readonly Task loop;
    private bool transmitting;
    private int disposed;

    /// <summary>Start sampling <paramref name="radio"/> on
    /// <see cref="TaitRadioHealthMonitorOptions.SampleInterval"/> (first sample immediately).
    /// Ownership of the radio is NOT taken.</summary>
    public TaitRadioHealthMonitor(
        TaitCcdiRadio radio,
        TaitRadioHealthMonitorOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(radio);
        this.radio = radio;
        this.options = options ?? new TaitRadioHealthMonitorOptions();
        clock = timeProvider ?? TimeProvider.System;
        stopToken = cts.Token;
        radio.TransmitterStateChanged += OnTransmitterStateChanged;
        loop = Task.Run(() => SampleLoopAsync(stopToken));
    }

    /// <summary>Every completed sample, in the order taken. Fires on the sampler task — keep
    /// handlers fast and non-blocking.</summary>
    public event EventHandler<TaitRadioHealthSample>? SampleTaken;

    /// <summary>Whether the radio currently reports its transmitter keyed (last PROGRESS PTT
    /// edge). <c>false</c> until the first edge arrives.</summary>
    public bool Transmitting
    {
        get
        {
            lock (stateGate)
            {
                return transmitting;
            }
        }
    }

    /// <summary>Current forward idle-offset estimate (median of the last
    /// <see cref="TaitRadioHealthMonitorOptions.IdleOffsetWindowSize"/> not-transmitting CCTM
    /// 318 readings), or <c>null</c> before the first idle sample.</summary>
    public int? ForwardIdleOffsetMillivolts
    {
        get
        {
            lock (stateGate)
            {
                return MedianOf(idleForwardMv);
            }
        }
    }

    /// <summary>Current reverse idle-offset estimate (median of the last
    /// <see cref="TaitRadioHealthMonitorOptions.IdleOffsetWindowSize"/> not-transmitting CCTM
    /// 319 readings), or <c>null</c> before the first idle sample.</summary>
    public int? ReverseIdleOffsetMillivolts
    {
        get
        {
            lock (stateGate)
            {
                return MedianOf(idleReverseMv);
            }
        }
    }

    /// <summary>Reduce the current rolling sample window to min/median/max per metric.</summary>
    public TaitRadioHealthSummary Summarize()
    {
        TaitRadioHealthSample[] snapshot;
        lock (stateGate)
        {
            snapshot = [.. window];
        }

        List<double> rssi = [];
        List<double> temp = [];
        List<double> fwd = [];
        List<double> rev = [];
        List<double> ratio = [];
        int txCount = 0;
        foreach (var s in snapshot)
        {
            if (s.Transmitting)
            {
                txCount++;
            }
            if (s.RssiDbm is { } r)
            {
                rssi.Add(r);
            }
            if (s.PaTemperatureCelsius is { } t)
            {
                temp.Add(t);
            }
            if (s.TxForwardOverIdleMillivolts is { } f)
            {
                fwd.Add(f);
            }
            if (s.TxReverseOverIdleMillivolts is { } v)
            {
                rev.Add(v);
            }
            if (s.TxReverseForwardRatio is { } q)
            {
                ratio.Add(q);
            }
        }

        return new TaitRadioHealthSummary(
            SampleCount: snapshot.Length,
            TransmitSampleCount: txCount,
            From: snapshot.Length > 0 ? snapshot[0].At : null,
            To: snapshot.Length > 0 ? snapshot[^1].At : null,
            RssiDbm: TaitRadioHealthStat.Over(rssi),
            PaTemperatureCelsius: TaitRadioHealthStat.Over(temp),
            TxForwardOverIdleMillivolts: TaitRadioHealthStat.Over(fwd),
            TxReverseOverIdleMillivolts: TaitRadioHealthStat.Over(rev),
            TxReverseForwardRatio: TaitRadioHealthStat.Over(ratio));
    }

    private void OnTransmitterStateChanged(object? sender, TransmitterStateChange e)
    {
        lock (stateGate)
        {
            transmitting = e.Transmitting;
        }
        if (e.Transmitting && options.SampleOnKeying && Volatile.Read(ref disposed) == 0)
        {
            _ = Task.Run(() => KeyedSampleAsync(stopToken));
        }
    }

    private async Task KeyedSampleAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the PA settle onto the carrier before reading the detectors.
            await Task.Delay(options.KeyedSampleDelay, clock, cancellationToken).ConfigureAwait(false);
            if (Transmitting)
            {
                await TakeSampleAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await TakeSampleAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(options.SampleInterval, clock, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task TakeSampleAsync(CancellationToken cancellationToken)
    {
        await sampleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var at = clock.GetUtcNow();
            bool txAtStart = Transmitting;

            float? rssi = null;
            if (!txAtStart)
            {
                rssi = await TryReadAsync(
                    async () => (float?)await radio.ReadAveragedRssiDbmAsync(cancellationToken)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            var temperature = await TryReadAsync<TaitPaTemperature?>(
                async () => await radio.ReadPaTemperatureAsync(cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            int? forward = await TryReadAsync(() => radio.ReadForwardPowerAsync(cancellationToken))
                .ConfigureAwait(false);
            int? reverse = await TryReadAsync(() => radio.ReadReversePowerAsync(cancellationToken))
                .ConfigureAwait(false);

            // A keying edge mid-sample makes the detector readings unattributable (part carrier,
            // part idle) — drop them rather than corrupt either the offsets or the TX trend.
            bool txAtEnd = Transmitting;
            if (txAtStart != txAtEnd)
            {
                forward = null;
                reverse = null;
            }

            int? fwdOffset;
            int? revOffset;
            int? txFwd = null;
            int? txRev = null;
            double? txRatio = null;
            lock (stateGate)
            {
                if (!txAtStart)
                {
                    PushIdle(idleForwardMv, forward);
                    PushIdle(idleReverseMv, reverse);
                }
                fwdOffset = MedianOf(idleForwardMv);
                revOffset = MedianOf(idleReverseMv);
            }
            if (txAtStart)
            {
                txFwd = forward is { } f ? Math.Max(0, f - (fwdOffset ?? 0)) : null;
                txRev = reverse is { } v ? Math.Max(0, v - (revOffset ?? 0)) : null;
                if (txFwd is { } cf && txRev is { } cr && cf >= options.MinimumForwardForRatioMillivolts)
                {
                    txRatio = (double)cr / cf;
                }
            }

            if (rssi is null && temperature is null && forward is null && reverse is null)
            {
                return; // nothing answered — a dead tick carries no information worth emitting
            }

            var sample = new TaitRadioHealthSample
            {
                At = at,
                Transmitting = txAtStart,
                RssiDbm = rssi,
                PaTemperatureCelsius = temperature?.Celsius,
                PaDetectorMillivolts = temperature?.AdcMillivolts,
                ForwardPowerMillivolts = forward,
                ReversePowerMillivolts = reverse,
                ForwardIdleOffsetMillivolts = fwdOffset,
                ReverseIdleOffsetMillivolts = revOffset,
                TxForwardOverIdleMillivolts = txFwd,
                TxReverseOverIdleMillivolts = txRev,
                TxReverseForwardRatio = txRatio,
            };

            lock (stateGate)
            {
                window.Enqueue(sample);
                while (window.Count > options.SummaryWindowSize)
                {
                    window.Dequeue();
                }
            }

            SampleTaken?.Invoke(this, sample);
        }
        finally
        {
            sampleGate.Release();
        }
    }

    private void PushIdle(Queue<int> readings, int? value)
    {
        if (value is not { } v)
        {
            return;
        }
        readings.Enqueue(v);
        while (readings.Count > options.IdleOffsetWindowSize)
        {
            readings.Dequeue();
        }
    }

    private static int? MedianOf(Queue<int> readings)
    {
        if (readings.Count == 0)
        {
            return null;
        }
        int[] sorted = [.. readings];
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>One CCDI read inside a sample: failures null the field instead of killing the
    /// tick (transaction errors, timeouts, malformed values, mode changes, dispose races).</summary>
    private static async Task<T?> TryReadAsync<T>(Func<Task<T?>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TaitCcdiException or TimeoutException or FormatException
            or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            return default;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        radio.TransmitterStateChanged -= OnTransmitterStateChanged;
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch
        {
        }
        cts.Dispose();
        sampleGate.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
