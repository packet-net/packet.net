namespace Packet.Node.Core.Api;

/// <summary>
/// The read model returned by <c>POST /api/v1/ports/{id}/tuning/session</c> when a guided
/// deviation-tuning session is armed on a port. A session is a two-ended, operator-initiated,
/// transmitting procedure coordinated over the radios' SDM side channel: this port is one end
/// (the <c>tuned</c> end where an operator turns the TX-DEV pot, or a <c>meter</c> that measures a
/// remote peer) and a peer radio is the other. Only one session may be active per port at a time.
/// System.Text.Json's web defaults camel-case the properties.
/// </summary>
/// <param name="SessionId">Opaque id for this session (unique per port-session).</param>
/// <param name="PortId">The port the session runs on.</param>
/// <param name="Role">This port's role: <c>tuned</c> (transmits bursts; operator turns the pot here)
/// or <c>meter</c> (measures a remote peer's bursts and computes the advice).</param>
/// <param name="PeerSdmId">The peer radio's 8-character SDM data identity the coordination link
/// addresses.</param>
/// <param name="State">The lifecycle state at the time of the response: <c>armed</c>,
/// <c>peer-connected</c>, <c>awaiting-adjustment</c>, <c>ended</c>, <c>error</c> or
/// <c>stopped</c>.</param>
/// <param name="BurstFrames">Frames requested per measurement burst.</param>
/// <param name="StartedAt">When the session was armed (UTC).</param>
public sealed record TuningSessionInfo(
    string SessionId,
    string PortId,
    string Role,
    string PeerSdmId,
    string State,
    int BurstFrames,
    DateTimeOffset StartedAt);

/// <summary>
/// One event on a tuning session's live feed (<c>GET /api/v1/ports/{id}/tuning/events</c>, SSE).
/// A single record carries both the per-round measurements and the session-lifecycle transitions,
/// discriminated by <see cref="Kind"/>. Web defaults camel-case the properties; unused fields for
/// a given kind serialise as <c>null</c> and are omitted-by-convention on the reader side.
/// </summary>
/// <param name="Kind">The event kind: <c>armed</c> · <c>peer-connected</c> · <c>round</c> ·
/// <c>awaiting-adjustment</c> · <c>ended</c> · <c>error</c>.</param>
/// <param name="At">When the event was raised (UTC).</param>
/// <param name="State">The session's lifecycle state after this event.</param>
/// <param name="BurstIndex">1-based measurement-round number (<c>round</c> events only).</param>
/// <param name="Decoded">Frames the meter decoded this round (<c>round</c> events only).</param>
/// <param name="Total">Frames requested this round (<c>round</c> events only).</param>
/// <param name="LevelDb">RX-audio level in dB from the meter's GETRSSI fast path, or <c>null</c>
/// when the firmware has no level meter (<c>round</c> events only).</param>
/// <param name="RssiDbm">Median CCDI RSSI during the burst in dBm, or <c>null</c> when no radio RSSI
/// source is available (<c>round</c> events only).</param>
/// <param name="Advice">The pot advice for the operator: <c>up</c> · <c>down</c> · <c>ok</c> ·
/// <c>sweep</c> (<c>round</c> events only; <c>null</c> for an unknown wire token).</param>
/// <param name="Note">A one-line operator-facing note (what to do with the pot, plus any level
/// trend), for <c>round</c> events.</param>
/// <param name="Error">A human-readable failure detail, for <c>error</c> events.</param>
/// <param name="TxDelayMs">The commanded TXDELAY of a TXDELAY-minimisation step, in ms
/// (<c>round</c> events of a txdelay session only). Additive (trailing optional): deviation-session
/// events omit it.</param>
/// <param name="PreDataCarrierMs">The meter's median measured carrier-rise→data lead for the step,
/// in ms — the as-heard effective TXDELAY cross-check (<c>round</c> events of a txdelay session,
/// when the meter has a carrier-sensing radio).</param>
/// <param name="RecommendedTxDelayMs">The sweep's recommendation, in ms (the final <c>ended</c>
/// event of a completed txdelay sweep only).</param>
public sealed record TuningEvent(
    string Kind,
    DateTimeOffset At,
    string State,
    int? BurstIndex = null,
    int? Decoded = null,
    int? Total = null,
    double? LevelDb = null,
    double? RssiDbm = null,
    string? Advice = null,
    string? Note = null,
    string? Error = null,
    int? TxDelayMs = null,
    double? PreDataCarrierMs = null,
    int? RecommendedTxDelayMs = null);
