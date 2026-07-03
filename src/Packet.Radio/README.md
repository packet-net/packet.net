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
  RSSI (fast while the channel is busy, slow while idle — idle samples track the noise floor),
  carrier-sense edges are tracked as transmission windows, and every inbound frame is
  re-yielded with `Ax25InboundFrame.Radio` populated: RSSI median/min/max/sample-count, SNR,
  noise floor, carrier-rise instant, burst index (AX.25 frame trains share one carrier), an
  airtime estimate, and — for the first frame of a burst — the measured pre-data carrier time,
  which is the transmitting station's effective TXDELAY (an **excess-TXDELAY detector** input).
  Frames with no qualifying sample get `null` metadata, never a guess.
- `CarrierSenseTxGate` — CSMA by hardware DCD: defers `SendAsync` while the radio reports the
  channel busy (bounded wait, fail-open), composing with the TNC's own persistence CSMA.
- `IRadioSideChannel` — a small-datagram control plane the radio itself provides (e.g. Tait
  SDM over the radios' internal FFSK modem): send/receive short payloads with over-air
  delivery confirmation and a `MaxPayloadLength` budget. Because it bypasses the audio-path
  modem entirely it is mode/deviation/channel-width-agnostic — the coordination channel for
  renegotiating the very link it sits beside (mode agility, remote tuning). Drivers advertise
  the machinery via `RadioCapabilities.SideChannel`; consumers must still probe that the
  feature is enabled in the radio's programming before gating features on it.

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
