# Packet.Aprs design

The APRS codec: every data type in the APRS 1.2 protocol reference, both directions, from either AX.25 fields or TNC2 / APRS-IS text.

## Sources, in order of precedence

1. **APRS12c.pdf** (APRS Protocol Reference 1.2, draft C, WB2OSZ 2024) from [wb2osz/aprsspec](https://github.com/wb2osz/aprsspec). It merges APRS101 with the 1.1 and 1.2 addenda. Where it and APRS101 disagree, APRS12c wins.
2. **Understanding-APRS-Packets.pdf** (same repo). It covers de facto rules the spec only implies, plus a catalogue of real-world errors.
3. **[aprsorg/aprs-deviceid](https://github.com/aprsorg/aprs-deviceid)** (CC BY-SA 2.0) for device identifiers and the Mic-E type codes, which APRS12c defers to.
4. **APRS-IS** conventions (q-constructs, non-AX.25 names in TNC2 text), which APRS12c points at but does not define.

Source code comments cite these as `APRS12c §n` / `UAP §n`. Spec text is not copied into the repo.

direwolf (`decode_aprs`) and Ham::APRS::FAP (the parser behind aprs.fi) are **reference implementations, not the spec**. They're used for differential testing against the corpus. Where they disagree with each other or with the spec, the disagreement is recorded in [`interpretations.md`](https://github.com/packet-net/aprs-vectors/blob/main/interpretations.md).

## Philosophy (the Packet.NET one)

- **Encoding is strict.** The encoder never produces anything APRS12c forbids. Invalid input throws `ArgumentException` at encode time.
- **Decoding never throws.** It returns an `AprsPacket` whose `Data` is always set. If the information field can't be decoded, `Data` is `AprsUnrecognizedData` with a reason.
- **Every deviation is reported.** Decoding collects `AprsDiagnostic`s (stable code, severity, message, offset). Tolerating a real-world defect is a **named flag** on `AprsParseOptions`. The flag is on in `Lenient` (the default) and off in `Strict`, and each one has a paired test: strict rejects the input, lenient accepts it and warns. The flag inventory is in [`strict-vs-pragmatic-audit.md`](strict-vs-pragmatic-audit.md#packetaprs).
- **Spec-sanctioned receive rules are not leniency.** When APRS12c itself tells receivers to accept something (longer message text, variable-width telemetry, ignoring the AX.25 C bits), it is accepted in every mode.

## Layers

```
TNC2 line bytes ──┐
                  ├─► AprsPacket { Source, Destination, Path, Information (raw bytes), Data, Diagnostics }
AX.25 UI frame ───┘                                                           │
                                                                              └─► AprsData (closed hierarchy, pattern-match on it)
```

- `AprsPacket` is the envelope. It keeps the raw information bytes, so re-emitting exactly what was heard is always possible.
- `AprsAddress` holds address text as seen, which may be non-AX.25 on APRS-IS (e.g. `WHO-IS`, `T2SPAIN`), plus `IsAx25`. AX.25 binary encoding requires `IsAx25`.
- `AprsPathEntry` is an address plus its has-been-repeated (H) bit. TNC2 `*` marks the last repeated entry, and every earlier entry is implied repeated.
- Mic-E is decoded at packet level because half of it lives in the destination address.
- Text fields are decoded as UTF-8 (APRS 1.2). Invalid UTF-8 falls back to Latin-1 per byte, with a diagnostic.

## Data types (`AprsData`)

| DTI | Type |
|---|---|
| `! = / @` | `AprsPositionReport` (includes complete weather, DF, Ultimeter `!!` excepted) |
| `` ` ' `` 0x1c 0x1d | `AprsMicEReport` |
| `;` | `AprsObjectReport` |
| `)` | `AprsItemReport` |
| `:` | `AprsMessage` subtypes: text message, ack, rej, bulletin/announcement/group bulletin, NWS bulletin, telemetry PARM/UNIT/EQNS/BITS, directed query |
| `>` | `AprsStatusReport` (plain, timestamped, Maidenhead + symbol, meteor-scatter beam/ERP) |
| `T` | `AprsTelemetryReport` |
| `_` | `AprsWeatherReport` (positionless) |
| `# * $ULTW !!` | `AprsRawWeatherReport` |
| `$` | `AprsNmeaReport` |
| `[` | `AprsMaidenheadBeacon` |
| `?` | `AprsGeneralQuery` |
| `<` | `AprsStationCapabilities` |
| `}` | `AprsThirdPartyTraffic` (the inner packet, decoded recursively) |
| `{` | `AprsUserDefinedData` |
| `,` | `AprsTestData` |
| `%` | `AprsAgreloDfReport` |
| anything else | `AprsUnrecognizedData` (non-APRS beacon, reserved DTI, or malformed) |

Position, object, item and Mic-E share a base, `AprsPositionedData`, carrying `Position`, `Symbol`, and everything that can ride in the data extension or comment. That covers course/speed, PHG (+PHGR), RNG, DFS, area object, DF bearing/NRQ, altitude, DAO, base-91 comment telemetry, voice frequency, weather, storm data, and signpost / corridor braces. `Comment` is whatever free text is left once those are extracted.

Values are held in the units APRS uses on air, with the unit in the property name (`SpeedKnots`, `AltitudeFeet`, `TemperatureFahrenheit`). Telemetry values are `decimal`, so their scale survives a round trip.

## Round trips

- Canonical input decodes and re-encodes **byte for byte**. Every spec example is a test of this.
- Legal but non-canonical input re-encodes to the canonical form, and **decoding is idempotent**: `decode(encode(decode(x)))` equals `decode(x)`. This holds for every spec-clean packet in the corpus. A packet decoded under a tolerance may lose precision it didn't have a canonical place for (for example, wind direction moved into a compressed position's 4-degree cs byte). The encoder refuses anything it can't write legally, such as a weather report with a comment.
- The received bytes are always in `AprsPacket.Information`. Retransmit those, not a re-encode, to pass a packet on unchanged.
- **Comments are checked, then confirmed.** Before writing free text, the encoder asks whether the comment on its own would read back as something structured (a frequency, `/A=` altitude, `!DAO!`, telemetry, PHG and so on). Whether it really would depends on what is written in front of it, so when the answer is yes the encoder decodes the finished field and refuses only if the comment, or anything it could have turned into, comes back different. A frequency later in a comment, after PHG or an altitude, is therefore written back where the sender had it.
- Some clean packets can be read but not sent: message text over 67 characters (APRS12c tells receivers to accept it and senders not to write it), or a comment whose layout the canonical form can't reproduce. The encoder refuses those with `ArgumentException`, and the corpus snapshot records each refusal so a new one is a reviewed change.

See [`aprs-validation.md`](aprs-validation.md) for the numbers.

## Fit with the rest of Packet.NET

`Packet.Aprs` depends on `Packet.Core` only. Frames cross to and from `Packet.Ax25` in KISS form (no flags, no FCS): `AprsPacket.DecodeAx25(frame.ToBytes())` decodes a received `Ax25Frame`, and `Ax25Frame.TryParse(packet.ToAx25Frame(), ...)` gives one to send; `tests/Packet.Aprs.Tests/Envelope/PacketNetInteropTests.cs` holds both directions to strict `Ax25ParseOptions`. The codec checks for a UI frame with PID 0xF0 itself, so the caller doesn't have to. `AprsAddress.FromCallsign` and `TryGetCallsign` convert to and from `Packet.Core.Callsign`.

### Why two address types

`Callsign` and `AprsAddress` look alike (`Base`, `Ssid`) but model different things, so they stay separate:

- `Packet.Core.Callsign` is a station you can transmit as: 1-6 upper-case letters and digits, SSID 0-15. The whole AX.25 stack relies on that, so everything it sends is valid.
- `AprsAddress` is a header token exactly as written. APRS headers are full of things that are not callsigns: q-constructs (`qAR`), `TCPIP`, path aliases (`WIDE2-1`), APRS-IS names (`WHO-IS`, `T2SPAIN`), lower-case and letter-SSID spellings, and `N0CALL-0` as distinct from `N0CALL`. Keeping the spelling is what lets a decoded packet re-encode to the bytes that were heard.

Loosening `Callsign` would cost the AX.25 layer its guarantee; using only `Callsign` here would lose a large share of APRS-IS headers. The bridge is explicit instead: `AprsAddress.IsAx25` says whether an address is one, `TryGetCallsign` converts it, and `ToAx25Frame` refuses a packet whose addresses are not.

## Out of scope for v1

APRS-IS client, messaging state (acks, retries, reply-ack bookkeeping), digipeater and IGate behaviour, SmartBeaconing. The codec exposes what those need, e.g. q-construct parsing and reply-ack fields, but keeps no state.
