namespace Packet.Node.Core.Configuration;

/// <summary>
/// The node's complete configuration, as a format-agnostic immutable record
/// tree. This is the <b>stable interface</b> every consumer
/// (<see cref="Hosting.NodeHostedService"/>, the console, the port supervisor)
/// depends on — it deliberately knows nothing about YAML, SQLite, or whatever
/// future store the config lives in. The <see cref="IConfigProvider"/> seam
/// produces <see cref="NodeConfig"/> instances; everything downstream reads
/// only this shape.
/// </summary>
/// <remarks>
/// Slice 1 loads this from a YAML file (<see cref="FileConfigProvider"/>); a
/// later slice stores the same YAML in a <c>config.db</c> column behind the
/// same seam. Nothing here couples to the serialisation format.
/// </remarks>
public sealed record NodeConfig
{
    /// <summary>The schema version the running code produces and understands — the single
    /// source of truth. <see cref="SchemaVersion"/> defaults to it, the store stamps it on
    /// every <c>Save</c>, and the load-time migration chain
    /// (<see cref="NodeConfigSchemaMigrations"/>) targets it. When a schema bump changes the
    /// persisted JSON shape: bump this AND register one <c>vN → vN+1</c> migration.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Schema version of the persisted config. Bumped when the shape
    /// changes incompatibly; lets a future loader migrate older blobs. Defaults to
    /// <see cref="CurrentSchemaVersion"/> — a config built in code is always at current.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Station identity — the callsign every <see cref="Ports"/> entry
    /// listens as, plus optional human-facing metadata.</summary>
    public required Identity Identity { get; init; }

    /// <summary>The configured AX.25 ports. May be empty — a node with no ports
    /// is a legal idle node (it still answers telnet + <c>/healthz</c>).</summary>
    public IReadOnlyList<PortConfig> Ports { get; init; } = [];

    /// <summary>Operator-facing service text (banner, prompt) — hot-reloadable
    /// by reference swap.</summary>
    public ServicesConfig Services { get; init; } = new();

    /// <summary>Management surfaces: the local telnet console and the (slice-1
    /// inert) web server bind.</summary>
    public ManagementConfig Management { get; init; } = new();

    /// <summary>NET/ROM awareness (read-only): hear NODES broadcasts and build a
    /// routing table. Pure consumer — never transmits. See <see cref="NetRomConfig"/>.</summary>
    public NetRomConfig NetRom { get; init; } = new();

    /// <summary>The system-default ID beacon — a periodic connectionless AX.25 UI
    /// frame sent per port to announce the node's presence. Default-OFF
    /// (<see cref="BeaconConfig.Enabled"/> defaults <c>false</c>): a node that never
    /// beaconed keeps not beaconing. A port may override it with
    /// <see cref="PortConfig.Beacon"/>. See <see cref="BeaconConfig"/>.</summary>
    public BeaconConfig Beacon { get; init; } = new();

    /// <summary>The RHPv2 server (the app platform's <b>network plane</b>): a JSON-over-TCP
    /// host API (PWP-0222/0245, XRouter-compatible) that lets an external application open and
    /// accept packet connections through this node's AX.25 engine. Default-off; binds loopback.
    /// See <c>docs/rhp2-server.md</c>.</summary>
    public RhpConfig Rhp { get; init; } = new();

    /// <summary>The MCP server (Phase 8): exposes the node's read / diagnostic /
    /// network-exploration tools — and operate-gated write tools — to MCP clients
    /// (Claude Code, etc.). Default-off. The stdio transport is the <c>pdn mcp</c>
    /// subcommand (a separate process, no config here); the in-process
    /// SSE/Streamable-HTTP transport piggybacks the web listener at
    /// <see cref="McpSseConfig.Path"/> (like RHPv2-WS at <c>/rhp</c>) and is gated
    /// <c>read</c> by the auth layer. See <c>docs/mcp-design.md</c>.</summary>
    public McpConfig Mcp { get; init; } = new();

    /// <summary>Registered node applications — the app-extensibility platform. Each entry
    /// is launched (out-of-process) when a connected user types its <see cref="ApplicationConfig.Command"/>
    /// verb at the node prompt; the session is bridged to the app over the
    /// <c>pdn-app/1</c> stdio wire (<c>docs/app-local-session-wire.md</c>). Default-empty: a
    /// node with no entries has no apps and behaves exactly as before. Read live at launch
    /// time — because each connect spawns a fresh process, a config edit is picked up by the
    /// next launch with no reconcile/restart machinery. See <c>docs/app-extensibility.md</c>.</summary>
    public IReadOnlyList<ApplicationConfig> Applications { get; init; } = [];

    /// <summary>The owner's app-package state: which <b>discovered</b> packages
    /// (<c>pdn-app.yaml</c> under the package roots — see <c>docs/app-packages.md</c>) are
    /// enabled, plus small overrides. Discovered packages default to DISABLED — an entry here
    /// is the owner's explicit trust grant. Distinct from <see cref="Applications"/>, which
    /// remains the owner-authored inline registry.</summary>
    public IReadOnlyList<AppOverrideConfig> Apps { get; init; } = [];

    /// <summary>Override of the package discovery roots (dev/test). Null = the standard
    /// roots (<c>/usr/share/packetnet/apps</c>, then <c>/var/lib/packetnet/apps</c> — later
    /// wins on id collision). When set, replaces the defaults entirely.</summary>
    public IReadOnlyList<string>? AppPackageRoots { get; init; }

    /// <summary>The persistent traffic log: every AX.25 frame the node sends or
    /// receives, on every port, written to a <b>separate</b> SQLite database for
    /// off-air troubleshooting. Default-ON. See <see cref="TrafficConfig"/>.</summary>
    public TrafficConfig Traffic { get; init; } = new();

    /// <summary>The embedded Tailscale node (the <c>tsnet</c> Go sidecar) — the blessed
    /// remote + passkey path. Default-OFF (<see cref="TailscaleConfig.Enabled"/> =
    /// <c>false</c>): pdn stays HTTP-only until the operator opts in. When enabled, a
    /// later slice (S2) launches the sidecar, which joins the operator's tailnet, gets a
    /// real Let's Encrypt cert for <c>pdn.&lt;tailnet&gt;.ts.net</c>, terminates TLS and
    /// reverse-proxies to pdn's loopback HTTP — so passkeys work remotely with no public
    /// DNS, port-forward, or cert management. <b>S1 only parses + validates this block;
    /// nothing reads it yet.</b> See <c>docs/network-access.md</c>.</summary>
    public TailscaleConfig Tailscale { get; init; } = new();

    /// <summary>Reporting to the OARC packet-network map (the community telemetry collector at
    /// <c>node-api.packet.oarc.uk</c>) — outbound only, so this node appears on the map alongside
    /// the BPQ/XRouter estate. <b>Default-OFF</b> (<see cref="OarcConfig.Enabled"/> = <c>false</c>):
    /// nothing is sent until the operator opts in, and each telemetry category is an independent
    /// toggle. See <see cref="OarcConfig"/> and <c>docs/oarc-reporting-design.md</c>.</summary>
    public OarcConfig Oarc { get; init; } = new();

    /// <summary>kissproxy-compatible MQTT frame emission — the node publishes every AX.25 frame it
    /// sends/receives to an MQTT broker in kissproxy's native topic/payload format, so pdn can replace
    /// a kissproxy instance at a site without losing the downstream <c>kiss-collector</c> capture.
    /// <b>Default-OFF</b> (<see cref="MqttConfig.Enabled"/> = <c>false</c>): a stock node publishes
    /// nothing. See <see cref="MqttConfig"/> and <c>docs/research/pdn-mqtt-frame-emission.md</c>.</summary>
    public MqttConfig Mqtt { get; init; } = new();

    /// <summary>The <b>POCSAG paging</b> service — a TCP line server transmitting/receiving pages
    /// over a dedicated soundmodem audio device. <b>Default-OFF</b>
    /// (<see cref="PagingConfig.Enabled"/> = <c>false</c>). See <see cref="PagingConfig"/>.</summary>
    public PagingConfig Paging { get; init; } = new();

    /// <summary>The <b>ARDOP virtual TNC</b> service — an ardopcf-compatible TCP host interface over
    /// a dedicated soundmodem audio device, letting external ARDOP hosts (BPQ, Pat, Winlink) drive
    /// this node as an ARDOP modem. <b>Default-OFF</b> (<see cref="ArdopConfig.Enabled"/> =
    /// <c>false</c>). See <see cref="ArdopConfig"/>.</summary>
    public ArdopConfig Ardop { get; init; } = new();

    /// <summary>The split-station <b>RF head-ends</b> this node talks to — boxes running the Go
    /// head-end daemon that bridge their serial radios/modems as raw TCP pipes (see
    /// <c>docs/research/split-station-rf-headend.md</c>). A port's <c>radio:</c> control channel or its
    /// <c>nino-tnc-tcp</c> transport binds to a device on one of these by <c>(headEndId, deviceId)</c>.
    /// Default-empty: a node with no head-ends is a purely-local station, exactly as before. Manual
    /// addresses in Stage 3a (mDNS discovery of the fleet lands in Stage 3b). See
    /// <see cref="HeadEndConfig"/>.</summary>
    public IReadOnlyList<HeadEndConfig> HeadEnds { get; init; } = [];
}

/// <summary>
/// Station identity. <see cref="Callsign"/> is held as a <see cref="string"/>
/// deliberately: <c>Packet.Core.Callsign</c> is a <c>readonly struct</c> that
/// will not bind cleanly as a nested config object, so the raw text is carried
/// here and parsed (via <c>Callsign.TryParse</c>) in validation.
/// </summary>
public sealed record Identity
{
    /// <summary>The node's callsign as text (e.g. <c>"M0LTE-1"</c>). Parsed +
    /// validated by <see cref="NodeConfigValidator"/>; never bound as a struct.</summary>
    public required string Callsign { get; init; }

    /// <summary>The node's alias / mnemonic — the single node-name concept (the BPQ NODEALIAS
    /// model). Optional; when set it must be ≤6 uppercase alphanumerics (the NET/ROM wire field is
    /// 6 octets). Used both for display (the OARC map name, the <c>{node}</c> banner/prompt token,
    /// the console name, <c>PDN_NODE_ALIAS</c>) AND as the alias advertised in the node's NODES
    /// broadcast. Null = fall back to the callsign for display, and the callsign base on the wire.
    /// Long friendly text belongs in <see cref="ServicesConfig.Banner"/>, not here.</summary>
    public string? Alias { get; init; }

    /// <summary>Optional Maidenhead grid locator (e.g. <c>"IO91wm"</c>). Free-form here; the OARC
    /// reporter validates it as a 6-char grid before reporting (a node needs a valid grid to appear
    /// on the map).</summary>
    public string? Grid { get; init; }
}
