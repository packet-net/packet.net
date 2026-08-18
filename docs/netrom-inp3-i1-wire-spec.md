# INP3 slice I-1 - wire-format spec (LOCKED)

*Implementation-ready byte-layout spec for the INP3 wire codecs (`Inp3L3RttFrame`, `Inp3Rif`, `Inp3Rip`, `Inp3Tlv`) - the first INP3 slice (`docs/netrom-inp3-plan.md` §9 I-1). This locks the formats so the C# reference, `@packet-net/ax25`, and pico-node can all encode/decode byte-identically. No behaviour is in scope here - only the codecs.*

**Grounding.** Every claim below is traced to `docs/netrom-inp3-plan.md` §4 (Wire formats) / §5 (data model), `netrom-research.md` §1.4 (INP3), and the existing `src/Packet.NetRom/Wire/` types this spec **reuses verbatim**. The INP3 spec itself (Gal DB7KG / NORD><LINK, `wiki.oarc.uk/_media/packet:internodeprotocolnp.pdf`) is the reference; this repo's `netrom-research.md` is the secondary source the layouts are derived from. Genuine ambiguities the sources do not pin down are flagged **AMBIGUITY** inline and must be resolved against a live peer or the PDF before I-2, not invented away.

**Reused types (one source of truth - do not re-implement):**

| Concept | Existing type to reuse | Notes |
|---|---|---|
| 7-octet AX.25-shifted callsign | `NetRomCallsign.TryReadShifted` / `WriteShifted` (→ `Ax25Address`) | `ShiftedLength = 7`. The shift/SSID/EOA codec has one home. |
| L3 network header (15 B) | `NetRomNetworkHeader` (`EncodedLength = 15`) | L3RTT datagram reuses this unchanged. |
| L4 transport header (5 B) | `NetRomTransportHeader` (`EncodedLength = 5`) | L3RTT body reuses this; opcode field carries 0x02. |
| Full L3 datagram | `NetRomPacket` (`HeaderLength = 20`) | L3RTT *is* a `NetRomPacket` with a specific dest + opcode + payload. |
| 0xFF signature / total-parse / lenient-default pattern | `NodesBroadcast` | RIF mirrors its multi-entry total-parse + named-divergence-options shape. |
| Strict/Lenient/Bpq/Xrouter parse presets | `NetRomParseOptions` | INP3 adds an analogous `Inp3ParseOptions` (same preset names). |

---

## 0. The totality contract (applies to every codec below)

Identical to the discipline `NodesBroadcast` / `NetRomPacket` / `NetRomNetworkHeader` already honour and the fuzz harness validates:

- **Every `TryParse` returns `bool` (C#) / `T | null` (TS) / `Option<T>` (Rust) and NEVER throws / panics** on *any* input - empty, truncated, oversized, random, adversarial.
- A too-short span → `false`/`null`/`None`. A field that fails to decode (e.g. a callsign slot with a non-space byte after padding) → `false`/`null`/`None`, not a partial object.
- Delegated decodes that *can* throw (`Ax25Address.Read` throws `ArgumentException` on a malformed slot) are wrapped exactly as `NetRomCallsign.TryReadShifted` already wraps them (`try { … } catch (ArgumentException) { return false; }`). **Never** introduce a new throwing decode path.
- Encoders (`ToBytes`/`Write`) stay strict - they may throw on a too-small destination buffer (as the existing writers do) because that is a caller bug, not wire input. We never *emit* a malformed INP3 frame.
- The parameterless `TryParse` overload uses the **`Lenient`** preset (promiscuous read-only ingest), matching `NodesBroadcast.TryParse(info, out _)`.

This is the non-negotiable fuzz contract. Code review rejects any INP3 codec that can throw on bytes off the wire.

---

## 1. L3RTT - link-time measurement frame

### 1.1 What it is (grounded)

Per plan §4.1 and research §1.4: L3RTT is an **ordinary L3 info datagram** - *not* a new frame family. It is a `NetRomPacket` whose **destination node callsign is the literal `L3RTT-0`**, whose transport **opcode is `0x02`**, and whose **payload is space-padded text** carrying the INP3 capability flags. The neighbour **reflects the frame back unchanged**; the originator times the round trip (RTT ÷ 2 → SNTT). A reflection that does not return within **180 s** resets the interlink (plan §4.1, §6.1 - out of scope for I-1, which is codec-only).

Because it is a `NetRomPacket`, it rides the **connected-mode interlink (PID 0xCF)** exactly like every other L3 datagram, and it reuses `NetRomNetworkHeader` (15 B) + `NetRomTransportHeader` (5 B) **unchanged**. `Inp3L3RttFrame` is therefore a thin **builder + recogniser** over `NetRomPacket`, not a parallel packet type.

### 1.2 Byte layout

```
  NetRomPacket (reused):
    [15] NetRomNetworkHeader   origin = us; destination = LITERAL "L3RTT-0"; TTL = default (25)
    [ 5] NetRomTransportHeader  opcode-&-flags low nibble = 0x02 (see AMBIGUITY-L3RTT-1)
    [ N] payload                space-padded ASCII capability text (see §1.3)
```

- **Destination callsign** = `L3RTT` base + SSID `0`. `L3RTT` is a valid 0-6-char alphanumeric base, so it round-trips through `NetRomCallsign.WriteShifted` / `Ax25Address` with no special-casing. The 7-octet shifted form is constant:
  `L3RTT-0  →  98 66 A4 A8 A8 40 60`
  (EOA/command bits clear, as `WriteShifted` writes; SSID 0 → byte `0x60`).
- **Origin** = the probing node's own callsign (varies per node).
- **TTL** = `NetRomNetworkHeader.DefaultTimeToLive` (25). **AMBIGUITY-L3RTT-2:** the INP3 PDF does not state a specific TTL for L3RTT; it is a single-hop neighbour probe, so any TTL ≥ 1 works. We default to the node's normal initial TTL for one source of truth. Flag-gate if a live peer rejects it.
- **Opcode `0x02`.** This is the same numeric value as `NetRomOpcode.ConnectAcknowledge` (0x02), but L3RTT is disambiguated by its **destination = `L3RTT-0`**, not by opcode alone. The recogniser keys on the destination callsign first (see §1.4).

### 1.3 Capability text payload (`$N` / `$IX`)

Per plan §4.1 and research §1.4, the text payload is **space-padded** and carries capability flags:

- **`$N`** - "I speak INP3." Two ASCII bytes `24 4E`. Presence of `$N` anywhere in the (trimmed) payload text is how a node advertises INP3 capability; its absence means fall back to vanilla NODES.
- **`$IX`** - "I accept IP version *X*." Three ASCII bytes `24 49 <digit>`, e.g. `$I4` = `24 49 34` for IPv4. Optional; appears in addition to `$N`.
- The remainder of the field is **ASCII space (`0x20`) padding** to a fixed text width.

**Encoding rule (locked):** emit `$N`, then any `$IX`, then right-pad with spaces. The recogniser scans the trimmed payload for the `$`-prefixed tokens; **unknown `$`-tokens are ignored** (forward-compat, same spirit as unknown TLVs). The parse is a substring/token scan, never an exception.

**AMBIGUITY-L3RTT-3 (flagged, do NOT invent):** the INP3 PDF does not fix the **exact width** of the padded text field, nor whether the `$N`/`$IX` tokens are space-separated or concatenated. This spec's vectors use a contiguous `$N$I4` with trailing-space pad to a width of 8, but the **codec must not depend on a fixed width** - it parses whatever text is present and recognises tokens by their `$`-prefix. The emitted width is an `Inp3Options` knob (default 8) to be calibrated against a live INP3 peer in I-2; flag-gate any peer that demands a specific width or separator.

### 1.4 How a reflection is recognised

A received `NetRomPacket` is an **L3RTT frame** iff:
1. `Network.Destination` decodes to base `L3RTT` (SSID ignored for the match, but canonically 0), **and**
2. `Transport` opcode low-nibble (`opcode & 0x0F`) == `0x02`.

It is a **reflection of our own probe** (vs. a peer's probe we must reflect) iff additionally `Network.Origin == our own node callsign` - i.e. the frame we sent has come back with origin unchanged. (The timing/180 s logic is I-2; I-1 only provides the boolean recogniser + the capability-token extractor.) Reflection is **byte-for-byte echo**: the reflector retransmits the received bytes unchanged, so the originator sees an identical `NetRomPacket`.

**AMBIGUITY-L3RTT-4 (flagged):** the PDF says the neighbour "reflects the frame unchanged," but does not specify whether the reflector swaps origin/destination or echoes verbatim. This spec locks **verbatim echo** (origin stays the original prober, destination stays `L3RTT-0`) because that is what lets the prober recognise its own frame by `Origin == self`. If a live peer swaps addresses instead, that is a named-flag accommodation for I-2, not a layout change here.

### 1.5 L3RTT worked hex vectors

All use origin `M0LTE-0` (`9A 60 98 A8 8A 40 60`), dest `L3RTT-0` (`98 66 A4 A8 A8 40 60`), TTL `0x19` (25), transport header `00 00 00 00 02` (index/id/seq zero; opcode 0x02, no flags).

**Vector L3RTT-A - probe advertising plain INP3 capability (`$N`):**
```
9A 60 98 A8 8A 40 60  98 66 A4 A8 A8 40 60  19  00 00 00 00 02  24 4E 20 20 20 20 20 20
└──── origin M0LTE-0 ─┘ └──── dest L3RTT-0 ─┘ TTL └─ transport ─┘ └ "$N" + 6 spaces ──┘
```
Length 28. Payload text = `"$N      "`.

**Vector L3RTT-B - probe advertising INP3 + IPv4 (`$N$I4`):**
```
9A 60 98 A8 8A 40 60  98 66 A4 A8 A8 40 60  19  00 00 00 00 02  24 4E 24 49 34 20 20 20
                                                                 └─ "$N$I4" + 3 spaces ┘
```
Length 28. Payload text = `"$N$I4   "`. `inp3_capable = true`, `ip_accept = 4`.

**Vector L3RTT-C - reflection (byte-identical echo of A):**
```
9A 60 98 A8 8A 40 60  98 66 A4 A8 A8 40 60  19  00 00 00 00 02  24 4E 20 20 20 20 20 20
```
Identical bytes to Vector A. Recognised as *our* reflection because `Origin == M0LTE-0 == self`.

---

## 2. RIF / RIP - routing information

### 2.1 What it is (grounded)

Per plan §4.2 and research §1.4:

- A **RIF (Routing Information Frame)** is the **information field of a connected-mode interlink I-frame (PID 0xCF)** whose **first byte is the `0xFF` signature**, followed by **one or more RIPs**. It is the connected-mode analogue of a NODES broadcast (which is also 0xFF-signatured but UI/connectionless to dest `NODES`). I-1 models the **info-field body** (`Inp3Rif`), exactly as `NodesBroadcast` models a UI info field - not the surrounding AX.25 frame.
- A **RIP (Routing Information Packet)** is one routing entry:

```
  [7] callsign        destination, AX.25-shifted (reuse NetRomCallsign.TryReadShifted)
  [1] hop count       hops to the destination
  [2] target time     MSB-first, 10 ms units; 0..65535 = 0..655.35 s; 60000 (0xEA60) = 600 s = horizon = unreachable
  [*] TLV fields      zero or more type/len/value records (§2.3)
  [1] 0x00            EOP (end-of-packet) terminator
```

### 2.2 RIF body layout

```
  [1]  0xFF  signature (gates the whole body; non-0xFF → not a RIF → None)
  then 1..N RIPs, each self-delimited by its 0x00 EOP
```

Parsing walks RIPs left-to-right, each consuming `7 + 1 + 2 + (TLV bytes) + 1` octets, until the body is exhausted. Mirror `NodesBroadcast`'s shape: **total**, lenient-by-default, with an `Inp3ParseOptions` record (presets `Strict` / `Lenient` / `Bpq` / `Xrouter`, same as `NetRomParseOptions`).

**Distinguishing RIF from NODES.** Both start with `0xFF`. They are **never confused** because they arrive on different carriers: NODES rides **UI frames to dest `NODES`**; RIF rides **connected-mode I-frames on the interlink**. The codec layer is told which it's parsing by the caller (the carrier), exactly as today the NODES parser is only ever fed UI-frame info fields. **AMBIGUITY-RIF-1 (flagged):** there is no in-band byte that distinguishes a RIF body from a NODES body once you have only the info field - both lead with `0xFF`. I-1 keeps them as **separate codecs selected by carrier**; do not attempt content-sniffing. If a future need arises to disambiguate from bytes alone, that is a new design decision, not something to invent here.

### 2.3 TLV fields

A `Inp3Tlv` is `{ type: u8, value: bytes }` on the wire as **`[type][len][value…]`** (len = `value.Length`, one byte, 0..255). Per plan §4.2 / §4.3:

- **type `0x00` = alias** - value is the destination's ASCII alias/mnemonic (decoded to a trimmed string; emit the raw bytes verbatim).
- **type `0x01` = IP** - value is an IP address; length 4 = IPv4, length 16 = IPv6 (decode to `IPAddress` / `[u8;4]` / `[u8;16]`).
- **UNKNOWN types** (anything else) are **retained verbatim** in an `unknown: List<Inp3Tlv>` and **re-emitted unchanged on forward**. A RIP is **never dropped** for carrying a TLV we don't understand (forward-compat - plan §4.2/§4.3).

**TLV vs EOP disambiguation (locked).** The EOP is `0x00`. The alias TLV type is also `0x00`. They are distinguished **positionally**: inside the TLV region the parser reads a byte as a **TLV type** and expects a **length byte to follow**; the terminating `0x00` is read as EOP **only when it stands where the next TLV type would start AND the remaining-bytes / framing rules say the RIP ends**. The concrete locked rule:

> Within a RIP, after the fixed `[7][1][2]` prefix, repeatedly: read one `type` byte. **If `type == 0x00` and there is no room for a `len` byte + 0-length value that keeps the RIP within the body, OR the byte is the designated terminator position, treat it as EOP and stop.** Otherwise read `len`, then `len` value bytes, append the TLV, and continue.

**AMBIGUITY-RIF-2 (flagged - this is the single subtlest point):** an alias TLV has `type = 0x00`, identical to the EOP byte. The INP3 PDF's terseness means the **exact rule for telling an alias-TLV `0x00` from the EOP `0x00`** is not crisply pinned. Two readings exist:
  (a) **EOP is always a bare `0x00`**; an alias TLV is `00 <len> <value>` - so a `0x00` followed by a plausible length+value is a TLV, a `0x00` at the natural end is EOP. (This is what the worked vectors below assume and what this spec **locks for I-1**.)
  (b) Alias is carried by a **different sentinel** and `0x00` is unambiguously EOP.
This spec locks reading **(a)** - alias = TLV type `0x00`, EOP = the trailing `0x00` - because plan §4.2 explicitly states "type 0x00 = alias" *and* "0x00 EOP," which only coexist under reading (a). The codec must therefore parse TLVs greedily and treat the **final** `0x00` (the one with no valid `[len][value]` following within the RIP) as EOP. **This is the highest-risk decision in I-1 and MUST be re-validated against a live INP3 peer / the PDF before I-2 relies on alias TLVs on the wire.** Until then, emitting the alias as a TLV is gated behind an `Inp3Options.emitAliasTlv` flag (default off) so our *output* never depends on the ambiguous reading even though our *parser* tolerates it.

### 2.4 The horizon / withdrawal encoding

Target time **`0xEA60` (60000 × 10 ms = 600.000 s)** = the routing horizon = **unreachable**. A RIP at or above the horizon is a **route withdrawal** (plan §5.3). I-1 decodes it faithfully (it is just a `target_time_ms` value); the *act* of withdrawing the route is I-3. The codec exposes a convenience `IsHorizon` (`target_time_ms >= 600_000`) so the routing layer doesn't re-derive the constant.

- Max encodable: `0xFFFF` = 65535 units = 655.35 s (above the horizon - also unreachable).
- Granularity: 10 ms/unit. `target_time_ms = units × 10`; `units = target_time_ms / 10`.

### 2.5 RIP / RIF worked hex vectors

Callsign shifted forms used below:
`GB7RDG-0 = 8E 84 6E A4 88 8E 60` · `GB7RDG-7 = 8E 84 6E A4 88 8E 6E` · `M0LTE-0 = 9A 60 98 A8 8A 40 60` · `GB7XYZ-0 = 8E 84 6E B0 B2 B4 60`

**Vector RIP-1 - alias TLV, hop 2, 450 ms:** `GB7RDG-0`, hop `0x02`, target `00 2D` (45 units = 450 ms), alias TLV `00 03 'RDG'`, EOP `00`.
```
8E 84 6E A4 88 8E 60  02  00 2D  00 03 52 44 47  00
└──── GB7RDG-0 ──────┘ hop tgt-t └ alias 'RDG' ─┘ EOP
```
Length 16. `target_time_ms = 450`, `hop = 2`, alias `"RDG"`.

**Vector RIP-2 - IP TLV, hop 1, 120 ms:** `M0LTE-0`, hop `0x01`, target `00 0C` (12 units = 120 ms), IP TLV `01 04 2C 83 5B 02` (44.131.91.2), EOP `00`.
```
9A 60 98 A8 8A 40 60  01  00 0C  01 04 2C 83 5B 02  00
└──── M0LTE-0 ───────┘ hop tgt-t └ IP 44.131.91.2 ┘ EOP
```
Length 17. `target_time_ms = 120`, `hop = 1`, IP = 44.131.91.2.

**Vector RIP-3 - UNKNOWN TLV (retained verbatim) + alias, hop 4, 2.5 s:** `GB7XYZ-0`, hop `0x04`, target `00 FA` (250 units = 2.5 s), unknown TLV `7F 02 AA BB`, alias TLV `00 03 'XYZ'`, EOP `00`.
```
8E 84 6E B0 B2 B4 60  04  00 FA  7F 02 AA BB  00 03 58 59 5A  00
└──── GB7XYZ-0 ──────┘ hop tgt-t └unk 0x7F ──┘ └ alias 'XYZ' ┘ EOP
```
Length 20. The `7F 02 AA BB` TLV is **unknown** → kept verbatim in `unknown[]` and re-emitted on forward. `target_time_ms = 2500`, alias `"XYZ"`.

**Vector RIP-4 - horizon / withdrawal, no TLV:** `GB7RDG-7`, hop `0xFF`, target `EA 60` (60000 units = 600 s = horizon), EOP `00`.
```
8E 84 6E A4 88 8E 6E  FF  EA 60  00
└──── GB7RDG-7 ──────┘ hop horiz EOP
```
Length 11. `IsHorizon == true` → this RIP **withdraws** the route to GB7RDG-7 (I-3 acts on it).

**Vector RIF-FULL - a complete RIF body carrying RIP-1..RIP-4:**
```
FF
8E 84 6E A4 88 8E 60 02 00 2D 00 03 52 44 47 00          (RIP-1)
9A 60 98 A8 8A 40 60 01 00 0C 01 04 2C 83 5B 02 00       (RIP-2)
8E 84 6E B0 B2 B4 60 04 00 FA 7F 02 AA BB 00 03 58 59 5A 00   (RIP-3)
8E 84 6E A4 88 8E 6E FF EA 60 00                         (RIP-4)
```
Flat: `FF 8E 84 6E A4 88 8E 60 02 00 2D 00 03 52 44 47 00 9A 60 98 A8 8A 40 60 01 00 0C 01 04 2C 83 5B 02 00 8E 84 6E B0 B2 B4 60 04 00 FA 7F 02 AA BB 00 03 58 59 5A 00 8E 84 6E A4 88 8E 6E FF EA 60 00`
Length 65 (1 signature + 16 + 17 + 20 + 11). Parses to a `Inp3Rif` with 4 RIPs.

**Vector RIF-MIN - minimal RIF (signature + one no-TLV RIP):** `M0LTE-0`, hop 1, 1.23 s (123 units = `00 7B`), no TLV, EOP.
```
FF  9A 60 98 A8 8A 40 60  01  00 7B  00
sig └──── M0LTE-0 ───────┘ hop tgt-t EOP
```
Length 12.

### 2.6 Totality / fuzz vectors (must all be rejected, never throw)

- `[]` (empty) → `None`.
- `[0xFF]` (signature only, zero RIPs) → **`Inp3ParseOptions`-gated**: `Strict` rejects (mirror `AllowEmptyDestinationList = false`); `Lenient` accepts an empty RIP list. (Mirrors `NodesBroadcast`'s empty-list handling.)
- `[0x00 …]` (wrong signature) → `None` (mirror `NodesBroadcast` "wrong signature → ignore").
- `FF 8E 84 6E A4 88 8E 60 02 00` (RIP truncated mid-target-time, no EOP) → `None` (or, under `Lenient`-with-partial flag, keep the whole RIPs already parsed and drop the trailing partial - mirror `AllowTrailingPartialEntry`).
- `FF … 00 03 52 44` (alias TLV claims len 3 but only 2 value bytes remain before body end) → the over-long TLV cannot be satisfied → `None`/drop-trailing per the partial flag. **Never** read past the body.
- A callsign slot with a non-space byte after padding → `NetRomCallsign.TryReadShifted` returns false → RIP `None`.

---

## 3. Options surface (mirror `NetRomParseOptions`)

A new `Inp3ParseOptions` record, same preset names and defaults discipline as `NetRomParseOptions`:

- `AllowEmptyRipList` (default `true` in `Lenient`, `false` in `Strict`) - accept a `0xFF`-only RIF body.
- `AllowTrailingPartialRip` (default `true` in `Lenient`, `false` in `Strict`) - keep the whole RIPs parsed, drop a clipped trailing one (RF clip tolerance), mirroring `AllowTrailingPartialEntry`.
- Presets `Strict` / `Lenient` / `Bpq` / `Xrouter` (the last two == `Lenient` today, kept named for future quirk landing - exactly as `NetRomParseOptions.Bpq`/`.Xrouter` are).
- A separate **emit-side** `Inp3Options` (not parse-side) holds `emitAliasTlv` (default **off** until AMBIGUITY-RIF-2 is resolved) and the L3RTT `capabilityTextWidth` (default 8, AMBIGUITY-L3RTT-3). Each gets a `docs/strict-vs-pragmatic-audit.md` row when first used on the wire (I-2+).

---

## 4. Open ambiguities to resolve before I-2 (do NOT silently bake in)

| ID | What's ambiguous | This spec's locked interim choice | Resolution path |
|---|---|---|---|
| **L3RTT-1** | Whether opcode 0x02 sits in a full 5-byte transport header or is a bare opcode byte after the network header | Full 5-byte `NetRomTransportHeader`, opcode-nibble 0x02, other fields zero (reuses existing type) | Validate vs live INP3 peer / PDF; flag-gate if peer uses a bare opcode |
| **L3RTT-2** | TTL value for the single-hop probe | Node default TTL (25) | Cosmetic; any TTL ≥ 1 works |
| **L3RTT-3** | Exact width / separator of the `$N`/`$IX` capability text | Token-scan parse (width-independent); emit width 8, contiguous tokens | Calibrate emit width vs live peer; parser already width-agnostic |
| **L3RTT-4** | Reflection = verbatim echo vs address-swap | Verbatim echo (origin stays prober) | Named flag if a peer swaps addresses |
| **RIF-1** | RIF vs NODES both lead with 0xFF; no in-band discriminator | Disambiguate by **carrier** (RIF = connected I-frame; NODES = UI to `NODES`); separate codecs | Keep carrier-selected; do not content-sniff |
| **RIF-2** | Alias TLV `type 0x00` collides with EOP `0x00` | Reading (a): alias = `00 <len> <val>` TLV; EOP = trailing bare `0x00` with no valid TLV following. **Parser tolerates; emitter gated off (`emitAliasTlv=false`).** | **Highest risk** - re-validate vs live peer / PDF before emitting alias TLVs |

---

## 5. Cross-stack parity requirement

Per plan §11 and the FNV-1a / quality-formula discipline: the three stacks (`Packet.NetRom` C#, `@packet-net/ax25` TS, pico-node Rust) **must produce byte-identical INP3 frames**. Every vector in §1.5 and §2.5 becomes a **shared golden vector** in the cross-stack parity suite (the same way the NODES vectors are shared). The C# reference codec is authoritative; TS and Rust mirror it 1:1 with the C# vectors as oracle. Rust stays `no_std`-clean: fixed-capacity TLV/RIP storage (const-generic arrays, no heap map), integer-only target-time maths, `u64` for any time field (none in I-1's codecs - timing is I-2).
