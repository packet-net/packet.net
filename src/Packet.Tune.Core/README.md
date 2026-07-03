# Packet.Tune.Core

Primitives for tuning a NinoTNC + radio pair when the two ends are apart —
the engine behind the `packet-tune` CLI (`tools/Packet.Tune`):

- **`TuningTelegram`** — the compact `V1|seq|verb|args` coordination protocol
  (verbs `HI`/`RQ`/`MS`/`AD`/`BY`), with a documented compact wire form that
  fits the 32-character Tait SDM budget.
- **`ITuningLink`** — the transport seam, in two flavours:
  - **`SdmTuningLink`** — telegrams ride Tait CCDI short data messages over
    the radios' own FFSK modem at factory deviation, fully independent of the
    NinoTNC pot under tune (bootstrap-safe, no internet);
  - **`WebSocketTuningLink`** + **`RendezvousRelay`** — the internet flavour:
    both ends join a minimal relay with a spoken 6-digit single-use PIN.
- **`TuningSession`** — the shared meter/tuned assistant loop: the meter
  requests bursts, measures decode rate + IL2P FEC-corrected bytes + lost-ADC
  clipping + CCDI RSSI (GETRSSI is gone in NinoTNC firmware 3.44), and sends
  `UP`/`DN`/`OK` advice to the operator at the TX-DEV pot.
- **`TuningDoctor`** — capability probes for the whole TNC↔radio stack
  (firmware features, DIPs, mode, TXDELAY software control, CCDI identity,
  PROGRESS, SDM programming, TNC↔radio pairing), each with a one-line remedy.

Source: [github.com/packet-net/packet.net](https://github.com/packet-net/packet.net).
