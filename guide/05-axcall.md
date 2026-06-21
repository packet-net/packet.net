# 5. Call: a connected-mode client

This is the chapter where the engine earns its keep. *Connected mode* (the AX.25
data link, LAPB-style) gives you acknowledged, sequenced, flow-controlled,
automatically-retransmitted delivery over a lossy half-duplex channel — and the
full v2.2 state machine that makes it work is inside `Ax25Session`, driven by the
SDL tables. You don't implement any of it. You drive it.

We'll build **`axcall`**: connect to a remote station, pipe your keyboard to it
and its replies to your screen, and disconnect cleanly — the classic terminal
client (think `call` from ax25-tools, or the connect line of a TNC).

## The mental model

A connection is an `Ax25Session`. You almost never construct one directly;
`Ax25Listener` mints and caches them. Two things flow:

- **You → session:** events, via `session.PostEvent(...)` — or the listener's
  helpers (`ConnectAsync`, `SendData`) which post them for you.
- **Session → you:** `DataLinkSignal`s, via the `DataLinkSignalEmitted` event (or
  `AttachConsumerWithReplay`, below).

Recall the primitives from
[chapter 1](01-architecture.md#the-data-link-primitives-dl): you'll send
`DlDataRequest` / `DlDisconnectRequest` down and react to
`DataLinkDataIndication` / `DataLinkDisconnectIndication` coming up.

## Connecting

```csharp
using Packet.Core;
using Packet.Ax25.Session;

await using IKissModem modem = /* chapter 2 */;

var listener = new Ax25Listener(modem, new Ax25ListenerOptions
{
    MyCall = Callsign.Parse(args[0]),   // who we are
});
await listener.StartAsync();             // start the inbound pump FIRST

var remote = Callsign.Parse(args[1]);    // who we're calling
Ax25Session session = await listener.ConnectAsync(remote);
Console.WriteLine($"Connected to {remote}.");
```

`ConnectAsync`:

- reuses the cached session for that peer if one exists (preserving its smoothed
  round-trip time and sequence history), otherwise builds one;
- posts `DlConnectRequest` and **awaits `DataLinkConnectConfirm`**;
- returns the connected `Ax25Session`, or **throws**:
  `TimeoutException` if the connect doesn't complete within the N2 × T1V retry
  budget, or `InvalidOperationException` if the peer refused (DM) or the link was
  torn down first.

!!! warning "Start the pump before you dial"
    `ConnectAsync` throws `InvalidOperationException` if the listener isn't
    running — the inbound pump has to be live to hear the UA that confirms the
    connect. Always `await listener.StartAsync()` first.

By default the dial prefers **AX.25 v2.2** (SABME / modulo-128), degrading
automatically to v2.0 (SABM / modulo-8) for peers that answer with FRMR or DM.
You can override per call with the `ConnectAsync(remote, local, extended: false,
…)` overload, or change the default via
`Ax25ListenerOptions.PreferExtendedConnect`. You don't have to think about modulo
or version after this — the session tracks it.

## Receiving data

Subscribe to the session's signals. Inbound connected-mode data arrives as
`DataLinkDataIndication(ReadOnlyMemory<byte> Info, byte Pid)`:

```csharp
session.DataLinkSignalEmitted += (_, signal) =>
{
    switch (signal)
    {
        case DataLinkDataIndication data:
            Console.Write(System.Text.Encoding.ASCII.GetString(data.Info.Span));
            break;

        case DataLinkDisconnectIndication:
            Console.WriteLine("\n[remote disconnected]");
            quit.Cancel();
            break;

        case DataLinkErrorIndication err:
            Console.WriteLine($"\n[link error {err.Code}]");
            break;
    }
};
```

!!! tip "Don't miss the banner: `AttachConsumerWithReplay`"
    Many nodes send a greeting *the instant* they accept your connect. With a
    plain `+= DataLinkSignalEmitted` there's a sliver of a window between
    `ConnectAsync` returning and your handler attaching, in which that banner can
    fire into the void. `session.AttachConsumerWithReplay(handler)` closes it: it
    atomically replays any inbound data already buffered, then subscribes. Prefer
    it for an outbound client:

    ```csharp
    session.AttachConsumerWithReplay((_, signal) => { /* same switch */ });
    ```

    Handlers run **synchronously** on the pump thread — keep them quick; hand
    real work to another task.

## Sending data

Use the listener's `SendData` — it applies §6.6 segmentation if the payload
exceeds the negotiated frame size and the segmenter was negotiated:

```csharp
void SendLine(string line)
    => listener.SendData(session, System.Text.Encoding.ASCII.GetBytes(line + "\r"));
```

`SendData` posts the right `DlDataRequest`(s) into the session; the session
sequences them, sends I-frames, and handles acknowledgement and retransmission.
If you want to bypass segmentation and post a single raw request, you can always
`session.PostEvent(new DlDataRequest(bytes, pid))` directly — but `SendData` is
the normal path.

## Disconnecting

Politely, by posting a disconnect request and letting the handshake complete:

```csharp
session.PostEvent(new DlDisconnectRequest());
// You'll observe DataLinkDisconnectConfirm / DataLinkDisconnectIndication
// via DataLinkSignalEmitted when the link is down.
```

## Tool #3 — `axcall`, end to end

```csharp
using Packet.Core;
using Packet.Ax25.Session;
using Packet.Kiss;
using Packet.Kiss.Serial;

// usage: axcall <port> <mycall> <remote>
await using IKissModem modem = KissSerialModem.Open(args[0]);

var listener = new Ax25Listener(modem, new Ax25ListenerOptions
{
    MyCall = Callsign.Parse(args[1]),
});
await listener.StartAsync();

using var quit = new CancellationTokenSource();
var remote = Callsign.Parse(args[2]);

Console.WriteLine($"Calling {remote}…");
Ax25Session session;
try
{
    session = await listener.ConnectAsync(remote, quit.Token);
}
catch (TimeoutException) { Console.WriteLine("No answer."); return; }
catch (InvalidOperationException) { Console.WriteLine("Refused."); return; }

Console.WriteLine("*** CONNECTED.  Ctrl-D / blank line to disconnect.");

session.AttachConsumerWithReplay((_, signal) =>
{
    switch (signal)
    {
        case DataLinkDataIndication d:
            Console.Write(System.Text.Encoding.ASCII.GetString(d.Info.Span));
            break;
        case DataLinkDisconnectIndication:
            Console.WriteLine("\n*** DISCONNECTED.");
            quit.Cancel();
            break;
        case DataLinkErrorIndication e:
            Console.WriteLine($"\n*** LINK ERROR {e.Code}.");
            break;
    }
});

// Pump the keyboard into the link until EOF or disconnect.
await Task.Run(() =>
{
    string? line;
    while (!quit.IsCancellationRequested && (line = Console.ReadLine()) is not null)
    {
        if (line.Length == 0) break;
        listener.SendData(session, System.Text.Encoding.ASCII.GetBytes(line + "\r"));
    }
}, quit.Token);

session.PostEvent(new DlDisconnectRequest());
await Task.Delay(2000);     // let the disconnect handshake settle
await listener.DisposeAsync();
```

That's a complete, spec-correct connected-mode terminal in ~50 lines, and it
inherits every hard-won behaviour of the engine: T1/T2/T3 timers, go-back-N or
SREJ recovery, window management, version fallback, and (with a capable peer) XID
parameter negotiation — none of which appears in your code.

## Inspecting a live session

For status displays, an `Ax25Session` exposes read-only state:

- `session.CurrentState` — the SDL state name (`"Connected"`, `"Disconnected"`,
  `"AwaitingConnection"`, …).
- `session.Context` — the `Ax25SessionContext`: `Remote`, `VS`/`VA`/`VR`,
  window `K`, retry count `RC`, smoothed RTT, the negotiated `IsExtended`
  (modulo-128) and `SrejEnabled` flags, and more.

`listener.ActiveSessions` gives a snapshot of all cached sessions — handy for a
`/sessions` view in a multi-user program. Reading these never disturbs a session.

---

You can now make and tear down connections. A **node** is the mirror image:
instead of dialing out, it answers calls. That's almost entirely a matter of
listening to one more event.

Next: [building a node →](06-building-a-node.md)
