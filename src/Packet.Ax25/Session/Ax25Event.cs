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
public sealed record DlDataRequest(ReadOnlyMemory<byte> Data) : Ax25Event("DL_DATA_request");

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

/// <summary>The session's I-frame queue surfaced its next ready frame.</summary>
public sealed record IFramePopsOffQueue() : Ax25Event("I_frame_pops_off_queue");

/// <summary>Any primitive the current state doesn't have a specific handler for.</summary>
public sealed record AllOtherPrimitives() : Ax25Event("all_other_primitives");

public sealed record ControlFieldError()        : Ax25Event("control_field_error");
public sealed record InfoNotPermittedInFrame()  : Ax25Event("info_not_permitted_in_frame");
public sealed record UOrSFrameLengthError()     : Ax25Event("u_or_s_frame_length_error");

// ─── Timer expiries ─────────────────────────────────────────────────────

public sealed record T1Expiry() : Ax25Event("T1_expiry");
public sealed record T2Expiry() : Ax25Event("T2_expiry");
public sealed record T3Expiry() : Ax25Event("T3_expiry");
