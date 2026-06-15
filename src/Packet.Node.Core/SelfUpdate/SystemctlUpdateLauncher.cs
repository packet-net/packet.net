using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Launches an update by writing the per-channel request to a spool dir, then starting the
/// <c>packetnet-update.service</c> systemd oneshot with <c>--no-block</c> (fire-and-return):
/// systemd runs the unit as root, a shipped polkit rule authorizes the unprivileged
/// <c>packetnet</c> user to start <em>only</em> that one unit, and the oneshot — detached from this
/// service — reads the spool, dispatches the per-channel helper (apt upgrade / github-release
/// <c>dpkg -i</c> / self-contained swap), and restarts us. We never run apt / dpkg / touch files.
/// </summary>
/// <remarks>
/// The C# side owns the apt-vs-github distinction (the build stamp only encodes <c>deb</c>), so the
/// resolved channel — and, for github, the validated download request — are written to
/// <c>/run/packetnet/</c> for the root oneshot to read. <c>/run</c> is tmpfs the oneshot can read;
/// the node's service user owns the spool dir (created by the package's <c>RuntimeDirectory</c>).
/// <c>--no-block</c> is load-bearing: the unit's job restarts <c>packetnet.service</c>, so blocking
/// on completion would mean blocking on our own death. A missing <c>systemctl</c> (non-systemd
/// host) is reported as <see cref="UpdateLaunchOutcome.NotSupported"/>, not a crash.
/// </remarks>
public sealed class SystemctlUpdateLauncher : ISystemUpdateLauncher
{
    private const string UpdateUnit = "packetnet-update.service";

    /// <summary>The spool dir the root oneshot reads the request from (the package ships
    /// <c>RuntimeDirectory=packetnet</c> so this exists, service-user-owned, under tmpfs).
    /// Overridable for tests via <c>PDN_UPDATE_SPOOL</c>.</summary>
    public static string SpoolDir =>
        Environment.GetEnvironmentVariable("PDN_UPDATE_SPOOL") is { Length: > 0 } s ? s : "/run/packetnet";

    /// <summary>The file naming the channel to dispatch (one line: <c>apt</c>/<c>github</c>/<c>selfcontained</c>).</summary>
    public static string ChannelFile => Path.Combine(SpoolDir, "update.channel");

    /// <summary>The github-channel request file (<c>{ targetVersion, arch, debUrl, sha256 }</c>).</summary>
    public static string GithubRequestFile => Path.Combine(SpoolDir, "github-update.json");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <inheritdoc/>
    public async Task<UpdateLaunchResult> StartUpdateAsync(SystemUpdateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            WriteSpool(request);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateLaunchResult.Failed($"could not stage the update request: {ex.Message}");
        }

        var psi = new ProcessStartInfo("systemctl")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("start");
        psi.ArgumentList.Add("--no-block");
        psi.ArgumentList.Add(UpdateUnit);

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // systemctl not on PATH → not a systemd host; nothing was attempted.
            return UpdateLaunchResult.NotSupported("systemctl not found");
        }

        if (proc is null)
        {
            return UpdateLaunchResult.Failed("could not start systemctl");
        }

        using (proc)
        {
            var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode == 0)
            {
                return UpdateLaunchResult.Started;
            }
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"systemctl exit {proc.ExitCode}" : stderr.Trim();
            return UpdateLaunchResult.Failed(detail);
        }
    }

    private static void WriteSpool(SystemUpdateRequest request)
    {
        Directory.CreateDirectory(SpoolDir);

        // Write the github request first (so the channel file — which the oneshot keys on — only
        // appears once its request is fully on disk). Stale request from a prior run is overwritten;
        // remove it on non-github channels so a github helper can never pick up a leftover.
        if (string.Equals(request.Channel, "github", StringComparison.Ordinal) && request.GithubRequest is { } g)
        {
            File.WriteAllText(GithubRequestFile, JsonSerializer.Serialize(g, Json));
        }
        else
        {
            TryDelete(GithubRequestFile);
        }

        File.WriteAllText(ChannelFile, request.Channel + "\n");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort; a stale github request is re-validated by the helper anyway.
        }
    }
}
