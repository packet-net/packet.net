namespace Packet.Aprs;

/// <summary>
/// A decoded APRS object report (DTI <c>;</c>) per APRS12c §11.
/// </summary>
/// <remarks>
/// <para>
/// An object report broadcasts a station-defined point of interest (a
/// repeater, weather alert, event, vehicle, etc.) that isn't the
/// transmitting station's own position. Each object has a 9-character
/// name (used by receivers to overwrite a previous report of the same
/// object) and a position with the same encoding as ordinary position
/// reports — uncompressed (§8) or compressed (§9).
/// </para>
/// <para>
/// Compare with <see cref="AprsPosition"/>, which carries the
/// transmitting station's own location. Objects use the same position
/// fields plus a name, an alive/killed flag, and a mandatory timestamp.
/// </para>
/// </remarks>
/// <param name="Name">
/// 9-character object name. Trailing ASCII spaces are preserved
/// verbatim per spec (the name field is fixed-width, not space-trimmed).
/// </param>
/// <param name="IsAlive">
/// <c>true</c> if the object is "live" (alive-or-killed indicator byte
/// was <c>*</c>); <c>false</c> if "killed" (indicator <c>_</c>). Killed
/// objects tell receivers to remove a previously-reported object of
/// the same name from their displays.
/// </param>
/// <param name="Position">The object's location — same encoding rules as a station position.</param>
public readonly record struct AprsObject(
    string Name,
    bool IsAlive,
    AprsPosition Position);
