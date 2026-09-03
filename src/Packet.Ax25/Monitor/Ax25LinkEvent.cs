using Packet.Core;

namespace Packet.Ax25.Monitor;

/// <summary>
/// One frame, read in the context of its link: who sent it, what it was, what it meant, and what
/// the link looked like once it had gone by. Or, once in a while, no frame at all: the observer
/// giving up on a call that nothing answered (<see cref="Ax25LinkFlags.Timeout"/>) is an event on
/// the link with no frame behind it, and reads with <see cref="FrameType"/> null.
/// </summary>
/// <param name="LinkId">The link it belongs to; see <see cref="Ax25LinkSnapshot.Id"/>.</param>
/// <param name="Port">The port the caller observed it on.</param>
/// <param name="At">When the caller observed it; for a timeout, when the wait ran out.</param>
/// <param name="Transmitted">True when the caller sent it rather than heard it.</param>
/// <param name="From">The sending station; for a timeout, the one that was waiting.</param>
/// <param name="To">The station it was addressed to.</param>
/// <param name="Via">
/// The digipeater path in order, each entry the callsign with <c>*</c> appended once that
/// digipeater has repeated the frame (the H bit), which is how monitors have always written
/// it. Empty for a direct frame.
/// </param>
/// <param name="FrameType">Its wire type; null for a timeout, which is not a frame.</param>
/// <param name="IsCommand">Command (true) or response (false), per the address C bits.</param>
/// <param name="PollFinal">The P/F bit.</param>
/// <param name="Ns">N(S) on an I frame; null on anything else.</param>
/// <param name="Nr">N(R) on an I or supervisory frame; null on anything else.</param>
/// <param name="Pid">The protocol identifier on an I or UI frame; null on anything else.</param>
/// <param name="InfoLength">Length of the information field, 0 when it has none.</param>
/// <param name="Text">
/// The information field as text, when it is text: a no-layer-3 payload made of printable
/// characters. Null for binary, for other protocols and for frames with no information field.
/// </param>
/// <param name="Narration">
/// What the frame did, in a sentence fragment that follows the sender's callsign: "calls
/// M0LTE-9", "acknowledges #0-#2", "asks GB7RDG-2 to resend from #3". Never empty.
/// </param>
/// <param name="Flags">What to draw attention to.</param>
/// <param name="Count">
/// With <see cref="Ax25LinkFlags.Repeat"/>, how many times in a row this has now been sent
/// unanswered (2 on the first repeat). Null otherwise.
/// </param>
/// <param name="State">The link's state after this frame.</param>
public sealed record Ax25LinkEvent(
    string LinkId,
    string Port,
    DateTimeOffset At,
    bool Transmitted,
    Callsign From,
    Callsign To,
    IReadOnlyList<string> Via,
    Ax25FrameType? FrameType,
    bool IsCommand,
    bool PollFinal,
    int? Ns,
    int? Nr,
    byte? Pid,
    int InfoLength,
    string? Text,
    string Narration,
    Ax25LinkFlags Flags,
    int? Count,
    Ax25LinkState State);
