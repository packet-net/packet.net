# About Packet.NET

Background on what this project is, what it deliberately is not, and how it relates to its sibling repositories. Nothing here is needed to install or use pdn — start at [getting started](../operating/00-install.md) for that.

## What it is

A modern AX.25 v2.2 stack and packet-radio node, written in .NET 10. Connected-mode sessions over KISS modems (USB/serial or TCP), a sound card, or AXUDP, with continuous interop tests against LinBPQ, XRouter, rax25, direwolf, and a NinoTNC pair.

Two deliverables live in this repository:

- **pdn** — the node host (`Packet.Node`): a packet-radio node with a web control panel, NET/ROM routing, radio and rig control, an app platform, and a `.deb`.
- **The `Packet.*` libraries** — the engine underneath, published to NuGet for anyone building their own packet software. See [`packages.md`](packages.md).

## What it is NOT

- A BBS, chat server, mailbox, or DAPPS — those land as out-of-tree apps on the [app platform](app-packages.md).
- An HF waveform stack — it talks to KISS (over TCP/serial), its own soundcard modem, and AXUDP. VARA and ARDOP-as-a-waveform are out of scope for v1 (pdn does host an ardopcf-compatible *interface* for external ARDOP hosts).
- A drop-in LinBPQ replacement — it aims for protocol-level interop, not bug-for-bug parity.

## Spec-compliant by default

The libraries produce and accept exactly what AX.25 v2.2 / APRS101 / the KISS TNC protocol describe. Real-world peers bend those rules, and Packet.NET accommodates them — but every accommodation is a **named flag** on a parse-options record with a documented default, never a silent widening of the parser. BPQ, XRouter, and direwolf are interop targets, not reference truth. The full inventory of accommodations, what drives each one, and which presets enable it is in [`strict-vs-pragmatic-audit.md`](strict-vs-pragmatic-audit.md).

## Provenance

`packet-net/packet.net` is the origin of the project. It started life as a monorepo holding everything — .NET libraries, SDL transcriptions plus codegen, the TypeScript library, two terminal apps. On 2026-05-17 it split into five repos along their natural ownership boundaries, each spinoff extracted with history preserved (`git filter-repo`). What is left here is the .NET surface: libraries, node host, and the interop CI matrix. (The split left copies of the generated C/Rust/Python/JSON spec packages behind in this tree; nothing referenced them and they were deleted on 2026-08-17. They live in `ax25sdl`.)

## Sibling repos

| Repo | What it is |
| --- | --- |
| **`packet-net/packet.net`** *(here)* | .NET libraries + node host. Hosts the interop matrix (LinBPQ/XRouter/rax25/NinoTNC). |
| [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl) | AX.25 v2.2 SDL transcriptions + codegen (7 backends). Publishes `Packet.Ax25.Sdl` to NuGet and `ax25sdl` to npm. |
| [`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts) | `@packet-net/ax25` — browser-targeted TypeScript library, parity-checked against this repo in CI. |
| [`packet-net/packet-term-tui`](https://github.com/packet-net/packet-term-tui) | `Packet.Term` — Terminal.Gui v2 TUI. Consumes `Packet.*` from NuGet. |
| [`packet-net/packet-term-web`](https://github.com/packet-net/packet-term-web) | Browser TNC2 emulator at <https://packet-term.m0lte.uk>. Consumes `@packet-net/ax25` from npm. |
| [`packet-net/pdn-libax25`](https://github.com/packet-net/pdn-libax25) | LGPL-3.0 drop-in `libax25.so` + `LD_PRELOAD` AF_AX25 interposer: native AX.25 apps (address a callsign) run over pdn via RHPv2. The native seam. |
| [`packet-net/pdn-net`](https://github.com/packet-net/pdn-net) | AGPL-3.0 TUN/IP host stack: run unmodified IP software (address an IP) over packet radio; standard IP-over-AX.25. The IP seam. |

All of the above are public. The [`packet-net`](https://github.com/orgs/packet-net/repositories) organisation holds more besides — the soundmodem engine, apps that run on the node, the net simulator — but the table above is the set this repository is directly coupled to.

The `ax25sdl` repo is the longest-lived contributor surface: that is where SDL transcriptions and spec-side work happen. Tom is working with the original AX.25 authors on whether `packethacking/ax25spec` should be the canonical community home for those transcriptions; `packet-net/ax25sdl` is the prove-out venue until that is agreed.

## Licence

[AGPL-3.0](../LICENSE) — the whole repo and every published `Packet.*` NuGet package. (The repo was MIT until 2026-06-14; the switch was ratified and the package metadata brought into line on 2026-07-14 — plan §17.)

## Acknowledgements

- The [packethacking](https://github.com/packethacking) AX.25 v2.2 specification rewrite.
- John Wiseman G8BPQ for LinBPQ, decades of packet work, and the multi-drop KISS / ACKMODE extensions.
- The [Online Amateur Radio Community (OARC)](https://oarc.uk).
