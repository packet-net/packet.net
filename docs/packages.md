# The `Packet.*` packages

Packet.NET is a layer cake of small libraries. Each one is an independent NuGet package; you reference only the layers you need, and they compose upward. The node host (`Packet.Node`) depends on all of them, but nothing else has to — pulling `Packet.Ax25` + `Packet.Kiss` is enough to build a station of your own.

The [developer guide](../guide/index.md) walks this surface from a raw frame dumper up through a beacon sender, a channel monitor, a connect client, and a hand-rolled node, all the way to NET/ROM. Start there; this page is just the inventory.

## Publication matrix

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
| `src/Packet.Node/` | Packet-radio node host (web UI, REST, MCP, app gateway) | not published (application) |
| `src/Packet.Node.Core/` | Node logic: config, ports, sessions, NET/ROM, apps, auth, self-update | not published (node-internal) |

Every published package is AGPL-3.0, like the repo.

## The SDL dependency

The SDL state-machine tables that drive `Packet.Ax25/Session/` come from the [`Packet.Ax25.Sdl`](https://www.nuget.org/packages/Packet.Ax25.Sdl) NuGet package, built and published by [`packet-net/ax25sdl`](https://github.com/packet-net/ax25sdl). They are not generated from this repo — spec-side changes are raised there, published, and picked up by bumping the pin in [`Directory.Packages.props`](../Directory.Packages.props).

## Versioning

All the libraries release together off a single `lib-v*` tag, so their versions move in lockstep; the node host has its own `node-v*` train. The full procedure is in [`releasing.md`](releasing.md).

## In the browser

The TypeScript sibling, [`@packet-net/ax25`](https://www.npmjs.com/package/@packet-net/ax25), tracks this engine behaviour-for-behaviour over Web Serial — same named-flag inventory, CI-enforced. It lives in [`packet-net/ax25-ts`](https://github.com/packet-net/ax25-ts).
