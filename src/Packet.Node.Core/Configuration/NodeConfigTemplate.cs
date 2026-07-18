namespace Packet.Node.Core.Configuration;

/// <summary>
/// The commented YAML template written on first start when no config file
/// exists. It documents every section, ships a placeholder callsign and zero
/// ports (so the node boots as a legal idle node), and binds the telnet console
/// to loopback. The operator edits it in place; the file watcher hot-applies.
/// </summary>
public static class NodeConfigTemplate
{
    /// <summary>The placeholder callsign written into a fresh template. The node
    /// boots idle on it; the operator is expected to replace it.</summary>
    public const string PlaceholderCallsign = "N0CALL";

    /// <summary>The commented YAML written to a fresh config file.</summary>
    public static string Yaml { get; } =
        """
        # Packet.NET node configuration.
        #
        # This file is the node's config blob. It is watched for changes and
        # hot-applied — most edits take effect without a restart. An invalid
        # edit is rejected whole and the running config is kept (see the logs).
        #
        # Edit the callsign below, then add one or more ports under `ports:`.

        schemaVersion: 1

        identity:
          callsign: N0CALL        # <-- your station callsign, e.g. M0LTE-1
          # alias: LONDON         # optional node name
          # grid: IO91wm          # optional Maidenhead locator

        # AX.25 ports. Empty = an idle node (still serves telnet + /healthz).
        # Each port needs a stable, unique `id` (the hot-reload reconcile key)
        # and a transport. Uncomment one of the examples to bring a port up.
        #
        # Tuning: by default a port uses the AX.25 spec defaults (T1 = 6 s, etc.).
        # Those are correct on a fast/reliable link but STALL connected-mode on a
        # slow half-duplex AFSK channel (two equal, phase-locked T1 timers collide
        # forever). For such a channel set `profile: slow-afsk1200` — a named,
        # opt-in bundle of channel-appropriate T1 + CSMA defaults. A profile only
        # fills fields you DON'T set explicitly; an explicit `ax25:`/`kiss:` value
        # always wins. There is deliberately no silent node-wide default: tuning is
        # per-port because T1/TXDELAY depend on the physical channel and one node
        # can mix fast and slow ports.
        ports: []
        #  - id: vhf
        #    enabled: true
        #    transport:
        #      kind: kiss-tcp      # serial-kiss | nino-tnc | kiss-tcp | axudp
        #      host: 127.0.0.1
        #      port: 8001
        #    ax25:                 # optional — omit for spec defaults
        #      t1Ms: 3000
        #      n2: 10
        #      windowSize: 4
        #      n1: 256             # max info-field length (PACLEN / N1), 16..256 octets.
        #                          # Default 256. Lower it on a slow/lossy medium — e.g.
        #                          # ~80 on a shared-HF port — to keep on-air frames short;
        #                          # XID can negotiate it lower but never higher.
        #    netRomQuality: 192    # optional — per-port NET/ROM route quality (BPQ per-port
        #                          # QUALITY), 0..255. Omit to inherit netRom.defaultNeighbourQuality.
        #                          # Set per port on a mixed-grade node (e.g. 191 on one link,
        #                          # 192 on another) so neighbours pick routes correctly.
        #    compat:               # optional — AX.25 compatibility profile, applied live
        #      preset: lenient     # which inbound wire frames the port accepts:
        #                          #   strict   - exactly AX.25 v2.2, nothing else
        #                          #   lenient  - accept every known real-world quirk (the default)
        #                          #   bpq | xrouter | direwolf - match that neighbour's dialect
        #      # Individual named flags override the preset (see
        #      # docs/strict-vs-pragmatic-audit.md for what each accepts and why):
        #      # allowEmptyCallsignBase: true        # all-space callsign slots (BPQ >IS ID beacons)
        #      # allowInfoOnSupervisoryFrames: true  # trailing bytes on S frames
        #      # allowCommandFrameAsResponse: true   # v1.x SABM/DISC without v2 command C-bits
        #      # quirks: default     # SDL session quirks: default (spec-correct) |
        #      #                     # strictly-faithful (figures as drawn, defects
        #      #                     # included - conformance study only)
        #    kiss:                 # optional — applied live, no restart
        #      txDelay: 30         # units of 10 ms
        #      persistence: 63
        #      slotTime: 10
        #      txTail: 5           # units of 10 ms. Set this > 0 if your modem
        #                          # needs a TX tail: a SOFTWARE modem (Dire Wolf
        #                          # / samoyed), or a NinoTNC into a radio with a
        #                          # non-zero-latency audio path, will clip the end
        #                          # of a transmission without one. Leave it unset
        #                          # (0) for a NinoTNC into a fully analogue audio
        #                          # path. It is NOT a channel/profile property —
        #                          # only you know your modem + radio.
        #      t1FromTxComplete: false  # default false. With an ACKMODE-capable TNC,
        #                          # run T1 from the frame's actual TX-completion echo
        #                          # instead of from enqueue — T1 then bounds the peer's
        #                          # response time, not your own TX queue + airtime.
        #      ackMode: false      # default false. Set true on a kiss-tcp port to a
        #                          # TNC / net-sim that honours the G8BPQ ACKMODE
        #                          # extension: the node then PACES its outbound
        #                          # frames — each sent in ACKMODE, the next held
        #                          # until the prior frame's TX-completion echo
        #                          # arrives — serialising onto the half-duplex
        #                          # channel instead of colliding with itself.
        #                          # Toggling it restarts the port (it is a
        #                          # construction-time choice, not a live setting).
        #  - id: hf
        #    enabled: false
        #    transport:
        #      kind: serial-kiss
        #      device: /dev/ttyACM0
        #      baud: 57600
        #  - id: nino
        #    enabled: false
        #    profile: slow-afsk1200 # slow half-duplex VHF packet: longer, asymmetric
        #                           # T1 (10 s) so the link doesn't stall on contention,
        #                           # plus sane CSMA. Override any field below.
        #                           # (TX tail is NOT part of any profile — set
        #                           #  kiss.txTail yourself if your modem needs it.)
        #    transport:
        #      kind: nino-tnc
        #      device: /dev/ttyACM1
        #      baud: 57600
        #      mode: 6             # NinoTNC mode 0..15
        #    radio:                # optional — attach the radio's own serial CONTROL
        #      kind: tait-ccdi     # channel (Tait TM8100/TM8200 CCDI; CAT/CI-V later).
        #      serial: 1G000123    # PREFERRED — pin by CCDI serial: stable across
        #                          # /dev/ttyUSB* renumbering AND the CP2102 shared-USB-serial
        #                          # dongles that make by-id ambiguous. Bring-up scans for it;
        #                          # discover it with GET /api/v1/radios/scan.
        #      # port: /dev/ttyUSB0  # OR pin by device path (exactly one of serial/port).
        #      baud: 28800         # inbound frames then carry per-frame RSSI/SNR, and the
        #                          # radio's status/health shows on GET /api/v1/radios.
        #                          # Serial-modem ports only (serial-kiss / nino-tnc). A radio
        #                          # that won't open (or a serial not found) degrades cleanly —
        #                          # the port still runs, just without signal metadata.
        #      healthIntervalSeconds: 10  # optional — health sample cadence (default 10 s).
        #  - id: axudp
        #    enabled: false
        #    transport:
        #      kind: axudp         # AX.25 frames over UDP (RFC-1226 AXIP/AXUDP, BPQAXIP)
        #      host: 10.0.0.2      # remote peer to send frames to
        #      port: 10093         # remote UDP port
        #      localPort: 10093    # local UDP port to receive on (match for a symmetric tunnel)
        #                          # AXUDP always carries the 2-octet AX.25 FCS (the de-facto
        #                          # wire form — required by LinBPQ BPQAXIP, XRouter, ax25ipd
        #                          # & JNOS). No FCS option.
        #    # No profile here: a UDP tunnel is fast + reliable, so the spec
        #    # defaults are correct. Don't apply a slow-channel profile to AXUDP.
        #  - id: tait
        #    enabled: false
        #    transport:
        #      kind: tait-transparent  # a Tait TM8100/TM8200 in Transparent mode AS the modem —
        #                              # NO external TNC. AX.25 rides the radio's own FFSK byte
        #                              # pipe with KISS SLIP framing. One device, no audio wiring.
        #      serial: 1G000123        # PREFERRED — pin by CCDI serial (stable across /dev/ttyUSB*
        #                              # renumbering + shared-USB-serial CP2102 dongles). Bring-up
        #                              # scans for it; discover it with GET /api/v1/radios/scan.
        #      # device: /dev/ttyUSB0  # OR pin by device path (exactly one of serial/device).
        #      baud: 28800             # CCDI command-mode baud (enter/exit rate).
        #      transparentBaud: 28800  # Transparent terminal baud (differs → the port re-clocks).
        #      ffskBaud: 2400          # FFSK over-air baud, for per-frame airtime estimation.
        #      leadInMs: 100           # modelled TX lead-in (key-up + FFSK preamble).
        #                              # NOTE no `radio:` block here: the radio IS the modem, so
        #                              # there is no CCDI control channel and no per-frame RSSI —
        #                              # only airtime timing. Teardown exits Transparent (restores
        #                              # Command). WARNING: if the radio is programmed "Ignore
        #                              # Escape Sequence" ON, the +++ exit fails — recovery is a
        #                              # power cycle. Program the escape sequence honoured.
        #  - id: sound
        #    enabled: false
        #    transport:
        #      kind: soundmodem    # the in-process pdn-soundmodem engine — the node runs the
        #                          # demodulator/modulator itself over a sound card. NO external
        #                          # TNC or daemon; native DCD gates the AX.25 stack's carrier sense.
        #      device: default     # ALSA capture+playback device (e.g. default, plughw:1,0), OR a
        #                          # flex:<radio>[:slice][@station] FlexRadio device (see below).
        #      captureRate: 48000  # card-native rate; the modem decimates to the mode's DSP rate.
        #                          # Must be a positive multiple of it (48000 works for every mode).
        #      mode: afsk1200      # the modem mode. The full set: the NinoTNC-compatible
        #                          #   afsk1200[-fx25|-fx25rx|-multi|-il2p|-il2p-nocrc], afsk300[-il2p|-il2pc],
        #                          #   bpsk300[-multi|-nocrc], bpsk1200, qpsk600/2400/3600,
        #                          #   fsk9600[-il2p], fsk4800-il2p modes; the C4FSK modes
        #                          #   (c4fsk9600/c4fsk19200); the FreeDV HF OFDM modes
        #                          #   (freedv-datac0/1/3/4/13/14); and the MIL-STD-188-110D App-D
        #                          #   modes (ms110d-wn0..6/13).
        #      # frequency: 1700   # centre/carrier Hz; 0/omitted = the mode convention. Only the
        #                          # variable-centre afsk/bpsk/qpsk families accept one (300..3300);
        #                          # rejected for the baseband fsk*/c4fsk* + fixed-centre freedv-*/ms110d-*.
        #      # ptt: serial:/dev/ttyUSB0:rts  # PTT: empty=VOX, serial:<dev>[:rts|:dtr], cm108:<hidraw>[:gpio].
        #                          # Leave empty for a flex: device — the radio keys itself.
        #    # bpsk300 is the differential frequency-diversity BANK — tune it with the two knobs
        #    # below (bpsk1200 stays the legacy single-carrier modem):
        #    #  mode: bpsk300
        #    #  offsetPairs: 4      # bank width: 2*pairs+1 stepped decoder branches (0 = single modem;
        #    #                      # omit = the mode default, 4).
        #    #  offsetStepHz: 7.5   # Hz step between branches (omit = the baud-derived default, baud/40).
        #    #  pskDetector: differential  # coherent | differential (omit = per-family default:
        #    #                             # BPSK differential, QPSK coherent).
        #    # A flex: device drives a FlexRadio headless slice — key it with a flex: block and NO ptt:
        #    #  device: "flex:MyFlex"
        #    #  flex:
        #    #    frequency: "14.100000"  # slice frequency (MHz, six-decimal Flex form)
        #    #    antenna: ANT1
        #    #    mode: DIGU              # a data slice mode
        #    #    daxChannel: "1"         # pick one SmartSDR isn't using when sharing a box

        # Operator-facing text. {node}/{call} are expanded.
        services:
          banner: "Welcome to {node} ({call})"
          prompt: "{call}> "

        # Management surfaces.
        management:
          telnet:
            enabled: true
            bind: 127.0.0.1        # loopback only by default
            port: 8011
          http:
            bind: 127.0.0.1
            port: 8080

        # NET/ROM. By default the node only HEARS NODES routing broadcasts on the
        # AX.25 ports and builds a routing table you can see with the `N` (Nodes)
        # command — it originates nothing on the air and cannot disturb a QSO.
        # The TX-bearing features are OPT-IN:
        #   broadcast: true  -> also originate your own NODES broadcast (advertise
        #                       your node + learned routes to neighbours).
        #   routing:         -> how much your node routes across the network:
        #       none     (default) passive — listen + maintain the table only.
        #       endpoint           open interlinks so `connect <alias>` can route
        #                          across the network, but don't relay transit.
        #       transit            full router — interlinks + relay other stations'
        #                          transit traffic onward (the network-layer role).
        # (The old `connect:`/`forward:` bools are still accepted for back-compat,
        #  but prefer `routing:` — `forward` was inert without `connect`.)
        # NET/ROM has no single normative standard (BPQ is the de-facto reference),
        # so the knobs default to the canonical values; override only to match a
        # specific network's conventions.
        netRom:
          enabled: true
          # broadcast: false              # originate NODES (TX is opt-in)
          # routing: none                 # none | endpoint | transit (routing role; TX/interlinks are opt-in)
          # alias: NODE                   # your NET/ROM alias in broadcasts (defaults to the identity alias)
          # defaultNeighbourQuality: 192  # assumed quality of a directly-heard link
          #                               # (override per port with a port's netRomQuality:)
          # minQuality: 0                 # drop routes below this (raise to reject mislabelled qualities)
          # obsoleteInitial: 6            # obsolescence count a route starts at (OBSINIT)
          # obsoleteMinimum: 4            # stop advertising a route below this (OBSMIN) before it is purged
          # sweepIntervalSeconds: 3600    # route decay + NODES broadcast interval (NODESINTERVAL)
          # window: 4                     # L4 circuit send window (L4WINDOW)
          # transportTimeoutSeconds: 5    # L4 retransmit timeout (L4TIMEOUT)
          # transportRetries: 3           # L4 max retransmits before a circuit fails (L4RETRIES)
          # timeToLive: 25                # L3 hop limit on circuits we originate (L3TIMETOLIVE)

        # ID beacon. A periodic connectionless AX.25 UI frame (dest BEACON, PID 0xF0)
        # transmitted on each port to announce the node's presence. DEFAULT-OFF — a
        # node that never beaconed keeps not beaconing. {node}/{call} are expanded in
        # the text. A port may override this default via the port's `beacon:` block.
        beacon:
          enabled: false                  # turn the ID beacon on (TX is opt-in)
          intervalMinutes: 30             # minutes between beacons (>= 1)
          text: "{node} pdn node"         # {node} = alias else callsign; {call} = callsign
        # Per-port override (under a `ports:` entry):
        #    beacon:
        #      enabled: true               # this port's on/off (authoritative for the port)
        #      intervalMinutes: 15         # omit to inherit the system default above
        #      text: "{node} on VHF"       # omit to inherit the system default text

        # Traffic log. Persists EVERY AX.25 frame the node sends or receives, on
        # every port, to a SEPARATE SQLite database (default: traffic.db beside
        # pdn.db) so there is durable off-air frame history when something needs
        # diagnosing — query it with GET /api/v1/traffic. Kept apart from pdn.db
        # so a huge or corrupt frame log can never threaten node state. Bounded:
        # rows older than retentionDays are pruned, and the file is hard-capped
        # at maxMb (oldest rows pruned beyond either bound). `enabled`/`path`
        # apply at startup (restart to change); retentionDays/maxMb are re-read
        # live at each prune pass.
        traffic:
          enabled: true                   # log every sent/received frame (ON by default)
          # path: traffic.db              # SQLite file; omit for traffic.db beside pdn.db
          # retentionDays: 14             # prune rows older than this many days (>= 1)
          # maxMb: 512                    # hard cap on the database file size (>= 1)

        # POCSAG paging service. A TCP line server (PAGE/HEARD) that transmits + receives POCSAG
        # pages over its OWN dedicated soundmodem audio device (separate from any port above).
        # Off by default. See docs/soundmodem.md.
        # paging:
        #   enabled: true
        #   device: default         # ALSA device, or a flex:<radio>[:slice][@station] device
        #   captureRate: 48000      # ALSA only; a flex: device supplies its own DAX clock
        #   bind: 127.0.0.1         # TCP bind for the paging line server
        #   port: 8106
        #   baud: 1200              # POCSAG baud: 512, 1200 (DAPNET) or 2400
        #   # invertPolarity: false # invert the baseband polarity if your radio needs it
        #   # ptt: serial:/dev/ttyUSB0:rts   # ALSA only; empty = VOX. A flex: device keys itself.

        # ARDOP virtual TNC. An ardopcf-compatible TCP host interface (command socket + data socket
        # on port+1) backed by a dedicated soundmodem audio device, so an external ARDOP host —
        # BPQ's DRIVER=ARDOP, Pat, Winlink Express — can drive this node's sound card / FlexRadio
        # as an ARDOP modem. Off by default. See docs/soundmodem.md.
        # ardop:
        #   enabled: true
        #   device: default         # ALSA device, or a flex:<radio>[:slice][@station] device
        #   captureRate: 48000      # ALSA only; a flex: device supplies its own DAX clock
        #   bind: 127.0.0.1         # TCP bind for the ARDOP host interface
        #   port: 8515              # command socket; the data socket listens on port+1 (8516)
        #   # ptt: serial:/dev/ttyUSB0:rts   # ALSA only; empty = VOX. A flex: device keys itself.

        """;
}
