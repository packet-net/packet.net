# The development workflow for a Rust RP2040 / Pico W node — real board, emulation, and hands-free CI

*Research / options analysis — 2026-06-03. Exploratory; no code, no repo changes. This note is about the **dev workflow** (toolchain, iterate loop, hands-free automation, debugging, hardware to procure), not the node design. The node design — feasibility, the table reuse, the runtime port, the connectivity tiers — is covered by its companions [`pico-packet-node.md`](pico-packet-node.md) ("the work is the runtime, not the tables") and [`codegen-reach.md`](codegen-reach.md). Read those for **what** to build; this is **how you'd build it and how an agent could iterate on it**. Grounded against current (2025/2026) embedded-Rust sources — links at the end.*

---

## 0. TL;DR — the recommended workflow (one paragraph)

Write the AX.25 link-layer logic (the codegen'd Rust tables + the hand-written runtime the companion notes describe) as a **target-independent `no_std` crate that builds for the host too**, and do the overwhelming majority of development as ordinary `cargo test` on x86-64 — same machine, same CI, completely hands-free, zero hardware. For the embedded firmware itself, use **Embassy** (`embassy-rp` + `cyw43` + `cyw43-pio` + `embassy-net`) on the `thumbv6m-none-eabi` target, and develop against a **real Pico W left permanently wired to a debug probe** (a second Pico flashed as Raspberry Pi's `debugprobe`, or the £12 official Raspberry Pi Debug Probe). With `runner = "probe-rs run"` in `.cargo/config.toml`, `cargo run` then flashes over SWD, resets, and streams `defmt` logs back over RTT — no BOOTSEL button, no drag-drop, no human touching the board. Put that wired board on a **self-hosted CI runner** (the repo already mandates self-hosted runners) and the flash → run → assert loop is genuinely hands-free on real silicon, including on-target tests via **`embedded-test`**. **Emulation (Wokwi / rp2040js, Renode) is useful for the CPU/GPIO/UART logic but emulates *no* CYW43 WiFi, so it cannot exercise the headline AXUDP-over-WiFi tier** — anything touching the radio needs the real board.

---

## 1. The verdict — what's hands-free vs needs hardware/human

| Work | Hands-free today? | How | Hardware |
|---|---|---|---|
| **Portable link-layer logic** (codegen'd tables + runtime port, frame codec, XID, segmenter, timers as pure logic) | **YES — fully** | `cargo test` on the host, `no_std` crate compiled with `std` in test builds. The existing conformance vectors + two-session harness run here unchanged. | **None** |
| **Cross-compile sanity** (does it build for `thumbv6m-none-eabi`, does it fit, no accidental soft-float/`std`) | **YES — fully** | `cargo build --target thumbv6m-none-eabi`, `cargo size`, `cargo bloat`, `#![no_std]` lints, in CI | **None** |
| **CPU / GPIO / UART / timer firmware behaviour** (no radio) | **Mostly** | Emulated: Wokwi `wokwi-cli` headless, or Renode, in CI. Single-core, no DMA/IRQ caveats. | **None** (emulated) |
| **WiFi / CYW43 / embassy-net / the AXUDP-over-WiFi tier** | **YES, but only on real hardware** | Real Pico W + permanent SWD probe + `probe-rs run` on a self-hosted runner. **No emulator does CYW43.** | **2× Pico (W) + USB**, left wired |
| **On-target unit/integration tests** (the runtime running on the actual M0+) | **YES** | `embedded-test` + `probe-rs run` as the `cargo test` runner, on the wired board on a self-hosted runner | Same wired board |
| **Interactive breakpoint/step debugging** | Human-driven (or scripted GDB) | `probe-rs` GDB stub / VS Code; scriptable but normally a person | Same wired board |
| **Initial probe flashing & physical wiring** | **One-time human task** | Flash `debugprobe` firmware to the probe Pico once; wire 3 jumpers (GND/SWCLK/SWDIO); plug in USB | One-time |

**Bottom line:** the *protocol* work — which the companion notes identify as the bulk of the effort ("~6 k LOC runtime, the tables are free") — is **100% hands-free with no hardware at all**, because it's portable `no_std` Rust that compiles and tests on the host. The *firmware integration* work splits cleanly: everything except the radio can be emulated hands-free in CI, but **the WiFi tier — the headline demo — has no emulator and must run on a real Pico W**. The good news is that a real Pico W behind a permanently-wired SWD probe on a self-hosted runner *is* a true hands-free flash/run/test loop (this is a solved, documented pattern as of 2025); the only human steps are the one-time wiring and flashing of the probe. An autonomous agent can drive `cargo test` / `cargo run` against that rig indefinitely without anyone touching the board.

This sequences naturally onto the companion note's phasing: **Phase 1 (host-side `no_std` runtime spike) is entirely hands-free, zero hardware** — do all of it before procuring anything. **Phase 2 (AXUDP-over-WiFi on the Pico W)** is the first step that *requires* the wired-board rig.

---

## 2. Toolchain — the de-facto modern Rust embedded stack for the Pico W (2026)

### 2.1 Target triple

**`thumbv6m-none-eabi`** — ARM Cortex-M0+, the RP2040's core. No FPU (the `eabi`, not `eabihf`, suffix); soft-float is pulled in for any `f32`/`f64`, which is exactly why the companion note insists the runtime's two timer formulas (SRT/T1V) be integerised. Add it with `rustup target add thumbv6m-none-eabi`.

> *Currency note — the Pico **2** W (RP2350) is a different target.* The RP2350 has Cortex-M33 cores (`thumbv8m.main-none-eabihf`, *with* FPU) or optional RISC-V Hazard3 (`riscv32imac-unknown-none-elf`). Embassy supports it (`embassy-rp` ≥ 0.3.1 with the rp235x feature; `cyw43` works), **but `probe-rs` does not yet support the RP2350** — Raspberry Pi steers you to `picotool` there, which breaks the `cargo run`-flashes-and-streams-RTT loop that makes the RP2040 hands-free story work. **For the hands-free probe loop, the original Pico W / RP2040 is the better choice today**, precisely because `probe-rs` supports it fully. Revisit RP2350 once `probe-rs` lands it.

### 2.2 HAL — recommend **Embassy (`embassy-rp`)**, not `rp-hal`/`rp2040-hal`

Two real options:

- **`embassy-rp`** — the RP2040 HAL inside the [Embassy](https://github.com/embassy-rs/embassy) async embedded framework. Async/await executor, `embassy-time` timers, and — critically — the **only** mature async networking path for the Pico W.
- **`rp-hal` / `rp2040-hal`** ([rp-rs](https://github.com/rp-rs/rp-hal)) — the community `embedded-hal`-based HAL. Excellent, blocking/interrupt-driven, the basis of the canonical [`rp2040-project-template`](https://github.com/rp-rs/rp2040-project-template). No first-party async WiFi story.

**Recommendation: Embassy**, and it's not close *for this project*, because of the WiFi + networking + concurrency requirement:

1. **The Pico W WiFi driver `cyw43` is an Embassy crate.** It lives in the embassy monorepo and is async-first; the WiFi tier of this node *is* the headline demo, and it's the idiomatic Embassy path. There is no comparably-supported async CYW43 driver outside Embassy.
2. **`embassy-net` is the no-alloc async TCP/IP stack** (a smoltcp-based stack with an Embassy-friendly async API). The companion note already recommends it over lwIP specifically because it sidesteps lwIP's heap (`embassy-net` is no-alloc, dodging the lwIP "memory freezing under load" risk flagged in `pico-packet-node.md` §9). `AxudpSocket` maps 1:1 onto `embassy_net::udp::UdpSocket`.
3. **Async is the right concurrency model for the multi-source event pump** — WiFi packets + N session timers + (later) a modem ISR, all as `embassy` tasks on one executor, no RTOS, no allocator. The companion note (§7) already lands on Embassy for the node design; the *workflow* reasons reinforce it: the entire `cyw43` + `embassy-net` example surface is maintained, defmt-instrumented, and probe-rs-runnable out of the box.

`rp-hal` stays the better pick only for a pure RF/KISS node with **no** IP networking (tier 5a/5b in the companion note), where you don't want the async machinery. Since the recommended first target is the WiFi/AXUDP tier (5c), **Embassy wins**.

### 2.3 WiFi driver + TCP/IP stack

- **`cyw43`** + **`cyw43-pio`** — drives the CYW43439 over the Pico W's nonstandard half-duplex SPI, bit-banged through a **PIO** state machine (that's what `cyw43-pio` is), IRQ-driven, no busy-poll. The WiFi firmware blob + CLM blob ship as byte arrays linked into flash. Station mode, AP mode, and raw Ethernet frames are all supported. Current versions (per the embassy `examples/rp` manifest): `cyw43` 0.7.x, `cyw43-pio` 0.10.x.
- **`embassy-net`** (0.9.x) over the cyw43 Ethernet device — features you'd enable: `udp` (for AXUDP), `dhcpv4`, `proto-ipv4`, `medium-ethernet`, `tcp`/`dns` as needed. **smoltcp** sits underneath `embassy-net`; you normally consume `embassy-net`, not smoltcp directly.

### 2.4 Build / flash tooling — recommend **`probe-rs`**

| Tool | What it does | Use it when | Hands-free? |
|---|---|---|---|
| **`probe-rs run`** | Flashes ELF over SWD via a CMSIS-DAP probe, resets, attaches, **streams RTT/defmt logs**, exits with the program's status. Set as `runner` so `cargo run` just works. | **The recommended dev loop.** Requires a debug probe. | **Yes** — fully scriptable, exit codes, log stream |
| **`cargo-embed`** | probe-rs's richer front-end: flash + reset + defmt session (+ optional GDB/RTT TUI). Config via `Embed.toml`. | Interactive sessions / multi-RTT-channel | Mostly (more interactive) |
| **`cargo-flash`** | probe-rs flash-only (no attach). Has a `--host`/`--token` remote mode. | CI flash step without log capture | Yes |
| **`elf2uf2-rs`** | Converts ELF → UF2 and copies it to the mounted BOOTSEL mass-storage volume. | No probe available; manual flashing | **No** — needs BOOTSEL button press (see §3) |
| **`picotool`** | Raspberry Pi's official PICOBOOT/UF2 tool. Required for RP2350; awkward as a cargo runner on RP2040 (expects `.elf` extension). | RP2350, or BOOTSEL-mode loads | No (BOOTSEL) |

**Recommendation: `probe-rs` (`probe-rs run` as the cargo runner)**, because it is the only option that gives `cargo run` → flash → reset → log-stream → exit-code *without a human pressing BOOTSEL*, which is the linchpin of the hands-free loop (§3). `defmt-test`'s old `probe-run` runner is **deprecated**; the project's own docs now point at probe-rs + `embedded-test`.

### 2.5 What a Pico W `cargo` project actually looks like in 2026

A single binary crate (or a workspace: a portable `*-core` lib + a `*-fw` binary — recommended here, see §3.1):

```
pico-w-node/
├── Cargo.toml            # deps below
├── .cargo/config.toml    # target + runner + linker args
├── build.rs              # emits memory.x to OUT_DIR, rerun-if-changed
├── memory.x              # RP2040 flash/RAM layout (BOOT2 + FLASH + RAM)
├── rust-toolchain.toml   # pin stable + thumbv6m-none-eabi component
└── src/main.rs           # #![no_std] #![no_main], #[embassy_executor::main]
```

- **`Cargo.toml` deps** (current, from the embassy `examples/rp` manifest): `embassy-rp` (features `rp2040`, `time-driver`, `critical-section-impl`, `defmt`), `embassy-executor` (`arch-cortex-m`, `executor-thread`, `executor-interrupt`), `embassy-time`, `embassy-net` (`udp`/`dhcpv4`/…), `cyw43` (`defmt`, `firmware-logs`), `cyw43-pio`, `defmt` (1.x), `defmt-rtt` (1.x), `panic-probe` (1.x, `print-defmt`), `static_cell` (2.x), `embassy-sync`. `cortex-m-rt` for the vector table.
- **`.cargo/config.toml`** — the load-bearing file:
  ```toml
  [target.thumbv6m-none-eabi]
  runner = "probe-rs run --chip RP2040"          # cargo run → flash+run+RTT over SWD
  rustflags = ["-C", "link-arg=--nmagic",
               "-C", "link-arg=-Tlink.x",
               "-C", "link-arg=-Tdefmt.x"]         # cortex-m-rt + defmt linker scripts
  [build]
  target = "thumbv6m-none-eabi"
  [env]
  DEFMT_LOG = "debug"
  ```
- **`memory.x`** defines `BOOT2` (252 B at the start of XIP flash), `FLASH` (2 MB), `RAM` (264 KB). `build.rs` copies it to the linker search path.
- `cargo run --release` then flashes and streams defmt logs. That's the whole loop. (The `rp2040-project-template` and the embassy examples are the canonical reference scaffolds — clone one, swap deps.)

---

## 3. The iterate loop — real board vs emulated

### 3.1 The dominant loop: host `cargo test` (no board at all)

The single most important workflow fact, and the one that aligns with the companion notes: **most of the work is portable logic that never needs a board.** The codegen'd Rust tables are `#![no_std]`-ready `&'static` data; the runtime around them (dispatcher `match`, guard eval, loop executor, frame codec, XID, segmenter, the integerised timers) is ordinary computation over bytes and enums. Structure it as a crate that is `no_std` on-target but **links `std` for host tests**:

```rust
#![cfg_attr(not(test), no_std)]   // std only when building the test harness on the host
```

…or the cleaner workspace form: a `node-core` lib crate (`no_std`, the logic) consumed by both a `node-fw` binary (thumbv6m) and a `node-core` test target (host). The existing **conformance vectors** and the **two-session harness** from the C#/TS stacks run against this crate via plain `cargo test` on x86-64 — no target, no probe, no emulator. (Watch the one cargo gotcha: a `target =` in `[build]` makes bare `cargo test` cross-compile; run host tests with an explicit host triple, or keep the logic in a separate crate without the pinned target. This is well-trodden.)

This is the loop an autonomous agent lives in: edit Rust → `cargo test` → green/red, in seconds, with full backtraces and `std` tooling (miri, proptest, coverage). It covers the *correctness* of the protocol completely. Only *integration with the silicon and the radio* needs the rest of §3.

### 3.2 Real board via probe-rs + an SWD probe — the recommended firmware loop

A debug probe speaking **CMSIS-DAP/SWD** to the target's SWD pins. Two ways to get one:

- **A second Pico flashed as `debugprobe`** (Raspberry Pi's official firmware; the modern successor to "picoprobe"). ~£4. Wire **3 jumpers**: probe `GP2` → target `SWCLK`, probe `GP3` → target `SWDIO`, `GND` → `GND` (optionally probe `GP4/GP5` UART ↔ target UART, and VSYS for power). **Important:** it's probe-GPIO → target-SWD, *not* SWD-to-SWD.
- **The official Raspberry Pi Debug Probe** (~£12) — a packaged `debugprobe`-on-RP2040 in a case with proper SWD + UART cables. Same thing, tidier, recommended if procuring fresh.

With `runner = "probe-rs run"`, **`cargo run --release`** then: builds → flashes over SWD → resets → attaches → streams `defmt`/RTT logs to the terminal → propagates the program's exit semantics. Edit-compile-flash-observe in one command, **no BOOTSEL press, no replug**. This is the loop, and it's the same whether the board is on your desk or on a CI runner (§4).

### 3.3 UF2 / BOOTSEL drag-drop — works, but **not hands-free**

Hold **BOOTSEL** while plugging in → the Pico enumerates as a USB mass-storage volume → copy a `.uf2` (built with `elf2uf2-rs`) onto it → it flashes and reboots. Fine for a one-off or a no-probe bring-up, but **disqualified for an automated loop**: entering BOOTSEL requires either physically holding the button during power-up or a `picotool reboot -f -u` from already-running firmware (which only works if the running firmware is healthy and cooperative — useless when the thing you're debugging has hung or panicked). No log-back-channel either. **A probe is what makes it hands-free; drag-drop is the manual fallback.**

### 3.4 Emulation — what each tool actually supports for RP2040, and the WiFi truth

| Emulator | RP2040 support | Dual-core | DMA/IRQ | PIO | **CYW43 WiFi?** | Headless/CI | Verdict for this node |
|---|---|---|---|---|---|---|---|
| **Wokwi** (uses **rp2040js**) | Yes — GPIO, UART, I2C, SPI, PWM, **PIO** (with debugger), Timer, SysTick, Watchdog, RTC, ADC, GDB | **No — single-core only** | Yes (DMA, Timer) | Yes | **No** | **Yes — [`wokwi-cli`](https://docs.wokwi.com/wokwi-ci/getting-started) headless, with a `wokwi.toml`/scenario file; free for open-source CI** | Good for CPU/GPIO/UART/PIO logic in CI; **cannot test the WiFi tier** |
| **Renode** ([matgla/Renode_RP2040](https://github.com/matgla/Renode_RP2040), community) | Partial — GPIO/UART/SPI/I2C/ADC | (scriptable) | **No — IRQ & DMA not yet supported**; clock cfg absent; PIO needs manual re-eval | Limited | **No** | Yes — Renode is built for scripted/CI HIL-style runs (Robot framework) | Usable for deterministic CPU-level tests; the no-IRQ/no-DMA gaps bite a real Embassy firmware; **no WiFi** |
| **QEMU** | Only generically — `qemu-system-arm` has **no RP2040 machine**; you can run raw `thumbv6m` code as `-cpu cortex-m3` (M0 ⊂ M3 ISA) with semihosting, but **no RP2040 peripherals at all** | n/a | n/a | No | **No** | Yes | Only useful for the *most* portable `no_std` code with no peripherals — and host `cargo test` (§3.1) does that better. Not a Pico emulator. |

**The load-bearing finding for this project: no emulator emulates the CYW43 WiFi.**

- **Wokwi's WiFi simulation is ESP32-only.** It works by intercepting at the WiFi *API* level (Arduino `WiFi`, MicroPython `network.WLAN`, Rust `esp-idf-svc`) and bridging through the "Wokwi-GUEST" virtual AP / internet gateway. It models the ESP32's stack, **not** the RP2040 driving a CYW43439 over PIO-SPI — which is exactly the path the Rust `cyw43` driver exercises. The Pico W "WiFi working" Wokwi projects you'll find are MicroPython/Arduino at that API layer; there is no path for `embassy-net` + `cyw43` traffic.
- **rp2040js** (the engine under Wokwi) makes **no mention of CYW43/WiFi/wireless** and is **single-core**; its docs cover Arduino/MicroPython/UART/GDB only.
- **Renode** has no CYW43 model either, and its community RP2040 lacks IRQ/DMA — which a real Embassy app leans on heavily.

So: emulation is a genuine, hands-free CI asset for the *CPU-side* firmware (blink, GPIO, UART, PIO bring-up, a KISS-over-UART path, panic-on-assert smoke tests), but **the AXUDP-over-WiFi tier that the companion note names as the headline demo cannot be emulated** — it needs a real Pico W. Flag this loudly in any plan: *the radio is the hardware-only frontier.*

---

## 4. Can you iterate HANDS-FREE? (the key question)

**Yes — for almost everything, and there's a real hands-free loop even for the WiFi-on-silicon part.** Path by path:

### 4.1 Host-side (portable logic) — **fully hands-free, zero hardware** ✅

Covered in §3.1. This is the bulk of the work per the companion notes. `cargo test` on the host CI runner, no board, no emulator, instant. An agent can develop, test, and regression-guard the *entire protocol* this way. **This is the recommended primary loop and it needs nothing physical.**

### 4.2 Emulated (`wokwi-cli` / Renode) in CI — **genuinely hands-free, zero hardware, but WiFi-blind** ✅/⚠️

`wokwi-cli` runs a Wokwi project headlessly from a `wokwi.toml` + a scenario file, asserting on serial/`defmt` output, with a timeout and an exit code — a real CI gate, free for open-source. Renode similarly scripts deterministic runs. **Both are hands-free with no hardware.** The catch is §3.4: they exercise the CPU/peripherals but **not CYW43**, and Wokwi is single-core, Renode lacks IRQ/DMA. So emulation in CI is a legitimate hands-free tier for *firmware that doesn't touch the radio* (e.g. a KISS-over-UART build, GPIO/PIO bring-up, panic/assert smoke tests) — but it cannot validate the headline WiFi tier. Treat it as a cheap pre-filter, not a substitute for the real-board job.

### 4.3 Real board left permanently wired + SWD probe on a self-hosted runner — **a real hands-free flash/run/test loop** ✅

This is the important answer: **yes, a permanently-wired Pico W behind an SWD probe on a self-hosted runner is a genuine hands-free flash/run/test loop, and it's a documented, current pattern (Ferrous Systems, 2025).** The board never needs a human after the one-time wiring because `probe-rs` flashes over SWD (BOOTSEL is never involved) and resets the chip in software each run.

Exact setup:

1. **Rig:** target **Pico W** + a **debug probe** (2nd Pico as `debugprobe`, or the official Debug Probe), 3 wires (GND/SWCLK/SWDIO), both on USB to the runner host. Leave it assembled and powered.
2. **Runner:** a **self-hosted** GitHub Actions runner on that host — which the repo *already mandates* (`runs-on: [self-hosted, Linux, X64]`; no GitHub-hosted runners). Install the Rust toolchain + `thumbv6m-none-eabi` + `probe-rs`. Add a `udev` rule so the probe is accessible without root. **Don't add a new hosted-runner workflow — extend the existing self-hosted pattern, and gate the hardware job with a trait/label exactly like the repo's existing `Category=HardwareLoop` convention.**
3. **Loop:** the CI job runs `cargo run --release` (smoke/integration firmware) or `cargo test` (with `embedded-test`, §4.4). `probe-rs` flashes, resets, runs, streams `defmt` back; the job asserts on that output and the exit code. No human, no BOOTSEL, no replug.
4. **Optional — shared/remote board:** `probe-rs` gained a **built-in remote server** (merged Feb 2025): run `probe-rs` in server mode on the host wired to the board, then `cargo run --host ws://… --token …` from anywhere flashes/runs/streams RTT *transparently over the network*, with token auth and no SSH. This decouples the board's host from the CI executor and lets multiple agents/jobs share one rig — strengthening the hands-free story (and fitting the self-hosted constraint: the board-host is just another self-hosted machine).

The **only** human steps are the **one-time** ones: flashing `debugprobe` to the probe Pico, wiring 3 jumpers, plugging in USB. After that, an autonomous agent flashes/runs/tests on real silicon — *including the WiFi tier* — indefinitely, untouched. (The WiFi tier additionally needs the rig within range of a known 2.4 GHz AP / the AXUDP peer reachable on that LAN — a one-time environment setup, not a per-run human action.)

### 4.4 On-target test frameworks — **`embedded-test`** (defmt-test is deprecated) ✅

- **`embedded-test`** (probe-rs project; v0.7.x, actively maintained into 2026) is the current on-target harness. It's **libtest-compatible**: `probe-rs run` reads the test list straight from the ELF, flashes once, runs each `#[test]` with a **device reset between cases**, supports an **`#[init]`** fixture, **`#[timeout(secs)]`** (default 60 s), `#[should_panic]`, `#[ignore]`, and **async tests** (via its `embassy-010` feature — directly relevant to an Embassy firmware). Set `runner = "probe-rs run --chip RP2040"` and **`cargo test` runs your tests on the actual M0+**. Because it's just `probe-rs run`, it composes with the self-hosted-runner rig (§4.3) *and* the remote server (§4.2-remote) — so on-target tests are hands-free on the wired board.
- **`defmt-test`** (knurling-rs) is the older harness tied to the **deprecated `probe-run`** runner; the defmt docs themselves now steer new work to `embedded-test`. **Use `embedded-test`.**

This is what earns the companion note's "provable parity on-target" goal (`pico-packet-node.md` §9.7): run a subset of the conformance vectors / a SABM-UA-Iframe scenario as `embedded-test` cases on the real Pico W, hands-free in CI.

### 4.5 Recommended hands-free loop (the synthesis)

1. **Host `cargo test`** on every commit (self-hosted runner) — the protocol, fully, instantly, no hardware. *This is where ~all correctness lives.*
2. **Cross-build gate** — `cargo build --target thumbv6m-none-eabi` + `cargo size`/`cargo-bloat`, to catch `std`/soft-float/size regressions. No hardware.
3. *(Optional)* **`wokwi-cli`** smoke test of a non-WiFi firmware path (boot, GPIO, UART/KISS) — hands-free, cheap, but WiFi-blind.
4. **On-target `embedded-test`** + a **`cargo run` integration smoke** on the **permanently-wired Pico W** (self-hosted runner, `Category=HardwareLoop`-style gate) — the only tier that validates CYW43 + `embassy-net` + the AXUDP-over-WiFi demo. Hands-free after one-time wiring.

Steps 1–2 (and arguably 3) need **zero hardware** and cover the majority of the companion notes' work; step 4 is the hardware-only frontier and is itself hands-free once wired.

---

## 5. Debugging story — what an agent realistically gets

| Mechanism | What it gives | Hands-free / scriptable? |
|---|---|---|
| **`defmt` + `defmt-rtt` + RTT, decoded by `probe-rs`** | **The workhorse.** Deferred, log-level-filtered logging: the firmware writes compact indices/args into a RAM ring buffer (RTT); `probe-rs` reads them over SWD and rehydrates them on the host using the format strings in the ELF's debug info. Cheap enough to leave in (no UART, no formatting on-device). `DEFMT_LOG` env sets the level. | **Yes — fully.** This is *the* diagnostic surface for an autonomous agent: structured log lines on stdout with a controllable level, no human. |
| **`panic-probe` + `defmt`** | Panics print a `defmt` message (location + message) over RTT and halt — the agent sees *why* and *where* it panicked in the log stream. | **Yes** |
| **HardFault** | `cortex-m-rt`'s `HardFault` handler (overridable) — combine with `defmt` to log fault status, or break on it in GDB. `flip-link` (zero-cost) turns stack-overflow-into-static-corruption into a clean fault by relocating the stack, making the failure *detectable* rather than silent. | Log path: yes. Fault-register decode: usually GDB (human) |
| **GDB over SWD** (`probe-rs` GDB stub, or `cargo-embed` → GDB) | Full interactive: breakpoints, single-step, memory/register inspection, backtraces, watchpoints. VS Code via the probe-rs debug extension / `probe-rs-debugger` (DAP). | **Human-driven** normally; *scriptable* via GDB batch (`-x script.gdb`, `set logging`) for canned breakpoint+dump sequences, but the agent's natural surface is defmt logs, not live stepping |
| **`probe-rs` itself** | Flashing, reset, RAM read/write, attach-to-running, RTT — all as exit-coded CLI commands; works locally or over the remote server. | **Yes** |

**What an agent realistically gets:** a clean, scriptable, hands-free diagnostic loop via **defmt-over-RTT** — structured, level-filtered log lines on stdout, plus panic/fault messages with location, with exit codes from `probe-rs run` and `embedded-test`. That is enough for log-driven debugging of the protocol-on-silicon (assert-and-log, trace a session, confirm a frame went out). **Interactive breakpoint/step debugging via GDB is available but is the human's tool** — an agent would reach for it rarely and only via pre-scripted GDB batches. The realistic agent diagnostic posture: *develop and prove correctness on the host (full `std` debugging, backtraces, miri/proptest), then use defmt-over-RTT on the real board to confirm the silicon/radio integration and catch anything host tests can't (timing, the cyw43 link, embassy-net behaviour).*

---

## 6. Hardware to procure / wire — and what's achievable with zero hardware

### 6.1 Zero hardware (host + emulated) — covers the majority of the work

- **Host `cargo test`** of the portable `no_std` crate (the codegen'd tables + the ported runtime, frame codec, XID, segmenter, integerised timers) — the bulk of the companion notes' effort, fully testable with **nothing but a dev machine / the existing self-hosted CI**.
- **Cross-compile + size gates** for `thumbv6m-none-eabi` — no hardware.
- **`wokwi-cli` / Renode** CI for non-WiFi firmware paths — no hardware, hands-free, but **WiFi-blind** (§3.4).

If the goal is "prove the link-layer port is correct and that it builds + fits for the M0+", **you can do all of it with zero hardware** — and should (companion-note Phase 1).

### 6.2 The recommended hands-free real-board rig (for the WiFi tier and on-target tests)

Minimal, ~£20 total:

- **1× Raspberry Pi Pico W** — the **target** (RP2040 + CYW43439). This is the node.
- **1× debug probe** — either a **2nd Raspberry Pi Pico** flashed with **`debugprobe`** firmware (~£4), or the **official Raspberry Pi Debug Probe** (~£12, recommended if buying fresh — packaged SWD+UART, no jumper fiddling).
- **Jumper wires** — 3 minimum (GND, SWCLK→probe GP2, SWDIO→probe GP3); +2 for UART (probe GP4/GP5 ↔ target UART) if you want a serial channel alongside RTT.
- **2× USB cables** to the runner host (probe + target; or power the target from the probe's VSYS to save one).
- **A 2.4 GHz AP in range + the AXUDP peer reachable on that LAN** (e.g. a LinBPQ/XRouter/`packet.net` endpoint) — needed only to exercise the WiFi tier; a one-time environment setup.
- **Host:** a **self-hosted** runner (already the repo norm) with Rust + `thumbv6m-none-eabi` + `probe-rs` + a `udev` rule for the probe. Optionally run `probe-rs` in **remote-server** mode so the board-host and the CI executor can be different self-hosted machines and multiple jobs/agents can share the rig.

One-time human actions: flash `debugprobe` to the probe Pico (BOOTSEL + drag the UF2, once), wire 3 jumpers, plug in. Thereafter the loop is hands-free.

*(If/when the RF tier of the companion note (5a/5b) is pursued, add an external VHF/UHF radio + a TNC or a 3rd Pico running `pico_tnc` for KISS — but that's node-design hardware, out of scope for this workflow note. The KISS-over-UART path, notably, *is* partly emulable via `wokwi-cli`'s UART, unlike the WiFi path.)*

---

## 7. How this lands against the companion notes

- [`pico-packet-node.md`](pico-packet-node.md) recommends **Rust + Embassy**, **AXUDP-over-WiFi (tier 5c) first**, and concludes **"the work is the runtime, not the tables"** with a phased path (Phase 1 host-side `no_std` spike → Phase 2 Pico W WiFi). This note operationalises that: Phase 1 is the **zero-hardware, fully-hands-free host `cargo test`** loop (§3.1, §4.1) — do all of it before buying anything; Phase 2 is the first step that needs the **wired-board rig** (§4.3, §6.2), because **WiFi can't be emulated** (§3.4). The note's §3 integerisation requirement is enforced by the §4.5 cross-build/no-`std`/no-soft-float gate.
- [`codegen-reach.md`](codegen-reach.md) wants **cross-language conformance vectors first** as the drift-proof safety net. Those vectors are exactly what the host `cargo test` loop (§3.1) runs against the Rust port — and a subset becomes the **`embedded-test` on-target suite** (§4.4) that earns the "provable parity on-target" claim. The workflow and the codegen roadmap meet at the vector corpus.
- A maturity update worth noting: **SP-010 (typed verbs *and* guards/events) has now shipped** in the ecosystem (it was the top open lever in both companion notes). That removes the "string parser on an M0+" risk those notes flagged — the codegen now emits closed typed sets the Rust `match` consumes directly, which is strictly better for the host-test-then-flash workflow here (no runtime string tokeniser, smaller flash, cleaner exhaustiveness).

---

## Sources

**Toolchain / HAL / project structure**
- Embassy framework: https://github.com/embassy-rs/embassy — RP examples (`examples/rp`, incl. `wifi_tcp_server.rs`, `wifi_scan.rs`, `wifi_webrequest.rs`): https://github.com/embassy-rs/embassy/tree/main/examples/rp/src/bin ; manifest (dep versions): https://github.com/embassy-rs/embassy/blob/main/examples/rp/Cargo.toml
- `cyw43` driver (Pico W WiFi, async): https://docs.embassy.dev/cyw43/ ; `cyw43-pio`: https://docs.embassy.dev/cyw43-pio/
- `embassy-net` (no-alloc async net stack over smoltcp): https://docs.embassy.dev/embassy-net/
- `rp-hal` / `rp2040-hal` (the non-Embassy HAL): https://github.com/rp-rs/rp-hal
- `rp2040-project-template` (canonical scaffold; probe-rs runner, defmt, flip-link): https://github.com/rp-rs/rp2040-project-template
- Pico W Rust blinky / project setup walkthroughs: https://www.darrik.dev/writing/blinking-pico-w-onboard-led-rust/ , https://murraytodd.medium.com/our-first-rust-blinky-program-on-raspberry-pi-pico-w-376211f1074d , https://baileytownsend.dev/articles/pico-w-webserver-with-rust
- RP2350 / Pico 2 W currency (probe-rs not yet supported → picotool): https://www.raspberrypi.com/news/rust-on-rp2350/ , https://murraytodd.medium.com/rust-with-the-raspberry-pi-pico-2-rp2350-e5f537af1c25 , https://github.com/Nivirx/embassy-pico2w-template

**Flash / run / debug tooling**
- probe-rs (run, cargo-embed, cargo-flash, debugger): https://probe.rs/ , https://probe.rs/docs/tools/debugger/
- probe-rs **remote server** (network-shared hardware, Feb 2025): https://blog.aheymans.xyz/post/probe_rs_remote/
- Raspberry Pi Debug Probe (official) + debugprobe firmware: https://www.raspberrypi.com/documentation/microcontrollers/debug-probe.html
- 2nd-Pico-as-probe wiring (GP2/GP3/GND; GPIO→SWD not SWD→SWD): https://mcuoneclipse.com/2022/09/17/picoprobe-using-the-raspberry-pi-pico-as-debug-probe/ , https://tobywf.com/2024/08/raspberry-pi-rp2040-debug-probe-swd/
- RTT + defmt logging on the Pico (how the log path works): https://pico.implrust.com/debugging/rtt.html , https://pico.implrust.com/debugging/pico-debug-probe.html
- flash tooling comparison (elf2uf2-rs / picotool / probe-rs): https://github.com/rp-rs/rp2040-project-template/blob/main/README.md , https://www.fullstack.com/labs/resources/blog/rust-raspberry-pi-pico-blink

**On-target test + hands-free CI**
- `embedded-test` (current on-target harness; libtest-compatible, init/timeout/async): https://github.com/probe-rs/embedded-test , https://docs.rs/embedded-test/ , https://crates.io/crates/embedded-test
- `defmt-test` **deprecated** (tied to deprecated probe-run; defmt docs point to embedded-test): https://github.com/knurling-rs/defmt-test , https://defmt.ferrous-systems.com/
- **Hardware-in-the-loop tests on GitHub Actions with a self-hosted runner** (the real-board hands-free pattern): https://ferrous-systems.com/blog/gha-hil-tests/
- Host-side `no_std` testing pattern (`#![cfg_attr(not(test), no_std)]`, dual-target gotchas): https://users.rust-lang.org/t/how-to-test-code-when-no-std-is-set/93180 , https://blog.dbrgn.ch/2019/12/24/testing-for-no-std-compatibility/

**Emulation (and the no-WiFi finding)**
- Wokwi Pi Pico part (single-core; no WiFi/CYW43 in the status table): https://docs.wokwi.com/parts/wokwi-pi-pico
- rp2040js (the engine; single-core, no WiFi/CYW43): https://github.com/wokwi/rp2040js
- Wokwi WiFi simulation is **ESP32-only** (API-level interception, Wokwi-GUEST gateway): https://docs.wokwi.com/guides/esp32-wifi
- `wokwi-cli` headless / CI: https://docs.wokwi.com/wokwi-ci/getting-started
- Renode RP2040 (community; IRQ & DMA not supported, no WiFi): https://github.com/matgla/Renode_RP2040 ; Renode intro/HIL: https://interrupt.memfault.com/blog/intro-to-renode
- QEMU for Cortex-M0 (no RP2040 machine; `-cpu cortex-m3` for raw thumbv6m + semihosting): https://docs.rust-embedded.org/book/start/qemu.html

**Companion notes (this repo)**
- [`pico-packet-node.md`](pico-packet-node.md) — node design / feasibility ("the work is the runtime, not the tables").
- [`codegen-reach.md`](codegen-reach.md) — codegen scope; conformance vectors as the drift-proof safety net.
