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

## In-place patching

The CPS reads and writes the entire map, but the protocol is record-addressed: a write block can carry just the record(s) that changed rather than all 170. The library models records individually, so a single-field patch is mechanically a matter of sending `b`, `i`, the changed record, `e`. The one caveat is section 0x27: if the radio validates a whole-codeplug checksum on commit, a partial write needs 0x27 recomputed over the resulting image (see the open questions).
