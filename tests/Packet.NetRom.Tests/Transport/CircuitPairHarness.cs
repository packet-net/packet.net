using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using Packet.Core;
using Packet.NetRom.Transport;
using Packet.NetRom.Wire;

namespace Packet.NetRom.Tests.Transport;

/// <summary>
/// A deterministic two-node NET/ROM L4 harness: two <see cref="CircuitManager"/>s
/// ("A" and "B") wired back-to-back over an in-process, controllable datagram
/// channel and a shared <see cref="FakeTimeProvider"/>. The L4 analogue of the
/// AX.25 conformance <c>TwoStationHarness</c> — a run is a pure function of the
/// scenario + the channel policy, fully deterministic, no wall-clock.
/// </summary>
/// <remarks>
/// Datagrams A emits are delivered to B and vice-versa (the channel stands in for
/// the AX.25 interlink that would carry PID-0xCF I-frames). The channel can
/// <see cref="DropNext"/> a datagram or <see cref="DuplicateNext"/> it to exercise
/// loss / duplication recovery. Delivery is synchronous and queued, drained by
/// <see cref="Pump"/>, so the whole exchange is single-threaded and reproducible.
/// </remarks>
public sealed class CircuitPairHarness
{
    public Callsign ANode { get; } = new("GB7AAA", 0);
    public Callsign BNode { get; } = new("GB7BBB", 0);

    public CircuitManager A { get; }
    public CircuitManager B { get; }
    public FakeTimeProvider Time { get; }

    private readonly Queue<(CircuitManager To, NetRomPacket Packet)> wire = new();

    /// <summary>Per-direction "drop the next datagram" predicate counts.</summary>
    private int dropAToB;
    private int dropBToA;
    private int dupAToB;
    private int dupBToA;

    public CircuitPairHarness(NetRomCircuitOptions? options = null, NetRomCircuitOptions? optionsB = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 6, 4, 12, 0, 0, TimeSpan.Zero));
        var optsA = options ?? NetRomCircuitOptions.Default;
        var optsB = optionsB ?? optsA;
        A = new CircuitManager(ANode, optsA, Time);
        B = new CircuitManager(BNode, optsB, Time);

        // Each manager's outbound datagram is queued for delivery to the OTHER
        // manager, honouring the channel's drop/duplicate policy.
        A.SendPacket = p => Enqueue(from: A, p);
        B.SendPacket = p => Enqueue(from: B, p);
    }

    private void Enqueue(CircuitManager from, NetRomPacket p)
    {
        bool aToB = ReferenceEquals(from, A);
        var to = aToB ? B : A;

        if (aToB ? TryConsume(ref dropAToB) : TryConsume(ref dropBToA))
        {
            return;   // dropped on the channel
        }

        wire.Enqueue((to, p));

        if (aToB ? TryConsume(ref dupAToB) : TryConsume(ref dupBToA))
        {
            wire.Enqueue((to, p));   // medium duplicated it
        }
    }

    private static bool TryConsume(ref int counter)
    {
        if (counter > 0) { counter--; return true; }
        return false;
    }

    /// <summary>Drop the next <paramref name="count"/> datagrams A→B.</summary>
    public void DropNextAToB(int count = 1) => dropAToB += count;

    /// <summary>Drop the next <paramref name="count"/> datagrams B→A.</summary>
    public void DropNextBToA(int count = 1) => dropBToA += count;

    /// <summary>Duplicate the next datagram A→B.</summary>
    public void DuplicateNextAToB() => dupAToB += 1;

    /// <summary>Deliver every queued datagram (and any they cascade) until the wire
    /// is empty.</summary>
    public void Pump()
    {
        for (int guard = 0; guard < 100_000 && wire.Count > 0; guard++)
        {
            var (to, packet) = wire.Dequeue();
            to.OnPacket(packet);
        }
        if (wire.Count > 0)
        {
            throw new InvalidOperationException("circuit exchange did not settle — possible send/ack livelock");
        }
    }

    /// <summary>Advance virtual time by <paramref name="delta"/>, fire both
    /// managers' retransmit ticks, then drain the wire.</summary>
    public void Advance(TimeSpan delta)
    {
        Time.Advance(delta);
        A.Tick();
        B.Tick();
        Pump();
    }

    /// <summary>A captured circuit end with its delivered data + lifecycle, for assertions.</summary>
    public sealed class Captured
    {
        public NetRomCircuit Circuit { get; }
        public List<byte[]> Received { get; } = new();
        public List<NetRomCircuitCloseReason> Closed { get; } = new();
        public bool Connected { get; private set; }

        public Captured(NetRomCircuit circuit)
        {
            Circuit = circuit;
            circuit.DataReceived += d => Received.Add(d.ToArray());
            circuit.Connected += () => Connected = true;
            circuit.Closed += r => Closed.Add(r);
        }

        /// <summary>All received logical frames concatenated.</summary>
        public byte[] ReceivedBytes => Received.SelectMany(x => x).ToArray();
    }

    /// <summary>Open a circuit from A to B (mint + capture). The caller then drives
    /// Connect / Send / Disconnect and Pumps.</summary>
    public Captured OpenFromA() => new(A.OpenCircuit(BNode));

    /// <summary>Capture every inbound circuit B accepts, auto-accepting it. Returns a
    /// list that fills as A connects in.</summary>
    public List<Captured> AutoAcceptOnB()
    {
        var accepted = new List<Captured>();
        B.IncomingCircuit += (_, e) =>
        {
            var cap = new Captured(e.Circuit);
            accepted.Add(cap);
            CircuitManager.AcceptIncoming(e);
        };
        return accepted;
    }
}
