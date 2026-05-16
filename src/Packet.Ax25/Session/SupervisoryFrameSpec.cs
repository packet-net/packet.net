using Packet.Core;

namespace Packet.Ax25.Session;

/// <summary>
/// Identifies one of the four AX.25 supervisory (S) frame subtypes per §4.3.2.
/// </summary>
public enum SupervisoryFrameType
{
    /// <summary>Receive Ready — RR.</summary>
    Rr,
    /// <summary>Receive Not Ready — RNR.</summary>
    Rnr,
    /// <summary>Reject — REJ.</summary>
    Rej,
    /// <summary>Selective Reject — SREJ.</summary>
    Srej,
}

/// <summary>
/// A request to send a supervisory frame. The dispatcher emits these in
/// response to figure-canonical signal_lower verbs (<c>RR_command</c>,
/// <c>RR</c> for the bare response form, <c>RNR_response</c>,
/// <c>REJ</c>, <c>SREJ</c>); the session translates them into
/// <see cref="Ax25Frame"/>s and ships them on the wire.
/// </summary>
/// <param name="Type">RR / RNR / REJ / SREJ.</param>
/// <param name="IsCommand">
/// <c>true</c> for a command frame (P bit), <c>false</c> for a
/// response frame (F bit). The action verb selects this: verbs whose
/// name ends in <c>_command</c> are commands; bare verbs and verbs
/// ending in <c>_response</c> are responses. Spec §4.3.3 ¶1.
/// </param>
/// <param name="Nr">
/// N(R) value to set on the outgoing frame's control field, mod-8 or
/// mod-128 depending on session state. Populated from
/// <see cref="PendingFrame.Nr"/> by the dispatcher when the signal_lower
/// verb fires; the chain's preceding processing verb (typically
/// <c>N(r) := V(r)</c>) must have set it.
/// </param>
/// <param name="PfBit">
/// P/F bit to set in the control field. For commands this is the P
/// bit; for responses it's the F bit. Populated from
/// <see cref="PendingFrame.PfBit"/> by the dispatcher; the chain's
/// preceding processing verb (typically <c>F := 1</c>, <c>F := P</c>,
/// or <c>p := 0</c>) must have set it.
/// </param>
/// <param name="Path">
/// Optional digipeater chain override for this specific outgoing frame.
/// See <see cref="UFrameSpec.Path"/> for the rationale. <c>null</c>
/// means "use the session context's chain"; non-null means "use this
/// list" (typically the reversed inbound chain when responding to a
/// digipeated trigger).
/// </param>
public readonly record struct SupervisoryFrameSpec(
    SupervisoryFrameType Type,
    bool IsCommand,
    byte Nr,
    bool PfBit,
    IReadOnlyList<Callsign>? Path = null);
