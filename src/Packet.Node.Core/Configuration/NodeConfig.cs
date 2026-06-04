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
    /// <summary>Schema version of the persisted config. Bumped when the shape
    /// changes incompatibly; lets a future loader migrate older blobs.</summary>
    public int SchemaVersion { get; init; } = 1;

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

    /// <summary>T2 response-delay timer, milliseconds. Null = engine default.</summary>
    public int? T2Ms { get; init; }

    /// <summary>T3 inactive-link timer, milliseconds. Null = engine default.</summary>
    public int? T3Ms { get; init; }

    /// <summary>N2 maximum retries before giving up. Null = engine default (10).</summary>
    public int? N2 { get; init; }

    /// <summary>Send-window size k. Null = engine default (4 for mod-8).</summary>
    public int? WindowSize { get; init; }

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

    /// <summary>KISS TXTAIL (0x04), in units of 10 ms. Most modern modems ignore it.</summary>
    public byte? TxTail { get; init; }
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

/// <summary>Management-surface configuration: the local telnet console and the
/// (slice-1 present-but-inert) web server.</summary>
public sealed record ManagementConfig
{
    /// <summary>The local dial-in telnet console.</summary>
    public TelnetConfig Telnet { get; init; } = new();

    /// <summary>The web server bind. Slice 1 maps only <c>GET /healthz</c>;
    /// API/auth/UI are later slices.</summary>
    public HttpConfig Http { get; init; } = new();
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
/// NET/ROM awareness configuration. This is the <b>read-only "NET/ROM aware"</b>
/// slice: when enabled, the node hears NODES routing broadcasts (UI frames to
/// dest <c>NODES</c>, PID 0xCF) heard on its AX.25 ports via the existing
/// frame-trace tap, parses them, and builds a routing table it surfaces in
/// <c>Nodes</c> / a future MCP tool. It <b>originates nothing</b> — no NODES
/// broadcasts, no L4 circuits — and cannot disturb a QSO.
/// </summary>
/// <remarks>
/// The routing knobs (quality floor, obsolescence init, table caps) are exposed
/// because NET/ROM has no single normative standard — the canonical defaults are
/// used unless the operator overrides, never a silent BPQ-ism. Default
/// <see cref="Enabled"/> is <c>true</c>: hearing NODES is free and harmless, so a
/// stock node is NET/ROM-aware out of the box.
/// </remarks>
public sealed record NetRomConfig
{
    /// <summary>Whether to listen for NODES broadcasts and maintain the routing
    /// table. Default <c>true</c> (read-only, harmless). Set <c>false</c> to make
    /// the node deaf to NET/ROM entirely.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Path quality assumed for a directly-heard neighbour (the canonical
    /// default-port quality). Null = canonical default (192).</summary>
    public int? DefaultNeighbourQuality { get; init; }

    /// <summary>Worst quality a learned route may have and still be kept (MINQUAL).
    /// Null = canonical default (0 — keep everything above zero).</summary>
    public int? MinQuality { get; init; }

    /// <summary>Obsolescence count a route is (re)initialised to on a broadcast
    /// (OBSINIT). Null = canonical default (6).</summary>
    public int? ObsoleteInitial { get; init; }

    /// <summary>Seconds between obsolescence sweeps (the broadcast-interval decay
    /// tick). Null = default (3600 — once an hour, the canonical NODES interval).</summary>
    public int? SweepIntervalSeconds { get; init; }
}
