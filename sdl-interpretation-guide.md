# How to Read the SDL Diagrams in AX.25 v2.2 Revision 4

This document distils every instruction, convention, and semantic cue that the AX.25 v2.2.4 specification gives — explicitly or by consistent use across the appendices — for interpreting the SDL (System Description Language) diagrams in Appendices C1 through C6.

Source: `ax.25.2.2.4_Oct_25.md`, §2.9, Appendix C1, and the conventions actually exhibited by Appendices C2a, C2b, C3, C4, C5, C6.

The spec is explicit about the authority of these diagrams: **"The SDL takes precedence over the text of this document and should be used to resolve any apparent discrepancies between the two. The SDL is a much clearer description of the protocol than the verbal text."** (§Preface.) Anything in this guide is a guide to reading the figures; the figures themselves are the protocol.

> **Audit note (2026-05-21).** The first version of this guide followed §C1.2 verbatim, but several of §C1.2's claims about *direction* are contradicted by the actual figures and by the encoded transcriptions in [`m0lte/ax25sdl`](https://github.com/m0lte/ax25sdl)'s GraphML/YAML. Where §C1.2 and the figures disagree, the figures win (per the §Preface quote above). Sections affected: §2.2 (input direction is **not** load-bearing; the spec text's claim that left-notch=upper, right-notch=lower is reversed in the data-link figures), §2.2 (timer expiries use the right-notch shape, not the left-notch shape §C1.2 implies), and §2.4 (internal queue ops use their own dedicated shape classes, not relabelled input/output shapes). The corrected behaviour is what's encoded in the project's GraphML palette and what its YAML DSL preserves. See §11 below for the corrected mapping and the project's "trust the figure" rule.

---

## 1. What the SDL is

The SDL describes each AX.25 component as an **Extended Finite State Machine (EFSM)** (§C1.1). It models one party on a communications channel. The machine has:

- **States** — resting conditions where it waits for input.
- **Input signals (primitives)** — events that wake it up (from other layers, from peer entities, or from timers).
- **Atomic action sequences** — when triggered, the machine executes a series of operations to completion without interruption, ending in some state (possibly the same one).
- **Internal variables** — flags, sequence counters, lists.
- **Timers** — settable, stoppable; expiry generates an input signal.
- **Internal queues** — to defer processing of input signals or other items.

Key consequence for diagram reading: **the entire chain of symbols beneath a state, ending at the next state symbol or return, is one atomic unit.** Nothing can interleave. New events arriving mid-sequence do not begin processing until the machine reaches a resting state.

Diagrams are read **top-to-bottom**. All paths leaving a state symbol flow downward from it (§C1.2 explicit).

---

## 2. Symbol catalogue

Figure C1.1 defines the symbols. The descriptions below are taken from §C1.2 verbatim where wording matters, and from how each symbol is used in subsequent appendices.

### 2.1 State symbol

- A labelled box (in the figure, drawn with rounded/elongated ends to distinguish from a process box).
- Carries **two labels**: a numeric **sequence number** and a **state name** (e.g. "0 — Disconnected", "3 — Connected", "4 — Timer Recovery").
- The sequence number is purely the order in which states are drawn — it carries no semantics beyond identification. Every error-code description and every cross-reference in the spec uses the state **number** (e.g. error code E: "DM received in states 3, 4 or 5"). So when narrative text says "state 4," it means the state labelled with `4` in the relevant diagram.
- All sequences of operations from a given state **originate below** the state symbol.
- Each state machine has a **summary page** listing every state name and number. Use the summary page when a state symbol elsewhere in the diagram only carries a number.

**Worked example.** In Appendix C4 (Data Link), the summary lists:

```
0 — Disconnected
1 — Awaiting Connection
2 — Awaiting Release
3 — Connected
4 — Timer Recovery
5 — Awaiting V2.2 Connection
```

So a diagram that ends in a box labelled `3` means "return to the Connected state."

### 2.2 Input signal (primitive) symbol — incoming event

- A flag-shaped box with a **notch on one of its left/right edges**.
- The name of the input event (primitive, frame, timer expiry, or queue pop) is written inside the symbol.
- **The notch direction is NOT load-bearing in the AX.25 figures.** §C1.2 of the spec text describes a convention (left-notch=upper, right-notch=lower), but the actual figures — and therefore the GraphML transcriptions in `m0lte/ax25sdl` — apply the convention inconsistently or in reverse. See §11.1 for the worked evidence.
- **Identify the event by its text label, not by the notch direction.** The d5 shape-class attribute in the GraphML (which copies the figc1.1 palette name verbatim) is used as a label *namespace* — e.g. when the same label "All Other Primitives" appears under two different shape classes in one state, the project disambiguates them as `..._from_lower_layer` / `..._from_upper_layer`, but those suffixes are about d5 provenance, not about which OSI layer the signal really came from.
- **Timer expirations are drawn as the *right-notch* input shape** (d5 = `Signal reception from upper layer`) throughout the v2.2.4 data-link figures — not the left-notch shape §C1.2 implies. Inside the symbol is `Timer T1 Expiry`, `Timer T3 Expiry`, etc.

The machine summary page lists every input primitive **by name and source**. Use the summary plus the protocol context, not the notch direction, to decide where an input actually originates.

### 2.3 Output signal (primitive) symbol — outgoing event

- A flag-shaped box with a **pointer (arrow) on one of its left/right edges**. The pointer is the side toward which the signal departs.
- **Pointer on the left → output to a higher or equal layer.**
- **Pointer on the right → output to a lower layer.**
- The name of the primitive is written inside the symbol.

The machine summary lists every output primitive **by name and destination**.

### 2.4 Internal signal symbol — queue operation

Internal queues are explicit, named, and visualised in the diagrams. Two **dedicated** shape classes exist for them, with their own custom SVG drawings — they are *not* relabelled input/output shapes:

- **`Internal Signal Generation`** — posting an item onto a queue. Drawn as a flag-shaped box with a notched-out tab on each side and a vertical stripe down the centre (custom Inkscape-rendered shape). Used for `Push Frame on Queue`, `Push on I Frame Queue`, `Push Old I Frame N(r) on Queue`, etc.
- **`Internal Signal Reception`** — triggering on an item arriving on a queue. Drawn as a similar but mirrored shape. Used for `I Frame Pops Off Queue` etc.

The label identifies the queue (`I Frame Queue`, `Priority Queue`, `Normal Queue`, `Current Queue`, `Served Queue`, `Awaiting Queue`) and the operation.

**Transcription caveat.** In practice the v2.2.4 figures sometimes draw a queue-pop using the **`Signal reception from Lower Layer`** shape rather than the dedicated `Internal Signal Reception` shape. For example, `I Frame Pops Off Queue` is drawn with `Signal reception from Lower Layer` in the AwaitingConnection / AwaitingV22Connection / Connected pages of figc4.x but with `Internal Signal Reception` in the TimerRecovery page. Both encodings are legitimate transcriptions of what the figure draws — the label is what identifies the queue op. The project's events catalogue treats them as the same logical event.

The machine summary lists every internal queue and its purpose. All queues in this spec are explicitly **first-in, first-out** (stated for each machine in §C2a.3, §C2b.3, §C3.3, §C4.3).

### 2.5 Save symbol

- A flag-shaped box (a distinct shape — typically drawn as an inverted notch / "save" pocket) labelled with an input event name.
- **Meaning:** the named input does *not* cause processing in the present state. It is held aside and re-presented when the machine enters a different state.
- This is the SDL way of saying "ignore this for now but don't lose it" — distinct from explicitly discarding the event.

**Worked example.** In the Awaiting Connection state (C4.2), if a higher-layer DL-DATA Request arrives before the SABM/UA exchange completes, that request can be SAVE'd so it is processed once the machine moves to the Connected state.

### 2.6 Processing description symbol

- A plain rectangle (no notches, no pointers, no rounded ends).
- Contains text describing **internal actions**: starting/stopping timers, setting/clearing flags, computing or assigning values to variables, modifying sequence counters, etc.
- Examples from the diagrams (paraphrased): "T1 := T1 \* 2", "Stop T3", "Layer 3 Initiated := TRUE", "V(s) := V(a)", "RC := 0", "Discard frame".

Multiple processing actions can be listed in one rectangle (semicolon- or newline-separated) and are executed in textual order. Because the entire path between states is atomic (§C1.1), the order within a processing block is purely a presentation convenience.

### 2.7 Test (decision) symbol

- A diamond (or hexagon — visually pointed, distinct from the rectangle).
- The text inside is **phrased as a question**.
- Branch labels leave the symbol on its sides/below.

**The text inside a test is always a question, not an assertion.** Read it as such. Example questions seen in the diagrams: "F = 1?", "Layer 3 Initiated?", "V(s) = N(r)?", "Peer Receiver Busy?", "P/F = 1?", "Length of I frame > N1?". The branches are typically labelled `Yes`/`No` (or with specific values such as `0`, `1`, comparison results, frame types like `RR/RNR/REJ/SREJ`).

A test symbol may have more than two outgoing branches when the question is a multi-way one (e.g. branching on which S-frame type was received).

### 2.8 Subroutine call symbol

- A rectangle with **double vertical bars on each side** (so it visually reads as `||name||`).
- The name of the subroutine appears inside.
- It is a call to a reusable sequence defined elsewhere in the same appendix.

**Constraints on subroutines (explicitly stated in §C1.2):**

- "Subroutines are not permitted to contain states." A subroutine is a straight-line operation block — it cannot wait, it cannot rest, it cannot defer to a future event.
- "Each subroutine has a single point of return." Subroutines are not allowed to branch into different return legs. Inside a subroutine you may see tests, processing, primitives, queue ops — but exactly one return symbol terminates it.

### 2.9 Subroutine start symbol

- A horizontal bar / "start of subroutine" marker carrying the subroutine name.
- All subroutine expansions begin with this symbol, then flow down the page in the usual way.
- The subroutine expansions are listed at the end of the relevant SDL machine description (each appendix has a "Subroutines" figure — e.g. `Figure C2a.s`, `Figure C3.4`, `Figure C4.7a/b`, `Figure C5.3..C5.8`).

### 2.10 Return-from-subroutine symbol

- A horizontal bar terminating a subroutine's flow.
- Returns control to the caller (the subroutine call symbol in the calling diagram).
- A subroutine has exactly one return — there is no per-branch return.

---

## 3. Reading the flow

### 3.1 Top-to-bottom, single sequence

A diagram for a given state shows the state symbol at the top, then the events that can occur in it, then the operation chain that each event triggers, ending in a state symbol (the next state, possibly the same one).

Sequences read down the page (§C1.2 verbatim).

### 3.2 Atomicity

Every chain from "input symbol" down to "next state symbol" is atomic. Two consequences:

1. **You may not interleave another event mid-chain.** If another input arrives during execution, it is queued until the machine reaches the next state.
2. **The order of processing actions within a chain is the order in which they execute** — but because nothing else can observe an intermediate state, you cannot use the atomic block to model concurrency.

### 3.3 Each state's diagram is one page or one figure group

States with too many events to fit on a single page span multiple figures (e.g. C4.4a / C4.4b / C4.4c are all the Connected state continued; C4.5a..e are Timer Recovery continued). The continuation pages have the same state symbol at the top and additional input handlers below; reading them is equivalent to reading one very long figure.

### 3.4 Edges (flow lines)

- Plain downward arrows connect one symbol's exit to the next symbol's entry.
- Labels on edges only appear at test-symbol branches (e.g. `Yes`/`No`, or the value being tested).
- An edge leaving a state symbol (always downward) represents the start of an event-handler chain.
- An edge entering a state symbol represents "the machine has now reached this state" (after processing).

### 3.5 Cross-references between figures

Because states are numbered, a diagram may end with a state symbol containing only a number (with no descriptive name) — e.g. a chain that ends "return to state 3." To resolve this, consult the machine summary page (each appendix has one). State references in the running text always use the number.

---

## 4. Semantics encoded in labels

The way text inside a symbol is written carries protocol semantics. This section captures every textual convention exhibited.

### 4.1 Primitive name syntax (§5 of the spec)

> *"The general syntax of a primitive is formed by a 2-letter indicator of the level, a hyphen, a label that indicates the function, and a type (Request, Indication or Confirm)."*

So a primitive label reads `XX-FUNCTION Type` where:

- **`XX`** is the 2-letter level indicator:
  - **`DL`** — between Layer 3 and the data-link layer
  - **`LM`** — between the data-link layer and the link multiplexer
  - **`PH`** — between the link multiplexer and the physical layer
  - **`MDL`** — between Layer 3 and the layer management
  - **`HW`** — hardware interface to layer 1 (3-letter exception)
- **`FUNCTION`** is the action (`CONNECT`, `DISCONNECT`, `DATA`, `UNIT-DATA`, `ERROR`, `FLOW-OFF`, `FLOW-ON`, `SEIZE`, `RELEASE`, `EXPEDITED-DATA`, `BUSY`, `QUIET`, `TON`, `TOFF`, `AOS`, `LOS`, `NEGOTIATE`, `DATA`).
- **`Type`** is one of `Request`, `Indication`, `Indicate`, `Confirm` (used interchangeably with `Indication`; the spec uses both forms).

The four primitive **types** are spec-defined (§2.3):

- **Request** — higher layer asks lower layer for a service.
- **Indication** — lower layer notifies higher layer of an event.
- **Response** — higher layer acknowledges an Indication. **AX.25 does not use Response primitives.** A confirming frame (e.g. UA) is sent on the wire instead.
- **Confirm** — lower layer tells higher layer the requested activity is done.

When you see `DL-CONNECT Request` in an input symbol with a left notch in the Data Link diagram, that means: "from above, Layer 3 has asked us to set up an AX.25 connection." When you see `LM-DATA Indication` in an input symbol with a right notch, that means: "from below, the link multiplexer has handed us a received frame."

### 4.2 Timer label syntax

Timers appear in two SDL contexts: as input symbols (timer expiry) and in processing rectangles (start/stop/restart). Convention from §C1.2:

> *"All timers are numbered, by convention, with indications beginning 'T' and then (usually) a three-digit number. The 'hundreds' digit indicates the layer number of the Open Systems Interconnection (OSI) model at which the state machine resides; e.g., T1xx timers are physical layer, T2xx timers are data-link layer, etc. However, to prevent confusion, the present indicators T1 and T3 are used for AX.25 timers."*

So the rules — partially regular, partially legacy — are:

- **`T1`, `T3`** — legacy AX.25 Data-Link timers (kept for historical compatibility with v2.0 documentation; they would otherwise be T2xx).
  - `T1` — Outstanding I frame or P-bit.
  - `T3` — Idle supervision (keep-alive).
- **`T1xx`** — Physical-layer timers. Concrete list from §C2a.3:
  - `T100` Repeater Hang (AXHANG), `T101` Priority Window (PRIACK), `T102` Slot Time (p-persistence), `T103` Transmitter Startup (TXDELAY), `T104` Repeater Startup (AXDELAY), `T105` Remote Receiver Sync, `T106` Ten-Minute Transmission Limit, `T107` Anti-Hogging Limit, `T108` Receiver Startup.
- **`TM2xx`** — Management Data-Link timers (`M` for Management, `2` for layer 2). E.g. `TM201` Retry timer for management.
- **`TR2xx`** — Reassembler timers (`R` for Reassembler, `2` for layer 2). E.g. `TR210` Time limit for receipt of next segment.

The single-letter prefix between `T` and the digits (`M`, `R`, …) is the disambiguator when multiple state machines live at the same OSI layer.

A timer expiry symbol in a diagram is drawn as a **left-notch input** containing just the timer identifier (e.g. `T1`, not `T1 expired` — the symbol shape itself means "expiry").

### 4.3 Internal queue label syntax

Internal-signal symbols name the queue and the item. Conventions exhibited:

- Queue names are written in title case ("Awaiting Queue", "Current Queue", "Served Queue", "I Frame Queue", "Priority Queue", "Normal Queue").
- The label distinguishes posting (`queue ← item`) from popping (`item ← queue`) via the shape (left-pointer vs left-notch) — there is no need for arrows in the label.
- Items are described by what they represent at the point of posting (e.g. "I frame", "Seize Request", "primitive from DL machine").

### 4.4 Variable assignments in processing rectangles

Conventions from the data-link diagrams:

- `:=` is assignment (e.g. `V(s) := V(a)`, `RC := 0`, `T1V := SRT × 2`).
- Variable identifiers in parentheses are sequence counters defined by HDLC/AX.25 lineage: `V(s)` send state, `V(r)` receive state, `V(a)` last acknowledged. They are not SDL constructs but AX.25 protocol variables — the SDL just references them by their conventional names.
- Flag identifiers are written as title-case English phrases (e.g. `Layer 3 Initiated`, `Peer Receiver Busy`, `Own Receiver Busy`, `Reject Exception`, `Selective Reject Exception`, `Acknowledge Pending`). Setting/clearing reads as `Layer 3 Initiated := TRUE` or "Set Layer 3 Initiated".
- Numeric parameters (`N1`, `N2`, `SRT`, `T1V`, `p`) are bare letters or short identifiers, never quoted.

### 4.5 Test-symbol questions

Test text is a question, with the question mark omitted in some hand-drawn figures but always parsed as one. Recognisable forms:

- **Single-bit tests:** `F = 1`, `P = 1`, `P/F = 1`. Compare against `0` or `1`.
- **Counter tests:** `V(s) = N(r)`, `RC = N2`, `N(s) = V(r)`.
- **Flag tests:** `Peer Receiver Busy?`, `Layer 3 Initiated?`. Branches are `Yes`/`No`.
- **Window tests:** `N(s) in window?`, `V(a) ≤ N(r) ≤ V(s)?`.
- **Length tests:** `Length > N1?`, `I frame too long?`.
- **Frame-type tests:** `Frame type?` with branches labelled `I / RR / RNR / REJ / SREJ / SABM / SABME / DISC / UA / DM / FRMR / UI / XID / TEST`.

The branches are labelled with the value(s) that select that arm. A test may have more than two arms.

### 4.6 Error-code outputs

`DL-ERROR Indication` output symbols carry a single-letter argument inside or alongside the symbol (e.g. `DL-ERROR(A)`, `DL-ERROR(I)`). The letters reference the error-code list in the machine summary. From Appendix C4:

```
A — F=1 received but P=1 not outstanding.
B — Unexpected DM with F=1 in states 3, 4 or 5.
C — Unexpected UA in states 3, 4 or 5.
D — UA received without F=1 when SABM or DISC was sent P=1.
E — DM received in states 3, 4 or 5.
F — Data link reset; i.e., SABM received in state 3, 4 or 5.
G — Connection timed out.
H — Connection timed out while disconnecting.
I — N2 timeouts: unacknowledged data.
J — N(r) sequence error.
K — Unexpected frame received.
L — Control field invalid or not implemented.
M — Information field was received in a U- or S-type frame.
N — Length of frame incorrect for frame type.
O — I frame exceeded maximum allowed length.
P — N(s) out of the window.
Q — UI response received, or UI command with P=1 received.
R — UI frame exceeded maximum allowed length.
S — I response received.
T — N2 timeouts: no response to enquiry.
U — N2 timeouts: extended peer busy condition.
V — No DL machines available to establish connection.
```

C5 (Management Data Link) has its own A–D code list. C6 (Reassembler) has Y, Z. Always read the **summary page of the appendix you are in** for the meaning.

### 4.7 Subroutine names

Subroutine names are written as short English phrases in title case: `Establish Data Link`, `Clear Exception Conditions`, `Transmit Enquiry`, `Enquiry Response`, `Invoke Retransmission`, `Check Need For Response`, `Check I Frame Acknowledged`, `Select T1 Value`, `Set Version 2.0`, `Set Version 2.2`, `Nr Error Recovery`, `Establish Extended Data Link`, etc.

The subroutine names function like protocol verbs — when you see `||Establish Data Link||` inside the Disconnected state diagram, that's the entire SABM-send-and-wait subroutine being invoked as one step.

---

## 5. The machine summary page

Each Appendix Cx machine ends its introductory text with a **summary page** before the figures. The summary always contains, in roughly this order:

1. **Primitives received from each peer/source**, named.
2. **Primitives sent to each destination**, named.
3. **States**, numbered and named.
4. **Error codes**, lettered and described.
5. **Queues**, named and described.
6. **Flags / variables**, named and described.
7. **Timers**, identified (with their indicator) and described.

This summary is **load-bearing for diagram comprehension.** A reader cannot interpret a state symbol containing only `3`, or an error output containing only `(I)`, without it. Whenever you start reading a new appendix, read its summary first.

---

## 6. Worked walkthrough — a typical event chain

To consolidate everything, here is how to read a hypothetical (but representative) chain from C4 (Data Link), expressed in shape-language:

```
[State 3 — Connected]                  ← state symbol, sequence number 3
        │
        ▼
[ DL-DATA Request ]                    ← left-notch input (from segmenter above)
        │
        ▼
[ Length > N1? ]                       ← test diamond, question
   ┌────┴────┐
  Yes        No
   │          │
   ▼          ▼
[DL-ERROR(O)] [ Push I Frame Queue ]   ← output (left-pointer), then internal post
   │          │
   ▼          ▼
[State 3]    [ ||I Frame Pop|| ]       ← subroutine call (frequently used)
              │
              ▼
            [State 3]
```

How to read this:

- The state is `Connected` (number 3).
- A DL-DATA Request from above (left notch) triggers the chain.
- A test asks whether the request's data exceeds the maximum frame length `N1`. If yes, emit a `DL-ERROR` to the higher layer with code `O` ("I frame exceeded maximum allowed length" from the summary) and return to state 3 without enqueuing. If no, post the I frame onto the I Frame Queue and call the `I Frame Pop` subroutine, which expands elsewhere into the LM-DATA Request send path.
- The whole chain is atomic; no other event interleaves.

---

## 7. Diagram-set conventions across appendices

A few conventions are not stated in §C1.2 but are uniformly observed across Appendices C2a–C6 and worth knowing:

- **One figure per state**, with continuation figures (`a`, `b`, `c`, …) where one state's handlers exceed a page. The continuation figures repeat the state header. (E.g. C4.4a/b/c, C4.5a/b/c/d/e, C4.6a/b.)
- **A dedicated subroutines figure at the end of each appendix** (often labelled `.s` or with a high index, e.g. `C2a.s`, `C3.4`, `C4.7a/b`, `C5.3..C5.8`, `C6` integrates them inline).
- **Figure naming pattern:** `figc<machine>.<sequence>` (e.g. `figc4.4a` = Data-Link appendix, fourth state, page a). Within the markdown, the figures are referenced by image file names like `media/figc4.4a.png`.
- **No global numbering of figures across appendices** — each appendix restarts at 1. So "Figure C4.1" is unambiguous; "Figure 1" is not.
- **Figure C1.1 is the legend.** It is the only place the symbol shapes themselves are drawn. Every later figure assumes you have read it.

---

## 8. Quick reference: symbol → meaning

| Shape class (d5 in `m0lte/ax25sdl` GraphML) | Visual cue | Meaning |
|---|---|---|
| `State` | Rounded/elongated box, contains number + name | Resting state; sequence starts beneath it |
| `Signal reception from Lower Layer` | Flag with **notch on left** | An input event. Identify by **label**, not direction. In figc4.x this shape is used for upper-layer DL service primitives (`DL-CONNECT Request`, `DL-DISCONNECT Request`, `DL-UNIT-DATA Request`) and for some queue-pop transcriptions. |
| `Signal reception from upper layer` | Flag with **notch on right** | An input event. Identify by **label**, not direction. In figc4.x this shape is used for peer frames (`SABM`, `UA`, `UI`, `DM`, `DISC`, `I`, `RR`, …), for timer expiries (`Timer T1 Expiry`, `Timer T3 Expiry`), and for several catch-all inputs. |
| `Signal generation to upper layer` | Flag with **pointer on left** | Output to upper layer (DL-* indications/confirms). Direction *is* load-bearing here — encoded as `kind: signal_upper` in the YAML DSL. |
| `Signal generation to lower layer` | Flag with **pointer on right** | Output to lower layer (frames to transmit). Encoded as `kind: signal_lower`. |
| `Internal Signal Generation` | Dedicated custom shape | Post item onto a named internal queue; label = queue + item (`Push Frame on Queue`). |
| `Internal Signal Reception` | Dedicated custom shape | Pop/trigger from a named internal queue (`I Frame Pops Off Queue`). |
| `Save a signal until a new state is reached` | Distinct parallelogram | Defer this input until next state. |
| `Processing description` | Plain rectangle | Internal actions (timers, flags, variables, assignments). |
| `Test or decision` | Diamond | Decision; label is a question; branches labelled with values (typically `Yes`/`No`). |
| `Subroutine call` | Rectangle with **double bars** on both sides | Invoke a named, single-return, state-free subroutine. |
| `Subroutine start` | Horizontal bar at top of a subroutine figure | Subroutine entry, carries name. |
| `Return from Subroutine` | Small circle with crossed lines | The single return point. |

---

## 9. Things the spec is explicit about, and how they constrain implementations

1. **The SDL is normative over the text.** Where they disagree, the SDL wins.
2. **All sequences are atomic.** Implementations may not interrupt an event handler with another event.
3. **Subroutines may not contain states.** Implementing a subroutine as something that can suspend is a spec violation.
4. **Subroutines have exactly one return.** No multi-exit subroutines.
5. **All queues are FIFO.** This is stated for every machine in its §x.3 internal-operation section.
6. **Timer identifiers encode the layer.** `T1xx` is physical; legacy AX.25 uses bare `T1`, `T3` for the data link; `TM` is management; `TR` is reassembler.
7. **The "Indication" type is sometimes spelled "Indicate"** — the spec uses both interchangeably. They mean the same thing in input/output labels.
8. **Response primitives are not used by AX.25.** Any apparent "response" is an outgoing frame on the wire (e.g. UA), not a Response primitive crossing a SAP.
9. **The HW-DATA primitive's Frame argument is the only non-atomic primitive in the system.** §C2a.2/§C2b.2 state this explicitly: HW-DATA "occupies time" so that the 10-minute and anti-hogging timers can fire. Every other primitive is instantaneous in the model. When you see HW-DATA Request in a diagram, you are looking at the one place where SDL atomicity is relaxed by an explicit exception.
10. **Each Data-Link, Management Data-Link, and Segmenter machine is per-link; each Link Multiplexer and Physical machine is per-channel.** Diagrams describe one instance; an implementation with N links has N parallel Data-Link machines. The summary pages and §2.2/2.3 establish this; the SDL diagrams themselves do not show the multiplicity, only the per-instance behaviour.

---

## 11. Correlation with the `m0lte/ax25sdl` GraphML/YAML encoding

The sibling repo `m0lte/ax25sdl` transcribes every figc4.x SDL page into yEd GraphML and then into a YAML DSL that drives codegen. The encoding decisions there are the practical ground truth for "what the figure actually says", and they reveal where §C1.2 of the spec text is unreliable.

### 11.1 d5 is the authoritative shape class

Every node in a `spec-sdl/v2.2-errata/**/*.graphml` file carries `<data key="d5">` whose CDATA text is one of exactly thirteen palette names (defined in `spec-sdl/ax25-sdl-palette.graphml`, where the same field lives at `d6` because the palette adds two extra keys for palette metadata):

| d5 value | Count in v2.2-errata data-link figures |
|---|---|
| `State` | 144 |
| `Processing description` | 174 |
| `Test or decision` | 118 |
| `Signal reception from upper layer` | 81 |
| `Signal generation to upper layer` | 67 |
| `Signal generation to lower layer` | 60 |
| `Subroutine call` | 59 |
| `Signal reception from Lower Layer` | 34 |
| `Subroutine start` | 13 |
| `Return from Subroutine` | 12 |
| `Internal Signal Generation` | 10 |
| `Save a signal until a new state is reached` | 2 |
| `Internal Signal Reception` | 1 |

The d5 text is a verbatim copy of the figc1.1 palette name — it identifies the **shape class** the figure draws, not necessarily the **semantic direction** of the signal. (See §11.2.)

### 11.2 Input shape direction is reversed in the figures

The spec text §C1.2 says: *"inputs with the notch on the left are from higher or equal layer state machines; inputs with the notch on the right are from the lower layer state machine."* The figures in Appendix C4 (and others) do the opposite. Worked evidence from `DataLink_Disconnected.graphml` (figc4.1):

| Node | Label | d5 shape class | Actual origin (per §5) |
|---|---|---|---|
| n1 | `DL-DISCONNECT Request` | `Signal reception from Lower Layer` (left-notch) | Upper layer (Layer 3 → DL) |
| n4 | `DL-UNIT-DATA Request` | `Signal reception from Lower Layer` (left-notch) | Upper layer |
| n7 | `DL-CONNECT Request` | `Signal reception from Lower Layer` (left-notch) | Upper layer |
| n30 | `UA` | `Signal reception from upper layer` (right-notch) | Lower layer (peer frame via LM) |
| n39 | `DISC` | `Signal reception from upper layer` (right-notch) | Lower layer |
| n43 | `SABM` | `Signal reception from upper layer` (right-notch) | Lower layer |

So in practice the d5 string `Signal reception from Lower Layer` is attached to events that come *from upper layer*, and vice versa. The project's policy is to ignore direction and trust the label — the YAML DSL does not preserve the d5 class for events at all (events are identified by name only, via `on:` / `on_label:`), with one exception: when the same label appears under two different d5 classes in one figure, an `__from_<shape-class>` suffix on the event id captures which one. See `events.yaml` `catchalls:` for `all_other_primitives__from_lower_layer` vs `all_other_primitives__from_upper_layer`.

### 11.3 Output shape direction *is* load-bearing

The output side matches §C1.2 and the palette agrees: left-pointer = upper layer, right-pointer = lower layer. The YAML DSL preserves this in the `kind:` field of action steps:

| YAML `kind:` | d5 shape class | Meaning |
|---|---|---|
| `signal_upper` | `Signal generation to upper layer` | DL-* indication/confirm to Layer 3 |
| `signal_lower` | `Signal generation to lower layer` | LM-DATA Request — frame to transmit |
| `processing` | `Processing description` | Internal task (`V(s) := 0`, `Start T1`, …) |
| `subroutine` | `Subroutine call` | Named subroutine reference |
| `internal_out` | `Internal Signal Generation` | Post onto a named internal queue |

The schema is in `spec-sdl/schema/sdl-machine.schema.json`.

### 11.4 Timer expiries

`Timer T1 Expiry`, `Timer T3 Expiry` (and any `T2 Expiry`) are drawn as `Signal reception from upper layer` (right-notch) — not the left-notch shape §C1.2 implies. Verified across `DataLink_AwaitingConnection`, `DataLink_AwaitingRelease`, `DataLink_AwaitingV22Connection`, `DataLink_Connected`, `DataLink_TimerRecovery`.

### 11.5 Catch-all and composite inputs

The data-link figures use input labels that have no analogue in §5:

- `All Other Primitives` — appears in figc4.1 under both the left-notch and right-notch input shapes; the d5 class is the only difference, and the project's events.yaml lists them as two distinct events disambiguated by `__from_<shape-class>` suffix.
- `All Other Commands` — single d5 class, one event.
- `I, RR, RNR, REJ or SREJ Commands` — figc4.3 (AwaitingRelease) bundles five frame types into one input column. Encoded as a single `i_or_s_command_received` event rather than fanned out.
- `Control Field Error`, `Info Not Permitted In Frame`, `U or S Frame Length Error` — input shapes used to trigger error-recovery transitions; the spec text describes the *condition* in §4 but the SDL exposes it as an input event.

Don't try to look these up in §5 — they don't exist there. The figure is the source.

### 11.6 Decision branches are binary in practice

The schema constrains decision branches to `Yes` / `No` (the comment notes: "if a 3-way decision appears in the spec we'll extend this enum"). The data-link figures observed so far comply. Multi-arm tests for things like frame type are typically realised as a separate input shape per frame type, not a multi-way diamond.

### 11.7 Practical reading rule

When you open a figc4.x figure or its GraphML transcription:

1. **Find the state symbol at the top** — that's the state being described.
2. **For each event handler chain, read the input shape's text label first**, ignore the notch direction, and look up what the label means by name in the relevant `§5.x` primitive list or the events.yaml catalogue.
3. **For each output shape, the direction is reliable** — left-pointer goes up, right-pointer goes down.
4. **For each processing rectangle, the text is the action**, executed in textual order within the atomic chain.
5. **For each diamond, the text is a question; outgoing arrows carry the answer values.**
6. **For each subroutine call (double-sidebar rectangle), expand by name** from the subroutines page.
7. **The chain ends at a state symbol** — the machine settles there, ready for the next event.

This is the rule the `m0lte/ax25sdl` codegen pipeline operates by, and it produces transcriptions that successfully drive the C# data-link runtime in `m0lte/packet.net` against real LinBPQ / Xrouter / NinoTNC peers — which is the load-bearing evidence that the rule is right and §C1.2's direction claims are not.
