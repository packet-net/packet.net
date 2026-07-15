# 9. Radios & rigs: the radio behind the modem

Everything so far has treated the radio as invisible. The transport hands you
frames; the radio behind the modem is just a box that keys up when the TNC tells
it to. [Chapter 2](02-transports.md) even shrugged at `Ax25InboundFrame.Radio` —
optional signal metadata, `null` on a bare transport. This optional last leg is
about the seams that fill it in: a radio with its own serial control channel
(Tait CCDI, hamlib CAT, flrig) can report signal strength, announce the instant
the channel goes busy, and key its transmitter under software control — and the
engine models that with **two** deliberately separate interfaces.

Neither is part of the AX.25 cake — you can build everything in chapters 1–8
without them. They sit *beside* the cake and plug into it at two points you
already know: the transport (a decorator) and `Ax25ListenerOptions`.

## Two seams, on purpose

The split mirrors what an operator experiences. **Station control** is what you
*tune*: dial frequency, operating mode, PTT, and the meters you watch while
transmitting. The **packet medium** is what the modem and the CSMA machinery
*consume*: is the channel busy right now, how strong was that frame, key up.
One physical radio can serve both; the roles stay distinct.

| | `IRadioControl` (`Packet.Radio`) | `IRigControl` (`Packet.Rig`) |
|---|---|---|
| The seam | the **packet medium** — what CSMA and frame tagging need | **station control** (CAT) — what an operator tunes |
| Members | `ReadRssiDbmAsync`, `ChannelBusy` + `CarrierSenseChanged`, `SetTransmitterAsync` | frequency get/set, mode get/set, PTT get/set, SWR + RF-power meters, `ReadDcdAsync`, `ReadSignalStrengthDbmAsync` |
| Capability flags | `RadioCapabilities` | `RigCapabilities` |
| Consumed by | `RssiTaggingTransport`, `RadioCarrierSense` → the listener's CSMA gate | station tooling: dashboards, QSY/mode buttons, TX-health monitors |
| Implementations | `TaitCcdiRadio` (native, push DCD), `RigRadioControl` (any `IRigControl`, polled) | `RigctldRig` (hamlib), `FlrigRig`, `TaitRigControl` |

QSY deliberately lives on the **rig seam only**: `RadioCapabilities` reserves
flags for channel/frequency/TX-power so richer radios can describe themselves,
but the members belong to `IRigControl` — retuning a station is control-room
work, not medium access. The `IRadioControl` subset ({RSSI-get, busy-get,
PTT-set}) has survived four implementations without an interface change, which
is the evidence the split is at the right altitude.

Both seams run the discipline you met on the transports in
[chapter 2](02-transports.md), taken one step further: **capabilities are
discovered at connect time, and calling an unadvertised member throws
`NotSupportedException`.** Probe `Capabilities` with `HasFlag` and degrade
honestly — no backend fakes a meter it doesn't have.

## Station control: `IRigControl`

`Packet.Rig` is a small, dependency-free contracts package (it doesn't pull in
the AX.25 stack at all). The workhorse backend is `Packet.Rig.Hamlib`'s
`RigctldRig` — hamlib's NET rigctl TCP protocol over pure managed sockets, no
native libhamlib, so one client reaches every rig hamlib supports:

```csharp
using Packet.Rig;
using Packet.Rig.Hamlib;

await using var rig = await RigctldRig.ConnectAsync(new RigctldRigOptions
{
    Host = "127.0.0.1",
    Port = 4532,                              // rigctld's stock port
});

Console.WriteLine($"{rig.Info.Manufacturer} {rig.Info.Model}");   // from \dump_caps

// Probe before you touch: every backend, and every rig behind a backend,
// supports a different slice.
if (rig.Capabilities.HasFlag(RigCapabilities.FrequencySet))
    await rig.SetFrequencyAsync(144_950_000);                     // QSY

if (rig.Capabilities.HasFlag(RigCapabilities.ModeSet))
    await rig.SetModeAsync(RigMode.PktFm);

if (rig.Capabilities.HasFlag(RigCapabilities.SwrMeter))
    Console.WriteLine($"SWR {await rig.ReadSwrAsync():0.0}");     // meaningful while transmitting
```

`RigMode` is a value wrapper over a token, not a closed enum — mode vocabularies
genuinely diverge across backends, so you compare against the well-known statics
(`RigMode.PktUsb`, `RigMode.Fm`, …) and fall back to `RigMode.From("DATA-U")`
for backend-native names. `Packet.Rig.Flrig`'s `FlrigRig` is the same contract
over flrig's XML-RPC server (`await FlrigRig.ConnectAsync()`); errors are typed
(`RigConnectionException`, `RigTimeoutException`, `RigCommandException`,
`RigProtocolException`) so retry policy can be honest about what failed.

!!! note "Testable without a rig"
    hamlib's dummy rig is the ecosystem's standard harness:
    `rigctld -m 1 --set-conf=static_data=1` serves a stateful fake with
    deterministic meters. Point `RigctldRig` at it and everything on this page
    runs with no hardware — the same no-hardware spirit as the fake transport in
    [chapter 8](08-beyond.md#testing-engine-backed-code).

## The packet medium: `IRadioControl`

Three members, chosen for what the AX.25 stack actually consumes:

- **`ReadRssiDbmAsync`** — instantaneous receive signal strength, suitable for
  per-frame attribution.
- **`ChannelBusy` + `CarrierSenseChanged`** — *hardware* data-carrier detect.
  This is the valuable one for CSMA: the radio reports RF on channel typically
  0.5–1 s before the modem finishes demodulating the frame that's on the air.
- **`SetTransmitterAsync`** — PTT, independent of the modem's PTT line.

The native implementation is `Packet.Radio.Tait`'s `TaitCcdiRadio` (Tait
TM8100/TM8200 over CCDI), which *pushes* carrier-sense edges as unsolicited
PROGRESS messages:

```csharp
using Packet.Radio;
using Packet.Radio.Tait;

await using var radio = TaitCcdiRadio.Open("/dev/ttyUSB0");   // CCDI, 28800 8N1 default
await radio.SetProgressMessagesAsync(true);                   // turn on push DCD events

radio.CarrierSenseChanged += (_, e) =>
    Console.WriteLine($"DCD {(e.Busy ? "up" : "down")} at {e.At:O}");
```

Two adapters carry this into the stack you built in earlier chapters:

**`RssiTaggingTransport`** is an `IAx25Transport` decorator — it wraps any
transport from [chapter 2](02-transports.md), runs a background RSSI sampler,
and re-yields every inbound frame with `Ax25InboundFrame.Radio` populated
(RSSI median/min/max, SNR against a tracked noise floor, carrier-rise instant,
airtime estimate). A frame with no qualifying sample gets `null` metadata, never
a guess. The frame dumper from chapter 2, now with signal numbers:

```csharp
await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM0"); // chapter 2
await using var tagged = new RssiTaggingTransport(tnc, radio);

await foreach (Ax25InboundFrame f in tagged.ReceiveAsync(ct))
    Console.WriteLine($"{f.Ax25.Length} bytes  " +
        $"RSSI {f.Radio?.RssiDbm:0.0} dBm  SNR {f.Radio?.SnrDb:0.0} dB");
```

**`RadioCarrierSense`** bridges the radio's `ChannelBusy` onto the neutral
`ICarrierSense` seam (`Packet.Ax25.Transport.Abstractions`) that `Ax25Listener`
consults before every keyup. Hand it to the listener via
`Ax25ListenerOptions.CarrierSense` and the listener's own gate holds
transmissions while the channel is busy — bounded wait, fail-open:

```csharp
var listener = new Ax25Listener(tagged, new Ax25ListenerOptions
{
    MyCall = me,
    CarrierSense = new RadioCarrierSense(radio),   // keyups wait for a clear channel
});
```

(A listener owns its transport's receive pump — [chapter 1](01-architecture.md) —
so in a listener-based tool you hand `tagged` over rather than iterating it
yourself, as in the monitor loop above.) Note the shape: the medium-access
deferral lives *in the stack* (you met the listener in
[chapter 5](05-axcall.md)), not in an opaque transport wrapper, and it composes
with the TNC's own persistence CSMA. Both decorators leave ownership
of the inner transport and the radio with you — disposing them only stops their
sampler/gate.

## Bridging the seams: `RigRadioControl` and its mirror

What if your station's radio is a CAT rig, not a Tait? `Packet.Radio`'s
`RigRadioControl` re-presents any `IRigControl` through the radio seam, so a CAT
transceiver feeds the same CSMA gate and per-frame-RSSI machinery a push-capable
radio does. Capabilities map at construction — `DcdRead → CarrierSense`,
`SignalStrengthRead → RssiRead`, `PttSet → TransmitterControl` — and a rig
advertising none of the three is rejected outright:

```csharp
using Packet.Radio;

await using var radio = new RigRadioControl(rig);   // the RigctldRig from above

// …then exactly as before — here over a soundmodem's KISS TCP port (chapter 2),
// the pairing that motivates the bridge:
await using var kissTcp = await KissTcpClient.ConnectAsync("127.0.0.1", 8001);
await using var tagged = new RssiTaggingTransport(kissTcp, radio);
var listener = new Ax25Listener(tagged, new Ax25ListenerOptions
{
    MyCall = me,
    CarrierSense = new RadioCarrierSense(radio),
});
```

One honesty note: rig backends are **poll-only**, so `RigRadioControl`
*synthesizes* carrier-sense edges from an owned DCD poll loop (100 ms default)
— edges shorter than the poll interval are invisible, coarser than a true push
source like CCDI's PROGRESS events. A failed read marks `ChannelBusy` `null`
(unknown ⇒ the gate fails open) and backs off until the backend self-heals.

The bridge has an inverse twin: `Packet.Radio.Tait`'s `TaitRigControl`
re-presents a Tait CCDI radio through the *rig* seam
(`await TaitRigControl.CreateAsync(radio)`), advertising only the slice CCDI can
honestly serve — PTT get/set and a relative RF-power meter. No frequency (not
CCDI-readable), no mode (an FM PMR radio has none), no SWR or watts (the power
detectors are raw millivolts, not calibrated units). Together the pair makes the
point of this chapter: **the seams describe roles, not device classes.** The
same physical radio can stand behind either interface, advertising exactly what
it can do and nothing more.

!!! note "The radio can even be the transport"
    `Packet.Radio.Tait` also ships `TaitTransparentTransport` — an
    `IAx25Transport` whose modem **is** the radio: AX.25 rides the Tait's own
    built-in FFSK modem as a byte pipe, no external TNC at all. It's one more
    proof of chapter 2's claim that KISS is an implementation behind the seam,
    not a property of it. The trade-off is inherent: while the serial port is a
    byte pipe the control channel is gone, so no RSSI, no DCD — only airtime
    timing. See the [`Packet.Radio.Tait` README](../src/Packet.Radio.Tait/README.md).

## Where not to look

If you're *operating* the shipped node host rather than building tooling, none
of this chapter is config you write: the node assembles these exact pieces for
you from a port's `radio:` and `rig:` YAML blocks (or scan-and-click in the web
panel), and layers dashboards, a setup doctor, and tuning workflows on top. That
is operator territory — start at
[the operator guide's radio-attachment chapter](../operating/01-attach-a-radio.md).
This chapter is the library view those features are assembled from, the same
relationship [chapter 8](08-beyond.md#adopting-the-node-hosts-building-blocks)
described for `Packet.Node.Core`.

---

That really is everything: the engine bottom to top in chapters 1–8, and now the
seams that let your station see the radio it's keying.

[← back to the guide index](index.md)
