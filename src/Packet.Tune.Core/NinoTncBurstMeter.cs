using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;

namespace Packet.Tune.Core;

/// <summary>Tunables for <see cref="NinoTncBurstMeter"/>.</summary>
public sealed record NinoTncBurstMeterOptions
{
    /// <summary>Fixed part of the measurement window. Default 4 s.</summary>
    public TimeSpan WindowBase { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Per-requested-frame part of the measurement window (TXDELAY +
    /// airtime + inter-frame gap at 1200 bd). Default 1.6 s.</summary>
    public TimeSpan WindowPerFrame { get; init; } = TimeSpan.FromSeconds(1.6);

    /// <summary>CCDI RSSI poll cadence during the window. Default 250 ms.</summary>
    public TimeSpan RssiPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// <see cref="IBurstMeter"/> over a NinoTNC (plus, optionally, the Tait CCDI
/// radio it is wired to). GETRSSI is gone in firmware 3.44, so the meter
/// reads the surviving signals instead:
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><b>Decoded frames vs sent</b> — counts inbound
///     <see cref="TuningBurst"/>-marked frames during the window.</item>
///   <item><b>IL2P FEC-corrected bytes delta</b> (GETALL register 11) — only
///     meaningful in IL2P modes (prefer mode 7 for tuning sessions), and
///     <c>null</c> when the firmware's GETALL reply doesn't carry the
///     register (3.41/3.44 answer the labelled diagnostic, which lacks
///     it).</item>
///   <item><b>Lost-ADC-sample delta</b> (labelled <c>LostADCSmp</c>) —
///     clipping = gross over-deviation.</item>
///   <item><b>Tait CCDI RSSI</b> — the median of busy-channel polls during
///     the window; the constant RF-path check (enable PROGRESS messages on
///     the radio so busy-gating works — without DCD the maximum poll is used
///     instead).</item>
/// </list>
/// </remarks>
public sealed class NinoTncBurstMeter : IBurstMeter
{
    private readonly NinoTncSerialPort tnc;
    private readonly TaitCcdiRadio? radio;
    private readonly NinoTncBurstMeterOptions options;

    /// <summary>Create over an open TNC (and optionally its radio's CCDI
    /// connection). Lifetimes stay the caller's.</summary>
    public NinoTncBurstMeter(NinoTncSerialPort tnc, TaitCcdiRadio? radio = null, NinoTncBurstMeterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tnc);
        this.tnc = tnc;
        this.radio = radio;
        this.options = options ?? new NinoTncBurstMeterOptions();
    }

    /// <summary>Diagnostic sink. Null = silent.</summary>
    public Action<string>? Log { get; set; }

    /// <inheritdoc/>
    public async Task<MeterReport> MeasureBurstAsync(int requestedFrames, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedFrames);
        TimeSpan window = options.WindowBase + options.WindowPerFrame * requestedFrames;

        NinoTncStatusFrame? before = await TryGetAllAsync(cancellationToken).ConfigureAwait(false);

        int decoded = 0;
        void OnFrame(object? sender, KissFrame frame)
        {
            if (TuningBurst.IsBurstFrame(frame))
            {
                Interlocked.Increment(ref decoded);
            }
        }

        tnc.FrameReceived += OnFrame;
        var rssiSamples = new List<(float Dbm, bool Busy)>();
        try
        {
            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            windowCts.CancelAfter(window);
            await PollRssiAsync(rssiSamples, windowCts.Token).ConfigureAwait(false);
        }
        finally
        {
            tnc.FrameReceived -= OnFrame;
        }

        NinoTncStatusFrame? after = await TryGetAllAsync(cancellationToken).ConfigureAwait(false);
        long? fec = null, clip = null;
        if (before is not null && after is not null)
        {
            var delta = NinoTncStatusDelta.Between(before, after);
            fec = delta.Il2pFecCorrectedBytes;
            clip = delta.LostAdcSamples;
        }

        return new MeterReport(
            Math.Min(decoded, requestedFrames),
            requestedFrames,
            fec,
            clip,
            PickRssi(rssiSamples));
    }

    private async Task PollRssiAsync(List<(float Dbm, bool Busy)> samples, CancellationToken windowToken)
    {
        try
        {
            while (!windowToken.IsCancellationRequested)
            {
                if (radio is not null)
                {
                    try
                    {
                        bool busy = radio.ChannelBusy == true;
                        float dbm = await radio.ReadRssiDbmAsync(windowToken).ConfigureAwait(false);
                        samples.Add((dbm, busy));
                    }
                    catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
                    {
                        Log?.Invoke($"meter: RSSI poll failed ({ex.Message})");
                    }
                }
                await Task.Delay(options.RssiPollInterval, windowToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (windowToken.IsCancellationRequested)
        {
            // Window elapsed — normal completion.
        }
    }

    private async Task<NinoTncStatusFrame?> TryGetAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await tnc.GetAllAsync(timeout: TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log?.Invoke("meter: GETALL got no reply — FEC/clip deltas unavailable this burst");
            return null;
        }
    }

    private static double? PickRssi(List<(float Dbm, bool Busy)> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }
        var busy = samples.Where(s => s.Busy).Select(s => s.Dbm).ToList();
        if (busy.Count > 0)
        {
            return Median(busy);
        }
        // No DCD gating available (PROGRESS off?) — the strongest poll is the
        // best carrier estimate on an otherwise idle channel.
        return samples.Max(s => s.Dbm);
    }

    private static double Median(List<float> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
