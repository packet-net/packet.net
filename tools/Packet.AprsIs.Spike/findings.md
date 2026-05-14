# APRS-IS corpus — early findings

## 2026-05-14 (later) — Extended decoder to `@` / `/` timestamped variants

Position decoder now handles all four position DTIs (`!`, `=`, `@`, `/`).
Timestamped variants (`@` and `/`) strip the 7-byte (DHM zulu / DHM local /
HMS) or 8-byte (MDHM) timestamp prefix before delegating to the same
position-decode path.

Re-ran differential over 170,976 corpus rows covering all 4 DTIs:

| Bucket | Count | % |
|---|---:|---:|
| `BothOkMatch` | 113,215 | **66.2%** (was 60.2% with just `!`/`=`) |
| `OnlyUs` | 53,483 | 31.3% (was 37.4% — direwolf accepts more `@` because gateways more often have spec-clean SSIDs) |
| `BothOkMismatch` | 3,016 | 1.8% |
| `BothFailed` | 793 | 0.5% |
| `OnlyDirewolf` | 469 | 0.3% (slightly up — some firmware uses non-spec timestamp terminator bytes like `S` that direwolf accepts and we reject) |

Adding the timestamped variants nets ~6% more match coverage and reveals
that the `BothOkMismatch` direwolf-bug pattern persists across `@` frames
too (e.g. an `@095214h1104.61N/06357.19Wy...` Venezuela frame — comment
mentions QRZ.com Venezuela suffix `YY7ECA` — gets `(11.08, -63.95)` from
us correctly vs `(62.83, -121.66)` from direwolf).

8 new unit tests covering DHM zulu / local / HMS / MDHM timestamp
formats plus negative paths (malformed terminator, non-digit body,
short input).

## 2026-05-14 (afternoon) — Position decoder differential vs direwolf

Stood up `src/Packet.Aprs` with `AprsPositionDecoder.TryDecode` covering the
uncompressed (APRS12c §8) and compressed (§9) variants of DTI `!` / `=`.
Ran `differential` mode over 125,036 corpus lines:

| Bucket | Count | % |
|---|---:|---:|
| `BothOkMatch` (we agree with direwolf within 1e-4°) | 75,316 | 60.2% |
| `OnlyUs` (we decoded; direwolf produced no lat/lon) | 46,724 | 37.4% |
| `BothOkMismatch` (both decoded but disagree) | 2,224 | 1.8% |
| `BothFailed` (both reject) | 602 | 0.5% |
| `OnlyDirewolf` (direwolf decoded; we rejected) | 170 | 0.1% |

### `OnlyUs` (37.4%) — direwolf rejects AX.25 envelope before getting to position

Spot-checking samples: every `OnlyUs` case is direwolf reporting `"Bad source address"`
and refusing to process the frame. The position bytes are well-formed; direwolf
gives up at the callsign-parse stage when it sees letter SSIDs (`-D`, `-B`, etc.)
that APRS-IS routinely leaks through.

Examples from the corpus (info-field bytes, all decoded fine by our payload-level decoder):

| Source | Info-field (truncated) | Why direwolf rejects |
|---|---|---|
| `K0MVH-D` | `!3011.07N\09749.87W&...` | SSID `D` not numeric |
| `ZS6ATZ-D` | `!2536.00SW02812.00Ei...` | SSID `D` |
| `SM6JWU-B` | `!5638.82ND01255.59E&...` | SSID `B` |

**Implication for our library**: payload-layer decoding should be independent of
AX.25 envelope validity — APRS payloads decode reliably even when the surrounding
frame is AX.25-invalid (D-Star / DMR gateways routinely violate). The strict
`Callsign` we use for outbound frame production is correct; for inbound APRS
monitor display, we'll want the permissive `AprsCallsign` type planned in
Tier 1(a) so we can still surface station identifiers from these frames.

### `BothOkMismatch` (1.8%) — most are direwolf bugs

Of 5 hand-checked `BothOkMismatch` examples, **4 show direwolf producing
wildly wrong coordinates** while our decoder produces a position that matches
the frame's comment text:

| Comment text in frame | Our decode | Direwolf decode |
|---|---|---|
| "Ajaccio VHF (145.6375)" | (41.95, 8.70) — Corsica ✓ | (-38.08, 76.55) — southern Indian Ocean |
| "Repeteur 147.255...Node 1453" (French) | (48.74, -69.09) — Quebec ✓ | (-79.63, 132.03) — Antarctica |
| "Free radio repeater 446.225" | (50.43, 30.38) — Kyiv area | (62.66, -125.31) — northern Canada |
| "LoRa APRS at Pir7 Tromsø" | (69.67, 19.02) — Tromsø ✓ | (-77.70, 131.92) — Antarctica |
| "LoRa Tracker !wl]!" | (55.9512, -4.3275) — Glasgow | (55.9513, -4.3276) — same, 11m apart |

The Glasgow row is right at our 1e-4° (~11m) tolerance — borderline floating-point
precision difference, not a real disagreement. The other four are systematic
direwolf bugs. The wrong direwolf coordinates cluster in a recognisable pattern
(latitudes around -77 to 62, longitudes around -125 to 132) suggesting direwolf
may be misreading some bytes outside the position field on certain frame layouts.

**Implication**: our decoder is competitive with — and on these cases, more
accurate than — direwolf. Worth filing the mismatches as upstream
`wb2osz/direwolf` issues once we've nailed down the exact direwolf version
+ reproducer.

### `OnlyDirewolf` (0.1%) — we're over-strict on symbol table identifier

Examples:
- `!5846.46N[01658.39El000/000...` — symbol table `[` (0x5B); we reject, direwolf accepts.
- `!4949.37N#01316.42EIPHG2130 12.1V` — symbol table `#`; we reject, direwolf accepts.

Per APRS12c §20, the symbol table identifier should be `/`, `\`, or an
overlay character from `0–9` / `A–Z`. `[` and `#` are non-spec but accepted
in practice. Loosening our validator to "any printable ASCII" would clear
these but at the cost of accepting some garbage. Trade-off to revisit.

### `BothFailed` (0.5%) — genuinely malformed firmware bugs

- `!335636.00ND1172241.40E&...` — 6-digit lat / 7-digit lon (overshoots format)
- `!1488.93NU09062.91W#` — `90.62` longitude minutes (>59, invalid)
- `!51.2867N,/07.7420E-` — decimal degrees in the lat field (non-standard)
- `!4944.2930N/0920.5371W#` — extra digits in minutes field

Real firmware bugs in the wild. Same flavour as the lowercase-source-address /
exotic-DTI bugs documented in the earlier APRS12c spec cross-reference.

### Bottom line

The `AprsPositionDecoder` v0 works. On the ~125k `!` / `=` frames sampled:

- We agree with direwolf on **>97%** of frames where direwolf produced a
  position (60.2 / (60.2 + 1.8 + 0.1) = 97.0%).
- The few mismatch cases we've examined favour our decoder.
- 37.4% of frames are AX.25-invalid (letter SSIDs) and direwolf rejects them
  outright — our decoder works on those because we look at payload bytes only.

Follow-ups:

1. Add support for `@` / `/` (timestamped positions) — strip the 7-byte
   timestamp prefix before delegating to the same decoder.
2. Decide on symbol-table strictness for production.
3. Investigate the `BothOkMismatch` direwolf bugs in more detail; file
   upstream if reproducible.
4. Implement Tier 1(a) `AprsCallsign` so letter-SSID frames are usable
   end-to-end in the monitor view.

---

First proper analyser pass. Curated narrative kept under source control;
raw stats.md / failures.jsonl land in `artifacts/aprs-is-analysis/<ts>/`
(gitignored).

Re-run with `dotnet run --project tools/Packet.AprsIs.Spike -- analyse`.

## 2026-05-14 — Cross-reference: corpus errors vs APRS12c spec

Each error class surfaced by the direwolf pipeline (previous section) mapped
to the canonical updated APRS specification.

**Sources:**

- `APRS101.pdf` — the original 1998 spec Tom linked. Now explicitly marked
  obsolete by [how.aprs.works/aprs101-pdf-is-obsolete](https://how.aprs.works/aprs101-pdf-is-obsolete/).
- `APRS12c.pdf` — version 1.2 draft C, the current de-facto spec. Maintained
  by `wb2osz` (direwolf author) at
  [github.com/wb2osz/aprsspec](https://github.com/wb2osz/aprsspec).
- `Understanding-APRS-Packets.pdf` — a more accessible "what real packets
  look like, including the common bugs" companion document by the same
  author.

Both updated docs available at
`https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS12c.pdf` and
`https://raw.githubusercontent.com/wb2osz/aprsspec/main/Understanding-APRS-Packets.pdf`.

### Headline: direwolf is right, the wild is wrong

Every error class direwolf rejected in our corpus is **provably non-spec**
under APRS12c. The corpus is showing us real firmware bugs and historical
sloppiness, not over-strict decoding.

### Per-error mapping

#### "Bad source address" (58,609 cases — biggest bucket)

Direwolf flags lowercase / >6-char / weird-SSID source addresses
(`dl9mfl-6`, `WINLINK`, `BD8AWU-18`, `BI4KVT-8G`).

**Spec position** — APRS12c Chapter 4: *"the field conforms to the standard
AX.25 callsign format (i.e. up to 6 upper case alphanumeric characters plus
SSID)"*. Understanding-APRS-Packets §1.1 lists invalid examples:
`N2GH-0`, `N2GH-16`, `N2GH -1`, **`n2gh`** — *"Must be upper case."*

§5.9 calls out lowercase as one of the documented common bugs.

→ **Our strict `Callsign.TryParse` is spec-correct.** Direwolf's "warn and
parse on" is a pragmatic lenience, not a spec interpretation. The
`AprsCallsign` permissive type we discussed should be a **display-layer
type**, not a relaxation of `Callsign`.

#### "Unknown APRS Data Type Indicator 'H' (KJ4ERJ APRSIS32)" (3,521 cases)

**Spec position** — APRS12c Chapter 5, DTI table:

| Range | Status |
|---|---|
| `A–S` (uppercase letters in this range) | **"[Do not use]"** |
| `U–Z` | **"[Do not use]"** |

`H` is in the explicit no-use range. **APRSIS32 is violating the spec.**

#### "Unknown APRS Data Type Indicator '2'" (1,849 cases, APRSdroid)

**Spec position** — APRS12c Chapter 5, DTI table: `0–9` is explicitly
listed as **"[Do not use]"**. APRSdroid is violating the spec.

#### "Unknown APRS Data Type Indicator '-'" / "' '" (space) / etc.

**Spec position** — APRS12c Chapter 5: `-` is in the `[Unused]` row. Space
(0x20) is not in the DTI table at all — implies the payload starts with
whitespace (a mis-formed frame).

#### "Invalid character in compressed longitude. Must be in range of '!' to '{'." (4,453 cases)

**Spec position** — APRS12c Chapter 9 (Compressed Position Report Data
Formats):

> *"The values of YYYY and XXXX are computed as follows: YYYY is 380926 ×
> (90 − latitude) [base 91] … XXXX is 190463 × (180 + longitude) [base 91]
> … To obtain the corresponding ASCII characters, 33 is added to each of
> these values."*

Base-91 values 0–90, plus 33 offset = ASCII 33 (`!`) through 123 (`{`).
Anything outside that range is impossible under valid encoding. **Direwolf
is correct.** The sender emitted a malformed compressed frame.

#### "Invalid symbol table id for compressed position" (2,871 cases)

**Spec position** — APRS12c Chapter 9 + 20: the Symbol Table Identifier is
`/` (primary) or `\` (secondary) or `0–9 / A–Z` (overlay characters).
Anything else is not a valid table id and the leading character is then
ambiguous (compressed-position or numeric-lat/long?). **Direwolf is
correct.**

### Net implication for our codebase

We have **two distinct lenience levers** to think about:

1. **AX.25 envelope** (`Callsign`, `Ax25Frame`): stay strict per spec. Real
   strictness is what we want on the wire — invalid frames shouldn't be
   accepted as "valid AX.25 + happens to have lowercase". Direwolf agrees;
   it warns rather than rejects but that's a UX choice, not a spec
   reading.
2. **APRS payload** (future `Packet.Aprs`): validate per APRS12c
   directly. The bugs in the wild (APRSIS32, APRSdroid, Kenwood TM-D710
   0xFF burst — §5.10 in Understanding-APRS-Packets) are real and
   documented. Our decoder should reject them cleanly and surface the
   reason (matching direwolf's class of error message), not silently
   accept.

For the **monitor / display layer** we likely want a permissive read-only
`AprsCallsign` type that round-trips real-world strings without trying to
fit them through the strict AX.25 mould. Lossy is fine: surface
"`dl9mfl-6` (lowercase, non-spec)" rather than rejecting.

For **frame production** (we send) we never emit lowercase or weird
SSIDs — strict `Callsign` is the right input type.

### APRS101 vs APRS12c — which to follow?

The community-maintained `wb2osz/aprsspec` repo explicitly says
APRS101.pdf is obsolete. APRS12c.pdf incorporates decades of corrections
+ extensions (Base-91 comment telemetry, mic-E extensions, deviceid
discipline, etc.) that APRS101 doesn't have. **For SP-008 (`Packet.Aprs`),
APRS12c is the right reference.** When 101 and 12c differ, 12c wins.

The original APRS101.pdf at `ui-view.net` is still up but is the
**unchanged 1998 document**. The 12c document on the wb2osz repo is the
maintained successor.

Direct refs for the eventual `Packet.Aprs` work, pinned by URL:

- [`APRS12c.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS12c.pdf) — primary spec.
- [`Understanding-APRS-Packets.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/Understanding-APRS-Packets.pdf) — implementer's companion + common-bugs catalogue.
- [`APRS-Symbols.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS-Symbols.pdf) — full symbol table reference (when we render).
- [`APRS-Digipeater-Algorithm.pdf`](https://raw.githubusercontent.com/wb2osz/aprsspec/main/APRS-Digipeater-Algorithm.pdf) — for the digipeating logic when we eventually digipeat.

## 2026-05-14 — Direwolf reference pipeline (276k lines, ~1 h capture)

First end-to-end pass of the corpus through direwolf's `decode_aprs`
utility, persisted into a sibling `direwolf_decoded` table keyed by
`line_id`. Lines are sanitised on the way in by stripping the APRS-IS
q-construct path (`,qA.*:` → `:`) — the exact transformation the
direwolf man page recommends.

**Throughput**: ~23,400 lines/sec on a single `decode_aprs` subprocess
with 1000-line batches. The 276k-line corpus processed in 12.4 s. Fast
enough to re-run after every collector accumulation.

**Headline**: 276,243 lines decoded by direwolf.

| Metric | Count | % |
|---|--:|--:|
| With position | 147,019 | 53.2 % |
| With parse error | 113,587 | 41.1 % |
| Other (status, telemetry, messages, ...) | 15,637 | 5.7 % |

### Differential vs Packet.NET's reconstruct pipeline

Our `Callsign.TryParse` rejects ~22 % of source callsigns. Direwolf's
full APRS payload validator rejects ~41 %. The disagreement set is the
useful corpus.

**Direwolf accepts, we reject (~26 % of direwolf-accept sample):**
Almost entirely **lowercase callsigns** — `dl9mfl-6`, `iw0uwf-4`,
`vk4zu-13`, `kh6s-4`, `py5td-13`. Direwolf logs a "Source Address has
lower case letters" warning but parses on. Our strict `Callsign` type
refuses. Confirms the case for a separate permissive `AprsCallsign`
type in the eventual `Packet.Aprs` library.

**We accept, direwolf rejects (~46 % of direwolf-reject sample):**
Valid AX.25 envelopes carrying malformed APRS payloads. Examples:

- `F4JHN-13>APSFWX:)Temp-JHN!4344.50NT00122.50WWSOUS` — direwolf:
  *"Unknown APRS Data Type Indicator '-'"*. The `)` is the Item DTI;
  direwolf's deeper validator catches an inconsistency further in.
- `DM4XI-13>APRS:@140951z4921.32N/01204.07E_c197s004…` — direwolf:
  *"Unknown APRS Data Type Indicator '2'"* (an APRSdroid bug).
- `SH6FHC-5>APDR16:=5801.78N/01248.40E<112/002/A=000…` — direwolf:
  *"Invalid character in compressed longitude — must be in range '!' to '{'."*
- `PJUWX-0` `PE2ETE-11` — DTI ' ' (space) — likely missing payload.

### Top error buckets across the corpus

| Error first-line | Count |
|---|--:|
| Failed to create packet from text. Bad source address | 58,609 |
| Invalid character in compressed longitude. Must be in range of '!' to '{' | 4,453 |
| Unknown APRS Data Type Indicator "H" (KJ4ERJ APRSIS32) | 3,521 |
| Invalid symbol table id for compressed position | 2,871 |
| Unknown APRS Data Type Indicator "2" (APRSdroid) | 1,849 |

The corpus is now a real **differential testing baseline**: for any
future `Packet.Aprs` decoder, every frame has a direwolf interpretation
in the same SQLite file. Diff outputs frame-by-frame → bug candidates.

## 2026-05-14 — payload-type breakdown (181k lines, ~30 min capture)

First run with the payload-type classifier wired in (Tier 0 unblocker).
Bucketed by the first information-field byte per APRS101 §5 DTI.

| Type | Count | % of classified | Group |
|---|--:|--:|---|
| `position_no_ts_no_msg` | 54,906 | 30.29 % | positions |
| `position_ts_msg` | 27,222 | 15.02 % | positions |
| `position_no_ts_msg` | 27,021 | 14.91 % | positions |
| `position_ts_no_msg` | 2,858 | 1.58 % | positions |
| `object` | 19,511 | 10.76 % | reports |
| `message` | 14,988 | 8.27 % | messaging |
| `status` | 13,834 | 7.63 % | reports |
| `telemetry` | 10,367 | 5.72 % | telemetry |
| `mic_e_current` | 7,877 | 4.35 % | mic-E |
| `item` | 1,501 | 0.83 % | reports |
| `mic_e_old` | 943 | 0.52 % | mic-E |
| `user_defined` | 233 | 0.13 % | other |
| `raw_gps_or_ultimeter` | 9 | 0.00 % | other |
| `third_party` | 1 | 0.00 % | other |

**Highlights:**

- **Positions dominate**: 4 variants total **61.80 %** of all corpus traffic.
  The decoder ordering is now clear — start with `position_no_ts_no_msg`
  (the `!` DTI, simplest format), then `position_ts_*` (timestamp
  variants), then mic-E (compressed binary).
- **Messages are 8.27 %** — a real chunk. Implementing them needs the
  ack/reject state machine (APRS messages have retry semantics on top of
  AX.25 UI frames).
- **Mic-E is ~4.9 %** combined — smaller than I'd guessed. Mic-E is
  bigger on local RF than on the APRS-IS firehose (probably because
  Mic-E is more popular for mobile, and APRS-IS is sourced from igates
  globally).
- **Zero `non_printable_*` or `empty`** rows means every TNC2 line we
  captured had a printable-ASCII first byte of payload. Good signal —
  the corpus is "well-formed APRS as APRS-IS sees it".

Reconstruct success rate held steady at **78.05 %** (was 77.60 % at
the smaller sample). The 22 % miss rate is structural (APRS-vs-AX.25
callsign conventions), not statistical noise.

## 2026-05-14 — first 63k lines (~12 minutes of capture)

| Metric | Count | % of total |
|---|--:|--:|
| Lines processed | 63,451 | 100.00 % |
| TNC2 parse failures | 0 | 0.00 % |
| Reconstruct failures | 14,210 | **22.40 %** |
| Round-trip failures | 0 | 0.00 % |
| Round-trip successes | **49,241** | **77.60 %** |

**Headline**: ~22 % of real APRS-IS traffic uses callsign/SSID patterns
that aren't valid under the AX.25 spec. Of those failures, **99.91 % are
invalid sources** — destinations and digipeaters are fine almost across
the board.

Of the lines that *do* reconstruct, every single one round-trips through
`Ax25Frame.TryParse` losslessly. The parser/builder pair is solid; the
gap is at the AX.25 ↔ APRS-convention boundary.

## Failure categories (sources)

By inspecting top offenders against the 14,197 invalid-source failures:

### 1. Lowercase callsigns (~700 lines)

Sources like `db0sda` (600 lines — a German DV repeater beaconing
constantly), `vk3mak-15`, `dl9mfl-6`, `iw0uwf-4`, `iz1wiy-3` etc. AX.25
requires uppercase A–Z; APRS-IS happily passes them through.

### 2. Tactical / non-callsign source addresses (~480 lines)

`WINLINK` (449 lines), `ONELOVE`, `DONTKILL`, `NorthRyde`, `EL-S55MA`.
Winlink RMS gateways advertise their packet presence using `WINLINK` as
a tactical alias. None of these are valid AX.25 addresses.

### 3. Multi-character / non-numeric SSIDs (~13,000 lines — the bulk)

Patterns like `M0IQF-N4`, `DD9PX-77`, `BI4KVT-8G`, `BD8CMN-T`,
`BA4QFV-O`. AX.25 spec allows SSIDs 0–15 numeric only. APRS uses:

- Letter SSIDs (`-A`, `-B`, `-T`, `-N`) for D-Star / DMR gateways, RMS
  stations, weather, etc.
- Multi-digit SSIDs (`-77`, `-20`, `-48`) used by Chinese / Russian
  stations and APRS firmwares.
- Combinations (`-8G`, `-N4`, `-N0`, `-S55MA`).

This is the largest class and the most thorny — APRS conventions
genuinely vary by country and software. Worth filing as an upstream
APRS-spec issue if "permitted SSID forms" aren't documented.

### 4. Long base callsigns (> 6 chars)

`BD8AWU-18`, `BD8CMN-S/T/H`, `BI4KVT-8G`. Spec is strict 1–6 chars.
Often overlaps category 3.

## Destination / digipeater failures (rare)

- 5 invalid destinations seen (`AP4R132`, `APLRd1`, `APOT212`, `UQSWT63`):
  long destination "to" calls — APRS software pads the destination with
  software-ID tokens that occasionally exceed 6 chars.
- 8 invalid digipeaters: lowercase (`wide1-1`), letter SSIDs
  (`DO0SAS-L4`, `9M2VKA-L`), multi-digit SSIDs (`OK2ZAW-17`).

## What this tells us about the AX.25 parser

The strict `Callsign.TryParse` is **correctly** rejecting these — the
AX.25 spec is unambiguous about A–Z / 0–9 and SSID 0–15. Our parser is
behaving as designed.

But for the *monitor* layer (e.g. a web UI showing live APRS-IS frames),
strict rejection means ~22 % of traffic disappears. The right shape for
the codebase is:

- **`Callsign`** — strict AX.25 type. Stays as-is.
- **`AprsCallsign`** — looser type accepting the APRS conventions above.
  Lives in the eventual `Packet.Aprs` library (SP-008).
- **A boundary layer** that maps `AprsCallsign` → `Callsign` where
  possible (`-B` → `-11`, etc., per common D-Star practice), and surfaces
  the raw `AprsCallsign` to UI consumers verbatim where not.

## What this tells us about the corpus

77.60 % clean parse is encouraging baseline. The remaining 22.40 % is
exactly the kind of real-world data the spike was built to surface —
each failure mode is now a documented edge case rather than a
hypothetical.

The corpus itself is feedstock for:

- **SP-002** (direwolf-as-reference harness) — A/B our parser against
  direwolf on the same TNC2 lines.
- **SP-003** (replay/regression harness) — once we have the
  `Packet.Replay.AprsIs` library, every captured line becomes a regression
  test fixture.
- **SP-004** (fuzz harness) — the corpus is a high-quality seed for
  SharpFuzz against `Ax25Frame.TryParse` (real-world inputs > synthetic
  inputs at finding edge cases).
- **SP-008** (full APRS library) — `AprsCallsign` design driven by what
  we actually see, not what we *think* the APRS spec allows.

## How to re-run

Against the live corpus:

```sh
dotnet run --project tools/Packet.AprsIs.Spike -- analyse \
  --data-dir /home/tf/aprs-is-data \
  --out-dir /tmp/aprs-analysis-$(date -u +%Y%m%d-%H%M%S)
```

Or one specific day:

```sh
dotnet run --project tools/Packet.AprsIs.Spike -- analyse \
  --db /home/tf/aprs-is-data/aprs-is-2026-05-14.sqlite
```

Reports go to `<out-dir>/stats.md` (markdown summary) +
`<out-dir>/failures.jsonl` (one line per failure with raw input + reason).
