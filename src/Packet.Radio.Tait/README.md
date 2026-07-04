# Packet.Radio.Tait

> Tait TM8100/TM8200 mobile-radio control over CCDI — the driver that gives the Packet.NET stack RSSI, hardware carrier-sense, PTT, and a radio-native side channel.

A [`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio) `IRadioControl` implementation for Tait TM8100/TM8200 radios over CCDI (the Computer-Controlled Data Interface — the radio's serial command protocol). Wire it to `RssiTaggingTransport` / `CarrierSenseTxGate` and a bare KISS packet link gains per-frame signal metadata and hardware CSMA. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Radio.Tait
```

## What it surfaces that a bare KISS modem cannot

- **RSSI in dBm** (CCTM queries 063/064, 0.1 dB resolution) — feed `RssiTaggingTransport` to stamp per-frame RSSI/SNR onto received AX.25 frames.
- **Hardware carrier-sense** — unsolicited PROGRESS "receiver busy / not busy" messages become `CarrierSenseChanged` events + a `ChannelBusy` property (a true RF-level DCD).
- **Transmitter keying** (`SetTransmitterAsync`) — CCDI-forced TX ignores the radio's TX timer, so the driver unkeys on dispose if you left it keyed through it.
- **Telemetry + health** — PA temperature (CCTM 047), forward/reverse power detector readings (CCTM 318/319, an antenna-health proxy while transmitting), and a periodic **`TaitRadioHealthMonitor`** that trends them: idle-offset-corrected fwd/rev + ratio (a TREND, never VSWR — the detectors are raw √P-scaled millivolts per Tait's service docs), typed sample events + rolling min/median/max summaries.
- **Identity** — model/tier, CCDI version, serial number, firmware/hardware version inventory.
- **An escape hatch** (`TransactRawAsync`) for CCDI commands the driver doesn't model yet — framing and checksumming handled, responses returned decoded.

## Usage
```csharp
await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0"); // 28800 8N1 default
await radio.SetProgressMessagesAsync(true);                 // per-session: enables DCD events

var id = await radio.QueryIdentityAsync();                  // e.g. "Tait TM8110", serial, versions
float rssi = await radio.ReadRssiDbmAsync();                // e.g. -90.3
radio.CarrierSenseChanged += (_, e) => Console.WriteLine($"DCD {(e.Busy ? "up" : "down")} at {e.At:O}");
```

The radio must be programmed with its data port in **Command mode** (the power-up state) at the matching baud rate. (For the TNC-less FFSK link, `TaitTransparentTransport` drives the Transparent-mode enter/escape for you — see below.)

## TNC-less AX.25: the FFSK Transparent transport

`TaitTransparentTransport` is an `IAx25Transport` whose modem **is** the radio — no external TNC. It puts the radio into Transparent mode (the radio's own FFSK modem as an 8-bit-clean byte pipe), frames AX.25 with **KISS SLIP framing** over that pipe, and de-frames the inbound byte stream back into whole AX.25 frames (the radio fragments/reassembles ≤46-byte over-air blocks itself). Because the transport *owns* the transmission it times it directly: a `TxTiming` event and `ITxCompletionTransport` give per-frame on-air start/end, and inbound frames carry `ReceivedAt` + `RadioMetadata.EstimatedAirtime`.

```csharp
await using var link = await TaitTransparentTransport.OpenAsync("/dev/ttyUSB0");
await link.SendAsync(ax25FrameBody);            // SLIP-framed over the FFSK pipe
await foreach (var f in link.ReceiveAsync(ct))  // whole AX.25 frames, ReceivedAt + airtime stamped
    Handle(f.Ax25, f.ReceivedAt, f.Radio?.EstimatedAirtime);
// DisposeAsync escapes Transparent (+++) and restores Command mode.
```

The inherent trade-off vs the `RssiTaggingTransport` (NinoTNC modem + CCDI control channel) arrangement: **one device, no audio wiring, but no signal telemetry** — RSSI/SNR/noise-floor/DCD are unavailable while the CCDI channel is a byte pipe (those `RadioMetadata` fields stay null; only airtime is known). ⚠ If the radio is programmed with "Ignore Escape Sequence" **on**, the `+++` exit cannot succeed and recovery is a power cycle — program the escape sequence honoured before running it unattended.

## Beyond telemetry

The driver models the rest of the documented surface: channel report/change (`QueryCurrentChannelAsync` / `GoToChannelAsync`), CANCEL / DIAL, and **SDM short-data messages** — radio-to-radio, no TNC: plain 32-character (`SendSdmAsync`) and extended 128-character (`SendExtendedSdmAsync`), requiring SDMs enabled in the radio's programming. `TaitSdmSideChannel` exposes SDMs as a `Packet.Radio.IRadioSideChannel`, the mode-agnostic coordination plane the tuning / mode-negotiation stack rides. Also: display query, Transparent mode (the radio's own FFSK/THSD modem as a byte pipe), a keep-alive **watchdog** (`ConnectionState` + events; probes on link silence, self-heals on recovery), **port auto-detection** (`TaitRadioPortDiscovery` — probes candidate ports with a MODEL query and identifies radios by CCDI serial number), and the whole **CCR mode** (`TaitCcrSession`, TM8100 only): run-time RX/TX frequency in Hz, TX power, bandwidth, CTCSS/DCS, Selcall encode/decode events, volume, and the pulse ping.

## CCR-over-SDM ⚠ experimental / unsafe

`UnsafeSendCcrOverSdmAsync` transmits a CCR command *into another radio* over the air — remote control that can retune, re-power, or key the target, with **no consent handshake in the protocol**. It is `[Experimental]` (`PKTTAIT001`) and carries the `Unsafe` prefix deliberately: a radio not already in CCR mode simply ignores it (immune), but any real deployment needs an application-layer consent/auth gate first — keep it to bench tooling and radios you own. See the [CCDI spike doc](https://github.com/packet-net/packet.net/blob/main/docs/research/tait-ccdi-spike.md).

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio) — the `IRadioControl` contract this implements
- [`Packet.Tune.Core`](https://www.nuget.org/packages/Packet.Tune.Core) — link-tuning + mode coordination over the SDM side channel

Verified on hardware: 2× TM8110 (`TMAB12-B100`, CCDI 03.02, firmware 02.18.00.00). On that firmware the CCDI-side TX-power set (FUNCTION 0/7) answers "unsupported command" — but the CCR-mode power command works, so power control lives on `TaitCcrSession`.

Status: **experimental**, spike-born (plan §5.10 Phase 10). Protocol reference: Tait MMA-00038-06 "TM8100/TM8200 CCDI Protocol Manual".
