namespace Packet.Node.Core.Configuration;

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
    /// the same panel over TLS - encrypting the password + JWT that would otherwise
    /// cross the LAN in clear, and providing the secure context WebAuthn/passkeys
    /// require.</summary>
    public HttpsConfig Https { get; init; } = new();

    /// <summary>Web control-API authentication. Default-OFF (see
    /// <see cref="AuthConfig"/>): with it off the API behaves exactly as it did
    /// before auth existed - the read / SSE / config / ports / sessions / ping
    /// endpoints and the SPA all serve unauthenticated. With it on, a JWT bearer
    /// token is required and the per-endpoint scope gates enforce.</summary>
    public AuthConfig Auth { get; init; } = new();

    /// <summary>LAN discovery: advertise this node over mDNS / DNS-SD (<c>_pdn._tcp</c>) so the
    /// pdn mobile app (and other LAN tools) can find it on the local network. Default-OFF; see
    /// <see cref="MdnsConfig"/>.</summary>
    public MdnsConfig Mdns { get; init; } = new();

    /// <summary>The browser's node command console (<c>POST /api/v1/console</c>): how long an
    /// abandoned session may live. See <see cref="SysopConsoleConfig"/>.</summary>
    public SysopConsoleConfig Console { get; init; } = new();
}

/// <summary>
/// The browser-facing node command console.
/// </summary>
/// <remarks>
/// Each open console owns a running node command service and a loopback connection pair, torn
/// down by <c>DELETE /api/v1/console/{id}</c>. A browser that is closed, crashes, or loses the
/// network never sends that DELETE, so the manager reaps sessions that have had no SSE
/// subscriber and no typed input for <see cref="IdleTimeoutMinutes"/> (review item C062, #694).
/// </remarks>
public sealed record SysopConsoleConfig
{
    /// <summary>Minutes an open console may sit with no SSE subscriber attached and no typed
    /// input before the node closes it. <c>0</c> disables reaping (sessions then live until
    /// closed or the host shuts down, the pre-#694 behaviour). Default 30.</summary>
    public int IdleTimeoutMinutes { get; init; } = 30;
}

/// <summary>
/// Web control-API authentication configuration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-ON since the LAN-bind default.</b> <see cref="Enabled"/> defaults to
/// <c>true</c>. It used to default off, which was only safe because the web listener
/// defaulted to loopback (<see cref="HttpConfig.Bind"/>); now that a fresh node binds
/// the LAN so an operator can actually reach its panel, the login is what stands
/// between the network and a full admin session - one that can rewrite config, add
/// ports, and <em>transmit</em>. The two defaults move together and must stay that way.
/// </para>
/// <para>
/// A fresh node has zero users, and <c>GET /setup/state</c> + <c>POST /setup</c> are
/// never gated, so first-run setup still works with auth on: the first visitor gets
/// the wizard, creates the admin, and every later request needs a token. The auth
/// machinery (user store, JWT issuing/validation, the scope policies) is always wired
/// either way - this flag only switches <em>enforcement</em>.
/// </para>
/// <para>
/// <b>No change for an existing node.</b> A stored config is a fully-explicit
/// serialised <see cref="NodeConfig"/> in <c>pdn.db</c> (every property written, not
/// just the non-defaults), so a node that was set up under the old defaults keeps
/// exactly the posture it had. This default is what a <em>new</em> install is seeded
/// with.
/// </para>
/// <para>
/// The signing key and the user records live in <c>pdn.db</c> (the consolidated
/// SQLite store), not here - this config record only carries the on/off switch
/// and the token lifetime. The key is generated on first start and persisted;
/// it is never written to config or logs.
/// </para>
/// </remarks>
public sealed record AuthConfig
{
    /// <summary>Whether the web control API requires authentication. Default
    /// <c>true</c> - a JWT bearer token is required on the gated endpoints and the
    /// <c>read</c>/<c>operate</c>/<c>admin</c> scope policies enforce. Set it
    /// <c>false</c> only for a node whose panel is not reachable from the network
    /// (a loopback bind, or a lab box behind something else that authenticates).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Access-token lifetime in minutes. Null = the default (60 - ~1h).
    /// Short-lived: when it expires the web client silently exchanges its refresh
    /// token (see <see cref="RefreshTokenMinutes"/>) for a fresh one rather than
    /// forcing a re-login.</summary>
    public int? AccessTokenMinutes { get; init; }

    /// <summary>Refresh-token lifetime in minutes. Null = the default (10080 - 7
    /// days). This is the real session length: a refresh token rotates on each use
    /// (one-time-use) and lets the client renew its short access token without a
    /// re-login until the refresh token itself expires. Must exceed
    /// <see cref="AccessTokenMinutes"/> when both are set (a refresh token that
    /// outlived its access token is the whole point - see
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
/// <b>localhost-first, zero-config.</b> The defaults - <see cref="RelyingPartyId"/> =
/// <c>localhost</c>, <see cref="AllowedOrigins"/> empty - make same-machine passkeys
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
/// real-domain case - when empty the host accepts the request's own origin (plus
/// <c>localhost</c>); when set it pins exactly those origins.
/// </para>
/// <para>
/// <b>Distribution tiers are parked.</b> Per the trust-pattern doc §8 decision gate,
/// the mDNS / ACME / IP-encoded-name machinery is NOT built - only the RP id + origins
/// are made configurable so a real-domain operator (doc §2a) can set them by hand.
/// </para>
/// </remarks>
public sealed record WebAuthnConfig
{
    /// <summary>The WebAuthn Relying Party ID - the registrable domain a passkey is
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
    // distinct AllowedOrigins lists would be unequal - breaking the YAML round-trip
    // identity (serialise→parse yields a fresh list). Compare the list by value so
    // equality is content-based, matching every other config record (see ConfigEquality).
    public bool Equals(WebAuthnConfig? other) =>
        other is not null
        && RelyingPartyId == other.RelyingPartyId
        && RelyingPartyName == other.RelyingPartyName
        && ConfigEquality.ListEqual(AllowedOrigins, other.AllowedOrigins);

    public override int GetHashCode() =>
        HashCode.Combine(RelyingPartyId, RelyingPartyName, ConfigEquality.ListHash(AllowedOrigins));
}

/// <summary>
/// The local telnet console listener. Defaults to loopback-only - the console
/// is operator-local dial-in, not a network service.
/// </summary>
public sealed record TelnetConfig
{
    /// <summary>Whether to run the telnet console at all.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Bind address. Defaults to <c>127.0.0.1</c> - loopback only.</summary>
    public string Bind { get; init; } = "127.0.0.1";

    /// <summary>TCP port for the telnet console.</summary>
    public int Port { get; init; } = 8011;
}

/// <summary>The web server bind. Present-but-inert in slice 1.</summary>
public sealed record HttpConfig
{
    /// <summary>Bind address for Kestrel. Default <c>0.0.0.0</c> - a headless node is
    /// no use if its own control panel is unreachable from the machine the operator is
    /// sitting at. What keeps that safe is <see cref="AuthConfig.Enabled"/> defaulting
    /// on; do not narrow one without considering the other. Read at process start, so a
    /// change needs a restart, not just a config apply.</summary>
    public string Bind { get; init; } = "0.0.0.0";

    /// <summary>TCP port for the web server.</summary>
    public int Port { get; init; } = 8080;
}

/// <summary>
/// LAN discovery via mDNS / DNS-SD. When enabled the node advertises an
/// <c>_pdn._tcp</c> service on the local link so the pdn mobile app (and any DNS-SD
/// browser) can find it without typing an address.
/// </summary>
/// <remarks>
/// <para>
/// <b>Default-OFF</b> (matching the node's posture for anything it emits): a stock node
/// advertises nothing until the operator opts in. Discovery is also only useful once
/// <see cref="HttpConfig.Bind"/> is a LAN address - with a loopback bind the advertised
/// endpoint would be unreachable, so the advertiser stays dormant (with a log line)
/// rather than publish an address that won't connect.
/// </para>
/// <para>
/// <b>The callsign is the identity.</b> Multiple nodes commonly share one LAN, so the
/// advert carries the node's <see cref="Identity.Callsign"/> in a <c>cs</c> TXT record
/// (the canonical identity the client keys on) AND as the default service instance name
/// (so even a plain browser shows distinct entries). The <see cref="Identity.Alias"/>,
/// if set, rides a <c>name</c> TXT as a friendly label, and the node version a <c>v</c> TXT.
/// </para>
/// <para>
/// <b>Mechanism + portability.</b> Registration goes through the system mDNS daemon
/// (<c>avahi-publish</c>) - the conflict-free path on the Linux hosts the node deb
/// targets (no second responder fighting Avahi for port 5353). Where <c>avahi-publish</c>
/// is absent (a host without Avahi, or a non-Linux dev box) the advertiser logs once and
/// stays dormant; the node is unaffected and manual add-by-address still works in the app.
/// </para>
/// </remarks>
public sealed record MdnsConfig
{
    /// <summary>Whether to advertise this node on the LAN over mDNS (<c>_pdn._tcp</c>).
    /// Default <c>false</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>The DNS-SD service instance name (the label a browser shows). Optional;
    /// defaults to the node's <see cref="Identity.Callsign"/> so multiple nodes on one LAN
    /// stay distinguishable. The callsign also rides the <c>cs</c> TXT record as the
    /// canonical identity (instance names are user/Bonjour-renamable on collision).</summary>
    public string? InstanceName { get; init; }
}

/// <summary>
/// Optional HTTPS/TLS listener for the web control panel. <b>Default-OFF</b>
/// (<see cref="Enabled"/> = <c>false</c>) - only the plain HTTP listener runs, so a
/// node that never configured TLS behaves exactly as before. When enabled, a second
/// Kestrel endpoint serves the same panel over TLS.
/// </summary>
/// <remarks>
/// <para>
/// The cert is loaded from <see cref="CertificatePath"/> (a PKCS#12 / .pfx) when set;
/// otherwise, if <see cref="GenerateSelfSignedOnMissing"/> is true, a self-signed cert
/// is generated on first start and persisted alongside the node state so it is stable
/// across restarts. A self-signed cert <em>encrypts the channel</em> (the password +
/// JWT no longer cross the LAN in clear) but is not trusted by browsers - to get a
/// trusted secure context (needed for WebAuthn/passkeys over a LAN IP) point
/// <see cref="CertificatePath"/> at a cert the client trusts, or reach the node via
/// <c>localhost</c>.
/// </para>
/// </remarks>
public sealed record HttpsConfig
{
    /// <summary>Whether the HTTPS listener runs. Default <c>false</c> - HTTP only.</summary>
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
