# Packet.Fuzz — findings

SP-004 fuzzer findings log. One entry per smoke / AFL run that surfaced
something. Each entry should include the input bytes (hex) and a stack-trace
summary.

See `Program.cs` for the harness and `corpus/` for the seed inputs.

## 2026-05-15 — initial smoke run

Default smoke configuration, N=1000 per parser, plus the 6 seed-corpus
files and 32 single-bit / single-byte mutations of each seed
(=198 fixed inputs + 1000 generated inputs per parser).

Targets:

- `Ax25Frame.TryParse(ReadOnlySpan<byte>, out _)` — direct AX.25 wire-format
  parser, KISS-form bytes (no flags, no FCS).
- `KissDecoder.Push(ReadOnlySpan<byte>)` — KISS framing decoder. The task
  brief asked for `KissFrame.TryParse`, but `KissFrame` is a plain record
  struct (port + command + payload) with no static parser — KISS is a
  stateful SLIP-style framer, not a one-shot parser, so the equivalent
  harness drives arbitrary bytes through a fresh `KissDecoder` for each
  input.

Result:

```
Packet.Fuzz smoke run: 1000 iterations per parser, seed=0xC0DEFEED

── Ax25Frame.TryParse ──
  1198 inputs (6 seed + 192 seed-mutations + 1000 generated) / 8 ms / 0 unhandled exceptions

── KissDecoder.Push ──
  1198 inputs (6 seed + 192 seed-mutations + 1000 generated) / 8 ms / 0 unhandled exceptions

════════ Summary ════════
  Ax25Frame.TryParse: clean — 1000 inputs, no throws.
  KissDecoder.Push: clean — 1000 inputs, no throws.
```

Extended runs (not part of the default smoke) — same harness with `N=100000`
and across five different RNG seeds (`0xC0DEFEED`, `0x00000001`, `0x0000002A`,
`0x0000270F`, `0x0012D687`, `0xFFFFFFFF`) — were also clean. ~600k total
fuzz inputs hit `Ax25Frame.TryParse`, and the same for `KissDecoder.Push`,
with zero unhandled exceptions in either.

### Interpretation

Both parsers handle malformed input by returning `false` / dropping bytes,
never by throwing — which is the documented contract:

- `Ax25Frame.TryParse` is already defended at its single throw-prone site
  (`Ax25Address.Read` can throw `ArgumentException` on bad address bytes);
  the catch-and-return-false path was added deliberately. There is an
  existing FsCheck property `TryParse_Never_Throws` in
  `tests/Packet.Ax25.Properties/Ax25ParserFuzzProperties.cs` that asserts
  the same thing across 2000 random inputs (amendment log
  2026-05-14 — "ax25: fuzz / property tests for the frame parser"). The
  SharpFuzz harness extends that property to a larger, more structured
  input distribution but reaches the same conclusion.
- `KissDecoder.Push` is by spec lenient (KissDecoder.cs:38–41 — "receivers
  should be lenient with malformed escape sequences. Drop the byte and
  continue."). There is no input that can drive it to throw at the framing
  layer.

So: nothing to fix from this run. Good baseline for future regression
detection — re-running the smoke after any parser change is cheap (<1 sec
for `N=10000`).

### Mutation strategies in the smoke run

The smoke generator mixes seven distributions:

| Strategy | Notes |
|----------|-------|
| Truncated buffer (0..16 bytes) | Probes the minimum-length guard |
| Around-min buffer (14..32 bytes) | Probes address-chain boundary conditions |
| Typical KISS payload (15..350 bytes) | Most-likely-real shape |
| Oversized buffer (350..4096 bytes) | Beyond AX.25 paclen |
| All-same-byte | FEND-only, 0xFF-only, 0x00-only edge cases |
| SLIP-pathological | Heavy bias to FEND / FESC / Tfend / Tfesc to stress the KISS escape state machine |
| Structured-AX25-like | 14-byte address pair + 0..9 digipeaters + ctrl + pid + info |

Plus replay + single-byte / single-bit mutation of the on-disk corpus.

## 2026-06-03 — v2.2 surface (extended / XID / segment) + a flagged finding

Extended the harness to the AX.25 v2.2 parsing/codec surface that the original
SP-004 fuzzer never touched: three new targets, three new seed corpora, and a
target-aware structured generator for each. New subcommands: `ax25ext`, `xid`,
`segment`. All three are replayed under `--smoke`.

Targets added:

- `Ax25Frame.TryParse(…, extended: true, out _)` — the EXTENDED / mod-128
  parse path (2-octet control field, Fig 4.1b). Fuzzed under both Strict and
  Lenient options.
- `XidInfoField.TryParse(…, options, out _)` — the XID information-field TLV
  codec (§4.3.3.7): attacker-controlled FI / GI / GL and a run of PI/PL/PV
  triples. Fuzzed under both Strict and Lenient; a successfully-parsed value is
  also re-encoded (must not throw).
- `Reassembler.Push` + the on-the-wire `SegmentationLayer.OnDataIndication`
  seam — hostile segment sequences (missing-first, out-of-sequence, empty /
  short fields, inner-PID edges), each replayed both directly and as a PID-0x08
  DL-DATA indication exactly as `Ax25Listener` drives it.

### Result

```
── Ax25Frame.TryParse(extended) ──   clean — no crash-class throws
── XidInfoField.TryParse ──          clean — no crash-class throws
── Reassembler.Push / SegmentationLayer ── clean — no crash-class throws
```

Default smoke (N=1000/target) plus an extended sweep — N=50000 across five RNG
seeds (`1`, `42`, `9999`, `1234567`, `0`), i.e. ~250k inputs per target —
were all clean. The extended-frame parser and the XID codec never throw on
arbitrary bytes (return `false` / a clean parse error), matching the documented
`TryParse` contract; FsCheck round-trip + never-throws properties for both were
added in `tests/Packet.Ax25.Properties/` (`Ax25ExtendedFrameRoundTripProperties`,
`XidInfoFieldProperties`, `SegmentationRoundTripProperties`).

### FINDING (flagged, not fixed) — `Reassembler.Push` throws on the wire path

The segment reassembler throws on a protocol-violating segment sequence, and
that throw is **reachable from the wire**: a received I-frame with PID 0x08
(segment) is handed by `Ax25Listener` → `SegmentationLayer.OnDataIndication` →
`Reassembler.Push(indication.Info.Span)` with no validation in between. A
hostile peer (or RF corruption) can therefore drive the reassembler to throw.

Reachable cases (each is a segment info field delivered as a PID-0x08
DL-DATA indication):

- input hex `05 AA BB` (non-First segment, no prior First):
  `InvalidOperationException` ("non-First segment received before any First…")
  at `Reassembler.Push` (`Segmenter.cs:276`) ← `SegmentationLayer.OnDataIndication`
  (`SegmentationLayer.cs:170`).
- input hex `` (empty info field on a 0x08-PID indication):
  `ArgumentException` ("segment info field must be at least 1 byte…")
  at `Reassembler.Push` (`Segmenter.cs:247`).
- input hex `80` (inner-PID First — the default quirk — with no inner-PID octet):
  `ArgumentException` ("first segment of an inner-PID series must be at least 2
  bytes…") at `Reassembler.Push` (`Segmenter.cs:257`).
- inputs hex `85 CC` then `03 DD` (out-of-sequence continuation, remaining 3
  where 4 expected): `InvalidOperationException` ("segment count out of
  sequence…") at `Reassembler.Push` (`Segmenter.cs:281`).

**Why this is flagged, not fixed.** The throw is the *documented and tested*
contract of `Reassembler.Push` — three asserts in
`tests/Packet.Ax25.Tests/Session/SegmenterTests.cs` pin it
(`Reassembler_Throws_On_Non_First_Segment_Without_Prior_First`,
`Reassembler_Throws_On_Out_Of_Sequence_Segments`, plus the short-field guard).
Making `Reassembler.Push` swallow protocol violations, or making
`SegmentationLayer` catch-and-drop / reset reassembly / raise a DL-ERROR, is a
**behavioural protocol decision**, not an obvious missing bounds check. Per the
workstream rule ("if the fix is non-obvious or would change protocol behaviour,
STOP and report"), it is reported rather than guessed at.

**Why it isn't a live crash today.** The one production caller, `Ax25Listener`,
wraps inbound dispatch in a catch-all (`Ax25Listener.cs` ≈ line 350 —
"swallowed: see Note on event-handler exceptions"), so the throw is swallowed and
the read loop survives. The cost is silent: the malformed segment's whole
DL-DATA indication is dropped, and a reassembler left mid-series can mis-react to
a subsequent valid continuation (the next legitimate `First` resets it, but an
out-of-sequence continuation in between throws again).

**Pinned regression.** `tests/Packet.Ax25.Properties/SegmentReassemblerThrowOnWireFindingTests.cs`
pins the *current* throwing behaviour at the wire seam for all four cases (plus a
happy-path counterpart). If the behaviour is deliberately changed, those tests
fail and force whoever changes it to update them and this finding together. The
fuzz `segment` target and the `*_Never_Crash_Throws_*` properties additionally
guarantee no *crash-class* exception (IndexOutOfRange / NullReference / …) ever
escapes — only the two documented contract types.

**Suggested resolution (for the owner to decide).** The §6.6 prose and Fig C5.2
(the receiver-side reassembler) treat a bad segment as a discardable error, not a
fatal one. The natural home for a clean rejection is `SegmentationLayer` (the
boundary process), which could catch the documented exceptions, reset the
reassembler, and either drop silently or raise a DL-ERROR — leaving the low-level
`Reassembler.Push` contract (and its existing tests) untouched. That is a small,
local change, but it changes observable behaviour, so it wants an explicit call.

## 2026-06-13 — higher-layer parsers added (APRS / AGW / NET/ROM) + a fixed AGW finding

Extended the harness past the AX.25/KISS/node surface to the three higher-layer
wire parsers that also take untrusted bytes: the APRS info-field decoders, the
AGW length-prefixed framing, and the NET/ROM network-layer datagram parsers.
Three new targets, three new seed corpora, a structured generator each. New
subcommands: `aprs`, `agw`, `netrom`. All replayed under `--smoke`.

Targets added:

- `FuzzAprsBytes` — every public APRS decoder (`AprsPositionDecoder`,
  `AprsMessageDecoder`, `AprsStatusDecoder`, `AprsObjectDecoder`,
  `AprsItemDecoder`, `AprsTelemetryDecoder`, `AprsMicEDecoder`) under both
  `Strict` and `Lenient`. All total by contract — return `false`, never throw.
- `FuzzAgwBytes` — `AgwFrame.Parse` (attacker-controlled little-endian
  data-length → body slice; throws `InvalidDataException` by contract, swallowed)
  plus `AgwFrame.TryReadDataLength` (a bool length-peek that must never throw).
- `FuzzNetRomBytes` — `NetRomPacket.TryParse`, `NetRomNetworkHeader.TryParse`,
  `NetRomTransportHeader.TryParse`, and `NodesBroadcast.TryParse` under both
  presets. All total by contract.

### Result

```
── APRS info-field decoders ──   clean — no throws
── AgwFrame.Parse ──             clean — no throws (after the fix below)
── NET/ROM wire parsers ──       clean — no throws
```

Default smoke (N=1000/target) and an N=50000 sweep are clean for all three after
the fix below. APRS and NET/ROM were clean from the start.

### FINDING (fixed) — `AgwFrame.TryReadDataLength` threw instead of returning false

The first 50k AGW sweep surfaced ~18.5k throws, all the same:

- input hex (50B): `000000004400F0004D304C54452D31000000474237524447000000000E0000800000000068656C6C6F206F76657220616777`
- `InvalidDataException: AGW frame advertises data length 2147483662 which would overflow Int32` at `Packet.Agw.AgwFrame.TryReadDataLength` (`AgwFrame.cs:124`).

Root cause: `TryReadDataLength` — a `Try*` method documented to *return false* on
unusable input — instead **threw** when the advertised data-length field exceeded
`int.MaxValue - HeaderSize`. It is wire-reachable: `AgwFrameStream`'s read loop
calls it on every full header off the socket (`AgwFrameStream.cs:107`), un-guarded,
so a peer sending a header whose 4-byte LE length field is ≥ `0x7FFFFFDC` throws
straight into the loop's catch-all (`AgwFrameStream.cs:140`) and tears down the
**whole** AGW frame stream. The default AGW bind is loopback, but `--listen-public`
exposes it.

**Fixed.** `TryReadDataLength` now returns `false` on the overflow case (consistent
with its existing too-short branch) — honouring the `Try*` contract. The streaming
caller's now-reachable false branch was updated to a clear "unusable length; stream
desynced" message (AGW has no frame delimiter, so a corrupt length can't be resynced
— tearing the stream down stays the correct outcome, just with an honest message and
no unhandled throw). `AgwFrame.Parse` keeps its own documented throwing contract for
the one-shot path — only the `Try*` peek was wrong. Regression pinned in
`tests/Packet.Agw.Tests/AgwFrameTests.cs`
(`TryReadDataLength_returns_false_for_an_overflowing_length_rather_than_throwing`),
and the `agw` fuzz target guards it from regressing.

## How to re-run

```sh
# Default smoke (1000 inputs per parser).
dotnet run --project tools/Packet.Fuzz -- --smoke

# Larger sweep with custom seed.
dotnet run --project tools/Packet.Fuzz -- --smoke 100000 1234

# Regenerate the seed corpus (ax25 + kiss + ax25ext + xid + segment +
# command + aprs + agw + netrom).
dotnet run --project tools/Packet.Fuzz -- --seed-corpus tools/Packet.Fuzz/corpus

# AFL/libfuzzer harness (requires afl-fuzz + libfuzzer-dotnet on PATH).
# One subcommand per target:
#   ax25 | kiss | ax25ext | xid | segment | command | aprs | agw | netrom
afl-fuzz -i tools/Packet.Fuzz/corpus/ax25 -o /tmp/ax25-out -- \
    dotnet tools/Packet.Fuzz/bin/Debug/net10.0/Packet.Fuzz.dll ax25 @@
afl-fuzz -i tools/Packet.Fuzz/corpus/agw -o /tmp/agw-out -- \
    dotnet tools/Packet.Fuzz/bin/Debug/net10.0/Packet.Fuzz.dll agw @@
```

## Format for future entries

```
## YYYY-MM-DD — short title

Run config (N, seed, parser target).

Findings:

- input hex (truncated to 64B if huge): `D408D08C8E8094E68C8C…`
- exception type + first stack frame: `IndexOutOfRangeException at Ax25Address.Read`
- short prose explaining the root cause, if known
```
