using System.Runtime.CompilerServices;
using Packet.Ax25.Session;
using Packet.NetRom.Transport;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Telemetry;

namespace Packet.Node.Core.Oarc;

/// <summary>
/// The concrete <see cref="IOarcStateSource"/> (#459): captures the node's live L2 links and L4
/// circuits from the running subsystems for the <see cref="OarcReporter"/>. It reads the live
/// <see cref="PortSupervisor"/> / <see cref="NetRomService"/> / <see cref="NodeTelemetry"/> handles off
/// <see cref="NodeHostedService"/> (which sets them at start, so the handles may be absent before the
/// node has started — a pre-start capture is simply empty). Enumerates each running port's
/// <see cref="Ax25Listener.ActiveSessions"/> (connected only), correlates per-link frame/byte counters
/// from telemetry, reads the NET/ROM circuit table, and tracks inbound-vs-outbound by latching the
/// <see cref="Ax25Listener.SessionAccepted"/> / <see cref="CircuitManager.IncomingCircuit"/> signals.
/// All identity/counter mapping lives here so the reporter stays a pure policy engine.
/// </summary>
/// <remarks>
/// Stable per-link/per-circuit ids are assigned lazily via <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed on the session/circuit instance (weak, so they vanish with the object and are never reused
/// while alive). Circuit segment/byte counters are reported as 0 — <see cref="NetRomCircuit"/> does not
/// expose them yet (the named §9 fidelity gap); a circuit still appears with its identity + lifecycle.
/// </remarks>
public sealed class NodeOarcStateSource : IOarcStateSource
{
    private readonly NodeHostedService host;
    private readonly IConfigProvider config;
    private readonly TimeProvider clock;
    private readonly DateTimeOffset start;

    // Stable identity + first-seen per live session/circuit (weak keys → auto-cleanup).
    private readonly ConditionalWeakTable<Ax25Session, LinkMeta> linkMeta = new();
    private readonly ConditionalWeakTable<NetRomCircuit, CircuitMeta> circuitMeta = new();

    // Weak sets latching the "this was remote-initiated" signal from the lifecycle events.
    private readonly ConditionalWeakTable<Ax25Session, object> inboundSessions = new();
    private readonly ConditionalWeakTable<NetRomCircuit, object> inboundCircuits = new();

    // Listeners we've already hooked SessionAccepted on, and the circuit manager we've hooked.
    private readonly ConditionalWeakTable<Ax25Listener, object> hookedListeners = new();
    private CircuitManager? hookedCircuitManager;
    private readonly object hookGate = new();

    private int linkIdSeq;
    private int circuitIdSeq;

    public NodeOarcStateSource(NodeHostedService host, IConfigProvider config, TimeProvider? clock = null)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.clock = clock ?? TimeProvider.System;
        start = this.clock.GetUtcNow();
    }

    public OarcNodeSnapshot Capture()
    {
        var now = clock.GetUtcNow();
        EnsureHooks();

        var netrom = host.NetRom;
        return new OarcNodeSnapshot
        {
            UptimeSeconds = Math.Max(0, (long)(now - start).TotalSeconds),
            L3Relayed = netrom?.ForwardingStats.ForwardedFrames ?? 0,
            Links = CaptureLinks(now),
            Circuits = CaptureCircuits(netrom, now),
        };
    }

    private List<OarcLinkState> CaptureLinks(DateTimeOffset now)
    {
        var links = new List<OarcLinkState>();
        var supervisor = host.Supervisor;
        if (supervisor is null)
        {
            return links;
        }

        var telemetry = host.Telemetry;
        foreach (var portId in supervisor.RunningPortIds)
        {
            var port = supervisor.GetPort(portId);
            if (port is null)
            {
                continue;
            }

            foreach (var session in port.Listener.ActiveSessions)
            {
                if (session.CurrentState is not ("Connected" or "TimerRecovery"))
                {
                    continue;   // only a live link counts
                }

                var meta = linkMeta.GetValue(session, s => new LinkMeta(
                    Interlocked.Increment(ref linkIdSeq),
                    inboundSessions.TryGetValue(s, out _),
                    now));

                var remote = session.Context.Remote.ToString();
                var local = session.Context.Local.ToString();
                var snap = telemetry.Link(portId, remote);

                links.Add(new OarcLinkState
                {
                    Id = meta.Id,
                    Port = portId,
                    Local = local,
                    Remote = remote,
                    Inbound = meta.Inbound,
                    // Per-(port,peer) counters; may aggregate across reconnects to the same peer until
                    // the port detaches (acceptable for the map's purposes).
                    FramesSent = snap?.FramesOut ?? 0,
                    FramesReceived = snap?.FramesIn ?? 0,
                    FramesResent = 0,   // not tracked by NodeTelemetry; a named follow-up
                    BytesSent = snap?.BytesOut ?? 0,
                    BytesReceived = snap?.BytesIn ?? 0,
                    L2RttMs = null,     // no smoothed RTT exposed yet
                    UpForSeconds = Math.Max(0, (long)(now - meta.FirstSeen).TotalSeconds),
                });
            }
        }
        return links;
    }

    private List<OarcCircuitState> CaptureCircuits(NetRomService? netrom, DateTimeOffset now)
    {
        var circuits = new List<OarcCircuitState>();
        var manager = netrom?.Circuits;
        if (manager is null)
        {
            return circuits;
        }

        var localNode = config.Current.Identity.Callsign;
        foreach (var circuit in manager.Circuits)
        {
            if (circuit.State != NetRomCircuitState.Connected)
            {
                continue;
            }

            var meta = circuitMeta.GetValue(circuit, c => new CircuitMeta(
                Interlocked.Increment(ref circuitIdSeq),
                inboundCircuits.TryGetValue(c, out _),
                now));

            circuits.Add(new OarcCircuitState
            {
                Id = meta.Id,
                Local = localNode,
                Remote = circuit.RemoteNode.ToString(),
                Inbound = meta.Inbound,
                Service = null,
                // NetRomCircuit exposes no segment/byte counters yet (design §9) — 0 for now.
                SegmentsSent = 0,
                SegmentsReceived = 0,
                SegmentsResent = 0,
                SegmentsQueued = 0,
                BytesSent = null,
                BytesReceived = null,
                UpForSeconds = Math.Max(0, (long)(now - meta.FirstSeen).TotalSeconds),
            });
        }
        return circuits;
    }

    /// <summary>Hook the inbound-direction signals for any listeners/circuit-manager that have appeared
    /// since the last capture (ports start/stop over the node's life; the circuit manager appears when
    /// NET/ROM L4 turns on). Idempotent.</summary>
    private void EnsureHooks()
    {
        var supervisor = host.Supervisor;
        if (supervisor is not null)
        {
            foreach (var portId in supervisor.RunningPortIds)
            {
                var listener = supervisor.GetPort(portId)?.Listener;
                if (listener is null || hookedListeners.TryGetValue(listener, out _))
                {
                    continue;
                }
                hookedListeners.AddOrUpdate(listener, new object());
                listener.SessionAccepted += OnSessionAccepted;
            }
        }

        var manager = host.NetRom?.Circuits;
        if (manager is not null && !ReferenceEquals(manager, hookedCircuitManager))
        {
            lock (hookGate)
            {
                if (!ReferenceEquals(manager, hookedCircuitManager))
                {
                    manager.IncomingCircuit += OnIncomingCircuit;
                    hookedCircuitManager = manager;
                }
            }
        }
    }

    private void OnSessionAccepted(object? sender, Ax25SessionEventArgs e) =>
        inboundSessions.AddOrUpdate(e.Session, new object());

    private void OnIncomingCircuit(object? sender, IncomingCircuitEventArgs e) =>
        inboundCircuits.AddOrUpdate(e.Circuit, new object());

    private sealed record LinkMeta(int Id, bool Inbound, DateTimeOffset FirstSeen);

    private sealed record CircuitMeta(int Id, bool Inbound, DateTimeOffset FirstSeen);
}
