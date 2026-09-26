# APRS conformance vectors

Language-neutral test cases for APRS decoders and encoders: what a packet means, what is wrong with it, and what a strict and a lenient decoder should each make of it. Any implementation in any language can run them. Packet.Aprs runs them as part of its own test suite (`tests/Packet.Aprs.Tests/Vectors/`), so they are kept honest against a real decoder on every build.

The cases come from three places, and each says which:

| Authority | Meaning | Other implementations should |
|---|---|---|
| `spec` | The expected values follow from the text of APRS12c or *Understanding APRS Packets* (UAP), usually a worked example printed there. | match them |
| `interpretation` | The spec is ambiguous, contradicts itself or is wrong here; the expected values follow a decision recorded in [`docs/aprs-spec-interpretations.md`](../../docs/aprs-spec-interpretations.md), linked from the case. | match them or document why not |
| `observed` | A real packet from the APRS-IS feed; the expected values are what Packet.Aprs does, kept as a regression record. | treat a difference as a question, not a failure |

> **Status:** a first batch (one or two cases of each kind) to settle the format before the Packet.Aprs test suite moves onto it. **Licence:** not yet decided.

## Files

| File | What |
|---|---|
| `cases/*.json` | The cases, one file per area (`position`, `mic-e`, `message`, ...). Each file is `{"cases": [ ... ]}`. |
| `codes.json` | Every diagnostic code a case can name, with its meaning and whether a lenient decoder may tolerate it. |
| `schema.json` | JSON Schema (2020-12) for the case files. |

## A case

```json
{
  "id": "position/lowercase-hemisphere",
  "description": "Lower-case hemisphere letters: a strict decoder rejects them, a lenient one reads them as upper case.",
  "source": "UAP 5.9",
  "authority": "spec",
  "input": { "tnc2": "N1EOE>APN391,N1NCI-3*,WIDE2-1:!4216.95n/07243.20w#phg6230/ Easthampton MA" },
  "expect": {
    "data": { "type": "position", "latitude": 42.2825, "longitude": -72.72, "symbol": "/#", "comment": "phg6230/ Easthampton MA" },
    "diagnostics": [ "warning:lowercase-hemisphere", "warning:lowercase-hemisphere" ]
  },
  "strict": { "rejected_by": "lowercase-hemisphere" },
  "reencode": "equivalent",
  "canonical_info": "!4216.95N/07243.20W#phg6230/ Easthampton MA"
}
```

| Key | Required | Meaning |
|---|---|---|
| `id` | yes | Unique, `area/short-name`. Stable: other suites may refer to it. |
| `description` | yes | One sentence saying why the case exists. |
| `source` | yes | Where the packet comes from: a spec section, a UAP section, or `APRS-IS <date>` for the live feed. |
| `authority` | yes | `spec`, `interpretation` or `observed` (above). |
| `interpretations` | no | Headings in `docs/aprs-spec-interpretations.md` (as GitHub anchors) that the expected values depend on. |
| `input` | yes | What to decode, or with `encode`, what to encode (below). |
| `expect` | yes | The lenient decoder's result, or an encode case's result. |
| `strict` | no | The strict decoder's result: `"same"` (the default), `{"rejected_by": code}` (add `"header": true` when the header is what fails), or a full `{"data", "diagnostics"}` when strict reads the packet differently without rejecting it. |
| `reencode` | no | Encoding the lenient decoder's data again: `"identical"` gives the input's information field byte for byte (for Mic-E, the destination too), `"equivalent"` gives bytes that decode to the same data, `"refused"` means the encoder must decline (for example message text over 67 characters, which receivers accept and senders may not write). |
| `canonical_info` | no | With `equivalent`: the bytes Packet.Aprs writes. Another encoder may choose differently and still conform. |

### Input

One of:

- `"tnc2"`: a TNC2 / APRS-IS text line. `"tnc2_hex"` when the line is not valid UTF-8.
- `"ax25_hex"`: an AX.25 UI frame in KISS form (no flags, no FCS), as hex.
- `"info"` (or `"info_hex"`): just the information field. The header is `N0CALL>APZ001` unless the case gives `"source"`, `"destination"` or `"path"` alongside it.
- `"encode"`: data in the form below, for an encode case. `"source"` and `"destination"` default as above.

### Expect

For a decode case:

| Key | Meaning |
|---|---|
| `data` | The decoded data (form below). |
| `diagnostics` | Every diagnostic, as `"severity:code"`. Compare as a multiset (order does not matter, repeats do). A decoder that does not report `info` diagnostics can ignore those. Absent means none. |
| `header` | The header, when the case is about it: `source`, `destination`, `path`, `q_construct`. |
| `header_error` | Instead of `data`: the header is unusable and nothing is decoded; the diagnostics that say why. |
| `device` | The sending device from the [aprs-deviceid](https://github.com/aprsorg/aprs-deviceid) database: `vendor`, `model`. Depends on the database version. |

For an encode case, `expect` is `{"info": "..."}` (the information field to write) or `{"refused": true}`.

## The data form

The rules, which make absence meaningful: a field that is not listed must not be produced.

- Names are snake_case. Quantities keep the units APRS sends, with the unit in the name (`speed_knots`, `altitude_feet`, `temperature_f`); nothing is converted.
- Null values, empty strings and empty lists are left out. The one exception is `reply_ack`, where an empty string says the sender supports reply-acks.
- Booleans are named for the less usual state and written only when true (`compressed`, `messaging`, `killed`, `old_data`).
- Enumerations are kebab-case strings (`off-duty`, `peet-bros-hash`).
- Timestamps are written as on air: `092345z`, `092345/`, `234517h`, or `10090556` (month, day, hour, minute).
- Digital telemetry bits are written as on air: eight `0`/`1` characters, channel B1 first.
- Values another field already determines are not written: a symbol's description, PHG in watts, a bulletin's kind, a Mic-E radio's messaging capability.
- Byte-valued text (user-defined data) is a string of code points U+0000-U+00FF, one per byte.

### Comparing

- Strings, booleans and integers compare exactly.
- Other numbers compare within 1e-9, relative to the larger magnitude (or absolute below 1). Implementations reach the same degrees by different arithmetic.
- An implementation that does not produce some field should skip it, and knows it has not been tested on it.

### Fields by type

Every decoded `data` has `type`. Positions, Mic-E reports, objects and items share the positioned fields.

| Type | Fields |
|---|---|
| `position` | `timestamp`, `messaging`, positioned fields |
| `mic-e` | `mic_e_message`, `old_data` (`'` rather than `` ` ``), `type_code`, `device_suffix`, `locator`, `legacy_telemetry`, `destination_ssid`, positioned fields |
| `object` | `name`, `killed`, `timestamp`, positioned fields |
| `item` | `name`, `killed`, positioned fields |
| `message` | `addressee`, `text`, `message_id`, `reply_ack` |
| `ack` / `reject` | `addressee`, `acked_id` / `rejected_id`, `reply_ack` |
| `bulletin`, `nws-bulletin` | `addressee`, `text`, `message_id` |
| `telemetry-names` / `-units` / `-coefficients` | `addressee`, `names` / `units` / `coefficients` |
| `telemetry-bits` | `addressee`, `bits`, `project` |
| `directed-query` | `addressee`, `query_type`, `target` |
| `status` | `timestamp`, `locator`, `symbol`, `beam` (`heading_code`, `power_code`), `text` |
| `telemetry` | `sequence` (as sent), `analog` (numbers, `null` for an empty channel), `bits`, `comment` |
| `weather` | `timestamp`, `weather`, `comment` |
| `raw-weather` | `format`, `data` |
| `nmea` | `sentence`, `has_checksum`, `latitude`, `longitude`, `fix` (`valid`/`invalid`), `course_degrees`, `speed_knots`, `altitude_m`, `time`, `waypoint` |
| `maidenhead-beacon` | `locator`, `comment` |
| `query` | `query_type`, `footprint` (`latitude`, `longitude`, `radius_miles`) |
| `capabilities` | `capabilities`: `[token]` or `[token, value]` pairs, in order |
| `third-party` | `packet`: the inner packet's `source`, `destination`, `path`, `data`, `diagnostics` |
| `user-defined` | `user_id`, `packet_type`, `data` |
| `test` | `data` |
| `agrelo-df` | `bearing_degrees`, `quality` |
| `unrecognized` | `reason`: `empty`, `not-aprs`, `reserved-data-type` or `malformed` |

Positioned fields:

| Field | Meaning |
|---|---|
| `latitude`, `longitude` | Degrees, north and east positive, with any `!DAO!` precision applied. An ambiguous position gives the centre of its box ([interpretation](../../docs/aprs-spec-interpretations.md#position-ambiguity-which-point-is-reported)). |
| `ambiguity` | 1-4 digits blanked; absent for none. |
| `symbol` | Table (or overlay) character and code, e.g. `/>`. |
| `compressed`, `compression` | Compressed format, and its type byte: `fix` (`old`/`current`), `source` (`other`/`gll`/`gga`/`rmc`), `origin`. |
| `course_degrees`, `speed_knots`, `altitude_feet` | As sent. |
| `phg` | Codes as sent: `power`, `height`, `gain`, `directivity`, and `beacons_per_hour` for PHGR. |
| `range_miles`, `dfs`, `area`, `df_bearing`, `storm` | The other data extensions, codes as sent. |
| `dao` | `datum` and `precision` (`none`/`thousandths`/`base91`). |
| `telemetry` | Base-91 comment telemetry: `sequence`, `analog`, `digital`. |
| `frequency` | APRS 1.2 voice frequency: `mhz`, `tone`, `tone_value`, `offset_khz`, `range`, `range_km`, `narrow`, `ten_khz_resolution`. |
| `weather` | `wind_direction_degrees`, `wind_speed_mph`, `wind_gust_mph`, `temperature_f`, `rain_1h_in`, `rain_24h_in`, `rain_midnight_in`, `rain_raw`, `humidity_percent`, `pressure_mbar`, `luminosity_w_m2`, `snow_24h_in`, `software`, `unit`, `extra` (`letter`, `value`). |
| `signpost`, `comment` | Signpost text, and the free text left once everything above is lifted out. |

## Adding a case

Write the input, description, source and authority by hand; for `spec` and `interpretation` cases, check every expected value against the document it cites rather than copying the decoder's output. Then run `dotnet test tests/Packet.Aprs.Tests --filter Vectors`: a new case must pass, and it should fail if the behaviour it describes is broken.
