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
        #   connect:   true  -> `connect <alias>` may route across the network via
        #                       NET/ROM L4 circuits (open an interlink to the best
        #                       neighbour + establish an end-to-end circuit).
        # NET/ROM has no single normative standard (BPQ is the de-facto reference),
        # so the knobs default to the canonical values; override only to match a
        # specific network's conventions.
        netRom:
          enabled: true
          # broadcast: false              # originate NODES (TX is opt-in)
          # connect: false                # allow connect <alias> across the network (opt-in)
          # alias: NODE                   # your NET/ROM alias in broadcasts (defaults to the identity alias)
          # defaultNeighbourQuality: 192  # assumed quality of a directly-heard link
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

        """;
}
