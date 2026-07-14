# Packet.Ax25

> AX.25 v2.2 frame codec + connected-mode session runtime + inbound-connection listener.

The core AX.25 protocol library: a U/S/I frame codec, an SDL-driven connected-mode link-layer state machine, and `Ax25Listener` — a node-style coordinator that accepts inbound connections, originates outbound ones, and caches a session per peer. It deals in raw AX.25 frame bytes over an `IAx25Transport`, so it is transport-neutral (KISS, AXUDP, KISS-over-TCP — your choice). Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Ax25
```

## Quick start

Run a station that answers inbound connections, echoes received data back to the peer, and can also originate outbound links. `Ax25Listener` takes any `IAx25Transport` (from `Packet.Kiss`, `Packet.Axudp`, etc.):

```csharp
using Packet.Ax25;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;
using Packet.Core;

// IAx25Transport comes from a transport package (Packet.Kiss, Packet.Axudp, ...).
IAx25Transport transport = /* e.g. a KISS or AXUDP transport */;

await using var listener = new Ax25Listener(transport, new Ax25ListenerOptions
{
    MyCall = new Callsign("N0CALL", 0),
});

// A peer connected to us — wire up its inbound-data stream.
listener.SessionAccepted += (_, e) =>
{
    var session = e.Session;
    session.DataLinkSignalEmitted += (_, signal) =>
    {
        if (signal is DataLinkDataIndication data)
        {
            // Echo whatever the peer sent straight back.
            listener.SendData(session, data.Info);
        }
    };
};

await listener.StartAsync();

// Originate an outbound connection, then send some data on it.
var outbound = await listener.ConnectAsync(new Callsign("Q0PDN", 0));
listener.SendData(outbound, "hello over AX.25"u8.ToArray());
```

Need just the codec? Build and parse frames directly, no session machinery:

```csharp
using Packet.Ax25;
using Packet.Core;

// Build a connectionless UI (unproto) frame.
var ui = Ax25Frame.Ui(
    destination: new Callsign("AA1AA", 0),
    source: new Callsign("N0CALL", 0),
    info: "73"u8,
    pid: Ax25Frame.PidNoLayer3);
byte[] kissBytes = ui.ToBytes();        // body only — a KISS TNC adds the FCS
byte[] axudpBytes = ui.ToBytesWithFcs(); // body + CRC-16-CCITT, for AXUDP / AXIP

// Parse a frame back from its on-the-wire (KISS-form) bytes.
if (Ax25Frame.TryParse(kissBytes, out var frame) && frame.IsUi)
{
    Console.WriteLine($"{frame.Source.Callsign} -> {frame.Destination.Callsign}");
}
```

## Key types
- `Ax25Frame` — one AX.25 frame; codec for U/S/I/UI frames (mod-8 and extended mod-128), `TryParse` / `ToBytes` / `ToBytesWithFcs`, plus static factories (`Ui`, `Sabm`, `Sabme`, `Disc`, `Ua`, `Dm`, `Rr`, `Rej`, `Srej`, `I`, `Xid`, `Test`, …).
- `Ax25Listener` — owns one `IAx25Transport`; accepts inbound SABM, originates `ConnectAsync`, caches a session per peer (LRU), and exposes `SessionAccepted` / `FrameTraced` events. The node-style entry point.
- `Ax25ListenerOptions` — listener config: `MyCall`, timer overrides (`T1V`/`T2`/`T3`/`N2`/`K`), `MaxCachedPeers`, `ParseOptions`, `Quirks`, extended-mode preference.
- `Ax25Session` — one connected-mode link's state machine; exposes `CurrentState`, `Context`, and the `DataLinkSignalEmitted` upward-signal stream.
- `DataLinkSignal` — the upward signal record hierarchy (`DataLinkConnectIndication`, `DataLinkDataIndication`, `DataLinkDisconnectIndication`, `DataLinkErrorIndication`, …).
- `Ax25ParseOptions` — strict-vs-lenient parser knobs (named flags for real-world-peer leniencies); `Ax25SessionQuirks` — named per-session deviations matching buggy reference implementations.
- `Ax25Adapter` — lower-level glue wiring an `Ax25Session` + dispatcher to a byte sink, for callers who don't want the full listener.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25.Transport.Abstractions](https://www.nuget.org/packages/Packet.Ax25.Transport.Abstractions) — the `IAx25Transport` seam the listener consumes
- [Packet.Kiss](https://www.nuget.org/packages/Packet.Kiss) — a KISS-TNC `IAx25Transport`
- [Packet.Axudp](https://www.nuget.org/packages/Packet.Axudp) — an AXUDP `IAx25Transport`
- [Packet.NetRom](https://www.nuget.org/packages/Packet.NetRom) — NET/ROM Layer 3/4 on top of AX.25

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
