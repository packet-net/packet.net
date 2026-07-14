# Headless C# soundmodem for PDN — initial research

*Research / options analysis. Status: **research complete 2026-07-14; core decisions taken same day**
(see [Decisions](#decisions-tom-2026-07-14)) — **work commenced in `packet-net/soundmodem`**.
Grounded against QtSoundModem 0.0.0.76
(commit `9cd2735`, cloned 2026-07-14 from `git://vps1.g8bpq.net/QtSM`), this repo at `main`
(9b59ed1), live kiss-collector data, bpq32 groups.io + OARC Discord field reports, and a managed-DSP
micro-benchmark run this session.*

**Question:** Can PDN grow a native, headless (no Qt, no GUI) soundcard packet modem in C# / .NET 10
— KISS-shaped, with native DCD reflected up through the existing carrier-sense seam and a browser
waterfall for setup/tuning — using QtSoundModem as the reference implementation? What would it take,
and what are the traps?

**Headline:** Feasible, and the architecture falls out almost for free — PDN already has every seam
a soundmodem needs (`IAx25Transport` + `ICarrierSense` → `CarrierSenseGate`, the `kind:` transport
union, per-port SSE tuning streams). DSP compute is a non-issue in managed code (~14 % of one
2012-era x64 core for the *worst-case* AFSK decoder bank, measured). The two real gates are
**licensing** — any port of QtSM must be GPL-3.0-or-later, which forces resolution of this repo's
own (currently self-contradictory) MIT/AGPL status — and **target modes**: the live GB7RDG network
runs **zero** classic 1200 AFSK; it is 100 % NinoTNC IL2P+CRC PSK/GFSK. The acceptance bar is
NinoTNC-waveform interop, not QtSM feature parity.

---

## 1. Why

QtSoundModem (G8BPQ's Qt/C port of UZ7HO SoundModem, GPLv3+) is the de-facto Linux soundcard modem
for BPQ-world packet. It works, and its decoder is well-regarded — but it is a ~75 k-line
machine-transliterated-from-Delphi C codebase with the GUI load-bearing (its FFT busy-detector —
the *only* DCD source for its 9600 RUH mode — literally runs inside the waterfall paint path and is
dead in its own `nogui` mode), a long field-reported catalogue of TX-audio/PTT/crash bugs, and no
way to surface DCD or spectra to a host. PDN currently *consumes* modems (NinoTNC, KISS-TCP
softmodems in CI) and never implements one; plan §11 scopes v1 as "KISS modems only".

A native `Packet.SoundModem` would give us: DCD wired straight into the AX.25 stack's carrier-sense
gate (no TNC in the loop), sample-accurate TX-complete (lighting up ACKMODE pacing and
`T1FromTxComplete` without the round-trip), a first true spectrum source for the tuning ecosystem,
one less external process to babysit, and a modem whose bugs we can actually fix. Notably, the
"service/UI split" this design implies is something Tom publicly wished QtSM had (OARC, Aug 2023) —
this is that, PDN-shaped.

## 2. The reference implementation, mapped

Digest of the full QtSM source survey (all references are QtSM 0.0.0.76 @ `9cd2735`).

### 2.1 Shape

- **One worker thread**: a 10 ms poll loop (`MainLoop()`) reads 512-frame (~42.7 ms) stereo S16_LE
  blocks at **12 000 Hz** internal rate and runs every demodulator serially (`sm_main.c:808–870`).
  If any direwolf-derived "RUH" 4800/9600 modem is configured, capture switches to 48 000 Hz /
  2048-frame blocks. An optional `multiCore` mode spawns one pthread per channel per buffer with
  **zero synchronisation** over shared globals (default off; a data race as shipped).
- **4 logical modem channels** (A–D) map onto the L/R sides of *one* stereo device — up to four
  independent modems, several of which may share one audio side at different centre frequencies
  (field users call this "4 modems in one passband … a massive qtsm plus"). KISS port nibble =
  channel index.
- **Rate conversion, two distinct paths** (both poor): opening the device at 12 kHz delegates
  resampling to the ALSA `plug` layer (default converter: linear interpolation, documented
  aliasing); when RUH forces a 48 kHz open, non-RUH modems get 48 k→12 k by **keeping 1 sample in
  4 with no anti-alias filter**, and TX upsamples by sample duplication (`sm_main.c:948–1006`,
  `SMMain.c:224–235`).
- **State**: 191 extern globals (75 of them per-channel arrays), and a statically-allocated
  `DET[3][16]` detector array of ~220 MB regardless of configuration (`UZ7HOStuff.h:1056`).
  Per-frame queues are a Delphi `TStringList` emulation doing malloc/realloc per frame in the hot
  path.
- Also aboard and compiled, but irrelevant to a packet modem: a complete grafted-in **ARDOP** modem
  (incl. a 908 KB constant-table file), an experimental MPSK "MP400" mode whose RX is dead code
  (untranslated x86 asm), fldigi-derived **RSID** (retuned to 12 kHz, deliberately incompatible with
  fldigi's), and a full internal AX.25 L2 engine + digipeater + AGW/host stack (~10 k lines) that
  PDN's own stack replaces outright.

### 2.2 The modems

| Family | Modes (bps) | Demod approach |
|---|---|---|
| AFSK (FSK) | 300 / 600 / 1200 / 2400 | per-decoder FIR BPF → complex mix to baseband → FIR LPF → differentiate-and-cross-multiply FM discriminator → adaptive dual-threshold slicer |
| BPSK | 300 / 600 / 1200 / 2400 | same front-end → differential atan2 phase detector |
| QPSK | 600 (V.26A) / 2400 (SM + V.26A + **V.26B π/4**) / 3600 / 4800 | differential PSK; the π/4 V.26B map was changed in 0.72 **specifically to match NinoTNC** |
| 8PSK | 900 / 4800 (V.27) | ditto |
| RUH | 4800 / 9600 | transplanted **Dire Wolf** `demod_9600`/`hdlc_rec` (G3RUH-style baseband at 48 kHz, LFSR descrambler, DPLL) |

Bit sync for all UZ7HO modes is a distinctive "energy-peak position" DPLL: a leaky per-position
energy buffer over the (normalised ~40-sample) symbol steers the bit oscillator via a triangle
function. The FSK path defers NRZI+destuffing until a whole frame is captured as a raw bit string,
enabling **Memory-ARQ** (majority bit-voting across ~200 stored corrupt copies) and single-bit
recovery from slicer disagreement — decode-quality features field users rate on par with NinoTNC
hardware.

**Multi-decoder**: each channel can run `2·RCVR+1` parallel decoders spaced 30 Hz (ini
`NRRcvrPairs`; the DET array is sized for 16 decoder slots × 3 emphasis variants) × up to 3
"pre-emphasis" passes = up to ~48 concurrent demods per channel, deduped by frame content. This —
UZ7HO's celebrated off-frequency tolerance — is a headline algorithm to keep.

**FEC**: IL2P TX+RX is a near-verbatim Dire Wolf port wired into *every* modem family, including
the NinoTNC **IL2P+CRC** trailer extension (4 Hamming(7,4) bytes carrying the AX.25 CRC16). FX.25
is UZ7HO's own implementation, TX on FSK/BPSK and RX only in the FSK path (none on RUH/QPSK/8PSK).
Both sit on GF(2⁸) poly 0x11d Reed-Solomon (IL2P fcr=0, FX.25 fcr=1) — one parameterised RS codec
covers both in a rewrite, and the whole IL2P+FX.25 core is deterministic byte/bit transforms,
cleanly separable from the DSP (well under ~1 500 lines of C#).

### 2.3 Interfaces

KISS is **TCP-only** (no serial/pty), default port 8105 in code / 8100 in the shipped ini, port
nibble = channel, multi-client. It honours **only** opcode 0 (data) and 0x0C (ACKMODE) —
TXDELAY/P/SLOTTIME/TXTAIL/FULLDUPLEX/SETHW are silently ignored (`kiss_mode.c:206–266`, "Still
need to process kiss control frames"). AGW implements the common frames plus a BPQ-private 32-byte
`'g'` protocol for remote get/set of frequency/modem/FX.25/IL2P. 6pack is a non-functional stub.
**No interface carries live DCD** — the only busy data anywhere is a per-minute
`STATS <ptt%> <busy%>` aggregate (Mgmt TCP port, or a private KISS opcode-7 extension), and those
timers live in the GUI class, so they're dead headless.

### 2.4 DCD / busy — three mechanisms, loosely integrated

1. **UZ7HO decoder DCD** (AFSK/PSK modes): EMA of bit-clock peak-position jitter vs a slider
   threshold, plus a flag/preamble shift-register fast-assert with a 48-bit-time hold
   (`ax25_demod.c:340–516`). Assert/deassert ~45–90 ms *by code constants* (not measured).
2. **Spectral busy detector** (ARDOP-derived sorted-FFT-bin S/N test): fed **from the waterfall
   display path** — `doWaterfall()` returns immediately in `nonGUIMode` and is the sole caller —
   so **headless QtSM has no carrier sense at all for RUH/ARDOP modes** (their `chk_dcd1` reads
   `blnBusyStatus`, which nothing updates). `blnBusyStatus` is also a single shared global written
   per-channel in a loop — cross-channel clobbering in multi-channel use.
3. **Dire Wolf's DPLL-lock DCD** (popcount hysteresis, assert ≥30/32 good transitions, drop ≤6/32):
   fully present in the imported `dw9600` code **but wired to an empty `dcd_change()` stub** — its
   output is discarded.

CSMA (slottime + a nonstandard squared-persistence formula,
`round((256/persist)²)·rand·0.5·slottime`) runs *inside the modem* in `chk_dcd1`, gating KISS and
internal-L2 frames identically; AGW UI/raw frames bypass it entirely; `fullduplex` is declared and
never used.

### 2.5 The waterfall

Tap in `BufferFull()`: per-channel 12 kHz mono samples batched to `FFTSize` (default 4096 → 2.93 Hz
bins; the GUI recomputes it from the displayed span so non-power-of-2 sizes occur), FFTW3f,
magnitudes log-scaled into ~900 one-byte bins per line, coloured through a 256-entry "raduga" LUT
into a QImage. **There is no remote/headless waterfall feed of any kind** — and none among packet
soundmodems anywhere (Dire Wolf has no spectrum display at all): a browser waterfall would be
genuinely novel. The data rate is trivial: ~900 B/line at 1.8–6.7 lines/s/channel ≈ **3–7 kB/s
raw** per channel.

### 2.6 Field-reported reality (bpq32 groups.io, 766 hits; OARC Discord, ~760 hits)

What breaks, per the community record:

- **TX-audio integrity** — silence gaps / truncated frames / crackles, chronic since >0.64,
  partially fixed 0.72, regressed 0.73; root cause never published.
- **PTT release** — based on ALSA flush; G8BPQ's own words: "not particularly reliable especially
  with newer kernels" (bpq32 msg 41453). Now a timed drop from sample count plus a manual
  "Soundcard TX Latency" fudge parameter.
- **Crashes** — dual-channel/right-channel segfaults, waterfall freezes (unsolved since 2022),
  stale-ini crashes; users run hourly `killall` crontabs. Two gdb-confirmed memory bugs found by a
  user in 2024 (`malloc(sizeof(ptr))` in kiss_mode.c; a `chk_dcd1` loop-variable-reuse runaway that
  pinned 4 cores and took the host down — the latter appears fixed in 0.0.0.76 as cloned).
- **MAC timing** — no DWAIT, Persist not settable in the GUI, premature TX on busy channel
  (Jan 2026 report).
- **Audio backends** — PulseAudio/pipewire pain, virtual dmix/dsnoop devices invisible to the GUI
  which then overwrites the hand-edited ini on exit.
- **Headless as afterthought** — GUI required for initial config; `nogui` only as bare positional
  arg; OARC packaging carries an open "segfaults when run headless" issue.

Our own source survey adds (static analysis, high-plausibility, **not runtime-proven**): an ALSA
underrun-recovery path that retransmits an uninitialised 512 KB stack buffer
(`ALSASound.c:831–844`); a monitor `sprintf` into `mon_frm[512]` reachable with ≤1023-byte IL2P
payloads; the 48 k upsample path apparently mis-filling stereo frames; DualPTT branches that both
set DTR.

**What must not regress** (the community's consistent praise): AFSK decode on par with NinoTNC
hardware; better-than-Direwolf connected-mode behaviour via ACKMODE + ack-coalescing ("KISS
Optimise"); multiple modems per passband on separate KISS sub-channels; the NinoTNC-compatible
BPSK/QPSK IL2P+CRC mode set; channel-utilisation (%DCD/%PTT) reporting.

## 3. What the network actually runs (kiss-collector, live query 2026-07-14)

gb7rdg-node, ~273 k frames over 22 days, four bands — **every observed TX mode is a NinoTNC
IL2P+CRC mode; zero classic 1200 AFSK, zero plain G3RUH**:

| Band | Mode (NinoTNC id, from tx-timing metadata) | Share |
|---|---|---|
| 70 cm | 9600 GFSK IL2P+CRC (mode 2) | 125 k frames — dominant |
| 40 m | 300 BPSK IL2P+CRC (mode 8) | 103 k |
| 2 m | 3600 QPSK IL2P+CRC (mode 5) | 34 k |
| 6 m | 2400 QPSK IL2P+CRC (mode 11) | 11 k |

ACKMODE is heavily used (44.7 k frames); per-band KISS TXDELAY/PERSIST/SLOTTIME/TXTAIL are actively
pushed; all half-duplex.

Consequences: **IL2P+CRC is table stakes**, 1200 AFSK is not (though it remains the universal
interop/corpus baseline and the APRS gateway mode — worth keeping in scope for that reason, as a
decision). And the single most important *unverified* interop question is whether QtSM's
RUH-9600+IL2P+CRC waveform actually interoperates with **NinoTNC mode 2 (9600 GFSK IL2P+CRC)** —
the busiest port on the network. QtSM↔NinoTNC interop is historically pairwise-negotiated, not
spec-guaranteed (3600 QPSK phase map only fixed in QtSM 0.72; the 3600 mode needs a 1650 Hz centre
vs 1500 for the others; unresolved May-2024 suspicions of IL2P CRC mismatches on long frames).
The mode-id→waveform mapping above comes from collector metadata and needs cross-checking against
NinoTNC docs before requirements hang off it.

## 4. Licensing

### 4.1 Provenance chain (verified in source this session)

- QtSoundModem has **no LICENSE file** (GitHub license detection: null). The grant exists as
  per-file headers: "Copyright (C) 2019-2020 Andrei Kopanchuk UZ7HO … GNU General Public License …
  either version 3 … or (at your option) any later version", with "UZ7HO Soundmodem Port by John
  Wiseman G8BPQ". UZ7HO's Delphi original is closed-source freeware; the public evidence of
  permission is Wiseman's acknowledgement ("Many thanks to Andy for allowing me to convert his
  excellent software"). No statement from Kopanchuk himself exists — the grant is
  evidenced-but-unconfirmed, and a courtesy email to Wiseman/Kopanchuk is recommended before
  shipping anything derived.
- Embedded lineages: Dire Wolf code (`dw9600.c`, `il2p.c`, `audio.c` OSS bits) — Dire Wolf is
  GPL-2.0-**or-later** per its file headers, so upgradeable into GPLv3+ works. The packet-relevant
  RS codecs (`ax25_fec.c`, RS inside `il2p.c`) are **unattributed Karn-lineage translations**
  (Karn's grant as carried by Dire Wolf is unversioned GPL; his standalone libfec is LGPL) — a
  licensing-hygiene wart in QtSM itself. Henry Minsky's RSCODE (GPLv3+) and the fldigi RSID code
  (GPLv3+) are used only by subsystems a PDN port would drop (ARDOP/MPSK/RSID). ~15 files carry no
  license header at all (moot for us if ARDOP is excluded). 6pack.cpp is GPLv2+ Linux-kernel code.
- All ingredients are GPLv3-compatible; QtSM as a whole is coherently **GPL-3.0-or-later**.

### 4.2 The position (stated once, precisely)

A C# translation/port of QtSM code is a **derivative work** and must itself be
**GPL-3.0-or-later**. It cannot be MIT. It also cannot be *relicensed* AGPL — but GPLv3 §13 /
AGPLv3 §13 expressly permit **combining** a GPLv3 library with the AGPLv3 node host in one program,
each part keeping its licence; the node's AGPL network-source offer must then also cover the modem
source. NuGet is fine with this: a per-project
`<PackageLicenseExpression>GPL-3.0-or-later</PackageLicenseExpression>` overrides the repo-wide
default. The one hard rule that follows: **no MIT-published Packet.\* package may ever depend on
Packet.SoundModem** (the dependency direction we'd want anyway — the modem depends on the MIT
abstractions, and the AGPL node host depends on the modem).

### 4.3 The blocker this exposes: packet.net's own licence is self-contradictory today

`LICENSE` was switched MIT → **AGPL-3.0** in commit `ac2fe22` (2026-06-14) with **no plan §17
entry**, while `README.md` §License, plan §3 (locked decisions), and
`Directory.Build.props:31` `<PackageLicenseExpression>MIT</PackageLicenseExpression>` (stamped on
all 14 packages in the publish-libs matrix; nuget.org shows Packet.Core 0.23.0 as MIT) still say
MIT. Whether this is intentional dual licensing (libs MIT / node AGPL) or an incomplete migration,
it must be resolved — independently of, but certainly before, a GPL-derived package lands.

### 4.4 Options

| | Option | Trade-offs | Verdict |
|---|---|---|---|
| (a) | **Port from QtSM, package is GPL-3.0-or-later** | Fastest path to UZ7HO's proven decoder (Memory-ARQ, multi-decoder); licence forces GPL package + the no-MIT-dependents rule; carries QtSM's own hygiene warts (unattributed Karn RS) unless those parts are re-done from spec | **Viable and honest.** Preferred where the algorithm's value is in accumulated empirical tuning (the AFSK/PSK demod + DPLL). |
| (b) | **Independent implementation from open specs/papers** — IL2P draft v0.6 (KK4HEJ; complete with worked test vectors — and Tom is in its acknowledgements), FX.25 Stensat spec (archive.org; Dire Wolf's FX.25 paper reproduces the tag table), Bell 202/G3RUH open designs, Dire Wolf's genuinely excellent algorithm papers | Cleanest provenance for the FEC layer, which is pure byte/bit transforms; but a *permissive-licence clean-room claim is not credibly available to this project* — we have read the GPL source line-by-line. Implementing from spec is still worth doing for code quality; just don't pretend it changes the licence outcome | **Do this for IL2P/FX.25/RS regardless** — better code, test vectors included — but licence the result GPL-3.0-or-later anyway, which makes the clean-room question moot. |
| (c) | **Arms-length: run direwolf/QtSM as a subprocess over KISS-TCP** | Zero licence impact (mere aggregation), works today (`kind: kiss-tcp`, exactly how net-sim's samoyed modem is consumed in interop CI) | Fails the actual requirements: no DCD through the seam, no waterfall, no mode control, still babysitting an external process. **Keep as the fallback shape, not the plan.** |

**Recommendation:** (a)+(b) hybrid, shipped as a GPL-3.0-or-later `Packet.SoundModem`: port the
UZ7HO demod family and DCD machinery from QtSM (that's where the field-proven value is), implement
IL2P/FX.25/RS from their specs with the published test vectors, take the RUH/9600 design from Dire
Wolf's papers + source (GPL-2.0-or-later, upgrade-compatible). Resolve the repo licence question
first.

## 5. Fit into PDN

### 5.1 The seams already exist

- **Transport**: `IAx25Transport` (+ optional facets `ITxCompletionTransport`,
  `ICsmaChannelParams`, `ICarrierSense`, feature-detected with `is`) in
  `Packet.Ax25.Transport.Abstractions`. In-process modem = no KISS framing needed at all;
  the modem sends/receives frame bodies directly.
- **DCD**: `ICarrierSense.ChannelBusy` (`bool?`, fail-open) → `Ax25ListenerOptions.CarrierSense` →
  `CarrierSenseGate` consulted at every physical keyup (OQ-012, landed 2026-07-04, parity-mirrored
  in ax25-ts). The seam's doc comment *explicitly anticipates* a transport that is its own
  carrier-sense source. One wiring gap: `PortSupervisor` builds carrier-sense only from an attached
  `radio:` block (`src/Packet.Node.Core/Hosting/PortSupervisor.cs:894`) and never probes
  `transport is ICarrierSense` — a one-line-ish supervisor change (probe the modem chain, not the
  reconnect decorators, which don't forward optional facets).
- **Port kinds**: the closed `TransportConfig` union (7 kinds). `tait-transparent` is the precedent
  for an in-process "the radio IS the modem" kind; `kiss-tcp` is the precedent for an external
  softmodem. Adding `kind: soundmodem` is the documented 5-arm change.
- **TX-complete**: `KissParams` already models ACKMODE pacing and `T1FromTxComplete` as options
  requiring `ITxCompletionTransport` — a soundmodem knows *exactly* when its last sample left the
  device and can implement this natively, better than any KISS echo round-trip. This directly
  attacks QtSM's worst field-reliability area.
- **Waterfall delivery**: the node is SSE-everywhere by design-doc decision ("SSE, not WebSocket"),
  and `PdnPortTuningApi` (per-port SSE feed, read-scoped; admin-scoped audited session verbs) is a
  near-exact structural template for a per-port spectrum stream.
- **Parity**: a transport + carrier-sense implementation below the listener adds **no new ax25-ts
  parity surface** (parity tracks `Ax25ParseOptions`/quirks/`XidParseOptions`/listener options; the
  CarrierSense option is already mirrored). To be confirmed against `parity-check.mjs` when the
  first PR goes up, but no listener-surface widening is anticipated.

### 5.2 Deployment shapes

1. **In-process** (recommended): `Packet.SoundModem` library + `kind: soundmodem` port. Native DCD,
   native TX-complete, waterfall tap runs in the node process, config in node config, supervision
   by `PortSupervisor` like every other port. This is the only shape that satisfies "native DCD
   through the existing seam" today.
2. **Out-of-process KISS-TCP daemon**: works with zero node changes right now, and is the natural
   **head-end deployment shape** — the Go head-end stays a deliberately dumb serial↔TCP bridge, and
   a soundmodem on the head-end Pi is just a sibling process exposing KISS-TCP back to the compute
   node (audio never crosses the LAN). But plain KISS carries no DCD: this shape stays
   DCD-less until a KISS DCD extension exists. Relevant: the "Nino KISS DCD extension" (plan
   OQ-012 residual) is still an *undefined* wire format, and survey found **no established
   DCD-over-KISS convention anywhere** (KISS spec, AGW, Dire Wolf, UZ7HO, Mobilinkd all lack it;
   the closest reported precedents are 6pack's in-band state octets and MeshCore's
   SetHardware-subcommand + unsolicited event scheme — the latter unverified against its firmware
   source). If/when that format gets defined for NinoTNC, the soundmodem daemon should emit the
   same thing. Design once, use twice.

Build the library so the same core serves both: DSP core + a thin in-process transport adapter now;
an optional KISS-TCP front-end later.

### 5.3 CSMA ownership

Today the host gate is deliberately *not* p-persistence (plain wait-for-clear, 100 ms slots,
fail-open, 10 s bound) and PERSIST/SLOTTIME are pushed down to the TNC, which owns the p-roll. An
in-process soundmodem should follow the same contract: implement `ICsmaChannelParams` and run
p-persistence + slot timing *in the modem* at sample resolution (where DCD is freshest), with the
host `CarrierSenseGate` remaining as the outer wait-for-clear guard. That matches both the QtSM/TNC
model and PDN's existing division of labour. Watch the double-gating interaction (two layers both
waiting on the same DCD is safe but can double the medium-access delay; the gate's fail-open,
bounded design makes this benign, but it should be characterised on the bench). Note QtSM's
persistence formula is nonstandard — implement classic p-persist per AX.25 §6, not the quirk.

### 5.4 Status surface

`RadioStatus.ChannelBusy` / `/api/v1/radios` / `pdn_radio_channel_busy` / the dashboard tri-state
are all keyed to a `radio:` config block, so a modem-only port shows no DCD anywhere today. The
port's DCD (and %DCD/%PTT utilisation, which QtSM users prize) needs either a parallel
port-status surface or a widening of the radio read-models — a design decision to take alongside
the supervisor probe.

### 5.5 Multi-channel scope

QtSM's 4-modems-over-stereo is real and loved in the field; PDN leans hard one-port-one-channel
(every outbound send hard-codes KISS port 0; NinoTNC scopes "one modem = one serial port = one
radio"). Proposed resolution: **one PDN port = one modem = one audio channel (L or R of a device)**
— two radios per stereo card is then just two ports sharing an `audioDevice:`; and "several modems
in one passband" becomes a *later* feature where additional decode-only listeners share a channel's
samples. Full duplex: out of scope (QtSM never implements it; the network is all half-duplex);
`FULLDUPLEX` config accepted and rejected-with-diagnostic. ARDOP: out of scope, aligned with plan
§11.

## 6. DCD design for the port

The port cannot copy the incumbent (headless QtSM's DCD for RUH modes literally doesn't run). The
survey converges on a two-signal design, per channel, display-decoupled:

1. **Packet DCD** — Dire Wolf's DPLL-lock scoring (transition within ±window of DPLL wrap = good;
   assert at ≥30/32 good, drop at ≤6/32), which QtSM already ships wired to a stub. Complement with
   the UZ7HO flag/preamble fast-assert for low-latency onset. Runs per demodulator; channel DCD =
   OR over decoders.
2. **Energy busy** — the ARDOP sorted-FFT-bin S/N detector done right: per-channel state (no shared
   globals), fed from the DSP chain (not the display), with hold/hysteresis.

`ICarrierSense.ChannelBusy` = packet-DCD **OR** energy-busy; `null` while the pipeline is
starting/stopped (fail-open); never self-assert during own TX; adopt the never-latch-busy hygiene
the Tait implementation established (stale-busy revalidation watchdog → `null`). Assert/deassert
latency targets need bench measurement — the ~45–90 ms figures for QtSM are derived from code
constants, and the audio capture period (~10 ms achievable with raw ALSA) sets the floor.

## 7. Waterfall

- **Tap**: post-channel-extraction samples; fixed **4096-point real FFT at 12 kHz** (2.93 Hz bins)
  — fixing the size keeps us on power-of-2 (FftFlat/FftSharp, both MIT; no FFTW native dependency
  needed, GPL-compatible though it is). 10–20 lines/s per channel.
- **Wire**: ~900 u8 bins/line → 3–7 kB/s/channel raw; base64-in-SSE overhead is trivial at this
  rate. Follow the design doc: **SSE, not WebSocket**, per-port endpoint modeled on
  `PdnPortTuningApi` (e.g. `/api/v1/ports/{id}/spectrum/events`), palette applied client-side on a
  canvas. Overlay the per-modem centre/shift brackets like QtSM's header rows — that's the tuning
  value. Must be added to `docs/node-api.yaml` and to the SSE `?access_token=` allowlist in
  `Program.cs` — where, incidentally, `/api/v1/ports/{id}/tuning/events` is **missing today**
  (verified `Program.cs:334–336` lists only events/sessions/console): apparent pre-existing
  auth-on bug worth checking/fixing independently.
- The waterfall complements, not replaces, the tuning ecosystem: TuningDoctor/ModeSurvey/
  DeviationAdvisor are all decode-side inference; this is the first direct spectral view (and the
  GETRSSI audio meter it partially replaces was removed in NinoTNC fw 3.44).
- **Headless calibration** (gap flagged in review): pair the waterfall with QtSM-style calibration
  TX aids — steady low/high/alternating tones and a level-set tone through the real TX chain —
  exposed as admin-scoped tuning-session verbs, plus TX audio level control (ALSA mixer) so the
  full set-levels-by-watching-the-waterfall loop works from the browser.

## 8. Audio I/O and PTT

### 8.1 Audio (surveyed; recommendation)

- **Primary: direct ALSA P/Invoke** (libasound, LGPL-2.1+; `Depends: libasound2` only — cleanest
  .deb story; precedent in QtSM/direwolf/NAudio.Alsa/Alsa.Net). Blocking-read capture thread,
  **capture at 48 000 Hz native** (CM108-family devices only do 44.1/48 k) and decimate ÷4 to the
  12 kHz DSP rate with a real polyphase FIR — explicitly *not* QtSM's plug-layer linear resampler
  or skip-3-of-4 decimation. ~10 ms periods achievable; DCD latency floor = period size.
- **Fallback / cross-platform: ppy.SDL3-CS** (MIT, osu!-team-maintained, monthly releases, bundled
  natives incl. linux-arm64 *and* linux-arm; SDL3 zlib) — best-maintained binding surveyed, weaker
  latency control (buffer size is a hint). Every other .NET audio binding surveyed is
  single-maintainer, preview-quality, or dormant (PortAudioSharp2 is the runner-up; NAudio's ALSA
  leg is author-labelled untested preview). Headless Pi OS Lite runs no sound server, so raw ALSA
  is the base API regardless; SDL3 exists in Debian only from trixie.
- CM108/CM119-family dongles (Digirig etc., chip identities per secondary sources — verify): full
  duplex, mono capture/stereo playback, crystal-less shared USB clock per device (so no intra-device
  drift); TX must be paced by device consumption, never wall clock. armhf support would eliminate
  PortAudioSharp2; ALSA and SDL3-CS both fine — needs the supported-arch decision.

### 8.2 PTT (identified gap — thinly researched, follow-up needed)

QtSM supports seven mechanisms; the .NET-side survey wasn't done in depth. Nothing looks hard:
serial RTS/DTR = `System.IO.Ports` `RtsEnable`/`DtrEnable`; **CM108 GPIO-PTT** = a 5-byte write to
`/dev/hidraw*` (QtSM's Linux path does exactly this, no HID library needed); Pi GPIO =
`System.Device.Gpio`; rigctld = a trivial TCP text client; CAT hex strings = serial writes. PDN
also has an option QtSM never had: **PTT via the existing `IRadioControl`** (Tait CCDI) — the
modem asks the port's attached radio to key. Scope for v1: RTS/DTR + CM108 + `IRadioControl`,
defer CAT/hamlib/FLRig.

The genuinely open engineering risk is **TX-complete detection / PTT release** — QtSM's most
chronic field failure. Design intent: pre-render or fully queue the frame, pace by device
consumption, compute release from sample count + `snd_pcm_delay`/`snd_pcm_status` rather than
flush/drain semantics, and bench-validate the release latency across kernels before trusting it.
This must be treated as a spike deliverable, not assumed solved.

## 9. Feasibility and performance (measured where possible)

Micro-benchmarked this session (self-contained .NET 10 bench, Linux x64, deliberately old 2012
i7-3770 without AVX2/FMA):

- Managed FIR throughput: **1 126 MMAC/s scalar**, 1 619 with `Vector<T>`, **1 768 MMAC/s with
  `TensorPrimitives.Dot`** at 256 taps (4 805 at 128 taps), single core.
- Worst realistic AFSK1200 configuration (4 channels × 5 offset receivers × 3 emphasis paths,
  QtSM's exact filter sizes at 12 kHz): **~246 MMAC/s ≈ 14 % of that one old core**. A basic single
  channel: 0.35 %. The RUH 9600 path and a 4096-pt waterfall FFT at 20 fps are single-digit
  MMAC/MFLOP noise on top.
- **Pi figures are extrapolations** (4–8× pessimistic scaling → comfortably feasible on a Pi 4, a
  Pi 5 halves it again) — *not measured*. The bench must be re-run on a Pi (5-minute job) before
  the report's feasibility claim is treated as settled; plan.md's own experience says software AFSK
  modems are load-sensitive (CPU glitch → phantom frame loss).

Managed-runtime specifics: zero-steady-state-allocation pipeline (preallocated history +
`Span<T>` + `TensorPrimitives` + `ArrayPool` for frames) makes GC a non-issue at 42.7 ms block
cadence with 3–4 periods of ALSA queue depth; `Thread.Priority` is a documented no-op on Linux
.NET (mitigate with queue depth, optionally P/Invoke `pthread_setschedparam(SCHED_RR)` granted via
systemd `LimitRTPRIO`); QtSM's leaky integrators decay into denormal range during silence — an x86
perf trap .NET can't globally FTZ away, so clamp/epsilon-inject in integrators (ARM64 NEON
behaviour to be spot-checked). NativeAOT is a startup nicety, not a throughput requirement.

And the port should beat upstream on CPU anyway: QtSM's inner loops are transliterated Delphi
assembler with per-iteration NaN checks, malloc-per-frame queues, and a ~220 MB static detector
array sized for 48 demodulators regardless of configuration.

## 10. What a rewrite keeps, fixes, and drops

**Keep (algorithms):** the UZ7HO demod skeleton (BPF → mix → LPF → discriminator/atan2) with its
empirically-tuned filter tables; the energy-peak DPLL; Memory-ARQ + dual-threshold single-bit
recovery; the multi-decoder offset bank; the NinoTNC-compatible PSK maps (incl. the 0.72 V.26B π/4
fix) and IL2P+CRC; Dire Wolf's RUH demod + DPLL DCD; the ARDOP sorted-bins busy detector (fixed);
calibration tones; ackmode semantics.

**Fix by design:** headless DCD (display-decoupled); DCD surfaced to the host (ICarrierSense — no
soundmodem anywhere does this today); TX-complete from sample accounting; real resampling; classic
p-persist; per-channel state isolation (no `blnBusyStatus` global, no shared EMA state); config as
node config with validation (no GUI-rewrites-the-ini); device-paced TX (the "fart mode" class of
bugs); the KISS control-frame no-op (in-process makes it moot; honour params if the KISS-TCP
front-end ships).

**Drop:** Qt; ARDOP/OFDM/MPSK/RSID (≈25 k lines + a 908 KB table file); the internal AX.25
L2/digipeater/AGW/host stack (~10 k lines — PDN owns all of it); AGW & Mgmt & RHP & 6pack & UDP
audio interfaces; the Delphi string/TStringList emulation; FFTW; 191 globals and the 220 MB DET
array (per-configured-channel allocation instead).

## 11. Proposed scope and phasing

- **Spike 0 — feasibility bench (1–2 days of rig time):** run the DSP bench on a Pi 4/5; ALSA
  capture/playback soak on a CM108-class dongle (period size, xruns, TX-release latency);
  record a **WAV corpus** through the NinoTNC bench rig (both TNCs + Tait radios exist; capture
  each NinoTNC mode as clean audio + attenuated/noisy variants) — this corpus is the
  decode-regression suite the whole effort currently lacks, and the only honest way to demonstrate
  "no regression vs NinoTNC/QtSM". Include WA8LMF Track 2 for AFSK (redistribution terms TBC).
- **Phase 1 — RX-only, offline:** `Packet.SoundModem` decoding WAV/stdin: AFSK1200 + IL2P+CRC
  300 BPSK first (the 40 m workhorse), HDLC/CRC, IL2P from spec with its test vectors. Exit: corpus
  decode rates ≥ QtSM and ≥ NinoTNC on the same recordings.
- **Phase 2 — live RX + DCD + waterfall:** ALSA capture, `kind: soundmodem` port (RX-only),
  `ICarrierSense` through the supervisor probe, spectrum SSE + browser waterfall + calibration UI.
- **Phase 3 — TX:** modulators, sample-paced PTT (RTS/DTR + CM108 + IRadioControl), CSMA in-modem,
  `ITxCompletionTransport`, bench-rig loop tests alongside the existing HardwareLoop suite; then
  the QPSK 2400/3600 and 9600 GFSK legs with **NinoTNC-interop as the exit criterion per mode**.
- **Phase 4 — breadth:** multi-decoder offset bank, FX.25, KISS-TCP front-end (head-end shape,
  aligned with whatever KISS-DCD extension gets agreed), Windows backend, shared-passband extra
  listeners.

Everything is spec-adjacent enough that the strict-by-default rule applies as usual: waveform/FEC
behaviour from the specs; QtSM/NinoTNC accommodations become named flags with paired tests where
they diverge (e.g. the 1650 Hz 3600-mode centre, IL2P long-frame CRC behaviour if the 2024 reports
reproduce).

## Decisions (Tom, 2026-07-14)

Taken the same day the research landed:

1. **Separate repo** (§12.1 option (c) + §4.4 hybrid method): the modem lives in
   **`packet-net/soundmodem`**, licensed **GPL-3.0-or-later**, consumed by packet.net via NuGet
   (the `Packet.Ax25.Sdl` pattern). Method inside that repo: port UZ7HO's proven demods from QtSM;
   implement IL2P/FX.25/RS from the open specs with their test vectors.
2. **packet.net is AGPL-3.0 throughout** (§12.1 repo contradiction, §4.3): the `ac2fe22` LICENSE
   switch was the intent — `PackageLicenseExpression` flipped to AGPL for all published packages,
   README §License + plan §3 updated to match.
3. **Phase 1 modes** (§12.3): **300 BPSK IL2P+CRC + 1200 AFSK** first; QPSK 2400/3600 and
   9600 GFSK follow with NinoTNC-interop exit gates.
4. **Channel model** (§12.4): **QtSM-style multiplex** — up to 4 logical modems per audio side,
   KISS sub-channel nibble addressing. (The PDN adapter can still expose each logical modem as its
   own `IAx25Transport`, so node ports stay simple; packet.net's hard-coded outbound KISS port 0
   will need addressing for the KISS-TCP shape.)
5. **Both deployment shapes are goals** (§5.2): a first-class integrated PDN port *and* a
   standalone headless KISS-TCP daemon for other apps, from one core library — headless-first.

Also clarified by Tom: QtSM's `Send CRC`/`Check CRC` = IL2P+CRC (IL2Pc), per IL2P spec draft v0.6 —
already reflected in §2.2/§3; spec+NinoTNC behaviour remains ground truth for our implementation.

Still open: §12.5 (status surface), §12.6 (Windows), §12.7 (plan placement); the §5.3 CSMA-ownership
recommendation stands unless vetoed.

## 12. Decisions needed (Tom)

1. **Repo licence** — resolve the AGPL-3.0 `LICENSE` vs MIT README/plan/`PackageLicenseExpression`
   contradiction (predates this work; commit `ac2fe22`). Then: accept a GPL-3.0-or-later
   `Packet.SoundModem` package (with the no-MIT-dependents rule), or require the
   from-specs-only route for everything (slower, loses UZ7HO's tuned demod, and a permissive
   clean-room claim still isn't credibly available given this research read the source)?
2. **Deployment shape** — in-process `kind: soundmodem` (recommended; only shape with native DCD
   today) vs out-of-process KISS-TCP; and CSMA ownership (recommended: modem-side p-persist via
   `ICsmaChannelParams`, host gate stays as outer guard).
3. **v1 mode list** — recommended: AFSK1200 + IL2P+CRC 300 BPSK first, then 2400/3600 QPSK +
   9600 GFSK with NinoTNC interop gates. Is 1200 AFSK actually wanted (APRS/legacy), or is the
   NinoTNC mode set the whole story?
4. **Multi-channel** — one-port-one-channel with shared audio devices (recommended) vs QtSM-style
   multi-modem channels; and does armhf (32-bit) matter for the audio-binding choice?
5. **Status surface** — how modem DCD/%DCD/%PTT reaches `/api/v1/*` + metrics + dashboard (the
   current surface is `radio:`-keyed).
6. **Windows** — in scope at all, and when?
7. **Plan placement** — Phase 10/11 territory vs a new SP-xxx spike; §11's "KISS modems only" scope
   line needs a §17-logged revision either way.

## 13. Verification debt (claims to check before relying on them)

- QtSM RUH-9600+IL2P+CRC ↔ NinoTNC mode 2 waveform interop: **unverified, highest-value test**.
- NinoTNC mode-id→waveform crosswalk (from kiss-collector metadata) vs NinoTNC docs.
- Pi 4/5 DSP throughput (extrapolated); SSE throughput at waterfall rates on a Pi; CM108 minimum
  stable period; TX-release latency mechanism.
- The static-analysis bug findings in §2.6 (uninitialised-buffer TX, `mon_frm` overflow, DualPTT,
  48 k stereo framing) — high plausibility, not runtime-proven.
- MeshCore SetHardware/event scheme as DCD-over-KISS precedent (web-sourced; check firmware).
- CM108-family chip identities/clock architecture (secondary sources).
- `/api/v1/ports/{id}/tuning/events` missing from the SSE token allowlist — allowlist content
  verified in source; whether it bites under `management.auth.enabled` needs a runtime check.
- UZ7HO's GPL grant: evidenced by Wiseman's headers/acknowledgement only — courtesy-confirm by
  email before shipping derived code.
- Whether QtSM upstream >0.0.0.76 fixed any headless gaps (no newer tree diffed).

## 14. Sources

- QtSoundModem 0.0.0.76, commit `9cd2735` (2025-11-10), `git://vps1.g8bpq.net/QtSM` — full-source
  survey (RX/demod, TX/PTT, DCD/busy, interfaces, FEC, waterfall/headless/audio, build/quality).
- This repo: `ICarrierSense`/`CarrierSenseGate`/`Ax25Listener` (OQ-012), `PortSupervisor`,
  `TransportConfig`, `Packet.Kiss*`, `PdnPortTuningApi`/`Program.cs` SSE wiring, plan §11/§17,
  `docs/releasing.md`, `Directory.Build.props`, `publish-libs.yml`.
- kiss-collector MCP (gb7rdg-node, 2026-06-22→07-14, ~273 k frames).
- bpq32 groups.io (766 "QtSoundModem" hits; notably msg 41453, topics 101856041/106010678) and OARC
  Discord (~760 hits) field-report sweep.
- Dire Wolf (GPL-2.0-or-later): `demod_afsk.c`/`demod_9600.c`/`fsk_demod_state.h`/`hdlc_rec.c` +
  the algorithm papers in `doc/` (incl. *A Better APRS Packet Demodulator* parts 1–2, the FX.25
  paper); decode-rate figures are direwolf's own.
- IL2P spec draft v0.6 (KK4HEJ, tarpn.net, 2024-03-16, with test vectors); FX.25 spec (Stensat
  2006, via web.archive.org).
- DSP micro-benchmark: .NET 10.0.9, TensorPrimitives/`Vector<T>`/scalar FIR, i7-3770 (session
  artefact; to be re-run on Pi).
