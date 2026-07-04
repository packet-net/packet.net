# 4. Tune your link (deviation)

**Goal:** set your radio's **transmit deviation** — how loud the packet tone drives
the transmitter — to the sweet spot where the other end decodes you cleanly.

Deviation is the classic packet-radio tuning knob. Too low and the far end can
barely hear the tones (missed frames). Too high and the audio clips (bit errors).
The right setting is a plateau in the middle. PDN gives you a **guided session** that
watches the decode numbers while you turn the pot, so you're not tuning blind.

## The edge-bracketing idea (in plain terms)

You cannot read "correct deviation" off a single measurement — a burst that decodes
badly could be too quiet *or* too loud. So the tuner finds the **edges** and puts you
between them:

1. Turn the pot **down** until frames start failing — that's the **low edge** (too
   quiet).
2. Turn it **up** until frames start failing the *other* way (clipping) — the
   **high edge**.
3. Sit in the **middle** of that bracket. That's your deviation.

The tuner does the *judging* for you. After each burst it tells you which way to go —
**turn UP**, **turn DOWN**, **leave it** (OK), or **sweep** — based on the decode
rate, the forward-error-correction workload, and whether the audio clipped:

- **UP** — frames are being missed with no sign of clipping → too quiet.
- **DOWN** — the audio is clipping, or errors are climbing on an otherwise strong
  signal → too loud.
- **OK** — nearly everything decodes and error correction is idle → you're there.
- **SWEEP** — *nothing* decoded and no clipping. A dead burst has no direction in it
  (too quiet, too loud, and no-path all look the same). Sweep the pot across its
  range until frames appear, then the directional advice takes over.

> [!TIP]
> Run the tuning session in an **IL2P mode** (e.g. NinoTNC mode 7, 1200 AFSK
> IL2P+CRC) so the forward-error-correction signal exists — that's what lets the
> tuner distinguish "getting worse at the loud end" from "fine". In plain-AX.25
> modes it still works, from decode rate and clipping alone, just with less nuance.

## The two ends: who turns the pot, who measures

Tuning is a **two-ended** job. One radio's pot is being adjusted; the other radio
listens and scores each burst. So each end takes a role:

- **tuned** — the end where **you turn the TX-DEV pot**. This is the radio being
  adjusted.
- **meter** — the end that **measures** the other radio's bursts and returns the
  advice.

The two ends coordinate over the radios' own **SDM** (Short Data Message)
side-channel — a Tait radio-to-radio datagram at the factory deviation. That's the
clever part: the coordination channel rides the radio's *own* modem at a fixed
deviation, so it keeps working **independently of the pot you're moving**.

## Run it (web UI): the guided tuner

From a port, open its **Tune link** action — the **`/tools/tuner`** workspace.

1. Pick the **role** for this end (**tuned** if you're at the pot; **meter** if
   you're scoring the far end).
2. Enter the **peer's SDM id** (the other radio's 8-character SDM identity) and,
   optionally, the burst size (frames per round, default 5).
3. **Start.** The session **pauses the port's normal traffic** (it transmits, so it
   takes the channel over) and connects to the peer.
4. **Watch the numbers.** A live trend table fills in per round: burst index,
   decoded/total, an audio level where available, and the **advice** for that round.
5. On the *tuned* end, after each round: **turn the pot** the way the advice says,
   then hit **Next round**. Repeat until the advice settles on **OK**.
6. **Stop.** The port is restored to normal traffic on exit.

> [!NOTE]
> The tuner **transmits and takes over the port**, so it is an **admin-initiated**
> action, one session per port at a time. However the session ends — you stop it, it
> errors, the browser closes — the port is **rebuilt and restored to normal
> operation**; it is never left paused.

## From the command line

The same session is available as a bench command (see [chapter 7](07-advanced-tooling.md)
for how the CLI is run). The SDM-coordinated flavour:

```
packet-tune deviation-sdm --role tuned|meter --tnc <port> --radio <ccdi> --peer <8charId> \
                          [--callsign N0CALL] [--burst 5] [--verbose]
```

- `--role` — **tuned** (you're at the pot) or **meter** (you're scoring the far end).
- `--tnc` — this end's NinoTNC serial port.
- `--radio` — this end's Tait CCDI serial port (required — the SDM link rides the
  radio's own modem).
- `--peer` — the far radio's 8-character SDM id.
- `--burst` — frames per round (1–50, default 5).

Run `deviation-sdm` at **both** ends (one `--role tuned`, one `--role meter`), put
both TNCs in an IL2P mode, and turn the pot at the tuned end following the printed
advice.

> [!NOTE]
> There is also an **internet-relay** flavour (`deviation-remote`, coordinating over
> a WebSocket rendezvous with a PIN instead of SDM) for tuning a link where the two
> operators aren't co-located. It's real but bench/CLI-only today; the web UI drives
> the SDM flavour. See [chapter 7](07-advanced-tooling.md).

## Coming later

There **is** now an SDM **station-hail** — but it's a *diagnostic* ("what mode/modem is
the peer on?"), not a "call the far operator to the tuner" auto-invite. See
[chapter 7 → Hailing a neighbour](07-advanced-tooling.md#hailing-a-neighbour-hail). An
auto-invite that pulls the far operator into a tuning session is still planned; for now,
arrange the two ends out-of-band (phone, chat) before you start.

## Next

Want these numbers in Grafana? [5. Radio metrics →](05-radio-metrics.md)
