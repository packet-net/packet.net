# How far can `ax25sdl` codegen reach? — shrinking the hand-written per-language AX.25 runtime

*Research / options analysis — 2026-06-03. Exploratory; no code, no repo changes. Grounded against the live `packet-net/ax25sdl`, `packet-net/packet.net` and `packet-net/ax25-ts` trees, the AX.25 v2.2 spec, the existing `docs/conformance-harness-plan.md`, and external prior art (Kaitai Struct, Wycheproof — links at the end). Companion to `pico-packet-node.md` ("the work is the runtime, not the tables").*

---

## 0. TL;DR / headline recommendation

`ax25sdl` today generates the **SDL transition/subroutine tables** + (since SP-010) the **`Ax25ActionVerb` closed set**, into 7 backends. Everything else AX.25 needs — the frame control-field codec, the XID TLV codec, the segmenter, the constants, the default parameter sets, the guard evaluator, the dispatcher — is **hand-written twice and counting** (C# in `packet.net`, TypeScript in `ax25-ts`), and the Pico research says it'll be hand-written a third time (Rust/C) for embedded. I read both runtimes side by side: **they are line-for-line ports of the same spec-defined bit layouts and lookup tables.** `CONTROL_SABM = 0x2f` appears verbatim in `Ax25Frame.Factories.cs` and `frame.ts`; `(control >> 5) & 0x07` is the mod-8 N(R) read in both; the XID PI constants, the §6.3.2 reverts-to merge, the Figure 6.2 segment byte — all duplicated. Every duplication is a drift risk and every new language pays the re-port tax.

**The single highest-leverage move is not more codegen — it is generating a language-neutral corpus of conformance vectors** (frame encode/decode pairs, XID round-trips, FSM scenario traces) that every backend executes. It drift-proofs the parts you *don't* generate, it's cheap, it has gold-standard prior art (Wycheproof), the repo already has the format strawman (`tests/conformance/connect-sabm-ua-disc.yaml`) and the plan already wants it (conformance-harness-plan.md Phase A3). **Do this first, as a safety net, before expanding codegen.**

**The highest-leverage new codegen target is the frame control-field codec + the constants/PID/default-param tables** — a fixed bit-layout (Fig 4.1a/4.1b/4.2/4.3) that is pure declarative data and is currently the most-duplicated, most bit-twiddly, most embedded-relevant hand-written code. This is the same job Kaitai Struct does for 11 languages; AX.25's framing is squarely in scope. **The XID TLV codec (§4.3.3.7)** is the second target — even more declarative (a PI/PL/PV table), and its reverts-to merge (§6.3.2) is a per-parameter rule table.

**SP-010's guard-atom + event-name closed sets are the natural, cheap continuation of the verb work** and should land next regardless — they're small, they kill a live bug class, and they're a prerequisite for the more ambitious *typed guard-predicate AST* (which is real but lower priority; the ad-hoc string-splitter works for now).

**What stays hand-written, permanently:** the *behaviour* an action performs (the side effect of `Set Own Receiver Busy` is `ctx.ownReceiverBusy = true` — trivial but semantic), timers, async I/O, the session/listener orchestration, the link-multiplexer model, platform transports. Codegen can supply the *structure, closed sets, wire formats, constants, and vectors*; it cannot supply the *effecting runtime*.

Priority order (value × feasibility), argued in §11:

1. **Conformance vectors** (drift-proofs everything; cheap; do first).
2. **SP-010 guard-atom + event closed sets** (cheap; kills a live bug class; already half-done).
3. **Constants / PID / default-parameter / control-base tables** (trivial, high drift-payoff, embedded-relevant).
4. **Frame control-field codec** (high value, medium effort; the big duplication; Kaitai-shaped).
5. **XID TLV codec + reverts-to merge table** (high value, medium effort; most declarative).
6. **Typed guard-predicate AST/evaluator** (real, but lower priority; the string-splitter suffices today).
7. Address codec, segmenter (modest; bundle with #4/#1).

---

## 1. What exists today (verified)

`ax25sdl` is a clean pipeline: **YAML (`spec-sdl/**.sdl.yaml`) → validate + canonicalise (against `actions.yaml`/`events.yaml`) → language-neutral IR (`ResolvedPage`/`ResolvedSubroutinesPage`, in `Packet.Sdl.IR/Resolved.cs`) → per-language emitters** (`CsharpEmitter`, `GoEmitter`, `TsEmitter`, `RustEmitter`, `CEmitter`, `PythonEmitter`, `JsonEmitter`; Scriban templates for C#). All 7 backends are built **and tested** in CI, not merely diff-checked.

Two kinds of artefact come out:

- **(a) the tables** — `TransitionSpec[]` / `ActionStep[]` / `SubroutinePath[]` / `LoopRange[]`, emitted as pure `static const` data in every backend (confirmed in `spec/c/src/disconnected.g.c`: zero functions, all initialisers).
- **(b) the `Ax25ActionVerb` closed set** (SP-010 / packet.net#260) — `spec/csharp/Ax25ActionVerb.g.cs` is a C# enum with ~140 members, one per canonical verb across all pages + subroutines; TS gets a string-literal union (`ax25-action-verb.g.ts`). The emit logic lives in `Program.cs` (`EmitActionVerbEnum` / `EmitActionVerbUnion`), gated to C# + TS only — **the other 5 backends keep the raw string verb.**

A crucial, often-missed third thing the pipeline already does: **it treats the hand-written runtime as a lint target.** `lint-targets.yaml` + the `Lint*` passes in `Program.cs` regex-scan each runtime's *actual source* (`ActionDispatcher`, `Ax25SessionBindings`, `SubroutineRegistry`) and fail codegen if a verb/predicate/subroutine the tables emit has no case/binding/registry entry in a given language. **This is the project already half-admitting the runtime is spec-derivable** — it cross-checks the hand-written runtime against the generated tables. Everything below is the question "what if, instead of *linting* the hand-written runtime against the spec, we *generated* the spec-derived parts of it?"

### The drift surface, quantified

| Runtime | Total LOC | Of which the "SDL runtime" (dispatcher/guards/segmenter/XID/frame) |
| --- | --- | --- |
| `packet.net` `Packet.Ax25/` | ~7.3 k C# (Session) + 0.6 k Xid | the bulk |
| `ax25-ts` `src/` | ~7.1 k TS | `src/sdl/` ~3.5 k + `frame.ts`/`xid.ts`/`segmenter.ts` |

The generated tables are a *small* fraction of this; **the runtime around them is the mass**, and it's the part re-ported per language. The Pico research already established the embedded port is ~6 kLOC of fresh Rust/C. Each candidate below is "could this slice of the 7 k come from codegen instead of a re-port?"

---

## 2. The codegen-able / hand-written line (the organising principle)

For each candidate, the test is three questions:

1. **Spec-derived?** Is the content fixed by AX.25 v2.2, not by an implementation choice?
2. **Declarative?** Can it be expressed as *data* (a bit layout, a TLV table, a constant set, a rule table) rather than *behaviour* (a side effect, an I/O call, a control-flow decision)?
3. **Multi-language?** Does the same data project cleanly into all 7 backends' idioms?

A "yes" to all three ⇒ codegen-able. The dividing line is sharp and worth stating once:

- **Codegen-able:** wire formats (bit/byte layouts), closed enumerations (verbs, guard atoms, events, error codes, PIDs, frame-type bases), constant sets (default parameter sets, timer defaults), rule tables (the reverts-to merge), and *vectors* (input→output pairs derived from any of the above).
- **Inherently hand-written:** the *effecting behaviour* of an action (what `Set Own Receiver Busy` actually mutates), timers and the clock, async/await and threading, byte I/O and transports (KISS/AXUDP/serial/WebSerial), the session/listener lifecycle and lookup, the link-multiplexer/channel-arbitration model, and any policy (`able_to_establish`).

The subtlety: even a "codegen-able" wire format needs a hand-written *type home* (the structs the generated tables reference). Today those type homes (`spec/rust/src/types.rs`, `spec/c/src/ax25sdl.h`, etc., ~120 lines each) are **hand-written and must be kept in sync across 6 languages by hand** — a real, if small, existing drift surface that any expansion should fold into codegen too.

---

## 3. Candidate A — Constants, PIDs, frame-type bases, default parameter sets

**What it is.** The scalar furniture of AX.25: PID values (0xF0 no-L3, 0xCF NET/ROM, 0x08 segmented), U-frame control bases (SABM 0x2F, SABME 0x6F, DISC 0x43, UA 0x63, DM 0x0F, FRMR 0x87, XID 0xAF, TEST 0xE3, P/F 0x10), S-frame bases (RR 0x01, RNR 0x05, REJ 0x09, SREJ 0x0D), UI control (0x03 / 0x13), the segment-control byte format (Fig 6.2: `F` bit 0x80 + 7-bit count mask 0x7F), `MaxDigipeaters = 8`, and the **v2.0 (§1436) / v2.2 (§4.3.3.7) default parameter sets** (N1=256, k=4/7/32, T1=3000 ms, N2=10, T2=3000, T3=...).

**Spec-derived / declarative?** Maximally. These are literally numbers in the spec. Every one I found is duplicated *verbatim* across the two runtimes: `Ax25Frame.Factories.cs` lines 19–31 and `frame.ts` lines 41–54 are the same hex table typed twice; `XidNegotiator.ApplyVersion20Defaults` (C#) and the §1436 list in `action-dispatcher.ts` comments are the same default set; the plan's §5.2 ("Defaults: PI 0x06 N1=256, PI 0x08 k=4/32, PI 0x09 T1=3000 ms, PI 0x0A N2=10") is a third copy in prose.

**Codegen sketch.** A new `spec-sdl/constants.yaml` (or `wire.yaml`) declares them as named, typed, spec-cited values and grouped sets:

```yaml
pids:
  - { name: NoLayer3, value: 0xF0, cite: "§3.4" }
  - { name: Segmented, value: 0x08, cite: "§6.6" }
u_frame_bases:
  - { name: SABM, value: 0x2F, cite: "§4.3.3.1" }
  - { name: SABME, value: 0x6F, cite: "§4.3.3.2" }
  # …
default_param_sets:
  v2_0:   { n1: 256, k: 7, t1_ms: 3000, n2: 10, modulo: 8, cite: "§1436" }
  v2_2:   { n1: 256, k_mod8: 4, k_mod128: 32, t1_ms: 3000, n2: 10, cite: "§4.3.3.7" }
```

Each backend emits its idiom: C# `const byte` / `static readonly` records, TS `export const`, Rust `pub const`, C `#define` / `static const`, Go `const`, Python module constants, JSON a manifest. This is the *easiest possible* emitter — no IR walking, just a flat data file → a constants module per language. It can reuse the existing `WriteIfChanged` + per-backend formatting plumbing wholesale.

**Value.** Disproportionate to the effort. It's the cheapest thing to generate and one of the highest drift-payoffs: a wrong control base or default is an immediate on-air interop failure, and today nothing links the C# copy to the TS copy. For the embedded port it means the Rust/C node gets its constants *for free and provably identical*. It also gives the conformance vectors (Candidate F) and the codec (Candidate B) a single source for the values they encode.

**Effort / risk.** Low. The only design question is granularity (one big file vs split by concern). Risk: near zero — it's data.

**What stays hand-written.** Nothing in this candidate; it's pure data. (The *use* of a default set — deciding *when* to apply §1436 — stays in the runtime.)

---

## 4. Candidate B — The frame control-field codec (encode + parse + accessors)

**What it is.** Mod-8 and mod-128 I/S/U control-field encode + decode, and the `N(S)`/`N(R)`/`P-F` accessors. Fixed bit layouts:

- **I-frame mod-8 (Fig 4.1a):** `(N(R)<<5) | (P<<4) | (N(S)<<1) | 0`.
- **I-frame mod-128 (Fig 4.1b):** octet0 `(N(S)<<1)|0`, octet1 `(N(R)<<1)|P`.
- **S-frame mod-8 (Fig 4.2):** `(N(R)<<5) | (P/F<<4) | base`.
- **S-frame mod-128 (Fig 4.3b):** base octet, then `(N(R)<<1)|P/F`.
- **U-frame:** base `| (P/F ? 0x10 : 0)`, 1 octet both modes.

**Spec-derived / declarative?** Yes — this is a bit-field table. And it is the **canonical example of the parallel-reimplementation problem**: I diffed `Ax25Frame.cs`/`.Factories.cs` against `frame.ts` and they are the same arithmetic twice. `Ax25Frame.Ns` is `(Control>>1)&0x07` (mod-8) / `(Control>>1)&0x7F` (mod-128); `getNs()` in TS is identical. `Nr`, `PollFinal`, `classify`, the SABM/SABME/… factories — all line-for-line. The Pico research will make it a third copy.

This is **exactly what Kaitai Struct does**: a declarative `.ksy` binary-format spec compiled to parsers in C++/C#/Go/Java/JS/Lua/Nim/Perl/PHP/Python/Ruby/Rust. AX.25 framing is in scope for that class of tool; the only reason it's hand-written here is that `ax25sdl` grew from the SDL side, not the wire side.

**Codegen sketch.** A `spec-sdl/frame.yaml` declaring the control-field as bit-field structs, parameterised by modulo:

```yaml
control_fields:
  i_frame:
    mod8:   { octets: 1, fields: [ {name: ns, bits: "3..1"}, {name: pf, bit: 4}, {name: nr, bits: "7..5"}, {const: {bit: 0, value: 0}} ] }
    mod128: { octets: 2, fields: [ {name: ns, bits: "1.7@0"}, {const: {bit: "0@0", value: 0}}, {name: nr, bits: "1.7@1"}, {name: pf, bit: "0@1"} ] }
  s_frame:
    mod8:   { octets: 1, fields: [ {name: ss, bits: "3..2"}, {const: {bits:"1..0", value: 1}}, {name: pf, bit: 4}, {name: nr, bits: "7..5"} ] }
    # …
```

An emitter generates per-language: an encode function (fields → octet(s)), a decode/accessor set (octet(s) → fields), and the frame-kind classifier (the `control & 0x01`/`0x03` dispatch). The address-field C-bit/E-bit handling (§6.1.2 command/response, E-bit on last address) is also declarative and belongs here.

**Realism caveat — this is the hardest emitter to write well.** Bit-field codegen across 7 languages is genuinely more work than the SDL-table emitter (which emits *data*; this emits *logic*). Two pragmatic options: (i) write the emitter inside `ax25sdl` (full control, matches the existing pattern, but it's a real bit-codec compiler); or (ii) **adopt Kaitai Struct for the framing layer specifically** — write a `.ksy`, let kaitai-struct-compiler emit the parsers, and keep `ax25sdl` for the SDL/closed-set/vector layers. Option (ii) gets you 11 languages and a maintained compiler for free but adds a second toolchain and a runtime dependency (the Kaitai runtime lib per language), and Kaitai is **parse-oriented** — its serialisation/encode support is newer and less complete than its parsers, which matters because AX.25 needs strict *encode* as much as decode. My lean: **own the emitter in `ax25sdl`** for encode/decode symmetry and zero runtime deps, but study Kaitai's `.ksy` as the design language. Either way, **generate the vectors (Candidate F) first** so whichever codec you ship is provably correct against the same corpus the hand-written ones pass today.

**Value.** High. This is the single biggest block of duplicated bit-twiddling, the most error-prone to port (off-by-one in a shift = silent wire corruption), and the most embedded-relevant (the Pico node's hot path is frame encode/decode). Generating it collapses N hand-written codecs to one spec file.

**Effort / risk.** Medium–high (the emitter is a small bit-field compiler). Risk: the mod-128 mode-awareness (control width isn't derivable from the bytes — the decoder must be told the link's modulo; see plan V1) must be modelled as a decode parameter, not inferred. The strict-encode / lenient-parse split (`Ax25ParseOptions`) is a *policy* layer that stays hand-written on top of the generated core.

**What stays hand-written.** The FCS/CRC-16 (it's an algorithm, though its *test vectors* are codegen-able), the KISS framing, the parse-options leniency policy, the `ReadOnlyMemory`/`Uint8Array` buffer ergonomics, and the stream/transport plumbing.

---

## 5. Candidate C — The XID information-field TLV codec (§4.3.3.7)

**What it is.** The XID parameter-negotiation payload: FI 0x82 + GI 0x80 + GL + an ordered run of PI/PL/PV triples (Classes of Procedures PI=2, HDLC Optional Functions PI=3, I-Field-Length-Rx PI=6, Window-k-Rx PI=8, Ack-Timer-T1 PI=9, Retries-N2 PI=10), plus the bit layouts *inside* the two bit-field parameters (COP: ABM/duplex bits; HOF: REJ/SREJ, modulo-8/128, segmenter, the always-1 bits).

**Spec-derived / declarative?** The most declarative candidate of all — it is a TLV table + two sub-bit-fields, all fixed by Figure 4.5. And, again, **duplicated**: `xid.ts` and `Packet.Ax25/Xid/*.cs` are parallel implementations (same PI constants, same HOF bit positions `HOF_BIT_MODULO_128 = 11`, same FI/GI, same big-endian numeric encoder). The plan's §17 even records the C# being written (V3 part 1) with the TS parity leg explicitly "pending" — i.e. the duplication is *known and scheduled*, which is precisely the re-port tax this analysis is about.

**Codegen sketch.** A `spec-sdl/xid.yaml`:

```yaml
xid:
  format_identifier: 0x82
  group_identifier: 0x80
  parameters:
    - { pi: 2, name: ClassesOfProcedures, kind: bitfield, pl: 2,
        bits: [ {name: abm, bit: 0, const: 1}, {name: half_duplex, bit: 5}, {name: full_duplex, bit: 6} ] }
    - { pi: 3, name: HdlcOptionalFunctions, kind: bitfield, pl: 3,
        bits: [ {name: rej, bit: 1}, {name: srej, bit: 2}, {name: ext_addr, bit: 7, const: 1},
                {name: modulo8, bit: 10}, {name: modulo128, bit: 11}, {name: segmenter, bit: 22}, … ] }
    - { pi: 6, name: IFieldLengthRx, kind: uint_be, unit: bits }
    - { pi: 8, name: WindowSizeRx,  kind: uint7 }
    - { pi: 9, name: AckTimer,      kind: uint_be, unit: ms }
    - { pi: 10, name: Retries,      kind: uint_be }
  reverts_to:                              # §6.3.2 merge rules — Candidate D
    HdlcOptionalFunctions: lesser          # SREJ>REJ, 128>8 → AND
    ClassesOfProcedures:   lesser          # full only if both
    WindowSizeRx:          min
    IFieldLengthRx:        min
    AckTimer:              greater
    Retries:               greater
```

The same emitter that does Candidate B's bit-fields handles the COP/HOF sub-fields; a TLV walker handles encode (ordered PI emission, GL computation) and decode (skip-unknown-PI, PL=0 = default). Reuses Candidate A's constants.

**Value.** High and *imminent* — the TS XID leg is unwritten, so generating it avoids writing the second copy at all, and pre-empts the third (embedded). It's the cleanest demonstration that "spec-derived declarative structure" generalises beyond the SDL tables.

**Effort / risk.** Medium. Shares the bit-field machinery with Candidate B (do them together). Risk: the spec figure has documented internal contradictions (the COP ABM bit-0-vs-bit-1 anomaly, the HOF caption-vs-prose REJ/SREJ mismatch — both carefully handled in the hand-written codecs' comments). Codegen must encode the *resolved* interpretation, and the conformance vectors must pin it. This is a place where `actions.yaml`-style "trust the figure but record the canonical" discipline applies to wire bits.

**What stays hand-written.** The `XidParseOptions` leniency (GL overrun, truncated parameter) — policy, like Candidate B. The negotiation *FSM* (figc5.x MDL) stays in the SDL tables / runtime.

---

## 6. Candidate D — The XID reverts-to merge rules (§6.3.2)

**What it is.** The per-parameter rule for combining our XID offer with the peer's response: HDLC-optional-functions → *lesser* (SREJ only if both; mod-128 only if both); duplex → *lesser* (full only if both); window-k and N1 → *min* (notification semantics); T1 and N2 → *greater*; absent-from-both → retain current. Implemented as `XidNegotiator.ApplyNegotiated` (C#, ~90 lines), TS leg pending.

**Spec-derived / declarative?** The *rule selection* is declarative — a one-word tag per parameter (`lesser`/`greater`/`min`/`mutual-and`), shown inline in the Candidate C sketch above. The *application* is a tiny generic combinator. So ~90 lines of hand-written per-language merge collapses to a rule table + one generic `applyMerge(offered, response, rules)` function.

**Codegen sketch.** Fold the `reverts_to:` block into `xid.yaml` (above). Emit either (a) a data table the runtime's generic merge consumes, or (b) the merge function itself per language. (a) is lower-risk and enough — the combinators (`min`/`max`/`and`) are trivial primitives the runtime keeps.

**Value.** Medium. Smaller surface than the codec, but it's pure spec semantics that's easy to get subtly wrong (the "lesser of SREJ/REJ means AND" reasoning), and the TS copy is unwritten — same imminent-re-port argument as Candidate C.

**Effort / risk.** Low–medium (rides on Candidate C). Risk: the spec prose has the well-known PI=10 "N1 vs N2" mislabel; the rule table must name the *semantic* parameter, not the figure's label.

**What stays hand-written.** Mapping the merged values onto the live session context (`ctx.SrejEnabled = …`) — that's the effecting behaviour, runtime by definition.

---

## 7. Candidate E — Guard atoms + event/trigger names (the SP-010 follow-on), and the harder typed-predicate AST

This splits into two very different sub-candidates.

### E1 — Closed sets for guard atoms + events (cheap, do next)

**What it is.** The same treatment SP-010 gave verbs, applied to (i) the guard-atom identifiers (`own_receiver_busy`, `peer_busy`, `srej_enabled`, `V_s_eq_V_a`, `P_eq_1`, …) and (ii) the event/trigger names (`I_received`, `DL_FLOW_OFF_request`, `T1_expiry`, …). Today both are free strings; `events.yaml` exists but is, per `Catalogs.cs`, **documentation-only** ("the codegen only consults it for transcription-typo detection") — it is *not* emitted as a closed type the way `actions.yaml` now is.

**Spec-derived / declarative?** Yes — these are closed vocabularies, exactly like the verbs. The plan's SP-010 entry explicitly names them as "natural follow-on closed types once the verb pattern is proven," and OQ-008 names "action verbs need a stable catalog" as a blocker for declaring the spec canonical — guard atoms and events are the same blocker.

**The bug evidence is overwhelming and live.** Read `Ax25SessionBindings.ts` and `GuardEvaluator.cs`: both are riddled with *dual-spelling bindings* because the codegen emits a guard atom in one spelling and the runtime registered it under another. The TS file binds `srej_enabled` **and** `SREJ_enabled`, `rc_eq_0` **and** `RC_eq_0`, `v_s_eq_x` **and** `vs_eq_X`, `vr_i_frame_stored` **and** `vr_I_frame_stored`, with multi-line comments explaining that a missing spelling makes the subroutine walker "silently no-op" (ax25-ts#12) or "throw mid-transition." The C# side carries an entire `PredicateAliases` dictionary (20+ entries) doing the same reconciliation. **This is the verb bug class (#258/#263) replicated in the guard layer, and it is being patched by hand, per language, right now.** A generated closed guard-atom set with codegen as sole canonicaliser deletes both the dual-bindings and the `PredicateAliases` map — exactly as SP-010 deleted `ActionVerbAliases`.

**Codegen sketch.** Promote `events.yaml` from doc-only to an emitted closed type (C# enum `Ax25Event` / TS union), mirroring `EmitActionVerbEnum`. Add a `guards.yaml` (or reuse `actions.yaml`'s pattern) cataloguing every guard atom with its canonical spelling + figure-verbatim aliases, and emit a closed `Ax25GuardAtom` type. The transitions' `On`/`Guard` fields then carry typed values; the dispatchers/binding-tables switch over the closed type; a new/renamed atom is a compile error in C#+TS, not an on-air no-op.

**Value.** High leverage for low effort — it's the proven SP-010 machinery applied to two more vocabularies, killing the *same* recurring bug class one layer down. It is the obvious next increment.

**Effort / risk.** Low (events) to low–medium (guard atoms, because the alias reconciliation has to move from runtime into the catalog — but that's a one-time consolidation of knowledge that already exists in `PredicateAliases` + the TS dual-bindings). Like SP-010, it spans 3 repos + an `ax25sdl` release, mostly mechanical.

### E2 — A generated typed predicate AST/evaluator (real, but lower priority)

**What it is.** Today `GuardEvaluator` (both languages, line-for-line) parses the compound guard string at runtime: `expr := term ("or" term)*; term := factor ("and" factor)*; factor := "not"? identifier`, split on whitespace, no parens. It works because, as both files note, "every guard observed in the spec is a simple conjunction of negated/unnegated flags, occasionally an or." OQ-008 flags this as a blocker: "the guard mini-DSL needs a real grammar (today GuardEvaluator parses by ad-hoc string splitting — fine for one consumer, not for many)."

**Spec-derived / declarative?** The *grammar* is fixed; the *atoms* are the E1 closed set. So codegen could parse each guard expression **at codegen time** into a typed AST (`And`/`Or`/`Not`/`Atom(Ax25GuardAtom)`) and emit that AST as data in the tables — eliminating runtime string parsing entirely. The runtime then needs only a trivial AST-walker over the closed atom set (a `match`/`switch`), not a parser. This is strictly better for embedded (no string tokeniser on an M0+, no string compares in the hot path — the Pico research flagged exactly this) and removes the second place (after verbs) where the runtime re-implements a parser.

**Codegen sketch.** Extend the IR: `ResolvedTransition.Guard` becomes a `ResolvedGuardExpr` tree (parsed once, in the resolver, from the same grammar the evaluators use). Emitters render it as nested constructors / tagged-union data: C# records, Rust enums, TS discriminated unions, C tagged structs, a JSON tree. The hand-written `GuardEvaluator` parser **deletes**; a ~20-line typed walker replaces it per language (and that walker is itself a candidate to generate, since it's a fold over a closed shape).

**Value.** Medium. Correctness/maintenance win (one grammar, parsed once, in the canonical repo) + an embedded win (no runtime parsing). But the current string-splitter is *not currently a bug source* the way the spelling drift is — it's a latent fragility OQ-008 wants fixed before declaring the spec canonical, not a live on-air failure. So it's "right, eventually," not "urgent."

**Effort / risk.** Medium. It's an IR + emitter change touching all backends, and the AST shape must round-trip through JSON cleanly. Risk: low semantically (the grammar is tiny and closed) but it's more emitter surface than E1. **Sequence it after E1** (you need the closed atom set first) and after the codec work has proven the IR can carry richer structure.

**What stays hand-written (E1+E2).** The *binding* of an atom to live state (`own_receiver_busy` → `ctx.ownReceiverBusy`) — that's the effecting behaviour. Codegen supplies the closed atom set and the parsed expression; the runtime supplies what each atom *reads*.

---

## 8. Candidate F — Cross-language conformance vectors (the safety net; do first)

**What it is.** A language-neutral corpus of input→expected-output cases, checked into `ax25sdl` (or a shared data package), that **every** backend executes through its own runtime:

- **Frame vectors:** `{bytes ⇄ {kind, addrs, N(S), N(R), P/F, pid, info}}` encode/decode pairs across mod-8/mod-128, every frame type, command/response, digipeated, edge widths.
- **XID vectors:** `{info-bytes ⇄ XidParameters}` round-trips, including the documented-anomaly cases and the strict-reject/lenient-accept pairs; reverts-to merge cases `{offered, response → agreed}`.
- **Segmenter vectors:** `{payload, N1 → segment[]}` and reassembly.
- **FSM scenario traces:** the existing `tests/conformance/connect-sabm-ua-disc.yaml` *is the strawman* — `(setup, steps[])` where steps post events and assert tx frames / state / timers / upward signals. Extend to cover figc4.7 subroutines, REJ/SREJ recovery, N2 exhaustion, inbound SABM, mod-128, segmentation end-to-end.

**Why this is the highest-leverage item despite generating no code.** It is the **drift-proofing for everything you choose *not* to generate** — and you'll never generate all of it (timers, async, transports, the session lifecycle all stay hand-written forever). Vectors make every hand-written runtime *provably identical on the wire and in behaviour* without unifying the code. This is precisely the **Wycheproof** model: a community repo of JSON test vectors + schema that every crypto library runs against, catching spec-inconsistencies and implementation bugs before they ship; downstream projects "regularly test their code using the vectors as part of their development." AX.25 has the identical shape (multiple independent implementations of one fixed spec) and the identical need.

It's also a **prerequisite for safely doing Candidates B–E**: if you're going to replace a hand-written codec with a generated one, you want a corpus that both the old and new codec must pass, so the swap is provably behaviour-preserving. Vectors first means every later codegen expansion lands on a safety net.

And the repo is *already moving here*: `tests/conformance/README.md` describes "one ~150 LOC executor per runtime" emitting `conformance-<runtime>.json`, aggregated into a CI heatmap; `docs/conformance-harness-plan.md` Phase A3 is "serialise scenarios to language-neutral JSON; run the same suite + invariants in ax25-ts → C#↔TS differential," explicitly calling it "the shared conformance vectors idea." The plan even has the *generated* version (Phase A1–A4: FsCheck generates and shrinks scenarios). So the recommendation is: **pull the hand-authored-vector half of that plan forward and make it the first deliverable**, because it protects every other candidate.

**Codegen angle (optional, powerful).** Beyond hand-authored vectors, `ax25sdl` can *generate* vectors: it already has the resolved tables, so it can enumerate `(state, event, guard) → (next, actions)` expectations directly from the IR for the FSM layer, and (once Candidate B/C land) emit frame/XID vectors from the bit-field specs. Generated vectors are exhaustive and never drift from the spec data. But hand-authored scenario vectors come first (cheap, immediately useful, and they validate the generated ones).

**Value.** Highest overall (drift-proofs the un-generated majority; de-risks all other candidates). **Effort.** Low for the corpus + per-runtime executors (~150 LOC each, per the existing plan); higher only if you build the generator. **Risk.** Low — and the oracle-trust caution from the harness plan (validate the checker on known-green cases first) applies and is already documented.

**What stays hand-written.** The per-runtime *executor* (~150 LOC each) that drives a vector through that language's runtime — but it's written once per language and never re-touched as vectors grow.

---

## 9. Candidate G — Address codec and segmenter (modest; bundle)

**Address codec** (`Ax25Address` / `address.ts`): the 7-octet shifted-ASCII callsign + SSID + C/H/E bits (§3.12, §6.1.2). Declarative bit/byte layout — belongs with Candidate B's `frame.yaml` (the address fields are part of the frame). Same duplication, same Kaitai-shaped story. Bundle into B.

**Segmenter** (`segmenter.ts` / `Segmenter.cs`): Figure 6.2's `F|count(7)` control byte + the split/reassemble loop. The *control byte* is a constant (Candidate A) + a tiny bit-field (Candidate B); the *split/reassemble loop* is simple algorithmic behaviour that's borderline — generable but low-payoff (it's ~80 lines and not bit-twiddly). My lean: **generate the segment-control byte (A/B), keep the loop hand-written**, but cover the whole thing with vectors (F) so the two copies stay identical regardless.

---

## 10. What is *not* worth generating (the hand-written core, stated plainly)

To keep the line sharp, these stay hand-written per language and should **not** be codegen targets — they fail the "declarative data, not behaviour" test:

- **The effecting behaviour of every action** — `ActionDispatcher`'s case *bodies*. The closed verb *set* is generated; what `Set Own Receiver Busy` *does* (`ctx.ownReceiverBusy = true`) is behaviour. (One could imagine a tiny "action effect DSL" — `set_own_receiver_busy: ownReceiverBusy := true` — and for the trivial flag-setters it'd even work; but the moment an action emits a frame, reads the trigger, arms a timer, or mutates a queue with edge cases (the #231 retransmit-renumbering subtlety, the SREJ quirks), it's real code. The flag-setter subset isn't worth a DSL of its own.)
- **Timers and the clock** — T1/T2/T3/TM201 arming, the SRT/T1V smoothing (the only floating-point in the stack), Karn's algorithm. Platform-specific, behavioural.
- **Async/threading and the pump/`Settle()` model.**
- **Byte I/O and transports** — KISS, AXUDP, serial, WebSerial, TCP.
- **The session/listener lifecycle, lookup by `(local, remote, port)`, the link-multiplexer/channel-arbitration model.**
- **Policy** — `able_to_establish` (station acceptance), the strict-vs-pragmatic `ParseOptions`/`SessionQuirks` layers. (The quirks are deliberately *deviations* from the spec figures — by definition not spec-derived.)
- **The FCS/CRC-16 algorithm** (its vectors are generable; the implementation is a standard routine).

---

## 11. Prioritised roadmap (value × feasibility)

Ordering by leverage (drift-proofing + re-port reduction, weighted for the embedded Rust/C consumer the Pico work needs) against feasibility:

**Tier 1 — do now (cheap, high payoff, de-risks everything else):**

1. **Conformance vectors (Candidate F).** Pull the hand-authored half of `conformance-harness-plan.md` Phase A3 forward. Ship the frame/XID/segmenter vector corpus + the FSM scenario library + one ~150-LOC executor per runtime + the CI heatmap. **Rationale:** drift-proofs the hand-written majority that will *never* be generated, and it's the safety net under every codegen expansion below. Wycheproof proves the model. Lowest effort-to-value ratio in the whole list.
2. **SP-010 guard-atom + event closed sets (Candidate E1).** Apply the proven verb machinery to two more vocabularies. **Rationale:** kills a *live, currently-being-hand-patched* bug class (the dual-spelling bindings + `PredicateAliases`), it's mostly mechanical, and OQ-008 names it a blocker for spec canonicalisation. The verb leg already shipped; this is the obvious continuation.
3. **Constants / PID / default-param / frame-base tables (Candidate A).** Trivial emitter, pure data, immediate drift-payoff, gives B/C/F their source values. **Rationale:** cheapest possible codegen win; high interop-safety value (a wrong base = on-air failure).

**Tier 2 — the substantive expansion (high value, medium effort; land on the Tier-1 safety net):**

4. **Frame control-field codec + address codec (Candidates B + G-address).** The biggest duplicated block, most error-prone, most embedded-critical. **Rationale:** collapses N hand-written bit-codecs to one spec file; the Pico node's hot path. Decide the build-vs-Kaitai question here (lean: own the emitter, study `.ksy`). Vectors (Tier 1) make the swap provably safe.
5. **XID TLV codec + reverts-to merge (Candidates C + D).** Rides on B's bit-field machinery; *imminent* re-port avoided (the TS XID leg is unwritten). **Rationale:** most declarative of all, and generating it pre-empts writing the second and third copies.

**Tier 3 — right eventually, not urgent:**

6. **Typed guard-predicate AST/evaluator (Candidate E2).** Parse guards at codegen time into a typed tree; delete the runtime string-parser. **Rationale:** OQ-008's "real grammar" ask + an embedded win, but the string-splitter isn't a *live* bug source today. Needs E1 first.
7. **Segmenter loop (Candidate G-segmenter).** Low payoff; covered by vectors regardless. Generate only if a third runtime makes the re-port annoying.

**Where SP-010 sits:** its verb leg is *done* (the precedent that makes everything above credible); its guard/event legs are **Tier 1 item 2** — the cheapest high-value continuation. SP-010 is the proof that `ax25sdl` can emit closed *types* across languages; Candidates A–D are the proof it can emit closed *data/wire-formats*; Candidate F is the proof that the parts it *can't* emit can still be drift-proofed.

**Is "generate conformance vectors" worth doing early as a safety net? — Yes, unambiguously, and it's item #1.** It's the only item that protects the code you keep hand-writing, it's cheap, the strawman + the plan already exist, and it makes every subsequent codec replacement a provably-behaviour-preserving swap rather than a leap.

---

## 12. Genuine unknowns / things to verify with Tom

- **Build-your-own bit-codec vs adopt Kaitai Struct for the framing layer.** The biggest open design call (Candidate B). Owning it keeps zero runtime deps + full encode/decode symmetry (AX.25 needs strict encode, Kaitai's weaker side); adopting Kaitai gets 11 languages + a maintained compiler but adds a toolchain + per-language runtime dep. I lean own-it; Tom may weigh the maintenance differently.
- **Does the wire layer belong in `ax25sdl` at all, or a sibling?** `ax25sdl`'s CLAUDE.md scopes it as "spec data + codegen, nothing else" for the *SDL*. Adding `frame.yaml`/`xid.yaml`/`constants.yaml` broadens its remit from "SDL state machines" to "the declarative AX.25 spec." That's arguably the right home (it's the same encode-then-verify discipline, same 7-backend pipeline) and OQ-008 already imagines it as "a community-canonical AX.25 v2.2 state-machine artifact" — but it's a scope decision for Tom, possibly tied to the `packethacking/ax25spec` canonical-home conversation.
- **Vector format ownership.** The strawman is YAML in `packet.net/tests/conformance/`. If vectors become the cross-repo safety net, they likely move to `ax25sdl` (or a `packethacking/` data repo) so all three consumers pull one source — same logic as the SDL extraction itself.
- **Type-home unification.** The per-language table type homes (`types.rs`/`ax25sdl.h`/…) are hand-written and synced by hand today. Any wire-layer expansion should fold them into codegen too, or the drift surface just moves.
- **How much of the dispatcher's flag-setter subset is worth an "action-effect" micro-DSL?** I argue no (the non-trivial actions dominate and stay code), but if the embedded port makes the trivial-flag duplication grate, a tiny effect annotation on the pure flag-setters is a possible Tier-3 micro-win. Flagged, not recommended.

---

## Sources / prior art

- **Kaitai Struct** — declarative binary-format DSL compiled to parsers in C++/C#/Go/Java/JS/Lua/Nim/Perl/PHP/Python/Ruby/Rust. The direct prior art for Candidates B/C/G (declarative wire-format → many languages). [kaitai.io](https://kaitai.io/) · [GitHub](https://github.com/kaitai-io/kaitai_struct) · [User Guide](https://doc.kaitai.io/user_guide.html). Caveat: parse-oriented; serialisation/encode support is newer — relevant because AX.25 needs strict encode.
- **Project Wycheproof (C2SP)** — community repo of JSON test vectors + JSON schema that crypto libraries run against to catch spec-inconsistencies and implementation bugs; downstream projects test continuously against the shared corpus. The direct prior art for Candidate F (cross-implementation conformance vectors). [GitHub](https://github.com/C2SP/wycheproof) · [files.md](https://github.com/C2SP/wycheproof/blob/main/doc/files.md) · [Testing Handbook](https://appsec.guide/docs/crypto/wycheproof/).
- **In-repo evidence:**
  - `packet-net/ax25sdl`: `docs/adr/0001-sdl-dsl.md`, `docs/sdl-verb-catalogue.md`, `spec-sdl/actions.yaml` + `events.yaml`, `codegen/src/Packet.Sdl.IR/{Models,Catalogs,Resolved}.cs`, `codegen/src/Packet.Sdl.CodeGen/Program.cs` (verb-enum emit + the runtime-cross-check lints), `spec/csharp/Ax25ActionVerb.g.cs`, `spec/c/src/disconnected.g.c` (data-not-behaviour), the hand-written type homes `spec/{rust/src/types.rs,c/src/ax25sdl.h,go/ax25sdl/types.go,python/ax25sdl/types.py,ts/src/ax25sdl/types.ts}`.
  - `packet-net/packet.net`: `src/Packet.Ax25/Ax25Frame.cs` + `Ax25Frame.Factories.cs` (control-field codec + bases), `src/Packet.Ax25/Xid/*` + `Session/XidNegotiator.cs` (TLV codec + §6.3.2 reverts-to), `src/Packet.Ax25/Session/{GuardEvaluator,Ax25SessionBindings,ActionDispatcher,Segmenter}.cs`, `tests/conformance/{README.md,connect-sabm-ua-disc.yaml}`, `docs/conformance-harness-plan.md`, `docs/plan.md` (SP-010 #260, OQ-008, the V1–V3 arc with the "ax25-ts parity leg pending" re-port markers).
  - `packet-net/ax25-ts`: `src/frame.ts`, `src/xid.ts`, `src/sdl/{guard-evaluator,session-bindings,action-dispatcher,segmenter}.ts` — the line-for-line parallel of the C# above (the duplication this analysis is about).
  - `pico-packet-node.md` — "the work is the runtime, not the tables"; the embedded Rust/C consumer that pays the re-port tax this roadmap targets.
