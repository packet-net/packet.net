namespace Packet.Node.Core.Hosting;

/// <summary>
/// The health of one configured port: its <see cref="PortState"/>, when it entered it, why
/// it last failed, and which components are missing. The supervisor holds exactly one of
/// these per configured port and every operator-facing surface projects from it
/// (packet-net/packet.net#722) - the API's <c>PortStatus</c>, the <c>pdn_port_*</c> metrics,
/// the MCP backend and the console <c>PORTS</c> verb.
/// </summary>
public sealed record PortHealth
{
    /// <summary>The port's configured id (the reconcile key).</summary>
    public required string Id { get; init; }

    /// <summary>Where the port is in its lifecycle.</summary>
    public required PortState State { get; init; }

    /// <summary>When it entered <see cref="State"/> (the supervisor's <c>TimeProvider</c>, UTC).</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>Why the port last failed to come up or died, or null if it never has. Retained
    /// across a recovery so the operator can still see what happened.</summary>
    public string? LastError { get; init; }

    /// <summary>The components this port is running without, by <see cref="PortComponents"/>
    /// name. Empty unless <see cref="State"/> is <see cref="PortState.Degraded"/>.</summary>
    public IReadOnlyList<string> Degraded { get; init; } = [];

    /// <summary>How many bring-up attempts the armed retry has made (0 when none is armed).</summary>
    public int RetryAttempt { get; init; }

    /// <summary>The canonical wire name of <see cref="State"/> (see <see cref="PortStates"/>).</summary>
    public string StateName => PortStates.Name(State);

    /// <summary>Whether the port is carrying traffic right now: <see cref="PortState.Up"/> or
    /// <see cref="PortState.Degraded"/>. This is what <c>pdn_port_up</c> reports - a degraded
    /// port is still on the air, just with a piece missing.</summary>
    public bool IsServing => State is PortState.Up or PortState.Degraded;
}

/// <summary>
/// The read side of the port state model: one snapshot for every <b>configured</b> port,
/// in canonical config order. Implemented by <see cref="PortSupervisor"/> and consumed by
/// the console (which has no supervisor reference of its own) and the status projector.
/// </summary>
public interface IPortHealthView
{
    /// <summary>The health of one configured port, or null when no port carries that id.</summary>
    PortHealth? GetHealth(string id);

    /// <summary>Every configured port's health, in <c>config.Ports</c> order - the canonical
    /// port ordering (the same 1-indexed order <c>C &lt;n&gt; &lt;call&gt;</c> and the console
    /// <c>PORTS</c> listing use).</summary>
    IReadOnlyList<PortHealth> Snapshot();
}

/// <summary>One port state transition, raised on <see cref="PortSupervisor.PortStateChanged"/>
/// after the move is committed.</summary>
/// <param name="PortId">Which port moved.</param>
/// <param name="From">The state it left.</param>
/// <param name="To">The state it entered (also <c>Health.State</c>).</param>
/// <param name="Health">The port's full health at the moment of the move.</param>
public sealed record PortStateChange(string PortId, PortState From, PortState To, PortHealth Health);

/// <summary>
/// Which moves between <see cref="PortState"/>s are legal. One table, so a new path through
/// the supervisor cannot invent a transition nobody reasoned about, and so the tests can
/// assert that every transition a real reconcile produces is one of these.
/// </summary>
public static class PortStateMachine
{
    // Reading the table: a port is Configured (in config, enabled, not attempted) or Disabled;
    // a bring-up moves it through Starting to Up / Degraded, or to Faulted; a fault arms a
    // Retrying loop whose attempts re-enter Starting; every teardown passes through Stopping
    // and lands back at Configured (still wanted) or Disabled (switched off). A self-transition
    // is always legal: re-asserting Degraded adds a component, re-asserting Faulted records a
    // newer error.
    private static readonly Dictionary<PortState, PortState[]> Legal = new()
    {
        [PortState.Configured] = [PortState.Disabled, PortState.Starting, PortState.Stopping],
        [PortState.Disabled] = [PortState.Configured, PortState.Starting],
        [PortState.Starting] = [PortState.Up, PortState.Degraded, PortState.Faulted, PortState.Stopping, PortState.Disabled],
        [PortState.Up] = [PortState.Degraded, PortState.Faulted, PortState.Stopping, PortState.Disabled],
        [PortState.Degraded] = [PortState.Up, PortState.Faulted, PortState.Stopping, PortState.Disabled],
        [PortState.Faulted] = [PortState.Retrying, PortState.Starting, PortState.Stopping, PortState.Configured, PortState.Disabled],
        [PortState.Retrying] = [PortState.Starting, PortState.Faulted, PortState.Stopping, PortState.Configured, PortState.Disabled],
        [PortState.Stopping] = [PortState.Configured, PortState.Disabled, PortState.Starting, PortState.Faulted],
    };

    /// <summary>Whether <paramref name="from"/> may move to <paramref name="to"/>. A
    /// self-transition is always legal (it re-asserts the same state with fresh detail).</summary>
    public static bool IsLegal(PortState from, PortState to)
        => from == to || (Legal.TryGetValue(from, out var allowed) && Array.IndexOf(allowed, to) >= 0);

    /// <summary>The states reachable in one move from <paramref name="from"/> (excluding the
    /// always-legal self-transition). For tests and documentation.</summary>
    public static IReadOnlyList<PortState> Next(PortState from)
        => Legal.TryGetValue(from, out var allowed) ? allowed : [];
}
