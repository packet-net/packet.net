# INP3 slice I-2 - link-timing design (LOCKED)

*Implementation-ready design for the INP3 link-timing engine - the second INP3 slice (`docs/netrom-inp3-plan.md` §9 I-2). This locks the SNTT smoother (integer IIR over RTT/2 samples), the per-neighbour INP3 state, and the host-free `Inp3Engine` surface (probe/reflect loop + 180 s reset → `MarkNeighbourDown`) so the C# reference, `@packet-net/ax25`, and pico-node can all implement it identically. It builds directly on the I-1 codecs (`Inp3L3RttFrame`) and consumes nothing the I-1 spec didn't already lock.*

**Grounding.** Every claim traces to `docs/netrom-inp3-plan.md` §5.1 (data model: `Inp3NeighbourState`, SNTT smoothing) / §6.1 (RTT measurement loop, 180 s reset), `docs/netrom-inp3-i1-wire-spec.md` §1 (the L3RTT codec this engine drives), and the existing types this design **mirrors verbatim** - `CircuitManager` (the host-free engine pattern: injected `TimeProvider`, `Tick()`, action-sink seams, no AX.25/node-host dependency), `NetRomQuality` (the integer-math house style - no floating point), and `NetRomRoutingTable.MarkNeighbourDown` (the link-down signal the 180 s reset feeds). The INP3 spec itself (Gal DB7KG / NORD><LINK, `wiki.oarc.uk/_media/packet:internodeprotocolnp.pdf`) is the reference. Genuine ambiguities the sources do not pin down are flagged **AMBIGUITY** inline and resolved against a live peer / the PDF - not invented away - before they go on the wire.

**Scope.** I-2 is *link timing only*: measure each interlink's neighbour transport time (SNTT), learn INP3 capability from the `$N`/`$IX` flags, and raise a link-down callback when a neighbour stops reflecting. **Out of scope (later slices):** the route metric / dual quality+time route space and RIF ingestion (I-3); RIF emission, triggered updates and poison-reverse (I-4); the 3-stack end-to-end harness and external interop (I-5). I-2 produces the SNTT value other slices consume but does **not** itself touch the routing table beyond signalling a down neighbour.

**Reused types (one source of truth - do not re-implement):**

| Concept | Existing type to reuse | Notes |
|---|---|---|
| L3RTT build / recognise / reflection-test | `Inp3L3RttFrame` (`Build`, `TryParse`, `TryFrom`, `IsL3Rtt`, `IsReflectionOf`, `Inp3Capable`, `IpAccept`) | I-1's codec. The engine is a *driver* over it; it adds no new wire bytes. |
| 7-octet shifted callsign / dictionary key | `Callsign` (`Packet.Core`, `readonly struct`, `IEquatable<Callsign>`, value `GetHashCode`) | Valid `Dictionary` key - the per-neighbour state is keyed by it. |
| Injected clock, `Tick()`, action sink, host-free engine shape | `CircuitManager` | `Inp3Engine` mirrors it 1:1: `TimeProvider time`, optional self-driving `ITimer`, manual `Tick()` for deterministic tests, an `Action<…>` send sink, a callback for the host. |
| Integer-only metric arithmetic (no FPU) | `NetRomQuality.Combine` (`(a*b+128)/256`) | The SNTT smoother is the same discipline: integer IIR with `+ (gain/2)` round-to-nearest, no floating point. |
| Link-down failover signal | `NetRomRoutingTable.MarkNeighbourDown(Callsign)` | The host wires the engine's `NeighbourDown` callback to this. The engine never references the routing table directly. |

---

## 0. The SNTT smoother (integer IIR over RTT/2 samples)

### 0.1 The formula (LOCKED)

The smoothed neighbour transport time **SNTT** (in milliseconds) is an integer EWMA over **RTT/2** raw samples. The locked default filter is a **1/8-gain IIR**, matching the AX.25 SRT (smoothed round-trip time) convention the timer maths in this ecosystem already uses, and the same round-to-nearest integer discipline as `NetRomQuality.Combine`:

```
  SNTT' = (7 * SNTT + sample + 4) / 8        (integer division)
```

where `sample = RTT / 2` (also integer, `RTT = now - last_l3rtt_sent_ms` in ms). This is a one-pole low-pass with gain `g = 1/8`: `SNTT' = SNTT + (sample - SNTT)/8`, rewritten to keep all intermediates non-negative for the integer divide. The `+ 4` is round-to-nearest on the divide-by-8 (`+ gainDenominator/2`), exactly the `+ 128` in `(a*b+128)/256`.

- **`sample = RTT / 2`** - the raw neighbour transport time is *half* the round trip (plan §5.1, i1-wire-spec §1.1). RTT is computed once, the integer halve happens once, then the halved value is fed to the filter. (Halving before vs. after smoothing differs only by sub-ms rounding; we halve **first** so the stored SNTT is directly the link metric the route layer sums, with no later /2.)
- **Integer-only.** No floating point anywhere - `u32`/`int` math throughout, so the pico-node M0+ has no FPU dependency and the three stacks agree bit-for-bit (the FNV-1a / quality-formula cross-stack discipline, plan §11).

### 0.2 Initial-sample seeding (LOCKED)

A fresh neighbour has no SNTT history. The standard EWMA cold-start trap is that seeding `SNTT = 0` makes the filter crawl up from zero over ~many samples, badly under-reporting the link cost at first. So:

> **The first valid sample seeds the filter directly: `SNTT := sample` (no smoothing on sample #1). Every subsequent sample applies the IIR.**

This is the canonical SRT/Karn seeding (first measurement *is* the estimate; smoothing begins at the second). State carries an `initialised` flag (or, equivalently, the sentinel `sntt_ms == SnttUnset`); the smoother branches on it:

```
  if (!initialised) { sntt = sample; initialised = true; }
  else             { sntt = (7 * sntt + sample + 4) / 8; }
```

`SnttUnset` is a named sentinel (`uint.MaxValue` / `0xFFFF_FFFF`) meaning "no measurement yet," distinct from a real `0 ms` (a same-host loopback could legitimately measure ~0). The route layer (I-3) reads `sntt_ms` only when `initialised`; an un-initialised neighbour contributes no time-route.

### 0.3 Overflow-safety bounds (LOCKED)

The intermediate `7 * sntt + sample + 4` must not overflow the accumulator and the stored SNTT must not run away on an absurd RTT. Bounds:

- **Sample clamp.** Before smoothing, clamp `sample` to `[0, SampleMaxMs]` where `SampleMaxMs = 600_000` (the INP3 600 s horizon from i1-wire-spec §2.4 - a transport time at/over the horizon is "unreachable," so there is no point smoothing a larger raw value; and the 180 s reset, §3 below, will tear the link down long before a real RTT reaches 600 s anyway). A negative `RTT` (clock went backwards / a stale reflection - see §2.4) is **discarded, not clamped to 0**: it is not a valid sample.
- **Accumulator headroom.** With `sntt ≤ 600_000` and `sample ≤ 600_000`: `7 * 600_000 + 600_000 + 4 = 4_800_004`, far under `int.MaxValue` (2.1e9) and `uint.MaxValue`. A 32-bit accumulator is safe with > 400× headroom. (Even an unclamped 16-bit-ms-unit max of 655_350 → `7*655350+655350+4 = 5_242_804` is safe.) The smoother therefore needs no widening to 64-bit; `int` intermediate, `u32` store.
- **Result range.** Because the IIR is a convex combination of two values each in `[0, 600_000]`, `SNTT'` stays in `[0, 600_000]` (round-to-nearest can land exactly on the top only when both inputs are at the top). No post-clamp is needed, but the implementation asserts `sntt ≤ SampleMaxMs` as a cheap invariant.

### 0.4 The gain is an OPTION - flagged for calibration

The exact INP3 SNTT filter constant is **interop-tuning, not wire-compat**: two nodes never exchange their smoothing gain, only the *resulting* SNTT-derived target times in RIPs (I-3/I-4), and even those are advisory metrics, not handshaked values. So the gain does **not** have to match any peer to interoperate - it only affects how twitchy vs. sluggish our own link metric is.

> **AMBIGUITY-I2-1 (flagged, do NOT silently bake in):** the Gal PDF does not pin a specific SNTT smoothing constant. We **default to 1/8** (the AX.25 SRT convention, shift-by-3, no multiply needed) because it is a sane, well-understood, cheap-on-an-MCU low-pass and matches the rest of the ecosystem's timer maths. It is exposed as a `NetRomInp3Options.SnttGainShift` knob (default `3` ⇒ gain `1/8`) **to be calibrated** against a live INP3 peer in I-5 if measured convergence behaviour warrants. Changing it is a local tuning change with **no wire-format impact** and no cross-stack-parity break *as long as all three stacks use the same configured value* (the parity requirement is "identical given identical config," exactly like a configurable quality floor).

**Generalised formula** (the shift form, so the gain stays a single integer knob and the divide is a shift):

```
  // gainShift = SnttGainShift (default 3 ⇒ 1/8). denom = 1 << gainShift.
  SNTT' = ( (denom - 1) * SNTT + sample + (denom >> 1) ) >> gainShift
```

For `gainShift = 3`: `denom = 8`, `denom-1 = 7`, `denom>>1 = 4` ⇒ exactly `(7*SNTT + sample + 4) / 8`. The default is stated as the concrete `/8` form everywhere user-facing; the shift form is the implementation so the knob is one integer.

### 0.5 Three worked numeric convergence examples

All in ms. `sample = RTT/2`. Default gain `1/8`: `SNTT' = (7*SNTT + sample + 4) / 8`. First sample seeds directly.

**Example A - convergence to a steady link (RTT steady at 400 ms ⇒ sample 200):**

| step | RTT | sample | computation | SNTT' |
|---|---|---|---|---|
| 1 (seed) | 400 | 200 | seed = sample | **200** |
| 2 | 400 | 200 | (7·200 + 200 + 4)/8 = 1604/8 | **200** |
| - | … | 200 | fixed point: (7·200+200+4)/8 = 200 (the +4 rounds the otherwise-exact 200) | **200** |

A steady input sits exactly on its fixed point - the round-to-nearest term keeps it pinned, no drift. (The exact fixed point of `x = (7x+s+4)/8` is `s` when `s` is reached; the `+4` guarantees a steady `s` reproduces `s`.)

**Example B - step up then settle (a link that got slower: RTT jumps from 100 ms to 1000 ms):**

| step | RTT | sample | computation | SNTT' |
|---|---|---|---|---|
| 1 (seed) | 100 | 50 | seed | **50** |
| 2 | 100 | 50 | (7·50+50+4)/8 = 404/8 | **50** |
| 3 | 1000 | 500 | (7·50+500+4)/8 = 854/8 = 106.75 | **106** |
| 4 | 1000 | 500 | (7·106+500+4)/8 = 1246/8 = 155.75 | **155** |
| 5 | 1000 | 500 | (7·155+500+4)/8 = 1589/8 = 198.6 | **198** |
| 6 | 1000 | 500 | (7·198+500+4)/8 = 1890/8 = 236.25 | **236** |
| … | 1000 | 500 | (asymptote → 500 over ~30-40 samples; ~1τ ≈ 8 samples to ~63% of the 450 gap → ≈ 333) | → **500** |

A 1/8-gain EWMA reaches ~63% of a step in ~8 samples (its time constant), ~95% in ~24 - appropriately sluggish so one slow probe doesn't whipsaw the route metric, while a *sustained* slowdown is fully reflected within a few probe intervals.

**Example C - single outlier rejected (steady 200 ms RTT ⇒ sample 100, with one 2000 ms spike ⇒ sample 1000):**

| step | RTT | sample | computation | SNTT' |
|---|---|---|---|---|
| 1 (seed) | 200 | 100 | seed | **100** |
| 2 | 200 | 100 | (7·100+100+4)/8 = 804/8 | **100** |
| 3 | 2000 | 1000 | (7·100+1000+4)/8 = 1704/8 = 213 | **213** |
| 4 | 200 | 100 | (7·213+100+4)/8 = 1595/8 = 199.4 | **199** |
| 5 | 200 | 100 | (7·199+100+4)/8 = 1497/8 = 187.1 | **187** |
| 6 | 200 | 100 | (7·187+100+4)/8 = 1413/8 = 176.6 | **176** |
| 7 | 200 | 100 | (7·176+100+4)/8 = 1336/8 = 167 | **167** |
| … | 200 | 100 | → back to **100** over ~several samples | → **100** |

A lone 10× spike moves SNTT by only +113 (213 vs 100) instead of jumping to 1000, and the filter walks it back to the true 100 within a handful of probes - the outlier-rejection the smoother exists for.

---

## 1. Per-neighbour INP3 state

Keyed by the neighbour's `Callsign`. Mirrors plan §5.1's `Inp3NeighbourState` exactly, with the ms-timestamp fields driven by the injected clock (no wall-clock). The engine owns a `Dictionary<Callsign, Inp3NeighbourState>` under a single lock (the `NetRomRoutingTable`/`CircuitManager` `gate` pattern).

```
Inp3NeighbourState {
    sntt_ms:              u32     // smoothed neighbour transport time (the link metric);
                                  //   SnttUnset (0xFFFF_FFFF) until the first reflection
    last_l3rtt_sent_ms:   u64     // monotonic ms when we last SENT a probe to this neighbour
                                  //   (drives the probe cadence + the RTT base); 0 = never sent
    last_reflection_ms:   u64     // monotonic ms when this neighbour last reflected our probe
                                  //   (drives the 180 s reset timer); seeded at add-time so a
                                  //   brand-new neighbour gets a full reset window before it can
                                  //   be torn down
    inp3_capable:         bool    // learned from the peer's $N flag in a reflection / its probe
    ip_accept:            Option<u8>   // from $IX (e.g. 4), if advertised; else none
}
```

C# shape (a mutable internal class, like `NetRomRoutingTable.NeighbourState`):

```csharp
private sealed class Inp3NeighbourState
{
    public uint  SnttMs;                 // SnttUnset until first reflection
    public long  LastL3RttSentMs;        // 0 = never probed; otherwise the monotonic-ms stamp
    public long  LastReflectionMs;       // seeded to "now" at add-time (full reset window)
    public bool  Inp3Capable;
    public byte? IpAccept;
    public bool  AwaitingReflection;     // a probe is outstanding (sent, not yet reflected)
    public bool  SnttInitialised => SnttMs != SnttUnset;
}
public const uint SnttUnset = uint.MaxValue;
```

Notes / locked decisions:

- **Monotonic ms time base.** All three ms-timestamp fields are *monotonic* milliseconds from the injected clock, **not** wall-clock UTC. C# uses `TimeProvider.GetTimestamp()` / `GetElapsedTime` (a monotonic source) converted to ms - *not* `GetUtcNow()` - so a wall-clock step (NTP, DST) cannot corrupt an RTT or fire/suppress the 180 s reset. (`CircuitManager` uses `GetUtcNow()` for circuit timers; INP3 is RTT-centric, so it deliberately uses the monotonic source. This is the one intentional divergence from `CircuitManager`'s clock usage, called out so the mirror is faithful where it matters and divergent only where time-correctness demands.) Rust uses its `u64` monotonic tick directly; TS uses the injected monotonic `now()`. The field type is `long` in C# / `u64` in Rust to hold a monotonic ms count.
- **`AwaitingReflection`.** A single outstanding-probe flag, not a queue: INP3 keeps at most one probe in flight per neighbour (one probe per cadence interval; the next is only sent after the prior reflects or the link is reset). This makes "is this reflection ours?" unambiguous and bounds state. A reflection arriving with `AwaitingReflection == false` (unsolicited / duplicate) is treated as a peer probe to reflect, not as our reflection (§2.4).
- **`last_reflection_ms` seeded at add-time.** When a neighbour is first registered, `LastReflectionMs = now`, so the 180 s reset window starts fresh - a neighbour is never torn down for "silence" before it has even had one probe interval to answer.
- **Lifecycle.** A neighbour entry is created lazily when the host first calls `ObserveNeighbour` (or on the first inbound L3RTT from an unknown neighbour - §2.5). It is removed on reset (§3) and re-learned on the next contact. Fixed-capacity in Rust (a const-generic array sized to the interlink count, no heap map - plan §11).

---

## 2. The host-free `Inp3Engine` link-timing surface

Mirrors `CircuitManager`: an injected `TimeProvider`, an optional self-driving `ITimer` (pass a tick interval) or a manual `Tick()` for deterministic fake-clock tests, `Action<…>` send sinks, and a host callback - **no AX.25 / node-host / routing-table dependency**. It speaks only `Callsign` + `Inp3L3RttFrame`/`NetRomPacket` in and out.

### 2.1 Public surface (LOCKED)

```csharp
namespace Packet.NetRom.Transport;   // alongside CircuitManager

public sealed class Inp3Engine : IDisposable
{
    // ── construction ──────────────────────────────────────────────────
    // localNode  : our own L3 callsign (origin we stamp into probes; the
    //              IsReflectionOf self-test). Settable later via SetLocalNode,
    //              exactly like CircuitManager (identity known at first port attach).
    // options    : cadence, reset window, SNTT gain, advertised ipAccept (§4).
    // time       : injected clock (monotonic source). Defaults to TimeProvider.System.
    // tickInterval: pass a TimeSpan to self-drive Tick() off an ITimer (production);
    //               pass null to drive Tick() manually after advancing a
    //               FakeTimeProvider (the deterministic-test path). Identical to
    //               CircuitManager's tickInterval semantics.
    public Inp3Engine(
        Callsign localNode,
        NetRomInp3Options? options = null,
        TimeProvider? time = null,
        TimeSpan? tickInterval = null);

    public void SetLocalNode(Callsign node);

    // ── host-wired seams ──────────────────────────────────────────────
    // Ship an L3RTT datagram onto the interlink toward `neighbour`. The host
    // wraps Frame.ToBytes() in a PID-0xCF I-frame on that neighbour's interlink
    // session. Must be set before any probe is due. (Mirrors CircuitManager.SendPacket.)
    public Action<Callsign /*neighbour*/, Inp3L3RttFrame /*frame*/>? SendL3Rtt { get; set; }

    // Raised when a neighbour has not reflected within the reset window (§3). The
    // host wires this to NetRomRoutingTable.MarkNeighbourDown(neighbour) and to
    // DISC/re-establish the interlink. The engine has already reset that
    // neighbour's INP3 state by the time this fires. (Mirrors the
    // IncomingCircuit event / the failover signal seam.)
    public event EventHandler<Inp3NeighbourDownEventArgs>? NeighbourDown;

    // ── inputs the host drives ────────────────────────────────────────
    // Register / refresh awareness of an interlink neighbour (e.g. when an
    // interlink session is established, or a NODES neighbour is learned). Creates
    // the per-neighbour state with a fresh reset window if new; a no-op refresh if
    // known. INP3 probing only starts once we believe the neighbour is INP3-capable
    // (learned via $N) OR optimistically probes to discover capability — see §2.6.
    public void ObserveNeighbour(Callsign neighbour);

    // Drop a neighbour the host knows is gone (interlink torn down for non-INP3
    // reasons). Removes its state; no NeighbourDown is raised (the host already knows).
    public void RemoveNeighbour(Callsign neighbour);

    // Advance the engine by one tick: (a) for each neighbour due a probe
    // (now - last_sent >= cadence AND not awaiting a reflection), emit a SendL3Rtt
    // and stamp last_sent + AwaitingReflection; (b) for each neighbour whose
    // now - last_reflection > resetWindow, raise NeighbourDown and reset its state.
    // Drive it from the internal ITimer (production) or manually after advancing a
    // FakeTimeProvider (tests). Mirrors CircuitManager.Tick().
    public void Tick();

    // Feed an inbound L3RTT frame received from `neighbour` on the interlink. The
    // caller has already recognised it as L3RTT (Inp3L3RttFrame.TryParse / TryFrom
    // off the shared receive path) OR passes the raw NetRomPacket and the engine
    // recognises it. Two overloads, same as CircuitManager.OnPacket taking a packet:
    //
    //   • If it is a reflection of OUR probe (frame.IsReflectionOf(localNode) AND we
    //     were AwaitingReflection from this neighbour): compute RTT = now - last_sent,
    //     feed RTT/2 to the SNTT smoother, stamp last_reflection, clear
    //     AwaitingReflection, and learn the peer's $N/$IX capability.
    //   • Otherwise it is a PEER's probe to us: reflect it verbatim via SendL3Rtt
    //     (byte-for-byte echo — i1-wire-spec §1.4 AMBIGUITY-L3RTT-4 locked to verbatim),
    //     and learn the peer's $N/$IX capability from it.
    public void OnL3Rtt(Callsign neighbour, Inp3L3RttFrame frame);
    public bool OnL3Rtt(Callsign neighbour, NetRomPacket packet);   // returns false if not L3RTT

    // ── surfacing / tests ─────────────────────────────────────────────
    // Immutable snapshot of per-neighbour timing state, for the console / MCP /
    // tests. Stable ordering by callsign (the NetRomRoutingTable.Snapshot discipline).
    public IReadOnlyList<Inp3NeighbourTiming> Neighbours { get; }

    // The smoothed neighbour transport time the route layer (I-3) reads; null if the
    // neighbour is unknown or has no measurement yet (SnttUnset). A pure read.
    public uint? SnttMs(Callsign neighbour);

    public void Dispose();   // stops the timer; raises nothing.
}

public sealed class Inp3NeighbourDownEventArgs : EventArgs
{
    public Callsign Neighbour { get; }      // the neighbour to MarkNeighbourDown
    public long     SilentForMs { get; }    // how long since its last reflection (≥ resetWindow)
}

public readonly record struct Inp3NeighbourTiming(
    Callsign Neighbour, uint? SnttMs, bool Inp3Capable, byte? IpAccept,
    long LastReflectionAgeMs, bool AwaitingReflection);
```

### 2.2 `Tick(now)` - the probe loop + the reset sweep (LOCKED)

```
Tick():
    now = monotonic_ms(time)
    snapshot neighbours under the lock
    for each neighbour n:
        # (a) probe-due → emit a probe
        if n.Inp3Capable (or probeUnknownCapability, §2.6)
           and not n.AwaitingReflection
           and (n.LastL3RttSentMs == 0 or now - n.LastL3RttSentMs >= cadenceMs):
              frame = Inp3L3RttFrame.Build(localNode, ipAccept: options.AdvertiseIpAccept,
                                           capabilityTextWidth: options.CapabilityTextWidth)
              n.LastL3RttSentMs   = now
              n.AwaitingReflection = true
              enqueue SendL3Rtt(n, frame)            # invoked OUTSIDE the lock

        # (b) reset-due → tear the neighbour down
        if now - n.LastReflectionMs > resetWindowMs:        # default 180_000
              record (n, now - n.LastReflectionMs) for NeighbourDown
              remove n from the table (full INP3-state reset)

    release the lock
    invoke each queued SendL3Rtt(...)
    raise NeighbourDown(...) for each recorded reset
```

- **Send/raise outside the lock.** Exactly like `CircuitManager.Tick` snapshots `byLocalKey.Values.ToArray()` and calls `c.Tick()` outside the gate, the engine collects the sends/resets under the lock and invokes the sinks/callbacks after releasing it - so a host handler that re-enters the engine (e.g. `RemoveNeighbour` inside the `NeighbourDown` handler) does not deadlock.
- **Reset wins over probe for the same neighbour in the same tick.** If a neighbour is both probe-due and reset-due, the reset is applied (the neighbour is gone); no probe is emitted to a neighbour we are tearing down. Order: compute probe-due against the *pre-reset* state, but if reset-due, drop the queued probe for that neighbour before invoking sinks. (Simplest: evaluate reset first; if reset, skip the probe branch.)
- **`AwaitingReflection` gates re-probing.** A neighbour with a probe in flight is not re-probed; the in-flight probe either reflects (clears the flag, §2.3) or the 180 s reset eventually fires. This bounds the engine to ≤ 1 probe per neighbour per cadence and one source of "is this reflection ours."

### 2.3 `OnL3Rtt` - reflection handling + capability learning (LOCKED)

```
OnL3Rtt(neighbour, frame):
    now = monotonic_ms(time)
    under the lock:
        n = ensure neighbour state (create if unknown, §2.5)
        learn capability: if frame.Inp3Capable: n.Inp3Capable = true
                          if frame.IpAccept is set: n.IpAccept = (byte)frame.IpAccept

        if frame.IsReflectionOf(localNode) and n.AwaitingReflection:
            # OUR probe came back
            rtt = now - n.LastL3RttSentMs
            n.AwaitingReflection = false
            n.LastReflectionMs   = now
            if rtt >= 0:                                   # discard a negative/stale RTT (§2.4)
                sample = clamp(rtt / 2, 0, SampleMaxMs)
                n.SnttMs = Smooth(n.SnttMs, sample, options.SnttGainShift)   # §0
            # (a negative rtt updates LastReflectionMs — the neighbour IS alive —
            #  but contributes no SNTT sample.)
            reflectToSend = none
        else:
            # a PEER's probe to us (origin != us, or we weren't awaiting one):
            # reflect it VERBATIM (i1-wire-spec §1.4 locked echo).
            reflectToSend = (neighbour, frame)
    release the lock
    if reflectToSend: SendL3Rtt(reflectToSend)             # outside the lock
```

- **Smoother seeding** is inside `Smooth` (§0.2): if `n.SnttMs == SnttUnset` it seeds `= sample`, else applies the IIR.
- **Capability is learned from *both* directions** - our reflection carries the peer's `$N`/`$IX` (it echoed *its* capability text back? no - see AMBIGUITY-I2-2), and a peer's probe carries its `$N`/`$IX` directly. Locked reading below.

> **AMBIGUITY-I2-2 (flagged, do NOT silently bake in):** when *we* probe, our frame carries *our* `$N`/`$IX`; the peer reflects it **verbatim** (i1-wire-spec §1.4), so the reflection we get back carries **our own** capability text, not the peer's - it tells us nothing new about the peer. The peer learns *our* capability from our probe; we learn *the peer's* capability from *its* probe to us (which we reflect). So **capability discovery is symmetric only because both ends probe.** Locked consequence: a neighbour's `inp3_capable` is set true when **we receive a probe from it bearing `$N`** (the `else` branch above), or when a reflection we recognise as ours nonetheless bears `$N` (degenerate - it's our own flag). We therefore **must probe-to-discover** (§2.6) rather than wait to be probed, and we must treat "received any L3RTT from this neighbour at all" as weak evidence of INP3 capability even before parsing `$N`. The PDF is terse on whether the reflector substitutes its own capability text or echoes ours; this design locks **verbatim echo + learn-peer-from-peer's-own-probe**, and flags that if a live peer instead rewrites the capability text on reflection, capability learning collapses to the simpler "reflection tells us the peer's flags" - a named-flag accommodation (`Inp3Options.PeerRewritesCapabilityOnReflect`) for I-5, not a redesign here.

### 2.4 Stale / adversarial reflections (LOCKED, totality)

The engine never throws on any input (the I-1 totality contract extends to the engine):

- **Negative RTT** (`now < last_sent` - monotonic clock should make this impossible, but a test fake or a 0-stamp edge could) → the reflection still proves liveness (`LastReflectionMs = now`, `AwaitingReflection` cleared) but contributes **no SNTT sample**. Never feed a negative sample to the filter.
- **Reflection while `AwaitingReflection == false`** (we weren't expecting one - duplicate reflection, or a peer probing us using *our* origin by accident) → treated as a **peer probe** (`else` branch): reflect it. It does not update SNTT (no outstanding probe to time). This cannot corrupt the metric.
- **Reflection from an unknown neighbour** → lazily create the state (§2.5), then process as above.
- **A non-L3RTT `NetRomPacket`** passed to the `NetRomPacket` overload → `Inp3L3RttFrame.TryFrom` returns false → the overload returns `false`, no state change. (The frame is something else the caller should route elsewhere.)

### 2.5 Lazy neighbour creation

`OnL3Rtt` and `ObserveNeighbour` both `ensure` the neighbour state: if absent, create it with `SnttMs = SnttUnset`, `LastReflectionMs = now`, `LastL3RttSentMs = 0`, `AwaitingReflection = false`, capability unknown. This means an INP3 peer that probes us first (before our host has called `ObserveNeighbour`) is still learned and reflected - robustness over ordering, the `CircuitManager` mint-on-inbound-Connect spirit.

### 2.6 Capability discovery - probe optimistically (LOCKED + flagged)

Per AMBIGUITY-I2-2, we only learn a peer is INP3-capable by *receiving its probe*. To bootstrap discovery we must probe first. Locked policy via a `NetRomInp3Options.ProbeUnknownCapability` knob (default **true**):

- **`ProbeUnknownCapability = true` (default):** probe every observed interlink neighbour at the cadence regardless of known capability. A non-INP3 neighbour simply never reflects (it sees an L3 datagram to an unknown `L3RTT-0` node and drops/forwards it harmlessly), so after one reset window it is torn down for INP3 purposes - **but** see the guard below. A capable neighbour reflects, and `inp3_capable` flips true.
- **`ProbeUnknownCapability = false`:** only probe neighbours already known INP3-capable (learned by being probed first). Conservative; relies on the peer probing us.

> **GUARD - do not `MarkNeighbourDown` a merely-non-INP3 neighbour.** The 180 s reset (§3) raises `NeighbourDown`, which the host wires to `MarkNeighbourDown` - a *routing* teardown. A vanilla (non-INP3) neighbour that never reflects our optimistic probes must **not** trigger a routing teardown - it is perfectly reachable by vanilla NODES, it just doesn't speak L3RTT. **Locked rule:** the 180 s reset only raises `NeighbourDown` for a neighbour that is **`inp3_capable == true`** (i.e. it has previously reflected / probed us, proving it speaks INP3, and *then* went silent). A never-capable neighbour that never reflects is simply **stopped being probed** (after `resetWindow` with no reflection and `inp3_capable == false`, drop it from the engine silently - no callback) so we don't probe a vanilla peer forever, but we never feed its silence into routing. This is the plan §2 "default-off overlay degrades gracefully to vanilla with non-INP3 neighbours" requirement made concrete.

This guard is the single most important behavioural decision in I-2 and is **flagged AMBIGUITY-I2-3**: the PDF does not explicitly say "only reset INP3-capable neighbours," but feeding a vanilla neighbour's non-reflection into `MarkNeighbourDown` would be a correctness bug (it would tear down good vanilla routes). The guard is the conservative, plan-aligned choice; revisit only if a live INP3 peer demands otherwise.

---

## 3. The 180 s reset

Locked behaviour, driven entirely by the injected clock (no wall-clock):

- **Trigger.** In `Tick`, a neighbour with `now - LastReflectionMs > resetWindowMs` (default `180_000` = 180 s, i1-wire-spec §1.1 / plan §6.1) is reset.
- **`inp3_capable == true` →** raise `NeighbourDown(neighbour, silentForMs)` and remove the neighbour's INP3 state. The host's handler calls `NetRomRoutingTable.MarkNeighbourDown(neighbour)` (drop every route through it - the failover signal) and DISC/re-establishes the interlink. After the callback the engine has already forgotten the neighbour; it will be re-learned (state recreated lazily) when the interlink comes back and L3RTT resumes.
- **`inp3_capable == false` →** silently drop the neighbour from the engine (stop probing it); **no** `NeighbourDown` (§2.6 guard). A purely-vanilla neighbour is never routing-torn-down by INP3.
- **Reset semantics.** "Reset that neighbour's INP3 state" = remove the dictionary entry. `SNTT`, `last_*`, capability, `AwaitingReflection` all go. A fresh entry (seeded `LastReflectionMs = now`, `SnttUnset`) is created on the next contact. There is no half-reset.
- **Idempotent / re-entrant-safe.** The callback fires after the lock is released (§2.2); a handler that calls `RemoveNeighbour` for the same neighbour is a no-op (already removed).

---

## 4. Options - `NetRomInp3Options` (TimeProvider-driven, injected)

A new record mirroring `NetRomCircuitOptions` / `NetRomRoutingOptions` (a `Default` preset, validated ranges). The cadence and reset window are `TimeSpan`s converted to ms internally; nothing reads the wall clock.

```csharp
public sealed record NetRomInp3Options
{
    // How often to probe each (capable / optimistically-probed) interlink neighbour.
    // Plan §8 l3RttIntervalSeconds default 60 s.
    public TimeSpan L3RttInterval { get; init; } = TimeSpan.FromSeconds(60);

    // Reflection-timeout → reset. Plan §8 l3RttResetSeconds default 180 s (spec value).
    public TimeSpan L3RttResetWindow { get; init; } = TimeSpan.FromSeconds(180);

    // SNTT IIR gain as a right-shift: gain = 1 / (1 << SnttGainShift). Default 3 ⇒ 1/8
    // (the AX.25 SRT convention). Interop-tuning, NOT wire-compat — AMBIGUITY-I2-1.
    // Range 1..8 (gain 1/2 .. 1/256). Validator rejects 0 (gain 1 = no smoothing,
    // pointless) and > 8 (sluggish past usefulness).
    public int SnttGainShift { get; init; } = 3;

    // Probe neighbours whose INP3 capability is not yet known (bootstrap discovery).
    // Default true. AMBIGUITY-I2-3 guard: a never-capable neighbour that never
    // reflects is dropped silently, never MarkNeighbourDown'd.
    public bool ProbeUnknownCapability { get; init; } = true;

    // The IP version to advertise in our probes' $IX token (e.g. 4), or null for none
    // ($N only). Plan §8 advertiseIp; off unless we run IP-over-NET/ROM.
    public int? AdvertiseIpAccept { get; init; } = null;

    // Emit-side capability-text pad width (i1-wire-spec AMBIGUITY-L3RTT-3). Default 8.
    public int CapabilityTextWidth { get; init; } = Inp3L3RttFrame.DefaultCapabilityTextWidth;

    public static NetRomInp3Options Default { get; } = new();
}
```

- **Validation** (a `Validate()` like the other options records, or guard in the ctor): `L3RttInterval > 0`; `L3RttResetWindow > L3RttInterval` (a reset window shorter than one probe interval would tear down a live neighbour before it could answer - reject); `SnttGainShift ∈ [1, 8]`; `AdvertiseIpAccept ∈ {null} ∪ [0,9]`; `CapabilityTextWidth ≥ 0`. The host's config validator surfaces out-of-range YAML (plan §8).
- **Hot-reload class.** Enabling/disabling INP3 is a single-port-restart (plan §8) - the engine is created/destroyed with the interlink transport; changing the gain or cadence on a *running* engine is safe (next tick uses the new value) but the plan classes the enable flag as restart-only, so I-2 does not add live-reconfigure beyond what swapping the options record gives.

---

## 5. Mirror-faithfulness checklist (vs. `CircuitManager`)

| `CircuitManager` trait | `Inp3Engine` equivalent |
|---|---|
| Injected `TimeProvider time` | same (but monotonic source - §2.1, the one intentional clock divergence) |
| Optional self-driving `ITimer` via `tickInterval`; else manual `Tick()` | identical |
| `Action<NetRomPacket>? SendPacket` sink | `Action<Callsign, Inp3L3RttFrame>? SendL3Rtt` sink |
| `event IncomingCircuit` host callback | `event NeighbourDown` host callback |
| `OnPacket(NetRomPacket)` input | `OnL3Rtt(Callsign, Inp3L3RttFrame)` / `OnL3Rtt(Callsign, NetRomPacket)` |
| `Tick()` advances all circuits | `Tick()` probes due + sweeps resets |
| single `gate` lock; snapshot-then-act-outside-lock | identical |
| `SetLocalNode` late identity | identical |
| `Dispose()` stops timer | identical |
| no AX.25 / node-host dependency | identical (speaks `Callsign` + L3RTT frame only) |

---

## 6. Open ambiguities to resolve before I-3/I-5 (do NOT silently bake in)

| ID | What's ambiguous | This design's locked interim choice | Resolution path |
|---|---|---|---|
| **I2-1** | Exact SNTT smoothing constant | 1/8 gain (`SnttGainShift = 3`), AX.25-SRT convention; an option | Interop-tuning, **not** wire-compat - calibrate vs. live peer in I-5; no parity break if all stacks share the config |
| **I2-2** | Does the reflector echo our capability text verbatim or substitute its own? | Verbatim echo (per i1 §1.4); learn the *peer's* capability from *its* probe, not from our reflection ⇒ both ends must probe | Re-validate vs. live INP3 peer; `PeerRewritesCapabilityOnReflect` flag if it rewrites |
| **I2-3** | Should a never-reflecting *vanilla* neighbour trigger `MarkNeighbourDown`? | **No** - only an `inp3_capable` neighbour that goes silent resets into routing; a never-capable silent neighbour is dropped from probing only, no callback | Plan §2 graceful-degradation requirement; conservative, revisit only if a peer demands |
| **I2-4** | Halve RTT before or after smoothing? | **Before** (`sample = RTT/2`), so stored SNTT is directly the link metric | Sub-ms rounding only; locked for one-source-of-truth metric |
| **I2-5** | Monotonic vs. UTC clock for the timing fields | **Monotonic** (`GetTimestamp`/`u64` tick), unlike `CircuitManager`'s UTC | RTT-correctness demands it; flagged so the mirror divergence is intentional |

---

## 7. Cross-stack parity requirement

Per plan §11 and the FNV-1a / quality-formula discipline: the three stacks (`Packet.NetRom` C#, `@packet-net/ax25` TS, pico-node Rust) **must produce byte-identical SNTT values given identical samples + identical configured gain.** The §0.5 worked examples become **shared golden vectors** in the cross-stack parity suite: feed the same `(seed, sample…)` sequence, assert the same SNTT trajectory in all three. The C# reference smoother is authoritative; TS and Rust mirror its integer arithmetic 1:1 (no `Math.round` / float anywhere - the shift-and-add form is exact and language-agnostic). Rust stays `no_std`-clean: integer-only SNTT maths (no FPU), fixed-capacity neighbour state (const-generic array, no heap map - plan §11), `u64` monotonic tick. Deterministic engine tests (fake clock): RTT→SNTT convergence (the §0.5 examples), the 180 s reset fires for a capable neighbour and raises `NeighbourDown`, a vanilla neighbour is dropped without a callback (I2-3 guard), a peer probe is reflected verbatim, capability is learned from a peer's probe.
