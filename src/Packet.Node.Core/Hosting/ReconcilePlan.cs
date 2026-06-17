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
    /// live-reseed the running listener's per-session parameters so NEW sessions
    /// use them; existing sessions keep their object identity and in-flight state.
    /// No restart. Keyed by the new config.</summary>
    public IReadOnlyList<PortConfig> Ax25ParamsChanged { get; init; } = [];

    /// <summary>Ports whose AX.25 compatibility profile (<see cref="PortConfig.Compat"/>)
    /// changed but nothing restart-class did → same live reseed mechanism as
    /// <see cref="Ax25ParamsChanged"/> (the reseeded parameter record carries the
    /// parse options + session quirks). Parse options take effect on the next
    /// inbound frame; quirks on the next-built session — existing sessions
    /// untouched. Keyed by the new config.</summary>
    public IReadOnlyList<PortConfig> CompatChanged { get; init; } = [];

    /// <summary>Ports whose per-port NET/ROM <see cref="PortConfig.NetRomQuality"/>
    /// changed but nothing restart-class did → hot-apply the new route quality to the
    /// port's NET/ROM attachment (no restart). QUALITY only affects how the next NODES
    /// broadcast on this port is quality-combined — read-only awareness/advertisement, it
    /// can never disturb a live session — so it is the lightest possible hot edit. Keyed
    /// by the new config.</summary>
    public IReadOnlyList<PortConfig> NetRomQualityChanged { get; init; } = [];

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
        KissParamsChanged.Count == 0 && Ax25ParamsChanged.Count == 0 && CompatChanged.Count == 0 &&
        NetRomQualityChanged.Count == 0;
}
