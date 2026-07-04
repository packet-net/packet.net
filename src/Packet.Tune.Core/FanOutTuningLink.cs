using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Packet.Tune.Core;

/// <summary>
/// An <see cref="ITuningLink"/> that lets <b>several consumers share one underlying link</b>:
/// a single pump reads the inner link's receive stream once and broadcasts each telegram to
/// every active <see cref="ReceiveAsync"/> enumerator, and every <see cref="SendAsync"/> passes
/// through to the inner link.
/// </summary>
/// <remarks>
/// <para>
/// This is what lets a PDN node run a resident <see cref="StationHailResponder"/> and issue
/// on-demand hails through a <see cref="StationHailer"/> on the <em>same</em> radio side channel:
/// a real radio's SDM buffer is one-deep, so two independent <see cref="SdmTuningLink"/>s on one
/// radio would steal each other's datagrams. Sharing one link via this fan-out avoids that — the
/// responder ignores <c>STAT</c> telegrams and the hailer ignores <c>HAIL</c> telegrams, so the
/// two never cross-talk even though both see every inbound telegram.
/// </para>
/// <para>
/// Outbound sequence numbers are re-stamped from a single shared counter (opt-out via the
/// constructor) so the two co-located senders can never collide on a sequence the far end would
/// then dedupe away. Because a <c>HAIL</c>/<c>STAT</c> exchange does not correlate by sequence
/// (unlike a mode-coordination attempt tag), re-stamping is transparent here.
/// </para>
/// </remarks>
public sealed class FanOutTuningLink : ITuningLink
{
    private readonly ITuningLink inner;
    private readonly bool ownsInner;
    private readonly bool restampSequence;
    private readonly object gate = new();
    private readonly List<Channel<TuningTelegram>> subscribers = [];
    private readonly CancellationTokenSource pumpCts = new();
    private readonly Task pumpLoop;
    private int sequence;
    private int disposed;

    /// <summary>Wrap an inner link.</summary>
    /// <param name="inner">The underlying single-consumer link (e.g. an <see cref="SdmTuningLink"/>).</param>
    /// <param name="ownsInner">When <c>true</c>, disposing this link disposes the inner link too.
    /// Default <c>true</c>.</param>
    /// <param name="restampOutboundSequence">When <c>true</c> (the default), outbound telegrams'
    /// sequence numbers are replaced with a shared monotonic counter, so multiple co-located
    /// senders never collide.</param>
    public FanOutTuningLink(ITuningLink inner, bool ownsInner = true, bool restampOutboundSequence = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        this.inner = inner;
        this.ownsInner = ownsInner;
        restampSequence = restampOutboundSequence;
        pumpLoop = Task.Run(() => PumpAsync(pumpCts.Token));
    }

    /// <inheritdoc/>
    public Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(telegram);
        var outgoing = restampSequence
            ? telegram with { Sequence = Interlocked.Increment(ref sequence) }
            : telegram;
        return inner.SendAsync(outgoing, cancellationToken);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscription = Channel.CreateUnbounded<TuningTelegram>();
        lock (gate)
        {
            subscribers.Add(subscription);
        }
        try
        {
            await foreach (var telegram in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return telegram;
            }
        }
        finally
        {
            lock (gate)
            {
                subscribers.Remove(subscription);
            }
            subscription.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await pumpCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await pumpLoop.ConfigureAwait(false);
        }
        catch
        {
        }
        if (ownsInner)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        pumpCts.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var telegram in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (gate)
                {
                    foreach (var subscriber in subscribers)
                    {
                        subscriber.Writer.TryWrite(telegram);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lock (gate)
            {
                foreach (var subscriber in subscribers)
                {
                    subscriber.Writer.TryComplete();
                }
            }
        }
    }
}
