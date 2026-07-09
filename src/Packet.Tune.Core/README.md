# Packet.Tune.Core

> Primitives for tuning a NinoTNC + radio pair when the two ends are far apart — the engine behind the `packet-tune` CLI.

When the two stations of a packet link are miles apart, you cannot lean over and nudge the far end's TX-deviation pot, or agree a new modem mode by shouting. This library coordinates both over a channel that keeps working *while the link itself is being reconfigured*: the radio's own small-datagram side channel (Tait SDM), or — when there's internet — a PIN-paired WebSocket relay. It is the reusable engine behind the `packet-tune` CLI (`tools/Packet.Tune`). Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Tune.Core
```

## What's here

- **Telegram protocol** (`TuningTelegram`) — a compact `V1|seq|verb|args` line (verbs HI/RQ/MS/AD/BY/MODE) with a documented **compact wire form** that fits the 32-character Tait SDM budget; the receiver dedupes on the sequence number so a transport retry never surfaces twice, and each session starts its counter from a random base so a re-run against a still-running peer isn't mistaken for the prior session's telegrams.
- **The transport seam** (`ITuningLink`) in two flavours:
  - `SdmTuningLink` — telegrams ride the radio's own side channel (`Packet.Radio.IRadioSideChannel`; canonically Tait CCDI SDMs over the radios' internal FFSK modem at factory deviation), fully independent of the NinoTNC mode/pot under tune — bootstrap-safe, no internet needed. **Receipt-tolerant:** the Tait SDM over-air delivery receipt is unreliable for close bidirectional traffic (a radio's auto-ack transmission poisons its next send's receipt — [the auto-ack refractory](https://github.com/packet-net/packet.net/blob/main/docs/research/tm8110-sdm-autoack-refractory.md)), so a send completes on radio-accept and reliability is the protocol's own reply (propose→confirm / step→report), not the receipt;
  - `WebSocketTuningLink` — the internet flavour: both ends join a relay with a spoken single-use PIN.
- **PIN rendezvous** (`RendezvousRelay`) — a minimal, embeddable RFC-6455 relay: two clients pair on a 6-digit single-use PIN (`GeneratePin`), frames are forwarded verbatim, and the session dies with either socket. Runs embedded (tests park it on port 0) or as `packet-tune rendezvous --listen`.
- **The deviation-tuning assistant** (`TuningSession` + `DeviationAdvisor`) — the meter end requests short bursts and measures decode rate + IL2P FEC-corrected bytes + lost-ADC clipping, then **edge-brackets** the correct pot position from the failure cliffs: nothing decoding → `UP`, ADC clipping → `DN`, solid decode with idle FEC → `OK`, a fully-dead burst (no direction in it) → `SW` (sweep). On NinoTNC **firmware 3.41 only** — the GETRSSI RX-audio meter, removed in 3.44 — a fast path adds a continuous level read as enrichment (`DescribeLevel` shows where inside the plateau the pot sits and which way it's moving), but the decode/clip cliffs stay the authoritative verdict.
- **Mode coordination** (`ModeCoordinator` / `ModeResponder`, the `MODE` telegrams) — renegotiate the TNC mode (and optionally the radio channel) over the mode/channel-agnostic side channel: **propose → confirm → commit**, probe-verify the switched link both ways, and on any failure **revert** both ends to the session's home mode/channel (a responder idle watchdog backstops the case where the side channel can no longer reach the peer). The Phase-10 mode-agility seed.
- **The capability doctor** (`TuningDoctor`) — probes the whole TNC↔radio stack (firmware + GETRSSI, DIP software control, running mode, TXDELAY software control, CCDI identity, PROGRESS, SDM programming, TNC↔radio PTT pairing), each a pass/fail/unknown with a one-line remedy. **It transmits** — run it on a bench/test channel.

## Usage

```csharp
using Packet.Core;
using Packet.Kiss.NinoTnc;
using Packet.Radio.Tait;
using Packet.Tune.Core;

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM0");
await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0");
await radio.SetProgressMessagesAsync(true);                    // SDM arrivals + receipts ride on PROGRESS

// A tuning link over the radios' own FFSK side channel — independent of the NinoTNC
// mode/deviation under negotiation. peerId is the peer's 8-character Tait SDM identity.
await using var link = SdmTuningLink.Create(radio, peerId: "00000002");

// Renegotiate the TNC mode over that side channel: propose -> confirm -> commit,
// probe-verify both directions, revert both ends on any failure past the commit.
var station = new NinoTncModeCoordStation(tnc, radio, Callsign.Parse("N0CALL"), initialMode: 6);
await using var coordinator = new ModeCoordinator(link, station) { Log = Console.WriteLine };

ModeCoordAttempt attempt = await coordinator.CoordinateAsync(mode: 2);
Console.WriteLine(attempt.Outcome);   // Switched, ProbeDead, Rejected, LinkFailed, ...
```

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio) — the `IRadioSideChannel` seam the SDM link rides
- [`Packet.Radio.Tait`](https://www.nuget.org/packages/Packet.Radio.Tait) — the Tait CCDI SDM side channel
- [`Packet.Kiss.NinoTnc`](https://www.nuget.org/packages/Packet.Kiss.NinoTnc) — the NinoTNC driver the meter / mode-coordination station drives

Status: **experimental** — spike-born (plan §5.10 Phase 10), hardware-validated on a 2× NinoTNC + 2× Tait TM8110 bench rig, and still moving as the mode-agility workstream matures.
