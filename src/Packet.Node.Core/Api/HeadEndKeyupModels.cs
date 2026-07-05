namespace Packet.Node.Core.Api;

/// <summary>
/// The result of a keyup-pairing run (<c>POST /api/v1/radios/headends/{instanceId}/pair-by-keyup</c>):
/// the physical modem↔radio map discovered by <b>briefly keying each free NinoTNC's transmitter</b>
/// and watching which co-located Tait reports its PTT line asserting. This is <em>ground truth</em> for
/// the co-location pairing — it replaces the scan's free-device guess (and verifies the unambiguous
/// case). It is an <b>operator-initiated RF action</b>: running it emits on-air. Never part of the
/// passive <c>GET /radios/headends</c> scan.
/// </summary>
/// <param name="InstanceId">The head-end instance this ran against.</param>
/// <param name="Reachable">True when the instance was found + scanned; false leaves the lists empty
/// and sets <see cref="Error"/> (unknown instance, unreachable, or a duplicate-id conflict).</param>
/// <param name="Error">The failure reason when <see cref="Reachable"/> is false; null otherwise.</param>
/// <param name="Pairs">The resolved physical pairs — each free NinoTNC whose keyup fired exactly one
/// Tait's PTT. This is the map to adopt.</param>
/// <param name="UnpairedTncs">Free NinoTNCs whose keyup fired no Tait PTT (radio powered off, PROGRESS
/// disabled, or not actually cabled to a scanned Tait) — could not be resolved.</param>
/// <param name="UnpairedRadios">Free Taits that no NinoTNC keyup fired — left unresolved.</param>
/// <param name="Ambiguous">Free NinoTNCs whose keyup fired more than one Tait's PTT (should not happen
/// physically — flagged for the operator rather than guessed).</param>
/// <param name="Caveat">The RF caveat describing what this action did on-air (<see cref="HeadEndKeyupCaveat.Text"/>).</param>
public sealed record HeadEndKeyupResult(
    string InstanceId,
    bool Reachable,
    string? Error,
    IReadOnlyList<HeadEndKeyupPair> Pairs,
    IReadOnlyList<string> UnpairedTncs,
    IReadOnlyList<string> UnpairedRadios,
    IReadOnlyList<HeadEndKeyupAmbiguity> Ambiguous,
    string Caveat);

/// <summary>A physically-verified modem↔radio pair: keying <see cref="TncDeviceId"/> asserted the PTT
/// line of exactly <see cref="RadioDeviceId"/> — they are cabled together on the head-end.</summary>
public sealed record HeadEndKeyupPair(string TncDeviceId, string RadioDeviceId);

/// <summary>A NinoTNC whose keyup fired more than one Tait's PTT — not physically expected; surfaced
/// for a manual decision instead of being guessed.</summary>
public sealed record HeadEndKeyupAmbiguity(string TncDeviceId, IReadOnlyList<string> RadioDeviceIds);

/// <summary>The RF caveat surfaced with every keyup-pairing response — what the action does on-air.</summary>
public static class HeadEndKeyupCaveat
{
    /// <summary>The human-readable RF caveat text.</summary>
    public const string Text =
        "RF WARNING: this action briefly keyed (transmitted through) each free NinoTNC on the head-end " +
        "to discover its physically-cabled radio by the PTT it asserts. It emits on-air and must only be " +
        "run by an operator on frequencies they are licensed and clear to key. It is never part of the " +
        "passive head-end scan.";
}
