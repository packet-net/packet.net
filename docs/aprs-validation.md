# Packet.Aprs validation

Five independent checks, from the spec outward.

## 1. Every example in the spec

`tests/Packet.Aprs.Tests/SpecExamples` decodes every example packet printed in APRS12c, and the real packets quoted in *Understanding APRS Packets*, and asserts the values the documents give. Canonical examples must also re-encode byte for byte. Four examples in the spec itself are wrong; see [`aprs-spec-interpretations.md`](aprs-spec-interpretations.md#errata-in-aprs12cs-own-examples).

## 2. Randomised round trips

`tests/Packet.Aprs.Tests/Properties` (FsCheck) generates thousands of valid reports per run: uncompressed and compressed positions with every extension, Mic-E, and messages. For each it checks that encode, decode and re-encode give identical bytes and equal data. It also feeds random bytes and random header text to the decoder, which must never throw anything but `AprsFormatException` for an unusable header. These tests found three bugs the example tests couldn't have:

- a DAO-shaped run inside base-91 telemetry;
- a Mic-E altitude that reads back as a device type code;
- a frequency field parser that leaked state.

## 3. Fuzzing

`tools/Packet.Fuzz`'s `aprs` target (nightly in `fuzz.yml`, one million inputs) pushes random and near-valid bytes through the codec as an information field, a TNC2 line and an AX.25 frame, under both presets. Decoding must not throw, and anything that decodes with no warning must re-encode to data that decodes back equal. About one input in seven gets that far. See `tools/Packet.Fuzz/FINDINGS.md`.

## 4. The live APRS-IS feed

`tools/Packet.Aprs.Corpus` (`aprs-corpus collect`) captures the receive-only full feed; `stats` decodes all of it. On 2026-09-26, after about ten hours of collection, 3,078,983 packets:

| | |
|---|---|
| decoder exceptions | 0 |
| unparseable headers | 0 |
| accepted by `Strict` | 88.1% |
| spec-clean packets that re-encode to identical data | 100% (2,668,301 of 2,668,301) |
| spec-clean packets whose sender was already byte-canonical | 65.7% |
| spec-clean packets the encoder refuses | 2,905 (0.11%): all but one are message or bulletin text over 67 characters, which APRS12c tells receivers to accept and senders not to write |

The byte-canonical figure is about senders, not the library. Non-canonical but legal choices are normalised on re-encode: filler bytes in compressed positions, delimiters, weather field order, telemetry padding, and the Mic-E speed encoding. The exact received bytes are always in `AprsPacket.Information`.

The first run (508,679 packets, 2026-09-25) found one bug, an object with a garbled timestamp read as a made-up position. The ten-hour run found no crash and no clean packet that failed to round-trip, but it did show where "decodes cleanly" and "can be written back" disagreed:

- **The decoder was too quiet** about four things the spec forbids, each now a named flag with a paired test: a `{` in message text, a space or `:` inside an addressee, `BLN` + letter + group name, and free text after `<` (see [`strict-vs-pragmatic-audit.md`](strict-vs-pragmatic-audit.md#packetaprs)). A query whose "target" is a sentence now decodes as a plain message, and `PARM.` / `UNIT.` for more than 13 channels likewise.
- **The encoder was too strict** in two places: object names may start with spaces, and a comment that would read as something else only if written first (a frequency after PHG or an altitude) is now confirmed by decoding rather than refused on sight.
- **One real error was being let through**: a raw NMEA sentence whose checksum doesn't match is corrupt, and is now rejected in both modes.

`aprs-corpus curate` picks a few packets of every distinct shape (data type, diagnostics, optional elements present) into `tests/Packet.Aprs.Tests/Corpus/samples.txt`; `CorpusRegressionTests` decodes them and compares with an approved snapshot, so any change in how real traffic decodes shows up as a reviewed diff.

## 5. Against Ham::APRS::FAP

FAP is the parser behind aprs.fi. `tools/Packet.Aprs.Corpus/fap/fap-decode.pl` decodes a sample with FAP, `aprs-corpus dump` decodes it here, and `tools/Packet.Aprs.Corpus/fap/compare-fap.py` compares field by field (converting FAP's metric units).

Results for about 33,000 packets:

| Field | Compared | Agree |
|---|---|---|
| latitude / longitude | 24,748 | 99.996% |
| symbol | 24,748 | 100% |
| course, speed | 24,748 | 99.94% |
| altitude | 24,748 | 99.98% |
| object / item name, alive | 3,587 | 99.9% / 100% |
| message addressee, text, ID | 214-270 | 100% |
| telemetry values | 1,879 | 100% |
| weather fields | 3,507 | 97.8-99.8% |

Every remaining disagreement was inspected. Each is one of three things:

- **A FAP defect.** Examples: a missing timestamp misread; rain values found inside a unit name or comment; compressed weather wind not decoded.
- **A deliberate presentation difference.** Examples: frequencies and Mic-E device codes lifted out of the comment.
- **A named tolerance** that FAP doesn't have.

The cases are listed in [`aprs-spec-interpretations.md`](aprs-spec-interpretations.md#where-packetaprs-and-hamaprsfap-deliberately-differ).

To reproduce:

```sh
dotnet run --project tools/Packet.Aprs.Corpus -c Release -- collect --out-dir ~/aprs-corpus
dotnet run --project tools/Packet.Aprs.Corpus -c Release -- stats ~/aprs-corpus
dotnet run --project tools/Packet.Aprs.Corpus -c Release -- dump lines.bin ours.jsonl
FAP_LIB=path/to/perl-aprs-fap-lib perl tools/Packet.Aprs.Corpus/fap/fap-decode.pl < lines.bin > fap.jsonl
python3 tools/Packet.Aprs.Corpus/fap/compare-fap.py lines.bin ours.jsonl fap.jsonl
```
