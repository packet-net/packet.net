# Packet.Tune.Core

Primitives for tuning a NinoTNC + radio pair when the two ends are apart —
the engine behind the `packet-tune` CLI (`tools/Packet.Tune`):

- **`TuningTelegram`** — the compact `V1|seq|verb|args` coordination protocol
  (verbs `HI`/`RQ`/`MS`/`AD`/`BY`/`MODE`), with a documented compact wire form
  that fits the 32-character Tait SDM budget.
- **`ITuningLink`** — the transport seam, in two flavours:
  - **`SdmTuningLink`** — telegrams ride the radio's own small-datagram side
    channel (`Packet.Radio.IRadioSideChannel`; canonically Tait CCDI short
    data messages over the radios' internal FFSK modem at factory deviation),
    fully independent of the NinoTNC mode/pot under tune or negotiation
    (bootstrap-safe, no internet);
  - **`WebSocketTuningLink`** + **`RendezvousRelay`** — the internet flavour:
    both ends join a minimal relay with a spoken 6-digit single-use PIN.
- **Mode coordination** (`ModeCoordinator`/`ModeResponder` + the `MODE`
  telegrams): renegotiate the TNC mode — and optionally the radio channel —
  over the mode/channel-agnostic side channel: propose → confirm → commit
  with delivery receipts, probe-verify the switched link both ways, and on
  any failure revert both ends to the session's home mode/channel (responder
  idle watchdog as the backstop). The Phase-10 mode-agility seed.
- **`TuningSession`** — the shared meter/tuned assistant loop: the meter
  requests bursts, measures decode rate + IL2P FEC-corrected bytes + lost-ADC
  clipping + CCDI RSSI (GETRSSI is gone in NinoTNC firmware 3.44), and sends
  `UP`/`DN`/`OK` advice to the operator at the TX-DEV pot.
- **`TuningDoctor`** — capability probes for the whole TNC↔radio stack
  (firmware features, DIPs, mode, TXDELAY software control, CCDI identity,
  PROGRESS, SDM programming, TNC↔radio pairing), each with a one-line remedy.

Source: [github.com/packet-net/packet.net](https://github.com/packet-net/packet.net).
