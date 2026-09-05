# Packet.Ax25.Radio.Tait

> AX.25 over a Tait TM8100/TM8200's own FFSK modem. One device, no TNC, no audio wiring.

`TaitTransparentTransport` is an `IAx25Transport` whose modem **is** the radio. It puts the radio into Transparent mode (its internal FFSK modem as an 8-bit-clean byte pipe), frames AX.25 with KISS SLIP framing over that pipe, and de-frames the inbound byte stream back into whole AX.25 frames. The radio fragments and reassembles the over-air blocks itself.

## Install
```sh
dotnet add package Packet.Ax25.Radio.Tait
```

## Usage

```csharp
using Packet.Ax25.Radio.Tait;

await using var link = await TaitTransparentTransport.OpenAsync("/dev/ttyUSB0");

await link.SendAsync(ax25FrameBody);            // SLIP-framed over the FFSK pipe
await foreach (var f in link.ReceiveAsync(ct))  // whole frames, ReceivedAt + airtime stamped
{
    Handle(f.Ax25, f.ReceivedAt, f.Radio?.EstimatedAirtime);
}
// DisposeAsync escapes Transparent (+++) and restores Command mode.
```

Because the transport *owns* the transmission it times it directly: a `TxTiming` event and `ITxCompletionTransport` give per-frame on-air start and end, and inbound frames carry `ReceivedAt` plus `RadioMetadata.EstimatedAirtime`.

## The trade-off against a separate modem

One device and no audio wiring, but **no signal telemetry**. While the CCDI channel is acting as a byte pipe, RSSI, SNR, noise floor and DCD are unavailable, so those `RadioMetadata` fields stay `null` and only airtime is known. If you want per-frame signal metadata, use a separate modem with [`Packet.Ax25.Radio`](https://www.nuget.org/packages/Packet.Ax25.Radio)'s `RssiTaggingTransport` over the CCDI control channel instead.

**Before running this unattended**, check the radio is not programmed with "Ignore Escape Sequence" **on**. If it is, the `+++` exit cannot succeed and recovery is a power cycle.

## See also
- [`M0LTE.Radio.Tait`](https://www.nuget.org/packages/M0LTE.Radio.Tait) - the CCDI driver underneath, which also gives you RSSI, DCD, telemetry and SDM
- [`Packet.Ax25.Radio`](https://www.nuget.org/packages/Packet.Ax25.Radio) - the separate-modem arrangement, with full signal metadata
- [Source & issues](https://github.com/packet-net/packet.net)

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
