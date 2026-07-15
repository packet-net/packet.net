# 8. Beyond

You can now build the whole stack — transport, frames, connected sessions, a
node, and NET/ROM. This chapter is a map of what's left: the engine
behaviours that ran quietly under the earlier chapters, the knobs that control
them, how to test code built on the engine, and the `Packet.Node.Core` building
blocks you can adopt instead of growing your own host.

## Segmentation and reassembly (§6.6)

AX.25 caps an information field at N1 octets. When an upper layer needs to send
more, §6.6 segmentation splits the payload into a sequence of PID-`0x08`
fragments that the far end reassembles. The engine does this transparently:

- **Sending:** `listener.SendData(session, data, pid)` runs the payload through
  the session's segmenter when it exceeds N1 *and* the segmenter was negotiated
  (via XID). If it's over-size on a session that didn't negotiate the segmenter,
  `SendData` throws `InvalidOperationException` rather than silently truncating.
- **Receiving:** reassembly is wired into every session's inbound path, so a
  `DataLinkDataIndication` you receive already carries the reassembled whole.

So you generally don't think about it — you `SendData` a logical message and
receive a logical message. Just be aware the limit exists, and that fragmentation
requires a peer that negotiated it.

## XID parameter negotiation

AX.25 v2.2 peers negotiate link parameters — window size, frame length,
acknowledgement timer, retry count, and whether SREJ is available — via **XID**
exchange. The engine drives this through a per-session management data-link
(`Ax25ManagementDataLink`); you don't call it directly. What you control is the
*policy*:

- On a v2.2 (SABME) dial, XID is negotiated automatically after the UA.
- On a v2.0 (mod-8) dial, `Ax25ListenerOptions.PreConnectXidNegotiatesSrej`
  (default `true`) runs a pre-SABM XID exchange to negotiate SREJ with peers like
  LinBPQ that only honour XID *before* the connect. It's always safe to leave on:
  a peer that doesn't answer XID just falls through to a plain go-back-N link.

The negotiated results land on `session.Context` (`SrejEnabled`, the effective
window, `IsExtended`, …) where your status code can read them. If you need to
parse or build XID info fields yourself — for a gateway, a conformance harness —
the codec is public as `XidInfoField` / `XidParameters` with its own
`XidParseOptions` (the same named-flag pattern).

## Quirks: `Ax25SessionQuirks`

This is the connected-mode sibling of `Ax25ParseOptions`
([chapter 3](03-frames-and-callsigns.md#ax25parseoptions--leniency-as-a-named-choice)).
Where `Ax25ParseOptions` governs how leniently you *decode frames*,
`Ax25SessionQuirks` governs *state-machine behaviour* — specifically two classes
of deviation from the printed SDL:

- **SDL figure-defect workarounds** (named `Ax25Spec<NN>…`): places where the
  published v2.2 diagrams contain a defect, and the spec-correct behaviour
  differs from the figure as drawn.
- **De-facto interop quirks**: real-world behaviours like Dire Wolf's
  segmentation format.

Two presets:

| Preset | Meaning |
|--------|---------|
| `Ax25SessionQuirks.Default` | every quirk on — **spec-correct**, the right choice for on-air use |
| `Ax25SessionQuirks.StrictlyFaithful` | every quirk off — runs the figures *exactly* as drawn, defects included; conformance study only |

Set it once via `Ax25ListenerOptions.Quirks`; it's seeded onto each new session's
`Context.Quirks`. You'll almost always want `Default` (which is what you get if
you set nothing). `StrictlyFaithful` exists for people studying the spec against
the diagrams, not for talking to real stations.

!!! info "The discipline behind the flags"
    Every flag in `Ax25ParseOptions`, `XidParseOptions`, and `Ax25SessionQuirks`
    is documented in [`docs/strict-vs-pragmatic-audit.md`](../docs/strict-vs-pragmatic-audit.md)
    with its wire driver, what it accepts/changes, and which presets enable it.
    If you're deciding how to talk to a particular peer, that table is the
    reference.

## Observability

`Ax25Listener.FrameTraced` (every frame, both directions, timestamped) is your
single tap for monitoring, logging, and `mheard`-style displays — you met it in
[chapter 4](04-listen.md). For per-session introspection, `session.CurrentState`
and `session.Context` expose the live state machine (sequence variables, timers,
retry counter, smoothed RTT), and `listener.ActiveSessions` enumerates all cached
sessions. Together these are enough to build a full station status dashboard
without reaching inside the engine.

## Testing engine-backed code

The engine is built to be testable, and your code on top of it can borrow the
same seams:

- **Deterministic time.** `Ax25Listener` and the sessions take a
  `TimeProvider`. Pass `Microsoft.Extensions.Time.Testing.FakeTimeProvider` and
  you can drive T1/T2/T3 expiry, connect timeouts, and retransmission in a unit
  test with no real waiting:

    ```csharp
    var time = new FakeTimeProvider();
    var listener = new Ax25Listener(fakeTransport, options, time);
    // …advance time.Advance(TimeSpan.FromSeconds(6)) to fire T1, etc.
    ```

- **A fake transport.** `IAx25Transport` is a tiny interface — implement it over
  an in-memory queue (or pair two together) to script frames in and assert on
  frames out, no hardware required. `SendAsync` enqueues what your code transmits;
  `ReceiveAsync` yields the `Ax25InboundFrame`s you want it to hear.

- **The interop stack.** For end-to-end confidence against real implementations,
  the repo ships a docker compose stack (LinBPQ + XRouter + rax25 + net-sim) and
  an `Interop` test category. See the [project README](../README.md) and
  [`CLAUDE.md`](../CLAUDE.md) for the commands.

## Adopting the node host's building blocks

If your ambitions run to a full deployable node, you don't have to grow the
config-reconcile-persist-multiport machinery yourself — `Packet.Node.Core`
already exposes it as composable parts, all sitting on the same `Ax25Listener`
core you've been using:

| Building block | Namespace | What it gives you |
|----------------|-----------|-------------------|
| `INodeConnection` | `Packet.Node.Core.Console` | a transport-agnostic byte stream over a session (AX.25 / NET/ROM / Telnet alike) |
| `INodeApplication` | `Packet.Node.Core.Applications` | the application seam: `RunAsync(connection, context, ct)` — a BBS, chat, gateway plug in here |
| `NetRomService` | `Packet.Node.Core.NetRom` | the assembled NET/ROM node from [chapter 7](07-netrom.md): NODES ingestion, advertising, forwarding, circuits, interlinks |
| `IConfigProvider` / `AddPacketNode(...)` | `Packet.Node.Core.*` | config-driven hosting: a `Microsoft.Extensions.Hosting` `BackgroundService` that supervises ports and reconciles live config changes |

The dependency-injection entry point ties it together:

```csharp
using Packet.Node.Core.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddPacketNode(configPath: "node.yaml", dbPath: "pdn.db");
await builder.Build().RunAsync();
```

That registers the transport factory, the port supervisor, the NET/ROM service,
beaconing, and the persistence stores, and runs them as a hosted service. You
provide config and applications; the host runs the cake.

The choice is yours: keep the hand-rolled `SessionAccepted` handler from
[chapter 6](06-building-a-node.md) for something small and bespoke, or stand on
`Packet.Node.Core` for a full node and spend your effort on the application
layer. Either way, the layers underneath are the same ones this guide walked you
through, and nothing in them is hidden from you.

## Where to read next

- [`README.md`](../README.md) — the library publication matrix and the
  sibling-repo map (`ax25sdl` for the SDL tables, `ax25-ts` for the TypeScript
  port).
- [`docs/plan.md`](../docs/plan.md) — the living design document and the source of
  truth for where the project is heading.
- [`docs/strict-vs-pragmatic-audit.md`](../docs/strict-vs-pragmatic-audit.md) —
  every named leniency flag, justified.
- The per-library READMEs, e.g.
  [`src/Packet.Kiss.NinoTnc/README.md`](../src/Packet.Kiss.NinoTnc/README.md).
- The `examples/` directory — small, complete node applications (`wall`,
  `lobby`) showing the `INodeApplication`-style contract from the other side.

---

That's the engine, bottom to top. You started by dumping hex off a modem; you can
now build a routed, multi-port, spec-correct packet node — and you've seen every
seam you'd reach for in between. One optional leg remains: the seams that let
your station see the radio behind the modem.

Next: [radios & rigs — RSSI, carrier-sense, and CAT control →](09-radios-and-rigs.md)
