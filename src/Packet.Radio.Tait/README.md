# Packet.Radio.Tait

> Tait TM8100/TM8200 mobile-radio control over CCDI — the driver that gives the Packet.NET stack RSSI, hardware carrier-sense, PTT, and a radio-native side channel.

A [`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio) `IRadioControl` implementation for Tait TM8100/TM8200 radios over CCDI (the Computer-Controlled Data Interface — the radio's serial command protocol). Wire it to `RssiTaggingTransport` and `RadioCarrierSense` (the `ICarrierSense` bridge the AX.25 stack's native CSMA gate consults) and a bare KISS packet link gains per-frame signal metadata and hardware carrier-sense CSMA. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

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
- **A station-control view** (`TaitRigControl`) — re-presents the radio through the [`Packet.Rig`](https://www.nuget.org/packages/Packet.Rig) `IRigControl` abstraction (the same seam the hamlib/rigctld and flrig backends implement), advertising the slice CCDI can honestly serve: PTT set/get and a relative RF-power meter (the CCTM 318 forward detector over its full scale). Frequency, mode, SWR and watts are deliberately unadvertised — the tuned frequency isn't CCDI-readable, the radio has no mode concept, and the power detectors are raw √P-scaled millivolts, not calibrated units. Cross-backend rig consumers feature-probe `RigCapabilities` and get exactly what's real.

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

### ⚠ SDM delivery receipts are unreliable for close bidirectional traffic

The over-air SDM **delivery receipt** (CCDI PROGRESS `1D`, para `1`=ack / `0`=nak) is not a
dependable delivery signal when two radios exchange SDMs back-and-forth within a few seconds — as a
coordination protocol does. Bench-characterised on 2× TM8110 (CCDI 03.02): a radio captures its
send's receipt **only if** it has not transmitted an SDM auto-acknowledge since its previous send
**and** ≥~9 s have elapsed since its last auto-ack; otherwise it reports NAK after the ~6 s timeout.
Crucially the **SDM payload is delivered every time regardless** — only the receipt is lost. Full
characterisation and proof: [`docs/research/tm8110-sdm-autoack-refractory.md`](https://github.com/packet-net/packet.net/blob/main/docs/research/tm8110-sdm-autoack-refractory.md).

Guidance: treat the receipt as an **optimistic fast-path only** — never fail delivery on its
absence. For reliability, confirm at the application layer (the peer's reply). Auto-ack itself is a
**codeplug (programming-application) setting, not a runtime toggle** — there is no `f`-command to
disable it; a radio you own can have "SDM Auto Acknowledge" turned off in its codeplug to remove the
effect and save the ack airtime, but you cannot assume that on radios you don't program.

## CCR-over-SDM ⚠ experimental / unsafe

`UnsafeSendCcrOverSdmAsync` transmits a CCR command *into another radio* over the air — remote control that can retune, re-power, or key the target, with **no consent handshake in the protocol**. It is `[Experimental]` (`PKTTAIT001`) and carries the `Unsafe` prefix deliberately: a radio not already in CCR mode simply ignores it (immune), but any real deployment needs an application-layer consent/auth gate first — keep it to bench tooling and radios you own. See the [CCDI spike doc](https://github.com/packet-net/packet.net/blob/main/docs/research/tait-ccdi-spike.md).

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio) — the `IRadioControl` contract this implements
- [`Packet.Tune.Core`](https://www.nuget.org/packages/Packet.Tune.Core) — link-tuning + mode coordination over the SDM side channel

Verified on hardware: 2× TM8110 (`TMAB12-B100`, CCDI 03.02, firmware 02.18.00.00). On that firmware the CCDI-side TX-power set (FUNCTION 0/7) answers "unsupported command" — but the CCR-mode power command works, so power control lives on `TaitCcrSession`.

Status: **experimental**, spike-born (plan §5.10 Phase 10). Protocol reference: Tait MMA-00038-06 "TM8100/TM8200 CCDI Protocol Manual".
