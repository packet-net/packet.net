namespace Packet.Tune.Core;

/// <summary>
/// A reliable-ish ordered telegram channel between the two ends of a tuning
/// session. Implementations retry inside <see cref="SendAsync"/> (so a
/// completed send means "very probably delivered") and dedupe repeated
/// sequence numbers on receive (so a transport retry never surfaces twice).
/// Two flavours ship: <see cref="SdmTuningLink"/> (radio-to-radio Tait SDMs,
/// no internet) and <see cref="WebSocketTuningLink"/> (internet, via a
/// PIN-rendezvous relay).
/// </summary>
public interface ITuningLink : IAsyncDisposable
{
    /// <summary>
    /// Send one telegram, retrying inside as the transport allows.
    /// </summary>
    /// <exception cref="TuningLinkException">The telegram could not be
    /// delivered after the transport's retries were exhausted.</exception>
    Task SendAsync(TuningTelegram telegram, CancellationToken cancellationToken = default);

    /// <summary>
    /// The stream of inbound telegrams, already deduplicated by sequence
    /// number. Completes when the link closes.
    /// </summary>
    IAsyncEnumerable<TuningTelegram> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>A tuning-link transport failure (delivery retries exhausted,
/// relay gone, socket closed mid-session…).</summary>
public sealed class TuningLinkException : Exception
{
    /// <summary>Create with a message describing the transport failure.</summary>
    public TuningLinkException(string message)
        : base(message)
    {
    }

    /// <summary>Create with a message and the underlying transport exception.</summary>
    public TuningLinkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Parameterless form (framework convention).</summary>
    public TuningLinkException()
    {
    }
}
