# NET/ROM INP3 - C# host-integration design (the awareness slice)

*Locks the design for wiring the four landed, host-free INP3 pieces (`Inp3Engine`, `Inp3UpdateScheduler`, `NetRomRoutingTable.{IngestRif,BuildRif,MarkNeighbourDown}`, and the `Wire/Inp3*` codecs) into the deployable node host (`Packet.Node.Core`'s `NetRomService`), behind a single default-off `inp3.enabled` gate. Companion to `netrom-inp3-plan.md` §7 (architecture / integration points) + §8 (config surface) and the slice designs `netrom-inp3-i2-design.md` / `-i3-design.md` / `-i4-design.md`. This is the build-PR design the §I-4 doc deferred ("Host wiring … is the I-4 build-PR scope … this doc locks the host-free table + scheduler contracts only").*

**Scope: AWARENESS ONLY.** Probe (L3RTT) / ingest (RIF) / advertise (RIF) / reset (180 s → `MarkNeighbourDown`). Forwarding-*by-time* - feeding `Inp3RouteSelector.SelectActiveRoute` into the forward / connect next-hop pick (`ForwardDatagram`, `SendNetRomPacket`, `ConnectCircuitAsync`) - is a **named follow-up, out of this slice** (see §8). With INP3 on, the node *learns and tells* the time-space; it does not yet *route by* it. `preferInp3Routes` is plumbed as config + validated but is **inert** this slice (no selection consumer wired). That is the one deliberate, named narrowing.

---

## 0. The contracts as they actually ship (read this first - there is doc/code drift)

The four pieces are host-free and event/sink/intent-shaped. The **shipped signatures** (not the prose in some XML docs) are the contract this design wires to:

| Piece | Shipped surface this slice consumes |
|---|---|
| `Inp3Engine` | ctor `(Callsign localNode, NetRomInp3Options?, TimeProvider?, TimeSpan? tickInterval)`; `SetLocalNode`; `ObserveNeighbour(call)`; `RemoveNeighbour(call)`; `Tick()`; `OnL3Rtt(Callsign, NetRomPacket) → bool`; sink `Action<Callsign,Inp3L3RttFrame> SendL3Rtt`; event `EventHandler<Inp3NeighbourDownEventArgs> NeighbourDown`; reads `SnttMs(Callsign) → uint?`, `Neighbours` (for the capable set). |
| `Inp3UpdateScheduler` | ctor `(NetRomInp3Options?, TimeProvider?, TimeSpan? tickInterval)`; `SetTargetNeighbours(IReadOnlyCollection<Callsign>)`; `MarkDirty(dest, Inp3UpdateClass)`; `MarkWithdrawn(dest)`; `Tick()`; sink `Action<Inp3AdvertiseIntent> Advertise` (intent = `(Callsign Neighbour, Inp3AdvertiseReason Reason)`). |
| `NetRomRoutingTable` | `IngestRif(fromNeighbour, myCall, neighbourSnttMs, Inp3Rif, hopLimit)`; **`BuildRif(Callsign myCall, Callsign toTargetNeighbour)` - 2-arg, returns `Inp3Rif`**; `MarkNeighbourDown(neighbour) → int`. |
| Wire | `Inp3L3RttFrame.{IsL3Rtt(packet), TryFrom(packet, out frame), Build(...)}`; `Inp3Rif.{TryParse(span, out rif), ToBytes()}`; `Inp3Rip.HorizonMs`; `Inp3Rif.Signature == 0xFF`. |

**DRIFT TO FLAG (do not silently follow the XML docs):**

1. **`BuildRif` is 2-arg.** `Inp3UpdateScheduler`'s XML comments and `netrom-inp3-i4-design.md` §6.1 say `BuildRif(myCall, N, preferInp3Routes)`. The **shipped** method is `BuildRif(Callsign myCall, Callsign toTargetNeighbour)` - no `preferInp3Routes`. The shipped emitter advertises a destination **iff the table holds any INP3 route for it** (explicitly *independent* of `preferInp3Routes` - see the `BuildRif` body comment: "We ADVERTISE a destination iff we HOLD an INP3 time-route for it … independent of preferInp3Routes, which is a local forwarding preference, not an advertisement gate"), and poisons over the **full kept next-hop set**, not the single `Inp3RouteSelector`-selected next hop. **The host calls the 2-arg form and passes `preferInp3Routes` nowhere into emission.** This is *consistent* with the awareness-only scope: advertisement does not depend on the (un-wired) forwarding preference. Resolution: treat the 2-arg shipped form as authoritative; leave a one-line audit note that the i4 §6.1 pseudocode + the scheduler XML are stale on this point (a docs-only fix, separate from this build PR).

2. **Recently-withdrawn set does not exist yet.** `netrom-inp3-i4-design.md` AMBIGUITY-I4-5 **locked** it to "the routing table; `BuildRif` consumes-and-clears it," but **no such set is implemented** - `BuildRif`, `WithdrawInp3`, `MarkNeighbourDown`, `Sweep` contain no withdrawn-set code today. **This slice adds it** (§6). It is genuinely new table state + a new consume/clear API, not just wiring.

---

## 1. Top-level structure: an inner `Inp3Host`, created iff `inp3.enabled`

All INP3 host state lives in **one nested type**, `NetRomService.Inp3Host`, constructed **only** when `config.Enabled && config.NetRom.Inp3.Enabled`. `NetRomService` holds a single nullable field:

```csharp
private readonly Inp3Host? inp3;   // null ⇔ INP3 disabled ⇔ byte-for-byte today
```

`Inp3Host` owns the `Inp3Engine`, the `Inp3UpdateScheduler`, the resolved `NetRomInp3Options`, and the glue (the `SendL3Rtt` sink, the `NeighbourDown` handler, the `Advertise` sink, the capable-neighbour refresh). It does **not** own the routing table (that stays `NetRomService.table`, shared with the vanilla L3/L4 paths - INP3 routes are a second metric space *on the same table*, per I-3) and does **not** own the interlinks map (it calls back into `NetRomService` private helpers to send).

**Why a nested type, not inline fields + null-checks everywhere:** every INP3 call site becomes `inp3?.X(...)` - one null-guard per seam, and the "default-off ⇒ none of this exists" guarantee (§7) is *structural*, not a scatter of `if (config.Inp3.Enabled)`. The pattern mirrors the existing `circuits` (a nullable `CircuitManager` created only under `config.Connect`).

```
NetRomService
  ├─ table : NetRomRoutingTable                 (shared; INP3 adds a 2nd metric space + the withdrawn set)
  ├─ interlinks : ConcurrentDictionary<...>     (shared; INP3 reuses the send path)
  ├─ circuits : CircuitManager?                 (L4; unchanged)
  └─ inp3 : Inp3Host?                            (NEW — null unless inp3.enabled)
        ├─ engine : Inp3Engine
        ├─ scheduler : Inp3UpdateScheduler
        └─ options : NetRomInp3Options           (resolved from config)
```

Construction (in the `NetRomService` ctor, inside the existing `if (config.Enabled)` block, after `circuits` is set up):

```csharp
if (config.NetRom.Inp3.Enabled)
{
    inp3 = new Inp3Host(this, ResolveInp3Options(config.NetRom), this.timeProvider);
    // engine.SendL3Rtt, engine.NeighbourDown, scheduler.Advertise wired inside Inp3Host ctor.
}
```

`Inp3Host` self-drives its ticks off its own `TimeProvider` timers (see §5) - it does **not** ride the NODES `sweepTimer` (a 3600 s cadence is far too slow for a 60 s probe / 5 s debounce). The engine + scheduler each already accept a `tickInterval`; the host passes a sub-cadence interval (§5).

---

## 2. CONFIG

### 2.1 The record: `NetRomConfig.Inp3 : NetRomInp3Options`

Add **one** property to `NetRomConfig` (the host config record in `Packet.Node.Core/Configuration/NodeConfig.cs`):

```csharp
/// <summary>
/// The INP3 routing overlay (default-off). When <c>Inp3.Enabled</c> is false the
/// node behaves byte-for-byte as today: no L3RTT probing, no RIF ingest/emit, no
/// INP3 routes. INP3 is an opt-in overlay on the vanilla quality-based NET/ROM
/// stack; see netrom-inp3-plan.md §8.
/// </summary>
public NetRomInp3Options Inp3 { get; init; } = new();   // == NetRomInp3Options.Default; Enabled=false
```

**Decision - reuse `NetRomInp3Options` directly, do not wrap.** `NetRomInp3Options` already lives in `Packet.NetRom.Wire`, is already a validated record with a `Default`, already carries every knob this slice needs (`Enabled`, `PreferInp3Routes`, `L3RttInterval`, `L3RttResetWindow`, `SnttGainShift`, `ProbeUnknownCapability`, `AdvertiseIpAccept`, `CapabilityTextWidth`, `HopLimit`, `RifInterval`, `PositiveDebounce`, `WorsenThresholdMs`), and already has a `Validate()`. `NodeConfig` already references `Packet.NetRom` (the `using Packet.NetRom;` at the top, for `NetRomForwardMode`), so the dependency is free. This is **the only NET/ROM type so far that the host binds directly** rather than mirroring as nullable host knobs (`NetRomConfig` mirrors `DefaultNeighbourQuality?`, `Window?`, etc. as nullable overlays over `…Options.Default`).

**AMBIGUITY-HOST-1 (flagged): direct-bind vs nullable-overlay.** The vanilla NET/ROM knobs use the nullable-overlay style (`int? Window` → resolved against `NetRomCircuitOptions.Default`). INP3 instead binds the whole `NetRomInp3Options` record. **Locked: direct-bind.** Rationale: (a) the overlay style exists to let a partial host config inherit lib defaults field-by-field; YamlDotNet's deserializer already gives exactly that - an absent YAML key leaves the record's C# default in place, because the property initialises to `new()` and YamlDotNet only sets keys it finds (`IgnoreUnmatchedProperties` + camel-case mapping, confirmed in `NodeConfigYaml.cs`). (b) `NetRomInp3Options` is durations + ints + bools (no discriminated union, no `Callsign` struct - those were the things that forced string-in-config before), so it binds cleanly. (c) one source of truth for the knobs + their `Validate()`. The cost: a YAML typo on a *nested* INP3 key is silently ignored (unmatched-property), same as every other key in this config - acceptable and consistent.

`TimeSpan`-typed knobs (`L3RttInterval`, `L3RttResetWindow`, `RifInterval`, `PositiveDebounce`) deserialize from YAML scalars via YamlDotNet's built-in `TimeSpan` converter. **The YAML carries them as durations** (e.g. `l3RttInterval: 00:01:00` or the invariant `TimeSpan` text), **not** as the plan §8's `…Seconds` integers. This is a deliberate, flagged divergence from plan §8 (§2.2 below).

**The JSON wire is a different form, decided later (#688).** YAML is the human-facing import/export path; the *management API* body and the `pdn.db` config blob are JSON, and there they carry **whole seconds as a JSON number** (`"l3RttInterval": 60`) via `SecondsTimeSpanJsonConverter`, registered in `NodeConfigJson`'s canonical option set and handed to `ConfigureHttpJsonOptions` by the composition root so the two dialects cannot drift. Reads still accept the legacy `"00:01:00"` string, so config blobs written before that converter existed load without a migration. The client (`web/packetnet-ui/src/lib/types.ts` `Inp3Config`) types all four as `number` and the config screen edits them as seconds, which is what made this the wire form worth having.

### 2.2 The YAML mapping

A nested `inp3:` block under `netRom:`:

```yaml
netRom:
  enabled: true
  # … existing vanilla knobs …
  inp3:
    enabled: false            # master switch (default false = exactly today)
    preferInp3Routes: false   # plumbed + validated; INERT this slice (forwarding-by-time deferred)
    snttGainShift: 3          # IIR gain 1/8 (cross-stack-pinned)
    probeUnknownCapability: true
    advertiseIpAccept:        # null unless IP-over-NET/ROM
    capabilityTextWidth: 8
    hopLimit: 30
    worsenThresholdMs: 1000
    l3RttInterval: 00:01:00       # 60 s probe cadence
    l3RttResetWindow: 00:03:00    # 180 s reflection-timeout → reset
    rifInterval: 00:05:00         # 300 s periodic full-RIF cadence
    positiveDebounce: 00:00:05    # 5 s positive coalescing window
```

No new converter, no new YAML class - `NodeConfigYaml`'s existing `DeserializerBuilder` (camel-case naming, `IgnoreUnmatchedProperties`, `TimeSpan` built-in) handles the nested record with **zero** code changes. Round-trip (`Serialize`) emits the same shape; `OmitNull` drops `advertiseIpAccept` when unset.

**AMBIGUITY-HOST-2 (flagged): `TimeSpan` keys vs plan §8's `…Seconds` ints.** Plan §8 shows `l3RttIntervalSeconds: 60` etc. The shipped `NetRomInp3Options` types these as `TimeSpan` (`L3RttInterval`, not `L3RttIntervalSeconds`). **Locked: bind the `TimeSpan` properties directly** (YAML key `l3RttInterval`, value an ISO/`TimeSpan` duration). Rationale: re-introducing `…Seconds:int` host-shadow properties + a hand-mapping back to `TimeSpan` would re-create exactly the overlay boilerplate AMBIGUITY-HOST-1 rejected, and would need a *second* `Validate()` for the shadow fields. The plan §8 YAML is illustrative and predates the typed options record; the design supersedes it. (Note for the build PR: update plan §8's YAML snippet + the `NodeConfigTemplate` sample to the `TimeSpan` form.) If operator-friendliness of `60` over `00:01:00` later matters, a `…Seconds` alias converter is a clean follow-up, not this slice. *(Resolved for JSON only, 2026-08-16 / #688: `SecondsTimeSpanJsonConverter` makes the JSON wire seconds. The YAML above is unchanged; an operator's `l3RttInterval: 00:01:00` still parses, and `pdn config export` still emits it.)*

### 2.3 The validator call

In `NetRomValidator` (the existing `AbstractValidator<NetRomConfig>` in `NodeConfigValidator.cs`), add **one** rule that delegates to the record's own `Validate()`, plus the cross-field enabled-requires-enabled guard matching `broadcast`/`connect`:

```csharp
// INP3 overlay: delegate range checks to the record's own Validate() (one source of
// truth for the knob ranges), surfaced as a FluentValidation failure so a bad nested
// inp3: block rejects the whole candidate config atomically (FileConfigProvider).
RuleFor(c => c.Inp3)
    .NotNull()
    .Must(BeValidInp3Options)
    .WithMessage(c => $"netrom.inp3 is invalid: {DescribeInp3Fault(c.Inp3)}");

// inp3.enabled requires netrom.enabled — an overlay on a deaf node is meaningless
// (mirrors the broadcast/connect-require-enabled guards).
RuleFor(c => c)
    .Must(c => !(c.Inp3.Enabled && !c.Enabled))
    .WithMessage("netrom.inp3.enabled requires netrom.enabled.");
```

with the helpers (in `NetRomValidator`):

```csharp
private static bool BeValidInp3Options(NetRomInp3Options o)
{
    try { o.Validate(); return true; }
    catch (ArgumentOutOfRangeException) { return false; }
}

private static string DescribeInp3Fault(NetRomInp3Options o)
{
    try { o.Validate(); return "ok"; }
    catch (ArgumentOutOfRangeException ex) { return ex.Message; }
}
```

**Why delegate, not re-encode in FluentValidation:** `NetRomInp3Options.Validate()` already encodes every range + the two cross-field rules (`L3RttResetWindow > L3RttInterval`; `0 < PositiveDebounce < RifInterval`). Re-stating them as `RuleFor` chains would be a second source of truth that drifts. The `try/Validate` bridge is the same "one validation authority" discipline the callsign rule uses (round-trip through `Callsign.TryParse`). It runs **before** the candidate becomes `IConfigProvider.Current` (atomic apply), so a malformed `inp3:` block is rejected whole - never half-applied.

**Default-off proof at the config layer:** `NetRomConfig.Inp3` defaults to `new()` ⇒ `NetRomInp3Options.Default` ⇒ `Enabled == false`. A config with **no `inp3:` block at all** therefore validates and yields `Inp3.Enabled == false` - the node creates no `Inp3Host` (§1) and is byte-for-byte today. Existing configs / tests need no change.

---

## 3. INBOUND DISPATCH - the load-bearing distinction

This is the crux. Today `OnInterlinkData(session, info)` parses the 0xCF info as a `NetRomPacket` and routes it to circuits (dest == us) or forwarding (dest != us). With INP3 on, the **same 0xCF interlink stream** also carries L3RTT datagrams and RIF I-frames, and they must **never** reach the circuit manager or the forwarder.

### 3.1 The two INP3 carriers, and how each is recognised

| Carrier | Wire shape on the 0xCF interlink | Recogniser | Fed to |
|---|---|---|---|
| **L3RTT** | a `NetRomPacket` whose **destination base is the literal `L3RTT`** (SSID ignored) and transport opcode nibble `0x02` | `Inp3L3RttFrame.IsL3Rtt(packet)` / `Inp3L3RttFrame.TryFrom(packet, out frame)` | `engine.OnL3Rtt(fromNeighbour, packet)` |
| **RIF** | an **I-frame whose info field's first byte is the `0xFF` signature** (`Inp3Rif.Signature`), the rest a RIP sequence | `Inp3Rif.TryParse(info, out rif)` (signature-gated; non-0xFF → false) | `table.IngestRif(fromNeighbour, myCall, engine.SnttMs(fromNeighbour) ?? Inp3Sntt.Unset, rif, options.HopLimit)` |

L3RTT and a vanilla L4 datagram are **both** `NetRomPacket`s - they differ only in the destination/opcode, which `IsL3Rtt` keys on. A RIF is **not** a parseable `NetRomPacket` (its info starts with `0xFF`, a NET/ROM network-header origin-callsign byte that won't frame as a sane L3 datagram in general) - but we must **not** rely on that coincidence; we gate the RIF check on the explicit `0xFF` signature byte, which is the one unambiguous discriminator that `Inp3Rif.TryParse` already enforces.

### 3.2 The exact precedence in `OnInterlinkData` (LOCKED)

The check order is a **correctness** property: a RIF or L3RTT must be peeled off *before* the existing L4 `NetRomPacket` parse so it can never be mis-fed to circuits/forwarding. Order:

```
OnInterlinkData(session, info):
    fromNeighbour = session.Context.Remote

    # ── INP3 taps come FIRST, and only when the overlay is on. ──────────────
    if inp3 is not null:                                    # ⇔ inp3.enabled
        # (A) RIF? — signature-gated on the raw info FIRST. Cheapest + most
        #     specific check (one byte). A 0xFF-led I-frame is a RIF, never an
        #     L4 datagram or an L3RTT (an L3RTT is a well-formed NetRomPacket,
        #     whose first byte is a shifted origin callsign, not 0xFF).
        if info.Span.Length >= 1 and info.Span[0] == Inp3Rif.Signature:
            if Inp3Rif.TryParse(info.Span, out rif):
                inp3.IngestRif(fromNeighbour, rif)         # → table.IngestRif(...)
            return                                          # consumed (parsed or not) — never falls through
                                                            #   a 0xFF-led-but-unparseable frame is a malformed
                                                            #   RIF, NOT an L4 datagram; dropping it is correct.

        # (B) L3RTT? — parse as a NetRomPacket, classify by dest/opcode.
        if NetRomPacket.TryParse(info.Span, out packet):
            if Inp3L3RttFrame.IsL3Rtt(packet):
                inp3.OnL3Rtt(fromNeighbour, packet)        # → engine.OnL3Rtt(...) (times/reflects)
                return                                      # consumed — never to circuits/forwarding
            # not L3RTT → fall through to the existing L4 path WITH the packet we
            # already parsed (don't double-parse).
            DispatchL4(packet, session)                     # the existing circuits-vs-forward switch
            return

        # info didn't parse as a NetRomPacket and wasn't a RIF → drop (today's behaviour).
        return

    # ── INP3 off: EXACTLY today's body, unchanged. ─────────────────────────
    if circuits is null: return
    if NetRomPacket.TryParse(info.Span, out packet):
        DispatchL4(packet, session)
```

`DispatchL4(packet, session)` is the existing dest-==-us → `circuits.OnPacket` / else `ForwardEnabled` → `ForwardDatagram` switch, refactored out of the current `OnInterlinkData` body verbatim (a pure extract-method; no behaviour change).

**Precedence rationale (each is load-bearing):**

1. **RIF before L3RTT before L4.** RIF is checked first because its `0xFF` signature is a single-byte, total, unambiguous discriminator and a RIF is *not* a valid L4 datagram - peeling it first means `NetRomPacket.TryParse` is never even attempted on RIF bytes. L3RTT is checked before the generic L4 dispatch because an L3RTT *is* a valid `NetRomPacket` and would otherwise be handed to forwarding (dest `L3RTT-0` ≠ us ⇒ `ForwardDatagram` would try to relay a probe - wrong) or, worse, dropped silently as an unroutable dest.
2. **A consumed INP3 frame `return`s - never falls through.** A 0xFF-led frame that fails `Inp3Rif.TryParse` (malformed/truncated RIF) is still a RIF attempt; it is dropped, **not** retried as an L4 packet. This is the "never mis-fed" guarantee made total.
3. **The L4 path parses the packet once.** When INP3 is on and the frame is a normal L4 datagram, we reuse the `packet` from the `NetRomPacket.TryParse` in branch (B) - `DispatchL4(packet, …)` takes the already-parsed packet, so the on-path cost of INP3-enabled is one extra single-byte compare (the 0xFF check) plus one `IsL3Rtt` dest/opcode compare. No double-parse.
4. **INP3 off ⇒ the original body runs verbatim.** The `inp3 is null` branch is the *current* `OnInterlinkData` line-for-line (guarded by `circuits is null`, `NetRomPacket.TryParse`, the dispatch switch). Zero behavioural delta when disabled - the default-off guarantee at this seam.

**`Inp3Host.IngestRif` / `Inp3Host.OnL3Rtt`** are thin host methods that supply the table/engine the context they need (host-free types take it as parameters, per I-3):

```csharp
// Inp3Host:
void IngestRif(Callsign from, Inp3Rif rif) =>
    owner.table.IngestRif(from, owner.nodeCall, engine.SnttMs(from) ?? Inp3Sntt.Unset, rif, options.HopLimit);

void OnL3Rtt(Callsign from, NetRomPacket packet) =>
    engine.OnL3Rtt(from, packet);   // engine reflects a peer probe via SendL3Rtt, or times our reflection
```

`engine.SnttMs(from)` is `null` when the link is un-probed; passing `Inp3Sntt.Unset` makes `IngestRif` *learn nothing and withdraw nothing* on an un-measured link (its documented skip - an un-probed link must not remove a route it never taught). This is correct: a RIF heard before the L3RTT handshake completes is parsed but contributes no time-route until the link cost is measured.

**Neighbour observation.** The first 0xCF datagram on a session already adds the neighbour to `interlinks` (existing `OnSessionAccepted` tap). The INP3 host must also `engine.ObserveNeighbour(from)` so the engine starts probing it. Locked seam: call `inp3?.ObserveNeighbour(fromNeighbour)` at the **top** of the INP3 branch in `OnInterlinkData` (idempotent refresh; cheap), so any neighbour we ever hear *anything* 0xCF from becomes a probe target. (Optimistic probing of unknown-capability neighbours is `ProbeUnknownCapability`, default true - so even a neighbour that only ever sent us an L4 datagram gets probed, and we learn its capability from its reflection or its own probe.)

---

## 4. OUTBOUND

Three outbound flows, all reusing the **existing interlink-send path** (`TrySendOverInterlink` → `link.Listener.SendData(link.Session, bytes, PidNetRom)`), so INP3 frames ride the exact same connected-mode 0xCF I-frame seam as L4 datagrams.

### 4.1 L3RTT send: `engine.SendL3Rtt` sink

Wired in the `Inp3Host` ctor:

```csharp
engine.SendL3Rtt = (neighbour, frame) =>
    owner.TrySendOverInterlinkBytes(neighbour, frame.Packet.ToBytes());   // 0xCF I-frame
```

The engine raises `SendL3Rtt` both to **send our probe** (on `Tick`) and to **reflect a peer's probe verbatim** (on `OnL3Rtt`) - the host treats both identically: ship `frame.Packet.ToBytes()` over `neighbour`'s interlink. `frame.Packet` is a `NetRomPacket` to `L3RTT-0`; `ToBytes()` is the 20-byte L3+L4 header + capability text - exactly an L4-shaped info field, sent with `PidNetRom` (0xCF).

**Cold-interlink policy (LOCKED): drop, don't dial.** If there is **no** interlink up to `neighbour` when a probe is due, the send is **dropped** (logged at Debug), **not** dialled. Rationale: L3RTT is a *liveness/metric* probe over an *already-established* interlink; dialling a fresh AX.25 connection just to probe would (a) invert the dependency (we probe links we have, we don't create links to probe) and (b) risk a probe-storm of SABMs to neighbours we have no traffic for. A neighbour with no interlink simply isn't probed until traffic (L4 or a heard RIF/probe) brings the link up. `TrySendOverInterlinkBytes` returns false → the engine's `AwaitingReflection` flag was already set, so the *next* `Tick` after the reset window will tear that neighbour's INP3 state down via the silent-drop path (never-reflected). Acceptable: an un-probeable neighbour contributes no SNTT and no time-route, which is correct.

`TrySendOverInterlinkBytes(neighbour, bytes)` is a tiny new sibling of `TrySendOverInterlink` (which takes a `NetRomPacket`); it takes raw bytes because a RIF is an `Inp3Rif` (not a `NetRomPacket`) and an L3RTT frame we already have as bytes. Both funnel to `link.Listener.SendData(link.Session, bytes, PidNetRom)`.

### 4.2 RIF advertise: `scheduler.Advertise` sink → `table.BuildRif` → interlink

Wired in the `Inp3Host` ctor:

```csharp
scheduler.Advertise = intent =>
{
    var rif = owner.table.BuildRif(owner.nodeCall, intent.Neighbour);   // 2-arg shipped form
    owner.TrySendOverInterlinkBytes(intent.Neighbour, BuildRifInfo(rif));   // 0xFF-led I-frame
    pendingRoundNeighbours.Add(intent.Neighbour);                       // for withdrawn-set clear (§6)
};
```

`BuildRifInfo(rif)` is `rif.ToBytes()` - the RIF's wire form *is* the I-frame info field (first byte `0xFF`). It is sent with `PidNetRom` (0xCF) over the connected interlink, exactly as the inbound-dispatch §3.1 expects to receive it.

Each intent is one neighbour; the scheduler fans out one intent per target neighbour per round (it calls `Advertise` once per `targetNeighbours` entry on a due `Tick`). The host rebuilds the **full poison-reversed RIF per neighbour** (the I-4 "full RIF" lock) - `BuildRif` poisons every destination whose kept next-hop set includes `intent.Neighbour`.

**Cold-interlink policy: same as L3RTT - drop, don't dial.** A periodic/triggered RIF to a neighbour with no interlink is dropped (the neighbour isn't in `interlinks`). We advertise to links we have. (A neighbour we'd *want* to advertise to but have no link to is one we have no traffic for; it learns our routes when an interlink comes up and the next periodic RIF fires.)

### 4.3 Neighbour-down: `engine.NeighbourDown` → `table.MarkNeighbourDown`

Wired in the `Inp3Host` ctor:

```csharp
engine.NeighbourDown += (_, e) =>
{
    int dropped = owner.table.MarkNeighbourDown(e.Neighbour);   // drops every route via e.Neighbour
    owner.LogInp3NeighbourDown(e.Neighbour.ToString(), e.SilentForMs, dropped);
    // The withdrawn destinations enter the table's recently-withdrawn set (§6); the
    // scheduler is escalated so the loss propagates immediately:
    scheduler.MarkWithdrawn(/* each dest that lost its last INP3 route */ ...);   // see §6.3
    owner.RefreshCapableNeighbours();   // e.Neighbour is gone → drop it from the fan-out set
};
```

The engine has **already removed** that neighbour's INP3 state before raising the event and raises it **outside its lock**, so this handler can re-enter the engine (e.g. `RefreshCapableNeighbours` reads `engine.Neighbours`) without deadlock. `MarkNeighbourDown` is the shipped link-down failover primitive - the **same** one the L4 `EnsureInterlinkAsync` dial-failure path calls. INP3 reuses it rather than inventing a teardown, exactly as plan §7 says ("INP3 reuses it rather than inventing its own teardown").

**Two `MarkNeighbourDown` triggers coexist:** (1) the existing L4 dial-failure (`EnsureInterlinkAsync` catch), and (2) the new INP3 180 s reflection-timeout. Both drop the neighbour's routes; both should also feed the withdrawn-set + scheduler escalation when INP3 is on (§6.4). The L4 path calls `table.MarkNeighbourDown` directly today; with INP3 on it must route through a shared helper that also handles the withdrawn-set/scheduler side (§6.4).

---

## 5. TICK - TimeProvider-driven, dedicated INP3 timers

Both host-free engines are tickable and accept a `tickInterval` for self-driving off the injected `TimeProvider`. **Locked: dedicated INP3 timers inside `Inp3Host`, not the NODES `sweepTimer`.**

- `engine` constructed with `tickInterval = options.L3RttInterval` is **wrong granularity for the reset check** - the engine's `Tick` does *both* probe-cadence *and* the reset-window check, so it must tick often enough to fire the reset promptly. **Locked: tick the engine at a sub-cadence** `min(L3RttInterval, L3RttResetWindow) / N` - concretely a fixed **`Inp3Host.TickInterval = TimeSpan.FromSeconds(1)`** (the same 1 s cadence `CircuitManager` uses), passed as `tickInterval` to **both** the engine and the scheduler. At 1 s: the 60 s probe cadence and 180 s reset are checked with ≤1 s slack; the 5 s positive-debounce resolves within 1 s of its deadline. This matches the scheduler's own XML guidance ("choose a value ≤ PositiveDebounce in production").
- The scheduler likewise self-drives at 1 s; its `Tick` evaluates negative-immediate / positive-debounced / periodic in precedence.

```csharp
// Inp3Host ctor:
engine = new Inp3Engine(owner.nodeCall, options, time, tickInterval: TickInterval);   // 1 s
scheduler = new Inp3UpdateScheduler(options, time, tickInterval: TickInterval);        // 1 s
```

**Change-marking drives off ingest, not the timer.** The scheduler's `MarkDirty`/`MarkWithdrawn` are called from the **ingest** path (`IngestRif` outcomes) and the **neighbour-down** path, not from a timer - the timer only *drains* what was marked. The marking seam is §6.3.

**Capable-neighbour refresh** (`scheduler.SetTargetNeighbours`) is driven on a coarser cadence - locked to the engine's tick via a piggybacked refresh: the `Inp3Host` runs a tiny `RefreshCapableNeighbours()` that reads `engine.Neighbours.Where(n => n.Inp3Capable).Select(n => n.Neighbour)` and calls `scheduler.SetTargetNeighbours(...)`. Call it (a) after each `engine` tick is moot (engine self-ticks) so instead call it from the points where the capable set actually changes: on `OnL3Rtt` (a peer just advertised `$N` → newly capable), on `NeighbourDown` (a neighbour left), and once per scheduler-tick as a cheap reconcile. **Locked: reconcile once per scheduler tick** (1 s) - `SetTargetNeighbours` takes a defensive distinct+ordered copy, so calling it every second with the current capable set is cheap and keeps the fan-out set correct without event plumbing. Simplicity over micro-optimisation; 1 s reconcile of a handful of neighbours is free.

Why dedicated timers rather than folding into `OnInterval`: `OnInterval` is the **3600 s** NODESINTERVAL sweep. Driving 1 s INP3 ticks from it is impossible; adding a 1 s branch to it would mean re-arming `sweepTimer` at 1 s and gating the 3600 s sweep behind a counter - strictly worse than two `TimeProvider.CreateTimer` instances the host-free engines already create for themselves. The `TimeProvider` is the same injected instance, so fake-clock determinism holds: in tests, construct `Inp3Host` with a `FakeTimeProvider`, advance it, the engine/scheduler self-tick. (Tests can also pass `tickInterval: null` and call `engine.Tick()`/`scheduler.Tick()` manually - the deterministic path the host-free unit tests already use.)

---

## 6. RECENTLY-WITHDRAWN (invariant W) - NEW table state + consume/clear API

This is the one genuinely-new piece of library code this slice adds. AMBIGUITY-I4-5 **locked** the set to the table; the I-4 build PR did **not** implement it. We implement it now, with the host-clears-after-fan-out refinement the task specifies.

### 6.1 The invariant being satisfied

**(W):** when a destination D loses its **last** INP3 route (withdrawn at horizon in `IngestRif`, dropped by `MarkNeighbourDown`, or aged out by `Sweep`), the **next RIF to each neighbour** must carry **one explicit horizon RIP for D** (`TargetTimeMs = Inp3Rip.HorizonMs`) so the peer withdraws immediately rather than waiting for its obsolescence sweep; and thereafter D is **absent** from every RIF and **never** re-advertised at a finite metric **until a fresh `IngestRif` re-learns** a time-route to D.

The subtlety the task calls out: the withdrawn RIP must reach **every** neighbour's RIF for the round, so the set must **survive being read by N neighbours' `BuildRif` calls** and be **cleared only once the whole fan-out is done** - not consumed-and-cleared on the first `BuildRif`. That is the correction to the I-4 "BuildRif consumes-and-clears it" wording (which, taken literally, would emit the withdrawal to only the *first* neighbour in the round).

### 6.2 The table API (LOCKED)

Add to `NetRomRoutingTable` a recently-withdrawn set of destination callsigns, populated wherever an INP3 route fully leaves, **read** (not auto-cleared) by `BuildRif`, and **explicitly cleared** by the host after a full fan-out round:

```csharp
// new field, under the existing gate-lock:
private readonly HashSet<Callsign> recentlyWithdrawn = new();

/// <summary>Snapshot the recently-withdrawn destinations (does NOT clear). BuildRif
/// also emits these as horizon RIPs; the host clears the set once a full fan-out
/// round (one BuildRif per neighbour) is built — so every neighbour's RIF carries
/// the withdrawal exactly once. Stable ordering for deterministic RIFs.</summary>
public IReadOnlyList<Callsign> RecentlyWithdrawn()
{
    lock (gate)
        return recentlyWithdrawn.OrderBy(c => c.ToString(), StringComparer.Ordinal).ToList();
}

/// <summary>Clear the recently-withdrawn set. Called by the host AFTER a full fan-out
/// round (all neighbours' RIFs built) so the one-shot horizon RIP reached every
/// neighbour. A no-op if empty. Re-entry of a destination on a later withdrawal
/// re-populates it (the next round re-advertises the new withdrawal).</summary>
public void ClearRecentlyWithdrawn()
{
    lock (gate) recentlyWithdrawn.Clear();
}
```

**`BuildRif` change (the only edit to the shipped emitter):** after building the own-node RIP + the destination RIPs, append **one horizon RIP per `recentlyWithdrawn` entry that is not already in the RIF** (a destination that was withdrawn-then-relearned-via-another-neighbour in the same round is carried by its finite RIP, not a horizon RIP):

```csharp
// at the end of BuildRif, before constructing the Inp3Rif:
foreach (var wd in recentlyWithdrawn.OrderBy(c => c.ToString(), StringComparer.Ordinal))
{
    if (wd.Equals(myCall)) continue;                       // never withdraw ourselves (Source invariant)
    if (emittedDestinations.Contains(wd)) continue;        // a re-learned dest is carried finite, not poisoned
    rips.Add(new Inp3Rip {
        Destination = wd, HopCount = 0,
        TargetTimeMs = Inp3Rip.HorizonMs, Tlvs = [],       // explicit one-shot withdrawal
    });
}
```

`BuildRif` **reads** `recentlyWithdrawn` under the same `gate` lock it already holds; it does **not** clear it (the host does, after the round). This is the deviation from I-4's "consumes-and-clears" - locked here as **"BuildRif reads; host clears after the round"** so every neighbour gets the withdrawal RIP.

### 6.3 Where the set is populated + the scheduler escalation

`recentlyWithdrawn.Add(dest)` is called inside the table, under the lock, at the three points where an INP3 route fully leaves - i.e. where, *after* the mutation, the destination has **no remaining route carrying an `Inp3` metric**:

| Site | Today | Add |
|---|---|---|
| `WithdrawInp3(dest, via)` | clears the `Inp3` metric / removes the route | if no other kept route to `dest` has a non-null `Inp3` ⇒ `recentlyWithdrawn.Add(dest)` |
| `MarkNeighbourDown(neighbour)` | removes every route via `neighbour` | for each `dest` that *had* an `Inp3`-bearing route via `neighbour` and now has none ⇒ `recentlyWithdrawn.Add(dest)` |
| `Sweep()` | purges obsolescence-0 routes | for each `dest` whose last `Inp3`-bearing route was purged ⇒ `recentlyWithdrawn.Add(dest)` |

**"Lost its last INP3 route" is the trigger, not "lost its last route."** A destination that drops its time-route but keeps a quality route is **still withdrawn from the INP3 space** (the peer must stop time-routing to it) even though it survives in the table for NODES. So the predicate is "no kept route to D has a non-null `Inp3`," evaluated after the mutation.

**Scheduler escalation is the host's job, not the table's** (the table is host-free - it cannot reach the scheduler). The table populating `recentlyWithdrawn` makes the *content* correct; the *timing* (an immediate NEGATIVE fan-out) is escalated by the host calling `scheduler.MarkWithdrawn(dest)`. The host learns which destinations to escalate from the **return surface of the ingest/down/sweep calls**. The shipped `IngestRif` returns `void` and `MarkNeighbourDown` returns an `int` count - neither tells the host *which* destinations were withdrawn. **Locked seam:** the host reads `table.RecentlyWithdrawn()` immediately after each mutating call and calls `scheduler.MarkWithdrawn(d)` for each (and `scheduler.MarkDirty(d, …)` for changed-but-not-withdrawn - see §6.5 on the positive/worsen marking gap). Concretely:

```csharp
// Inp3Host.IngestRif, after owner.table.IngestRif(...):
foreach (var d in owner.table.RecentlyWithdrawn())
    scheduler.MarkWithdrawn(d);
// (positive/improved marks: see §6.5 — flagged gap)
```

This is safe because `RecentlyWithdrawn()` is cumulative until the host clears it: reading it after each ingest yields the supersets withdrawn so far this round; `MarkWithdrawn` is idempotent (a dest already NEGATIVE stays NEGATIVE). The host clears the set only after the fan-out round (§6.4), so the scheduler-escalation reads and the BuildRif reads see the same content.

### 6.4 The full round: build-all-then-clear

The scheduler fans out **one `Advertise` intent per neighbour per due round**. The host must clear `recentlyWithdrawn` **after the last neighbour's RIF is built**, not after the first. Since the scheduler emits intents one-by-one (a loop over `targetNeighbours` inside one `Tick`, all under no lock by the time `Advertise` is invoked), the host tracks the round and clears at its end. **Locked mechanism:** the host accumulates the neighbours advertised-to in the current drain and clears once the scheduler's drain for this tick completes. The clean seam is a **post-drain hook**: the scheduler invokes all its intents synchronously within `Tick()`; the host wraps a round marker around the drain.

Because the scheduler does not expose a "round complete" callback, the host uses the simplest correct rule: **clear `recentlyWithdrawn` once per scheduler tick, after the engine/scheduler `Tick` returns**, in the `Inp3Host`'s own 1 s reconcile step:

```csharp
// Inp3Host, driven by the scheduler's tick boundary — locked: clear AFTER the tick’s
// intents have all been invoked. The scheduler invokes Advertise synchronously within
// Tick(), so by the time Tick() returns, every neighbour’s BuildRif for this round has
// run and read the withdrawn set. Clear it then.
void OnSchedulerTickCompleted()
{
    owner.table.ClearRecentlyWithdrawn();
}
```

**Implementation note (flagged):** the scheduler self-drives via its internal `tickInterval` timer, so "after `Tick()` returns" is not a host-visible boundary today. Two clean options:

- **(a) Host-driven ticks (LOCKED for this slice).** Pass `tickInterval: null` to the scheduler and have `Inp3Host` own a single 1 s `TimeProvider` timer whose callback does: `scheduler.Tick(); owner.table.ClearRecentlyWithdrawn();`. This gives the host the exact post-drain boundary and keeps **one** INP3 timer (the engine still self-ticks, or is folded into the same callback: `engine.Tick(); scheduler.Tick(); ClearRecentlyWithdrawn(); RefreshCapableNeighbours();`). Locked: **`Inp3Host` owns one 1 s timer that drives `engine.Tick()` then `scheduler.Tick()` then `ClearRecentlyWithdrawn()` then `RefreshCapableNeighbours()`**, in that order. This makes the round boundary explicit and deterministic, and is the cleanest fake-clock test surface (advance 1 s → all four run in order).
- (b) A scheduler "round-complete" callback - rejected: more surface on the host-free type for no gain over (a).

So the §5 "dedicated timers" decision is refined to **one** `Inp3Host`-owned 1 s timer driving the ordered sequence, with engine/scheduler constructed with `tickInterval: null`.

### 6.5 FLAGGED GAP - positive/worsen change-marking is not wired this slice

The scheduler distinguishes **NEGATIVE** (withdrawal / worsen ≥ threshold) from **POSITIVE** (new / improved / sub-threshold worsen). This slice wires the **withdrawal → NEGATIVE** path fully (via `RecentlyWithdrawn()` → `MarkWithdrawn`). It does **not** wire the **positive / worsen-classification** path, because the shipped `IngestRif` returns `void` - it does not report *which* destinations were newly-learned / improved / worsened, nor by how much, so the host cannot classify them for `MarkDirty(dest, Positive|Negative)`.

**Consequence:** with this slice, **new/improved INP3 routes propagate on the periodic RIF (`RifInterval`, default 300 s), not via triggered positive fan-out**; **withdrawals propagate immediately** (the NEGATIVE path, which is the correctness-critical one - a stale route is worse than a slow-to-appear one). The periodic full RIF still carries every current finite route, so the awareness space *converges*; it just isn't *triggered* on positives.

**Named follow-up (not this slice):** give `IngestRif` (and `Sweep`/`MarkNeighbourDown` for worsen) a change-report return (e.g. an `Inp3IngestResult` listing per-destination `{New|Improved|Worsened(byMs)|Withdrawn}`) so the host can `MarkDirty` with the correct class and the WorsenThresholdMs is applied. This is a library-surface change on `NetRomRoutingTable`, cleanly separable, and belongs with the I-4 cascade (it must be mirrored in TS/Rust anyway). Flagged here per "don't silently narrow scope": the *triggered-positive* half of I-4's scheduler is present in the library but **not yet fed** by the host; the periodic baseline covers correctness meanwhile.

---

## 7. THE DEFAULT-OFF GUARANTEE - structure + tests

### 7.1 How it's structured (one gate, structural)

- **One field, one construction site.** `inp3` is `null` unless `config.Enabled && config.NetRom.Inp3.Enabled`. When null: no `Inp3Engine`, no `Inp3UpdateScheduler`, no INP3 timer, no `SendL3Rtt`/`Advertise`/`NeighbourDown` wiring exist. Nothing to drive.
- **Every seam is `inp3?.`.** The three new touch-points in `NetRomService` are:
  1. `OnInterlinkData` - the INP3 branch is `if (inp3 is not null) { … return-on-consume … } else { /* verbatim today */ }`. When null, the **original body runs unchanged** (§3.2).
  2. The shared neighbour-down helper - when `inp3` is null, it is exactly today's `table.MarkNeighbourDown(neighbour)` (the L4 dial-failure path); the withdrawn-set/scheduler escalation is `inp3?.OnNeighbourDown(...)`.
  3. Construction + dispose - `inp3?.Dispose()` in both `Dispose` and `DisposeAsync`.
- **The table's new state is inert when unfed.** `recentlyWithdrawn` is only ever `Add`-ed inside the INP3 ingest/down/sweep mutations - but those withdrawn-set adds run **only when INP3 routes exist**, and INP3 routes are only created by `IngestRif`, which is only ever called by `inp3.IngestRif`. With INP3 off, no `IngestRif` is ever called ⇒ no route ever has an `Inp3` metric ⇒ the withdrawn-set adds in `WithdrawInp3`/`MarkNeighbourDown`/`Sweep` never fire (their predicate "had an `Inp3`-bearing route" is never true) ⇒ `recentlyWithdrawn` stays empty ⇒ `BuildRif` (never called when off) is moot. The table is byte-for-byte today when INP3 is off, even though it gained the field. **Verify this is true:** the withdrawn-set `Add` in `MarkNeighbourDown`/`Sweep` must be **guarded on "the removed route carried an `Inp3` metric,"** not on "a route was removed" - otherwise a vanilla `MarkNeighbourDown` (the L4 dial-failure path, which runs with INP3 off) would populate `recentlyWithdrawn` for quality-only routes. That guard is the load-bearing detail of the default-off guarantee at the table layer (§6.3 predicate).
- **Config default.** Absent `inp3:` block ⇒ `Inp3.Enabled == false` ⇒ no host. Existing YAML, the `NodeConfigTemplate`, and every existing test parse + validate unchanged.

### 7.2 How it's tested

- **Byte-for-byte-today (the headline).** A `NetRomService` test with `Inp3.Enabled == false` driving the full L3/L4 surface (hear NODES, originate, open interlink, forward transit, connect-route, L4 datagram over interlink) asserts identical behaviour to a baseline run: no L3RTT frames emitted, no RIF frames emitted, `OnInterlinkData` of a normal L4 datagram reaches circuits/forwarding exactly as today. Concretely: a `SendData` spy on the interlink listener sees **zero** 0xCF frames whose info dest is `L3RTT` or whose first info byte is `0xFF`.
- **The disabled `OnInterlinkData` path is the original.** A test feeds a RIF-shaped (`0xFF`-led) info and an L3RTT-shaped `NetRomPacket` to a **disabled** service and asserts they go down the *vanilla* path (the RIF-shaped bytes fail `NetRomPacket.TryParse` → dropped as today; the L3RTT packet, dest `L3RTT-0` ≠ us, hits `ForwardEnabled` → forward-or-drop exactly as a normal unroutable datagram would today). I.e. **disabled INP3 does not even recognise** these frames - they get today's generic treatment.
- **Table default-off invariant.** A unit test on `NetRomRoutingTable`: with **no** `IngestRif` ever called, drive `MarkNeighbourDown` + `Sweep` over quality-only routes and assert `RecentlyWithdrawn()` stays empty and `BuildRif` (if called) emits only the own-node RIP. Proves the withdrawn-set guard (§7.1) holds.
- **Enabled-path behavioural tests** (the positive coverage): probe→reflect→SNTT converges; a RIF ingests a time-route; a 180 s silence raises `NeighbourDown` → routes dropped + withdrawn-set populated + horizon RIP in the next RIF to **every** neighbour (the round-clear correctness); inbound dispatch peels RIF + L3RTT before L4 (an L3RTT to a peer is reflected, never forwarded; a RIF is ingested, never sent to circuits). All on a `FakeTimeProvider` via the host-driven 1 s tick (§6.4).
- **Config tests:** an `inp3:` block round-trips through `NodeConfigYaml`; an out-of-range nested value (e.g. `l3RttResetWindow < l3RttInterval`, or `positiveDebounce >= rifInterval`) fails `NodeConfigValidator` with the record's own message; `inp3.enabled: true` with `netrom.enabled: false` fails the cross-field rule; absent `inp3:` validates with `Enabled == false`.

---

## 8. NAMED FOLLOW-UPS (out of this slice, per "don't silently narrow scope")

1. **Forwarding-by-time (the big one).** Feed `Inp3RouteSelector.SelectActiveRoute(dest, options.PreferInp3Routes)` into the next-hop pick in `ForwardDatagram` (`NetRomForwarding.Decide` currently keys on quality only), `SendNetRomPacket`, and `ConnectCircuitAsync`. **This is what makes `preferInp3Routes` live.** Until then `preferInp3Routes` is config-plumbed + validated but inert. This slice is AWARENESS ONLY: probe/ingest/advertise/reset. Routing-by-time is the follow-up that turns awareness into behaviour.
2. **Triggered-positive change-marking (§6.5).** Give `IngestRif`/`Sweep`/`MarkNeighbourDown` a per-destination change-report so the host can `MarkDirty(dest, Positive|Negative)` with `WorsenThresholdMs` applied - wiring the *triggered-positive* half of the I-4 scheduler. Until then positives ride the periodic RIF (300 s); withdrawals (the correctness-critical NEGATIVE path) are immediate.
3. **Surfacing.** Expose `Inp3Engine.Neighbours` (SNTT, capable, IP-accept) + the INP3 metric column of the routing snapshot via the console / `Nodes` view / MCP. Read-only; no behavioural risk; out of scope here.
4. **Docs drift fix (docs-only).** Update `netrom-inp3-i4-design.md` §6.1 + the `Inp3UpdateScheduler` XML comments from the stale 3-arg `BuildRif(myCall, N, preferInp3Routes)` to the shipped 2-arg form; update plan §8's YAML from `…Seconds:int` to the `TimeSpan` keys + the `NodeConfigTemplate` sample.
5. **Alias TLV on the wire (AMBIGUITY-I4-1), alternate-reverse (AMBIGUITY-I4-2), partial/delta RIFs** - all already deferred by I-4 to I-5; unchanged.
6. **Cold-interlink probing policy** - this slice locks "drop, don't dial" (§4.1). If fleet testing shows we want to *establish* interlinks specifically to probe/advertise INP3 to known-capable neighbours we have no traffic for, that is a future policy knob - not this slice.

---

## 9. Returned summary (the four locked answers)

**OnInterlinkData dispatch order (§3.2):** `inp3 is not null` ⇒ (A) **RIF first** - if `info[0] == 0xFF`: `Inp3Rif.TryParse` → `table.IngestRif`; **`return` regardless** (a 0xFF-led frame is never an L4 datagram). (B) **L3RTT next** - `NetRomPacket.TryParse`; if `Inp3L3RttFrame.IsL3Rtt(packet)`: `engine.OnL3Rtt(from, packet)` + `return`; else fall through to the existing L4 dispatch **with the already-parsed packet** (no double-parse). `inp3 is null` ⇒ the current body verbatim. Precedence is a correctness property: a RIF/L3RTT is peeled off before the L4 parse so it can never reach circuits/forwarding; INP3-off is byte-for-byte today.

**Config shape (§2):** `NetRomConfig.Inp3 : NetRomInp3Options` (`new()` default ⇒ `Enabled == false`), bound directly (not nullable-overlay) via the existing camel-case YAML deserializer - a nested `inp3:` block (`enabled` / `preferInp3Routes` / `snttGainShift` / `probeUnknownCapability` / `advertiseIpAccept` / `capabilityTextWidth` / `hopLimit` / `worsenThresholdMs` / and the `TimeSpan` keys `l3RttInterval` / `l3RttResetWindow` / `rifInterval` / `positiveDebounce`). `NetRomValidator` gains a rule delegating to `Inp3.Validate()` (one validation authority) + an `inp3.enabled ⇒ netrom.enabled` cross-field guard. Default-off requires zero config.

**Recently-withdrawn API (§6):** new `HashSet<Callsign> recentlyWithdrawn` in `NetRomRoutingTable`, populated under-lock in `WithdrawInp3` / `MarkNeighbourDown` / `Sweep` **only when a destination loses its last `Inp3`-bearing route** (the guard that keeps it inert with INP3 off). New public `IReadOnlyList<Callsign> RecentlyWithdrawn()` (read, no clear) + `void ClearRecentlyWithdrawn()`. `BuildRif` **reads** the set and appends one `HorizonMs` RIP per entry not already emitted finite (never for `myCall`). The host (`Inp3Host`, one 1 s `TimeProvider` timer) drives `engine.Tick(); scheduler.Tick(); table.ClearRecentlyWithdrawn(); RefreshCapableNeighbours()` **in that order** - so every neighbour's `BuildRif` in a round reads the same withdrawn set, then it's cleared once after the round (the correction to I-4's "consumes-and-clears," which would have reached only the first neighbour). The host also reads `RecentlyWithdrawn()` after each `IngestRif`/down/sweep to `scheduler.MarkWithdrawn(d)` (NEGATIVE → immediate fan-out).

**Default-off structure (§7):** one nullable `Inp3Host? inp3` field, constructed only under `config.Enabled && Inp3.Enabled`; every seam is `inp3?.`; the `OnInterlinkData` `else` branch is the current body line-for-line; the table's `recentlyWithdrawn` adds are guarded on "removed route carried an `Inp3` metric," so a vanilla `MarkNeighbourDown`/`Sweep` (INP3 off) never touches it. Tested by a byte-for-byte-today behavioural test (zero L3RTT/RIF frames on the wire; L4 dispatch unchanged), a disabled-path test (RIF/L3RTT-shaped bytes get today's generic treatment), and a table invariant test (no `IngestRif` ⇒ `RecentlyWithdrawn()` stays empty).
