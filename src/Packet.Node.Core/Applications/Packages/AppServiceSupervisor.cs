using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;

namespace Packet.Node.Core.Applications.Packages;

/// <summary>
/// The app-package service supervisor (<c>docs/app-packages.md</c> § Lifecycle): for every
/// enabled, error-free package whose manifest declares a <c>service:</c> block with
/// <c>managed: pdn</c>, pdn owns the daemon — start on enable / node start / config apply,
/// stop on disable / shutdown (SIGTERM the process group → grace → kill the tree, the same
/// discipline as <see cref="ExternalProcessApplication"/>'s teardown), restart per the
/// manifest policy with exponential backoff, and a crash-loop breaker into
/// <see cref="AppServiceState.Faulted"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReconcileAsync"/> is idempotent desired-vs-running and serialized internally, so
/// the hosted service can call it from startup, every config apply, and on demand without
/// racing itself. The desired spawn is fingerprinted (resolved command + args + working dir +
/// merged environment): a manifest or override edit that changes the spawn restarts the
/// service; an edit that doesn't leaves it alone. A <see cref="AppServiceState.Faulted"/>
/// service is only resurrected by <see cref="RestartAsync"/> or a reconcile whose desired
/// fingerprint <b>changed</b> — a plain re-reconcile never resurrects it.
/// </para>
/// <para>
/// On Linux each daemon is spawned as a process-group leader (via <c>setsid</c>, present on
/// every mainstream distro), so the graceful stop can SIGTERM the <b>whole group</b> — the
/// daemon and anything it forked. Where <c>setsid</c> is unavailable the direct child gets the
/// SIGTERM and the kill-tree fallback covers still-attached descendants. All timer-shaped
/// waits (backoff, stop grace, the crash-loop window) ride the injected
/// <see cref="TimeProvider"/> per the repo's §2.7 discipline.
/// </para>
/// </remarks>
public sealed partial class AppServiceSupervisor(
    IConfigProvider config,
    IAppPackageCatalog catalog,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    TimeSpan? backoffBase = null,
    TimeSpan? stopGrace = null) : IAppServiceSupervisor, IAsyncDisposable
{
    /// <summary>The crash-loop breaker's sliding window (the contract's 5 minutes).</summary>
    private static readonly TimeSpan CrashLoopWindow = TimeSpan.FromMinutes(5);

    /// <summary>Starts inside <see cref="CrashLoopWindow"/> that trip the breaker.</summary>
    private const int CrashLoopThreshold = 5;

    private readonly IConfigProvider config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly IAppPackageCatalog catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILoggerFactory loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly ILogger<AppServiceSupervisor> logger =
        (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<AppServiceSupervisor>();
    private readonly TimeSpan backoffBase = backoffBase ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan stopGrace = stopGrace ?? TimeSpan.FromSeconds(5);

    /// <summary>Serializes reconcile / restart / dispose — concurrent reconciles can't race.</summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Guards <see cref="services"/> and every entry's mutable status fields.</summary>
    private readonly object stateGate = new();

    private readonly Dictionary<string, ServiceEntry> services = new(StringComparer.Ordinal);
    private volatile bool disposed;

    /// <inheritdoc/>
    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = config.Current;
            var discovered = catalog.Discover(current);
            // The node is the callsign authority — resolve every enabled packet app's callsign
            // once per reconcile so the PDN_APP_CALLSIGN we inject is consistent across the set.
            var callsigns = AppCallsignResolver.Resolve(current, discovered);

            // Desired set: enabled, error-free, service block, managed by pdn.
            var desired = new Dictionary<string, (ServiceSpawnSpec Spec, string Fingerprint)>(StringComparer.Ordinal);
            foreach (var pkg in discovered)
            {
                if (!pkg.Enabled || pkg.Error is not null)
                {
                    continue;
                }
                if (pkg.Manifest?.Service is not { Managed: AppServiceManaged.Pdn })
                {
                    continue;
                }
                var spec = BuildSpec(pkg, current, callsigns);
                desired[pkg.Id] = (spec, FingerprintOf(spec));
            }

            // Stop the surplus: was enabled now disabled, package removed, now external/broken.
            List<ServiceEntry> tracked;
            lock (stateGate)
            {
                tracked = [.. services.Values];
            }
            foreach (var entry in tracked)
            {
                if (!desired.ContainsKey(entry.Id))
                {
                    LogStoppingSurplus(entry.Id);
                    await StopAndForgetAsync(entry).ConfigureAwait(false);
                }
            }

            // Start the missing; restart on a changed spawn fingerprint; leave matching alone.
            foreach (var (id, (spec, fingerprint)) in desired)
            {
                ServiceEntry? existing;
                lock (stateGate)
                {
                    services.TryGetValue(id, out existing);
                }
                if (existing is not null)
                {
                    if (string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        // Matching — leave alone. This includes a Faulted or cleanly-exited
                        // (OnFailure, exit 0) entry: a plain re-reconcile with an unchanged
                        // desired spawn never resurrects; RestartAsync / a fingerprint change /
                        // a disable-enable cycle are the deliberate ways back.
                        continue;
                    }
                    LogRestartingChanged(id);
                    await StopAndForgetAsync(existing).ConfigureAwait(false);
                }
                Start(id, spec, fingerprint);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RestartAsync(string id, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = config.Current;
            var discovered = catalog.Discover(current);
            var pkg = discovered.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Unknown app package '{id}'.");
            var service = pkg.Manifest?.Service
                ?? throw new InvalidOperationException($"App package '{id}' declares no service.");
            if (service.Managed == AppServiceManaged.External)
            {
                throw new InvalidOperationException(
                    $"Service '{id}' is owner-managed (managed: external) - pdn does not start or stop it.");
            }
            if (pkg.Error is not null)
            {
                throw new InvalidOperationException($"App package '{id}' is broken: {pkg.Error}");
            }
            if (!pkg.Enabled)
            {
                throw new InvalidOperationException($"App package '{id}' is disabled - enable it first.");
            }

            ServiceEntry? existing;
            lock (stateGate)
            {
                services.TryGetValue(id, out existing);
            }
            if (existing is not null)
            {
                await StopAndForgetAsync(existing).ConfigureAwait(false);
            }

            // A fresh entry by construction clears Faulted, the backoff ladder, and the
            // crash-loop window — this is the owner's way out of Faulted.
            var callsigns = AppCallsignResolver.Resolve(current, discovered);
            var spec = BuildSpec(pkg, current, callsigns);
            LogRestartRequested(id);
            Start(id, spec, FingerprintOf(spec));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<AppServiceStatus> Statuses
    {
        get
        {
            Dictionary<string, AppServiceStatus> live;
            lock (stateGate)
            {
                live = services.Values.ToDictionary(
                    e => e.Id,
                    e => new AppServiceStatus(e.Id, e.State, e.Pid, e.Detail),
                    StringComparer.Ordinal);
            }

            var result = new List<AppServiceStatus>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pkg in catalog.Discover(config.Current))
            {
                // Every discovered package with a service block. (An unreadable-manifest error
                // entry has no manifest at all — nothing to report a service for.)
                if (pkg.Manifest?.Service is not { } service || !seen.Add(pkg.Id))
                {
                    continue;
                }
                if (service.Managed == AppServiceManaged.External)
                {
                    result.Add(new AppServiceStatus(pkg.Id, AppServiceState.External));
                }
                else if (live.TryGetValue(pkg.Id, out var status))
                {
                    result.Add(status);
                }
                else
                {
                    // Managed but not tracked: disabled, broken, or simply never reconciled in.
                    result.Add(new AppServiceStatus(pkg.Id, AppServiceState.Stopped, Detail: pkg.Error));
                }
            }
            // Still-tracked services whose package vanished from discovery mid-flight (the next
            // reconcile stops them) keep reporting their live state.
            foreach (var (id, status) in live)
            {
                if (seen.Add(id))
                {
                    result.Add(status);
                }
            }
            return result;
        }
    }

    /// <summary>Node shutdown: stop everything cleanly (graceful TERM → grace → kill tree).</summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            List<ServiceEntry> all;
            lock (stateGate)
            {
                all = [.. services.Values];
            }
            foreach (var entry in all)
            {
                await StopAndForgetAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
        gate.Dispose();
    }

    // ---- the desired spawn -------------------------------------------------------------

    /// <summary>Resolve one desired package service to its concrete spawn: command/args against
    /// the package dir (the contract's path rule), working dir (?? state dir), and the merged
    /// environment overlay — PDN_APP_*, then PDN_RHP_* when the RHP server is on, then the
    /// manifest map, then the owner's override map (last wins).</summary>
    private static ServiceSpawnSpec BuildSpec(
        DiscoveredAppPackage pkg, NodeConfig current,
        IReadOnlyDictionary<string, AppCallsignResolver.ResolvedAppCallsign> callsigns)
    {
        var service = pkg.Manifest!.Service!;
        var command = AppPackagePaths.ResolveFile(service.Command, pkg.PackageDir);
        var args = service.Args.Select(a => AppPackagePaths.ResolveFile(a, pkg.PackageDir)).ToArray();
        var workingDirectory = string.IsNullOrWhiteSpace(service.WorkingDirectory)
            ? pkg.StateDir
            : AppPackagePaths.ResolveDirectory(service.WorkingDirectory!, pkg.PackageDir);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PDN_APP_ID"] = pkg.Id,
            ["PDN_APP_DIR"] = pkg.PackageDir,
            ["PDN_APP_STATE"] = pkg.StateDir,
            // The host node's identity. Kept for back-compat: an un-migrated app still
            // self-derives its callsign as an SSID of this (the convention: <nodecall>-N).
            ["PDN_NODE_CALLSIGN"] = current.Identity.Callsign,
        };
        // The node is the callsign authority: a migrated app binds the node-resolved
        // PDN_APP_CALLSIGN instead of deriving its own. Additive — PDN_NODE_CALLSIGN stays.
        if (callsigns.TryGetValue(pkg.Id, out var resolved))
        {
            environment["PDN_APP_CALLSIGN"] = resolved.Callsign.ToString();
        }
        if (!string.IsNullOrWhiteSpace(current.Identity.Alias))
        {
            environment["PDN_NODE_ALIAS"] = current.Identity.Alias!;
        }
        if (current.Rhp.Enabled)
        {
            environment["PDN_RHP_HOST"] = current.Rhp.Bind;
            environment["PDN_RHP_PORT"] = current.Rhp.Port.ToString(CultureInfo.InvariantCulture);
        }
        foreach (var (key, value) in service.Environment)
        {
            environment[key] = value;
        }
        if (pkg.Override is { } ov)
        {
            foreach (var (key, value) in ov.Environment)
            {
                environment[key] = value;
            }
        }

        return new ServiceSpawnSpec(command, args, workingDirectory, environment, pkg.StateDir, service.Restart);
    }

    /// <summary>A canonical string of everything that shapes the spawn — command, args, working
    /// dir, and the <b>effective</b> merged environment (key-sorted, so merge order that nets
    /// out identical doesn't read as a change). Equal fingerprint ⇒ leave the service alone;
    /// changed ⇒ stop-and-respawn on the next reconcile.</summary>
    private static string FingerprintOf(ServiceSpawnSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append("cmd").Append(spec.Command);
        foreach (var arg in spec.Args)
        {
            sb.Append("arg").Append(arg);
        }
        sb.Append("cwd").Append(spec.WorkingDirectory);
        foreach (var (key, value) in spec.Environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            sb.Append("env").Append(key).Append('=').Append(value);
        }
        return sb.ToString();
    }

    // ---- lifecycle ----------------------------------------------------------------------

    /// <summary>Create a fresh tracked entry and launch its run loop. Caller holds the gate.</summary>
    private void Start(string id, ServiceSpawnSpec spec, string fingerprint)
    {
        var entry = new ServiceEntry
        {
            Id = id,
            Fingerprint = fingerprint,
            Spec = spec,
            Stopping = new CancellationTokenSource(),
        };
        lock (stateGate)
        {
            services[id] = entry;
            entry.State = AppServiceState.Starting;
        }
        entry.Run = Task.Run(() => RunServiceAsync(entry), CancellationToken.None);
    }

    /// <summary>Signal an entry's run loop to stop (it owns the process and performs the
    /// graceful TERM → grace → kill-tree teardown), await it, and untrack it.</summary>
    private async Task StopAndForgetAsync(ServiceEntry entry)
    {
        await entry.Stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await entry.Run.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The run loop is total; belt-and-braces only.
            LogRunLoopFault(ex, entry.Id);
        }
        entry.Stopping.Dispose();
        lock (stateGate)
        {
            if (services.TryGetValue(entry.Id, out var tracked) && ReferenceEquals(tracked, entry))
            {
                services.Remove(entry.Id);
            }
        }
    }

    /// <summary>One service's whole supervised life: spawn → run → exit → policy →
    /// backoff/restart, with the crash-loop breaker, until stopped or done. Total — a defect
    /// here faults the one service, never the node.</summary>
    private async Task RunServiceAsync(ServiceEntry entry)
    {
        var ct = entry.Stopping.Token;
        var delay = backoffBase;
        var appLogger = loggerFactory.CreateLogger("app:" + entry.Id);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (stateGate)
                {
                    entry.StartTimes.Add(timeProvider.GetUtcNow());
                }
                SetState(entry, AppServiceState.Starting, pid: null, detail: null);

                Process? process = null;
                string? spawnError = null;
                var groupLeader = false;
                var startedAt = timeProvider.GetUtcNow();
                try
                {
                    (process, groupLeader) = Spawn(entry.Spec);
                }
                catch (Exception ex)
                {
                    spawnError = ex.Message;
                }

                string detail;
                bool failed;
                if (process is null)
                {
                    LogSpawnFailed(entry.Id, entry.Spec.Command, spawnError ?? "unknown error");
                    detail = $"spawn failed: {spawnError}";
                    failed = true;
                }
                else
                {
                    SetState(entry, AppServiceState.Running, process.Id, detail: null);
                    LogServiceStarted(entry.Id, entry.Spec.Command, process.Id);

                    // The pumps are cancellable and tied to this entry's stop (C076): a stop
                    // must be able to end log capture, not wait on a pipe nobody will close.
                    using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var pumps = Task.WhenAll(
                        ProcessSupervision.PumpAsync(process.StandardOutput, line => AppLog.Stdout(appLogger, line), pumpStop.Token),
                        ProcessSupervision.PumpAsync(process.StandardError, line => AppLog.Stderr(appLogger, line), pumpStop.Token));
                    // A daemon gets no stdin — closing the pipe reads as immediate EOF
                    // (equivalent to /dev/null for a well-behaved service).
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception)
                    {
                        // Already closed / broken pipe — irrelevant to supervision.
                    }

                    try
                    {
                        await process.WaitForExitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Stop requested (disable / shutdown / restart): graceful teardown. The
                        // stop SIGKILLs the group, which releases the pipes, so the drain here
                        // completes; it is bounded anyway.
                        var stoppedPid = SafePid(process);
                        await GracefulStopAsync(process, groupLeader, entry.Id).ConfigureAwait(false);
                        SetState(entry, AppServiceState.Stopped, pid: null, detail: "stopped");
                        await DrainPumpsAsync(pumps, pumpStop, process, stoppedPid, entry.Id).ConfigureAwait(false);
                        return;
                    }

                    var exitedPid = SafePid(process);
                    var exitCode = process.ExitCode;
                    LogServiceExited(entry.Id, exitCode);
                    detail = $"exited {exitCode}";
                    failed = exitCode != 0;

                    // State FIRST, then drain (C076). WaitForExitAsync returns when the DIRECT
                    // child exits, but a grandchild holding stdout/stderr means pipe EOF may
                    // never arrive; the entry must not sit in Running with a dead pid while we
                    // salvage the log tail, and the drain must not park the run loop, the
                    // reconcile gate and shutdown behind it.
                    SetState(entry, AppServiceState.Stopped, pid: null, detail);
                    await DrainPumpsAsync(pumps, pumpStop, process, exitedPid, entry.Id).ConfigureAwait(false);

                    // A run that lasted long enough counts as healthy: reset the backoff so one
                    // failure after hours of uptime is not punished at the crash-loop rate.
                    if (ProcessSupervision.WasHealthyRun(timeProvider.GetUtcNow() - startedAt))
                    {
                        delay = backoffBase;
                    }
                }

                var restart = entry.Spec.Restart switch
                {
                    AppServiceRestart.Never => false,
                    AppServiceRestart.Always => true,
                    _ => failed,   // OnFailure
                };
                if (!restart)
                {
                    SetState(entry, AppServiceState.Stopped, pid: null, detail);
                    return;
                }

                // Crash-loop breaker: too many starts inside the sliding window → Faulted, no
                // further restarts until RestartAsync or a changed-fingerprint reconcile.
                var now = timeProvider.GetUtcNow();
                bool tripped;
                lock (stateGate)
                {
                    entry.StartTimes.RemoveAll(t => now - t > CrashLoopWindow);
                    tripped = entry.StartTimes.Count >= CrashLoopThreshold;
                }
                if (tripped)
                {
                    var faultDetail =
                        $"crash loop: {CrashLoopThreshold} starts in {CrashLoopWindow.TotalMinutes:0} minutes (last: {detail})";
                    SetState(entry, AppServiceState.Faulted, pid: null, faultDetail);
                    LogServiceFaulted(entry.Id, faultDetail);
                    return;
                }

                SetState(entry, AppServiceState.Backoff, pid: null, detail);
                try
                {
                    await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    SetState(entry, AppServiceState.Stopped, pid: null, detail: "stopped");
                    return;
                }
                delay = ProcessSupervision.NextBackoff(delay);
            }
            SetState(entry, AppServiceState.Stopped, pid: null, detail: "stopped");
        }
        catch (Exception ex)
        {
            // Total: a supervisor defect must fault the one service, never unwind the node.
            LogRunLoopFault(ex, entry.Id);
            SetState(entry, AppServiceState.Faulted, pid: null, $"supervisor fault: {ex.Message}");
        }
    }

    /// <summary>Spawn the service: state dir ensured, then the shared group-leader spawn
    /// (<see cref="ProcessSupervision.Spawn"/>) with this service's working directory and
    /// environment overlay.</summary>
    private static (Process Process, bool GroupLeader) Spawn(ServiceSpawnSpec spec)
    {
        Directory.CreateDirectory(spec.StateDir);
        return ProcessSupervision.Spawn(spec.Command, spec.Args, spec.WorkingDirectory, spec.Environment);
    }

    /// <summary>Graceful stop, mirroring <see cref="ExternalProcessApplication"/>'s teardown
    /// discipline with SIGTERM in place of stdin-EOF: TERM the process group (or the child,
    /// when no group was available) → the grace period → SIGKILL the group + kill the tree.
    /// The mechanics live in <see cref="ProcessSupervision"/>; only the log line is ours.</summary>
    private async Task GracefulStopAsync(Process process, bool groupLeader, string id)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return;   // no process associated (already torn down)
        }

        var outcome = await ProcessSupervision
            .GracefulStopAsync(process, groupLeader, stopGrace, timeProvider).ConfigureAwait(false);
        switch (outcome)
        {
            case ProcessSupervision.StopOutcome.Terminated:
                LogServiceStopped(id, pid);
                break;
            case ProcessSupervision.StopOutcome.Killed:
                LogServiceKilled(id, pid);
                break;
            default:
                break;   // already exited: nothing to report
        }
    }

    /// <summary>Bounded post-exit drain (C076): salvage the log tail, then close our ends of the
    /// pipes and give up rather than park the supervisor. Disposes the process handle. A lost tail
    /// is logged; it is never a reason to stop supervising.</summary>
    private async Task DrainPumpsAsync(
        Task pumps, CancellationTokenSource pumpStop, Process process, int pid, string id)
    {
        var drained = await ProcessSupervision.DrainPumpsAsync(
            pumps, pumpStop, process, ProcessSupervision.DefaultDrainGrace, timeProvider).ConfigureAwait(false);
        if (!drained)
        {
            LogDrainAbandoned(id, pid);
        }
    }

    /// <summary>The child's pid, or 0 once the handle no longer has one. Only ever used for a log
    /// line - never to signal, because a reaped pid can already belong to something else.</summary>
    private static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private void SetState(ServiceEntry entry, AppServiceState state, int? pid, string? detail)
    {
        lock (stateGate)
        {
            entry.State = state;
            entry.Pid = pid;
            entry.Detail = detail;
        }
    }

    /// <summary>One tracked managed service: the desired spawn it was started for, the run-loop
    /// task that owns its process, and its mutable status (guarded by the supervisor's state
    /// gate).</summary>
    private sealed class ServiceEntry
    {
        public required string Id { get; init; }
        public required string Fingerprint { get; init; }
        public required ServiceSpawnSpec Spec { get; init; }
        public required CancellationTokenSource Stopping { get; init; }
        public Task Run { get; set; } = Task.CompletedTask;

        public AppServiceState State { get; set; } = AppServiceState.Stopped;
        public int? Pid { get; set; }
        public string? Detail { get; set; }

        /// <summary>Start instants inside the crash-loop sliding window (pruned as it slides).</summary>
        public List<DateTimeOffset> StartTimes { get; } = [];
    }

    /// <summary>The fully-resolved spawn for one desired service — what the fingerprint hashes
    /// and the spawn executes. <see cref="Environment"/> is the overlay applied over the
    /// inherited environment (already merged manifest-then-override).</summary>
    private sealed record ServiceSpawnSpec(
        string Command,
        IReadOnlyList<string> Args,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment,
        string StateDir,
        AppServiceRestart Restart);

    /// <summary>The per-app log category (<c>app:&lt;id&gt;</c>): the daemon's stdout at
    /// Information, stderr at Warning.</summary>
    private static partial class AppLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "{Line}")]
        public static partial void Stdout(ILogger logger, string line);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "{Line}")]
        public static partial void Stderr(ILogger logger, string line);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "App service '{Id}' started: {Command} (pid {Pid}).")]
    private partial void LogServiceStarted(string id, string command, int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "App service '{Id}' exited (code {ExitCode}).")]
    private partial void LogServiceExited(string id, int exitCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App service '{Id}' failed to spawn ({Command}): {Reason}")]
    private partial void LogSpawnFailed(string id, string command, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "App service '{Id}' (pid {Pid}) stopped on SIGTERM.")]
    private partial void LogServiceStopped(string id, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App service '{Id}' (pid {Pid}) ignored SIGTERM; killed its process tree.")]
    private partial void LogServiceKilled(string id, int pid);

    [LoggerMessage(Level = LogLevel.Error, Message = "App service '{Id}' tripped the crash-loop breaker: {Detail}")]
    private partial void LogServiceFaulted(string id, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "App service '{Id}' is no longer desired; stopping it.")]
    private partial void LogStoppingSurplus(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "App service '{Id}' spawn changed; restarting it.")]
    private partial void LogRestartingChanged(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "App service '{Id}' restart requested.")]
    private partial void LogRestartRequested(string id);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "App service '{Id}' (pid {Pid}) exited but its output pipes stayed open (a background grandchild still holds them); abandoning the log tail.")]
    private partial void LogDrainAbandoned(string id, int pid);

    [LoggerMessage(Level = LogLevel.Error, Message = "App service '{Id}' supervision loop faulted.")]
    private partial void LogRunLoopFault(Exception ex, string id);
}
