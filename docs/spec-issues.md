# AX.25 v2.2 spec-issue tracker

A consolidated list of candidate issues with the AX.25 v2.2 specification
(`ax.25.2.2.4_Oct_25`, source: `packethacking/ax25spec`) surfaced during
the figc4.* SDL transcription pass. Each entry is sourced from one or
more `verification_pending` notes in `spec-sdl/data-link/*.sdl.yaml`
plus triangulating evidence from the four pinned reference
implementations.

This document is the **single source of truth** for "what should we
ask upstream about". If a YAML transition has a `verification_pending`
note that isn't represented here, the doc is out of date — please add it.

## How to read an entry

Each issue has:

- **ID** (`SI-NN`) — stable identifier.
- **Affected figure / transitions** — where this surfaces in our YAML.
- **Type** — what kind of issue this is (typo, contradiction, ambiguity,
  internal inconsistency).
- **Description** — what's wrong as drawn.
- **Triangulation** — what at least one reference-implementation author
  said about it (the strongest signal that this is a spec issue and
  not our reading).
- **Status** — `verification-pending` (one observation, ours), or
  `triangulated` (≥2 independent observations).
- **Resolution** — what we did, why.

The verification-pending vs triangulated distinction matters: a
triangulated finding is one we'd be comfortable filing against
`packethacking/ax25spec`. A verification-pending one needs more
corroboration before we'd surface it upstream.

## Categories

| Category | Issues |
|---|---|
| Spec text errors (typos, wording, missing labels) | SI-01, SI-05, SI-13, SI-14 |
| Figure-vs-prose contradiction | SI-08, SI-09 |
| Internal inconsistency across spec revisions (1998/2006/2017) | SI-03, SI-04, SI-10, SI-12, SI-16, SI-17 |
| Semantic ambiguity | SI-02, SI-06, SI-15, SI-11 |
| Spec ignored in practice (deserves a "why?" answer) | SI-07, SI-18, SI-19 |

## Issues

### SI-01 — figc4.2 DL-DATA Request / I-Frame Pops Off Queue: Yes/No labels missing from figure

- **Affected**: `awaiting_connection.sdl.yaml` t13/t14/t15/t16 (data_layer_3_initiated diamond).
- **Type**: spec text error (missing edge labels).
- **Description**: the `Layer 3 Initiated?` diamond between the DL-DATA Request and I-Frame Pops Off Queue columns has no Yes/No labels drawn in the spec PNG. Tom flagged this in the graphml as "assumed; missing from spec".
- **Triangulation**: direwolf author independently flags the same diamond at `src/ax25_link.c:1466-1473`: *"The flow chart shows 'push on I frame queue' if layer 3 initiated is NOT set. This seems backwards but I don't understand enough yet to make a compelling argument that it is wrong. Implemented as in flow chart."* direwolf reads the diamond with the **opposite** Yes/No interpretation vs our transcription. Three independent observations of confusion (Tom, direwolf author, our transcription).
- **Status**: **triangulated**.
- **Resolution**: encoded per Tom's reading (L3-init Yes → push to queue), `verification_pending` note preserved on each of the 4 transitions, direwolf's inverted reading captured in `references[].note`. Upstream worth filing.

### SI-02 — figc4.2 DL-DISCONNECT "requeue" semantics unclear

- **Affected**: `awaiting_connection.sdl.yaml` page-level `save: [DL_DISCONNECT_request]` (figc4.2 column 1 SDL Save shape).
- **Type**: semantic ambiguity.
- **Description**: figc4.2 column 1 uses the SDL Save shape for DL-DISCONNECT request, which means "defer until a new state is reached". The §6 prose doesn't clearly describe this behaviour and the term "requeue" appears nowhere consistent.
- **Triangulation**: three of four reference implementations question or skip the save semantics:
  - **direwolf** (`src/ax25_link.c:1112`): *"Erratum: The protocol spec says 'requeue.' If we put disconnect req back in the queue we will probably get it back again here while still in same state…"*
  - **rax25** (`src/state.rs:1262`): *"1998&2017 bug: It says requeue. What does that even mean? Run this same function again?"*
  - **linbpq**: sets a `DISCPENDING` flag rather than a true save/replay.
  - **linux_oot**: `af_ax25.c:1021` immediately sends DISC(P=1) on `release()` — no save/replay.
- **Status**: **triangulated** (3 of 4 impls flag it).
- **Resolution**: encoded as page-level `save:` directive per the figure shape, but **none of the four implementations actually implement save-and-replay**. The Packet.NET runtime will need an opinion. Upstream worth filing.

### SI-03 — figc4.2 v2006 SABM/SABME swap bug

- **Affected**: `awaiting_connection.sdl.yaml` t23 (SABM-while-Awaiting collision).
- **Type**: internal inconsistency (1998 vs 2006 spec revisions).
- **Description**: the 2006 version of figc4.2 draws SABME on the SABM-collision column (twice — once correctly for SABME, once where SABM should be). The 1998 version is correct.
- **Triangulation**: direwolf author at `src/ax25_link.c:4390-4392`: *"Erratum! 2006 version shows SABME twice for state 1. First one should be SABM in last page of Figure C4.2. Original appears to be correct."* Confirms our 1998 reading is correct.
- **Status**: **triangulated** (direwolf + our transcription agree on 1998 reading).
- **Resolution**: encoded per 1998 reading. No action needed — the 2006 spec is the buggy one.

### SI-04 — figc4.2 t07 UA(F=1) !L3-init V(s)≠V(a): redundant Start T1 / Stop T1

- **Affected**: `awaiting_connection.sdl.yaml` t07 (and the analogous figc4.6 t18 Start T3 twice).
- **Type**: internal inconsistency across spec revisions.
- **Description**: the figure's "V(s) ≠ V(a) recovery" branch starts T1 (via the SRT init prelude) and then immediately stops it as part of the standard housekeeping. Redundant on paper.
- **Triangulation**:
  - **rax25** (`src/state.rs:1226`): *"bug in the 2017 spec...start T1, then immediately stop it again"*.
  - **direwolf** (`ua_frame:4849-4876`): erratum comment that 2006 wording differs from 1998 here.
- **Status**: **triangulated**.
- **Resolution**: encoded verbatim (start T1 followed by stop T1). figc4.6 t18 reproduces the pattern with Start T3 twice. Upstream worth filing — likely a 2017 spec regression.

### SI-05 — figc4.2 DL-ERROR code G typo (case)

- **Affected**: `awaiting_connection.sdl.yaml` t08 (T1 retry exhaustion → DL-ERROR).
- **Type**: spec text error (typo).
- **Description**: 1998 spec writes the error code as lowercase `g` where context implies uppercase `G`.
- **Triangulation**: rax25 author at `src/state.rs:1192`: *"Typo in 1998 spec: G, not g"*. Direct call-out.
- **Status**: **triangulated** (rax25 author + our transcription).
- **Resolution**: encoded as `DL_ERROR_indication_G` (uppercase). Minor typo, but worth fixing upstream.

### SI-06 — figc4.3 t01 DL-DISCONNECT request → "Expedited DM, stay" semantics

- **Affected**: `awaiting_release.sdl.yaml` t01.
- **Type**: semantic ambiguity.
- **Description**: figc4.3 says a second DL-DISCONNECT received while already in AwaitingRelease should respond with an "Expedited DM" and stay. What does "expedited" mean? Why stay rather than ignore?
- **Triangulation**: all four implementations diverge from this and several authors comment on it:
  - **rax25** (`src/state.rs:1329`): *"1998&2017 bug: What's an 'expedited' DM?"* and `:1332`: *"Doesn't specify pf."* rax25 transitions to Disconnected.
  - **direwolf** (`dl_disconnect_request:1147-1148`): similar erratum questioning "expedited" wording. direwolf also transitions to state_0.
  - **linbpq**: never re-enters DL-DISCONNECT codepath in this state.
  - **linux_oot**: `release()` is one-shot from socket layer; no re-entry.
- **Status**: **triangulated** (4-of-4 divergence + 2 explicit author comments).
- **Resolution**: encoded per figure verbatim. Worth surfacing upstream — "Expedited DM, stay" looks implementationally unnatural.

### SI-07 — figc4.3 t02 DL-ERROR(G) on T1 retry exhaustion: nobody implements it

- **Affected**: `awaiting_release.sdl.yaml` t02 (and figc4.6 t19).
- **Type**: spec ignored in practice.
- **Description**: figure mandates DL-ERROR(G) on T1 expiry at RC==N2. No reference implementation emits this typed error.
  - **rax25**: emits `DlError::H` (different letter entirely).
  - **direwolf**: only `dw_printf`'s an untyped error string.
  - **linbpq**: silently `CLEAROUTLINK`s.
  - **linux_oot**: surfaces `ETIMEDOUT` errno.
- **Status**: **triangulated** (4-of-4 divergence).
- **Resolution**: encoded per figure (G). The Packet.NET implementation will be the first to actually surface DL-ERROR(G) as a typed value. Whether to file upstream depends on whether the "G" choice is intentional — possibly worth asking.

### SI-08 — figc4.3 t14 DISC received → UA, stay vs §6.3.4 ¶2 "enter Disconnected"

- **Affected**: `awaiting_release.sdl.yaml` t14.
- **Type**: figure-vs-prose contradiction.
- **Description**: figc4.3 has DISC received → F:=P, Expedited UA, **stay in AwaitingRelease**. But §6.3.4 ¶2 prose says *"After receiving a valid DISC command, the TNC sends a UA response frame and enters the disconnected state"*. Also §6.3.6.2 says when sent and received DISC are the same, "both devices enter the indicated state".
- **Triangulation**: linbpq (`L2LINKACTIVE:1090`) and linux_oot (`ax25_std_state2_machine:111`) follow the prose (UA + Disconnected); direwolf alone (`disc_frame:4544`) follows the figure (UA, stay).
- **Status**: **triangulated** (figure and prose disagree; implementations split 1:2).
- **Resolution**: encoded per figure (Trust-the-Figure). Worth surfacing upstream — the prose should be aligned with the figure (or vice versa).

### SI-09 — figc4.3 t17 UI(P=1) → DM(F=1) contradicts §6.3.5 ¶3

- **Affected**: `awaiting_release.sdl.yaml` t17.
- **Type**: figure-vs-prose contradiction.
- **Description**: figure has UI(P=1) → UI_Check → DM(F=1). But §6.3.5 ¶3 prose explicitly **excludes UI** from the "respond DM" rule: *"Any TNC receiving a command frame other than a SABM(E) or UI frame with the P bit set to '1' responds with a DM frame…"*. The figure violates its own §6.3.5.
- **Triangulation**: linbpq, linux_oot and rax25 all follow the prose (no DM on UI(P=1)); only direwolf (`ui_frame:5115`) follows the figure.
- **Status**: **triangulated** (3-of-4 implementations follow prose; figure stands alone).
- **Resolution**: encoded per figure. Strong candidate for upstream — either the figure is wrong or the §6.3.5 ¶3 exclusion is.

### SI-10 — figc4.3 t15 DM(F=1): 1998 CONNECT vs DISCONNECT, confirm vs indication

- **Affected**: `awaiting_release.sdl.yaml` t15.
- **Type**: internal inconsistency across spec revisions.
- **Description**: direwolf author at `dm_frame:4677` captures the spec history: *"Original flow chart, page 91, shows DL-CONNECT confirm. It should clearly be DISconnect rather than Connect. 2006 has DISCONNECT *Indication*. Should it be indication or confirm? Not sure."*
- **Triangulation**: direwolf author's annotation. Our figc4.3 transcription resolves it as DL-DISCONNECT **confirm** (the connect/disconnect typo corrected, the indication/confirm question answered as "confirm").
- **Status**: **triangulated** (direwolf author flags both halves).
- **Resolution**: encoded as `DL_DISCONNECT_confirm`. Upstream worth raising — at minimum the 1998 CONNECT/DISCONNECT typo, plus the 2006 indication-vs-confirm ambiguity.

### SI-11 — figc4.3 t19 SREJ in the "I, RR, RNR, REJ or SREJ Commands" column?

- **Affected**: `awaiting_release.sdl.yaml` t19/t20.
- **Type**: semantic ambiguity.
- **Description**: the figure's input column enumerates SREJ as a command. SREJ is canonically a response only.
- **Triangulation**: direwolf at `srej_frame:4046`: *"Based on X.25, I don't think SREJ can be a command"*.
- **Status**: **verification-pending** (one author observation).
- **Resolution**: encoded verbatim with composite event `i_or_s_command_received`. May affect downstream FRMR generation when an SREJ command is actually received — flagged.

### SI-12 — figc4.3 t19 1998 vs 2006 figure divergence on I/S response frames

- **Affected**: `awaiting_release.sdl.yaml` t19/t20 (and broader: any state handling I/S responses).
- **Type**: internal inconsistency across spec revisions.
- **Description**: direwolf erratum at `rr_rnr_frame:3537-3541` (duplicated in rej/srej): *"RR, RNR, REJ, SREJ responses would fall under all other primitives. In the original, we simply ignore it and stay in state 2. The 2006 version, page 94, says go into 1 awaiting connection state. That makes no sense to me."*
- **Triangulation**: direwolf author's erratum is the lone observation but it's explicit about which revision differs.
- **Status**: **verification-pending** (one author observation, but cites specific revision divergence).
- **Resolution**: encoded per 1998 reading (silent ignore, stay). Upstream worth surfacing — the 2006 transition to AwaitingConnection looks wrong.

### SI-13 — figc4.4 "Push on I Frame Queue" — word-order uncertainty

- **Affected**: `connected.sdl.yaml` (figc4.4 t18 and others).
- **Type**: spec text wording.
- **Description**: Tom flagged in graphml as "(note: word order?)" — uncertainty about whether the spec phrase is "Push on I Frame Queue" vs "Push I Frame on Queue" vs similar.
- **Triangulation**: none yet — single-observation, low-priority wording question.
- **Status**: **verification-pending** (one observation, minor).
- **Resolution**: canonical verb chosen as `push_on_I_frame_queue`. Worth a spec re-read but not a blocker.

### SI-14 — figc4.6 n8 column label "Info Field Permitted In Frame" (missing "Not")

- **Affected**: `awaiting_v22_connection.sdl.yaml` t09.
- **Type**: spec text error (typo — dropped "Not").
- **Description**: the figc4.6 spec PNG draws this input column as "Info Field Permitted In Frame". But:
  - figc4.3's equivalent column reads "Info Not Permitted In Frame".
  - The downstream action is DL-ERROR(M), which is canonically "info field NOT permitted in frame".
  - "Permitted" makes no sense as an error condition.
- **Triangulation**: Tom confirmed the spec PNG literally has "Permitted" with no "Not" (i.e. it's the spec, not the graphml). Two contextual reads (figc4.3 sibling, DL-ERROR(M) meaning) both point the other way.
- **Status**: **triangulated** (strong contextual evidence; Tom confirmed the spec PNG).
- **Resolution**: encoded with canonical event id `info_not_permitted_in_frame` (action target dictates); figure-verbatim label preserved in transition notes. Strong candidate for upstream — clearly a dropped "Not".

### SI-15 — figc4.6 UI-column `P == 1?` Yes/No swap vs figc4.3 and §6.3.5 ¶3

- **Affected**: `awaiting_v22_connection.sdl.yaml` t11/t12.
- **Type**: spec internal inconsistency (figure-vs-figure + figure-vs-prose).
- **Description**: figc4.6's UI-column `P == 1?` diamond has Yes/No labels **swapped** vs figc4.3 — the figure as drawn says P==1 Yes → no action, P==1 No → DM(F=1). This contradicts:
  - **figc4.3**: P==1 Yes → DM(F=1), P==1 No → stay.
  - **§6.3.5 ¶3**: *"Any TNC receiving a command frame…with the P bit set to '1' responds with a DM frame with the F bit set to '1'."*
- **Triangulation**: Tom confirmed the swap is genuine in the figc4.6 spec PNG.
- **Status**: **triangulated** (figure-vs-figure + figure-vs-prose, Tom-confirmed).
- **Resolution**: encoded verbatim per Trust-the-Figure. Strong candidate for upstream — the figc4.6 figure almost certainly has Yes/No swapped accidentally.

### SI-16 — figc4.6 t18 Start T3 twice — same pattern as figc4.2 SI-04

- **Affected**: `awaiting_v22_connection.sdl.yaml` t18.
- **Type**: internal inconsistency (recurrence of SI-04 pattern).
- **Description**: the V(s) ≠ V(a) recovery branch starts T3 (via SRT init prelude) and then starts T3 again in the housekeeping block. Same redundancy as figc4.2 t07's Start T1 / Stop T1.
- **Triangulation**: see SI-04. rax25's "bug in the 2017 spec" comment applies to this pattern too.
- **Status**: **triangulated** (SI-04 evidence applies).
- **Resolution**: encoded verbatim. Same upstream fix as SI-04.

### SI-17 — figc4.6 t21 FRMR sets `layer_3_initiated`; figc4.2 clears it on the same condition

- **Affected**: `awaiting_v22_connection.sdl.yaml` t21 (figure-FRMR path forces Set Version 2.0 + Establish Data Link + Set Layer 3 Initiated).
- **Type**: internal inconsistency between figures.
- **Description**: figc4.6's FRMR handler explicitly sets `layer_3_initiated`. Yet state 1 (figc4.2's AwaitingConnection) treats `layer_3_initiated` as a flag that gets cleared on certain entries.
- **Triangulation**: direwolf author at `frmr_frame:5047`: *"State 1 clears it. State 5 sets it. Why not the same?"*
- **Status**: **triangulated** (direwolf author flags explicitly).
- **Resolution**: encoded per figure. Worth raising — different states' treatment of the same flag is suspicious.

### SI-18 — Only direwolf implements a distinct v2.2 Awaiting Connection state

- **Affected**: `awaiting_v22_connection.sdl.yaml` page-wide.
- **Type**: spec ignored in practice.
- **Description**: the entire figc4.6 page describes a state that 3 of 4 reference implementations don't have:
  - **linbpq**: rejects SABME globally at `L2Code.c:687-693` (*"Although some say V2.2 requires SABME I don't agree! Reject until we support Mod 128"*).
  - **rax25**: conflates v2.0+v2.2 awaiting; author TODO at `src/state.rs:1256`: *"This is supposed to transition to 'awaiting connect 2.2'."*
  - **linux_oot**: fuses state 1 and state 5 in `ax25_std_state1_machine`, distinguishing only modulus/window on SABM vs SABME.
  - **direwolf**: has full `state_5_awaiting_v22_connection`.
- **Status**: **triangulated** (3-of-4 omission with explicit author admission in rax25).
- **Resolution**: encoded per figure. This is a "the spec is right, the implementations are lazy" finding rather than a spec bug. But it means **deploying a fully spec-compliant Packet.NET will likely encounter interop issues with peers that don't honour SABME at all**.

### SI-19 — Only direwolf fires MDL-NEGOTIATE Request on v2.2 establish

- **Affected**: `awaiting_v22_connection.sdl.yaml` t16/t17/t18 (UA(F=1)→Connected paths).
- **Type**: spec ignored in practice.
- **Description**: figc4.6 mandates MDL-NEGOTIATE Request on entering Connected from the v2.2 awaiting state. Only direwolf actually fires it (`ua_frame:4910-4912`), and direwolf's own erratum notes that *the figure itself omits the action*.
- **Triangulation**: direwolf is sole implementer. rax25/linux/linbpq have no concept of MDL-NEGOTIATE at all.
- **Status**: **verification-pending** (only one implementation observation, but direwolf's "figure omits this" comment is interesting — possibly the figure was incomplete and direwolf fixed it).
- **Resolution**: encoded per figure (MDL-NEGOTIATE fires). Worth asking upstream whether MDL-NEGOTIATE on v2.2 establish is intended.

## Filing notes

Before filing any of these against `packethacking/ax25spec`:

1. Per CLAUDE.md: *"Don't file upstream issues against `packethacking/ax25spec` without an explicit ask from Tom."*
2. The `triangulated` issues are the strong-evidence ones; `verification-pending` issues should accumulate more corroboration before going upstream.
3. SI-14 (the dropped "Not") and SI-15 (Yes/No swap) are the most defensible candidates — both are figure typos with multiple contextual cross-checks.
4. SI-08 and SI-09 (figure-vs-prose contradictions) are also strong because both halves are spec text — the contradiction is intrinsic.

## Adding new issues

When transcribing a future figure, every `verification_pending` note in the
YAML should land here too. Conventions:

- Cross-link in both directions: the YAML's `notes:` should reference the
  SI-NN id, and this doc should cite the YAML's transition id.
- New IDs are assigned monotonically — don't reuse retired ones.
- Status starts at `verification-pending` and gets upgraded to
  `triangulated` when a second independent observation lands (whether from
  another reference implementation, another figure, or external review).
