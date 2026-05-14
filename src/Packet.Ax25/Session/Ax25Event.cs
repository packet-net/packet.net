namespace Packet.Ax25.Session;

/// <summary>
/// Base type for all events the AX.25 state-machine orchestrator dispatches
/// on. <see cref="Name"/> is the event identifier used to match against the
/// codegen's transition tables (<c>on:</c> field in <c>*.sdl.yaml</c>).
/// </summary>
/// <remarks>
/// The event vocabulary is bounded by <c>/spec-sdl/events.yaml</c>. Every
/// event referenced by a transcription must have a corresponding subtype
/// here, otherwise the dispatcher won't be able to deliver it.
/// </remarks>
public abstract record Ax25Event(string Name);

// ─── Upper-layer (Layer-3 → Data-Link) primitives ──────────────────────

/// <summary>Layer 3 requests link establishment.</summary>
public sealed record DlConnectRequest() : Ax25Event("DL_CONNECT_request");

/// <summary>Layer 3 requests link teardown.</summary>
public sealed record DlDisconnectRequest() : Ax25Event("DL_DISCONNECT_request");

/// <summary>Layer 3 hands an I-frame payload to the data-link.</summary>
public sealed record DlDataRequest(ReadOnlyMemory<byte> Data, byte Pid = Ax25Frame.PidNoLayer3)
    : Ax25Event("DL_DATA_request");

/// <summary>Layer 3 hands a UI-frame payload to the data-link.</summary>
public sealed record DlUnitDataRequest(ReadOnlyMemory<byte> Data, byte Pid = Ax25Frame.PidNoLayer3)
    : Ax25Event("DL_UNIT_DATA_request");

/// <summary>Layer 3 asks the data-link to suspend incoming flow (set busy).</summary>
public sealed record DlFlowOffRequest() : Ax25Event("DL_FLOW_OFF_request");

/// <summary>Layer 3 asks the data-link to resume incoming flow (clear busy).</summary>
public sealed record DlFlowOnRequest() : Ax25Event("DL_FLOW_ON_request");

// ─── Management Data-Link primitives ────────────────────────────────────

/// <summary>MDL requests XID parameter negotiation with the peer.</summary>
public sealed record MdlNegotiateRequest() : Ax25Event("MDL_NEGOTIATE_request");

/// <summary>MDL confirms XID parameter negotiation completed.</summary>
public sealed record MdlNegotiateConfirm() : Ax25Event("MDL_NEGOTIATE_confirm");

/// <summary>MDL signals an error condition.</summary>
public sealed record MdlErrorIndicate(string Code) : Ax25Event("MDL_ERROR_indicate");

// ─── Frame-received events (from the link multiplexer) ─────────────────

public sealed record IFrameReceived(Ax25Frame Frame)     : Ax25Event("I_received");
public sealed record RrReceived(Ax25Frame Frame)         : Ax25Event("RR_received");
public sealed record RnrReceived(Ax25Frame Frame)        : Ax25Event("RNR_received");
public sealed record RejReceived(Ax25Frame Frame)        : Ax25Event("REJ_received");
public sealed record SrejReceived(Ax25Frame Frame)       : Ax25Event("SREJ_received");
public sealed record UiReceived(Ax25Frame Frame)         : Ax25Event("UI_received");
public sealed record SabmReceived(Ax25Frame Frame)       : Ax25Event("SABM_received");
public sealed record SabmeReceived(Ax25Frame Frame)      : Ax25Event("SABME_received");
public sealed record DiscReceived(Ax25Frame Frame)       : Ax25Event("DISC_received");
public sealed record UaReceived(Ax25Frame Frame)         : Ax25Event("UA_received");
public sealed record DmReceived(Ax25Frame Frame)         : Ax25Event("DM_received");
public sealed record FrmrReceived(Ax25Frame Frame)       : Ax25Event("FRMR_received");
public sealed record XidReceived(Ax25Frame Frame)        : Ax25Event("XID_received");
public sealed record TestReceived(Ax25Frame Frame)       : Ax25Event("TEST_received");

// ─── Internal events ────────────────────────────────────────────────────

/// <summary>
/// The session's I-frame queue surfaced its next ready frame for
/// transmission. Synthesised by <see cref="Ax25Session"/> after a
/// transition's action chain enqueued data and conditions allow
/// transmission (peer not busy, V(s) within window).
/// </summary>
public sealed record IFramePopsOffQueue(ReadOnlyMemory<byte> Data, byte Pid = Ax25Frame.PidNoLayer3)
    : Ax25Event("I_frame_pops_off_queue");

/// <summary>
/// Composite frame-received event covering the single SDL input column
/// "I, RR, RNR, REJ or SREJ Commands" drawn on figc4.3 Awaiting Release.
/// The figure groups five distinct frame types into one input column; the
/// orchestrator fans them in here to preserve the figure's structure.
/// </summary>
public sealed record IOrSCommandReceived(Ax25Frame Frame) : Ax25Event("i_or_s_command_received");

// ─── Catch-all events ───────────────────────────────────────────────────
// Source-class suffixed to disambiguate identical English labels appearing
// under two different figc1.1 shape classes in the same figure (see
// figc4.1's Disconnected state). The suffix names the `d5` description
// the corresponding box was drawn with.

/// <summary>Catch-all for unhandled boxes drawn with the "Signal reception from Lower Layer" shape.</summary>
public sealed record AllOtherPrimitivesFromLowerLayer() : Ax25Event("all_other_primitives__from_lower_layer");

/// <summary>Catch-all for unhandled boxes drawn with the "Signal reception from upper layer" shape.</summary>
public sealed record AllOtherPrimitivesFromUpperLayer() : Ax25Event("all_other_primitives__from_upper_layer");

/// <summary>Catch-all for command frames not explicitly handled by a state's transition table.</summary>
public sealed record AllOtherCommands() : Ax25Event("all_other_commands");

public sealed record ControlFieldError()        : Ax25Event("control_field_error");
public sealed record InfoNotPermittedInFrame()  : Ax25Event("info_not_permitted_in_frame");
public sealed record UOrSFrameLengthError()     : Ax25Event("u_or_s_frame_length_error");

// ─── Link-multiplexer events ────────────────────────────────────────────

/// <summary>The link multiplexer has confirmed our SEIZE request — we own the medium.</summary>
public sealed record LmSeizeConfirm() : Ax25Event("LM_SEIZE_confirm");

// ─── Timer expiries ─────────────────────────────────────────────────────

public sealed record T1Expiry() : Ax25Event("T1_expiry");
public sealed record T2Expiry() : Ax25Event("T2_expiry");
public sealed record T3Expiry() : Ax25Event("T3_expiry");
