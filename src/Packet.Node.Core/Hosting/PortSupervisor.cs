using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Packet.Ax25.Session;
using Packet.Core;
using Packet.Kiss;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
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
        ILoggerFactory? loggerFactory = null)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        logger = this.loggerFactory.CreateLogger<PortSupervisor>();
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
        return first is null ? null : new Ax25OutboundConnector(first.Id, first.Listener, r => ClaimOutbound(r));
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

        // AX.25 param changes — update the recorded baseline so the next bring-up
        // uses them; the live listener + its sessions are left alone (see the
        // ReconcilePlanner remarks). Object identity preserved.
        foreach (var port in plan.Ax25ParamsChanged)
        {
            RebaselineConfig(port);
            LogAx25ParamsDeferred(port.Id);
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

        var options = BuildListenerOptions(port, myCall);
        var listener = new Ax25Listener(modem, options, timeProvider);
        var connector = new Ax25OutboundConnector(port.Id, listener, r => ClaimOutbound(r));
        listener.SessionAccepted += (_, e) => OnSessionAccepted(listener, connector, e.Session);

        try
        {
            await listener.StartAsync(ct).ConfigureAwait(false);
            await ApplyKissParamsToModemAsync(modem, port.Kiss, ct).ConfigureAwait(false);
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

        await ApplyKissParamsToModemAsync(running.Modem, port.Kiss, ct).ConfigureAwait(false);
        RebaselineConfig(port);
        LogKissParamsApplied(port.Id);
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

    private static Ax25ListenerOptions BuildListenerOptions(PortConfig port, Callsign myCall)
    {
        var ax25 = port.Ax25;
        return new Ax25ListenerOptions
        {
            MyCall = myCall,
            T1V = ax25?.T1Ms is { } t1 ? TimeSpan.FromMilliseconds(t1) : null,
            T2 = ax25?.T2Ms is { } t2 ? TimeSpan.FromMilliseconds(t2) : null,
            T3 = ax25?.T3Ms is { } t3 ? TimeSpan.FromMilliseconds(t3) : null,
            N2 = ax25?.N2,
            K = ax25?.WindowSize,
            MaxCachedPeers = ax25?.MaxCachedPeers ?? 64,
        };
    }

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
                    var env = new NodeConsoleEnvironment(config, connector);
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Port {Id}: AX.25 parameters recorded for the next bring-up (live sessions untouched).")]
    private partial void LogAx25ParamsDeferred(string id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Console session for {PeerId} faulted.")]
    private partial void LogConsoleFaulted(Exception ex, string peerId);
}
