# Packet.Tait.Codeplug (spike)

A Linux "codeplug patcher" transport for the Tait TM8100/TM8200, reverse-engineered from Free
Serial Analyzer captures of the Windows CPS. This is milestone **M1** of
[`docs/research/tait-codeplug-programming-brief.md`](../../docs/research/tait-codeplug-programming-brief.md):
enough of the proprietary programming protocol to do a safe read then write, no CPS.

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
  `ChannelBits`): channels (frequency, bandwidth, power, split-TX), the data/signalling block
  (SDM, THSD, transparent mode, data port, FFSK baud), and audio taps. See
  `docs/research/tait-codeplug-protocol.md` for the map. Each field is pinned by a test.
- `FieldConsole` - name/value access used by the `dump`/`get`/`set` CLI verbs.
- `ISerialLine` / `SerialPortLine` - the byte seam (mirrors `Packet.Radio.Tait.ISerialIo`); tests
  substitute a scripted mock radio.
- `TaitProgrammer` - the lock-step transport state machine (connect, interrogate, read, write).
- `Program.cs` - the CLI.

## CLI

```
# offline (no radio)
dotnet run --project tools/Packet.Tait.Codeplug -- parse <file.m8p>              # verify checksums + section map
dotnet run --project tools/Packet.Tait.Codeplug -- dump  <file.m8p>              # decode every mapped field
dotnet run --project tools/Packet.Tait.Codeplug -- get   <file.m8p> [field]      # read one field (or all)
dotnet run --project tools/Packet.Tait.Codeplug -- set   <file.m8p> <field> <v>  # set one field + save

# hardware (radio latched into programming mode on <port>: power-cycle as you trigger)
dotnet run --project tools/Packet.Tait.Codeplug -- version <port> [--baud N]
dotnet run --project tools/Packet.Tait.Codeplug -- read    <port> <out.m8p> [--baud N]
dotnet run --project tools/Packet.Tait.Codeplug -- write   <port> <in.m8p>  [--baud N]
dotnet run --project tools/Packet.Tait.Codeplug -- patch   <port> <field> <value> [--restore]  # live-set one field (writes only the changed record)
```

The single-record `patch` wire path is unit-tested but not yet bench-validated (the full same-image
write is, from the M1 session); validate it on a sacrificial radio before trusting it.

## Status and safety

Offline model + the transport state machine are validated against the real captures (the
`TaitProgrammer` tests replay the captured interrogate and decode the identity with no hardware).
The hardware verbs are **not yet bench-verified** - the read/write choreography is faithful to the
captures but has not been driven against a radio. Golden rules before you do:

1. `write` auto-snapshots the current codeplug first; keep that backup.
2. Codeplug region only - this never writes firmware.
3. Version-pin: the field-offset map (M2, not yet built) is valid only for one DBVer.
4. Bench on a sacrificial radio first, and re-read to verify after any write.

Open unknowns (non-blocking): the `i53380146` init argument, section 0x27 semantics
(write-counter vs rolling checksum), the `{Cnn}` codes, and the `p00`/`p01` page effect.
