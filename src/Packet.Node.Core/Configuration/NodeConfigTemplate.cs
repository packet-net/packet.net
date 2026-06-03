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
        ports: []
        #  - id: vhf
        #    enabled: true
        #    transport:
        #      kind: kiss-tcp      # serial-kiss | nino-tnc | kiss-tcp
        #      host: 127.0.0.1
        #      port: 8001
        #    ax25:                 # optional — omit for spec defaults
        #      t1Ms: 3000
        #      n2: 10
        #      windowSize: 4
        #    kiss:                 # optional — applied live, no restart
        #      txDelay: 30         # units of 10 ms
        #      persistence: 63
        #      slotTime: 10
        #  - id: hf
        #    enabled: false
        #    transport:
        #      kind: serial-kiss
        #      device: /dev/ttyACM0
        #      baud: 57600
        #  - id: nino
        #    enabled: false
        #    transport:
        #      kind: nino-tnc
        #      device: /dev/ttyACM1
        #      baud: 57600
        #      mode: 6             # NinoTNC mode 0..15

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

        """;
}
