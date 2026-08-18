namespace Packet.Node.Core.Tailscale;

/// <summary>
/// The thread-safe, live status of the embedded Tailscale <c>tsnet</c> sidecar - the
/// single source of truth the supervisor <b>writes</b> and the read API <b>reads</b>. A
/// process-wide singleton (registered in DI): the <see cref="TailscaleSidecarHostedService"/>
/// updates it as the child reports JSON status lines on stdout; the control API projects it
/// to <c>GET /api/v1/system/tailscale</c> and the web panel polls that.
/// </summary>
/// <remarks>
/// Default is <see cref="TailscaleStatusSnapshot.Disabled"/> - a default node never enables
/// the sidecar, so the panel shows "disabled" and nothing has run. Reads return an immutable
/// snapshot (no torn read of the individual fields); writes swap the whole snapshot atomically.
/// </remarks>
public interface ITailscaleStatus
{
    /// <summary>The current immutable status snapshot. Never null.</summary>
    TailscaleStatusSnapshot Current { get; }

    /// <summary>Atomically swap in a new status snapshot (the supervisor's write seam).</summary>
    void Update(TailscaleStatusSnapshot snapshot);
}

/// <summary>
/// An immutable snapshot of the sidecar's status - what the read API serialises (camelCase)
/// and the web panel renders.
/// </summary>
/// <param name="Enabled">Whether the sidecar is configured to run (config <c>tailscale.enabled</c>).
/// When <c>false</c> the other fields describe the disabled state.</param>
/// <param name="State">The lifecycle state, one of: <c>disabled</c> (not configured to run),
/// <c>starting</c> (child launched, not yet joined), <c>needs-login</c> (interactive auth
/// required - see <paramref name="AuthUrl"/>), <c>running</c> (joined; reachable at
/// <paramref name="Fqdn"/>), <c>error</c> (the child reported or hit a fault - see
/// <paramref name="Error"/>; the supervisor backs off and retries).</param>
/// <param name="Fqdn">The assigned MagicDNS FQDN (<c>pdn.&lt;tailnet&gt;.ts.net</c>) once the
/// node has joined, else null.</param>
/// <param name="AuthUrl">The interactive <c>login.tailscale.com</c> URL when first-join needs
/// the operator to authorise the node, else null.</param>
/// <param name="Error">The last error message when <see cref="State"/> is <c>error</c>, else null.</param>
/// <param name="Funnel">Whether public exposure via Tailscale Funnel is configured on.</param>
public sealed record TailscaleStatusSnapshot(
    bool Enabled,
    string State,
    string? Fqdn,
    string? AuthUrl,
    string? Error,
    bool Funnel)
{
    /// <summary>The default / not-configured-to-run snapshot.</summary>
    public static TailscaleStatusSnapshot Disabled { get; } =
        new(Enabled: false, State: "disabled", Fqdn: null, AuthUrl: null, Error: null, Funnel: false);

    /// <summary>A <c>starting</c> snapshot - the child has launched (or is relaunching after a
    /// backoff) but has not yet joined the tailnet.</summary>
    public static TailscaleStatusSnapshot Starting(bool funnel) =>
        new(Enabled: true, State: "starting", Fqdn: null, AuthUrl: null, Error: null, Funnel: funnel);

    /// <summary>An <c>error</c> snapshot carrying <paramref name="message"/> - the child reported
    /// or hit a fault; the supervisor backs off and retries.</summary>
    public static TailscaleStatusSnapshot Faulted(string message, bool funnel) =>
        new(Enabled: true, State: "error", Fqdn: null, AuthUrl: null, Error: message, Funnel: funnel);
}

/// <summary>
/// The default <see cref="ITailscaleStatus"/>: an immutable snapshot behind a lock-free
/// atomic reference (<see cref="Volatile"/>), so a read never tears across fields and a write
/// is a single reference swap. Starts at <see cref="TailscaleStatusSnapshot.Disabled"/>.
/// </summary>
public sealed class TailscaleStatusHolder : ITailscaleStatus
{
    private TailscaleStatusSnapshot snapshot = TailscaleStatusSnapshot.Disabled;

    /// <inheritdoc/>
    public TailscaleStatusSnapshot Current => Volatile.Read(ref snapshot);

    /// <inheritdoc/>
    public void Update(TailscaleStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref this.snapshot, snapshot);
    }
}
