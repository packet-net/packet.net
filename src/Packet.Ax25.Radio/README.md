# Packet.Ax25.Radio

> What a radio's control channel is worth to an AX.25 link: per-frame signal metadata, and real RF carrier-sense feeding the CSMA gate.

[`M0LTE.Radio`](https://www.nuget.org/packages/M0LTE.Radio) models the control channel to a radio (RSSI, hardware DCD, PTT) without knowing anything about packet. This package is the adapter layer that brings it into the AX.25 stack.

## Install
```sh
dotnet add package Packet.Ax25.Radio
```

## Types

- **`RssiTaggingTransport`** - an `IAx25Transport` decorator. A background sampler polls RSSI (fast while the channel is busy, slow while idle so the idle samples track the noise floor) and re-yields every inbound frame with `Ax25InboundFrame.Radio` populated: RSSI median/min/max/sample-count, SNR, noise floor, carrier-rise instant, burst index (an AX.25 frame train shares one carrier), airtime estimate, and, for the first frame of a burst, the measured pre-data carrier time, which is the sender's effective TXDELAY and an excess-TXDELAY detector input. A frame with no qualifying sample gets `null` metadata, never a guess.
- **`RadioCarrierSense`** - CSMA by hardware DCD, done *natively* by the stack. It bridges the radio's `ChannelBusy` onto the neutral `ICarrierSense` seam that `Ax25Listener` consults before every keyup, via `Ax25ListenerOptions.CarrierSense`. The listener's own `CarrierSenseGate` holds the keyup while the channel is busy (bounded wait, fail-open), so the medium-access deferral lives in the stack rather than an opaque transport wrapper, and composes with the TNC's own persistence CSMA.

Hardware DCD typically calls the channel busy 0.5-1 s before the modem finishes demodulating the frame, which is exactly the head start a CSMA gate wants.

## Usage

```csharp
using M0LTE.Radio.Tait;
using Packet.Ax25.Radio;

await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0");
await radio.SetProgressMessagesAsync(true);                   // turn on DCD events

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM1"); // from Packet.Kiss.NinoTnc
await using var tagged = new RssiTaggingTransport(tnc, radio);

var listener = new Ax25Listener(tagged, new Ax25ListenerOptions
{
    CarrierSense = new RadioCarrierSense(radio),
});

await foreach (var frame in tagged.ReceiveAsync(ct))
{
    // frame.Radio?.RssiDbm / frame.Radio?.SnrDb now populated
}
```

With carrier-sense, frames are attributed to the transmission window containing their arrival. Without it, `RssiTaggingTransport` falls back to a threshold-over-noise-floor filter and the window-derived fields (carrier-rise, burst index, pre-data carrier) stay `null`. `NoiseFloorDbm` exposes the live idle-sample estimate.

Both types leave ownership of the inner transport and the radio with the caller; disposing the decorator only stops its sampler.

## See also
- [`M0LTE.Radio`](https://www.nuget.org/packages/M0LTE.Radio) - the radio-control contract this adapts
- [`Packet.Ax25.Radio.Tait`](https://www.nuget.org/packages/Packet.Ax25.Radio.Tait) - AX.25 over a Tait's own FFSK modem, no TNC at all
- [Source & issues](https://github.com/packet-net/packet.net)

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
