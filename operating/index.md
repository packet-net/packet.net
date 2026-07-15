# Running a PDN node with a radio

This guide is for **operators** — people running the packet.net node host (PDN,
`pdn.m0lte.uk`-style) with a real radio attached. It is task-oriented and
UX-first: attach a radio, then **see and improve your link**. It is *not* the
[developer guide](../guide/index.md) (that one is for people building software on
the NuGet libraries).

You do not need to be a programmer to use anything here. Most of it is buttons in
the node's web control panel; the rest is a few lines of YAML in your node config,
or a command you paste into a terminal on the node.

## What "radio support" gives you

Out of the box, a PDN port talks to a TNC (a NinoTNC, a KISS TNC, Dire Wolf) and
the radio behind that TNC is invisible to the node — it is just a box that keys up
when told to. **Attaching a radio** wires the node to the radio's own serial
control channel as well, so the node can *read* the radio while packets flow:

- **See link quality** — every received frame gets tagged with its **RSSI and
  SNR**, and a dashboard panel shows each radio's live health (signal, channel
  activity, PA temperature, an antenna-health trend).
- **Check the setup** — a one-click "doctor" tells you in plain language whether
  the TNC and radio are wired and tuned correctly, and what to fix.
- **Tune the link** — a guided workflow walks you through setting the radio's
  transmit deviation while you watch the decode numbers move.
- **Sense the channel in hardware** — a radio-attached port automatically holds
  off transmitting while the channel is busy (real DCD), no config needed.
- **Graph and alert** — a Prometheus `/metrics` surface exposes all of it for
  Grafana.
- **Work the rig from the node** — a port with a `rig:` block (CAT control) gets a
  live card in the panel: dial frequency, mode, PTT and TX meters, with QSY and
  mode buttons.

Two kinds of radio control are supported today: the **Tait TM8100 / TM8200**
family, over its CCDI serial protocol, and **CAT rigs** — anything hamlib's
`rigctld` or flrig can drive — via a port's `rig:` block (both are
[chapter 1](01-attach-a-radio.md)).

## Which of these do I want?

Start with the row that matches what you are trying to do.

| I want to… | Go to | What it needs |
|---|---|---|
| Point the node at my radio | [1. Attach a radio](01-attach-a-radio.md) | A control cable + a couple of config fields (or one scan-and-click) |
| Control my rig (CAT) from the node | [1. Attach a radio](01-attach-a-radio.md#kind-rig--re-use-the-ports-cat-rig-as-its-radio) | hamlib `rigctld` or flrig — or let the node run `rigctld` for you |
| See how good my link is | [2. See your link quality](02-see-your-link-quality.md) | A radio attached (step 1) |
| Find out why a link is bad | [3. Check your setup (the doctor)](03-check-your-setup-doctor.md) | Nothing extra — the safe check never transmits |
| Set my transmit deviation right | [4. Tune your link](04-tune-your-link.md) | A second radio/TNC at the other end |
| Graph radio health in Grafana | [5. Radio metrics](05-radio-metrics.md) | Prometheus scraping the node |
| Run a Tait link with **no TNC** | [6. TNC-less Tait-to-Tait links](06-tnc-less-tait-links.md) | Two Tait radios, nothing else |
| Flash firmware / use the CLI tools | [7. Advanced tooling](07-advanced-tooling.md) | A terminal on the node |
| Run the modems/radios on a **separate box** from PDN | [8. Split-station RF head-end](08-split-station-head-end.md) | A spare Pi + the head-end daemon |

## Three ways a radio can join a port

This trips people up, so it is worth stating up front. There are **three different**
ways a radio can relate to a PDN port, and they are not the same thing:

1. **A Tait radio *attached to* a TNC port** (the `radio:` block, `kind:
   tait-ccdi`). Your modem is still a TNC (NinoTNC, KISS TNC); the radio sits
   *beside* it and the node reads it over a *second* serial cable for telemetry.
   This is what gives you RSSI/SNR, the health panel, the doctor, and hardware
   carrier-sense. Covered in [chapter 1](01-attach-a-radio.md).
2. **A Tait radio *as* the port** (the `tait-transparent` transport kind). There is
   **no TNC at all** — AX.25 rides the radio's own built-in FFSK modem. One device,
   no audio wiring, but no per-frame signal telemetry. Covered in
   [chapter 6](06-tnc-less-tait-links.md).
3. **A CAT rig re-presented as the port's radio** (a `rig:` block plus `radio:
   kind: rig`). The port's CAT rig — anything hamlib's `rigctld` or flrig can
   drive — doubles as its radio, with **no second cable at all**, so it works with
   **any** transport kind (a `kiss-tcp` soundmodem included). The rig's DCD gates
   the node's transmissions like hardware carrier-sense, and where the rig reports
   calibrated signal strength, inbound frames get tagged with it. Covered in
   [chapter 1](01-attach-a-radio.md#kind-rig--re-use-the-ports-cat-rig-as-its-radio).

Options 1 and 3 are the same idea — a radio *attached to* a TNC port, read while
packets flow — with different control protocols, and most operators want one of
them. Option 2 is a special case for people who have two Tait radios and no TNCs
and want a working link anyway.

---

Ready? Start with [attaching a radio →](01-attach-a-radio.md)
