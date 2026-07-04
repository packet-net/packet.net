# 7. Advanced tooling

**Goal:** the bench-side extras — the `packet-tune` command-line tool, firmware
flashing, and mode coordination. These are for hands-on work at the node, beyond the
web control panel.

> [!IMPORTANT]
> **`packet-tune` is a bench/developer tool, not (yet) a packaged command.** Despite
> the name used throughout the docs, there is no installed `packet-tune` binary on
> your PATH. It lives in the repo at `tools/Packet.Tune` and is run from a checkout
> with:
>
> ```sh
> dotnet run --project tools/Packet.Tune -- <verb> [args…]
> ```
>
> Wherever this guide writes `packet-tune <verb> …`, read it as that. Treat these as
> operator/bench utilities you run on the node host, not a supported product surface.

## The `packet-tune` verbs at a glance

The tool is a toolbox of radio/TNC bench commands. The ones an operator is most
likely to reach for:

| Verb | What it does | Covered in |
|---|---|---|
| `doctor <tncPort> [ccdiPort]` | Capability/health probes for a port (safe + transmitting). | [Chapter 3](03-check-your-setup-doctor.md) |
| `deviation-sdm --role … --tnc … --radio … --peer …` | Guided deviation tuning, coordinated over the radios' SDM channel. | [Chapter 4](04-tune-your-link.md) |
| `deviation-remote --role … --tnc … --rendezvous <ws> [--pin …]` | Deviation tuning over an internet WebSocket relay + PIN (operators not co-located). | [Chapter 4](04-tune-your-link.md) |
| `flash-tnc <tncPort> <hexFile>` | Flash NinoTNC firmware. | Below |
| `mode-coord --role … --tnc … --radio … --peer …` | Negotiate a modem mode with a peer over the SDM side-channel. | Below |
| `hail --tnc … --radio … --peer …` | Ask a peer for its mode/modem + capabilities over SDM — **works across a mode mismatch that blocks the packet path**. | Below |
| `set-mode <tncPort> <mode>` | Set a NinoTNC's modem mode (`--persist` to keep it across reboot). | — |
| `radio-health <ccdiPort>` | Sample a Tait radio's RSSI / PA-temp / power-detector trend. | [Chapter 2](02-see-your-link-quality.md) |
| `radio-channel <ccdiPort> [channel]` | Read or set the radio's channel. | — |
| `mode-survey <tncA> <tncB> <ccdiA> <ccdiB>` | Sweep modem modes across a two-radio bench to compare decode rates. | — |

There are a few more low-level bench verbs (`verify-control`, `measure`,
`rendezvous`, `radio-reset`); run the tool with no arguments to see the full usage.

## Firmware flashing (`flash-tnc`)

Flash a NinoTNC with an Intel-HEX firmware image using the built-in bootloader
flasher:

```sh
packet-tune flash-tnc <tncPort> <firmware.hex> [--yes]
```

It pre-flights before writing: it classifies the image's target chip, **refuses if
another process is holding the port** (a flash through a shared port would strand the
TNC), reads the currently-running firmware version, and asks you to confirm (skip the
prompt with `--yes`). After a successful flash it waits for the reboot and
re-verifies the version.

> [!WARNING]
> **Do not interrupt a flash.** The transfer takes **2–4 minutes**. Do not unplug
> the TNC, close the tool, or let the machine sleep during it. An interrupted flash
> strands the TNC in its bootloader — it's **recoverable by re-running the same
> command**, but the TNC is dead until you do. Have a **known-good firmware image on
> hand** before you start so you can always re-flash.

> [!NOTE]
> After a successful flash the TNC **reboots and its RAM mode resets to 0.** Re-apply
> your modem mode afterwards, e.g. `packet-tune set-mode <tncPort> 6` (or set it in
> your port config so it's re-applied on the next port start).

## Mode coordination

`mode-coord` lets two stations **agree on a modem mode** (baud/modulation) over the
radios' SDM side-channel — propose a mode, both confirm, both commit, and revert
cleanly if it doesn't take. It's the seed of adaptive mode-agility (picking a faster
mode on a good channel, a more robust one on a poor channel).

Today it's a **CLI/bench capability** (`mode-coord --role coordinator|responder …`).
The web-UI surface for it is not shipped yet.

## Hailing a neighbour (`hail`)

`hail` lets one station **ask another for its status** — callsign, current NinoTNC mode
(number + name) and bit rate, radio channel, supported modes, and capabilities — over the
radios' SDM side-channel.

The point is **diagnostic**. The SDM side-channel rides the radio's own FFSK signalling
modem, which is *independent of the packet modulation*. So you can hail — and get a status
reply from — a station you **cannot** reach on the packet path *because* the two of you are
on incompatible modem modes. The reply tells you exactly what mode the peer is on, which is
usually the mismatch you were chasing. Nothing else in the stack can report on a station you
cannot decode.

```sh
# Ask the peer (radio SDM id PDN00001) for its status:
packet-tune hail --tnc /dev/ttyACM0 --radio /dev/ttyUSB0 --peer PDN00001 --callsign G0HAIL

# Arm this station to answer hails from that peer (opt-in — nothing answers unless armed):
packet-tune hail --respond --tnc /dev/ttyACM1 --radio /dev/ttyUSB1 --peer PDN00002 --callsign G0RSP
```

A successful hail prints the peer's mode/bitrate/channel/capabilities. The reply is optionally
tagged with the responder's **RSSI of the hail** — a bidirectional link-quality snapshot. The
richer status rides an *extended* SDM (up to 128 characters), which these radios split and
reassemble natively.

On the **node**, the same capability is `POST /api/v1/ports/{id}/hail` with `{ "peerSdmId": … }`
(admin-scoped and audited — it transmits); and a port can be armed as a resident responder with
the `radio.hailResponder` / `radio.hailResponderPeer` config on its radio block (off by default).
v1 is point-to-point; answering arbitrary hailers (routing the reply to the SDM caller id) is a
documented follow-up, as is a web-UI neighbour-mode map.

## What's CLI-only vs. in the web UI

To keep the boundary honest:

| Capability | Web UI | CLI (`packet-tune`) |
|---|---|---|
| Attach a radio, see health, heard list | ✅ | — |
| Doctor (check radio setup) | ✅ | ✅ (`doctor`) |
| Guided deviation tuning (SDM) | ✅ (`/tools/tuner`) | ✅ (`deviation-sdm`) |
| Deviation tuning over internet relay | — | ✅ (`deviation-remote`) |
| Firmware flashing | — | ✅ (`flash-tnc`) |
| Mode coordination | — | ✅ (`mode-coord`) |
| Station hail (diagnostic, over SDM) | — (node API: `POST /ports/{id}/hail`) | ✅ (`hail`) |

## Back to the start

That's the operator arc: [attach a radio](01-attach-a-radio.md) →
[see your link](02-see-your-link-quality.md) →
[check it](03-check-your-setup-doctor.md) →
[tune it](04-tune-your-link.md) → [graph it](05-radio-metrics.md).

Return to the [operating guide index](index.md).
