# Packet.Ax25.Transport.Abstractions

> The dependency-free seam between the AX.25 data-link layer and whatever moves frames on/off a channel.

This package defines the contract a transport implements to carry **AX.25 frame bodies** (no FCS, no link-framing) — a KISS TNC, an AXUDP socket, a future AGW or native-socket transport. The currency is AX.25 frame bytes, never a wire protocol: KISS is one implementation behind this contract, never a property of it. Take a dependency on this when you only need to move frames, not on a concrete transport. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Ax25.Transport.Abstractions
```

## Quick start
The core surface is two methods: send a frame, and read the stream of inbound frames. Two further capabilities are **optional** — a transport implements them only when its medium can genuinely support them, and consumers feature-detect with `is`.

```csharp
using Packet.Ax25.Transport;

async Task PumpAsync(IAx25Transport transport, ReadOnlyMemory<byte> ax25FrameBody, CancellationToken ct)
{
    // Send one AX.25 frame body (no FCS, no KISS framing — the transport adds what its medium needs).
    await transport.SendAsync(ax25FrameBody, ct);

    // Optional capability: confirm a frame actually left the wire (G8BPQ ACKMODE, de-KISS-named).
    // Absent on AXUDP / plain serial KISS — feature-detect, then fall back to SendAsync.
    if (transport is ITxCompletionTransport txc)
    {
        TxCompletion tx = await txc.SendAwaitingCompletionAsync(ax25FrameBody, cancellationToken: ct);
        Console.WriteLine($"on the air in {tx.Elapsed.TotalMilliseconds:F0} ms");
    }

    // Optional capability: CSMA channel-access knobs (KISS TXDELAY/PERSIST/SLOTTIME/TXTAIL).
    // Only meaningful on a shared half-duplex radio channel.
    if (transport is ICsmaChannelParams csma)
        await csma.SetPersistenceAsync(63, ct);

    // Read inbound frames until disposal or cancellation. The transport pre-filters to genuine
    // AX.25 frames, so you never see the wire protocol. Default impl is an empty stream.
    await foreach (Ax25InboundFrame frame in transport.ReceiveAsync(ct))
    {
        // frame.Ax25 is the FCS-stripped frame body, ready for Ax25Frame.TryParse (Packet.Ax25).
        Console.WriteLine($"rx {frame.Ax25.Length} bytes on port {frame.PortId} at {frame.ReceivedAt:O}");
    }
}
```

Because the package carries `ReadOnlyMemory<byte>` rather than a parsed `Ax25Frame`, it has **zero** `ProjectReferences` and cannot create an AX.25-to-transport dependency cycle: `Packet.Ax25` depends on this, and transports depend on this (plus `Packet.Ax25` only where they parse).

## Key types
- `IAx25Transport` — the core contract: `SendAsync` a frame body, `ReceiveAsync` the inbound stream. The minimal surface a listener needs.
- `Ax25InboundFrame` — one inbound frame: the FCS-stripped `Ax25` body plus `PortId`, `ReceivedAt`, and optional `Radio` metadata.
- `RadioMetadata` — optional per-frame signal data (`RssiDbm`, `SnrDb`); grows additively as radio-control channels surface more.
- `ITxCompletionTransport` — optional capability: `SendAwaitingCompletionAsync` confirms a frame left the wire and times it (for T1 re-arm, pacing, latency).
- `TxCompletion` — the timing of a confirmed transmit (`Queued`, `Completed`, `Elapsed`).
- `ICsmaChannelParams` — optional capability: set the CSMA channel-access knobs (TXDELAY / PERSIST / SLOTTIME / TXTAIL) of a shared half-duplex radio.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 data-link layer that drives this contract
- [Packet.Kiss](https://www.nuget.org/packages/Packet.Kiss) — a KISS implementation behind this seam
- [Packet.Axudp](https://www.nuget.org/packages/Packet.Axudp) — an AXUDP transport that implements only the core interface

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
