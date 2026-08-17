using Packet.Ax25.Session;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The host-side <see cref="ISysopOperations"/> — the privileged actions an elevated
/// over-RF sysop drives, implemented over the EXACT serialized seams the web control API
/// uses: <see cref="NodeHostedService.RunExclusiveAsync{T}"/> for session enumeration /
/// kick (so they can't race a config reconcile or a port bring-up), and
/// <see cref="IWritableConfigProvider.TryApply"/> for the port enable/disable (the same
/// validated, persisted, reconcile-driving path the Ports API takes). Reload re-reads the
/// conffile via <see cref="FileConfigProvider.Reload"/>, identical to the file-watcher's
/// own trigger, and is offered only on a conffile node: <see cref="SupportsReload"/> is
/// false when config lives in <c>pdn.db</c> (see <c>docs/config-in-db.md</c>), so the
/// console neither advertises nor promises a verb that cannot work there.
/// </summary>
/// <remarks>
/// The caller (<c>NodeCommandService</c>) has already authorised: it only invokes these
/// after a live, unexpired <c>SYSOP</c> elevation with a sufficient scope. This type does
/// not re-check elevation — it is the authorised action surface.
/// </remarks>
internal sealed class HostSysopOperations : ISysopOperations
{
    private readonly NodeHostedService host;
    private readonly IConfigProvider config;

    public HostSysopOperations(NodeHostedService host, IConfigProvider config)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken ct = default) =>
        host.RunExclusiveAsync<IReadOnlyList<string>>(() =>
        {
            var lines = new List<string>();
            var sup = host.Supervisor;
            if (sup is not null)
            {
                foreach (var portId in sup.RunningPortIds)
                {
                    var port = sup.GetPort(portId);
                    if (port is null)
                    {
                        continue;
                    }
                    foreach (var session in port.Listener.ActiveSessions)
                    {
                        // The same id the web API mints, so a sysop can KICK what SESSIONS shows
                        // even when a station holds two circuits here at once (#723 item 5).
                        var id = Api.SessionIds.Format(
                            portId,
                            session.Context.Remote.ToString(),
                            session.Context.Local.ToString(),
                            port.Listener.MyCall.ToString());
                        lines.Add($"{id} {session.CurrentState}");
                    }
                }
            }
            return Task.FromResult<IReadOnlyList<string>>(lines);
        }, ct);

    /// <inheritdoc/>
    public Task<SysopActionResult> KickAsync(string sessionId, CancellationToken ct = default) =>
        host.RunExclusiveAsync(() =>
        {
            if (!Api.SessionIds.TryParse(sessionId, out var portId, out var peer, out var local))
            {
                return Task.FromResult(SysopActionResult.Failure(
                    $"Bad session id '{sessionId}'. Use the id SESSIONS prints."));
            }
            var listener = host.Supervisor?.GetPort(portId)?.Listener;
            // Match the engine's FULL key: the short form means this port's own callsign.
            var wantedLocal = local ?? listener?.MyCall.ToString();
            var session = listener?.ActiveSessions
                .FirstOrDefault(s => s.Context.Remote.ToString() == peer
                    && string.Equals(s.Context.Local.ToString(), wantedLocal, StringComparison.OrdinalIgnoreCase));
            if (session is null)
            {
                return Task.FromResult(SysopActionResult.Failure($"No active session {sessionId}."));
            }
            // Request an orderly disconnect — the same event the web Sessions API posts.
            session.PostEvent(new DlDisconnectRequest());
            return Task.FromResult(SysopActionResult.Success($"Disconnecting {sessionId}."));
        }, ct);

    /// <inheritdoc/>
    public Task<SysopActionResult> SetPortEnabledAsync(string portId, bool enabled, CancellationToken ct = default)
    {
        if (config is not IWritableConfigProvider writable)
        {
            return Task.FromResult(SysopActionResult.Failure("Config is not writable on this node."));
        }
        var existing = config.Current.Ports.FirstOrDefault(p => p.Id == portId);
        if (existing is null)
        {
            return Task.FromResult(SysopActionResult.Failure($"No port '{portId}'."));
        }
        var word = enabled ? "up" : "down";
        if (existing.Enabled == enabled)
        {
            return Task.FromResult(SysopActionResult.Success($"Port '{portId}' is already {word}."));
        }
        // Flip Enabled and apply through the seam — TryApply validates + persists +
        // raises OnChange, and the reconcile worker brings the port up/down. Same path as
        // the web Ports lifecycle endpoint.
        var ports = config.Current.Ports
            .Select(p => p.Id == portId ? p with { Enabled = enabled } : p)
            .ToArray();
        if (!writable.TryApply(config.Current with { Ports = ports }, out var errors))
        {
            var why = errors.Count > 0 ? errors[0].Message : "rejected by validation";
            return Task.FromResult(SysopActionResult.Failure($"Could not bring '{portId}' {word}: {why}"));
        }
        return Task.FromResult(SysopActionResult.Success($"Port '{portId}' {word} (reconciling)."));
    }

    /// <inheritdoc/>
    public bool SupportsReload => config is FileConfigProvider;

    /// <inheritdoc/>
    public Task<SysopActionResult> ReloadAsync(CancellationToken ct = default)
    {
        if (config is FileConfigProvider file)
        {
            bool applied = file.Reload();
            return Task.FromResult(SysopActionResult.Success(
                applied ? "Config reloaded." : "Config re-read (no change / unchanged or invalid; see logs)."));
        }
        // Not a conffile node: config lives in pdn.db (docs/config-in-db.md) and every write
        // applies through the reconcile path as it is made, so there is nothing to re-read.
        return Task.FromResult(SysopActionResult.Failure(
            "Nothing to reload: config lives in pdn.db and applies as soon as it is written."));
    }

}
