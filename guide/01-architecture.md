# 1. Architecture & the two seams

Before writing any code, it pays to understand how the engine is sliced, because
the slices are exactly the points where *you* plug in. The whole design is built
around keeping each layer ignorant of the ones above and below it, joined only by
a couple of narrow interfaces.

## The layer cake, concretely

| Package | Namespace(s) | What it owns | Depends on |
|---------|--------------|--------------|------------|
| `Packet.Core` | `Packet.Core` | `Callsign`, `Ax25Address`, CRC, primitives | nothing |
| `Packet.Ax25.Transport.Abstractions` | `Packet.Ax25.Transport` | `IAx25Transport`, `Ax25InboundFrame`, the two optional capabilities | nothing |
| `Packet.Kiss` | `Packet.Kiss` | KISS framing codec (`KissFrame`, `KissCommand`, `KissEncoder`, `KissDecoder`); `KissTcpClient` | `Packet.Core`, transport abstractions |
| `Packet.Kiss.Serial` | `Packet.Kiss.Serial` | `KissSerialModem` | `Packet.Kiss` |
| `Packet.Kiss.NinoTnc` | `Packet.Kiss.NinoTnc` | `NinoTncSerialPort`, discovery, mode switching | `Packet.Kiss` |
| `Packet.Agw` | `Packet.Agw` | AGWPE/SV2AGW client + session-as-`Stream` | `Packet.Core`, `Packet.Ax25` |
| `Packet.Axudp` | `Packet.Axudp` | `AxudpSocket` (RFC 1226) | `Packet.Ax25` |
| `Packet.Ax25` | `Packet.Ax25`, `Packet.Ax25.Session` | frame codec + factories; the connected-mode state machine, `Ax25Session`, `Ax25Listener` | `Packet.Core`, transport abstractions, `Packet.Ax25.Sdl` |
| `Packet.NetRom` | `Packet.NetRom.*` | NET/ROM wire types, routing table, forwarding, circuits | `Packet.Ax25` |

The state-machine tables that drive `Packet.Ax25/Session/` are *not* in this
repo — they come from the [`Packet.Ax25.Sdl`](https://www.nuget.org/packages/Packet.Ax25.Sdl)
package, transcribed from the AX.25 v2.2 SDL diagrams. You consume them
transitively; you never touch them.

## Seam #1 — `IAx25Transport`: "send and receive AX.25 frames"

Everything below AX.25 reduces to a single idea: a device that takes a buffer of
AX.25 frame bytes and puts them on the air, and hands you buffers it hears back.
That is `IAx25Transport`:

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

The bytes flowing through `SendAsync` / `ReceiveAsync` are **AX.25 frame bodies**:
address + control + PID + info, *without* the HDLC flags or the FCS (the transport
adds and strips whatever its medium needs). That is exactly what
`Ax25Frame.ToBytes()` produces and `Ax25Frame.TryParse(...)` consumes — so the
frame layer and the transport layer meet cleanly at this byte boundary. The
transport **pre-filters to genuine AX.25 frames**, so what you receive is always
a frame, never a wire-protocol artefact.

Two *further* abilities are optional, on separate interfaces a transport may also
implement — `ITxCompletionTransport` (confirm a frame left the wire — the de-KISS-named
ACKMODE need) and `ICsmaChannelParams` (the half-duplex-radio TXDELAY/PERSIST/SLOTTIME/TXTAIL
knobs). You feature-detect them with `is` and degrade when they're absent, so a
transport that has neither (AXUDP) implements only `IAx25Transport` and fakes
nothing. [Chapter 2](02-transports.md) covers all three interfaces in detail.

Concrete implementations (covered in [chapter 2](02-transports.md)):

- `KissTcpClient` — KISS over TCP (Dire Wolf, QtSoundModem, a BPQ KISS port).
- `KissSerialModem` — KISS over any serial port.
- `NinoTncSerialPort` — a NinoTNC over USB, with extras (mode switching, confirmed TX).
- an AXUDP adapter (`AxudpFrameTransport`) — AX.25-over-UDP, no KISS at all.

Because they all implement `IAx25Transport`, **you write your application against
the interface** and choose the transport at startup. KISS is the dominant
implementation, but it's just that — an implementation. `Packet.Agw` sits off to
the side: an AGW server runs the AX.25 link itself, so it's a *session* seam, not
a frame transport (see [chapter 2](02-transports.md#agw--sv2agw--packetagw)).

## Seam #2 — `Ax25Listener`: "a station"

The next idea up is a *station*: something with a callsign that other stations
connect to and from. That is `Ax25Listener` (`Packet.Ax25.Session`). It:

- owns exactly one `IAx25Transport`,
- runs an inbound pump that decodes every frame off the air,
- filters by your callsign (and any aliases) at the session layer,
- maintains one `Ax25Session` — a full AX.25 v2.2 connection state machine — per
  peer, building one on first contact (inbound SABM or your outbound
  `ConnectAsync`),
- exposes connectionless sends (`SendUiAsync`, `SendTestAsync`) that bypass the
  session layer,
- and raises `FrameTraced` for *every* frame in either direction, which is all a
  monitor needs.

```csharp
namespace Packet.Ax25.Session;

var listener = new Ax25Listener(transport, new Ax25ListenerOptions { MyCall = me });
await listener.StartAsync();

// Outbound: dial a peer, get a connected session back.
Ax25Session session = await listener.ConnectAsync(remote);

// Inbound: react to peers connecting to us.
listener.SessionAccepted += (_, e) => Attach(e.Session);

// Connectionless: a UI beacon, no connection needed.
await listener.SendUiAsync(destination: new Callsign("BEACON"), info: "hi"u8.ToArray());
```

You will spend chapters [5](05-axcall.md), [6](06-building-a-node.md) and
[7](07-netrom.md) inside this class. The key conceptual point now: **a "node" in
packet.net is an `Ax25Listener` plus a policy for what to do with the sessions it
hands you.** That's it. The hard part — the timers, retransmission, windowing,
SREJ recovery, version negotiation — is inside `Ax25Session`, driven by the SDL
tables, and you never implement it.

## The data-link primitives (DL)

`Ax25Session` talks to the layer above it the way the spec describes: in
**data-link service primitives**, modelled as records. You drive a session by
*posting events down* and *observing signals up*.

Down (you → session), via `session.PostEvent(...)`:

| Record (`Packet.Ax25.Session`) | Meaning |
|--------------------------------|---------|
| `DlConnectRequest()` | establish the link |
| `DlDisconnectRequest()` | tear it down |
| `DlDataRequest(ReadOnlyMemory<byte> Data, byte Pid)` | send connected-mode data |
| `DlUnitDataRequest(...)` | send a UI frame |
| `DlFlowOffRequest()` / `DlFlowOnRequest()` | assert/clear local busy |

Up (session → you), via the `DataLinkSignalEmitted` event:

| Record (`Packet.Ax25.Session`) | Meaning |
|--------------------------------|---------|
| `DataLinkConnectIndication()` | a peer connected to us |
| `DataLinkConnectConfirm()` | our outbound connect succeeded |
| `DataLinkDataIndication(ReadOnlyMemory<byte> Info, byte Pid)` | inbound connected data |
| `DataLinkDisconnectIndication()` | the link dropped |
| `DataLinkErrorIndication(string Code)` | a protocol error (code per §C5) |

In practice `Ax25Listener` does most of this plumbing for you —
`ConnectAsync` posts `DlConnectRequest` and awaits `DataLinkConnectConfirm`;
`SendData` builds `DlDataRequest`s — but the primitives are public so you can
drive a session directly when you need to. The connect client in
[chapter 5](05-axcall.md) uses both styles.

## Strict by default; pragmatism is a named flag

One design rule shapes the whole API and will save you confusion later:

> The libraries produce and accept **exactly** what AX.25 v2.2 describes by
> default. Accommodations for real-world peers exist, but each is a **named
> flag** on an options record — never a silent default.

You meet this in two places:

- **`Ax25ParseOptions`** — inbound *frame* leniency. Presets: `Strict`,
  `Lenient`, and peer-named (`Bpq`, `Xrouter`, `Direwolf`). The decoder defaults
  to `Lenient`; `Ax25Listener` lets you pick per port. See
  [chapter 3](03-frames-and-callsigns.md).
- **`Ax25SessionQuirks`** — connected-mode *behaviour* (SDL figure-defect
  workarounds and interop quirks). Presets: `Default` (spec-correct) and
  `StrictlyFaithful`. See [chapter 8](08-beyond.md).

The **outbound** construction path has no such knobs: frames you *build* are
always spec-faithful. You can accept a malformed frame from a sloppy peer; you
can never be made to transmit one.

---

Next: [open a transport and watch raw frames go by →](02-transports.md)
