using Packet.Ax25.Transport;

namespace Packet.Kiss;

/// <summary>
/// Adapts a legacy <see cref="IKissModem"/> to the neutral <see cref="IAx25Transport"/> seam —
/// the migration shim that lets the AX.25 layer consume <see cref="IAx25Transport"/> while the
/// concrete transports still speak <see cref="IKissModem"/>. It pre-filters the KISS read model
/// to AX.25 Data frames (so the consumer never sees a KISS command) and surfaces the optional
/// capabilities: TX-completion (ACKMODE) and the CSMA channel-access params.
/// </summary>
/// <remarks>
/// <para>
/// This implements <see cref="ITxCompletionTransport"/> unconditionally because an
/// <see cref="IKissModem"/> always EXPOSES <see cref="IKissModem.SendFrameWithAckAsync"/> — but
/// the underlying modem may still throw <see cref="NotSupportedException"/> at call time (plain
/// serial KISS, AXUDP). The consumer therefore keeps a runtime fallback in addition to the
/// capability probe; the probe says "this transport can attempt completion", the runtime catch
/// handles a modem that turns out not to.
/// </para>
/// <para>Transitional: removed once the concrete transports implement <see cref="IAx25Transport"/>
/// natively (later migration step).</para>
/// </remarks>
public sealed class KissModemTransport(IKissModem modem, TimeProvider? timeProvider = null)
    : ITxCompletionTransport, ICsmaChannelParams
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <summary>The wrapped modem (for callers that still need the KISS-specific surface, e.g. mode-switch).</summary>
    public IKissModem Modem => modem;

    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default)
        => modem.SendFrameAsync(ax25, cancellationToken);

    public async IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var kiss in modem.ReadFramesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (kiss.Command != KissCommand.Data) continue; // drop non-AX.25 KISS commands here
            yield return new Ax25InboundFrame(kiss.Payload, kiss.Port, clock.GetUtcNow());
        }
    }

    public async Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        // The KISS ACKMODE sequence tag is auto-assigned inside the modem; it is a wire artefact
        // the neutral contract does not surface. NotSupportedException / TimeoutException propagate
        // unchanged — the consumer distinguishes them exactly as it did against IKissModem.
        var receipt = await modem.SendFrameWithAckAsync(ax25, timeout, sequenceTag: null, cancellationToken)
            .ConfigureAwait(false);
        return new TxCompletion(receipt.Queued, receipt.Acknowledged);
    }

    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => modem.SetTxDelayAsync(tenMsUnits, cancellationToken);

    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default)
        => modem.SetPersistenceAsync(value, cancellationToken);

    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => modem.SetSlotTimeAsync(tenMsUnits, cancellationToken);

    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default)
        => modem.SetTxTailAsync(tenMsUnits, cancellationToken);

    public ValueTask DisposeAsync() =>
        modem is IAsyncDisposable d ? d.DisposeAsync()
        : modem is IDisposable s ? Sync(s)
        : ValueTask.CompletedTask;

    private static ValueTask Sync(IDisposable d)
    {
        d.Dispose();
        return ValueTask.CompletedTask;
    }
}
