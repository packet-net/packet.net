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
/// The per-channel parameters the privileged update oneshot needs — what the runtime channel
/// detection (and, for github, the available-version check + the resolved release asset) computed,
/// handed to the root helper so it <em>validates rather than trusts the caller</em>. The C# side
/// owns the apt-vs-github distinction (the build stamp can't disambiguate <c>deb</c>), so the
/// launcher tells the single oneshot which per-channel helper to dispatch.
/// </summary>
/// <param name="Channel">The resolved install channel the oneshot dispatches on
/// (<c>apt</c> / <c>github</c> / <c>selfcontained</c>).</param>
/// <param name="GithubRequest">For the github channel: the validated download request the helper
/// applies (<c>targetVersion</c> / <c>arch</c> / <c>debUrl</c> / <c>sha256</c>). Null otherwise.</param>
public sealed record SystemUpdateRequest(string Channel, GithubUpdateRequest? GithubRequest = null);

/// <summary>
/// The github-channel Apply request file the privileged helper consumes — written by the node, but
/// the helper re-validates every field (the URL host, the sha256 against the download) rather than
/// trusting it blindly (docs/node-self-update-design.md § Channel = github).
/// </summary>
/// <param name="TargetVersion">The release version being applied (e.g. <c>0.9.0</c>).</param>
/// <param name="Arch">The Debian arch of the <c>.deb</c> to fetch (<c>amd64</c>/<c>arm64</c>/<c>armhf</c>).</param>
/// <param name="DebUrl">The HTTPS GitHub-release download URL of <c>packetnet_&lt;ver&gt;_&lt;arch&gt;.deb</c>.</param>
/// <param name="Sha256">The expected SHA-256 (lowercase hex) the helper verifies the download against.</param>
public sealed record GithubUpdateRequest(string TargetVersion, string Arch, string DebUrl, string Sha256);

/// <summary>
/// Triggers the privileged, out-of-process update job — never does the privileged work
/// itself. The node service stays unprivileged; this seam starts a root systemd unit
/// (authorized by polkit) that survives the service restart the update causes. The
/// abstraction also keeps the update endpoint unit-testable without a real systemd.
/// </summary>
public interface ISystemUpdateLauncher
{
    /// <summary>Dispatch the update job (<c>packetnet-update.service</c>) and return immediately.
    /// The single oneshot dispatches the per-channel helper named by
    /// <paramref name="request"/>.<see cref="SystemUpdateRequest.Channel"/> — the apt-channel
    /// targeted <c>apt upgrade</c>, the github-channel release-<c>.deb</c> <c>dpkg -i</c>, or the
    /// self-contained download/swap. Detached: it must not be awaited to completion, because the
    /// job restarts this very process.</summary>
    Task<UpdateLaunchResult> StartUpdateAsync(SystemUpdateRequest request, CancellationToken cancellationToken = default);
}
