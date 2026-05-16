namespace Packet.Kiss;

/// <summary>
/// What an ACKMODE-aware send returns: the 16-bit tag the host chose (or
/// the driver auto-assigned), the moment the frame was handed to the
/// wire, and the moment the TNC echoed back. The elapsed time is the
/// true TX-completion latency — meaningful for sizing T1 on slow modes
/// where queue-acceptance is not transmit-completion.
/// </summary>
/// <param name="SequenceTag">The 16-bit ACKMODE tag round-tripped by the TNC.</param>
/// <param name="Queued">When the host wrote the frame to the serial stream.</param>
/// <param name="Acknowledged">When the TNC's echo arrived back.</param>
public readonly record struct AckModeReceipt(ushort SequenceTag, DateTimeOffset Queued, DateTimeOffset Acknowledged)
{
    /// <summary>Wall-clock time between submission and TX-completion echo.</summary>
    public TimeSpan Elapsed => Acknowledged - Queued;
}
