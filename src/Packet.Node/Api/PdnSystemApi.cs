using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.SelfUpdate;

namespace Packet.Node.Api;

/// <summary>
/// The system / self-update slice of the pdn control API (Phase 7). Exposes the node's
/// version + install channel (<c>GET /api/v1/system/info</c>, read scope) and the
/// channel-aware update trigger (<c>POST /api/v1/system/update</c>, admin scope, audited).
/// </summary>
/// <remarks>
/// <para>
/// <b>One owner per file.</b> The endpoint never touches the filesystem itself — what it
/// does is decided by the build-stamped <see cref="IInstallChannelProvider"/>. On the
/// <see cref="InstallChannel.Apt"/> channel it asks <see cref="ISystemUpdateLauncher"/> to
/// dispatch the privileged, detached <c>packetnet-update.service</c> oneshot (a targeted
/// <c>apt</c> upgrade), so dpkg stays the sole owner of the installed files. See
/// <c>docs/node-self-update-design.md</c>.
/// </para>
/// <para>
/// <b>Fire-and-acknowledge.</b> A successful launch returns <c>202 Accepted</c>, not a
/// result: the update job restarts this very process, so the outcome can't come back
/// in-band. The web UI polls <c>GET /api/v1/system/info</c> until the version changes.
/// Both <c>apt</c> and <c>self-contained</c> dispatch the same oneshot (its helper body
/// differs per install); only an <c>unknown</c> channel declines (<c>409</c>).
/// </para>
/// </remarks>
public static class PdnSystemApi
{
    private const string AuditCategory = "Packet.Node.System";
    private static readonly string NodeVersion = ResolveVersion();

    /// <summary>Map the system endpoints. Called from the composition root before the SPA
    /// fallback (the specific <c>/api/v1/*</c> routes win over the catch-all).</summary>
    public static void MapPdnSystemApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var system = app.MapGroup("/api/v1/system");

        // Version + channel + what an update would do. Read scope (no-op when auth is off).
        system.MapGet("/info", (IInstallChannelProvider channel) =>
            Results.Ok(new SystemInfoResponse(
                Version: NodeVersion,
                Channel: ChannelName(channel.Channel),
                UpdateMechanism: MechanismName(channel.Channel))))
            .RequireAuthorization(PdnAuthPolicies.Read);

        // Trigger an update — channel-aware. Admin scope, audited.
        system.MapPost("/update", async (
            HttpContext http,
            IInstallChannelProvider channel,
            ISystemUpdateLauncher launcher,
            ILoggerFactory logs) =>
        {
            var audit = logs.CreateLogger(AuditCategory);
            var ip = ClientIp(http);
            var user = UserName(http);
            var ch = channel.Channel;
            SystemLog.UpdateRequested(audit, ChannelName(ch), user, ip);

            // apt + self-contained both dispatch the same packetnet-update.service oneshot
            // (its helper body differs per install); only an unknown channel declines.
            if (ch is InstallChannel.Apt or InstallChannel.SelfContained)
            {
                var via = ChannelName(ch);
                var result = await launcher.StartUpdateAsync(http.RequestAborted).ConfigureAwait(false);
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
                "This node's install channel is unknown, so it won't self-update. Update via your package manager or reinstall.");
        }).RequireAuthorization(PdnAuthPolicies.Admin);
    }

    private static IResult Started(ILogger audit, string via, string user, string ip)
    {
        SystemLog.UpdateStarted(audit, via, user, ip);
        var what = via == "apt"
            ? "A targeted apt upgrade is running"
            : "A self-contained update is downloading";
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

    private static string ChannelName(InstallChannel c) => c switch
    {
        InstallChannel.Apt => "apt",
        InstallChannel.SelfContained => "self-contained",
        _ => "unknown",
    };

    // What POST /update will actually do on this channel — for the web UI to label the button.
    private static string MechanismName(InstallChannel c) => c switch
    {
        InstallChannel.Apt => "apt",
        InstallChannel.SelfContained => "self-contained",
        _ => "none",
    };

    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string UserName(HttpContext http) =>
        http.User.Identity?.Name
        ?? http.User.FindFirst("sub")?.Value
        ?? "anonymous";

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

/// <summary>The node's version, install channel, and what an update would do.</summary>
/// <param name="Version">The running node version (assembly informational version, build-metadata stripped).</param>
/// <param name="Channel">The install channel: <c>apt</c> / <c>self-contained</c> / <c>unknown</c>.</param>
/// <param name="UpdateMechanism">What <c>POST /system/update</c> does here: <c>apt</c> / <c>self-contained</c> / <c>none</c>.</param>
public sealed record SystemInfoResponse(string Version, string Channel, string UpdateMechanism);

/// <summary>Acknowledgement that a detached update job was dispatched (the node will restart).</summary>
/// <param name="Status">Always <c>started</c>.</param>
/// <param name="Via">The mechanism used (<c>apt</c>).</param>
/// <param name="Message">Operator-facing note on how to observe completion.</param>
public sealed record UpdateStartedResponse(string Status, string Via, string Message);
