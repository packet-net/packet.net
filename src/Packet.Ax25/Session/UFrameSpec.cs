using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// Identifies one of the eight AX.25 unnumbered (U) frame subtypes per
/// §4.3.3 / §4.3.4.
/// </summary>
public enum UFrameType
{
    /// <summary>Set Asynchronous Balanced Mode — establishes a mod-8 connection.</summary>
    Sabm,
    /// <summary>Set Asynchronous Balanced Mode Extended — establishes a mod-128 connection.</summary>
    Sabme,
    /// <summary>Disconnect — initiates link teardown.</summary>
    Disc,
    /// <summary>Unnumbered Acknowledge — confirms SABM(E) / DISC.</summary>
    Ua,
    /// <summary>Disconnected Mode — peer is not in a connection.</summary>
    Dm,
    /// <summary>Frame Reject — protocol violation reported.</summary>
    Frmr,
    /// <summary>Exchange Identification — parameter negotiation per §4.3.4.</summary>
    Xid,
    /// <summary>Test — diagnostic round-trip per §4.3.4.</summary>
    Test,
}

/// <summary>
/// A request to send an unnumbered (U) frame. The dispatcher emits these
/// in response to figure-canonical signal_lower verbs (<c>UA</c>,
/// <c>DM</c>, <c>SABM (P == 1)</c>, <c>SABME (P = 1)</c>,
/// <c>DISC (P = 1)</c>, <c>Expedited UA</c>, <c>Expedited DM</c>, and
/// the <c>DM (F = 1)</c> normalisation cluster); the session translates
/// each into an <see cref="Ax25Frame"/> and ships it on the wire.
/// </summary>
/// <param name="Type">U-frame subtype.</param>
/// <param name="IsCommand">
/// <c>true</c> for a command frame (P bit); <c>false</c> for a response
/// (F bit). Determined by the verb name — SABM(E) / DISC are always
/// commands; UA / DM are always responses; FRMR is a response; XID and
/// TEST appear in both roles. Spec §4.3.3 / §4.3.4.
/// </param>
/// <param name="PfBit">
/// P/F bit value to set in the control field. Determined by the verb:
/// verbs whose name carries an explicit <c>(P = 1)</c> / <c>(P == 1)</c>
/// / <c>(F = 1)</c> qualifier force <c>true</c>; bare verbs (<c>UA</c>,
/// <c>DM</c>, <c>Expedited UA</c>, <c>Expedited DM</c>) read
/// <see cref="PendingFrame.PfBit"/>, defaulting to <c>false</c>.
/// </param>
/// <param name="IsExpedited">
/// <c>true</c> for the <c>Expedited UA</c> / <c>Expedited DM</c> verbs
/// drawn in figc4.3. Hints to the wire-translation layer that this frame
/// should bypass any pending I-frame queue and go out at the next
/// opportunity. False for the normal-priority forms.
/// </param>
/// <param name="Path">
/// Optional digipeater chain override for this specific outgoing frame.
/// When non-null, the wire-translation layer uses this list verbatim as
/// the via-chain instead of the session context's
/// <see cref="Ax25SessionContext.Digipeaters"/>. Populated by the
/// dispatcher when the triggering frame arrived via a digipeater path —
/// the response carries the source-to-destination path reversed so the
/// peer's digipeaters route the reply back along the same chain. See
/// AX.25 v2.2 §C.2 (Path Construction). <c>null</c> on triggers without
/// an inbound frame (upper-layer DL requests, timer expiries) — in
/// which case the context's chain is used.
/// </param>
public readonly record struct UFrameSpec(
    UFrameType Type,
    bool IsCommand,
    bool PfBit,
    bool IsExpedited = false,
    IReadOnlyList<Callsign>? Path = null);
