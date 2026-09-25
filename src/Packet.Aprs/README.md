# Packet.Aprs

> APRS encoder and decoder: every APRS 1.2 data type, both directions, from AX.25 frames or TNC2 / APRS-IS text.

Packet.Aprs encodes and decodes Automatic Packet Reporting System packets per [APRS12c](https://github.com/wb2osz/aprsspec), the merged APRS 1.0 + 1.1 + 1.2 reference, with *Understanding APRS Packets* and the [APRS device identification database](https://github.com/aprsorg/aprs-deviceid). It is spec-strict, with a named, individually switchable flag for each defect real traffic is full of. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack; it sits above [Packet.Ax25](https://www.nuget.org/packages/Packet.Ax25) and decodes what an AX.25 UI frame carries.

Checked against real traffic: half a million full-feed APRS-IS packets decode without an exception, every spec-clean one re-encodes to identical data, and field values agree with Ham::APRS::FAP (the aprs.fi parser) except where FAP is wrong. See [docs/aprs-validation.md](https://github.com/packet-net/packet.net/blob/main/docs/aprs-validation.md).

## Install
```sh
dotnet add package Packet.Aprs
```

## The shape of it

Everything goes through `AprsPacket`: a header (`Source`, `Destination`, `Path`), the raw `Information` bytes, the decoded `Data`, and any `Diagnostics`.

```csharp
using Packet.Aprs;

AprsPacket packet = AprsPacket.Decode("N0CALL-9>APZ001,WIDE1-1,qAR,M0LTE-10:!5130.00N/00007.00W>088/036/A=001234Hello");

if (packet.Data is AprsPositionReport p)
{
    Console.WriteLine($"{p.Position.Latitude}, {p.Position.Longitude}"); // 51.5, -0.11666666666666667
    Console.WriteLine(p.Symbol.Description);                             // Car
    Console.WriteLine($"{p.CourseDegrees} deg at {p.SpeedKnots} kn");    // 88 deg at 36 kn
    Console.WriteLine($"{p.AltitudeFeet} ft, \"{p.Comment}\"");          // 1234 ft, "Hello"
}
```

`Data` is a closed hierarchy you pattern-match on: `AprsPositionReport`, `AprsMicEReport`, `AprsObjectReport`, `AprsTextMessage`, `AprsWeatherReport`, `AprsTelemetryReport` and so on, with `AprsUnrecognizedData` for anything that isn't APRS or can't be read. Positions, objects, items and Mic-E reports share a base, `AprsPositionedData`, so one `case` handles every kind of thing that has a place on a map.

Decoding never throws because of the information field: `Data` is always set, and `Diagnostics` says what was wrong. Only an unusable header (no `SOURCE>DEST:`) throws `AprsFormatException`; `AprsPacket.TryDecode` returns false instead. Encoding is the opposite: it is strict, and throws `ArgumentException` naming the offending property rather than write something the spec forbids.

## Worked examples

### 1. Put every station from an APRS-IS feed on a map

APRS-IS servers send one packet per line, in the same TNC2 text form a TNC's monitor prints, plus comment lines starting with `#`. Connecting to a server is up to you (this package has no APRS-IS client); each line goes straight into `TryDecode`.

```csharp
foreach (string line in lines)
{
    if (line.StartsWith('#') || !AprsPacket.TryDecode(line, out AprsPacket? packet))
    {
        continue; // server comments, and the rare line with no usable header
    }

    // An IGate's third-party packet carries the real one inside it.
    AprsPacket station = packet.Data is AprsThirdPartyTraffic relayed ? relayed.Packet : packet;

    switch (station.Data)
    {
        case AprsObjectReport { IsAlive: false } killed:
            Remove(killed.Name);
            break;
        case AprsObjectReport o:
            Plot(o.Name, o.Position, o.Symbol);
            break;
        case AprsItemReport i:
            Plot(i.Name, i.Position, i.Symbol);
            break;
        case AprsPositionedData p: // position reports and Mic-E
            Plot(station.Source.Value, p.Position, p.Symbol);
            break;
    }
}
```

Objects and items are things a station reports on behalf of something else (a repeater, a hurricane, a net's meeting point), so they are plotted under their own name rather than the sender's.

### 2. Work out who is moving, and on what radio

Mic-E is the compact format mobile radios send. Half of the position is packed into the destination address (`TRQP7T` here), which is why decoding always takes the whole packet rather than just the information field.

```csharp
AprsPacket packet = AprsPacket.Decode("N1JCM-9>TRQP7T,WA1PLE-4*:`c'wl|+>/`\"4-}_%");
var mobile = (AprsMicEReport)packet.Data;

Console.WriteLine($"{mobile.Position.Latitude}, {mobile.Position.Longitude}");      // 42.179, -71.1985
Console.WriteLine($"{mobile.SpeedKnots} kn heading {mobile.CourseDegrees} deg");  // 9 kn heading 215 deg
Console.WriteLine($"{mobile.AltitudeFeet:F0} ft");                                // 72 ft
Console.WriteLine(mobile.Message);                                                // OffDuty
Console.WriteLine(mobile.Device);                                                 // Yaesu FTM-400DR
Console.WriteLine(mobile.MessagingCapable);                                       // True
Console.WriteLine(packet.Path[0]);                                                // WA1PLE-4*
```

The radio model comes from the type code and suffix around the status text, looked up in the embedded device database. `AprsDeviceIdentification.Identify(packet)` does the same for any packet, using the destination address (the "tocall") when it isn't Mic-E.

### 3. Read a weather station

Weather rides on a position report with the weather symbol. Values keep the units APRS sends, with the unit in the property name, so converting is explicit.

```csharp
AprsPacket packet = AprsPacket.Decode("W1TG2>APU25N:@091842z4256.20N/07049.42W_310/004g015t081r000p033P002h54b10001/ - Hampton, NH Wx");
var report = (AprsPositionReport)packet.Data;
AprsWeather wx = report.Weather!;

double celsius = (wx.TemperatureFahrenheit!.Value - 32) / 1.8;
Console.WriteLine($"{celsius:F1} C, {wx.HumidityPercent}% humidity, {wx.PressureMillibars} hPa"); // 27.2 C, 54% humidity, 1000.1 hPa
Console.WriteLine($"wind {wx.WindDirectionDegrees} deg at {wx.WindSpeedMph} mph, gusting {wx.WindGustMph}"); // wind 310 deg at 4 mph, gusting 15
Console.WriteLine($"{wx.RainSinceMidnightInches} in of rain since midnight");                // 0.02 in of rain since midnight

// APRS timestamps are partial (this one is day 09, 18:42 UTC); resolve against when you heard it.
DateTime? observed = report.Timestamp?.Resolve(receivedAt);      // 2026-09-09 18:42 UTC
Console.WriteLine(AprsDeviceIdentification.Identify(packet)?.Model); // UI-View32
```

The spec gives a weather report no comment field, but almost every station sends one. By default the text is kept in `report.Comment` and the packet carries a warning saying so:

```csharp
Console.WriteLine(packet.Diagnostics[0].Code); // WeatherComment
```

### 4. Send a message and know when it arrived

```csharp
var message = new AprsTextMessage { Addressee = "N0CALL-7", Text = "Meet at the club at 8?", MessageId = "42" };
AprsPacket outgoing = AprsPacket.Create("M0LTE-9", "APZ001", message, "WIDE1-1,WIDE2-1");
Console.WriteLine(outgoing); // M0LTE-9>APZ001,WIDE1-1,WIDE2-1::N0CALL-7 :Meet at the club at 8?{42

// Later, from the feed:
AprsPacket incoming = AprsPacket.Decode("N0CALL-7>APDR16,WIDE1-1,qAR,N0CALL-10::M0LTE-9  :ack42");
if (incoming.Data is AprsMessageAck ack && ack.Addressee == "M0LTE-9" && ack.AcknowledgedId == message.MessageId)
{
    // Delivered: stop retrying.
}
```

Retrying and remembering what's outstanding is up to you; the codec keeps no state. Addressee padding, the `{` before the ID and reply-acks (`ReplyAck`) are handled for you. `AprsBulletin`, `AprsMessageReject` and the rest of the message family work the same way.

### 5. Announce a repeater

An object puts something that isn't your station on the map. APRS 1.2 adds a voice frequency, tone and offset that radios can tune to directly.

```csharp
var repeater = new AprsObjectReport
{
    Name = "MYRPTR",
    IsAlive = true,
    Timestamp = AprsTimestamp.DayHoursMinutes(25, 18, 30), // or AprsTimestamp.FromDateTime(DateTime.UtcNow)
    Position = new AprsPosition(51.4543, -0.9781),
    Symbol = AprsSymbol.Parse("/r"),
    Frequency = new AprsVoiceFrequency { FrequencyMHz = 145.725m, ToneType = AprsToneType.Tone, ToneValue = 118, OffsetKHz = -600 },
    Comment = "Reading repeater",
};

Console.WriteLine(AprsPacket.Create("M0LTE", "APZ001", repeater, "WIDE2-1"));
// M0LTE>APZ001,WIDE2-1:;MYRPTR   *251830z5127.26N/00058.69Wr145.725MHz T118 -060 Reading repeater

// To take it off everyone's map, send it again marked dead:
AprsPacket kill = AprsPacket.Create("M0LTE", "APZ001", repeater with { IsAlive = false }, "WIDE2-1");
```

Positions go out to the hundredth of a minute the format allows (about 18 m), so what decodes back is the rounded position. Set `IsCompressed = true` for the shorter base-91 form, which is also more precise.

### 6. Turn telemetry into real values

A telemetry station sends five raw analog values and eight bits, and separately (as messages to itself) what each channel is called, its unit, and the equation that turns the raw number into a value.

```csharp
string[] lines =
[
    "M0LTE-11>APZ001::M0LTE-11 :PARM.Battery,Temp,Humidity,Light,Pressure,Door,Mains,Fan",
    "M0LTE-11>APZ001::M0LTE-11 :UNIT.V,degC,%,lux,hPa,open,on,on",
    "M0LTE-11>APZ001::M0LTE-11 :EQNS.0,0.075,0,0,0.5,-40,0,0.4,0,0,4,0,0,1,900",
    "M0LTE-11>APZ001:T#123,172,123,150,050,113,10100000",
];

AprsTelemetryParameterNames? names = null;
AprsTelemetryUnits? units = null;
AprsTelemetryCoefficients? equations = null;
foreach (string line in lines)
{
    switch (AprsPacket.Decode(line).Data)
    {
        case AprsTelemetryParameterNames n: names = n; break;
        case AprsTelemetryUnits u: units = u; break;
        case AprsTelemetryCoefficients e: equations = e; break;
        case AprsTelemetryReport t when names is not null && units is not null && equations is not null:
            for (int channel = 1; channel <= t.Analog.Count; channel++)
            {
                if (t.Analog[channel - 1] is decimal raw)
                {
                    Console.WriteLine($"{names.Names[channel - 1]}: {equations.Scale(channel, raw):0.##} {units.Units[channel - 1]}");
                }
            }

            for (int bit = 0; bit < 8 && 5 + bit < names.Names.Count; bit++)
            {
                bool set = ((t.Digital >> bit) & 1) == 1;
                Console.WriteLine($"{names.Names[5 + bit]}: {(set ? units.Units[5 + bit] : "-")}");
            }

            break;
    }
}
// Battery: 12.9 V
// Temp: 21.5 degC
// Humidity: 60 %
// Light: 200 lux
// Pressure: 1013 hPa
// Door: open
// Mains: -
// Fan: on
```

In a real receiver, keep the definitions per station (the addressee is the station they describe).

### 7. Check your own beacon against the spec

`Strict` rejects anything the spec doesn't allow and says why, which makes it a handy linter for a beacon text or a new tracker's output.

```csharp
const string beacon = "N1EOE>APN391,N1NCI-3*,WIDE2-1:!4216.95n/07243.20w#phg6230/ Easthampton MA";

AprsPacket strict = AprsPacket.Decode(beacon, AprsParseOptions.Strict);
foreach (AprsDiagnostic d in strict.Diagnostics)
{
    Console.WriteLine($"{d.Severity} {d.Code}: {d.Message}");
}
// Error LowercaseHemisphere: hemisphere 'n' must be upper case (UAP 5.9)

AprsPacket lenient = AprsPacket.Decode(beacon); // decodes, with two LowercaseHemisphere warnings
```

### 8. On the air with Packet.Ax25 or a KISS TNC

On RF the packet is an AX.25 UI frame. `ToAx25Frame()` writes it in KISS form (no flags, no FCS), which is what a KISS TNC takes and what `Ax25Frame.ToBytes()` produces, so frames cross between the two libraries as bytes:

```csharp
AprsPacket received = AprsPacket.DecodeAx25(frame.ToBytes());        // an Ax25Frame from Packet.Ax25
Ax25Frame.TryParse(packet.ToAx25Frame(), out Ax25Frame? toSend);     // one to hand to a transport

await kiss.SendAsync(0, KissCommand.Data, packet.ToAx25Frame(), ct); // or straight to a KISS TNC
```

`DecodeAx25` checks for a UI frame with PID 0xF0 itself. APRS-IS carries names that aren't AX.25 callsigns (`qAR`, `TCPIP`, `WHO-IS`), so the header uses `AprsAddress`, which keeps any address as written; convert to and from Packet.NET's strict `Callsign` when you need one:

```csharp
AprsAddress me = AprsAddress.FromCallsign(new Callsign("M0LTE", 9));
if (received.Source.TryGetCallsign(out Callsign source)) { /* an AX.25 station, not an APRS-IS name */ }
```

`ToAx25Frame` throws if an address isn't valid AX.25.

## Strict or lenient
```csharp
AprsPacket.Decode(line);                              // AprsParseOptions.Lenient: accept and warn
AprsPacket.Decode(line, AprsParseOptions.Strict);     // only packets that follow the spec
AprsPacket.Decode(line, AprsParseOptions.Lenient with { AllowWeatherComment = false });
```

Each tolerance is a named flag, so you can turn off exactly the ones you don't want. Every flag, the spec rule it relaxes, and how common it is on the network are listed in [docs/strict-vs-pragmatic-audit.md](https://github.com/packet-net/packet.net/blob/main/docs/strict-vs-pragmatic-audit.md#packetaprs). About 88% of APRS-IS traffic passes `Strict`.

## Good to know
- **Units are in the names.** `SpeedKnots`, `AltitudeFeet`, `TemperatureFahrenheit`, `WindSpeedMph`, `PressureMillibars`: the units APRS uses on air, so nothing is converted behind your back.
- **To pass a packet on unchanged, send `Information`.** It holds the bytes exactly as received. Re-encoding `Data` gives the canonical form, which can differ from what the sender wrote (filler bytes, field order, padding) while meaning the same thing.
- **Timestamps are partial.** APRS timestamps have no year and some have no date; `AprsTimestamp.Resolve(reference)` picks the matching time nearest the moment you received the packet.
- **Text is UTF-8.** Invalid UTF-8 falls back to Latin-1 byte by byte, with a `NonUtf8Text` warning.
- **Mic-E needs the destination.** Decode the whole packet; the information field alone isn't enough.

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
