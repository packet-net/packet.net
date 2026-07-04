# Tait TM8100/TM8200 codeplug programming — spike brief (fresh-session handoff)

*Written 2026-07-04 as a self-contained handoff. If you are a fresh Claude session picking
this up: read this document and `CLAUDE.md` first, then the bench-rig memory
(`~/.claude/projects/-home-tf-src-packet-net/memory/tait-ninotnc-bench-rig.md`) and the
prior spike (`docs/research/tait-ccdi-spike.md`). Then pick your entry point from §5 by what
Tom has captured so far, and confirm with him before touching a radio.*

---

## 0. One-line mission

Build a **Linux "codeplug patcher"** for the Tait TM8100/TM8200 that can change a small set of
persistent configuration fields **without the Windows CPS** (Tait Programming Application), by
reverse-engineering just enough of the proprietary programming protocol to do a safe
**read → patch-known-bytes → recompute-checksum → write**. This is explicitly **not** a full
CPS reimplementation.

## 1. Scope — what's in and what's firmly out

**In scope (all are codeplug fields):**
- Enable/disable **SDM** reception (the field Tom toggled by hand this project).
- Enable/disable **FFSK Transparent** / data operation.
- **RX/TX audio tap points** (e.g. the R5→R2 RX-tap change that unblocked all direct-FSK modes).
- Per-channel **frequency, bandwidth (narrow/wide), and TX power *level*** (Hi/Lo/etc. selection).

**Out of scope — do not touch, ever:**
- **Firmware.** Interrupting a firmware flash hard-bricks the radio (dealer return, ~$350). The
  patcher only ever writes the codeplug region. A separate, already-built NinoTNC dsPIC flasher
  exists (`src/Packet.Kiss.NinoTnc/Firmware/BootloaderNinoTncFirmwareFlasher.cs`) — different
  silicon, not reusable here, and not needed.
- **PA / RSSI / deviation *calibration*.** That lives in a **separate tuning file** written by the
  Tait *Calibration Application* (a different protocol/image). Channel power *level* is codeplug
  (in scope); power *calibration* is the tuning file (out of scope).

**Full CPS reimplementation is out of scope** — it would require decoding the entire
per-database-version schema. The patcher preserves every byte it doesn't understand.

## 2. Why this is safe enough to attempt (the single most important finding)

Programming mode is entered by a **boot-time handshake** — the community-documented recovery is
*"power the radio off, trigger the read or write in the software, then immediately power on."* The
radio latches into programming mode **before the codeplug is loaded/applied**. Consequence: a bad
or invalid codeplug write **does not lock you out** — you catch the radio at boot in programming
mode and re-write a known-good image. This is the standard recovery path and the reason a patcher
is low-risk.

**Golden rules (non-negotiable):**
1. **Always read and save a full pre-write codeplug image before any write.** That backup is your
   recovery.
2. **Never write the firmware region.** Codeplug only.
3. **Version-pin.** Field offsets are keyed to the radio's *database version* (below). Refuse to
   patch a database version the tool hasn't been mapped against.
4. **Bench on a sacrificial radio first.** Tom's units are cheap and expendable for this — use that
   budget; prove read/write round-trips before trusting a patch.
5. Programming is over the serial/data connector with **no RF** — the rig's 2 W-attenuator limit
   and "never above VeryLow" rules are irrelevant to this work (no transmit).

## 3. Essential background

**CCDI ≠ programming.** Everything the packet.net Tait driver already does (RSSI, DCD, SDM, CCR,
identity) is *runtime control* over CCDI, and CCDI **cannot write the codeplug** — confirmed by the
CCDI manual (there is no PROGRAM / database-write / enter-programming-mode command). The programming
protocol is separate, proprietary, and **publicly undocumented with essentially zero
reverse-engineering prior art** (see §9). This is a capture-and-diff-from-scratch project.

**The bench rig** (see the bench-rig memory for the live port map — device paths renumber, so
identify by CCDI serial): 2× **Tait TM8110**, model **TMAB12-B100**, **CCDI 03.02**, firmware
**QMA1F_std_02.18.00.00**, serials **19925328** and **19925369**. CCDI currently on the mic/data
(front RJ45) connector at **28800 8N1** via CP2102 USB dongles. Radio **channel 0 = narrow, channel
1 = wide**. The radios report a **database version** via CCDI `RADIO_VERSIONS` record **02** — read
it first; the patcher's offset map is valid only for that version.

**Reusable code.** `src/Packet.Radio.Tait/` already has the serial plumbing you may want: an
`ISerialIo` seam, CCDI framing/checksum (`Ccdi/`), `TaitCcdiRadio` (to read the DB version + identity
over CCDI before/after programming), and `TaitRadioPortDiscovery` (identify a radio by CCDI serial).
The CCDI channel is the natural way to (a) read the DB version to select the offset map and (b)
verify a patch landed by reading back the affected runtime behaviour.

**Open question to resolve on the bench:** the serial *line levels* for programming mode. The
community is split — some insist programming needs **inverted 3.3 V TTL** (genuine FTDI with
Invert-TXD/RXD, or a signal-inverting board); others report the line is RS-232-tolerant so a plain
adapter works. CCDI already works over the CP2102 at 28800, but programming-mode entry may have
different requirements. Confirm empirically; get the connector pinout from the service manual
(MMA-00005, §9).

## 4. What is known / suspected about the protocol (all to be confirmed by capture)

- **Mode entry:** proprietary power-on handshake (the "off → trigger → on" latch), NOT a CCDI command.
- **Baud:** the programming-session baud is **not documented** — establish it from the capture (CCDI
  itself runs 1200–115200 configurable; programming may fix or negotiate its own rate at entry).
- **Framing:** presumably **binary block transfer** (not CCDI's bracketed ASCII) — confirm.
- **Read:** the CPS "Read/Interrogate the radio" streams the whole codeplug back — capture reveals
  block size, addressing, and how the transfer terminates.
- **Write:** the CPS "Program the radio" blocks the image back with (almost certainly) a per-block or
  whole-image **integrity checksum/CRC the radio validates** — **reversing and recomputing this is
  the single most likely hard blocker.** Plan for it.
- **Database-version coupling:** field byte-offsets are valid only for a given database version; the
  CPS loads a matching data file to interpret the codeplug.

## 5. The plan — gated milestones (pick your entry point by what Tom has)

**M0 — Codeplug-file diff (zero tooling, near-free; do this first if available).**
Tom saves a codeplug from the CPS (`Read the radio` → `Save`), changes **one** field (e.g. SDM
off→on), saves again as a second file. Diff the two saved files offline. This may reveal field
encodings and the file format **without** touching the wire protocol, and de-risks the offset side.
It does *not* give the read/write protocol (still need §M1) but is the cheapest possible start.
→ *Entry state A: Tom has provided two one-field-different saved codeplug files.* Start here: diff
them (`cmp -l`, `xxd` + diff, or a small Python differ), characterise the file structure, locate the
changed bytes, look for a checksum that moved.

**M1 — Decode the transport (the from-scratch core).**
Capture a full **Read** and a full **Write** (§6–7). Decode: mode-entry handshake, baud, block
framing, addressing, transfer termination, and the write's integrity checksum. Deliverable: a Linux
tool that can **read a full codeplug to a file and write it back byte-identical** (round-trip) against
a **backed-up** radio, including recomputing any checksum. Prove the round-trip before ever changing
a byte.
→ *Entry state B: Tom has provided a serial capture (read and/or write).* Start here: decode the
capture; build the read/write round-trip harness (reuse `ISerialIo`/CCDI plumbing where useful).

**M2 — Map the target fields.**
For each in-scope field: use the CPS to toggle exactly one setting, write, read back, and **diff the
codeplug bytes** to locate that field's offset+encoding on the radios' database version. A dozen
paired captures maps everything in §1.

**M3 — Build the patcher.**
A strict pipeline: **read whole codeplug → verify against a recognised database version (refuse
otherwise) → patch only known offsets → recompute checksum → write → read-back verify**. Always
snapshot the pre-write image. Ship as a Linux CLI (§8). Bench-validate each field's effect via CCDI
runtime behaviour (e.g. patch SDM-enable, confirm SDMs now flow; patch RX tap, confirm the mode
that was blocked now decodes).

**If Tom has provided nothing yet (Entry state C):** help him set up capture — walk him through the
two cheap saved files (M0) and the sniff (§6), and meanwhile prepare the analysis harness (a diff
tool + a Linux pty MITM script + a codeplug-file structure explorer) so it's ready the moment data
arrives. Don't block on hardware.

## 6. What Tom must provide (in increasing effort)

1. **Two saved codeplug files differing by one field** (M0) — trivial, no tooling: Read → Save →
   change one setting → Save-as. Send both files.
2. **A COM-port capture of a Read + a one-field Write** (M1) — the wire protocol. Easiest on Windows
   with **HHD Serial Port Monitor** sniffing the COM port the CPS uses (non-intrusive, hex both
   directions, timestamps). See §7 for alternatives.
3. **Connector pinout + cable details** for his TM8110 front RJ45 mic/data connector, and whether his
   programming cable inverts (FTDI Invert-TXD/RXD?) or is plain — from the service manual (§9) and his
   own setup.
4. **Confirmation of which radio is the sacrificial bench unit** for the first live write.

## 7. Capture tooling (concrete)

- **Hardware Y-tap (most reliable; inversion-agnostic).** Two USB-serial adapters, each logging one
  direction (adapter1 RX ← radio-TX, adapter2 RX ← PC-TX, common ground), timestamped
  (`jpnevulator --ascii --timing-print`, dual `interceptty`/`cat`, or a `sigrok`/logic-analyzer on
  the two UART lines). Captures the true bytes regardless of driver-side inversion. Recommended if the
  software sniff is ambiguous.
- **Windows software sniffer (fastest to stand up).** **HHD Device Monitoring Studio / Serial Port
  Monitor** (commercial, best), or **Free Serial Analyzer** / **COMSniffer** (free-ish). Sysinternals
  **Portmon** is free but often fails on modern 64-bit Windows — fallback only. Run the CPS against the
  real radio and sniff its COM port.
- **Linux pty MITM (best for iterating a decoder/replayer).** Put a virtual port between CPS (in a
  Windows VM) and the radio:
  `interceptty -s 'ispeed 19200 ospeed 19200' /dev/ttyUSB0 /dev/ttyMITM`
  or `socat -x -v PTY,link=/dev/ttyMITM /dev/ttyUSB0` (`-x -v` = hex both directions). This is the
  natural harness once you start replaying/patching, because your Linux tool sits in the CPS's seat.
  (Note: com0com null-modem pairs alone don't sniff a hardware device — software-only case.)

## 8. Deliverable shape (suggested)

- A new **`Packet.Radio.Tait.Codeplug`** library (or `tools/Packet.Tait.Codeplug` CLI first, spike-
  grade) — read/write transport, a version-pinned field-offset map, checksum recompute, strict patch
  pipeline. Reuse `Packet.Radio.Tait`'s `ISerialIo`/CCDI plumbing; read the DB version over CCDI to
  select the map.
- CLI verbs: `read <port> <out.codeplug>` (always the first step / backup), `write <port>
  <in.codeplug>` (with a confirm + pre-write auto-backup), `patch <port> --set sdm=on --set
  rxtap=R2 …` (read→patch→recompute→write→verify), `dump <file>` (decode a saved image for the
  mapped fields), `version <port>` (report DB version + whether it's a recognised map).
- **Refuse to run against an unrecognised database version.** Always auto-snapshot before write.
- Unit tests over scripted captures (transport framing, checksum, patch-preserves-other-bytes);
  hardware validation on the sacrificial radio with CCDI-verified field effects.
- Update `docs/plan.md` §17 + a spike writeup in `docs/research/`.

## 9. Sources (verified this project)

- CCDI Protocol Manual MMA-00038 (confirms CCDI ≠ programming):
  https://manuals.repeater-builder.com/2006/TM8000/TM8000%20CCDI%20Protocol%20Manual%20v3.01/MMA-00038-01%20TM8000%20CCDI%20Protocol%20Manual%20May%202006.pdf
- Service Manual MMA-00005 (connector pinouts, programming section):
  https://www.repeater-builder.com/tait/pdf/tait-tm8100-tm8200-service-manual.pdf
- `mumrah/tait-usb-programmer` (only Tait open project — **cabling/hardware only**, no protocol):
  https://github.com/mumrah/tait-usb-programmer
- OARC wiki TM8100 (cable/inversion debate + the boot-time programming-mode trick):
  https://wiki.oarc.uk/radios:tait_tm8100 ; firmware/bricking: https://wiki.oarc.uk/radios:tait_tmxxx_firmware
- TARPN TM8105 notes (FTDI Invert-TXD/RXD, "Read the radio"):
  https://tarpn.net/t/builder/tait_tm8105_notes/builders_radios_tait8105.html
- TN-919-AN data operation (baud/flow control):
  https://manuals.repeater-builder.com/2007/TECHNOTE/TM8000/TN-919_AN_Configuring%20the%20TM8100%20for%20Data%20Operation.pdf
- RadioReference: database-version mismatch https://forums.radioreference.com/threads/tait-programming-datafile-mismatch.447409/ ;
  firmware-brick → dealer https://forums.radioreference.com/threads/tait-tp8100-bricked.462334/
- Capture tools: HHD https://hhdsoftware.com/serial-port-monitor ; Portmon https://learn.microsoft.com/en-us/sysinternals/downloads/portmon
- CPS/firmware archives (to run for capture): https://archive.org/details/tait-software-collection ;
  https://radiosoftware.online/TAIT/SOFTWARE/8100_8200/
- Calibration Application (separate tuning-file protocol — **out of scope**):
  https://tait-tm8000-calibration-application.software.informer.com/

## 10. First actions for the fresh session

1. Read this + `CLAUDE.md` + the bench-rig memory + `docs/research/tait-ccdi-spike.md`.
2. Establish the entry state (A: saved-file diff / B: wire capture / C: nothing yet) with Tom.
3. Branch off `main` (`tait-codeplug`), work in a worktree if running alongside other agents.
4. If C: build the analysis harness (file differ + pty-MITM script + codeplug dumper) and guide Tom's
   capture. If A: diff the two saved files. If B: decode the transport toward an M1 round-trip.
5. Read the radios' database version over CCDI early (`RADIO_VERSIONS` record 02) — it pins everything.
6. Obey the golden rules (§2). Back up before every write; never firmware; version-pin; sacrificial
   radio first.
