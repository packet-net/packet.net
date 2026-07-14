# Packet.Kiss

> KISS framing, ACKMODE, multi-drop ports, and a TCP transport for AX.25.

Encodes and decodes [KISS](https://github.com/packethacking/ax25spec/blob/main/doc/kiss-tnc-protocol.md) frames (the SLIP-style framing that TNCs and software modems speak), handles the G8BPQ ACKMODE extension and multi-drop port nibble, and ships `KissTcpClient` for talking KISS-over-TCP to a TNC or node. Decoded KISS-Data payloads surface as typed `Ax25Frame` events. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Kiss
```

## Quick start

Encode an AX.25 frame to KISS wire bytes, and decode wire bytes back into frames:

```csharp
using Packet.Kiss;

// Encode: FEND | (port<<4)|cmd | escaped-payload | FEND
byte[] wire = KissEncoder.Encode(port: 0, KissCommand.Data, ax25Bytes);

// Decode is stateful — push bytes as they arrive off the wire, pull frames out.
var decoder = new KissDecoder();
foreach (KissFrame frame in decoder.Push(wire))
{
    if (frame.Command == KissCommand.Data)
    {
        // frame.Payload is the AX.25 frame body (the TNC strips/inserts the FCS).
    }
}
```

Classify an inbound frame into a typed event (Data → AX.25, ACKMODE-Data, or Unknown):

```csharp
KissInboundEvent evt = KissFrameClassifier.Classify(frame);
if (evt is Ax25FrameReceivedEvent ax25)
{
    // ax25.Ax25 is a parsed Packet.Ax25.Ax25Frame
}
```

Talk KISS over TCP to a TNC or node (e.g. a LinBPQ KISS-over-TCP listener):

```csharp
await using var client = await KissTcpClient.ConnectAsync("127.0.0.1", 8001);

// Fire-and-forget a KISS-Data frame on port 0.
await client.SendFrameAsync(ax25Bytes);

// Or send in ACKMODE and await the TNC's TX-completion echo (timing included).
TxCompletion done = await client.SendAwaitingCompletionAsync(ax25Bytes);

// Stream inbound frames until the link closes.
await foreach (KissFrame frame in client.ReadFramesAsync())
{
    // ...
}
```

## Key types
- `KissEncoder` / `KissDecoder` — encode AX.25 bytes to KISS wire bytes; statefully decode an incoming byte stream into `KissFrame`s.
- `KissFrame` — one decoded frame: `Port`, `Command`, and raw `Payload`.
- `KissCommand` — KISS command codes (`Data`, `TxDelay`, `Persistence`, `SetHardware`, `AckMode`, …).
- `KissFraming` — the FEND/FESC/TFEND/TFESC framing constants plus the exit-KISS byte.
- `KissTcpClient` — KISS-over-TCP client implementing `IAx25Transport`; handles framing both directions, ACKMODE TX-completion, CSMA params, and half-open-link idle detection.
- `KissFrameClassifier` / `KissInboundEvent` — map a raw frame to a typed inbound event (`Ax25FrameReceivedEvent`, `AckModeDataReceivedEvent`, `UnknownInboundEvent`).
- `KissAckMode` — build/parse the G8BPQ ACKMODE extension (command 0x0C) with its 2-byte sequence tag.
- `KissAx25Bridge` — wire a KISS transport to a `Packet.Ax25` connected-mode session adapter.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [`Packet.Ax25`](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 frames + connected-mode sessions KISS carries.
- [`Packet.Kiss.NinoTnc`](https://www.nuget.org/packages/Packet.Kiss.NinoTnc) — NinoTNC-specific KISS extensions (ACKMODE, SETHW, frame classification).
- [`Packet.Kiss.Serial`](https://www.nuget.org/packages/Packet.Kiss.Serial) — generic serial-port KISS modem.

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
