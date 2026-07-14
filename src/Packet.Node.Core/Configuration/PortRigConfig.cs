namespace Packet.Node.Core.Configuration;

/// <summary>
/// Optional per-port <b>rig-control attachment</b> (<c>rig:</c>): the CAT/station-control channel
/// to the transceiver behind this port — frequency, mode, PTT state and TX-side meters via
/// <c>Packet.Rig.IRigControl</c> (hamlib's <c>rigctld</c> network protocol or flrig's XML-RPC
/// server). Read-only surface today: the node polls and projects rig state
/// (<c>GET /api/v1/rigs</c>, <c>GET /api/v1/ports/{id}/rig</c>, the <c>/api/v1/rigs/events</c>
/// SSE stream); it never tunes, changes mode, or keys. Null / absent = no rig attached,
/// byte-for-byte today's behaviour.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>station-control</b> sibling of the <c>radio:</c> block (plan OQ-011): a
/// <c>radio:</c> attachment is the packet-medium seam (RSSI tagging, hardware carrier-sense
/// CSMA), a <c>rig:</c> attachment is the operator-facing CAT view. A port can carry both — an
/// HF port might have a NinoTNC modem, a Tait <c>radio:</c> is nonsense there, but a
/// <c>rig: {kind: hamlib}</c> gives the operator the transceiver's dial state and TX health.
/// The rig never touches the packet path.
/// </para>
/// <para>
/// <b>Two binding shapes.</b> The <b>BYO-daemon</b> shape (<see cref="Host"/> +
/// <see cref="Port"/>) points at a rigctld/flrig the operator already runs — the only shape
/// flrig supports (it is a GUI app the node can't sensibly spawn). The <b>node-managed</b>
/// shape (<see cref="Device"/> + <see cref="Model"/>, hamlib only) hands the daemon's lifetime
/// to the node: it spawns <c>rigctld -m &lt;model&gt; -r &lt;device&gt;</c> on a loopback port it
/// allocates itself, supervises it (respawn with capped backoff — an unplugged USB CAT cable
/// self-heals on replug), and dials that. <see cref="Device"/> being set is what selects the
/// shape; <see cref="Host"/>/<see cref="Port"/> then stay unset (the node owns the endpoint),
/// and <see cref="Model"/>/<see cref="SerialSpeed"/> belong only to it.
/// </para>
/// <para>
/// A rig that fails to connect at port start degrades cleanly: the fault is logged and the port
/// runs without rig status — an absent rigctld/flrig daemon must never take a working packet
/// channel down. After a successful attach, transport drops self-heal (the backends re-dial per
/// command), so a bounced daemon comes back on the next poll. Changing this block is a
/// restart-class config edit (see <c>Hosting.ReconcilePlanner</c>).
/// </para>
/// </remarks>
public sealed record PortRigConfig
{
    /// <summary>The rig-control backend kind — one of <see cref="RigKinds.Names"/>
    /// (<c>hamlib</c> = rigctld's network protocol, <c>flrig</c> = flrig's XML-RPC server).
    /// Matched case- and hyphen/underscore-insensitively.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Host running the rig daemon (the BYO-daemon shape). Default loopback — neither
    /// rigctld nor flrig has authentication, so pointing beyond localhost is a deliberate
    /// station-owner choice. Must stay unset (or the loopback default) when <see cref="Device"/>
    /// selects the node-managed shape — the node spawns its daemon locally.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>The daemon's TCP port (the BYO-daemon shape). Null uses the kind's stock port
    /// (<c>hamlib</c> → 4532, <c>flrig</c> → 12345). Must stay unset when <see cref="Device"/>
    /// selects the node-managed shape — the node allocates a loopback port itself.</summary>
    public int? Port { get; init; }

    /// <summary>The rig's serial device path (e.g. <c>/dev/serial/by-id/usb-Icom_Inc._IC-7300…</c>
    /// — the by-id path survives renumbering). Setting this selects the <b>node-managed</b>
    /// shape: the node spawns and supervises <c>rigctld</c> against this device instead of
    /// dialling an operator-run daemon. hamlib only; <see cref="Model"/> is required with it.</summary>
    public string? Device { get; init; }

    /// <summary>The hamlib rig model number <c>rigctld -m</c> is given (see <c>rigctl -l</c> for
    /// the list, e.g. 3073 = IC-7300). Node-managed shape only — required with, and only with,
    /// <see cref="Device"/>.</summary>
    public int? Model { get; init; }

    /// <summary>The CAT serial speed <c>rigctld -s</c> is given, in baud. Node-managed shape
    /// only. Null = hamlib's per-backend default for the model.</summary>
    public int? SerialSpeed { get; init; }

    /// <summary>True when this block binds by <see cref="Device"/>+<see cref="Model"/> — the
    /// node-managed shape, where the node spawns and supervises the rigctld itself. The single
    /// authority the validator and the port supervisor both consult, so a shape the validator
    /// accepted always resolves the same way at bring-up. (Mirrors
    /// <see cref="PortRadioConfig.IsHeadEndBound"/>.)</summary>
    public bool IsNodeManaged => !string.IsNullOrWhiteSpace(Device);

    /// <summary>How often (seconds) the rig status monitor polls frequency/mode/PTT while the
    /// transmitter is idle. Null uses the 5 s default. Must be positive when set. Each poll is a
    /// couple of CAT round-trips on the rig's serial bus — keep it conservative.</summary>
    public int? PollIntervalSeconds { get; init; }

    /// <summary>How often (seconds) the monitor polls while the transmitter is keyed — the fast
    /// cadence that makes SWR/power meters live during a transmission (meters read ~0 idle, so
    /// there is no point polling them fast otherwise). Null uses the 1 s default. Must be
    /// positive when set.</summary>
    public int? MeterIntervalSeconds { get; init; }

    /// <summary>
    /// Human-readable endpoint for status surfaces — the single authority behind
    /// <see cref="Api.RigStatus.Endpoint"/> (<c>Rigs.RigStatusMonitor</c> and
    /// <c>Rigs.RigReadModels</c> both use it). BYO daemon: <c>host:port</c> with the kind default
    /// resolved. Node-managed: the device path plus the spawned daemon's loopback endpoint when
    /// the allocated port is known (i.e. on the effective config the supervisor dials), or just
    /// the device path when it isn't (the config-only projection of an unattached rig — the port
    /// only exists once the daemon runs).
    /// </summary>
    public string DescribeEndpoint() =>
        !IsNodeManaged ? $"{Host}:{Port ?? RigKinds.DefaultPort(Kind)}"
        : Port is { } p ? $"{Device} (managed rigctld @{Host}:{p})"
        : $"{Device} (managed rigctld)";
}

/// <summary>
/// The canonical rig-control <c>kind:</c> strings and their matching rules — the single
/// authority the validator and the <c>Rigs.RigControlFactory</c> both use, so a kind the
/// validator accepted can never fail to resolve at bring-up. (Mirrors <see cref="RadioKinds"/>.)
/// </summary>
public static class RigKinds
{
    /// <summary>hamlib's NET-rigctl network protocol — a <c>rigctld</c> daemon (or one of the
    /// many emulators of it) fronting any of hamlib's 200+ rigs (<c>Packet.Rig.Hamlib</c>).</summary>
    public const string Hamlib = "hamlib";

    /// <summary>flrig's XML-RPC server (<c>Packet.Rig.Flrig</c>).</summary>
    public const string Flrig = "flrig";

    /// <summary>The recognised kind names (for the validator's error message + docs).</summary>
    public static IReadOnlyList<string> Names { get; } = [Hamlib, Flrig];

    /// <summary>True if <paramref name="kind"/> names a known rig-control kind. Null/empty is NOT
    /// valid — a rig block must say what protocol it speaks.</summary>
    public static bool IsKnown(string? kind) => Is(kind, Hamlib) || Is(kind, Flrig);

    /// <summary>The kind's stock daemon TCP port (<c>hamlib</c> → 4532, <c>flrig</c> → 12345) —
    /// what a null <see cref="PortRigConfig.Port"/> resolves to. 0 for an unknown kind
    /// (unreachable for validated config).</summary>
    public static int DefaultPort(string? kind) =>
        Is(kind, Hamlib) ? 4532 :
        Is(kind, Flrig) ? 12345 : 0;

    /// <summary>Case- and hyphen/underscore-insensitive kind comparison, matching the
    /// transport-kind and radio-kind conventions.</summary>
    public static bool Is(string? kind, string canonical) =>
        !string.IsNullOrWhiteSpace(kind) &&
        string.Equals(Normalise(kind), Normalise(canonical), StringComparison.Ordinal);

    private static string Normalise(string raw) =>
        raw.Replace("-", "", StringComparison.Ordinal)
           .Replace("_", "", StringComparison.Ordinal)
           .ToLowerInvariant();
}
