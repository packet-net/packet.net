using System.Runtime.CompilerServices;
using Packet.Ax25.Transport;

namespace Packet.Radio;

/// <summary>
/// CSMA by hardware carrier-sense: decorates an <see cref="IAx25Transport"/> so that
/// <see cref="SendAsync"/> defers while the radio reports the channel busy, keying up only once
/// it clears. This is the production form of the LinkBench <c>ITxGatePolicy</c> seam, driven by
/// a real radio's DCD instead of a simulated channel — and it is materially better than what a
/// modem can do alone, because the radio raises carrier-sense at RF detection, typically the
/// whole preamble + frame ahead of the modem's decoded output (bench-measured 0.5–1 s at
/// 1200 Bd).
/// </summary>
/// <remarks>
/// <para>
/// The gate is deliberately simple: wait-for-clear with a bounded wait, then hand off to the
/// inner transport (whose TNC still applies its own persistence/slot-time CSMA on its audio
/// DCD — the two compose). An unknown busy state (radio faulted, no edge seen yet, no
/// carrier-sense capability) fails open: traffic must not stop because telemetry did.
/// </para>
/// <para>
/// <b>Superseded (OQ-012).</b> This transport-level decorator was the interim/degenerate form
/// of carrier-sense CSMA — a blunt wrapper with no connection to the AX.25 stack's medium-access
/// model. The native seam now lives inside the stack: inject an
/// <see cref="ICarrierSense"/> into <c>Packet.Ax25.Session.Ax25Listener</c> (the node bridges a
/// radio-attached port's <see cref="IRadioControl"/> DCD via <see cref="RadioCarrierSense"/>),
/// and <c>Packet.Ax25.Session.CarrierSenseGate</c> holds the keyup at the link-multiplexer's
/// transmit path — the same seam the coming Nino KISS DCD extension lands in. Prefer that. This
/// type is retained only as a fallback for a bare transport with no medium-access layer (a raw
/// <see cref="IAx25Transport"/> consumer not going through <c>Ax25Listener</c>).
/// </para>
/// </remarks>
[Obsolete(
    "Superseded by the native carrier-sense seam (OQ-012): inject an ICarrierSense into " +
    "Ax25Listener (see RadioCarrierSense) so the link-multiplexer's CarrierSenseGate defers " +
    "keyups. This transport-level decorator remains only as a degenerate fallback for stacks " +
    "with no medium-access layer.")]
public sealed class CarrierSenseTxGate : IAx25Transport, IAsyncDisposable
{
    private readonly IAx25Transport inner;
    private readonly IRadioControl radio;
    private readonly CarrierSenseTxGateOptions options;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private TaskCompletionSource clearTcs = CreateTcs();
    private int disposed;

    /// <summary>Wrap <paramref name="inner"/>, gating transmissions on
    /// <paramref name="radio"/>'s carrier-sense (which must advertise
    /// <see cref="RadioCapabilities.CarrierSense"/>). Ownership of both stays with the caller.</summary>
    public CarrierSenseTxGate(
        IAx25Transport inner,
        IRadioControl radio,
        CarrierSenseTxGateOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(radio);
        if (!radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense))
        {
            throw new ArgumentException("radio does not report carrier-sense", nameof(radio));
        }

        this.inner = inner;
        this.radio = radio;
        this.options = options ?? new CarrierSenseTxGateOptions();
        clock = timeProvider ?? TimeProvider.System;
        radio.CarrierSenseChanged += OnCarrierSenseChanged;
    }

    /// <summary>Fired when a transmission was actually deferred, with how long it waited —
    /// the observability hook for "what is carrier-sense CSMA buying us".</summary>
    public event EventHandler<TimeSpan>? TransmissionDeferred;

    /// <inheritdoc/>
    public async Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
    {
        var waited = await WaitForClearAsync(cancellationToken).ConfigureAwait(false);
        if (waited > TimeSpan.Zero)
        {
            TransmissionDeferred?.Invoke(this, waited);
        }
        await inner.SendAsync(ax25, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inner.ReceiveAsync(cancellationToken);

    private async Task<TimeSpan> WaitForClearAsync(CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        var deadline = started + options.MaxWait;
        while (true)
        {
            Task clearTask;
            lock (gate)
            {
                // Only a definite "busy" defers; unknown fails open by design.
                if (radio.ChannelBusy != true)
                {
                    return clock.GetUtcNow() - started;
                }
                clearTask = clearTcs.Task;
            }

            var remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return HandleTimeout(started);
            }

            try
            {
                await clearTask.WaitAsync(remaining, clock, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return HandleTimeout(started);
            }
        }
    }

    private TimeSpan HandleTimeout(DateTimeOffset started)
    {
        if (options.FailOpen)
        {
            return clock.GetUtcNow() - started;
        }
        throw new TimeoutException(
            $"channel still busy after {options.MaxWait.TotalSeconds:0.#}s carrier-sense wait");
    }

    private void OnCarrierSenseChanged(object? sender, CarrierSenseChange e)
    {
        lock (gate)
        {
            if (e.Busy)
            {
                if (clearTcs.Task.IsCompleted)
                {
                    clearTcs = CreateTcs();
                }
            }
            else
            {
                clearTcs.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CreateTcs() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            radio.CarrierSenseChanged -= OnCarrierSenseChanged;
            lock (gate)
            {
                clearTcs.TrySetResult();
            }
        }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Tuning for <see cref="CarrierSenseTxGate"/>.</summary>
public sealed record CarrierSenseTxGateOptions
{
    /// <summary>Longest a transmission will be held waiting for the channel to clear.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>When the wait expires: <c>true</c> (default) transmits anyway — telemetry must
    /// never wedge traffic; <c>false</c> surfaces a <see cref="TimeoutException"/>.</summary>
    public bool FailOpen { get; init; } = true;
}
