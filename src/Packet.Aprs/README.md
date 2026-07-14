# Packet.Aprs

> Decode-focused APRS payload codec — position, Mic-E, message, object, item, status, telemetry.

Parses Automatic Packet Reporting System (APRS) information-field payloads per the APRS spec, with named strict-vs-pragmatic parse options for the quirks seen on the live APRS-IS firehose. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack — this package sits above [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) and decodes the bytes carried inside an AX.25 UI frame's info field.

## Install
```sh
dotnet add package Packet.Aprs
```

## Quick start
Each report type has a static `TryDecode` that takes the info-field bytes (with or without the leading data-type identifier) and yields a `readonly record struct`:

```csharp
using Packet.Aprs;

// An uncompressed position report's info field (DTI '!' kept; it's stripped for you).
ReadOnlySpan<byte> info = "!4725.22N/00810.83E_WX-station"u8;

if (AprsPositionDecoder.TryDecode(info, out AprsPosition pos))
{
    Console.WriteLine($"{pos.Latitude:F5}, {pos.Longitude:F5}");  // 47.42033, 8.18050
    Console.WriteLine($"symbol {pos.SymbolTable}{pos.SymbolCode}, {pos.Format}");
    Console.WriteLine(pos.Comment);                                // WX-station
}
```

The decoder is strict on the fixed-position fields (digit ranges, hemisphere indicators, base-91 range) and returns `false` for any structural defect rather than throwing. For the payload types where real-world senders diverge from the spec (status text, telemetry, legacy Mic-E DTIs), pass an `AprsParseOptions` preset:

```csharp
// Reject anything the spec forbids:
AprsTelemetryDecoder.TryDecode(info, AprsParseOptions.Strict, out var telemetry);

// Accept the firehose's quirks (this is also the parameterless default):
AprsStatusDecoder.TryDecode(info, AprsParseOptions.Lenient, out var status);
```

Mic-E is the exception: it splits data across the AX.25 destination address and the info field, so its decoder also needs the 6-character destination base:

```csharp
AprsMicEDecoder.TryDecode("Q0PDN0", info, out AprsMicE micE);
```

## Key types
- `AprsPositionDecoder` — uncompressed (`DDMM.mmN`) and base-91 compressed position reports; `TryDecode` strips DTI + timestamp, `TryDecodePayload` for embedded position payloads.
- `AprsMicEDecoder` / `AprsMicE` — Mic-E reports, decoded from the destination base + info field (`MicEMessageType` carries the standard/custom/emergency bits).
- `AprsMessageDecoder` / `AprsMessage` — text messages (DTI `:`) with addressee and optional message ID.
- `AprsObjectDecoder` / `AprsItemDecoder` — object (DTI `;`) and item (DTI `)`) reports, with an embedded position.
- `AprsStatusDecoder` / `AprsTelemetryDecoder` — status text (DTI `>`) and telemetry (DTI `T`) reports.
- `AprsParseOptions` — strict-vs-pragmatic parse knobs with `Strict` / `Lenient` / `Direwolf` / `AprsIs` presets; each accommodation is a named, individually-toggleable flag.
- `AprsCallsign` — permissive monitor-layer callsign that round-trips APRS-IS spellings (letter SSIDs, lowercase, long bases) that strict `Packet.Core.Callsign` rejects, with coercion helpers.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) — the AX.25 frames whose info field carries these payloads
- [Packet.Core](https://www.nuget.org/packages/Packet.Core) — shared primitives including the strict `Callsign`

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
