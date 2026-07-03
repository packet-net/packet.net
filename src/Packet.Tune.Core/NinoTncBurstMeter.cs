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

    /// <summary>CCDI RSSI / GETRSSI poll cadence during the window. Default 250 ms.</summary>
    public TimeSpan RssiPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Timeout for the one-shot GETRSSI availability probe at
    /// session start (<see cref="NinoTncBurstMeter.ProbeAudioLevelMeterAsync"/>).
    /// Firmware 3.44+ never replies, so this bounds the cost of finding out.
    /// Default 2 s.</summary>
    public TimeSpan AudioLevelProbeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Idle-baseline GETRSSI samples taken when the probe succeeds
    /// (median kept). Default 5.</summary>
    public int IdleBaselineSamples { get; init; } = 5;
}

/// <summary>
/// <see cref="IBurstMeter"/> over a NinoTNC (plus, optionally, the Tait CCDI
/// radio it is wired to). The bracketing signals are always read; on
/// firmware 3.41 the GETRSSI RX-audio level meter (REMOVED in 3.44) adds a
/// continuous deviation reading on top:
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
///   <item><b>GETRSSI RX-audio level</b> (<b>firmware 3.41-era only —
///     REMOVED in 3.44</b>): the RMS level of the TNC's receive audio
///     post-FM-demod in dB. A carrier <em>quiets</em> the audio, so lower =
///     more quieting = signal present. Call
///     <see cref="ProbeAudioLevelMeterAsync"/> once at session start —
///     it probes availability (bounded by
///     <see cref="NinoTncBurstMeterOptions.AudioLevelProbeTimeout"/>) and
///     captures the idle baseline before the first burst; without that call
///     the fast path stays off and the meter behaves exactly as on 3.44.
///     During the window the level is sampled on the same cadence as the
///     CCDI RSSI; the burst's figure is the median of samples during
///     carrier (DCD-gated), falling back to the max-quieting (minimum)
///     sample when no DCD source is attached.</item>
/// </list>
/// </remarks>
public sealed class NinoTncBurstMeter : IBurstMeter
{
    private readonly NinoTncSerialPort tnc;
    private readonly TaitCcdiRadio? radio;
    private readonly NinoTncBurstMeterOptions options;
    private bool? audioLevelMeterAvailable;

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

    /// <summary>Whether the GETRSSI audio-level fast path is available:
    /// <c>true</c>/<c>false</c> after <see cref="ProbeAudioLevelMeterAsync"/>
    /// has run, <c>null</c> before (fast path off until probed).</summary>
    public bool? AudioLevelMeterAvailable => audioLevelMeterAvailable;

    /// <summary>Idle-channel RX-audio baseline in dB (median of the probe's
    /// samples), or <c>null</c> when unprobed/unavailable.</summary>
    public double? IdleAudioLevelDb { get; private set; }

    /// <summary>
    /// Probe the TNC's GETRSSI RX-audio level meter once (<b>firmware
    /// 3.41-era only — REMOVED in 3.44</b>, where the query gets no reply
    /// and the probe times out) and, when present, capture the idle-channel
    /// baseline before the first burst. Call at session start on a quiet
    /// channel; subsequent calls return the cached verdict. Without a
    /// successful probe the fast path stays off and
    /// <see cref="MeasureBurstAsync"/> reports no audio level.
    /// </summary>
    /// <returns><c>true</c> when GETRSSI answered (fast path active).</returns>
    public async Task<bool> ProbeAudioLevelMeterAsync(CancellationToken cancellationToken = default)
    {
        if (audioLevelMeterAvailable is { } known)
        {
            return known;
        }

        var idleSamples = new List<float>();
        try
        {
            idleSamples.Add(await tnc.GetRssiAsync(options.AudioLevelProbeTimeout, cancellationToken)
                .ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            audioLevelMeterAvailable = false;
            Log?.Invoke(
                $"meter: GETRSSI got no reply in {options.AudioLevelProbeTimeout.TotalSeconds:0.#} s — " +
                "no RX-audio level fast path (firmware 3.41-era feature, removed in 3.44)");
            return false;
        }

        for (int i = 1; i < options.IdleBaselineSamples; i++)
        {
            await Task.Delay(options.RssiPollInterval, cancellationToken).ConfigureAwait(false);
            try
            {
                idleSamples.Add(await tnc.GetRssiAsync(options.AudioLevelProbeTimeout, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (TimeoutException)
            {
                break; // answered at least once — keep what we have
            }
        }

        IdleAudioLevelDb = Median(idleSamples);
        audioLevelMeterAvailable = true;
        Log?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"meter: GETRSSI fast path active (firmware 3.41-era) — idle RX-audio {IdleAudioLevelDb:0.0} dB (n={idleSamples.Count})"));
        return true;
    }

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
        var levelSamples = new List<(float Db, bool Busy)>();
        try
        {
            using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            windowCts.CancelAfter(window);
            await PollSignalsAsync(rssiSamples, levelSamples, windowCts.Token).ConfigureAwait(false);
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
            PickRssi(rssiSamples),
            PickAudioLevel(levelSamples));
    }

    private async Task PollSignalsAsync(
        List<(float Dbm, bool Busy)> rssiSamples,
        List<(float Db, bool Busy)> levelSamples,
        CancellationToken windowToken)
    {
        try
        {
            while (!windowToken.IsCancellationRequested)
            {
                bool busy = radio?.ChannelBusy == true;
                if (radio is not null)
                {
                    try
                    {
                        float dbm = await radio.ReadRssiDbmAsync(windowToken).ConfigureAwait(false);
                        rssiSamples.Add((dbm, busy));
                    }
                    catch (Exception ex) when (ex is TimeoutException or TaitCcdiException)
                    {
                        Log?.Invoke($"meter: RSSI poll failed ({ex.Message})");
                    }
                }
                if (audioLevelMeterAvailable == true)
                {
                    // GETRSSI fast path (firmware 3.41-era): the continuous
                    // RX-audio deviation meter, sampled on the same cadence
                    // and tagged with the radio's DCD when one is attached.
                    try
                    {
                        float db = await tnc.GetRssiAsync(options.AudioLevelProbeTimeout, windowToken)
                            .ConfigureAwait(false);
                        levelSamples.Add((db, busy));
                    }
                    catch (TimeoutException)
                    {
                        Log?.Invoke("meter: GETRSSI poll got no reply — level sample skipped");
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

    internal static double? PickRssi(List<(float Dbm, bool Busy)> samples)
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

    internal static double? PickAudioLevel(List<(float Db, bool Busy)> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }
        var busy = samples.Where(s => s.Busy).Select(s => s.Db).ToList();
        if (busy.Count > 0)
        {
            return Median(busy);
        }
        // No DCD gating available — a carrier QUIETS the demodulated audio,
        // so the max-quieting (minimum) sample is the best "level while
        // signal present" estimate on an otherwise idle channel.
        return samples.Min(s => s.Db);
    }

    private static double Median(List<float> values)
    {
        var sorted = values.Order().ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
