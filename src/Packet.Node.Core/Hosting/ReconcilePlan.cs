using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The field-level delta between two <see cref="NodeConfig"/>s, expressed as the
/// minimal set of reconcile actions. Pure and deterministic — computed by
/// <see cref="ReconcilePlanner.Plan"/>, then executed by the
/// <see cref="PortSupervisor"/> / <see cref="NodeHostedService"/>. Splitting the
/// "decide" from the "do" makes the marquee invariant
/// (a change touches exactly the right ports, in exactly the right class)
/// property-testable without spinning up real listeners.
/// </summary>
public sealed record ReconcilePlan
{
    /// <summary>True if the station callsign changed — a node-wide reset
    /// (every listener recreated, all sessions end). When set, the per-port
    /// lists describe the post-reset target set rather than incremental diffs.</summary>
    public bool NodeWideReset { get; init; }

    /// <summary>Ports present in the new config but not the old → bring up.</summary>
    public IReadOnlyList<PortConfig> ToBringUp { get; init; } = [];

    /// <summary>Port ids present in the old config but not the new → tear down.</summary>
    public IReadOnlyList<string> ToTearDown { get; init; } = [];

    /// <summary>Ports whose restart-class fields changed (transport, or an
    /// enabled→enabled identity change) → single-port restart (tear down + bring
    /// up). Keyed by the new <see cref="PortConfig"/>.</summary>
    public IReadOnlyList<PortConfig> ToRestart { get; init; } = [];

    /// <summary>Ports that flipped enabled→disabled → tear down (kept in config).</summary>
    public IReadOnlyList<string> ToDisable { get; init; } = [];

    /// <summary>Ports that flipped disabled→enabled → bring up.</summary>
    public IReadOnlyList<PortConfig> ToEnable { get; init; } = [];

    /// <summary>Ports whose KISS params changed but nothing restart-class did →
    /// apply the new params live (no restart). Keyed by the new config.</summary>
    public IReadOnlyList<PortConfig> KissParamsChanged { get; init; } = [];

    /// <summary>Ports whose AX.25 params changed but nothing restart-class did →
    /// recorded for the next bring-up; the live listener + its sessions are NOT
    /// disturbed. Keyed by the new config.</summary>
    public IReadOnlyList<PortConfig> Ax25ParamsChanged { get; init; } = [];

    /// <summary>True if the telnet console bind/port/enabled changed → restart
    /// just the telnet listener.</summary>
    public bool TelnetChanged { get; init; }

    /// <summary>True if the operator-facing service text changed → reference swap
    /// (the console reads it live; nothing to restart).</summary>
    public bool ServicesChanged { get; init; }

    /// <summary>True when the plan would touch nothing — an idempotent re-apply
    /// of the same config.</summary>
    public bool IsNoOp =>
        !NodeWideReset && !TelnetChanged && !ServicesChanged &&
        ToBringUp.Count == 0 && ToTearDown.Count == 0 && ToRestart.Count == 0 &&
        ToDisable.Count == 0 && ToEnable.Count == 0 &&
        KissParamsChanged.Count == 0 && Ax25ParamsChanged.Count == 0;
}
