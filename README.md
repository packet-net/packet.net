# Packet.NET

A modern, open, cross-platform AX.25 v2.2+ stack and packet radio node, written in .NET 10. Connected-mode sessions over KISS modems (USB/serial or TCP), a sound card, or AXUDP - with continuous interop tests against LinBPQ, XRouter, rax25, direwolf, and a NinoTNC pair.

Two things live in this repo, and either is usable on its own:

- **Packet.NET** - or **pdn** - a packet-radio node you install on a Pi or any Debian-ish box: ports, NET/ROM, a web control panel, radio and rig control, an app platform.
- **The `Packet.*` libraries** - the engine underneath and various accessories, on NuGet, for building your own .NET packet software.

Packet.NET is built on top of [ax25sdl](https://github.com/packet-net/ax25sdl) - which is an effort to codify the state machines documented in the [AX.25 specification](https://github.com/packethacking/ax25spec/) in a formal, provable manner, whose output is a suite of codegenned libraries in various languages including C# .NET.

## Run a node

Most visitors will probably just be interested in running a packet radio node of their own.

pdn is distributed two ways, both from [Releases](https://github.com/packet-net/packet.net/releases): a **`.deb`** for Debian, Ubuntu and Raspberry Pi OS, and a **`.tar.gz`** archive of the same node for anything else. On a Debian-ish box (a Raspberry Pi is ideal), grab the `.deb` for your architecture and:

```sh
cd /tmp && curl -fsSLO "https://github.com/packet-net/packet.net/releases/latest/download/packetnet_$(dpkg --print-architecture).deb"
sudo apt install "/tmp/packetnet_$(dpkg --print-architecture).deb"
```

That URL always resolves to the current release: the `.deb` filenames carry no version, so there is nothing to look up first and nothing to edit next time. The version is inside the package - `dpkg -I packetnet_arm64.deb`.

(Downloading to `/tmp` rather than your home directory keeps apt from printing a *"Download is performed unsandboxed as root"* notice: apt fetches even a local file as the `_apt` user, which cannot read a `0750` home directory.)

The install finishes by printing your node's control panel address - `http://your-node:8080`, reachable from any machine on your network. Open it in a browser, and a three-step wizard asks for your callsign, an admin login, and your first port.

**[Getting started](operating/00-install.md)** has both routes in full, along with what to do next and what to do if you run into problems.

## Build software on it

This section is for .NET developers.

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

The **[developer guide](guide/index.md)** builds that up properly - a raw frame dumper, then a beacon sender, a channel monitor, a connect client, a node of your own, and NET/ROM.

## Documentation

| Read | For |
| --- | --- |
| [Getting started](operating/00-install.md) | Install pdn, reach the panel, add a port, get on the air |
| [Operator guide](operating/index.md) | Attach a radio, then see and improve your link: RSSI/SNR, the radio doctor, deviation tuning, metrics, CAT rig control, split-station head-ends, programming a Tait from the panel |
| [Developer guide](guide/index.md) | Build packet-radio software on the libraries, layer by layer |
| [Packages](docs/packages.md) | What each `Packet.*` library does and where it publishes |
| [About](docs/about.md) | Scope, provenance, sibling repos, licence |
| [Plan](docs/plan.md) | The living roadmap and amendment log - the source of truth for direction and status |
| [Contributing](CONTRIBUTING.md) · [Releasing](docs/releasing.md) | House rules; how a change reaches the world |

## Build from source

```sh
dotnet build
dotnet test --filter "Category!=HardwareLoop&Category!=Interop"
```

Requires the .NET 10 SDK (see `global.json`). The full interop matrix - LinBPQ, XRouter, rax25 and a net-sim in docker, plus the TypeScript library's suite run against the same stack - lives in [`.github/workflows/interop.yml`](.github/workflows/interop.yml).

There is also a container image of the node, `ghcr.io/packet-net/packet.net` ([`docker/node/README.md`](docker/node/README.md)). Treat it as a **development tool** - a disposable node for testing against, and what the interop stack builds on. It is not a supported way to run a station: use the `.deb`.

## Licence

[AGPL-3.0](LICENSE) - the whole repo and every published `Packet.*` NuGet package. 

In short: if you distribute software built on this code, or let users reach it over a network, you must publish your own source under the same licence. This being networking software, that second clause will apply to you.

That's deliberate. This is amateur radio software with no commercial value, and for the good of the hobby, code running on our airwaves should be open.

If this isn't for you, please spin the dial.
