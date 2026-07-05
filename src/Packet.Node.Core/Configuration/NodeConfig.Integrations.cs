namespace Packet.Node.Core.Configuration;

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
/// kissproxy-compatible MQTT frame emission (<c>docs/research/pdn-mqtt-frame-emission.md</c>). When
/// enabled, the node publishes every AX.25 frame it sends/receives to an MQTT broker in
/// <a href="https://github.com/M0LTE/kissproxy">kissproxy</a>'s native wire format, so pdn can replace
/// a kissproxy instance at a site without losing the downstream <c>kiss-collector</c> capture pipeline.
/// <b>Outbound only</b> and <b>default-OFF</b>: a stock node publishes nothing.
/// </summary>
/// <remarks>
/// <para>
/// The topic contract the collector ingests is <c>[{prefix}/]kissproxy/{node}/{instance}/{dir}/{sub}</c>
/// where <c>{node}</c> is <see cref="NodeName"/> (resolving to the machine name when null),
/// <c>{instance}</c> is the port's <see cref="PortConfig.MqttInstance"/> (the band, operationally),
/// <c>{dir}</c> is <c>fromModem</c> (RX) / <c>toModem</c> (TX), and two <c>{sub}</c>s are emitted per
/// frame: <c>unframed/port0/DataFrameKissCmd</c> (the SLIP-decoded AX.25 bytes the collector reads) and
/// <c>framed</c> (the full KISS frame incl. FEND). See <c>MqttFrameEmitter</c>.
/// </para>
/// <para>
/// <b>This block carries a broker credential.</b> <see cref="Password"/> (and <see cref="Username"/>)
/// belong in the git-ignored <c>appsettings.Local.json</c>, never in a committed <c>config.yaml</c>.
/// The validator checks host/credential coherence <em>always</em> (even when disabled) so a
/// disabled-but-edited block can't hold junk that detonates on enable, and requires
/// <see cref="BrokerHost"/> when <see cref="Enabled"/>.
/// </para>
/// </remarks>
public sealed record MqttConfig
{
    /// <summary>The master switch. Default <c>false</c> — the node publishes to MQTT only when the
    /// operator opts in; with it off the emitter is dormant and sends nothing.</summary>
    public bool Enabled { get; init; }

    /// <summary>The MQTT broker hostname / IP. Required when <see cref="Enabled"/>. Default empty.</summary>
    public string BrokerHost { get; init; } = "";

    /// <summary>The broker's TCP port. Default 1883 (plain MQTT); 8883 is the TLS convention.</summary>
    public int BrokerPort { get; init; } = 1883;

    /// <summary>Whether to connect over TLS. Default <c>false</c> (plain TCP, matching kissproxy,
    /// which has no TLS). When <c>true</c> the client negotiates TLS to <see cref="BrokerPort"/>.</summary>
    public bool UseTls { get; init; }

    /// <summary>Broker username, or null for an anonymous connection. Default null. Put this (and
    /// especially <see cref="Password"/>) in the git-ignored <c>appsettings.Local.json</c>.</summary>
    public string? Username { get; init; }

    /// <summary>Broker password, or null. Default null. <b>Never commit a real value</b> — it belongs
    /// in the git-ignored <c>appsettings.Local.json</c> (see the type remarks).</summary>
    public string? Password { get; init; }

    /// <summary>An optional topic prefix prepended as <c>{prefix}/kissproxy/…</c>. Default empty (no
    /// prefix — the bare <c>kissproxy/…</c> tree the existing collector subscribes to).</summary>
    public string TopicPrefix { get; init; } = "";

    /// <summary>The <c>{node}</c> topic segment + the client-id stem. Null (the default) resolves to
    /// <see cref="Environment.MachineName"/> at runtime — set it explicitly to match an existing
    /// collector's host key (e.g. <c>gb7rdg-node</c>) when migrating a site off kissproxy.</summary>
    public string? NodeName { get; init; }

    /// <summary>Whether payloads are base64-encoded (kissproxy's per-modem base64 flag). Default
    /// <c>false</c> — raw binary bytes. When <c>true</c>, payloads are
    /// <see cref="Convert.ToBase64String(byte[], Base64FormattingOptions)"/> with
    /// <see cref="Base64FormattingOptions.InsertLineBreaks"/> (byte-for-byte kissproxy).</summary>
    public bool Base64 { get; init; }

    /// <summary>MQTT publish QoS (0 at-most-once, 1 at-least-once, 2 exactly-once). Default 2
    /// (ExactlyOnce), matching kissproxy. Validated to 0..2.</summary>
    public int Qos { get; init; } = 2;

    /// <summary>When <c>true</c>, publish only over-air (RF) frames and skip internal/loopback
    /// traffic. Default <c>false</c>. Mirrors <see cref="OarcConfig.TracesRfOnly"/> — every current
    /// pdn transport is RF, so the filter is effectively pass-through until a non-RF transport
    /// exists, but it is honoured so it's correct the day one is added.</summary>
    public bool RfOnly { get; init; }
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
