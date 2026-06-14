using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Core.Tailscale;

/// <summary>
/// Supervises the embedded Tailscale <c>tsnet</c> Go sidecar (<c>packetnet-tsnet</c>) — pdn's
/// blessed remote + passkey path (<c>docs/network-access.md</c> § The sidecar). <b>Infra, not
/// an app</b>: it never appears in the apps inventory. It subscribes to
/// <see cref="IConfigProvider.OnChange"/> and reconciles the single child against
/// <c>tailscale.*</c> — enable launches it, disable stops it, a relevant-field change restarts
/// it — pumping the child's JSON status lines (stdout) into <see cref="ITailscaleStatus"/> and
/// its logs (stderr) into the node log, restarting on failure with exponential backoff.
/// </summary>
/// <remarks>
/// <para>
/// The spawn / teardown discipline mirrors
/// <see cref="Applications.Packages.AppServiceSupervisor"/>: on Linux the child is a
/// process-group leader (via <c>setsid</c>) so a stop SIGTERMs the <b>whole group</b>; the
/// graceful stop is SIGTERM → grace period → SIGKILL the group + kill the tree; restart backoff
/// doubles per consecutive failure, capped. Every timer-shaped wait rides the injected
/// <see cref="TimeProvider"/> (FakeTimeProvider in tests). The surface is <b>total</b>: a
/// missing binary, a spawn failure, or any defect sets <see cref="ITailscaleStatus"/> to
/// <c>error</c>, logs, and backs off — it never throws out of the run loop or crashes the node.
/// </para>
/// <para>
/// <b>The pinned sidecar contract.</b> The binary lives at
/// <see cref="DefaultBinaryPath"/> (override via the <see cref="BinaryPathEnvVar"/> env var,
/// e.g. for tests). Flags: <c>--hostname</c>, <c>--state-dir</c>, <c>--target</c>,
/// <c>--authkey-file &lt;path&gt;</c> (when an auth key is configured — an inline
/// <c>tailscale.authKey</c> is written to a temp 0600 file; an <c>authKeyFile</c> is passed
/// through directly), and <c>--funnel</c> (when <c>tailscale.funnel</c>). The child writes one
/// JSON status object per line to stdout — <c>{"state":"starting"}</c>,
/// <c>{"state":"needs-login","authURL":"…"}</c>,
/// <c>{"state":"running","fqdn":"pdn.&lt;tailnet&gt;.ts.net"}</c>, <c>{"state":"error","error":"…"}</c>
/// — logs to stderr, and exits 0 on SIGTERM.
/// </para>
/// </remarks>
public sealed partial class TailscaleSidecarHostedService : BackgroundService, IAsyncDisposable
{
    /// <summary>The packaged binary path (staged into the <c>.deb</c> beside the self-update
    /// helpers — <c>docs/network-access.md</c> § Packaging).</summary>
    public const string DefaultBinaryPath = "/usr/lib/packetnet/packetnet-tsnet";

    /// <summary>Env var overriding <see cref="DefaultBinaryPath"/> (tests point this at a fake
    /// sidecar script).</summary>
    public const string BinaryPathEnvVar = "PDN_TSNET_BIN";

    /// <summary>Backoff doubles per consecutive failed start, capped here.</summary>
    private static readonly TimeSpan BackoffCap = TimeSpan.FromSeconds(60);

    private const int Sigterm = 15;
    private const int Sigkill = 9;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // setsid(1) execs the target in-place when the child is not already a group leader (it never
    // is — just forked from the node), so the tracked PID IS the child's PID and pid == pgid:
    // kill(-pid, …) reaches the whole tree.
    private static readonly Lazy<string?> SetsidPath = new(() =>
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

    private readonly IConfigProvider config;
    private readonly ITailscaleStatus status;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<TailscaleSidecarHostedService> logger;
    private readonly string binaryPath;
    private readonly TimeSpan backoffBase;
    private readonly TimeSpan stopGrace;

    /// <summary>Serialises reconcile / stop / dispose so a config change can't race the run worker.</summary>
    private readonly SemaphoreSlim gate = new(1, 1);

    private IDisposable? changeSubscription;
    private CancellationTokenSource? lifetime;

    // The single live child run, and the config fingerprint it was launched for. Null = no child.
    private CancellationTokenSource? runStopping;
    private Task runLoop = Task.CompletedTask;
    private string? runFingerprint;
    private volatile bool disposed;

    public TailscaleSidecarHostedService(
        IConfigProvider config,
        ITailscaleStatus status,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        string? binaryPath = null,
        TimeSpan? backoffBase = null,
        TimeSpan? stopGrace = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.status = status ?? throw new ArgumentNullException(nameof(status));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<TailscaleSidecarHostedService>();
        // Explicit override (tests) → env var → packaged default.
        this.binaryPath = binaryPath
            ?? Environment.GetEnvironmentVariable(BinaryPathEnvVar)
            ?? DefaultBinaryPath;
        this.backoffBase = backoffBase ?? TimeSpan.FromSeconds(1);
        this.stopGrace = stopGrace ?? TimeSpan.FromSeconds(5);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        // Bring the child up from the initial config, then reconcile on every change. The
        // subscription only requests a reconcile; the reconcile itself runs under the gate.
        await ReconcileAsync(lifetime.Token).ConfigureAwait(false);
        changeSubscription = config.OnChange(_ => RequestReconcile());

        try
        {
            // The hosted-service lifetime: wait for stop. The work is event-driven (OnChange).
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Node shutting down.
        }
        finally
        {
            changeSubscription?.Dispose();
            changeSubscription = null;
            await StopChildAsync().ConfigureAwait(false);
            status.Update(TailscaleStatusSnapshot.Disabled);
        }
    }

    private void RequestReconcile()
    {
        var ct = lifetime?.Token ?? CancellationToken.None;
        // Fire-and-forget on the thread pool: OnChange fires off the editing/watcher path and
        // must not block it. The reconcile is gate-serialised, so concurrent changes queue.
        _ = Task.Run(() => ReconcileSafelyAsync(ct), CancellationToken.None);
    }

    private async Task ReconcileSafelyAsync(CancellationToken ct)
    {
        try
        {
            await ReconcileAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
        catch (Exception ex)
        {
            // Total: a reconcile defect must never escape onto the thread pool.
            LogReconcileFault(ex);
        }
    }

    /// <summary>Idempotent desired-vs-running reconcile. Enable starts the child, disable stops
    /// it, a relevant-field change (the fingerprint) restarts it, an unchanged spawn is left
    /// alone. Serialised by the gate.</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (disposed)
        {
            return;
        }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ts = config.Current.Tailscale;

            if (!ts.Enabled)
            {
                if (runStopping is not null)
                {
                    LogDisabling();
                    await StopChildLockedAsync().ConfigureAwait(false);
                }
                status.Update(TailscaleStatusSnapshot.Disabled);
                return;
            }

            var fingerprint = FingerprintOf(ts);
            if (runStopping is not null && string.Equals(runFingerprint, fingerprint, StringComparison.Ordinal))
            {
                // Running with the same spawn-relevant config — leave it alone.
                return;
            }
            if (runStopping is not null)
            {
                LogReconfiguring();
                await StopChildLockedAsync().ConfigureAwait(false);
            }

            StartChildLocked(ts, fingerprint, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The spawn-relevant config (everything passed to the child as a flag or file).
    /// An edit that changes this restarts the child; an irrelevant edit leaves it alone.</summary>
    private static string FingerprintOf(TailscaleConfig ts) =>
        string.Join(' ',
            ts.Hostname,
            ts.StateDir,
            ts.Target,
            ts.Funnel ? "funnel" : "",
            ts.AuthKey ?? "",
            ts.AuthKeyFile ?? "");

    // ---- lifecycle ------------------------------------------------------------------------

    /// <summary>Launch the child run loop for the given config. Caller holds the gate.</summary>
    private void StartChildLocked(TailscaleConfig ts, string fingerprint, CancellationToken parentCt)
    {
        var stopping = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        runStopping = stopping;
        runFingerprint = fingerprint;
        status.Update(new TailscaleStatusSnapshot(
            Enabled: true, State: "starting", Fqdn: null, AuthUrl: null, Error: null, Funnel: ts.Funnel));
        runLoop = Task.Run(() => RunChildAsync(ts, stopping.Token), CancellationToken.None);
    }

    /// <summary>Signal the live child to stop, await its run loop, untrack it. Caller holds the
    /// gate.</summary>
    private async Task StopChildLockedAsync()
    {
        var stopping = runStopping;
        var loop = runLoop;
        runStopping = null;
        runFingerprint = null;
        runLoop = Task.CompletedTask;
        if (stopping is null)
        {
            return;
        }
        await stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The run loop is total; belt-and-braces.
            LogRunLoopFault(ex);
        }
        stopping.Dispose();
    }

    /// <summary>Stop the live child (acquires the gate). Used by shutdown/dispose.</summary>
    private async Task StopChildAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopChildLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The child's whole supervised life: spawn → pump status/logs → exit → backoff →
    /// respawn, until stopped. Total — a defect faults the status, never the node.</summary>
    private async Task RunChildAsync(TailscaleConfig ts, CancellationToken ct)
    {
        var delay = backoffBase;
        var childLogger = loggerFactory.CreateLogger("tailscale");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Each attempt sets "starting" (a backoff retry resets the visible state too).
                status.Update(new TailscaleStatusSnapshot(
                    Enabled: true, State: "starting", Fqdn: null, AuthUrl: null, Error: null, Funnel: ts.Funnel));

                string? tempKeyFile = null;
                Process? process = null;
                string? spawnError = null;
                var groupLeader = false;
                try
                {
                    var (args, keyFile) = BuildArgs(ts);
                    tempKeyFile = keyFile;
                    (process, groupLeader) = Spawn(binaryPath, args);
                }
                catch (Exception ex)
                {
                    spawnError = ex.Message;
                }

                if (process is null)
                {
                    DeleteTempKey(tempKeyFile);
                    LogSpawnFailed(binaryPath, spawnError ?? "unknown error");
                    status.Update(new TailscaleStatusSnapshot(
                        Enabled: true, State: "error", Fqdn: null, AuthUrl: null,
                        Error: $"could not launch the Tailscale sidecar ({binaryPath}): {spawnError}",
                        Funnel: ts.Funnel));
                }
                else
                {
                    LogStarted(binaryPath, process.Id);
                    var pumps = Task.WhenAll(
                        PumpStdoutAsync(process.StandardOutput, ts.Funnel),
                        PumpAsync(process.StandardError, line => SidecarLog.Stderr(childLogger, line)));
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
                        // Stop requested (disable / reconfigure / shutdown): graceful teardown.
                        await GracefulStopAsync(process, groupLeader).ConfigureAwait(false);
                        await SwallowAsync(pumps).ConfigureAwait(false);
                        process.Dispose();
                        DeleteTempKey(tempKeyFile);
                        return;
                    }

                    var exitCode = process.ExitCode;
                    await SwallowAsync(pumps).ConfigureAwait(false);
                    process.Dispose();
                    DeleteTempKey(tempKeyFile);
                    LogExited(exitCode);
                    // The child exited on its own (it should only exit on SIGTERM). Surface the
                    // unexpected exit as an error and back off into a respawn.
                    status.Update(new TailscaleStatusSnapshot(
                        Enabled: true, State: "error", Fqdn: null, AuthUrl: null,
                        Error: $"the Tailscale sidecar exited unexpectedly (code {exitCode}); retrying",
                        Funnel: ts.Funnel));
                }

                // Backoff before the next attempt (spawn failure or unexpected exit both retry).
                try
                {
                    await Task.Delay(delay, timeProvider, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = delay + delay > BackoffCap ? BackoffCap : delay + delay;
            }
        }
        catch (Exception ex)
        {
            // Total: a supervisor defect faults the status, never unwinds the node.
            LogRunLoopFault(ex);
            status.Update(new TailscaleStatusSnapshot(
                Enabled: true, State: "error", Fqdn: null, AuthUrl: null,
                Error: $"Tailscale supervisor fault: {ex.Message}", Funnel: ts.Funnel));
        }
    }

    // ---- the spawn ------------------------------------------------------------------------

    /// <summary>Build the child's argument list from config, returning the temp auth-key file
    /// path to delete after the child exits (null when none was written).</summary>
    private static (List<string> Args, string? TempKeyFile) BuildArgs(TailscaleConfig ts)
    {
        var args = new List<string>
        {
            "--hostname", ts.Hostname,
            "--state-dir", ts.StateDir,
            "--target", ts.Target,
        };

        string? tempKeyFile = null;
        if (!string.IsNullOrWhiteSpace(ts.AuthKeyFile))
        {
            // An on-disk key file is passed through directly (the operator owns its perms).
            args.Add("--authkey-file");
            args.Add(ts.AuthKeyFile!);
        }
        else if (!string.IsNullOrWhiteSpace(ts.AuthKey))
        {
            // An inline key is never put on the command line (it'd show in `ps`); write it to a
            // private 0600 temp file and hand the child the path. Deleted after the child exits.
            tempKeyFile = WriteTempKeyFile(ts.AuthKey!);
            args.Add("--authkey-file");
            args.Add(tempKeyFile);
        }

        if (ts.Funnel)
        {
            args.Add("--funnel");
        }
        return (args, tempKeyFile);
    }

    private static string WriteTempKeyFile(string key)
    {
        var path = Path.Combine(Path.GetTempPath(), "pdn-tsnet-authkey-" + Guid.NewGuid().ToString("N"));
        // Create 0600 BEFORE writing so the secret is never briefly world-readable.
        if (!OperatingSystem.IsWindows())
        {
            using (File.Create(path)) { }
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.WriteAllText(path, key);
        return path;
    }

    private static void DeleteTempKey(string? path)
    {
        if (path is null)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort: a leftover 0600 temp file in TMP is harmless.
        }
    }

    /// <summary>Spawn the sidecar: stdout+stderr captured, and — where the platform allows — as
    /// a new process group (Linux <c>setsid</c>) so a stop can signal the whole tree.</summary>
    private static (Process Process, bool GroupLeader) Spawn(string command, IReadOnlyList<string> args)
    {
        var setsid = SetsidPath.Value;
        var psi = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,           // no shell — args pass verbatim, no injection
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };
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

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null.");
        return (process, setsid is not null);
    }

    /// <summary>Graceful stop, mirroring the app supervisor: SIGTERM the group (or the child,
    /// when no group was available) → grace → SIGKILL the group + kill the tree.</summary>
    private async Task GracefulStopAsync(Process process, bool groupLeader)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }
            var pid = process.Id;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Race: already gone.
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _ = SysKill(groupLeader ? -pid : pid, Sigterm);
            using var grace = new CancellationTokenSource(stopGrace, timeProvider);
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                LogStopped(pid);
            }
            catch (OperationCanceledException)
            {
                // TERM ignored within the grace — kill the whole group, then the tree.
                LogKilled(pid);
                if (groupLeader)
                {
                    _ = SysKill(-pid, Sigkill);
                }
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Race: already gone.
                }
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Reaped elsewhere — nothing left to wait for.
                }
            }
        }
        catch (InvalidOperationException)
        {
            // No process associated (already torn down) — nothing to do.
        }
    }

    // ---- status pumps ---------------------------------------------------------------------

    /// <summary>Read the child's stdout line by line; each line is one JSON status object —
    /// parse it and update <see cref="ITailscaleStatus"/>. A malformed line is logged and
    /// ignored (never disturbs supervision). Carries <paramref name="funnel"/> through so the
    /// snapshot reflects the configured exposure.</summary>
    private async Task PumpStdoutAsync(StreamReader reader, bool funnel)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                if (TryParseStatus(line, funnel, out var snapshot))
                {
                    status.Update(snapshot);
                    LogStatus(snapshot.State, snapshot.Fqdn, snapshot.AuthUrl);
                    if (snapshot.Error is not null)
                    {
                        // Surface the sidecar's own reason (e.g. a sandboxed AF_NETLINK
                        // failure) so an error state is diagnosable without digging stderr.
                        LogStatusError(snapshot.Error);
                    }
                }
                else
                {
                    LogUnparseableStatus(line);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort status capture only — never disturbs supervision.
        }
    }

    private static bool TryParseStatus(string line, bool funnel, out TailscaleStatusSnapshot snapshot)
    {
        snapshot = TailscaleStatusSnapshot.Disabled;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("state", out var stateEl)
                || stateEl.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var state = stateEl.GetString()!;
            var fqdn = ReadString(root, "fqdn");
            // Accept both authURL (the pinned contract) and authUrl (defensive).
            var authUrl = ReadString(root, "authURL") ?? ReadString(root, "authUrl");
            var error = ReadString(root, "error");
            snapshot = new TailscaleStatusSnapshot(
                Enabled: true, State: state, Fqdn: fqdn, AuthUrl: authUrl, Error: error, Funnel: funnel);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static async Task PumpAsync(StreamReader reader, Action<string> sink)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Length > 0)
                {
                    sink(line);
                }
            }
        }
        catch (Exception)
        {
            // Best-effort log capture only — never disturbs supervision.
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Handled in-pump; ignore here.
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        changeSubscription?.Dispose();
        changeSubscription = null;
        await StopChildAsync().ConfigureAwait(false);
        lifetime?.Dispose();
        gate.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // Classic DllImport (not LibraryImport): the source-generated marshaller demands
    // AllowUnsafeBlocks project-wide, which this one int-only syscall does not justify.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);
#pragma warning restore SYSLIB1054

    /// <summary>The sidecar's stderr, logged at Warning under the <c>tailscale</c> category.</summary>
    private static partial class SidecarLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "{Line}")]
        public static partial void Stderr(ILogger logger, string line);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale sidecar started: {Command} (pid {Pid}).")]
    private partial void LogStarted(string command, int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailscale sidecar failed to launch ({Command}): {Reason}")]
    private partial void LogSpawnFailed(string command, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale sidecar exited (code {ExitCode}).")]
    private partial void LogExited(int exitCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale sidecar (pid {Pid}) stopped on SIGTERM.")]
    private partial void LogStopped(int pid);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailscale sidecar (pid {Pid}) ignored SIGTERM; killed its process tree.")]
    private partial void LogKilled(int pid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale status: state={State} fqdn={Fqdn} authUrl={AuthUrl}.")]
    private partial void LogStatus(string state, string? fqdn, string? authUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tailscale sidecar error: {Error}")]
    private partial void LogStatusError(string error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Tailscale sidecar emitted an unparseable status line: {Line}")]
    private partial void LogUnparseableStatus(string line);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale disabled; stopping the sidecar.")]
    private partial void LogDisabling();

    [LoggerMessage(Level = LogLevel.Information, Message = "Tailscale config changed; restarting the sidecar.")]
    private partial void LogReconfiguring();

    [LoggerMessage(Level = LogLevel.Error, Message = "Tailscale reconcile faulted.")]
    private partial void LogReconcileFault(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tailscale sidecar run loop faulted.")]
    private partial void LogRunLoopFault(Exception ex);
}
