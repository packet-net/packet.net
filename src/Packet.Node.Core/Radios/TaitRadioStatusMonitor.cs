using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Radio.Tait;

namespace Packet.Node.Core.Radios;

/// <summary>
/// The <see cref="IRadioStatusMonitor"/> for a Tait CCDI radio: it runs a
/// <see cref="TaitRadioHealthMonitor"/> at the port's configured cadence (default 10 s), keeps the
/// most recent sample, and queries the radio's identity once at attach — then projects the current
/// connection state, carrier-sense, identity, and latest health into a <see cref="RadioStatus"/>.
/// </summary>
/// <remarks>
/// It owns the health monitor but <b>not</b> the radio (the port supervisor owns the radio's
/// lifetime): disposing this stops sampling but leaves the radio open, so the port can dispose the
/// radio last. Every projection reads already-captured state — no serial I/O on the request path —
/// so a faulted or silent radio degrades to null health / a <c>faulted</c> connection state rather
/// than blocking or throwing.
/// </remarks>
public sealed class TaitRadioStatusMonitor : IRadioStatusMonitor
{
    private static readonly TimeSpan DefaultHealthInterval = TimeSpan.FromSeconds(10);

    private readonly string portId;
    private readonly PortRadioConfig config;
    private readonly TaitCcdiRadio radio;
    private readonly TaitRadioHealthMonitor health;
    private readonly CancellationTokenSource identityCts = new();
    private readonly object gate = new();
    private TaitRadioHealthSample? latestSample;
    private TaitRadioIdentity? identity;
    private int disposed;

    /// <summary>Start health sampling on <paramref name="radio"/> for <paramref name="portId"/> and
    /// kick a one-shot identity query. Ownership of the radio is NOT taken.</summary>
    public TaitRadioStatusMonitor(
        string portId, PortRadioConfig config, TaitCcdiRadio radio, TimeProvider? timeProvider = null)
    {
        this.portId = portId ?? throw new ArgumentNullException(nameof(portId));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.radio = radio ?? throw new ArgumentNullException(nameof(radio));

        var interval = config.HealthIntervalSeconds is { } s and > 0
            ? TimeSpan.FromSeconds(s)
            : DefaultHealthInterval;
        health = new TaitRadioHealthMonitor(
            radio, new TaitRadioHealthMonitorOptions { SampleInterval = interval }, timeProvider);
        health.SampleTaken += OnSampleTaken;

        _ = Task.Run(() => QueryIdentityAsync(identityCts.Token));
    }

    private void OnSampleTaken(object? sender, TaitRadioHealthSample sample)
    {
        lock (gate)
        {
            latestSample = sample;
        }
    }

    // Best-effort identity query at attach (a few retries — a freshly-opened port can still be
    // settling). A radio that never answers leaves identity null, which the snapshot reports honestly.
    private async Task QueryIdentityAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3 && !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                var id = await radio.QueryIdentityAsync(ct).ConfigureAwait(false);
                lock (gate)
                {
                    identity = id;
                }
                return;
            }
            catch (Exception ex) when (ex is TaitCcdiException or TimeoutException or FormatException
                or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <inheritdoc/>
    public RadioStatus Snapshot()
    {
        TaitRadioHealthSample? sample;
        TaitRadioIdentity? id;
        lock (gate)
        {
            sample = latestSample;
            id = identity;
        }

        var summary = health.Summarize();
        RadioHealth? healthProjection = sample is null ? null : new RadioHealth(
            RssiDbm: sample.RssiDbm,
            AveragedRssiDbm: summary.RssiDbm is { } stat ? (float)stat.Median : null,
            PaTemperatureC: sample.PaTemperatureCelsius,
            ForwardTrendMillivolts: sample.TxForwardOverIdleMillivolts,
            ReverseTrendMillivolts: sample.TxReverseOverIdleMillivolts,
            ReverseForwardRatio: sample.TxReverseForwardRatio is { } r ? Math.Round(r, 3) : null,
            SampleAt: sample.At);

        string? serial = !string.IsNullOrWhiteSpace(config.Serial) ? config.Serial : id?.SerialNumber;

        return new RadioStatus(
            PortId: portId,
            Attached: true,
            Kind: config.Kind,
            ControlPort: radio.PortName,
            Serial: string.IsNullOrWhiteSpace(serial) ? null : serial,
            Identity: id is null ? null : new RadioIdentity(id.ProductName, id.CcdiVersion),
            ConnectionState: radio.ConnectionState == TaitConnectionState.Faulted ? "faulted" : "healthy",
            ChannelBusy: radio.ChannelBusy,
            Health: healthProjection);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        health.SampleTaken -= OnSampleTaken;
        await identityCts.CancelAsync().ConfigureAwait(false);
        await health.DisposeAsync().ConfigureAwait(false);
        identityCts.Dispose();
    }
}
