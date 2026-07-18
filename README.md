# Packet.NET

A modern AX.25 v2.2 stack and packet-radio node, written in .NET 10. Connected-mode sessions over KISS modems (USB/serial or TCP) or AXUDP, with continuous interop tests against LinBPQ, XRouter, rax25, direwolf, and a NinoTNC pair.

**Status:** the libraries are publishing to NuGet; the node host (`Packet.Node`) is taking shape. See [`docs/plan.md`](docs/plan.md) for the live roadmap.

## What's here

Each library is its own NuGet package (or planned package). They compose: the node host depends on all of them, but you can pull just `Packet.Ax25` + `Packet.Kiss` if you're building an app of your own.

> **Building your own tooling on the libraries?** Start with the [developer guide](guide/index.md) — it walks the public API from a raw frame dumper up through a beacon sender, a channel monitor, a connect client, and a hand-rolled node, all the way to NET/ROM.

> **Running the node host with a radio attached?** Start with the [operator guide](operating/index.md) — attach a radio to a port, then see and improve your link: per-frame RSSI/SNR, a radio-health dashboard, the "check radio" doctor, guided deviation tuning, Prometheus metrics, CAT rig control (hamlib `rigctld` / flrig — dial, mode, meters, rig-DCD carrier-sense), and TNC-less Tait-to-Tait links.

| Path | Purpose | NuGet |
| --- | --- | --- |
| `src/Packet.Core/` | Shared primitives (Callsign, Ax25Address) | [`Packet.Core`](https://www.nuget.org/packages/Packet.Core) |
| `src/Packet.Ax25.Transport.Abstractions/` | Frame-transport contract (`IAx25Transport`, `Ax25InboundFrame`, optional `ITxCompletionTransport` / `ICsmaChannelParams`) | [`Packet.Ax25.Transport.Abstractions`](https://www.nuget.org/packages/Packet.Ax25.Transport.Abstractions) |
| `src/Packet.Ax25/` | AX.25 v2.2 frame codec + connected-mode session machine + `Ax25Listener` | [`Packet.Ax25`](https://www.nuget.org/packages/Packet.Ax25) |
| `src/Packet.NetRom/` | NET/ROM L3 routing + L4 circuits + INP3 time-routing | [`Packet.NetRom`](https://www.nuget.org/packages/Packet.NetRom) |
| `src/Packet.Kiss/` | KISS framing, ACKMODE, multi-drop, TCP transport (`KissFrame`, `KissTcpClient`) | [`Packet.Kiss`](https://www.nuget.org/packages/Packet.Kiss) |
| `src/Packet.Aprs/` | APRS payload codec (position, mic-E, message, object, telemetry) | [`Packet.Aprs`](https://www.nuget.org/packages/Packet.Aprs) |
| `src/Packet.Agw/` | AGW (AGWPE / SV2AGW) client | [`Packet.Agw`](https://www.nuget.org/packages/Packet.Agw) |
| `src/Packet.Axudp/` | AXUDP (AX.25-over-IP / RFC 1226) transport (`AxudpSocket`) | [`Packet.Axudp`](https://www.nuget.org/packages/Packet.Axudp) |
| `src/Packet.Kiss.Serial/` | Generic serial-port KISS modem | [`Packet.Kiss.Serial`](https://www.nuget.org/packages/Packet.Kiss.Serial) |
| `src/Packet.Kiss.NinoTnc/` | NinoTNC-specific KISS extensions (ACKMODE, SETHW, frame classification) | [`Packet.Kiss.NinoTnc`](https://www.nuget.org/packages/Packet.Kiss.NinoTnc) |
| `src/Packet.Radio/` | Radio-control abstraction for the packet medium (`IRadioControl`: RSSI, carrier-sense/DCD, PTT; `RssiTaggingTransport`) | [`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio) |
| `src/Packet.Radio.Tait/` | Tait TM8100/TM8200 CCDI driver (RSSI, DCD, SDM side channel, Transparent-mode transport) | [`Packet.Radio.Tait`](https://www.nuget.org/packages/Packet.Radio.Tait) |
| `src/Packet.Rig/` | Station-rig (CAT) control abstraction (`IRigControl`: frequency, mode, PTT, SWR/power meters) — dependency-free | [`Packet.Rig`](https://www.nuget.org/packages/Packet.Rig) |
| `src/Packet.Rig.Hamlib/` | Rig control over hamlib's `rigctld` network protocol (no native libhamlib dependency) | [`Packet.Rig.Hamlib`](https://www.nuget.org/packages/Packet.Rig.Hamlib) |
| `src/Packet.Rig.Flrig/` | Rig control over flrig's XML-RPC server | [`Packet.Rig.Flrig`](https://www.nuget.org/packages/Packet.Rig.Flrig) |
| `src/Packet.Tune.Core/` | RF tuning/coordination toolkit (mode coordination, TXDELAY minimisation, tuning doctor) | [`Packet.Tune.Core`](https://www.nuget.org/packages/Packet.Tune.Core) |
| `src/Packet.Mcp/` | MCP server scaffolding | _not yet published_ |
| `src/Packet.Rhp2/` | RHPv2 (Radio Host Protocol v2) wire codec | [`Packet.Rhp2`](https://www.nuget.org/packages/Packet.Rhp2) |
| `src/Packet.Rhp2.Server/` | RHPv2 server (node network plane) | n/a — node-internal (depends on `Packet.Node.Core`) |
| `src/Packet.Node/` + `.Extensions/` | Packet-radio node host (web UI, REST, MCP, plugin shim) | n/a — application |

The SDL state-machine tables that drive `Packet.Ax25/Session/` come from the [`Packet.Ax25.Sdl`](https://www.nuget.org/packages/Packet.Ax25.Sdl) NuGet package, built and published by [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl) — don't try to regenerate them from here.

## Provenance

`packet-net/packet.net` is the origin of the project. It started life as a monorepo holding everything — .NET libraries, SDL transcriptions + codegen, the TypeScript library, two terminal apps. On 2026-05-17 it split into five repos along their natural ownership boundaries, each spinoff extracted with history preserved (`git filter-repo`). What's left here is the .NET surface — libraries + node host + interop CI.

## Sibling repos

| Repo | What it is | Visibility |
| --- | --- | --- |
| **`packet-net/packet.net`** *(here)* | .NET libraries + node host. Hosts the interop matrix (LinBPQ/XRouter/rax25/NinoTNC). | private |
| [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl) | AX.25 v2.2 SDL transcriptions + codegen (7 backends). Publishes `Packet.Ax25.Sdl` to NuGet + `ax25sdl` to npm. | private (prove-out) |
| [`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts) | `@packet-net/ax25` — browser-targeted TypeScript library. | public |
| [`packet-net/packet-term-tui`](https://github.com/packet-net/packet-term-tui) | `Packet.Term` — Terminal.Gui v2 TUI. Consumes `Packet.*` from NuGet. | private |
| [`packet-net/packet-term-web`](https://github.com/packet-net/packet-term-web) | Browser TNC2 emulator at https://packet-term.m0lte.uk. Consumes `@packet-net/ax25` from npm. | public |
| [`packet-net/pdn-libax25`](https://github.com/packet-net/pdn-libax25) | LGPL-3.0 drop-in `libax25.so` + `LD_PRELOAD` AF_AX25 interposer: native AX.25 apps (address a callsign) run over pdn via RHPv2. The native seam. | public |
| [`packet-net/pdn-net`](https://github.com/packet-net/pdn-net) | AGPL-3.0 TUN/IP host stack: run unmodified IP software (address an IP) over packet radio; standard IP-over-AX.25. The IP seam. | public |

The `ax25sdl` repo is the longest-lived contributor surface — that's where SDL transcriptions and spec-side work happen. Tom is working with the original AX.25 authors on whether `packethacking/ax25spec` should be the canonical community home for those transcriptions; `packet-net/ax25sdl` is the prove-out venue until that's agreed.

## Build + test

```sh
dotnet build
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"
```

Requires .NET 10 SDK (see `global.json`).

The full interop matrix (docker stack + cross-runtime tests) lives in [`.github/workflows/interop.yml`](.github/workflows/interop.yml). It stands up LinBPQ/XRouter/rax25/netsim in docker and runs the C# interop tests against it; it also clones `packet-net/ax25-ts` and runs its integration suite against the same stack.

## What this is NOT

- A BBS, chat server, mailbox, or DAPPS — those land as out-of-tree plugins.
- An HF waveform stack — talks to KISS (over TCP/serial) and AXUDP only. VARA, ARDOP and friends are out of scope for v1.
- A drop-in LinBPQ replacement — aims for protocol-level interop, not bug-for-bug parity.

## License

[AGPL-3.0](LICENSE) — the whole repo and every published `Packet.*` NuGet package. (The repo was
MIT until 2026-06-14; the switch was ratified and the package metadata brought into line on
2026-07-14 — plan §17.)

## Acknowledgements

- The [packethacking](https://github.com/packethacking) AX.25 v2.2 specification rewrite.
- John Wiseman G8BPQ for LinBPQ, decades of packet work, and the multi-drop KISS / ACKMODE extensions.
- The [Online Amateur Radio Community (OARC)](https://oarc.uk).
