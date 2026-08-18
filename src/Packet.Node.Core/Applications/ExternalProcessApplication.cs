using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.Applications;

/// <summary>
/// The <c>pdn-app/1</c> stdio "floor": runs a registered <see cref="ApplicationConfig"/> as an
/// out-of-process child spawned per connect, bridging the connected user's
/// <see cref="INodeConnection"/> to the child's stdin/stdout via <see cref="AppSessionBridge"/>.
/// The node and the app share <b>no code</b> - only the documented wire
/// (<c>docs/app-local-session-wire.md</c>), which is the entire separation boundary.
/// </summary>
/// <remarks>
/// This type owns the process lifecycle (spawn, stderr drain, teardown); the duplex wire itself
/// lives in <see cref="AppSessionBridge"/>, shared with the long-running-socket rung. Teardown is
/// unconditional: the child's stdin is closed (EOF - a well-behaved app exits), and after a short
/// grace the process tree is killed. The node never crashes because an app misbehaves; a spawn
/// failure surfaces as <see cref="ApplicationStartException"/> for the host to report.
/// </remarks>
public sealed partial class ExternalProcessApplication : INodeApplication
{
    /// <summary>How long to wait for the child to exit on stdin-EOF before killing its tree.</summary>
    private static readonly TimeSpan TeardownGrace = TimeSpan.FromSeconds(3);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ApplicationConfig config;
    private readonly ILogger logger;

    public ExternalProcessApplication(ApplicationConfig config, ILogger? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.logger = logger ?? NullLogger.Instance;
        if (string.IsNullOrWhiteSpace(config.Executable))
        {
            throw new ArgumentException("A process application requires a command.", nameof(config));
        }
    }

    /// <inheritdoc/>
    public async Task RunAsync(INodeConnection session, NodeAppContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);

        var process = StartProcess();   // throws ApplicationStartException if the spawn fails
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = linked.Token;

            var stderr = DrainStderrAsync(process, ct);

            // The bridge owns the wire and is total - it forwards the child's output even if the
            // header couldn't be delivered (a one-shot child that exited fast). The only "couldn't
            // start" failure is the spawn above (StartProcess → ApplicationStartException).
            await AppSessionBridge.RunAsync(
                session,
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                AppSessionBridge.BuildHeader(config.Id, context),
                ct).ConfigureAwait(false);

            await linked.CancelAsync().ConfigureAwait(false);
            await SwallowAsync(stderr).ConfigureAwait(false);
        }
        finally
        {
            await TeardownAsync(process).ConfigureAwait(false);
        }
    }

    private Process StartProcess()
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Executable!,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,            // no shell - args are passed verbatim, no injection
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        foreach (var arg in config.Args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (!string.IsNullOrEmpty(config.WorkingDirectory))
        {
            psi.WorkingDirectory = config.WorkingDirectory;
        }

        try
        {
            var process = Process.Start(psi)
                ?? throw new ApplicationStartException(config.Id, config.Executable!, reason: "Process.Start returned null.");
            LogStarted(config.Id, config.Executable!, process.Id);
            return process;
        }
        catch (ApplicationStartException)
        {
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException or InvalidOperationException)
        {
            LogStartFailed(ex, config.Id, config.Executable!);
            throw new ApplicationStartException(config.Id, config.Executable!, ex);
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.Length > 0)
                {
                    LogAppStderr(config.Id, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // teardown - normal.
        }
        catch (Exception)
        {
            // best-effort diagnostics only.
        }
    }

    // Unconditional teardown: close stdin (EOF), give the child a short grace to exit on its own,
    // then kill the whole tree. Always disposes the Process.
    private async Task TeardownAsync(Process process)
    {
        try { process.StandardInput.Close(); } catch { /* may already be closed */ }

        try
        {
            if (!process.HasExited)
            {
                using var grace = new CancellationTokenSource(TeardownGrace);
                try
                {
                    await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Didn't exit on stdin-EOF within the grace - kill the tree.
                    try { process.Kill(entireProcessTree: true); } catch { /* race: already gone */ }
                    try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    LogKilled(config.Id);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            // No process associated (already torn down) - nothing to do.
        }
        finally
        {
            try { LogExited(config.Id, process.HasExited ? process.ExitCode : -1); } catch { }
            process.Dispose();
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try { await task.ConfigureAwait(false); } catch { /* handled in-pump; ignore here */ }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "App '{Id}' started: {Command} (pid {Pid}).")]
    private partial void LogStarted(string id, string command, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App '{Id}' failed to start: {Command}.")]
    private partial void LogStartFailed(Exception ex, string id, string command);

    [LoggerMessage(Level = LogLevel.Information, Message = "App '{Id}' exited (code {ExitCode}).")]
    private partial void LogExited(string id, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App '{Id}' did not exit on stdin-EOF; killed its process tree.")]
    private partial void LogKilled(string id);

    [LoggerMessage(Level = LogLevel.Debug, Message = "App '{Id}' stderr: {Line}")]
    private partial void LogAppStderr(string id, string line);
}

/// <summary>
/// Thrown when a registered application cannot be reached (a process can't be spawned, or a
/// socket app's daemon isn't listening). The host catches it, logs, and tells the user the
/// application is unavailable - the node itself never fails because an app is misconfigured.
/// </summary>
public sealed class ApplicationStartException : Exception
{
    public ApplicationStartException(string appId, string target, Exception inner)
        : base($"Application '{appId}' could not start ('{target}').", inner)
    {
        AppId = appId;
        Target = target;
    }

    public ApplicationStartException(string appId, string target, string reason)
        : base($"Application '{appId}' could not start ('{target}'): {reason}")
    {
        AppId = appId;
        Target = target;
    }

    public string AppId { get; }

    /// <summary>The thing that couldn't be reached - the command (process app) or socket path.</summary>
    public string Target { get; }

    /// <summary>The command, for a process application (alias of <see cref="Target"/>).</summary>
    public string Command => Target;
}
