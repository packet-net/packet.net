namespace Packet.Aprs;

/// <summary>
/// A decoded APRS item report (DTI <c>)</c>) per APRS101 §11.
/// </summary>
/// <remarks>
/// <para>
/// Items are like objects (see <see cref="AprsObject"/>) but have a
/// variable-length name (3–9 characters) instead of a fixed 9-byte
/// name slot, and have no timestamp. Used for transient or
/// less-frequently-updated points of interest such as first-aid
/// stations, rare DX spots, gas stations.
/// </para>
/// </remarks>
/// <param name="Name">
/// 3-to-9 character item name as it appeared on the wire. Spec §11
/// reserves <c>!</c> and <c>_</c> from the name character set (they
/// terminate the name).
/// </param>
/// <param name="IsAlive">
/// <c>true</c> if the indicator byte was <c>!</c> (live); <c>false</c>
/// if <c>_</c> (killed).
/// </param>
/// <param name="Position">The item's location.</param>
public readonly record struct AprsItem(
    string Name,
    bool IsAlive,
    AprsPosition Position);
