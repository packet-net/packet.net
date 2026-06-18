using Packet.NetRom;
using Packet.NetRom.Wire;
using YamlDotNet.Serialization;
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
    public const int CurrentSchemaVersion = 1;

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
}

/// <summary>
/// Reporting to the OARC packet-network map (<c>docs/oarc-reporting-design.md</c>). The node pushes
/// its telemetry — node up/status/down, L2 links, L4 circuits, and (opt-in) per-frame L2 traces —
/// to the OARC collector's typed ingest endpoints over HTTPS, so a pdn station shows on the map.
/// <b>Outbound only</b> (no consumption in v1) and <b>default-OFF</b>: a stock node reports nothing.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Enabled"/> is the master switch. The three aggregate categories
/// (<see cref="ReportNodeStatus"/>/<see cref="ReportLinks"/>/<see cref="ReportCircuits"/>) default
/// ON so enabling the master "just works"; the high-volume, most-revealing per-frame trace feed
/// (<see cref="ReportTraces"/>) defaults OFF and is opt-in, RF-only by default
/// (<see cref="TracesRfOnly"/>). Position is the locator only unless
/// <see cref="PublishExactPosition"/> is set.
/// </para>
/// <para>
/// <b>Locator is a hard precondition.</b> The collector requires a valid 6-char Maidenhead locator
/// on node-up/node-status (<c>^[A-R]{2}\d{2}[A-Xa-x]{2}$</c>). The node's
/// <see cref="Identity.Grid"/> is free-form, so the reporter validates it and will not report node
/// events without a valid locator (the UI flags this). The auth model is <b>open</b> — the collector
/// requires no credential — so there is no secret in this block.
/// </para>
/// <para>
/// <see cref="Enabled"/> and the category toggles are hot-reload aware (a master flip sends
/// node-up/node-down at the boundary); the intervals are re-read on the live config each cycle.
/// </para>
/// </remarks>
public sealed record OarcConfig
{
    /// <summary>The master switch. Default <c>false</c> — a node joins the OARC map only when the
    /// operator opts in; with it off the reporter is dormant and sends nothing.</summary>
    public bool Enabled { get; init; }

    /// <summary>The collector base URL. Default the OARC production collector. Overridable for a
    /// staging collector or a local test double; must be an absolute http(s) URL.</summary>
    public string BaseUrl { get; init; } = "https://node-api.packet.oarc.uk/";

    /// <summary>Report node up/status/down (identity, locator, software/version, link &amp; circuit
    /// counts, L3-relayed). Default <c>true</c> — the baseline "this node is on the map" report.</summary>
    public bool ReportNodeStatus { get; init; } = true;

    /// <summary>Report L2 link lifecycle + status (link-up/-status/-down: frames, bytes, throughput,
    /// RTT). Default <c>true</c>.</summary>
    public bool ReportLinks { get; init; } = true;

    /// <summary>Report L4 NET/ROM circuit lifecycle + status (circuit-up/-status/-down: segment
    /// stats). Default <c>true</c>.</summary>
    public bool ReportCircuits { get; init; } = true;

    /// <summary>Report the per-frame L2 trace feed (the wire-monitor firehose — every frame's
    /// src/dest/ctrl/seq/len). Default <c>false</c> — the highest-volume, most-revealing category,
    /// opt-in only.</summary>
    public bool ReportTraces { get; init; }

    /// <summary>When <see cref="ReportTraces"/> is on, report only over-air (RF) frames and skip
    /// internal/loopback/inter-process traffic. Default <c>true</c> — the firehose is rarely wanted
    /// unfiltered.</summary>
    public bool TracesRfOnly { get; init; } = true;

    /// <summary>Publish exact latitude/longitude alongside the locator. Default <c>false</c> — the
    /// node reports its Maidenhead locator (grid-square resolution) only, unless the operator opts
    /// in to precise coordinates.</summary>
    public bool PublishExactPosition { get; init; }

    /// <summary>Seconds between periodic node-status heartbeats. Default 300 (5 min). Must be &gt; 0.</summary>
    public int StatusIntervalSecs { get; init; } = 300;

    /// <summary>Seconds between link-status / circuit-status refreshes for each active session.
    /// Default 60. Must be &gt; 0.</summary>
    public int SessionStatusIntervalSecs { get; init; } = 60;
}

/// <summary>
/// The persistent traffic-log configuration. The log rides the node's existing
/// frame-trace telemetry (the same tap the web monitor's SSE feed consumes — no
/// second decode path) and persists one row per traced frame to its own SQLite
/// file, deliberately SEPARATE from <c>pdn.db</c> so a huge or corrupt frame log
/// can never threaten node state. Default-<b>ON</b>: the whole point is having
/// durable frame history already on disk when a fault needs diagnosing.
/// </summary>
/// <remarks>
/// <see cref="Enabled"/> and <see cref="Path"/> are applied at startup
/// (restart-applies — the store + writer are constructed in the composition
/// root); <see cref="RetentionDays"/> and <see cref="MaxMb"/> are re-read from
/// the live config at every prune pass, so tightening the bounds is a hot edit.
/// </remarks>
public sealed record TrafficConfig
{
    /// <summary>Whether frames are persisted at all. Default <c>true</c> — the
    /// log exists for troubleshooting and is bounded, so it is on by default.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The SQLite file the log is written to. Null (the default) =
    /// <c>traffic.db</c> in the same directory as <c>pdn.db</c> (the writable
    /// StateDirectory on the packaged node). Never the node-state db.</summary>
    public string? Path { get; init; }

    /// <summary>Rows older than this many days are pruned. Default 14.</summary>
    public int RetentionDays { get; init; } = 14;

    /// <summary>Hard cap on the database file size in megabytes — the oldest
    /// rows are pruned until the file fits. Default 512.</summary>
    public int MaxMb { get; init; } = 512;
}

/// <summary>
/// The owner's per-package state for a discovered app package: the enable switch (the trust
/// grant) and small overrides merged over the package's own manifest. See
/// <c>docs/app-packages.md</c> § Owner state.
/// </summary>
public sealed record AppOverrideConfig
{
    /// <summary>The package id this entry applies to (matches the manifest / directory name).
    /// An entry whose id matches no discovered package is a warning, not an error.</summary>
    public required string Id { get; init; }

    /// <summary>The trust switch. Default <c>false</c> — a discovered package never runs,
    /// resolves a verb, or shows a tile until the owner flips this.</summary>
    public bool Enabled { get; init; }

    /// <summary>Optional override of the manifest's <c>packet.command</c> node-prompt verb
    /// (<c>docs/app-packages.md</c> § Application packet identity). Null = use the manifest's
    /// own <see cref="AppPacketSpec.Command"/>. This replaces the old <c>match</c> field — the
    /// verb for a session app is now <see cref="AppPacketSpec.Command"/>, owner-overridable here.</summary>
    public string? Command { get; init; }

    /// <summary>Optional pinned callsign for this app — the node's choice, the on-air L2 identity
    /// stations dial directly (<c>docs/app-packages.md</c> § Application packet identity). A full
    /// callsign (e.g. <c>M9YYY-1</c>) or a bare <c>-N</c> SSID appended to the node base. Null =
    /// the node auto-assigns <c>&lt;node-base&gt;-&lt;lowest free SSID&gt;</c>. Injected as
    /// <c>PDN_APP_CALLSIGN</c>.</summary>
    public string? Callsign { get; init; }

    /// <summary>Optional opt-in NET/ROM advertisement for this app (<c>docs/app-packages.md</c>
    /// § Application packet identity). Present (with an alias) ⇒ the node advertises this app's
    /// alias → its resolved callsign in its NODES broadcast; absent ⇒ nothing extra on the mesh
    /// (the anti-noise default).</summary>
    public AppNetromConfig? Netrom { get; init; }

    /// <summary>Owner environment for the package's service, merged OVER the manifest's
    /// <c>environment</c> map (owner wins).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// The owner's opt-in NET/ROM advertisement for one app (<c>docs/app-packages.md</c>
/// § Application packet identity). When present with an <see cref="Alias"/>, the node advertises
/// that alias → the app's resolved callsign in its NODES broadcast with <see cref="Quality"/>.
/// The alias + quality are the <b>node's</b> (they encode this node's location), so they live in
/// the owner's file beside <c>enabled</c>, never in the portable app manifest.
/// </summary>
public sealed record AppNetromConfig
{
    /// <summary>The network-wide NET/ROM alias users <c>C</c> to (e.g. <c>RDGBBS</c>). Null /
    /// blank ⇒ nothing is advertised for this app (off by default).</summary>
    public string? Alias { get; init; }

    /// <summary>The quality (0..255) to advertise the alias at. Null ⇒ a sensible default
    /// (<see cref="DefaultQuality"/>).</summary>
    public int? Quality { get; init; }

    /// <summary>The default advertised quality when <see cref="Quality"/> is unset — high (an
    /// app on this node is one hop away, directly reachable), matching the BPQ
    /// <c>APPLICATION ...,Quality</c> convention for a local application.</summary>
    public const int DefaultQuality = 255;
}

/// <summary>
/// The RHPv2 server's configuration. Default-off and loopback-bound: a node that doesn't opt
/// in serves no RHP. <see cref="RequireAuth"/> gates the wire's plaintext <c>auth</c> message
/// against the node's existing user store — appropriate for a loopback/LAN bind; RHP has no
/// TLS, so never expose the port beyond a trusted network.
/// </summary>
public sealed record RhpConfig
{
    /// <summary>Whether the RHPv2 listener runs. Default <c>false</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>Bind address. Default loopback — the trust boundary (cf. the app gateway).</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port. 9000 is the RHPv2 convention (PWP-0222).</summary>
    public int Port { get; init; } = 9000;

    /// <summary>When true, a client must <c>auth</c> (validated against the node's users)
    /// before any other request is honoured. Default <c>false</c> (loopback trust).</summary>
    public bool RequireAuth { get; init; }

    /// <summary>Maximum concurrent client TCP connections; the overflow is closed on accept.
    /// Bounds connection-exhaustion. Default 64.</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>Maximum live handles a single client connection may hold; a request past it is
    /// refused with errCode 4. Bounds per-client memory growth. Default 256.</summary>
    public int MaxHandlesPerClient { get; init; } = 256;

    /// <summary>Seconds a peer may take to finish a frame once it has started one (idle between
    /// frames is unbounded). A stalled peer (slowloris) is dropped after this. Default 30;
    /// 0 disables the bound.</summary>
    public int InFrameTimeoutSeconds { get; init; } = 30;
}

/// <summary>
/// The MCP server config. <see cref="Enabled"/> is the master switch (registers the
/// MCP tool surface in DI); <see cref="Sse"/> additionally mounts the in-process
/// HTTP transport. The <c>pdn mcp</c> stdio subcommand is independent of this — it
/// runs in its own process and bridges to the node's loopback REST API. See
/// <c>docs/mcp-design.md</c>.
/// </summary>
public sealed record McpConfig
{
    /// <summary>Whether the MCP tool surface is registered at all. Default <c>false</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>The SSE/Streamable-HTTP transport (piggybacks the web listener).</summary>
    public McpSseConfig Sse { get; init; } = new();

    /// <summary>Lifetime (days) of a minted MCP bearer token — the durable credential a
    /// Claude Code config holds in its <c>Authorization</c> header (login JWTs are too
    /// short-lived for a static header). Default 90. Only relevant when auth is enabled;
    /// the token is admin-gated to mint, scoped (defaulting to <c>read</c>), and audited.</summary>
    public int TokenLifetimeDays { get; init; } = 90;

    /// <summary>The OAuth 2.1 authorization-server endpoints for the hosted claude.ai
    /// connector. Default-off: dormant until an operator opts in. See <see cref="McpOauthConfig"/>
    /// and <c>docs/mcp-oauth-design.md</c>.</summary>
    public McpOauthConfig Oauth { get; init; } = new();
}

/// <summary>
/// The MCP OAuth 2.1 authorization-server config (the hosted claude.ai connector path).
/// <b>Default-off and security-critical</b> — when <see cref="Enabled"/>, the node exposes
/// discovery + dynamic client registration + an interactive authorize/consent + token
/// endpoints (all on the existing web listener). Review before enabling in production
/// (cf. the WebAuthn review). See <c>docs/mcp-oauth-design.md</c>.
/// </summary>
public sealed record McpOauthConfig
{
    /// <summary>Whether the OAuth endpoints are mapped at all. Default <c>false</c> — when off,
    /// the discovery/register/authorize/token/revoke routes return 404 and nothing is exposed.</summary>
    public bool Enabled { get; init; }

    /// <summary>Lifetime (minutes) of an OAuth-issued MCP access token. Default 60 — short,
    /// because the connector re-runs the authorize flow when it expires (no refresh token in
    /// this cut; refresh is a documented follow-up).</summary>
    public int AccessTokenLifetimeMinutes { get; init; } = 60;
}

/// <summary>
/// The in-process MCP HTTP transport. It is served on the node's existing web
/// listener (so it inherits TLS + the auth gateway), not a separate socket — the
/// §6 "8051" plan note is superseded by this piggyback pattern, matching RHPv2-WS
/// at <c>/rhp</c>. Gated <c>read</c> by the auth layer (pass-through when auth is off).
/// </summary>
public sealed record McpSseConfig
{
    /// <summary>Whether to mount the HTTP transport. Default <c>false</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>Path the transport is mounted at on the web listener. Default <c>/mcp</c>.</summary>
    public string Path { get; init; } = "/mcp";
}

/// <summary>How a registered application is run. <see cref="Process"/> is the spawn-per-connect
/// floor; <see cref="Socket"/> is the long-running-daemon rung (shared in-memory state across
/// users); a future in-WASM tier is a later addition to this closed set.</summary>
public enum ApplicationKind
{
    /// <summary>An external process spawned per connect, the session piped over its stdio
    /// per the <c>pdn-app/1</c> wire. Any language. No shared state across users.</summary>
    Process,

    /// <summary>A long-running daemon listening on a Unix-domain socket; the node opens a fresh
    /// connection per connect and bridges the session over it (same <c>pdn-app/1</c> wire). Lets
    /// the app hold shared in-memory state across users + push unsolicited output. The owner runs
    /// the daemon; the node only connects (it does not manage its lifecycle).</summary>
    Socket,
}

/// <summary>
/// One registered node application — the inline, owner-authored analog of a BPQ
/// <c>APPLICATION n,CMD,Call,Alias,Quality</c> line. <see cref="Id"/> is the stable identity
/// (log / reconcile key); <see cref="Command"/> is the console verb that launches it. Out-of-process
/// by design — the node never links app code (see <c>docs/app-extensibility.md</c>). The
/// packet-identity fields (<see cref="Command"/> verb, <see cref="Callsign"/>, <see cref="Netrom"/>)
/// mirror the discovered-package <see cref="AppOverrideConfig"/> (<c>docs/app-packages.md</c>
/// § Application packet identity).
/// </summary>
public sealed record ApplicationConfig
{
    /// <summary>Stable, operator-chosen identifier (e.g. <c>"myapp"</c>). Must be unique
    /// within <see cref="NodeConfig.Applications"/>; surfaced to the app in its connect
    /// header and used in logs.</summary>
    public required string Id { get; init; }

    /// <summary>The console verb a connected user types to launch this app (e.g. <c>"MYAPP"</c>).
    /// Matched case-insensitively, exact (no prefix abbreviation), and only after the built-in
    /// console verbs — so an app can never shadow <c>BYE</c>/<c>CONNECT</c>/etc. Must be unique
    /// within <see cref="NodeConfig.Applications"/> and must not collide with a built-in verb.
    /// This is the <i>verb</i>; the <i>executable</i> is <see cref="Executable"/>.</summary>
    public required string Command { get; init; }

    /// <summary>Whether this app is launchable. A disabled entry is retained in config but
    /// never spawned (its verb falls through to "unknown command"). Default <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How to run the app. Default <see cref="ApplicationKind.Process"/>.</summary>
    public ApplicationKind Kind { get; init; } = ApplicationKind.Process;

    /// <summary>The executable to spawn (<see cref="ApplicationKind.Process"/>) — e.g.
    /// <c>/usr/bin/python3</c>. Required for a process app. Distinct from <see cref="Command"/>
    /// (the node-prompt verb).</summary>
    public string? Executable { get; init; }

    /// <summary>The Unix-domain socket the daemon listens on (<see cref="ApplicationKind.Socket"/>)
    /// — e.g. <c>/run/packetnet/lobby.sock</c>. The node connects here per session. Required for a
    /// socket app.</summary>
    public string? SocketPath { get; init; }

    /// <summary>Arguments passed to <see cref="Executable"/> (e.g. the script path). Each element
    /// is one argument, passed without shell interpretation.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>Working directory for the spawned process (e.g. where the app keeps its state
    /// file). Null = inherit the node's working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>The capabilities the owner grants this app, declared in config (the owner-owns-trust
    /// model). In slice 1 only <c>session</c> is meaningful (the local session is always handed
    /// over); <c>network</c>/<c>config</c>/<c>storage</c> are mediated by later slices. Free-form
    /// for forward-compatibility.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>The optional human-plane web-UI manifest. When present, the app appears in the
    /// control panel's Apps launcher and pdn reverse-proxies <c>/apps/{id}/*</c> to
    /// <see cref="AppUiConfig.Upstream"/>, injecting the authenticated identity. Absent = a
    /// packet-plane-only app (no launcher tile, no proxy). See <c>docs/app-gateway.md</c>.</summary>
    public AppUiConfig? Ui { get; init; }

    /// <summary>Optional pinned callsign — the node's choice, the on-air L2 identity this inline
    /// app binds (<c>docs/app-packages.md</c> § Application packet identity). A full callsign or
    /// a bare <c>-N</c> SSID appended to the node base. Null = the node auto-assigns
    /// <c>&lt;node-base&gt;-&lt;lowest free SSID&gt;</c>. Injected as <c>PDN_APP_CALLSIGN</c>.</summary>
    public string? Callsign { get; init; }

    /// <summary>Optional opt-in NET/ROM advertisement (alias → resolved callsign, with quality).
    /// Absent ⇒ nothing extra advertised. See <see cref="AppNetromConfig"/>.</summary>
    public AppNetromConfig? Netrom { get; init; }
}

/// <summary>
/// How the control panel opens an app's web UI from its left-nav entry — the <c>ui.mode</c>
/// contract (<c>docs/app-packages.md</c> § UI surface modes). The pdn-bbs side is built to this
/// exact set; unknown/missing → <see cref="Standalone"/>.
/// </summary>
public enum AppUiMode
{
    /// <summary>The nav entry is a full browser navigation to the app's own page at
    /// <c>/apps/{id}/</c> (the historical behaviour). The default.</summary>
    Standalone,

    /// <summary>The nav entry is an in-panel SPA route (<c>/apps/:id</c>) that renders the panel
    /// shell around a borderless iframe of the app's <c>/apps/{id}/</c> page. The app renders its
    /// own full page inside the frame — no signal param is appended.</summary>
    Embedded,

    /// <summary>Like <see cref="Embedded"/>, but the iframe src carries <c>?pdn_embed=1</c> so the
    /// app renders chrome-less and blends into the single PDN chrome.</summary>
    Slot,
}

/// <summary>
/// The human-plane manifest for an application: where its own web server lives and how its
/// launcher tile reads. pdn reverse-proxies to <see cref="Upstream"/> and never imports the
/// app — it is a broker (see <c>docs/app-gateway.md</c>).
/// </summary>
public sealed record AppUiConfig
{
    /// <summary>The app's own web server base URL — <b>loopback</b> (e.g.
    /// <c>http://127.0.0.1:9090</c>). pdn reverse-proxies <c>/apps/{id}/*</c> here, stripping
    /// the prefix. Required when a <c>ui</c> block is present; must be an absolute http(s) URL.</summary>
    public required string Upstream { get; init; }

    /// <summary>The launcher tile label. Null = the app's <see cref="ApplicationConfig.Id"/>.</summary>
    public string? Name { get; init; }

    /// <summary>An optional lucide icon name for the launcher tile (purely cosmetic).</summary>
    public string? Icon { get; init; }

    /// <summary>How the panel opens this app from its nav entry. Default
    /// <see cref="AppUiMode.Standalone"/> (a full navigation, the historical behaviour);
    /// <see cref="AppUiMode.Embedded"/> / <see cref="AppUiMode.Slot"/> render it in an in-panel
    /// iframe. Unknown/missing values bind to <see cref="AppUiMode.Standalone"/>.</summary>
    public AppUiMode Mode { get; init; } = AppUiMode.Standalone;
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

    /// <summary>Optional human-facing alias / node name (e.g. <c>"LONDON"</c>).</summary>
    public string? Alias { get; init; }

    /// <summary>Optional Maidenhead grid locator (e.g. <c>"IO91wm"</c>). Free-form
    /// in slice 1 — not validated as a grid square yet.</summary>
    public string? Grid { get; init; }
}

/// <summary>
/// One AX.25 port — a single radio channel reached through one KISS transport,
/// hosting exactly one <c>Ax25Listener</c>.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the <b>stable reconcile key</b>: the hot-reload delta
/// matches old and new ports by <see cref="Id"/> to decide added / removed /
/// changed. Renaming a port's <see cref="Id"/> therefore reads as "remove the
/// old, add the new" — a full restart of that port. Keep it stable across edits.
/// </remarks>
public sealed record PortConfig
{
    /// <summary>Stable, operator-chosen identifier for this port (the reconcile
    /// key — see the type remarks). Must be unique within
    /// <see cref="NodeConfig.Ports"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Whether the port should be brought up. A disabled port is
    /// retained in config but torn down at runtime.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How to reach the modem for this port (serial KISS, NinoTNC,
    /// KISS-over-TCP, or AXUDP). A discriminated union keyed by its <c>kind</c>.</summary>
    public required TransportConfig Transport { get; init; }

    /// <summary>
    /// Optional named channel-tuning profile (e.g. <c>slow-afsk1200</c>). Opt-in:
    /// it fills only the AX.25 / KISS fields the operator left unset on this port —
    /// an explicit value always wins — and absence means exact spec defaults. See
    /// <see cref="ChannelProfiles"/> for what each profile sets and why it is a
    /// named per-port choice rather than a silent node-wide default.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>AX.25 listener parameters for this port (timers, window, retries).
    /// Null = spec defaults (or the <see cref="Profile"/>'s value, if a profile is
    /// set and the field is unset here).</summary>
    public Ax25PortParams? Ax25 { get; init; }

    /// <summary>KISS modem tuning (TXDELAY, persistence, slot time) applied live
    /// once the port is up. Null = leave the modem at its power-on defaults.</summary>
    public KissParams? Kiss { get; init; }

    /// <summary>
    /// Optional AX.25 compatibility profile for this port: which wire frames it
    /// accepts (an <c>Ax25ParseOptions</c> preset — <c>strict</c> / <c>lenient</c> /
    /// <c>bpq</c> / <c>xrouter</c> / <c>direwolf</c> — plus per-flag overrides) and
    /// which SDL session quirks new sessions run with. Null = lenient parsing +
    /// default (spec-correct) quirks, the node's historical behaviour. Applied
    /// live on edit: parsing changes take effect on the next inbound frame, quirks
    /// on the next-built session. See <see cref="PortCompatConfig"/>.
    /// </summary>
    public PortCompatConfig? Compat { get; init; }

    /// <summary>
    /// Optional per-port ID-beacon override. Null = inherit the system default
    /// (<see cref="NodeConfig.Beacon"/>) wholesale. When present, its
    /// <see cref="PortBeaconConfig.Enabled"/> flag wins outright, and its nullable
    /// <see cref="PortBeaconConfig.IntervalMinutes"/> / <see cref="PortBeaconConfig.Text"/>
    /// fields fill in from the system default when left null (a per-field merge —
    /// see <see cref="EffectiveBeacon"/>).
    /// </summary>
    public PortBeaconConfig? Beacon { get; init; }

    /// <summary>
    /// Optional per-port NET/ROM <b>route quality</b> (0..255) — the quality assumed
    /// for a directly-heard neighbour on this port, and the quality basis the routes
    /// it advertises are combined against. Overrides the node-wide
    /// <see cref="NetRomConfig.DefaultNeighbourQuality"/> for routes learned on this
    /// port. Null (the default) ⇒ inherit the global value (then the canonical 192).
    /// This is the BPQ per-port <c>QUALITY</c> knob: a mixed-grade node advertises an
    /// accurate quality on each port (e.g. 191 on one link, 192 on another) instead of
    /// one uniform value, so neighbours pick routes correctly. See
    /// <see cref="EffectiveNetRomQuality"/> for the resolution chain.
    /// </summary>
    public int? NetRomQuality { get; init; }

    /// <summary>
    /// Resolve this port's effective NET/ROM neighbour quality: the explicit per-port
    /// <see cref="NetRomQuality"/> if set, else the node-wide
    /// <paramref name="globalDefault"/> (<see cref="NetRomConfig.DefaultNeighbourQuality"/>),
    /// else the canonical default (192 — see <c>NetRomRoutingOptions.DefaultNeighbourQuality</c>).
    /// The returned value is clamped to 0..255 defensively (validation already rejects
    /// out-of-range, but a clamp keeps the routing math total).
    /// </summary>
    public int EffectiveNetRomQuality(int? globalDefault)
        => Math.Clamp(NetRomQuality ?? globalDefault ?? 192, 0, 255);
}

/// <summary>
/// Per-port AX.25 listener tuning. Each value is optional; an unset value means
/// "use the engine's spec default". These map onto <c>Ax25ListenerOptions</c>.
/// </summary>
/// <remarks>
/// Changing any of these is a <b>new-sessions-only</b> change — the reconcile
/// rebuilds the listener's option seed so future sessions pick up the new
/// values, but it never reaches into a live session's negotiated context.
/// </remarks>
public sealed record Ax25PortParams
{
    /// <summary>T1 acknowledgement timer seed, milliseconds. Null = engine default.</summary>
    public int? T1Ms { get; init; }

    /// <summary>T2 acknowledge-delay timer (§6.7.1.2), milliseconds. Received
    /// in-sequence I-frames coalesce into one cumulative RR sent T2 after the
    /// first unacknowledged frame. 0 = no delay (ack per frame). Null = engine
    /// default (3000).</summary>
    public int? T2Ms { get; init; }

    /// <summary>T3 inactive-link timer, milliseconds. Null = engine default.</summary>
    public int? T3Ms { get; init; }

    /// <summary>N2 maximum retries before giving up. Null = engine default (10).</summary>
    public int? N2 { get; init; }

    /// <summary>Send-window size k. Null = engine default (4 for mod-8).</summary>
    public int? WindowSize { get; init; }

    /// <summary>
    /// Maximum information-field length in octets — N1 / PACLEN. This is the per-port
    /// frame-length limit: the node segments an outbound SDU into I-frames of at most
    /// this many info octets, and an inbound frame larger than this is not accepted into
    /// a session. Null = engine default (256, the AX.25 v2.2 default + the XID-offered N1).
    /// A heterogeneous node sets a small value on a slow/lossy medium — e.g. ~80 on a
    /// shared-HF port — so on-air frame durations stay short and robust, while leaving the
    /// VHF/UHF ports at the 256 default.
    /// </summary>
    /// <remarks>
    /// <b>Precedence vs XID.</b> The configured value is the port's <em>offered N1</em>
    /// (the link's starting cap). XID negotiation can only LOWER it — the negotiator takes
    /// the minimum of the two ends' offered N1 — so a peer offering a smaller N1 still wins,
    /// but the configured cap is never exceeded on the wire. With no per-port value the
    /// session starts at 256 and XID behaves exactly as before. Like the timer/window knobs
    /// this is a new-sessions-only reseed: it seeds <c>Ax25SessionContext.N1</c> at session
    /// build time and never reaches into a live session's negotiated N1.
    /// </remarks>
    public int? N1 { get; init; }

    /// <summary>LRU cap on cached per-peer sessions. Null = engine default (64).</summary>
    public int? MaxCachedPeers { get; init; }
}

/// <summary>
/// KISS modem tuning knobs, all in the units the KISS spec uses. Each is
/// optional. Applied via the <c>IKissModem</c> setters once the port is up, and
/// re-applied live on a hot reconfigure (no port restart).
/// </summary>
public sealed record KissParams
{
    /// <summary>KISS TXDELAY (0x01), in units of 10 ms.</summary>
    public byte? TxDelay { get; init; }

    /// <summary>KISS PERSIST (0x02), 0..255.</summary>
    public byte? Persistence { get; init; }

    /// <summary>KISS SLOTTIME (0x03), in units of 10 ms.</summary>
    public byte? SlotTime { get; init; }

    /// <summary>
    /// KISS TXTAIL (0x04), in units of 10 ms. Unlike the other KISS knobs here this has
    /// an <b>implicit default of 0</b> (not "leave the modem alone"): the node sends an
    /// explicit, deterministic tail to the modem on bring-up, on the regular KISS-param
    /// apply cadence, and on a hot config change — so a port whose <c>txTail</c> is
    /// unset still receives <c>0</c> (see <c>PortSupervisor.ApplyKissParamsToModemAsync</c>).
    /// <para>
    /// 0 is correct for most paths — a NinoTNC into a fully analogue audio path, even on
    /// a slow AFSK1200 channel. A <b>non-zero</b> tail is needed only for a software modem
    /// (samoyed / Dire Wolf) or a NinoTNC driving a radio with a non-zero-latency audio
    /// path: the need is a <em>modem + radio-audio-path-latency</em> property, NOT a
    /// channel/baud one, which the node can't infer — so the operator overrides per port
    /// and that explicit value wins. Set non-null to override the implicit 0.
    /// </para>
    /// </summary>
    public byte? TxTail { get; init; }

    /// <summary>
    /// Whether this port's outbound transmissions are <b>paced</b> over the G8BPQ
    /// ACKMODE extension. Default <c>false</c> — a stock port blasts frames
    /// fire-and-forget exactly as before. When <c>true</c> (and the transport is a
    /// kiss-tcp link to an ACKMODE-honouring TNC / net-sim), the node sends each
    /// frame in ACKMODE and waits for the TNC's TX-completion echo before releasing
    /// the next, serialising its transmissions onto the half-duplex channel instead
    /// of colliding with itself. Unlike the other KISS knobs here this is NOT a live
    /// modem setting — it gates how the modem is constructed (the
    /// <c>PacingKissModem</c> wrapper), so toggling it restarts the port (see
    /// <c>ReconcilePlanner</c>). Only meaningful on a kiss-tcp transport; a no-op on
    /// transports that don't speak ACKMODE.
    /// </summary>
    public bool AckMode { get; init; }

    /// <summary>
    /// TX-complete→T1: when <c>true</c>, the port's AX.25 engine transmits every
    /// T1-arming frame (I-frame, P=1 enquiry, SABM/SABME/DISC) in ACKMODE and
    /// re-arms a still-running T1 to (now + T1V) when the TNC's TX-completion
    /// echo reports the frame has actually cleared the air — so T1 bounds the
    /// peer's response time instead of also absorbing this port's own TX-queue
    /// and airtime (which at 1200 baud makes the default T1 expire mid-window;
    /// see <c>Ax25ListenerOptions.RestartT1OnTxComplete</c> for the full
    /// rationale and the LinkBench measurements). Default <c>false</c>.
    /// Requires an ACKMODE-capable transport (kiss-tcp to net-sim / a real
    /// NinoTNC); on anything else the engine quietly latches back to plain
    /// sends. Composes with (but does not require) <see cref="AckMode"/> pacing.
    /// Like <see cref="AckMode"/> this is a construction-time choice, so a
    /// toggle restarts the port (see <c>ReconcilePlanner</c>).
    /// </summary>
    public bool T1FromTxComplete { get; init; }
}

/// <summary>Operator-facing service strings, hot-swappable by reference.</summary>
public sealed record ServicesConfig
{
    /// <summary>Welcome banner shown on every new console connection (telnet or
    /// over-the-air). <c>{node}</c> and <c>{call}</c> placeholders are expanded.</summary>
    public string Banner { get; init; } = "Welcome to {node} ({call})";

    /// <summary>The command prompt emitted after the banner and after each
    /// command. <c>{call}</c> is expanded.</summary>
    public string Prompt { get; init; } = "{call}> ";
}

/// <summary>
/// The node's system-default ID beacon: a periodic connectionless AX.25 UI frame
/// (an "ID"/presence broadcast) transmitted on each port. <b>Default-OFF</b> —
/// <see cref="Enabled"/> defaults <c>false</c> so a stock node never transmits an
/// unsolicited beacon until the operator opts in (the no-regression contract). A
/// port may override this with <see cref="PortConfig.Beacon"/>.
/// </summary>
public sealed record BeaconConfig
{
    /// <summary>Whether the node beacons on its ports by default. Default
    /// <c>false</c> — a node that has never beaconed must keep not beaconing.</summary>
    public bool Enabled { get; init; }

    /// <summary>Minutes between beacon transmissions on a port. Default 30.</summary>
    public int IntervalMinutes { get; init; } = 30;

    /// <summary>The beacon's information text. <c>{node}</c> (alias else callsign)
    /// and <c>{call}</c> (the station callsign) placeholders are expanded — exactly
    /// like the services banner / prompt. Default <c>"{node} pdn node"</c>.</summary>
    public string Text { get; init; } = "{node} pdn node";
}

/// <summary>
/// A per-port ID-beacon override. <see cref="Enabled"/> always wins outright; the
/// nullable <see cref="IntervalMinutes"/> / <see cref="Text"/> fields inherit the
/// system default (<see cref="BeaconConfig"/>) when left null — a per-field merge.
/// </summary>
public sealed record PortBeaconConfig
{
    /// <summary>Whether this port beacons. This flag is authoritative for the port —
    /// it is not merged: a port-override with <c>Enabled = false</c> silences a port
    /// even when the system default is on, and vice-versa.</summary>
    public bool Enabled { get; init; }

    /// <summary>Minutes between this port's beacons. Null = inherit the system
    /// default's <see cref="BeaconConfig.IntervalMinutes"/>.</summary>
    public int? IntervalMinutes { get; init; }

    /// <summary>This port's beacon text (<c>{node}</c>/<c>{call}</c> expanded). Null =
    /// inherit the system default's <see cref="BeaconConfig.Text"/>.</summary>
    public string? Text { get; init; }
}

/// <summary>
/// The fully-resolved beacon for one port — the per-port override (if any) merged
/// over the system default. This is what the <c>BeaconService</c> arms a timer from.
/// </summary>
/// <param name="Enabled">Whether to beacon on this port at all.</param>
/// <param name="IntervalMinutes">Resolved transmit interval, minutes (≥ 1).</param>
/// <param name="Text">Resolved beacon text, with <c>{node}</c>/<c>{call}</c> still unexpanded.</param>
public readonly record struct EffectiveBeacon(bool Enabled, int IntervalMinutes, string Text)
{
    /// <summary>
    /// Resolve the effective beacon for a port: the per-port <paramref name="port"/>
    /// override merged over the system <paramref name="systemDefault"/>. When the port
    /// has no override the system default applies wholesale; when it has one, its
    /// <see cref="PortBeaconConfig.Enabled"/> wins outright and its null interval/text
    /// fall back to the system default's.
    /// </summary>
    public static EffectiveBeacon Resolve(BeaconConfig systemDefault, PortBeaconConfig? port)
    {
        ArgumentNullException.ThrowIfNull(systemDefault);
        if (port is null)
        {
            return new EffectiveBeacon(systemDefault.Enabled, systemDefault.IntervalMinutes, systemDefault.Text);
        }
        return new EffectiveBeacon(
            port.Enabled,
            port.IntervalMinutes ?? systemDefault.IntervalMinutes,
            port.Text ?? systemDefault.Text);
    }
}

/// <summary>Management-surface configuration: the local telnet console and the
/// (slice-1 present-but-inert) web server.</summary>
public sealed record ManagementConfig
{
    /// <summary>The local dial-in telnet console.</summary>
    public TelnetConfig Telnet { get; init; } = new();

    /// <summary>The web server bind. Slice 1 maps only <c>GET /healthz</c>;
    /// API/auth/UI are later slices.</summary>
    public HttpConfig Http { get; init; } = new();

    /// <summary>Optional HTTPS/TLS listener for the web control panel. Default-OFF
    /// (see <see cref="HttpsConfig"/>): with it off only the plain <see cref="Http"/>
    /// listener runs, exactly as before. With it on, a second Kestrel endpoint serves
    /// the same panel over TLS — encrypting the password + JWT that would otherwise
    /// cross the LAN in clear, and providing the secure context WebAuthn/passkeys
    /// require.</summary>
    public HttpsConfig Https { get; init; } = new();

    /// <summary>Web control-API authentication. Default-OFF (see
    /// <see cref="AuthConfig"/>): with it off the API behaves exactly as it did
    /// before auth existed — the read / SSE / config / ports / sessions / ping
    /// endpoints and the SPA all serve unauthenticated. With it on, a JWT bearer
    /// token is required and the per-endpoint scope gates enforce.</summary>
    public AuthConfig Auth { get; init; } = new();
}

/// <summary>
/// Web control-API authentication configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-OFF, no regression.</b> <see cref="Enabled"/> defaults to
/// <c>false</c>: the auth machinery (user store, JWT issuing/validation, the
/// scope policies) is always wired, but <em>enforcement</em> is conditional on
/// this flag. With it off, every endpoint that would otherwise be gated serves
/// unauthenticated exactly as before — so turning auth on is a deliberate,
/// reviewed step and never a silent behaviour change for an existing node.
/// </para>
/// <para>
/// The signing key and the user records live in <c>pdn.db</c> (the consolidated
/// SQLite store), not here — this config record only carries the on/off switch
/// and the token lifetime. The key is generated on first start and persisted;
/// it is never written to config or logs.
/// </para>
/// </remarks>
public sealed record AuthConfig
{
    /// <summary>Whether the web control API requires authentication. Default
    /// <c>false</c> — the API is unauthenticated until the operator opts in. When
    /// <c>true</c>, a JWT bearer token is required on the gated endpoints and the
    /// <c>read</c>/<c>operate</c>/<c>admin</c> scope policies enforce.</summary>
    public bool Enabled { get; init; }

    /// <summary>Access-token lifetime in minutes. Null = the default (60 — ~1h).
    /// Short-lived: when it expires the web client silently exchanges its refresh
    /// token (see <see cref="RefreshTokenMinutes"/>) for a fresh one rather than
    /// forcing a re-login.</summary>
    public int? AccessTokenMinutes { get; init; }

    /// <summary>Refresh-token lifetime in minutes. Null = the default (10080 — 7
    /// days). This is the real session length: a refresh token rotates on each use
    /// (one-time-use) and lets the client renew its short access token without a
    /// re-login until the refresh token itself expires. Must exceed
    /// <see cref="AccessTokenMinutes"/> when both are set (a refresh token that
    /// outlived its access token is the whole point — see
    /// <see cref="NodeConfigValidator"/>).</summary>
    public int? RefreshTokenMinutes { get; init; }

    /// <summary>WebAuthn / passkey configuration (the relying-party identity + allowed
    /// origins). See <see cref="WebAuthnConfig"/>. The defaults (<c>localhost</c>) make
    /// same-machine passkeys a zero-config feature; an operator on a real domain sets
    /// the RP id + origins here.</summary>
    public WebAuthnConfig WebAuthn { get; init; } = new();

    /// <summary>How long an over-RF <c>SYSOP</c> elevation lasts, in minutes. Null =
    /// the default (15). After a connected operator presents a valid rolling code, their
    /// session is elevated for this long; once it lapses they must re-present a code to
    /// run a privileged command again. Bounds the blast radius of a session left
    /// connected. Must be &gt; 0 when set (see <see cref="NodeConfigValidator"/>).</summary>
    public int? SysopElevationMinutes { get; init; }
}

/// <summary>
/// WebAuthn / passkey relying-party configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>localhost-first, zero-config.</b> The defaults — <see cref="RelyingPartyId"/> =
/// <c>localhost</c>, <see cref="AllowedOrigins"/> empty — make same-machine passkeys
/// work today with no setup: reach the panel on <c>http://localhost:&lt;port&gt;</c>
/// (a secure context with no cert) and the RP id and origin coincide, which is the
/// case <c>docs/passkeys-lan-trust-pattern.md</c> §2 / §4 names as the one to nail
/// first because the origin-vs-RP-id split is the most error-prone part.
/// </para>
/// <para>
/// <b>The expected origin is the SERVING origin, not config.</b> For verification the
/// host passes Fido2 the <em>actual</em> origin the browser used (request scheme + host
/// + port), so a node reached on <c>http://localhost:8080</c> just works. The RP id
/// must be a registrable suffix of that origin; with the <c>localhost</c> default they
/// are identical. <see cref="AllowedOrigins"/> is the explicit allow-list for the
/// real-domain case — when empty the host accepts the request's own origin (plus
/// <c>localhost</c>); when set it pins exactly those origins.
/// </para>
/// <para>
/// <b>Distribution tiers are parked.</b> Per the trust-pattern doc §8 decision gate,
/// the mDNS / ACME / IP-encoded-name machinery is NOT built — only the RP id + origins
/// are made configurable so a real-domain operator (doc §2a) can set them by hand.
/// </para>
/// </remarks>
public sealed record WebAuthnConfig
{
    /// <summary>The WebAuthn Relying Party ID — the registrable domain a passkey is
    /// scoped to. Default <c>localhost</c> (the loopback secure-context exemption). Must
    /// be a registrable suffix of the serving origin's host; an IP literal is NOT a
    /// legal RP id (trust-pattern doc §1).</summary>
    public string RelyingPartyId { get; init; } = "localhost";

    /// <summary>The human-facing Relying Party name shown by the authenticator UI.
    /// Default <c>"pdn node"</c>.</summary>
    public string RelyingPartyName { get; init; } = "pdn node";

    /// <summary>The exact origins the verifier accepts (e.g.
    /// <c>https://pdn.lab.example:8443</c>). <b>Empty (the default) = accept the
    /// request's own serving origin plus <c>localhost</c></b>, which is what makes the
    /// localhost default zero-config. Set this on a real domain to pin the accepted
    /// origin(s) exactly.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];

    // Records compare a collection member by REFERENCE, so two configs with equal-but-
    // distinct AllowedOrigins lists would be unequal — breaking the YAML round-trip
    // identity (serialise→parse yields a fresh list). Compare the list by sequence so
    // equality is value-based, matching every other config record.
    public bool Equals(WebAuthnConfig? other) =>
        other is not null
        && RelyingPartyId == other.RelyingPartyId
        && RelyingPartyName == other.RelyingPartyName
        && AllowedOrigins.SequenceEqual(other.AllowedOrigins);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RelyingPartyId);
        hash.Add(RelyingPartyName);
        foreach (var origin in AllowedOrigins)
        {
            hash.Add(origin);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// The embedded Tailscale node (<c>tsnet</c> Go sidecar) configuration — the blessed
/// remote + passkey path. <b>Default-OFF</b>: pdn serves plain HTTP (loopback + LAN)
/// and stays HTTP-only until the operator sets <see cref="Enabled"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsed + validated, INERT in S1.</b> This block carries the operator's intent;
/// the consuming <c>TailscaleSidecarHostedService</c> arrives in S2. When enabled there,
/// the sidecar joins the operator's tailnet, obtains a real Let's Encrypt cert for
/// <c>pdn.&lt;tailnet&gt;.ts.net</c> via <c>ListenTLS</c>, terminates TLS, and
/// reverse-proxies to <see cref="Target"/> (pdn's loopback HTTP). The browser then sees
/// trusted HTTPS → passkeys work remotely with no public DNS, port-forward, or cert
/// management. See <c>docs/network-access.md</c>.
/// </para>
/// <para>
/// <b>The auth key is sensitive</b> (first-join only). Prefer <see cref="AuthKeyFile"/>
/// (a 0600 file, packetnet-owned) over an inline <see cref="AuthKey"/>; supplying neither
/// leaves first join to interactive login (the sidecar prints a <c>login.tailscale.com</c>
/// URL). After first join the node identity lives in <see cref="StateDir"/>.
/// </para>
/// </remarks>
public sealed record TailscaleConfig
{
    /// <summary>Whether the embedded Tailscale sidecar runs. Default <c>false</c> — pdn
    /// stays HTTP-only until opted in.</summary>
    public bool Enabled { get; init; }

    /// <summary>A tailnet pre-auth key used on first join only. Null (the default) =
    /// none. Prefer <see cref="AuthKeyFile"/> for secrets; this and
    /// <see cref="AuthKeyFile"/> must not both be set (see
    /// <see cref="TailscaleConfigValidator"/>).</summary>
    public string? AuthKey { get; init; }

    /// <summary>Path to a 0600 file holding the tailnet pre-auth key (preferred over an
    /// inline <see cref="AuthKey"/> so the secret never lives in the config text). Null
    /// (the default) = none.</summary>
    public string? AuthKeyFile { get; init; }

    /// <summary>The desired node name → <c>&lt;hostname&gt;.&lt;tailnet&gt;.ts.net</c> (the
    /// actual name is read back from the sidecar). <b>Empty (the default) ⇒ derive
    /// <c>&lt;callsign&gt;-pdn</c></b> (the lowercased base callsign) so multiple nodes on
    /// one tailnet don't collide on a bare <c>pdn</c> — see
    /// <see cref="Tailscale.TailscaleHostname"/>. When set explicitly it must match
    /// <c>^[a-z0-9-]+$</c>.</summary>
    public string Hostname { get; init; } = "";

    /// <summary>Tailnet tags applied to the node (e.g. <c>tag:server</c> — a
    /// tailnet-owned node, right for an always-on box). Default empty.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The PERSISTENT state directory the sidecar uses to rejoin as the same
    /// node (and keep the same cert) across restarts. Load-bearing for a stable hostname
    /// → stable passkeys. Default <c>/var/lib/packetnet/tsnet</c>.</summary>
    public string StateDir { get; init; } = "/var/lib/packetnet/tsnet";

    /// <summary>The loopback HTTP endpoint (<c>host:port</c>) the sidecar reverse-proxies
    /// to — pdn's own HTTP listener. Default <c>127.0.0.1:8080</c>.</summary>
    public string Target { get; init; } = "127.0.0.1:8080";

    /// <summary>Opt-in public exposure via Tailscale Funnel (vs tailnet-only). Default
    /// <c>false</c>; a <c>true</c> value with <see cref="Enabled"/> off is inert (a
    /// validation warning, not an error — see <see cref="TailscaleConfigValidator"/>).</summary>
    public bool Funnel { get; init; }

    // Records compare a collection member by REFERENCE, so two configs with equal-but-
    // distinct Tags lists would be unequal — breaking the YAML round-trip identity
    // (serialise→parse yields a fresh list). Compare the list by sequence so equality is
    // value-based, matching WebAuthnConfig.AllowedOrigins and every other config record.
    public bool Equals(TailscaleConfig? other) =>
        other is not null
        && Enabled == other.Enabled
        && AuthKey == other.AuthKey
        && AuthKeyFile == other.AuthKeyFile
        && Hostname == other.Hostname
        && StateDir == other.StateDir
        && Target == other.Target
        && Funnel == other.Funnel
        && Tags.SequenceEqual(other.Tags);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Enabled);
        hash.Add(AuthKey);
        hash.Add(AuthKeyFile);
        hash.Add(Hostname);
        hash.Add(StateDir);
        hash.Add(Target);
        hash.Add(Funnel);
        foreach (var tag in Tags)
        {
            hash.Add(tag);
        }
        return hash.ToHashCode();
    }
}

/// <summary>
/// The local telnet console listener. Defaults to loopback-only — the console
/// is operator-local dial-in, not a network service.
/// </summary>
public sealed record TelnetConfig
{
    /// <summary>Whether to run the telnet console at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Bind address. Defaults to <c>127.0.0.1</c> — loopback only.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port for the telnet console.</summary>
    public int Port { get; init; } = 8011;
}

/// <summary>The web server bind. Present-but-inert in slice 1.</summary>
public sealed record HttpConfig
{
    /// <summary>Bind address for Kestrel.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port for the web server.</summary>
    public int Port { get; init; } = 8080;
}

/// <summary>
/// Optional HTTPS/TLS listener for the web control panel. <b>Default-OFF</b>
/// (<see cref="Enabled"/> = <c>false</c>) — only the plain HTTP listener runs, so a
/// node that never configured TLS behaves exactly as before. When enabled, a second
/// Kestrel endpoint serves the same panel over TLS.
/// </summary>
/// <remarks>
/// <para>
/// The cert is loaded from <see cref="CertificatePath"/> (a PKCS#12 / .pfx) when set;
/// otherwise, if <see cref="GenerateSelfSignedOnMissing"/> is true, a self-signed cert
/// is generated on first start and persisted alongside the node state so it is stable
/// across restarts. A self-signed cert <em>encrypts the channel</em> (the password +
/// JWT no longer cross the LAN in clear) but is not trusted by browsers — to get a
/// trusted secure context (needed for WebAuthn/passkeys over a LAN IP) point
/// <see cref="CertificatePath"/> at a cert the client trusts, or reach the node via
/// <c>localhost</c>.
/// </para>
/// </remarks>
public sealed record HttpsConfig
{
    /// <summary>Whether the HTTPS listener runs. Default <c>false</c> — HTTP only.</summary>
    public bool Enabled { get; init; }

    /// <summary>Bind address for the HTTPS listener. Defaults to loopback.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port for HTTPS.</summary>
    public int Port { get; init; } = 8443;

    /// <summary>Path to a PKCS#12 (.pfx/.p12) certificate bundle (cert + private key).
    /// Null = use a generated self-signed cert (see
    /// <see cref="GenerateSelfSignedOnMissing"/>).</summary>
    public string? CertificatePath { get; init; }

    /// <summary>Password for the PKCS#12 at <see cref="CertificatePath"/>, if it is
    /// encrypted. Null = no password.</summary>
    public string? CertificatePassword { get; init; }

    /// <summary>When no <see cref="CertificatePath"/> is set, generate a self-signed
    /// cert on first start and persist it (default <c>true</c>). Set false to require an
    /// explicit cert (the HTTPS listener then fails to start without one).</summary>
    public bool GenerateSelfSignedOnMissing { get; init; } = true;
}

/// <summary>
/// The node's NET/ROM routing role — the single 3-state knob that replaces the old
/// pair of <c>connect</c> + <c>forward</c> bools (which had an inert combination:
/// <c>forward</c> did nothing unless <c>connect</c> was also on, because forwarding
/// reuses the connected-mode interlink machinery <c>connect</c> gated). The three
/// states are a clean escalation of how much transit work the node does, orthogonal
/// to <see cref="NetRomConfig.Enabled"/> (hearing) and
/// <see cref="NetRomConfig.Broadcast"/> (advertising). See <see cref="NetRomConfig.Routing"/>.
/// </summary>
/// <remarks>
/// This is a node-host config concept (how this station participates), distinct from
/// the library's <see cref="NetRomForwardMode"/> (how a transit node picks <em>among</em>
/// kept routes). They compose: <see cref="Transit"/> + <see cref="NetRomForwardMode"/>.
/// </remarks>
public enum NetRomRouting
{
    /// <summary>Passive — listen for NODES and maintain the routing table only; no
    /// connected-mode interlinks, no transit. The default (equivalent to the old
    /// <c>connect: false</c>). A stock node hears the network but transmits nothing
    /// on it and opens no circuits.</summary>
    None,

    /// <summary>Open connected-mode interlinks so this node's own
    /// <c>connect &lt;alias&gt;</c> can route an L4 circuit across the network, but do
    /// <b>not</b> relay third-party transit datagrams. An endpoint that participates
    /// (originates + terminates circuits) without carrying others' traffic
    /// (equivalent to the old <c>connect: true, forward: false</c>).</summary>
    Endpoint,

    /// <summary>Full router — interlinks for our own circuits <b>and</b> relay transit
    /// datagrams for other stations (TTL-decremented, hop-by-hop). The network-layer
    /// routing role (equivalent to the old <c>connect: true, forward: true</c>).</summary>
    Transit,
}

/// <summary>
/// NET/ROM configuration. The node always <b>hears</b> NODES routing broadcasts
/// (UI frames to dest <c>NODES</c>, PID 0xCF) via the frame-trace tap, parses
/// them, and builds a routing table surfaced in <c>Nodes</c> / a future MCP tool —
/// the read-only awareness slice. With <see cref="Broadcast"/> on it also
/// <b>originates</b> its own NODES broadcast on the NODESINTERVAL schedule, and the
/// <see cref="Routing"/> mode escalates how much it routes:
/// <see cref="NetRomRouting.Endpoint"/> opens <b>L4 virtual circuits</b> over
/// connected-mode AX.25 interlinks so <c>connect &lt;alias&gt;</c> routes a user to
/// a distant node, and <see cref="NetRomRouting.Transit"/> additionally
/// <b>forwards transit datagrams</b> for other stations — the full network-layer
/// routing role.
/// </summary>
/// <remarks>
/// <para>
/// The knobs are exposed because NET/ROM has no single normative standard — the
/// canonical defaults apply unless the operator overrides, never a silent BPQ-ism.
/// Default <see cref="Enabled"/> is <c>true</c> (hearing is free + harmless), but
/// the TX-bearing escalations (<see cref="Broadcast"/>, <see cref="Routing"/>)
/// default off / <see cref="NetRomRouting.None"/>: a stock node does not transmit on
/// the air or open interlinks until the operator opts in (spec-faithful +
/// safe-by-default).
/// </para>
/// <para>
/// <b>Back-compat.</b> The old <c>connect</c> + <c>forward</c> bools are retained as
/// nullable <b>legacy inputs</b> (<see cref="Connect"/>, <see cref="Forward"/>) that
/// feed <see cref="ResolveRouting"/>; an existing config with those keys keeps parsing
/// and behaving identically. <see cref="Routing"/> wins when explicitly set. See
/// <see cref="ResolveRouting"/> for the mapping + the warnings it surfaces.
/// </para>
/// </remarks>
public sealed record NetRomConfig
{
    /// <summary>Whether to listen for NODES broadcasts and maintain the routing
    /// table. Default <c>true</c> (read-only, harmless). Set <c>false</c> to make
    /// the node deaf to NET/ROM entirely (also disables broadcast + routing).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether to <b>originate</b> our own NODES routing broadcast (and so advertise
    /// our presence + learned routes to neighbours). Default <c>false</c> —
    /// transmitting on the air is opt-in. Requires <see cref="Enabled"/>.
    /// </summary>
    public bool Broadcast { get; init; }

    /// <summary>
    /// The node's routing role (see <see cref="NetRomRouting"/>). The single 3-state
    /// successor to the old <c>connect</c> + <c>forward</c> bools.
    /// <see cref="NetRomRouting.None"/> (the default) is passive — hear + maintain the
    /// table, no interlinks, no transit; <see cref="NetRomRouting.Endpoint"/> opens
    /// interlinks for our own <c>connect &lt;alias&gt;</c> but does not relay transit;
    /// <see cref="NetRomRouting.Transit"/> is the full router (interlinks + relay
    /// transit). Requires <see cref="Enabled"/> when not <see cref="NetRomRouting.None"/>.
    /// </summary>
    /// <remarks>
    /// <b>Bound nullable to distinguish unset from explicit <c>None</c>.</b> The YAML
    /// binder writes <c>null</c> when the <c>routing:</c> key is absent and a concrete
    /// value when it is present — so <see cref="ResolveRouting"/> can tell "operator said
    /// nothing, fall back to the legacy <c>connect</c>/<c>forward</c> keys" apart from
    /// "operator explicitly chose <c>none</c>, ignore the legacy keys". A non-nullable
    /// enum defaulting to <c>None</c> could not make that distinction. Every consumer
    /// reads the resolved value via <see cref="EffectiveRouting"/>, never this raw field.
    /// </remarks>
    public NetRomRouting? Routing { get; init; }

    /// <summary>
    /// <b>Legacy input (deprecated — use <see cref="Routing"/>).</b> Whether
    /// <c>connect &lt;alias&gt;</c> may route across the network via NET/ROM L4
    /// circuits. Retained as a nullable input so existing configs with a <c>connect:</c>
    /// key keep parsing + behaving identically; <c>null</c> = the key was absent. Fed to
    /// <see cref="ResolveRouting"/> only when <see cref="Routing"/> is unset. Never read
    /// directly by a consumer — read <see cref="EffectiveRouting"/>.
    /// </summary>
    public bool? Connect { get; init; }

    /// <summary>
    /// <b>Legacy input (deprecated — use <see cref="Routing"/>).</b> Whether this node
    /// forwarded transit datagrams. Retained as a nullable input so existing configs
    /// with a <c>forward:</c> key keep parsing; <c>null</c> = the key was absent. Note
    /// the old wart this fix removes: <c>forward</c> was inert unless <c>connect</c> was
    /// also on. Fed to <see cref="ResolveRouting"/> only when <see cref="Routing"/> is
    /// unset; see that method for the mapping (including the contradictory
    /// <c>connect:false, forward:true</c> → <see cref="NetRomRouting.None"/> + a warning).
    /// Never read directly by a consumer — read <see cref="EffectiveRouting"/>.
    /// </summary>
    public bool? Forward { get; init; }

    /// <summary>
    /// How a forwarding node picks among multiple kept routes to a destination
    /// (<see cref="NetRomForwardMode"/>). Default
    /// <see cref="NetRomForwardMode.PerFlow"/> — a transit node spreads distinct L4
    /// circuits across the kept routes, quality-weighted, each circuit pinned to one
    /// path (so its ordering is preserved). Set <see cref="NetRomForwardMode.BestRoute"/>
    /// to always use the single best route. Only consulted when <see cref="Routing"/>
    /// is <see cref="NetRomRouting.Transit"/>.
    /// </summary>
    public NetRomForwardMode ForwardMode { get; init; } = NetRomForwardMode.PerFlow;

    /// <summary>
    /// Our NET/ROM node alias / mnemonic, advertised in our NODES broadcast (the
    /// 6-char field). Null/empty = fall back to the node identity alias (then the
    /// callsign). Only the first 6 characters reach the wire.
    /// </summary>
    public string? Alias { get; init; }

    /// <summary>Path quality assumed for a directly-heard neighbour (the canonical
    /// default-port quality). Null = canonical default (192).</summary>
    public int? DefaultNeighbourQuality { get; init; }

    /// <summary>Worst quality a learned route may have and still be kept (MINQUAL).
    /// Null = canonical default (0 — keep everything above zero).</summary>
    public int? MinQuality { get; init; }

    /// <summary>Obsolescence count a route is (re)initialised to on a broadcast
    /// (OBSINIT). Null = canonical default (6).</summary>
    public int? ObsoleteInitial { get; init; }

    /// <summary>The obsolescence advertise-gate (OBSMIN): a route below this is kept
    /// but no longer included in our outgoing broadcasts. Null = canonical default (4).</summary>
    public int? ObsoleteMinimum { get; init; }

    /// <summary>Seconds between obsolescence sweeps + (when <see cref="Broadcast"/>)
    /// NODES broadcasts — the canonical NODESINTERVAL. Null = default (3600 — once an
    /// hour).</summary>
    public int? SweepIntervalSeconds { get; init; }

    /// <summary>The L4 circuit send-window we propose / accept (BPQ <c>L4WINDOW</c>).
    /// Null = canonical default (4).</summary>
    public int? Window { get; init; }

    /// <summary>The L4 retransmit timeout in seconds (BPQ <c>L4TIMEOUT</c>-ish). Null
    /// = default (5 s).</summary>
    public int? TransportTimeoutSeconds { get; init; }

    /// <summary>Max L4 retransmit attempts before a circuit fails (BPQ
    /// <c>L4RETRIES</c>). Null = default (3).</summary>
    public int? TransportRetries { get; init; }

    /// <summary>Initial L3 network-header time-to-live (hop limit) on circuits we
    /// originate (BPQ <c>L3TIMETOLIVE</c>). Null = default (25).</summary>
    public int? TimeToLive { get; init; }

    /// <summary>
    /// The INP3 link-timing routing overlay (default-off). When
    /// <see cref="NetRomInp3Options.Enabled"/> is <c>false</c> — which is the
    /// default, since the property initialises to <c>new()</c> ⇒
    /// <see cref="NetRomInp3Options.Default"/> — the node behaves byte-for-byte as
    /// today: no L3RTT probing, no RIF ingest/emit, no INP3 routes. INP3 is an
    /// opt-in overlay on the vanilla quality-based NET/ROM stack; it requires both
    /// <see cref="Enabled"/> and a <see cref="Routing"/> mode that opens interlinks
    /// (<see cref="NetRomRouting.Endpoint"/> or <see cref="NetRomRouting.Transit"/>) —
    /// the L3RTT / RIF frames ride the connected-mode interlink machinery those modes
    /// gate, so the host constructs the overlay only when interlinks are enabled — the
    /// validator rejects <c>inp3.enabled</c> with <c>routing: none</c> rather than
    /// silently no-op.
    /// </summary>
    /// <remarks>
    /// Unlike the nullable-overlay knobs above (<see cref="Window"/> etc., which are
    /// resolved field-by-field against a lib <c>Default</c>), this binds the whole
    /// <see cref="NetRomInp3Options"/> record directly: it is one validated record
    /// (its own <see cref="NetRomInp3Options.Validate"/> is the single source of
    /// truth for the knob ranges), it is pure durations / ints / bools (no
    /// discriminated union, no <c>Callsign</c> struct), and an absent nested
    /// <c>inp3:</c> key simply leaves the C# default in place under the existing
    /// <c>IgnoreUnmatchedProperties</c> + camel-case deserializer. The
    /// <see cref="System.TimeSpan"/>-typed knobs (<see cref="NetRomInp3Options.L3RttInterval"/>,
    /// <see cref="NetRomInp3Options.L3RttResetWindow"/>, <see cref="NetRomInp3Options.RifInterval"/>,
    /// <see cref="NetRomInp3Options.PositiveDebounce"/>) carry as YAML duration scalars
    /// (e.g. <c>l3RttInterval: 00:01:00</c>) via YamlDotNet's built-in
    /// <c>TimeSpan</c> converter. See docs/netrom-inp3-host-integration-design.md §2.
    /// </remarks>
    public NetRomInp3Options Inp3 { get; init; } = new();

    /// <summary>
    /// The resolved routing role every consumer reads (the <see cref="ResolveRouting"/>
    /// tuple's first element, discarding the warnings). This is the single value
    /// <c>NetRomService</c> / the validator / the API gate on — never the raw
    /// <see cref="Routing"/> / <see cref="Connect"/> / <see cref="Forward"/> fields.
    /// </summary>
    /// <remarks><see cref="YamlIgnoreAttribute"/>: a derived view, not a stored field — it
    /// must never round-trip through the YAML; only <see cref="Routing"/> /
    /// <see cref="Connect"/> / <see cref="Forward"/> are persisted.</remarks>
    [YamlIgnore]
    public NetRomRouting EffectiveRouting => ResolveRouting().Routing;

    /// <summary>
    /// Resolve the effective <see cref="NetRomRouting"/> from the new <see cref="Routing"/>
    /// knob and the legacy <see cref="Connect"/> / <see cref="Forward"/> bools, returning
    /// any back-compat warnings the operator should see (surfaced at config load — see
    /// <c>FileConfigProvider</c>). The mapping, in precedence order:
    /// <list type="bullet">
    /// <item><b><see cref="Routing"/> explicitly set</b> → it wins. If a legacy
    /// <c>connect</c>/<c>forward</c> key is ALSO present, a warning notes they are ignored
    /// in favour of <c>routing</c>.</item>
    /// <item><b>Legacy keys present, <see cref="Routing"/> unset</b> →
    /// <c>connect==true &amp;&amp; forward!=false</c> ⇒ <see cref="NetRomRouting.Transit"/>;
    /// <c>connect==true &amp;&amp; forward==false</c> ⇒ <see cref="NetRomRouting.Endpoint"/>;
    /// <c>connect!=true &amp;&amp; forward!=true</c> ⇒ <see cref="NetRomRouting.None"/>;
    /// <c>connect!=true &amp;&amp; forward==true</c> (the contradictory combo that was always
    /// inert) ⇒ <see cref="NetRomRouting.None"/> <b>plus a warning</b> that <c>forward: true</c>
    /// did nothing without <c>connect</c> and is now treated as <c>routing: none</c>.</item>
    /// <item><b>Nothing set</b> → <see cref="NetRomRouting.None"/> (the default).</item>
    /// </list>
    /// Pure + side-effect free, so it is safe to call from the property getter, the
    /// validator, and the config-load warning path alike.
    /// </summary>
    public (NetRomRouting Routing, IReadOnlyList<string> Warnings) ResolveRouting()
    {
        var legacyPresent = Connect.HasValue || Forward.HasValue;

        if (Routing is { } explicitMode)
        {
            // The new knob wins outright. Note (don't honour) any stale legacy keys.
            return legacyPresent
                ? (explicitMode,
                   ["netrom.connect / netrom.forward are ignored because netrom.routing is set explicitly; remove the legacy keys."])
                : (explicitMode, []);
        }

        if (!legacyPresent)
        {
            return (NetRomRouting.None, []);   // nothing configured — the default.
        }

        // Legacy mapping. Treat absent legacy bools as false (their historical defaults
        // were connect:false, forward:true — but forward only ever mattered under connect,
        // so an absent forward under connect:true maps to Transit, matching old behaviour).
        var connect = Connect == true;
        var forward = Forward == true;

        if (connect)
        {
            // connect:true, forward defaulting on (forward != false) ⇒ Transit;
            // connect:true, forward:false ⇒ Endpoint.
            return (Forward == false ? NetRomRouting.Endpoint : NetRomRouting.Transit, []);
        }

        // connect not on. forward:true here is the contradictory combo — it was always
        // inert (forwarding needs the interlink machinery connect gated). Surface it.
        if (forward)
        {
            return (NetRomRouting.None,
                ["netrom.forward: true had no effect without netrom.connect and is now treated as netrom.routing: none. Set netrom.routing: transit for the full router role."]);
        }

        return (NetRomRouting.None, []);
    }
}
