# 2. Transports: talking to a TNC

This chapter is about the bottom of the cake: getting bytes on and off the air.
By the end you'll have a tiny program that opens a modem and dumps every frame it
hears — the "hello world" of packet radio — and you'll know how to swap the
transport without touching the rest of your code.

Everything here lives behind one interface, `IKissModem` (namespace
`Packet.Kiss`), introduced in [chapter 1](01-architecture.md#seam-1--ikissmodem-send-and-receive-frames-as-bytes).

## The frame you receive: `KissFrame`

`ReadFramesAsync` yields `KissFrame` values:

```csharp
namespace Packet.Kiss;

public readonly record struct KissFrame(byte Port, KissCommand Command, byte[] Payload);
```

- `Port` — the KISS port index (0 for single-port TNCs).
- `Command` — a `KissCommand`. For received traffic this is almost always
  `KissCommand.Data`; other values are channel-parameter or diagnostic frames.
- `Payload` — for a `Data` frame, the **AX.25 frame bytes in KISS form** (no
  flags, no FCS). This is what you hand to `Ax25Frame.TryParse` in
  [chapter 4](04-listen.md).

So the canonical receive loop is:

```csharp
await foreach (KissFrame f in modem.ReadFramesAsync(ct))
{
    if (f.Command != KissCommand.Data)
        continue; // parameter/diagnostic frame, not traffic
    // f.Payload is an AX.25 frame; parse it (chapter 4).
}
```

`ReadFramesAsync` runs until you cancel the token or dispose the modem.

## KISS over TCP — `KissTcpClient`

The most common setup: a software modem (Dire Wolf, QtSoundModem) or a BPQ KISS
port listening on TCP.

```csharp
using Packet.Kiss;

await using var modem = await KissTcpClient.ConnectAsync("127.0.0.1", 8001);
// modem is an IKissModem from here on.
```

`KissTcpClient` adds a couple of robustness features over a bare socket: a
read-idle timeout that detects a half-open link (default 5 minutes), and support
for G8BPQ **ACKMODE** (`SendFrameWithAckAsync`) so you can correlate a TX with
its on-air completion when the far end supports it.

## KISS over a serial port — `KissSerialModem`

For a hardware TNC or a USB-CDC modem on a COM port / `/dev/tty*`:

```csharp
using Packet.Kiss.Serial;

await using var modem = KissSerialModem.Open("/dev/ttyUSB0", baudRate: 57600);
```

`KissSerialModem` exposes both pull-style (`ReadFramesAsync`) and push-style
(`FrameReceived` event) delivery — pick whichever fits your program. It does
*not* support ACKMODE (a generic serial TNC has no TX-completion signal); use
`NinoTncSerialPort` if you need that on a serial link.

## A NinoTNC over USB — `NinoTncSerialPort`

The NinoTNC gets a dedicated driver because it speaks a few extensions: hardware
mode switching (`SETHW`), TX-completion ACKMODE, and typed inbound diagnostics.

```csharp
using Packet.Kiss.NinoTnc;

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM0");
await tnc.SetModeAsync(mode: 6);   // e.g. 1200 baud AFSK AX.25

// Raw KISS frames, same as any IKissModem:
await foreach (var f in tnc.ReadFramesAsync(ct)) { /* ... */ }

// …or typed, classified inbound events:
tnc.InboundEvent += (_, evt) =>
{
    if (evt is Ax25FrameReceivedEvent rx)
        Console.WriteLine($"{rx.Ax25.Source.Callsign} -> {rx.Ax25.Destination.Callsign}");
};
```

You can discover attached NinoTNCs by VID/PID rather than hard-coding a port:

```csharp
foreach (var c in NinoTncPortDiscovery.EnumerateCandidates())
    Console.WriteLine(c.PortName);   // stable open path, e.g. /dev/serial/by-id/...
```

The NinoTNC library has [its own README](../src/Packet.Kiss.NinoTnc/README.md)
covering adaptive TX parameters, TX-Test frames, and firmware checks — read it if
you're targeting that hardware specifically.

## Two transports that aren't KISS

### AGW / SV2AGW — `Packet.Agw`

AGWPE-style servers (LinBPQ's AGW port, Dire Wolf's AGW port, the real AGWPE)
speak a different wire protocol. `AgwClient` wraps it, and notably gives you a
**connected AX.25 session as a `System.IO.Stream`** — the server runs the data
link, you just read and write bytes:

```csharp
using Packet.Agw;

await using var agw = await AgwClient.ConnectAsync("127.0.0.1", 8000);
await agw.RegisterCallsignAsync("M0LTE-1");

AgwSession s = await agw.OpenSessionAsync(from: "M0LTE-1", to: "GB7RDG-1");
await s.WriteAsync("HELLO\r"u8.ToArray());
var buf = new byte[256];
int n = await s.ReadAsync(buf);
```

This is the quickest path to a connect client *if* you already have an AGW
server — the AGW server owns the AX.25 state machine, so you skip
`Ax25Listener` entirely. The trade-off is that you're bound to the server's
behaviour and policy. The native path (`Ax25Listener` over an `IKissModem`,
[chapter 5](05-axcall.md)) gives you the full strict-vs-pragmatic control the
engine is built around.

### AXUDP (RFC 1226) — `Packet.Axudp`

AXUDP carries AX.25 frames in UDP datagrams (each with a mandatory 2-octet FCS).
`AxudpSocket` works directly in terms of `Ax25Frame`:

```csharp
using Packet.Axudp;

using var sock = new AxudpSocket(localPort: 10093);
await sock.SendAsync(remote: endpoint, frame: myFrame);     // frame is an Ax25Frame
var result = await sock.ReceiveAsync(ct);                   // FCS validated, bad ones dropped
Console.WriteLine(result.DecodedFrame?.Source.Callsign);
```

Note AXUDP isn't an `IKissModem` — it deals in whole frames, not KISS payloads,
so the layers above consume it slightly differently. For a first node, stick to a
KISS transport.

## Tool #1 — a raw frame dumper

Putting it together: open a modem, dump everything. The only transport-specific
line is the `Open`/`ConnectAsync` call — everything below it is `IKissModem`.

```csharp
using Packet.Kiss;
using Packet.Kiss.Serial;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Swap this one line to change transports:
await using IKissModem modem = KissSerialModem.Open("/dev/ttyUSB0");
// await using IKissModem modem = await KissTcpClient.ConnectAsync("127.0.0.1", 8001);

Console.WriteLine("Listening… Ctrl-C to stop.");
try
{
    await foreach (KissFrame f in modem.ReadFramesAsync(cts.Token))
    {
        if (f.Command != KissCommand.Data) continue;
        Console.WriteLine($"port {f.Port}: {f.Payload.Length} bytes  {Convert.ToHexString(f.Payload)}");
    }
}
catch (OperationCanceledException) { /* Ctrl-C */ }
```

This prints hex, which isn't very satisfying. In [chapter 4](04-listen.md) we'll
parse `f.Payload` into an `Ax25Frame` and print callsigns and text — but first we
need the frame layer itself, which is also how we'll *send*.

---

Next: [callsigns, frames, and a beacon sender →](03-frames-and-callsigns.md)
