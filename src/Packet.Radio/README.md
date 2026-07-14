# Packet.Radio

> The radio-control abstraction for Packet.NET — what a serial control channel to the radio *behind* the modem adds to a packet link, expressed protocol-neutrally.

Standard KISS gives you demodulated frames and nothing else. A radio with a control channel (Tait CCDI, Yaesu CAT, ICOM CI-V, …) can also report signal strength, tell you the moment the channel goes busy, and key its transmitter — and this package is the driver-neutral seam that surfaces those to the AX.25 stack. It is the **base contract the concrete drivers implement** (see [`Packet.Radio.Tait`](https://www.nuget.org/packages/Packet.Radio.Tait)); applications code against the interfaces here. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Radio
```

## What a control channel gives you that raw KISS can't

- **RSSI in dBm** — attribute signal strength (and SNR against a tracked noise floor) to every received frame.
- **Hardware carrier-sense (DCD)** — the channel is busy *now*, typically 0.5–1 s before the modem finishes demodulating the frame on air: exactly the head start CSMA wants.
- **Transmitter keying** — PTT under software control, independent of the modem's PTT line.
- **A radio-native side channel** — small control datagrams that bypass the audio modem entirely, so they keep working while the link they sit beside is being reconfigured.

## Types

- `IRadioControl` — the capability-probed contract: `ReadRssiDbmAsync`, `SetTransmitterAsync`, `ChannelBusy` + `CarrierSenseChanged`, and a `RadioCapabilities` flags enum. Drivers advertise only what their radio/firmware actually supports; reserved flags (channel change, frequency, TX power) exist so richer radios can be described before the interface grows those members.
- `RssiTaggingTransport` — an `IAx25Transport` decorator: a background sampler polls RSSI (fast while the channel is busy, slow while idle so the idle samples track the noise floor) and re-yields every inbound frame with `Ax25InboundFrame.Radio` populated — RSSI median/min/max/sample-count, SNR, noise floor, carrier-rise instant, burst index (an AX.25 frame train shares one carrier), airtime estimate, and — for the first frame of a burst — the measured pre-data carrier time, the sender's effective TXDELAY (an excess-TXDELAY detector input). Frames with no qualifying sample get `null` metadata, never a guess.
- `RadioCarrierSense` — CSMA by hardware DCD, done *natively* by the AX.25 stack: bridges the radio's `ChannelBusy` onto the neutral `ICarrierSense` seam (`Packet.Ax25.Transport.Abstractions`) that `Ax25Listener` consults before every keyup (via the `Ax25ListenerOptions.CarrierSense` option). The listener's own `CarrierSenseGate` holds the keyup while the channel is busy (bounded wait, fail-open) — the medium-access deferral lives in the stack, not an opaque transport wrapper, and composes with the TNC's own persistence CSMA.
- `RigRadioControl` — the rig→radio bridge: surfaces a CAT rig ([`Packet.Rig`](https://www.nuget.org/packages/Packet.Rig)'s `IRigControl` — hamlib `rigctld`, flrig) as this package's `IRadioControl`, so a CAT transceiver feeds the same CSMA gate and per-frame-RSSI machinery a push-capable radio does. Capabilities map at construction (`DcdRead → CarrierSense`, `SignalStrengthRead → RssiRead`, `PttSet → TransmitterControl`); a rig offering none of the three is rejected. Rig backends are poll-only, so carrier-sense edges are *synthesized* by an owned DCD poll loop (100 ms default) — edges shorter than the poll interval are invisible, coarser than a true push source. A failed read fails open (`ChannelBusy = null`) and backs off to a slower retry cadence until the backend self-heals. `ownsRig: true` hands the rig's lifetime to the adapter (dispose stops polling, then disposes the rig); the default `false` leaves the rig with the caller, and dispose best-effort unkeys anything the adapter left keyed.
- `IRadioSideChannel` — a small-datagram control plane the radio itself provides (e.g. Tait SDM over the radios' internal FFSK modem): `SendAsync` / `ReadBufferedAsync` short payloads with over-air delivery receipts and a `MaxPayloadLength` budget. Because it bypasses the audio modem it is mode/deviation/channel-width-agnostic — the coordination channel for renegotiating the very link it sits beside (mode agility, remote tuning). Drivers advertise the machinery via `RadioCapabilities.SideChannel`; consumers must still probe that it is enabled in the radio's programming before gating features on it.

## Usage

```csharp
await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0");   // from Packet.Radio.Tait
await radio.SetProgressMessagesAsync(true);                   // turn on DCD events

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM1"); // from Packet.Kiss.NinoTnc
await using var tagged = new RssiTaggingTransport(tnc, radio);

await foreach (var frame in tagged.ReceiveAsync(ct))
{
    // frame.Radio?.RssiDbm / frame.Radio?.SnrDb now populated
}
```

With carrier-sense, frames are attributed to the transmission window that contains their arrival; without it, `RssiTaggingTransport` falls back to a threshold-over-noise-floor filter and the window-derived fields (carrier-rise, burst index, pre-data carrier) stay `null`. `NoiseFloorDbm` exposes the live idle-sample estimate. Both decorators leave ownership of the inner transport and the radio with the caller — disposing the decorator only stops its sampler/gate.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [`Packet.Radio.Tait`](https://www.nuget.org/packages/Packet.Radio.Tait) — the Tait TM8100/TM8200 CCDI implementation of this contract
- [`Packet.Tune.Core`](https://www.nuget.org/packages/Packet.Tune.Core) — link-tuning + mode coordination over `IRadioSideChannel`
- [`Packet.Kiss.NinoTnc`](https://www.nuget.org/packages/Packet.Kiss.NinoTnc) — the NinoTNC `IAx25Transport` these decorators wrap

Status: **experimental** — the `IRadioControl` shape is plan OQ-011's proposed common subset {RSSI-get, busy-get, PTT-set} and may move as second/third implementations (Yaesu CAT, ICOM CI-V) land.
