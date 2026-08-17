using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Packet.Node.Core.Audit;
using Packet.Node.Core.SelfUpdate;
using Packet.Node.Core.Tailscale;

namespace Packet.Node.Api;

/// <summary>
/// The system / self-update slice of the pdn control API (Phase 7). Exposes the node's
/// version + install channel (<c>GET /api/v1/system/info</c>, read scope) and the
/// channel-aware update trigger (<c>POST /api/v1/system/update</c>, admin scope, audited).
/// </summary>
/// <remarks>
/// <para>
/// <b>One owner per file.</b> The endpoint never touches the filesystem itself - what it
/// does is decided by the runtime-resolved <see cref="IInstallChannelProvider"/>. On the
/// <see cref="InstallChannel.Apt"/> channel it asks <see cref="ISystemUpdateLauncher"/> to
/// dispatch the privileged, detached <c>packetnet-update.service</c> oneshot (a targeted
/// <c>apt</c> upgrade), so dpkg stays the sole owner of the installed files. See
/// <c>docs/node-self-update-design.md</c>.
/// </para>
/// <para>
/// <b>Fire-and-acknowledge.</b> A successful launch returns <c>202 Accepted</c>, not a
/// result: the update job restarts this very process, so the outcome can't come back
/// in-band. The web UI polls <c>GET /api/v1/system/info</c> until the version changes.
/// Both <c>apt</c> and <c>github</c> dispatch the same oneshot (its helper body differs
/// per channel); an <c>unknown</c> channel declines (<c>409</c>) - nothing here owns the
/// files, so the operator replaces them.
/// </para>
/// </remarks>
public static class PdnSystemApi
{
    private const string AuditCategory = "Packet.Node.System";

    /// <summary>The running version - resolved from the assembly informational version, build
    /// metadata stripped. Exposed so the composition root seeds <see cref="ISystemVersionService"/>
    /// with the same string the API reports.</summary>
    public static string NodeVersion { get; } = ResolveVersion();

    /// <summary>Map the system endpoints. Called from the composition root before the SPA
    /// fallback (the specific <c>/api/v1/*</c> routes win over the catch-all).</summary>
    public static void MapPdnSystemApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var system = app.MapGroup("/api/v1/system");

        // Version + channel + what an update would do + whether one's available. Read scope.
        // updateAvailable/latestVersion come from ISystemVersionService - a cached, TTL-refreshed
        // snapshot of the per-channel check (apt-cache policy / the GitHub Releases API),
        // so /info stays an in-memory read and never blocks on the network. The check is total:
        // offline / missing tool / API error → the safe default (updateAvailable:false).
        system.MapGet("/info", (IInstallChannelProvider channel, ISystemVersionService versions) =>
        {
            var availability = versions.GetAvailabilitySnapshot();
            return Results.Ok(new SystemInfoResponse(
                Version: versions.Version,
                Channel: ChannelName(channel.Channel),
                UpdateMechanism: MechanismName(channel.Channel),
                UpdateAvailable: availability.UpdateAvailable,
                LatestVersion: availability.LatestVersion));
        }).RequireAuthorization(PdnAuthPolicies.Read);

        // The embedded Tailscale sidecar's live status (network-access.md § Status readback):
        // state, the assigned FQDN, any pending interactive-login URL, and the configured
        // Funnel exposure. Read scope (no-op when auth is off). The web control panel polls
        // this while its "Remote access (Tailscale)" panel is open. The status holder is a
        // singleton the TailscaleSidecarHostedService updates; default = disabled (a default
        // node never runs the sidecar).
        system.MapGet("/tailscale", (ITailscaleStatus status) =>
        {
            var s = status.Current;
            return Results.Ok(new TailscaleStatusResponse(
                Enabled: s.Enabled,
                State: s.State,
                Fqdn: s.Fqdn,
                AuthUrl: s.AuthUrl,
                Funnel: s.Funnel));
        }).RequireAuthorization(PdnAuthPolicies.Read);

        // Trigger an update - channel-aware. Admin scope, audited.
        system.MapPost("/update", async (
            HttpContext http,
            IInstallChannelProvider channel,
            ISystemUpdateLauncher launcher,
            GithubUpdateRequestBuilder githubRequests,
            IAuditLog auditLog,
            TimeProvider clock,
            ILoggerFactory logs) =>
        {
            var audit = logs.CreateLogger(AuditCategory);
            var ip = ClientIp(http);
            var user = UserName(http);
            var ch = channel.Channel;
            SystemLog.UpdateRequested(audit, ChannelName(ch), user, ip);
            // Also persist to the node-wide audit log (pdn.db) - a self-update restarts the
            // whole node, so it belongs in the durable §6 record, not just the structured log.
            auditLog.RecordRest(http, clock, "system_update", ChannelName(ch), "requested", "");

            // apt + github both dispatch the same packetnet-update.service oneshot (its helper
            // body differs per channel); only an unknown channel declines.
            if (ch is InstallChannel.Apt or InstallChannel.Github)
            {
                var via = ChannelName(ch);

                // The github channel needs a validated download request resolved first (latest
                // release → per-arch .deb URL → expected sha256 from SHA256SUMS, all over HTTPS).
                // If we can't build one (offline / no release / unsupported arch), decline rather
                // than launch a helper with nothing to apply.
                GithubUpdateRequest? githubReq = null;
                if (ch is InstallChannel.Github)
                {
                    githubReq = await githubRequests.BuildAsync(http.RequestAborted).ConfigureAwait(false);
                    if (githubReq is null)
                    {
                        return Declined(audit, via, "github-no-release", user, ip,
                            StatusCodes.Status409Conflict,
                            "Could not resolve a GitHub release to install (offline, no release, or an unsupported architecture).");
                    }
                }

                var request = new SystemUpdateRequest(via, githubReq);
                var result = await launcher.StartUpdateAsync(request, http.RequestAborted).ConfigureAwait(false);
                return result.Outcome switch
                {
                    UpdateLaunchOutcome.Started => Started(audit, via, user, ip),
                    UpdateLaunchOutcome.NotSupported => Declined(audit, via, "launcher-unsupported", user, ip,
                        StatusCodes.Status501NotImplemented,
                        "Update launcher is unavailable on this host (no systemd)."),
                    _ => LaunchFailed(audit, via, result.Detail, user, ip),
                };
            }

            return Declined(audit, "unknown", "unknown-channel", user, ip,
                StatusCodes.Status409Conflict,
                "Nothing on this host manages this install (it is not a dpkg-owned .deb), so the node won't replace its own files. Update by unpacking the newer release archive over it, or install the .deb.");
        }).RequireAuthorization(PdnAuthPolicies.Admin);

        // Runtime log-level control (restart-free). The node's appsettings.json is read-only
        // under ProtectSystem=strict and a restart drops every session, so an operator who needs
        // Debug/Trace on a category live can't get it by editing config + restarting. These
        // endpoints mutate the DynamicLogLevelOverrides singleton, which fires the MEL
        // filter-options change token and re-applies the rebuilt rules to every already-created
        // logger - so the new level takes effect immediately. See DynamicLogLevelOverrides.

        // The current effective default level + the active runtime overrides. Read scope.
        system.MapGet("/loglevel", (
            DynamicLogLevelOverrides dyn,
            IOptionsMonitor<LoggerFilterOptions> filterOptions) =>
        {
            // The configured floor (appsettings "Default") - MEL's LoggerFilterOptions.MinLevel.
            // Overrides above this take effect via the appended rules; the override map is the
            // live, restart-free delta the operator has applied on top.
            var effectiveDefault = filterOptions.CurrentValue.MinLevel.ToString();
            var active = dyn.Snapshot()
                .Select(kv => new LogLevelOverride(kv.Key, kv.Value.ToString()))
                .ToArray();
            return Results.Ok(new LogLevelResponse(effectiveDefault, active));
        }).RequireAuthorization(PdnAuthPolicies.Read);

        // Set or clear a runtime override. Admin scope (a mutating, security-relevant capability:
        // raising verbosity can surface sensitive material to the log sink), audited. A null /
        // empty / "clear" level removes the override; any other value must be a valid LogLevel.
        system.MapPut("/loglevel", (
            LogLevelRequest request,
            HttpContext http,
            DynamicLogLevelOverrides dyn,
            IAuditLog auditLog,
            TimeProvider clock,
            ILoggerFactory logs) =>
        {
            var audit = logs.CreateLogger(AuditCategory);
            var ip = ClientIp(http);
            var user = UserName(http);

            var category = request.Category?.Trim();
            if (string.IsNullOrEmpty(category))
            {
                return Results.Problem(
                    "A non-empty 'category' is required.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var rawLevel = request.Level?.Trim();
            // Clear semantics: null / "" / "clear" (case-insensitive) removes the override.
            if (string.IsNullOrEmpty(rawLevel) ||
                string.Equals(rawLevel, "clear", StringComparison.OrdinalIgnoreCase))
            {
                dyn.Clear(category);
                SystemLog.LogLevelCleared(audit, category, user, ip);
                auditLog.RecordRest(http, clock, "PUT /system/loglevel", category, "cleared", "");
                return Results.Ok(new LogLevelChangeResponse(category, null, "cleared"));
            }

            // Otherwise the level must parse to a real LogLevel (LogLevel.None is rejected:
            // it isn't a meaningful runtime verbosity target). 400 on anything else.
            if (!Enum.TryParse<LogLevel>(rawLevel, ignoreCase: true, out var level) ||
                level == LogLevel.None || !Enum.IsDefined(level))
            {
                return Results.Problem(
                    $"'{request.Level}' is not a valid log level. Use one of: Trace, Debug, Information, Warning, Error, Critical (or null/empty/\"clear\" to remove).",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            dyn.Set(category, level);
            SystemLog.LogLevelSet(audit, category, level.ToString(), user, ip);
            auditLog.RecordRest(http, clock, "PUT /system/loglevel", category, "set", level.ToString());
            return Results.Ok(new LogLevelChangeResponse(category, level.ToString(), "set"));
        }).RequireAuthorization(PdnAuthPolicies.Admin);
    }

    private static IResult Started(ILogger audit, string via, string user, string ip)
    {
        SystemLog.UpdateStarted(audit, via, user, ip);
        var what = via switch
        {
            "apt" => "A targeted apt upgrade is running",
            _ => "A GitHub-release update is downloading",
        };
        return Results.Json(
            new UpdateStartedResponse("started", via,
                $"{what}; the node will restart. Poll /api/v1/system/info until the version changes."),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static IResult Declined(ILogger audit, string channel, string reason, string user, string ip, int status, string message)
    {
        SystemLog.UpdateDeclined(audit, channel, reason, user, ip);
        return Results.Problem(message, statusCode: status);
    }

    private static IResult LaunchFailed(ILogger audit, string via, string? detail, string user, string ip)
    {
        SystemLog.UpdateLaunchFailed(audit, via, detail ?? "unknown", user, ip);
        return Results.Problem($"Could not start the update: {detail}", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    // The wire channel name - the shared API contract: "apt" | "github" | "unknown".
    private static string ChannelName(InstallChannel c) => c switch
    {
        InstallChannel.Apt => "apt",
        InstallChannel.Github => "github",
        _ => "unknown",
    };

    // What POST /update will actually do on this channel - for the web UI to label the button.
    private static string MechanismName(InstallChannel c) => c switch
    {
        InstallChannel.Apt => "apt",
        InstallChannel.Github => "github",
        _ => "none",
    };

    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string UserName(HttpContext http) =>
        PrincipalName.Or(http.User, "anonymous");

    private static string ResolveVersion()
    {
        var info = typeof(PdnSystemApi).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
        {
            return "dev";
        }
        int plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? info[..plus] : info;
    }
}

/// <summary>The node's version, install channel, and what an update would do (the shared
/// <c>GET /api/v1/system/info</c> contract the web UI consumes).</summary>
/// <param name="Version">The running node version (assembly informational version, build-metadata stripped).</param>
/// <param name="Channel">The install channel: <c>apt</c> / <c>github</c> / <c>unknown</c>.</param>
/// <param name="UpdateMechanism">What <c>POST /system/update</c> does here: <c>apt</c> / <c>github</c> / <c>none</c>.</param>
/// <param name="UpdateAvailable">Whether a newer version is known to be available (the per-channel
/// available-version check; reports <c>false</c> until that check ships).</param>
/// <param name="LatestVersion">The latest known version when <paramref name="UpdateAvailable"/>, else <c>null</c>.</param>
public sealed record SystemInfoResponse(
    string Version, string Channel, string UpdateMechanism, bool UpdateAvailable, string? LatestVersion);

/// <summary>Acknowledgement that a detached update job was dispatched (the node will restart).</summary>
/// <param name="Status">Always <c>started</c>.</param>
/// <param name="Via">The mechanism used (<c>apt</c>).</param>
/// <param name="Message">Operator-facing note on how to observe completion.</param>
public sealed record UpdateStartedResponse(string Status, string Via, string Message);

/// <summary>The embedded Tailscale sidecar's status, as the control panel sees it.</summary>
/// <param name="Enabled">Whether the sidecar is configured to run (<c>tailscale.enabled</c>).</param>
/// <param name="State"><c>disabled</c> / <c>starting</c> / <c>needs-login</c> / <c>running</c> / <c>error</c>.</param>
/// <param name="Fqdn">The assigned MagicDNS FQDN (<c>pdn.&lt;tailnet&gt;.ts.net</c>) once joined, else null.</param>
/// <param name="AuthUrl">The interactive <c>login.tailscale.com</c> URL when first-join needs authorising, else null.</param>
/// <param name="Funnel">Whether public exposure via Tailscale Funnel is configured on.</param>
public sealed record TailscaleStatusResponse(bool Enabled, string State, string? Fqdn, string? AuthUrl, bool Funnel);

/// <summary>The current runtime logging state: the effective default minimum level and the
/// active restart-free overrides (<c>GET /api/v1/system/loglevel</c>).</summary>
/// <param name="EffectiveDefault">The configured default minimum level (MEL <c>LoggerFilterOptions.MinLevel</c>,
/// i.e. the <c>appsettings.json</c> "Default") - the floor overrides are layered on top of.</param>
/// <param name="Overrides">The active runtime overrides (category prefix → level), longest-prefix-wins
/// like MEL's own filter rules; empty when none have been set.</param>
public sealed record LogLevelResponse(string EffectiveDefault, IReadOnlyList<LogLevelOverride> Overrides);

/// <summary>One active runtime log-level override.</summary>
/// <param name="Category">The category prefix the override applies to (longest-prefix match wins).</param>
/// <param name="Level">The minimum <see cref="LogLevel"/> name for that category prefix.</param>
public sealed record LogLevelOverride(string Category, string Level);

/// <summary>A request to set or clear a runtime log-level override
/// (<c>PUT /api/v1/system/loglevel</c>).</summary>
/// <param name="Category">The category prefix to affect (e.g. <c>Packet.Ax25</c>). Required.</param>
/// <param name="Level">The target level name (<c>Trace</c>…<c>Critical</c>) to set; or
/// <c>null</c> / empty / <c>"clear"</c> to remove the override.</param>
public sealed record LogLevelRequest(string? Category, string? Level);

/// <summary>Acknowledgement of a runtime log-level change.</summary>
/// <param name="Category">The affected category prefix.</param>
/// <param name="Level">The level now applied, or <c>null</c> when the override was cleared.</param>
/// <param name="Action"><c>set</c> or <c>cleared</c>.</param>
public sealed record LogLevelChangeResponse(string Category, string? Level, string Action);
