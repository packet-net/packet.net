# Packet.NET

A modern AX.25 v2.2 stack and packet-radio node, written in .NET 10. Connected-mode sessions over KISS modems (USB/serial or TCP), a sound card, or AXUDP — with continuous interop tests against LinBPQ, XRouter, rax25, direwolf, and a NinoTNC pair.

Two things live here, and either is usable on its own:

- **pdn** — a packet-radio node you install on a Pi or any Debian-ish box: ports, NET/ROM, a web control panel, radio and rig control, an app platform.
- **The `Packet.*` libraries** — the engine underneath, on NuGet, for building your own packet software.

## Run a node

The quickest route, on any Debian-ish Linux box (a Raspberry Pi is ideal):

```sh
curl -fsSL https://pdn-dist.m0lte.compute.oarc.uk/install.sh | sudo sh
```

Then tunnel to the control panel — `ssh -L 8080:127.0.0.1:8080 you@your-node` — and open <http://localhost:8080>, where a three-step wizard asks for your callsign, an admin login, and your first port.

Prefer a `.deb` from [Releases](https://github.com/packet-net/packet.net/releases), or Docker? Both are in **[Getting started](operating/00-install.md)**, along with what to do next and what to do when it does not work.

## Build software on it

```sh
dotnet add package Packet.Ax25
dotnet add package Packet.Kiss
```

```csharp
await using IAx25Transport transport = KissSerialModem.Open("/dev/ttyACM0");
var listener = new Ax25Listener(transport, new Ax25ListenerOptions { MyCall = Callsign.Parse("M0LTE-1") });
await listener.StartAsync();
var session = await listener.ConnectAsync(Callsign.Parse("GB7RDG-1"));
```

The **[developer guide](guide/index.md)** builds that up properly — a raw frame dumper, then a beacon sender, a channel monitor, a connect client, a node of your own, and NET/ROM.

## Documentation

| Read | For |
| --- | --- |
| [Getting started](operating/00-install.md) | Install pdn, reach the panel, add a port, get on the air |
| [Operator guide](operating/index.md) | Attach a radio, then see and improve your link: RSSI/SNR, the radio doctor, deviation tuning, metrics, CAT rig control, split-station head-ends |
| [Developer guide](guide/index.md) | Build packet-radio software on the libraries, layer by layer |
| [Packages](docs/packages.md) | What each `Packet.*` library does and where it publishes |
| [About](docs/about.md) | Scope, provenance, sibling repos, licence |
| [Plan](docs/plan.md) | The living roadmap and amendment log — the source of truth for direction and status |
| [Contributing](CONTRIBUTING.md) · [Releasing](docs/releasing.md) | House rules; how a change reaches the world |

## Build from source

```sh
dotnet build
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"
```

Requires the .NET 10 SDK (see `global.json`). The full interop matrix — LinBPQ, XRouter, rax25 and a net-sim in docker, plus the TypeScript library's suite run against the same stack — lives in [`.github/workflows/interop.yml`](.github/workflows/interop.yml).

## Licence

[AGPL-3.0](LICENSE) — the whole repo and every published `Packet.*` NuGet package.
