using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Transports;

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
public sealed partial class PortSupervisor : IAsyncDisposable
{
    private readonly IConfigProvider config;
    private readonly ITransportFactory transportFactory;
    private readonly TimeProvider timeProvider;
    private readonly ILoggerFactory loggerFactory;
    private readonly ILogger<PortSupervisor> logger;
    private readonly NetRomService? netRom;
    private readonly Dictionary<string, RunningPort> ports = new(StringComparer.Ordinal);
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
        NetRomService? netRom = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<PortSupervisor>();
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
    // Ax25OutboundConnector claims a console connect-out.
    private async Task<Ax25Session> OpenInterlinkAsync(string portId, Callsign neighbour, CancellationToken ct)
    {
        RunningPort? port;
        lock (ports) ports.TryGetValue(portId, out port);
        var listener = port?.Listener
            ?? throw new InvalidOperationException($"NET/ROM interlink: port '{portId}' is not running.");

        using var ticket = ClaimOutbound(neighbour);
        return await listener.ConnectAsync(neighbour, ct).ConfigureAwait(false);
    }

    // Run the node command service over an inbound connection (used for NET/ROM L4
    // circuits that reach our prompt). The dialling user can itself `connect`
    // onward, so the console gets a NET/ROM-routing connector with no AX.25
    // fallback (the local-channel dial doesn't apply to a network-arrived user).
    private async Task RunNodeConsoleAsync(INodeConnection connection, CancellationToken ct)
    {
        Callsign user = Callsign.TryParse(connection.PeerId, out var u) ? u : default;
        var connector = netRom is not null ? new Packet.Node.Core.NetRom.NetRomOutboundConnector(netRom, fallback: null, user) : null;
        var env = new NodeConsoleEnvironment(config, connector, netRom);
        var service = new NodeCommandService(env, loggerFactory.CreateLogger<NodeCommandService>());
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
        get { lock (ports) return ports.Keys.ToArray(); }
    }

    /// <summary>Look up a running port by id (for tests).</summary>
    public RunningPort? GetPort(string id)
    {
        lock (ports) return ports.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>
    /// An outbound connector for the first running port (by id order), or null if
    /// no port is up. The telnet console uses this so a <c>Connect</c> from a
    /// local dial-in dials out on a real AX.25 port — slice-1 same-port-only in
    /// the sense that there is exactly one deterministic dial-out port.
    /// </summary>
    public IOutboundConnector? ResolveDefaultConnector()
    {
        RunningPort? first;
        lock (ports)
        {
            first = ports.Values.OrderBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
        }
        var ax25 = first is null ? null : new Ax25OutboundConnector(first.Id, first.Listener, r => ClaimOutbound(r));

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
                if (n <= 1) outboundInProgress.Remove(remote);
                else outboundInProgress[remote] = n - 1;
            }
        }
    }

    private bool IsOutbound(Callsign remote)
    {
        lock (outboundGate) return outboundInProgress.ContainsKey(remote);
    }

    private sealed class OutboundTicket(PortSupervisor owner, Callsign remote) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) owner.ReleaseOutbound(remote);
        }
    }

    /// <summary>Bring up all enabled ports in the current config. Called once on
    /// host start, before the first <see cref="ApplyAsync"/>.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var current = config.Current;
        foreach (var port in current.Ports.Where(p => p.Enabled))
        {
            await BringUpAsync(port, current.Identity, cancellationToken).ConfigureAwait(false);
        }
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
        foreach (var id in plan.ToTearDown) await TearDownAsync(id).ConfigureAwait(false);
        foreach (var id in plan.ToDisable) await TearDownAsync(id).ConfigureAwait(false);

        // Single-port restarts: down then up.
        foreach (var port in plan.ToRestart)
        {
            await TearDownAsync(port.Id).ConfigureAwait(false);
            await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        }

        // Bring up new + newly-enabled ports.
        foreach (var port in plan.ToBringUp) await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);
        foreach (var port in plan.ToEnable) await BringUpAsync(port, newConfig.Identity, cancellationToken).ConfigureAwait(false);

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
    }

    private async Task BringUpAsync(PortConfig port, Identity identity, CancellationToken ct)
    {
        if (!Callsign.TryParse(identity.Callsign, out var myCall))
        {
            // Should be unreachable — validation guarantees a parseable callsign
            // before the config is applied — but never throw out of a reconcile.
            LogPortFaulted(port.Id, $"identity callsign '{identity.Callsign}' did not parse");
            return;
        }

        // Hoisted once (CA1873): cheap, and keeps method-invocation args out of
        // the log call sites below.
        var endpointText = port.Transport.DescribeEndpoint();

        // Resolve the port's named channel profile (if any) into effective AX.25 +
        // KISS params — explicit values win, the profile fills the gaps, no profile
        // = spec defaults. Opt-in tuning at the node-host layer (see ChannelProfiles).
        var (effectiveAx25, effectiveKiss) = ChannelProfiles.Resolve(port);

        IKissModem modem;
        try
        {
            modem = await transportFactory.CreateAsync(port.Transport, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Runtime fault on THIS port only — log + skip; the reconcile and the
            // rest of the ports proceed.
            LogPortFaultedEx(ex, port.Id, endpointText);
            return;
        }

        // kiss-tcp ports self-heal across a TNC/softmodem bounce: wrap the connected
        // modem so a dropped link reconnects (backoff + KISS-param replay) instead of
        // the port silently dying. The eager connect above preserves initial fault
        // isolation; this only adds reconnect-after-drop. (#50)
        if (port.Transport is KissTcpTransport)
        {
            modem = new ReconnectingKissModem(
                modem,
                token => transportFactory.CreateAsync(port.Transport, token),
                endpointText,
                loggerFactory.CreateLogger<ReconnectingKissModem>(),
                timeProvider);
        }

        var options = BuildListenerOptions(effectiveAx25, myCall);
        var listener = new Ax25Listener(modem, options, timeProvider);
        var connector = new Ax25OutboundConnector(port.Id, listener, r => ClaimOutbound(r));
        listener.SessionAccepted += (_, e) => OnSessionAccepted(listener, connector, e.Session);

        try
        {
            await listener.StartAsync(ct).ConfigureAwait(false);
            await ApplyKissParamsToModemAsync(modem, effectiveKiss, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogPortFaultedEx(ex, port.Id, endpointText);
            await listener.DisposeAsync().ConfigureAwait(false);
            await DisposeModemAsync(modem).ConfigureAwait(false);
            return;
        }

        var running = new RunningPort
        {
            Id = port.Id,
            Config = port,
            Modem = modem,
            Listener = listener,
            Started = true,
        };
        lock (ports) ports[port.Id] = running;

        // NET/ROM read-only awareness: subscribe this port's frame-trace tap so the
        // node-level service hears NODES broadcasts on it. Observation-only — it
        // cannot disturb the session path. Detached on teardown.
        netRom?.AttachPort(port.Id, myCall, listener);

        // Hoist the callsign too (CA1873) — endpointText is the one declared above.
        var callText = myCall.ToString();
        LogPortUp(port.Id, callText, endpointText);
    }

    private async Task TearDownAsync(string id)
    {
        RunningPort? running;
        lock (ports)
        {
            if (!ports.Remove(id, out running)) return;
        }
        // Detach the NET/ROM tap and cleanly disconnect any interlink AX.25 sessions
        // on this port BEFORE disposing the listener — DetachPortAsync DISCs each
        // interlink and waits (bounded) for the DISC/UA to round-trip on the wire, so
        // the neighbour isn't left with a half-open link it polls (the #309
        // contamination class). The listener is still alive here to carry the DISC.
        // Learned routes survive; their neighbours age out via obsolescence.
        if (netRom is not null) await netRom.DetachPortAsync(id).ConfigureAwait(false);
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
            if (netRom is not null) await netRom.DetachPortAsync(p.Id).ConfigureAwait(false);
            await p.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ApplyKissParamsAsync(PortConfig port, CancellationToken ct)
    {
        RunningPort? running;
        lock (ports)
        {
            ports.TryGetValue(port.Id, out running);
        }
        if (running is null) return;   // not up (e.g. faulted) — nothing live to tune

        // Resolve the profile here too so a live KISS re-apply uses the same
        // effective values a fresh bring-up would (explicit wins, profile fills).
        var (_, effectiveKiss) = ChannelProfiles.Resolve(port);
        await ApplyKissParamsToModemAsync(running.Modem, effectiveKiss, ct).ConfigureAwait(false);
        RebaselineConfig(port);
        LogKissParamsApplied(port.Id);
    }

    private void ApplyAx25Params(PortConfig port)
    {
        RunningPort? running;
        lock (ports)
        {
            ports.TryGetValue(port.Id, out running);
        }
        if (running is null) return;   // not up (e.g. faulted) — the next bring-up reads the new config

        // Resolve the profile here too so a live AX.25 reseed uses the same
        // effective values a fresh bring-up would (explicit wins, profile fills).
        var (effectiveAx25, _) = ChannelProfiles.Resolve(port);

        // Live-reseed: new sessions on this listener pick up the new AX.25 params;
        // existing sessions keep their identity and their in-flight state.
        running.Listener.UpdateSessionParameters(MapAx25Params(effectiveAx25));
        RebaselineConfig(port);
        LogAx25ParamsApplied(port.Id);
    }

    private static async Task ApplyKissParamsToModemAsync(IKissModem modem, KissParams? kiss, CancellationToken ct)
    {
        if (kiss is null) return;
        if (kiss.TxDelay is { } txd) await modem.SetTxDelayAsync(txd, ct).ConfigureAwait(false);
        if (kiss.Persistence is { } per) await modem.SetPersistenceAsync(per, ct).ConfigureAwait(false);
        if (kiss.SlotTime is { } slot) await modem.SetSlotTimeAsync(slot, ct).ConfigureAwait(false);
        if (kiss.TxTail is { } tail) await modem.SetTxTailAsync(tail, ct).ConfigureAwait(false);
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
                    Modem = running.Modem,
                    Listener = running.Listener,
                    Started = running.Started,
                };
            }
        }
    }

    private static Ax25ListenerOptions BuildListenerOptions(Ax25PortParams? ax25, Callsign myCall)
    {
        var p = MapAx25Params(ax25);
        return new Ax25ListenerOptions
        {
            MyCall = myCall,
            T1V = p.T1V,
            T2 = p.T2,
            T3 = p.T3,
            N2 = p.N2,
            K = p.K,
            MaxCachedPeers = p.MaxCachedPeers,
        };
    }

    // Map the config's AX.25 knobs to the engine's live-reseedable parameter
    // record. The single definition both BringUp (construction-time seed) and the
    // hot AX.25-params reconcile (UpdateSessionParameters) share, so the two paths
    // can never drift.
    private static Ax25SessionParameters MapAx25Params(Ax25PortParams? ax25) => new()
    {
        T1V = ax25?.T1Ms is { } t1 ? TimeSpan.FromMilliseconds(t1) : null,
        T2 = ax25?.T2Ms is { } t2 ? TimeSpan.FromMilliseconds(t2) : null,
        T3 = ax25?.T3Ms is { } t3 ? TimeSpan.FromMilliseconds(t3) : null,
        N2 = ax25?.N2,
        K = ax25?.WindowSize,
        MaxCachedPeers = ax25?.MaxCachedPeers ?? 64,
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
                    var env = new NodeConsoleEnvironment(config, routed, netRom);
                    var service = new NodeCommandService(env, loggerFactory.CreateLogger<NodeCommandService>());
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

    private static async Task DisposeModemAsync(IKissModem modem)
    {
        switch (modem)
        {
            case IAsyncDisposable ad: await ad.DisposeAsync().ConfigureAwait(false); break;
            case IDisposable d: d.Dispose(); break;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        await lifecycle.CancelAsync().ConfigureAwait(false);
        await TearDownAllAsync().ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Port {Id} faulted: {Reason}")]
    private partial void LogPortFaulted(string id, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: KISS parameters applied live (no restart).")]
    private partial void LogKissParamsApplied(string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: AX.25 parameters reseeded live; new sessions use them (existing sessions untouched).")]
    private partial void LogAx25ParamsApplied(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console session for {PeerId} faulted.")]
    private partial void LogConsoleFaulted(Exception ex, string peerId);
}
