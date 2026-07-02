using System.Runtime.CompilerServices;
using Packet.Ax25.Transport;

namespace Packet.Radio;

/// <summary>
/// Decorates any <see cref="IAx25Transport"/> with per-frame radio signal metadata: a background
/// sampler polls <see cref="IRadioControl.ReadRssiDbmAsync"/> (fast while the channel is busy,
/// slow while idle, so the idle samples double as a noise-floor estimate), and every inbound
/// frame is re-yielded with <see cref="Ax25InboundFrame.Radio"/> populated — RSSI attributed
/// from the samples taken while the frame was on air, SNR as RSSI minus the tracked noise floor.
/// This is the piece standard KISS cannot do: the modem never sees signal strength, the radio's
/// control channel does.
/// </summary>
/// <remarks>
/// Attribution is timestamp-correlation, not wire-level tagging: samples within
/// <see cref="RssiTaggingOptions.AttributionLookback"/> of the frame's
/// <see cref="Ax25InboundFrame.ReceivedAt"/> that sit above the noise floor by at least
/// <see cref="RssiTaggingOptions.SignalThresholdDb"/> are taken as "the signal that carried this
/// frame" and reduced to their median. Frames that arrive with no qualifying sample (very short
/// bursts between polls) get <c>null</c> metadata rather than a guess.
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
            yield return frame with { Radio = Attribute(frame.ReceivedAt) };
        }
    }

    private RadioMetadata? Attribute(DateTimeOffset receivedAt)
    {
        var from = receivedAt - options.AttributionLookback;
        List<float> signal = [];
        double? floor;
        lock (gate)
        {
            floor = noiseFloorDbm;
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
        }

        if (signal.Count == 0)
        {
            return null;
        }

        signal.Sort();
        float rssi = signal[signal.Count / 2];
        float? snr = floor is { } f ? rssi - (float)f : null;
        return new RadioMetadata(rssi, snr);
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
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    // One missed poll is not fatal; the attribution window just gets sparser.
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
}

/// <summary>Tuning knobs for <see cref="RssiTaggingTransport"/>. The defaults suit a CCDI-class
/// control channel (~10 ms per poll at 28 800 baud) under 1200–9600 baud packet traffic.</summary>
public sealed record RssiTaggingOptions
{
    /// <summary>Poll cadence while the channel is busy (or busy-state is unknown).</summary>
    public TimeSpan BusySamplePeriod { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Poll cadence while the channel is idle — these samples track the noise floor.</summary>
    public TimeSpan IdleSamplePeriod { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How far back from a frame's arrival instant samples may be attributed to it.
    /// Should comfortably exceed the longest expected frame airtime + TXDELAY.</summary>
    public TimeSpan AttributionLookback { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How far above the noise floor a sample must sit to count as signal (dB).</summary>
    public float SignalThresholdDb { get; init; } = 6f;

    /// <summary>EMA coefficient (0–1) for the idle-sample noise-floor tracker.</summary>
    public float NoiseFloorSmoothing { get; init; } = 0.2f;
}
