using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.Rigs;

/// <summary>
/// A node-managed <c>rigctld</c> for one port's node-managed <c>rig:</c> block
/// (<see cref="PortRigConfig.IsNodeManaged"/>): spawned as
/// <c>rigctld -m &lt;model&gt; -r &lt;device&gt; [-s &lt;serialSpeed&gt;] -T 127.0.0.1 -t &lt;port&gt;</c>
/// on a loopback port allocated <b>once</b> at start, and supervised for the port's lifetime —
/// respawn on exit with doubling backoff (capped), forever. The <see cref="Hosting.PortSupervisor"/>
/// creates one per node-managed port, points the existing TCP rig client(s) at
/// <see cref="ClientConfig"/>, and disposes it LAST on teardown (clients before their daemon).
/// </summary>
/// <remarks>
/// <para>
/// The spawn / teardown discipline mirrors <see cref="Tailscale.TailscaleSidecarHostedService"/>:
/// on Linux the child is a process-group leader (via <c>setsid</c>) so a stop SIGTERMs the whole
/// group; the graceful stop is SIGTERM → grace period → SIGKILL the group + kill the tree. Every
/// timer-shaped wait rides the injected <see cref="TimeProvider"/>. The surface is <b>total</b>:
/// a missing binary or a spawn failure logs and backs off — it never throws out of the run loop
/// or crashes the port. There is deliberately no crash-loop fault: a respawn loop with capped
/// backoff is exactly right for plug-and-play — the daemon self-heals when an unplugged USB
/// device comes back.
/// </para>
/// <para>
/// Because the port is allocated once and every respawn reuses it, the <c>RigctldRig</c> clients
/// (which re-dial per command) recover from a daemon bounce without reconfiguration — the same
/// self-heal contract a BYO daemon gives them. Readiness is a caller concern:
/// <see cref="WaitUntilReadyAsync"/> poll-connects the listener and reports <c>false</c> on
/// budget expiry so the caller can degrade (never throws for "not ready").
/// </para>
/// </remarks>
public sealed partial class ManagedRigDaemon : IAsyncDisposable
{
    /// <summary>Env var overriding the <c>rigctld</c> binary resolved from <c>PATH</c> (tests
    /// point this at a fake script). An explicit factory argument overrides both.</summary>
    public const string BinaryPathEnvVar = "PDN_RIGCTLD_BIN";

    /// <summary>The default binary name, resolved from <c>PATH</c> by the spawn (the .deb
    /// Depends on <c>libhamlib-utils</c>, which ships it at <c>/usr/bin/rigctld</c>).</summary>
    public const string DefaultBinaryName = "rigctld";

    /// <summary>The default <see cref="WaitUntilReadyAsync"/> budget the port supervisor uses.</summary>
    public static readonly TimeSpan DefaultReadyBudget = TimeSpan.FromSeconds(10);

    /// <summary>How often <see cref="WaitUntilReadyAsync"/> re-probes the listener.</summary>
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(150);

    private readonly string portId;
    private readonly PortRigConfig config;
    private readonly TimeProvider clock;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger logger;
    private readonly string binaryPath;
    private readonly TimeSpan backoffBase;
    private readonly TimeSpan stopGrace;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task runLoop;

    // Completes the first time a spawn attempt throws (binary missing / not executable): a
    // launch that can't even start will not become ready within any budget, so
    // WaitUntilReadyAsync bails out early instead of burning it. Never completes for a child
    // that launches and then exits — that one may legitimately come good on a respawn.
    private readonly TaskCompletionSource spawnFaulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The live child's pid, published while it runs (cleared on exit/teardown) — the disposal
    // path signals it, and tests assert respawn/orphan behaviour through it. -1 = none.
    private readonly object childGate = new();
    private int childPid = -1;
    private int disposed;

    private ManagedRigDaemon(
        string portId,
        PortRigConfig config,
        int allocatedPort,
        ILoggerFactory loggerFactory,
        TimeProvider clock,
        string binaryPath,
        TimeSpan backoffBase,
        TimeSpan stopGrace)
    {
        this.portId = portId;
        this.config = config;
        this.clock = clock;
        this.binaryPath = binaryPath;
        this.backoffBase = backoffBase;
        this.stopGrace = stopGrace;
        this.loggerFactory = loggerFactory;
        logger = loggerFactory.CreateLogger<ManagedRigDaemon>();
        Port = allocatedPort;
        ClientConfig = config with { Host = "127.0.0.1", Port = allocatedPort };
        runLoop = Task.Run(() => RunChildAsync(stopping.Token), CancellationToken.None);
    }

    /// <summary>The loopback TCP port allocated for the daemon — fixed for this instance's whole
    /// life, so every respawn comes back on the same endpoint the clients already dial.</summary>
    public int Port { get; }

    /// <summary>The effective <c>rig:</c> config the TCP client(s) should dial — the node-managed
    /// block re-pointed at the spawned daemon (<c>127.0.0.1:<see cref="Port"/></c>). Carries the
    /// original <see cref="PortRigConfig.Device"/>, so its
    /// <see cref="PortRigConfig.DescribeEndpoint"/> stays honest about what is really attached.</summary>
    public PortRigConfig ClientConfig { get; }

    /// <summary>The running child's pid, or <c>null</c> when no child is currently running
    /// (mid-backoff, or stopped). Diagnostic — tests assert respawn and no-orphan through it.</summary>
    public int? ChildPid
    {
        get
        {
            lock (childGate)
            {
                return childPid < 0 ? null : childPid;
            }
        }
    }

    /// <summary>
    /// Allocate the daemon's loopback port and start the supervision loop (the first spawn
    /// happens on it, in the background). Binary resolution: <paramref name="binaryPath"/> →
    /// the <see cref="BinaryPathEnvVar"/> env var → <see cref="DefaultBinaryName"/> from
    /// <c>PATH</c>. A binary that can't launch does NOT throw here — the loop logs and backs
    /// off, and <see cref="WaitUntilReadyAsync"/> reports <c>false</c> so the caller degrades.
    /// </summary>
    /// <param name="portId">The owning node port (log/category context).</param>
    /// <param name="config">The node-managed <c>rig:</c> block (<see cref="PortRigConfig.IsNodeManaged"/>
    /// must hold — <see cref="PortRigConfig.Device"/>/<see cref="PortRigConfig.Model"/> drive the args).</param>
    /// <param name="loggerFactory">Sink for the supervisor's own log + the child's output
    /// (stderr → Warning, stdout → Debug under <c>rigctld:&lt;portId&gt;</c>).</param>
    /// <param name="timeProvider">Clock every backoff/grace/readiness wait rides. Null = system.</param>
    /// <param name="binaryPath">Explicit binary override (tests). Null = env var / PATH.</param>
    /// <param name="backoffBase">Respawn backoff base (doubles per exit, capped 60 s). Null = 1 s.</param>
    /// <param name="stopGrace">SIGTERM→SIGKILL grace on stop. Null = 5 s.</param>
    public static ManagedRigDaemon Start(
        string portId,
        PortRigConfig config,
        ILoggerFactory? loggerFactory = null,
        TimeProvider? timeProvider = null,
        string? binaryPath = null,
        TimeSpan? backoffBase = null,
        TimeSpan? stopGrace = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(config);
        if (!config.IsNodeManaged || config.Model is null)
        {
            throw new ArgumentException(
                "ManagedRigDaemon needs a node-managed rig config (device + model set) - " +
                "validated config always carries both, so this is a wiring bug at the call site.",
                nameof(config));
        }

        // Grab a free loopback port, then hand it to rigctld. (Bind-release has a nominal race;
        // in practice the pattern is what the whole ecosystem uses — rigctld can't bind port 0.)
        int allocatedPort;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            allocatedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var resolvedBinary = binaryPath
            ?? Environment.GetEnvironmentVariable(BinaryPathEnvVar)
            ?? DefaultBinaryName;

        return new ManagedRigDaemon(
            portId,
            config,
            allocatedPort,
            loggerFactory ?? NullLoggerFactory.Instance,
            timeProvider ?? TimeProvider.System,
            resolvedBinary,
            backoffBase ?? TimeSpan.FromSeconds(1),
            stopGrace ?? TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Wait until the daemon accepts a TCP connection on <see cref="Port"/> (connect itself is
    /// the readiness probe — the idiom the rigctld ecosystem uses), polling on the injected
    /// clock. Returns <c>false</c> — never throws — when the budget expires, or early when the
    /// binary could not even be launched (a missing rigctld cannot come good within the budget);
    /// the caller then degrades to running the port without a rig.
    /// </summary>
    public async Task<bool> WaitUntilReadyAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        var deadline = clock.GetUtcNow() + budget;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, Port, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (SocketException)
            {
                // Not listening yet (or just died) — fall through to the fail-fast/deadline checks.
            }

            if (spawnFaulted.Task.IsCompleted || clock.GetUtcNow() >= deadline)
            {
                return false;
            }
            await Task.Delay(ReadyPollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The child's whole supervised life: spawn → pump logs → exit → backoff → respawn,
    /// until disposed. Total — a defect logs, never unwinds the port. (The
    /// <see cref="Tailscale.TailscaleSidecarHostedService"/> RunChildAsync shape.)</summary>
    private async Task RunChildAsync(CancellationToken ct)
    {
        var delay = backoffBase;
        // rigctld chatters (each verbose client command echoes) — its output gets its own
        // per-port category, like the app supervisor's "app:<id>" children.
        var childLogger = loggerFactory.CreateLogger("rigctld:" + portId);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Process? process = null;
                string? spawnError = null;
                var groupLeader = false;
                var startedAt = clock.GetUtcNow();
                try
                {
                    EnsureLaunchable(binaryPath);
                    (process, groupLeader) = ProcessSupervision.Spawn(binaryPath, BuildArgs());
                }
                catch (Exception ex)
                {
                    spawnError = ex.Message;
                }

                if (process is null)
                {
                    LogSpawnFailed(portId, binaryPath, spawnError ?? "unknown error");
                    // A launch that can't start will not come good within a readiness budget —
                    // let WaitUntilReadyAsync bail out now instead of burning it.
                    spawnFaulted.TrySetResult();
                }
                else
                {
                    LogStarted(portId, binaryPath, process.Id, Port);
                    PublishChildPid(process.Id);
                    // Cancellable pumps tied to this run's stop (C076).
                    using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var pid = process.Id;
                    var pumps = Task.WhenAll(
                        ProcessSupervision.PumpAsync(process.StandardOutput, line => DaemonLog.Stdout(childLogger, line), pumpStop.Token),
                        ProcessSupervision.PumpAsync(process.StandardError, line => DaemonLog.Stderr(childLogger, line), pumpStop.Token));
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // Already closed / broken pipe — irrelevant.
                    }

                    try
                    {
                        await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stop requested (port teardown / dispose): graceful teardown.
                        ClearChildPid(process.Id);
                        await GracefulStopAsync(process, groupLeader).ConfigureAwait(false);
                        await CleanupChildAsync(process, pumps, pumpStop, pid, groupLeader).ConfigureAwait(false);
                        return;
                    }

                    ClearChildPid(process.Id);
                    var exitCode = process.ExitCode;
                    // The child exited on its own (a crash, or the USB device vanished from under
                    // it). Respawn after the backoff — same port, so the re-dialling clients
                    // recover without reconfiguration when the device comes back. The drain is
                    // BOUNDED (C076): a grandchild holding the pipes must not wedge this loop.
                    LogExited(portId, exitCode, delay.TotalSeconds);
                    await CleanupChildAsync(process, pumps, pumpStop, pid, groupLeader).ConfigureAwait(false);

                    // A run that lasted counts as healthy: reset the backoff so one failure
                    // after hours of uptime is not punished at the crash-loop rate.
                    if (ProcessSupervision.WasHealthyRun(clock.GetUtcNow() - startedAt))
                    {
                        delay = backoffBase;
                    }
                }

                // Backoff before the next attempt (spawn failure or unexpected exit both retry).
                try
                {
                    await Task.Delay(delay, clock, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = ProcessSupervision.NextBackoff(delay);
            }
        }
        catch (Exception ex)
        {
            // Total: a supervisor defect logs, never unwinds the port that owns this daemon.
            LogRunLoopFault(ex, portId);
        }
        finally
        {
            // Belt-and-braces: the run loop has ended, so no child of this run is signalable.
            lock (childGate)
            {
                childPid = -1;
            }
        }
    }

    /// <summary>The pinned rigctld argument contract:
    /// <c>-m &lt;model&gt; -r &lt;device&gt; [-s &lt;serialSpeed&gt;] -T 127.0.0.1 -t &lt;port&gt;</c>.</summary>
    private List<string> BuildArgs()
    {
        var args = new List<string>
        {
            "-m", config.Model!.Value.ToString(CultureInfo.InvariantCulture),
            "-r", config.Device!,
        };
        if (config.SerialSpeed is { } speed)
        {
            args.Add("-s");
            args.Add(speed.ToString(CultureInfo.InvariantCulture));
        }
        args.Add("-T");
        args.Add("127.0.0.1");
        args.Add("-t");
        args.Add(Port.ToString(CultureInfo.InvariantCulture));
        return args;
    }

    /// <summary>Fail a spawn attempt up-front when the binary clearly cannot launch: under the
    /// <c>setsid</c> wrapper a missing target would otherwise "start" fine (setsid itself
    /// launches) and immediately exit — indistinguishable from a crash, so the missing-rigctld
    /// case would burn the caller's whole readiness budget instead of failing it fast. A path
    /// (contains a separator) must exist; a bare name must resolve somewhere on <c>PATH</c>.</summary>
    private static void EnsureLaunchable(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            if (!File.Exists(command))
            {
                throw new FileNotFoundException($"no such binary: {command}", command);
            }
            return;
        }
        var onPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, command))) ?? false;
        if (!onPath)
        {
            throw new FileNotFoundException(
                $"'{command}' was not found on PATH (apt install libhamlib-utils).", command);
        }
    }

    /// <summary>Graceful stop: SIGTERM the group (or the child, when no group was available)
    /// → grace → SIGKILL the group + kill the tree. The mechanics live in
    /// <see cref="Hosting.ProcessSupervision"/>; only the log line is ours.</summary>
    private async Task GracefulStopAsync(Process process, bool groupLeader)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;   // already torn down
        }

        var outcome = await ProcessSupervision
            .GracefulStopAsync(process, groupLeader, stopGrace, clock).ConfigureAwait(false);
        switch (outcome)
        {
            case ProcessSupervision.StopOutcome.Terminated:
                LogStopped(portId, pid);
                break;
            case ProcessSupervision.StopOutcome.Killed:
                LogKilled(portId, pid);
                break;
            default:
                break;   // already exited
        }
    }

    /// <summary>The shared post-exit cleanup: drain the stdout/stderr pumps (BOUNDED, escalating
    /// to a group SIGKILL when a grandchild is holding the pipes open - C076), then dispose the
    /// process handle.</summary>
    private async Task CleanupChildAsync(
        Process process, Task pumps, CancellationTokenSource pumpStop, int pid, bool groupLeader)
    {
        var drained = await ProcessSupervision.DrainPumpsAsync(
            pumps, pumpStop, pid, groupLeader, ProcessSupervision.DefaultDrainGrace, clock).ConfigureAwait(false);
        if (!drained)
        {
            LogDrainAbandoned(portId, pid);
        }
        process.Dispose();
    }

    private void PublishChildPid(int pid)
    {
        lock (childGate)
        {
            childPid = pid;
        }
    }

    private void ClearChildPid(int pid)
    {
        lock (childGate)
        {
            if (childPid == pid)
            {
                childPid = -1;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await runLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The run loop is total; belt-and-braces.
            LogRunLoopFault(ex, portId);
        }
        stopping.Dispose();
    }

    /// <summary>The daemon's own output, logged under the <c>rigctld:&lt;portId&gt;</c>
    /// category: stderr at Warning, stdout at Debug (rigctld chatters).</summary>
    private static partial class DaemonLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Line}")]
        public static partial void Stderr(ILogger logger, string line);

        [LoggerMessage(EventId = 2, Level = LogLevel.Debug, Message = "{Line}")]
        public static partial void Stdout(ILogger logger, string line);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Port {Id}: node-managed rigctld started: {Command} (pid {Pid}) listening on 127.0.0.1:{Port}.")]
    private partial void LogStarted(string id, string command, int pid, int port);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Port {Id}: node-managed rigctld failed to launch ({Command}): {Reason}")]
    private partial void LogSpawnFailed(string id, string command, string reason);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Port {Id}: node-managed rigctld exited (code {ExitCode}); respawning in {Seconds}s (same port - clients recover on their next dial).")]
    private partial void LogExited(string id, int exitCode, double seconds);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Port {Id}: node-managed rigctld (pid {Pid}) stopped on SIGTERM.")]
    private partial void LogStopped(string id, int pid);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Port {Id}: node-managed rigctld (pid {Pid}) ignored SIGTERM; killed its process tree.")]
    private partial void LogKilled(string id, int pid);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Port {Id}: node-managed rigctld (pid {Pid}) exited but its output pipes stayed open (a background grandchild still holds them); abandoning the log tail.")]
    private partial void LogDrainAbandoned(string id, int pid);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id}: node-managed rigctld supervisor faulted.")]
    private partial void LogRunLoopFault(Exception ex, string id);
}
