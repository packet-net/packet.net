# Packet.Core

> Shared primitive types for the Packet.NET amateur-radio stack.

Packet.Core holds the small, dependency-free building blocks the rest of the stack is built on: callsigns, AX.25 address slots, the FCS CRC, and the strict-vs-pragmatic parse options. It's the foundation package that Packet.Ax25, Packet.Kiss, Packet.Aprs and downstream applications depend on. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Core
```

## Quick start
```csharp
using Packet.Core;

// Parse a human-typed callsign ("BASE" or "BASE-SSID"), strictly.
var call = Callsign.Parse("Q0PDN-7");
Console.WriteLine(call.Base);      // "Q0PDN"
Console.WriteLine(call.Ssid);      // 7
Console.WriteLine(call.ToString()); // "Q0PDN-7"

// Encode one 7-octet AX.25 address slot, then read it back.
var addr = new Ax25Address(call, CrhBit: true, ExtensionBit: false);
Span<byte> slot = stackalloc byte[Ax25Address.EncodedLength]; // 7
addr.Write(slot);

var decoded = Ax25Address.Read(slot);       // lenient by default
Console.WriteLine(decoded == addr);          // True (round-trips)

// Strict spec parsing: reject pragmatic accommodations (e.g. all-space slots).
var strict = Ax25Address.Read(slot, Ax25ParseOptions.Strict);

// AX.25 frame check sequence (CRC-16/X-25).
ushort fcs = Crc16Ccitt.Compute("123456789"u8); // 0x906E
```

`Callsign.Parse` / `TryParse` stay strict (≥1 char, A–Z / 0–9, SSID 0–15) because they're for user-typed input. The wire-parse path (`Ax25Address.Read`) is lenient by default — it accepts real-world quirks like all-space address slots — and you opt into strictness via `Ax25ParseOptions.Strict`.

## Key types
- `Callsign` — a base callsign + SSID (0–15) value type, with strict `Parse`/`TryParse` over text.
- `Ax25Address` — one 7-octet AX.25 header address slot; `Read`/`Write` between the value and its wire form, with the C/H and extension bits.
- `Ax25ParseOptions` — named, individually-toggleable pragmatic-parse flags with `Strict` / `Lenient` and peer presets (`Bpq`, `Xrouter`, `Direwolf`).
- `Crc16Ccitt` — the AX.25 frame check sequence (CRC-16/X-25, polynomial 0x1021).

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 framing and session layer built on these primitives.
- [Packet.Kiss](https://www.nuget.org/packages/Packet.Kiss) — KISS TNC framing.

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
