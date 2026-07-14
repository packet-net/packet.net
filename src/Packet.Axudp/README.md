# Packet.Axudp

> AX.25-over-IP: UDP-encapsulated AX.25 frames, RFC 1226 style.

`AxudpSocket` is a bidirectional AXUDP endpoint — UDP encapsulation of AX.25 frames per the RFC 1226 "AX.25 over IP" convention. The UDP payload is the bare AX.25 frame body followed by the 2-octet AX.25 FCS, exactly as every real AXIP/AXUDP peer (LinBPQ's BPQAXIP, XRouter, ax25ipd, JNOS) speaks it. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Axudp
```

## Quick start
```csharp
using System.Net;
using Packet.Ax25;
using Packet.Axudp;

using var receiver = new AxudpSocket(localPort: 0);   // 0 = any free ephemeral port
using var sender = new AxudpSocket(localPort: 0);

var frame = Ax25Frame.Ui(
    destination: new Callsign("Q0PDN", 0),
    source: new Callsign("N0CALL", 7),
    info: "hello"u8);

// SendAsync always appends the 2-octet AX.25 FCS (CRC-16-CCITT, low byte first).
await sender.SendAsync(new IPEndPoint(IPAddress.Loopback, receiver.LocalPort), frame);

// ReceiveAsync strips + validates the FCS, dropping any bad-CRC datagram, then
// returns the sender endpoint, the bare frame body, and a best-effort decode.
AxudpReceiveResult result = await receiver.ReceiveAsync();
Console.WriteLine(result.DecodedFrame?.Source.Callsign);   // N0CALL-7
```

The FCS is part of the AXUDP wire format and is unconditional: there is no FCS-less form. The decode on receive is best-effort at modulo-8 (this transport layer holds no session context); a modulo-128 / extended link must re-parse `RawFrame` at its negotiated modulo before trusting N(S)/N(R)/PID/info.

## Key types
- `AxudpSocket` — the bidirectional AXUDP endpoint: `SendAsync` (appends FCS), `ReceiveAsync` (strips + validates FCS), `SendRawAsync` (verbatim escape hatch for replaying captures), `LocalPort`, `Dispose`.
- `AxudpReceiveResult` — one received datagram after FCS strip/validation: `From` (sender `IPEndPoint`), `RawFrame` (bare AX.25 body), `DecodedFrame` (parsed `Ax25Frame`, or `null` if the body didn't decode).

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 frame model (`Ax25Frame`, `Callsign`) that AXUDP carries
- [Packet.Core](https://www.nuget.org/packages/Packet.Core) — shared primitives, including the `Crc16Ccitt` FCS

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
