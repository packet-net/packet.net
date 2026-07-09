namespace Packet.Tune.Core;

/// <summary>
/// An ordered telegram channel between the two ends of a tuning session, with
/// per-sender sequence numbers the receiver dedupes on (a transport retry never
/// surfaces twice; each session starts its counter from a random base — #590 — so a
/// re-run against a still-running peer is not mistaken for the prior session's traffic).
/// Two flavours ship: <see cref="SdmTuningLink"/> (radio-to-radio Tait SDMs, no internet)
/// and <see cref="WebSocketTuningLink"/> (internet, via a PIN-rendezvous relay).
/// <para><b>Delivery model differs by transport.</b> The WebSocket relay is a reliable
/// stream, but <see cref="SdmTuningLink"/> is <em>receipt-tolerant</em>: the Tait SDM
/// over-air delivery receipt is unreliable for close bidirectional traffic (the auto-ack
/// refractory — see <see cref="SdmTuningLink"/>), so a completed send means "the transport
/// accepted the datagram", and end-to-end reliability is the caller's application-level
/// reply (send-until-expected-reply, e.g. propose→confirm / step→report).</para>
/// </summary>
public interface ITuningLink : IAsyncDisposable
{
    /// <summary>
    /// Send one telegram. Returns once the transport has accepted it for delivery — for
    /// <see cref="SdmTuningLink"/>, once the radio accepts the datagram (the over-air receipt
    /// is advisory and not awaited by default); the caller confirms delivery via its own reply.
    /// </summary>
    /// <exception cref="TuningLinkException">The transport could not accept the telegram
    /// (radio command-rejects exhausted / relay gone / socket closed mid-session).</exception>
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
