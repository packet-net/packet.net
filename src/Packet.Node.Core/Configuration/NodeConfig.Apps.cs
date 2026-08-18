namespace Packet.Node.Core.Configuration;

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

    /// <summary>The trust switch. Default <c>false</c> - a discovered package never runs,
    /// resolves a verb, or shows a tile until the owner flips this.</summary>
    public bool Enabled { get; init; }

    /// <summary>Optional override of the manifest's <c>packet.command</c> node-prompt verb
    /// (<c>docs/app-packages.md</c> § Application packet identity). Null = use the manifest's
    /// own <see cref="AppPacketSpec.Command"/>. This replaces the old <c>match</c> field - the
    /// verb for a session app is now <see cref="AppPacketSpec.Command"/>, owner-overridable here.</summary>
    public string? Command { get; init; }

    /// <summary>Optional pinned callsign for this app - the node's choice, the on-air L2 identity
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

    // The Environment dictionary is a collection member, which a record compares by
    // REFERENCE - so a YAML round-trip (which yields a fresh dictionary) would read as
    // changed. Hand-roll value equality over it, matching every other config record
    // with a collection member (see ConfigEquality).
    public bool Equals(AppOverrideConfig? other) =>
        other is not null
        && Id == other.Id
        && Enabled == other.Enabled
        && Command == other.Command
        && Callsign == other.Callsign
        && Netrom == other.Netrom
        && ConfigEquality.DictEqual(Environment, other.Environment);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Enabled);
        hash.Add(Command);
        hash.Add(Callsign);
        hash.Add(Netrom);
        hash.Add(ConfigEquality.DictHash(Environment));
        return hash.ToHashCode();
    }
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

    /// <summary>The default advertised quality when <see cref="Quality"/> is unset - high (an
    /// app on this node is one hop away, directly reachable), matching the BPQ
    /// <c>APPLICATION ...,Quality</c> convention for a local application.</summary>
    public const int DefaultQuality = 255;
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
/// One registered node application - the inline, owner-authored analog of a BPQ
/// <c>APPLICATION n,CMD,Call,Alias,Quality</c> line. <see cref="Id"/> is the stable identity
/// (log / reconcile key); <see cref="Command"/> is the console verb that launches it. Out-of-process
/// by design - the node never links app code (see <c>docs/app-extensibility.md</c>). The
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
    /// console verbs - so an app can never shadow <c>BYE</c>/<c>CONNECT</c>/etc. Must be unique
    /// within <see cref="NodeConfig.Applications"/> and must not collide with a built-in verb.
    /// This is the <i>verb</i>; the <i>executable</i> is <see cref="Executable"/>.</summary>
    public required string Command { get; init; }

    /// <summary>Whether this app is launchable. A disabled entry is retained in config but
    /// never spawned (its verb falls through to "unknown command"). Default <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>How to run the app. Default <see cref="ApplicationKind.Process"/>.</summary>
    public ApplicationKind Kind { get; init; } = ApplicationKind.Process;

    /// <summary>The executable to spawn (<see cref="ApplicationKind.Process"/>) - e.g.
    /// <c>/usr/bin/python3</c>. Required for a process app. Distinct from <see cref="Command"/>
    /// (the node-prompt verb).</summary>
    public string? Executable { get; init; }

    /// <summary>The Unix-domain socket the daemon listens on (<see cref="ApplicationKind.Socket"/>)
    /// - e.g. <c>/run/packetnet/lobby.sock</c>. The node connects here per session. Required for a
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

    /// <summary>Optional pinned callsign - the node's choice, the on-air L2 identity this inline
    /// app binds (<c>docs/app-packages.md</c> § Application packet identity). A full callsign or
    /// a bare <c>-N</c> SSID appended to the node base. Null = the node auto-assigns
    /// <c>&lt;node-base&gt;-&lt;lowest free SSID&gt;</c>. Injected as <c>PDN_APP_CALLSIGN</c>.</summary>
    public string? Callsign { get; init; }

    /// <summary>Optional opt-in NET/ROM advertisement (alias → resolved callsign, with quality).
    /// Absent ⇒ nothing extra advertised. See <see cref="AppNetromConfig"/>.</summary>
    public AppNetromConfig? Netrom { get; init; }

    // Args and Capabilities are collection members, which a record compares by REFERENCE
    // - so a YAML round-trip (which yields fresh lists) would read as changed. Hand-roll
    // value equality over them, matching every other config record with a collection
    // member (see ConfigEquality).
    public bool Equals(ApplicationConfig? other) =>
        other is not null
        && Id == other.Id
        && Command == other.Command
        && Enabled == other.Enabled
        && Kind == other.Kind
        && Executable == other.Executable
        && SocketPath == other.SocketPath
        && WorkingDirectory == other.WorkingDirectory
        && Callsign == other.Callsign
        && Ui == other.Ui
        && Netrom == other.Netrom
        && ConfigEquality.ListEqual(Args, other.Args)
        && ConfigEquality.ListEqual(Capabilities, other.Capabilities);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Command);
        hash.Add(Enabled);
        hash.Add(Kind);
        hash.Add(Executable);
        hash.Add(SocketPath);
        hash.Add(WorkingDirectory);
        hash.Add(Callsign);
        hash.Add(Ui);
        hash.Add(Netrom);
        hash.Add(ConfigEquality.ListHash(Args));
        hash.Add(ConfigEquality.ListHash(Capabilities));
        return hash.ToHashCode();
    }
}

/// <summary>
/// How the control panel opens an app's web UI from its left-nav entry - the <c>ui.mode</c>
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
    /// own full page inside the frame - no signal param is appended.</summary>
    Embedded,

    /// <summary>Like <see cref="Embedded"/>, but the iframe src carries <c>?pdn_embed=1</c> so the
    /// app renders chrome-less and blends into the single PDN chrome.</summary>
    Slot,
}

/// <summary>
/// The human-plane manifest for an application: where its own web server lives and how its
/// launcher tile reads. pdn reverse-proxies to <see cref="Upstream"/> and never imports the
/// app - it is a broker (see <c>docs/app-gateway.md</c>).
/// </summary>
public sealed record AppUiConfig
{
    /// <summary>The app's own web server base URL - <b>loopback</b> (e.g.
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
