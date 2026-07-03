namespace Packet.Radio;

/// <summary>
/// A small-datagram messaging channel provided by the <em>radio itself</em>, riding the
/// radio's own internal signalling modem rather than the audio-path modem/TNC (the canonical
/// implementation is Tait CCDI Short Data Messages over the radios' built-in FFSK modem —
/// see <c>TaitSdmSideChannel</c> in <c>Packet.Radio.Tait</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this seam exists.</b> Because the side channel bypasses the TNC entirely, it is
/// <b>modem-mode- and deviation-agnostic</b>: it keeps working while the stations at both ends
/// reconfigure (or misconfigure) the very link the audio modems carry. That makes it the
/// coordination channel for exactly the operations that would otherwise be
/// chicken-and-egg over the main link — renegotiating TNC modes (Phase 10 mode agility),
/// remote deviation tuning, and any future switch-then-verify manoeuvre. It is small and slow
/// (tens of characters per datagram, seconds per exchange) by design; it is a control plane,
/// never a data plane.
/// </para>
/// <para>
/// <b>Capability-gating intent.</b> Not every radio offers one, and offering the API is not
/// the same as the feature being enabled in the radio's programming (Tait radios must have
/// SDMs + auto-acknowledgements enabled, e.g.). Hosts are expected to <em>gate</em>
/// side-channel-coordinated features on a live probe — drivers advertise the machinery via
/// <see cref="RadioCapabilities.SideChannel"/>, and a doctor-style probe (send-to-self or a
/// known-peer exchange, cf. <c>TuningDoctor</c>'s "SDM accepted" probe) confirms it end-to-end
/// before anything (mode negotiation, tuning sessions) is offered on that port.
/// </para>
/// <para>
/// <b>Delivery model.</b> <see cref="SendAsync"/> completes when the radio accepts the datagram
/// for transmission; actual over-air delivery is confirmed (or denied) asynchronously via
/// <see cref="DeliveryReceipt"/> when the transport supports receipts. Receive is
/// arrival-signalled but pull-based (<see cref="ReadBufferedAsync"/>) because real hardware
/// buffers are typically one-deep with overwrite-on-arrival — consumers must read promptly on
/// <see cref="DatagramArrived"/> and tolerate spurious arrival events.
/// </para>
/// </remarks>
public interface IRadioSideChannel
{
    /// <summary>
    /// The largest payload one datagram can carry, in characters (e.g. 32 for a plain Tait
    /// SDM). Protocols riding the channel must fit their wire form inside this budget —
    /// <see cref="SendAsync"/> throws on oversize payloads.
    /// </summary>
    int MaxPayloadLength { get; }

    /// <summary>Whether the radio currently reports RF on channel (hardware carrier sense) —
    /// senders should hold off while busy. <c>null</c> = unknown (no carrier-sense source,
    /// e.g. the radio's unsolicited reporting is not enabled).</summary>
    bool? ChannelBusy { get; }

    /// <summary>Something datagram-shaped arrived at the radio — read
    /// <see cref="ReadBufferedAsync"/> promptly (hardware buffers are typically one-deep and
    /// overwritten by the next arrival). May fire spuriously (e.g. on mere carrier rise);
    /// readers must tolerate an empty buffer.</summary>
    event EventHandler? DatagramArrived;

    /// <summary>An over-air delivery confirmation for a previously sent datagram:
    /// <c>true</c> = the destination radio acknowledged receipt, <c>false</c> = no
    /// acknowledgement within the radio's configured wait (which a wrong or absent
    /// destination also produces).</summary>
    event EventHandler<bool>? DeliveryReceipt;

    /// <summary>Transmit one datagram to <paramref name="destinationId"/> (the peer radio's
    /// side-channel identity, in the implementation's address format — e.g. the 8-character
    /// Tait SDM data identity). Completes when the radio accepts the send; listen on
    /// <see cref="DeliveryReceipt"/> for the over-air outcome.</summary>
    /// <exception cref="ArgumentException">The destination identity is malformed or the
    /// payload exceeds <see cref="MaxPayloadLength"/>.</exception>
    Task SendAsync(string destinationId, string payload, CancellationToken cancellationToken = default);

    /// <summary>Read and clear the radio's buffered received datagram (<c>null</c> when
    /// nothing is buffered).</summary>
    Task<string?> ReadBufferedAsync(CancellationToken cancellationToken = default);
}
