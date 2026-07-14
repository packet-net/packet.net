# Packet.Rhp2

> The RHPv2 (Radio Host Protocol v2) JSON-over-TCP wire codec.

`Packet.Rhp2` is the single source of truth for the **RHPv2** (PWP-0222 / PWP-0245) wire format: framing, the message catalogue, JSON serialization, and payload encoding — with no engine, no transport, and no client/server policy. It is the shared codec layer that both RHPv2 servers and RHPv2 clients build on. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install

```sh
dotnet add package Packet.Rhp2
```

## Quick start

Build a typed message, serialize it to JSON, and write it as one length-prefixed frame; on the way back, read a frame and dispatch on its `type`.

```csharp
using Packet.Rhp2;

// --- send: connect a socket handle to a remote station ---
var connect = new ConnectMessage { Id = 1, Handle = 1234, Remote = "Q0PDN-1" };
byte[] json = RhpJson.Serialize(connect);          // type-first UTF-8 JSON
await RhpFraming.WriteFrameAsync(stream, json);     // 2-byte BE length + bytes

// --- send binary payload: data goes in the JSON `data` field as Latin-1, not base64 ---
var send = new SendMessage { Id = 2, Handle = 1234, Data = RhpDataEncoding.ToWireString(payloadBytes) };
await RhpFraming.WriteFrameAsync(stream, RhpJson.Serialize(send));

// --- receive: one frame, then dispatch on the concrete type ---
byte[]? frame = await RhpFraming.ReadFrameAsync(stream);   // null = peer hung up between frames
if (frame is not null)
{
    RhpMessage msg = RhpJson.Deserialize(frame);
    switch (msg)
    {
        case ConnectReplyMessage r when r.ErrCode == RhpErrorCode.Ok:
            // connected
            break;
        case RecvMessage rx:
            byte[] data = RhpDataEncoding.FromWireString(rx.Data);
            break;
        case UnknownMessage u:
            // forward-compatible: a newer XRouter added a type we don't model
            break;
    }
}
```

`ReadFrameAsync` returns `null` at a clean end-of-stream (the normal way an RHP conversation ends) and throws `EndOfStreamException` only when a peer hangs up mid-frame. An overload takes an in-frame timeout to drop a slowloris peer without bounding idle-between-frames waits.

## Key types

- `RhpFraming` — read/write one length-prefixed frame (2-byte big-endian length + UTF-8 JSON); zero-length frames are legal.
- `RhpJson` — serialize/deserialize `RhpMessage` DTOs; type-first key emission, null omission, case-insensitive reads.
- `RhpMessage` — abstract base for every message; subclasses (`AuthMessage`, `OpenMessage`, `ConnectMessage`, `SendMessage`, `RecvMessage`, … and their replies) map one-to-one to the `type` discriminators.
- `UnknownMessage` — carries the raw JSON for an unrecognised `type` instead of throwing (forward-compatible).
- `RhpDataEncoding` — bytes ↔ the JSON `data` field via Latin-1 (one byte per code unit). **Not base64.**
- `RhpConstants` — `ProtocolFamily`, `SocketMode`, `OpenFlags`, `StatusFlags`, `RhpErrorCode` (with canonical `Text()`), `RhpMessageType`.
- `RhpProtocolException` — thrown for a codec-level violation (non-object JSON, missing `type`), but never for a merely unknown `type`.

## Wire fidelity

The shapes here are pinned against **live XRouter**, not just the published spec: capital `errCode` / `errText` on every reply (read case-insensitively so the spec's lowercase form still parses), the `connectReply` PascalCase-typo tolerance on read, `port` string-or-number normalisation, `errCode 17` "Not connected", and the Latin-1 (not base64) `data` encoding.

## See also

- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.NetRom](https://www.nuget.org/packages/Packet.NetRom) — NET/ROM layer 3/4
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 link layer underneath

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
