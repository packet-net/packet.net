namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// Supervises the daemons of enabled packages whose manifest declares a
/// <c>service:</c> block with <see cref="AppServiceManaged.Pdn"/>: start on enable, stop on
/// disable, restart-with-backoff per policy, crash-loop breaker into
/// <see cref="AppServiceState.Faulted"/>. Reconcile is idempotent (desired vs running) and runs
/// at startup, on every config apply, and on demand. Contract: <c>docs/app-packages.md</c>
/// § Lifecycle.
/// </summary>
public interface IAppServiceSupervisor
{
    /// <summary>Bring running services in line with the current config + catalog snapshot:
    /// start missing, stop surplus, leave matching alone.</summary>
    Task ReconcileAsync(CancellationToken cancellationToken = default);

    /// <summary>Stop-then-start one service regardless of backoff state - the owner's way out
    /// of <see cref="AppServiceState.Faulted"/>. Throws <see cref="InvalidOperationException"/>
    /// for an unknown id or a service pdn does not manage.</summary>
    Task RestartAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Current state of every known service (managed and external).</summary>
    IReadOnlyList<AppServiceStatus> Statuses { get; }
}

/// <summary>Lifecycle state of one app service.</summary>
public enum AppServiceState
{
    /// <summary>Not running (disabled, never started, or cleanly stopped).</summary>
    Stopped,

    /// <summary>Spawn in progress.</summary>
    Starting,

    /// <summary>Process alive.</summary>
    Running,

    /// <summary>Exited; waiting out the restart backoff.</summary>
    Backoff,

    /// <summary>Crash-loop breaker tripped - stays down until toggled or restarted.</summary>
    Faulted,

    /// <summary>Owner-managed (<see cref="AppServiceManaged.External"/>) - pdn does not track health.</summary>
    External,
}

/// <summary>A point-in-time snapshot of one service's state.</summary>
public sealed record AppServiceStatus(
    string Id,
    AppServiceState State,
    int? Pid = null,
    string? Detail = null);
