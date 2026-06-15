# Reconciling the SDL prose (§C1.2), the SDL legend (figc1.1), and the actual figures

When I first wrote [`sdl-interpretation-guide.md`](sdl-interpretation-guide.md) I worked from §C1.2 of the AX.25 v2.2.4 prose alone. When I then audited it against the GraphML/YAML transcriptions in `packet-net/ax25sdl`, I found apparent contradictions and corrected the guide. Both passes were partially right and partially wrong, because the three sources — prose, legend image, and actual state-machine figures — are not internally consistent with each other, and resolving "which is the protocol" requires looking at all three plus the §5 primitive catalogue.

This document walks through each contradiction, presents the visual evidence, and reasons out which source is the authority.

---

## The three sources, and their authority order

1. **§Preface** of the AX.25 spec states: *"The SDL takes precedence over the text of this document and should be used to resolve any apparent discrepancies between the two."* So the figures beat the prose where they disagree.
2. **§5** of the spec defines every primitive's direction (`DL-*` between Layer 3 and the data link, `LM-*` between the data link and the link multiplexer, etc.). §5 is unambiguous prose. It is the ground truth for what "from upper" / "from lower" *means* for any given primitive.
3. **§C1.2** prose describes the SDL symbol conventions.
4. **figc1.1** is the visual SDL legend. It has two layers of content: the shape *drawings* (left-notch vs right-notch SVG geometry) and the *text labels* attached to those drawings ("Signal reception from Lower Layer" etc.). Plus the *example labels* placed inside the shape drawings (`DL-RELEASE Request`, `SABM`, `UI command`, `DL-UNIT-DATA Indication`).
5. **figc4.x** (and friends) — the actual state-machine figures.
6. **`packet-net/ax25sdl` GraphML palette** — a yEd reimplementation of figc1.1's shapes. Each palette node has both an SVG (the shape geometry) and a `d6` text label (copy of figc1.1's text label).
7. **`packet-net/ax25sdl` GraphML figure transcriptions** — a yEd reimplementation of figc4.x. Each node references a palette item by SVG `refid` (preserving the figure's shape geometry) and copies the palette's `d6` text into its own `d5`.

The authority order:

> §5 + figc4.x  ≻  §C1.2 prose  ≻  figc1.1

§5 + figc4.x is the protocol. §C1.2 is a description of the figures, valuable when accurate but subordinate. figc1.1 is a visual aid that, as we'll see, has multiple bugs.

The `ax25sdl` palette is a faithful copy of figc1.1's *bugs and all*; the figure transcriptions are a faithful copy of figc4.x. So d5 strings in the GraphML inherit figc1.1's label bugs, but the SVG geometry inherits figc4.x's correct shapes. **Trust the geometry, not the d5 string.**

---

## Case 1 — input shape direction (left-notch vs right-notch)

### What each source says

- **§C1.2 prose**: *"inputs with the notch on the left are from higher or equal layer state machines; inputs with the notch on the right are from the lower layer state machine."* So: **left=upper, right=lower**.
- **figc1.1 text labels**: the left-notch shape is labelled "Signal reception from Lower Layer"; the right-notch shape is labelled "Signal Reception from Upper Layer". So per the *text*: **left=lower, right=upper** — opposite of the prose.
- **figc1.1 example labels placed inside the shapes**: the left-notch example carries `DL-RELEASE Request`; the right-notch example carries `SABM`. Per §5, `DL-RELEASE Request` is an upper-layer service primitive (`DL-*` between Layer 3 and data link); `SABM` is a frame received from the peer via the link multiplexer (lower layer). So per the *example placement*: **left=upper, right=lower** — matches the prose, contradicts the text label.
- **figc4.1**: `DL-DISCONNECT Request`, `DL-UNIT-DATA Request`, `DL-CONNECT Request` are all drawn with the left-notch shape; `SABM`, `SABME`, `UA`, `UI`, `DISC`, `DM` are all drawn with the right-notch shape. Per §5 these are upper- and lower-layer respectively. So per the *figure*: **left=upper, right=lower** — matches the prose.
- **`ax25sdl` palette**: node `n1` uses the left-notch SVG and is named `Signal reception from Lower Layer`; node `n2` uses the right-notch SVG and is named `Signal reception from upper layer`. This copies figc1.1's *text labels* verbatim — so the palette names are swapped relative to §C1.2 prose and to the figure semantics.
- **`ax25sdl` figure transcriptions**: a node carrying the label `DL-CONNECT Request` uses palette node `n1` (left-notch SVG) and so carries `d5="Signal reception from Lower Layer"`. The SVG geometry is correct (matches figc4.1); the d5 text reads the opposite of the semantic truth.

### Reasoning

Three of the five sources (§C1.2 prose, figc1.1 example *placement*, and figc4.x) all agree: **left-notch = from upper layer, right-notch = from lower layer.** The one outlier is figc1.1's *text labels*, which got swapped — and that swap propagated into the `ax25sdl` palette node names, and from there into every transcribed figure's `d5` string.

So §C1.2 prose is correct on this point. My original guide was right. My "correction" was wrong — I was misled by the d5 strings, which faithfully replicate the legend's mislabel.

### Verdict

**§C1.2 prose wins.** left-notch = upper, right-notch = lower. The d5 strings should be read as palette-node identifiers only — not as accurate descriptions of where the signal comes from.

---

## Case 2 — what shape do timer expiries use?

### What each source says

- **§C1.2 prose**: *"In addition, the left-notch input signal symbol is used for timer expiration."*
- **figc1.1**: no timer-expiry example is shown in the legend — so no direct evidence either way.
- **figc4.4b** (Connected state): `Timer T1 Expiry` and `Timer T3 Expiry` are drawn with the **right-notch** shape — visually identical to the SABM/SABME/UA shapes to their right. Confirmed by direct inspection of the cropped image.
- **`ax25sdl` palette + transcriptions**: every timer-expiry input across the data-link figures uses palette node `n2` (right-notch SVG, `d5="Signal reception from upper layer"`).

### Reasoning

§C1.2 prose says timer expiries are left-notched. figc4.4b and every other state-machine figure draw them right-notched. The figures win per §Preface. The transcription is faithful.

§C1.2 is wrong on this point. There is no plausible reading of figc4.4b that makes the timer-expiry shapes left-notched — they are drawn with the identical right-notch geometry as the peer-frame inputs in the same row.

### Verdict

**The figures win.** Timer expiries are drawn with the right-notch input shape. §C1.2's claim that "the left-notch input signal symbol is used for timer expiration" is an error in the prose.

---

## Case 3 — internal signal shapes (queue post and queue pop)

### What each source says

- **§C1.2 prose**: *"Internal signal symbols are used to post items onto queues (points to left) and to trigger the state machine when something is waiting on the queues to be popped off (notch to left)."* So: **post = left-pointer, pop = left-notch**.
- **figc1.1**:
  - `Push on l frame queue` (Internal Signal Generation) is drawn as a shape with a **left-pointer tab** plus a **vertical separator stripe on the right side**.
  - `I frame pops off queue` (Internal Signal Reception) is drawn as a shape with a **vertical separator stripe on the left side** plus a **right-notch** on the main body.
  
  So per figc1.1: post = left-pointer ✓ matches prose; pop = right-notch ✗ contradicts prose.
- **figc4.x figures**: queue posts (`Push Frame on Queue`, `Push on I Frame Queue`, `Push Old I Frame N(r) on Queue`, …) consistently use the Internal Signal Generation shape — left-pointer + right-separator — matching figc1.1's drawing.
- **figc4.x figures**: queue pops (`I Frame Pops Off Queue`) usually use the **`Signal reception from Lower Layer`** shape (i.e. the left-notch input shape, *without* the separator stripe) — for instance in figc4.2 (Awaiting Connection), figc4.4a (Connected), figc4.6a (Awaiting V2.2 Connection). One figure (figc4.5a, Timer Recovery) uses the dedicated `Internal Signal Reception` shape with the right-notch + left-separator geometry.
- **`ax25sdl` palette**: nodes `n9` (`Internal Signal Reception`) and `n10` (`Internal Signal Generation`) are distinct shape classes with custom Inkscape SVGs, matching figc1.1's drawings.

### Reasoning

Three sub-questions:

**3a. Does a queue post use the same shape as a normal output?** No. figc1.1 draws Internal Signal Generation as a left-pointer **plus a vertical separator stripe on the right**. Normal output shapes have no separator. The palette and figures both reflect this. My original guide called them "identical visually to a left-going output, but the label identifies the queue" — that was overstated. They are distinct shapes that share the left-pointer feature.

**3b. Does a queue pop use the same shape as a normal input?** Inconsistent across the figures. figc1.1 shows the dedicated Internal Signal Reception shape (right-notch + left-separator). Most figc4.x figures use plain left-notch input (Signal reception from Lower Layer) for queue pops; figc4.5a uses the dedicated shape. The transcription captures this inconsistency: most `I Frame Pops Off Queue` nodes carry `d5="Signal reception from Lower Layer"`, but figc4.5a's carries `d5="Internal Signal Reception"`. The protocol behaviour is the same either way.

**3c. Direction of the dedicated internal-signal shapes:** §C1.2 says both have left-pointer/notch. figc1.1 has post = left-pointer (matches prose) but pop = right-notch (contradicts prose).

### Verdict

- **My original guide was wrong** to call internal-signal shapes "identical visually to" the regular input/output shapes. They have an extra vertical-separator stripe.
- **§C1.2 prose is partially wrong** about the direction: it says pop = notch to left, but figc1.1 draws pop = notch to right.
- **My correction was right** to flag dedicated shape classes and the transcription inconsistency.
- The protocol semantics — "post adds to a queue, pop fires on something arriving in the queue" — are uncontested across all sources. Only the visual conventions disagree.

---

## Bonus bug — figc1.1's output example placement

While checking outputs, I noticed figc1.1 also has its **output examples placed on the wrong side**:

- `DL-UNIT-DATA Indication` is drawn with a **right-pointer** ("Signal Generation to Lower Layer"). Per §5, `DL-UNIT-DATA Indication` is "used by the reassembler to indicate reception of Layer 3 data" — it goes to the upper layer (Layer 3). So it should be drawn with a left-pointer, not right.
- `UI command` is drawn with a **left-pointer** ("Signal Generation to Upper Layer"). UI is a frame that gets transmitted on the wire — i.e. goes to the lower layer via LM-DATA Request. So it should be drawn with a right-pointer, not left.

§C1.2 prose (left=upper, right=lower for outputs) is consistent with the figure usage in figc4.x — DM, UA, RR, etc. are all drawn right-pointer in figc4.x and they all go to the lower layer per §5. So the prose and the state-machine figures agree on output direction; figc1.1's *example placement* for outputs is the bug.

That makes figc1.1 buggy in three independent ways:

1. **Input shape text labels are swapped** (left-notch is called "from Lower" when it's "from Upper" everywhere else).
2. **Output example placements are swapped** (`DL-UNIT-DATA Indication` shown right-pointer; `UI command` shown left-pointer).
3. **Internal-signal pop direction** is right-notch where §C1.2 prose calls for left-notch.

The figures themselves are consistent. §C1.2 prose is consistent with the figures except for the timer-expiry shape and internal-pop direction. figc1.1 the legend is the most unreliable of the three sources.

---

## What this means for my original guide

My original guide (written from the prose only) was substantially correct:

| My original claim | Verdict |
|---|---|
| left-notch = upper, right-notch = lower for inputs | ✓ correct |
| Timer expiries use left-notch | ✗ wrong (figures use right-notch) |
| Internal post and pop are "identical visually" to normal output/input | ✗ wrong (extra separator stripe) |
| Output direction left=upper, right=lower | ✓ correct |
| All other shape descriptions, atomic execution, sub-routine rules, primitive-name syntax, timer-naming, error-code letters | ✓ correct |

My audit correction was partially right and partially wrong:

| My correction | Verdict |
|---|---|
| Direction is not load-bearing for inputs; trust the label | ✗ wrong — direction *is* load-bearing in the figures; what's wrong is that d5 strings inherit figc1.1's swapped *text labels* |
| Timer expiries use right-notch | ✓ correct |
| Internal-signal shapes are dedicated palette classes | ✓ correct |
| The figures override §C1.2 in general | ✗ overgeneralised — they override it on timer-expiry shape but not on input direction |

The right framing is: **the figures and §C1.2 prose agree on input/output direction. The figures and §C1.2 prose disagree on timer-expiry shape. figc1.1 is independently buggy in three ways. The d5 strings in the GraphML inherit figc1.1's text-label bug, so they are correct as palette identifiers but misleading if read as semantic descriptions.**

---

## What this means for reading a transcription

When reading a `*.sdl.yaml` or `*.graphml` in `packet-net/ax25sdl`:

1. **Identify events by name, plus the §5 primitive catalogue, plus the shape geometry.** Do not derive layer direction from the d5 string. If a transcribed node has `d5="Signal reception from Lower Layer"` and the label `DL-CONNECT Request`, the node correctly represents an upper-layer input — the d5 string is just a palette identifier inherited from a mislabelled legend.

2. **Trust output `kind: signal_upper` / `signal_lower` in the YAML.** Output direction *is* load-bearing in the figures and is correctly captured by the schema's `kind` field.

3. **Treat the `__from_lower_layer` / `__from_upper_layer` event suffixes as palette-namespace disambiguators**, not semantic-direction claims. When the same input label appears under both palette shape classes in one figure (e.g. `All Other Primitives`), the suffix records which palette item the transcriber picked — that distinction *does* matter for replaying the figure, because two distinct event paths exist, but the suffix names are inherited from the broken legend and should not be taken as layer claims.

4. **Treat the d5 string as a shape-class identifier**, not as a semantic description. The shape-class identity is meaningful (state vs input vs decision vs subroutine vs internal-signal); the layer-direction English in the identifier is residue from a broken legend.

---

## How I'm going to fix the guide

Given the above, my earlier correction to `sdl-interpretation-guide.md` was too aggressive. The guide should:

- Reinstate the original §C1.2-derived statement that input direction is load-bearing (left=upper, right=lower) and that the figures honour this convention.
- Keep the corrected timer-expiry shape (right-notch).
- Keep the corrected internal-signal description (dedicated shapes with separator stripe).
- Replace the broad "figures override the legend" framing with a narrower note that figc1.1 has specific bugs (text-label swap on inputs, example-placement swap on outputs, internal-signal pop direction), and that the ax25sdl palette inherits the input text-label bug, so d5 strings are palette identifiers not semantic claims.

That gives a guide that matches the figures, matches the §5 primitive catalogue, and explains why the GraphML's d5 strings *look* contradictory without making them the authority.
