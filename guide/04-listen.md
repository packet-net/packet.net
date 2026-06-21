# 4. Listen: a channel monitor

A *monitor* (the classic `axlisten` / `mheard`) decodes every frame on the
channel and prints it in human-readable form. It's the natural next tool: it
exercises the decode path you'll rely on everywhere, and it's read-only, so
there's nothing to break on the air.

We'll build it twice — once at the raw transport level (full control, no session
context), and once through `Ax25Listener.FrameTraced` (less code, and it also
sees the frames *you* transmit).

## Decoding off the raw modem

The receive loop from [chapter 2](02-transports.md) handed us `KissFrame`s;
[chapter 3](03-frames-and-callsigns.md) gave us `Ax25Frame.TryParse`. Join them:

```csharp
using Packet.Core;
using Packet.Ax25;
using Packet.Kiss;

static string Fmt(Ax25Frame f)
{
    string src  = f.Source.Callsign.ToString();
    string dst  = f.Destination.Callsign.ToString();
    string path = f.Digipeaters.Count == 0
        ? ""
        : " via " + string.Join(",", f.Digipeaters.Select(d => d.Callsign));

    string kind =
        f.IsUi              ? "UI"  :
        f.IsCommand         ? "CMD" :
        f.IsResponse        ? "RSP" : "?";

    string text = f.Info.Length == 0
        ? ""
        : "  " + System.Text.Encoding.ASCII.GetString(f.Info.Span).Replace('\r', '↵');

    return $"{src,-9} > {dst,-9}{path}  [{kind} pid={f.Pid:X2}]{text}";
}

await foreach (KissFrame kf in modem.ReadFramesAsync(ct))
{
    if (kf.Command != KissCommand.Data) continue;

    if (Ax25Frame.TryParse(kf.Payload, Ax25ParseOptions.Lenient, out var frame))
        Console.WriteLine(Fmt(frame));
    else
        Console.WriteLine($"  (undecodable: {Convert.ToHexString(kf.Payload)})");
}
```

A few decisions worth calling out:

- **`Ax25ParseOptions.Lenient`** is the right default for a *monitor*. You want to
  see everything on the air, including frames a stricter peer would reject. If you
  were debugging a specific peer's conformance you might switch to `Strict` and
  watch what disappears.
- **Modulo-8 assumption.** The simple `TryParse` overload assumes a 1-octet
  control field. For UI frames and connection-setup frames that's always correct.
  For I/S frames inside a *modulo-128* connection you'd mis-read the control field
  — but a passive monitor generally can't know a passing connection's negotiated
  modulo anyway, so modulo-8 best-effort is the honest choice. (Inside your own
  sessions, the engine tracks the modulo for you; see chapter 5.)
- **Text rendering.** `f.Info` is raw bytes; treat it as opaque and only render
  what you can. AX.25 info fields are not guaranteed to be ASCII.

## The same thing via `Ax25Listener.FrameTraced`

If your program is *also* a station (it has a callsign and will connect or
accept), you'll already have an `Ax25Listener`. It raises `FrameTraced` for every
frame in **both** directions, already decoded, with a timestamp — so you get
monitoring for free, including a view of your own transmissions:

```csharp
using Packet.Ax25.Session;

var listener = new Ax25Listener(modem, new Ax25ListenerOptions
{
    MyCall = Callsign.Parse("M0LTE-1"),
    ParseOptions = Ax25ParseOptions.Lenient,   // how this port decodes the air
});

listener.FrameTraced += (_, e) =>
{
    char dir = e.Direction == FrameDirection.Transmitted ? '>' : '<';
    Console.WriteLine($"{e.Timestamp:HH:mm:ss} {dir} {Fmt(e.Frame)}");
};

await listener.StartAsync();
// …the listener's inbound pump now drives FrameTraced; keep the process alive.
await Task.Delay(Timeout.Infinite, ct);
```

`Ax25FrameEventArgs` carries `Frame`, `Direction` (`FrameDirection.Transmitted`
/ `Received`), and `Timestamp`. Note `FrameTraced` is **never filtered by
callsign** — it's the promiscuous tap, exactly what a monitor wants — whereas the
*session* layer (next chapter) only acts on frames addressed to `MyCall`.

!!! note "Which approach to use"
    Use the **raw modem** loop for a pure, standalone monitor that does nothing
    else. Use **`FrameTraced`** when monitoring is one feature of a larger station
    — you avoid running two readers against one modem (you can't; only one
    consumer can drain `ReadFramesAsync`), and you get TX visibility for free.

## Building a "heard" list

A monitor naturally grows a *heard* table — who's been active, and when. Because
`Callsign` is a value type, this is a few lines:

```csharp
var heard = new Dictionary<Callsign, DateTimeOffset>();

void Note(Ax25Frame f, DateTimeOffset at) => heard[f.Source.Callsign] = at;
```

That's the seed of the `mheard` half of node software, and of the routing-table
ingestion you'll do for NET/ROM in [chapter 7](07-netrom.md), where the "frames"
you collect are NODES broadcasts rather than free-text UI.

---

You can now observe the channel completely. Time to *participate* in it: open a
connection, exchange acknowledged data, and hang up.

Next: [a connected-mode client, `axcall` →](05-axcall.md)
