# Packet.Tait.Codeplug

A Tait TM8100/TM8200 codeplug library (and a thin CLI front-end, `Packet.Tait.Codeplug.Cli`),
reverse-engineered from Free Serial Analyzer captures of the Windows CPS. It reads and writes the
codeplug over the serial programming interface without the CPS, exposes a typed, version-pinned field
map for the whole CPS Data form and the channel table, and applies the Packet.NET (PDN) upgrade
profiles. The protocol was milestone **M1** of
[`docs/research/tait-codeplug-programming-brief.md`](../../docs/research/tait-codeplug-programming-brief.md);
the read + write path is hardware-validated (see `docs/research/tait-codeplug-protocol.md`). The
library is the piece the node can later consume to program an attached radio.

The full protocol write-up is in `tait-programming-research/FINDINGS.md` (outside the repo, with
the captures). Short version:

- ASCII-hex, line-oriented, CR-terminated, strictly lock-step (every command gets one `>` prompt
  before the next).
- Records share the `.m8p` framing `<addr:4hex><len:2hex><data><checksum:2hex>`; checksum is the
  CCDI-family negated sum over the decoded bytes (whole record sums to 0 mod 256). `addr` is
  `(section << 8) | index`.
- Session: `^` (reset -> `v`), `#` (enter programming -> `>`), `ld` -> `{C05}`, `d00` -> `{C01}`.
  Read a section: `r<section>`. Write: `b`, `i<arg>`, a run of `w<record>`, `e`. Teardown: `^`.
- Baud opens at 9600, switches to 19200 for the transfer.

## What is here

- `CodeplugChecksum` / `CodeplugRecord` / `CodeplugImage` - the record model, checksum, and .m8p
  load/save + section map. Fully offline and unit-tested.
- `Fields/` - the typed, version-pinned field map (`CodeplugFields`, `CodeplugEnums`,
  `ChannelBits`): channels (frequency, bandwidth, power, split-TX, CTCSS/DCS), the whole CPS **Data**
  form (its General, Serial Communications, RF Modems, SDM and TOTAL Transparent Mode tabs live in the
  one data/signalling record; the GPS and Customer Data tabs are separate records; plus the unit data
  identity), and audio taps. See `docs/research/tait-codeplug-protocol.md` for the map. Each field is
  pinned by a test.
- `FieldConsole` - name/value access used by the `dump`/`get`/`set` CLI verbs.
- `CodeplugFields.ApplyPdnBasic()` / `ApplyPdnExtra()` - the two PDN upgrade profiles (see below).
- `ISerialLine` / `SerialPortLine` - the byte seam (mirrors `Packet.Radio.Tait.ISerialIo`); tests
  substitute a scripted mock radio.
- `TaitProgrammer` - the lock-step transport state machine (connect, interrogate, read, write).

The CLI (`tools/Packet.Tait.Codeplug.Cli`) is a thin front-end over this library.

## CLI

```
# offline (no radio)
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- parse <file.m8p>              # verify checksums + section map
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- dump  <file.m8p>              # decode every mapped field
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- get   <file.m8p> [field]      # read one field (or all)
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- set   <file.m8p> <field> <v>  # set one field + save
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- set   <file.m8p> profile <name>  # apply a PDN profile

# hardware (radio latched into programming mode on <port>: power-cycle as you trigger)
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- version <port> [--baud N]
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- read    <port> <out.m8p> [--baud N]
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- patch   <port> <field> <value>   # live-set one field (backs up first)
dotnet run --project tools/Packet.Tait.Codeplug.Cli -- patch   <port> profile <name>    # live-apply a PDN profile
```

## PDN upgrade profiles

Two composable patches that *upgrade a radio to the Packet.NET feature set* without touching its RF
config (channels, frequencies, power), so they layer safely onto a radio already provisioned for its
environment. They change only the data record (0x09). For a radio arriving from a foreign application,
prefer a clean flash of a full codeplug first, then apply a profile.

- **`pdn-basic`** enables the CCDI command channel that carries `Packet.Radio.Tait`'s telemetry and
  control: averaged/instantaneous RSSI, forward/reverse power, PA temperature, status/identity,
  transmitter keying, and the PROGRESS stream for carrier-sense (DCD) and external-PTT edges. It sets
  CCDI-mode-allowed on, power-up state to Command (so the radio is always CCDI-reachable), progress
  messages on, and the command baud to 28800.
- **`pdn-extra`** includes `pdn-basic` and adds the TNC-less internal FFSK packet modem plus the SDM
  side channel used for mode signalling: transparent mode on, **ignore-escape-sequence off** (so the
  transport can escape back to command mode - without this the radio wedges), ignore-subaudible on the
  data path, the transparent terminal baud (28800) and over-air FFSK baud (2400), and SDM + CCDI SDM
  output. The over-air baud must match at both ends; adjust the bauds and the data port for your setup.

`patch` reads the codeplug, sets the field, backs up the pre-change image to a file, and writes the
whole codeplug (a single-record write is not committed by the radio - #744). It is hardware-validated.
There is no raw whole-file write verb: the underlying write method refuses a radio whose database
version is not in its validated set (the write init argument and field offsets are version-specific).
Reading is unrestricted.

## Status and safety

The read + write path is hardware-validated against a real TM8100 (a same-image write round-tripped
every writable record byte-identical), and the field map is validated field-by-field against
real-radio CPS saves. Field writes are byte-identical to the CPS's own saves. Safety rails:

1. `patch` auto-snapshots the current codeplug to a backup file before writing; keep it.
2. Codeplug region only - this never writes firmware.
3. Version-pin: the write path refuses a radio whose database version is not in its validated set
   (currently 0094 / 0095); the field offsets are version-specific.
4. The field map enforces the CPS's own input rules (value ranges, character sets, and the "only
   available if ..." availability dependencies), so the tool will not write a state the CPS rejects.
5. Bench on a sacrificial radio first, and re-read (after a power-cycle) to verify a write.

Open unknowns (non-blocking): the `i53380146` init argument (a DBVer constant, #744) and the section
0x27 database-version record (#745).
