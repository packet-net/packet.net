# 3. Frames & callsigns

Now we move up one layer to the thing that flows across a transport: the AX.25
frame. This chapter covers the value types (`Callsign`, `Ax25Address`,
`Ax25Frame`), the factory methods that build well-formed frames, and the
`Ax25ParseOptions` that govern how leniently you decode. We finish by building
**`axbeacon`**, a UI/beacon sender — your first program that *transmits*.

## Callsigns — `Packet.Core.Callsign`

A `Callsign` is a readonly struct: a base (1–6 chars) plus an SSID (0–15).

```csharp
using Packet.Core;

var a = new Callsign("M0LTE", 1);        // M0LTE-1
var b = Callsign.Parse("GB7RDG-7");      // base "GB7RDG", ssid 7

if (Callsign.TryParse(userInput, out var c))
    Console.WriteLine($"{c.Base} ssid {c.Ssid}");
```

SSID 0 renders without a suffix (`M0LTE`). Callsigns compare by value, so they
work as dictionary keys — which is exactly how `Ax25Listener` keys its per-peer
session cache.

A `Callsign` is the *logical* identity. On the wire, AX.25 wraps it in an
`Ax25Address` (`Packet.Core`), which adds the two HDLC control bits:

```csharp
public readonly record struct Ax25Address(Callsign Callsign, bool CrhBit, bool ExtensionBit);
```

You rarely build these by hand — the frame factories take plain `Callsign`s and
set the bits correctly — but when you *read* a received frame, its
`Destination`, `Source`, and `Digipeaters` are `Ax25Address` values, so reach for
`address.Callsign` to get the callsign back out.

## The frame — `Packet.Ax25.Ax25Frame`

`Ax25Frame` is the one type for every frame kind — UI, the I/S/U families — with
properties that mean different things depending on the kind:

```csharp
public sealed partial class Ax25Frame
{
    public Ax25Address Destination { get; }
    public Ax25Address Source { get; }
    public IReadOnlyList<Ax25Address> Digipeaters { get; }
    public byte Pid { get; }
    public ReadOnlyMemory<byte> Info { get; }

    public bool IsUi { get; }
    public bool IsCommand { get; }
    public bool IsResponse { get; }
    public bool PollFinal { get; }
    public byte Nr { get; }       // N(R), for I and S frames
    public byte Ns { get; }       // N(S), for I frames

    // Well-known PIDs:
    public const byte PidNoLayer3 = 0xF0;   // plain text / no L3
    public const byte PidNetRom   = 0xCF;   // NET/ROM
    public const byte PidSegmented = 0x08;  // §6.6 segmentation
}
```

### Building frames: the factories

Outbound construction is **always strict** — the factories produce spec-faithful
frames and there are no leniency knobs here. Each factory takes `Callsign`s (not
`Ax25Address`es) and handles the bits.

The one you'll use most is the UI factory (connectionless data — beacons, APRS,
NET/ROM NODES broadcasts):

```csharp
public static Ax25Frame Ui(
    Callsign destination,
    Callsign source,
    ReadOnlySpan<byte> info,
    byte pid = PidNoLayer3,
    bool isCommand = true,
    bool pollFinal = false,
    IEnumerable<Callsign>? digipeaters = null);
```

The rest of the family (you'll rarely build these by hand — `Ax25Session` does —
but they're public for tooling and tests):

| Family | Factories |
|--------|-----------|
| Connection setup/teardown (U) | `Sabm`, `Sabme`, `Disc`, `Ua`, `Dm`, `Frmr`, `Xid`, `Test` |
| Supervisory (S) | `Rr`, `Rnr`, `Rej`, `Srej` |
| Information (I) | `I` |

The U-frame factories take `(dest, source, pollBit/finalBit, digipeaters)`; S and
I frames also take sequence numbers and an `extended` flag selecting the
modulo-128 (2-octet) control field. For example:

```csharp
var sabm = Ax25Frame.Sabm(destination: them, source: me, pollBit: true);
var rr   = Ax25Frame.Rr(them, me, nr: 3, isCommand: false, pollFinal: false,
                        digipeaters: null, extended: false);
```

### Frames to bytes and back

```csharp
byte[] kissForm = frame.ToBytes();          // address+control+pid+info, no FCS — for an IKissModem
byte[] withFcs  = frame.ToBytesWithFcs();   // append CRC-16-CCITT — for AXUDP / raw HDLC
```

`ToBytes()` is precisely the KISS-form payload an `IKissModem` wants, closing the
loop with [chapter 2](02-transports.md).

Parsing is the inverse:

```csharp
if (Ax25Frame.TryParse(kissForm, out Ax25Frame? frame))         // lenient by default
{ /* use frame */ }

// or with explicit options / known modulo:
Ax25Frame.TryParse(kissForm, Ax25ParseOptions.Strict, out frame);
Ax25Frame.TryParse(kissForm, Ax25ParseOptions.Bpq, extended: true, out frame);
```

The `extended` overload matters because an I/S frame's control field is 1 octet
under modulo-8 and 2 under modulo-128, and you **cannot** tell which from the
bytes alone — the receiver knows it from the session's negotiated modulo. For
connectionless monitoring (UI frames, [chapter 4](04-listen.md)) modulo-8 is
correct and the simple overload is fine.

## `Ax25ParseOptions` — leniency as a named choice

This is the strict-vs-pragmatic rule from
[chapter 1](01-architecture.md#strict-by-default-pragmatism-is-a-named-flag) made
concrete. `Ax25ParseOptions` (namespace `Packet.Core`) is a record of named
boolean flags, each gating one specific real-world accommodation, with presets:

| Preset | Use it when |
|--------|-------------|
| `Ax25ParseOptions.Strict` | you want spec-exact acceptance; anything off-spec is dropped |
| `Ax25ParseOptions.Lenient` | accept-everything (the decoder's default) |
| `Ax25ParseOptions.Bpq` | the peer/channel is LinBPQ-flavoured |
| `Ax25ParseOptions.Xrouter` | XRouter-flavoured |
| `Ax25ParseOptions.Direwolf` | Dire Wolf-flavoured |

Individual flags include things like `AllowEmptyCallsignBase`,
`AllowInfoOnSupervisoryFrames`, and `AllowCommandFrameAsResponse`. You usually
just pick a preset. When you build an `Ax25Listener` ([chapter 5](05-axcall.md))
you pass the choice once via `Ax25ListenerOptions.ParseOptions` and every frame on
that port is decoded with it.

!!! tip "Why this matters operationally"
    A frame the options reject is dropped *before* it reaches your code — the port
    is deaf to it, exactly as if it had failed CRC. If a known-good peer seems
    silent, a too-strict `ParseOptions` is a prime suspect. The repo's
    [`docs/strict-vs-pragmatic-audit.md`](../docs/strict-vs-pragmatic-audit.md)
    documents every flag, what it accepts, and which preset turns it on.

## Tool #2 — `axbeacon`

A beacon is the simplest possible transmitter: build a UI frame, encode it, hand
the bytes to the modem. No state machine, no connection.

```csharp
using Packet.Core;
using Packet.Ax25;
using Packet.Kiss;
using Packet.Kiss.Serial;

// args: <port> <mycall> <text>
string port = args[0];
var me      = Callsign.Parse(args[1]);
string text = args[2];

await using IKissModem modem = KissSerialModem.Open(port);

// "BEACON" is a conventional UI destination; APRS uses "APRS", etc.
var frame = Ax25Frame.Ui(
    destination: new Callsign("BEACON"),
    source: me,
    info: System.Text.Encoding.ASCII.GetBytes(text),
    pid: Ax25Frame.PidNoLayer3);

await modem.SendFrameAsync(frame.ToBytes());
Console.WriteLine($"Beaconed {frame.ToBytes().Length} bytes from {me}.");
```

Run it and watch the frame come back on the dumper from
[chapter 2](02-transports.md) (or on any monitor on the channel). To beacon
periodically, wrap the send in a `PeriodicTimer` loop.

If you wanted to route the beacon through a digipeater path, pass
`digipeaters: new[] { Callsign.Parse("WIDE1-1") }` to the factory — the outbound
path will lay the address field out correctly.

You can now **send** and (with chapter 4) **decode**. That's connectionless
packet radio in full. The jump to *connected* mode — where frames are
acknowledged, retransmitted, sequenced, and flow-controlled — is the jump to
`Ax25Session`, and it's the subject of chapter 5. First, let's make the receive
side readable.

---

Next: [a channel monitor, `axlisten` →](04-listen.md)
