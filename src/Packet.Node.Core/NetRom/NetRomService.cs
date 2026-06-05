using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;
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
/// / <see cref="NetRomConfig.Connect"/>) and default off.
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

    private readonly NetRomConfig config;
    private readonly NetRomRoutingTable table;
    private readonly NetRomRoutingOptions routingOptions;
    private readonly NetRomCircuitOptions circuitOptions;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<NetRomService> logger;
    private readonly ITimer? sweepTimer;
    private readonly CircuitManager? circuits;

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

    private int disposed;

    /// <inheritdoc/>
    public bool Enabled => config.Enabled;

    /// <summary>True if NET/ROM L4 connect-routing is enabled (interlinks + circuits).</summary>
    public bool ConnectEnabled => config.Enabled && config.Connect;

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
    public Func<string, Callsign, CancellationToken, Task<Ax25Session>>? OpenInterlink { get; set; }

    /// <summary>The circuit manager (null when NET/ROM connect is disabled). Exposed
    /// for the outbound connector + tests.</summary>
    public CircuitManager? Circuits => circuits;

    /// <summary>Construct the service from the node's NET/ROM config.</summary>
    public NetRomService(NetRomConfig config, TimeProvider? timeProvider = null, ILogger<NetRomService>? logger = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.logger = logger ?? NullLogger<NetRomService>.Instance;

        routingOptions = ResolveRoutingOptions(config);
        circuitOptions = ResolveCircuitOptions(config);
        table = new NetRomRoutingTable(routingOptions, this.timeProvider);

        if (config.Enabled)
        {
            var interval = TimeSpan.FromSeconds(config.SweepIntervalSeconds is > 0 ? config.SweepIntervalSeconds.Value : 3600);
            // The NODESINTERVAL tick: ages the table (obsolescence) and, when
            // broadcast is on, originates our NODES broadcast. It never disturbs a
            // session — broadcasts are UI frames, the sweep is pure table maintenance.
            sweepTimer = this.timeProvider.CreateTimer(_ => OnInterval(), state: null, dueTime: interval, period: interval);

            if (config.Connect)
            {
                circuits = new CircuitManager(default, circuitOptions, this.timeProvider, tickInterval: TimeSpan.FromSeconds(1));
                circuits.SendPacket = SendNetRomPacket;
                circuits.IncomingCircuit += OnIncomingCircuit;
            }
        }
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
        };
    }

    /// <summary>
    /// Begin hearing NODES on a port and, when connect is enabled, make the port
    /// available for interlinks. No-op if NET/ROM is disabled or already attached.
    /// </summary>
    public void AttachPort(string portId, Callsign myCall, Ax25Listener listener)
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
        }

        void FrameHandler(object? sender, Ax25FrameEventArgs e) => OnFrameTraced(portId, myCall, e);
        void SessionHandler(object? sender, Ax25SessionEventArgs e) => OnSessionAccepted(portId, e.Session);

        var attachment = new Attachment(portId, myCall, listener, FrameHandler, SessionHandler);
        if (!attachments.TryAdd(portId, attachment))
        {
            return;
        }

        listener.FrameTraced += FrameHandler;
        if (config.Connect)
        {
            // Tap inbound NET/ROM data on every session this port accepts so the
            // circuit manager sees interlink datagrams.
            listener.SessionAccepted += SessionHandler;
        }

        var callText = myCall.ToString();
        LogAttached(portId, callText);
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

            table.Ingest(originator, myCall, portId, broadcast);
            LogHeard(portId, originatorText, broadcast.SenderAlias, broadcast.Entries.Count);
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

            if (config.Broadcast)
            {
                BroadcastNodes();
            }
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

        var entries = table.BuildAdvertisement(routingOptions.ObsoleteMinimum);
        var alias = ResolveAlias();
        var frames = NodesBroadcastBuilder.Build(alias, entries);
        var dest = new Callsign(NodesBroadcast.NodesDestination, 0);

        foreach (var (portId, attachment) in attachments)
        {
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
        if (!string.IsNullOrWhiteSpace(config.Alias))
        {
            return config.Alias!;
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

        session.DataLinkSignalEmitted += (_, sig) =>
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
                tapped.TryRemove(session, out byte _);
                if (interlinks.TryGetValue(peer, out var link) && ReferenceEquals(link.Session, session))
                {
                    interlinks.TryRemove(peer, out Interlink? _);
                }
            }
        };
    }

    private void OnInterlinkData(Ax25Session session, ReadOnlyMemory<byte> info)
    {
        if (circuits is null)
        {
            return;
        }
        try
        {
            if (NetRomPacket.TryParse(info.Span, out var packet))
            {
                circuits.OnPacket(packet!);
            }
        }
        catch (Exception ex)
        {
            var peerText = session.Context.Remote.ToString();
            LogInterlinkFault(ex, peerText);
        }
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
        var best = destination.BestRoute
            ?? throw new InvalidOperationException($"no usable NET/ROM route to {destination.Destination}.");

        // Ensure the interlink to the best neighbour is up before originating.
        await EnsureInterlinkAsync(best.Neighbour, ct).ConfigureAwait(false);

        var circuit = circuits.OpenCircuit(destination.Destination);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        circuit.Connected += () => connected.TrySetResult();
        circuit.Closed += r => connected.TrySetException(
            new IOException($"NET/ROM circuit to {destination.Destination} {r.ToString().ToLowerInvariant()}."));

        var connection = new NetRomNodeConnection(circuit, destination.Destination);
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

        // Dial the interlink. Prefer the supervisor's claim-aware hook (so no console
        // is started against the neighbour — see OpenInterlink); fall back to a direct
        // listener dial when no supervisor is wired (unit tests).
        var session = OpenInterlink is { } open
            ? await open(attachment.PortId, neighbour, ct).ConfigureAwait(false)
            : await attachment.Listener.ConnectAsync(neighbour, ct).ConfigureAwait(false);
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
                if (resolved?.BestRoute is { } route)
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
        sweepTimer?.Dispose();
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
        sweepTimer?.Dispose();
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
        EventHandler<Ax25FrameEventArgs> FrameHandler, EventHandler<Ax25SessionEventArgs> SessionHandler);

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

    [LoggerMessage(Level = LogLevel.Information, Message = "NET/ROM: inbound circuit from {OriginatingUser} via {RemoteNode}.")]
    private partial void LogInboundCircuit(string remoteNode, string originatingUser);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: no route to {Destination} for an outbound datagram.")]
    private partial void LogNoRoute(string destination);

    [LoggerMessage(Level = LogLevel.Debug, Message = "NET/ROM: no interlink to {Neighbour} for an outbound datagram.")]
    private partial void LogNoInterlink(string neighbour);

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
