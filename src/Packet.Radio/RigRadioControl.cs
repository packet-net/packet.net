using Packet.Rig;

namespace Packet.Radio;

/// <summary>
/// Surfaces a CAT rig (<c>Packet.Rig</c>'s <see cref="IRigControl"/> — hamlib's <c>rigctld</c>,
/// flrig, …) as the packet stack's radio-control seam: the rig's receive-side reads
/// (<see cref="IRigControl.ReadDcdAsync"/> / <see cref="IRigControl.ReadSignalStrengthDbmAsync"/>)
/// become <see cref="ChannelBusy"/>/<see cref="CarrierSenseChanged"/> and
/// <see cref="ReadRssiDbmAsync"/>, and <see cref="IRigControl.SetPttAsync"/> becomes
/// <see cref="SetTransmitterAsync"/>. The inverse-direction sibling of
/// <c>Packet.Radio.Tait</c>'s <c>TaitRigControl</c> (a radio re-presented through the rig seam):
/// here a rig is re-presented through the radio seam, so a CAT transceiver can feed the same
/// CSMA gate and per-frame RSSI machinery a push-capable PMR radio does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Poll-based carrier sense.</b> Rig backends are poll-only (no push/notification channel),
/// so DCD is sampled by an owned loop at <see cref="RigRadioControlOptions.DcdPollInterval"/>
/// and <see cref="CarrierSenseChanged"/> edges are synthesized from consecutive samples — a
/// poll-based source cannot see edges shorter than the poll interval. A failed read
/// (<see cref="RigException"/>) marks <see cref="ChannelBusy"/> <c>null</c> (unknown ⇒ the CSMA
/// gate fails open) and backs off to <see cref="RigRadioControlOptions.FaultRetryInterval"/>;
/// the rig backend re-dials on the next call, so the loop self-heals on the next successful
/// read. Recovery repopulates <see cref="ChannelBusy"/> and fires an edge only if the
/// re-observed value differs from the last known one — the unknown window itself is never an
/// edge. The loop only runs when the mapped capabilities include
/// <see cref="RadioCapabilities.CarrierSense"/>.
/// </para>
/// <para>
/// <b>Capability mapping</b> is computed once at construction:
/// <see cref="RigCapabilities.DcdRead"/> → <see cref="RadioCapabilities.CarrierSense"/>,
/// <see cref="RigCapabilities.SignalStrengthRead"/> → <see cref="RadioCapabilities.RssiRead"/>,
/// <see cref="RigCapabilities.PttSet"/> → <see cref="RadioCapabilities.TransmitterControl"/>.
/// A rig advertising none of the three is rejected with <see cref="ArgumentException"/> — it
/// offers nothing the packet-medium seam can use.
/// </para>
/// <para>
/// <b>Ownership and unkey.</b> <c>ownsRig: true</c> transfers the rig's lifetime to this
/// adapter (the node factory hands it a dedicated rig connection): dispose stops the poll loop,
/// then disposes the rig. <c>false</c> (default) leaves the rig to outlive the adapter. Dispose
/// best-effort unkeys (swallowing failures) only when the last transmitter command through this
/// adapter was a key AND the rig is not owned — an owned rig's own dispose already guarantees
/// best-effort unkey (the <see cref="IRigControl.SetPttAsync"/> contract), so the adapter
/// avoids issuing a second one.
/// </para>
/// </remarks>
public sealed class RigRadioControl : IRadioControl
{
    private readonly IRigControl rig;
    private readonly RigRadioControlOptions options;
    private readonly TimeProvider clock;
    private readonly bool ownsRig;
    private readonly CancellationTokenSource cts = new();
    private readonly Task pollLoop;
    private readonly object gate = new();

    // Captured state, written by the poll loop under the gate. channelBusy is what callers see
    // (null while unknown/faulting); lastKnownBusy survives fault windows so recovery can tell
    // a genuine edge from a re-observation of the same value.
    private bool? channelBusy;
    private bool? lastKnownBusy;
    private volatile bool lastCommandedPtt;
    private int disposed;

    /// <summary>
    /// Wrap <paramref name="rig"/>, mapping its capabilities onto the radio seam (see the class
    /// remarks) and starting the DCD poll loop when carrier-sense is available.
    /// <paramref name="ownsRig"/> transfers disposal: when true, disposing the adapter disposes
    /// the rig after stopping the loop; when false the rig outlives the adapter. All timing runs
    /// on <paramref name="timeProvider"/> (<see cref="TimeProvider.System"/> when null).
    /// </summary>
    /// <exception cref="ArgumentException">The rig advertises none of
    /// <see cref="RigCapabilities.DcdRead"/> / <see cref="RigCapabilities.SignalStrengthRead"/> /
    /// <see cref="RigCapabilities.PttSet"/>.</exception>
    public RigRadioControl(
        IRigControl rig,
        RigRadioControlOptions? options = null,
        TimeProvider? timeProvider = null,
        bool ownsRig = false)
    {
        ArgumentNullException.ThrowIfNull(rig);

        var rigCaps = rig.Capabilities;
        var caps = RadioCapabilities.None;
        if (rigCaps.HasFlag(RigCapabilities.DcdRead))
        {
            caps |= RadioCapabilities.CarrierSense;
        }
        if (rigCaps.HasFlag(RigCapabilities.SignalStrengthRead))
        {
            caps |= RadioCapabilities.RssiRead;
        }
        if (rigCaps.HasFlag(RigCapabilities.PttSet))
        {
            caps |= RadioCapabilities.TransmitterControl;
        }
        if (caps == RadioCapabilities.None)
        {
            throw new ArgumentException(
                "The rig advertises none of DcdRead / SignalStrengthRead / PttSet — nothing the " +
                "packet-medium seam can use.", nameof(rig));
        }

        this.rig = rig;
        this.options = options ?? new RigRadioControlOptions();
        this.ownsRig = ownsRig;
        clock = timeProvider ?? TimeProvider.System;
        Capabilities = caps;

        pollLoop = caps.HasFlag(RadioCapabilities.CarrierSense)
            ? Task.Run(() => PollLoopAsync(cts.Token))
            : Task.CompletedTask;
    }

    /// <inheritdoc/>
    public RadioCapabilities Capabilities { get; }

    /// <inheritdoc/>
    /// <remarks>Written by the DCD poll loop: <c>null</c> before the first successful sample and
    /// while the rig is faulting (unknown fails open at the CSMA gate), else the last sampled
    /// value — at most one poll interval stale.</remarks>
    public bool? ChannelBusy
    {
        get
        {
            lock (gate)
            {
                return channelBusy;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Synthesized from consecutive DCD samples, so edges shorter than the poll
    /// interval are invisible (see the class remarks). Handlers run on the poll loop: keep them
    /// fast and non-blocking, or the sampling cadence suffers.</remarks>
    public event EventHandler<CarrierSenseChange>? CarrierSenseChanged;

    /// <inheritdoc/>
    public async ValueTask<float> ReadRssiDbmAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Capabilities.HasFlag(RadioCapabilities.RssiRead))
        {
            throw new NotSupportedException(
                "The rig does not advertise SignalStrengthRead — RSSI cannot be served through " +
                "this adapter. Probe Capabilities for RadioCapabilities.RssiRead before calling.");
        }
        return (float)await rig.ReadSignalStrengthDbmAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask SetTransmitterAsync(bool transmit, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Capabilities.HasFlag(RadioCapabilities.TransmitterControl))
        {
            throw new NotSupportedException(
                "The rig does not advertise PttSet — the transmitter cannot be keyed through " +
                "this adapter. Probe Capabilities for RadioCapabilities.TransmitterControl before calling.");
        }
        await rig.SetPttAsync(transmit, cancellationToken).ConfigureAwait(false);
        lastCommandedPtt = transmit;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool faulted;
            try
            {
                bool busy = await rig.ReadDcdAsync(ct).ConfigureAwait(false);
                OnSample(busy);
                faulted = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            // Per-read fault isolation: a RigException is a tick-level upset (daemon bounce, rig
            // busy), never a loop-level one. NotSupportedException should be unreachable (the
            // loop only runs when DcdRead is advertised) and a stray backend-internal
            // cancellation is a fault, not our shutdown — both are tolerated the same way for
            // belt-and-braces.
            catch (Exception ex) when (ex is RigException or NotSupportedException
                or ObjectDisposedException or OperationCanceledException)
            {
                OnFault();
                faulted = true;
            }

            try
            {
                await Task.Delay(faulted ? options.FaultRetryInterval : options.DcdPollInterval, clock, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnSample(bool busy)
    {
        var at = clock.GetUtcNow();
        bool edge;
        lock (gate)
        {
            // Only a genuine bool→bool transition is an edge: the first-ever sample and a
            // fault-recovery re-observation of the same value merely (re)populate ChannelBusy.
            edge = lastKnownBusy is { } last && last != busy;
            lastKnownBusy = busy;
            channelBusy = busy;
        }
        if (edge)
        {
            CarrierSenseChanged?.Invoke(this, new CarrierSenseChange(busy, at));
        }
    }

    private void OnFault()
    {
        lock (gate)
        {
            // Unknown fails open at the CSMA gate. lastKnownBusy is deliberately retained so
            // recovery can suppress the non-edge (same value re-observed) case.
            channelBusy = null;
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
            await pollLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop observed the cancel mid-await — done either way.
        }
        cts.Dispose();

        if (lastCommandedPtt && !ownsRig)
        {
            // The rig outlives this adapter, so its own dispose-unkey won't run — unkey here.
            try
            {
                using var unkeyCts = new CancellationTokenSource(TimeSpan.FromSeconds(2), clock);
                await rig.SetPttAsync(false, unkeyCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RigException or NotSupportedException
                or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort only — the link (or the rig object) may already be gone.
            }
        }

        if (ownsRig)
        {
            await rig.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Tuning knobs for <see cref="RigRadioControl"/>'s DCD poll loop.</summary>
public sealed record RigRadioControlOptions
{
    /// <summary>Cadence of the DCD poll while reads are succeeding. The default suits CSMA —
    /// which wants a fresh answer before every keyup — against a local rigctld, whose loopback
    /// round trip is sub-millisecond; slow it down for a rig daemon at the end of a real
    /// link.</summary>
    public TimeSpan DcdPollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Retry cadence after a failed DCD read, while <see cref="RigRadioControl.ChannelBusy"/>
    /// reads <c>null</c>. Deliberately much slower than <see cref="DcdPollInterval"/> — the rig
    /// backend re-dials on the next call, and hammering a bounced daemon helps nobody.</summary>
    public TimeSpan FaultRetryInterval { get; init; } = TimeSpan.FromSeconds(2);
}
