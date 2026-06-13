namespace Packet.Node.Core.SelfUpdate;

/// <summary>What happened when the update endpoint asked the host to begin an update.</summary>
public enum UpdateLaunchOutcome
{
    /// <summary>The privileged update job was dispatched; the node will be restarted by
    /// it. The caller can't observe the result in-band (its own process is replaced) —
    /// the UI polls the version endpoint for completion.</summary>
    Started,

    /// <summary>This host can't launch the update (no launcher mechanism present — e.g.
    /// no <c>systemctl</c>). Distinct from <see cref="Failed"/>: nothing was attempted.</summary>
    NotSupported,

    /// <summary>The launch was attempted but the trigger itself failed (e.g. polkit
    /// denied starting the unit). See <see cref="UpdateLaunchResult.Detail"/>.</summary>
    Failed,
}

/// <summary>The outcome of an update launch + a short non-secret detail for the audit log.</summary>
public sealed record UpdateLaunchResult(UpdateLaunchOutcome Outcome, string? Detail = null)
{
    /// <summary>The job was dispatched.</summary>
    public static UpdateLaunchResult Started { get; } = new(UpdateLaunchOutcome.Started);

    /// <summary>No launch mechanism on this host.</summary>
    public static UpdateLaunchResult NotSupported(string? detail = null) => new(UpdateLaunchOutcome.NotSupported, detail);

    /// <summary>The trigger failed.</summary>
    public static UpdateLaunchResult Failed(string? detail = null) => new(UpdateLaunchOutcome.Failed, detail);
}

/// <summary>
/// Triggers the privileged, out-of-process update job — never does the privileged work
/// itself. The node service stays unprivileged; this seam starts a root systemd unit
/// (authorized by polkit) that survives the service restart the update causes. The
/// abstraction also keeps the update endpoint unit-testable without a real systemd.
/// </summary>
public interface ISystemUpdateLauncher
{
    /// <summary>Dispatch the apt-channel update job (the targeted <c>apt</c> upgrade in
    /// <c>packetnet-update.service</c>) and return immediately. Detached: it must not be
    /// awaited to completion, because the job restarts this very process.</summary>
    Task<UpdateLaunchResult> StartAptUpdateAsync(CancellationToken cancellationToken = default);
}
