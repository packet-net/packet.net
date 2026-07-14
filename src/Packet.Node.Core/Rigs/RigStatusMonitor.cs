using Packet.Node.Core.Api;
using Packet.Node.Core.Configuration;
using Packet.Rig;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// A node-layer view of one port's attached rig: it owns the poll loop (frequency/mode/PTT at the
/// idle cadence; SWR/power meters at the fast cadence while the transmitter is keyed) and
/// projects the current state as a serialisable <see cref="RigStatus"/> on demand — the rig-side
/// sibling of <see cref="Radios.IRadioStatusMonitor"/>. The port supervisor creates one when a
/// rig attaches and disposes it on teardown (before the rig, so polling stops first).
/// </summary>
public interface IRigStatusMonitor : IAsyncDisposable
{
    /// <summary>Project the rig's current status. Non-blocking — reads captured state only.</summary>
    RigStatus Snapshot();

    /// <summary>Wake the poll loop for an immediate tick — called after a mutation (set
    /// frequency/mode) so the projection and the SSE feed reflect the change now rather than at
    /// the next cadence boundary. Non-blocking; a no-op on a disposed monitor.</summary>
    void RequestRefresh();
}

/// <summary>Builds the <see cref="IRigStatusMonitor"/> for an attached rig.</summary>
public static class RigStatusMonitors
{
    /// <summary>Create a status monitor for a just-connected <paramref name="rig"/> on
    /// <paramref name="portId"/>, publishing each tick's status to <paramref name="telemetry"/>
    /// when present. Never returns null — an attached rig always has a status.</summary>
    public static IRigStatusMonitor Create(
        string portId,
        PortRigConfig config,
        IRigControl rig,
        RigTelemetry? telemetry = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rig);
        return new RigStatusMonitor(portId, config, rig, telemetry, timeProvider);
    }
}

/// <summary>
/// The poll loop behind <see cref="IRigStatusMonitor"/>. Every read is gated by the rig's
/// advertised <see cref="RigCapabilities"/> (an unadvertised member is never called), and every
/// read is individually fault-isolated: a failed read nulls its field and the loop carries on —
/// a rig daemon bounce degrades the projection to <c>faulted</c> for a tick and self-heals when
/// the backend re-dials on the next one. Snapshots read captured state only; no rig I/O ever
/// happens on a request path.
/// </summary>
/// <remarks>
/// It owns the polling but <b>not</b> the rig (the port supervisor owns the rig's lifetime):
/// disposing this stops the loop but leaves the rig open, so the port can dispose the rig last.
/// All timing runs on the injected <see cref="TimeProvider"/> (<c>Task.Delay(interval, clock,
/// ct)</c>), so tests drive the loop with a fake clock.
/// </remarks>
public sealed class RigStatusMonitor : IRigStatusMonitor
{
    /// <summary>Idle poll cadence when <see cref="PortRigConfig.PollIntervalSeconds"/> is unset.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Transmitting (meter) poll cadence when <see cref="PortRigConfig.MeterIntervalSeconds"/>
    /// is unset.</summary>
    public static readonly TimeSpan DefaultMeterInterval = TimeSpan.FromSeconds(1);

    private readonly string portId;
    private readonly PortRigConfig config;
    private readonly IRigControl rig;
    private readonly RigTelemetry? telemetry;
    private readonly TimeProvider clock;
    private readonly TimeSpan pollInterval;
    private readonly TimeSpan meterInterval;
    private readonly CancellationTokenSource cts = new();
    private readonly Task loop;
    private readonly object gate = new();
    private readonly object wakeGate = new();
    private CancellationTokenSource? wake;

    // Captured state, written by the loop under the gate, read by Snapshot().
    private long? frequencyHz;
    private string? mode;
    private int? passbandHz;
    private bool? transmitting;
    private RigMeters? meters;
    private DateTimeOffset? sampledAt;
    private bool lastTickHealthy;
    private int disposed;

    internal RigStatusMonitor(
        string portId, PortRigConfig config, IRigControl rig, RigTelemetry? telemetry, TimeProvider? timeProvider)
    {
        this.portId = portId;
        this.config = config;
        this.rig = rig;
        this.telemetry = telemetry;
        clock = timeProvider ?? TimeProvider.System;
        pollInterval = config.PollIntervalSeconds is { } s and > 0 ? TimeSpan.FromSeconds(s) : DefaultPollInterval;
        meterInterval = config.MeterIntervalSeconds is { } m and > 0 ? TimeSpan.FromSeconds(m) : DefaultMeterInterval;
        loop = Task.Run(() => PollLoopAsync(cts.Token));
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool keyed;
            try
            {
                keyed = await TakeSampleAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Fast while transmitting so the SWR/power meters are live during a transmission —
            // the moment that matters for TX health; slow while idle so a quiet rig costs a
            // couple of CAT round-trips per poll interval and no more. The delay carries a
            // wake token so RequestRefresh() (post-mutation) ticks immediately.
            CancellationTokenSource delayCts;
            lock (wakeGate)
            {
                delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wake = delayCts;
            }
            try
            {
                await Task.Delay(keyed ? meterInterval : pollInterval, clock, delayCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Woken early by RequestRefresh — fall through to the next tick now.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                lock (wakeGate)
                {
                    if (ReferenceEquals(wake, delayCts))
                    {
                        wake = null;
                    }
                }
                delayCts.Dispose();
            }
        }
    }

    /// <inheritdoc/>
    public void RequestRefresh()
    {
        lock (wakeGate)
        {
            try
            {
                wake?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop just moved past this delay — the next tick is imminent anyway.
            }
        }
    }

    /// <summary>One poll tick. Returns whether the transmitter was observed keyed (drives the
    /// cadence choice).</summary>
    private async Task<bool> TakeSampleAsync(CancellationToken ct)
    {
        var caps = rig.Capabilities;
        var healthy = false;

        var freq = await TryReadAsync(
            caps.HasFlag(RigCapabilities.FrequencyGet), () => rig.GetFrequencyAsync(ct), ct).ConfigureAwait(false);
        healthy |= freq.Ok;

        (bool Ok, RigModeState Value) modeState = default;
        if (caps.HasFlag(RigCapabilities.ModeGet))
        {
            modeState = await TryReadAsync(true, () => rig.GetModeAsync(ct), ct).ConfigureAwait(false);
            healthy |= modeState.Ok;
        }

        var ptt = await TryReadAsync(
            caps.HasFlag(RigCapabilities.PttGet), () => rig.GetPttAsync(ct), ct).ConfigureAwait(false);
        healthy |= ptt.Ok;

        // Meters only while the transmitter is keyed — idle they read ~0 and each read is a CAT
        // round-trip we'd rather not spend.
        RigMeters? meterSample = null;
        if (ptt.Ok && ptt.Value)
        {
            var swr = await TryReadAsync(
                caps.HasFlag(RigCapabilities.SwrMeter), () => rig.ReadSwrAsync(ct), ct).ConfigureAwait(false);
            var watts = await TryReadAsync(
                caps.HasFlag(RigCapabilities.RfPowerMeterWatts), () => rig.ReadRfPowerWattsAsync(ct), ct).ConfigureAwait(false);
            var relative = await TryReadAsync(
                caps.HasFlag(RigCapabilities.RfPowerMeter), () => rig.ReadRfPowerAsync(ct), ct).ConfigureAwait(false);
            if (swr.Ok || watts.Ok || relative.Ok)
            {
                meterSample = new RigMeters(
                    Swr: swr.Ok ? swr.Value : null,
                    RfPowerWatts: watts.Ok ? watts.Value : null,
                    RfPowerRelative: relative.Ok ? relative.Value : null,
                    SampleAt: clock.GetUtcNow());
            }
        }

        lock (gate)
        {
            if (freq.Ok)
            {
                frequencyHz = freq.Value;
            }
            if (modeState.Ok)
            {
                mode = modeState.Value.Mode.Token;
                passbandHz = modeState.Value.PassbandHz;
            }
            transmitting = ptt.Ok ? ptt.Value : transmitting;
            if (meterSample is not null)
            {
                meters = meterSample;
            }
            lastTickHealthy = healthy;
            if (healthy)
            {
                sampledAt = clock.GetUtcNow();
            }
        }

        telemetry?.Publish(Snapshot());
        return ptt.Ok && ptt.Value;
    }

    // Per-read fault isolation: a RigException is a tick-level upset (daemon bounce, rig busy),
    // never a loop-level one. NotSupportedException should be unreachable (reads are
    // capability-gated) but is tolerated the same way for belt-and-braces.
    private static async Task<(bool Ok, T Value)> TryReadAsync<T>(
        bool supported, Func<ValueTask<T>> read, CancellationToken ct)
    {
        if (!supported)
        {
            return (false, default!);
        }
        try
        {
            return (true, await read().ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is RigException or NotSupportedException or ObjectDisposedException)
        {
            return (false, default!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <inheritdoc/>
    public RigStatus Snapshot()
    {
        lock (gate)
        {
            return new RigStatus(
                PortId: portId,
                Attached: true,
                Kind: config.Kind,
                Endpoint: config.DescribeEndpoint(),
                Backend: rig.Info.Backend,
                Manufacturer: rig.Info.Manufacturer,
                Model: rig.Info.Model,
                Capabilities: RigStatus.CapabilityNames(rig.Capabilities),
                ConnectionState: sampledAt is null ? "unknown" : lastTickHealthy ? "healthy" : "faulted",
                FrequencyHz: frequencyHz,
                Mode: mode,
                PassbandHz: passbandHz,
                Transmitting: transmitting,
                Meters: meters,
                SampledAt: sampledAt);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop observed the cancel mid-await — done either way.
        }
        cts.Dispose();
    }
}
