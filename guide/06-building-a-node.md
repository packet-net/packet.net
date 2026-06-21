# 6. Building a node

A *node* is a station that exists to **answer** connections rather than make
them: someone calls your callsign, you greet them, run a command loop or hand
them to an application, and clean up when they leave. In packet.net terms a node
is, almost exactly:

> an `Ax25Listener` + a handler for `SessionAccepted` + a policy for what to do
> with each session.

Everything you learned in [chapter 5](05-axcall.md) about driving an
`Ax25Session` applies — only now the sessions arrive *inbound*.

## Accepting inbound connections

The listener already accepts inbound SABMs and builds sessions; you just react to
them. `SessionAccepted` fires once per peer-initiated connect, after the session
exists and is mid-handshake:

```csharp
using Packet.Core;
using Packet.Ax25.Session;
using Packet.Ax25.Transport;

await using IAx25Transport transport = /* chapter 2: a KISS or AXUDP transport */;

var listener = new Ax25Listener(transport, new Ax25ListenerOptions
{
    MyCall = Callsign.Parse("GB7XYZ-1"),
});

listener.SessionAccepted += (_, e) => _ = HandleConnectionAsync(e.Session);

await listener.StartAsync();
await Task.Delay(Timeout.Infinite);   // run forever
```

`Ax25SessionEventArgs` carries the `Session`. From here it's symmetric with the
client: subscribe to its signals, send with `listener.SendData`, disconnect with
`PostEvent(new DlDisconnectRequest())`.

!!! tip "Attach handlers *before* data flows: `ConfigureSession`"
    `SessionAccepted` fires while the connect handshake is in flight, which is
    usually early enough. If you need to be certain you're attached before *any*
    event reaches a brand-new session, use the
    `Ax25ListenerOptions.ConfigureSession` hook — it runs once per newly-built
    session before events flow into it:

    ```csharp
    new Ax25ListenerOptions
    {
        MyCall = me,
        ConfigureSession = session =>
            session.DataLinkSignalEmitted += OnSignal,
    };
    ```

## A command loop

Most nodes present a command prompt. Because the session delivers data as it
arrives, the cleanest structure is an async channel: signals push lines in, a
loop pulls them out and responds. Here's a minimal interpreter — greet, echo a
prompt, handle a couple of commands, and `BYE` to hang up:

```csharp
async Task HandleConnectionAsync(Ax25Session session)
{
    var peer = session.Context.Remote;
    var inbox = System.Threading.Channels.Channel.CreateUnbounded<string>();

    session.AttachConsumerWithReplay((_, signal) =>
    {
        switch (signal)
        {
            case DataLinkDataIndication d:
                inbox.Writer.TryWrite(System.Text.Encoding.ASCII.GetString(d.Info.Span));
                break;
            case DataLinkDisconnectIndication:
                inbox.Writer.TryComplete();
                break;
        }
    });

    void Send(string s) => listener.SendData(session, System.Text.Encoding.ASCII.GetBytes(s));

    Send($"Welcome to GB7XYZ, {peer}.\rType HELP.\r=> ");

    await foreach (string chunk in inbox.Reader.ReadAllAsync())
    {
        string cmd = chunk.Trim().ToUpperInvariant();
        switch (cmd)
        {
            case "HELP": Send("Commands: HELP, TIME, BYE\r=> "); break;
            case "TIME": Send($"{DateTimeOffset.UtcNow:u}\r=> "); break;
            case "BYE":
                Send("73!\r");
                session.PostEvent(new DlDisconnectRequest());
                return;
            default:
                Send($"Unknown: {cmd}\r=> ");
                break;
        }
    }
}
```

Note the use of `\r` (carriage return) as the line terminator — that's the packet
convention, not `\n`. Real terminals on the far end expect CR.

This is genuinely a working single-application node. From here, "node software"
is mostly a question of *what HandleConnection does*: a BBS, a chat lobby, a
gateway, a DX cluster, a weather service. The transport, the data link, and the
session lifecycle are all handled.

## Several callsigns at once

A real node often answers for more than one callsign: the node's own call plus an
application callsign (a BBS at `GB7XYZ-2`, a chat at `GB7XYZ-3`). Register aliases
on the listener and it accepts inbound SABMs to any of them:

```csharp
listener.AddLocalAlias(Callsign.Parse("GB7XYZ-2"));   // e.g. the BBS
listener.AddLocalAlias(Callsign.Parse("GB7XYZ-3"));   // e.g. the chat
```

The session's `Context.Local` tells you which callsign a given connection came in
on, so your `SessionAccepted` handler can route to the right application. Aliases
are reference-counted — balance each `AddLocalAlias` with a `RemoveLocalAlias`.

## Several ports at once

A node usually has more than one RF port (a 2 m channel and a 70 cm channel, say).
Each port is its own `IAx25Transport` and its own `Ax25Listener`. Build one per
port, share your `SessionAccepted` handler, and you have a multi-port node:

```csharp
IAx25Transport[] transports = { vhf, uhf };
var listeners = transports
    .Select(t => new Ax25Listener(t, new Ax25ListenerOptions { MyCall = me }))
    .ToArray();
foreach (var l in listeners)
{
    l.SessionAccepted += (_, e) => _ = HandleConnectionAsync(e.Session);
    await l.StartAsync();
}
```

Routing *between* ports — accepting on VHF and dialing back out on UHF — is what
turns a node into a switch, and it's exactly what NET/ROM automates
([chapter 7](07-netrom.md)). At this layer you can already do it by hand: in your
handler, `await otherListener.ConnectAsync(target)` and pump bytes between the two
sessions.

## Refusing connections

To take the node offline to new callers without dropping live sessions, flip
`AcceptIncoming`:

```csharp
listener.AcceptIncoming = false;   // new SABMs get a DM (refused); existing links keep running
```

This is the clean way to drain a node for shutdown or maintenance.

## Connectionless services

Not every node service is connection-oriented. UI broadcasts (beacons, bulletins,
APRS, NET/ROM NODES) ride connectionless frames, which you send with
`SendUiAsync` and receive via `FrameTraced`:

```csharp
await listener.SendUiAsync(
    destination: new Callsign("ID"),
    info: System.Text.Encoding.ASCII.GetBytes("GB7XYZ node — 144.950 MHz"),
    pid: Ax25Frame.PidNoLayer3);
```

There's also `SendTestAsync` — an AX.25 TEST command, the basis of "axping": a
spec-compliant peer echoes the info field back, and you time the round trip via
`FrameTraced`.

## Where the real node host goes from here

What we've built is the spine of a node. The packet.net node host
(`Packet.Node` / `Packet.Node.Core`) wraps this same `Ax25Listener` core with the
production concerns a deployable node needs, and **`Packet.Node.Core` exposes them
as reusable building blocks** if you'd rather not reinvent them:

- **`INodeConnection`** — a transport-agnostic byte-stream abstraction over a
  session, so your application logic doesn't care whether the user arrived over
  AX.25, NET/ROM, or Telnet:

    ```csharp
    namespace Packet.Node.Core.Console;
    public interface INodeConnection : IAsyncDisposable
    {
        ValueTask<ReadOnlyMemory<byte>> ReadAsync(CancellationToken ct = default);
        ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
        // PeerId, TransportKind, Completion …
    }
    ```

- **`INodeApplication`** — the seam every node application implements; the host
  resolves a command verb to one and runs it against a connection:

    ```csharp
    namespace Packet.Node.Core.Applications;
    public interface INodeApplication
    {
        Task RunAsync(INodeConnection session, NodeAppContext context, CancellationToken ct = default);
    }
    ```

- a configuration/reconcile loop, persistence, a Telnet console, beaconing, and
  the NET/ROM service — all driven from config and reconciled live.

You can adopt those wholesale, or keep your own `SessionAccepted` handler and
borrow only the pieces you want. We come back to this menu in
[chapter 8](08-beyond.md). But there's one more protocol layer to climb first —
the one that turns a network of nodes into a *network*.

---

Next: [NET/ROM — routing and circuits →](07-netrom.md)
