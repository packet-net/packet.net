using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Beacons;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Telemetry;
using Packet.Node.Core.Transports;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// The node's hosted service: the long-running <see cref="BackgroundService"/>
/// that ties everything together. It owns the <see cref="PortSupervisor"/> (the
/// AX.25 port set) and the <see cref="TelnetConsoleListener"/> (local dial-in),
/// and it is the <b>sole</b> subscriber to <see cref="IConfigProvider.OnChange"/>:
/// every config change funnels through here, is turned into a
/// <see cref="ReconcilePlan"/>, and is applied on a single reconcile worker so no
/// two reconciles overlap (pending requests coalesce).
/// </summary>
public sealed partial class NodeHostedService : BackgroundService
{
    private readonly IConfigProvider config;
    private readonly ITransportFactory transportFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<NodeHostedService> logger;
    private readonly INetRomRoutingStore? routingStore;
    private readonly NodeTelemetry telemetry;
    private readonly BeaconService beacons;
    // Optional over-RF sysop dependencies (DI passes the registered user store + TOTP
    // verifier). Null in older tests / a node without them — the console then has no SYSOP
    // capability (the default-off contract). The SysopContext is assembled once at start.
    private readonly IUserStore? userStore;
    private readonly TotpService? totp;
    private SysopContext? sysopContext;
    private readonly SemaphoreSlim reconcileSignal = new(0);
    // Serialises supervisor mutation: the reconcile worker AND any web-initiated
    // action (port restart, session connect/disconnect/send) acquire this, so an
    // action can never race a config reconcile (or another action) touching the
    // live port set — honouring PortSupervisor's single-threaded-by-contract rule.
    private readonly SemaphoreSlim supervisorGate = new(1, 1);
    private readonly object swapGate = new();

    private PortSupervisor? supervisor;
    private TelnetConsoleListener? telnet;
    private NetRomService? netRom;
    private IDisposable? changeSubscription;
    private NodeConfig appliedConfig;
    private int pendingReconcile;

    public NodeHostedService(
        IConfigProvider config,
        ITransportFactory? transportFactory = null,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        INetRomRoutingStore? routingStore = null,
        BeaconService? beacons = null,
        IUserStore? userStore = null,
        TotpService? totp = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.transportFactory = transportFactory ?? TransportFactory.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.routingStore = routingStore;
        this.userStore = userStore;
        this.totp = totp;
        telemetry = new NodeTelemetry(this.loggerFactory.CreateLogger<NodeTelemetry>());
        // The ID-beacon service. Optional ctor param (DI passes the registered singleton);
        // when null — every existing direct-construction test — we build one over the same
        // config + clock, so beacons are always wired but inert by default (default-off).
        this.beacons = beacons ?? new BeaconService(
            this.config, this.timeProvider, this.loggerFactory.CreateLogger<BeaconService>());
        logger = this.loggerFactory.CreateLogger<NodeHostedService>();
        appliedConfig = config.Current;
    }

    /// <summary>The port supervisor — exposed for component tests.</summary>
    public PortSupervisor? Supervisor => supervisor;

    /// <summary>The NET/ROM read-only routing service — exposed for component tests
    /// (and any future read surface). Null until <see cref="ExecuteAsync"/> runs.</summary>
    public NetRomService? NetRom => netRom;

    /// <summary>The live frame/byte telemetry (frame counters + the monitor SSE
    /// feed). Created in the constructor so it's never null — the read API + the
    /// <c>/events</c> endpoint read it straight off this singleton.</summary>
    public NodeTelemetry Telemetry => telemetry;

    /// <summary>The ID-beacon service (per-port periodic UI-frame beacon). Created in
    /// the constructor so it's never null; inert until a port whose effective beacon is
    /// enabled comes up. Exposed for component tests.</summary>
    public BeaconService Beacons => beacons;

    /// <summary>The telnet listener — exposed for component tests (e.g. to read
    /// the bound port).</summary>
    public TelnetConsoleListener? Telnet => telnet;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var startConfig = config.Current;
        LogStarting(startConfig.Identity.Callsign, startConfig.Ports.Count);

        // The NET/ROM read-only service is created once at start from the initial
        // config. It's a pure consumer of each port's frame-trace tap (subscribed
        // by the supervisor as ports come up), so it can never disturb a session.
        netRom = new NetRomService(startConfig.NetRom, timeProvider, loggerFactory.CreateLogger<NetRomService>(), routingStore);

        // Assemble the over-RF sysop context before the supervisor so its per-connection
        // consoles (AX.25 + NET/ROM) can serve SYSOP; the telnet factory reads the same field.
        sysopContext = BuildSysopContext();

        supervisor = new PortSupervisor(config, transportFactory, timeProvider, loggerFactory, netRom, telemetry, beacons, sysopContext);
        await supervisor.StartAsync(stoppingToken).ConfigureAwait(false);

        StartTelnet(startConfig.Management.Telnet, stoppingToken);

        lock (swapGate) appliedConfig = startConfig;

        // Subscribe AFTER the initial bring-up so the first reconcile diffs
        // against the started state, not a phantom empty baseline.
        changeSubscription = config.OnChange(_ => RequestReconcile());

        try
        {
            await ReconcileLoopAsync(stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            changeSubscription?.Dispose();
            changeSubscription = null;
            // Stop all beacon timers before tearing the ports down (the timers send via
            // the listeners the supervisor is about to dispose).
            await beacons.DisposeAsync().ConfigureAwait(false);
            if (telnet is not null) await telnet.DisposeAsync().ConfigureAwait(false);
            // Dispose NET/ROM BEFORE the supervisor: DisposeAsync cleanly DISCs each
            // interlink AX.25 session (so a neighbour isn't left with a half-open link
            // it polls), and that DISC needs the ports' listeners still alive — the
            // supervisor disposes those, so it must run after.
            if (netRom is not null) await netRom.DisposeAsync().ConfigureAwait(false);
            if (supervisor is not null) await supervisor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void RequestReconcile()
    {
        // Coalesce: set the pending flag and release the worker once. If the
        // worker is mid-reconcile it will see the flag again and loop; multiple
        // rapid OnChange calls collapse into at most one extra pass.
        if (Interlocked.Exchange(ref pendingReconcile, 1) == 0)
        {
            reconcileSignal.Release();
        }
    }

    private async Task ReconcileLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await reconcileSignal.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            // Drain the pending flag — anything that arrives after this point
            // re-releases the semaphore for the next iteration.
            Interlocked.Exchange(ref pendingReconcile, 0);

            await ReconcileOnceAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run a single reconcile against the provider's current config. Exposed
    /// (internal) so component tests can drive a reconcile deterministically
    /// without racing the debounce/semaphore. Single-threaded by construction —
    /// the caller (the loop, or a test) never overlaps calls.
    /// </summary>
    internal async Task ReconcileOnceAsync(CancellationToken ct)
    {
        NodeConfig from;
        var to = config.Current;
        lock (swapGate) from = appliedConfig;

        var plan = ReconcilePlanner.Plan(from, to);
        if (plan.IsNoOp)
        {
            // A no-op for the PORT supervisor isn't necessarily a no-op for beacons: a
            // beacon-only edit (system default or a per-port override) leaves the port
            // set untouched, so the reconcile plan is empty — but the timers must re-arm
            // to the new interval/text/enabled. Re-arm from the live config (idempotent),
            // the same way the console reads ServicesConfig live.
            beacons.Reapply();
            lock (swapGate) appliedConfig = to;
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var summary = Describe(plan);
            LogReconciling(summary);
        }

        // Hold the supervisor gate for the mutation so a concurrent web action
        // (port restart / session connect-disconnect-send) can't touch the port
        // set mid-reconcile.
        await supervisorGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (supervisor is not null)
            {
                await supervisor.ApplyAsync(plan, to, ct).ConfigureAwait(false);
            }

            if (plan.TelnetChanged)
            {
                await RestartTelnetAsync(to.Management.Telnet, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            supervisorGate.Release();
        }

        // Services changes need no action — the console reads ServicesConfig live
        // through the provider; the reference swap is implicit.

        // Re-arm beacons from the new config. Ports brought up/restarted in the plan
        // already armed from live config on AttachPort; this catches beacon edits to
        // ports the plan left untouched (and is idempotent for the rest).
        beacons.Reapply();

        lock (swapGate) appliedConfig = to;
    }

    /// <summary>
    /// Run a supervisor/session action under the same single-threaded gate the
    /// reconcile worker uses, so a web-initiated action (port restart, session
    /// connect/disconnect/send) never races a config reconcile — or another action
    /// — mutating the live port set. <b>Keep the critical section short:</b> capture
    /// what you need (a listener reference, a session) and run long-running I/O (a
    /// connect dial that awaits SABM/UA) <em>outside</em> the delegate, because the
    /// gate blocks reconciles while it's held. The action runs even before
    /// <see cref="ExecuteAsync"/> has created the supervisor (it sees a null
    /// <see cref="Supervisor"/> then and should no-op), so callers null-check.
    /// </summary>
    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await supervisorGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            supervisorGate.Release();
        }
    }

    /// <summary>Void-returning overload of <see cref="RunExclusiveAsync{T}"/>.</summary>
    public async Task RunExclusiveAsync(Func<Task> action, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await RunExclusiveAsync<bool>(async () => { await action().ConfigureAwait(false); return true; }, ct)
            .ConfigureAwait(false);
    }

    private void StartTelnet(TelnetConfig telnetConfig, CancellationToken ct)
    {
        if (!telnetConfig.Enabled)
        {
            LogTelnetDisabled();
            return;
        }

        var listener = new TelnetConsoleListener(
            telnetConfig,
            BuildTelnetServiceFactory(),
            loggerFactory.CreateLogger<TelnetConsoleListener>());
        try
        {
            listener.StartAsync(ct).GetAwaiter().GetResult();
            telnet = listener;
        }
        catch (Exception ex)
        {
            // A telnet bind clash must not crash the node — log + run without it.
            LogTelnetFaulted(ex, telnetConfig.Bind, telnetConfig.Port);
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private async Task RestartTelnetAsync(TelnetConfig telnetConfig, CancellationToken ct)
    {
        if (telnet is not null)
        {
            await telnet.DisposeAsync().ConfigureAwait(false);
            telnet = null;
        }
        StartTelnet(telnetConfig, ct);
    }

    private Func<INodeConnection, NodeCommandService> BuildTelnetServiceFactory()
    {
        return _ =>
        {
            // Telnet's connect-out is same-port-only in spirit: it dials on a
            // running AX.25 port (the first one, deterministically). If no port is
            // up, Connect reports "not available". Resolved per session so it
            // reflects the live port set.
            var connector = supervisor?.ResolveDefaultConnector();
            var env = new NodeConsoleEnvironment(config, connector, netRom, sysopContext);
            return new NodeCommandService(env, loggerFactory.CreateLogger<NodeCommandService>(), timeProvider);
        };
    }

    // Assemble the over-RF sysop dependencies once at start: the user store + TOTP verifier
    // (DI-supplied) plus the host-side privileged operations (this host + config). Null when
    // either dependency is absent — the console then has no SYSOP capability, exactly as
    // before. Operations close over `this`, whose Supervisor is set immediately after.
    private SysopContext? BuildSysopContext() =>
        userStore is not null && totp is not null
            ? new SysopContext(userStore, totp, new HostSysopOperations(this, config))
            : null;

    private static string Describe(ReconcilePlan p)
    {
        if (p.NodeWideReset) return "node-wide reset (callsign changed)";
        var parts = new List<string>();
        if (p.ToBringUp.Count > 0) parts.Add($"+{p.ToBringUp.Count} up");
        if (p.ToEnable.Count > 0) parts.Add($"+{p.ToEnable.Count} enabled");
        if (p.ToTearDown.Count > 0) parts.Add($"-{p.ToTearDown.Count} removed");
        if (p.ToDisable.Count > 0) parts.Add($"-{p.ToDisable.Count} disabled");
        if (p.ToRestart.Count > 0) parts.Add($"{p.ToRestart.Count} restart");
        if (p.KissParamsChanged.Count > 0) parts.Add($"{p.KissParamsChanged.Count} kiss-live");
        if (p.Ax25ParamsChanged.Count > 0) parts.Add($"{p.Ax25ParamsChanged.Count} ax25-live");
        if (p.TelnetChanged) parts.Add("telnet restart");
        if (p.ServicesChanged) parts.Add("services swap");
        return parts.Count == 0 ? "(no-op)" : string.Join(", ", parts);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Node starting (callsign {Callsign}, {PortCount} configured port(s)).")]
    private partial void LogStarting(string callsign, int portCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciling config change: {Summary}.")]
    private partial void LogReconciling(string summary);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telnet console disabled by config.")]
    private partial void LogTelnetDisabled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Telnet console failed to bind {Bind}:{Port}; running without it.")]
    private partial void LogTelnetFaulted(Exception ex, string bind, int port);
}
