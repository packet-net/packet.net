# NinoTNC hardware repros

Self-contained scripts intended to be shared upstream with the relevant
hardware / firmware authors. Each is a single file with minimal
dependencies so it can be dropped onto someone else's host without
pulling Packet.NET source.

## `ninotnc_mode12_repro.py` — DRAFT, does not yet reliably reproduce

Skeleton repro for the **mode 12 (300 AFSK AX.25) intermittent
RX-lockup** observed on the back-to-back NinoTNC pair on 2026-05-14.

**Status:** the harness works (opens both ports, sets mode 12 via
SETHW, walks a TXDELAY ladder, reports the failure pattern in a
machine-readable way), but in verification runs the catastrophic
RX-lockup *did not fire*. Only the benign first-frame-after-SetMode
artefact appears. **The trigger condition isn't yet understood**, so
this script is not yet useful to hand to the firmware author. See
the in-tree task list ("make it actually reproduce the bug") for
hypotheses to try.

```sh
pip install pyserial
python3 ninotnc_mode12_repro.py /dev/ttyACM0 /dev/ttyACM1
# or on Windows:
python3 ninotnc_mode12_repro.py COM6 COM8 --txdelays 20,50,100
```

When the bug fires (as it did in two of the C# `mode12-probe` runs):
the failure summary will read like `0 + contiguous 5..99 (95 frames)`
— frame 0 plus every subsequent frame lost in a single run. Modes 13
(`300 AFSKPLL IL2P`) and 14 (`300 AFSKPLL IL2P+CRC`) have never
exhibited this pattern over the same N=50/100 sample sizes, which
points the finger at mode 12's plain-AFSK demodulator rather than
300-baud air time or AX.25 framing.

Full background and observed data are in
[`docs/nino-tnc-characterisation.md`](../../docs/nino-tnc-characterisation.md)
under the *Mode 12 deep dive* section.
