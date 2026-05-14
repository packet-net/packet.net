namespace Packet.Ax25.Session;

/// <summary>
/// Base type for signals the data-link layer raises to the **link
/// multiplexer** below it. Emitted by the dispatcher when the
/// <c>LM_*_request</c> signal_lower verbs fire (§4.4 / figc4.4).
/// </summary>
/// <remarks>
/// The link multiplexer is the medium-access arbiter — it owns the radio
/// and serialises frame transmissions across multiple data-link sessions
/// on the same port. Production code wires this callback to the actual
/// multiplexer; tests can capture the signals into a list to assert
/// against.
/// </remarks>
public abstract record LinkMultiplexerSignal(string Name);

/// <summary>
/// Request that the link multiplexer seize the medium for us — once
/// granted, the multiplexer raises <see cref="LmSeizeConfirm"/>.
/// </summary>
public sealed record LinkMultiplexerSeizeRequest() : LinkMultiplexerSignal("LM_SEIZE_request");

/// <summary>
/// Inform the link multiplexer we are done with the medium for now.
/// </summary>
public sealed record LinkMultiplexerReleaseRequest() : LinkMultiplexerSignal("LM_RELEASE_request");

/// <summary>
/// Hand the link multiplexer one or more I-frames to transmit. The
/// figc4.4 paths use this in retransmission flows after
/// <c>push_old_I_frame_N_r_on_queue</c> repopulates the outbound queue.
/// </summary>
public sealed record LinkMultiplexerDataRequest() : LinkMultiplexerSignal("LM_DATA_request");
