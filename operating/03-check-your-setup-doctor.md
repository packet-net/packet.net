# 3. Check your setup (the doctor)

**Goal:** find out, in plain language, whether a port's TNC and radio are wired and
tuned correctly — and what to fix when they're not.

The **doctor** is a one-click health check for a single port. It runs a checklist of
probes and shows each as **pass** (green), **fail** (red, with a suggested fix), or
**unknown** (grey).

## Run it (web UI)

On the **Ports** screen, each port has a **Check radio** button. Click it and a
*Check radio setup* panel opens and **auto-runs the safe check** — a read-only probe
that **never transmits**.

Each probe row shows its name, a plain-language detail line, and — on a failure — a
**→ remedy** telling you what to do about it. A summary at the bottom says how many
checks failed.

## Safe by default; full check transmits

The doctor has two levels, and this distinction matters on a shared channel:

- **Safe check** (runs automatically on open, and on **Re-check**). Read-only. It
  never keys the transmitter. Probes that *need* to transmit show as **unknown**.
- **Full check** — the **Run full check (briefly transmits)** button. This runs the
  transmitting probes: TXDELAY timing, the SDM side-channel, and TNC↔radio pairing.
  It **briefly keys your transmitter** and perturbs TXDELAY.

> [!WARNING]
> The full check **transmits on your radio**. Only run it when it's appropriate to
> key up on that channel. The safe check is always fine to run — it just listens and
> asks the radio questions over its control channel.

Because the full check keys the transmitter, it is an **admin-scoped, audited**
action; the safe check only needs read access.

## What it's telling you

The probes cover the chain from "is there a TNC?" through "is the radio answering?"
to "is the transmit path actually tuned?". Typical rows:

- **TNC present** — the modem responded on its serial port.
- **Radio present** — the attached radio answered CCDI.
- **SDM** *(full check)* — the radio-to-radio Short Data Message side-channel works
  (this is the coordination channel [tuning](04-tune-your-link.md) rides on).
- **TXDELAY / pairing** *(full check)* — the transmit timing and TNC↔radio wiring
  check out.

A failing row always carries a remedy — read it, do it, hit **Re-check**.

## From the command line

The same checks are available as a standalone tool for bench work, without the web
UI. See [chapter 7](07-advanced-tooling.md) for how the `packet-tune` tooling is run;
the doctor verb is:

```
packet-tune doctor <tncPort> [ccdiPort] [--json] [--callsign N0CALL] [--mode 6]
```

- `<tncPort>` — the TNC's serial port (e.g. `/dev/ttyACM0`).
- `[ccdiPort]` — optionally, the radio's CCDI serial port, to probe the radio too.
- `--json` — machine-readable output.
- Exit code **0** = no failed probe, **1** = at least one failure (handy in scripts).

> [!NOTE]
> Like the web UI's full check, the CLI doctor's TXDELAY / SDM / pairing probes
> **transmit briefly**. It prints a reminder to that effect when it runs.

## Coming later

A dedicated doctor for the [TNC-less Transparent](06-tnc-less-tait-links.md) setup
(checking the Transparent-mode programming gotchas) is planned but **not shipped
yet** — for now, work through that chapter's checklist by hand.

## Next

Doctor says the setup is sound but the link is still marginal? It's probably
deviation. Tune it:
[4. Tune your link →](04-tune-your-link.md)
