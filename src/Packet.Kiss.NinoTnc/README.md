# Packet.Kiss.NinoTnc

> NinoTNC (N9600A) driver — the firmware-specific overlay on top of Packet.Kiss.

A thin driver over [`Packet.Kiss`](https://www.nuget.org/packages/Packet.Kiss) / [`Packet.Kiss.Serial`](https://www.nuget.org/packages/Packet.Kiss.Serial) that adds the bits specific to the NinoTNC firmware: the `SETHW` mode-selection byte semantics, the synthetic TX-Test diagnostic frame, USB VID/PID port discovery, and a firmware catalogue. All the *generic* KISS surface (the `IAx25Transport` seam, the typed inbound-event hierarchy, ACKMODE TX-completion correlation, the adaptive estimators) lives in `Packet.Kiss` and works with any KISS modem. Part of [Packet.NET](https://github.com/packet-net/packet.net), a .NET amateur-radio / AX.25 packet stack.

## Install
```sh
dotnet add package Packet.Kiss.NinoTnc
```

## Quick start
`NinoTncSerialPort` opens the USB-CDC port (57600 8N1), is an `IAx25Transport` / `ITxCompletionTransport` / `ICsmaChannelParams`, and raises a typed `InboundEvent` for every classified inbound frame.

```csharp
using Packet.Core;
using Packet.Ax25;
using Packet.Kiss;
using Packet.Kiss.NinoTnc;

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM0"); // or "COM6"
await tnc.SetModeAsync(mode: 6);                               // 1200 AFSK AX.25
                                                              // (non-persistent by default)

tnc.InboundEvent += (_, evt) =>
{
    switch (evt)
    {
        case Ax25FrameReceivedEvent rx:
            Console.WriteLine($"{rx.Ax25.Source} -> {rx.Ax25.Destination}");
            break;
        case NinoTncTxTestFrameReceivedEvent diag:
            Console.WriteLine($"TX-Test button: firmware {diag.Diagnostic.FirmwareVersionRaw}, " +
                              $"running {diag.Diagnostic.RunningMode?.Name}");
            break;
        case AckModeDataReceivedEvent ack:
            Console.WriteLine($"ACKMODE inbound, tag 0x{ack.SequenceTag:X4}");
            break;
    }
};

// Outbound UI frame (placeholder callsigns only).
var ui = Ax25Frame.Ui(
    destination: new Callsign("Q0PDN"),
    source: new Callsign("N0CALL", 1),
    info: "hello"u8);
await tnc.SendFrameAsync(ui.ToBytes());
```

## `SetModeAsync` and flash wear
`SetModeAsync(mode)` defaults to **non-persistent**: it applies the `+16` offset so the TNC's flash is not touched and the mode reverts on reboot. Pass `persistToFlash: true` only when the operator wants the choice to survive a power cycle — flash has a finite write-cycle budget. The same logic is exposed standalone via `NinoTncSetHardware.BuildPayloadByte` / `BuildKissFrame`.

## ACKMODE TX-completion
On slow HF modes (300/600 bps) the gap between "frame accepted by the TNC" and "frame on the air" matters. `SendFrameWithAckAsync` sends a G8BPQ ACKMODE frame (KISS command `0x0C`), correlates the TNC's echo by a 16-bit sequence tag, and returns a `TxCompletion` timing the round trip:

```csharp
var receipt = await tnc.SendFrameWithAckAsync(ui.ToBytes(), timeout: TimeSpan.FromSeconds(30));
Console.WriteLine($"tx-complete after {receipt.Elapsed.TotalMilliseconds:F0} ms");
```

The driver auto-assigns tags from an internal counter (skipping `0x0000`) and correlates concurrent sends through a thread-safe dictionary; you can pin the tag if you need to. `SendAwaitingCompletionAsync` is the neutral `ITxCompletionTransport` form. An ACKMODE-Data frame *received from* a peer surfaces as `AckModeDataReceivedEvent` on `InboundEvent`.

## Adaptive parameters
Drop the TNC into the generic `Packet.Kiss.AdaptiveKissTransport` — it works because `NinoTncSerialPort` implements both `ITxCompletionTransport` and `ICsmaChannelParams`:

```csharp
using Packet.Kiss; // AdaptiveKissTransport
using Packet.Kiss.Adaptive;

var estimator = new CompositeAdaptiveEstimator(
    new TxDelayHillClimbEstimator(initialTxDelay: 50),
    new CsmaContentionEstimator(initialPersistence: 63, initialSlotTime: 10));

await using var transport = new AdaptiveKissTransport(tnc, estimator);
await transport.SendAsync("N0CALL-9", ui.ToBytes());

// AX.25-layer signals, when the session machine knows a frame was lost / retransmitted:
transport.RecordRetransmittedAck("N0CALL-9", payloadBytes: 100);
transport.RecordLoss("N0CALL-9", payloadBytes: 100);
```

## TX-Test diagnostic frame
Pressing the front-panel TX-Test button transmits a test signal *and* sends a synthetic KISS data frame to the host carrying firmware version, identity, uptime, counters, and the running mode. `NinoTncFrameClassifier` recognises it (via the `=FirmwareVr:` marker) and raises `NinoTncTxTestFrameReceivedEvent`. The separate over-air UI frame heard when a *partner* presses *their* button is raised as `NinoTncAirTestFrameReceivedEvent` (partner's learned callsign, per-press sequence counter, deterministic ASCII pattern).

## Firmware queries + remote diagnostics
The firmware also answers host-side query commands (NinoTNC extensions, not standard KISS — payload builders in `NinoTncCommands`, replies on the raw command byte `0xE0`):

```csharp
var status  = await tnc.GetAllAsync();      // GETALL → NinoTncStatusFrame (registers 00–11)
var version = await tnc.GetVersionAsync();  // GETVER → "3.41"
var levelDb = await tnc.GetRssiAsync();     // GETRSSI → RX-audio RMS level in dB (not dBm!)
await tnc.StopTxAsync();                    // STOPTX
await tnc.SetBeaconIntervalAsync(5);        // SETBCNINT, minutes
```

`NinoTncStatusFrame` is the numeric `=II:HEXDATA` register report — the TNC also emits it spontaneously as a periodic status frame (default every 60 s; SETBCNINT re-paces it), and `NinoTncStatusDelta.Between(before, after)` turns two snapshots into per-register deltas (e.g. preamble words → effective TXDELAY seconds). On firmware 3.41 GETALL answers with the *labelled* diagnostic instead; `GetAllAsync` maps it so callers see one shape either way. GETRSSI is bench-calibrated as an RX-audio RMS meter (open-squelch flat-tap noise ≈ −33 dB, a carrier quieting the channel with a 440 Hz tone ≈ −62 dB) — a remote level/deviation meter, not an RF signal-strength report.

For remote audio tuning the firmware ships a **CQBEEP responder**: arm a TNC by transmitting a `[TARPNstat` status frame through it (`ArmCqBeepResponderAsync`; volatile, re-arm after reset), and it answers any received `CQBEEP-N` UI frame with N seconds of 440 Hz tone (bench: N=7 → 6.99 s). `NinoTncCqBeep` builds both frames; `SendCqBeepRequestAsync` triggers a beep. `tools/Packet.Tune` drives the whole loop (verify software control, level survey, interactive TX-deviation tuning).

## Port discovery
```csharp
foreach (var c in NinoTncPortDiscovery.EnumerateCandidates())
    Console.WriteLine($"{c.PortName}  ({c.ResolvedDevicePath})");
```

- **Windows** — walks the registry USB enum for the known VID/PID (`04D8:00DD`, the Microchip USB-CDC reference) and reads each match's `Device Parameters\PortName`.
- **Linux** — prefers stable `/dev/serial/by-id/...` symlinks, falls back to `/dev/ttyACM*`.
- **macOS / fallback** — `SerialPort.GetPortNames()`.

The VID/PID is shared with other Microchip-CDC reference projects, so a match is "might be a NinoTNC", not "definitely is" — the TX-Test frame is the authoritative confirmation. `NinoTncPortDiscovery.PortsEnvVar` (`PACKETNET_NINOTNC_PORTS`, a comma-separated list) is the final override.

## Firmware catalogue
`Packet.Kiss.NinoTnc.Firmware` models what's running, whether a newer image exists, and performs the flash itself. `GitHubNinoTncFirmwareCatalogue.CheckForUpdateAsync(version)` reads the upstream `flashtnc` repo and surfaces the current release per chip variant; the firmware major (`3.xx` / `4.xx`) selects the dsPIC variant, and flashing the wrong image bricks the modem until ICSP recovery.

## Firmware flashing
`BootloaderNinoTncFirmwareFlasher` (behind the `INinoTncFirmwareFlasher` seam) is a native C# port of the dsPIC bootloader protocol upstream `flashtnc.py` speaks, byte-compatible with the hardware-validated sequence: drain-to-silence, stranded-bootloader probe (`'R'`→`'K'`), triple GETALL fill-and-flush, bootloader entry (`C0 0D 37 C0`), one-letter version/chip check (lowercase = EP256, uppercase = EP512, mismatch refused), then the Intel-HEX image line-by-line (first line char-by-char at 100 ms — page erase) with per-line `K`/`Z`/`F`/`N`/`X` replies. `NinoTncFirmwareHexImage` validates + chip-classifies the image before the modem is touched; failures throw `NinoTncFlashException` with the terminal state classified (`NinoTncFlashFailure`); progress is reported per accepted line (`NinoTncFlashProgress`). **An interrupted flash strands the modem in the bootloader** (recoverable: re-run the flash — the stranded probe resumes). After success the modem reboots (first boot: ~2 s bootloader self-update) and its RAM mode resets to 0 — re-apply via `SetModeAsync`. `UnsupportedFirmwareFlasher` remains for hosts that must refuse to flash.

## Key types
- `NinoTncSerialPort` — the driver: `IAx25Transport` + `ITxCompletionTransport` + `ICsmaChannelParams`, with `SetModeAsync` and `SendFrameWithAckAsync`.
- `NinoTncSetHardware` — builds the `SETHW` payload byte / KISS frame (mode 0–15, `+16` non-persist offset).
- `NinoTncFrameClassifier` — overlays the generic classifier and upgrades TX-Test / over-air-test frames to typed events.
- `NinoTncTxTestFrame` / `NinoTncTxTestFrameReceivedEvent` — the decoded front-panel diagnostic (labelled `=FirmwareVr:` form).
- `NinoTncStatusFrame` / `NinoTncStatusDelta` / `NinoTncStatusFrameReceivedEvent` — the numeric `=II:` register report + snapshot deltas.
- `NinoTncCommands` — GETALL/GETVER/STOPTX/SETBCNINT/GETRSSI/BOOTLOADER payloads and wire frames.
- `BootloaderNinoTncFirmwareFlasher` / `NinoTncFirmwareHexImage` / `NinoTncFlashException` — the firmware flasher (see above).
- `NinoTncRssiReading` / `NinoTncRssiReadingReceivedEvent` — the GETRSSI RX-audio level reply.
- `NinoTncCqBeep` — `[TARPNstat` arming + `CQBEEP-N` beep-request frame factories.
- `NinoTncAirTestFrame` / `NinoTncAirTestFrameReceivedEvent` — a partner's over-air test transmission (any `CQBEEP-N`).
- `NinoTncCatalog` / `NinoTncMode` — the DIP-position and firmware-byte mode tables (firmware v3.44).
- `NinoTncPortDiscovery` — VID/PID-based candidate enumeration.

## Out of scope
POLL mode (`0x0E`), the XOR-checksum multi-drop variant, return-from-KISS (`0xFF`), and the multi-drop port nibble on the driver API are intentionally not exposed — one modem = one serial port = one radio.

## See also
- [Source & issues](https://github.com/packet-net/packet.net)
- [`Packet.Kiss`](https://www.nuget.org/packages/Packet.Kiss) — the generic KISS surface this builds on (framing, typed events, ACKMODE, adaptive estimators).
- [`Packet.Kiss.Serial`](https://www.nuget.org/packages/Packet.Kiss.Serial) — the serial-port transport underneath.
- [NinoTNC wiki](https://wiki.oarc.uk/packet:ninotnc) — operator-facing hardware documentation.

---
*AGPL-3.0-licensed. Part of the [Packet.NET](https://github.com/packet-net/packet.net) stack.*
