# Packet.NetRom

> NET/ROM L3 routing, L4 circuits, and the INP3 time-routing overlay for the Packet.NET stack.

Packet.NetRom is the NET/ROM networking layer: the **L3 routing table** (NODES broadcasts + the multiplicative per-hop quality model), the **L4 virtual-circuit** state machine (`CircuitManager` / `NetRomCircuit`), the L3 **forwarding decision** for transit nodes, and the **INP3** time-routing overlay (L3RTT link timing, RIF/RIP). It runs above an AX.25 interlink — the wire types are codecs over `NetRomPacket`, and the routing/transport types are host-free (no AX.25 or node-host dependency), so you wire them to a transport yourself. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack, built on `Packet.Core` and `Packet.Ax25`.

## Install
```sh
dotnet add package Packet.NetRom
```

## Quick start
Parse a NODES broadcast you heard from a neighbour and learn its routes into a routing table — the read-only ingest path a node uses to build its view of the network:

```csharp
using Packet.Core;
using Packet.NetRom.Routing;
using Packet.NetRom.Wire;

var me   = new Callsign("Q0PDN");      // our own node callsign
var nbr  = new Callsign("N0CALL");     // the neighbour whose broadcast we heard

// Build a node's own NODES broadcast, then parse it (here we round-trip the builder;
// in production you'd parse the info field of a received PID-0xCF UI frame).
byte[][] frames = NodesBroadcastBuilder.Build(
    senderAlias: "NODE",
    entries: [ new(new Callsign("AA1AA"), "BBS", new Callsign("AA1AA"), Quality: 200) ]);

if (NodesBroadcast.TryParse(frames[0], out var broadcast))
{
    var table = new NetRomRoutingTable();
    table.Ingest(originator: nbr, myCall: me, portId: "vhf", broadcast: broadcast);

    NetRomRoutingSnapshot view = table.Snapshot();
    foreach (var dest in view.Destinations)
        Console.WriteLine($"{dest.Alias} ({dest.Destination}) via {dest.BestRoute?.Neighbour} q={dest.BestRoute?.Quality}");
}
```

Call `table.Sweep()` at the broadcast interval to age routes out, `BuildAdvertisement(obsoleteMinimum)` to originate your own NODES, and `MarkNeighbourDown(neighbour)` for immediate link-down failover. For L4 circuits, mint one with `CircuitManager.OpenCircuit(remoteNode)`, wire its `SendPacket` sink to your interlink, then `Connect` / `Send` / `Disconnect`.

## Key types
- `NetRomRoutingTable` — the learned L3 routing table: ingests NODES broadcasts, derives per-hop qualities, keeps best routes per destination with obsolescence decay, hands out immutable snapshots.
- `NetRomRoutingSnapshot` / `NetRomDestination` / `NetRomRoute` — the immutable read-only routing view (with `ResolveDestination` for `connect <alias>`).
- `CircuitManager` — owns the L4 circuit table: mints circuits, demultiplexes inbound datagrams, accepts/refuses inbound connects, drives retransmit timers.
- `NetRomCircuit` — one end of an L4 virtual circuit: sliding-window transport with negotiated window, choke flow control, NAK retransmit, and 236-byte fragment/reassembly.
- `NetRomForwarding` — the pure L3 forwarding decision for a transit node (TTL decrement, loop guard, best/per-flow next-hop selection).
- `Inp3Engine` — the host-free INP3 link-timing engine: L3RTT probing, SNTT smoothing, neighbour-down detection.
- `NetRomPacket` / `NodesBroadcast` / `Inp3Rif` — the wire codecs (total parsing — malformed bytes return `false`, never throw).

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 interlink NET/ROM datagrams ride over (PID 0xCF)
- [Packet.Core](https://www.nuget.org/packages/Packet.Core) — `Callsign` and the shared primitives

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
