using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Applications;
using Packet.Node.Core.Beacons;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Kiss.NinoTnc;
using Packet.Node.Core.Console;
using Packet.Node.Core.HeadEnd;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Radios;
using Packet.Node.Core.Rigs;
using Packet.Rig;
using Packet.Node.Core.Telemetry;
using Packet.Node.Core.Transports;
using Packet.Radio;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// Owns the reconcilable set of AX.25 ports — exactly one
/// <see cref="Ax25Listener"/> per port — and executes a
/// <see cref="ReconcilePlan"/> against it, touching only what changed. This is
/// the "do" half of hot reconfiguration; <see cref="ReconcilePlanner"/> is the
/// "decide" half.
/// </summary>
/// <remarks>
/// <para>
/// When a listener accepts a session (inbound or, indirectly, the console's
/// outbound connect) the supervisor wires it to the node console by wrapping it
/// as an <see cref="Ax25NodeConnection"/> and running a
/// <see cref="NodeCommandService"/> over it — same-port connect-out available
/// via an <see cref="Ax25OutboundConnector"/> on the same listener.
/// </para>
/// <para>
/// A runtime fault bringing one port up (e.g. a serial device that won't open)
/// faults only that port — it is logged and skipped, the rest of the reconcile
/// completes, and <see cref="IConfigProvider.Current"/> still advances. This is
/// distinct from a whole-config validation failure, which is rejected pre-apply
/// by the provider and never reaches here.
/// </para>
/// </remarks>
public sealed partial class PortSupervisor : IAsyncDisposable, Applications.ILocalAppRegistry
{
    private readonly IConfigProvider config;
    private readonly ITransportFactory transportFactory;
    private readonly IRadioControlFactory radioFactory;
    private readonly IRigControlFactory rigFactory;
    private readonly RigTelemetry? rigTelemetry;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<PortSupervisor> logger;
    private readonly NetRomService? netRom;
    private readonly NodeTelemetry? telemetry;
    private readonly BeaconService? beacons;
    // Optional over-RF sysop context, threaded into each per-connection console env so an
    // AX.25 / NET-ROM operator can SYSOP-elevate. Null = no sysop capability (default-off).
    private readonly SysopContext? sysopContext;
    // Optional application launcher, threaded into each per-connection console env so an
    // inbound user can launch a registered app by its verb. Null = no app platform wired.
    private readonly IApplicationHost? applicationHost;
    // Optional per-peer AX.25 capability cache, threaded into every Ax25OutboundConnector
    // this supervisor constructs so a user CONNECT consults it for the dial version + XID
    // probe and records the outcome. Null = today's behaviour (each connector dials via the
    // listener defaults + records nothing). Interlinks consult the cache in NetRomService.
    private readonly PeerCapabilityCache? capabilityCache;
    private readonly HeadEnd.IHeadEndDiscovery? headEndDiscovery;
    // App callsigns the node answers for on behalf of an external program (the RHPv2 server's
    // `bind`): callsign → registration. Applied to running listeners as local aliases, re-applied
    // when a port (re)starts, and routed in OnSessionAccepted (an inbound session whose Local is
    // an app callsign goes to the registration's handler, never to the node console).
    private readonly Dictionary<Callsign, AppCallsignRegistration> appCallsigns = new();
    private readonly object appCallsignGate = new();
    private readonly Dictionary<string, RunningPort> ports = new(StringComparer.Ordinal);
    // Serialises port-set mutation between the caller-serialised paths (StartAsync / ApplyAsync /
    // RestartPortAsync — the host's supervisor gate already keeps THOSE from overlapping) and the
    // supervisor's own head-end bring-up retry loops (#576), which run on background timers and
    // would otherwise race a reconcile touching the same port set.
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    // One armed retry loop per head-end-bound port whose bring-up failed (#576): portId → its
    // cancellation. Guarded by its own lock; loops remove themselves when they succeed or abandon.
    private readonly Dictionary<string, CancellationTokenSource> bringUpRetries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Ax25Session, byte> consoleSessions = new();
    // Remotes a console connect-OUT is dialling right now (with a refcount, since
    // two console sessions could dial the same call). SessionAccepted for a remote
    // in here is the outbound session we just opened — NOT an inbound caller — so
    // we must not start a node console against it.
    private readonly Dictionary<Callsign, int> outboundInProgress = new();
    private readonly object outboundGate = new();
    private readonly CancellationTokenSource lifecycle = new();
    private int disposed;

    public PortSupervisor(
        IConfigProvider config,
        ITransportFactory transportFactory,
        TimeProvider timeProvider,
        ILoggerFactory? loggerFactory = null,
        NetRomService? netRom = null,
        NodeTelemetry? telemetry = null,
        BeaconService? beacons = null,
        SysopContext? sysopContext = null,
        IApplicationHost? applicationHost = null,
        PeerCapabilityCache? capabilityCache = null,
        IRadioControlFactory? radioFactory = null,
        HeadEnd.IHeadEndDiscovery? headEndDiscovery = null,
        IRigControlFactory? rigFactory = null,
        RigTelemetry? rigTelemetry = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        // Optional split-station discovery: when present, a head-end binding whose config address is
        // blank resolves its current host:port via mDNS at bring-up (keyed by instance id, so a
        // re-addressed Pi keeps its port configs). Null = config-address-only (a purely-local node).
        this.headEndDiscovery = headEndDiscovery;
        // Optional rig-control (CAT) seam: how a port's `rig:` block becomes a live
        // IRigControl. Defaults to the production factory (real daemons); component
        // tests substitute a scripted rig. The telemetry hub, when present, receives a
        // RigStatus after every poll tick (the /api/v1/rigs/events SSE feed).
        this.rigFactory = rigFactory ?? RigControlFactory.Instance;
        // Optional radio-control seam: how a port's `radio:` block becomes a live
        // IRadioControl. Defaults to the production factory (real serial hardware);
        // component tests substitute a scripted radio. The default is built OVER this
        // supervisor's rig seam, so a `radio: kind rig` port dials its dedicated rig
        // connection through the same factory the status poller uses — inject a fake
        // rig factory once and both arms are scripted.
        this.radioFactory = radioFactory ?? new RadioControlFactory(this.rigFactory);
        this.rigTelemetry = rigTelemetry;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        this.sysopContext = sysopContext;
        this.applicationHost = applicationHost;
        this.capabilityCache = capabilityCache;
        logger = this.loggerFactory.CreateLogger<PortSupervisor>();
        // Optional live telemetry: when present, each port that comes up has its
        // frame-trace tap subscribed (and unsubscribed on teardown) so the node's
        // frame/byte counters + monitor SSE feed see every frame. Observation-only,
        // like the NET/ROM tap — it can never disturb a session.
        this.telemetry = telemetry;
        // Optional ID-beacon service: when present, each port that comes up arms a
        // periodic beacon timer IF its effective beacon (per-port override merged over
        // the system default) is enabled — default-off, so a stock node never beacons.
        // It only ever SENDS a UI frame (never disturbs a session, never mutates the
        // port set), so it attaches alongside telemetry, outside the supervisor gate.
        this.beacons = beacons;
        // Optional node-level NET/ROM consumer. When present, each port that comes up
        // has its frame-trace tap subscribed (and unsubscribed on teardown) so the
        // service hears NODES broadcasts; with connect-routing enabled it also taps
        // interlink sessions + drives L4 circuits. Hearing can never disturb a
        // session — the frame tap is observation-only.
        this.netRom = netRom;

        // When NET/ROM connect-routing is on, an inbound L4 circuit (a user routed to
        // us across the network) is bridged to a fresh node console — the same prompt
        // an AX.25/telnet user gets. The service raises this hook with the circuit
        // wrapped as an INodeConnection.
        if (this.netRom is not null)
        {
            this.netRom.RunInboundConsole = RunNodeConsoleAsync;
            this.netRom.OpenInterlink = OpenInterlinkAsync;
        }
    }

    // Dial an interlink AX.25 session to a neighbour with the outbound claim held, so
    // OnSessionAccepted does NOT start a node console against the dialled neighbour
    // (an interlink is NET/ROM datagrams, not console text). Mirrors how
    // Ax25OutboundConnector claims a console connect-out. The service hands us the
    // PeerDialPlan it computed from the per-peer capability cache (version + pre-connect
    // XID); we just dial it. The default plan (no cache) is mod-8 + the listener's
    // pre-connect-XID default — byte-for-byte today's behaviour.
    private async Task<Ax25Session> OpenInterlinkAsync(
        string portId, Callsign neighbour, PeerDialPlan plan, CancellationToken ct)
    {
        RunningPort? port;
        lock (ports)
        {
            ports.TryGetValue(portId, out port);
        }

        var listener = port?.Listener
            ?? throw new InvalidOperationException($"NET/ROM interlink: port '{portId}' is not running.");

        using var ticket = ClaimOutbound(neighbour);
        return await listener
            .ConnectAsync(neighbour, listener.MyCall, plan.Extended, plan.PreConnectXid, ct)
            .ConfigureAwait(false);
    }

    // Run the node command service over an inbound connection (used for NET/ROM L4
    // circuits that reach our prompt). The dialling user can itself `connect`
    // onward, so the console gets a NET/ROM-routing connector with no AX.25
    // fallback (the local-channel dial doesn't apply to a network-arrived user).
    private async Task RunNodeConsoleAsync(INodeConnection connection, CancellationToken ct)
    {
        Callsign user = Callsign.TryParse(connection.PeerId, out var u) ? u : default;
        var connector = netRom is not null ? new Packet.Node.Core.NetRom.NetRomOutboundConnector(netRom, fallback: null, user) : null;
        var env = new NodeConsoleEnvironment(config, connector, netRom, sysopContext, applicationHost, CreateConnectRouter(connector), capabilityCache);
        var service = new NodeCommandService(env, loggerFactory.CreateLogger<NodeCommandService>(), timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifecycle.Token);
        await service.RunAsync(connection, linked.Token).ConfigureAwait(false);
    }

    // Wrap a same-port AX.25 connector with NET/ROM routing when connect-routing is
    // enabled; otherwise return the AX.25 connector unchanged.
    private IOutboundConnector? WrapWithNetRom(IOutboundConnector? ax25Connector, Callsign originatingUser)
    {
        if (netRom is { ConnectEnabled: true })
        {
            return new Packet.Node.Core.NetRom.NetRomOutboundConnector(netRom, ax25Connector, originatingUser);
        }
        return ax25Connector;
    }

    /// <summary>The ids of the ports currently up (for tests + the Nodes command
    /// cross-check). Snapshot; ordering not guaranteed.</summary>
    public IReadOnlyCollection<string> RunningPortIds
    {
        get { lock (ports)
            {
                return ports.Keys.ToArray();
            }
        }
    }

    /// <summary>Look up a running port by id (for tests).</summary>
    public RunningPort? GetPort(string id) => TryGetRunning(id);

    // Centralises the `lock (ports) { TryGetValue }` read so the running-port
    // synchronisation invariant lives in one place rather than being open-coded at
    // every lookup site. Returns null when no running port has that id (e.g. it is
    // disabled, faulted, or mid-restart).
    private RunningPort? TryGetRunning(string id)
    {
        lock (ports)
        {
            return ports.TryGetValue(id, out var p) ? p : null;
        }
    }

    // Build a head-end device resolver over the LIVE fleet. With discovery wired (split-station),
    // the headEndId → address step prefers a pinned config address and falls back to an mDNS browse
    // of the instance id — so a head-end configured in discover mode (blank address) or one that
    // re-addressed resolves at bring-up. Without discovery it is config-address-only (unchanged).
    private HeadEndDeviceResolver BuildHeadEndResolver()
    {
        var headEnds = config.Current.HeadEnds;
        HeadEnd.IHeadEndAddressResolver? addressResolver = headEndDiscovery is null
            ? null
            : new HeadEnd.HeadEndAddressResolver(headEnds, headEndDiscovery, loggerFactory);
        return new HeadEndDeviceResolver(headEnds, addressResolver: addressResolver, loggerFactory: loggerFactory);
    }

    // Reverse-resolve a listener to the id of the running port that owns it (for logging).
    // "?" when no running port matches — e.g. a SessionAccepted racing a teardown.
    private string PortIdFor(Ax25Listener listener)
    {
        lock (ports)
        {
            return ports.Values.FirstOrDefault(p => ReferenceEquals(p.Listener, listener))?.Id ?? "?";
        }
    }

    /// <summary>
    /// An outbound connector for the first running port (by id order), or null if
    /// no port is up. The telnet console uses this so a <c>Connect</c> from a
    /// local dial-in dials out on a real AX.25 port — slice-1 same-port-only in
    /// the sense that there is exactly one deterministic dial-out port.
    /// </summary>
    /// <summary>
    /// Resolve a same-port AX.25 connector for a <b>specific</b> running port (the RHPv2
    /// server's outbound <c>open</c> dials on the port the client named). Null when the port
    /// isn't running. The connector claims the dialled remote for the duration of the connect,
    /// exactly like the console's connect-out, so no inbound console is started against it.
    /// <paramref name="localOverride"/> originates the session from an application callsign
    /// instead of the node's own (the wire's <c>open.local</c>).
    /// </summary>
    public IOutboundConnector? ResolveConnector(string portId, Callsign? localOverride = null)
    {
        var port = TryGetRunning(portId);

        return port is null ? null : new Ax25OutboundConnector(port.Id, port.Listener, r => ClaimOutbound(r), localOverride, capabilityCache);
    }

    /// <summary>
    /// Build the connect router a console session uses for <c>C[onnect] [port] &lt;call&gt;</c>:
    /// it bridges to a locally-registered app SSID (loopback crossconnect), dials a chosen
    /// 1-indexed port directly, or — for a plain <c>C &lt;call&gt;</c> — returns
    /// <paramref name="defaultConnector"/> (the session's usual same-port / NET/ROM-wrapped
    /// dial). Resolves against the live config + app registry, so a port that comes up or an app
    /// that binds mid-session is reachable on the next command.
    /// </summary>
    public IConnectRouter CreateConnectRouter(IOutboundConnector? defaultConnector) =>
        new ConnectRouter(this, defaultConnector);

    // Look up a live app-callsign registration (the loopback-crossconnect target). Null when the
    // callsign isn't registered as a local app right now.
    private AppCallsignRegistration? FindAppRegistration(Callsign target)
    {
        lock (appCallsignGate)
        {
            return appCallsigns.TryGetValue(target, out var reg) ? reg : null;
        }
    }

    // ── ILocalAppRegistry — the live key set, for the bare-verb resolver (packet.net#476) ──
    // A self-deriving app binds an SSID it chose, not the node-resolved PDN_APP_CALLSIGN; the
    // verb resolver consults this to bridge to whatever the app actually bound. Read-only.

    /// <inheritdoc/>
    public bool IsRegistered(Callsign callsign)
    {
        lock (appCallsignGate)
        {
            return appCallsigns.ContainsKey(callsign);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<Callsign> RegisteredCallsigns()
    {
        lock (appCallsignGate)
        {
            return appCallsigns.Keys.ToArray();
        }
    }

    /// <summary>
    /// Build a loopback-crossconnect connector for <paramref name="target"/> if it is a callsign
    /// the node is locally registered for right now (an RHP-attached app on its own SSID), else
    /// null. "Connect to a local app SSID bridges in-process" defined once, for both consumers:
    /// the console connect router and the RHPv2 gateway's outbound <c>open</c>.
    /// <paramref name="callerPeerId"/>/<paramref name="callerKind"/> are what the target app sees
    /// as the connecting peer (the human who dialled, or the originating app's callsign).
    /// </summary>
    public IOutboundConnector? TryResolveLocalAppConnector(Callsign target, string callerPeerId, NodeTransportKind callerKind)
    {
        var registration = FindAppRegistration(target);
        if (registration is null)
        {
            return null;
        }
        var label = registration.PortId ?? "local";
        return new LocalAppConnector(registration.OnAccepted, callerPeerId, callerKind, label);
    }

    /// <summary>
    /// Register an application callsign the node answers for (the RHPv2 server's <c>bind</c>:
    /// "the RHP client tells us what callsigns we should answer for"). Running listeners on the
    /// matching port(s) gain it as a local alias immediately; ports that (re)start later have it
    /// re-applied. An inbound session addressed to it routes to <paramref name="onAccepted"/>
    /// (wrapped as an <see cref="INodeConnection"/>, with the arrival port id) instead of the
    /// node console. Dispose the returned registration to stop answering.
    /// </summary>
    /// <exception cref="InvalidOperationException">The callsign is already registered (one
    /// listener per callsign — the wire's "Duplicate socket"), or is the node's own.</exception>
    public IDisposable RegisterAppCallsign(Callsign local, string? portId, Func<INodeConnection, string, Task> onAccepted)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        Callsign nodeCall = Callsign.TryParse(config.Current.Identity.Callsign, out var nc) ? nc : default;
        if (local.Equals(nodeCall))
        {
            throw new InvalidOperationException("the node's own callsign is already in use (the node console listens on it).");
        }

        lock (appCallsignGate)
        {
            if (appCallsigns.ContainsKey(local))
            {
                throw new InvalidOperationException($"callsign {local} is already registered.");
            }
            appCallsigns[local] = new AppCallsignRegistration { Local = local, PortId = portId, OnAccepted = onAccepted };
        }

        // Alias the running listener(s) now; ports that come up later get it in BringUpAsync.
        foreach (var port in MatchingPorts(portId))
        {
            port.Listener.AddLocalAlias(local);
        }
        LogAppCallsignRegistered(local, portId ?? "*");
        return new AppCallsignUnsubscriber(this, local, portId);
    }

    private void UnregisterAppCallsign(Callsign local, string? portId)
    {
        lock (appCallsignGate)
        {
            if (!appCallsigns.Remove(local))
            {
                return;
            }
        }
        foreach (var port in MatchingPorts(portId))
        {
            port.Listener.RemoveLocalAlias(local);
        }
        LogAppCallsignUnregistered(local);
    }

    private List<RunningPort> MatchingPorts(string? portId)
    {
        lock (ports)
        {
            return ports.Values.Where(p => portId is null || string.Equals(p.Id, portId, StringComparison.Ordinal)).ToList();
        }
    }

    // Apply every live registration to a port that just came up (a reconciled/restarted port's
    // fresh listener must answer for the registered app callsigns too).
    private void ApplyAppCallsignsTo(RunningPort port)
    {
        List<AppCallsignRegistration> matching;
        lock (appCallsignGate)
        {
            matching = appCallsigns.Values
                .Where(r => r.PortId is null || string.Equals(r.PortId, port.Id, StringComparison.Ordinal))
                .ToList();
        }
        foreach (var r in matching)
        {
            port.Listener.AddLocalAlias(r.Local);
        }
    }

    private sealed class AppCallsignRegistration
    {
        public required Callsign Local { get; init; }
        public required string? PortId { get; init; }
        public required Func<INodeConnection, string, Task> OnAccepted { get; init; }
    }

    // The console's connect router (see CreateConnectRouter). Holds the supervisor + the session's
    // default connector; reads the live config/registry on each Resolve so it tracks port and app
    // changes within a session. NET/ROM is intentionally not consulted here — an explicit port is
    // a direct dial; aliases come later.
    private sealed class ConnectRouter(PortSupervisor owner, IOutboundConnector? defaultConnector) : IConnectRouter
    {
        public ConnectResolution Resolve(int? port, Callsign target, INodeConnection inbound)
        {
            // No port: a registered app SSID wins (loopback crossconnect to the app); otherwise
            // the session's default dial. An explicit port skips this — it's a deliberate "go RF".
            if (port is null)
            {
                var localApp = owner.TryResolveLocalAppConnector(target, inbound.PeerId, inbound.TransportKind);
                if (localApp is not null)
                {
                    return ConnectResolution.LocalApp(localApp);
                }

                return defaultConnector is not null
                    ? ConnectResolution.Dial(defaultConnector)
                    : ConnectResolution.Fail("Connect is not available on this connection (no outbound port configured).");
            }

            // Explicit port: 1-indexed config order (XRouter convention).
            var ports = owner.config.Current.Ports;
            if (port < 1 || port > ports.Count)
            {
                return ConnectResolution.Fail($"No such port {port} (1..{ports.Count}).");
            }

            var portId = ports[port.Value - 1].Id;
            var dial = owner.ResolveConnector(portId);
            return dial is not null
                ? ConnectResolution.Dial(dial)
                : ConnectResolution.Fail($"Port '{portId}' is not running.");
        }
    }

    private sealed class AppCallsignUnsubscriber(PortSupervisor owner, Callsign local, string? portId) : IDisposable
    {
        private int gone;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref gone, 1) == 0)
            {
                owner.UnregisterAppCallsign(local, portId);
            }
        }
    }

    public IOutboundConnector? ResolveDefaultConnector()
    {
        RunningPort? first;
        lock (ports)
        {
            first = ports.Values.OrderBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
        }
        var ax25 = first is null ? null : new Ax25OutboundConnector(first.Id, first.Listener, r => ClaimOutbound(r), localOverride: null, cache: capabilityCache);

        // A telnet dial-in has no callsign of its own; a NET/ROM-routed `connect`
        // originates on behalf of this node. Wrap with NET/ROM routing when enabled
        // (it still falls back to the same-port AX.25 dial for a local callsign).
        if (netRom is { ConnectEnabled: true })
        {
            Callsign nodeCall = Callsign.TryParse(config.Current.Identity.Callsign, out var nc) ? nc : default;
            return new Packet.Node.Core.NetRom.NetRomOutboundConnector(netRom, ax25, nodeCall);
        }
        return ax25;
    }

    // Mark a remote as an in-flight outbound connect (refcounted); the returned
    // ticket decrements on dispose. OnSessionAccepted skips remotes that are
    // claimed, so dialling OUT never starts a node console against the dialled
    // station.
    private OutboundTicket ClaimOutbound(Callsign remote)
    {
        lock (outboundGate)
        {
            outboundInProgress[remote] = outboundInProgress.TryGetValue(remote, out var n) ? n + 1 : 1;
        }
        return new OutboundTicket(this, remote);
    }

    private void ReleaseOutbound(Callsign remote)
    {
        lock (outboundGate)
        {
            if (outboundInProgress.TryGetValue(remote, out var n))
            {
                if (n <= 1)
                {
                    outboundInProgress.Remove(remote);
                }
                else
                {
                    outboundInProgress[remote] = n - 1;
                }
            }
        }
    }

    private bool IsOutbound(Callsign remote)
    {
        lock (outboundGate)
        {
            return outboundInProgress.ContainsKey(remote);
        }
    }

    private sealed class OutboundTicket(PortSupervisor owner, Callsign remote) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.ReleaseOutbound(remote);
            }
        }
    }

    /// <summary>Bring up all enabled ports in the current config. Called once on
    /// host start, before the first <see cref="ApplyAsync"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = config.Current;
            foreach (var port in current.Ports.Where(p => p.Enabled))
            {
                await BringUpAsync(port, current.Identity, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <summary>
    /// Transiently restart one configured, enabled port — tear its listener down and
    /// bring it back up on the same config — <b>without</b> a config change (a config
    /// edit can't express "restart an unchanged port": the reconcile planner would see
    /// no diff). Returns <c>false</c> (no-op) if the id is unknown or the port is
    /// disabled — the caller maps that to a 404/409. Single-threaded by contract, like
    /// <see cref="ApplyAsync"/>: the caller must serialise this against reconciles (the
    /// host runs it under its supervisor gate via <c>RunExclusiveAsync</c>).
    /// </summary>
    public async Task<bool> RestartPortAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var current = config.Current;
        var port = current.Ports.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
        if (port is null || !port.Enabled)
        {
            return false;   // nothing to restart — unknown or disabled (use up/down to enable)
        }
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownAsync(id).ConfigureAwait(false);
            await BringUpAsync(port, current.Identity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
        return true;
    }

    /// <summary>
    /// Execute a reconcile plan. Single-threaded by contract — the
    /// <see cref="NodeHostedService"/> serialises calls so two reconciles never
    /// overlap. Touches only the ports the plan names.
    /// </summary>
    public async Task ApplyAsync(ReconcilePlan plan, NodeConfig newConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(newConfig);

        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyCoreAsync(plan, newConfig, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    private async Task ApplyCoreAsync(ReconcilePlan plan, NodeConfig newConfig, CancellationToken cancellationToken)
    {
        if (plan.NodeWideReset)
        {
            LogNodeWideReset(newConfig.Identity.Callsign);
            await TearDownAllAsync().ConfigureAwait(false);
            foreach (var port in plan.ToBringUp)
            {
                await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // Tear down removed + disabled ports first (frees devices before any
        // restart re-grabs one).
        foreach (var id in plan.ToTearDown)
        {
            await TearDownAsync(id).ConfigureAwait(false);
        }

        foreach (var id in plan.ToDisable)
        {
            await TearDownAsync(id).ConfigureAwait(false);
        }

        // Single-port restarts: down then up.
        foreach (var port in plan.ToRestart)
        {
            await TearDownAsync(port.Id).ConfigureAwait(false);
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        // Bring up new + newly-enabled ports.
        foreach (var port in plan.ToBringUp)
        {
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        foreach (var port in plan.ToEnable)
        {
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        // Hot KISS-param changes — apply live, no restart, sessions untouched.
        foreach (var port in plan.KissParamsChanged)
        {
            await ApplyKissParamsAsync(port, cancellationToken).ConfigureAwait(false);
        }

        // AX.25 param changes — live-reseed the running listener so NEW sessions
        // pick up the new params, without rebuilding the listener or disturbing any
        // existing session (object identity preserved). See the ReconcilePlanner
        // remarks + Ax25Listener.UpdateSessionParameters.
        foreach (var port in plan.Ax25ParamsChanged)
        {
            ApplyAx25Params(port);
        }

        // Compat-profile changes ride the same reseed (the rebuilt parameter
        // record carries the parse options + session quirks); split out only so
        // the log says what actually changed. A port the params loop already
        // reseeded carries the new compat too — skip it.
        foreach (var port in plan.CompatChanged)
        {
            if (!plan.Ax25ParamsChanged.Contains(port))
            {
                ApplyCompat(port);
            }
        }

        // Per-port NET/ROM awareness changes (QUALITY / MINQUAL / NODESPACLEN) — hot-apply
        // the new values to the port's NET/ROM attachment (no restart, no session
        // disturbance). NET/ROM awareness + advertisement is read-only; QUALITY/MINQUAL
        // govern the next NODES ingest, NODESPACLEN the next broadcast's framing.
        foreach (var port in plan.NetRomQualityChanged)
        {
            netRom?.UpdatePortQuality(port.Id, port.NetRomQuality, port.NetRomMinQuality, port.NodesPaclen);
            RebaselineConfig(port);
            LogNetRomQualityApplied(port.Id);
        }
    }

    private async Task BringUpAsync(PortConfig port, Identity identity, CancellationToken ct, bool quiet = false)
    {
        if (!Callsign.TryParse(identity.Callsign, out var myCall))
        {
            // Should be unreachable — validation guarantees a parseable callsign
            // before the config is applied — but never throw out of a reconcile.
            LogPortFaulted(port.Id, $"identity callsign '{identity.Callsign}' did not parse");
            return;
        }

        // Never double-bring-up: the head-end retry loop can win the race between a reconcile
        // plan being computed (port down) and applied (port up via the retry) — a second stack
        // would fight the first for the same head-end pipe and the ports-indexer overwrite would
        // leak the first stack alive. Every teardown-then-up path (restart, node-wide reset)
        // clears the entry first, so an existing entry always means "already correctly up".
        if (TryGetRunning(port.Id) is not null)
        {
            return;
        }

        // Hoisted once (CA1873): cheap, and keeps method-invocation args out of
        // the log call sites below.
        var endpointText = port.Transport.DescribeEndpoint();

        // Resolve the port's named channel profile (if any) into effective AX.25 +
        // KISS params — explicit values win, the profile fills the gaps, no profile
        // = spec defaults. Opt-in tuning at the node-host layer (see ChannelProfiles).
        var (effectiveAx25, effectiveKiss) = ChannelProfiles.Resolve(port);

        // Resolves a head-end-bound radio / nino-tnc-tcp transport (split-station topology) to its
        // raw TCP pipe via the head-end's inventory. Built from the LIVE head-end fleet so a
        // re-addressed head-end is picked up on the next resolve (a purely-local node has an empty
        // fleet and never touches this). Ignored by the local / kiss-tcp / AXUDP arms.
        var headEndResolver = BuildHeadEndResolver();

        IAx25Transport transport;
        try
        {
            transport = await transportFactory.CreateAsync(port.Transport, timeProvider, headEndResolver, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Runtime fault on THIS port only — log + skip; the reconcile and the
            // rest of the ports proceed. A head-end-bound port arms a background retry
            // (#576): a Pi that boots slower than the node must not need a config edit.
            if (quiet)
            {
                LogPortRetryStillDown(port.Id, ex.Message);
            }
            else
            {
                LogPortFaultedEx(ex, port.Id, endpointText);
            }
            ScheduleHeadEndBringUpRetry(port, ct);
            return;
        }

        // Captured before any decorator hides it: a NinoTNC modem knows its own
        // over-air bit rate, which the RSSI-tagging wrapper (below) turns into
        // per-frame airtime / pre-data-carrier estimates.
        var ninoTnc = transport as NinoTncSerialPort;

        // Networked ports self-heal across a far-end bounce: wrap the connected transport so a
        // dropped link reconnects (backoff + KISS-param replay) instead of the port silently dying.
        // The eager connect above preserves initial fault isolation; this only adds
        // reconnect-after-drop. Covers kiss-tcp (a TNC/softmodem bounce, #50), nino-tnc-tcp (a
        // split-station head-end bounce or re-address — the reconnect re-resolves the inventory from
        // the LIVE head-end fleet, so a moved head-end's new tcpPort is picked up), AND a
        // head-end-bound tait-transparent port (#585 — the transport IS an IAx25Transport whose
        // inbound stream ENDS when the pipe dies, so the same wrapper supervises it; the reconnect
        // re-resolves, re-clocks via the line verb, and re-enters Transparent, recovering a radio
        // left as a stale byte pipe by escaping it first). The Stage-1 TCP IO faults on half-open
        // (read-idle / OS keepalive), which is what ends the stream and triggers this. A LOCAL
        // tait-transparent port keeps today's behaviour: a USB unplug is a physical event.
        ITransportLinkState? linkState = null;
        if (port.Transport is KissTcpTransport or NinoTncTcpTransport
            or TaitTransparentTransportConfig { IsHeadEndBound: true })
        {
            var reconnectingModem = new ReconnectingKissModem(
                transport,
                token => transportFactory.CreateAsync(
                    port.Transport, timeProvider, BuildHeadEndResolver(), token),
                endpointText,
                loggerFactory.CreateLogger<ReconnectingKissModem>(),
                timeProvider);
            transport = reconnectingModem;
            // Captured before the pacing/tagging decorators hide it (like ninoTnc above): the
            // metrics exporter reads IsReconnecting off the RunningPort (#583).
            linkState = reconnectingModem;
        }

        // ACKMODE pacing (opt-in, default-off): when this port's kiss.ackMode is set,
        // wrap the transport so the listener's outbound frames are serialised over the
        // half-duplex channel — each sent awaiting TX-completion, the next held until the
        // prior frame's completion arrives (or a short timeout). The pacing decorator needs
        // a TX-completion-capable inner; a transport with no completion signal (plain serial
        // KISS, AXUDP) cannot be paced, so the wrap is skipped and the port stays
        // fire-and-forget. The wrapper owns the transport it wraps, so RunningPort.DisposeAsync
        // (which disposes Transport) tears the whole chain down. (See PacingKissModem +
        // KissParams.AckMode.)
        if (effectiveKiss?.AckMode == true && transport is ITxCompletionTransport txCapable)
        {
            transport = new PacingKissModem(
                txCapable,
                PacingKissModem.DefaultPacingTimeout,
                loggerFactory.CreateLogger<PacingKissModem>());
        }

        // The modem chain as it stands here (before the optional RSSI-tagging wrap):
        // the transport ICsmaChannelParams / ITxCompletionTransport feature-detection
        // must target, because the tagging wrapper deliberately doesn't forward those.
        var modemTransport = transport;

        // Node-managed rigctld (plug-and-play rig): a rig: block bound by device/model
        // (instead of host/port) means the NODE owns the daemon — spawn a supervised rigctld
        // on a loopback port allocated once, wait for it to listen, and point every rig dial
        // below (the radio kind-rig arm AND the rig status attach) at it via the effective
        // config. Started FIRST because both dials need a live endpoint. Degrade-cleanly, like
        // an unreachable BYO daemon: a spawn failure or a daemon that never starts listening
        // leaves the effective rig config null, so the rest of bring-up sees "no rig" (a
        // radio: kind rig port then degrades its radio exactly as it does when a BYO daemon is
        // down). Once ready, the daemon self-heals for the port's lifetime — it respawns with
        // capped backoff on the SAME port, so the re-dialling clients recover when an unplugged
        // USB CAT device comes back.
        ManagedRigDaemon? rigDaemon = null;
        var effectiveRig = port.Rig;
        if (port.Rig is { IsNodeManaged: true } managedRig)
        {
            try
            {
                rigDaemon = ManagedRigDaemon.Start(port.Id, managedRig, loggerFactory, timeProvider);
                if (await rigDaemon.WaitUntilReadyAsync(ManagedRigDaemon.DefaultReadyBudget, ct).ConfigureAwait(false))
                {
                    effectiveRig = rigDaemon.ClientConfig;
                }
                else
                {
                    LogRigDaemonFailed(
                        port.Id, managedRig.Device!,
                        "the daemon never started listening within the readiness budget " +
                        "(missing rigctld binary, or a device/model it cannot open — see the rigctld log)");
                    await rigDaemon.DisposeAsync().ConfigureAwait(false);
                    rigDaemon = null;
                    effectiveRig = null;
                }
            }
            catch (Exception ex)
            {
                LogRigDaemonFailed(port.Id, managedRig.Device!, ex.Message);
                if (rigDaemon is not null)
                {
                    await rigDaemon.DisposeAsync().ConfigureAwait(false);
                    rigDaemon = null;
                }
                effectiveRig = null;
            }
        }

        // Optional radio-control attachment (port.radio, restart-class): open the
        // radio's control channel and wrap the transport OUTERMOST so every inbound
        // frame the listener sees carries per-frame RSSI/SNR metadata
        // (Ax25InboundFrame.Radio), plus start the radio-health/status monitor. A radio
        // open failure degrades cleanly — log and run the port without metadata; an
        // unplugged control cable (or a serial-bound radio that isn't plugged in) must
        // never take a working packet channel down. RunningPort tracks the pieces and
        // disposes them in order (node tap → modem chain → status monitor → radio).
        IRadioControl? radio = null;
        IRadioStatusMonitor? radioStatus = null;
        IInboundRadioSource? radioSource = null;
        if (port.Radio is { } radioConfig)
        {
            // Describe the attachment by whichever key pins it: a rig-backed radio (kind rig) by
            // the rig daemon it dials a dedicated connection to (the EFFECTIVE config, so a
            // node-managed daemon shows its device + allocated loopback port); a cabled radio by
            // its control device, or — serial-bound radios have an empty Port (the device is
            // resolved by scanning) — its CCDI serial.
            var radioEndpoint = RadioKinds.Is(radioConfig.Kind, RadioKinds.Rig)
                ? (effectiveRig is { } rigCfg
                    ? $"rig:{rigCfg.DescribeEndpoint()}"
                    : port.Rig is { } deadRig
                        ? $"rig:{deadRig.DescribeEndpoint()}"
                        : "rig:(no rig: block)")
                : !string.IsNullOrWhiteSpace(radioConfig.Port)
                    ? radioConfig.Port
                    : $"serial:{radioConfig.Serial}";
            try
            {
                // A node-managed rig whose daemon failed above has nothing to dial: degrade the
                // rig-backed radio through the same catch an unreachable BYO daemon would hit.
                if (RadioKinds.Is(radioConfig.Kind, RadioKinds.Rig) && effectiveRig is null && port.Rig is not null)
                {
                    throw new RigConnectionException(
                        "the port's node-managed rigctld is not running (it failed to start — " +
                        "see the log above), so the rig-backed radio has no daemon to dial.");
                }
                radio = await radioFactory.CreateAsync(radioConfig, timeProvider, headEndResolver, effectiveRig, ct).ConfigureAwait(false);

                // Head-end-bound radio control gets reconnect supervision (#576): the stable
                // facade is what every consumer below holds (tagging transport, carrier-sense
                // gate, status monitor, RunningPort.Radio), so when the control socket dies —
                // a head-end restart, a .deb upgrade's try-restart, a replug — the facade
                // disposes the dead driver and re-opens it (fresh inventory resolve, configured
                // baud re-clock, progress re-enable) underneath them all. A local-serial radio
                // keeps today's behaviour: a USB unplug is a physical event, not a bounce. A
                // rig-backed radio (kind rig, never head-end-bound) also skips the wrap — the
                // rig backends re-dial per command, so the adapter self-heals on its own.
                if (radioConfig.IsHeadEndBound)
                {
                    radio = new ReconnectingRadioControl(
                        radio, port.Id, radioConfig, radioFactory, BuildHeadEndResolver,
                        loggerFactory.CreateLogger<ReconnectingRadioControl>(), timeProvider);
                }

                if (radio.Capabilities.HasFlag(RadioCapabilities.RssiRead))
                {
                    // Outer→inner: node tap → RSSI-tagging wrapper → modem chain. The listener consumes
                    // the tap; the tap reads each inbound frame's RSSI/SNR (populated by the wrapper) so
                    // NodeTelemetry can stamp it onto the monitor/heard/traffic surfaces — a node-telemetry
                    // concern kept entirely OFF the parity-tracked AX.25 listener contract.
                    var tagging = new RssiTaggingTransport(
                        transport,
                        radio,
                        new RssiTaggingOptions
                        {
                            // A NinoTNC modem reports its live over-air bit rate; consulted
                            // per frame so a mode change is picked up without a restart.
                            BitRateHzProvider = ninoTnc is null ? null : () => ninoTnc.CurrentBitRateHz,
                        },
                        timeProvider);
                    var tap = new InboundRadioTap(tagging);
                    transport = tap;
                    radioSource = tap;
                    LogRadioAttached(port.Id, radioConfig.Kind, radioEndpoint);
                }
                else
                {
                    // A radio without RSSI reads (e.g. a rig whose DCD is calibrated but whose
                    // strength meter isn't): no tagging wrapper — the inbound path stays exactly
                    // as a no-radio port's — but the carrier-sense gate and the status monitor
                    // below still get the radio.
                    LogRadioAttachedNoRssi(port.Id, radioConfig.Kind, radioEndpoint, radio.Capabilities);
                }
                radioStatus = RadioStatusMonitors.Create(port.Id, radioConfig, radio, timeProvider);
            }
            catch (Exception ex)
            {
                LogRadioFaulted(ex, port.Id, radioConfig.Kind, radioEndpoint);
                // Unwind whatever we built, sampler/health-monitor first, radio last.
                if (radioStatus is not null)
                {
                    await radioStatus.DisposeAsync().ConfigureAwait(false);
                    radioStatus = null;
                }
                if (!ReferenceEquals(transport, modemTransport))
                {
                    await transport.DisposeAsync().ConfigureAwait(false);   // node tap → RSSI wrapper (stops sampler)
                    transport = modemTransport;   // degrade: run the port without radio metadata
                }
                radioSource = null;
                if (radio is not null)
                {
                    await radio.DisposeAsync().ConfigureAwait(false);
                    radio = null;
                }
            }
        }

        // Native carrier-sense CSMA (OQ-012): when a radio with hardware DCD is attached, feed
        // its carrier-sense into the listener's medium-access gate so the AX.25 stack itself
        // defers keyups while the channel is busy — the native seam, owned by the stack rather
        // than an opaque transport wrapper. A radio without carrier-sense (or no radio at all)
        // yields a null source, i.e. the always-clear gate — byte-for-byte today's behaviour.
        // Passed on the first-class, ax25-ts-parity-tracked Ax25ListenerOptions.CarrierSense
        // member (mirrors the TS carrierSense option). (The coming Nino KISS DCD extension lands
        // in the same gate.)
        // A transport that IS its own carrier-sense source (the in-process soundmodem's
        // native DCD; a future Nino KISS DCD extension) plugs into the same gate — probe
        // the modem chain, not the decorators, which don't forward optional facets.
        ICarrierSense? carrierSense = radio is not null && radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense)
            ? new RadioCarrierSense(radio)
            : modemTransport as ICarrierSense;
        // TX-complete→T1 (kiss.t1FromTxComplete): construction-time, like the
        // PacingKissModem wrap above — see KissParams.T1FromTxComplete.
        var options = BuildListenerOptions(
            effectiveAx25, port.Compat, myCall,
            restartT1OnTxComplete: effectiveKiss?.T1FromTxComplete == true,
            carrierSense: carrierSense);
        // The transport speaks the neutral IAx25Transport seam the listener consumes directly.
        var listener = new Ax25Listener(transport, options, timeProvider);

        // N1 (PACLEN) is carried on the live-reseed parameter record, not on the
        // parity-tracked Ax25ListenerOptions (it is node-host per-port config, not a
        // library listener flag). The constructor seeds its params from `options`, which
        // has no N1 — so reseed once now with the full MapAx25Params (which carries N1)
        // so this freshly-built listener's NEW sessions pick up the configured PACLEN. A
        // null N1 leaves the context default (256) — byte-for-byte today's behaviour.
        listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat));
        var connector = new Ax25OutboundConnector(port.Id, listener, r => ClaimOutbound(r), localOverride: null, cache: capabilityCache);
        listener.SessionAccepted += (_, e) => OnSessionAccepted(listener, connector, e.Session);

        try
        {
            await listener.StartAsync(ct).ConfigureAwait(false);
            // Target the modem chain, not the (possibly radio-tagged) outermost
            // transport — the tagging wrapper doesn't forward ICsmaChannelParams.
            await ApplyKissParamsToModemAsync(modemTransport, effectiveKiss, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (quiet)
            {
                LogPortRetryStillDown(port.Id, ex.Message);
            }
            else
            {
                LogPortFaultedEx(ex, port.Id, endpointText);
            }
            await listener.DisposeAsync().ConfigureAwait(false);
            await transport.DisposeAsync().ConfigureAwait(false);
            if (!ReferenceEquals(transport, modemTransport))
            {
                await modemTransport.DisposeAsync().ConfigureAwait(false);
            }
            if (radioStatus is not null)
            {
                await radioStatus.DisposeAsync().ConfigureAwait(false);
            }
            if (radio is not null)
            {
                await radio.DisposeAsync().ConfigureAwait(false);
            }
            if (rigDaemon is not null)
            {
                // Last, mirroring RunningPort.DisposeAsync: the (possibly rig-backed) radio
                // client above goes before the daemon it dials.
                await rigDaemon.DisposeAsync().ConfigureAwait(false);
            }
            ScheduleHeadEndBringUpRetry(port, ct);
            return;
        }

        // Rig-control (CAT) attachment: dial the rig daemon and start the status poller.
        // Placed after every throwing bring-up step so a port-level failure can't leak a
        // connected rig, and self-degrading — an unreachable rigctld/flrig must never take
        // a working packet channel down. The rig never touches the packet path (it is the
        // station-control sibling of the radio: seam, plan OQ-011), so no transport
        // wrapping happens here. After attach, backend transport drops self-heal (the
        // clients re-dial per command); only an attach-time failure leaves the rig off
        // until the next reconcile.
        IRigControl? rig = null;
        IRigStatusMonitor? rigStatus = null;
        if (effectiveRig is { } rigConfig)
        {
            // The EFFECTIVE config: for a node-managed rig this is daemon.ClientConfig (device +
            // the allocated loopback port), so both the dial and the status monitor's projected
            // endpoint stay honest; for a BYO daemon it IS port.Rig, byte-for-byte.
            var rigEndpoint = rigConfig.DescribeEndpoint();
            try
            {
                rig = await rigFactory.CreateAsync(rigConfig, timeProvider, ct).ConfigureAwait(false);
                rigStatus = RigStatusMonitors.Create(port.Id, rigConfig, rig, rigTelemetry, timeProvider);
                LogRigAttached(port.Id, rigConfig.Kind, rigEndpoint);
            }
            catch (Exception ex)
            {
                LogRigFaulted(ex, port.Id, rigConfig.Kind, rigEndpoint);
                if (rigStatus is not null)
                {
                    await rigStatus.DisposeAsync().ConfigureAwait(false);
                    rigStatus = null;
                }
                if (rig is not null)
                {
                    await rig.DisposeAsync().ConfigureAwait(false);
                    rig = null;
                }
            }
        }

        var running = new RunningPort
        {
            Id = port.Id,
            Config = port,
            Transport = transport,
            InnerTransport = ReferenceEquals(transport, modemTransport) ? null : modemTransport,
            // Captured above before the pacing/tagging decorators hid it, so the capability
            // doctor can probe the NinoTNC directly on a live port.
            NinoTnc = ninoTnc,
            Radio = radio,
            RadioStatus = radioStatus,
            Rig = rig,
            RigStatus = rigStatus,
            RigDaemon = rigDaemon,
            LinkState = linkState,
            Listener = listener,
            CarrierSense = carrierSense,
            Started = true,
        };
        lock (ports)
        {
            ports[port.Id] = running;
        }

        // A fresh listener must answer for the app callsigns registered while the port was
        // down/restarting (the RHPv2 server's binds outlive any individual port lifecycle).
        ApplyAppCallsignsTo(running);

        // NET/ROM read-only awareness: subscribe this port's frame-trace tap so the
        // node-level service hears NODES broadcasts on it. Observation-only — it
        // cannot disturb the session path. Detached on teardown. The per-port NET/ROM
        // knobs (all null = inherit the node-wide defaults) govern this port: QUALITY the
        // quality assumed for a neighbour heard here, MINQUAL the route-keep floor, and
        // NODESPACLEN the size cap on our NODES broadcast on this port.
        netRom?.AttachPort(port.Id, myCall, listener, port.NetRomQuality, port.NetRomMinQuality, port.NodesPaclen);

        // Live telemetry: tap the same frame trace for the node's frame/byte counters
        // + the monitor SSE feed. Also observation-only; detached on teardown. The radio
        // source (the node tap, when a radio is attached) lets it stamp per-frame RSSI/SNR
        // onto received frames without widening the AX.25 listener contract.
        telemetry?.AttachPort(port.Id, listener, radioSource);

        // ID beacon: arm the periodic UI-frame beacon on this port (default-off — armed
        // only when the effective beacon is enabled). Sends-only; detached on teardown.
        beacons?.AttachPort(port.Id, new ListenerBeaconChannel(listener));

        // Hoist the callsign too (CA1873) — endpointText is the one declared above.
        var callText = myCall.ToString();
        LogPortUp(port.Id, callText, endpointText);
    }

    private async Task TearDownAsync(string id)
    {
        RunningPort? running;
        lock (ports)
        {
            if (!ports.Remove(id, out running))
            {
                return;
            }
        }
        // Detach the NET/ROM tap and cleanly disconnect any interlink AX.25 sessions
        // on this port BEFORE disposing the listener — DetachPortAsync DISCs each
        // interlink and waits (bounded) for the DISC/UA to round-trip on the wire, so
        // the neighbour isn't left with a half-open link it polls (the #309
        // contamination class). The listener is still alive here to carry the DISC.
        // Learned routes survive; their neighbours age out via obsolescence.
        if (netRom is not null)
        {
            await netRom.DetachPortAsync(id).ConfigureAwait(false);
        }

        telemetry?.DetachPort(id);
        beacons?.DetachPort(id);
        await running.DisposeAsync().ConfigureAwait(false);
        LogPortDown(id);
    }

    private async Task TearDownAllAsync()
    {
        RunningPort[] all;
        lock (ports)
        {
            all = ports.Values.ToArray();
            ports.Clear();
        }
        foreach (var p in all)
        {
            // Clean interlink DISC (bounded) before disposing the listener — see
            // TearDownAsync for the rationale (avoid leaving a neighbour a half-open
            // link it polls onto the shared channel).
            if (netRom is not null)
            {
                await netRom.DetachPortAsync(p.Id).ConfigureAwait(false);
            }

            telemetry?.DetachPort(p.Id);
            beacons?.DetachPort(p.Id);
            await p.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>How often a failed head-end-bound port bring-up is retried (#576). A head-end
    /// that boots slower than the node (the Pi vs the LXC) or bounces while the node is up must
    /// come back without a config edit — reconcile only runs on config change.</summary>
    public static readonly TimeSpan HeadEndBringUpRetryInterval = TimeSpan.FromSeconds(30);

    // A port is head-end-bound when its data pipe or its radio control channel lives on a
    // split-station head-end — the class of port whose bring-up failure is worth retrying on a
    // timer (the head-end being briefly unreachable is an expected, recoverable state; a missing
    // local serial device is a physical event).
    private static bool IsHeadEndBound(PortConfig port) =>
        port.Transport is NinoTncTcpTransport or TaitTransparentTransportConfig { IsHeadEndBound: true }
        || port.Radio is { IsHeadEndBound: true };

    // Arm the background retry loop for a head-end-bound port whose bring-up just failed. One
    // loop per port (re-arming while armed is a no-op — BringUpAsync calls this on every failed
    // attempt, including the loop's own). State transitions log once; per-attempt noise is Debug.
    private void ScheduleHeadEndBringUpRetry(PortConfig port, CancellationToken ct)
    {
        // Deliberately NOT gated on ct: a cancelled bring-up (an aborted restart request, a
        // reconcile cut short) still leaves an enabled head-end port down — exactly what the
        // retry exists for. Supervisor shutdown is what stops retries (lifecycle/disposed).
        _ = ct;
        if (!IsHeadEndBound(port) || Volatile.Read(ref disposed) != 0 || lifecycle.IsCancellationRequested)
        {
            return;
        }
        lock (bringUpRetries)
        {
            if (bringUpRetries.ContainsKey(port.Id))
            {
                return;   // already armed — this is a retry attempt's own failure
            }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(lifecycle.Token);
            bringUpRetries[port.Id] = cts;
            LogHeadEndRetryArmed(port.Id, (int)HeadEndBringUpRetryInterval.TotalSeconds);
            // Deliberately NOT the caller's token: the loop outlives this reconcile and is
            // cancelled by its own linked CTS (lifecycle / abandon), never by the reconcile ending.
            _ = Task.Run(() => RetryBringUpLoopAsync(port.Id, cts), CancellationToken.None);
        }
    }

    private async Task RetryBringUpLoopAsync(string portId, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeadEndBringUpRetryInterval, timeProvider, ct).ConfigureAwait(false);

                await mutationGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Always read LIVE config: a config edit between attempts must win (and a
                    // reconcile that removed/disabled the port ends the loop; one that already
                    // brought it up makes this attempt a no-op success).
                    var current = config.Current;
                    var port = current.Ports.FirstOrDefault(
                        p => string.Equals(p.Id, portId, StringComparison.Ordinal));
                    if (port is null || !port.Enabled || !IsHeadEndBound(port))
                    {
                        LogHeadEndRetryAbandoned(portId);
                        return;
                    }
                    if (TryGetRunning(portId) is not null)
                    {
                        return;   // a reconcile/restart brought it up meanwhile — nothing to log
                    }

                    // Bound the attempt: the gate is shared with reconciles/API actions, and an
                    // unbounded dial against a blackholing host (DROP firewall, dead Pi) would
                    // otherwise hold it for minutes per attempt, stalling the whole port surface.
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    attempt.CancelAfter(HeadEndBringUpRetryInterval);
                    await BringUpAsync(port, current.Identity, attempt.Token, quiet: true).ConfigureAwait(false);
                    if (TryGetRunning(portId) is not null)
                    {
                        LogHeadEndRetrySucceeded(portId);
                        return;
                    }
                    // Still down — loop for the next attempt (the failure logged at Debug).
                }
                finally
                {
                    mutationGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // supervisor shutting down / lifecycle cancelled
        }
        finally
        {
            lock (bringUpRetries)
            {
                if (bringUpRetries.TryGetValue(portId, out var current) && ReferenceEquals(current, cts))
                {
                    bringUpRetries.Remove(portId);
                }
            }
            cts.Dispose();
        }
    }

    private async Task ApplyKissParamsAsync(PortConfig port, CancellationToken ct)
    {
        var running = TryGetRunning(port.Id);
        if (running is null)
        {
            return;   // not up (e.g. faulted) — nothing live to tune
        }

        // Resolve the profile here too so a live KISS re-apply uses the same
        // effective values a fresh bring-up would (explicit wins, profile fills).
        // ModemTransport, not Transport: on a radio-tagged port the CSMA-capable
        // modem sits beneath the RSSI-tagging wrapper.
        var (_, effectiveKiss) = ChannelProfiles.Resolve(port);
        await ApplyKissParamsToModemAsync(running.ModemTransport, effectiveKiss, ct).ConfigureAwait(false);
        RebaselineConfig(port);
        LogKissParamsApplied(port.Id);
    }

    private void ApplyAx25Params(PortConfig port)
    {
        var running = TryGetRunning(port.Id);
        if (running is null)
        {
            return;   // not up (e.g. faulted) — the next bring-up reads the new config
        }

        // Resolve the profile here too so a live AX.25 reseed uses the same
        // effective values a fresh bring-up would (explicit wins, profile fills).
        var (effectiveAx25, _) = ChannelProfiles.Resolve(port);

        // Live-reseed: new sessions on this listener pick up the new AX.25 params;
        // existing sessions keep their identity and their in-flight state.
        running.Listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat));
        RebaselineConfig(port);
        LogAx25ParamsApplied(port.Id);
    }

    private void ApplyCompat(PortConfig port)
    {
        var running = TryGetRunning(port.Id);
        if (running is null)
        {
            return;   // not up (e.g. faulted) — the next bring-up reads the new config
        }

        var (effectiveAx25, _) = ChannelProfiles.Resolve(port);

        // Same live reseed as ApplyAx25Params — the parameter record carries the
        // compat values. Parse options apply from the next inbound frame; quirks
        // seed sessions built from now on. Existing sessions untouched.
        running.Listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat));
        RebaselineConfig(port);
        LogCompatApplied(port.Id);
    }

    private static async Task ApplyKissParamsToModemAsync(IAx25Transport transport, KissParams? kiss, CancellationToken ct)
    {
        // CSMA params are meaningful only on a transport that exposes them. A transport
        // with no CSMA channel (none today — AXUDP exposes them as no-ops through the
        // migration shim) is simply skipped, preserving today's behaviour.
        if (transport is not ICsmaChannelParams csma)
        {
            return;
        }

        // TXDELAY/PERSIST/SLOTTIME stay opt-in — unset means "leave the modem at its
        // own default", because the right value for those is firmware-specific and a
        // wrong guess degrades CSMA. TXTAIL is different (#465): its default is an
        // IMPLICIT 0, sent UNCONDITIONALLY on every apply — bring-up, the regular
        // KISS-param cadence, and a hot config change — so the modem always gets a
        // deterministic, explicit tail. 0 is correct for most paths (a NinoTNC into a
        // fully analogue audio path, even on a slow AFSK1200 channel); a non-zero tail
        // is a MODEM + radio-audio-path-latency property (a software modem — samoyed /
        // Dire Wolf — or a NinoTNC into a non-zero-latency audio path), which the node
        // can't infer, so the operator sets `kiss.txTail` per port and that explicit
        // value wins here (the `?? 0` only supplies the default when unset).
        if (kiss?.TxDelay is { } txd)
        {
            await csma.SetTxDelayAsync(txd, ct).ConfigureAwait(false);
        }

        if (kiss?.Persistence is { } per)
        {
            await csma.SetPersistenceAsync(per, ct).ConfigureAwait(false);
        }

        if (kiss?.SlotTime is { } slot)
        {
            await csma.SetSlotTimeAsync(slot, ct).ConfigureAwait(false);
        }

        await csma.SetTxTailAsync(kiss?.TxTail ?? 0, ct).ConfigureAwait(false);
    }

    // Update the stored baseline config for a still-running port without
    // touching the listener — so the next field-diff is against the latest
    // applied values and a no-op re-apply stays a no-op.
    private void RebaselineConfig(PortConfig port)
    {
        lock (ports)
        {
            if (ports.TryGetValue(port.Id, out var running))
            {
                ports[port.Id] = new RunningPort
                {
                    Id = running.Id,
                    Config = port,
                    Transport = running.Transport,
                    InnerTransport = running.InnerTransport,
                    NinoTnc = running.NinoTnc,
                    Radio = running.Radio,
                    RadioStatus = running.RadioStatus,
                    LinkState = running.LinkState,
                    Listener = running.Listener,
                    CarrierSense = running.CarrierSense,
                    Started = running.Started,
                };
            }
        }
    }

    private static Ax25ListenerOptions BuildListenerOptions(
        Ax25PortParams? ax25, PortCompatConfig? compat, Callsign myCall,
        bool restartT1OnTxComplete = false, ICarrierSense? carrierSense = null)
    {
        var p = MapAx25Params(ax25, compat);
        return new Ax25ListenerOptions
        {
            MyCall = myCall,
            T1V = p.T1V,
            T2 = p.T2,
            T3 = p.T3,
            N2 = p.N2,
            K = p.K,
            MaxCachedPeers = p.MaxCachedPeers,
            ParseOptions = p.ParseOptions,
            Quirks = p.Quirks,
            RestartT1OnTxComplete = restartT1OnTxComplete,
            // Native carrier-sense CSMA (OQ-012): the radio-attached port's DCD, or null
            // (always-clear gate) when no carrier-sense-capable radio is attached.
            CarrierSense = carrierSense,
        };
    }

    // Map the config's AX.25 knobs (+ the compat profile) to the engine's
    // live-reseedable parameter record. The single definition both BringUp
    // (construction-time seed) and the hot reconcile paths (UpdateSessionParameters)
    // share, so the paths can never drift.
    private static Ax25SessionParameters MapAx25Params(Ax25PortParams? ax25, PortCompatConfig? compat) => new()
    {
        T1V = ax25?.T1Ms is { } t1 ? TimeSpan.FromMilliseconds(t1) : null,
        T2 = ax25?.T2Ms is { } t2 ? TimeSpan.FromMilliseconds(t2) : null,
        T3 = ax25?.T3Ms is { } t3 ? TimeSpan.FromMilliseconds(t3) : null,
        N2 = ax25?.N2,
        K = ax25?.WindowSize,
        N1 = ax25?.N1,   // PACLEN seed (null ⇒ context default 256)
        MaxCachedPeers = ax25?.MaxCachedPeers ?? 64,
        ParseOptions = Ax25CompatPresets.ResolveParseOptions(compat),
        Quirks = Ax25CompatPresets.ResolveQuirks(compat),
    };

    private void OnSessionAccepted(Ax25Listener listener, Ax25OutboundConnector connector, Ax25Session session)
    {
        // A session we are dialling OUT to (the console's Connect command) also
        // raises SessionAccepted on this listener — but it is NOT an inbound
        // caller, so we must not start a node console against it (that would spew
        // our prompt at the station we connected to). The connector claims the
        // remote for the duration of the connect.
        if (IsOutbound(session.Context.Remote))
        {
            return;
        }

        // Cutover observability: a genuine inbound caller (the outbound guard above ruled out
        // our own connect-out). Logged once here, before the console/app split, so every
        // accepted inbound circuit is positively visible — not just faults. Guarded so the
        // ToString()/port reverse-lookup are skipped when Information is off (CA1873).
        if (logger.IsEnabled(LogLevel.Information))
        {
            var peer = session.Context.Remote.ToString();
            var portId = PortIdFor(listener);
            LogInboundSessionAccepted(peer, portId);
        }

        // An inbound session addressed to an APP callsign (the session's Local is a
        // registered alias, not the port's own call) routes to the app's handler —
        // the RHPv2 server's accept path — never to the node console.
        if (!session.Context.Local.Equals(listener.MyCall))
        {
            OnAppSessionAccepted(listener, session);
            return;
        }

        // SessionAccepted can re-fire for the same session (a reconnect SABM on a
        // cached session). Only the first start a console loop; the dictionary is
        // the dedupe guard. Entries are removed when the loop ends.
        if (!consoleSessions.TryAdd(session, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var connection = new Ax25NodeConnection(listener, session);
            await using (connection.ConfigureAwait(false))
            {
                try
                {
                    // Wrap the same-port AX.25 connector with NET/ROM routing (when
                    // enabled) so `connect <alias>` reaches a distant node; the
                    // dialling user is this inbound peer.
                    var routed = WrapWithNetRom(connector, session.Context.Remote);
                    var env = new NodeConsoleEnvironment(config, routed, netRom, sysopContext, applicationHost, CreateConnectRouter(routed), capabilityCache);
                    var service = new NodeCommandService(env, loggerFactory.CreateLogger<NodeCommandService>(), timeProvider);
                    await service.RunAsync(connection, lifecycle.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var peer = session.Context.Remote.ToString();
                    LogConsoleFaulted(ex, peer);
                }
                finally
                {
                    consoleSessions.TryRemove(session, out _);
                }
            }
        }, CancellationToken.None);
    }

    // Route an inbound session for an app callsign to its registration's handler. The session
    // wraps as the standard INodeConnection; the handler (the RHPv2 server) owns its lifetime
    // from here. Re-fired SessionAccepted (a reconnect SABM) is deduped exactly like the
    // console path; the entry clears when the connection completes so a genuine reconnect
    // dispatches a fresh accept. No registration (a just-removed bind racing an accept) →
    // dispose the wrapper, which posts DISC.
    private void OnAppSessionAccepted(Ax25Listener listener, Ax25Session session)
    {
        if (!consoleSessions.TryAdd(session, 0))
        {
            return;
        }

        AppCallsignRegistration? registration;
        lock (appCallsignGate)
        {
            appCallsigns.TryGetValue(session.Context.Local, out registration);
        }

        string portId = PortIdFor(listener);

        _ = Task.Run(async () =>
        {
            var connection = new Ax25NodeConnection(listener, session);
            try
            {
                if (registration is null)
                {
                    LogAppSessionUnclaimed(session.Context.Local.ToString(), session.Context.Remote.ToString());
                    await connection.DisposeAsync().ConfigureAwait(false);   // posts DISC
                    consoleSessions.TryRemove(session, out byte _);
                    return;
                }
                await registration.OnAccepted(connection, portId).ConfigureAwait(false);
                // The handler owns the connection from here; clear the dedupe entry when the
                // link ends so a reconnect SABM dispatches a fresh accept.
                _ = connection.Completion.ContinueWith(
                    _ => consoleSessions.TryRemove(session, out byte _),
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                LogAppSessionFaulted(ex, session.Context.Local.ToString());
                try { await connection.DisposeAsync().ConfigureAwait(false); } catch { /* teardown */ }
                consoleSessions.TryRemove(session, out byte _);
            }
        }, CancellationToken.None);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        // Cancelling the lifecycle stops the head-end bring-up retry loops; taking the mutation
        // gate then waits out any attempt already mid-bring-up so the teardown can't interleave
        // with it (the cancelled token makes that attempt fail fast and clean itself up).
        await lifecycle.CancelAsync().ConfigureAwait(false);
        await mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAllAsync().ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
        lifecycle.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Identity callsign changed to {Callsign}; resetting all ports (all sessions end).")]
    private partial void LogNodeWideReset(string callsign);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id} up as {Callsign} on {Endpoint}.")]
    private partial void LogPortUp(string id, string callsign, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id} down.")]
    private partial void LogPortDown(string id);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id} faulted bringing up {Endpoint}; skipping it (other ports unaffected).")]
    private partial void LogPortFaultedEx(Exception ex, string id, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: radio control attached ({Kind} on {RadioPort}) — inbound frames carry RSSI/SNR metadata.")]
    private partial void LogRadioAttached(string id, string kind, string radioPort);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: radio control attached ({Kind} on {RadioPort}) without RSSI reads — capabilities: {Capabilities}; inbound frames carry no signal metadata.")]
    private partial void LogRadioAttachedNoRssi(string id, string kind, string radioPort, RadioCapabilities capabilities);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: radio control ({Kind} on {RadioPort}) failed to open; the port runs WITHOUT radio metadata.")]
    private partial void LogRadioFaulted(Exception ex, string id, string kind, string radioPort);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: rig control attached ({Kind} at {Endpoint}) — frequency/mode/PTT/meters are polled and projected on /api/v1/rigs.")]
    private partial void LogRigAttached(string id, string kind, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: rig control ({Kind} at {Endpoint}) failed to connect; the port runs WITHOUT rig status.")]
    private partial void LogRigFaulted(Exception ex, string id, string kind, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: node-managed rigctld for {Device} failed to come up ({Reason}); the port runs WITHOUT rig control.")]
    private partial void LogRigDaemonFailed(string id, string device, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id} faulted: {Reason}")]
    private partial void LogPortFaulted(string id, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: head-end-bound bring-up failed; retrying every {Seconds}s until the head-end appears (logged once — attempts are Debug).")]
    private partial void LogHeadEndRetryArmed(string id, int seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Port {Id}: bring-up retry still failing ({Reason}).")]
    private partial void LogPortRetryStillDown(string id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: head-end bring-up retry succeeded; the port is up.")]
    private partial void LogHeadEndRetrySucceeded(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: head-end bring-up retry abandoned (port removed, disabled, or no longer head-end-bound).")]
    private partial void LogHeadEndRetryAbandoned(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: KISS parameters applied live (no restart).")]
    private partial void LogKissParamsApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: AX.25 parameters reseeded live; new sessions use them (existing sessions untouched).")]
    private partial void LogAx25ParamsApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: AX.25 compatibility profile applied live — inbound parsing from the next frame, session quirks for new sessions (existing sessions untouched).")]
    private partial void LogCompatApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: NET/ROM route quality applied live; the next NODES broadcast on this port uses it.")]
    private partial void LogNetRomQualityApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbound session accepted from {Peer} on port {Port}.")]
    private partial void LogInboundSessionAccepted(string peer, string port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console session for {PeerId} faulted.")]
    private partial void LogConsoleFaulted(Exception ex, string peerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "App callsign {Callsign} registered (port {Port}) — the node now answers for it.")]
    private partial void LogAppCallsignRegistered(Callsign callsign, string port);

    [LoggerMessage(Level = LogLevel.Information, Message = "App callsign {Callsign} unregistered.")]
    private partial void LogAppCallsignUnregistered(Callsign callsign);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound session to app callsign {Local} from {Remote} had no live registration; disconnected.")]
    private partial void LogAppSessionUnclaimed(string local, string remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App-session handler for {Local} faulted.")]
    private partial void LogAppSessionFaulted(Exception ex, string local);
}
