# Soundmodem 0.5.0 integration — design & phased plan

**Status:** proposal for review (plan-first; no code yet)
**Author:** Claude Code (investigation-backed)
**Scope:** bring `packet.net`'s `pdn-soundmodem` integration up to the 0.5.0 surface and
expose *all* of the new functionality.

---

## 1. Where we are

`packet.net` pins **`pdn-soundmodem 0.4.0`** (`Directory.Packages.props:64`) and consumes it
in-process through a single transport kind, `soundmodem`:

- config record `SoundModemTransportConfig` (`src/Packet.Node.Core/Configuration/TransportConfig.cs:349-385`) — 5 knobs: `device`, `captureRate`, `mode`, `frequency`, `ptt`
- transport `SoundModemFrameTransport` (`src/Packet.Node.Core/Transports/SoundModemFrameTransport.cs`)
- a **hardcoded 19-mode string switch** `CreateModem` (`SoundModemFrameTransport.cs:315-343`)
- validator `SoundModemValidator` (`NodeConfigValidator.Ports.cs:573-638`)
- factory arm (`TransportFactory.cs:137-140`), YAML/JSON converters, and a view-only waterfall in the web UI.

Soundmodem's released tag is **`v0.5.0`** (2026-07-18; HEAD is byte-identical for the shipped
library — the 2 later commits touch only `tools/NinoCompare`). So the catch-up target is **0.4.0 → 0.5.0**.

### 1.1 The root cause of the recurring churn

`pdn-soundmodem` ships **one** NuGet package (`Packet.SoundModem`) containing the modem/DSP
library. **The mode-name → modem factory, the DSP-rate derivation, the centre-frequency
gating, and the config DTOs all live in the daemon (`src/Packet.SoundModem.Daemon/`), not in
the library.** packet.net therefore carries its *own* private copy of that switch + rate logic +
validation — and the two copies have **already drifted**:

| | daemon (0.5.0) | packet.net (0.4.0) |
|---|---|---|
| `afsk1200-il2p-nocrc` | ✔ | ✗ missing |
| `bpsk300-multi` / `bpsk1200-multi` aliases | ✔ | ✗ missing |
| FreeDV datac ×6 | ✔ | ✗ missing |
| MS110D App-D ×8 | ✔ | ✗ missing |
| BPSK = differential diversity bank | ✔ (`BpskMultiModem`) | ✗ still single `BpskModem` |
| DSP rate for `c4fsk*`/`fsk4800`/`freedv`/`ms110d` | one predicate | **two inconsistent copies** (see §2.1) |

This is the reason "soundmodem moved on and packet.net has to change to expose it" keeps
recurring. **Two ways forward** — the plan below assumes (A) but strongly recommends we open
(B) upstream in parallel:

- **(A) Keep mirroring the daemon.** Fast, no upstream change; but every soundmodem release
  keeps forcing hand-edits here and drift will keep happening.
- **(B) Upstream a shared registry (recommended, strategic).** Lift the mode→factory map, the
  DSP-rate rule, and the frequency-gating rule out of the daemon into the **`Packet.SoundModem`
  library** as a public `ModemCatalog` (e.g. `IModem CreateModem(string mode, int dspRate, Action<byte[]> sink, ModemOptions opts)` + `int DspRateFor(string mode)` + `bool AcceptsCentreFrequency(string mode)` + `IReadOnlyList<string> KnownModes`). Then **both** the daemon and packet.net consume one source of truth and future modes need **zero** packet.net switch edits — only a pin bump. This is a small, self-contained PR against `pdn-soundmodem` and removes an entire class of future work. See §9.

> Recommendation: do (A) now to unblock, and file (B) as a `pdn-soundmodem` issue immediately so
> the next release collapses this integration to a pin-bump. The rest of this plan is written so
> that if (B) lands, phases 1–2 shrink to "call `ModemCatalog`".

---

## 2. Latent bugs to fix regardless (Phase 0)

These exist *today* at 0.4.0 and should be fixed in the same pass (they also block correct
handling of the new baseband modes).

### 2.1 DSP-rate logic is duplicated and inconsistent — **live bug**

- transport `DspRate` (`SoundModemFrameTransport.cs:311-313`): `fsk*` **and** `c4fsk*` → 48000, else 12000.
- validator `DspRate` (`NodeConfigValidator.Ports.cs:617-618`): **only** `fsk9600*` → 48000.

So for `fsk4800-il2p`, `c4fsk9600`, `c4fsk19200` the validator computes 12000 while the transport
uses 48000. The validator will **accept** a `captureRate` (e.g. `24000`) that `Open()` then
rejects with `ArgumentException` (`SoundModemFrameTransport.cs:73-78`). The 48000 default masks
it, but any non-default capture rate on those modes is a latent crash-on-bring-up.

**Fix:** extract a single shared `static int SoundModemRates.DspRate(string mode)` used by *both*
the transport and the validator (and the passband-frequency rule). This is also the natural home
if we go with §1.1(B). New baseband modes (freedv/ms110d) must be 48000 here.

### 2.2 Mode XML-doc drift

`SoundModemTransportConfig.Mode`'s XML doc (`TransportConfig.cs:363-372`) lists the 0.2.0-era
modes and **omits `c4fsk9600`/`c4fsk19200`** even though the validator + switch handle them.
Any generated docs/UI built from that comment miss two modes. Refresh it (and keep it generated
from `KnownModes` if possible).

### 2.3 YAML truncates `frequency` to int

`TransportConfigYamlConverter.cs:121` parses `frequency` via the `Int(...)` helper into a
`double` field, so fractional-Hz carriers can't be set via YAML (the JSON path preserves double).
Add a `Double(...)` helper / use it here. Low effort, real fidelity bug for carrier tuning.

---

## 3. The delta, grouped by how it fits packet.net's model

| Group | New capability | Fits the one-modem-per-AX.25-port transport? |
|---|---|---|
| **A** | FreeDV datac ×6, MS110D App-D ×8, `afsk1200-il2p-nocrc`, bpsk-multi aliases | ✔ yes — drop-in modes |
| **B** | BPSK differential frequency-diversity **bank** + `offsetPairs`/`offsetStepHz`/detector | ✔ yes — but changes existing `bpsk*` port behaviour |
| **C** | **Flex** radio device backend (`flex:…`) + multi-channel **IQ** RX front-end | ⚠ partly — new device path; IQ fan-out breaks one-port-one-device |
| **D** | **ARDOP** virtual TNC + **POCSAG** paging | ✗ **no** — session/paging layers, must be hosted services (§7) |

The four in-process seams every group hangs off (`SoundModemChannel`):

- **packet modems:** `channel.AddModem(int subChannel, Func<Action<byte[]>, IModem> factory)`
- **raw RX tap:** `channel.AddReceiveTap(Action<ReadOnlySpan<float>>)` (POCSAG, ARDOP)
- **raw TX:** `channel.EnqueueTransmit(Func<int,float[]> renderAtTxDelayMs, Action<Exception>? onError)`
- **audio/PTT device:** `channel.RunTransmitterAsync(IAudioOutput, IPttControl, ct)` + an RX read/decimate/`ProcessReceive` loop; Flex supplies `IAudioInput`/`IAudioOutput`/`IPttControl` directly, ALSA via `AlsaAudioInput`/`AlsaAudioOutput`.

**DSP-rate rule to mirror** (daemon `Program.cs:218-222`): **12000** for AFSK/BPSK/QPSK/ARDOP;
**48000** for `fsk*`, `c4fsk*`, anything containing `9600`, `freedv-*`, `ms110d-*`. FreeDV needs a
multiple of 8000, MS110D a multiple of 9600 — both satisfied by 48000.

---

## 4. Phase 1 — Group A: drop-in KISS modes

Add the new modes to packet.net's private switch + validator, mirroring the daemon exactly.
(If §1.1(B) lands, this becomes "delegate to `ModemCatalog`" and the switch goes away.)

**Modes to add** (beyond the current 19): `freedv-datac0/1/3/4/13/14` (6), `ms110d-wn0/1/2/3/4/5/6/13` (8),
`afsk1200-il2p-nocrc`, plus the `bpsk300-multi`/`bpsk1200-multi` aliases (these fall out of Phase 2).

**Factory calls to add to `CreateModem` (`SoundModemFrameTransport.cs:315-343`):**

```csharp
// FreeDV — no centre frequency; DSP rate 48000 (multiple of 8000)
"freedv-datac0"  => FreeDvDatacModem.Datac0(dspRate, sink),
"freedv-datac1"  => FreeDvDatacModem.Datac1(dspRate, sink),
"freedv-datac3"  => FreeDvDatacModem.Datac3(dspRate, sink),
"freedv-datac4"  => FreeDvDatacModem.Datac4(dspRate, sink),
"freedv-datac13" => FreeDvDatacModem.Datac13(dspRate, sink),
"freedv-datac14" => FreeDvDatacModem.Datac14(dspRate, sink),

// MS110D — RX is autobaud; the wn suffix selects TX waveform only. No centre frequency; DSP 48000 (multiple of 9600)
"ms110d-wn0" or "ms110d-wn1" or "ms110d-wn2" or "ms110d-wn3" or
"ms110d-wn4" or "ms110d-wn5" or "ms110d-wn6" or "ms110d-wn13" =>
    new Ms110dModem(dspRate, sink,
        new Ms110dTxSettings { WaveformNumber = int.Parse(mode["ms110d-wn".Length..]) }),
```

Reference (daemon): `Program.cs:297-305`. Signatures verified:
`FreeDvDatacModem.DatacN(int sampleRate, Action<byte[]> frameReceived)`;
`Ms110dModem(int sampleRate, Action<byte[]> frameReceived, Ms110dTxSettings? tx, Ms110dDemodOptions? rx)`
with `Ms110dTxSettings.WaveformNumber` default 6.

**Files to touch (Phase 1 checklist):**

1. `SoundModemFrameTransport.cs` — `CreateModem` switch (add arms); `DspRate` helper → route
   `freedv-*`/`ms110d-*` to 48000 (fold into the shared `SoundModemRates.DspRate` from §2.1).
2. `NodeConfigValidator.Ports.cs` — `SoundModemValidator.KnownModes` (`:575-582`) add the 14
   strings; the shared `DspRate`; and generalise the passband-frequency rule (`:606-609`) so
   **baseband modes** (`fsk*`, `c4fsk*`, `freedv-*`, `ms110d-*`) **reject a non-zero `frequency`**
   and carrier modes (`afsk*`, `bpsk*`, `qpsk*`) accept it — mirroring daemon gating
   (`Program.cs:244-247,259-265`).
3. `TransportConfig.cs` — refresh the `Mode` XML doc (§2.2).
4. Web editor mode list (Phase 5).
5. Tests: extend `SoundModemConfigTests` (unknown-mode / known-mode, frequency-gating) and
   `SoundModemFrameTransportTests` (a freedv + an ms110d bring-up round-trip).

**Risk:** low. These are additive, fit the existing model, and are exercised by round-trip tests.
Watch the capture-rate/DSP-rate interaction on the 48 kHz baseband modes (that's exactly the §2.1 bug).

---

## 5. Phase 2 — Group B: BPSK differential diversity bank

At 0.5.0, `bpsk300`/`bpsk1200` in the daemon are built by **`BpskMultiModem`** — differential
detection + a `2·offsetPairs+1`-branch frequency-diversity bank (default `offsetPairs=4`,
`detector=Differential`). packet.net still instantiates the single `BpskModem`. Migrating changes
the **decode behaviour of existing `bpsk*` ports** (better weak-signal / off-tune capture), so this
is a deliberate, flagged change, not a silent one.

**Switch changes (`CreateModem`):**

```csharp
"bpsk300"       => BpskMultiModem.Bpsk300(dspRate, sink, crc: true,
                       detector: pskDetector, carrierFrequency: freq ?? 1500, offsetPairs: offsetPairs),
"bpsk300-nocrc" => new BpskMultiModem(dspRate, sink, crc: false, centreFrequency: freq ?? 1500,
                       baud: 300, offsetPairs: offsetPairs, offsetHz: offsetStepHz, detector: pskDetector),
"bpsk1200"      => BpskMultiModem.Bpsk1200(dspRate, sink, crc: true,
                       detector: pskDetector, carrierFrequency: freq ?? 1500, offsetPairs: offsetPairs),
// aliases
"bpsk300-multi"  => /* same as bpsk300 */,
"bpsk1200-multi" => /* same as bpsk1200 */,
```

Verified ctor (`BpskMultiModem.cs:52-55`):
`BpskMultiModem(int sampleRate, Action<byte[]> frameReceived, bool crc = true, double centreFrequency = 1500, int baud = 300, int offsetPairs = 4, double? offsetHz = null, PskDetector detector = PskDetector.Differential)`;
`offsetHz` defaults to `baud/40` when null; `offsetPairs:0` collapses to a single plain modem.
DSP rate stays **12000** (unchanged from today's bpsk path).

**New config knobs** on `SoundModemTransportConfig` (all optional, defaulted to the daemon's
values so behaviour matches soundmodem out of the box):

| Key | Type | Default | Maps to |
|---|---|---|---|
| `offsetPairs` | int? | 4 | `BpskMultiModem` bank width (`ModemConfig.OffsetPairs`) |
| `offsetStepHz` | double? | null → `baud/40` | `offsetHz` (`ModemConfig.OffsetStepHz`) |
| `pskDetector` | string? (`coherent`\|`differential`) | `differential` for bpsk | `PskDetector` |

**Files to touch (Phase 2 checklist):**

1. `TransportConfig.cs` — add the three fields to `SoundModemTransportConfig`; update
   `DescribeEndpoint()` if useful.
2. `TransportConfigYamlConverter.cs` — read (`:116-124`) + write (`:234-246`) the new fields
   (use the `Double` helper from §2.3 for `offsetStepHz`).
3. `TransportConfigJsonConverter.cs` — generic `Write`; add to the `soundmodem` `Read` arm (`:43`) if fields aren't auto-bound.
4. `NodeConfigValidator.Ports.cs` — validate ranges (`offsetPairs >= 0`, sane `offsetStepHz`,
   `pskDetector` ∈ {coherent, differential}); only meaningful for `bpsk*`/`qpsk*`.
5. `SoundModemFrameTransport.cs` — thread the knobs into `CreateModem`; default `pskDetector`
   per-family (bpsk→Differential, qpsk→Coherent) exactly like daemon `Program.cs:251-252`.
6. Tests + web editor (Phase 5).

**Risk:** medium — it changes existing ports' decode path. Mitigations: keep `offsetPairs:0` as an
explicit "old single-modem behaviour" escape hatch; call the behaviour change out in release notes;
add a round-trip test asserting `bpsk300` now reports a `…-multi<N>` mode string.

---

## 6. Phase 3 — Group C: Flex device + multi-channel IQ

### 6.1 Flex device backend (`flex:<radio>[:slice][@station]`)

Today `SoundModemFrameTransport.Open` (`:116-141`) hardcodes the ALSA trio — `AlsaCaptureSource`
(a private nested `ISoundModemCapture`, `:364`), `AlsaAudioOutput`/`UpsamplingAudioOutput`
(`:128-130`), and a PTT parsed from the spec string (`:345-361`). The device string is passed
**verbatim** to ALSA; **nothing inspects a prefix**. The ctor already takes the three seam
interfaces and does zero ALSA-specific work, so swapping the backend is localized.

**Design:** extract a device-string resolver that returns the capture/output/PTT triple, and add a
`flex:` branch:

```csharp
if (FlexDevice.IsFlex(config.Device)) {
    var flex = await FlexDevice.OpenAsync(config.Device, dspRate, packetBuffer: 3, tuning, ct);
    // flex.Input : IAudioInput, flex.Output : IAudioOutput, flex.Ptt : IPttControl
    // Flex self-keys → reject any configured `ptt` (mirror daemon Program.cs:356-361)
    capture = new AudioInputCaptureAdapter(flex.Input); // IAudioInput(float) -> ISoundModemCapture(short), or refactor RxPump float-native
    output  = flex.Output;
    ptt     = flex.Ptt;
    // keep the FlexRuntime alive for the transport's lifetime; dispose it in DisposeAsync
} else { /* existing ALSA path */ }
```

Verified surface: `FlexDevice.IsFlex(string)`, `FlexDevice.OpenAsync(string device, int dspRate,
int packetBuffer, FlexTuning? tuning, CancellationToken) → FlexRuntime`; `FlexRuntime` exposes
`Input`/`Output`/`Ptt` (the same `M0LTE.Radio.Audio` seams the channel already consumes) and is
`IAsyncDisposable`. Daemon reference: detect `Program.cs:226`, open+bind `:462-479`, RX loop
`:521-546`, TX `:515`, PTT-conflict guard `:356-361`.

**Two design wrinkles:**

- **Async construction.** `Open` is currently a **static sync** method; `FlexDevice.OpenAsync` is
  async. `TransportFactory.CreateAsync` is already async, so add an `OpenAsync` and route the
  factory arm to it (keep the sync `Open` for ALSA, or make both async).
- **Capture seam mismatch.** `FlexRuntime.Input` is `IAudioInput` (float), but packet.net's RX pump
  reads `ISoundModemCapture` (`short` + normalise). Options: (a) a thin `IAudioInput → ISoundModemCapture`
  adapter (minimal blast radius, recommended for Phase 3); (b) refactor `RxPump` to consume
  `IAudioInput` float-native (cleaner, matches the library, larger diff). Recommend (a) now, (b) as
  a follow-up cleanup.

**New config** to expose Flex tuning (headless slice): a nested `flex` block on
`SoundModemTransportConfig` mirroring `FlexTuning`/daemon `FlexConfig`:
`frequency` ("14.100000"), `antenna` ("ANT1"), `mode` ("DIGU"), `daxChannel` ("1"). Only meaningful
when `device` starts `flex:`; validate that pairing. (`captureRate` is ignored for Flex — DAX
supplies its own clock; the validator must not demand a multiple relationship for `flex:` devices.)

**Files to touch (Phase 3a checklist):**

1. `SoundModemFrameTransport.cs` — add `OpenAsync`, device-string resolver, `flex:` branch,
   adapter, and keep the `FlexRuntime` in the instance for disposal.
2. `TransportFactory.cs` — call `OpenAsync` for the soundmodem arm.
3. `TransportConfig.cs` + converters + validator — the `flex` sub-block; `captureRate`-exemption
   for Flex; `ptt`-must-be-empty for Flex.
4. `Directory.Packages.props` — `pdn-soundmodem` already pulls `M0LTE.Flex`/`M0LTE.Radio.Audio`
   transitively; add a direct `PackageReference` only if we touch those types directly.
5. Tests: a `flex:mock` bring-up (soundmodem ships `MockFlexRadio` via `flex:mock`) → RX/TX
   round-trip with no hardware.

**Risk:** medium-high — new device path, async refactor, adapter. The `flex:mock` runtime makes it
**testable in CI without a radio**, which de-risks it a lot.

### 6.2 Multi-channel IQ RX — separate, deferrable sub-project

`MultiChannelReceiver` + `ChannelSpec(OffsetHz, Decimation, Taps, AudioCentreHz)`,
`DigitalDownconverter`, `IIqSource`, `BufferIqSource` are **library primitives that even the
soundmodem daemon does not wire** (grep-confirmed). packet.net would be the *first* consumer.
The model — one Flex IQ stream → `MultiChannelReceiver` → N `IAudioInput` → N `SoundModemChannel`
— **breaks packet.net's one-port-one-device assumption** (multiple ports share one physical Flex
IQ source). That needs its own design (port grouping, shared-device lifecycle, DCD semantics per
sub-channel). **Recommend: descope from this pass; file as a dedicated follow-up design.** Phase 3a
(single-slice Flex audio) delivers Flex usefulness without it.

---

## 7. Phase 4 — Group D: ARDOP + POCSAG as hosted services (NOT transports)

**Architecture verdict (load-bearing):** ARDOP and POCSAG **must not** be `TransportKinds` values
with a `TransportFactory` arm. `IAx25Transport`'s own contract doc
(`src/Packet.Ax25.Transport.Abstractions/IAx25Transport.cs:22-27`) *explicitly excludes*
session-owning layers — it names AGW connected-mode, SEQPACKET, and **VARA's ARQ** as "a different
(session) layer [that does] NOT implement this interface; do not force them under it."
`ICsmaChannelParams` repeats it ("…and VARA have nothing to set"). ARDOP is the direct VARA
analog (an HF **ARQ session** that owns retransmission/sequencing over its own TCP host ports);
POCSAG is a one-way **paging line protocol**, not AX.25 frames at all.

packet.net already has the right home: **config-driven hosted TCP services** (distinct from the
transport union). Template = **`RhpServerHostedService`** (`src/Packet.Rhp2.Server/RhpServerHostedService.cs:22`,
`ReconcileAsync` on `config.OnChange` `:51-80`), registered via `AddHostedService` in
`src/Packet.Node/Program.cs` (beside RHP at `:616`). Peers: telnet console, MQTT emitter, mDNS,
Tailscale sidecar, OARC reporter — all `IHostedService`/`BackgroundService` with their own config.
The transport doc-comment even states "the telnet console is **not** a transport."

So each of ARDOP and POCSAG becomes: a **top-level config block** + an **`IHostedService`** that owns
its **own** `SoundModemChannel` + audio device (ALSA or Flex, reusing the Phase-3 resolver) + a
**TCP server**, with independent start/stop/hot-reload decoupled from the AX.25 port set.

### 7.1 POCSAG paging service

Simplest of the two. Verified surface: `PagingTcpServer(SoundModemChannel channel, int port = 8106,
int baud = 1200, PocsagPolarity polarity = Normal, IPAddress? bind = null)`; `.Start()`;
`IAsyncDisposable`. It attaches itself to the channel (`AddReceiveTap(decoder.Process)` for RX,
`EnqueueTransmit(...)` for TX) and speaks a tiny UTF-8 line protocol
(`PAGE <ric> <function> ALPHA|NUMERIC|TONE [text]` → `OK/ERR`; broadcasts `HEARD …`). Daemon
reference `Program.cs:441-451`; config `PagingConfig { Port=8106, Baud(512|1200|2400), InvertPolarity }`.

**packet.net service:** `PagingHostedService` reconciling a `paging` config block
`{ device, port, baud, invertPolarity, ptt }` → build a 12 kHz `SoundModemChannel` on the resolved
device, `new PagingTcpServer(channel, …).Start()`, run the channel transmitter + RX loop.

### 7.2 ARDOP virtual TNC service

**This is the reference-model piece Tom asked about — see §7.3 for how BPQ does it.** ARDOP in
soundmodem is a **wholly separate object**, not an `IModem`. Verified surface
(`M0LTE.Ardop 0.1.0`, transitive via `pdn-soundmodem`):

- `ArdopHostTnc(string captureDevice, string playbackDevice, …)` with a settable
  `Transmitter` delegate; `ProcessReceive(ReadOnlySpan<float>)`, `Poll()`, `ProcessCommand`,
  `AcceptHostData`, `DisposeAsync`.
- `ArdopHostServer(ArdopHostTnc tnc, int port, IPAddress? bind, bool ownsTnc)` — the
  **ardopcf-compatible TCP host** (command socket on `port`, data socket on `port+1`); `.Start()`,
  `LocalCommandPort`/`LocalDataPort`. Also a device-driven convenience
  `ArdopHostServer.ForAudio(IAudioInput, IAudioOutput, IPttControl, port, …)`.
- `ArdopModulator.SampleRate` = **12000** (ARDOP's native rate; the channel must be 12 kHz).

Daemon wiring (`Program.cs:404-439`): ARDOP is **exclusive** with `--modem`/`--paging`
(`DaemonConfig.cs:161-165`, `Program.cs:207-211`); the KISS server is not started; CSMA persistence
is forced to 255 (`:409`); it binds to the channel via `channel.AddReceiveTap(tnc.ProcessReceive)`
(RX) + `Transmitter = audio => channel.EnqueueTransmit(...)` (TX, `short[]`→`/32768f` floats), then
`new ArdopHostServer(tnc, port, ownsTnc:true).Start()`.

**packet.net service:** `ArdopHostedService` reconciling an `ardop` config block
`{ device, port=8515, ... }`. Two viable constructions:
- (i) **mirror the daemon** — own a 12 kHz `SoundModemChannel` on the resolved device, tap it, and
  run `ArdopHostServer(tnc, port, ownsTnc:true)`; or
- (ii) **`ArdopHostServer.ForAudio(input, output, ptt, port)`** directly against the resolved
  audio seams (simpler, since an ARDOP service owns its device exclusively anyway).

Recommend (ii) for a dedicated-device ARDOP service unless we need the channel's CSMA/DCD surface.

**The key product point (elaborated in §7.3):** packet.net would be **the ARDOP TNC**, exposing the
ardopcf host-port protocol over TCP. External ARDOP *hosts* — BPQ, Pat, any ardopcf client — then
drive packet.net's soundcard/Flex as an ARDOP modem, unchanged. This is a *provider* role, exactly
symmetric to how BPQ consumes ardopcf today.

### 7.3 Reference model — how BPQ integrates ARDOP

BPQ is the canonical example, and it validates the "packet.net is the TNC/provider" design.

**Topology: BPQ is the HOST; ardopcf is the TNC.** ARDOP ships as a standalone virtual-TNC
process (`ardopcf` on Linux, `ARDOP_Win.exe` on Windows) that does all the DSP/soundcard/PTT work
and exposes a **TCP host interface**. BPQ connects to it over loopback TCP and drives it — two
separate processes. (G8BPQ: "the ARDOP driver for BPQ32 is defined as an External port" pointed at
"the IP address and Port of the ARDOP TNC" — cantab.net/…/ARDOP.html.)

**The host interface is two TCP sockets** — this is the interop contract packet.net must expose:

| Socket | Default port | Framing |
|---|---|---|
| Command/control | **8515** | `<CR>`-terminated (0x0D) ASCII commands + replies + async notifications, case-insensitive |
| Data | **8516** (always command **+1**) | binary `[2-byte big-endian length][payload]`; TNC→host payloads carry a 3-char `ARQ`/`FEC`/`ERR`/`IDF` tag |

Host→TNC control commands: `INITIALIZE`, `MYCALL`, `GRIDSQUARE`, `PROTOCOLMODE {ARQ|FEC|RXO}`,
`LISTEN`, `ARQBW`, `ARQCALL <target> <n>`, `DISCONNECT`, `ABORT`, …; async TNC→host: `CONNECTED`,
`DISCONNECTED`, `NEWSTATE`, `PTT`, `BUSY`, `PING…`. To transmit, the host writes payload to the
**data** socket and the TNC keys PTT + sends automatically — **the host never keys TX directly**
(PTT is the TNC's job). Sources: ardopcf `docs/Host_Interface_Commands.md`, `docs/Commandline_options.md`
(`ardopcf <host-tcp-port> [capture playback]`, defaults 8515/8516), Winlink ARDOP host-mode spec.

**BPQ config (`bpq32.cfg`) — a dedicated `DRIVER=ARDOP` External port:**

```
PORT
 ID=ARDOP
 DRIVER=ARDOP
 INTERLOCK=4
CONFIG
 ADDR 127.0.0.1 8515 PTT CI-V PATH C:\ARDOPTNC\ARDOP_Win.exe   ; PATH ⇒ BPQ auto-launches the TNC; omit it to attach to an already-running TNC
 CAPTURE IC-7300 (USB Audio Codec)
 PLAYBACK IC-7300 (USB Audio Codec)
 LISTEN TRUE
 PROTOCOLMODE ARQ
 ARQBW 2000MAX
 GRIDSQUARE JO11VN
ENDPORT
```

`CONFIG ADDR <ip> <port>` is the **command** socket endpoint (data = port+1, inferred; BPQ needs no
data-port config). The `PROTOCOLMODE`/`ARQBW`/`LISTEN`/`GRIDSQUARE`/… lines are pushed to the TNC as
host commands at startup, mapping 1:1 to the interface commands above. Sources:
cantab.net/…/ARDOP.html, packet-radio.net/bpq32-example-ardop-port/, `g8bpq/LinBPQ/ARDOP.c`.

**How ARDOP sits in BPQ's stack — AX.25 is _replaced_, not tunnelled.** An ARDOP port is a special
**single-session connected-mode/ARQ port** (handled like WINMOR/Pactor), *not* a normal AX.25 port.
BPQ does **not** run AX.25 SABM/UA/I-frames over ARDOP; ARDOP's own error-corrected ARQ session
**is** the link layer, and BPQ layers its node / NET-ROM / BBS-forwarding **byte stream directly
over it**. What flows over the data socket is an application byte stream, **not AX.25 frames and not
KISS**. The port is single-session: a user `ATTACH`es the port to allocate the TNC, then `C <call>`
places an ARQ call (inbound via `LISTEN TRUE` → `CONNECTED`). Intended use is HF mail forwarding
between BBSs. (cantab.net/…/ARDOP.html: "The TNC only supports a single connection (unlike ax.25
packet); an ARDOP port must be allocated to a user before making connects.")

> **This independently reinforces the §7 verdict.** ARDOP is single-session and *owns the session
> byte stream* — a second, orthogonal reason (beyond `IAx25Transport`'s explicit exclusion) that it
> cannot be a packet.net frame-transport. It is a session layer, full stop.

**Implication for packet.net — be the TNC/provider, and BPQ drives it unchanged.** The host
interface is a pure byte-level wire protocol; any process that faithfully exposes it is a drop-in
ARDOP TNC. **soundmodem already gives us exactly that**: `M0LTE.Ardop`'s `Host/` layer is a
**byte-compatible port of ardopcf's `TCPHostInterface.c`** (command port `N`, data port `N+1`,
ardopcf's exact command/reply/fault spellings), validated by a 107-command transcript diff against
a live ardopcf **and** a real Pat 1.0.0 B2F exchange (`M0LTE.Ardop/PROVENANCE.md`,
`pdn-soundmodem/docs/ardop-design.md` §7). So packet.net's `ArdopHostedService` (§7.2) running
`ArdopHostServer(tnc, port)` **exposes the ardopcf host ports directly** — a BPQ operator points
`CONFIG ADDR <packet.net-ip> <port>` at it (omitting `PATH`, so BPQ attaches rather than launches)
and it works with **no BPQ code change**. Pat, Winlink Express, ARIM, hamChat drive it identically.

The role is symmetric to how BPQ consumes ardopcf today — packet.net simply plays the ardopcf
(TNC) side instead of the BPQ (host) side. Note this means packet.net's *own* AX.25/NET-ROM stack
does not consume the ARDOP link (it's a service offered to external hosts); a future "packet.net as
ARDOP host/session-consumer" is a distinct, larger piece and explicitly out of scope here.

**Byte-exactness caveat:** hosts key off exact strings — `NEWSTATE`'s trailing space, the
`not recoginized` misspelling, the PING-in-RXO double reply, etc. `M0LTE.Ardop` already reproduces
these; packet.net gets them for free by consuming that library rather than re-implementing the host
interface.

### 7.4 Files to touch (Phase 4 checklist)

1. New `src/Packet.Node.Core/…/PagingHostedService.cs` and `ArdopHostedService.cs` (template:
   `RhpServerHostedService`).
2. New top-level config blocks `paging` / `ardop` on the node config (NOT `TransportConfig`
   subtypes) + their validators + YAML/JSON binding; exclusivity rules (ARDOP wants a dedicated
   device; can't share a soundcard with a soundmodem port on the same device).
3. `src/Packet.Node/Program.cs` — `AddHostedService<PagingHostedService>()` /
   `AddHostedService<ArdopHostedService>()` beside the RHP registration (`:616`).
4. Reuse the Phase-3 device resolver for ALSA/Flex.
5. Optionally add a direct `PackageReference` to `M0LTE.Ardop` / `M0LTE.Pocsag` for clarity
   (they arrive transitively via `pdn-soundmodem`).
6. Tests: line-protocol round-trips (POCSAG `PAGE`/`HEARD`), an ARDOP host-port smoke test.
7. Web UI + docs (Phase 5): these are node-level services, so they belong in a "Services" area of
   the UI, not the Ports editor.

**Risk:** high — largest surface, new subsystem, new config shape, exclusive device ownership,
external-protocol compatibility (ardopcf host interface). Strongly favours landing after A/B/C.

---

## 8. Phase 5 — Web UI + docs

### 8.1 Ports editor can't create/edit a soundmodem port (pre-existing gap)

The TS type + `transportDefaults` + `transportDesc` already know `soundmodem`
(`web/packetnet-ui/src/lib/types.ts:30-32`, `screens/ports.tsx:419-427,70-87`), but the create/edit
`<Select>` **doesn't list it** (`ports.tsx:562-568`) and there is **no soundmodem field block**.
So today a soundmodem port is config-file-only. To fix + expose the new knobs:

1. `ports.tsx:562` — add `<option value="soundmodem">`.
2. `ports.tsx:571-622` — add a `{t.kind === "soundmodem" && (…)}` block: Device, Capture-rate,
   **Mode `<Select>`** (new `SOUNDMODEM_MODES` constant mirroring `SoundModemValidator.KnownModes`),
   Frequency, PTT, and the Phase-2 knobs (`offsetPairs`/`offsetStepHz`/`pskDetector`) shown only for
   `bpsk*`/`qpsk*`, and the Phase-3 `flex` sub-fields shown only when `device` starts `flex:`.
3. `lib/mock.ts:702-703` — add `soundmodem` to `KIND_LABEL` **and** `KIND_USES_KISS` (both omit it
   today → blank badge + `undefined` usesKiss).
4. `screens/setup.tsx:52-66` — the exhaustive `switch` already has a `soundmodem` case (`:63-65`);
   no change unless we surface it in the wizard picker.
5. `types.ts` — extend `SoundModemTransport` with the new optional fields to match the server record.
6. The `/api/v1/ports/{id}/quality` endpoint (`PdnPortQualityApi.cs`) currently has **no UI
   consumer** — optionally surface FrameQuality in the port detail view.

### 8.2 ARDOP/POCSAG services UI

New "Services" config surface (not Ports) for the `ardop`/`paging` blocks — or, minimally, document
them as config-file features first and add UI later.

### 8.3 Docs

- Ship a worked `kind: soundmodem` example config (none exists today — grep of `examples/`,
  `guide/`, `operating/` finds nothing).
- Update `docs/node-api.yaml` for any new fields/endpoints.
- Node guide: the new modes (FreeDV/MS110D), the BPSK bank behaviour change, Flex device strings,
  and the ARDOP/POCSAG services.

---

## 9. §1.1(B) — the shared-catalogue upstream (recommended parallel track)

Concretely, a small PR against `pdn-soundmodem` adding to the **library**:

```csharp
namespace Packet.SoundModem.Modems;
public static class ModemCatalog {
    public static IReadOnlyList<string> KnownModes { get; }
    public static int  DspRateFor(string mode);            // the single source for §2.1
    public static bool AcceptsCentreFrequency(string mode); // the frequency-gating rule
    public static IModem Create(string mode, int dspRate, Action<byte[]> sink, ModemOptions opts);
}
public readonly record struct ModemOptions(
    double? CentreFrequencyHz = null, int? OffsetPairs = null,
    double? OffsetStepHz = null, PskDetector? Detector = null, FskFraming? Framing = null);
```

The daemon's `Program.cs:267-308` switch + `:218-222` rate logic + `:244-265` gating collapse to
calls into this. packet.net's `CreateModem` + both `DspRate` copies + the passband rule likewise.
**Payoff:** future modes require a soundmodem release + a packet.net pin-bump — zero switch edits,
zero drift. This is the highest-leverage change and should be filed now even if we ship Phase 1–2
against the mirror first.

---

## 10. Suggested sequencing & sizing

| Phase | Content | Size | Risk | Depends on |
|---|---|---|---|---|
| 0 | pin 0.4.0→0.5.0, shared `SoundModemRates.DspRate`, doc + YAML-double fixes | S | low | — |
| 1 | Group A drop-in modes (FreeDV, MS110D, aliases) + frequency gating | S–M | low | 0 |
| 2 | Group B BPSK diversity bank + `offsetPairs`/`offsetStepHz`/`pskDetector` | M | med (behaviour change) | 0 |
| 3a | Group C Flex device backend (`flex:`), `flex:mock` tests | M–L | med-high | 0 |
| 3b | Multi-channel IQ RX | L | high | 3a + new design |
| 4 | Group D ARDOP + POCSAG hosted services | L | high | 0 (indep. of ports) |
| 5 | Web UI editor + services UI + docs/examples | M | low-med | 1–4 |
| B | Upstream `ModemCatalog` in pdn-soundmodem (parallel) | S–M (in soundmodem) | low | — |

Phases 0–2 land the bulk of the *AX.25* value quickly and low-risk. 3a adds Flex. 4 is a distinct
subsystem that can proceed in parallel. 3b and the full services UI are the natural descope points
if we want a first PR sooner.

---

## 11. Open questions for Tom

1. **Upstream the shared `ModemCatalog` (§1.1B / §9)?** Recommended — collapses this recurring
   integration to a pin-bump. Do you want that filed against `pdn-soundmodem` now?
2. **BPSK behaviour change (Phase 2):** OK to make `bpsk300`/`bpsk1200` default to the differential
   diversity bank (matching soundmodem), with `offsetPairs:0` as the opt-out? Or keep single-modem
   default and make the bank opt-in?
3. **Multi-channel IQ (3b):** descope to a follow-up? (Recommended — it breaks one-port-one-device
   and needs its own design.)
4. **ARDOP role:** confirm packet.net should be the **ARDOP TNC/provider** (exposes the ardopcf
   host-port protocol for BPQ/Pat to drive) rather than an ARDOP *host*. §7.3 documents the BPQ
   model; the provider role is the natural fit for the `ArdopHostServer` surface soundmodem gives us.
5. **First-PR boundary:** ship Phases 0–2 (+5 editor) as PR #1 and Flex/ARDOP/POCSAG as follow-ups,
   or one big branch?
