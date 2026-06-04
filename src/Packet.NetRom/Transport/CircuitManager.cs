using System.Collections.Concurrent;
using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Transport;

/// <summary>
/// Owns this node's NET/ROM L4 circuit table: mints local circuits (allocating the
/// circuit index/id pair), demultiplexes inbound datagrams to the right
/// <see cref="NetRomCircuit"/>, accepts/refuses inbound connect requests, and
/// drives every circuit's retransmit timer off one injected
/// <see cref="System.TimeProvider"/> tick. It is the protocol-side seam the node
/// host plugs into: the host supplies a <see cref="SendPacket"/> sink (wire a
/// datagram onto the interlink to its destination node) and subscribes
/// <see cref="IncomingCircuit"/> (a remote opened a circuit to us).
/// </summary>
/// <remarks>
/// <para>
/// <b>Host-free.</b> Like <see cref="NetRomCircuit"/>, the manager has no AX.25 /
/// node-host dependency — it speaks only <see cref="NetRomPacket"/> in and out, so
/// it is fully unit-testable and the same instance can sit behind any transport.
/// </para>
/// <para>
/// <b>Demultiplexing.</b> A datagram's transport header names <em>our</em> circuit
/// (the index/id we handed the peer at connect time), so inbound routing is a
/// table lookup on <c>(index,id)</c>. A Connect Request, by contrast, names the
/// <em>peer's</em> circuit in those fields (we don't have one yet) — so a Connect
/// Request that doesn't match an existing circuit mints a fresh inbound circuit and
/// raises <see cref="IncomingCircuit"/> for the host to accept or refuse.
/// </para>
/// </remarks>
public sealed class CircuitManager : IDisposable
{
    private Callsign localNode;
    private readonly NetRomCircuitOptions options;
    private readonly TimeProvider time;
    private readonly object gate = new();

    // Our circuits keyed by the (index,id) we allocated — the key the peer stamps
    // into datagrams addressed to us.
    private readonly Dictionary<(byte Index, byte Id), NetRomCircuit> byLocalKey = new();
    // Inbound circuits also keyed by the PEER's identity (origin node + the peer's
    // own index/id from its Connect Request) so a RETRANSMITTED Connect Request — its
    // header names the peer's circuit, not ours, so it can't match byLocalKey —
    // re-acks the existing circuit instead of minting a duplicate.
    private readonly Dictionary<(Callsign Node, byte Index, byte Id), NetRomCircuit> byPeerKey = new();
    // Reverse map so deregistration can drop a circuit's peer-key entry without a
    // value scan.
    private readonly Dictionary<NetRomCircuit, (Callsign Node, byte Index, byte Id)> peerKeyOf = new();
    private byte nextIndex;
    private byte nextId;

    private readonly ITimer? tickTimer;
    private int disposed;

    /// <summary>The sink the host wires to ship a datagram onto the interlink toward
    /// <see cref="NetRomPacket.Network"/>.Destination. Must be set before any
    /// circuit transmits.</summary>
    public Action<NetRomPacket>? SendPacket { get; set; }

    /// <summary>
    /// Raised when a remote originates a circuit to us. The handler decides whether
    /// to accept (call <see cref="AcceptIncoming"/>) or refuse
    /// (<see cref="RefuseIncoming"/>) and, on accept, wires the circuit's
    /// data/close callbacks + bridges it. The circuit is freshly minted and not yet
    /// acknowledged when this fires.
    /// </summary>
    public event EventHandler<IncomingCircuitEventArgs>? IncomingCircuit;

    /// <summary>Construct the manager for a node. Pass <paramref name="tickInterval"/>
    /// to self-drive retransmit timers off the time provider; pass <c>null</c> to
    /// drive <see cref="Tick"/> manually (the deterministic-test path).</summary>
    public CircuitManager(
        Callsign localNode,
        NetRomCircuitOptions? options = null,
        TimeProvider? time = null,
        TimeSpan? tickInterval = null)
    {
        this.localNode = localNode;
        this.options = options ?? NetRomCircuitOptions.Default;
        this.time = time ?? TimeProvider.System;

        if (tickInterval is { } interval && interval > TimeSpan.Zero)
        {
            tickTimer = this.time.CreateTimer(_ => Tick(), state: null, dueTime: interval, period: interval);
        }
    }

    /// <summary>The live circuits (snapshot), for surfacing / tests.</summary>
    public IReadOnlyList<NetRomCircuit> Circuits
    {
        get { lock (gate) return byLocalKey.Values.ToList(); }
    }

    /// <summary>
    /// Set the local node callsign stamped into the L3 origin of circuits this
    /// manager mints. The node host calls this once the node identity is known (at
    /// first port attach) — circuits are minted after, so they carry it. Affects
    /// circuits opened <em>after</em> the call (existing circuits keep their origin).
    /// </summary>
    public void SetLocalNode(Callsign node)
    {
        lock (gate)
        {
            localNode = node;
        }
    }

    /// <summary>
    /// Mint a local circuit to <paramref name="remoteNode"/>, allocate its (index,id),
    /// register it, and wire its packet sink + auto-deregistration on close. The
    /// caller then sets <see cref="NetRomCircuit.DataReceived"/> /
    /// <see cref="NetRomCircuit.Connected"/> / <see cref="NetRomCircuit.Closed"/> and
    /// calls <see cref="NetRomCircuit.Connect"/>.
    /// </summary>
    public NetRomCircuit OpenCircuit(Callsign remoteNode)
    {
        lock (gate)
        {
            var (index, id) = AllocateKey();
            var circuit = new NetRomCircuit(index, id, localNode, remoteNode, options, time)
            {
                SendPacket = p => SendPacket?.Invoke(p),
            };
            Register(circuit);
            return circuit;
        }
    }

    /// <summary>
    /// Feed an inbound datagram (parsed from an interlink I-frame's info field).
    /// Routes it to the addressed circuit, or, for a Connect Request with no
    /// matching circuit, mints an inbound circuit and raises
    /// <see cref="IncomingCircuit"/>. Tolerant of stray datagrams — an
    /// unroutable non-connect datagram is dropped.
    /// </summary>
    public void OnPacket(NetRomPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var t = packet.Transport;
        var key = (t.CircuitIndex, t.CircuitId);

        NetRomCircuit? circuit;
        lock (gate)
        {
            byLocalKey.TryGetValue(key, out circuit);
        }

        if (circuit is not null)
        {
            circuit.OnPacket(packet);
            return;
        }

        // No existing circuit. Only a Connect Request creates one; everything else
        // is for a circuit we don't have (a late/duplicate datagram) — drop it,
        // except a Disconnect Request, which we courteously disconnect-ack so the
        // peer stops retransmitting.
        if (t.Opcode == NetRomOpcode.ConnectRequest)
        {
            // Dedup a retransmitted Connect Request (its header names the peer's
            // circuit, so it never matches byLocalKey): if we already minted a circuit
            // for this peer-circuit identity, hand the retransmit to it (it re-acks)
            // rather than minting a duplicate.
            var peerKey = (packet.Network.Origin, t.CircuitIndex, t.CircuitId);
            NetRomCircuit? existing;
            lock (gate)
            {
                byPeerKey.TryGetValue(peerKey, out existing);
            }
            if (existing is not null)
            {
                existing.OnPacket(packet);
                return;
            }
            MintInbound(packet);
        }
        else if (t.Opcode == NetRomOpcode.DisconnectRequest)
        {
            // Reflect a Disconnect Acknowledge addressed to the peer's circuit
            // (carried in this request's index/id) so a half-open peer settles.
            var ack = new NetRomPacket
            {
                Network = new NetRomNetworkHeader
                {
                    Origin = localNode,
                    Destination = packet.Network.Origin,
                    TimeToLive = options.TimeToLive,
                },
                Transport = new NetRomTransportHeader
                {
                    CircuitIndex = t.CircuitIndex,
                    CircuitId = t.CircuitId,
                    TxSequence = 0,
                    RxSequence = 0,
                    Opcode = NetRomOpcode.DisconnectAcknowledge,
                    Flags = NetRomTransportFlags.None,
                },
            };
            SendPacket?.Invoke(ack);
        }
    }

    /// <summary>Accept a circuit raised by <see cref="IncomingCircuit"/>: adopt the
    /// peer's index/id + proposed window, move it to Connected, and send the Connect
    /// Acknowledge.</summary>
    public static void AcceptIncoming(IncomingCircuitEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Circuit.AcceptInbound(e.PeerIndex, e.PeerId, e.ProposedWindow);
    }

    /// <summary>Refuse a circuit raised by <see cref="IncomingCircuit"/>: send a
    /// refusing Connect Acknowledge and drop it from the table.</summary>
    public void RefuseIncoming(IncomingCircuitEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        e.Circuit.RefuseInbound(e.PeerIndex, e.PeerId);
        Deregister(e.Circuit);
    }

    /// <summary>Advance every circuit's timers by one tick (retransmits + timeouts).
    /// Called by the internal timer when a tick interval was supplied, or by a test
    /// after advancing a <c>FakeTimeProvider</c>.</summary>
    public void Tick()
    {
        NetRomCircuit[] all;
        lock (gate)
        {
            all = byLocalKey.Values.ToArray();
        }
        foreach (var c in all)
        {
            c.Tick();
        }
    }

    // ─── Internals ──────────────────────────────────────────────────────

    private void MintInbound(NetRomPacket request)
    {
        var t = request.Transport;
        Callsign remoteNode = request.Network.Origin;

        // Parse the originating user (first shifted callsign in the connect payload)
        // for the host's benefit; fall back to the origin node if absent.
        Callsign originatingUser = remoteNode;
        if (request.Payload.Length >= NetRomCallsign.ShiftedLength &&
            NetRomCallsign.TryReadShifted(request.Payload.Span, out var user))
        {
            originatingUser = user;
        }

        var peerKey = (remoteNode, t.CircuitIndex, t.CircuitId);
        NetRomCircuit circuit;
        lock (gate)
        {
            var (index, id) = AllocateKey();
            circuit = new NetRomCircuit(index, id, localNode, remoteNode, options, time)
            {
                SendPacket = p => SendPacket?.Invoke(p),
            };
            Register(circuit);
            byPeerKey[peerKey] = circuit;
            peerKeyOf[circuit] = peerKey;
        }

        // The peer's own (index,id) live in the Connect Request's index/id fields;
        // its proposed window is in the TX-sequence slot.
        var args = new IncomingCircuitEventArgs(circuit, remoteNode, originatingUser, t.CircuitIndex, t.CircuitId, t.TxSequence);
        var handler = IncomingCircuit;
        if (handler is null)
        {
            // No one is listening — refuse rather than leave a dangling half-open
            // circuit.
            RefuseIncoming(args);
            return;
        }
        handler.Invoke(this, args);
    }

    private (byte Index, byte Id) AllocateKey()
    {
        // Linear probe for a free (index,id). The id advances so a reused index gets
        // a fresh serial (the canonical "circuit id qualifies the index" rule).
        for (int attempt = 0; attempt < 65536; attempt++)
        {
            var key = (nextIndex, nextId);
            nextIndex++;
            if (nextIndex == 0)
            {
                nextId++;   // wrapped the index — bump the id serial
            }
            if (!byLocalKey.ContainsKey(key))
            {
                return key;
            }
        }
        throw new InvalidOperationException("NET/ROM circuit table exhausted (65536 live circuits).");
    }

    private void Register(NetRomCircuit circuit)
    {
        byLocalKey[(circuit.LocalIndex, circuit.LocalId)] = circuit;
        circuit.Closed += _ => Deregister(circuit);
    }

    private void Deregister(NetRomCircuit circuit)
    {
        lock (gate)
        {
            byLocalKey.Remove((circuit.LocalIndex, circuit.LocalId));
            if (peerKeyOf.Remove(circuit, out var peerKey))
            {
                byPeerKey.Remove(peerKey);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }
        tickTimer?.Dispose();
        NetRomCircuit[] all;
        lock (gate)
        {
            all = byLocalKey.Values.ToArray();
            byLocalKey.Clear();
        }
        foreach (var c in all)
        {
            c.Disconnect();
        }
    }
}

/// <summary>
/// Carries an inbound NET/ROM circuit request to a <see cref="CircuitManager.IncomingCircuit"/>
/// handler. The handler accepts (<see cref="CircuitManager.AcceptIncoming"/>) or
/// refuses (<see cref="CircuitManager.RefuseIncoming"/>) it.
/// </summary>
public sealed class IncomingCircuitEventArgs : EventArgs
{
    internal IncomingCircuitEventArgs(
        NetRomCircuit circuit, Callsign remoteNode, Callsign originatingUser,
        byte peerIndex, byte peerId, int proposedWindow)
    {
        Circuit = circuit;
        RemoteNode = remoteNode;
        OriginatingUser = originatingUser;
        PeerIndex = peerIndex;
        PeerId = peerId;
        ProposedWindow = proposedWindow;
    }

    /// <summary>The freshly-minted circuit (registered, not yet acknowledged).</summary>
    public NetRomCircuit Circuit { get; }

    /// <summary>The far node that originated the circuit.</summary>
    public Callsign RemoteNode { get; }

    /// <summary>The end user the circuit is on behalf of (from the connect payload).</summary>
    public Callsign OriginatingUser { get; }

    /// <summary>The peer's circuit-table index (to address replies to it).</summary>
    public byte PeerIndex { get; }

    /// <summary>The peer's circuit-table id.</summary>
    public byte PeerId { get; }

    /// <summary>The window size the peer proposed in its Connect Request.</summary>
    public int ProposedWindow { get; }
}
