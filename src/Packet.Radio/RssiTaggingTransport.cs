using System.Runtime.CompilerServices;
using Packet.Ax25.Transport;

namespace Packet.Radio;

/// <summary>
/// Decorates any <see cref="IAx25Transport"/> with per-frame radio signal metadata: a background
/// sampler polls <see cref="IRadioControl.ReadRssiDbmAsync"/> (fast while the channel is busy,
/// slow while idle, so the idle samples double as a noise-floor estimate), carrier-sense edges
/// from the radio are tracked as transmission windows, and every inbound frame is re-yielded
/// with <see cref="Ax25InboundFrame.Radio"/> populated: RSSI (median/min/max/sample-count of the
/// samples taken while its carrier was up), SNR against the tracked noise floor, the carrier
/// rise instant, the frame's index within its burst (AX.25 allows several frames in one
/// continuous transmission), an estimated airtime, and — for the first frame of a burst — the
/// measured pre-data carrier time (the transmitting station's effective TXDELAY, the input to an
/// excess-TXDELAY detector). This is the layer standard KISS cannot provide: the modem never
/// sees signal strength or carrier edges; the radio's control channel does.
/// </summary>
/// <remarks>
/// <para>
/// Attribution is timestamp-correlation, not wire-level tagging. With a carrier-sense-capable
/// radio, a frame is attributed to the transmission window that contains its arrival (delivery
/// trails end-of-RF by the modem's decode+serial latency — windows stay open for
/// <see cref="RssiTaggingOptions.WindowAttributionSlack"/> past carrier-fall to absorb that).
/// Without carrier-sense, attribution falls back to a threshold-over-noise-floor filter across
/// <see cref="RssiTaggingOptions.AttributionLookback"/>; window-derived fields stay null.
/// Frames with no qualifying sample get <c>null</c> metadata rather than a guess.
/// </para>
/// <para>
/// Bench-measured context for the derived timing fields (Tait CCDI at 28800 Bd, 1200 Bd AFSK
/// link): RSSI poll round trip 14.4 ms median (p95 15.2 ms); carrier-edge report latency ~27 ms
/// with under ±2 ms jitter; <see cref="RadioMetadata.PreDataCarrier"/> tracked the transmitting
/// TNC's configured TXDELAY within a ~40–75 ms constant overhead.
/// </para>
/// </remarks>
public sealed class RssiTaggingTransport : IAx25Transport, IAsyncDisposable
{
    private readonly IAx25Transport inner;
    private readonly IRadioControl radio;
    private readonly RssiTaggingOptions options;
    private readonly TimeProvider clock;
    private readonly CancellationTokenSource samplerCts = new();
    private readonly Task samplerLoop;
    private readonly object gate = new();
    private readonly Queue<(DateTimeOffset At, float Dbm)> samples = new();
    private readonly Queue<CarrierWindow> windows = new();
    private CarrierWindow? currentWindow;
    private double? noiseFloorDbm;
    private int disposed;

    /// <summary>
    /// Wrap <paramref name="inner"/>, sampling RSSI from <paramref name="radio"/> (which must
    /// advertise <see cref="RadioCapabilities.RssiRead"/>). Ownership of both is NOT taken:
    /// dispose order stays with the caller, though disposing this stops the sampler.
    /// </summary>
    public RssiTaggingTransport(
        IAx25Transport inner,
        IRadioControl radio,
        RssiTaggingOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(radio);
        if (!radio.Capabilities.HasFlag(RadioCapabilities.RssiRead))
        {
            throw new ArgumentException("radio does not support RSSI reads", nameof(radio));
        }

        this.inner = inner;
        this.radio = radio;
        this.options = options ?? new RssiTaggingOptions();
        clock = timeProvider ?? TimeProvider.System;

        if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
        {
            radio.CarrierSenseChanged += OnCarrierSenseChanged;
        }

        samplerLoop = Task.Run(() => SampleLoopAsync(samplerCts.Token));
    }

    /// <summary>Current noise-floor estimate in dBm (EMA over channel-idle samples), or
    /// <c>null</c> until the first idle sample lands.</summary>
    public float? NoiseFloorDbm
    {
        get
        {
            lock (gate)
            {
                return noiseFloorDbm is { } nf ? (float)nf : null;
            }
        }
    }

    /// <inheritdoc/>
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
        inner.SendAsync(ax25, cancellationToken);

    /// <summary>
    /// <see cref="IAx25Transport"/>: the inner transport's inbound stream with
    /// <see cref="Ax25InboundFrame.Radio"/> populated wherever attribution succeeded.
    /// </summary>
    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame with { Radio = Attribute(frame.ReceivedAt, frame.Ax25.Length) };
        }
    }

    private void OnCarrierSenseChanged(object? sender, CarrierSenseChange e)
    {
        lock (gate)
        {
            if (e.Busy)
            {
                currentWindow = new CarrierWindow(e.At);
            }
            else if (currentWindow is { } w)
            {
                w.FallAt = e.At;
                windows.Enqueue(w);
                currentWindow = null;
                while (windows.Count > 8)
                {
                    windows.Dequeue();
                }
            }
        }
    }

    private RadioMetadata? Attribute(DateTimeOffset receivedAt, int ax25Length)
    {
        TimeSpan? airtime = null;
        if (options.BitRateHzProvider?.Invoke() is int rate and > 0)
        {
            // Wire bytes + FCS(2) + one flag, ignoring bit-stuffing (runs 0-8% long).
            airtime = TimeSpan.FromSeconds((ax25Length + 3) * 8.0 / rate);
        }

        lock (gate)
        {
            double? floor = noiseFloorDbm;

            if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
            {
                var window = FindWindow(receivedAt);
                if (window is null)
                {
                    return BuildThresholdAttribution(receivedAt, floor, airtime);
                }

                int burstIndex = window.FramesDelivered++;
                var stats = RssiStatistics(
                    from: window.RiseAt,
                    to: window.FallAt is { } fall && fall < receivedAt ? fall : receivedAt);
                TimeSpan? preData = null;
                if (burstIndex == 0 && airtime is { } air)
                {
                    preData = receivedAt - air - window.RiseAt;
                }

                return new RadioMetadata(
                    RssiDbm: stats?.Median,
                    SnrDb: stats is { } s && floor is { } f ? s.Median - (float)f : null,
                    NoiseFloorDbm: floor is { } nf ? (float)nf : null,
                    RssiMinDbm: stats?.Min,
                    RssiMaxDbm: stats?.Max,
                    RssiSampleCount: stats?.Count ?? 0,
                    CarrierRiseAt: window.RiseAt,
                    BurstIndex: burstIndex,
                    EstimatedAirtime: airtime,
                    PreDataCarrier: preData);
            }

            return BuildThresholdAttribution(receivedAt, floor, airtime);
        }
    }

    /// <summary>Fallback when the radio has no carrier-sense channel (or the window was
    /// missed): samples above the noise floor within the lookback are taken as signal.</summary>
    private RadioMetadata? BuildThresholdAttribution(DateTimeOffset receivedAt, double? floor, TimeSpan? airtime)
    {
        var from = receivedAt - options.AttributionLookback;
        List<float> signal = [];
        foreach (var (at, dbm) in samples)
        {
            if (at < from || at > receivedAt)
            {
                continue;
            }
            if (floor is null || dbm >= floor.Value + options.SignalThresholdDb)
            {
                signal.Add(dbm);
            }
        }

        if (signal.Count == 0)
        {
            return airtime is null ? null : new RadioMetadata(EstimatedAirtime: airtime);
        }

        signal.Sort();
        float rssi = signal[signal.Count / 2];
        return new RadioMetadata(
            RssiDbm: rssi,
            SnrDb: floor is { } f ? rssi - (float)f : null,
            NoiseFloorDbm: floor is { } nf2 ? (float)nf2 : null,
            RssiMinDbm: signal[0],
            RssiMaxDbm: signal[^1],
            RssiSampleCount: signal.Count,
            EstimatedAirtime: airtime);
    }

    private CarrierWindow? FindWindow(DateTimeOffset receivedAt)
    {
        // The frame belongs to the window containing its arrival; delivery trails carrier-fall
        // by the modem's decode+serial latency, so closed windows stay eligible for a slack.
        if (currentWindow is { } open && open.RiseAt <= receivedAt)
        {
            return open;
        }
        CarrierWindow? best = null;
        foreach (var w in windows)
        {
            if (w.RiseAt <= receivedAt && receivedAt <= w.FallAt!.Value + options.WindowAttributionSlack)
            {
                best = w;
            }
        }
        return best;
    }

    private (float Median, float Min, float Max, int Count)? RssiStatistics(DateTimeOffset from, DateTimeOffset to)
    {
        List<float> inWindow = [];
        foreach (var (at, dbm) in samples)
        {
            if (at >= from && at <= to)
            {
                inWindow.Add(dbm);
            }
        }
        if (inWindow.Count == 0)
        {
            return null;
        }
        inWindow.Sort();
        return (inWindow[inWindow.Count / 2], inWindow[0], inWindow[^1], inWindow.Count);
    }

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Unknown busy-state (no carrier-sense capability, or no edge yet) samples fast:
                // better to spend a few serial round-trips than to miss a frame's on-air window.
                bool busy = radio.ChannelBusy ?? true;
                try
                {
                    float dbm = await radio.ReadRssiDbmAsync(cancellationToken).ConfigureAwait(false);
                    Record(dbm, busy);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // One missed poll is not fatal; the attribution window just gets sparser.
                    // Deliberately broad: a radio behind a reconnect facade throws
                    // ObjectDisposedException / InvalidOperationException while its control
                    // channel is being re-opened, and the sampler must outlive that window.
                }

                await Task.Delay(busy ? options.BusySamplePeriod : options.IdleSamplePeriod, clock, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Record(float dbm, bool channelBusy)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            samples.Enqueue((now, dbm));
            var horizon = now - options.AttributionLookback - options.AttributionLookback;
            while (samples.Count > 0 && samples.Peek().At < horizon)
            {
                samples.Dequeue();
            }

            // Idle samples feed the noise-floor EMA. When carrier-sense is unavailable we fall
            // back to "quieter than the current floor + threshold counts as idle", seeded by the
            // first sample ever seen.
            bool idle = radio.ChannelBusy is { } cs
                ? !cs && !channelBusy
                : noiseFloorDbm is null || dbm < noiseFloorDbm.Value + options.SignalThresholdDb;
            if (idle)
            {
                noiseFloorDbm = noiseFloorDbm is { } nf
                    ? nf + options.NoiseFloorSmoothing * (dbm - nf)
                    : dbm;
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
        if (radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
        {
            radio.CarrierSenseChanged -= OnCarrierSenseChanged;
        }
        await samplerCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await samplerLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        samplerCts.Dispose();
    }

    private sealed class CarrierWindow(DateTimeOffset riseAt)
    {
        public DateTimeOffset RiseAt { get; } = riseAt;

        public DateTimeOffset? FallAt { get; set; }

        public int FramesDelivered { get; set; }
    }
}

/// <summary>Tuning knobs for <see cref="RssiTaggingTransport"/>. The defaults suit a CCDI-class
/// control channel (measured 14.4 ms median poll round trip at 28 800 baud) under 1200–9600 Bd
/// packet traffic.</summary>
public sealed record RssiTaggingOptions
{
    /// <summary>Poll cadence while the channel is busy (or busy-state is unknown).</summary>
    public TimeSpan BusySamplePeriod { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Poll cadence while the channel is idle — these samples track the noise floor.</summary>
    public TimeSpan IdleSamplePeriod { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How far back from a frame's arrival instant samples may be attributed to it in
    /// the no-carrier-sense fallback. Should comfortably exceed the longest expected frame
    /// airtime + TXDELAY.</summary>
    public TimeSpan AttributionLookback { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long past carrier-fall a delivered frame may still be attributed to that
    /// window — covers the modem's end-of-frame decode + serial delivery latency (measured
    /// 34–115 ms at 1200 Bd / 57600 serial).</summary>
    public TimeSpan WindowAttributionSlack { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How far above the noise floor a sample must sit to count as signal (dB) in the
    /// no-carrier-sense fallback.</summary>
    public float SignalThresholdDb { get; init; } = 6f;

    /// <summary>EMA coefficient (0–1) for the idle-sample noise-floor tracker.</summary>
    public float NoiseFloorSmoothing { get; init; } = 0.2f;

    /// <summary>
    /// Supplies the receiving modem's current over-air bit rate in Hz (e.g.
    /// <c>NinoTncSerialPort.CurrentBitRateHz</c>), enabling
    /// <see cref="Packet.Ax25.Transport.RadioMetadata.EstimatedAirtime"/> and
    /// <see cref="Packet.Ax25.Transport.RadioMetadata.PreDataCarrier"/>. Return <c>null</c>
    /// when unknown. Consulted per frame, so mode changes are picked up live.
    /// </summary>
    public Func<int?>? BitRateHzProvider { get; init; }
}
