# Packet.Aprs

> APRS encoder and decoder: every APRS 1.2 data type, both directions, from AX.25 frames or TNC2 / APRS-IS text.

Packet.Aprs encodes and decodes Automatic Packet Reporting System packets per [APRS12c](https://github.com/wb2osz/aprsspec), the merged APRS 1.0 + 1.1 + 1.2 reference, with *Understanding APRS Packets* and the [APRS device identification database](https://github.com/aprsorg/aprs-deviceid). It is spec-strict, with a named, individually switchable flag for each defect real traffic is full of. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack; it sits above [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) and decodes what an AX.25 UI frame carries.

Checked against real traffic: half a million full-feed APRS-IS packets decode without an exception, every spec-clean one re-encodes to identical data, and field values agree with Ham::APRS::FAP (the aprs.fi parser) except where FAP is wrong. See [docs/aprs-validation.md](https://github.com/packet-net/packet.net/blob/main/docs/aprs-validation.md).

## Install
```sh
dotnet add package Packet.Aprs
```

## Decode
```csharp
using Packet.Aprs;

AprsPacket packet = AprsPacket.Decode("N0CALL-9>APZ001,WIDE1-1,qAR,M0LTE-10:!5130.00N/00007.00W>088/036/A=001234Hello");

switch (packet.Data)
{
    case AprsPositionReport p:
        Console.WriteLine($"{p.Position.Latitude}, {p.Position.Longitude} {p.Symbol.Description}");
        Console.WriteLine($"{p.CourseDegrees} deg, {p.SpeedKnots} kn, {p.AltitudeFeet} ft, \"{p.Comment}\"");
        break;
    case AprsMicEReport m:
        Console.WriteLine($"{m.Message} from a {m.Device}");
        break;
    case AprsTextMessage msg:
        Console.WriteLine($"to {msg.Addressee}: {msg.Text} (ack wanted: {msg.RequestsAck})");
        break;
    case AprsUnrecognizedData u:
        Console.WriteLine($"not decoded: {u.Reason}");
        break;
}
```

Decoding never throws because of the information field. `Data` is always set, and `Diagnostics` says what was wrong:

```csharp
foreach (AprsDiagnostic d in packet.Diagnostics)
{
    Console.WriteLine(d); // e.g. "Warning WeatherComment @52: a weather report has no comment field ..."
}
```

Only an unusable header (no `SOURCE>DEST:`) throws `AprsFormatException`; `AprsPacket.TryDecode` returns false instead.

## Strict or lenient
```csharp
AprsPacket.Decode(line);                              // AprsParseOptions.Lenient: accept and warn
AprsPacket.Decode(line, AprsParseOptions.Strict);     // only packets that follow the spec
AprsPacket.Decode(line, AprsParseOptions.Lenient with { AllowWeatherComment = false });
```

Every flag, the spec rule it relaxes, and how common it is on the network are listed in [docs/strict-vs-pragmatic-audit.md](https://github.com/packet-net/packet.net/blob/main/docs/strict-vs-pragmatic-audit.md#packetaprs).

## Encode
```csharp
var report = new AprsPositionReport
{
    Position = new AprsPosition(51.5, -0.1166667),
    Symbol = AprsSymbol.Parse("/>"),
    MessagingCapable = true,
    CourseDegrees = 88,
    SpeedKnots = 36,
    Comment = "Mobile",
};

AprsPacket packet = AprsPacket.Create("M0LTE-9", "APZ001", report, "WIDE1-1,WIDE2-1");
byte[] tnc2 = packet.ToTnc2();       // M0LTE-9>APZ001,WIDE1-1,WIDE2-1:=5130.00N/00007.00W>088/036Mobile
byte[] frame = packet.ToAx25Frame(); // KISS form: no flags, no FCS

// Mic-E puts half the position in the destination address:
AprsPacket micE = AprsPacket.CreateMicE(AprsAddress.Parse("M0LTE-9"), new AprsMicEReport { /* ... */ });
```

The encoder is strict. It never produces anything APRS12c forbids, and it throws `ArgumentException` naming the offending property. It also refuses comment text that would decode as something else, such as an `/A=` altitude, so every packet it builds decodes back to the same data.

## With Packet.Ax25
Frames cross in KISS form, the form `Ax25Frame.ToBytes()` writes and `Ax25Frame.TryParse` reads:

```csharp
AprsPacket aprs = AprsPacket.DecodeAx25(frame.ToBytes());       // a received Ax25Frame
Ax25Frame.TryParse(packet.ToAx25Frame(), out Ax25Frame? ui);     // one to send

AprsAddress me = AprsAddress.FromCallsign(new Callsign("M0LTE", 9));
if (aprs.Source.TryGetCallsign(out Callsign source)) { /* an AX.25 station, not an APRS-IS name */ }
```

## What's covered
| Data type | Types |
|---|---|
| Positions (`! = / @`), uncompressed and compressed, with timestamps, ambiguity, `!DAO!` | `AprsPositionReport` |
| Course/speed, PHG (+PHGR), RNG, DFS, DF bearing/NRQ, area objects, storm data, signposts | properties on `AprsPositionedData` |
| Weather: complete, positionless, raw station formats | `AprsWeather`, `AprsWeatherReport`, `AprsRawWeatherReport` |
| Mic-E, including device type codes, altitude, grid locator, legacy DTIs | `AprsMicEReport` |
| Objects and items, including area and frequency objects | `AprsObjectReport`, `AprsItemReport` |
| Messages, acks, rejects, reply-acks, bulletins, announcements, NWS | `AprsTextMessage`, `AprsMessageAck`, ... |
| Telemetry reports, base-91 comment telemetry, PARM/UNIT/EQNS/BITS | `AprsTelemetryReport`, `AprsCommentTelemetry`, ... |
| Status (incl. grid locator, meteor scatter beam/ERP), queries, capabilities | `AprsStatusReport`, `AprsGeneralQuery`, `AprsDirectedQuery`, ... |
| Voice frequency / tone / offset (APRS 1.2) | `AprsVoiceFrequency` |
| Third-party traffic, user-defined, NMEA, Maidenhead beacons, test data, Agrelo DF | ... |
| Symbols, device identification, APRS-IS q-constructs | `AprsSymbolTable`, `AprsDeviceIdentification`, `AprsQConstruct` |

Out of scope for now: an APRS-IS client, messaging state (retries, ack tracking), digipeater and IGate logic.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [Design](https://github.com/packet-net/packet.net/blob/main/docs/aprs-design.md) and [spec interpretations](https://github.com/packet-net/packet.net/blob/main/docs/aprs-spec-interpretations.md): where APRS12c is ambiguous or wrong, and what this library does
- [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) - the AX.25 frames whose information field carries these packets
- [Packet.Core](https://www.nuget.org/packages/Packet.Core) - shared primitives including the strict `Callsign`

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack. The embedded device database is from [aprsorg/aprs-deviceid](https://github.com/aprsorg/aprs-deviceid), CC BY-SA 2.0; see THIRD-PARTY-NOTICES.md in the package.*
