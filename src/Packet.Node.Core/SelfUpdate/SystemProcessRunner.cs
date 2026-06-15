using System.ComponentModel;
using System.Diagnostics;

namespace Packet.Node.Core.SelfUpdate;

/// <summary>
/// The production <see cref="IProcessRunner"/>: runs the command via <see cref="Process"/>,
/// captures stdout, and — load-bearing — turns a missing executable into
/// <see cref="ProcessRunResult.NotLaunched"/> rather than letting the launch error escape.
/// So a probe for <c>dpkg-query</c> or <c>apt-cache</c> on a host that has neither (Alpine,
/// Fedora-without-them, Windows) is a clean negative, not a crash.
/// </summary>
/// <remarks>
/// The two launch-failure shapes we treat as "executable absent":
/// <list type="bullet">
/// <item><see cref="Win32Exception"/> — how .NET surfaces both ENOENT ("No such file or
/// directory") and Windows' ERROR_FILE_NOT_FOUND when the binary isn't on PATH.</item>
/// </list>
/// Probes are short and synchronous (read-to-end then wait), with a bounded timeout so a
/// wedged probe can't hang boot-time channel resolution.
/// </remarks>
public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>The default singleton — channel detection holds no per-call state.</summary>
    public static SystemProcessRunner Instance { get; } = new();

    private readonly TimeSpan timeout;

    /// <param name="timeout">How long to wait for a probe before giving up (defaults to 10s).
    /// A wedged probe is treated as <see cref="ProcessRunResult.NotLaunched"/>.</param>
    public SystemProcessRunner(TimeSpan? timeout = null) =>
        this.timeout = timeout ?? TimeSpan.FromSeconds(10);

    /// <inheritdoc/>
    public ProcessRunResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // Executable not on PATH / not present → the documented safe-fallback signal.
            return ProcessRunResult.NotLaunched;
        }

        if (proc is null)
        {
            return ProcessRunResult.NotLaunched;
        }

        using (proc)
        {
            try
            {
                var stdout = proc.StandardOutput.ReadToEnd();
                if (!proc.WaitForExit(timeout))
                {
                    try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                    return ProcessRunResult.NotLaunched;
                }
                return ProcessRunResult.Ran(proc.ExitCode, stdout);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // Pipe/stream fault mid-read — treat as a failed probe (safe fallback).
                return ProcessRunResult.NotLaunched;
            }
        }
    }
}
