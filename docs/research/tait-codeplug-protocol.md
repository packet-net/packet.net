# Tait TM8100/TM8200 codeplug programming protocol (reverse-engineered)

The proprietary serial protocol the Windows CPS (Tait Programming Application) uses to read and write the Tait TM8100/TM8200 codeplug, reverse-engineered from serial captures and validated end to end against real hardware. This is milestone M1 of [`tait-codeplug-programming-brief.md`](tait-codeplug-programming-brief.md): enough of the protocol to do a safe read then write without the CPS. The Linux implementation is [`tools/Packet.Tait.Codeplug`](../../tools/Packet.Tait.Codeplug).

## How it was captured

Free Serial Analyzer (HHD) captured the CPS talking to a TM8110 (s/n 19925328) over its CP2102 USB serial dongle, for three operations: interrogate (basic info), read, and write. Each was exported both directions and as one-direction (read-filter / write-filter) IRP dumps. The exports are the Windows IRP stream: `IRP_MJ_WRITE` (major function 4) buffers carry the PC to radio bytes, `IRP_MJ_READ` (3) completions carry one radio to PC byte each. The raw captures are not committed (see the `.gitignore`); this document is the durable record.

## The record format

On the wire and in the saved `.m8p` a record is the ASCII-hex string `<addr:4 hex><len:2 hex><data:2*len hex><checksum:2 hex>`. The 16-bit address is not a flat byte offset: it decodes as `(section << 8) | index`, so the codeplug is a set of numbered sections, each a run of indexed records. This radio's codeplug is 44 sections; section 0x02 (72 records) is the per-channel table, section 0x4A (24 records, high entropy) is a signature or calibration blob, section 0x01 is global settings.

The checksum is the CCDI family (modulo-256 sum, two's-complemented), computed over the decoded record bytes (address hi, address lo, length, then data). The property that falls out: every byte of a complete decoded record, checksum included, sums to 0 modulo 256. Example: `2700025E0079` decodes to `27 00 02 5E 00` with checksum `79`, and `0x27 + 0x00 + 0x02 + 0x5E + 0x00 = 0x87`, `(-0x87) & 0xFF = 0x79`.

## The command set

Everything is ASCII, CR-terminated (except the bare `^` and `#`), strictly lock-step: every command is answered by a single `>` prompt before the next is sent. `>` is the programming-mode prompt (the equivalent of CCDI's `.`). `{Cnn}` are bracketed control acks.

| cmd | example | radio reply | meaning |
|-----|---------|-------------|---------|
| `^` | `^` | `v` | reset / exit programming mode (also the session teardown) |
| `#` | `#` | `>` | enter programming mode |
| `ld` | `ld` | `{C05}` | login / handshake |
| `d` | `d00` | `{C01}` | database / mode select |
| `p` | `p00`, `p01` | `>` | page / bank select (silent ack) |
| `r` | `r00`, `r27` | records + `>` | read a section (arg = section number); radio streams that section's records |
| `b` | `b` | `>` | begin write block |
| `i` | `i53380146` | `>` | init / unlock write; the 4-byte arg is not yet mapped (issue #744) |
| `w` | `w010020030E...3A` | `>` | write one record (a `.m8p` line verbatim) |
| `e` | `e` | `>` | end / commit write block |

## Session flows

Interrogate (basic info): `ld d00 r00 p01 r27 p00 r2F`, then teardown.

Read: the interrogate preamble, then `p01` and an `r<section>` for every section, then teardown. The radio streams each section's records; parse the CR-delimited hex lines and ignore prompts and acks.

Write: preamble `ld d00 r00 p01 r27 p00 r2F p01 r22`, then the write block `b`, `i53380146`, one `w<record>` per record (each awaiting its `>`), `e`, then teardown `^`. Section 0 (the read-only model / firmware / serial identity) is never written, matching the CPS.

## Transport parameters

CP2102 dongle at 8N1. The session opens at 9600 then switches to 19200 for the transfer; which rate the boot handshake wants is unconfirmed, so the tool probes both within one connect window. Programming mode is entered by a boot-time latch: trigger the operation first, then power-cycle the radio, and it latches into programming mode as it boots with the tool already probing. A bad or interrupted codeplug write therefore never locks the radio out: catch it at the next boot in programming mode and rewrite a known-good image. No RF is involved; all work is over the data connector.

## Hardware validation

The Linux tool ran against the real radio (CP2102, 19200 8N1, boot-latch power-cycle):

- Interrogate returned `TMAB12-B100_0201` / `QMA1F_std_02.18.00.00` / serial `19925328`.
- A full read produced records that were byte-identical to the CPS `.m8p` for every writable record (the only differences were the read-only identity, which the CPS blanks to a stub in the file, and section 0x27).
- A same-image write (read a backup, write every writable record back, re-read) round-tripped 170 of 170 writable records byte-identical.

## Open questions

Two fields are addressed but not yet understood; both are tracked as issues and neither blocks a same-image round-trip.

The `i53380146` init argument (`53 38 01 46`, issue #744) is sent once before the write block. Its derivation (a write range, a size, or an unlock key) is unknown; the tool replays the captured value.

Section 0x27 (issue #745) does not round-trip: the radio holds `0x5E`, the CPS `.m8p` holds `0x5F`, and two CPS save-as of the same codeplug are byte-identical (so it is not a save counter), while a same-content hardware write left it unchanged (so it is not a per-write counter). The working theory is that 0x27 is a checksum over the whole codeplug including section 0, whose value flips because the radio's section-0 identity differs from the file's blanked stub. Reversing the checksum function so a patcher can recompute it is the remaining work before a single-field in-place patch is safe.

## Patching a field

A field change is a read, a `CodeplugFields` set, then a write. There is no whole-codeplug checksum (integrity is per-record), so a change perturbs exactly the one record the field lives in. Hardware-validated end to end: `patch ch0.bandwidth Wide` changed a single record's single bandwidth byte (`00` -> `02`), persisted across a power-cycle, and left every other record untouched; a restore returned the radio byte-identical to its start.

The write is the full codeplug (all 170 writable records), not just the changed record. A **single-record write block does not commit**: the radio acks a `b`/`i`/`w`/`e` block containing only the changed record but discards it (bench-confirmed, the codeplug read back unchanged). The `i53380146` init argument almost certainly encodes the full-codeplug scope, so a partial block is inconsistent and dropped; a working single-record patch is gated on decoding that argument (issue #744). Until then, patching writes the whole image, which is the validated path.

Read-back in the same session after a write is unreliable (the post-write read comes back malformed), so verify a write with a fresh read after a power-cycle rather than in-session.

The CLI exposes this as `patch <port> <field> <value>` (read, set the field, write the whole codeplug), which snapshots the pre-change codeplug to a backup file first. There is no raw whole-file write verb: the write path is only validated for specific database versions (the write init argument and field offsets are version-specific), so the underlying write method refuses a radio whose DB version is not in its validated set. Reading is unrestricted.

## Codeplug field map (DBVer 0094 / 0095)

Fields are bit-packed into record payloads. [`CodeplugFields`](../../tools/Packet.Tait.Codeplug/Fields/CodeplugFields.cs) exposes a typed, version-pinned view; each field below is pinned by a test and validated against a real radio's codeplug. Record 0x27 (a 12-bit field) carries the database version, which pins the map (the tool refuses an unmapped version).

**Channels** live in record type 0x05 as one contiguous LSB-first bit-stream, 181 bits per channel, physically split across a run of up to 32-byte records, so a channel can straddle a record boundary. Channel N's field at stream bit `N*181 + offset`:

| field | offset (bits) | width | encoding |
|-------|---------------|-------|----------|
| separate TX frequency | 0 | 1 | flag: TX differs from RX |
| TX frequency | 16 | 32 | unsigned Hz |
| RX frequency | 48 | 32 | unsigned Hz |
| bandwidth | 80 | 2 | 0 = 12.5 kHz, 1 = 20 kHz, 2 = 25 kHz |
| TX inhibit | 82 | 2 | 0 = None, 1 = Busy, 2 = Mute |
| squelch (RxBusyDetect) | 84 | 2 | 0 = Country, 1 = City, 2 = Hard |
| TX subaudible type | 86 | 2 | 0 = None, 1 = CTCSS, 2 = DCS |
| RX subaudible type | 88 | 2 | 0 = None, 1 = CTCSS, 2 = DCS |
| TX subaudible index | 90 | 8 | slot in the per-codeplug tone table (see below) |
| RX subaudible index | 98 | 8 | slot in the per-codeplug tone table (see below) |
| network | 106 | 3 | network reference (the CPS "Network" column), 0..7 |
| TX power | 109 | 3 | 0 = Off, 1 = VeryLow, 2 = Low, 3 = Medium, 4 = High |

The CPS greys a channel's TX Inhibit field when the channel's receive frequency is 0, so the field map refuses to set it there. Most other channel-form rules the manual documents depend on fields not mapped here (the frequency band, FCC-compliance flags, wideband licensing), so they are not enforced.

**Data / signalling** is record 0x09/0, a single LSB-first bit-stream packed in schema field order. This one record holds the whole CPS **Data** form (its General, Serial Communications, RF Modems, SDM and TOTAL Transparent Mode tabs). Three fields near the front are wider on disk than a naive read of the field table's length column suggests: the two flow-control character codes (XON and XOFF) are a byte each rather than a flag, and the unit-data-identity field is 7 bytes (56 bits). Those extra widths are what shift every field from the SDM-options field onward by a constant 63 bits. The whole logical record is 293 bits, but the **stored record is trimmed to 32 bytes (256 bits)**, so the trailing TOTAL fields past bit 245 (MTU size, validate-CRC, confirmed-mode retries/timeout, busy back-off, and the General tab's channel-access method) are not written and the CPS fills schema defaults for them.

Every offset below is validated against a real-radio CPS save, either a single-setting diff or by decoding a codeplug the CPS displays in full (a non-default like TOTAL destination ID = 0xFFFF confirms the tail offsets too).

| field | stream bit | width | encoding / meaning |
|-------|-----------|-------|--------------------|
| data options enabled | 0 | 1 | set alongside transparent-mode enable |
| XON character | 1 | 8 | flow-control byte (CPS shows it in hex; 0x11 = DC1) |
| XOFF character | 9 | 8 | flow-control byte (0x13 = DC3) |
| power-up state | 17 | 2 | 0 = Command, 1 = FFSK Transparent, 2 = THSD Transparent |
| unit data identity | 19 | 56 | eight 7-bit ASCII characters (the CPS "Unit Data Identity"; blank = all zero) |
| FFSK lead-in delay | 75 | 10 | 5 ms steps, 0..5100 ms |
| ignore subaudible on data | 85 | 1 | flag: modem not gated by CTCSS/DCS |
| SDM enabled | 86 | 1 | (the CPS also cascades the text-SDM flags at 155..157) |
| SDM auto-ack delay | 87 | 6 | count of 100 ms steps, 0..5000 ms |
| SDM wait-for-ack | 93 | 4 | count, 1..15 |
| command-mode (CCDI) baud | 97 | 3 | index into 1200..28800 |
| command-mode flow control | 100 | 2 | 0 = None, 1 = Software, 2 = Hardware |
| FFSK transparent (terminal) baud | 102 | 3 | index into 1200..28800 |
| FFSK transparent flow control | 105 | 2 | 0 = None, 1 = Software, 2 = Hardware |
| THSD (HSD) baud | 107 | 3 | index into 1200..28800 |
| THSD flow control | 110 | 2 | 0 = None, 1 = Software, 2 = Hardware |
| data / CCDI port | 112 | 2 | 0 = Mic, 1 = Aux, 2 = Internal Options |
| ignore escape sequence | 114 | 1 | flag: OFF for a raw transparent byte pipe |
| FFSK lead-out delay | 115 | 8 | 0..250 ms |
| THSD modem enabled | 123 | 1 | flag |
| THSD layer-2 protocol | 124 | 2 | 0 = None, 1 = Simple, 2 = TOTAL |
| FFSK tone blanking | 126 | 1 | flag (schema MuteOnFFSKReceiving) |
| THSD forward error correction | 127 | 1 | flag |
| THSD lead-in delay | 128 | 13 | 0..5000 ms |
| THSD lead-out delay | 141 | 8 | 0..250 ms |
| CCDI progress message enabled | 149 | 1 | flag: emit CCDI progress/result to host |
| output all selcall receptions | 150 | 1 | flag |
| CCDI SDM output enabled | 151 | 1 | flag: deliver received SDMs to the CCDI host |
| SDM caller ID (encode / decode) | 152 | 2 | the CPS keeps the two bits equal |
| wideband modem enabled | 154 | 1 | flag |
| text-SDM indicator | 155 | 1 | flag (cascaded on by SDM enable) |
| text-SDM auto-ack transmission | 156 | 1 | flag (cascaded on by SDM enable) |
| text-SDM auto-ack reception | 157 | 1 | flag (cascaded on by SDM enable) |
| check packet length | 158 | 1 | flag |
| SDM buffer overwrite | 159 | 1 | flag |
| maximum initial frame length | 160 | 1 | flag |
| UART write delay | 161 | 9 | 0..500 ms |
| CCDI SDM text-only | 170 | 1 | flag |
| FFSK (over-air modem) baud | 171 | 2 | 0 = 1200, 1 = 1200 (A75), 2 = 2400 |
| open monitor on dialled call | 173 | 1 | flag |
| THSD number of blocks (FEC) | 174 | 3 | 1..7 |
| CCDI mode allowed | 177 | 1 | flag: master gate for the CCDI command channel |
| Tx back-off time (min) | 178 | 9 | 0..500 ms |
| Tx back-off time (max) | 187 | 10 | 0..1000 ms |
| TOTAL service | 197 | 1 | 0 = Unconfirmed, 1 = Confirmed |
| TOTAL radio ID | 198 | 16 | 0..65535 |
| TOTAL system ID | 214 | 8 | 0..255 |
| TOTAL destination ID | 222 | 16 | 0..65535 (default 0xFFFF) |
| TOTAL link ID | 238 | 8 | 0..255 |

Fields past bit 245 (TOTAL MTU size, validate-CRC, confirmed-mode retries/timeout, busy back-off, channel-access method) are trimmed from the 32-byte stored record; the CPS fills their schema defaults. Setting them would require the record to grow. The CPS only un-greys those TOTAL controls when the RF-Modems **Layer 2 Protocol** is set to **TOTAL** (which needs THSD Modem Enabled), so a sample in that state is what would let the tail be mapped; none of the available codeplugs are, so those five plus the channel-access method are left unmapped.

The **unit data identity** is exposed: it is an eight-character 7-bit-ASCII field at bit 19 (the same packed encoding as the GPS dispatcher address below). The **GPS** and **Customer Data** tabs are separate records, covered next.

## GPS tab (record 0x45/0)

The Data form's GPS tab is its own record, an LSB-first bit-stream. Offsets validated against a GPS-enabled real-radio save (a non-default dispatcher address "12345678" pins the packed-ASCII field):

| field | stream bit | width | encoding / CPS control |
|-------|-----------|-------|------------------------|
| GPS serial port | 0 | 2 | 0 = Mic, 1 = Aux, 2 = Internal Options ("GPS Port") |
| GPS baud rate | 2 | 4 | index into 1200..28800 ("Baud Rate") |
| poll-response channel | 6 | 7 | 0..99 dedicated channel ("Channel") |
| callout interval | 13 | 6 | 5 s steps, 0..300 s ("Callout Interval") |
| max number of callouts | 19 | 8 | 0..250 ("Maximum Number of Callouts") |
| connection time-out | 30 | 8 | 20 s steps, 20..600 s ("Connection Time Out") |
| GPS lead-in delay | 38 | 8 | 5 ms steps, 0..1200 ms ("GPS Lead-In Delay") |
| send on emergency callout | 49 | 1 | flag ("Send Position on Emergency Callout") |
| dispatcher address | 50 | 56 | eight 7-bit ASCII characters ("Dispatcher Address", default "00000000") |
| GPS position reporting enabled | 136 | 1 | flag ("GPS Position Reporting Enabled"; the CPS needs SDM on to set it) |
| poll-response channel type | 137 | 1 | 0 = Current, 1 = Dedicated ("Poll Response Channel Type") |
| poll-response delay time | 138 | 9 | 10 ms steps, 0..5000 ms ("Poll Response Delay Time") |

(The three per-network "send position" fields occupy bits 106..135; the caller-ID, PTT-suppression, NMEA and SDM-ack GPS fields sit past bit 146 and are not on the main GPS form, so they are not mapped.)

Where the CPS's own help documents an input rule, the field map enforces the same rule (sourced from the programming manual, not the schema's min/max columns, which do not match the CPS's real input ranges). For the GPS fields: position reporting can only be enabled when SDM is on; the dispatcher address is a radio identity (up to eight characters from A-Z, 0-9, or the wildcard `*`, the same as the unit data identity); and the dedicated poll-response channel must be None or a channel that exists in the codeplug. The numeric fields carry their manual-stated ranges and steps.

## Customer Data tab (records 0x4C/0 and 0x4D/n)

Plain bytes. Record 0x4C/0 is eight bytes: four leading pad bytes then the four global bytes (the CPS "Global Byte 1..4"). Each network row is record 0x4D at that network's index (network 1 = 0x4D/0), four bytes = the CPS "Byte 1..4".

Transparent-mode enable additionally sets bit 0 (data options) plus bits 158 and 160; SDM enable sets bit 86 and cascades bits 155..157; these composite writes reproduce the CPS byte-for-byte.

These are the codeplug prerequisites the `Packet.Radio.Tait` runtime features depend on. CCDI (RSSI, DCD / channel-busy, PTT, status queries) needs **CCDI mode allowed** on and a command-capable power-up state and CCDI port/baud. SDM reception at a host needs **SDM enabled** plus **CCDI SDM output enabled**. The FFSK Transparent byte pipe needs **transparent mode enabled**, **ignore escape sequence OFF** (the classic wedge), matching **FFSK over-air baud** at both ends, and usually **ignore subaudible on data**. Every offset in the table is fixture-confirmed by a single-setting CPS save, and offsets between two confirmed points are bit-exact by the contiguous packing. Power-up state pins the one subtlety in the leading region: it lands at bit 17, not the bit 3 a naive field-order count gives, because the two flow-control character fields ahead of it (the XON and XOFF character codes, a byte each) are not single flags. The identity field's on-disk width is likewise larger than the field table's length column, which is what produces the constant shift for the fields past it.

Each of these is a control on the CPS **Data** form. By tab:

| CPS control | tab / group | field here |
|-------------|-------------|------------|
| Powerup State | General > Common Data Parameters | power-up state |
| CCDI Mode Allowed | General > Command Mode | CCDI mode allowed |
| Output Progress Messages | General > Command Mode | CCDI progress message |
| Output SDMs Automatically | General > Command Mode | CCDI SDM output |
| CCDI SDM Text Only | General > Command Mode | CCDI SDM text-only |
| Transparent Mode Enabled | General > Transparent Mode | transparent mode enabled |
| Ignore Escape Sequence | General > Transparent Mode | ignore escape sequence |
| THSD Modem Enabled | General > Transparent Mode | THSD modem enabled |
| Baud Rate (Command Mode column) | Serial Communications | command-mode (CCDI) baud |
| Baud Rate (FFSK Transparent Mode column) | Serial Communications | FFSK transparent (terminal) baud |
| Baud Rate (THSD Transparent Mode column) | Serial Communications | THSD (HSD) baud |
| Data Port | Serial Communications | data / CCDI port |
| Ignore DCS/CTCSS | RF Modems > FFSK Modem | ignore subaudible on data |
| FFSK Baud Rate | RF Modems > FFSK Modem | FFSK (over-air modem) baud |
| SDM Enabled | SDM > All SDMs | SDM enabled |
| Indicate When SDM Received | SDM > Text SDMs Only | text-SDM indicator |
| Transmit SDM Auto Acknowledgement | SDM > Text SDMs Only | text-SDM auto-ack transmission |
| Receive SDM Auto Acknowledgement | SDM > Text SDMs Only | text-SDM auto-ack reception |
| SDM Auto Acknowledge Delay | SDM > Text SDMs Only | SDM auto-ack delay |
| SDM Wait For Acknowledgement Time | SDM > Text SDMs Only | SDM wait-for-ack |
| Open Monitor On Dialled Call | General > Command Mode | open monitor on dialled call |
| Output All Selcall Receptions | General > Command Mode | output all selcall receptions |
| Maximum Initial Frame Length | General > Transparent Mode | maximum initial frame length |
| UART Write Delay | General > Transparent Mode | UART write delay |
| Tx Back-off Time (Min) / (Max) | General > Transparent Mode | Tx back-off time min / max |
| XON Character / XOFF Character | Serial Communications | XON / XOFF character |
| Flow Control (three columns) | Serial Communications | command / FFSK / THSD flow control |
| Check Packet Length | RF Modems > FFSK Modem | check packet length |
| FFSK Tone Blanking | RF Modems > FFSK Modem | FFSK tone blanking |
| FFSK Lead-In Delay / Lead-Out Delay | RF Modems > FFSK Modem | FFSK lead-in / lead-out delay |
| Wide Band Modem Enabled | RF Modems > THSD Modem | wideband modem enabled |
| Layer 2 Protocol | RF Modems > THSD Modem | THSD layer-2 protocol |
| Forward Error Correction (FEC) | RF Modems > THSD Modem | THSD forward error correction |
| Number of Blocks | RF Modems > THSD Modem | THSD number of blocks |
| THSD Lead-In Delay / Lead-Out Delay | RF Modems > THSD Modem | THSD lead-in / lead-out delay |
| SDM Buffer Overwrite | SDM > All SDMs | SDM buffer overwrite |
| SDM Caller ID | SDM > Text SDMs Only | SDM caller ID |
| TOTAL Service / Radio ID / System ID / Destination ID / Link ID | TOTAL Transparent Mode | TOTAL service / radio ID / system ID / destination ID / link ID |

The field map also mirrors the CPS's "this field is only available if ..." rules: a setter whose enabling condition is not met throws, so the tool cannot produce a field-state the greyed UI would not let you create (you enable the parent first, exactly as in the CPS). For example THSD Modem Enabled needs Transparent Mode on; the FFSK / THSD baud, flow-control, and delay fields need their respective mode on; the FFSK / THSD power-up-state options need the matching mode on; the SDM auto-ack fields need SDM plus the matching transmit/receive auto-ack; the TOTAL fields need Layer 2 Protocol = TOTAL; the GPS dedicated channel needs the Poll Response Channel Type set to Dedicated; and the Tx back-off maximum must exceed its minimum (or both 0 to disable). These are the rules the programming manual documents; the many purely-cosmetic UI states are not enforced.

Verified end to end by loading a tool-written codeplug, every item-9 field set to a distinctive value, into the CPS and reading back every control on all five Data tabs. Two things to note. First, an in-CPS UI enable-rule: **SDM Wait For Acknowledgement Time** is only editable when **Receive SDM Auto Acknowledgement** is on; the stored value is written regardless, the CPS just greys the box when its precondition is off. Second, the CPS shows the character fields and the TOTAL IDs in hexadecimal (so **XON Character** reads `11` for the stored byte 0x11 = DC1); the tool prints and accepts those with a `0x` prefix. The two CPS controls that are not item-9 fields are **SDM Format** (a derived field) and the Hardware Flow Control **CTS / RTS** (which live in the Programmable-I/O item), so they are out of scope for this record.

**Audio tap** is record 0x3B/0:

| field | encoding |
|-------|----------|
| RX tap-out node | byte 3 low nibble (R1=1, R2=2, R4=4, R5=5, R7=7, R10=10) |
| EPTT1 tap-in node | byte 11 = 0x20 \| (node << 1) (T3=3, T5=5, T8=8, T13=13) |
| RX tap-out unmute | byte 4 bits[3:1] |
| RX tap-out inverted | byte 4 bit 0x40 |
| EPTT1 tap-in inverted | byte 14 bit 0x08 |

The full audio I/O block (record 0x3B, item 59) is a multi-row table with a trimmed on-disk encoding (the stored record is smaller than the schema's per-entry size), and the row Type/Unmute enums are not in the recovered schema, so the individual fields beyond those above are not all mapped. What is provided is the audio routing the amateur-packet community has settled on, as a validated preset: `set <file.m8p> audio packet-defaults` writes the known-good audio-IO record (Rx tap-out R1 type D-Split unmute Except-on-PTT; EPTT1 tap-in T13; Mic PTT and EPTT2 default; inversion off) and its item count. This is a community convention, not a CPS feature (the CPS has no notion of packet radio); the bytes were validated to reproduce a CPS save of that manual configuration. The audio-IO block is self-contained, so this applies cleanly on top of any codeplug.

**Subaudible tones** are two-level: a channel's subaudible index is a slot in a small per-codeplug table (populated in insertion order), not a fixed tone number. The tones themselves are stored in their own records: **CTCSS in record type 0x32** as 12-bit entries holding the frequency in tenths of a Hz (e.g. `670` = 67.0 Hz), and **DCS in record type 0x3D** as 9-bit entries holding the code as its octal value (e.g. `15` = octal 017). So channel N's tone is `CtcssTable[index]` or `DcsTable[index]`; `CodeplugFields` resolves it (`get ch0.rxtone` -> `CTCSS 67.0` / `DCS 017` / `None`).

Writing a tone (`set ch0.rxtone "CTCSS 88.5"`) finds the tone in the table, or reuses a free (zero) slot, or appends a new entry; appending grows the table record and bumps that item's record count in the item index (record type 0x01). Three records change and nothing else, which reproduces the CPS's own single-tone saves byte-for-byte (there is no whole-file checksum to keep in sync). The item index is a table of 7-byte entries per codeplug item (`ItemID`, `RecordSizeInBits`, `CurrentRecordCount`, ...); a record count there is the number of logical entries an item holds.

The CLI reads and writes these by name: `get <file.m8p> [field]`, `set <file.m8p> <field> <value>` (e.g. `set base.m8p ch0.bandwidth Wide`). A `set` rewrites only the one record the field lives in.
