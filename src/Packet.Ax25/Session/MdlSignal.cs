namespace Packet.Ax25.Session;

/// <summary>
/// Base type for the signals the management data-link (MDL) state machine
/// raises <strong>upward</strong> to the Layer 3 entity — the
/// <c>MDL-NEGOTIATE Confirm</c> / <c>MDL-ERROR Indicate (X)</c> primitives of
/// AX.25 v2.2 §5.1 / Appendix C5.3. Emitted by <see cref="ActionDispatcher"/>
/// when the MDL machine's <c>signal_upper</c> verbs fire, and forwarded to the
/// MDL driver's consumer via the configured <c>sendMdl</c> callback.
/// </summary>
/// <remarks>
/// Distinct from <see cref="DataLinkSignal"/> (the data-link layer's upward
/// primitives). The MDL is a sibling state machine (figc5.1/figc5.2) handling
/// only XID parameter negotiation; its primitive set is much smaller — a single
/// "negotiation complete" confirm and a letter-coded error indicate. The MDL
/// pages are a prose-derived bootstrap (verification_pending; figc5.x not yet
/// redrawn) — see the management-data-link YAML headers in packet-net/ax25sdl.
/// </remarks>
public abstract record MdlSignal(string Name);

/// <summary>
/// <c>MDL-NEGOTIATE Confirm</c> (§5.1): notification/negotiation is complete.
/// Raised by figc5.2 on a successful XID-response exchange (the negotiated
/// parameters have been applied) and on the §6.3.2 ¶1 FRMR → version-2.0
/// fallback (a v2.0 connection is made).
/// </summary>
public sealed record MdlNegotiateConfirmSignal() : MdlSignal("MDL_NEGOTIATE_confirm");

/// <summary>
/// <c>MDL-ERROR Indicate (X)</c> (§5.1 / §C5.3): notification/negotiation has
/// failed. <see cref="Code"/> is the management error-code letter per the
/// §C5.3 table: <c>"B"</c> — unexpected XID response (received in Ready);
/// <c>"C"</c> — management retry limit exceeded (TM201/NM201 exhausted);
/// <c>"D"</c> — XID response without F=1. (Error <c>"A"</c> — XID command
/// without P=1 — is figc5.x reception-path detail not encoded by the prose
/// bootstrap.)
/// </summary>
public sealed record MdlErrorIndicateSignal(string Code) : MdlSignal("MDL_ERROR_indicate");
