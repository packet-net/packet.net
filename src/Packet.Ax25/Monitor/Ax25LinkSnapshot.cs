using Packet.Core;

namespace Packet.Ax25.Monitor;

/// <summary>
/// One link as the observer currently understands it, with the most recent frames on it.
/// </summary>
/// <param name="Id">
/// Stable identity: the port and the two callsigns, the callsigns in ordinal order so the id is
/// the same whichever station spoke first. The form is <c>port|CALL1&lt;&gt;CALL2</c>.
/// </param>
/// <param name="Port">The port the link was observed on.</param>
/// <param name="A">The station heard first on this link.</param>
/// <param name="B">The other one.</param>
/// <param name="State">Where the link stands.</param>
/// <param name="Inferred">
/// True when the link was judged <see cref="Ax25LinkState.Connected"/> from numbered traffic
/// alone, no call having been heard: the observer joined it in progress.
/// </param>
/// <param name="Modulo">8, or 128 after a SABME.</param>
/// <param name="FirstSeen">When the first frame on this link was observed.</param>
/// <param name="LastSeen">When the latest one was.</param>
/// <param name="AtoB">What <see cref="A"/> has sent to <see cref="B"/>.</param>
/// <param name="BtoA">What <see cref="B"/> has sent to <see cref="A"/>.</param>
/// <param name="Concern">
/// The one thing wrong with the link right now, in a phrase, or null when nothing is: "3 calls
/// unanswered", "2 polls unanswered", "GB7RDG-2 is busy". Derived from the sides' current
/// counters, so it clears itself when the link recovers.
/// </param>
/// <param name="Recent">The latest frames, oldest first, up to the observer's per-link cap.</param>
public sealed record Ax25LinkSnapshot(
    string Id,
    string Port,
    Callsign A,
    Callsign B,
    Ax25LinkState State,
    bool Inferred,
    int Modulo,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    Ax25LinkSideStats AtoB,
    Ax25LinkSideStats BtoA,
    string? Concern,
    IReadOnlyList<Ax25LinkEvent> Recent);

/// <summary>
/// What one station has sent the other on a link.
/// </summary>
/// <param name="Frames">Every frame from this side, digipeated copies excluded.</param>
/// <param name="DataFrames">I frames sent for the first time.</param>
/// <param name="DataBytes">Information bytes in those, resends not counted twice.</param>
/// <param name="Resends">I frames sent again.</param>
/// <param name="Polls">RR/RNR commands with P set: "what have you received?".</param>
/// <param name="PollsUnanswered">Polls in a row from this side with no final response heard
/// from the other; 0 once one is.</param>
/// <param name="Rejects">REJ and SREJ frames sent by this side.</param>
/// <param name="CallsUnanswered">SABM/SABME (or DISC) frames in a row from this side that
/// nothing has answered; 0 once the link is up or down.</param>
/// <param name="Busy">True while this side's last supervisory frame was an RNR.</param>
/// <param name="AwaitingAck">
/// I frames this side has sent that the other has not been heard to acknowledge: the gap
/// between the other side's last N(R) and this side's next N(S). Null when either is unknown.
/// </param>
public sealed record Ax25LinkSideStats(
    int Frames,
    int DataFrames,
    long DataBytes,
    int Resends,
    int Polls,
    int PollsUnanswered,
    int Rejects,
    int CallsUnanswered,
    bool Busy,
    int? AwaitingAck);
