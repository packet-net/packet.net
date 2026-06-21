using Packet.Ax25.Transport;

namespace Packet.LinkBench.Channel;

/// <summary>
/// Pass-through decorator that records every <see cref="TxCompletion"/> the
/// inner transport returns. Slots between <c>PacingKissModem</c> and the channel
/// endpoint so the bench can report TX-completion echo round-trip stats — in
/// particular rung 2's open question (plan §7): is net-sim's 0x0C echo
/// immediate-on-receive (pacing is a no-op) or post-transmission (pacing real)?
/// Compare the recorded RTTs against the frame's modeled airtime.
/// </summary>
internal sealed class AckReceiptTap(ITxCompletionTransport inner, Action<TxCompletion> onReceipt)
    : ITxCompletionTransport, ICsmaChannelParams, IAsyncDisposable
{
    public Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken cancellationToken = default) =>
        inner.SendAsync(ax25, cancellationToken);

    public async Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var receipt = await inner.SendAwaitingCompletionAsync(ax25, timeout, cancellationToken)
            .ConfigureAwait(false);
        onReceipt(receipt);
        return receipt;
    }

    public IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
        inner.ReceiveAsync(cancellationToken);

    public Task SetTxDelayAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        inner is ICsmaChannelParams c ? c.SetTxDelayAsync(tenMsUnits, cancellationToken) : Task.CompletedTask;

    public Task SetPersistenceAsync(byte value, CancellationToken cancellationToken = default) =>
        inner is ICsmaChannelParams c ? c.SetPersistenceAsync(value, cancellationToken) : Task.CompletedTask;

    public Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        inner is ICsmaChannelParams c ? c.SetSlotTimeAsync(tenMsUnits, cancellationToken) : Task.CompletedTask;

    public Task SetTxTailAsync(byte tenMsUnits, CancellationToken cancellationToken = default) =>
        inner is ICsmaChannelParams c ? c.SetTxTailAsync(tenMsUnits, cancellationToken) : Task.CompletedTask;

    // Standard decorator inner-ownership chain (mirrors PacingKissModem).
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
