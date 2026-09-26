# APRS spec interpretations

Where APRS12c is ambiguous, contradicts itself, or disagrees with the reference implementations, the decision `Packet.Aprs` takes is recorded here with its reasoning. direwolf means `decode_aprs` from [wb2osz/direwolf](https://github.com/wb2osz/direwolf). FAP means [Ham::APRS::FAP](https://github.com/hessu/perl-aprs-fap), the parser behind aprs.fi.

## Weather wind speed unit in the DIR/SPD extension

- APRS12c §7 (Wind Direction and Wind Speed): the DIR/SPD extension speed "is expressed in knots".
- APRS12c §12 (Complete Weather Reports): the DIR/SPD extension "replace[s] the cccc and ssss fields" of the positionless report, whose `s` is "sustained one-minute wind speed (in mph)".
- direwolf reads it as knots. FAP (aprs.fi) reads it as mph. Weather software that feeds CWOP sends mph.

**Decision:** in a weather report the DIR/SPD speed is mph (`AprsWeather.WindSpeedMph`). §12 is the more specific rule for weather reports, and it matches what senders actually transmit. In a non-weather report, DIR/SPD is course/speed and the speed is knots.

## Position ambiguity: which point is reported

- APRS12c §6 (Position Ambiguity) says ambiguity "is not a truncation and it is not a box", and should be drawn as a circle centred on the position.
- direwolf treats the blanked digits as zeros, so it reports the corner of the range. FAP reports the centre of the range: +0.05', +0.5', 5', or +0.5° for ambiguity 1 to 4.

**Decision:** report the centre (FAP), and expose the level as `AprsPosition.Ambiguity`. Encoding blanks the same digits again, so the round trip is exact.

## Compressed coordinates: round, not truncate

APRS12c §9 computes `190463 x (180 - 72.75) = 20427156` for the worked example, dropping the `.75`, which gives `<*e7`. The encoder here rounds to the nearest step, as direwolf's `latlong.c` does, which gives `<*e8`, the point nearer the true position. Decoding is unaffected, and any decoded compressed position re-encodes to the same characters.

## Delimiters at the start of free text

APRS12c §18: "any free text field that begins with either of these two delimiters [space or `/`] ... can be ignored and the beginning of the text field begins after them." After the structured elements are lifted out of a position, object, item or Mic-E comment, **one** leading space or `/` is dropped. This covers `PHG7440/DigiPeater` and `r/RV46 PRIJEDOR` (FAP does the same) and Mic-E `' KJ6TMS` (direwolf and FAP both give `KJ6TMS`). A `/` that begins `/A=` is an altitude, not a delimiter. The encoder writes a `/` before a comment that itself starts with a space or `/`, or that would otherwise read as a data extension, so every comment round-trips.

## Compressed course 0 is north

The compressed course byte gives 0 to 356 degrees in 4-degree steps and has no "unknown" value (APRS12c §9). This library reports course 1-360 with 360 as north, so a compressed `c` of `!` (0) decodes as 360, as FAP does. A compressed course/speed can't be built without a course; an unknown course has no compressed form.

## Base-91 telemetry and `!DAO!`

The spec orders them comment, telemetry, DAO (APRS12c §13). Base-91 telemetry can contain a run that happens to look like a DAO (`|!m2n!Q[P|`), so the telemetry block is located first and the DAO is only looked for outside it.

## `!DAO!` on a compressed position

Compressed positions already resolve to about 0.3 m. A `!DAO!` on one is kept (its datum is information) but its extra digits are not applied.

## Mic-E type codes

APRS 1.2 reassigned `` ` `` and `'` after the symbol to "messaging capable" and "not messaging capable" device type codes. They were originally Mic-E telemetry flags, and the telemetry is now obsolete. They are decoded as type codes. A single leading space is the original Mic-E type code. The device suffix is matched against the device identification database, as direwolf does, so an unknown suffix stays in the comment rather than being guessed at.

## Grid locator status reports

`>JN55VD Powered by...` is a bare 6-character locator followed by text: plain status text, not the grid format. The grid format requires a symbol straight after the locator. A 4-character reading (`JN55` with symbol `VD`) is only tried when the first 6 characters are not themselves a locator.

## Object names may start with spaces

- APRS12c §11: the name "may consist of any printable ASCII characters, including embedded spaces. Trailing spaces are used to make the field 9 characters wide."

**Decision:** leading spaces are part of the name (`;   OR4F  *...` is the object `   OR4F`), and the encoder writes them. Only a trailing space is refused, since it would be read back as padding.

## Raw NMEA with a checksum that doesn't match

- APRS12c defers the sentence format to NMEA 0183, whose `*hh` checksum exists to detect corruption. FAP rejects a mismatch (`nmea_inv_cksum`).

**Decision:** a mismatch is an error in both modes, not a tolerance: the sentence is corrupt, and a real one (`$GPRMC,...,4609.2815,N8.9077,W,...`) had lost characters from its longitude. The encoder refuses to write a sentence whose checksum is wrong. A sentence with no checksum is fine; `HasChecksum` says which.

## A directed query's target is one callsign

- APRS12c §15: a directed query is `?APRSx` with, for some types, the callsign it asks about.

**Decision:** text after the query type that is longer than 9 characters or contains spaces is not a target, so the message is a plain text message (with an `Info` diagnostic), not a query. A bot's help text that begins `?APRSM for the last 10...` is the case that found this.

## Where Packet.Aprs and Ham::APRS::FAP deliberately differ

Found by comparing decodes of the same 33,000 APRS-IS packets (see [`aprs-validation.md`](aprs-validation.md)):

| Case | FAP | Here | Why |
|---|---|---|---|
| `/3517.73N/...` (a `/` report with no timestamp) | skips 7 characters and decodes garbage (61.97N) | decodes the position that follows the DTI | a timestamp starts with 6 digits, so a position straight after the DTI is unambiguous |
| `...wHP1000` or a comment containing `ESP32` after weather data | reads `P100` / `P32` as rain since midnight | stops weather fields at the first non-field | weather fields are a contiguous run (APRS12c §12) |
| compressed weather wind in cs | not decoded | decoded as wind direction and speed | APRS12c §12 compressed weather format |
| `!DAO!` in a weather report | not applied | applied | the DAO belongs to the position, not the comment |
| `rejAR}` | rejected ID `AR}` | rejected ID `AR`, reply-ack `""` | reply-ack format (APRS12c §14) |
| PHGR with rate `0` (`PHG01000/`) | accepted | plain PHG; `0/` stays in the comment | the rate is 1-9 then A-Z |
| Mic-E comment | keeps device prefix and suffix | lifts them into `TypeCode` / `DeviceSuffix` | APRS12c §10 says applications should remove them |
| voice frequency | stays in the comment | lifted into `Frequency` | APRS12c §18 |

## Errata in APRS12c's own examples

These examples in the spec cannot be what was meant; the tests use the corrected form.

- **§21 Overlays with Symbols**: `=BL!!<*e7>7P[` is 12 characters after the DTI; a compressed position is 13. The latitude `5L!!` has lost its `5`: `=B5L!!<*e7>7P[`.
- **§12 Complete Weather Report, compressed, no timestamp**: `=/5L!!<*e7>_7P[g005...` has a stray `>` before the `_` symbol code. As printed it decodes as a car (`/>`) with course 248 and a comment. The timestamped example beside it (`@092345z/5L!!<*e7_7P[g005...`) is right.
- **§11 Area Objects**: `;FLIGHTPTH*4903.50N\07201.75W l 610/310{100}` has no timestamp, though the same chapter says an object always has one. The spaces around `l` are typesetting, not data (a raw text extraction of the PDF gives `Wl610/310`). `AllowObjectWithoutTimestamp` exists partly for this.
- **§12 Complete Weather Report with Object**: `;BRENDA   *4903.50N/07201.75W_220/004g005t077...` likewise has no timestamp.
- **§8 examples** use `…` to mean "more fields here", not literal characters.
