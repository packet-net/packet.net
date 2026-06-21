# 7. NET/ROM: routing and circuits

AX.25 connects two stations that can hear each other. **NET/ROM** is the layer
that lets stations that *can't* hear each other talk anyway, by hopping through
intermediate nodes — it's the network (L3) and transport (L4) layer that turns a
scattering of nodes into a routed mesh. `Packet.NetRom` gives you the pieces:
the wire formats, a routing table fed by NODES broadcasts, a forwarding decision
function, and an end-to-end virtual-circuit layer.

This is the top of the cake. It rides entirely on what you already have —
NET/ROM packets travel as the info field of AX.25 frames (UI frames for routing
broadcasts, connected-mode I-frames for circuit data, PID `0xCF` throughout).

!!! note "Scope and altitude"
    NET/ROM is a substantial protocol and `Packet.NetRom` is a substantial
    library. This chapter shows the shape of the public API and how the pieces fit
    onto your `Ax25Listener` — enough to orient you and write the wiring. For
    exhaustive behaviour (INP3 measured-time routing, obsolescence, poison
    reverse) see the library's source and the `docs/netrom-*` design notes in this
    repo.

The four moving parts:

| Part | Type(s) | Role |
|------|---------|------|
| Wire | `NetRomPacket`, `NodesBroadcast` (`Packet.NetRom.Wire`) | encode/decode L3 datagrams and NODES routing broadcasts |
| Routing | `NetRomRoutingTable`, `NetRomRoutingSnapshot` (`Packet.NetRom.Routing`) | learn the network from NODES broadcasts; answer "who's the next hop?" |
| Forwarding | `NetRomForwarding` (`Packet.NetRom`) | decide whether/where to forward a transit packet |
| Transport | `CircuitManager`, `NetRomCircuit` (`Packet.NetRom.Transport`) | end-to-end reliable circuits across the mesh |

## Learning the network: NODES broadcasts → routing table

Nodes periodically broadcast a **NODES** frame — a UI frame, PID `0xCF`, AX.25
destination the literal callsign `NODES` — advertising the destinations they can
reach and at what quality. Your routing table is built by feeding it every NODES
broadcast you hear.

You're already hearing UI frames via `FrameTraced` ([chapter 4](04-listen.md)).
Filter for NODES, parse, and ingest:

```csharp
using Packet.Core;
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.NetRom.Wire;
using Packet.NetRom.Routing;

var routing = new NetRomRoutingTable();
var myCall  = Callsign.Parse("GB7XYZ-1");

listener.FrameTraced += (_, e) =>
{
    var f = e.Frame;
    if (e.Direction != FrameDirection.Received) return;
    if (!f.IsUi || f.Pid != Ax25Frame.PidNetRom) return;
    if (f.Destination.Callsign.Base != "NODES") return;

    if (NodesBroadcast.TryParse(f.Info.Span, out var broadcast))
        routing.Ingest(
            originator: f.Source.Callsign,   // the neighbour who sent it
            myCall: myCall,
            portId: "vhf",                    // your label for the port it arrived on
            broadcast: broadcast);
};
```

`NodesBroadcast` exposes `SenderAlias` and `Entries` (each a destination +
neighbour + quality). `NetRomRoutingTable.Ingest` folds the advertisement into
the table, combining the advertised quality with your link quality to the
neighbour and applying NET/ROM's obsolescence rules.

To use the table, take a `Snapshot()` — an immutable, point-in-time view safe to
read from any thread — and resolve a destination by alias or callsign:

```csharp
NetRomRoutingSnapshot snap = routing.Snapshot();
NetRomDestination? dest = snap.ResolveDestination("GB7ABC");
if (dest?.BestRoute is { } route)
    Console.WriteLine($"Best next hop for GB7ABC: {route.Neighbour} (quality {route.Quality})");
```

A node that *advertises* (rather than only listening) periodically builds its own
NODES broadcast from the table and sends it as a UI frame with `SendUiAsync` to
the `NODES` destination. The table can produce the advertisement entries for you;
sweep obsolescence on the same timer.

## Forwarding transit traffic

When a NET/ROM packet arrives that isn't for you, you decide its fate.
`NetRomForwarding.Decide` is a pure function over the packet, who you got it from,
your callsign, and the routing snapshot:

```csharp
using Packet.NetRom;

var decision = NetRomForwarding.Decide(
    packet: incoming,
    receivedFrom: neighbour,
    nodeCall: myCall,
    routing: routing.Snapshot(),
    maxTimeToLive: 25);

if (decision.ShouldForward)
{
    // ship decision.Packet (TTL already decremented) to decision.NextHop
}
else
{
    // decision.Outcome is one of NetRomForwarding.ForwardOutcome.Drop* — drop, optionally log
}
```

It handles TTL decrement, loop detection, and next-hop selection (deterministic
best-route or per-flow load balancing). Being pure, it's trivial to unit-test —
no I/O, no mocks.

## End-to-end circuits

The transport layer (L4) gives applications a **reliable, ordered byte pipe**
across the mesh — NET/ROM's answer to a TCP connection. `CircuitManager`
multiplexes circuits; `NetRomCircuit` is one circuit's state machine.

The manager talks to the rest of your stack through two seams: a `SendPacket`
sink you wire to actually transmit a `NetRomPacket`, and an `IncomingCircuit`
event for circuits opened *to* you.

```csharp
using Packet.NetRom.Transport;

var circuits = new CircuitManager(localNode: myCall);

// Outbound: ship circuit packets over the right AX.25 link to the next hop.
circuits.SendPacket = packet => TransmitToNextHop(packet, routing.Snapshot());

// Inbound: a remote opened a circuit to us — accept and serve it.
circuits.IncomingCircuit += (_, e) =>
{
    NetRomCircuit c = e.Circuit;
    c.DataReceived += data => Serve(c, data);   // application bytes, reassembled
    c.Closed       += reason => Cleanup(c, reason);
    CircuitManager.AcceptIncoming(e);
};
```

Feed inbound L3 datagrams (parsed from the I-frames of your AX.25 sessions to the
node) into `circuits.OnPacket(packet)`, and call `circuits.Tick()` on a timer to
drive retransmission and timeouts.

Opening a circuit outbound and sending on it:

```csharp
NetRomCircuit c = circuits.OpenCircuit(remoteNode: Callsign.Parse("GB7ABC-1"));
c.Connected     += () => c.Send(System.Text.Encoding.ASCII.GetBytes("hello over NET/ROM\r"));
c.DataReceived  += data => Console.Write(System.Text.Encoding.ASCII.GetString(data.Span));
c.Connect(originatingUser: myUser);
```

`NetRomCircuit` handles the sliding window, fragmentation into ≤236-byte pieces,
sequencing, and reassembly — the same division of labour as `Ax25Session` one
layer down: you `Send` bytes and handle `DataReceived`, it owns the protocol.

## How it all stacks up

Putting the whole guide together, a NET/ROM-capable node is:

```
IAx25Transport (ch.2)
  └─ Ax25Listener (ch.5/6)            one per RF port
       ├─ FrameTraced ──► NODES filter ──► NetRomRoutingTable.Ingest   (routing)
       ├─ SessionAccepted ──► local console / app                       (users, ch.6)
       └─ connected I-frames (PID 0xCF) ──► CircuitManager.OnPacket     (transit + circuits)
                                                 │
                                                 ├─ NetRomForwarding.Decide ──► next hop
                                                 └─ NetRomCircuit ──► application byte pipe
```

Each layer does its one job and hands off through a narrow seam, exactly as
[chapter 1](01-architecture.md) promised. You can build this incrementally:
listen-only NODES ingestion first (a routing observer), then advertising, then
forwarding, then circuits — each stage useful and testable on its own.

This is precisely the assembly the packet.net node host performs in its
`NetRomService` — which we look at next, alongside the other production concerns
`Packet.Node.Core` will hand you for free.

---

Next: [beyond — segmentation, XID, quirks, and the node host's building blocks →](08-beyond.md)
