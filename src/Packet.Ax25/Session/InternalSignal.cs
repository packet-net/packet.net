namespace Packet.Ax25.Session;

/// <summary>
/// Base type for signals the data-link layer raises **internally** to
/// neighbouring entities — the management data-link (MDL) and the
/// internal I-frame queue. Emitted by the dispatcher when verbs with
/// <c>kind: internal_out</c> fire.
/// </summary>
/// <remarks>
/// Distinct from <see cref="DataLinkSignal"/> (upward to Layer 3) and
/// <see cref="LinkMultiplexerSignal"/> (downward to the link multiplexer).
/// These signals stay inside the data-link state machine module — the
/// MDL is a sibling state machine handling XID negotiation, and the
/// I-frame queue is internal storage between the data-link and the
/// link multiplexer.
/// </remarks>
public abstract record InternalSignal(string Name);

/// <summary>
/// Trigger the management data-link to start XID parameter negotiation
/// with the peer. Used in figc4.6's awaiting-2.2-connection paths after
/// successful SABME handshake.
/// </summary>
public sealed record MdlNegotiateRequestSignal() : InternalSignal("MDL_NEGOTIATE_request");

/// <summary>
/// Push a payload onto the internal I-frame transmission queue. Used by
/// figc4.2/4.4 in the DL_DATA_request / I_frame_pops_off_queue paths
/// and the retransmission path. The payload comes from either the
/// triggering primitive or stored sent-frame state, depending on the verb.
/// </summary>
/// <param name="Payload">
/// The information field to send on the I-frame.
/// </param>
public sealed record PushIFrameQueueSignal(ReadOnlyMemory<byte> Payload) : InternalSignal("push_I_frame_queue");
