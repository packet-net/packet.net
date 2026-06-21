namespace Packet.Ax25.Transport;

/// <summary>
/// Optional capability: a transport that can confirm a frame actually left the wire and
/// report how long that took. This is the de-KISS-named form of the G8BPQ ACKMODE need —
/// the AX.25 layer uses it to re-arm T1 on real TX-completion, the pacing layer uses it as a
/// back-pressure gate, and the adaptive layer reads <see cref="TxCompletion.Elapsed"/>. The
/// KISS-specific 16-bit sequence tag stays inside the KISS implementation; no consumer of this
/// contract reads it.
/// </summary>
/// <remarks>
/// Only transports that can genuinely observe TX-completion implement this (a KISS TNC that
/// echoes the ACKMODE tag, etc.). A transport that cannot — AXUDP, plain serial KISS — simply
/// does not implement it, and consumers feature-detect with
/// <c>transport is ITxCompletionTransport</c> and fall back to <see cref="IAx25Transport.SendAsync"/>.
/// </remarks>
public interface ITxCompletionTransport : IAx25Transport
{
    /// <summary>
    /// Send an AX.25 frame and await its actual transmit-completion.
    /// </summary>
    /// <returns>
    /// A <see cref="TxCompletion"/> timing the send. Note the distinct failure mode: if the
    /// frame is committed to the wire but the completion signal never arrives within
    /// <paramref name="timeout"/>, this throws <see cref="TimeoutException"/> (the frame IS on
    /// the air — the caller must treat it as sent, not retransmit blindly). This is different
    /// from the capability being absent, which the caller avoids by feature-detecting first.
    /// </returns>
    Task<TxCompletion> SendAwaitingCompletionAsync(
        ReadOnlyMemory<byte> ax25,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Timing of a confirmed transmit. <see cref="Completed"/> is the instant the frame finished
/// leaving the wire/air (transmit-completion), NOT queue-acceptance.
/// </summary>
public readonly record struct TxCompletion(DateTimeOffset Queued, DateTimeOffset Completed)
{
    /// <summary>Wall-clock from queue to confirmed transmit-completion.</summary>
    public TimeSpan Elapsed => Completed - Queued;
}
