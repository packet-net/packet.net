namespace Packet.Node.Core.Configuration;

/// <summary>
/// The node's system-default ID beacon: a periodic connectionless AX.25 UI frame
/// (an "ID"/presence broadcast) transmitted on each port. <b>Default-OFF</b> -
/// <see cref="Enabled"/> defaults <c>false</c> so a stock node never transmits an
/// unsolicited beacon until the operator opts in (the no-regression contract). A
/// port may override this with <see cref="PortConfig.Beacon"/>.
/// </summary>
public sealed record BeaconConfig
{
    /// <summary>Whether the node beacons on its ports by default. Default
    /// <c>false</c> - a node that has never beaconed must keep not beaconing.</summary>
    public bool Enabled { get; init; }

    /// <summary>Minutes between beacon transmissions on a port. Default 30.</summary>
    public int IntervalMinutes { get; init; } = 30;

    /// <summary>The beacon's information text. <c>{node}</c> (alias else callsign)
    /// and <c>{call}</c> (the station callsign) placeholders are expanded - exactly
    /// like the services banner / prompt. Default <c>"{node} pdn node"</c>.</summary>
    public string Text { get; init; } = "{node} pdn node";
}

/// <summary>
/// A per-port ID-beacon override. <see cref="Enabled"/> always wins outright; the
/// nullable <see cref="IntervalMinutes"/> / <see cref="Text"/> fields inherit the
/// system default (<see cref="BeaconConfig"/>) when left null - a per-field merge.
/// </summary>
public sealed record PortBeaconConfig
{
    /// <summary>Whether this port beacons. This flag is authoritative for the port -
    /// it is not merged: a port-override with <c>Enabled = false</c> silences a port
    /// even when the system default is on, and vice-versa.</summary>
    public bool Enabled { get; init; }

    /// <summary>Minutes between this port's beacons. Null = inherit the system
    /// default's <see cref="BeaconConfig.IntervalMinutes"/>.</summary>
    public int? IntervalMinutes { get; init; }

    /// <summary>This port's beacon text (<c>{node}</c>/<c>{call}</c> expanded). Null =
    /// inherit the system default's <see cref="BeaconConfig.Text"/>.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// The fully-resolved beacon for one port - the per-port override (if any) merged
/// over the system default. This is what the <c>BeaconService</c> arms a timer from.
/// </summary>
/// <param name="Enabled">Whether to beacon on this port at all.</param>
/// <param name="IntervalMinutes">Resolved transmit interval, minutes (≥ 1).</param>
/// <param name="Text">Resolved beacon text, with <c>{node}</c>/<c>{call}</c> still unexpanded.</param>
public readonly record struct EffectiveBeacon(bool Enabled, int IntervalMinutes, string Text)
{
    /// <summary>
    /// Resolve the effective beacon for a port: the per-port <paramref name="port"/>
    /// override merged over the system <paramref name="systemDefault"/>. When the port
    /// has no override the system default applies wholesale; when it has one, its
    /// <see cref="PortBeaconConfig.Enabled"/> wins outright and its null interval/text
    /// fall back to the system default's.
    /// </summary>
    public static EffectiveBeacon Resolve(BeaconConfig systemDefault, PortBeaconConfig? port)
    {
        ArgumentNullException.ThrowIfNull(systemDefault);
        if (port is null)
        {
            return new EffectiveBeacon(systemDefault.Enabled, systemDefault.IntervalMinutes, systemDefault.Text);
        }
        return new EffectiveBeacon(
            port.Enabled,
            port.IntervalMinutes ?? systemDefault.IntervalMinutes,
            port.Text ?? systemDefault.Text);
    }
}
