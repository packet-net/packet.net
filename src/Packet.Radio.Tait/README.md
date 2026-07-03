# Packet.Radio.Tait

Tait TM8100/TM8200 mobile-radio control over CCDI (the Computer-Controlled Data Interface, the
radio's serial command protocol), implementing
[`Packet.Radio`](https://www.nuget.org/packages/Packet.Radio)'s `IRadioControl`.

What it surfaces that a bare KISS modem cannot:

- **RSSI in dBm** (CCTM queries 063/064, 0.1 dB resolution) — feed `RssiTaggingTransport` to
  stamp per-frame RSSI/SNR onto received AX.25 frames;
- **hardware carrier-sense** — unsolicited PROGRESS "receiver busy / not busy" messages become
  `CarrierSenseChanged` events + a `ChannelBusy` property (a true RF-level DCD);
- **transmitter keying** (`SetTransmitterAsync`) — CCDI-forced TX ignores the radio's TX timer,
  so the driver unkeys on dispose if you left it keyed through it;
- **telemetry** — PA temperature (CCTM 047), forward/reverse power detector readings
  (CCTM 318/319, a VSWR/antenna-health proxy while transmitting);
- **identity** — model/tier, CCDI version, serial number, firmware/hardware version inventory;
- an **escape hatch** (`TransactRawAsync`) for CCDI commands the driver doesn't model yet —
  framing and checksumming handled, responses returned decoded.

## Usage

```csharp
await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0"); // 28800 8N1 default
await radio.SetProgressMessagesAsync(true);                 // per-session: enables DCD events

var id = await radio.QueryIdentityAsync();                  // e.g. "Tait TM8110", serial, versions
float rssi = await radio.ReadRssiDbmAsync();                // e.g. -90.3
radio.CarrierSenseChanged += (_, e) => Console.WriteLine($"DCD {(e.Busy ? "up" : "down")} at {e.At:O}");
```

The radio must be programmed with its data port in **Command mode** (power-up state) at the
matching baud rate; this driver does not attempt the Transparent-mode escape sequence.

Beyond telemetry, the driver models the rest of the documented surface: channel report/change,
CANCEL/DIAL, SDM short-data messages (radio-to-radio, no TNC — requires SDMs enabled in the
radio's programming; `TaitSdmSideChannel` exposes them as `Packet.Radio.IRadioSideChannel`,
the mode-agnostic coordination plane the tuning/mode-negotiation stack rides), display query, Transparent mode (the radio's own FFSK/THSD modem as a
byte pipe), a built-in keep-alive **watchdog** (`ConnectionState` + events; probes on link
silence, self-heals on recovery), **port auto-detection** (`TaitRadioPortDiscovery` — probes
candidate ports with a MODEL query and identifies radios by CCDI serial number), and the whole
**CCR mode** (`TaitCcrSession`, TM8100 only): run-time RX/TX frequency in Hz, TX power,
bandwidth, CTCSS/DCS, Selcall encode/decode events, volume, and the pulse ping.

Verified on hardware: 2× TM8110 (`TMAB12-B100`, CCDI 03.02, firmware 02.18.00.00). On that
firmware the CCDI-side TX-power set (FUNCTION 0/7) answers "unsupported command" — but the
CCR-mode power command works, so power control lives on `TaitCcrSession`.

Status: **experimental**, spike-born (plan §5.10 Phase 10). Protocol reference: Tait
MMA-00038-06 "TM8100/TM8200 CCDI Protocol Manual".
