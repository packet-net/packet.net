# Packet.Radio

The radio-control abstraction for the Packet.NET stack: what a serial control channel to the
radio *behind* the modem can add to a packet link, expressed protocol-neutrally.

Standard KISS gives you demodulated frames and nothing else. A radio with a control channel
(Tait CCDI, Yaesu CAT, ICOM CI-V, …) can also tell you:

- **RSSI in dBm** — attribute signal strength (and SNR against a tracked noise floor) to every
  received frame;
- **hardware carrier-sense (DCD)** — the channel is busy *now*, typically 0.5–1 s before the
  modem finishes demodulating the frame that's on air, which is exactly the head start CSMA
  wants;
- **transmitter keying** — PTT under software control, independent of the modem's PTT line.

## Types

- `IRadioControl` — the capability-probed contract: `ReadRssiDbmAsync`, `SetTransmitterAsync`,
  `ChannelBusy` + `CarrierSenseChanged`, and a `RadioCapabilities` flags enum. Drivers advertise
  only what their radio/firmware actually supports; reserved flags (channel change, frequency
  control, TX power) exist for richer radios before the interface grows those members.
- `RssiTaggingTransport` — a decorator over any `IAx25Transport`: a background sampler polls
  RSSI (fast while the channel is busy, slow while idle — idle samples track the noise floor)
  and every inbound frame is re-yielded with `Ax25InboundFrame.Radio` populated (RSSI dBm +
  SNR dB). Attribution is timestamp-correlation against the frame's `ReceivedAt`; frames with
  no qualifying sample get `null` metadata, never a guess.

## Usage

```csharp
await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0");   // from Packet.Radio.Tait
await radio.SetProgressMessagesAsync(true);                    // turn on DCD events

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM1");  // from Packet.Kiss.NinoTnc
await using var tagged = new RssiTaggingTransport(tnc, radio);

await foreach (var frame in tagged.ReceiveAsync(ct))
{
    // frame.Radio?.RssiDbm / frame.Radio?.SnrDb now populated
}
```

Implementations: [`Packet.Radio.Tait`](https://www.nuget.org/packages/Packet.Radio.Tait)
(Tait TM8100/TM8200 over CCDI).

Status: **experimental** — the `IRadioControl` shape is plan OQ-011's proposed common subset and
may move as second/third implementations (Yaesu CAT, ICOM CI-V) land.
