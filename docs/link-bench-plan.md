# AX.25 connected-mode link bench — implementation plan

A plan for a fresh context. Builds a low-level test rig that drives the AX.25 engine directly, so we can iterate in seconds (not multi-minute live windows), stress-test bulk connected-mode transfers, and isolate engine/transport-timing behaviour from the application, the channel, and the BPQ peer. Resume in the originating context with the rung-1 results.

## 0. TL;DR — build this first

A small bench tool (`tools/Packet.LinkBench`) that stands up **two AX.25 engines in one process**, connects them through a **pluggable channel**, pushes a configurable **bulk payload** (e.g. 64–256 KB) as connected-mode I-frames A→B, and prints a **metrics table**: wall-time, throughput, I-frame / supervisory counts, **duplicate-supervisory count**, retransmits, REJ/SREJ, completion + payload integrity. Run it losslessly first. It answers three things we have never cleanly isolated:

1. Does the engine bulk-transfer cleanly, and what is throughput vs `k`(MAXFRAME) / T1?
2. **Is the duplicate-supervisory-frame quirk (#79) engine-intrinsic?** — i.e. does pdn still burst 2–6 identical RR/REJ frames when talking to *itself* losslessly, with no BPQ and no channel? If yes, it's a pure emission bug; if no, it's an interaction.
3. **Does ackmode change anything measurable?** — same transfer, ackmode on vs off, compared with hard numbers instead of inference.

## 1. Why we are dropping below the node

We have been debugging a ~6-layer stack — BBS → FBB → SDL AX.25 engine → KISS → net-sim → LinBPQ — through a slow loop (build a .deb, deploy to the lab, wait for the real GB7RDG to dial, watch a multi-minute transfer, grep net-sim logs). Every question we actually care about (windowing, flow control, the #79 dup-supervisory burst, whether ackmode/pacing does anything, throughput vs the channel) lives in the **engine + transport timing** and needs none of the BBS, FBB, BPQ, or even a channel to reproduce. Too many confounders; far too slow. The bench removes the confounders one rung at a time.

## 2. Standing assumptions (set by Tom)

- **ackmode is ON and assumed.** The primary target modem (NinoTNC) supports KISS ACKMODE (G8BPQ extension, command `0x0C`: host sends `…|0xC|seqHi|seqLo|payload`, the TNC echoes `…|0xC|seqHi|seqLo` when the frame has been keyed onto the air). Working position: **session-based connected-mode packet fundamentally needs ackmode** — the host must know when its frame actually transmitted to size T1 and sequence its sends. So design ackmode as the **primary/default path**; an ackmode-off mode exists only as a control for the isolation experiment, not as a supported operating mode.
- **A DCD-over-KISS extension is coming** (Tom is working with Nino / the NinoTNC designer and others). It will let the modem tell the host when the channel is busy — i.e. real **carrier sense**, which ackmode is *not*. The bench must be **designed as the prototyping/validation vehicle for it**. See §8. Do not implement DCD yet.
- **ackmode ≠ carrier sense.** ackmode = "your frame finished transmitting." It says nothing about anyone else's transmission. Keep the two signals distinct in every abstraction: ackmode TX-complete (a reply to *my* send) vs DCD channel-busy/clear (an unsolicited modem→host state signal). The "don't transmit into a busy channel" problem is DCD's job, not ackmode's.

## 3. The channel abstraction — the heart of the rig

Put a **pluggable channel** between the two engines. Each engine talks to its own `IKissModem`; the modems are joined by a channel model. Three implementations, increasing fidelity:

1. **`InProcChannel` (primary, fastest, deterministic).** In-memory. Models per-frame **airtime** (`frameBits / baud` + txdelay/txtail), optional **half-duplex** (one transmitter at a time, with a configurable turnaround), optional **loss/bit-error**, and — because ackmode is assumed — emits the **ackmode TX-complete echo to the sender after the modeled airtime** (not instantly), so ackmode/pacing is exercised meaningfully. This is the engine + ackmode + (later) DCD prototyping vehicle. Runs in milliseconds; fully repeatable.
2. **`AxudpChannel` (lossless cross-check).** Two engines over `AxudpKissModem` on UDP loopback — real sockets, real async/serialisation, full-duplex, lossless. NOTE: AXUDP is a tunnel with **no TNC, so no ackmode echo** (`AxudpKissModem.SendFrameWithAckAsync` throws today). Use this as Tom's "lossless AXUDP" baseline to confirm the engine result isn't an in-proc artifact — ackmode-off by nature here.
3. **`NetSimChannel` (high-fidelity).** Two bench instances as KISS-TCP clients to **net-sim** (real AFSK1200, half-duplex shared medium, loss knob) with ackmode via net-sim's Samoyed modem. The realistic cross-check. Pin in §7.

`InProcChannel` is the one to lean on: it's where ackmode (assumed-on) and DCD (coming) are modelled and where iteration is fastest. `AxudpChannel` and `NetSimChannel` are cross-checks that the in-proc model isn't lying.

## 4. The experimental ladder

- **Rung 1 — engine ↔ engine, lossless** (`InProcChannel` lossless + `AxudpChannel`). Isolates pure engine flow-control/windowing and the #79 question. **Build this now.**
- **Rung 1b — engine ↔ engine, lossless but ackmode-timed** (`InProcChannel` with modeled airtime + ackmode echoes). Makes ackmode/pacing meaningful while still lossless/full-duplex; measures ackmode on-vs-off with hard numbers.
- **Rung 2 — net-sim channel** (`NetSimChannel`). Adds *only* the real channel (half-duplex AFSK, loss) between two simple rigs — no BBS/FBB/BPQ. Where ackmode, T2, MAXFRAME, CSMA actually get stressed. Also: directly measure net-sim's `0x0C` echo timing (instant-on-receive vs post-TX) to settle whether the current `PacingKissModem` is a no-op.
- **Rung 3 — full node ↔ real BPQ over net-sim.** What we already have (interop/integration). Not part of this rig.

## 5. Rung 1 — the bench (BUILD THIS)

**Project:** `tools/Packet.LinkBench` (a console app; tools are not shipped libraries). Reuses the existing engine — do not reimplement AX.25.

**Wire it from existing parts:**
- Engine: `Ax25Listener` / `Ax25Session` (the SDL-driven engine in `Packet.Ax25`). The per-session TX sink is `Ax25Listener.cs:922 SendBytes` (fire-and-forget, wired as the four `sendIFrame/sendSFrame/sendUFrame/sendUiFrame` dispatcher sinks at ~:968–973). You drive the engine; you do not modify it.
- Modems: `AxudpKissModem` (exists, `IKissModem`) for `AxudpChannel`; a new in-proc `IKissModem` pair joined by `InProcChannel` (model airtime + ackmode echo). The KISS-TCP ackmode path and the `PacingKissModem` decorator already exist on branch `feat/kiss-tcp-ackmode` / PR #388 — reuse `PacingKissModem` to wrap a bench modem when testing pacing.
- There is already an in-memory/loopback modem used by the console/interop tests — find it (grep the test projects for a loopback/in-memory `IKissModem`) and build `InProcChannel` from it rather than starting cold.
- Connected-mode data send/receive: use the **same path the node uses for interactive session data / forwarding** (the RHP↔AX.25 bridge / interactive console connect+send). Find that send API on the session rather than inventing one.

**Driver:** connect A→B; stream a payload of N bytes as connected-mode I-frames; drain on B; assert B received the exact bytes; clean DISC. Support unidirectional and bidirectional (both ends sending at once — exercises the turn/RNR logic).

**Knobs (CLI flags):** payload size; `k` (MAXFRAME) sweep; T1; T2; ackmode on/off; channel = inproc | axudp | netsim; (channel-model only) baud, half/full-duplex, loss. Make parameter sweeps scriptable (e.g. run `k = 1,2,4,7` × ackmode `{on,off}` and table the results).

**Metrics (per run):** wall-time; throughput (payload bytes / s); total frames TX & RX; I-frame count; supervisory count (RR/RNR/REJ/SREJ split); **duplicate-supervisory count** (consecutive identical supervisory frames emitted within a small window — this directly quantifies #79); retransmissions; window-stall time (time blocked on a full window); completion + payload-integrity check. Capture frame-level data by tapping the channel (the bench owns both modems) and/or the existing `NodeTelemetry` / `MonitorEvent` / `FrameTraced` stream.

## 6. Questions to answer / results to bring back

1. Engine bulk-transfer clean lossless? Throughput vs `k` and T1 (the sweep table).
2. **#79: engine-intrinsic?** Duplicate-supervisory count in the lossless `InProcChannel` and `AxudpChannel` runs. Nonzero with no channel and no BPQ ⇒ pure engine emission bug; raise against the SDL/engine accordingly.
3. **ackmode effect:** rung-1b on-vs-off throughput / retransmit / dup-supervisory deltas. Provable, not inferred.
4. Baseline numbers to compare rung 2 (net-sim) against.

## 7. Rung 2 — net-sim channel + the pinned version

Swap the bench transport to KISS-TCP → net-sim; run **two bench instances** (not the node), each a KISS-TCP client to a net-sim port; net-sim provides the channel. Then also instrument net-sim's `0x0C` echo timing.

**The ackmode-capable net-sim — pin this exactly (Tom asked for specificity):**
- Image: `ghcr.io/packethacking/net-sim` — the build that adds ACKMODE support in its **Samoyed** modem.
- Digest (pin by this, the tag is rolling): `sha256:aeac4a8942790b78e391a462fffc494530ffa3430af3c68023b54fa159f65aec`
- Git revision: `5ff8830925ae30e04de0219295d19763d81d0d1e` · source `https://github.com/packethacking/net-sim`
- Built: `2026-06-11T21:08Z` · published tag `:main` (rolling — do **not** rely on `:main`, pin the digest).
- This exact build is live on the lab (`pn-netsim`) and confirmed echoing. ACKMODE is handled transparently at the KISS layer (command `0x0C`), so there is **no `network.yaml` knob** — the host just sends ackmode frames and the Samoyed modem echoes TX-complete. (If you need the precise introducing commit, check the net-sim repo history around rev `5ff8830`.)
- Lab references: compose `/opt/netsim/compose.yml` (uses `:main` — switch to the digest for reproducibility); config `/opt/netsim/network.yaml`; ports gb7rdg `8101`, pdn `8102`; modem `afsk1200`.

**Open question to settle here:** is net-sim's TX-complete echo **immediate (on KISS-receive)** or **post-transmission**? If immediate, `PacingKissModem` (which awaits the echo before releasing the next frame) is a no-op and ackmode buys nothing for pacing. Measure by capturing the raw KISS `0x0C` round-trip timing on port 8102 vs the frame's modeled airtime.

## 8. DCD-over-KISS — forward design (DO NOT BUILD YET)

A coming KISS extension (Nino + others): a modem→host **DCD** message — channel busy / clear — i.e. real carrier sense, the piece ackmode is missing. The bench is the place to prototype and validate it. Design for it now, build nothing:

- The **channel abstraction (§3) is the seam.** `InProcChannel` already knows the channel's busy/idle state (it models half-duplex). Add to the modem→host interface a *designed-but-unimplemented* asynchronous **`ChannelState` signal** (Busy asserted / cleared), kept **distinct** from frame-RX and from the ackmode TX-complete echo. Stub it; do not wire it to the engine yet.
- Design a **pluggable TX gate**: a policy that may defer a transmission while DCD is asserted (CSMA-by-DCD). Leave it as a no-op policy today; the seam is what matters. Do not touch the SDL.
- Once Nino's KISS DCD message format is agreed, the bench plan becomes: implement the message in the bench's KISS codec, have `InProcChannel` (and a net-sim/modem build) emit it, wire the TX gate to it, and measure the collision/throughput improvement on the half-duplex rung — the controlled experiment ackmode could never give us.
- Keep ackmode (TX-complete, a reply) and DCD (channel-busy, unsolicited state) as separate first-class concepts throughout. Conflating them is exactly the mistake to avoid.

## 9. Working method & constraints (packet.net)

- **SDL state machine is a NuGet dependency** (`Packet.Ax25.Sdl`, built by `packet-net/ax25sdl`). Do **not** edit/regenerate it from this repo. If rung 1 shows an engine-emission bug (e.g. #79), raise it against `packet-net/ax25sdl`, publish, bump the pin in `Directory.Packages.props`. The bench *drives* the engine; it does not modify the SDL.
- **Spec-compliant by default; pragmatism is a named flag** on a `…ParseOptions`/`Quirks` record, never a silent default. The bench is a tool, but any engine/library change must follow this.
- **ax25-ts parity is CI-enforced**: do not change `Ax25ParseOptions` / `Ax25SessionQuirks` / `XidParseOptions` / the `Ax25Listener` options surface without the TS leg (or a recorded parity exception). The bench should not need to.
- **No GitHub-hosted CI runners** — every workflow job is `[self-hosted, Linux, X64]`.
- Tests for any library change; the bench tool itself is `tools/` and likely `[skip-plan]`, but a library/engine change updates `docs/plan.md` §17 in the same PR.
- Build: `dotnet build`. Test: `dotnet test --filter "Category!=HardwareLoop&Category!=Interop"`.

## 10. Current state / pointers

- **PR #388** (`feat/kiss-tcp-ackmode`): KISS ACKMODE on the live kiss-tcp transport (`KissTcpClient.SendFrameWithAckAsync`) + `PacingKissModem` decorator (serialises a port's TX, default-off, per-port `ackMode` config flag) + reconcile-planner restart classification. Full suite green. Reuse `PacingKissModem` in the bench.
- **Pacing caveat to remember:** `PacingKissModem` serialises *host→modem* delivery but does **not** wire TX-complete into the SDL's T1 — T1 still starts at enqueue. And net-sim may echo instantly (§7). So the current pacing may be a no-op on the air; the bench is how we find out.
- **Lab status (context):** the GB7RDG bulletin flood is draining *stably* on the lab (one session, 13+ min, zero drops, bulletins storing) with ackmode on + the new net-sim image — but the cause is **unproven** (new image vs ackmode not isolated). The bench settles it.
- **Live-fix context:** the flood stalls began as a CRLF R-line bug (pdn emitted bare-CR R: lines, crashing LinBPQ's packet-map reporter — fixed, pdn-bbs PR #1) and a half-duplex contention / #79 dup-supervisory problem (open). The bench targets the latter class.
- Key files: `src/Packet.Ax25/Session/Ax25Listener.cs` (`:922 SendBytes` sink), `src/Packet.Ax25/Session/Ax25Session.cs`, `src/Packet.Kiss/KissAckMode.cs` (0x0C codec), `src/Packet.Kiss/KissTcpClient.cs` (ackmode send), `src/Packet.Node.Core/Transports/{AxudpKissModem,PacingKissModem,ReconnectingKissModem}.cs`.

## 11. Success criteria for rung 1

- `tools/Packet.LinkBench` builds and runs a lossless A→B bulk transfer to completion with a verified-intact payload, over both `InProcChannel` and `AxudpChannel`.
- Emits the metrics table; a `k`×ackmode sweep is scriptable.
- Produces hard answers to the three §6 questions — in particular the **#79 engine-intrinsic** verdict (duplicate-supervisory count with no channel/BPQ) and the **ackmode on-vs-off** deltas — to bring back to the originating context.

## 12. Rung-1 / rung-1b results (2026-06-11)

The bench is built (`tools/Packet.LinkBench`, see its README) and every §11 criterion is met. All numbers below are 64 KiB unidirectional unless noted; payload integrity verified byte-exact and DISC clean on every ✓ run.

**Finding 0 — the bench immediately caught a real engine concurrency bug.** First 64 KiB ackmode-off sweeps stalled mid-transfer (B stuck at ~51 KiB on a lossless instant channel) or crashed inside `Ax25Session.PostEvent` (`Queue<T>.Enqueue` corruption). Root cause: `PostEvent`'s run-to-completion machinery was not thread-safe, yet the inbound pump, timer-expiry callbacks, and `SendData` callers all post concurrently — racing threads could dispatch concurrently or have a deferred event silently wiped by the dispatcher's `finally`-Clear. Fixed (reentrant `lock` around the dispatch loop, same-thread re-entrancy semantics preserved), regression-tested red-then-green (`PostEventConcurrencyTests`), full suite green. This instability class — silent lost events under concurrent load — is a strong candidate contributor to the lab flakiness. See plan.md §17 (2026-06-11).

**Q1 — engine bulk-transfer clean lossless? Yes (post-fix).** All k ∈ {1,2,4,7} × ackmode {on,off} complete intact on `InProcChannel` (zero-airtime): 256 I-frames, 0 retransmits, 0 REJ, 2–14 MB/s with T2=0. `AxudpChannel` cross-check (real UDP loopback) agrees: all k complete intact, 2–6 MB/s. **But with the engine-default T2=3 s, throughput on an infinite-speed lossless channel is 347 B/s** — the sender spends 100 % of the transfer window-stalled waiting out T2-coalesced acks (63 RRs / 256 I-frames; measured 189 s for 64 KiB; the k·paclen/T2 = 4·256/3 ≈ 341 B/s ceiling, observed). T2 is *the* lossless throughput limiter, exactly as designed post-#385; on a real half-duplex channel the coalescing pays for itself (see Q3).

**Q2 — #79 engine-intrinsic? No, under clean conditions.** Duplicate-supervisory count is **zero** in every lossless run on both `InProcChannel` and `AxudpChannel` — the engine talking to itself with no channel and no BPQ never emits back-to-back identical RR/REJ. Small dup bursts (dupS=2, burst 2) appear *only* in the rung-1b runs where mis-sized T1 provokes retransmit storms — i.e. duplicate supervisories are an interaction symptom (timer pressure / contention), not a pure emission bug. Raising against the SDL is not warranted on this evidence; reproduce the #79 burst shape on rung 2 (net-sim + BPQ) instead.

**Q3 — ackmode effect: nil on rung 1/1b, as the §10 caveat predicted.** ackmode on-vs-off deltas are noise in every sweep — including the rung-1b failures, which fail identically with pacing on. `PacingKissModem` serialises host→modem delivery, but T1 still starts at enqueue, so pacing alone cannot save a window whose airtime exceeds T1. The pacing question now moves to rung 2 (is net-sim's 0x0C echo post-TX or instant — the `AckReceiptTap` records every echo RTT for exactly this).

**Finding 4 — the big one for the lab: k≥4 at 1200 baud half-duplex self-destructs on default timers.** Rung 1b (`--baud 1200 --half-duplex --time-scale 50`, 16 KiB): k=1 and k=2 complete cleanly (real-equivalent ~46 / ~92 B/s); **k=4 and k=7 fail** — 22–52 retransmits, REJs, dup-supervisory bursts, transfer never completes. Arithmetic: one 256-byte I-frame is ~2.1 s of air at 1200 baud, so a k=4 window is ~8.5 s — but T1V defaults to 6 s **and starts at enqueue**, so T1 expires while the window is still leaving the transmitter. With T1 sized to cover the window (30 s real-equivalent), k=4/7 complete cleanly: 0 retransmits, 0 dups. T2 sweep at that operating point: T2=3 s beats T2=0 (32 RR keyups vs 64, ~9 % more throughput) — the #385 coalescing earns its keep on half-duplex. **Operating guidance for the lab (pending rung 2 confirmation): either keep k ≤ 2 on 1200-baud half-duplex, or raise T1 to ≥ (k+1)·frame-airtime + turnaround; and the durable fix is wiring TX-complete into T1 start (the ackmode→SDL seam §10 flagged), which removes the enqueue-vs-on-air gap entirely.**

**Next (rung 2):** point the bench at the pinned net-sim image (§7) — two KISS-TCP clients, ports 8101/8102 on a scratch net-sim instance (not the live lab ports) — to (a) measure the 0x0C echo timing vs modeled airtime, (b) re-run the k×T1 matrix on the real AFSK channel, (c) try to reproduce the #79 burst shape against LinBPQ.

## 13. Rung-2 results (2026-06-12) — real net-sim AFSK1200

Scratch instance of the §7-pinned ackmode image (`docker run … net-sim@sha256:aeac4a89…`, two nodes, one afsk1200 channel, ports 19101/19102 — the live lab untouched). All runs ackmode-on unless stated; per-run SSIDs isolate runs from each other's frames still draining through the channel.

**§7's open question is settled: net-sim's 0x0C echo is POST-transmission.** Echo RTTs: min ~110 ms (short supervisories), mean ~1.5–2.1 s, max ~4.7 s — consistent with real airtime for 256-byte I-frames at AFSK1200 plus queueing, not with an instant-on-receive echo. `PacingKissModem` genuinely serialises onto the air. (Pacing still measures ~nil for a single unidirectional sender — net-sim's port modem already serialises its own TX queue — so ackmode's real value remains the TX-complete *signal*, i.e. the future T1 wiring.)

**k×T1 matrix (4 KiB):** default T1=6 s is broken at every k on this channel — k=2 wastes 33 % of I-frames on retransmits, k=4 wastes 64 % (31 B/s). With T1 sized to the window: k=2/T1=15 s and k=3/T1=20 s are **clean (0 retransmits, 77–83 B/s)**; k=4/T1=30 s and k=7/T1=45 s still retransmit (4–7 per 4 KiB) — B's T2-delayed RR keys up into the tail of A's k-deep burst and collides (collision_mode silence). **That contention is the DCD gap (§8) quantified on the real channel: above k≈3 the receiver's ack and the sender's window overlap no matter what T1 says.** Best observed operating point: **k=2–3, T1 ≈ 15–20 s, T2 default — ~80 B/s clean**.

**The sustained-transfer killer, found and fixed (ax25spec#9).** 32 KiB runs died mid-transfer (~19 KiB, twice, near-deterministic) with A spontaneously emitting DM and going Disconnected while B was still acking. The bench's `--trace` + transition journal pinned it: A entered TimerRecovery once and never left (under sustained load V(S)≠V(A) at every F=1 checkpoint, so the figures' recovery branch stays in TimerRecovery), and **RC never resets on that branch** — ten T1 hiccups across 200 s of *progressing* transfer ratcheted RC to N2 → `t21_t1_expiry_yes_no` → DL-ERROR I → DM. The figures only reset RC on the fully-acked checkpoint (V(S)=V(A) → Connected) — exactly the already-filed **packethacking/ax25spec#9** (Thomas Habets, with the same fix shipped in rax25). Fixed engine-side as session quirk `Ax25Spec9AckProgressResetsRc` (default on; off under StrictlyFaithful): a T1 expiry that follows V(A)-advancing progress clamps RC to 1 *before* the RC=N2 guard, so RC counts **consecutive** failures; the clamp is at-expiry (not eager at ack time) because RC==0 doubles as `Select_T1`'s Karn sampling signal — an eager zero corrupted SRT/T1V and wedged the SREJ-under-loss suite. Paired tests in `Ax25Spec9RcResetQuirkTests` (clamp-on-progress / ratchet-without-progress / dead-link-still-dies-at-N2 / StrictlyFaithful-as-drawn).

**Result: sustained bulk transfer over net-sim with ackmode now completes.** 32 KiB, k=3/T1=20 s: intact payload, clean DISC, zero duplicate supervisories, 561 s — surviving 42 retransmit hiccups that pre-quirk killed the session at the 10th. Same at k=2/T1=15 s: 524 s, 63 B/s, only 15 retransmits (11 %) — the better sustained operating point. At sustained length even k=2 occasionally collides with B's T2 ack (the 4 KiB matrix's "0 retx" doesn't fully hold over 10 minutes of air), which is more weight behind DCD (§8) as the next lever. (#79 footnote: dupS=0 even on these long contended runs; the only dup bursts ever observed were length-2 under pre-fix T1 storms — still nothing resembling the 2–6-frame #79 bursts, reinforcing the "interaction with BPQ" hypothesis for rung 3.)

**Parity note:** `Ax25Spec9AckProgressResetsRc` is a new named flag on `Ax25SessionQuirks` — the ax25-ts counterpart (or a recorded parity exception) is required before interop CI passes, per the parity guard.

## 14. Rung-2 round 2 (2026-06-12) — TX-complete→T1 + the improved simulator

The follow-up campaign: implement the §10/§12 "durable fix" (TX-complete→T1, `kiss.t1FromTxComplete` — see plan.md §17 2026-06-12) and iterate net-sim/samoyed toward FM reality, then re-measure. Sim improvements landed (packethacking/net-sim#17): `time_scale` (receive-path use only for now — samoyed paces TX in wall clock, so ACKMODE/throughput tests need 1.0; samoyed-side speed factor tracked in net-sim#18), `collision_mode: noise` (FM-real garble instead of digital silence), per-link `squelch_open_ms` (RX squelch opening delay — makes TXDELAY load-bearing), `-rt-priority` (+ file-caps Dockerfile support; inert on unprivileged-LXC hosts, documented). samoyed fixes (packet-net/samoyed#1/#2): the ACKMODE echo now fires at PTT release rather than render time (pre-fix minimum observed echo 7.3 ms for ~350 ms-minimum frames; post-fix minimum 626 ms across every run — pacing is now honest end-to-end), and a tq lost-wakeup race (xmit thread parked forever on a non-empty queue) found by goroutine-dump archaeology and fixed with a red-green race-detector hammer. A third samoyed change (flush a departed KISS client's TX queue) **bisected as the cause of a permanent channel wedge** under repeated mid-transfer disconnects and was reverted (packet-net/samoyed#3; redesign tracked in net-sim#19 — the bench now settles the shared channel after unclean teardowns and per-run SSIDs isolate stragglers instead).

**Headline (4 KiB matrix, harsher channel: 80 ms squelch + noise collisions, everything on spec-DEFAULT T1V=6 s):** k=2 with `t1FromTxComplete` on — **77 B/s, 0 retransmits** vs 49 B/s / 10 retransmits without (+57 % throughput); k=4 — 53 vs 47 B/s and dups eliminated. The wiring matches the old hand-tuned T1=15 s optimum (§13) with no tuning, on a less forgiving channel. **Sustained 32 KiB, default timers, k=2, t1tx on: 537 s, 61 B/s, intact, clean DISC, zero duplicate supervisories** — 23 retransmits over 151 I-frames is the genuine collision rate of a carrier-sense-less half-duplex channel at sustained length, recovered cleanly every time (zero on the 4 KiB runs; DCD remains the next lever, §8). The operating guidance simplifies to: **spec-default timers + k=2–4 + ackmode + t1FromTxComplete**; raise k only when DCD lands.

Rung 3 (vs real LinBPQ) and the hardware NinoTNC channel (crossover baseline first, mixed-bus contention later, real radios after that) remain the next rungs.
