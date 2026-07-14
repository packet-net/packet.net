# Packet.Agw

> AGW (AGWPE / SV2AGW) client for talking to LinBPQ, direwolf, SoundModem, and XRouter.

`Packet.Agw` dials an AGW server over TCP, registers your callsign, and opens AX.25 connected-mode sessions — each exposed as a plain .NET `Stream`. AGW (SV2AGW's PE+ protocol) is the canonical interop interface for software AX.25 stacks, so this is the easy way to drive a packet TNC from C# without owning the radio layer yourself. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Agw
```

## Quick start
Connect to an AGW server, register a callsign, open a connected-mode session, and read/write it like any other stream:

```csharp
using System.Text;
using Packet.Agw;

// Dial the AGW listener (LinBPQ / direwolf / SoundModem default to port 8000).
await using var client = await AgwClient.ConnectAsync("127.0.0.1", port: 8000);

// Register the callsign the server should route inbound frames to.
await client.RegisterCallsignAsync("N0CALL");

// Open an AX.25 connected-mode session (sends SABM, awaits the connect-ack).
await using AgwSession session = await client.OpenSessionAsync(from: "N0CALL", to: "Q0PDN");

// AgwSession is a Stream: write bytes to send I-frames, read bytes from the remote.
await session.WriteAsync(Encoding.ASCII.GetBytes("ports\r"));

var buffer = new byte[256];
int n = await session.ReadAsync(buffer);          // 0 == remote disconnected (EOF)
Console.WriteLine(Encoding.ASCII.GetString(buffer, 0, n));

await session.DisconnectAsync();
```

Writes larger than the per-frame cap (~256 bytes) are split into multiple data frames automatically. The default PID is `0xF0` ("no layer 3"); set `session.DefaultPid` to override (e.g. `0xCF` for NET/ROM L3). `client.GetPortInfoAsync()` returns the server's configured radio ports.

## Key types
- `AgwClient` — connect-initiator over one TCP connection; `ConnectAsync` / `FromStream`, `RegisterCallsignAsync`, `OpenSessionAsync`, `GetPortInfoAsync`. Pumps a keepalive ping so idle servers (BPQ closes at ~20s) don't drop you.
- `AgwSession` — one connected-mode session as a `Stream`; `Read`/`Write`, `DefaultPid`, `DisconnectAsync`, `DisconnectedTask`.
- `AgwFrame` — one frame on the wire (36-byte header + body); `ToBytes`, `Parse`, `TryReadDataLength`.
- `AgwFrameStream` — low-level frame I/O over any duplex byte `Stream`; use it directly for raw frame access.
- `AgwCommandKind` — the AGW command-kind ASCII letters (`C` connect, `D` data, `d` disconnect, `X` register, `G` port-info, ...).

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 frame + session layer
- [Packet.Kiss](https://www.nuget.org/packages/Packet.Kiss) — the KISS-TNC alternative when you own the radio layer

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
