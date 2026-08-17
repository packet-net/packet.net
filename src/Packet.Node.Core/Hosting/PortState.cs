namespace Packet.Node.Core.Hosting;

/// <summary>
/// The lifecycle state of ONE configured port, held once by the
/// <see cref="PortSupervisor"/> (packet-net/packet.net#722).
/// </summary>
/// <remarks>
/// <para>
/// Before this type the state was inferred from dictionary membership: a port present in
/// the supervisor's port set was "up", and absent meant disabled, never-attempted, faulted
/// or mid-restart, all indistinguishable. Every consumer re-derived its own vocabulary from
/// that, and a port that died on the air while its entry survived read "up" forever.
/// </para>
/// <para>
/// The supervisor holds one state per <b>configured</b> port (running or not) and moves it
/// through <see cref="PortStateMachine"/>'s legal transitions. <see cref="RunningPort"/> is
/// the runtime half, referenced from the entry only while the port is
/// <see cref="Up"/> / <see cref="Degraded"/> (and briefly <see cref="Stopping"/>).
/// </para>
/// </remarks>
public enum PortState
{
    /// <summary>In config and enabled, but not attempted yet (before the first bring-up) or
    /// between a teardown and the bring-up that follows it.</summary>
    Configured,

    /// <summary>In config with <c>enabled: false</c>. Not running by design.</summary>
    Disabled,

    /// <summary>A bring-up is in flight (transport open, listener start, radio/rig attach).</summary>
    Starting,

    /// <summary>Serving: the listener is running and every configured component attached.</summary>
    Up,

    /// <summary>Serving, but with a piece missing - see <see cref="PortHealth.Degraded"/> for
    /// which (a radio that would not open, a rig whose daemon is down, a networked transport
    /// that is mid-reconnect). The packet channel still carries traffic.</summary>
    Degraded,

    /// <summary>Not serving: a bring-up failed, or a running port's listener died underneath
    /// it. <see cref="PortHealth.LastError"/> says why.</summary>
    Faulted,

    /// <summary>Faulted with a bounded-backoff retry armed - the port is trying to come back
    /// with no config edit. <see cref="PortHealth.RetryAttempt"/> counts the attempts.</summary>
    Retrying,

    /// <summary>A teardown is in flight (reconcile, restart, disable, or shutdown).</summary>
    Stopping,
}

/// <summary>
/// The canonical wire names for <see cref="PortState"/> - the one string set
/// <c>PortStatus.State</c> carries, the console <c>PORTS</c> verb prints, and the SPA's
/// <c>PortState</c> union mirrors. Lower-case, hyphen-free, stable.
/// </summary>
public static class PortStates
{
    /// <summary><see cref="PortState.Configured"/>.</summary>
    public const string Configured = "configured";

    /// <summary><see cref="PortState.Disabled"/>.</summary>
    public const string Disabled = "disabled";

    /// <summary><see cref="PortState.Starting"/>.</summary>
    public const string Starting = "starting";

    /// <summary><see cref="PortState.Up"/>.</summary>
    public const string Up = "up";

    /// <summary><see cref="PortState.Degraded"/>.</summary>
    public const string Degraded = "degraded";

    /// <summary><see cref="PortState.Faulted"/>.</summary>
    public const string Faulted = "faulted";

    /// <summary><see cref="PortState.Retrying"/>.</summary>
    public const string Retrying = "retrying";

    /// <summary><see cref="PortState.Stopping"/>.</summary>
    public const string Stopping = "stopping";

    /// <summary>Every state name, in <see cref="PortState"/> declaration order. The closed set
    /// the client contract fixture pins.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Configured, Disabled, Starting, Up, Degraded, Faulted, Retrying, Stopping];

    /// <summary>The wire name of a state.</summary>
    public static string Name(PortState state) => state switch
    {
        PortState.Configured => Configured,
        PortState.Disabled => Disabled,
        PortState.Starting => Starting,
        PortState.Up => Up,
        PortState.Degraded => Degraded,
        PortState.Faulted => Faulted,
        PortState.Retrying => Retrying,
        PortState.Stopping => Stopping,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "unknown port state"),
    };
}

/// <summary>
/// The named per-port components a <see cref="PortState.Degraded"/> port can be missing.
/// A port degrades in place (it still carries packet traffic) rather than failing, because
/// none of these is on the data path.
/// </summary>
/// <remarks>
/// Carrier sense is deliberately not a component of its own: it is a facet of the radio
/// (a radio with hardware DCD feeds the listener's medium-access gate), so a port that lost
/// its radio reports <see cref="Radio"/> and the lost CSMA gate is implied.
/// </remarks>
public static class PortComponents
{
    /// <summary>The radio-control attachment (<c>port.radio</c>): no per-frame RSSI/SNR
    /// metadata and no hardware carrier sense feeding the CSMA gate.</summary>
    public const string Radio = "radio";

    /// <summary>The rig-control (CAT) attachment (<c>port.rig</c>): no frequency/mode/PTT
    /// status on <c>/api/v1/rigs</c>.</summary>
    public const string Rig = "rig";

    /// <summary>The node-managed <c>rigctld</c> daemon this port's <c>rig:</c> block asked for
    /// (it never started listening, so there was nothing for the rig - or a rig-backed radio -
    /// to dial).</summary>
    public const string Rigctld = "rigctld";

    /// <summary>The port's self-healing networked transport has lost its link and is
    /// re-dialling (the reconnect decorator on a kiss-tcp / nino-tnc-tcp / head-end-bound
    /// tait port). The listener is alive; nothing reaches the air until it reconnects.</summary>
    public const string Transport = "transport";
}
