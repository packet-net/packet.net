# The soundmodem transport and services

PDN can be its own modem. The `soundmodem` transport runs the pdn-soundmodem
engine **in-process** — the node demodulates and modulates AX.25 itself over a
sound card (or a FlexRadio), with **no external TNC or daemon**. Native carrier
detect (DCD) feeds the AX.25 stack's carrier-sense gate, transmit completion is
sample-accurate, and the KISS TXDELAY / PERSIST / SLOTTIME knobs drive the
modem's own p-persistent CSMA.

The same engine also backs two node-level **services** (not transports): a
**POCSAG paging** line server and an **ARDOP virtual TNC**. All three are
configured in the node's YAML and are off by default.

> These are all soundmodem-audio features. There is no dedicated web-UI editor
> for the paging/ardop services yet — configure them in YAML (a Services editor
> is a follow-up). The `soundmodem` transport **is** editable in the Ports
> screen of the control panel.

## A soundmodem port

Add a port whose transport `kind` is `soundmodem`:

```yaml
ports:
  - id: sound
    enabled: true
    transport:
      kind: soundmodem
      device: default       # ALSA capture+playback device (default, plughw:1,0),
                            # or a flex:<radio>[:slice][@station] FlexRadio device.
      captureRate: 48000    # card-native rate; the modem decimates to the mode's DSP
                            # rate. Must be a positive multiple of it (48000 fits every
                            # mode). Ignored for a flex: device (it clocks off DAX).
      mode: afsk1200        # the modem mode — see the catalogue below.
      # frequency: 1700     # centre/carrier Hz; 0/omit = the mode convention. Only the
                            # variable-centre afsk/bpsk/qpsk families accept one (300–3300).
      # ptt: serial:/dev/ttyUSB0:rts   # empty = VOX · serial:<dev>[:rts|:dtr] · cm108:<hidraw>[:gpio]
```

Because a soundmodem port carries native AX.25 over a shared CSMA channel, it
uses the same `kiss:` channel-access block (TXDELAY / PERSIST / SLOTTIME / TXTAIL)
and `ax25:` timing block as the RF TNC transports.

### The mode catalogue

`mode` accepts the shared modem catalogue (the server's
`SoundModemValidator.KnownModes` — `ModemCatalog.KnownModes` minus
`bpsk1200-multi`):

| Family | Modes |
|---|---|
| AFSK 1200 | `afsk1200`, `afsk1200-fx25`, `afsk1200-fx25rx`, `afsk1200-multi`, `afsk1200-il2p`, `afsk1200-il2p-nocrc` |
| AFSK 300 | `afsk300`, `afsk300-il2p`, `afsk300-il2pc` |
| BPSK | `bpsk300`, `bpsk300-multi`, `bpsk300-nocrc`, `bpsk1200` |
| QPSK | `qpsk600`, `qpsk2400`, `qpsk3600` |
| FSK | `fsk9600`, `fsk9600-il2p`, `fsk4800-il2p` |
| C4FSK | `c4fsk9600`, `c4fsk19200` |
| FreeDV (HF OFDM) | `freedv-datac0`, `freedv-datac1`, `freedv-datac3`, `freedv-datac4`, `freedv-datac13`, `freedv-datac14` |
| MIL-STD-188-110D App-D | `ms110d-wn0` … `ms110d-wn6`, `ms110d-wn13` |

Three families are new alongside the classic NinoTNC-compatible AFSK/BPSK/QPSK/FSK
set:

- **C4FSK** (`c4fsk9600` / `c4fsk19200`) — four-level FSK at 9600 / 19200.
- **FreeDV datac** (`freedv-datac0/1/3/4/13/14`) — the FreeDV HF **OFDM** data
  waveforms, for robust HF paths.
- **MS110D App-D** (`ms110d-wn0..6/13`) — MIL-STD-188-110D Appendix D
  narrowband waveforms (`wn` = walsh/narrowband variants).

The baseband `fsk*` / `c4fsk*` modes and the fixed-centre `freedv-*` / `ms110d-*`
modes have **no settable centre frequency** — a non-zero `frequency` is rejected
rather than silently ignored. Only the variable-centre `afsk` / `bpsk` / `qpsk`
families accept one (0 = the mode convention, otherwise 300–3300 Hz).

### The bpsk300 differential frequency-diversity bank

`bpsk300` is a **differential frequency-diversity bank**, not a single carrier.
It runs `2·offsetPairs + 1` stepped decoder branches across the passband and
combines them, which buys robustness on a drifting or selective-fade HF channel.
Tune it with two knobs (both ignored by non-bank modes):

```yaml
transport:
  kind: soundmodem
  device: plughw:1,0
  mode: bpsk300
  offsetPairs: 4        # bank width: 2*pairs+1 branches (0 = a plain single modem;
                        # omit = the mode default, 4).
  offsetStepHz: 7.5     # Hz step between branches (omit = the baud-derived default, baud/40).
  pskDetector: differential   # coherent | differential (omit = per-family default:
                              # BPSK differential, QPSK coherent).
```

`bpsk1200` is deliberately **not** a bank — it stays the legacy single-carrier
modem (the 1200-baud diversity variant `bpsk1200-multi` is not exposed, pending
over-the-air evidence).

`pskDetector` (`coherent` | `differential`) applies to all the `bpsk*` / `qpsk*`
modes; omit it for the per-family default (BPSK differential, QPSK coherent).

### FlexRadio (`flex:`) devices

Set `device` to `flex:<radio>[:slice][@station]` to drive a FlexRadio over DAX
instead of a sound card. For a **headless** slice, add a `flex:` block to tune
it; leave `ptt` empty — the radio keys itself over CAT, so a configured PTT is
rejected. (An attach-mode `@station` flex device and ALSA devices ignore the
`flex:` block.)

```yaml
transport:
  kind: soundmodem
  device: "flex:MyFlex"
  mode: freedv-datac1
  flex:
    frequency: "14.100000"   # slice frequency (MHz, six-decimal Flex form; default 14.100000)
    antenna: ANT1            # RX/TX antenna (default ANT1)
    mode: DIGU              # slice demod mode — a data mode (default DIGU)
    daxChannel: "1"          # the DAX channel the client claims; pick one SmartSDR isn't
                            # using when sharing a box (default 1)
```

`captureRate` does not apply to a `flex:` device — it clocks off the DAX stream.

## Node-level services

Both services run over their **own dedicated audio device** (independent of any
`ports:` entry), use the same ALSA / `flex:` device model, and are off by
default.

### POCSAG paging

A TCP line server (PAGE / HEARD verbs) that transmits and receives POCSAG pages:

```yaml
paging:
  enabled: true
  device: default        # ALSA device, or flex:<radio>[:slice][@station]
  captureRate: 48000     # ALSA only (a flex: device supplies its own DAX clock)
  bind: 127.0.0.1
  port: 8106             # the paging line server's TCP port
  baud: 1200             # POCSAG baud: 512, 1200 (DAPNET) or 2400
  # invertPolarity: false
  # ptt: serial:/dev/ttyUSB0:rts   # ALSA only; empty = VOX (a flex: device keys itself)
```

### ARDOP virtual TNC

An **ardopcf-compatible** TCP host interface, so an external ARDOP host can drive
this node's sound card / FlexRadio as an ARDOP modem. It exposes the standard
ardopcf two-socket layout: a **command socket** on `port` and a **data socket**
on `port + 1` (defaults **8515** command / **8516** data). BPQ (`DRIVER=ARDOP`),
Pat, and Winlink Express can all connect to it.

```yaml
ardop:
  enabled: true
  device: default        # ALSA device, or flex:<radio>[:slice][@station]
  captureRate: 48000     # ALSA only (a flex: device supplies its own DAX clock)
  bind: 127.0.0.1
  port: 8515             # command socket; the data socket listens on port+1 (8516)
  # ptt: serial:/dev/ttyUSB0:rts   # ALSA only; empty = VOX (a flex: device keys itself)
```

Point your ARDOP host at `127.0.0.1:8515` (host/command) — Pat's ardop connection,
BPQ's `ARDOPPORT`, or Winlink Express's ARDOP TNC session all speak this.

## See also

- `packaging/packetnet.yaml` and the first-run template
  (`NodeConfigTemplate.cs`) — commented copies of every block above.
- `docs/node-api.yaml` — the `SoundModemTransport`, `PagingConfig`, and
  `ArdopConfig` schemas the control API exposes.
