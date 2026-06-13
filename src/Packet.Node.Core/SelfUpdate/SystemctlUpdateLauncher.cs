using System.ComponentModel;
using System.Diagnostics;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// Launches the apt-channel update by starting the <c>packetnet-update.service</c>
/// systemd oneshot with <c>--no-block</c> (fire-and-return): systemd runs the unit as
/// root, a shipped polkit rule authorizes the unprivileged <c>packetnet</c> user to
/// start <em>only</em> that one unit, and the oneshot — detached from this service —
/// runs the targeted <c>apt</c> upgrade and restarts us. We never run apt ourselves.
/// </summary>
/// <remarks>
/// <c>--no-block</c> is load-bearing: the unit's job restarts <c>packetnet.service</c>,
/// so blocking on completion would mean blocking on our own death. We only confirm the
/// start request was accepted; the web UI polls the version endpoint for the result.
/// A missing <c>systemctl</c> (non-systemd host, container) is reported as
/// <see cref="UpdateLaunchOutcome.NotSupported"/>, not a crash.
/// </remarks>
public sealed class SystemctlUpdateLauncher : ISystemUpdateLauncher
{
    private const string UpdateUnit = "packetnet-update.service";

    /// <inheritdoc/>
    public async Task<UpdateLaunchResult> StartUpdateAsync(CancellationToken cancellationToken = default)
    {
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
}
