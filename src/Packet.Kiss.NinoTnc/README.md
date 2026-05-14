# Packet.Kiss.NinoTnc

NinoTNC (N9600A) driver for Packet.NET. A thin overlay over
[`Packet.Kiss`](../Packet.Kiss/) that adds firmware-specific bits
(mode catalog, SETHW byte semantics, TX-Test parsing, USB VID/PID
discovery). All the *generic* KISS surface — `IKissModem`,
`AdaptiveKissTransport`, the typed-event hierarchy, the adaptive
estimators — lives in `Packet.Kiss` and works with any KISS-speaking
modem (QtSoundModem, Dire Wolf, etc.).

This package adds:

- The NinoTNC `SETHW` mode-selection extension (mode 0–14 + `+16`
  non-persist offset).
- The synthetic TX-Test diagnostic frame the modem emits when its
  front-panel button is pressed
  (`NinoTncTxTestFrameReceivedEvent` — firmware version, serial
  number, uptime, packet counters, running mode).
- The over-air TX-Test UI frame heard via the modem when a *partner*
  presses *their* button
  (`NinoTncAirTestFrameReceivedEvent` — partner's learned callsign,
  per-press sequence counter, deterministic ASCII pattern).
- USB VID/PID-based port discovery (`04D8:00DD`).
- A NinoTNC-specific frame classifier that overlays the generic one
  in `Packet.Kiss` and upgrades the synthetic / over-air TX-Test
  cases to the typed events above.

From `Packet.Kiss` you also get (and which work with any KISS modem):

- KISS framing (`KissEncoder`, `KissDecoder`, `KissFrame`,
  `KissCommand`, `KissFraming`).
- The G8BPQ `ACKMODE` KISS extension (KISS command `0x0C`) with
  per-tag TX-completion correlation through `IKissModem.SendFrameWithAckAsync`.
- The typed inbound event hierarchy (`KissInboundEvent`,
  `Ax25FrameReceivedEvent`, `AckModeDataReceivedEvent`,
  `UnknownInboundEvent`) and `KissFrameClassifier`.
- Per-peer adaptive KISS parameters via `Packet.Kiss.Adaptive`:
  `TxDelayHillClimbEstimator` (TXDELAY) + `CsmaContentionEstimator`
  (PERSIST + SLOTTIME), composed with `CompositeAdaptiveEstimator`.
- `AdaptiveKissTransport` that ties an estimator to any
  `IKissModem`.

The driver models one modem = one serial port = one radio. The KISS
multi-drop port nibble is supported at the framing level but not
exposed on the driver API; POLL mode and the XOR checksum extension
are intentionally out of scope.

This is part of [Packet.NET](https://github.com/M0LTE/packet.net); see
the parent project's [`docs/plan.md`](../../docs/plan.md) for the
big picture.

## Quick start

```csharp
using Packet.Kiss;
using Packet.Kiss.NinoTnc;
using Packet.Ax25;
using Packet.Core;

await using var tnc = NinoTncSerialPort.Open("COM6"); // or "/dev/ttyACM0"
await tnc.SetModeAsync(mode: 6);                       // 1200 AFSK AX.25
                                                       // (non-persist by default)

// Typed inbound events
tnc.InboundEvent += (_, evt) =>
{
    switch (evt)
    {
        case Ax25FrameReceivedEvent rx:
            Console.WriteLine($"{rx.Ax25.Source.Callsign} → {rx.Ax25.Destination.Callsign}");
            break;
        case TxTestFrameReceivedEvent diag:
            Console.WriteLine($"TX-Test pressed: firmware {diag.Diagnostic.FirmwareVersion}, " +
                              $"running mode {diag.Diagnostic.RunningMode?.Name}");
            break;
        case AckModeDataReceivedEvent ack:
            Console.WriteLine($"ACKMODE inbound tag 0x{ack.SequenceTag:X4}, {ack.Ax25Payload.Length} B");
            break;
    }
};

// Outbound
var ui = Ax25Frame.Ui(
    destination: new Callsign("CQ"),
    source: new Callsign("M0LTE", 1),
    info: "hello"u8);
await tnc.SendFrameAsync(ui.ToBytes());
```

## `SetModeAsync` and flash wear

`SetModeAsync(mode)` defaults to **non-persistent**: the TNC's flash is
not touched, and the configured mode reverts on reboot. Pass
`persistToFlash: true` only when the operator wants the choice to
survive a power cycle. Flash has a finite write-cycle budget; tooling
should not burn it on every dev iteration.

## ACKMODE with TX-completion correlation

ACKMODE (KISS command `0x0C`, G8BPQ multi-drop extension) is critical
on slow modes — at 300 / 600 bps HF the difference between "frame
accepted by TNC" and "frame on the air" is significant, and an AX.25
session machine that doesn't know the latter sizes T1 wrongly.

**Outbound** — the host appends a 2-byte sequence tag and the TNC
echoes it back when it has finished keying the frame:

```csharp
var receipt = await tnc.SendFrameWithAckAsync(
    ui.ToBytes(),
    timeout: TimeSpan.FromSeconds(30));
Console.WriteLine($"tx-complete after {receipt.Elapsed.TotalMilliseconds:F0} ms");
```

The driver auto-assigns sequence tags from an internal counter
(skipping `0x0000`) and correlates echoes by tag through a thread-safe
dictionary. Concurrent `SendFrameWithAckAsync` calls each get their
own receipt; the TNC pipelines them through its TX queue.

**Inbound** — if a peer (some other ACKMODE-aware master on a shared
multi-drop bus) sends an ACKMODE-Data frame your way, it surfaces as
`AckModeDataReceivedEvent` on `InboundEvent` with the tag pre-decoded
and the AX.25 payload sliced out. The TX-completion echo for your
*own* outbound frames is *not* exposed as a typed event — it returns
through `SendFrameWithAckAsync`'s `AckModeReceipt`.

## Adaptive parameters

`AdaptiveNinoTncTransport` calls into an `IAdaptiveParameterEstimator`
to learn per-peer KISS parameters from observed outcomes:

```csharp
var estimator = new CompositeAdaptiveEstimator(
    new TxDelayHillClimbEstimator(initialTxDelay: 50),
    new CsmaContentionEstimator(initialPersistence: 63, initialSlotTime: 10));

await using var transport = new AdaptiveNinoTncTransport(tnc, estimator);

// Before each TX the transport asks the estimator for that peer's
// recommended parameters, applies any deltas, sends in ACKMODE, and
// feeds the outcome back.
await transport.SendAsync("M0LTE-9", ui.ToBytes());

// AX.25-layer signals (when the session machine knows a frame was
// retransmitted / lost):
transport.RecordRetransmittedAck("M0LTE-9", payloadBytes: 100);
transport.RecordLoss("M0LTE-9", payloadBytes: 100);
```

Concrete estimators:

- `TxDelayHillClimbEstimator` — walks TXDELAY down on consecutive
  first-try ACKs, ratchets up on loss / ACK timeout. Clamped.
- `CsmaContentionEstimator` — drops PERSIST + raises SLOTTIME on
  `AckModeTimedOut` (channel-busy signal from the TNC). Slowly
  raises PERSIST back when the channel stays clear.
- `CompositeAdaptiveEstimator` — combines any set of children into a
  single recommendation. Each child sees every `Observe` call; their
  recommendations are merged field-wise.

For unit-testing the transport without a real TNC, depend on
`INinoTncModem` and inject a fake.

## Port discovery

```csharp
foreach (var candidate in NinoTncPortDiscovery.EnumerateCandidates())
{
    Console.WriteLine($"{candidate.PortName}  ({candidate.ResolvedDevicePath})");
}
```

- **Windows**: walks `HKLM\SYSTEM\CurrentControlSet\Enum\USB\` for
  devices whose VID/PID matches a known NinoTNC pair (`04D8:00DD`
  for current firmware — Microchip USB-CDC reference). For each
  match the registry's `Device Parameters\PortName` value is the
  COM port. Locked-down hosts that can't read this branch fall
  through to the generic enumeration.
- **Linux**: prefers `/dev/serial/by-id/...` symlinks (stable across
  reboots and replug); falls back to `/dev/ttyACM*`.
- **macOS / fallback**: `SerialPort.GetPortNames()`.

The env-var override stays as a final escape hatch:

```sh
# Linux
PACKETNET_NINOTNC_PORTS=/dev/ttyACM0,/dev/ttyACM1 ./packetnet ...

# Windows PowerShell
$env:PACKETNET_NINOTNC_PORTS = "COM6,COM8"
```

The NinoTNC's stock VID/PID is shared with several other Microchip-
USB-CDC reference projects; "matched VID/PID" is "this might be a
NinoTNC", not "this definitely is". The TX-Test diagnostic frame is
the authoritative confirmation — open the candidate, invite the
operator to press the TX-Test button, parse the resulting
`TxTestFrameReceivedEvent`.

## TX-Test diagnostic frame

The button on the modem's front panel is the only path to a "current
running mode + firmware version" read on this hardware. KISS has no
read commands, the NinoTNC firmware does not respond to a query SETHW,
and there is no other channel for parameter readback short of pressing
the button:

```csharp
tnc.InboundEvent += (_, evt) =>
{
    if (evt is TxTestFrameReceivedEvent t)
    {
        Console.WriteLine($"firmware {t.Diagnostic.FirmwareVersion}, " +
                          $"DIP {t.Diagnostic.DipSwitchPosition}, " +
                          $"running mode {t.Diagnostic.RunningMode?.Name}, " +
                          $"uptime {t.Diagnostic.Uptime}");
    }
};
```

Note that the *over-air* frame the modem transmits when the button is
pressed is **not** this diagnostic — that's a separate test signal.
`TxTestFrameReceivedEvent` is the synthetic KISS frame the firmware
sends to the host alongside the on-air test transmission.

## Firmware management

`Packet.Kiss.NinoTnc.Firmware` lays the groundwork for an
update-aware operator workflow. The strong types tell you what's
running and whether something newer is available; the seam for the
actual flash operation is in place but **the flash itself is not
implemented yet** (see "What's intentionally not here" below).

```csharp
using Packet.Kiss.NinoTnc.Firmware;

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("yourapp", "1.0"));
INinoTncFirmwareCatalogue catalogue = new GitHubNinoTncFirmwareCatalogue(http);

tnc.InboundEvent += async (_, evt) =>
{
    if (evt is NinoTncTxTestFrameReceivedEvent t &&
        t.Diagnostic.FirmwareVersion is { } version)
    {
        var availability = await catalogue.CheckForUpdateAsync(version);
        Console.WriteLine($"Running {availability.CurrentVersion} on {availability.ChipVariant}.");
        if (availability.UpdateAvailable)
        {
            Console.WriteLine($"Update available: {availability.LatestAvailable!.Version}");
            Console.WriteLine($"Download: {availability.LatestAvailable.DownloadUrl}");
        }
    }
};
```

**Chip variant detection.** The major component of the firmware
version (`3.xx` or `4.xx`) identifies which dsPIC chip is fitted:

| Firmware major | Chip | Image filename |
|---:|---|---|
| 3 | dsPIC33EP256GP | `N9600A-v3-{minor}.hex` |
| 4 | dsPIC33EP512GP | `N9600A-v4-{minor}.hex` |

Flashing the wrong image bricks the modem until ICSP recovery — the
chip-variant check exists so a future UI / MCP layer can refuse to
proceed when the operator picks a hex for the wrong variant.

**Catalogue.** `GitHubNinoTncFirmwareCatalogue` reads
`ninocarrillo/flashtnc@master` and pulls out whatever
`N9600A-v{major}-{minor}.hex` files are sitting there at the moment.
Nino's release process is "drop new hex, remove old hex", so the
catalogue surfaces only the *current* release per chip variant. Old
versions are reachable via git history but aren't in scope here.

## What's intentionally not here

- The PIC bootloader protocol itself. `INinoTncFirmwareFlasher` is the
  seam; the only in-tree implementation is `UnsupportedFirmwareFlasher`
  which throws. A native C# implementation (or a process shell-out to
  `flashtnc.py`) will arrive in a separate PR with its own review.
- **POLL mode** (KISS command `0x0E`). Multi-drop, not used by any
  current hardware we care about.
- **XOR checksum mode** (multi-drop variant). Same reason.
- **Return-from-KISS** (`0xFF`). NinoTNC stays in KISS, and we don't
  drive any TNC that needs to be flipped back to command mode.
- **Multi-drop port nibble on the driver API**. The KISS framing
  layer still respects it (port 0–15 in the command byte's high
  nibble); the driver consistently uses port 0 and assumes one
  modem = one radio.

## Operating-mode catalog

`NinoTncCatalog.ByMode` is the DIP-switch-position → mode table for
firmware v3.44; `NinoTncCatalog.FirmwareByteToMode` is the reverse
lookup keyed on the firmware byte the TNC reports in its
`BrdSwchMod` diagnostic field. The catalog is firmware-version-
specific; bump when needed.

## See also

- [`docs/nino-tnc-characterisation.md`](../../docs/nino-tnc-characterisation.md)
  — empirical measurements from the back-to-back NinoTNC pair.
- [`tools/Packet.NinoTnc.Spike`](../../tools/Packet.NinoTnc.Spike/) — the
  spike-and-soak tool that produced those numbers.
- [Multi-drop KISS spec](https://github.com/packethacking/ax25spec/blob/main/doc/multi-drop-kiss-operation.md)
  — authoritative reference for ACKMODE, POLL, the port nibble.
- [NinoTNC wiki](https://wiki.oarc.uk/packet:ninotnc) — operator-facing
  hardware documentation.
