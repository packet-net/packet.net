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
using M0LTE.Rig;
using Packet.Node.Core.Telemetry;
using Packet.Node.Core.Transports;
using M0LTE.Radio;
using Packet.Ax25.Radio;

namespace Packet.Node.Core.Hosting;

/// <summary>
/// Owns the reconcilable set of AX.25 ports - exactly one
/// <see cref="Ax25Listener"/> per port - and executes a
/// <see cref="ReconcilePlan"/> against it, touching only what changed. This is
/// the "do" half of hot reconfiguration; <see cref="ReconcilePlanner"/> is the
/// "decide" half.
/// </summary>
/// <remarks>
/// <para>
/// When a listener accepts a session (inbound or, indirectly, the console's
/// outbound connect) the supervisor wires it to the node console by wrapping it
/// as an <see cref="Ax25NodeConnection"/> and running a
/// <see cref="NodeCommandService"/> over it - same-port connect-out available
/// via an <see cref="Ax25OutboundConnector"/> on the same listener.
/// </para>
/// <para>
/// A runtime fault bringing one port up (e.g. a serial device that won't open)
/// faults only that port - it is logged and skipped, the rest of the reconcile
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
    // `bind`): (callsign, port scope) → registration. Applied to running listeners as local
    // aliases, re-applied when a port (re)starts, and routed in OnSessionAccepted (an inbound
    // session whose Local is an app callsign goes to the registration's handler, never to the
    // node console).
    //
    // The key carries the PORT SCOPE (#723 item 2), so the same callsign may be bound on two
    // different ports by two different apps - a local BBS on VHF and a gateway BBS on HF is
    // ordinary multi-port practice, and the old callsign-only key made it impossible. The rule,
    // enforced in RegisterAppCallsign and documented on it:
    //   * a per-port registration claims exactly that port;
    //   * a WILDCARD registration (portId null - the RHP wire's `bind` with no port label)
    //     claims EVERY port, so it conflicts with any other registration of that callsign,
    //     wildcard or per-port, in either order;
    //   * two per-port registrations of one callsign conflict only when they name the same port.
    // Resolution is (Local, arrival port) first, the wildcard as fallback - see OnAppSessionAccepted.
    private readonly Dictionary<AppCallsignKey, AppCallsignRegistration> appCallsigns = new();
    private readonly object appCallsignGate = new();
    // One entry per CONFIGURED port (running or not) - the port owner: its state, its config
    // baseline, its degraded set, its armed retry, and (while serving) the RunningPort that is
    // the runtime half. See PortSupervisor.State.cs; membership is no longer the state (#722).
    private readonly Dictionary<string, PortEntry> ports = new(StringComparer.Ordinal);
    // Serialises port-set mutation between the caller-serialised paths (StartAsync / ApplyAsync /
    // RestartPortAsync - the host's supervisor gate already keeps THOSE from overlapping) and the
    // supervisor's own background loops (the bring-up retry, #576/#722, and the running-state
    // watchdog), which would otherwise race a reconcile touching the same port set.
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<Ax25Session, byte> consoleSessions = new();
    // Remotes a connect-OUT is dialling right now, keyed by (PORT, remote) with a refcount
    // (two console sessions could dial the same call on the same port). SessionAccepted for a
    // claimed (port, remote) is the outbound session we just opened - NOT an inbound caller - so
    // we must not start a node console against it.
    //
    // The port is IN THE KEY (#723 item 1): the claim used to be node-wide by callsign, so while
    // port A dialled G8XYZ an inbound SABM from G8XYZ on port B was accepted by the engine (UA
    // sent, link up) and then silently dropped on the floor by this guard - the caller got a
    // connected link and dead air until T3/DISC, with no log line. A dial holds its claim for up
    // to (N2+1)×T1V, so the window is wide.
    private readonly Dictionary<(string PortId, Callsign Remote), int> outboundInProgress = new();
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
        // connection through the same factory the status poller uses - inject a fake
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
        // like the NET/ROM tap - it can never disturb a session.
        this.telemetry = telemetry;
        // Optional ID-beacon service: when present, each port that comes up arms a
        // periodic beacon timer IF its effective beacon (per-port override merged over
        // the system default) is enabled - default-off, so a stock node never beacons.
        // It only ever SENDS a UI frame (never disturbs a session, never mutates the
        // port set), so it attaches alongside telemetry, outside the supervisor gate.
        this.beacons = beacons;
        // Optional node-level NET/ROM consumer. When present, each port that comes up
        // has its frame-trace tap subscribed (and unsubscribed on teardown) so the
        // service hears NODES broadcasts; with connect-routing enabled it also taps
        // interlink sessions + drives L4 circuits. Hearing can never disturb a
        // session - the frame tap is observation-only.
        this.netRom = netRom;

        // When NET/ROM connect-routing is on, an inbound L4 circuit (a user routed to
        // us across the network) is bridged to a fresh node console - the same prompt
        // an AX.25/telnet user gets. The service raises this hook with the circuit
        // wrapped as an INodeConnection.
        if (this.netRom is not null)
        {
            this.netRom.RunInboundConsole = RunNodeConsoleAsync;
            this.netRom.OpenInterlink = OpenInterlinkAsync;
            // The port's declared link policy, so an interlink dial honours a `dial: v22` port
            // (auto and v20 both keep the conservative mod-8 interlink default).
            this.netRom.PortLinkPolicy = LinkPolicyOf;
            // The node's ONE canonical port order (config order, serving ports only) so the
            // interlink egress fallback and the NODES broadcast walk are deterministic rather
            // than dependent on a ConcurrentDictionary's enumeration order (#723 items 3 + 4).
            this.netRom.PortOrder = CanonicalServingPortIds;
        }

        // Every configured port gets an entry up front, so a read (the API, PORTS, metrics)
        // that lands before the first reconcile answers "configured"/"disabled" rather than
        // inventing a fault.
        SyncEntries(this.config.Current);

        // Watch the RUNNING state, not just bring-up: a listener whose inbound pump faults
        // marks itself not-running, and until #722 nothing observed that (see SuperviseLoopAsync).
        _ = Task.Run(() => SuperviseLoopAsync(lifecycle.Token), CancellationToken.None);
    }

    // Dial an interlink AX.25 session to a neighbour with the outbound claim held, so
    // OnSessionAccepted does NOT start a node console against the dialled neighbour
    // (an interlink is NET/ROM datagrams, not console text). Mirrors how
    // Ax25OutboundConnector claims a console connect-out. The service hands us the
    // PeerDialPlan it computed from the per-peer capability cache (version + pre-connect
    // XID); we just dial it. The default plan (no cache) is mod-8 + the listener's
    // pre-connect-XID default - byte-for-byte today's behaviour.
    private async Task<Ax25Session> OpenInterlinkAsync(
        string portId, Callsign neighbour, PeerDialPlan plan, CancellationToken ct)
    {
        var port = TryGetRunning(portId);
        var listener = port?.Listener
            ?? throw new InvalidOperationException($"NET/ROM interlink: port '{portId}' is not running.");

        // The claim carries THIS port (#723 item 1): an interlink dial on the backbone port must
        // not suppress the node console for the same neighbour calling in on a user port.
        using var ticket = ClaimOutbound(portId, neighbour);
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
        var env = new NodeConsoleEnvironment(
            config, connector, netRom, sysopContext, applicationHost, CreateConnectRouter(connector), capabilityCache,
            heard: null, portHealth: this);
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

    /// <summary>
    /// The ids of the ports currently serving, in the node's <b>canonical port order</b>:
    /// configuration order, the same order <see cref="Snapshot"/> yields, the console's
    /// <c>PORTS</c> listing numbers and <c>C &lt;n&gt; &lt;call&gt;</c> addresses (#723 item 3).
    /// Feeds <c>/ports</c>'s running set, <c>/sessions</c>, the metrics and the hail service, so
    /// every read surface answers "which port" the same way. A snapshot, not a live view.
    /// </summary>
    public IReadOnlyCollection<string> RunningPortIds => CanonicalServingPortIds();

    /// <summary>
    /// Look up the <b>runtime half</b> of a serving port: its listener, transport, radio and
    /// rig handles. Null when the port is not serving (disabled, faulted, retrying, mid-restart,
    /// or unknown).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a borrowed reference, not a lease.</b> The port it returns can be torn down
    /// underneath a caller that holds it across an await (a reconcile, a restart, or the
    /// running-state watchdog): teardown flips <see cref="RunningPort.IsAlive"/> to false
    /// <em>before</em> disposing anything, so a long-held reference can re-check it and fail
    /// fast instead of writing to a disposed listener. Roughly twenty read/act surfaces hold
    /// one this way (the read APIs, the metrics collector, the doctor, RHP, the MCP backend,
    /// the hail + tuning services). A full lease protocol - <c>GetPort</c> returning a scope
    /// that keeps the port alive or refuses - is deliberately a follow-up (see the PC2 item on
    /// packet-net/packet.net#726); PC1 adds only the alive flag and the idempotent dispose.
    /// </para>
    /// </remarks>
    public RunningPort? GetPort(string id) => TryGetRunning(id);

    // Centralises the `lock (ports) { TryGetValue }` read so the running-port
    // synchronisation invariant lives in one place rather than being open-coded at
    // every lookup site. Returns null when no port with that id is serving (e.g. it is
    // disabled, faulted, retrying, or mid-restart).
    private RunningPort? TryGetRunning(string id)
    {
        lock (ports)
        {
            return ports.TryGetValue(id, out var e) ? e.Running : null;
        }
    }

    // Build a head-end device resolver over the LIVE fleet. With discovery wired (split-station),
    // the headEndId → address step prefers a pinned config address and falls back to an mDNS browse
    // of the instance id - so a head-end configured in discover mode (blank address) or one that
    // re-addressed resolves at bring-up. Without discovery it is config-address-only (unchanged).
    private HeadEndDeviceResolver BuildHeadEndResolver()
    {
        var headEnds = config.Current.HeadEnds;
        HeadEnd.IHeadEndAddressResolver? addressResolver = headEndDiscovery is null
            ? null
            : new HeadEnd.HeadEndAddressResolver(headEnds, headEndDiscovery, loggerFactory);
        return new HeadEndDeviceResolver(headEnds, addressResolver: addressResolver, loggerFactory: loggerFactory);
    }

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

        return port is null
            ? null
            : new Ax25OutboundConnector(
                port.Id, port.Listener, r => ClaimOutbound(port.Id, r), localOverride, capabilityCache, LinkPolicyFor(port.Id));
    }

    /// <summary>
    /// Build the connect router a console session uses for <c>C[onnect] [port] &lt;call&gt;</c>:
    /// it bridges to a locally-registered app SSID (loopback crossconnect), dials a chosen
    /// 1-indexed port directly, or - for a plain <c>C &lt;call&gt;</c> - returns
    /// <paramref name="defaultConnector"/> (the session's usual same-port / NET/ROM-wrapped
    /// dial). Resolves against the live config + app registry, so a port that comes up or an app
    /// that binds mid-session is reachable on the next command.
    /// </summary>
    public IConnectRouter CreateConnectRouter(IOutboundConnector? defaultConnector) =>
        new ConnectRouter(this, defaultConnector);

    // Look up the live app-callsign registration that owns an inbound session for `target`
    // ARRIVING ON `arrivalPortId`: the port-scoped registration first, the wildcard ("*", every
    // port) as the fallback. Null when nothing is registered for that callsign on that port -
    // which, since #723 item 2, is a real answer rather than a lookup miss: an app bound to
    // port A must NOT answer a caller who arrived on port B.
    private AppCallsignRegistration? FindAppRegistration(Callsign target, string arrivalPortId)
    {
        lock (appCallsignGate)
        {
            if (appCallsigns.TryGetValue(new AppCallsignKey(target, arrivalPortId), out var scoped))
            {
                return scoped;
            }
            return appCallsigns.TryGetValue(new AppCallsignKey(target, null), out var wildcard) ? wildcard : null;
        }
    }

    // Any live registration for `target`, whatever its port scope - the loopback-crossconnect
    // target, which is in-process and so has no arrival port of its own. The wildcard wins;
    // otherwise the port-scoped registration whose port sorts first in canonical (config) order,
    // so a callsign bound on two ports still bridges deterministically.
    private AppCallsignRegistration? FindAnyAppRegistration(Callsign target)
    {
        List<AppCallsignRegistration> candidates;
        lock (appCallsignGate)
        {
            if (appCallsigns.TryGetValue(new AppCallsignKey(target, null), out var wildcard))
            {
                return wildcard;
            }
            candidates = appCallsigns
                .Where(kv => kv.Key.Local.Equals(target))
                .Select(kv => kv.Value)
                .ToList();
        }
        if (candidates.Count <= 1)
        {
            return candidates.FirstOrDefault();
        }
        var order = CanonicalPortOrdinals();
        return candidates
            .OrderBy(r => order.TryGetValue(r.PortId!, out int i) ? i : int.MaxValue)
            .ThenBy(r => r.PortId, StringComparer.Ordinal)
            .First();
    }

    // ── ILocalAppRegistry - the live key set, for the bare-verb resolver (packet.net#476) ──
    // A self-deriving app binds an SSID it chose, not the node-resolved PDN_APP_CALLSIGN; the
    // verb resolver consults this to bridge to whatever the app actually bound. Read-only.

    /// <inheritdoc/>
    public bool IsRegistered(Callsign callsign)
    {
        lock (appCallsignGate)
        {
            // Registered ANYWHERE (any port scope): the verb resolver asks "is this callsign a
            // local app", not "on which port".
            return appCallsigns.Keys.Any(k => k.Local.Equals(callsign));
        }
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<Callsign> RegisteredCallsigns()
    {
        lock (appCallsignGate)
        {
            // Distinct callsigns - one callsign bound on two ports is still one local app
            // identity as far as the bare-verb resolver is concerned.
            return appCallsigns.Keys.Select(k => k.Local).Distinct().ToArray();
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
        var registration = FindAnyAppRegistration(target);
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
    /// <remarks>
    /// <para>
    /// <b>The registration is scoped to a port</b> (#723 item 2). <paramref name="portId"/> null
    /// is the WILDCARD scope - the RHP wire's <c>bind</c> with no port label - and claims the
    /// callsign on <em>every</em> port, present and future. A named port claims that port only.
    /// So the same callsign may be bound by two different apps on two different ports (a local
    /// BBS on VHF, a gateway BBS on HF), while a wildcard bind conflicts with any other
    /// registration of that callsign in either order, and two per-port binds conflict only when
    /// they name the same port. A caller arriving on a port with no registration for the callsign
    /// it dialled is disconnected, never handed to the wrong app or to the node console.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The callsign is already registered on a
    /// conflicting scope (the wire's "Duplicate socket"), or is the node's own.</exception>
    public IDisposable RegisterAppCallsign(Callsign local, string? portId, Func<INodeConnection, string, Task> onAccepted)
    {
        ArgumentNullException.ThrowIfNull(onAccepted);
        Callsign nodeCall = Callsign.TryParse(config.Current.Identity.Callsign, out var nc) ? nc : default;
        if (local.Equals(nodeCall))
        {
            throw new InvalidOperationException("the node's own callsign is already in use (the node console listens on it).");
        }

        var key = new AppCallsignKey(local, portId);
        lock (appCallsignGate)
        {
            if (ConflictingScope(key) is { } clash)
            {
                throw new InvalidOperationException(
                    $"callsign {local} is already registered on {DescribeScope(clash.PortId)}"
                    + $" (this bind asked for {DescribeScope(portId)}).");
            }
            appCallsigns[key] = new AppCallsignRegistration { Local = local, PortId = portId, OnAccepted = onAccepted };
        }

        // Alias the running listener(s) now; ports that come up later get it in BringUpAsync.
        foreach (var port in MatchingPorts(portId))
        {
            port.Listener.AddLocalAlias(local);
        }
        LogAppCallsignRegistered(local, portId ?? "*");
        return new AppCallsignUnsubscriber(this, key);
    }

    // The live registration whose scope collides with `key`, or null when the bind is free.
    // Caller holds appCallsignGate. A wildcard collides with every scope of that callsign; a
    // per-port scope collides with the same port and with a wildcard.
    private AppCallsignRegistration? ConflictingScope(AppCallsignKey key)
    {
        foreach (var (existing, registration) in appCallsigns)
        {
            if (!existing.Local.Equals(key.Local))
            {
                continue;
            }
            if (existing.PortId is null || key.PortId is null
                || string.Equals(existing.PortId, key.PortId, StringComparison.Ordinal))
            {
                return registration;
            }
        }
        return null;
    }

    private static string DescribeScope(string? portId) =>
        portId is null ? "every port (a wildcard bind)" : $"port '{portId}'";

    private void UnregisterAppCallsign(AppCallsignKey key)
    {
        lock (appCallsignGate)
        {
            if (!appCallsigns.Remove(key))
            {
                return;
            }
        }
        foreach (var port in MatchingPorts(key.PortId))
        {
            port.Listener.RemoveLocalAlias(key.Local);
        }
        LogAppCallsignUnregistered(key.Local);
    }

    /// <summary>
    /// Re-scope every app-callsign registration bound to <paramref name="oldPortId"/> onto
    /// <paramref name="newPortId"/>. A port RENAME plans as remove-then-add (the id is the
    /// reconcile key), so without this an app bound to the old id is silently orphaned - it
    /// keeps a registration naming a port that no longer exists and stops answering, with no
    /// error anywhere (#723 item 2). The alias itself rides the bring-up, which re-applies every
    /// live registration to the fresh listener.
    /// </summary>
    private void RescopeAppCallsigns(string oldPortId, string newPortId)
    {
        List<AppCallsignRegistration> moved = [];
        lock (appCallsignGate)
        {
            foreach (var (key, registration) in appCallsigns.ToArray())
            {
                if (key.PortId is null || !string.Equals(key.PortId, oldPortId, StringComparison.Ordinal))
                {
                    continue;
                }
                appCallsigns.Remove(key);
                var rescoped = registration with { PortId = newPortId };
                appCallsigns[new AppCallsignKey(key.Local, newPortId)] = rescoped;
                moved.Add(rescoped);
            }
        }
        foreach (var r in moved)
        {
            LogAppCallsignRescoped(r.Local, oldPortId, newPortId);
        }
    }

    private List<RunningPort> MatchingPorts(string? portId)
    {
        lock (ports)
        {
            return ports.Values
                .Where(e => e.Running is not null && (portId is null || string.Equals(e.Id, portId, StringComparison.Ordinal)))
                .Select(e => e.Running!)
                .ToList();
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

    /// <summary>The app-callsign registry key: the bound callsign and its port scope (null = the
    /// wildcard "every port" bind). Ordinal on the port id, like every other port-id compare.</summary>
    private readonly record struct AppCallsignKey(Callsign Local, string? PortId);

    private sealed record AppCallsignRegistration
    {
        public required Callsign Local { get; init; }
        public required string? PortId { get; init; }
        public required Func<INodeConnection, string, Task> OnAccepted { get; init; }
    }

    // The console's connect router (see CreateConnectRouter). Holds the supervisor + the session's
    // default connector; reads the live config/registry on each Resolve so it tracks port and app
    // changes within a session. NET/ROM is intentionally not consulted here - an explicit port is
    // a direct dial; aliases come later.
    private sealed class ConnectRouter(PortSupervisor owner, IOutboundConnector? defaultConnector) : IConnectRouter
    {
        public ConnectResolution Resolve(int? port, Callsign target, INodeConnection inbound)
        {
            // No port: a registered app SSID wins (loopback crossconnect to the app); otherwise
            // the session's default dial. An explicit port skips this - it's a deliberate "go RF".
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

    private sealed class AppCallsignUnsubscriber(PortSupervisor owner, AppCallsignKey key) : IDisposable
    {
        private int gone;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref gone, 1) == 0)
            {
                owner.UnregisterAppCallsign(key);
            }
        }
    }

    /// <summary>
    /// The node's default outbound connector: the <b>first enabled port that is serving, in
    /// canonical (configuration) order</b> - the very port the console's <c>PORTS</c> listing
    /// numbers 1 (or the first one after it that is actually up). Null when no port is serving.
    /// </summary>
    /// <remarks>
    /// This used to sort the serving ports by id STRING, which is a different order from the
    /// 1-indexed config order <c>C &lt;n&gt; &lt;call&gt;</c> and <c>PORTS</c> use: on a node
    /// configured <c>[vhf, hf]</c> a bare <c>C G8XYZ</c> left on <c>hf</c> while <c>C 1 G8XYZ</c>
    /// left on <c>vhf</c>, so operator-visible port numbering could not be trusted. One canonical
    /// order now serves all of them (#723 item 3).
    /// </remarks>
    public IOutboundConnector? ResolveDefaultConnector()
    {
        var serving = CanonicalServingPortIds();
        var first = serving.Count == 0 ? null : TryGetRunning(serving[0]);
        var ax25 = first is null
            ? null
            : new Ax25OutboundConnector(
                first.Id, first.Listener, r => ClaimOutbound(first.Id, r), localOverride: null, cache: capabilityCache,
                linkPolicy: LinkPolicyFor(first.Id));

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

    // Mark (port, remote) as an in-flight outbound connect (refcounted); the returned ticket
    // decrements on dispose. OnSessionAccepted skips a session whose ARRIVAL PORT and remote are
    // both claimed, so dialling OUT never starts a node console against the dialled station -
    // and a caller of the same callsign arriving on a DIFFERENT port still gets its console.
    private OutboundTicket ClaimOutbound(string portId, Callsign remote)
    {
        var key = (portId, remote);
        lock (outboundGate)
        {
            outboundInProgress[key] = outboundInProgress.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return new OutboundTicket(this, portId, remote);
    }

    private void ReleaseOutbound(string portId, Callsign remote)
    {
        var key = (portId, remote);
        lock (outboundGate)
        {
            if (outboundInProgress.TryGetValue(key, out var n))
            {
                if (n <= 1)
                {
                    outboundInProgress.Remove(key);
                }
                else
                {
                    outboundInProgress[key] = n - 1;
                }
            }
        }
    }

    private bool IsOutbound(string portId, Callsign remote)
    {
        lock (outboundGate)
        {
            return outboundInProgress.ContainsKey((portId, remote));
        }
    }

    private sealed class OutboundTicket(PortSupervisor owner, string portId, Callsign remote) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                owner.ReleaseOutbound(portId, remote);
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
            SyncEntries(current);
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
    /// Transiently restart one configured, enabled port - tear its listener down and
    /// bring it back up on the same config - <b>without</b> a config change (a config
    /// edit can't express "restart an unchanged port": the reconcile planner would see
    /// no diff). Returns <c>false</c> (no-op) if the id is unknown or the port is
    /// disabled - the caller maps that to a 404/409. Single-threaded by contract, like
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
            return false;   // nothing to restart - unknown or disabled (use up/down to enable)
        }
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownAsync(id, TeardownReason.Restart).ConfigureAwait(false);
            await BringUpAsync(port, current.Identity, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
        return true;
    }

    /// <summary>
    /// Take one configured port <b>out of service and leave it there</b> - a teardown with no
    /// bring-up. Returns <c>false</c> (no-op) when the id is unknown. Single-threaded by contract,
    /// like <see cref="RestartPortAsync"/>: the caller must serialise this against reconciles (the
    /// host runs it under its supervisor gate via <c>RunExclusiveAsync</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the one operation that needs the port's <em>hardware</em>, not just its air time:
    /// programming an attached Tait's codeplug (<c>Radios/Programming</c>) drives the radio's own
    /// serial device, which the running port holds open. Pausing the listener - what a tuning
    /// session does - is not enough; the port has to let go of the device.
    /// </para>
    /// <para>
    /// <b>The caller must bring the port back</b> (<see cref="RestartPortAsync"/>) on every exit
    /// path. Nothing else will: a torn-down port lands in <see cref="PortState.Configured"/> with no
    /// running half, which the running-state watchdog deliberately ignores (it only supervises ports
    /// that are up), and a reconcile only touches ports whose <em>config</em> changed.
    /// </para>
    /// </remarks>
    public async Task<bool> StopPortAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await TearDownAsync(id, TeardownReason.Suspend).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
        return true;
    }

    /// <summary>
    /// Execute a reconcile plan. Single-threaded by contract - the
    /// <see cref="NodeHostedService"/> serialises calls so two reconciles never
    /// overlap. Touches only the ports the plan names.
    /// </summary>
    public async Task<PortApplyOutcome> ApplyAsync(ReconcilePlan plan, NodeConfig newConfig, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(newConfig);

        await mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ApplyCoreAsync(plan, newConfig, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            mutationGate.Release();
        }
    }

    /// <summary>
    /// The reasons this supervisor would <b>refuse</b> to apply <paramref name="candidate"/>
    /// against its LIVE state, or an empty list when it is applicable. Today there is exactly one:
    /// a new <c>identity.callsign</c> that a live app registration has already bound
    /// (#723 item 2). Pure and side-effect-free, so the config-write API can ask BEFORE it
    /// persists and answer the operator with a 422 instead of letting the apply fail later.
    /// </summary>
    /// <remarks>
    /// The bind-time guard in <see cref="RegisterAppCallsign"/> only ever covered one direction.
    /// Coming the other way - renaming the node onto an SSID a BBS had already bound - the
    /// node-wide reset rebuilt every listener under the new <c>MyCall</c>, and
    /// <c>OnSessionAccepted</c>'s split is <c>Local != listener.MyCall</c>, so every connect for
    /// that app fell into the node-console branch instead. Silent: no error, no log, the app just
    /// stopped receiving callers. The rule is symmetric now - the node's callsign and an app
    /// binding are mutually exclusive whichever one moves.
    /// </remarks>
    public IReadOnlyList<string> LiveApplyConflicts(NodeConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!Callsign.TryParse(candidate.Identity.Callsign, out var newCall))
        {
            return [];   // an unparsable callsign is the validator's business, not ours.
        }

        List<AppCallsignKey> clashes;
        lock (appCallsignGate)
        {
            clashes = appCallsigns.Keys.Where(k => k.Local.Equals(newCall)).ToList();
        }
        return clashes.Count == 0
            ? []
            : [.. clashes.Select(k =>
                $"identity.callsign '{candidate.Identity.Callsign}' is already bound as an application callsign on "
                + $"{DescribeScope(k.PortId)}. Unbind that application first, or give the node a different callsign - "
                + "the node console and an application cannot answer for the same callsign.")];
    }

    private async Task<PortApplyOutcome> ApplyCoreAsync(ReconcilePlan plan, NodeConfig newConfig, CancellationToken cancellationToken)
    {
        // Refuse BEFORE anything moves (#723 item 2). The config store has already accepted this
        // config - a file-provider hot reload never passes through the API's pre-check - so the
        // supervisor is the last line: it keeps the live identity and every app binding, logs
        // why at Error, and reports the refusal to the reconcile worker, which leaves its applied
        // baseline where it was so the next edit re-plans from the truth rather than from a
        // config that never took effect. Nothing is half-applied: no alias moves, no port resets.
        if (LiveApplyConflicts(newConfig) is { Count: > 0 } refusals)
        {
            foreach (var reason in refusals)
            {
                LogApplyRefused(reason);
            }
            return PortApplyOutcome.Refused(refusals);
        }

        // A RENAME plans as remove-then-add, so any app bound to the old id would be orphaned.
        // Re-scope those registrations first, while the entries still carry the OLD config, so
        // the fresh listener's bring-up re-applies them under the new id.
        RescopeRenamedPorts(plan);

        // Every configured port has an entry before anything moves, and each entry's config
        // baseline advances to the new config (the ports the plan doesn't touch included).
        SyncEntries(newConfig);

        if (plan.NodeWideReset)
        {
            LogNodeWideReset(newConfig.Identity.Callsign);
            await TearDownAllAsync(TeardownReason.Restart).ConfigureAwait(false);
            foreach (var port in plan.ToBringUp)
            {
                await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
            }
            return PortApplyOutcome.Applied;
        }

        // ── PHASE 1: every teardown, before any bring-up ─────────────────────────────
        // Not just removals: a RESTART's teardown belongs here too. Restarting per port
        // (down-then-up, in config order) meant a validated edit that moves one serial device
        // from port a to port b collided whenever b's id sorted first - b's bring-up dialled a
        // device a still held, and the failure left a legal config with a permanently dead port
        // (#722). Two phases make device handover between ports safe by construction.
        foreach (var id in plan.ToTearDown)
        {
            await TearDownAsync(id, TeardownReason.Remove).ConfigureAwait(false);
        }

        foreach (var id in plan.ToDisable)
        {
            await TearDownAsync(id, TeardownReason.Disable).ConfigureAwait(false);
        }

        foreach (var port in plan.ToRestart)
        {
            await TearDownAsync(port.Id, TeardownReason.Restart).ConfigureAwait(false);
        }

        // ── PHASE 2: every bring-up, now that all the devices are free ───────────────
        foreach (var port in plan.ToRestart)
        {
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        foreach (var port in plan.ToBringUp)
        {
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        foreach (var port in plan.ToEnable)
        {
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        // Hot KISS-param changes - apply live, no restart, sessions untouched.
        foreach (var port in plan.KissParamsChanged)
        {
            await ApplyKissParamsAsync(port, cancellationToken).ConfigureAwait(false);
        }

        // AX.25 param changes - live-reseed the running listener so NEW sessions
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
        // reseeded carries the new compat too - skip it.
        foreach (var port in plan.CompatChanged)
        {
            if (!plan.Ax25ParamsChanged.Contains(port))
            {
                ApplyCompat(port);
            }
        }

        // Link-policy changes ride the same reseed (the rebuilt parameter record carries the
        // listener's dial defaults; the connector reads the live policy per dial). Skipped when
        // one of the loops above already reseeded this port with the same MapAx25Params call.
        foreach (var port in plan.LinkChanged)
        {
            if (!plan.Ax25ParamsChanged.Contains(port) && !plan.CompatChanged.Contains(port))
            {
                ApplyLinkPolicy(port);
            }
        }

        // Per-port NET/ROM awareness changes (QUALITY / MINQUAL / NODESPACLEN) - hot-apply
        // the new values to the port's NET/ROM attachment (no restart, no session
        // disturbance). NET/ROM awareness + advertisement is read-only; QUALITY/MINQUAL
        // govern the next NODES ingest, NODESPACLEN the next broadcast's framing.
        foreach (var port in plan.NetRomQualityChanged)
        {
            netRom?.UpdatePortQuality(port.Id, port.NetRomQuality, port.NetRomMinQuality, port.NodesPaclen);
            RebaselineConfig(port);
            LogNetRomQualityApplied(port.Id);
        }

        // Per-port ID-beacon changes - hot: the beacon service re-arms every attached port's
        // timer from the LIVE config, so one Reapply covers however many ports changed. The
        // host calls Reapply after every reconcile too (it is idempotent); doing it here as
        // well keeps the supervisor honest on its own.
        if (plan.BeaconChanged.Count > 0)
        {
            foreach (var port in plan.BeaconChanged)
            {
                RebaselineConfig(port);
                LogBeaconApplied(port.Id);
            }

            beacons?.Reapply();
        }

        // Per-port MQTT instance label - nothing to apply: MqttFrameEmitter resolves the label
        // from the live config on every frame. Rebaseline + say so, so the reconcile log does
        // not report a real edit as having done nothing.
        foreach (var port in plan.MqttInstanceChanged)
        {
            RebaselineConfig(port);
            LogMqttInstanceApplied(port.Id);
        }

        return PortApplyOutcome.Applied;
    }

    // A port RENAME is invisible to the reconcile planner - the id IS the key, so it plans as
    // ToTearDown(old) + ToBringUp(new) - but it is very visible to an app bound to the old id,
    // which would keep a registration naming a port that no longer exists and quietly stop
    // answering. Pair the two by the transport ENDPOINT: exactly one port leaving and exactly
    // one arriving on the same device is a rename by any operator's definition. Anything more
    // ambiguous (two ports swapping devices, a genuine add + a genuine remove) is left alone -
    // guessing there would move a binding to a port the operator never meant.
    private void RescopeRenamedPorts(ReconcilePlan plan)
    {
        if (plan.ToTearDown.Count == 0 || plan.ToBringUp.Count == 0)
        {
            return;
        }

        Dictionary<string, PortConfig> leaving;
        lock (ports)
        {
            leaving = plan.ToTearDown
                .Where(id => ports.ContainsKey(id))
                .ToDictionary(id => id, id => ports[id].Config, StringComparer.Ordinal);
        }

        foreach (var arriving in plan.ToBringUp)
        {
            var matches = leaving
                .Where(kv => string.Equals(
                    kv.Value.Transport.EndpointKey, arriving.Transport.EndpointKey, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 1)
            {
                RescopeAppCallsigns(matches[0].Key, arriving.Id);
            }
        }
    }

    /// <param name="preOpened">A transport the caller has ALREADY opened for this port (the
    /// retry loop opens the pipe outside the mutation gate, so a blackholing head-end cannot
    /// stall every other reconcile - #722). Adopted here: this method owns it from now on,
    /// success or failure.</param>
    private async Task BringUpAsync(
        PortConfig port, Identity identity, CancellationToken ct, bool quiet = false, IAx25Transport? preOpened = null)
    {
        // Never double-bring-up: the retry loop can win the race between a reconcile plan being
        // computed (port down) and applied (port up via the retry) - a second stack would fight
        // the first for the same pipe and the overwrite would leak the first stack alive. Every
        // teardown-then-up path (restart, node-wide reset) clears the runtime half first, so a
        // live one here always means "already correctly up".
        if (TryGetRunning(port.Id) is not null)
        {
            await DiscardPreOpenedAsync(preOpened).ConfigureAwait(false);
            return;
        }

        EnsureEntry(port);
        SetState(port.Id, PortState.Starting, "bring-up", degraded: []);

        if (!Callsign.TryParse(identity.Callsign, out var myCall))
        {
            // Should be unreachable - validation guarantees a parseable callsign
            // before the config is applied - but never throw out of a reconcile.
            var reason = $"identity callsign '{identity.Callsign}' did not parse";
            LogPortFaulted(port.Id, reason);
            SetState(port.Id, PortState.Faulted, "bring-up failed", lastError: reason);
            await DiscardPreOpenedAsync(preOpened).ConfigureAwait(false);
            return;
        }

        // Hoisted once (CA1873): cheap, and keeps method-invocation args out of
        // the log call sites below.
        var endpointText = port.Transport.DescribeEndpoint();

        // Resolve the port's named channel profile (if any) into effective AX.25 +
        // KISS params - explicit values win, the profile fills the gaps, no profile
        // = spec defaults. Opt-in tuning at the node-host layer (see ChannelProfiles).
        var (effectiveAx25, effectiveKiss) = ChannelProfiles.Resolve(port);

        // Resolves a head-end-bound radio / nino-tnc-tcp transport (split-station topology) to its
        // raw TCP pipe via the head-end's inventory. Built from the LIVE head-end fleet so a
        // re-addressed head-end is picked up on the next resolve (a purely-local node has an empty
        // fleet and never touches this). Ignored by the local / kiss-tcp / AXUDP arms.
        var headEndResolver = BuildHeadEndResolver();

        IAx25Transport transport;
        if (preOpened is not null)
        {
            transport = preOpened;
        }
        else
        {
            try
            {
                transport = await transportFactory.CreateAsync(port.Transport, timeProvider, headEndResolver, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Runtime fault on THIS port only - log + skip; the reconcile and the rest of
                // the ports proceed. The port then arms the bounded-backoff bring-up retry
                // (#576/#722), whatever its transport kind: a Pi that boots slower than the
                // node, a USB TNC that enumerates late, and a softmodem not yet listening are
                // all recoverable without a config edit.
                if (quiet)
                {
                    LogPortRetryStillDown(port.Id, ex.Message);
                }
                else
                {
                    LogPortFaultedEx(ex, port.Id, endpointText);
                }
                SetState(port.Id, PortState.Faulted, "transport did not open", lastError: ex.Message);
                ArmRetry(port.Id);
                return;
            }
        }

        // Captured before any decorator hides it: a NinoTNC modem knows its own
        // over-air bit rate, which the RSSI-tagging wrapper (below) turns into
        // per-frame airtime / pre-data-carrier estimates.
        var ninoTnc = transport as NinoTncSerialPort;

        // Networked ports self-heal across a far-end bounce: wrap the connected transport so a
        // dropped link reconnects (backoff + KISS-param replay) instead of the port silently dying.
        // The eager connect above preserves initial fault isolation; this only adds
        // reconnect-after-drop. Covers kiss-tcp (a TNC/softmodem bounce, #50), nino-tnc-tcp (a
        // split-station head-end bounce or re-address - the reconnect re-resolves the inventory from
        // the LIVE head-end fleet, so a moved head-end's new tcpPort is picked up), AND a
        // head-end-bound tait-transparent port (#585 - the transport IS an IAx25Transport whose
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
        // half-duplex channel - each sent awaiting TX-completion, the next held until the
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
                loggerFactory.CreateLogger<PacingKissModem>(),
                timeProvider);
        }

        // The modem chain as it stands here (before the optional RSSI-tagging wrap):
        // the transport ICsmaChannelParams / ITxCompletionTransport feature-detection
        // must target, because the tagging wrapper deliberately doesn't forward those.
        var modemTransport = transport;

        // In-process soundmodem: attach the per-frame receive-quality early-warning log (#635).
        // Cumulative FEC counters ride on the transport snapshot (pdn_port_fec_* + the /quality
        // API); here we tap the per-frame push only to emit a structured line when a frame needed
        // FEC repair - persistently non-zero corrections mean the link is spending its error
        // budget before frames start dropping. Subscribed once per transport instance at bring-up:
        // the reconcile-rebuild path (RebaselineConfig) reuses the same transport, so it does not
        // re-subscribe. Handler runs on the receive-pump thread - the LoggerMessage call is cheap
        // and self-gates on level.
        if (modemTransport is SoundModemFrameTransport soundModemQuality)
        {
            string qualityPortId = port.Id;
            soundModemQuality.FrameQualityDecoded += sample =>
            {
                if (sample.CorrectedBytes is int corrected && corrected > 0)
                {
                    LogSoundModemFecCorrections(qualityPortId, sample.Mode, corrected, sample.FrameBytes);
                }
            };
        }

        // Node-managed rigctld (plug-and-play rig): a rig: block bound by device/model
        // (instead of host/port) means the NODE owns the daemon - spawn a supervised rigctld
        // on a loopback port allocated once, wait for it to listen, and point every rig dial
        // below (the radio kind-rig arm AND the rig status attach) at it via the effective
        // config. Started FIRST because both dials need a live endpoint. Degrade-cleanly, like
        // an unreachable BYO daemon: a spawn failure or a daemon that never starts listening
        // leaves the effective rig config null, so the rest of bring-up sees "no rig" (a
        // radio: kind rig port then degrades its radio exactly as it does when a BYO daemon is
        // down). Once ready, the daemon self-heals for the port's lifetime - it respawns with
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
                    const string NotListening = "the daemon never started listening within the readiness budget " +
                        "(missing rigctld binary, or a device/model it cannot open - see the rigctld log)";
                    LogRigDaemonFailed(port.Id, managedRig.Device!, NotListening);
                    NoteDegraded(port.Id, PortComponents.Rigctld, $"node-managed rigctld: {NotListening}");
                    await rigDaemon.DisposeAsync().ConfigureAwait(false);
                    rigDaemon = null;
                    effectiveRig = null;
                }
            }
            catch (Exception ex)
            {
                LogRigDaemonFailed(port.Id, managedRig.Device!, ex.Message);
                NoteDegraded(port.Id, PortComponents.Rigctld, $"node-managed rigctld: {ex.Message}");
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
        // open failure degrades cleanly - log and run the port without metadata; an
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
            // its control device, or - serial-bound radios have an empty Port (the device is
            // resolved by scanning) - its CCDI serial.
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
                        "the port's node-managed rigctld is not running (it failed to start - " +
                        "see the log above), so the rig-backed radio has no daemon to dial.");
                }
                radio = await OpenRadioAsync(port.Id, radioConfig, headEndResolver, effectiveRig, ct)
                    .ConfigureAwait(false);

                // Head-end-bound radio control gets reconnect supervision (#576): the stable
                // facade is what every consumer below holds (tagging transport, carrier-sense
                // gate, status monitor, RunningPort.Radio), so when the control socket dies -
                // a head-end restart, a .deb upgrade's try-restart, a replug - the facade
                // disposes the dead driver and re-opens it (fresh inventory resolve, configured
                // baud re-clock, progress re-enable) underneath them all. A local-serial radio
                // keeps today's behaviour: a USB unplug is a physical event, not a bounce. A
                // rig-backed radio (kind rig, never head-end-bound) also skips the wrap - the
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
                    // NodeTelemetry can stamp it onto the monitor/heard/traffic surfaces - a node-telemetry
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
                    // strength meter isn't): no tagging wrapper - the inbound path stays exactly
                    // as a no-radio port's - but the carrier-sense gate and the status monitor
                    // below still get the radio.
                    LogRadioAttachedNoRssi(port.Id, radioConfig.Kind, radioEndpoint, radio.Capabilities);
                }
                radioStatus = RadioStatusMonitors.Create(port.Id, radioConfig, radio, timeProvider);
            }
            catch (Exception ex)
            {
                LogRadioFaulted(ex, port.Id, radioConfig.Kind, radioEndpoint);
                // First-class degradation (#722): the port still carries traffic, but with no
                // per-frame RSSI/SNR and no hardware carrier sense feeding the CSMA gate, and
                // /ports now says so instead of reading identical to a healthy port.
                NoteDegraded(port.Id, PortComponents.Radio, $"radio ({radioConfig.Kind} on {radioEndpoint}): {ex.Message}");
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
        // defers keyups while the channel is busy - the native seam, owned by the stack rather
        // than an opaque transport wrapper. A radio without carrier-sense (or no radio at all)
        // yields a null source, i.e. the always-clear gate - byte-for-byte today's behaviour.
        // Passed on the first-class, ax25-ts-parity-tracked Ax25ListenerOptions.CarrierSense
        // member (mirrors the TS carrierSense option). (The coming Nino KISS DCD extension lands
        // in the same gate.)
        // A transport that IS its own carrier-sense source (the in-process soundmodem's
        // native DCD; a future Nino KISS DCD extension) plugs into the same gate - probe
        // the modem chain, not the decorators, which don't forward optional facets.
        ICarrierSense? carrierSense = radio is not null && radio.Capabilities.HasFlag(RadioCapabilities.CarrierSense)
            ? new RadioCarrierSense(radio)
            : modemTransport as ICarrierSense;
        // TX-complete→T1 (kiss.t1FromTxComplete): construction-time, like the
        // PacingKissModem wrap above - see KissParams.T1FromTxComplete.
        var options = BuildListenerOptions(
            effectiveAx25, port.Compat, myCall,
            restartT1OnTxComplete: effectiveKiss?.T1FromTxComplete == true,
            carrierSense: carrierSense,
            portName: endpointText,
            link: port.Link);
        // The transport speaks the neutral IAx25Transport seam the listener consumes directly.
        var listener = new Ax25Listener(transport, options, timeProvider, loggerFactory.CreateLogger<Ax25Listener>());

        // N1 (PACLEN) is carried on the live-reseed parameter record, not on the
        // parity-tracked Ax25ListenerOptions (it is node-host per-port config, not a
        // library listener flag). The constructor seeds its params from `options`, which
        // has no N1 - so reseed once now with the full MapAx25Params (which carries N1)
        // so this freshly-built listener's NEW sessions pick up the configured PACLEN. A
        // null N1 leaves the context default (256) - byte-for-byte today's behaviour.
        listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat, port.Link));
        var connector = new Ax25OutboundConnector(
            port.Id, listener, r => ClaimOutbound(port.Id, r), localOverride: null, cache: capabilityCache,
            linkPolicy: LinkPolicyFor(port.Id));
        // The arrival port id is captured here rather than reverse-looked-up from the listener:
        // it is now load-bearing (the outbound claim and the app-registration lookup are both
        // keyed on it), and a SessionAccepted racing a teardown must not resolve to "?".
        listener.SessionAccepted += (_, e) => OnSessionAccepted(port.Id, listener, connector, e.Session);

        try
        {
            await listener.StartAsync(ct).ConfigureAwait(false);
            // Target the modem chain, not the (possibly radio-tagged) outermost
            // transport - the tagging wrapper doesn't forward ICsmaChannelParams.
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
            SetState(port.Id, PortState.Faulted, "listener did not start", lastError: ex.Message, degraded: []);
            ArmRetry(port.Id);
            return;
        }

        // Rig-control (CAT) attachment: dial the rig daemon and start the status poller.
        // Placed after every throwing bring-up step so a port-level failure can't leak a
        // connected rig, and self-degrading - an unreachable rigctld/flrig must never take
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
                NoteDegraded(port.Id, PortComponents.Rig, $"rig ({rigConfig.Kind} at {rigEndpoint}): {ex.Message}");
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
        };
        var degradedComponents = AdoptRunningPort(port, running);

        // A fresh listener must answer for the app callsigns registered while the port was
        // down/restarting (the RHPv2 server's binds outlive any individual port lifecycle).
        ApplyAppCallsignsTo(running);

        // NET/ROM read-only awareness: subscribe this port's frame-trace tap so the
        // node-level service hears NODES broadcasts on it. Observation-only - it
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

        // ID beacon: arm the periodic UI-frame beacon on this port (default-off - armed
        // only when the effective beacon is enabled). Sends-only; detached on teardown.
        beacons?.AttachPort(port.Id, new ListenerBeaconChannel(listener));

        // Hoist the callsign too (CA1873) - endpointText is the one declared above.
        var callText = myCall.ToString();
        LogPortUp(port.Id, callText, endpointText);

        // Serving. A port that lost a non-data-path component on the way up (radio, rig, the
        // node-managed rigctld) is UP-BUT-DEGRADED and says which, instead of reading identical
        // to a healthy port on /ports, in metrics and in PORTS (#722).
        if (degradedComponents.Count > 0)
        {
            LogPortDegraded(port.Id, string.Join(", ", degradedComponents));
            SetState(port.Id, PortState.Degraded, "up with a component missing");
        }
        else
        {
            SetState(port.Id, PortState.Up, "up");
        }
    }

    // Publish the runtime half onto the port's entry (the one place membership is granted) and
    // report which components the bring-up recorded as missing along the way.
    private IReadOnlyList<string> AdoptRunningPort(PortConfig port, RunningPort running)
    {
        lock (ports)
        {
            var entry = ports.TryGetValue(port.Id, out var e) ? e : null;
            if (entry is null)
            {
                return [];
            }

            entry.Config = port;
            entry.Running = running;
            entry.SupervisionSuspended = false;
            CancelRetry(entry);
            return entry.Degraded.Count == 0 ? [] : [.. entry.Degraded];
        }
    }

    // Dispose a pre-opened transport the bring-up decided not to adopt (see the preOpened param).
    private static async Task DiscardPreOpenedAsync(IAx25Transport? preOpened)
    {
        if (preOpened is not null)
        {
            await preOpened.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Why a port is being torn down - it decides the state the entry lands in
    /// (and whether the entry survives at all).</summary>
    private enum TeardownReason
    {
        /// <summary>A bring-up follows immediately (restart, node-wide reset): the port stays
        /// configured.</summary>
        Restart,

        /// <summary>The port flipped to <c>enabled: false</c>.</summary>
        Disable,

        /// <summary>The port left the config: the entry goes too.</summary>
        Remove,

        /// <summary>The running port died and is being cleaned up before the retry (the caller
        /// sets <see cref="PortState.Faulted"/> itself, with the reason).</summary>
        Fault,

        /// <summary>The supervisor is being disposed.</summary>
        Shutdown,

        /// <summary>The port was stopped deliberately so something else can use its hardware
        /// (codeplug programming), and the caller will bring it back.</summary>
        Suspend,
    }

    private async Task TearDownAsync(string id, TeardownReason reason)
    {
        RunningPort? running;
        lock (ports)
        {
            if (!ports.TryGetValue(id, out var entry))
            {
                return;
            }

            // An armed retry for a port that is being removed / disabled / restarted has
            // nothing left to do; a restart's own bring-up supersedes it.
            CancelRetry(entry);
            running = entry.Running;
            entry.Running = null;
            entry.SupervisionSuspended = false;
            if (reason == TeardownReason.Remove)
            {
                ports.Remove(id);
            }
        }

        if (running is null)
        {
            // Nothing live to dispose (already faulted, retrying, or never up) - but the state
            // still moves: a disabled port must read `disabled`, not `retrying`.
            SettleAfterTeardown(id, reason);
            return;
        }

        // Flip the alive flag BEFORE anything is disposed, so a consumer holding this port
        // across an await can re-check and fail fast rather than touching a dead listener
        // (see PortSupervisor.GetPort).
        running.BeginTeardown();
        SetState(id, PortState.Stopping, reason switch
        {
            TeardownReason.Restart => "restart",
            TeardownReason.Disable => "disabled",
            TeardownReason.Remove => "removed from config",
            TeardownReason.Fault => "faulted",
            TeardownReason.Suspend => "stopped to program the radio",
            _ => "shutdown",
        });

        // Detach the NET/ROM tap and cleanly disconnect any interlink AX.25 sessions
        // on this port BEFORE disposing the listener - DetachPortAsync DISCs each
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
        SettleAfterTeardown(id, reason);
    }

    // Where a torn-down port's entry lands. A Fault teardown is the exception: the watchdog
    // sets Faulted itself, with the reason, right after this returns.
    private void SettleAfterTeardown(string id, TeardownReason reason)
    {
        if (reason is TeardownReason.Remove or TeardownReason.Fault)
        {
            return;
        }

        SetState(
            id,
            reason == TeardownReason.Disable ? PortState.Disabled : PortState.Configured,
            reason switch
            {
                TeardownReason.Disable => "disabled",
                TeardownReason.Suspend => "stopped to program the radio",
                _ => "torn down",
            },
            degraded: []);
    }

    private async Task TearDownAllAsync(TeardownReason reason)
    {
        RunningPort[] all;
        lock (ports)
        {
            all = ports.Values.Where(e => e.Running is not null).Select(e => e.Running!).ToArray();
            foreach (var entry in ports.Values)
            {
                CancelRetry(entry);
                entry.Running = null;
                entry.SupervisionSuspended = false;
            }
        }
        foreach (var p in all)
        {
            p.BeginTeardown();
            SetState(p.Id, PortState.Stopping, reason == TeardownReason.Shutdown ? "shutdown" : "node-wide reset");
            // Clean interlink DISC (bounded) before disposing the listener - see
            // TearDownAsync for the rationale (avoid leaving a neighbour a half-open
            // link it polls onto the shared channel).
            if (netRom is not null)
            {
                await netRom.DetachPortAsync(p.Id).ConfigureAwait(false);
            }

            telemetry?.DetachPort(p.Id);
            beacons?.DetachPort(p.Id);
            await p.DisposeAsync().ConfigureAwait(false);
            SettleAfterTeardown(p.Id, reason == TeardownReason.Shutdown ? TeardownReason.Shutdown : TeardownReason.Restart);
        }
    }

    private async Task ApplyKissParamsAsync(PortConfig port, CancellationToken ct)
    {
        var running = TryGetRunning(port.Id);
        if (running is null)
        {
            return;   // not up (e.g. faulted) - nothing live to tune
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
            return;   // not up (e.g. faulted) - the next bring-up reads the new config
        }

        // Resolve the profile here too so a live AX.25 reseed uses the same
        // effective values a fresh bring-up would (explicit wins, profile fills).
        var (effectiveAx25, _) = ChannelProfiles.Resolve(port);

        // Live-reseed: new sessions on this listener pick up the new AX.25 params;
        // existing sessions keep their identity and their in-flight state.
        running.Listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat, port.Link));
        RebaselineConfig(port);
        LogAx25ParamsApplied(port.Id);
    }

    private void ApplyCompat(PortConfig port)
    {
        var running = TryGetRunning(port.Id);
        if (running is null)
        {
            return;   // not up (e.g. faulted) - the next bring-up reads the new config
        }

        var (effectiveAx25, _) = ChannelProfiles.Resolve(port);

        // Same live reseed as ApplyAx25Params - the parameter record carries the
        // compat values. Parse options apply from the next inbound frame; quirks
        // seed sessions built from now on. Existing sessions untouched.
        running.Listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat, port.Link));
        RebaselineConfig(port);
        LogCompatApplied(port.Id);
    }

    private void ApplyLinkPolicy(PortConfig port)
    {
        var running = TryGetRunning(port.Id);
        if (running is null)
        {
            return;   // not up (e.g. faulted) - the next bring-up reads the new config
        }

        var (effectiveAx25, _) = ChannelProfiles.Resolve(port);

        // Same live reseed as ApplyAx25Params - the parameter record carries the listener's dial
        // defaults. The connector reads the policy itself per dial (LinkPolicyFor), so this reseed
        // is what covers the paths that dial the listener directly. Existing sessions untouched:
        // the policy gates what a FUTURE connect offers, not a link already negotiated.
        running.Listener.UpdateSessionParameters(MapAx25Params(effectiveAx25, port.Compat, port.Link));
        RebaselineConfig(port);
        var declared = PortLinkConfig.Resolve(port.Link);
        LogLinkPolicyApplied(port.Id, declared.Dial, declared.PreConnectXid);
    }

    private static async Task ApplyKissParamsToModemAsync(IAx25Transport transport, KissParams? kiss, CancellationToken ct)
    {
        // CSMA params are meaningful only on a transport that exposes them. A transport
        // with no CSMA channel (none today - AXUDP exposes them as no-ops through the
        // migration shim) is simply skipped, preserving today's behaviour.
        if (transport is not ICsmaChannelParams csma)
        {
            return;
        }

        // TXDELAY/PERSIST/SLOTTIME stay opt-in - unset means "leave the modem at its
        // own default", because the right value for those is firmware-specific and a
        // wrong guess degrades CSMA. TXTAIL is different (#465): its default is an
        // IMPLICIT 0, sent UNCONDITIONALLY on every apply - bring-up, the regular
        // KISS-param cadence, and a hot config change - so the modem always gets a
        // deterministic, explicit tail. 0 is correct for most paths (a NinoTNC into a
        // fully analogue audio path, even on a slow AFSK1200 channel); a non-zero tail
        // is a MODEM + radio-audio-path-latency property (a software modem - samoyed /
        // Dire Wolf - or a NinoTNC into a non-zero-latency audio path), which the node
        // can't infer, so the operator sets `kiss.txTail` per port and that explicit
        // value wins here (the `?? 0` only supplies the default when unset).
        // The config knobs are int? (so an out-of-range value is a named 422 from
        // KissParamsValidator rather than an opaque model-binding 400 - #672), while
        // ICsmaChannelParams is byte, which is the wire truth. Clamp rather than cast:
        // config reaching here has been validated to 0..255, so the clamp is unreachable
        // for a validated path, but an unchecked cast would silently wrap a bad value
        // into a plausible one (300 → 44) if a future path ever skipped validation.
        static byte ToWire(int value) => (byte)Math.Clamp(value, 0, 255);

        if (kiss?.TxDelay is { } txd)
        {
            await csma.SetTxDelayAsync(ToWire(txd), ct).ConfigureAwait(false);
        }

        if (kiss?.Persistence is { } per)
        {
            await csma.SetPersistenceAsync(ToWire(per), ct).ConfigureAwait(false);
        }

        if (kiss?.SlotTime is { } slot)
        {
            await csma.SetSlotTimeAsync(ToWire(slot), ct).ConfigureAwait(false);
        }

        await csma.SetTxTailAsync(ToWire(kiss?.TxTail ?? 0), ct).ConfigureAwait(false);
    }

    // Update the stored baseline config for a port a hot apply just tuned, so the doctor,
    // the hail service and anything else asking "what is this port running on" sees the
    // values that are actually live. The baseline lives on the port ENTRY (the owner), not
    // on the RunningPort: there is exactly one per-port config record (#722), so a hot apply
    // can never leave two of them disagreeing - and the old bug class where rebaselining
    // rebuilt the runtime half and dropped the members it forgot (the rig trio, C068) is
    // structurally gone, because nothing is rebuilt.
    private void RebaselineConfig(PortConfig port)
    {
        lock (ports)
        {
            if (ports.TryGetValue(port.Id, out var entry))
            {
                entry.Config = port;
            }
        }
    }

    // A live reader for one port's declared link policy (PortConfig.Link). Handed to every
    // Ax25OutboundConnector so a hot `link:` edit reaches the long-lived connector the bring-up
    // built, and to NetRomService for its interlink dials. Reads the CURRENT config rather than a
    // captured snapshot, so it needs no reconcile of its own beyond the listener reseed.
    private Func<PortLinkConfig?> LinkPolicyFor(string portId) => () => LinkPolicyOf(portId);

    private PortLinkConfig? LinkPolicyOf(string portId) =>
        config.Current.Ports.FirstOrDefault(p => string.Equals(p.Id, portId, StringComparison.Ordinal))?.Link;

    private static Ax25ListenerOptions BuildListenerOptions(
        Ax25PortParams? ax25, PortCompatConfig? compat, Callsign myCall,
        bool restartT1OnTxComplete = false, ICarrierSense? carrierSense = null,
        string? portName = null, PortLinkConfig? link = null)
    {
        var p = MapAx25Params(ax25, compat, link);
        return new Ax25ListenerOptions
        {
            MyCall = myCall,
            PortName = portName,
            T1V = p.T1V,
            T2 = p.T2,
            T3 = p.T3,
            N2 = p.N2,
            K = p.K,
            MaxCachedPeers = p.MaxCachedPeers,
            ParseOptions = p.ParseOptions,
            Quirks = p.Quirks,
            // The port's declared link policy, seeded onto the listener's own dial defaults so a
            // path that bypasses Ax25OutboundConnector (a bare listener.ConnectAsync) still
            // honours a `dial: v20` / `preConnectXid: off` port. Both are existing, parity-tracked
            // Ax25ListenerOptions members - no new library flag.
            PreferExtendedConnect = p.PreferExtendedConnect,
            PreConnectXidNegotiatesSrej = p.PreConnectXidNegotiatesSrej,
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
    private static Ax25SessionParameters MapAx25Params(
        Ax25PortParams? ax25, PortCompatConfig? compat, PortLinkConfig? link = null) => new()
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
        // Per-port link policy → the listener's dial defaults. Null / all-auto resolves to the
        // engine's own defaults (prefer v2.2, pre-connect XID on), so an absent link: block is
        // byte-for-byte today's behaviour. Live-reseedable like the rest of this record: it gates
        // FUTURE dials only, never a link already up.
        PreferExtendedConnect = PortLinkConfig.Resolve(link).PrefersExtendedConnect,
        PreConnectXidNegotiatesSrej = PortLinkConfig.Resolve(link).PreConnectXidNegotiatesSrej,
    };

    private void OnSessionAccepted(string portId, Ax25Listener listener, Ax25OutboundConnector connector, Ax25Session session)
    {
        // A session we are dialling OUT to (the console's Connect command) also
        // raises SessionAccepted on this listener - but it is NOT an inbound
        // caller, so we must not start a node console against it (that would spew
        // our prompt at the station we connected to). The connector claims the
        // (port, remote) for the duration of the connect; comparing THIS port is what
        // keeps a same-callsign caller arriving on another port from being swallowed.
        if (IsOutbound(portId, session.Context.Remote))
        {
            return;
        }

        // Cutover observability: a genuine inbound caller (the outbound guard above ruled out
        // our own connect-out). Logged once here, before the console/app split, so every
        // accepted inbound circuit is positively visible - not just faults. Guarded so the
        // ToString() is skipped when Information is off (CA1873).
        if (logger.IsEnabled(LogLevel.Information))
        {
            var peer = session.Context.Remote.ToString();
            LogInboundSessionAccepted(peer, portId);
        }

        // An inbound session addressed to an APP callsign (the session's Local is a
        // registered alias, not the port's own call) routes to the app's handler -
        // the RHPv2 server's accept path - never to the node console.
        if (!session.Context.Local.Equals(listener.MyCall))
        {
            OnAppSessionAccepted(portId, listener, session);
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
                    var env = new NodeConsoleEnvironment(
                        config, routed, netRom, sysopContext, applicationHost, CreateConnectRouter(routed), capabilityCache,
                        heard: null, portHealth: this);
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
    private void OnAppSessionAccepted(string portId, Ax25Listener listener, Ax25Session session)
    {
        if (!consoleSessions.TryAdd(session, 0))
        {
            return;
        }

        // Resolved by (Local, ARRIVAL PORT), wildcard as fallback (#723 item 2). An app bound to
        // port A must not answer a caller who reached us on port B: no registration for this
        // (callsign, port) is a disconnect, not a hand-off to whoever bound it elsewhere.
        AppCallsignRegistration? registration = FindAppRegistration(session.Context.Local, portId);

        _ = Task.Run(async () =>
        {
            var connection = new Ax25NodeConnection(listener, session);
            try
            {
                if (registration is null)
                {
                    LogAppSessionUnclaimed(session.Context.Local.ToString(), session.Context.Remote.ToString(), portId);
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

    /// <summary>
    /// Open the port's radio-control attachment, retrying a bounded number of times before giving
    /// up and letting the caller degrade the port.
    /// </summary>
    /// <remarks>
    /// The failure this exists for is a radio that is briefly not there: a Tait resets itself at
    /// the end of a codeplug programming session and spends several seconds booting, during which
    /// a serial-bound scan finds nothing and a port-bound open answers nothing. That used to leave
    /// the port serving traffic with no radio until an operator noticed and restarted it by hand,
    /// because a degraded component arms no retry of its own (see <see cref="RadioOpenAttempts"/>).
    /// An unsupported radio kind is not retried - it is a config fact, and no amount of waiting
    /// changes it.
    /// </remarks>
    private async Task<IRadioControl> OpenRadioAsync(
        string portId,
        PortRadioConfig radioConfig,
        HeadEndDeviceResolver? resolver,
        PortRigConfig? rig,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await radioFactory.CreateAsync(radioConfig, timeProvider, resolver, rig, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not NotSupportedException
                                           and not OperationCanceledException
                                           && attempt < RadioOpenAttempts)
            {
                LogRadioOpenRetry(portId, attempt, RadioOpenAttempts, ex.Message);
                await Task.Delay(RadioOpenRetryDelay, timeProvider, ct).ConfigureAwait(false);
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
        // Cancelling the lifecycle stops the bring-up retry loops and the running-state
        // watchdog; taking the mutation gate then waits out any attempt already mid-bring-up so
        // the teardown can't interleave with it (the cancelled token makes that attempt fail
        // fast and clean itself up).
        await lifecycle.CancelAsync().ConfigureAwait(false);
        await mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await TearDownAllAsync(TeardownReason.Shutdown).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: radio control attached ({Kind} on {RadioPort}) - inbound frames carry RSSI/SNR metadata.")]
    private partial void LogRadioAttached(string id, string kind, string radioPort);

    // Per-frame soundmodem FEC early-warning (#635): only emitted when a frame actually needed
    // repair (CorrectedBytes > 0), so a clean link stays silent. Not called "BER" - it is an
    // honest byte-error-rate floor, not a bit-error rate.
    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: inbound {Mode} frame needed FEC repair - {CorrectedBytes} of {FrameBytes} byte(s) corrected (early warning: persistently non-zero means the link is spending its error budget).")]
    private partial void LogSoundModemFecCorrections(string id, string mode, int correctedBytes, int frameBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: radio control attached ({Kind} on {RadioPort}) without RSSI reads - capabilities: {Capabilities}; inbound frames carry no signal metadata.")]
    private partial void LogRadioAttachedNoRssi(string id, string kind, string radioPort, RadioCapabilities capabilities);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: radio control did not open (attempt {Attempt} of {Attempts}); retrying. ({Reason})")]
    private partial void LogRadioOpenRetry(string id, int attempt, int attempts, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: radio control ({Kind} on {RadioPort}) failed to open; the port runs WITHOUT radio metadata.")]
    private partial void LogRadioFaulted(Exception ex, string id, string kind, string radioPort);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: rig control attached ({Kind} at {Endpoint}) - frequency/mode/PTT/meters are polled and projected on /api/v1/rigs.")]
    private partial void LogRigAttached(string id, string kind, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: rig control ({Kind} at {Endpoint}) failed to connect; the port runs WITHOUT rig status.")]
    private partial void LogRigFaulted(Exception ex, string id, string kind, string endpoint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id}: node-managed rigctld for {Device} failed to come up ({Reason}); the port runs WITHOUT rig control.")]
    private partial void LogRigDaemonFailed(string id, string device, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id} faulted: {Reason}")]
    private partial void LogPortFaulted(string id, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Port {Id}: bring-up retry still failing ({Reason}).")]
    private partial void LogPortRetryStillDown(string id, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Port {Id} is up but DEGRADED - running without: {Components}. The packet channel carries traffic; see the warnings above for why.")]
    private partial void LogPortDegraded(string id, string components);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: KISS parameters applied live (no restart).")]
    private partial void LogKissParamsApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: AX.25 parameters reseeded live; new sessions use them (existing sessions untouched).")]
    private partial void LogAx25ParamsApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: AX.25 compatibility profile applied live - inbound parsing from the next frame, session quirks for new sessions (existing sessions untouched).")]
    private partial void LogCompatApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: link policy applied live - dial {Dial}, pre-connect XID {PreConnectXid}; the next outbound connect uses it (links already up untouched).")]
    private partial void LogLinkPolicyApplied(string id, LinkDialPreference dial, LinkPreConnectXid preConnectXid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: NET/ROM route quality applied live; the next NODES broadcast on this port uses it.")]
    private partial void LogNetRomQualityApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: ID-beacon settings applied live; the beacon timer re-armed from the new values.")]
    private partial void LogBeaconApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: MQTT instance label applied live; the next published frame uses the new topic segment.")]
    private partial void LogMqttInstanceApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbound session accepted from {Peer} on port {Port}.")]
    private partial void LogInboundSessionAccepted(string peer, string port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console session for {PeerId} faulted.")]
    private partial void LogConsoleFaulted(Exception ex, string peerId);

    [LoggerMessage(Level = LogLevel.Information, Message = "App callsign {Callsign} registered (port {Port}) - the node now answers for it.")]
    private partial void LogAppCallsignRegistered(Callsign callsign, string port);

    [LoggerMessage(Level = LogLevel.Information, Message = "App callsign {Callsign} unregistered.")]
    private partial void LogAppCallsignUnregistered(Callsign callsign);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound session to app callsign {Local} from {Remote} on port {Port} had no live registration for that port; disconnected.")]
    private partial void LogAppSessionUnclaimed(string local, string remote, string port);

    [LoggerMessage(Level = LogLevel.Information, Message = "App callsign {Callsign} re-scoped from renamed port {OldPort} to {NewPort} - it keeps answering there.")]
    private partial void LogAppCallsignRescoped(Callsign callsign, string oldPort, string newPort);

    [LoggerMessage(Level = LogLevel.Error, Message = "Config apply REFUSED: {Reason} The node keeps its current identity and every app binding; fix the config (or unbind the app) and apply again.")]
    private partial void LogApplyRefused(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "App-session handler for {Local} faulted.")]
    private partial void LogAppSessionFaulted(Exception ex, string local);
}
