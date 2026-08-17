using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The one implementation of "the node supervises a child process": the <c>setsid</c> probe,
/// the group-leader spawn, the SIGTERM -> grace -> SIGKILL-the-group teardown, the stdout/stderr
/// pumps, the bounded post-exit drain, and the respawn backoff policy.
/// </summary>
/// <remarks>
/// <para>
/// C079: this core was copy-pasted three times (<see cref="Applications.Packages.AppServiceSupervisor"/>,
/// <see cref="Tailscale.TailscaleSidecarHostedService"/>, <see cref="Rigs.ManagedRigDaemon"/>) -
/// <c>Spawn</c> was byte-identical between two of them and the graceful stops differed only in a
/// field name. Every process edge case therefore had to be fixed three times, which is exactly
/// how the C076 drain hang came to exist in all three at once. One copy, one test suite.
/// </para>
/// <para>
/// C076: <see cref="DrainPumpsAsync"/> is the fix for the hang. With manual (non
/// <c>BeginOutputReadLine</c>) reads, <c>WaitForExitAsync</c> returns as soon as the DIRECT child
/// exits, but the pipes stay open as long as any grandchild holds them - a service that
/// double-forks or backgrounds a helper. An unbounded <c>await pumps</c> there parks the run loop
/// forever, and with it the reconcile gate and shutdown. The drain is therefore bounded by a
/// <see cref="TimeProvider"/> grace and escalates to SIGKILL of the process group, which is what
/// actually releases the pipes.
/// </para>
/// </remarks>
internal static class ProcessSupervision
{
    /// <summary>SIGHUP: a live reload signal for children that support one.</summary>
    public const int Sighup = 1;

    /// <summary>SIGTERM: the polite stop.</summary>
    public const int Sigterm = 15;

    /// <summary>SIGKILL: the stop that is not negotiable.</summary>
    public const int Sigkill = 9;

    /// <summary>Ceiling on the doubling respawn backoff, so a permanently broken child retries
    /// once a minute forever rather than never.</summary>
    public static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(60);

    /// <summary>How long <see cref="DrainPumpsAsync"/> waits for pipe EOF at each escalation
    /// step. Short: the child is already gone, so this is pure log-tail salvage.</summary>
    public static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(2);

    /// <summary>UTF-8 without a BOM for the child's captured streams.</summary>
    public static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The <c>setsid(1)</c> binary, or null when there is none (Windows, or a stripped image).
    /// setsid execs the target in-place when the child is not already a group leader (it never
    /// is - it was just forked from the node), so the tracked PID IS the daemon's PID and
    /// pid == pgid: <c>kill(-pid, SIGTERM)</c> reaches the whole tree gracefully.
    /// </summary>
    public static readonly Lazy<string?> SetsidPath = new(() =>
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    });

    /// <summary>What a <see cref="GracefulStopAsync"/> had to do to stop the child.</summary>
    public enum StopOutcome
    {
        /// <summary>It had already exited; no signal was sent.</summary>
        AlreadyExited,

        /// <summary>It exited within the grace after SIGTERM.</summary>
        Terminated,

        /// <summary>It ignored SIGTERM and was killed (group + tree).</summary>
        Killed,
    }

    /// <summary>
    /// Spawn a supervised child: stdin/stdout/stderr redirected, no shell (args pass verbatim,
    /// so there is no injection surface), and - where the platform allows - as a new process
    /// group so a stop can signal the whole tree. Returns the process and whether it really is a
    /// group leader (i.e. whether negative-pid signalling is available).
    /// </summary>
    public static (Process Process, bool GroupLeader) Spawn(
        string command,
        IReadOnlyList<string> args,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(args);

        var setsid = SetsidPath.Value;
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,           // no shell - args pass verbatim, no injection
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        if (setsid is not null)
        {
            psi.FileName = setsid;
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = command;
        }
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        return (process, setsid is not null);
    }

    /// <summary>
    /// Stop the child: SIGTERM the group (or the child itself, when no group was available),
    /// wait out the grace on the injected clock, then SIGKILL the group and kill the tree as the
    /// backstop for anything the group missed. Never throws; the caller logs the outcome, which
    /// is the only thing that differed between the three copies of this.
    /// </summary>
    public static async Task<StopOutcome> GracefulStopAsync(
        Process process, bool groupLeader, TimeSpan grace, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(clock);
        try
        {
            if (process.HasExited)
            {
                return StopOutcome.AlreadyExited;
            }
            var pid = process.Id;
            if (OperatingSystem.IsWindows())
            {
                // No SIGTERM to offer - go straight to the tree kill.
                KillTree(process);
                await SwallowAsync(process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
                return StopOutcome.Killed;
            }

            _ = Signal(groupLeader ? -pid : pid, Sigterm);
            using var graceCts = new CancellationTokenSource(grace, clock);
            try
            {
                await process.WaitForExitAsync(graceCts.Token).ConfigureAwait(false);
                return StopOutcome.Terminated;
            }
            catch (OperationCanceledException)
            {
                // TERM ignored within the grace - kill the whole group, then the tree as the
                // backstop for anything not in the group.
                if (groupLeader)
                {
                    _ = Signal(-pid, Sigkill);
                }
                KillTree(process);
                await SwallowAsync(process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
                return StopOutcome.Killed;
            }
        }
        catch (InvalidOperationException)
        {
            // No process associated (already torn down) - nothing to do.
            return StopOutcome.AlreadyExited;
        }
    }

    /// <summary>
    /// Drain the stdout/stderr pumps after the direct child has exited, BOUNDED (C076).
    /// <para>
    /// Waits <paramref name="grace"/> for pipe EOF; if a pipe-holding grandchild means EOF is
    /// never coming, SIGKILLs the process group (the only thing that actually releases those
    /// pipes) and waits one more grace; failing that, cancels the pump token and abandons them.
    /// Returns whether the pumps finished, so a caller can log an honest "log tail lost".
    /// </para>
    /// <para>
    /// The pump token is belt-and-braces, not the fix: a read already blocked inside the OS pipe
    /// is not reliably interruptible, which is why killing the group is the load-bearing step.
    /// </para>
    /// </summary>
    public static async Task<bool> DrainPumpsAsync(
        Task pumps,
        CancellationTokenSource pumpStop,
        int pid,
        bool groupLeader,
        TimeSpan grace,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(pumps);
        ArgumentNullException.ThrowIfNull(pumpStop);
        ArgumentNullException.ThrowIfNull(clock);

        if (await FinishedWithinAsync(pumps, grace, clock).ConfigureAwait(false))
        {
            return true;
        }

        if (groupLeader && !OperatingSystem.IsWindows())
        {
            _ = Signal(-pid, Sigkill);
            if (await FinishedWithinAsync(pumps, grace, clock).ConfigureAwait(false))
            {
                return true;
            }
        }

        try
        {
            await pumpStop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Raced with teardown; the abandon below still applies.
        }
        // Abandoned deliberately: the pumps only feed a logger, so an unkillable reader costs a
        // parked task, whereas awaiting it costs the whole supervisor.
        return await FinishedWithinAsync(pumps, grace, clock).ConfigureAwait(false);
    }

    /// <summary>Copy one child stream into <paramref name="sink"/> line by line. Total: a pump
    /// fault (or cancellation) only ends log capture, it never disturbs supervision.</summary>
    public static async Task PumpAsync(StreamReader reader, Action<string> sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                if (line.Length > 0)
                {
                    sink(line);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort log capture only - never disturbs supervision.
        }
    }

    /// <summary>Await a task, swallowing whatever it throws (it was handled where it happened).</summary>
    public static async Task SwallowAsync(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Handled at the source; ignore here.
        }
    }

    /// <summary>The next respawn delay: double it, capped at <see cref="BackoffCap"/>.</summary>
    public static TimeSpan NextBackoff(TimeSpan current) =>
        current + current > BackoffCap ? BackoffCap : current + current;

    /// <summary>Whether a run that lasted <paramref name="uptime"/> counts as healthy enough to
    /// reset the backoff to its base. Without this a child that runs fine for hours and then
    /// dies once is respawned at the accumulated (up to <see cref="BackoffCap"/>) delay, as if it
    /// were still crash-looping.</summary>
    public static bool WasHealthyRun(TimeSpan uptime) => uptime >= HealthyRunThreshold;

    /// <summary>A run this long is taken as "it worked" for backoff purposes.</summary>
    public static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromSeconds(30);

    /// <summary>Send <paramref name="signal"/> to <paramref name="pid"/> (negative = the process
    /// group). Returns the raw <c>kill(2)</c> result; a failure is normally just a race with the
    /// child exiting.</summary>
    public static int Signal(int pid, int signal) => SysKill(pid, signal);

    private static async Task<bool> FinishedWithinAsync(Task task, TimeSpan grace, TimeProvider clock)
    {
        if (task.IsCompleted)
        {
            await SwallowAsync(task).ConfigureAwait(false);
            return true;
        }
        var timeout = Task.Delay(grace, clock, CancellationToken.None);
        var finished = await Task.WhenAny(task, timeout).ConfigureAwait(false);
        if (!ReferenceEquals(finished, task))
        {
            return false;
        }
        await SwallowAsync(task).ConfigureAwait(false);
        return true;
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Race: already gone.
        }
    }

    // Classic DllImport (not LibraryImport): the source-generated marshaller demands
    // AllowUnsafeBlocks project-wide, which this one int-only syscall does not justify.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);
#pragma warning restore SYSLIB1054
}
