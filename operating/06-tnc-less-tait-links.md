# 6. TNC-less Tait-to-Tait links

**Goal:** run an AX.25 link between two Tait radios with **no TNC at all** — using
each radio's own built-in FFSK modem as the bearer.

This is **option 2** from the [overview](index.md#two-ways-a-tait-radio-can-join-a-port):
the radio *is* the port. It's the `tait-transparent` transport kind. One device per
end, no audio cables, no NinoTNC — the AX.25 frames ride the radio's internal FFSK
modem, framed with KISS SLIP over the serial byte pipe.

It is genuinely useful for a simple Tait-to-Tait hop. But it comes with a real
trade-off and a set of **programming gotchas** that will cost you an afternoon if you
skip them. (They cost us one. Hence this chapter.)

## The honest trade-off

Read this before you commit to Transparent mode:

- **No signal telemetry.** In Transparent mode the serial port is a plain byte pipe
  — the CCDI control channel is unavailable — so there is **no RSSI, no SNR, no noise
  floor, and no hardware carrier-sense (DCD)**. Only frame airtime timing is known.
  You lose everything in [chapter 2](02-see-your-link-quality.md). That's the
  inherent cost of a TNC-less link: one device, but you can't *ask the radio
  questions* while it's a byte pipe.
- **Closed bearer, no interop.** The FFSK modem is the *radio's own* — a
  Tait-to-Tait scheme. It does **not** interoperate with standard AFSK/GFSK packet:
  a Tait in Transparent mode cannot talk to a NinoTNC or Dire Wolf station. Both ends
  must be Tait radios in this mode.

If you want signal telemetry, carrier-sense, or interop with normal packet, use a
TNC and [attach the radio](01-attach-a-radio.md) instead — that's option 1.

## The config

```yaml
ports:
  - id: tait
    enabled: true
    transport:
      kind: tait-transparent     # the radio IS the modem — NO external TNC
      serial: "19925328"         # PREFERRED — pin by CCDI serial (stable)
      # device: /dev/ttyUSB0     # OR by device path (exactly one of serial/device)
      baud: 28800                # CCDI COMMAND-mode baud (enter/exit rate)
      transparentBaud: 28800     # Transparent-mode TERMINAL baud (see gotchas)
      ffskBaud: 2400             # FFSK over-air baud (for airtime estimation)
      leadInMs: 100              # modelled TX lead-in (key-up + FFSK preamble)
```

Field notes:

| Field | Meaning | Default |
|---|---|---|
| `serial` / `device` / `headEndId`+`deviceId` | Bind by CCDI serial (preferred), device path, **or** a [head-end](08-split-station-head-end.md) device — exactly one mode. | — |
| `baud` | The **command-mode** serial rate (used to enter/exit Transparent). | `28800` |
| `transparentBaud` | The **Transparent-mode terminal** serial rate. If it differs from `baud`, the port re-clocks on entry. | `28800` |
| `ffskBaud` | FFSK over-air baud, used only to estimate airtime. | `2400` |
| `leadInMs` | Modelled transmit lead-in, ms. | `100` |

> [!NOTE]
> There is **no `radio:` block** on a `tait-transparent` port (validation rejects
> one) — the radio is the modem, so there's no separate control channel to attach.
> Binding by CCDI serial is still preferred, for the same re-enumeration reasons as
> [chapter 1](01-attach-a-radio.md#why-bind-by-serial-not-device-path). You can find
> the serial with the same **Scan** button / `GET /api/v1/radios/scan`.

## Run it over a head-end

The radio doesn't have to be on the PDN box: a `tait-transparent` port can bind a
radio hosted on a [split-station head-end](08-split-station-head-end.md) instead of
a local serial port. Swap the `serial`/`device` binding for the head-end pair:

```yaml
headEnds:
  - id: shack-north
    address: 192.168.1.44:7300      # optional; omit to resolve over mDNS

ports:
  - id: tait
    enabled: true
    transport:
      kind: tait-transparent
      headEndId: shack-north        # which head-end hosts the radio
      deviceId: platform-xhci-hcd.1-usb-0:2:1.0-port0   # its inventory id there
      baud: 28800
      transparentBaud: 28800
      ffskBaud: 2400
```

Exactly one binding mode — `device`, `serial`, or `headEndId`+`deviceId` (both
halves) — and the `headEndId` must be declared in `headEnds:`, same rules as any
other head-end-bound port. The `deviceId` is the radio's serial port's inventory id
on that head-end (what the Head-ends screen / `GET /inventory` shows).

How it works: the port dials the head-end's raw TCP pipe for the radio's serial
port, and every **line-rate change rides the head-end's line verb**
(`POST /ports/{id}/line`) — the raw socket is a pure byte pipe and can't carry
baud. That matters more here than for any other port kind, because Transparent
mode *re-clocks at runtime*: bring-up sets the command baud, entering Transparent
switches to `transparentBaud`, and teardown switches back — each transition is an
out-of-band line-verb call. A `baud`/`transparentBaud` pair that matches (the
common case) needs no mid-flight re-clock.

The port self-heals across a head-end bounce (daemon restart, `.deb` upgrade,
network blip) like any other head-end-bound port: the dropped pipe ends the
stream, and the supervisor re-resolves the inventory, re-dials, and **re-enters
Transparent mode** — if the radio was left as a stale Transparent byte pipe by the
dropped session (the pipe died before teardown could escape), the re-open escapes
it first (`+++`, then retries the entry). In-flight AX.25 sessions don't survive
the bounce (normal T1/N2 recovery applies); the port comes back listening.

> [!WARNING]
> **The "Ignore Escape Sequence = OFF" gotcha is even more load-bearing remotely.**
> A remote Transparent port's *recovery* path depends on the `+++` escape working:
> after a pipe drop the radio is still in Transparent, and the reconnect can only
> re-enter after escaping it. On a radio programmed to ignore the escape, every
> reconnect attempt fails (and transmits its few probe bytes over the air as data)
> until someone power-cycles the radio — and it's up a mast on a Pi now. Run the
> [Transparent-readiness doctor](#check-it-the-readiness-doctor) against the radio
> **before** deploying it to a head-end.

Two limitations to know:

- **No head-end scan/adopt affordance for this kind (yet).** The Head-ends screen
  adopts NinoTNC+Tait *pairs* (a `nino-tnc-tcp` port). A remote `tait-transparent`
  port is configured by YAML/API as above; the scan still shows the Tait and its
  inventory id, and marks it in-use once your port binds it.
- **The readiness doctor's escape/baud probes are CLI-only** and need a local
  serial port, so run them at the bench before the radio goes remote (the running
  port's `GET /api/v1/ports/{id}/doctor` reports what it can, exactly as for a
  local Transparent port).

## The setup gotchas (program the radio right)

These are radio **programming** settings, done in the Tait programming application —
not PDN config. Get them wrong and you'll see a radio that won't leave Transparent,
or garbled data, and no obvious reason why.

> [!WARNING]
> **"Ignore Escape Sequence" must be OFF.**
> PDN leaves Transparent mode by sending the `+++` escape sequence at teardown. If
> the radio is programmed with **Ignore Escape Sequence ON**, that escape does
> **nothing** — there is **no software way out**, and the only recovery is a
> **power cycle** of the radio. This is exactly the lockout we hit on the bench
> before turning the option off. Program the escape sequence to be **honoured**
> before running a Transparent port unattended.

The full checklist:

1. **Transparent Mode — Enabled.** Obviously, but it's a distinct setting; turn it on.
2. **Ignore Escape Sequence — OFF.** (See the warning above. This is the big one.)
3. **The two baud fields are different settings — understand both.** The Tait
   programming help exposes a **command-mode baud** *and* a separate **"Baud Rate
   (FFSK transparent mode)"**. They are not the same field. If you conflate them you
   get garbled data — the terminal is clocking bytes at one rate while you think it's
   another. Match PDN's `baud` to the command-mode rate and `transparentBaud` to the
   Transparent-mode terminal rate.
4. **Match the FFSK Baud Rate on both radios.** The over-air FFSK rate must be the
   same at both ends or they won't decode each other.
5. **Ignore DCS/CTCSS on both radios.** So the link isn't gated by tone squelch.

Get those five right at **both** ends and a Transparent Tait-to-Tait link comes up
cleanly.

## What teardown does

When the port stops (or the node shuts down), PDN escapes Transparent mode with the
`+++` guard sequence and restores the command baud, returning the radio to Command
mode. A radio left stuck in Transparent is **deaf to CCDI** — which is why gotcha #2
matters so much: if the escape can't fire, teardown can't recover it.

## Check it: the readiness doctor

You don't have to work that checklist by eye. The **Transparent-readiness doctor** (a
[chapter 3](03-check-your-setup-doctor.md)-style check, but for these programming
settings) runs the gotchas as **behavioral** pass/fail/unknown probes — each with a
remedy naming the exact Data-form field. Because the codeplug settings above are **not
CCDI-readable**, the doctor can't just ask the radio; it exercises the behaviour.

Run it against the radio in **Command mode**, before you commit it to a Transparent port:

```
packet-tune transparent-doctor <ccdiPort> [peerCcdiPort] --interrupt
```

| Probe | Covers | On failure |
|---|---|---|
| `transparent-mode-enabled` | gotcha 1 | error 0/06 → *Transparent Mode not enabled — enable it in the radio's Data form → General tab* |
| `escape-recovers` | gotcha 2 | escape ignored → the radio is **wedged** (power-cycle to recover); *uncheck 'Ignore Escape Sequence' in the Data form* |
| `baud-clean` | gotchas 3–5 | garbled/no round-trip → *check Baud Rate (FFSK transparent mode) matches your terminal baud, and FFSK Baud Rate matches on both radios* (a tone-squelch mismatch shows here too, as nothing received) |

> [!WARNING]
> **The escape probe can wedge a misconfigured radio.** Entering Transparent is only
> reversible while the escape works, so this probe is the one that can leave a radio
> stuck (recovery = power cycle). It only runs with **`--interrupt`** (the safe form
> reports the behavioral probes `unknown` and never enters Transparent or transmits),
> it retries the escape before declaring the radio wedged, and it surfaces a wedge
> loudly. Don't run it against a radio you can't power-cycle.

`baud-clean` needs a **peer** — pass a second CCDI port so a known frame can be looped
through both radios in Transparent mode; without one it's reported `unknown`. The
doctor leaves both radios verified back in **Command mode** when it finishes.

The node surfaces the same checklist at `GET/POST /api/v1/ports/{id}/doctor` for a
running `tait-transparent` port — but since a running Transparent port's radio is a
byte pipe (no CCDI), it reports `transparent-mode-enabled` as proven by the port
running and points you back to the CLI above for the escape/baud checks (a live byte
pipe can't be safely command-probed).

## Next

The CLI tools, firmware flashing, and mode coordination:
[7. Advanced tooling →](07-advanced-tooling.md)
