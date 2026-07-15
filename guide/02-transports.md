# 2. Transports: talking to a TNC

This chapter is about the bottom of the cake: getting AX.25 frames on and off the
air. By the end you'll have a tiny program that opens a transport and dumps every
frame it hears — the "hello world" of packet radio — and you'll know how to swap
the transport without touching the rest of your code.

Everything here lives behind one interface, `IAx25Transport` (namespace
`Packet.Ax25.Transport`, in the package `Packet.Ax25.Transport.Abstractions`),
introduced in [chapter 1](01-architecture.md#seam-1--iax25transport-send-and-receive-ax25-frames).
KISS — the host↔TNC protocol you may have met elsewhere — is **one
implementation** behind this seam, not the seam itself. We'll meet the KISS
transports below, and also AXUDP, which carries AX.25 over UDP with no KISS at
all. The point of the seam is that the layers above don't care which.

## The seam: `IAx25Transport`

The whole transport contract is two methods:

```csharp
namespace Packet.Ax25.Transport;

public interface IAx25Transport : IAsyncDisposable
{
    // Send one AX.25 frame body (no FCS, no link-framing), fire-and-forget.
    Task SendAsync(ReadOnlyMemory<byte> ax25, CancellationToken ct = default);

    // A long-running stream of inbound AX.25 frames.
    IAsyncEnumerable<Ax25InboundFrame> ReceiveAsync(CancellationToken ct = default);
}
```

The currency is the **AX.25 frame body** — address + control + PID + info,
*without* the HDLC flags or the FCS (the transport adds and strips whatever its
medium needs). That is exactly what `Ax25Frame.ToBytes()` produces and
`Ax25Frame.TryParse(...)` consumes, so the frame layer and the transport layer
meet cleanly at this byte boundary.

Crucially, **the transport pre-filters to genuine AX.25 frames.** A KISS
transport drops non-Data KISS commands (parameter echoes, diagnostics) itself, so
your consumer never sees a wire-protocol frame and never has to know the wire
protocol. There is no command byte to test, no KISS envelope to unwrap.

### The frame you receive: `Ax25InboundFrame`

`ReceiveAsync` yields `Ax25InboundFrame` values:

```csharp
namespace Packet.Ax25.Transport;

public readonly record struct Ax25InboundFrame(
    ReadOnlyMemory<byte> Ax25,            // the bare AX.25 frame body, FCS-stripped
    byte PortId,                          // multi-drop channel id (0–15); 0 for single-channel
    DateTimeOffset ReceivedAt,            // capture time, stamped by the transport
    RadioMetadata? Radio = null);         // optional per-frame RSSI/SNR, null on most transports
```

- `Ax25` — the AX.25 frame body, ready for `Ax25Frame.TryParse` ([chapter 4](04-listen.md)).
- `PortId` — the channel index for multi-port serial / AGW; `0` for a
  single-channel transport.
- `ReceivedAt` — the true receive instant, stamped when the frame arrived (not
  reconstructed later), so timing-sensitive code sees real latency.
- `Radio` — optional signal metadata; `null` for every transport with no radio
  control channel. [Chapter 9](09-radios-and-rigs.md) shows the decorator that
  populates it.

So the canonical receive loop is:

```csharp
await foreach (Ax25InboundFrame f in transport.ReceiveAsync(ct))
{
    // f.Ax25 is an AX.25 frame body; parse it (chapter 4).
}
```

`ReceiveAsync` runs until you cancel the token or dispose the transport. Note how
much simpler this is than the old KISS-shaped loop: no `if (command != Data)`
guard, because the transport already did that for you.

### Two optional capabilities

The seam above is deliberately minimal — it's the surface `Ax25Listener` actually
needs. Two *further* abilities are not part of every transport, so they live on
separate interfaces a transport **may** also implement. You feature-detect them
with `is` and degrade gracefully when they're absent:

- **`ITxCompletionTransport`** — confirmed transmit. A transport that can observe
  when a frame actually left the wire (a KISS TNC that echoes the G8BPQ ACKMODE
  tag) implements this; the AX.25 layer uses it to re-arm its T1 timer on real
  TX-completion, and a pacing layer uses it for back-pressure.

    ```csharp
    namespace Packet.Ax25.Transport;

    public interface ITxCompletionTransport : IAx25Transport
    {
        Task<TxCompletion> SendAwaitingCompletionAsync(
            ReadOnlyMemory<byte> ax25, TimeSpan? timeout = null, CancellationToken ct = default);
    }

    public readonly record struct TxCompletion(DateTimeOffset Queued, DateTimeOffset Completed)
    {
        public TimeSpan Elapsed => Completed - Queued;
    }
    ```

    Use it like this:

    ```csharp
    if (transport is ITxCompletionTransport confirmed)
    {
        TxCompletion tx = await confirmed.SendAwaitingCompletionAsync(frame.ToBytes());
        Console.WriteLine($"on the air after {tx.Elapsed.TotalMilliseconds:0} ms");
    }
    else
    {
        await transport.SendAsync(frame.ToBytes());   // fall back to fire-and-forget
    }
    ```

    !!! warning "The confirmed-send failure mode"
        If the frame is committed to the wire but the completion signal never
        arrives within the timeout, `SendAwaitingCompletionAsync` throws
        `TimeoutException` — the frame **is** on the air, so treat it as sent and
        do not blindly retransmit. That's different from the capability being
        absent, which you avoid by feature-detecting first.

- **`ICsmaChannelParams`** — the half-duplex-radio channel-access knobs (the
  TXDELAY / PERSIST / SLOTTIME / TXTAIL parameters). Meaningful only on a shared
  CSMA radio channel, so a UDP tunnel or an AGW server simply doesn't implement
  it:

    ```csharp
    namespace Packet.Ax25.Transport;

    public interface ICsmaChannelParams
    {
        Task SetTxDelayAsync(byte tenMsUnits, CancellationToken ct = default);
        Task SetPersistenceAsync(byte value, CancellationToken ct = default);
        Task SetSlotTimeAsync(byte tenMsUnits, CancellationToken ct = default);
        Task SetTxTailAsync(byte tenMsUnits, CancellationToken ct = default);
    }
    ```

    ```csharp
    if (transport is ICsmaChannelParams csma)
        await csma.SetTxDelayAsync(30);   // 300 ms keyup-to-data
    ```

The whole design hangs on this: a transport with neither capability (AXUDP)
implements only `IAx25Transport` and **fakes nothing** — no no-op CSMA setters,
no throwing ACKMODE method. A consumer that wants a capability discovers its
absence honestly and copes.

## The KISS transport family

KISS is the dominant host↔TNC protocol, so most of the concrete transports are
KISS speakers. They all implement `IAx25Transport`; KISS is purely how they talk
to their hardware. You pick one at startup and the rest of your code never knows.

### KISS over TCP — `KissTcpClient`

The most common setup: a software modem (Dire Wolf, QtSoundModem) or a BPQ KISS
port listening on TCP.

```csharp
using Packet.Ax25.Transport;
using Packet.Kiss;

await using IAx25Transport transport = await KissTcpClient.ConnectAsync("127.0.0.1", 8001);
```

`KissTcpClient` adds a couple of robustness features over a bare socket: a
read-idle timeout that detects a half-open link (default 5 minutes), and
confirmed transmit. It implements **both** capabilities —
`ITxCompletionTransport` (via G8BPQ ACKMODE) and `ICsmaChannelParams` — so you
can correlate a TX with its on-air completion and set channel parameters when the
far end supports them.

### KISS over a serial port — `KissSerialModem`

For a hardware TNC or a USB-CDC modem on a COM port / `/dev/tty*`:

```csharp
using Packet.Ax25.Transport;
using Packet.Kiss.Serial;

await using IAx25Transport transport = KissSerialModem.Open("/dev/ttyUSB0", baudRate: 57600);
```

`KissSerialModem` implements `IAx25Transport` and `ICsmaChannelParams`, but **not**
`ITxCompletionTransport` — a generic serial KISS TNC has no TX-completion signal,
so it honestly doesn't offer that capability (a probe with `is
ITxCompletionTransport` correctly skips it). Use `NinoTncSerialPort` if you need
confirmed transmit on a serial link.

### A NinoTNC over USB — `NinoTncSerialPort`

The NinoTNC gets a dedicated driver because it speaks a few extensions: hardware
mode switching (`SETHW`), confirmed-transmit ACKMODE, and typed inbound
diagnostics. It implements `IAx25Transport` plus **both** capabilities.

```csharp
using Packet.Ax25.Transport;
using Packet.Kiss;             // Ax25FrameReceivedEvent et al. live here
using Packet.Kiss.NinoTnc;

await using var tnc = NinoTncSerialPort.Open("/dev/ttyACM0");
await tnc.SetModeAsync(mode: 6);   // e.g. 1200 baud AFSK AX.25

// Inbound AX.25 frames, same seam as any transport:
await foreach (Ax25InboundFrame f in tnc.ReceiveAsync(ct)) { /* ... */ }

// …or NinoTNC's typed, classified inbound events:
tnc.InboundEvent += (_, evt) =>
{
    if (evt is Ax25FrameReceivedEvent rx)
        Console.WriteLine($"{rx.Ax25.Source.Callsign} -> {rx.Ax25.Destination.Callsign}");
};
```

Mode switching (`SetModeAsync`) is deliberately *not* on the transport seam — it
varies per modem and is selected from config at construction, so it lives on the
NinoTNC type itself, not on `IAx25Transport`.

You can discover attached NinoTNCs by VID/PID rather than hard-coding a port:

```csharp
foreach (var c in NinoTncPortDiscovery.EnumerateCandidates())
    Console.WriteLine(c.PortName);   // stable open path, e.g. /dev/serial/by-id/...
```

The NinoTNC library has [its own README](../src/Packet.Kiss.NinoTnc/README.md)
covering adaptive TX parameters, TX-Test frames, and firmware checks — read it if
you're targeting that hardware specifically.

!!! note "The KISS codec is still there — one layer down"
    A KISS transport has to encode and decode the KISS wire framing internally,
    and those codec types live in `Packet.Kiss`: `KissFrame`, `KissCommand`,
    `KissEncoder`, `KissDecoder`. You only reach for them if you're *implementing*
    a KISS transport yourself, or decoding a captured KISS byte stream — never as
    the transport API. For everything in this guide, `IAx25Transport` /
    `Ax25InboundFrame` is the surface you work against.

## Transports that aren't KISS

### AXUDP (RFC 1226) — over `Packet.Axudp`

AXUDP carries AX.25 frames in UDP datagrams (each with a mandatory 2-octet FCS).
This is the transport whose mere existence proves KISS is one implementation
behind the seam, not a property of it: a datagram's payload *is* the AX.25 frame
body, so an AXUDP transport implements **only** the neutral `IAx25Transport` —
**no** `ICsmaChannelParams` (a UDP link has no carrier to sense) and **no**
`ITxCompletionTransport` (there's no TNC to echo a completion). It constructs no
KISS object at all.

The published `Packet.Axudp` package gives you a frame-oriented `AxudpSocket`:

```csharp
using Packet.Axudp;

using var sock = new AxudpSocket(localPort: 10093);
await sock.SendAsync(remote: endpoint, frame: myFrame);     // frame is an Ax25Frame
var result = await sock.ReceiveAsync(ct);                   // FCS validated, bad ones dropped
Console.WriteLine(result.DecodedFrame?.Source.Callsign);
```

`AxudpSocket` works in terms of whole `Ax25Frame`s, not the byte-currency seam, so
to run an `Ax25Listener` over AXUDP you wrap it in a thin `IAx25Transport`
adapter: on send, append the FCS to the frame body and put it in a datagram; on
receive, yield the FCS-stripped body as an `Ax25InboundFrame`. The node host ships
exactly this adapter as `AxudpFrameTransport`:

```csharp
using Packet.Node.Core.Transports;   // ships with the node host package
using System.Net;

await using IAx25Transport transport =
    new AxudpFrameTransport(remote: IPEndPoint.Parse("192.0.2.10:10093"), localPort: 10093);
```

!!! note "Where AXUDP-as-transport lives"
    `AxudpFrameTransport` currently lives in `Packet.Node.Core` (the node host),
    not in `Packet.Axudp`. If you're building tooling without the node host, the
    adapter is ~30 lines over `AxudpSocket` — append a CRC-16-CCITT FCS (low byte
    first) on send, yield `socket.ReceiveAsync().RawFrame` on receive — and the
    node host's implementation is your reference.

### AGW / SV2AGW — `Packet.Agw`

AGWPE-style servers (LinBPQ's AGW port, Dire Wolf's AGW port, the real AGWPE)
speak a different wire protocol *and run the AX.25 data link themselves*. That
makes AGW a different kind of seam: it's a **session** transport, not a
frame transport, so it is deliberately **not** an `IAx25Transport`. `AgwClient`
wraps it and notably gives you a connected AX.25 session as a
`System.IO.Stream` — the server owns the state machine, you just read and write
bytes:

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
server — you skip `Ax25Listener` entirely. The trade-off is that you're bound to
the server's behaviour and policy. The native path (`Ax25Listener` over an
`IAx25Transport`, [chapter 5](05-axcall.md)) gives you the full
strict-vs-pragmatic control the engine is built around.

## Tool #1 — a raw frame dumper

Putting it together: open a transport, dump everything. The only
transport-specific line is the `Open`/`ConnectAsync` call — everything below it is
the `IAx25Transport` seam.

```csharp
using Packet.Ax25.Transport;
using Packet.Kiss;
using Packet.Kiss.Serial;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Swap this one line to change transports:
await using IAx25Transport transport = KissSerialModem.Open("/dev/ttyUSB0");
// await using IAx25Transport transport = await KissTcpClient.ConnectAsync("127.0.0.1", 8001);

Console.WriteLine("Listening… Ctrl-C to stop.");
try
{
    await foreach (Ax25InboundFrame f in transport.ReceiveAsync(cts.Token))
    {
        Console.WriteLine(
            $"port {f.PortId}: {f.Ax25.Length} bytes  {Convert.ToHexString(f.Ax25.Span)}");
    }
}
catch (OperationCanceledException) { /* Ctrl-C */ }
```

This prints hex, which isn't very satisfying. In [chapter 4](04-listen.md) we'll
parse `f.Ax25` into an `Ax25Frame` and print callsigns and text — but first we
need the frame layer itself, which is also how we'll *send*.

---

Next: [callsigns, frames, and a beacon sender →](03-frames-and-callsigns.md)
