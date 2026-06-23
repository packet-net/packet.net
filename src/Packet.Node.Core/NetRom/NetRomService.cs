using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.NetRom;
using Packet.NetRom.Routing;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;

namespace Packet.Node.Core.NetRom;

/// <summary>
/// The node-level NET/ROM service: a singleton above the <see cref="Hosting.PortSupervisor"/>
/// that hears NODES routing broadcasts on every AX.25 port and maintains a
/// <see cref="NetRomRoutingTable"/> (the read-only awareness slice), and — when the
/// operator opts in — <b>originates</b> its own NODES broadcast on the NODESINTERVAL
/// schedule (L3 origination) and runs the <b>L4 virtual-circuit</b> layer over
/// connected-mode AX.25 interlinks (a <see cref="CircuitManager"/>) so
/// <c>connect &lt;alias&gt;</c> routes a user to a distant node across the network.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hearing can never disturb a QSO.</b> The read-only tap is the existing
/// <see cref="Ax25Listener.FrameTraced"/> event (fires before address filtering, so
/// it hears NODES — dest <c>NODES</c>, not us — with no engine change) and only
/// reads. The TX-bearing behaviours are opt-in (<see cref="NetRomConfig.Broadcast"/>
/// / a <see cref="NetRomConfig.Routing"/> mode that opens interlinks) and default off.
/// </para>
/// <para>
/// <b>Interlinks.</b> The service owns the connected-mode AX.25 sessions (PID 0xCF)
/// to neighbours: it dials one out (cached per neighbour) to carry a circuit's
/// datagrams, and it taps inbound 0xCF data on every session (the supervisor's
/// console ignores 0xCF — see <see cref="Ax25NodeConnection"/>) to feed the
/// circuit manager. An inbound circuit (a user routed to us via NET/ROM) is bridged
/// to a fresh node console via the injected <see cref="RunInboundConsole"/> hook.
/// </para>
/// </remarks>
public sealed partial class NetRomService : INetRomRoutingView, IDisposable, IAsyncDisposable
{
    // States from which an interlink AX.25 session still owes the peer a clean
    // DISC. Mirrors Ax25NodeConnection's teardown set — anything that isn't fully
    // Disconnected leaves the neighbour with a half-open link it will poll.
    private static readonly string[] LiveSessionStates =
        ["Connected", "TimerRecovery", "AwaitingConnection", "AwaitingV22Connection", "AwaitingRelease"];

    // How long to wait for an interlink's DISC/UA to settle on the wire before we
    // give up and drop the socket anyway. Bounded so teardown can never hang — a
    // peer that won't UA still gets the DISC frame on the wire (which is what stops
    // it polling); the wait only buys the clean DISC/UA round-trip when the channel
    // is healthy.
    private static readonly TimeSpan InterlinkDisconnectGrace = TimeSpan.FromSeconds(8);

    // How long after the last NODES ingest to wait before persisting the table — a
    // debounce so a burst of broadcast frames produces one write, not dozens.
    private static readonly TimeSpan PersistDebounce = TimeSpan.FromSeconds(30);

    private readonly NetRomConfig config;
    // The node's own NET/ROM alias for the NODES broadcast — unified with Identity.Alias (the
    // single node-name concept); passed in at construction since identity.alias is node-reset impact.
    private readonly string? nodeAlias;
    // The resolved routing role (Endpoint/Transit ⇒ interlinks; Transit ⇒ also relay
    // transit). Resolved once from config.ResolveRouting() so every gate below reads one
    // settled value, not the raw routing/connect/forward fields.
    private readonly NetRomRouting routing;
    private readonly NetRomRoutingTable table;
    private readonly NetRomRoutingOptions routingOptions;
    private readonly NetRomCircuitOptions circuitOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NetRomService> logger;
    private readonly ITimer? sweepTimer;
    private readonly CircuitManager? circuits;
    private readonly INetRomRoutingStore? store;
    private readonly ITimer? persistTimer;

    // The per-peer AX.25 capability cache (optional). Null ⇒ today's behaviour exactly:
    // interlinks dial hard-coded mod-8 with the listener's pre-connect-XID default, and
    // nothing is recorded. Non-null ⇒ each interlink dial consults PlanDial for the
    // version + XID probe and records the OUTCOME of a RETURNED dial (never on a throw —
    // a dial that throws yielded no link of either version, so it carries no capability
    // signal; that is the correctness hinge).
    private readonly PeerCapabilityCache? capabilityCache;

    // The INP3 routing overlay (link timing + RIF ingest/emit + triggered updates).
    // Null ⇔ INP3 disabled ⇔ the node is byte-for-byte today: no Inp3Engine, no
    // Inp3UpdateScheduler, no INP3 timer, no SendL3Rtt/Advertise/NeighbourDown wiring.
    // Created only under config.Enabled && interlinks-enabled (routing Endpoint/Transit)
    // && config.Inp3.Enabled. Every seam in NetRomService is `inp3?.` so the default-off
    // guarantee is structural, not a scatter of `if`s. The implementation lives in
    // NetRomService.Inp3.cs.
    private readonly Inp3Host? inp3;

    // Live port attachments: portId -> attachment. Concurrent because attach/detach
    // run on the reconcile worker while FrameTraced fires on listener pump threads.
    private readonly ConcurrentDictionary<string, Attachment> attachments = new(StringComparer.Ordinal);

    // Interlink sessions to neighbours, keyed by neighbour callsign.
    private readonly ConcurrentDictionary<Callsign, Interlink> interlinks = new();

    // Sessions we have already tapped for inbound 0xCF (so we attach the tap once).
    private readonly ConcurrentDictionary<Ax25Session, byte> tapped = new();

    // Our node callsign (set on first attach; all ports share the node identity).
    private Callsign nodeCall;
    private bool nodeCallSet;

    // L3 transit-forwarding throughput counters (the /metrics exporter's "forwarding"
    // bucket — #457). Bumped only on the transit-forward path (ForwardDatagram); a
    // datagram addressed to us terminates here and never touches these. Interlocked
    // because ForwardDatagram runs on listener pump threads. Bytes are the encoded
    // NET/ROM datagram length (the L3 PDU forwarded on toward the destination).
    private long forwardedFrames;
    private long forwardedBytes;
    private long droppedTtl;
    private long droppedLooped;
    private long droppedNoRoute;

    private int disposed;

    /// <inheritdoc/>
    public bool Enabled => config.Enabled;

    /// <summary>
    /// A snapshot of the L3 transit-forwarding throughput counters (frames/bytes forwarded
    /// + the three drop reasons), for the Prometheus <c>/metrics</c> exporter's forwarding
    /// bucket (#457). All-zero on an endpoint-only / disabled node (nothing is ever forwarded).
    /// The counters are monotonic for the lifetime of the service (process uptime), which is the
    /// counter semantics Prometheus expects.
    /// </summary>
    public NetRomForwardingStats ForwardingStats => new(
        ForwardedFrames: Interlocked.Read(ref forwardedFrames),
        ForwardedBytes: Interlocked.Read(ref forwardedBytes),
        DroppedTtlExpired: Interlocked.Read(ref droppedTtl),
        DroppedLooped: Interlocked.Read(ref droppedLooped),
        DroppedNoRoute: Interlocked.Read(ref droppedNoRoute));

    /// <summary>True if NET/ROM L4 connect-routing is enabled (interlinks + circuits) —
    /// the routing role opens connected-mode interlinks (<see cref="NetRomRouting.Endpoint"/>
    /// or <see cref="NetRomRouting.Transit"/>). The successor to the old <c>connect</c>
    /// capability.</summary>
    public bool ConnectEnabled => config.Enabled && routing is NetRomRouting.Endpoint or NetRomRouting.Transit;

    /// <summary>True if this node forwards transit datagrams (the network-layer
    /// routing role) — only under <see cref="NetRomRouting.Transit"/>. The successor to
    /// the old <c>connect &amp;&amp; forward</c> gate; <see cref="NetRomRouting.Endpoint"/> ⇒
    /// interlinks but endpoint-only (no transit).</summary>
    public bool ForwardEnabled => config.Enabled && routing == NetRomRouting.Transit;

    // Whether this node opens connected-mode interlinks at all (the routing role is
    // Endpoint or Transit). The construction-time gate for the CircuitManager / INP3 /
    // the per-port session tap — what the old `config.Connect` gated. Equivalent to
    // ConnectEnabled, named for the construction seam where it reads more clearly.
    private bool InterlinksEnabled => config.Enabled && routing is NetRomRouting.Endpoint or NetRomRouting.Transit;

    /// <summary>
    /// The hook the supervisor supplies to run a node console over an inbound NET/ROM
    /// circuit (a user routed to us reaching the prompt). Null = inbound circuits are
    /// accepted but not bridged to a console (still useful for transit/tests).
    /// </summary>
    public Func<INodeConnection, CancellationToken, Task>? RunInboundConsole { get; set; }

    /// <summary>
    /// The hook the supervisor supplies to dial an interlink AX.25 session to a
    /// neighbour with the <em>outbound claim</em> held — so the supervisor does not
    /// start a node console against the neighbour we dialled (an interlink carries
    /// NET/ROM datagrams, not console text; a console against it would flood the
    /// session with banner/prompt frames and starve the circuit). When null, the
    /// service dials the listener directly (the unit-test path, where no supervisor
    /// console competes).
    /// </summary>
    /// <remarks>
    /// Carries the <see cref="PeerDialPlan"/> the service computed from the per-peer
    /// capability cache (version + pre-connect-XID), so the supervisor's claim-aware
    /// dial uses the very same plan the direct-fallback path does.
    /// </remarks>
    public Func<string, Callsign, PeerDialPlan, CancellationToken, Task<Ax25Session>>? OpenInterlink { get; set; }

    /// <summary>The circuit manager (null when NET/ROM connect is disabled). Exposed
    /// for the outbound connector + tests.</summary>
    public CircuitManager? Circuits => circuits;

    /// <summary>Test seam (InternalsVisibleTo <c>Packet.Node.Tests</c>): the live
    /// routing table, so a test can prime routes and inspect link-down failover
    /// directly instead of staging NODES traffic over a bus.</summary>
    internal NetRomRoutingTable RoutingTable => table;

    /// <summary>Construct the service from the node's NET/ROM config. When
    /// <paramref name="store"/> is supplied the learned routing table is persisted to it
    /// — hydrated (downtime-aged) on construction and saved on the sweep tick, after a
    /// debounced ingest, and on graceful dispose — so a restart does not lose the
    /// learned topology. Null = in-memory only (the default; every existing call site
    /// and test is unchanged).</summary>
    /// <summary>
    /// Optional source of <b>app NET/ROM adverts</b> (<c>docs/app-packages.md</c> § Application
    /// packet identity): returns the extra destination entries to append to our NODES broadcast —
    /// one per enabled app whose owner set <c>netrom.alias</c>, each pointing the alias at the
    /// app's resolved callsign with the configured quality (best-neighbour = this node). Null /
    /// returns-empty ⇒ nothing extra advertised (the opt-in default, off). Read fresh on each
    /// broadcast so an alias edit hot-applies. Set by the composition root, which holds the
    /// catalog + the node callsign authority the resolution needs.
    /// </summary>
    public Func<IReadOnlyList<NodesBroadcastBuilder.Entry>>? AppAdvertSource { get; set; }

    public NetRomService(
        NetRomConfig config,
        TimeProvider? timeProvider = null,
        ILogger<NetRomService>? logger = null,
        INetRomRoutingStore? store = null,
        PeerCapabilityCache? capabilityCache = null,
        string? nodeAlias = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.nodeAlias = nodeAlias;
        routing = this.config.EffectiveRouting;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<NetRomService>.Instance;
        this.store = store;
        this.capabilityCache = capabilityCache;

        routingOptions = ResolveRoutingOptions(config);
        circuitOptions = ResolveCircuitOptions(config);
        table = new NetRomRoutingTable(routingOptions, this.timeProvider);

        if (config.Enabled)
        {
            var interval = SweepInterval(config);

            // Hydrate the table from the store BEFORE the sweep timer arms, ageing the
            // persisted routes by however many broadcast intervals elapsed while we
            // were down (so a long-dead route is not restored at full obsolescence).
            HydrateFromStore(interval);

            // The NODESINTERVAL tick: ages the table (obsolescence) and, when
            // broadcast is on, originates our NODES broadcast. It never disturbs a
            // session — broadcasts are UI frames, the sweep is pure table maintenance.
            sweepTimer = this.timeProvider.CreateTimer(_ => OnInterval(), state: null, dueTime: interval, period: interval);

            // A one-shot debounce that persists the table a short while after the last
            // ingest, so routes learned between sweeps survive an ungraceful crash.
            // Idle until ArmPersist() fires it.
            if (store is not null)
            {
                persistTimer = this.timeProvider.CreateTimer(
                    _ => SaveSnapshot(), state: null, dueTime: Timeout.InfiniteTimeSpan, period: Timeout.InfiniteTimeSpan);
            }

            if (InterlinksEnabled)
            {
                circuits = new CircuitManager(default, circuitOptions, this.timeProvider, tickInterval: TimeSpan.FromSeconds(1));
                circuits.SendPacket = SendNetRomPacket;
                circuits.IncomingCircuit += OnIncomingCircuit;

                // INP3 rides on the connected-mode interlink machinery (the L3RTT / RIF
                // frames are 0xCF I-frames on the same interlink sessions L4 uses), so it
                // is created only when interlinks are enabled (routing Endpoint/Transit) —
                // and then only when the operator opts in. When inp3 is null the node is
                // byte-for-byte today (design §1).
                if (config.Inp3.Enabled)
                {
                    inp3 = new Inp3Host(this, config.Inp3, this.timeProvider);
                }
            }
        }
    }

    private static TimeSpan SweepInterval(NetRomConfig config) =>
        TimeSpan.FromSeconds(config.SweepIntervalSeconds is > 0 ? config.SweepIntervalSeconds.Value : 3600);

    // Load the persisted routing table (if any) into the fresh table, ageing each route
    // by the elapsed downtime measured in sweep intervals. Resilient: the store returns
    // null on any failure, and Restore of an empty/odd snapshot is harmless.
    private void HydrateFromStore(TimeSpan sweepInterval)
    {
        if (store is null || store.Load() is not { } persisted)
        {
            return;
        }

        int decay = 0;
        var elapsed = timeProvider.GetUtcNow() - persisted.SavedAt;
        if (elapsed > TimeSpan.Zero && sweepInterval > TimeSpan.Zero)
        {
            decay = (int)Math.Min(elapsed.Ticks / sweepInterval.Ticks, int.MaxValue);
        }

        table.Restore(persisted.Snapshot, decay);
        LogRestored(persisted.Snapshot.DestinationCount, persisted.Snapshot.NeighbourCount, decay);
    }

    private static NetRomRoutingOptions ResolveRoutingOptions(NetRomConfig config)
    {
        var d = NetRomRoutingOptions.Default;
        return d with
        {
            DefaultNeighbourQuality = config.DefaultNeighbourQuality ?? d.DefaultNeighbourQuality,
            MinQuality = config.MinQuality ?? d.MinQuality,
            ObsoleteInitial = config.ObsoleteInitial ?? d.ObsoleteInitial,
            ObsoleteMinimum = config.ObsoleteMinimum ?? d.ObsoleteMinimum,
        };
    }

    private static NetRomCircuitOptions ResolveCircuitOptions(NetRomConfig config)
    {
        var d = NetRomCircuitOptions.Default;
        return d with
        {
            WindowSize = config.Window ?? d.WindowSize,
            RetransmitTimeout = config.TransportTimeoutSeconds is > 0
                ? TimeSpan.FromSeconds(config.TransportTimeoutSeconds.Value) : d.RetransmitTimeout,
            MaxRetries = config.TransportRetries ?? d.MaxRetries,
            TimeToLive = config.TimeToLive is > 0 and <= 255 ? (byte)config.TimeToLive.Value : d.TimeToLive,
            CompressionEnabled = config.Compress,
        };
    }

    /// <summary>
    /// Begin hearing NODES on a port and, when connect is enabled, make the port
    /// available for interlinks. No-op if NET/ROM is disabled or already attached.
    /// </summary>
    /// <param name="portId">The node-host port id.</param>
    /// <param name="myCall">This node's callsign (all ports share the identity).</param>
    /// <param name="listener">The port's AX.25 listener whose frame/session taps we subscribe.</param>
    /// <param name="neighbourQuality">
    /// The per-port NET/ROM route quality (BPQ per-port <c>QUALITY</c>) to assume for a
    /// directly-heard neighbour on this port. <c>null</c> (the default) ⇒ the node-wide
    /// <see cref="NetRomConfig.DefaultNeighbourQuality"/> — byte-for-byte the prior behaviour.
    /// When set, routes learned on this port are quality-combined against this value, so a
    /// mixed-grade node advertises an accurate per-port quality.
    /// </param>
    /// <param name="minQuality">
    /// The per-port NET/ROM minimum quality (BPQ per-port <c>MINQUAL</c>): the worst quality a
    /// route learned on this port may have and still be kept. <c>null</c> (the default) ⇒ the
    /// node-wide <see cref="NetRomConfig.MinQuality"/> — byte-for-byte the prior behaviour. When
    /// set, a route heard on this port deriving below it is not kept (the route-keep decision).
    /// </param>
    /// <param name="nodesPaclen">
    /// The per-port NODES-broadcast UI-frame octet cap (BPQ per-port <c>NODESPACLEN</c>):
    /// our NODES broadcast on this port fragments into frames no larger than this. <c>null</c>
    /// (the default) ⇒ no cap (the canonical 11-entries-per-frame structural limit) —
    /// byte-for-byte the prior behaviour.
    /// </param>
    public void AttachPort(string portId, Callsign myCall, Ax25Listener listener, int? neighbourQuality = null, int? minQuality = null, int? nodesPaclen = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        ArgumentNullException.ThrowIfNull(listener);
        if (!config.Enabled || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        // Pin the node callsign (all ports share the node identity) the first time so
        // the circuit manager + origination use it.
        if (!nodeCallSet)
        {
            nodeCall = myCall;
            nodeCallSet = true;
            circuits?.SetLocalNode(myCall);
            inp3?.SetLocalNode(myCall);   // INP3 engine stamps it into probes + the reflection self-test
        }

        void FrameHandler(object? sender, Ax25FrameEventArgs e) => OnFrameTraced(portId, myCall, e);
        void SessionHandler(object? sender, Ax25SessionEventArgs e) => OnSessionAccepted(portId, e.Session);

        var attachment = new Attachment(portId, myCall, listener, FrameHandler, SessionHandler, neighbourQuality, minQuality, nodesPaclen);
        if (!attachments.TryAdd(portId, attachment))
        {
            return;
        }

        listener.FrameTraced += FrameHandler;
        if (InterlinksEnabled)
        {
            // Tap inbound NET/ROM data on every session this port accepts so the
            // circuit manager sees interlink datagrams.
            listener.SessionAccepted += SessionHandler;
        }

        var callText = myCall.ToString();
        LogAttached(portId, callText);
    }

    /// <summary>
    /// Hot-apply the per-port NET/ROM awareness/advertisement knobs — route quality (BPQ
    /// per-port <c>QUALITY</c>), minimum quality (<c>MINQUAL</c>), and the NODES-broadcast
    /// UI-frame cap (<c>NODESPACLEN</c>) — to a running attachment without re-subscribing its
    /// taps. No-op if the port isn't attached. All three affect only how the <em>next</em>
    /// NODES ingest/broadcast on this port is handled (read-only awareness + outbound
    /// advertisement) — none can ever disturb a live session — so they are applied live rather
    /// than via a port restart.
    /// </summary>
    public void UpdatePortQuality(string portId, int? neighbourQuality, int? minQuality = null, int? nodesPaclen = null)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (attachments.TryGetValue(portId, out var existing) &&
            (existing.NeighbourQuality != neighbourQuality ||
             existing.MinQuality != minQuality ||
             existing.NodesPaclen != nodesPaclen))
        {
            attachments[portId] = existing with
            {
                NeighbourQuality = neighbourQuality,
                MinQuality = minQuality,
                NodesPaclen = nodesPaclen,
            };
        }
    }

    /// <summary>Stop hearing NODES on a port and unsubscribe its taps. Learned routes
    /// survive. Interlinks on the port are <b>disconnected</b> (a best-effort DISC is
    /// posted so the neighbour doesn't keep a half-open AX.25 link) and dropped. For a
    /// clean DISC/UA round-trip on the wire before the listener is disposed, prefer
    /// <see cref="DetachPortAsync"/>.</summary>
    public void DetachPort(string portId)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (attachments.TryRemove(portId, out var attachment))
        {
            attachment.Listener.FrameTraced -= attachment.FrameHandler;
            attachment.Listener.SessionAccepted -= attachment.SessionHandler;

            // Drop interlinks running on this port, posting a best-effort DISC first
            // so the neighbour tears its half of the AX.25 link down (otherwise it is
            // left with a half-open session it polls — channel noise / interop flake).
            foreach (var (nbr, link) in interlinks)
            {
                if (string.Equals(link.PortId, portId, StringComparison.Ordinal))
                {
                    RequestInterlinkDisconnect(link);
                    interlinks.TryRemove(nbr, out Interlink? _);
                }
            }
            LogDetached(portId);
        }
    }

    /// <summary>
    /// Async counterpart to <see cref="DetachPort"/>: gracefully <b>disconnects</b>
    /// the port's interlink AX.25 sessions — posting the DISC and waiting (bounded)
    /// for each to reach Disconnected so the DISC/UA round-trips on the wire — before
    /// the caller disposes the listener. Use this on port teardown / reconfigure so a
    /// neighbour is never left with a half-open interlink it polls (the #309
    /// contamination class). Learned routes survive.
    /// </summary>
    public async Task DetachPortAsync(string portId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(portId);
        if (!attachments.TryRemove(portId, out var attachment))
        {
            return;
        }
        attachment.Listener.FrameTraced -= attachment.FrameHandler;
        attachment.Listener.SessionAccepted -= attachment.SessionHandler;

        var onPort = interlinks
            .Where(kv => string.Equals(kv.Value.PortId, portId, StringComparison.Ordinal))
            .ToArray();
        foreach (var (nbr, link) in onPort)
        {
            interlinks.TryRemove(nbr, out Interlink? _);
            await CloseInterlinkAsync(link, ct).ConfigureAwait(false);
        }
        LogDetached(portId);
    }

    // ─── L4: interlink teardown ─────────────────────────────────────────

    // Post a best-effort DISC to an interlink session if it is still live. Fire and
    // forget: the SDL serialises the DISC frame onto the wire on its pump. Used by
    // the synchronous Dispose / DetachPort paths where we cannot await the round-trip
    // (the caller must keep the listener alive a moment for the frame to flush).
    private static void RequestInterlinkDisconnect(Interlink link)
    {
        var session = link.Session;
        if (session is null)
        {
            return;
        }
        try
        {
            if (LiveSessionStates.Contains(session.CurrentState, StringComparer.Ordinal))
            {
                session.PostEvent(new DlDisconnectRequest());
            }
        }
        catch
        {
            // Best-effort teardown; never throw while tearing an interlink down.
        }
    }

    // Gracefully disconnect an interlink: post the DISC and wait (bounded) for the
    // session to reach Disconnected, so the DISC/UA round-trips on the wire before
    // the listener/socket is dropped. Best-effort — a peer that never UAs still got
    // the DISC frame (which stops it polling); we just stop waiting at the grace cap.
    private async Task CloseInterlinkAsync(Interlink link, CancellationToken ct)
    {
        var session = link.Session;
        if (session is null)
        {
            return;
        }
        try
        {
            if (!LiveSessionStates.Contains(session.CurrentState, StringComparer.Ordinal))
            {
                return;
            }

            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnSignal(object? _, DataLinkSignal sig)
            {
                if (sig is DataLinkDisconnectConfirm or DataLinkDisconnectIndication)
                {
                    disconnected.TrySetResult();
                }
            }
            session.DataLinkSignalEmitted += OnSignal;
            try
            {
                session.PostEvent(new DlDisconnectRequest());

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(InterlinkDisconnectGrace);
                // Poll the state too (covers a DISC that completes without a signal we
                // subscribed for, and lets us exit the moment it is Disconnected).
                while (!cts.IsCancellationRequested &&
                       !disconnected.Task.IsCompleted &&
                       !string.Equals(session.CurrentState, "Disconnected", StringComparison.Ordinal))
                {
                    try { await Task.Delay(50, cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally
            {
                session.DataLinkSignalEmitted -= OnSignal;
            }
            var neighbourText = session.Context.Remote.ToString();
            LogInterlinkClosed(neighbourText);
        }
        catch
        {
            // Best-effort teardown; never throw while tearing an interlink down.
        }
    }

    /// <inheritdoc/>
    public NetRomRoutingSnapshot Snapshot()
        => config.Enabled ? table.Snapshot() : NetRomRoutingSnapshot.Empty;

    /// <summary>Age the routing table by one obsolescence tick (test-driven decay).</summary>
    public int Sweep() => config.Enabled ? table.Sweep() : 0;

    // (Re)arm the debounce so the table is persisted shortly after the last ingest.
    private void ArmPersist() => persistTimer?.Change(PersistDebounce, Timeout.InfiniteTimeSpan);

    // Persist the current table snapshot. No-op without a store; resilient — the store
    // swallows + logs its own faults and we guard here too, so a persist can never
    // disturb the node.
    private void SaveSnapshot()
    {
        if (store is null || !config.Enabled)
        {
            return;
        }
        try
        {
            store.Save(table.Snapshot(), timeProvider.GetUtcNow());
        }
        catch (Exception ex)
        {
            LogPersistFault(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "NET/ROM: restored {Destinations} destination(s) + {Neighbours} neighbour(s) from the routing store (obsolescence aged {Decay} interval(s)).")]
    private partial void LogRestored(int destinations, int neighbours, int decay);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "NET/ROM: failed to persist the routing table; continuing in-memory.")]
    private partial void LogPersistFault(Exception ex);

    // ─── L3: the read-only tap ──────────────────────────────────────────

    private void OnFrameTraced(string portId, Callsign myCall, Ax25FrameEventArgs e)
    {
        try
        {
            if (e.Direction != FrameDirection.Received)
            {
                return;
            }
            var frame = e.Frame;
            if (!frame.IsUi || frame.Pid != Ax25Frame.PidNetRom || !IsNodesDestination(frame.Destination.Callsign))
            {
                return;
            }

            var originator = frame.Source.Callsign;
            var originatorText = originator.ToString();
            if (!NodesBroadcast.TryParse(frame.Info.Span, out var broadcast))
            {
                LogUnparseable(portId, originatorText);
                return;
            }

            // Per-port QUALITY + MINQUAL (BPQ per-port QUALITY / MINQUAL): the directly-heard
            // neighbour on this port gets the port's configured quality, and a route learned via
            // this broadcast is kept only if it derives at/above the port's MINQUAL floor (both
            // null ⇒ the table-wide defaults — byte-for-byte the prior behaviour).
            attachments.TryGetValue(portId, out var att);
            int? portQuality = att?.NeighbourQuality;
            int? portMinQuality = att?.MinQuality;
            table.Ingest(originator, myCall, portId, broadcast, portQuality, portMinQuality);
            LogHeard(portId, originatorText, broadcast.SenderAlias, broadcast.Entries.Count);
            ArmPersist();
        }
        catch (Exception ex)
        {
            LogTapFault(ex, portId);
        }
    }

    private static bool IsNodesDestination(Callsign dest)
        => dest.Ssid == 0 && string.Equals(dest.Base, NodesBroadcast.NodesDestination, StringComparison.Ordinal);

    // ─── L3: origination + obsolescence (the NODESINTERVAL tick) ────────

    private void OnInterval()
    {
        try
        {
            int purged = Sweep();
            if (purged > 0)
            {
                LogSwept(purged);
            }

            // The NODESINTERVAL sweep may have aged out the last INP3 route to some
            // destination (table.Sweep populates recentlyWithdrawn). Escalate those to the
            // scheduler NEGATIVE so the withdrawal fans out promptly rather than waiting for
            // the periodic RIF. Also nudge the INP3 ticks so a node whose own 1 s timer is
            // somehow starved still makes progress on the coarse cadence. No-op when INP3 is
            // off (inp3 == null). (The 1 s Inp3Host timer is the primary driver — design §5.)
            inp3?.OnNodesInterval();

            if (config.Broadcast)
            {
                BroadcastNodes();
            }

            // Persist the freshly-aged table (no-op without a store).
            SaveSnapshot();
        }
        catch (Exception ex)
        {
            LogSweepFault(ex);
        }
    }

    /// <summary>
    /// Originate our NODES broadcast on every attached port: our alias + the best
    /// route to each advertisable destination (OBSMIN-gated). Sent as UI frames
    /// (dest <c>NODES</c>, PID 0xCF). Exposed so a test can drive a broadcast
    /// deterministically without waiting on the interval timer.
    /// </summary>
    public void BroadcastNodes()
    {
        if (!config.Enabled || !config.Broadcast || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var learned = table.BuildAdvertisement(routingOptions.ObsoleteMinimum);
        var alias = ResolveAlias();

        // Opt-in app aliases (docs/app-packages.md § Application packet identity): append each
        // enabled app whose owner set netrom.alias, advertised AT our node (best-neighbour =
        // nodeCall) so a station C's the alias and routes to us, then to the app. Absent source
        // (or no aliases) ⇒ nothing extra — the off-by-default, anti-noise behaviour. Composes
        // with the learned routes + the implicit node-self advert (the UI frame's source call).
        var appAdverts = AppAdvertSource?.Invoke() ?? [];
        var entries = appAdverts.Count == 0 ? learned : [.. learned, .. appAdverts];

        var dest = new Callsign(NodesBroadcast.NodesDestination, 0);

        // The entry list is identical on every port, but the FRAMING is per-port: a port with
        // a NODESPACLEN cap fragments the same entries into more, smaller UI frames so a large
        // NODES table stays robust on a slow/shared channel. A port with no cap (the default)
        // uses the canonical structural limit (11 entries/frame) — byte-for-byte today's
        // behaviour. Build once per port so the cap is honoured per port.
        foreach (var (portId, attachment) in attachments)
        {
            var frames = NodesBroadcastBuilder.Build(alias, entries, attachment.NodesPaclen);
            foreach (var info in frames)
            {
                _ = SendUiSafe(attachment.Listener, dest, info, portId);
            }
        }
        LogBroadcast(alias, entries.Count, attachments.Count);
    }

    private async Task SendUiSafe(Ax25Listener listener, Callsign dest, byte[] info, string portId)
    {
        try
        {
            await listener.SendUiAsync(dest, info, Ax25Frame.PidNetRom).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBroadcastFault(ex, portId);
        }
    }

    private string ResolveAlias()
    {
        // The node alias is unified with Identity.Alias (the single node-name concept).
        if (!string.IsNullOrWhiteSpace(nodeAlias))
        {
            return nodeAlias!;
        }
        // Fall back to the node callsign base (the first 6 chars reach the wire).
        return nodeCallSet ? nodeCall.Base : string.Empty;
    }

    // ─── L4: interlink session tap + inbound circuits ───────────────────

    private void OnSessionAccepted(string portId, Ax25Session session)
    {
        // Attach the NET/ROM data tap to this session exactly once. The tap feeds
        // PID-0xCF DL-DATA indications into the circuit manager; the console
        // (Ax25NodeConnection) ignores 0xCF, so the two coexist on one session. We
        // record the session as an interlink only when it actually carries NET/ROM
        // (on the first 0xCF datagram) so a plain console session is never mistaken
        // for one.
        if (circuits is null || !tapped.TryAdd(session, 0))
        {
            return;
        }

        var listener = attachments.TryGetValue(portId, out var a) ? a.Listener : null;
        var peer = session.Context.Remote;

        // The tap is declared as a self-referencing local so it can DETACH itself on
        // disconnect. A cached Ax25Session is reused across disconnect/reconnect (the
        // listener re-runs figc4.1 on a re-SABM and re-fires SessionAccepted), so if
        // the tap merely dropped its `tapped` membership on disconnect, the next
        // accept would add a SECOND closure to the same session and every inbound
        // datagram would be processed twice. (Today the lower layers absorb it — the
        // circuit dedups a stale-sequence datagram and byPeerKey dedups a Connect
        // Request — so it's currently benign, but a duplicated tap is a latent
        // footgun on exactly the fresh/re-established interlinks this guards.) Detach
        // on disconnect + remove from `tapped`, so a reconnect re-attaches exactly one.
        EventHandler<DataLinkSignal> tap = null!;
        tap = (_, sig) =>
        {
            if (sig is DataLinkDataIndication di && di.Pid == Ax25Frame.PidNetRom)
            {
                // First 0xCF data → this is an interlink; remember the session so our
                // outbound datagrams to this neighbour reuse it.
                if (listener is not null)
                {
                    interlinks.TryAdd(peer, new Interlink(portId, listener, session));
                }
                OnInterlinkData(session, di.Info);
            }
            else if (sig is DataLinkDisconnectIndication or DataLinkDisconnectConfirm)
            {
                session.DataLinkSignalEmitted -= tap;
                tapped.TryRemove(session, out byte _);
                if (interlinks.TryGetValue(peer, out var link) && ReferenceEquals(link.Session, session))
                {
                    interlinks.TryRemove(peer, out Interlink? _);
                }
            }
        };
        session.DataLinkSignalEmitted += tap;
    }

    private void OnInterlinkData(Ax25Session session, ReadOnlyMemory<byte> info)
    {
        var fromNeighbour = session.Context.Remote;
        try
        {
            // ── INP3 taps come FIRST, and only when the overlay is on (inp3 != null ⇔
            //    config.Inp3.Enabled). A RIF (0xFF-led) or an L3RTT (a NetRomPacket to
            //    L3RTT-0) must be peeled off here so it can NEVER reach circuits /
            //    forwarding. When the frame is consumed, we return; otherwise we fall
            //    through to the existing L4 dispatch with the already-parsed packet
            //    (no double-parse). See docs/netrom-inp3-host-integration-design.md §3.2.
            if (inp3 is not null)
            {
                if (DispatchInp3(fromNeighbour, info, out var l4Packet))
                {
                    return;   // consumed as a RIF or an L3RTT (or dropped as malformed) — never L4.
                }
                if (l4Packet is not null)
                {
                    DispatchL4(l4Packet, fromNeighbour);   // a normal L4 datagram — reuse the parsed packet.
                }
                return;
            }

            // ── INP3 off: EXACTLY today's body, unchanged (the default-off guarantee).
            if (circuits is null)
            {
                return;
            }
            if (!NetRomPacket.TryParse(info.Span, out var packet))
            {
                return;
            }
            DispatchL4(packet!, fromNeighbour);
        }
        catch (Exception ex)
        {
            var peerText = fromNeighbour.ToString();
            LogInterlinkFault(ex, peerText);
        }
    }

    // The existing dest-==-us → circuits / else forward switch, extracted verbatim so it
    // is shared by the INP3-on and INP3-off paths (a pure extract-method; no behaviour
    // change). L3 dispatch mirrors BPQ L4Code.c: a datagram addressed to this node
    // terminates here (up to the L4 circuit layer); one addressed to another node is
    // forwarded toward its destination (the network-layer routing role); an endpoint-only
    // node silently drops it (we don't relay third-party traffic we didn't opt into).
    private void DispatchL4(NetRomPacket packet, Callsign receivedFrom)
    {
        if (circuits is null)
        {
            return;
        }
        if (nodeCallSet && packet.Network.Destination.Equals(nodeCall))
        {
            circuits.OnPacket(packet);
        }
        else if (ForwardEnabled)
        {
            ForwardDatagram(packet, receivedFrom);
        }
    }

    /// <summary>
    /// Forward a transit datagram (one whose destination node is not us) one hop
    /// toward its destination, mirroring the BPQ <c>L4Code.c</c> forward decision:
    /// decrement the hop-limit TTL (discard at zero), cap it, drop a datagram that
    /// has looped back to its own origin, resolve the destination's best next-hop
    /// neighbour that is not the one it just arrived from (don't bounce it straight
    /// back), ensure the interlink to that neighbour, and re-send. Synchronous when
    /// the interlink is already up (the common transit path — preserves datagram
    /// order); a cold-start interlink is dialled on a background task first.
    /// </summary>
    // The resolved INP3 forwarding/selection preference (BPQ PREFERINP3ROUTES). False
    // unless the overlay is constructed (inp3 != null ⇒ inp3.enabled + connect) AND the
    // operator flipped the knob — so forward + connect route by quality byte-for-byte as
    // today by default, even with the overlay on for awareness. When the overlay is off no
    // INP3 route is ever ingested, so this is moot; gating on `inp3 is not null` makes the
    // default-off guarantee explicit at the selection seam.
    private bool PreferInp3Routes => inp3 is not null && config.Inp3.PreferInp3Routes;

    // The active route to a destination under the INP3 selection policy (the connect /
    // best-route-forward next hop): the lowest-target-time INP3 route when the overlay is on,
    // the knob is set, and the destination holds a time-route; otherwise the best-quality
    // route, exactly as today. Null when the destination has no usable route. Shares
    // Inp3RouteSelector with the forward path's SelectInp3NextHop so the two agree.
    private NetRomRoute? SelectActiveRoute(NetRomDestination destination) =>
        Inp3RouteSelector.SelectActiveRoute(destination, PreferInp3Routes);

    private void ForwardDatagram(NetRomPacket packet, Callsign receivedFrom)
    {
        // The forward decision (TTL decrement/cap, loop guard, next-hop resolution)
        // is the pure NetRomForwarding.Decide; this method does only the I/O it asks
        // for. destText is a local (not an inline log arg) so it isn't evaluated when
        // the trace is disabled (CA1873).
        var destText = packet.Network.Destination.ToString();
        var decision = NetRomForwarding.Decide(packet, receivedFrom, nodeCall, table.Snapshot(), circuitOptions.TimeToLive, config.ForwardMode, PreferInp3Routes);

        switch (decision.Outcome)
        {
            case NetRomForwarding.ForwardOutcome.DropTtlExpired:
                Interlocked.Increment(ref droppedTtl);
                LogForwardTtlExpired(destText);
                return;
            case NetRomForwarding.ForwardOutcome.DropLooped:
                Interlocked.Increment(ref droppedLooped);
                LogForwardLoop(destText);
                return;
            case NetRomForwarding.ForwardOutcome.DropNoRoute:
                Interlocked.Increment(ref droppedNoRoute);
                LogForwardNoRoute(destText);
                return;
        }

        var neighbour = decision.NextHop;
        var forwarded = decision.Packet;

        // Count the forward once the decision resolved a next hop — the datagram is on its way
        // toward the destination whether the interlink is hot (sent now) or cold (dialled then
        // sent). Bytes are the encoded L3 PDU length. A cold interlink that ultimately can't be
        // raised is rare and still counts as a forward attempt accepted by routing.
        Interlocked.Increment(ref forwardedFrames);
        Interlocked.Add(ref forwardedBytes, forwarded.ToBytes().Length);

        // If the interlink is already up, send now (in order); otherwise dial it on a
        // background task and send when it comes up.
        if (TrySendOverInterlink(neighbour, forwarded))
        {
            var neighbourText = neighbour.ToString();
            LogForwarded(destText, neighbourText, forwarded.Network.TimeToLive);
            return;
        }

        _ = ForwardOverColdInterlinkAsync(forwarded, neighbour);
    }

    private async Task ForwardOverColdInterlinkAsync(NetRomPacket forwarded, Callsign neighbour)
    {
        var neighbourText = neighbour.ToString();
        try
        {
            await EnsureInterlinkAsync(neighbour, CancellationToken.None).ConfigureAwait(false);
            if (TrySendOverInterlink(neighbour, forwarded))
            {
                var destText = forwarded.Network.Destination.ToString();
                LogForwarded(destText, neighbourText, forwarded.Network.TimeToLive);
            }
            else
            {
                LogNoInterlink(neighbourText);
            }
        }
        catch (Exception ex)
        {
            LogSendFault(ex);
        }
    }

    /// <summary>Send an encoded datagram over an existing interlink to
    /// <paramref name="neighbour"/>. Returns <c>false</c> if no interlink is up.</summary>
    private bool TrySendOverInterlink(Callsign neighbour, NetRomPacket packet)
        => TrySendOverInterlinkBytes(neighbour, packet.ToBytes());

    /// <summary>
    /// Send raw 0xCF info-field bytes over an existing interlink to
    /// <paramref name="neighbour"/> — the byte-shaped sibling of
    /// <see cref="TrySendOverInterlink"/>, used by the INP3 host because a RIF is an
    /// <c>Inp3Rif</c> (not a <see cref="NetRomPacket"/>) and an L3RTT frame is already
    /// bytes. Both funnel to the same <c>SendData(session, bytes, 0xCF)</c> seam L4 uses.
    /// Returns <c>false</c> if no interlink is up (the INP3 cold-interlink policy is
    /// drop-don't-dial — design §4.1, so the host treats false as "not probed/advertised
    /// this round," not a failure). A test seam (<see cref="interlinkSendSinkForTest"/>)
    /// captures the bytes instead of touching a real listener when set.
    /// </summary>
    private bool TrySendOverInterlinkBytes(Callsign neighbour, byte[] info)
    {
        if (interlinkSendSinkForTest is { } sink)
        {
            return sink(neighbour, info);
        }
        if (interlinks.TryGetValue(neighbour, out var link) && link.Session is not null && link.Listener is not null)
        {
            link.Listener.SendData(link.Session, info, Ax25Frame.PidNetRom);
            return true;
        }
        return false;
    }

    // Test seam (InternalsVisibleTo Packet.Node.Tests): when set, every interlink send
    // (L4 and INP3) is captured here instead of being posted to a real Ax25Session, so a
    // deterministic node test can drive the INP3 host on a FakeTimeProvider and assert what
    // went on the wire without standing up real AX.25 handshakes. Returns the "did it send"
    // result TrySendOverInterlinkBytes would have returned (true = an interlink was up).
    internal Func<Callsign, byte[], bool>? interlinkSendSinkForTest;

    // Test seam (InternalsVisibleTo Packet.Node.Tests): drive ONE interlink dial directly,
    // exercising the capability-cache wiring in EnsureInterlinkAsync (PlanDial → the dial →
    // RecordOutcome-on-return / no-record-on-throw) without standing up the full L4 circuit
    // machinery a public ConnectCircuitAsync would pull in. Behaviour-identical to the dial
    // ConnectCircuitAsync performs; it just stops once the interlink is up (or rethrows).
    internal Task EnsureInterlinkForTestAsync(Callsign neighbour, CancellationToken ct = default)
        => EnsureInterlinkAsync(neighbour, ct);

    // The shared neighbour-down path. With INP3 off (inp3 == null) this is EXACTLY today's
    // table.MarkNeighbourDown — the L4 dial-failure failover, byte-for-byte. With INP3 on it
    // additionally drops the engine's per-neighbour timing state and escalates every
    // destination that just lost its last INP3 route to the scheduler (NEGATIVE → immediate
    // fan-out), so an L4 dial failure also propagates the INP3 withdrawal promptly. The 180 s
    // INP3 reflection-timeout uses Inp3Host's own NeighbourDown handler (which calls this too).
    private int MarkNeighbourDownShared(Callsign neighbour)
    {
        int dropped = table.MarkNeighbourDown(neighbour);
        inp3?.OnNeighbourGone(neighbour);
        return dropped;
    }

    private void OnIncomingCircuit(object? sender, IncomingCircuitEventArgs e)
    {
        // A remote opened a circuit to us. If a console hook is wired, bridge it to a
        // fresh node console so the routed user reaches the prompt.
        var userText = e.OriginatingUser.ToString();
        var remoteNodeText = e.RemoteNode.ToString();
        LogInboundCircuit(remoteNodeText, userText);

        var runConsole = RunInboundConsole;
        if (runConsole is null)
        {
            // No console bridge — just accept the circuit (useful for transit/tests).
            CircuitManager.AcceptIncoming(e);
            return;
        }

        // Subscribe the connection's data tap BEFORE accepting, so no inbound Info
        // that races the connect-ack can be delivered before there is a reader (the
        // connection subscribes circuit.DataReceived in its constructor).
        var connection = new NetRomNodeConnection(e.Circuit, e.OriginatingUser);
        CircuitManager.AcceptIncoming(e);
        _ = Task.Run(async () =>
        {
            await using (connection.ConfigureAwait(false))
            {
                try
                {
                    await runConsole(connection, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogConsoleFault(ex, userText);
                }
            }
        }, CancellationToken.None);
    }

    // ─── L4: outbound circuit origination (connect-routing) ─────────────

    /// <summary>
    /// Open a NET/ROM L4 circuit to <paramref name="destinationNode"/> on behalf of
    /// <paramref name="originatingUser"/>, routing it via the best neighbour the
    /// routing table knows, and await it reaching Connected (or fail). Returns the
    /// circuit wrapped as an <see cref="INodeConnection"/> the console relays
    /// against. Throws if connect-routing is disabled, there is no route, or the
    /// circuit is refused / times out.
    /// </summary>
    public async Task<INodeConnection> ConnectCircuitAsync(
        NetRomDestination destination, Callsign originatingUser, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (circuits is null || !nodeCallSet)
        {
            throw new InvalidOperationException("NET/ROM connect-routing is not enabled on this node.");
        }
        var destCall = destination.Destination;

        // Failover loop: try the best route; if the interlink to its neighbour can't
        // be raised (the neighbour is down), EnsureInterlinkAsync has already marked
        // that neighbour down — which drops its routes from the table — so re-resolve
        // the destination's now-best route and try again, until one connects or the
        // destination has no routes left. This is the connect-side of the link-down
        // failover signal: a `connect <alias>` re-routes around a dead next hop
        // instead of failing outright. Bounded by the dwindling route set (each
        // failure removes a route) with a hard cap as a backstop.
        for (int attempt = 0; attempt < 1 + NetRomRoutingOptions.Default.MaxRoutesPerDestination; attempt++)
        {
            var liveDest = table.Snapshot().ResolveDestination(destCall.ToString());
            var best = (liveDest is null ? null : SelectActiveRoute(liveDest))
                ?? throw new InvalidOperationException($"no usable NET/ROM route to {destCall}.");

            try
            {
                // Ensure the interlink to the best neighbour is up before originating.
                await EnsureInterlinkAsync(best.Neighbour, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // EnsureInterlinkAsync marked the neighbour down already; loop to the
                // next-best route. If that was the last route, the next ResolveDestination
                // returns null → the throw above surfaces the no-route failure.
                var failedNeighbour = best.Neighbour.ToString();
                var failedDest = destCall.ToString();
                LogConnectFailoverRetry(failedNeighbour, failedDest, ex);
                continue;
            }

            return await OriginateCircuitAsync(destCall, originatingUser, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"no usable NET/ROM route to {destCall} (all next hops are down).");
    }

    private async Task<INodeConnection> OriginateCircuitAsync(
        Callsign destCall, Callsign originatingUser, CancellationToken ct)
    {
        var circuit = circuits!.OpenCircuit(destCall);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        circuit.Connected += () => connected.TrySetResult();
        circuit.Closed += r => connected.TrySetException(
            new IOException($"NET/ROM circuit to {destCall} {r.ToString().ToLowerInvariant()}."));

        var connection = new NetRomNodeConnection(circuit, destCall);
        circuit.Connect(originatingUser);

        // Wait for Connected (or close), bounded by the cancellation token.
        using (ct.Register(() => connected.TrySetCanceled(ct)))
        {
            try
            {
                await connected.Task.ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        return connection;
    }

    private async Task EnsureInterlinkAsync(Callsign neighbour, CancellationToken ct)
    {
        if (interlinks.TryGetValue(neighbour, out var existing) && existing.Session is { } s &&
            !string.Equals(s.CurrentState, "Disconnected", StringComparison.Ordinal))
        {
            return;   // already up
        }

        // Dial the neighbour on the port we last heard it on (else the first port).
        var nbr = table.Snapshot().NeighbourFor(neighbour);
        Attachment? attachment = null;
        if (nbr is not null && attachments.TryGetValue(nbr.PortId, out var a))
        {
            attachment = a;
        }
        attachment ??= attachments.Values.FirstOrDefault();
        if (attachment is null)
        {
            throw new InvalidOperationException("no NET/ROM port available to open an interlink.");
        }

        // Consult the per-peer capability cache for this dial. The default (no cache)
        // preserves today's behaviour exactly: extended:false (mod-8 NET/ROM
        // infrastructure) + PreConnectXid:true (the listener's pre-connect-XID default,
        // which the no-arg ConnectAsync overload was honouring). A learned positive lets
        // a known-extended neighbour go straight to SABME, and a learned non-XID-answerer
        // skips the pre-connect XID it would only stall on.
        var plan = capabilityCache?.PlanDial(attachment.PortId, neighbour.ToString(), PeerDialPolicy.Interlink)
            ?? new PeerDialPlan(Extended: false, PreConnectXid: true);

        // Dial the interlink. Prefer the supervisor's claim-aware hook (so no console
        // is started against the neighbour — see OpenInterlink); fall back to a direct
        // listener dial when no supervisor is wired (unit tests). A dial that fails
        // (the neighbour never answered the SABM — ConnectAsync exhausts N2 and throws)
        // is the explicit link-down signal: mark the neighbour down so its now-dead
        // routes leave the table at once and the caller (forward / connect failover)
        // re-routes to an alternate next hop instead of re-dialling a link that isn't
        // there. A pre-dial setup fault (no port) is a local-config problem, not a
        // neighbour-down — only the dial itself is guarded.
        Ax25Session session;
        try
        {
            session = OpenInterlink is { } open
                ? await open(attachment.PortId, neighbour, plan, ct).ConfigureAwait(false)
                // NET/ROM interlinks are mod-8 infrastructure by default: dial v2.0 (SABM),
                // NOT the listener's PreferExtendedConnect default. The NET/ROM neighbour
                // population is overwhelmingly v2.0/mod-8 (BPQ/XRouter), and a peer that
                // silently ignores our SABME (e.g. BPQ's AXUDP NET/ROM port) makes the dial
                // exhaust N2 and throw instead of FRMR-degrading — breaking circuit
                // origination. The per-peer capability cache (above) makes this adaptive:
                // a neighbour learned-extended dials SABME, and a known non-XID-answerer
                // skips the pre-connect XID — but with no cache the plan is the conservative
                // mod-8 + pre-connect-XID default, byte-for-byte today's behaviour.
                : await attachment.Listener.ConnectAsync(
                    neighbour, attachment.Listener.MyCall, plan.Extended, plan.PreConnectXid, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            int dropped = MarkNeighbourDownShared(neighbour);
            var downText = neighbour.ToString();
            LogNeighbourDown(downText, dropped);
            throw;
        }
        // The dial RETURNED a session — record the outcome (plan-aware: what we dialled +
        // what the resulting link observed). This is reached ONLY on a returned dial; the
        // catch above rethrows, so a dial that throws never records (no link ⇒ no signal).
        capabilityCache?.RecordOutcome(
            attachment.PortId, neighbour.ToString(),
            dialedExtended: plan.Extended, observedIsExtended: session.Context.IsExtended,
            dialedPreConnectXid: plan.PreConnectXid, observedSrejEnabled: session.Context.SrejEnabled);

        // Tap the session for inbound NET/ROM (the tap is idempotent — TryAdd guards
        // it — so OnSessionAccepted firing for the dial too is harmless).
        OnSessionAccepted(attachment.PortId, session);
        interlinks[neighbour] = new Interlink(attachment.PortId, attachment.Listener, session);
        var neighbourText = neighbour.ToString();
        LogInterlinkUp(neighbourText);
    }

    // The circuit manager's SendPacket sink: route a datagram to its destination
    // node's best neighbour over that neighbour's interlink.
    private void SendNetRomPacket(NetRomPacket packet)
    {
        try
        {
            var dest = packet.Network.Destination;

            // Find the next-hop neighbour. Order: (1) a direct interlink to the
            // destination node itself — this covers replying to a peer over the very
            // session its datagram arrived on, even one that never broadcast NODES
            // (e.g. a pure client); (2) the best route in the routing table; (3) the
            // destination as a directly-heard neighbour.
            Callsign? neighbour = null;
            if (interlinks.ContainsKey(dest))
            {
                neighbour = dest;
            }
            else
            {
                var snap = table.Snapshot();
                var resolved = snap.Destinations.FirstOrDefault(d => d.Destination.Equals(dest));
                if ((resolved is null ? null : SelectActiveRoute(resolved)) is { } route)
                {
                    neighbour = route.Neighbour;
                }
                else if (snap.NeighbourFor(dest) is not null)
                {
                    neighbour = dest;
                }
            }

            if (neighbour is null)
            {
                var destText = dest.ToString();
                LogNoRoute(destText);
                return;
            }

            if (!interlinks.TryGetValue(neighbour.Value, out var link) || link.Session is null || link.Listener is null)
            {
                // No interlink yet — the EnsureInterlinkAsync path (outbound connect)
                // establishes it before the first datagram, so a missing link here is
                // a transit/edge case we log rather than block on (sync sink).
                var neighbourText = neighbour.Value.ToString();
                LogNoInterlink(neighbourText);
                return;
            }

            link.Listener.SendData(link.Session, packet.ToBytes(), Ax25Frame.PidNetRom);
        }
        catch (Exception ex)
        {
            LogSendFault(ex);
        }
    }

    /// <summary>
    /// Synchronous dispose. Tears down the timer + circuit manager and detaches every
    /// port, posting a <b>best-effort</b> DISC to each interlink AX.25 session on the
    /// way (so a neighbour doesn't keep a half-open link). The DISC is fire-and-forget
    /// here — the caller must keep the listeners alive momentarily for the frame to
    /// flush. For a guaranteed clean DISC/UA round-trip on the wire, dispose via
    /// <see cref="DisposeAsync"/> instead (the node host does).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        SaveSnapshot();             // final flush before teardown
        persistTimer?.Dispose();
        sweepTimer?.Dispose();
        inp3?.Dispose();            // stop INP3 ticking + tear down engine/scheduler
        circuits?.Dispose();
        foreach (var portId in attachments.Keys.ToArray())
        {
            DetachPort(portId);   // posts a best-effort DISC per interlink
        }
    }

    /// <summary>
    /// Graceful async dispose: stops origination, tears the L4 circuits down (their
    /// Disconnect Requests flow over the still-live interlinks), then <b>cleanly
    /// disconnects each interlink AX.25 session</b> — posting the DISC and waiting
    /// (bounded) for the DISC/UA to round-trip on the wire — before detaching the
    /// ports. This is what stops a neighbour (e.g. LinBPQ) being left with a half-open
    /// connected-mode link that it polls onto the shared channel (the #309
    /// contamination class). The caller must still keep the listeners alive until this
    /// returns (the node host disposes NetRomService before the ports for exactly
    /// this reason).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        SaveSnapshot();             // final flush before teardown
        persistTimer?.Dispose();
        sweepTimer?.Dispose();
        inp3?.Dispose();            // stop INP3 ticking before the interlinks go down
        // Tear the L4 circuits down first so their Disconnect Requests ride the
        // interlinks while those AX.25 sessions are still up.
        circuits?.Dispose();

        // Now cleanly disconnect every interlink AX.25 session, waiting (bounded) for
        // each DISC/UA to settle on the wire. Done while the listeners are still alive.
        using var cts = new CancellationTokenSource(InterlinkDisconnectGrace + TimeSpan.FromSeconds(2));
        foreach (var (nbr, link) in interlinks.ToArray())
        {
            interlinks.TryRemove(nbr, out Interlink? _);
            await CloseInterlinkAsync(link, cts.Token).ConfigureAwait(false);
        }

        // Detach the ports (unsubscribe taps). Any interlinks are already closed +
        // removed above, so this just drops the frame/session handlers.
        foreach (var portId in attachments.Keys.ToArray())
        {
            DetachPort(portId);
        }
    }

    private sealed record Attachment(
        string PortId, Callsign MyCall, Ax25Listener Listener,
        EventHandler<Ax25FrameEventArgs> FrameHandler, EventHandler<Ax25SessionEventArgs> SessionHandler,
        int? NeighbourQuality, int? MinQuality, int? NodesPaclen);

    private sealed record Interlink(string PortId, Ax25Listener? Listener, Ax25Session? Session);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: listening for NODES on port {PortId} (as {Callsign}).")]
    private partial void LogAttached(string portId, string callsign);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: stopped listening on port {PortId}.")]
    private partial void LogDetached(string portId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: heard NODES from {Originator} (alias {Alias}) on {PortId} with {EntryCount} entr(ies).")]
    private partial void LogHeard(string portId, string originator, string alias, int entryCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: NODES from {Originator} on {PortId} did not parse; ignored.")]
    private partial void LogUnparseable(string portId, string originator);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: obsolescence sweep purged {Purged} route(s).")]
    private partial void LogSwept(int purged);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: broadcast NODES as {Alias} ({EntryCount} entr(ies)) on {PortCount} port(s).")]
    private partial void LogBroadcast(string alias, int entryCount, int portCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: interlink to {Neighbour} up.")]
    private partial void LogInterlinkUp(string neighbour);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: interlink to {Neighbour} disconnected (clean teardown).")]
    private partial void LogInterlinkClosed(string neighbour);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: neighbour {Neighbour} down (interlink unreachable) — dropped {Dropped} route(s); failing over.")]
    private partial void LogNeighbourDown(string neighbour, int dropped);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: connect via {Neighbour} to {Destination} failed (neighbour down); trying next route.")]
    private partial void LogConnectFailoverRetry(string neighbour, string destination, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: inbound circuit from {OriginatingUser} via {RemoteNode}.")]
    private partial void LogInboundCircuit(string remoteNode, string originatingUser);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: no route to {Destination} for an outbound datagram.")]
    private partial void LogNoRoute(string destination);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: no interlink to {Neighbour} for an outbound datagram.")]
    private partial void LogNoInterlink(string neighbour);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: forwarded transit datagram for {Destination} via {Neighbour} (ttl {TimeToLive}).")]
    private partial void LogForwarded(string destination, string neighbour, byte timeToLive);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: dropped transit datagram for {Destination} — TTL expired.")]
    private partial void LogForwardTtlExpired(string destination);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: dropped transit datagram for {Destination} — looped back to its origin.")]
    private partial void LogForwardLoop(string destination);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: dropped transit datagram for {Destination} — no onward route.")]
    private partial void LogForwardNoRoute(string destination);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: frame tap on port {PortId} faulted (frame ignored).")]
    private partial void LogTapFault(Exception ex, string portId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: NODESINTERVAL tick faulted.")]
    private partial void LogSweepFault(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: NODES broadcast on port {PortId} faulted.")]
    private partial void LogBroadcastFault(Exception ex, string portId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: interlink data from {Neighbour} faulted (datagram ignored).")]
    private partial void LogInterlinkFault(Exception ex, string neighbour);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: outbound datagram send faulted.")]
    private partial void LogSendFault(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "NET/ROM: inbound circuit console for {OriginatingUser} faulted.")]
    private partial void LogConsoleFault(Exception ex, string originatingUser);
}

/// <summary>
/// An immutable read of the node's L3 transit-forwarding throughput counters — the
/// "forwarding" bucket of the Prometheus <c>/metrics</c> exporter (#457). All counts are
/// monotonic over the service lifetime (process uptime), the semantics Prometheus
/// counters expect. All-zero on an endpoint-only or NET/ROM-disabled node.
/// </summary>
/// <param name="ForwardedFrames">Transit datagrams routed onward toward their destination.</param>
/// <param name="ForwardedBytes">Total encoded NET/ROM PDU bytes of those forwarded datagrams.</param>
/// <param name="DroppedTtlExpired">Datagrams discarded because the hop-limit TTL reached zero.</param>
/// <param name="DroppedLooped">Datagrams discarded because they had looped back to their own origin.</param>
/// <param name="DroppedNoRoute">Datagrams discarded because no onward route to the destination was known.</param>
public sealed record NetRomForwardingStats(
    long ForwardedFrames,
    long ForwardedBytes,
    long DroppedTtlExpired,
    long DroppedLooped,
    long DroppedNoRoute)
{
    /// <summary>Total datagrams dropped on the forward path, across all three drop reasons.</summary>
    public long DroppedTotal => DroppedTtlExpired + DroppedLooped + DroppedNoRoute;
}
