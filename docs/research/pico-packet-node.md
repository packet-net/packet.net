# A lightweight, v2.2-compliant AX.25 packet node on Pico W-class hardware (RP2040)

*Research / options analysis — 2026-06-02. Exploratory; no code, no repo changes. Grounded against the live `packet-net/ax25sdl` + `packet-net/packet.net` trees and external prior art (links at the end).*

---

## 0. TL;DR / headline recommendation

**It is feasible, and the compelling version of it is real.** A Pico W-class node can run the *exact same* generated AX.25 v2.2 link-layer state machine that the desktop (`packet.net`, C#) and browser (`ax25-ts`, TypeScript) stacks run — because `ax25sdl`'s codegen already emits the state tables as **pure static, heap-free, `no_std`-ready data** in both C and Rust. That gives a genuinely novel claim: *provable link-layer parity across hardware classes from one spec source*. No existing MCU AX.25 stack (OpenTNC, the pico_tnc family, MMDVM-TNC) has that property — they are all hand-written and spec-traceable only by inspection.

**What comes free:** the ~243 v2.2 link-layer transitions + 32 figc4.7 subroutine paths, as tables. On x86-64 they compile to **~70 KB of `.rodata` with zero BSS**; on the RP2040's 32-bit target, expect meaningfully less (pointers halve), and a codegen "strip-metadata + typed-verbs" mode (Tom's already-planned SP-010) takes the load-bearing part to a few KB.

**What must be ported (the real work):** the *runtime around the tables* — currently ~6 k LOC of hand-written C# (`ActionDispatcher`, `Ax25Session`, `GuardEvaluator`, `SubroutineRegistry`, `SdlLoopExecutor`, the `Ax25Frame` codec, `Segmenter`). None of it is exotic, but it is the bulk of the effort and the only place floating-point appears (SRT/T1V timer smoothing).

**Recommended target:** **Rust + Embassy (`no_std`)** consuming the generated Rust tables, in the **AX.25-over-WiFi (AXUDP/AXIP)** connectivity tier first, with the **KISS-host** tier as the natural second step and the **on-air PIO-AFSK RF node** as the ambitious third. Rationale below.

**Most important open questions** (expanded in §9):
1. Is the **typed-verb/guard codegen (SP-010)** done before or as part of this? It's the difference between a clean enum-dispatch port and re-implementing a string parser on an M0+.
2. Is "fully compliant" scoped to the **link layer** (yes, deliverable) — with node/L3 features explicitly out of the v2.2-compliance claim?
3. Does the v2.2 work-in-progress (mod-128, SABME, XID, segmentation) land in the **tables** (free) or the **runtime** (port cost) — mostly the latter for the moving parts.

---

## 1. The key insight, verified

The differentiator holds up under inspection. `ax25sdl` transcribes the AX.25 v2.2 SDL figures (figc4.1–figc4.7) to YAML and codegens 7 backends. The two that matter here are `spec/c` and `spec/rust`.

**The generated code is data, not behaviour.** I confirmed this directly:

- `spec/c/src/*.g.c` contains **zero function definitions** — every file is a tree of `static const` initialisers (`TransitionSpec[]`, `ActionStep[]`, `SubroutinePath[]`, `LoopRange[]`). Grep for any non-`const` function-like definition returns nothing.
- `spec/rust/src/*.g.rs` is the same: the only `fn`s present are `#[test]` functions (e.g. `transitions_are_present`, `t02_dl_connect_request`) which compile out of a release/`--no-default-features` build. The tables are `pub static … : &[TransitionSpec]`.
- The hand-written *type homes* (`spec/c/src/ax25sdl.h`, `spec/rust/src/types.rs`) are tiny (117 and 128 lines) and use only `&'static str` / `const char *` / `&'static [...]` — **no `Vec`, `String`, `Box`, `HashMap`, `Rc`, `Arc`** anywhere, in either the tables or the type homes. Confirmed by grep.

**Concrete size (the load-bearing finding).** Compiling `spec/c` for host x86-64 and running `size -t` on the static lib:

```
   text    data     bss     dec
  25881   43648       0   69529   (TOTALS, libax25sdl.a, x86-64)
```

So **~70 KB, all `.rodata`, zero BSS** — i.e. no mutable runtime globals, nothing on the heap, ROM-able into flash. Breaking down the string-literal content of the C source (~52 KB of literals):

| Category | ~bytes (source) | Load-bearing? |
| --- | --- | --- |
| `verb` strings (`"V(s) := V(s) + 1"`, `"DM"`, …) | 25.6 KB | **Yes today** — but exactly what SP-010 (typed verbs) replaces with an enum |
| `guard` strings (`"peer_busy == false"`, …) | 7.7 KB | **Yes today** — same; SP-010 replaces with typed predicate enum |
| `predicate` (loop continue conditions) | 0.2 KB | Yes |
| `notes` (transcription metadata) | 3.5 KB | **No** — pure documentation |
| `url` / `cite` / `quote` (citations) | ~0.1 KB | **No** — and the C backend already emits `references_len = 0` for every transition, so citations aren't even in the C tables |

Two big levers fall straight out of this:

1. **The C backend already strips citations.** The ~52 KB of citation `quote`/`note`/`url` text that bloats the C# package is *not* in the generated C (and only empty `&[]` slices in Rust). So the embedded tables are already lean.
2. **Verb + guard strings (~33 KB) are precisely the SP-010 target.** Tom already wants the stringly-typed verb/guard/event sets replaced by codegen'd compile-time-typed closed sets (per memory note *prefer-typed-over-string-dispatch*). On the desktop that's a correctness/maintenance win; **on the Pico it's also a 30 KB flash saving and — more importantly — it removes runtime string comparison and string-keyed dictionary lookups from the hot path** (see §3). This is the single highest-leverage piece of shared work: it helps every backend and is *load-bearing* for the embedded one.

**Bottom line on the tables:** they are essentially ideal for embedding — const, `no_std`, no allocator, ROM-able, ~70 KB today and shrinkable to single-digit KB of actual logic after SP-010. The walker that consumes them is small. **The tables are not the problem; the runtime is the work.**

---

## 2. What the runtime actually does (the C#/TS port surface)

The generated tables are inert. Everything that *executes* them is hand-written, lives in `packet.net/src/Packet.Ax25`, and is mirrored almost line-for-line in `ax25-ts`. This is what a C or Rust node must re-implement. Measured LOC:

| Component | File | LOC | What it does | Port difficulty |
| --- | --- | --- | --- | --- |
| **Action dispatcher** | `Session/ActionDispatcher.cs` | 1053 | The core. A `switch` over a closed enum of ~150 action verbs; each arm mutates the session context, arms/cancels timers, builds outgoing frame specs, raises L3 signals, or calls a subroutine. **The only home of floating-point** (SRT IIR, T1V). | **High** — biggest single file, but mechanical: a giant switch ports directly |
| **Session orchestrator** | `Session/Ax25Session.cs` | 369 | The walk loop: for an event, find transitions for current state, filter by event name, eval guard, run actions via the loop executor, advance state, with timer-rollback on a throwing action. | Medium |
| **Multi-session coordinator** | `Session/Ax25Listener.cs` | 729 | Owns the modem, address-filters inbound frames, maintains a per-peer session cache (LRU, `ConcurrentDictionary`), inbound pump, connect/accept. | Medium (simplify hugely for embedded — fixed session array, no LRU dict) |
| **Guard evaluator** | `Session/GuardEvaluator.cs` | 187 | **A runtime string-expression parser** (`expr := term (or term)*`, etc.) over a string-keyed binding dictionary, plus an alias map for codegen spelling drift. | Medium — **but SP-010 deletes most of this** (typed predicates → no parser, no dict) |
| **Subroutine registry** | `Session/SubroutineRegistry.cs` | 292 | Resolves `kind: subroutine` verbs by name, walks the figc4.7 `SubroutineSpec` paths through the same dispatcher+guards. String-keyed + alias map. | Medium — SP-010 helps here too |
| **Loop executor** | `Session/SdlLoopExecutor.cs` | 101 | Expands `LoopRange` slices (test-at-head / test-at-tail) over the flat action list, with a 1024-iteration safety cap. Shared by transitions and subroutines. | Low — clean, allocation-light, ports as-is |
| **Frame codec** | `Ax25Frame.cs` (+ `.Factories`) | 697 | Encode/decode AX.25 frames: address fields, mod-8/mod-128 control field width, N(S)/N(R)/PF bit extraction, PID, info. | Medium — bit-twiddling, ports cleanly to `&[u8]` |
| **XID** | `Xid/*.cs` | 633 | XID parameter negotiation (HDLC optional functions, classes of procedures) for v2.2. | Medium (only if XID negotiation is in scope) |
| **Segmenter / Reassembler** | `Session/Segmenter.cs` | 157 | §6.6 segmentation: split payload into ≤64 I-frame segments; reassemble. | Low |
| **Timer scheduler** | `Session/SystemTimerScheduler.cs` | 158 | `TimeProvider`-backed armed-timer dictionary (T1/T2/T3) with capture/restore. | Low–Medium — replace with hardware/Embassy timers |
| **Frame/signal record types** | `Session/*Spec.cs`, `*Signal.cs`, `Ax25Event.cs` | ~600 | The value types (`SupervisoryFrameSpec`, `UFrameSpec`, `IFrameSpec`, DL primitives, internal signals). | Low — plain structs/enums |
| Frame classifier, adapter, context, bindings | various | ~900 | Glue. | Low–Medium |
| **Total hand-written AX.25 runtime** | | **~6.2 k LOC** | | |

Plus the transports:

| Transport | File | LOC | Notes for embedded |
| --- | --- | --- | --- |
| **KISS** | `Packet.Kiss/*.cs` | ~830 (modular) | Encoder/decoder/framing/classifier. `KissEncoder` uses `ArrayPool` (heap) — trivially rewritten to a fixed buffer. The core SLIP-style escape logic is ~100 LOC and ports directly. |
| **AXUDP/AXIP** | `Packet.Axudp/AxudpSocket.cs` | 110 | Thinnest layer — UDP payload *is* the AX.25 frame body (optionally + CRC-16-CCITT FCS for XRouter). **Maps 1:1 onto lwIP `udp_pcb` / `embassy-net` UdpSocket.** This is the WiFi path's entire transport. |

**The node/L3 tiers do not exist yet:** `Packet.Node/Program.cs` and `Packet.L3/Class1.cs` are 6-line stubs. So "node functions" (BBS/CLI, beaconing, NET/ROM) are greenfield in *both* the desktop stack and any Pico port — there's nothing to reuse there, and nothing v2.2 says about them.

---

## 3. The no-FPU point, located precisely

The RP2040's Cortex-M0+ cores have **no hardware FPU**; `f64`/`f32` math is software-emulated (slow, and pulls in libgcc/compiler-rt soft-float). The question is whether that math is in the *tables* (a codegen concern, shared) or the *runtime* (a port concern, local). **It's entirely in the runtime**, in `ActionDispatcher`:

- **SRT IIR smoothing** (figc4.7 `Select_T1_Value`, §6.7.1.2):
  `ctx.Srt = 0.875 * Srt.TotalMilliseconds + 0.125 * sample.TotalMilliseconds` — floating-point in C#.
- **Linear backoff** (`Next T1 := (RC*0.25)+SRT*2`):
  `ctx.T1V = RC * 250 + Srt.TotalMilliseconds * 2` — already partly integerised (the `*0.25` became `*250` on ms), but still touches `double`.

The *tables* carry these as **opaque verb strings** (`"SRT := 7(SRT)/8 + (T1)/8 - ..."`); they don't do arithmetic. So:

- **No codegen change is needed** for the no-FPU target — the tables are arithmetic-free.
- The **port must use fixed-point / integer math** for the two formulas above. Both are trivially integerised: work in integer milliseconds and compute `7*srt/8 + sample/8` and `rc*250 + srt*2` with `u32`/`i32`. This is a ~10-line concern in the dispatcher port, not a structural problem. Worth a shared note in the runtime-port spec so the C and Rust ports (and arguably the C#/TS runtimes, for determinism) integerise identically.

Everything else in the runtime is already integer/byte/bool (`V(S)`, `V(A)`, `V(R)` are `byte`; flags are `bool`; counters are `int`).

---

## 4. The hardware reality (RP2040 / Pico W)

| Spec | Value | Relevance |
| --- | --- | --- |
| Cores | 2× Cortex-M0+ @ ~133 MHz | No FPU (§3). Second core is the natural home for the modem DSP (prior art does exactly this). |
| SRAM | **264 KB** (6 banks) | The crux for "fully compliant under mod-128". See §6. |
| Flash | 2 MB (external QSPI, XIP) | The ~70 KB const tables ROM happily here; flash is not the constraint. |
| PIO | 2 blocks × 4 state machines | **Excellent for bit-banged HDLC/AFSK/GFSK.** This is why the RP2040 is a strong modem host. Also drives the Pico W's nonstandard half-duplex SPI to the WiFi chip. |
| ADC | 12-bit, ~500 kS/s | Enough for AFSK1200 receive (correlator demod); prior art confirms. |
| PWM / DAC | PWM (RC-filtered) | AFSK/GFSK transmit. Prior art (`eleccoder`) does PWM AFSK TX. |
| Radio | **CYW43439 2.4 GHz WiFi** (the "W") | **Not a ham transceiver.** It's the lever for the AX.25-over-IP tier. lwIP (C SDK) or `embassy-net` (Rust). |

**There is no ham-band radio on the board.** That forces a connectivity decision (§5).

---

## 5. PHY / connectivity options

### 5a. RF node — external VHF/UHF radio + Pico-generated/decoded AFSK1200 (or G3RUH 9600) in PIO

A *real on-air node*. The Pico does the modem in PIO + ADC (RX) + PWM (TX); an external transceiver provides RF; PTT on a GPIO.

- **Prior art is strong and directly relevant:**
  - **`amedes/pico_tnc`** — encodes *and decodes* Bell 202 AFSK **without a modem chip**, on a bare Pico.
  - **`pfiliberti/pico_tnc`** (fork) — a *full* AX.25 Level-2 TNC (not APRS-only) with **KISS** + USB/TTL serial. Proof the RP2040 can do modem + link-layer simultaneously.
  - **`eleccoder/raspi-pico-aprs-tnc`** — PWM AFSK TX for APRS (Pico/Pico2).
  - **NinoTNC N9600A** (dsPIC33, not RP2040) — proves the "MCU does modem + AX.25" tier at 300/1200 AFSK + 9600 G3RUH/GFSK + IL2P; the de-facto modern TARPN node modem.
  - **OpenTNC** (STM32F411) and **MMDVM-TNC** (g4klx, STM32) — from-scratch embedded AX.25 TNCs.
  - **PiccoloSDR / Pi Pico Rx** — show the ADC + second-core DSP is genuinely capable of demod.
- **Trade-offs:** highest effort (modem DSP + timing + de-emphasis/pre-emphasis + DCD/carrier sense + PTT). AFSK1200 RX is the hard part (clock recovery, twist, weak-signal). 9600 G3RUH wants tighter analog (DC coupling). But it's the only option that puts the node *on the air*.
- **Verdict:** the prestige option, and the one with the most existing RP2040 code to lean on for the PHY — but the PHY work is *orthogonal* to the link-layer reuse story. You could even fork `pico_tnc`'s modem and feed its KISS output into the generated tables.

### 5b. KISS host — Pico runs the stack, talks KISS to an external modem/TNC over UART/USB

The Pico is the *brain* (link layer + node logic); a separate TNC (NinoTNC, a second Pico running `pico_tnc`, a Mobilinkd, direwolf on a host) is the modem.

- **Reuse:** `Packet.Kiss` ports almost verbatim (~100 LOC of escape logic + a frame classifier). The link-layer tables + runtime sit directly behind it.
- **Trade-offs:** lowest PHY risk (someone else's proven modem), clean separation, easy to bench-test (KISS-over-USB to a Linux host running `kissattach`/direwolf for interop). Doesn't use the "W" radio at all.
- **Verdict:** the **lowest-risk way to prove the link-layer port on real hardware**. Excellent Phase-1/2 target. A Pico talking KISS over USB to direwolf, completing a SABM/UA + I-frame exchange driven by the *generated tables*, is a clean milestone.

### 5c. AX.25-over-IP node (uses the W) — AXUDP/AXIP over WiFi

The Pico W carries AX.25 frames inside UDP over 2.4 GHz WiFi onto the packet-over-internet network (the LinBPQ/XRouter AXIP/AXUDP mesh). An RF-less node/gateway.

- **Reuse:** `Packet.Axudp` is 110 LOC and maps **1:1 onto lwIP `udp_pcb` or `embassy-net`'s UdpSocket** — receive datagram → it *is* the AX.25 frame body → feed to the tables; emit → serialise frame → send datagram. Optional CRC-16-CCITT FCS for XRouter interop (already in the codec).
- **Trade-offs:** **the only option that exploits the Pico *W's* actual radio.** No DSP, no analog, no PTT. Costs lwIP/`embassy-net` RAM (§6). It's "packet radio" only in the protocol sense (no RF), but it's a legitimate, useful node type — an internet gateway / always-on mailbox endpoint that speaks real AX.25 v2.2 to peers across AXUDP links.
- **Verdict:** **the best first target for the compelling story.** It exercises the full v2.2 link layer (connected mode, retransmission, mod-128, SREJ, segmentation) against real peers (packet.net, LinBPQ, XRouter) with *zero* PHY/DSP risk, and it's the option the hardware's headline feature is *for*. Prove parity here, then add a PHY (5a/5b) underneath the same tables.

**Recommended sequencing:** 5c (prove the port + parity over WiFi/AXUDP) → 5b (KISS host, prove it over a real modem with no new link-layer work) → 5a (on-air PIO modem, the showcase).

---

## 6. Memory / feasibility budget (264 KB SRAM, the crux)

Flash is not the constraint (2 MB; tables ~70 KB → much less after SP-010). **SRAM is.** Budget:

**Fixed costs:**

| Item | Estimate | Notes |
| --- | --- | --- |
| Const SDL tables | **0 KB SRAM** | Lives in flash (`.rodata`, XIP). Zero BSS confirmed. |
| Link-layer runtime code | flash, not RAM | The dispatcher/walker is code. |
| lwIP (WiFi tier only) | **~30–50 KB** | Pico W example `lwipopts`: `MEM_SIZE` 4000, `PBUF_POOL_SIZE` 24 × ~1.5 KB ≈ 36 KB of pbuf pool + heap + TCP segs. **Tunable down hard** for a UDP-only, low-throughput AXUDP node (small MSS, few pbufs) — plausibly ~16–24 KB. This is the single biggest RAM line on the WiFi tier. |
| cyw43 driver + WiFi firmware buffers | ~10–15 KB RAM | Firmware blob is in flash; working buffers in RAM. |
| Modem DSP buffers (RF tier only) | ~4–16 KB | ADC sample ring + correlator state on core 1. Not present on KISS/WiFi tiers. |
| Stacks (2 cores) + general heap | ~8–16 KB | |

**Per-session cost — the mod-128 driver:**

A session's `Ax25SessionContext` *scalar* state is tiny: V(S)/V(A)/V(R) (3 bytes), RC, a dozen bools, T1V/SRT/T2 (integers after §3), N1/N2/k. Call it **<64 bytes**. The RAM is in the three per-session collections:

- `IFrameQueue` — payloads awaiting first transmission (bounded by how much the app queues).
- `SentIFrames` — **map of N(S) → sent payload, kept for retransmission. Bounded by the send window k.**
- `StoredReceivedIFrames` — out-of-sequence received frames held for reordering. **Bounded by the receive window.**

Under **mod-8**, k ≤ 7 → at most ~7 outstanding frames each way. Under **mod-128**, the window can be up to **127**, and the v2.2 default after `Set_Version_2_2` is **k = 32** (from the generated `subroutines.g.c`: `Modulo := 128; k := 32; N1 := 2048`). So worst-case retransmit/reorder buffering per session is roughly:

```
2 directions × k frames × N1 bytes/frame
  mod-8,  k=7,  N1=256  →  2 × 7  × 256  ≈   3.6 KB / session
  mod-128 default k=32, N1=2048 →  2 × 32 × 2048 ≈ 256 KB / session   (!!)
  mod-128 default k=32, N1=256  →  2 × 32 × 256  ≈  16 KB / session
```

**This is the headline feasibility finding.** *Naïvely* honouring the mod-128 v2.2 defaults (k=32, **N1=2048**) blows the entire 264 KB on a single session's buffers. Mitigations, all legitimate and spec-compatible:

1. **Negotiate smaller k and N1 via XID.** XID exists precisely to negotiate these down. A node can advertise (say) k=8, N1=256 and still be fully mod-128-*capable*. This is the clean answer and it's spec-compliant — XID negotiation is part of v2.2 and is in the runtime port surface anyway.
2. **Cap concurrent sessions.** Replace the desktop `Ax25Listener`'s unbounded `ConcurrentDictionary` + LRU with a small fixed array (e.g. 2–4 sessions). A Pico node is not a 100-user BBS.
3. **Don't pre-reserve the window; allocate buffers as frames are actually outstanding.** The maps are sparse — a link rarely sits at full window. With static per-session arena allocators sized for a *configured* k (not the theoretical 127), RAM is predictable.
4. **Segmentation reassembly** (`Reassembler`) holds up to 64 × (N1−1) bytes for one in-flight multi-segment payload — ~16 KB at N1=256, ~130 KB at N1=2048. Same lever: bound N1.

**Conclusion:** *"Fully v2.2 link-layer compliant"* and *"fits in 264 KB"* are simultaneously achievable **provided N1 and k are bounded by configuration/XID rather than taking the mod-128 maxima.** Being mod-128/SREJ/segmentation *capable* (the wire formats, the state machine) costs almost nothing; being willing to *buffer* a 127-deep window of 2 KB frames is what doesn't fit. This distinction is worth stating loudly in any "fully compliant" claim — it's a buffering/negotiation policy, not a protocol-conformance gap. A 2-session WiFi/AXUDP node at k=8/N1=256 is comfortable (≈2× ~4 KB sessions + ~30 KB lwIP + overhead ≈ well under 100 KB).

---

## 7. Language / runtime comparison

| Option | Tables reuse | Runtime story | Concurrency (WiFi + modem) | RAM/flash | Verdict |
| --- | --- | --- | --- | --- | --- |
| **Rust + Embassy (`no_std`)** | **Generated Rust tables drop in** — already `Copy`, `&'static`, no alloc; need only `#![no_std]` on the hand-written `lib.rs`/`types.rs` (a tiny edit to non-generated files). | Port ~6 k LOC C# → Rust. Borrow-checker + exhaustive `match` catch the verb/guard bugs the C# port found at runtime. The C# dispatcher is *already* an exhaustive `switch` over a closed enum — ports almost mechanically to a Rust `match`. | **`embassy-net` is a mature `no_std`, no-alloc async stack; `cyw43` is the official async Pico W WiFi driver** (station mode, raw Ethernet frames, PIO-SPI, IRQ-driven). Async is a natural fit for "modem ISR + WiFi + timers + N sessions". | Smallest RAM of the three; flash competitive. | **RECOMMENDED.** Best safety, best concurrency primitives for this exact problem, and the codegen already emits the tables. The async model elegantly handles the multi-source event pump. |
| **C + Pico SDK** | **Generated C tables drop in** — pure `const`, ROM-able, already citation-stripped. Most native to the SDK; lwIP is C. | Port ~6 k LOC C# → C. The dispatcher's big `switch` ports directly; no enum exhaustiveness safety net (mitigate with `-Wswitch -Werror`). More manual memory discipline. | lwIP (C SDK) is the canonical Pico W path; mature, well-trodden. Concurrency is manual (core1 + IRQs + a run loop), no async sugar. | Comparable flash; RAM fine. | **Strong alternative**, especially if the team is more C than Rust, or wants the tightest SDK/lwIP integration. Loses the compile-time verb-exhaustiveness guarantee that caught real bugs (#258, #263) in the C# stack. |
| **TinyGo (generated Go)** | Generated Go tables exist (`spec/go`). | Would port the runtime to Go. | **GC latency is unpredictable** (any allocation can trigger a mark-sweep pause); cooperative scheduler. Multicore is new on RP2040. | Larger than C/Rust. | **IP-only tier at most.** The non-deterministic GC pause is disqualifying for the real-time AFSK modem path (bit timing). Acceptable-ish for a WiFi/AXUDP node if allocations are kept out of the hot path — but you'd be fighting the GC for no benefit over Rust. **Not recommended.** |
| **MicroPython (generated Python)** | Generated Python tables exist (`spec/python`). | Interpret the tables in Python. | Interpreted; **many× slower** than C/Rust/TinyGo in published MCU benchmarks. | Heaviest. | **Demo/教学 only.** Could limp an AXUDP node for hobby/teaching, never an on-air modem or a performance-credible node. **Not recommended** for the real deliverable. |

**Why Rust+Embassy over C, concretely for *this* codebase:**
- The C# `ActionDispatcher` is *already* written as an exhaustive `switch` *expression* over a closed enum specifically so that "a new verb cannot ship without a handler" is a **compile error** (CS8509). Two real production bugs (UI-reception #258, DL-DATA-while-connecting #263) were eliminated by that pattern. **Rust's exhaustive `match` preserves that guarantee verbatim; C's `switch` does not** (you'd lean on `-Wswitch-enum -Werror` and hope). Given the whole project's thesis is *provable* conformance, keeping the compile-time exhaustiveness across the port is worth a lot.
- `embassy-net` + `cyw43` give the WiFi tier for free in the idiom (async tasks) that suits a multi-source event pump, with no allocator.
- The generated Rust tables are *already in the repo and CI-tested*; making them `no_std` is a one-line attribute on a hand-written file.

---

## 8. What "fully compliant" and "node" mean — scope tiers

Be precise about the claim. "Fully AX.25 v2.2-compliant" should mean **the link layer (LAPB-for-amateur-radio) is fully compliant** — and that is exactly what the shared SDL delivers:

| Tier | What it is | v2.2 link-layer compliance | Reuse from this ecosystem | Pico feasibility |
| --- | --- | --- | --- | --- |
| **Bare TNC / modem (KISS)** | HDLC framing + modem; no connected-mode logic of its own (host does AX.25). | N/A (it's a modem) | `Packet.Kiss` | Trivial (prior art: pico_tnc). Not the interesting claim. |
| **Connected-mode endpoint** | Full data-link state machine: SABM(E)/UA/DISC/DM, I/RR/RNR/REJ/SREJ, T1/T2/T3, retransmission, mod-8 **and mod-128**, XID, segmentation. | **YES — this is the deliverable.** Same generated tables as desktop/browser. | **The whole ax25sdl + Ax25Session/Dispatcher port.** | **Feasible** within budget (§6), N1/k bounded. |
| **Digipeater** | Repeats frames per the digi path (UI + connected). | Compliant repeating is link-layer + a small forwarding policy. | Frame codec path-handling (`ReversedTriggerPath` etc. already in dispatcher). | Feasible; modest add. |
| **Full node (BPQ/XRouter-like)** | User CLI, beaconing, mailbox/BBS, NET/ROM L3/L4 routing. | **Outside v2.2** — these are above the link layer; v2.2 says nothing about them. | **Nothing to reuse** — `Packet.Node`/`Packet.L3` are empty stubs in the desktop stack too. Greenfield. | CLI + beaconing: feasible. NET/ROM L3/L4: feasible but a separate, larger project; RAM for routing tables is its own budget. |

**The compelling story to tell:** *a node whose link-layer compliance is the **same generated state machine** the desktop (`packet.net`) and browser (`ax25-ts`) stacks run — byte-for-byte the same ~243 transitions + figc4.7 subroutines, from one spec source, proven by the same per-transition conformance tests, now executing on a $6 dual-M0+ with 264 KB of RAM.* That's "provable parity across hardware classes", and it's the differentiator no hand-written MCU TNC (OpenTNC, pico_tnc, MMDVM-TNC) can claim. Node features (CLI/beacon/NET-ROM) layer *above* that and are explicitly not part of the v2.2-compliance claim.

The in-flight v2.2 work (mod-128 framing, SABME, XID negotiation, segmentation) is being added to the **same SDL + runtime**, so a Pico node *inherits* it — but note (§2, §3, §6) that the moving parts land mostly in the **runtime** (XID parser, mod-128 control-field handling in the codec, segmentation buffers) and the **tables** (the SABME/Set_Version_2_2 transitions, already present). So inheriting it is "re-port the updated runtime", not "free".

---

## 9. Risks / unknowns

1. **SP-010 (typed verbs/guards) timing — biggest lever.** If the codegen still emits stringly-typed verbs/guards when the port happens, the C/Rust runtime must either (a) ship a string-expression parser + string-keyed dispatch on an M0+ (works — it's what C#/TS do — but wasteful in flash and cycles), or (b) the porter writes the enum mapping by hand and it drifts from the codegen. **Strongly prefer doing SP-010 first / as part of this**, so the codegen emits typed enums that the Rust `match` / C `switch` consume directly. This is shared work that benefits every backend and is *load-bearing* for embedded. **Open question for Tom.**
2. **mod-128 buffering policy (§6).** "Fully compliant" must be paired with a stated N1/k bounding policy (via XID + config), or the claim implies 256 KB/session. Decision needed: what does the node advertise? (Recommend k≤8–16, N1≤256 for a Pico, fully mod-128-capable.)
3. **Floating-point integerisation (§3).** Two formulas. Low risk, but the C, Rust (and ideally C#/TS, for cross-impl determinism) ports should integerise *identically* or conformance vectors involving T1V/SRT could diverge. Worth a shared spec note.
4. **AFSK RX robustness (RF tier only).** The Pico can demod AFSK1200 (prior art proves it), but weak-signal/twist/clock-recovery quality vs. a NinoTNC/direwolf is unproven *for this project*. De-risk by treating the modem as swappable (KISS boundary) — start on 5c/5b, where the modem isn't the Pico's problem.
5. **lwIP RAM under load (WiFi tier).** Forum reports of lwIP "memory freezing" on Pico W under TCP. AXUDP is UDP, lower-risk, but needs careful `lwipopts` tuning and a tested back-pressure path. `embassy-net` (no-alloc) sidesteps lwIP's heap entirely — another point for Rust.
6. **Reuse boundary is a *port*, not a *link*.** Unlike `ax25-ts` (which consumes the npm `ax25sdl` tables and re-implements the runtime in TS), there is no published C/Rust *runtime* to depend on — only the tables. The ~6 k LOC runtime is a genuine re-implementation. It must be kept in sync with `packet.net`/`ax25-ts` as the v2.2 runtime evolves (the same maintenance tax `ax25-ts` already pays). Consider whether the Rust runtime should itself become a published `no_std` crate so a Pico firmware just depends on it — that would make the runtime, not just the tables, reusable, and is arguably the highest-value structural outcome of this exercise.
7. **Conformance proof on-target.** The generated per-transition tests run on host. Re-running (a subset of) them on-target — or better, running the same interop harness (vs. LinBPQ/XRouter/direwolf over AXUDP) that `packet.net`/`ax25-ts` use — is what *earns* the "provable parity" claim. Plan for it; it's also the best functional test of the port.
8. **No FPU + soft-float pulled in by accident.** Easy to drag in `f64` via a careless `as f64` and balloon flash. Lint for it; keep the dispatcher integer-only.

---

## 10. Suggested phased path to a prototype

**Phase 0 — Spec/codegen prep (in `ax25sdl`, shared, no Pico yet).**
- Land **SP-010**: codegen emits typed verb + guard/predicate enums (closed sets) in C and Rust. (Biggest single de-risk; benefits all backends.)
- Add a codegen/build flag to **strip transcription `notes`** from embedded backends (citations already absent from C).
- Add `#![no_std]` capability to the generated Rust crate (`lib.rs`/`types.rs` edit + a `default = ["std"]` feature for the test harness). Confirm `cargo build --no-default-features --target thumbv6m-none-eabi` produces the tables.
- Write a one-page "runtime integerisation" note (SRT/T1V fixed-point) so C/Rust/(C#/TS) agree.

**Phase 1 — Host-side `no_std` runtime spike (Rust, on a workstation).**
- Port the *core* runtime to a `no_std` Rust crate: `Ax25SessionContext`, `GuardEvaluator`(typed), `SdlLoopExecutor`, `SubroutineRegistry`(typed), `ActionDispatcher` (the big `match`), frame codec, `Segmenter`. Static/arena allocation, configurable k/N1.
- Drive it with the **existing conformance vectors** + a couple of the `packet.net` two-session harness scenarios (SABM/UA → I-frame exchange → loss/SREJ recovery). Green here = the port is correct *before* touching hardware.
- Decide: should this crate be the publishable `no_std` runtime (risk #6)?

**Phase 2 — AXUDP-over-WiFi node on the Pico W (tier 5c).**
- Embassy + `cyw43` + `embassy-net`; map `Packet.Axudp` onto a UdpSocket. Hardware timers for T1/T2/T3. Fixed 2-session array (no LRU dict).
- Milestone: **the Pico W completes a full connected-mode session (SABM/UA, I-frames, RR/REJ/SREJ recovery, clean DISC) against `packet.net` or LinBPQ over AXUDP**, driven by the generated tables. Run the interop harness against it. *This is the headline "parity across hardware classes" demo.*
- Measure real RAM; tune `embassy-net`/k/N1.

**Phase 3 — KISS-host node (tier 5b).**
- Port `Packet.Kiss` (fixed-buffer encoder). Same link-layer stack behind a UART/USB KISS port. Bench against direwolf/`kissattach` on a host, and against a second Pico running `pico_tnc` as the modem.
- Milestone: on-air-capable via *someone else's* proven modem, with zero new link-layer code.

**Phase 4 — On-air PIO-AFSK RF node (tier 5a, the showcase).**
- Bring the modem onto core 1 (PIO + ADC RX + PWM TX + GPIO PTT), borrowing from `amedes/pico_tnc`. Carrier sense / DCD / TXdelay.
- Milestone: a self-contained $6 on-air AX.25 v2.2 node whose link layer is the same generated state machine as the desktop and browser stacks.

**Phase 5 (optional) — Node features.** CLI/beaconing (small); NET/ROM L3/L4 (separate larger project, greenfield everywhere, its own RAM budget). Not part of the v2.2-compliance claim.

---

## Sources

- amedes/pico_tnc — Bell 202 AFSK encode+decode without a modem chip on Pico: https://github.com/amedes/pico_tnc
- pfiliberti/pico_tnc — full AX.25 Level-2 TNC + KISS on Pico: https://github.com/pfiliberti/pico_tnc
- eleccoder/raspi-pico-aprs-tnc — PWM AFSK TX for APRS on Pico/Pico2: https://github.com/eleccoder/raspi-pico-aprs-tnc
- TARPN NinoTNC N9600A (dsPIC33; 300/1200 AFSK, 9600 G3RUH/GFSK, IL2P): https://tarpn.net/t/nino-tnc/n9600a/n9600a_info.html
- MMDVM-TNC (g4klx, STM32 AX.25 TNC firmware): https://github.com/g4klx/MMDVM-TNC
- AE6EO OpenTNC (STM32F411, from-scratch embedded AX.25 TNC): https://www.radagast.org/~dplatt/hamradio/OpenTNC/
- Dire Wolf (soundcard AX.25 TNC — desktop reference, not MCU): https://github.com/wb2osz/direwolf
- Embassy cyw43 driver (official async Pico W WiFi driver): https://docs.embassy.dev/cyw43/
- embassy-net (no_std no-alloc async network stack): https://docs.embassy.dev/embassy-net/
- Embassy framework: https://github.com/embassy-rs/embassy
- Pico W lwIP example config (MEM_SIZE/PBUF_POOL/MSS): https://github.com/raspberrypi/pico-examples/blob/master/pico_w/wifi/lwipopts_examples_common.h
- lwIP throughput/memory tuning: https://lwip.fandom.com/wiki/Maximizing_throughput , https://lwip.fandom.com/wiki/Lwipopts.h
- RP2040 memory layout: https://petewarden.com/2024/01/16/understanding-the-raspberry-pi-picos-memory-layout/
- RP2040 datasheet: https://datasheets.raspberrypi.com/rp2040/rp2040-datasheet.pdf
- AFSK correlator demod primer: https://notblackmagic.com/bitsnpieces/afsk/
- Pi Pico Rx / PiccoloSDR (ADC + second-core DSP receive on Pico): https://101-things.readthedocs.io/en/latest/radio_receiver.html , https://www.rtl-sdr.com/piccolosdr-a-simple-sdr-from-a-raspberry-pi-pico/
- TinyGo build options / scheduler / GC notes: https://tinygo.org/docs/reference/usage/important-options/
- C/C++/MicroPython/Rust/TinyGo MCU benchmark (MicroPython many× slower): https://www.mdpi.com/2079-9292/12/1/143
- AX.25 v2.2 specification (link-layer reference): https://www.ax25.net/AX25.2.2-Jul%2098-2.pdf
- ax25sdl source of truth (this analysis read `spec/c`, `spec/rust`): https://github.com/packet-net/ax25sdl
