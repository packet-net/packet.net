using Packet.Core;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Transport;

/// <summary>
/// One end of a NET/ROM L4 virtual circuit: a hand-written, end-to-end
/// sliding-window transport (connect / info / disconnect with negotiated window,
/// 8-bit sequence numbers, choke flow control, selective-NAK retransmit, and L4
/// fragment/reassembly at 236 bytes). It runs <em>above</em> the AX.25 interlink —
/// it emits <see cref="NetRomPacket"/>s through a sink the host wires to
/// <c>Ax25Listener.SendData(…, pid 0xCF)</c>, and is fed inbound datagrams via
/// <see cref="OnPacket"/>; it knows nothing about AX.25 itself, keeping
/// <c>Packet.NetRom</c> free of host/transport dependencies.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not derived from SDL.</b> NET/ROM has no normative SDL figures and BPQ is
/// the de-facto reference, so per the research this transport is hand-written with
/// hard tests rather than routed through the ax25sdl pipeline. The state machine
/// is the conventional one (Disconnected / Connecting / Connected / Disconnecting,
/// <see cref="NetRomCircuitState"/>); divergence-prone constants (window, timers,
/// retries, TTL, fragment size) are named knobs on <see cref="NetRomCircuitOptions"/>.
/// </para>
/// <para>
/// <b>Time + threading.</b> All timing is via the injected
/// <see cref="System.TimeProvider"/> (§2.7); the owner drives retransmits by
/// calling <see cref="Tick"/> at the clock's cadence (the
/// <see cref="CircuitManager"/> does this on a <c>TimeProvider</c> timer). Every
/// public method is serialised under one lock, so inbound datagrams (off an AX.25
/// pump thread), application sends, and the timer tick never tear the circuit.
/// </para>
/// </remarks>
public sealed class NetRomCircuit
{
    private readonly NetRomCircuitOptions options;
    private readonly TimeProvider time;
    private readonly object gate = new();

    // Outbound packets queued by Emit during a locked section, flushed to SendPacket
    // AFTER the gate is released (see FlushSends). The sink reaches into the AX.25
    // session, which takes its own lock; invoking it while we hold the gate deadlocks
    // against the inbound pump, which holds the session lock and then takes the gate
    // (OnPacket). Guarded by gate.
    private readonly List<NetRomPacket> pendingSends = new();

    // Identity. "Local" index/id are the values WE chose and put in our outgoing
    // headers' index/id so the peer addresses replies to us; "remote" index/id are
    // the values the PEER chose (learned from its Connect / Connect-Ack) that we
    // stamp into datagrams we send it.
    private readonly byte localIndex;
    private readonly byte localId;
    private byte remoteIndex;
    private byte remoteId;

    private readonly Callsign localNode;     // our node callsign (L3 origin on our sends)
    private readonly Callsign remoteNode;    // the far node (L3 destination on our sends)

    private NetRomCircuitState state = NetRomCircuitState.Disconnected;

    // Negotiated window (set at connect time).
    private int window;

    // Send side — 8-bit sequence space (mod 256).
    private byte vs;                                       // next send sequence to allocate
    private byte va;                                       // oldest unacknowledged sequence
    private readonly Queue<Fragment> sendQueue = new();    // fragments waiting for window room
    private readonly List<Unacked> unacked = new();        // in-flight Information, oldest first

    // Receive side.
    private byte vr;                                   // next send sequence we expect from the peer
    private readonly List<byte> reassembly = new();    // accumulates more-follows fragments

    // Flow control.
    private bool peerChoked;                            // peer told us to stop sending Info
    private bool localChoked;                           // we told the peer to stop (receive backlog)
    private int pendingDeliveries;                      // received-but-not-yet-delivered count

    // Connect/disconnect retransmit bookkeeping (Information uses per-message timers
    // inside `unacked`). The control frame is rebuilt on each retry, so no bytes
    // are cached.
    private DateTimeOffset controlDeadline;
    private bool controlTimerArmed;
    private int controlRetries;

    // The end user the circuit is on behalf of (carried in the Connect Request).
    private Callsign connectUser;

    /// <summary>The single sink that emits an outbound datagram. The owner (the
    /// <see cref="CircuitManager"/>) wires it to ship the bytes in an interlink
    /// I-frame (PID 0xCF). Set once before driving the circuit.</summary>
    public Action<NetRomPacket>? SendPacket { get; set; }

    /// <summary>Raised with reassembled user data to deliver upward (one event per
    /// completed logical frame — fragments are joined first). An <c>event</c> so the
    /// manager and the consumer can both observe without clobbering each other.</summary>
    public event Action<ReadOnlyMemory<byte>>? DataReceived;

    /// <summary>Raised once when the circuit transitions to
    /// <see cref="NetRomCircuitState.Connected"/> (our connect accepted, or we
    /// accepted an inbound connect).</summary>
    public event Action? Connected;

    /// <summary>Raised once when the circuit reaches
    /// <see cref="NetRomCircuitState.Disconnected"/>, with the reason. The manager
    /// subscribes this to deregister the circuit; consumers subscribe it for the
    /// close notification — both fire because it is an <c>event</c>.</summary>
    public event Action<NetRomCircuitCloseReason>? Closed;

    /// <summary>Construct a circuit end. The owner allocates the local index/id and
    /// supplies the node callsigns for the L3 header.</summary>
    public NetRomCircuit(
        byte localIndex, byte localId,
        Callsign localNode, Callsign remoteNode,
        NetRomCircuitOptions? options = null, TimeProvider? time = null)
    {
        this.localIndex = localIndex;
        this.localId = localId;
        this.localNode = localNode;
        this.remoteNode = remoteNode;
        this.options = options ?? NetRomCircuitOptions.Default;
        this.time = time ?? TimeProvider.System;
        window = this.options.WindowSize;
    }

    /// <summary>Our circuit-table index (the value the peer addresses replies to).</summary>
    public byte LocalIndex => localIndex;

    /// <summary>Our circuit-table id (qualifies <see cref="LocalIndex"/>).</summary>
    public byte LocalId => localId;

    /// <summary>The far node this circuit reaches.</summary>
    public Callsign RemoteNode => remoteNode;

    /// <summary>The current lifecycle state (snapshot).</summary>
    public NetRomCircuitState State { get { lock (gate) return state; } }

    /// <summary>The negotiated send-window size (after connect).</summary>
    public int Window { get { lock (gate) return window; } }

    /// <summary>True while the peer has us choked (we are holding Information back).</summary>
    public bool PeerChoked { get { lock (gate) return peerChoked; } }

    // ─── Origination ────────────────────────────────────────────────────

    /// <summary>
    /// Originate the circuit: send a Connect Request (proposing our window) carrying
    /// the originating user + node callsigns, and arm the connect retransmit timer.
    /// No-op if not in <see cref="NetRomCircuitState.Disconnected"/>.
    /// </summary>
    /// <param name="originatingUser">The end user the circuit is on behalf of
    /// (carried in the Connect Request payload, after the originating node).</param>
    public void Connect(Callsign originatingUser)
    {
        lock (gate)
        {
            if (state != NetRomCircuitState.Disconnected)
            {
                return;
            }
            state = NetRomCircuitState.Connecting;
            controlRetries = 0;
            connectUser = originatingUser;
            SendConnectRequest();
            ArmControlTimer();
        }
        FlushSends();
    }

    // ─── Application send ───────────────────────────────────────────────

    /// <summary>
    /// Queue user data for transmission. Fragments it into ≤<see cref="NetRomCircuitOptions.FragmentSize"/>
    /// Information messages (more-follows on all but the last) and pushes as many
    /// as the window + peer-choke allow onto the wire now; the rest drain as acks
    /// arrive. No-op (data dropped) if the circuit is not connected.
    /// </summary>
    public void Send(ReadOnlyMemory<byte> data)
    {
        lock (gate)
        {
            if (state != NetRomCircuitState.Connected || data.IsEmpty)
            {
                return;
            }

            // Fragment into ≤FragmentSize chunks. Each fragment carries more-follows
            // except the last of THIS logical frame; the flag is stored alongside
            // the bytes so it survives across multiple Send() calls in the queue.
            int frag = Math.Max(1, options.FragmentSize);
            int offset = 0;
            while (offset < data.Length)
            {
                int take = Math.Min(frag, data.Length - offset);
                bool more = (offset + take) < data.Length;
                sendQueue.Enqueue(new Fragment(data.Slice(offset, take).ToArray(), more));
                offset += take;
            }

            PumpSendQueue();
        }
        FlushSends();
    }

    // ─── Disconnect ─────────────────────────────────────────────────────

    /// <summary>
    /// Tear the circuit down: send a Disconnect Request and arm its retransmit
    /// timer. If not connected, closes locally at once. Idempotent.
    /// </summary>
    public void Disconnect()
    {
        // try/finally so the queued Disconnect Request is flushed even though the
        // switch returns from inside the lock (see FlushSends — deadlock fix).
        try
        {
            lock (gate)
            {
                switch (state)
                {
                    case NetRomCircuitState.Disconnected:
                    case NetRomCircuitState.Disconnecting:
                        return;
                    case NetRomCircuitState.Connecting:
                        // Never established — close locally; nothing to disconnect-ack.
                        Close(NetRomCircuitCloseReason.Normal);
                        return;
                    case NetRomCircuitState.Connected:
                        state = NetRomCircuitState.Disconnecting;
                        controlRetries = 0;
                        SendDisconnectRequest();
                        ArmControlTimer();
                        return;
                }
            }
        }
        finally
        {
            FlushSends();
        }
    }

    // ─── Inbound ────────────────────────────────────────────────────────

    /// <summary>
    /// Feed an inbound datagram (already parsed from an interlink I-frame's info
    /// field) addressed to this circuit. Drives the FSM: connect/ack, info (with
    /// ack + choke + NAK + reassembly), disconnect/ack. Tolerant of any opcode —
    /// an unexpected message for the current state is ignored, never throws.
    /// </summary>
    public void OnPacket(NetRomPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (gate)
        {
            var t = packet.Transport;
            switch (t.Opcode)
            {
                case NetRomOpcode.ConnectRequest:
                    OnConnectRequest(t);
                    break;
                case NetRomOpcode.ConnectAcknowledge:
                    OnConnectAcknowledge(t);
                    break;
                case NetRomOpcode.DisconnectRequest:
                    OnDisconnectRequest(t);
                    break;
                case NetRomOpcode.DisconnectAcknowledge:
                    OnDisconnectAcknowledge(t);
                    break;
                case NetRomOpcode.Information:
                    OnInformation(t, packet.Payload);
                    break;
                case NetRomOpcode.InformationAcknowledge:
                    OnInformationAcknowledge(t);
                    break;
                default:
                    // Unknown opcode — ignore.
                    break;
            }
        }
        FlushSends();
    }

    /// <summary>
    /// Accept an inbound circuit: this end was created in response to a Connect
    /// Request, so adopt the peer's index/id and proposed window, move to Connected,
    /// and send the Connect Acknowledge. Used by <see cref="CircuitManager"/> when
    /// it mints a circuit for an incoming connect.
    /// </summary>
    internal void AcceptInbound(byte peerIndex, byte peerId, int proposedWindow)
    {
        lock (gate)
        {
            remoteIndex = peerIndex;
            remoteId = peerId;
            window = Math.Clamp(Math.Min(proposedWindow <= 0 ? options.WindowSize : proposedWindow, options.WindowSize), 1, 127);
            state = NetRomCircuitState.Connected;
            SendConnectAcknowledge(refused: false);
            Connected?.Invoke();
        }
        FlushSends();
    }

    /// <summary>
    /// Refuse an inbound circuit: send a Connect Acknowledge with the refuse
    /// (choke) bit set and stay disconnected. Used by the manager when it cannot
    /// accept (e.g. no listener / table full).
    /// </summary>
    internal void RefuseInbound(byte peerIndex, byte peerId)
    {
        lock (gate)
        {
            remoteIndex = peerIndex;
            remoteId = peerId;
            SendConnectAcknowledge(refused: true);
            state = NetRomCircuitState.Disconnected;
        }
        FlushSends();
    }

    // ─── Timer ──────────────────────────────────────────────────────────

    /// <summary>
    /// Drive time-based behaviour: retransmit the oldest unacknowledged Information
    /// message (or the pending connect/disconnect control message) whose timeout
    /// has elapsed, failing the circuit once retries are exhausted. The owner calls
    /// this at the clock cadence; it is cheap when nothing is due.
    /// </summary>
    public void Tick()
    {
        // try/finally so queued retransmits are flushed even on the timeout-Close
        // early returns from inside the lock (see FlushSends — deadlock fix).
        try
        {
            lock (gate)
            {
                var now = time.GetUtcNow();

                // Control (connect/disconnect) retransmit.
                if (controlTimerArmed && now >= controlDeadline)
                {
                    if (controlRetries >= options.MaxRetries)
                    {
                        controlTimerArmed = false;
                        Close(NetRomCircuitCloseReason.Timeout);
                        return;
                    }
                    controlRetries++;
                    switch (state)
                    {
                        case NetRomCircuitState.Connecting: SendConnectRequest(); break;
                        case NetRomCircuitState.Disconnecting: SendDisconnectRequest(); break;
                    }
                    ArmControlTimer();
                }

                // Information retransmit — oldest unacked first.
                if (state == NetRomCircuitState.Connected && unacked.Count > 0)
                {
                    var oldest = unacked[0];
                    if (now >= oldest.SentAt + options.RetransmitTimeout)
                    {
                        if (oldest.Retries >= options.MaxRetries)
                        {
                            Close(NetRomCircuitCloseReason.Timeout);
                            return;
                        }
                        // Retransmit every in-flight frame from the oldest (go-back style),
                        // bumping their timers — NET/ROM has no cumulative-ack guarantee
                        // the peer kept later frames after a gap.
                        for (int i = 0; i < unacked.Count; i++)
                        {
                            var u = unacked[i];
                            SendInformation(u.Sequence, u.Payload, u.MoreFollows);
                            unacked[i] = u with { SentAt = now, Retries = u.Retries + 1 };
                        }
                    }
                }
            }
        }
        finally
        {
            FlushSends();
        }
    }

    // ─── Receive-side flow control ──────────────────────────────────────

    /// <summary>
    /// Tell the consumer's drain progress: call after delivering received data so
    /// the circuit can release a previously-asserted choke once its receive backlog
    /// drains below the threshold. The node bridge delivers synchronously so this
    /// is usually a no-op, but it is the seam for real backpressure.
    /// </summary>
    public void OnDeliveryDrained()
    {
        lock (gate)
        {
            if (pendingDeliveries > 0)
            {
                pendingDeliveries--;
            }
            MaybeReleaseChoke();
        }
        FlushSends();
    }

    // ─── FSM handlers (caller holds the lock) ───────────────────────────

    private void OnConnectAcknowledge(NetRomTransportHeader t)
    {
        if (state != NetRomCircuitState.Connecting)
        {
            return;
        }

        // The ack names OUR index/id in its index/id fields (it is addressed to us),
        // and the peer's own index/id are carried in the TX/RX sequence slots on a
        // connect-ack (the canonical overload: it tells us how to address it back).
        remoteIndex = t.TxSequence;
        remoteId = t.RxSequence;

        controlTimerArmed = false;

        // Bit-7 (choke) on a connect-ack means refused.
        if (t.Choke)
        {
            Close(NetRomCircuitCloseReason.Refused);
            return;
        }

        // Window negotiation: our Connect Request proposed options.WindowSize and we
        // keep that as our send ceiling. (The far end has independently accepted a
        // window ≤ its own proposal; vanilla NET/ROM does not require us to shrink
        // ours below what we proposed, and our send window is bounded by `window`
        // either way.)
        state = NetRomCircuitState.Connected;
        Connected?.Invoke();
        PumpSendQueue();
    }

    private void OnConnectRequest(NetRomTransportHeader t)
    {
        // A retransmitted Connect Request after we're already up: just re-ack.
        if (state == NetRomCircuitState.Connected)
        {
            SendConnectAcknowledge(refused: false);
            return;
        }
        // Otherwise the manager owns inbound-connect minting (AcceptInbound /
        // RefuseInbound); a bare circuit ignores an unexpected request.
    }

    private void OnDisconnectRequest(NetRomTransportHeader t)
    {
        // Acknowledge and close, from any live state.
        SendDisconnectAcknowledge();
        if (state != NetRomCircuitState.Disconnected)
        {
            Close(NetRomCircuitCloseReason.Normal);
        }
    }

    private void OnDisconnectAcknowledge(NetRomTransportHeader t)
    {
        if (state == NetRomCircuitState.Disconnecting)
        {
            controlTimerArmed = false;
            Close(NetRomCircuitCloseReason.Normal);
        }
    }

    private void OnInformation(NetRomTransportHeader t, ReadOnlyMemory<byte> payload)
    {
        if (state != NetRomCircuitState.Connected)
        {
            // Information before connect / after disconnect — drop, but if we're
            // disconnecting still ack-progress isn't needed.
            return;
        }

        // First, absorb the piggybacked ack (their RX = next seq they expect from us).
        AbsorbAck(t.RxSequence);

        // Honour an inbound choke / choke-release flag on the Information message.
        ApplyPeerChoke(t.Choke);

        // In-order delivery: accept only the frame we expect (NET/ROM mod-256). A
        // NAK is the selective-retransmit mechanism for gaps; on a duplicate or
        // future frame we simply re-ack our current expected sequence.
        if (t.TxSequence == vr)
        {
            // Accept.
            vr = (byte)((vr + 1) & 0xFF);

            if (!payload.IsEmpty)
            {
                reassembly.AddRange(payload.ToArray());
            }

            if (!t.MoreFollows && reassembly.Count > 0)
            {
                // Logical frame complete — deliver upward.
                var whole = reassembly.ToArray();
                reassembly.Clear();

                // Backpressure accounting only matters when a choke threshold is
                // configured; otherwise the consumer drains synchronously (the node
                // bridge does) and we never accumulate a backlog. A host that can
                // stall its reader sets ChokeThreshold and calls OnDeliveryDrained.
                if (options.ChokeThreshold > 0)
                {
                    pendingDeliveries++;
                }
                DataReceived?.Invoke(whole);
                MaybeAssertChoke();
            }

            SendInformationAcknowledge(nak: false);
        }
        else
        {
            // Out-of-sequence. If it's a future frame (a gap), NAK the one we want;
            // a stale duplicate just gets a plain ack so the sender advances.
            bool future = Mod256After(t.TxSequence, vr);
            SendInformationAcknowledge(nak: future);
        }
    }

    private void OnInformationAcknowledge(NetRomTransportHeader t)
    {
        if (state != NetRomCircuitState.Connected)
        {
            return;
        }

        ApplyPeerChoke(t.Choke);

        if (t.Nak)
        {
            // Selective retransmit: the peer wants the frame named by its RX seq
            // (the next it expects). Retransmit that frame (and following in-flight)
            // immediately, then absorb the implied ack of everything before it.
            AbsorbAck(t.RxSequence);
            RetransmitFrom(t.RxSequence);
        }
        else
        {
            AbsorbAck(t.RxSequence);
        }

        PumpSendQueue();
    }

    // ─── Send helpers (caller holds the lock) ───────────────────────────

    private void SendConnectRequest()
    {
        // Connect Request: index/id are OUR own (so the peer learns how to address
        // us). The PROPOSED WINDOW and the originating user/node callsigns travel in
        // the INFO field, NOT the transport header — see ConnectRequestInfo for the
        // wire layout (this is the de-facto NET/ROM form LinBPQ originates + accepts,
        // verified on the wire; #308 interop follow-up).
        var t = new NetRomTransportHeader
        {
            CircuitIndex = localIndex,
            CircuitId = localId,
            TxSequence = 0,
            RxSequence = 0,
            Opcode = NetRomOpcode.ConnectRequest,
            Flags = NetRomTransportFlags.None,
        };

        var user = connectUser.Base.Length == 0 ? localNode : connectUser;
        Emit(t, ConnectRequestInfo.Build((byte)Math.Clamp(options.WindowSize, 1, 127), user, localNode));
    }

    private void SendConnectAcknowledge(bool refused)
    {
        // Connect Acknowledge: addressed to the peer (its index/id), and carries OUR
        // index/id in the TX/RX slots so the peer can address us. TX also doubles as
        // the accepted window. Bit-7 set = refused.
        var t = new NetRomTransportHeader
        {
            CircuitIndex = remoteIndex,
            CircuitId = remoteId,
            TxSequence = localIndex,
            RxSequence = localId,
            Opcode = NetRomOpcode.ConnectAcknowledge,
            Flags = refused ? NetRomTransportFlags.Choke : NetRomTransportFlags.None,
        };
        Emit(t, ReadOnlyMemory<byte>.Empty);
    }

    private void SendDisconnectRequest()
    {
        var t = new NetRomTransportHeader
        {
            CircuitIndex = remoteIndex,
            CircuitId = remoteId,
            TxSequence = 0,
            RxSequence = 0,
            Opcode = NetRomOpcode.DisconnectRequest,
            Flags = NetRomTransportFlags.None,
        };
        Emit(t, ReadOnlyMemory<byte>.Empty);
    }

    private void SendDisconnectAcknowledge()
    {
        var t = new NetRomTransportHeader
        {
            CircuitIndex = remoteIndex,
            CircuitId = remoteId,
            TxSequence = 0,
            RxSequence = 0,
            Opcode = NetRomOpcode.DisconnectAcknowledge,
            Flags = NetRomTransportFlags.None,
        };
        Emit(t, ReadOnlyMemory<byte>.Empty);
    }

    private void SendInformation(byte seq, ReadOnlyMemory<byte> payload, bool moreFollows)
    {
        var flags = NetRomTransportFlags.None;
        if (moreFollows)
        {
            flags |= NetRomTransportFlags.MoreFollows;
        }
        if (localChoked)
        {
            flags |= NetRomTransportFlags.Choke;
        }
        var t = new NetRomTransportHeader
        {
            CircuitIndex = remoteIndex,
            CircuitId = remoteId,
            TxSequence = seq,
            RxSequence = vr,        // piggyback our receive expectation
            Opcode = NetRomOpcode.Information,
            Flags = flags,
        };
        Emit(t, payload);
    }

    private void SendInformationAcknowledge(bool nak)
    {
        var flags = NetRomTransportFlags.None;
        if (nak)
        {
            flags |= NetRomTransportFlags.Nak;
        }
        if (localChoked)
        {
            flags |= NetRomTransportFlags.Choke;
        }
        var t = new NetRomTransportHeader
        {
            CircuitIndex = remoteIndex,
            CircuitId = remoteId,
            TxSequence = 0,
            RxSequence = vr,
            Opcode = NetRomOpcode.InformationAcknowledge,
            Flags = flags,
        };
        Emit(t, ReadOnlyMemory<byte>.Empty);
    }

    private void Emit(NetRomTransportHeader transport, ReadOnlyMemory<byte> payload)
    {
        var network = new NetRomNetworkHeader
        {
            Origin = localNode,
            Destination = remoteNode,
            TimeToLive = options.TimeToLive,
        };
        var packet = new NetRomPacket { Network = network, Transport = transport, Payload = payload };
        // Queue, don't send: the caller holds the gate. FlushSends ships these to the
        // sink once the gate is released (deadlock fix — see pendingSends).
        pendingSends.Add(packet);
    }

    /// <summary>
    /// Ship packets queued by <see cref="Emit"/> during a locked section to the
    /// <see cref="SendPacket"/> sink, <b>outside</b> the gate. The sink reaches into the
    /// AX.25 session (which locks); holding the circuit gate across that call deadlocks
    /// against the inbound pump (session lock → gate). Every public entry point that may
    /// emit calls this immediately after its <c>lock (gate)</c> block.
    /// </summary>
    private void FlushSends()
    {
        NetRomPacket[] toSend;
        lock (gate)
        {
            if (pendingSends.Count == 0)
            {
                return;
            }
            toSend = pendingSends.ToArray();
            pendingSends.Clear();
        }
        var sink = SendPacket;
        if (sink is null)
        {
            return;
        }
        foreach (var p in toSend)
        {
            sink(p);
        }
    }

    // ─── Window + ack mechanics (caller holds the lock) ─────────────────

    private void PumpSendQueue()
    {
        if (state != NetRomCircuitState.Connected || peerChoked)
        {
            return;
        }

        var now = time.GetUtcNow();
        while (sendQueue.Count > 0 && InFlight() < window)
        {
            var fragment = sendQueue.Dequeue();
            byte seq = vs;
            vs = (byte)((vs + 1) & 0xFF);
            unacked.Add(new Unacked(seq, fragment.Bytes, fragment.MoreFollows, now, 0));
            SendInformation(seq, fragment.Bytes, fragment.MoreFollows);
        }
    }

    private int InFlight() => unacked.Count;

    // Absorb a cumulative ack: the peer expects `expected` next, so every in-flight
    // sequence strictly before `expected` (mod 256, within the window) is acked.
    private void AbsorbAck(byte expected)
    {
        if (unacked.Count == 0)
        {
            va = expected;
            return;
        }

        // Remove every unacked frame whose sequence is "before" expected.
        unacked.RemoveAll(u => SeqAcked(u.Sequence, expected));
        va = expected;

        // Window opened up — try to send more.
        PumpSendQueue();
    }

    // True if sequence `seq` is acknowledged by a peer that now expects `expected`:
    // i.e. seq is in [va, expected) walking forward mod 256, bounded by the window.
    private static bool SeqAcked(byte seq, byte expected)
    {
        // Distance from seq to expected, walking forward mod 256.
        int dist = (expected - seq) & 0xFF;
        // Acked iff expected is strictly after seq within a window-sized horizon.
        return dist >= 1 && dist <= 128;
    }

    private void RetransmitFrom(byte seq)
    {
        // Selective-NAK: resend the named frame and every in-flight frame after it
        // (the peer dropped a frame and the ones it kept after a gap can't be acked
        // until the gap fills).
        var now = time.GetUtcNow();
        for (int i = 0; i < unacked.Count; i++)
        {
            var u = unacked[i];
            if (u.Sequence == seq || Mod256After(u.Sequence, seq))
            {
                SendInformation(u.Sequence, u.Payload, u.MoreFollows);
                unacked[i] = u with { SentAt = now, Retries = u.Retries + 1 };
            }
        }
    }

    // True if `a` is strictly after `b` within a half-window horizon (mod 256).
    private static bool Mod256After(byte a, byte b)
    {
        int dist = (a - b) & 0xFF;
        return dist >= 1 && dist <= 128;
    }

    // ─── Choke (caller holds the lock) ──────────────────────────────────

    private void ApplyPeerChoke(bool choke)
    {
        if (choke && !peerChoked)
        {
            peerChoked = true;
        }
        else if (!choke && peerChoked)
        {
            peerChoked = false;
            PumpSendQueue();   // peer released choke — resume sending
        }
    }

    private void MaybeAssertChoke()
    {
        if (options.ChokeThreshold > 0 && pendingDeliveries >= options.ChokeThreshold && !localChoked)
        {
            localChoked = true;
        }
    }

    private void MaybeReleaseChoke()
    {
        if (localChoked && pendingDeliveries < options.ChokeThreshold)
        {
            localChoked = false;
            // Tell the peer it may resume: a plain InfoAck with choke clear.
            if (state == NetRomCircuitState.Connected)
            {
                SendInformationAcknowledge(nak: false);
            }
        }
    }

    // ─── Lifecycle (caller holds the lock) ──────────────────────────────

    private void ArmControlTimer()
    {
        controlDeadline = time.GetUtcNow() + options.RetransmitTimeout;
        controlTimerArmed = true;
    }

    private void Close(NetRomCircuitCloseReason reason)
    {
        if (state == NetRomCircuitState.Disconnected)
        {
            return;
        }
        state = NetRomCircuitState.Disconnected;
        controlTimerArmed = false;
        unacked.Clear();
        sendQueue.Clear();
        reassembly.Clear();
        Closed?.Invoke(reason);
    }

    private readonly record struct Unacked(byte Sequence, byte[] Payload, bool MoreFollows, DateTimeOffset SentAt, int Retries);

    private readonly record struct Fragment(byte[] Bytes, bool MoreFollows);
}
