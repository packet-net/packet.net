using System.Runtime.CompilerServices;
using Packet.Tune.Core;

namespace Packet.Node.Core.Tuning;

/// <summary>
/// An <see cref="ITuningLink"/> pass-through decorator that taps every telegram flowing in either
/// direction and reports it to an observer, without altering the wire behaviour. This is how the
/// node turns the console-oriented <see cref="TuningSession"/> protocol loop into a structured
/// event stream: the loop drives an unmodified copy of the protocol over the inner link, and the
/// node decodes the same <c>HI</c>/<c>MS</c>/<c>AD</c> telegrams off the wire into
/// <see cref="Packet.Node.Core.Api.TuningEvent"/>s — no fork of the protocol logic.
/// </summary>
internal sealed class ObservingTuningLink : ITuningLink
{
    private readonly ITuningLink inner;
    private readonly Action<TuningTelegram, bool> observe;

    /// <summary>Wrap <paramref name="inner"/>, invoking <paramref name="observe"/> with each
    /// telegram and a <c>sent</c> flag (<c>true</c> for outbound, <c>false</c> for inbound). The
    /// observer must not throw; it runs on the send/receive path.</summary>
    public ObservingTuningLink(ITuningLink inner, Action<TuningTelegram, bool> observe)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(observe);
        this.inner = inner;
        this.observe = observe;
    }

    /// <inheritdoc/>
    public async Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default)
    {
        // Observe before the send so an outbound round is recorded even if delivery later fails
        // (the protocol treats delivery as best-effort and measures/continues anyway).
        observe(telegram, true);
        await inner.SendAsync(telegram, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<TuningTelegram> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var telegram in inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            observe(telegram, false);
            yield return telegram;
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
