# Building packet-radio tooling with the packet.net engine

This guide is for **.NET developers who want to build their own packet-radio
software** — a connect client, a channel monitor, an automatic station, or a
full NET/ROM node — directly on top of the packet.net libraries.

packet.net ships a node host of its own (`Packet.Node`), but you do **not** need
it. The node host is just one consumer of the same public libraries documented
here. This guide deliberately ignores it until the very end and shows you how to
assemble equivalent tooling yourself, one layer at a time, using only the
NuGet-published engine.

## Who this is for

You should be comfortable with:

- C# and `async`/`await`, `IAsyncEnumerable<T>`, `ReadOnlyMemory<byte>`/`Span<byte>`.
- The basic idea of AX.25 (callsigns, frames, connected vs. connectionless), at
  the "I have read the front matter of the spec" level. You do not need to know
  the state machine — the engine owns that.
- Having a TNC — a hardware modem like the NinoTNC, or a software modem like
  Dire Wolf or QtSoundModem — or an AGW/AXUDP endpoint you can talk to. Most
  examples can also be exercised against a loopback or the interop stack without
  hardware.

## The shape of the engine

packet.net is a **layer cake of small libraries**. Each one is an independent
NuGet package; you reference only the layers you need. They compose upward:

```
┌─────────────────────────────────────────────────────────────┐
│  your application  (axcall, axlisten, a BBS, a node …)        │
├─────────────────────────────────────────────────────────────┤
│  Packet.NetRom            NET/ROM L3/L4: routing + circuits    │
├─────────────────────────────────────────────────────────────┤
│  Packet.Ax25 (Session)    connected-mode data-link: Ax25Listener,
│                           Ax25Session, the v2.2 SDL state machine │
├─────────────────────────────────────────────────────────────┤
│  Packet.Ax25 (frames)     Ax25Frame encode/decode, factories   │
│  Packet.Core              Callsign, Ax25Address, primitives     │
├─────────────────────────────────────────────────────────────┤
│  Packet.Ax25.Transport.   IAx25Transport — the transport seam   │
│    Abstractions                                                 │
│  Packet.Kiss / .Serial /  KISS transports (TCP / serial / Nino) │
│  .NinoTnc                  — each implements IAx25Transport      │
│  Packet.Axudp · Packet.Agw  AXUDP (non-KISS) · AGW (session)    │
└─────────────────────────────────────────────────────────────┘
```

Two interfaces are the load-bearing seams you will design against:

- **`IAx25Transport`** (`Packet.Ax25.Transport`) — "a thing that sends and
  receives AX.25 frames as bytes." Every frame transport implements it, so the
  layers above never care whether you are on a USB TNC, a TCP KISS socket, or an
  AXUDP tunnel. **KISS is one implementation behind this seam, never a property
  of it.**
- **`Ax25Listener`** (`Packet.Ax25.Session`) — "a station." It owns one
  `IAx25Transport`, runs the AX.25 state machine per peer, accepts inbound
  connections, dials outbound ones, and traces every frame. Almost everything
  node-shaped goes through it.

One more pair of seams sits *beside* the cake rather than in it: `IRadioControl`
(`Packet.Radio`) and `IRigControl` (`Packet.Rig`) — the **radio behind the
modem**, for stations whose radio has a serial control channel (RSSI, hardware
carrier-sense, CAT). They're optional and covered last, in
[chapter 9](09-radios-and-rigs.md).

## How this guide builds up

Each chapter adds one layer and a runnable tool that exercises it. Read them in
order; later chapters assume the vocabulary of earlier ones.

| # | Chapter | You build | New API |
|---|---------|-----------|---------|
| 1 | [Architecture & the two seams](01-architecture.md) | — | the mental model |
| 2 | [Transports: talking to a TNC](02-transports.md) | a raw frame dumper | `IAx25Transport`, `Ax25InboundFrame`, `KissTcpClient`, `KissSerialModem`, `NinoTncSerialPort` |
| 3 | [Frames & callsigns](03-frames-and-callsigns.md) | **`axbeacon`** — a UI/beacon sender | `Callsign`, `Ax25Frame`, the factories, `Ax25ParseOptions` |
| 4 | [Listen: a channel monitor](04-listen.md) | **`axlisten`** | `Ax25Frame.TryParse`, frame inspection |
| 5 | [Call: a connected-mode client](05-axcall.md) | **`axcall`** | `Ax25Listener.ConnectAsync`, `Ax25Session`, DL primitives |
| 6 | [Building a node](06-building-a-node.md) | a connectable command server | `SessionAccepted`, command loops, multi-port, aliases |
| 7 | [NET/ROM: routing & circuits](07-netrom.md) | a NODES-aware node | `NodesBroadcast`, `NetRomRoutingTable`, `NetRomForwarding`, `CircuitManager` |
| 8 | [Beyond](08-beyond.md) | — | segmentation, XID, quirks, observability, and where `Packet.Node.Core` takes over |
| 9 | [Radios & rigs](09-radios-and-rigs.md) | a signal-aware monitor | `IRigControl`, `RigctldRig`, `IRadioControl`, `RssiTaggingTransport`, `RadioCarrierSense`, `RigRadioControl` |

## Installing the packages

The engine targets modern .NET (see `global.json` in the repo for the exact
SDK). Add the layers you need as NuGet packages:

```sh
# The minimum to build and decode frames:
dotnet add package Packet.Core
dotnet add package Packet.Ax25

# The transport seam (a tiny, dependency-free contracts package; pulled in
# transitively by Packet.Ax25, so you rarely add it explicitly):
dotnet add package Packet.Ax25.Transport.Abstractions

# A concrete transport (pick what your hardware/peer speaks):
dotnet add package Packet.Kiss             # KISS over TCP
dotnet add package Packet.Kiss.Serial      # KISS over a serial port
dotnet add package Packet.Kiss.NinoTnc     # NinoTNC USB

# Higher layers, as you reach them:
dotnet add package Packet.Agw              # AGWPE / SV2AGW client

# Radio/rig control, if your radio has a control channel (chapter 9):
dotnet add package Packet.Radio            # the packet-medium seam + decorators
dotnet add package Packet.Rig              # the station-control (CAT) seam
dotnet add package Packet.Rig.Hamlib       # rigctld backend
```

!!! note "Published packages"
    Every package this guide uses — `Packet.Core`, `Packet.Ax25`,
    `Packet.Ax25.Transport.Abstractions`, `Packet.Kiss*`, `Packet.Agw`,
    `Packet.Axudp`, `Packet.NetRom`, `Packet.Radio*`, `Packet.Rig*` — is
    published to NuGet; see the
    [top-level README](../README.md#whats-here) for the full publication
    matrix.

!!! info "A note on strictness"
    The engine **produces and accepts exactly what AX.25 v2.2 describes by
    default.** Real-world peers (BPQ, XRouter, Dire Wolf) bend the rules, and the
    engine accommodates them — but every accommodation is a **named flag** on an
    options record, never a silent default. You'll meet this pattern as
    `Ax25ParseOptions` in [chapter 3](03-frames-and-callsigns.md) and
    `Ax25SessionQuirks` in [chapter 8](08-beyond.md). Keep it in mind: when a
    frame your code rejects "should" have been accepted, the answer is usually a
    flag, not a parser change.

Ready? Start with [the architecture and the two seams →](01-architecture.md)
