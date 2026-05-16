using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// A request to send a UI (Unnumbered Information) frame. The dispatcher
/// emits these in response to the figure-canonical <c>UI_command</c>
/// signal_lower verb (figc4.1 / 4.2 / 4.3 / 4.4 / 4.6 — every state's
/// DL_UNIT_DATA_request column).
/// </summary>
/// <param name="IsCommand">
/// <c>true</c> for a command frame, <c>false</c> for a response. The
/// figure-canonical verb spelling <c>UI_command</c> is always command;
/// future <c>UI_response</c> would map to <c>false</c>.
/// </param>
/// <param name="PfBit">
/// P/F bit value. Read from <see cref="PendingFrame.PfBit"/>, defaulting
/// to <c>false</c>. UI frames sometimes carry P=1 to elicit a response
/// (§4.3.3.6).
/// </param>
/// <param name="Info">
/// Information field payload. Comes from the triggering
/// <see cref="DlUnitDataRequest.Data"/> primitive.
/// </param>
/// <param name="Pid">
/// Protocol Identifier byte (§3.4). Comes from the triggering
/// <see cref="DlUnitDataRequest.Pid"/>; defaults to <c>0xF0</c>
/// (no Layer-3 protocol) per the primitive's default.
/// </param>
/// <param name="Path">
/// Optional digipeater chain override. See <see cref="UFrameSpec.Path"/>.
/// UI is typically broadcast-shaped (no reply path), but this is kept
/// for symmetry with the other frame specs and to let callers shape
/// UI-via-digi when desired.
/// </param>
public readonly record struct UiFrameSpec(
    bool IsCommand,
    bool PfBit,
    ReadOnlyMemory<byte> Info,
    byte Pid,
    IReadOnlyList<Callsign>? Path = null);
