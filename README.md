# Packet.NET

A modern AX.25 v2.2 stack and packet-radio node, written in .NET 10. Connected-mode sessions over KISS modems (USB/serial or TCP) or AXUDP, with continuous interop tests against LinBPQ, XRouter, rax25, direwolf, and a NinoTNC pair.

**Status:** the libraries are publishing to NuGet; the node host (`Packet.Node`) is taking shape. See [`docs/plan.md`](docs/plan.md) for the live roadmap.

## What's here

Each library is its own NuGet package (or planned package). They compose: the node host depends on all of them, but you can pull just `Packet.Ax25` + `Packet.Kiss` if you're building an app of your own.

| Path | Purpose | NuGet |
| --- | --- | --- |
| `src/Packet.Core/` | Shared primitives (Callsign, Ax25Address, KissFrame) | [`Packet.Core`](https://www.nuget.org/packages/Packet.Core) |
| `src/Packet.Ax25/` | AX.25 v2.2 frame codec + connected-mode session machine + `Ax25Listener` | [`Packet.Ax25`](https://www.nuget.org/packages/Packet.Ax25) |
| `src/Packet.Kiss.Abstractions/` | KISS modem interface | [`Packet.Kiss.Abstractions`](https://www.nuget.org/packages/Packet.Kiss.Abstractions) |
| `src/Packet.Kiss/` | KISS framing, ACKMODE, multi-drop, TCP transport | [`Packet.Kiss`](https://www.nuget.org/packages/Packet.Kiss) |
| `src/Packet.Aprs/` | APRS frame codec | _not yet published_ |
| `src/Packet.Agw/` | AGW (SV2AGW) client | _not yet published_ |
| `src/Packet.Axudp/` | AXUDP transport | _not yet published_ |
| `src/Packet.Kiss.NinoTnc/` | NinoTNC-specific KISS transport | _not yet published_ |
| `src/Packet.Mcp/` | MCP server scaffolding | _not yet published_ |
| `src/Packet.Rhp2/` + `.Server/` | RHPv2 protocol | _not yet published_ |
| `src/Packet.Node/` + `.Extensions/` | Packet-radio node host (web UI, REST, MCP, plugin shim) | n/a — application |

The SDL state-machine tables that drive `Packet.Ax25/Session/` come from the [`Packet.Ax25.Sdl`](https://www.nuget.org/packages/Packet.Ax25.Sdl) NuGet package, built and published by [`m0lte/ax25sdl`](https://github.com/m0lte/ax25sdl) — don't try to regenerate them from here.

## Provenance

`m0lte/packet.net` is the origin of the project. It started life as a monorepo holding everything — .NET libraries, SDL transcriptions + codegen, the TypeScript library, two terminal apps. On 2026-05-17 it split into five repos along their natural ownership boundaries, each spinoff extracted with history preserved (`git filter-repo`). What's left here is the .NET surface — libraries + node host + interop CI.

## Sibling repos

| Repo | What it is | Visibility |
| --- | --- | --- |
| **`m0lte/packet.net`** *(here)* | .NET libraries + node host. Hosts the interop matrix (LinBPQ/XRouter/rax25/NinoTNC). | private |
| [`m0lte/ax25sdl`](https://github.com/m0lte/ax25sdl) | AX.25 v2.2 SDL transcriptions + codegen (7 backends). Publishes `Packet.Ax25.Sdl` to NuGet + `ax25sdl` to npm. | private (prove-out) |
| [`m0lte/ax25-ts`](https://github.com/m0lte/ax25-ts) | `@packet-net/ax25` — browser-targeted TypeScript library. | public |
| [`m0lte/packet-term-tui`](https://github.com/m0lte/packet-term-tui) | `Packet.Term` — Terminal.Gui v2 TUI. Consumes `Packet.*` from NuGet. | private |
| [`m0lte/packet-term-web`](https://github.com/m0lte/packet-term-web) | Browser TNC2 emulator at https://packet-term.m0lte.uk. Consumes `@packet-net/ax25` from npm. | public |

The `ax25sdl` repo is the longest-lived contributor surface — that's where SDL transcriptions and spec-side work happen. Tom is working with the original AX.25 authors on whether `packethacking/ax25spec` should be the canonical community home for those transcriptions; `m0lte/ax25sdl` is the prove-out venue until that's agreed.

## Build + test

```sh
dotnet build
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"
```

Requires .NET 10 SDK (see `global.json`).

The full interop matrix (docker stack + cross-runtime tests) lives in [`.github/workflows/interop.yml`](.github/workflows/interop.yml). It stands up LinBPQ/XRouter/rax25/netsim in docker and runs the C# interop tests against it; it also clones `m0lte/ax25-ts` and runs its integration suite against the same stack.

## What this is NOT

- A BBS, chat server, mailbox, or DAPPS — those land as out-of-tree plugins.
- An HF waveform stack — talks to KISS (over TCP/serial) and AXUDP only. VARA, ARDOP and friends are out of scope for v1.
- A drop-in LinBPQ replacement — aims for protocol-level interop, not bug-for-bug parity.

## License

[MIT](LICENSE).

## Acknowledgements

- The [packethacking](https://github.com/packethacking) AX.25 v2.2 specification rewrite.
- John Wiseman G8BPQ for LinBPQ, decades of packet work, and the multi-drop KISS / ACKMODE extensions.
- The [Online Amateur Radio Community (OARC)](https://oarc.uk).
