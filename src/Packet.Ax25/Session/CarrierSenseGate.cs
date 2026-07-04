using System.Globalization;
using Packet.Ax25.Transport;

namespace Packet.Ax25.Session;

/// <summary>
/// The native carrier-sense CSMA gate at the AX.25 link-multiplexer's transmit path. The
/// link multiplexer is "the medium-access arbiter that owns the radio and serialises
/// transmissions" (see <see cref="LinkMultiplexerSignal"/>); before it keys the radio it
/// consults this gate, which holds the transmission while <see cref="ICarrierSense"/>
/// reports the channel busy and releases it once the channel clears (or a bounded wait
/// expires — fail-open). This is the production form of hardware DCD driving channel
/// access, and it supersedes the transport-level <c>Packet.Radio.CarrierSenseTxGate</c>
/// interim decorator: the stack itself now defers the keyup rather than an opaque wrapper.
/// </summary>
/// <remarks>
/// <para>
/// Wait-for-clear with slot-time polling — the simplest of the §6.4.2 p-persistence /
/// slot-time CSMA family, and enough for a half-duplex packet channel. <see cref="ICarrierSense.ChannelBusy"/>
/// is re-read each slot, so a live driver's DCD edge is picked up within one slot. An
/// unknown busy-state (no source, no edge seen yet, telemetry faulted) is treated as clear
/// and fails open: traffic must never stop because carrier-sense went dark.
/// </para>
/// <para>
/// Off by default: constructed with a <c>null</c> source, the gate always reports clear
/// immediately, so a stack with no carrier-sense wired behaves byte-for-byte as before —
/// the SDL transition behaviour is unchanged; only the <em>physical</em> keyup is deferred,
/// and only when a source is present and the channel is genuinely busy. The gate does not
/// touch the data-link state machine, so it composes with the SDL conformance harness
/// without altering any transition.
/// </para>
/// </remarks>
public sealed class CarrierSenseGate
{
    private readonly ICarrierSense? source;
    private readonly TimeProvider clock;
    private readonly CarrierSenseGateOptions options;

    /// <summary>
    /// Build a gate over an optional carrier-sense source. A <c>null</c>
    /// <paramref name="source"/> is the degenerate always-clear gate (no CSMA — the stack
    /// keys up immediately, exactly as with no gate at all).
    /// </summary>
    /// <param name="source">The carrier-sense source to consult, or <c>null</c> for always-clear.</param>
    /// <param name="timeProvider">Clock driving the slot-time wait; <c>null</c> uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="options">CSMA tuning (slot time, bounded wait, fail-open); <c>null</c> uses defaults.</param>
    public CarrierSenseGate(
        ICarrierSense? source,
        TimeProvider? timeProvider = null,
        CarrierSenseGateOptions? options = null)
    {
        this.source = source;
        clock = timeProvider ?? TimeProvider.System;
        this.options = options ?? new CarrierSenseGateOptions();
    }

    /// <summary>
    /// True when a carrier-sense source is attached — i.e. this port does native CSMA. False
    /// for the always-clear degenerate gate. (Inspection convenience for wiring assertions;
    /// does not affect transmission.)
    /// </summary>
    public bool HasSource => source is not null;

    /// <summary>
    /// Await a clear channel before the caller keys the radio. Completes <em>synchronously</em>
    /// when there is no source or the channel is clear/unknown (so the common path adds no
    /// async hop and no reordering); otherwise polls the source every
    /// <see cref="CarrierSenseGateOptions.SlotTime"/> until it clears or
    /// <see cref="CarrierSenseGateOptions.MaxWait"/> elapses.
    /// </summary>
    /// <returns>How long the caller waited for the channel to clear (<see cref="TimeSpan.Zero"/> when it was already clear).</returns>
    /// <exception cref="TimeoutException">
    /// When the bounded wait expires and <see cref="CarrierSenseGateOptions.FailOpen"/> is
    /// <c>false</c>. With fail-open <c>true</c> (the default) the wait instead returns and the
    /// caller transmits anyway.
    /// </exception>
    public ValueTask<TimeSpan> WaitForClearAsync(CancellationToken cancellationToken = default)
    {
        // Fast path: no source, or the channel is clear/unknown -> key up now. A definite
        // "busy" is the only thing that defers; unknown (null) fails open by design.
        if (source is null || source.ChannelBusy != true)
        {
            return new ValueTask<TimeSpan>(TimeSpan.Zero);
        }

        return new ValueTask<TimeSpan>(WaitLoopAsync(cancellationToken));
    }

    private async Task<TimeSpan> WaitLoopAsync(CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        while (true)
        {
            // Re-read each slot: only a definite "busy" holds us; a clear or an unknown
            // state (source went dark) releases the keyup (fail-open).
            if (source!.ChannelBusy != true)
            {
                return clock.GetUtcNow() - started;
            }

            var waited = clock.GetUtcNow() - started;
            if (waited >= options.MaxWait)
            {
                if (options.FailOpen)
                {
                    return waited;
                }

                throw new TimeoutException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"channel still busy after {options.MaxWait.TotalSeconds:0.#}s carrier-sense wait"));
            }

            await Task.Delay(options.SlotTime, clock, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Tuning for <see cref="CarrierSenseGate"/> — the slot-time CSMA knobs.</summary>
public sealed record CarrierSenseGateOptions
{
    /// <summary>
    /// How often the gate re-samples carrier-sense while waiting for a busy channel to clear
    /// (the CSMA slot interval). Default 100 ms — the KISS SLOTTIME default (10 × 10 ms).
    /// </summary>
    public TimeSpan SlotTime { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Longest a transmission is held waiting for the channel to clear before fail-open.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// When the bounded wait expires: <c>true</c> (default) transmits anyway — carrier-sense
    /// must never wedge traffic; <c>false</c> surfaces a <see cref="TimeoutException"/>.
    /// </summary>
    public bool FailOpen { get; init; } = true;
}
