using System.Net;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Packet.Node.Api;
using Packet.Node.Cli;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Beacons;
using Packet.Node.Core.Capabilities;
using Packet.Node.Core.Configuration;
using Packet.Node.Core.Console;
using Packet.Node.Core.Hosting;
using Packet.Node.Core.NetRom;
using Packet.Node.Core.Oarc;
using Packet.Node.Core.Traffic;
using Packet.Node.Core.Transports;
using Packet.Node.Mcp;

// The composition root for the Packet.NET node. This IS a Generic Host (the
// WebApplication builder gives us DI, config, hosted services, and logging),
// but slice 1 maps ZERO authenticated endpoints — only GET /healthz. The web
// server is present-but-inert: Kestrel binds from config, and the API / auth /
// UI arrive in later slices.

// `pdn mcp` — the stdio MCP server subcommand (Phase 8). A separate, short process
// that bridges to the running node's loopback REST API and speaks MCP over stdio to a
// local client (Claude Code, etc.). It must short-circuit BEFORE the web host is built
// — it is not the node, it talks to the node. See McpStdioEntry + docs/mcp-design.md.
if (args.Length > 0 && args[0] == "mcp")
{
    await McpStdioEntry.RunAsync(args);
    return 0;
}

// `pdn config export [--out <path>]` / `pdn config import <path>` — the headless
// inspect/diff/restore CLI for config-in-DB (#473). Like `pdn mcp` they short-circuit
// BEFORE the web host is built: they boot ONLY the SqliteConfigProvider over pdn.db
// (no Kestrel, no hosted services), so an operator with shell access can export the
// live config to YAML, diff it, edit it, and import it back — preserving the
// edit-as-text ergonomic now that config lives in the DB, not a watched file.
if (args.Length > 0 && args[0] == "config")
{
    return await PdnConfigCli.RunAsync(args);
}

var configPath = ResolveConfigPath(args);
var dbPath = ResolveDbPath(args);
var seedPath = ResolveSeedPath();
var templatePath = ResolveBootstrapTemplatePath();

// ContentRoot = the app's own directory (where the published web UI's wwwroot
// sits), NOT the working directory. The packaged node runs with a WorkingDirectory
// of the writable StateDirectory (/var/lib/packetnet) while the binary + wwwroot
// live in /opt/packetnet/app, so defaulting ContentRoot to the CWD would make
// UseStaticFiles look in the wrong place. (Config/DB paths still resolve against
// the CWD by design — see ResolveConfigPath/ResolveDbPath.)
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Build the config provider eagerly: it loads the live config from pdn.db (or, on a
// FIRST boot with no row, migrates the legacy --config YAML / seed / template into the
// DB) and gives us the HTTP bind to hand to Kestrel before the host starts. Registered
// as the singleton IConfigProvider so the hosted service reuses this very instance (a
// single source of truth). The eager provider logs to the bootstrap console; once the
// host is up, NodeHostedService logs through the configured pipeline.
//
// Config-in-DB (#473): config now lives in the same pdn.db as routing/auth/heard, as a
// single versioned JSON blob, NOT in a watched /etc YAML. A config write (PUT /config,
// port CRUD) persists to the DB and raises the SAME OnChange the file watcher used to —
// so the reconcile path is unchanged. The --config YAML is read ONLY on first boot (to
// migrate a hand-tuned config across the 0.17 upgrade) and is vestigial thereafter; edit
// config via the web UI / API / `pdn config import`. See docs/config-in-db.md.
using var bootstrapLoggers = LoggerFactory.Create(b => b.AddConsole());
var configStore = new SqliteConfigStore(
    dbPath,
    TimeProvider.System,
    bootstrapLoggers.CreateLogger<SqliteConfigStore>());
var configProvider = new SqliteConfigProvider(
    configStore,
    configPath,                                            // the legacy YAML to import on first boot
    seedPath,                                              // PACKETNET_CONFIG_SEED (headless image bootstrap)
    templatePath,                                          // /usr/share/packetnet/packetnet.yaml.example
    markerDir: Path.GetDirectoryName(Path.GetFullPath(dbPath)),
    TimeProvider.System,
    bootstrapLoggers.CreateLogger<SqliteConfigProvider>());
builder.Services.AddSingleton<IConfigProvider>(configProvider);
// The same instance is also the WRITE seam for the config API (PUT /config et al.).
// Registered separately so read-only consumers + the test fakes stay on the plain
// IConfigProvider; only this provider can persist an edit.
builder.Services.AddSingleton<IWritableConfigProvider>(configProvider);

// Teach the minimal-API JSON layer to (de)serialise the polymorphic TransportConfig
// union (a PUT /config body needs the `kind`-discriminated read; this is transparent
// for the existing GET serialisation). Web defaults (camelCase) are otherwise intact.
// This is the SAME converter SqliteConfigStore persists the blob with (NodeConfigJson),
// so the structured PUT /config body and the on-disk DB bytes are byte-identical — one
// canonical serialisation, no second JSON dialect to drift.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new TransportConfigJsonConverter()));

// The routing-table persistence store (pdn.db). Created eagerly so it can hydrate
// NetRomService at start; registered as the singleton INetRomRoutingStore the hosted
// service injects. A store fault degrades to in-memory — it never fails the node.
var routingStore = new SqliteNetRomRoutingStore(
    dbPath,
    bootstrapLoggers.CreateLogger<SqliteNetRomRoutingStore>());
builder.Services.AddSingleton<INetRomRoutingStore>(routingStore);

// The per-peer AX.25 capability cache (same pdn.db, same resilient discipline). Program.cs
// composes services manually (it does not call AddPacketNode), so — like BeaconService and the
// other stores — both the durable store AND the cache service are registered eagerly here; the
// deployed node thus gets the pdn.db-backed variant.
var peerCapabilityStore = new SqlitePeerCapabilityStore(
    dbPath,
    bootstrapLoggers.CreateLogger<SqlitePeerCapabilityStore>());
builder.Services.AddSingleton<IPeerCapabilityStore>(peerCapabilityStore);
builder.Services.AddSingleton(sp => new PeerCapabilityCache(
    sp.GetService<IPeerCapabilityStore>(),
    sp.GetService<TimeProvider>() ?? TimeProvider.System));

// The MHeard log (#454; same pdn.db, same resilient discipline). The durable store + the log
// service are registered eagerly here (Program.cs composes manually); the store hydrates the log
// on construction and the telemetry tap feeds it every received frame's source station. A node
// without a writable db degrades to an in-memory log (the store still constructs, just stays empty).
var heardStore = new Packet.Node.Core.Heard.SqliteHeardStore(
    dbPath,
    bootstrapLoggers.CreateLogger<Packet.Node.Core.Heard.SqliteHeardStore>());
builder.Services.AddSingleton<Packet.Node.Core.Heard.IHeardStore>(heardStore);
builder.Services.AddSingleton(sp => new Packet.Node.Core.Heard.HeardLog(
    sp.GetService<Packet.Node.Core.Heard.IHeardStore>(),
    sp.GetService<TimeProvider>() ?? TimeProvider.System));

// Radio bus scanner (GET /api/v1/radios/scan): probes serial ports for attached radios by CCDI
// serial. No hardware is touched until a scan is requested; the scanner is bounded + single-flight.
builder.Services.AddSingleton<Packet.Node.Core.Radios.IRadioScanner>(new Packet.Node.Core.Radios.TaitRadioScanner());

// --- Web control-API auth foundation (default-OFF behind management.auth.enabled) ---
//
// The machinery is ALWAYS wired (user store, JWT issuing/validation, the scope
// policies + handler, the authentication/authorization middleware); only ENFORCEMENT
// is conditional on the flag (ScopeRequirementHandler passes through when off). So a
// node with auth off serves every endpoint exactly as before — turning auth on is a
// deliberate config change, never a silent behaviour shift.
//
// The user store + JWT signing key live in the same pdn.db as the routing table. The
// key is generated by a CSPRNG on first start and persisted (tokens survive a restart);
// it is never logged. If the store can't produce a key, JwtTokenService is left null —
// auth then cannot be enabled (login returns 503), but the node still boots.
var userStore = new SqliteUserStore(dbPath, bootstrapLoggers.CreateLogger<SqliteUserStore>());
builder.Services.AddSingleton<IUserStore>(userStore);

// The node-wide audit log for privileged actions, persisted to the same pdn.db. Same
// resilient discipline as the user/routing stores (a store fault degrades to
// "logged-but-not-persisted", never faults the node). Wired into the MCP write tools;
// REST write endpoints adopt the same IAuditLog seam. The §6 audit promise.
var auditLog = new Packet.Node.Core.Audit.SqliteAuditLog(
    dbPath, bootstrapLoggers.CreateLogger<Packet.Node.Core.Audit.SqliteAuditLog>());
builder.Services.AddSingleton<Packet.Node.Core.Audit.IAuditLog>(auditLog);

// MCP OAuth 2.1 stores (the hosted claude.ai connector — docs/mcp-oauth-design.md). Registered
// unconditionally (same resilient pdn.db pattern); the endpoints are inert unless
// mcp.oauth.enabled. Persist registered clients + single-use authorization codes.
builder.Services.AddSingleton<Packet.Node.Core.Auth.Oauth.IOauthClientStore>(
    new Packet.Node.Core.Auth.Oauth.SqliteOauthClientStore(
        dbPath, bootstrapLoggers.CreateLogger<Packet.Node.Core.Auth.Oauth.SqliteOauthClientStore>()));
builder.Services.AddSingleton<Packet.Node.Core.Auth.Oauth.IOauthCodeStore>(
    new Packet.Node.Core.Auth.Oauth.SqliteOauthCodeStore(
        dbPath, bootstrapLoggers.CreateLogger<Packet.Node.Core.Auth.Oauth.SqliteOauthCodeStore>()));

var signingKey = userStore.GetOrCreateSigningKey();
var accessTokenLifetime = TimeSpan.FromMinutes(configProvider.Current.Management.Auth.AccessTokenMinutes ?? 60);
JwtTokenService? tokenService =
    signingKey is not null ? new JwtTokenService(signingKey, accessTokenLifetime, TimeProvider.System) : null;
// Registered only when the signing key is available. When it is NOT (e.g. pdn.db
// unwritable), the service stays unregistered and the login handler's
// `[FromServices] JwtTokenService?` parameter resolves to null → login returns 503,
// while the node still boots and serves everything else. (The parameter MUST carry
// [FromServices]: without it, minimal-API inference can't classify an unregistered
// complex-type parameter and FAILS AT STARTUP — "Failure to infer … tokens | UNKNOWN"
// — aborting the whole host instead of degrading.)
if (tokenService is not null)
{
    builder.Services.AddSingleton(tokenService);
}

// Refresh-token rotation (auth part 2). The store lives in the same pdn.db (by hash
// only — the opaque token is never persisted in clear); the service wraps it with the
// one-time-use rotation + reuse-detection (theft-response) logic. Gated on the signing
// key exactly like JwtTokenService: with no key, login can't issue an access token, so
// a refresh token would be useless — leave the service unregistered (the handlers'
// `[FromServices] RefreshTokenService?` resolves to null → 503), node still boots.
var refreshTokenStore = new SqliteRefreshTokenStore(dbPath, bootstrapLoggers.CreateLogger<SqliteRefreshTokenStore>());
builder.Services.AddSingleton<IRefreshTokenStore>(refreshTokenStore);
if (tokenService is not null)
{
    var refreshLifetime = TimeSpan.FromMinutes(configProvider.Current.Management.Auth.RefreshTokenMinutes ?? 10080);
    builder.Services.AddSingleton(new RefreshTokenService(refreshTokenStore, refreshLifetime, TimeProvider.System));
}

// Login lockout: a sliding-window failure counter keyed per-username AND per-source-IP
// (5 failures / 5 min → 429, self-healing cooldown). Always registered (it's pure
// in-memory + the clock); the login handler's `[FromServices] LoginThrottle?` is still
// optional so an unregistered-service path can never abort startup.
builder.Services.AddSingleton(new LoginThrottle(TimeProvider.System));

// WebAuthn / passkeys (auth part 3, default-off behind management.auth.enabled). The
// credential STORE (public keys, sign counters, transports) lives in the same pdn.db as
// the users — a fault degrades to "no passkeys", never crashing the node. The challenge
// cache holds pending ceremonies in-memory (server-generated, single-use, expiring,
// user/session-bound — the anti-replay core). The per-request IFido2 verifier is built
// in the endpoints (WebAuthnFido2Builder) from the live config + the actual serving
// origin, so the RP-id/origin split (localhost-first) is handled there, not baked at
// startup. Registered as the SAME nullable-service contract as the JWT/refresh services:
// the credential store always registers (it degrades internally), and the host treats a
// null token/refresh service (no signing key) as "passkeys unusable" → 503, node boots.
var webAuthnStore = new SqliteWebAuthnCredentialStore(dbPath, bootstrapLoggers.CreateLogger<SqliteWebAuthnCredentialStore>());
builder.Services.AddSingleton<IWebAuthnCredentialStore>(webAuthnStore);
builder.Services.AddSingleton(new WebAuthnChallengeCache(TimeProvider.System));

// Over-RF sysop-code (TOTP) enrolment (auth part 4, enrolment half, default-off behind
// management.auth.enabled). The per-user secret + callsign + replay counter live on the
// existing user store (pdn.db) — added additively, degrade-safe like the rest. The
// pending-enrolment cache holds in-flight enrolments in-memory (server-minted secret,
// single-use, expiring — never persisted until the user confirms a code), mirroring the
// WebAuthn challenge cache. Always registered (pure in-memory + the clock); the endpoint's
// `TotpEnrollmentCache?` stays optional so an unregistered-service path can never abort
// startup. The console SYSOP gate that VERIFIES a presented code over a packet session is a
// separate piece — it consumes IUserStore.FindByCallsign + TotpService, which this readies.
builder.Services.AddSingleton(new TotpEnrollmentCache(TimeProvider.System));

// The RFC-6238 verifier (stateless over the clock — the single-use replay guard rides the
// persisted per-user counter, not in-memory state). Registered so the host injects it into
// NodeHostedService for the over-RF SYSOP gate; the enrolment endpoints construct their own
// over the request clock. With it registered (and IUserStore above), an auth-enabled node's
// console gains the SYSOP elevation command — inert until a user enrols a TOTP credential.
builder.Services.AddSingleton(new TotpService(TimeProvider.System));

// Authentication: JWT bearer validated against THIS node's signing key/issuer/audience
// (HS256 only). Always registered so a token presented when auth is on is validated;
// when the key is unavailable the validator gets a throwaway parameters object that
// fails every token (auth simply can't be used). The middleware never CHALLENGES on its
// own — the per-endpoint scope policy decides, and that passes through when auth is off.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (tokenService is not null)
        {
            options.TokenValidationParameters = tokenService.ValidationParameters;
        }
        else
        {
            // No signing key → reject every token (validate against an impossible key set).
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateLifetime = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32]),
                ValidAlgorithms = [Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256],
            };
        }

        // SSE token-by-query: a browser EventSource can't set an Authorization
        // header, so the live feeds accept the JWT as a `?access_token=` query
        // param. Restricted to the SSE paths only (so we don't normalise
        // tokens-in-URLs across the API — they can leak into logs/referrers) and
        // only when no bearer header was supplied. The token is still fully
        // validated by the same pipeline; the query is just where it's read from.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token))
                {
                    var path = context.HttpContext.Request.Path;
                    bool isSse = path.StartsWithSegments("/api/v1/events")
                        || (path.StartsWithSegments("/api/v1/sessions") && path.Value?.EndsWith("/stream", StringComparison.Ordinal) == true)
                        || (path.StartsWithSegments("/api/v1/console") && path.Value?.EndsWith("/stream", StringComparison.Ordinal) == true);
                    if (isSse)
                    {
                        var queryToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(queryToken))
                        {
                            context.Token = queryToken;
                        }
                    }
                    else if (path.StartsWithSegments("/apps"))
                    {
                        // A browser navigation to a proxied app UI (/apps/{id}/*) can't set an
                        // Authorization header, so read the access token from the HttpOnly
                        // gateway cookie pdn sets at login/refresh. Same full validation applies.
                        var cookie = context.Request.Cookies[PdnAppGateway.CookieName];
                        if (!string.IsNullOrEmpty(cookie))
                        {
                            context.Token = cookie;
                        }
                    }
                }
                return Task.CompletedTask;
            },

            // MCP OAuth discovery hint (RFC 9728): when OAuth is enabled, a 401 on the MCP
            // endpoint carries WWW-Authenticate pointing at the protected-resource metadata, so
            // an unconfigured MCP client (claude.ai) can discover how to get a token. Only for
            // /mcp + only when mcp.oauth.enabled — every other 401 keeps the default challenge.
            //
            // App-gateway human-plane recovery: a browser navigation / slot iframe to a gated
            // /apps/{id}/* path can't carry an Authorization header (it relies on the pdn_at
            // cookie), and when that cookie's token is expired/absent the bare Bearer 401 is
            // UNRENDERABLE in a browser frame — iOS Safari saves the empty body as a file. So
            // for a /apps/* request that is a real browser navigation (not an XHR/API call) we
            // swap the bare 401 for a login redirect on the human plane: a top-level navigation
            // gets a 302 to the SPA login; a sub-frame (iframe/frame) can't 302 its parent, so
            // it gets a tiny 200 HTML page that breaks the SLOT out to the top-level login. This
            // does NOT weaken auth — the request is still rejected and re-login is still
            // required; it only replaces an undownloadable 401 with a renderable re-auth on
            // navigations a human (not a fetch) made. XHR/API 401s (Accept: application/json,
            // empty/fetch Sec-Fetch-Dest) are left EXACTLY as before — the SPA's on401 handler
            // owns those (silent refresh, else logout).
            OnChallenge = context =>
            {
                var http = context.HttpContext;
                var cfg = http.RequestServices.GetService<Packet.Node.Core.Configuration.IConfigProvider>();
                if (cfg?.Current.Mcp.Oauth.Enabled == true
                    && http.Request.Path.StartsWithSegments(cfg.Current.Mcp.Sse.Path))
                {
                    context.HandleResponse();
                    var b = $"{http.Request.Scheme}://{http.Request.Host.Value}";
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    http.Response.Headers.Append(
                        "WWW-Authenticate",
                        $"Bearer resource_metadata=\"{b}/.well-known/oauth-protected-resource\"");
                    return Task.CompletedTask;
                }

                if (http.Request.Path.StartsWithSegments("/apps")
                    && AppGatewayChallenge.IsBrowserNavigation(http.Request))
                {
                    // Suppress the bare-401 challenge and emit the renderable re-auth response
                    // (302 for a top-level nav, break-out HTML for a sub-frame). Returned as the
                    // event's task so the body write completes before the pipeline moves on.
                    context.HandleResponse();
                    return AppGatewayChallenge.WriteLoginRedirect(http);
                }
                return Task.CompletedTask;
            },
        };
    });

// Authorization: the read/operate/admin scope policies, evaluated by the
// flag-aware ScopeRequirementHandler (registered scoped so it reads the live config).
builder.Services.AddAuthorization(options => options.AddPdnScopePolicies());
builder.Services.AddScoped<IAuthorizationHandler, ScopeRequirementHandler>();

// The app-gateway reverse proxy (human plane): IHttpForwarder forwards /apps/{id}/* to an
// app's loopback web server. See PdnAppGateway + docs/app-gateway.md.
builder.Services.AddHttpForwarder();

builder.Services.AddSingleton<ITransportFactory>(TransportFactory.Instance);
builder.Services.AddSingleton(TimeProvider.System);

// Runtime, restart-free log-level overrides. The node's appsettings.json is read-only under
// ProtectSystem=strict and a restart drops every session, so an operator who needs to raise a
// category to Debug/Trace live can't do it by editing config. This singleton holds the live
// override map; registering it ALSO as an IConfigureOptions<LoggerFilterOptions> +
// IOptionsChangeTokenSource<LoggerFilterOptions> wires it into MEL's filter pipeline — a mutation
// fires the change token and the LoggerFactory re-applies the rebuilt rules to every cached
// logger, so the new level takes effect immediately. Empty by default (logging exactly as
// configured). Mutated by PUT /api/v1/system/loglevel (admin); read by GET (read). See
// DynamicLogLevelOverrides + PdnSystemApi.
builder.Services.AddSingleton<DynamicLogLevelOverrides>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<Microsoft.Extensions.Logging.LoggerFilterOptions>>(
    sp => sp.GetRequiredService<DynamicLogLevelOverrides>());
builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptionsChangeTokenSource<Microsoft.Extensions.Logging.LoggerFilterOptions>>(
    sp => sp.GetRequiredService<DynamicLogLevelOverrides>());
// Phase 7 self-update (docs/node-self-update-design.md): the install channel is resolved
// at boot — the build stamp gives deb-vs-selfcontained, and apt-vs-github is decided live
// (dpkg ownership of the running binary + apt-cache repo origin, every probe guarded). The
// launcher triggers the privileged, detached packetnet-update.service oneshot (the node
// never runs apt / touches files itself).
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.IInstallChannelProvider>(
    new Packet.Node.Core.SelfUpdate.RuntimeInstallChannelProvider());
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.ISystemUpdateLauncher,
    Packet.Node.Core.SelfUpdate.SystemctlUpdateLauncher>();
// The available-version check (docs/node-self-update-design.md § Channel = github / Available-
// version check), surfaced on GET /api/v1/system/info as { updateAvailable, latestVersion }. One
// dispatching probe per channel — apt: `apt-cache policy` (the same guarded IProcessRunner seam
// channel detection uses); github: the GitHub Releases API (rate-limited, cached); selfcontained:
// the configured latest.json feed. Every external call is guarded → offline/missing-tool/API-error
// reports no-update, never throws. ISystemVersionService caches the result behind a TTL so /info
// stays an in-memory read; the github request builder resolves the per-arch .deb URL + sha256 for
// the Apply path.
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.IProcessRunner>(
    Packet.Node.Core.SelfUpdate.SystemProcessRunner.Instance);
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.IGitHubReleaseClient>(sp =>
    new Packet.Node.Core.SelfUpdate.GitHubReleaseClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.ISelfContainedFeedClient>(sp =>
    new Packet.Node.Core.SelfUpdate.SelfContainedFeedClient(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>(),
        // The self-contained feed URL is configuration (slice 2c surfaces it in node config; until
        // then it's the PDN_UPDATE_FEED_URL env seam). No feed configured → the check is a no-op
        // (the client short-circuits to null with zero network calls), which is the correct default
        // for the deb channels that don't consume the feed at all.
        feedUrl: Environment.GetEnvironmentVariable("PDN_UPDATE_FEED_URL") is { Length: > 0 } f
                 && Uri.TryCreate(f.EndsWith('/') ? f : f + "/", UriKind.Absolute, out var fu) ? fu : null));
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.IUpdateAvailabilityProbe>(sp =>
    new Packet.Node.Core.SelfUpdate.ChannelUpdateAvailabilityProbe(
        sp.GetRequiredService<Packet.Node.Core.SelfUpdate.IInstallChannelProvider>(),
        sp.GetRequiredService<Packet.Node.Core.SelfUpdate.IProcessRunner>(),
        sp.GetRequiredService<Packet.Node.Core.SelfUpdate.IGitHubReleaseClient>(),
        sp.GetRequiredService<Packet.Node.Core.SelfUpdate.ISelfContainedFeedClient>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.ISystemVersionService>(sp =>
    new Packet.Node.Core.SelfUpdate.SystemVersionService(
        Packet.Node.Api.PdnSystemApi.NodeVersion,
        sp.GetRequiredService<Packet.Node.Core.SelfUpdate.IUpdateAvailabilityProbe>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Packet.Node.Core.SelfUpdate.GithubUpdateRequestBuilder>(sp =>
    new Packet.Node.Core.SelfUpdate.GithubUpdateRequestBuilder(
        sp.GetRequiredService<Packet.Node.Core.SelfUpdate.IGitHubReleaseClient>(),
        sp.GetRequiredService<ILoggerFactory>()));
// The app-package catalog (docs/app-packages.md): discovers pdn-app.yaml packages under the
// package roots and merges the owner's apps: overrides. A pure, cheap, side-effect-free scan
// per call — the gateway, the packages API, and the hosted service consume it directly.
builder.Services.AddSingleton<Packet.Node.Core.Applications.Packages.IAppPackageCatalog,
    Packet.Node.Core.Applications.Packages.AppPackageCatalog>();
// The app-service supervisor (docs/app-packages.md § Lifecycle): owns the daemon of every
// enabled package whose manifest declares a pdn-managed service: block — start/stop/backoff/
// crash-loop breaker. NodeHostedService picks both of these up via its optional ctor params
// and reconciles at startup + on every config apply; the packages API drives RestartAsync.
// An explicit factory (the ctor's trailing TimeSpan? tuning knobs are test-only).
builder.Services.AddSingleton<Packet.Node.Core.Applications.Packages.IAppServiceSupervisor>(sp =>
    new Packet.Node.Core.Applications.Packages.AppServiceSupervisor(
        sp.GetRequiredService<IConfigProvider>(),
        sp.GetRequiredService<Packet.Node.Core.Applications.Packages.IAppPackageCatalog>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>()));
// The app catalog (docs/app-catalog.md): the curated index of AVAILABLE apps
// (/usr/share/packetnet/catalog/apps.yaml) and the installer that fetches + sha256-verifies a
// pinned artifact and lays down a discoverable package the supervisor then picks up unchanged.
// Slice 6a is plumbing only — no API/UI surface yet (6b adds that). IHttpClientFactory backs
// the production fetcher; the deb extractor shells dpkg-deb -x.
builder.Services.AddHttpClient();
builder.Services.AddSingleton<Packet.Node.Core.Applications.Catalog.IAppCatalog>(sp =>
    new Packet.Node.Core.Applications.Catalog.EmbeddedAppCatalog(
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Packet.Node.Core.Applications.Catalog.IArtifactFetcher>(sp =>
    new Packet.Node.Core.Applications.Catalog.HttpArtifactFetcher(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(),
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Packet.Node.Core.Applications.Catalog.IDebExtractor>(sp =>
    new Packet.Node.Core.Applications.Catalog.DpkgDebExtractor(
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<Packet.Node.Core.Applications.Catalog.IAppInstaller>(sp =>
    new Packet.Node.Core.Applications.Catalog.AppInstaller(
        sp.GetRequiredService<Packet.Node.Core.Applications.Catalog.IArtifactFetcher>(),
        sp.GetRequiredService<Packet.Node.Core.Applications.Catalog.IDebExtractor>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>()));
// Holds operator-initiated connect-out sessions as interactive consoles (the web
// Sessions drawer reads their output over SSE + types into them). Disposed on host
// shutdown (IAsyncDisposable) → each adopted connection gets a clean DISC.
builder.Services.AddSingleton<SysopConsoleManager>();
// The ID-beacon service: a singleton over the live config + clock. The hosted service
// injects it (and passes it to the supervisor, which attaches it per port) — it is
// inert until a port whose effective beacon is enabled comes up (default-off).
builder.Services.AddSingleton<BeaconService>();
// Register the hosted service as a singleton AND as the hosted service, so the
// control-API endpoint handlers can inject it and read its live Supervisor /
// NetRom handles (the read API projects the node's state from these).
builder.Services.AddSingleton<NodeHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeHostedService>());

// The persistent traffic log (default-ON behind traffic.enabled): every traced AX.25
// frame, on every port, written to a SEPARATE SQLite db (default traffic.db beside
// pdn.db — never pdn.db itself, so a huge/corrupt frame log can never threaten node
// state). The writer subscribes to the same NodeTelemetry stream the SSE monitor
// rides (no second decode path) through a bounded queue, so a slow disk drops log
// rows (counted, surfaced in /status) rather than ever back-pressuring the radio
// path. enabled/path apply at startup (restart-applies — see the config template);
// retentionDays/maxMb are re-read live at each prune. The store degrades internally
// on fault, like every other store: the node always boots.
if (configProvider.Current.Traffic.Enabled)
{
    var trafficStore = new SqliteTrafficStore(
        ResolveTrafficDbPath(configProvider.Current.Traffic, dbPath),
        bootstrapLoggers.CreateLogger<SqliteTrafficStore>());
    builder.Services.AddSingleton(trafficStore);
    builder.Services.AddSingleton(sp => new TrafficLogService(
        sp.GetRequiredService<NodeHostedService>().Telemetry,
        trafficStore,
        sp.GetRequiredService<IConfigProvider>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<TrafficLogService>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TrafficLogService>());
}

// OARC network-map reporting (#459, default-off behind oarc.enabled): a background reporter that
// pushes this node's telemetry (node up/status/down, L2 links, L4 circuits, opt-in per-frame traces)
// to the OARC collector so the node appears on the packet-network map. Outbound only; open ingest (no
// secret). Registered UNCONDITIONALLY — it self-gates on oarc.enabled + the per-category toggles and
// handles a master edge, so a hot-enable needs no restart. The state source reads the live
// Supervisor/NetRom/Telemetry off NodeHostedService; the ingest client is a thin HttpClient layer.
// See docs/oarc-reporting-design.md.
builder.Services.AddSingleton<IOarcStateSource>(sp => new NodeOarcStateSource(
    sp.GetRequiredService<NodeHostedService>(),
    sp.GetRequiredService<IConfigProvider>(),
    sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHttpClient(nameof(OarcIngestClient), c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<IOarcIngestClient>(sp => new OarcIngestClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OarcIngestClient)),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<OarcIngestClient>()));
builder.Services.AddSingleton(sp => new OarcReporter(
    sp.GetRequiredService<IConfigProvider>(),
    sp.GetRequiredService<IOarcStateSource>(),
    sp.GetRequiredService<IOarcIngestClient>(),
    sp.GetRequiredService<NodeHostedService>().Telemetry,
    sp.GetRequiredService<TimeProvider>(),
    NodeCommandService.Version,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<OarcReporter>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<OarcReporter>());

// RHPv2 server (the app platform's network plane, default-off behind rhp.enabled): the
// JSON-over-TCP host API bridged onto the running supervisor. See docs/rhp2-server.md.
builder.Services.AddSingleton<Packet.Rhp2.Server.IRhpGateway, Packet.Node.Rhp.SupervisorRhpGateway>();
builder.Services.AddSingleton<Packet.Rhp2.Server.RhpServerHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Packet.Rhp2.Server.RhpServerHostedService>());

// The embedded Tailscale tsnet sidecar (network-access.md § The sidecar, default-off behind
// tailscale.enabled): INFRA, not an app. The status holder is a singleton the read API
// (GET /api/v1/system/tailscale) projects and the web panel polls; the hosted service launches
// + supervises the packetnet-tsnet child when enabled (same spawn/teardown/backoff discipline
// as the app-service supervisor), parses its JSON status lines into the holder, and reconciles
// on every config change. A default node enables nothing, so the child never runs (and a host
// without the binary is fine — the default config is disabled). The binary path comes from
// PDN_TSNET_BIN or the packaged default; tuning knobs (backoff/grace) keep production defaults.
builder.Services.AddSingleton<Packet.Node.Core.Tailscale.ITailscaleStatus,
    Packet.Node.Core.Tailscale.TailscaleStatusHolder>();
builder.Services.AddSingleton(sp =>
    new Packet.Node.Core.Tailscale.TailscaleSidecarHostedService(
        sp.GetRequiredService<IConfigProvider>(),
        sp.GetRequiredService<Packet.Node.Core.Tailscale.ITailscaleStatus>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>(),
        // The app-package catalog feeds the sidecar the enabled apps' declared tailnet forwards
        // (the --forwards-file); they ride the spawn fingerprint, so enabling/disabling a
        // forward-declaring app restarts the sidecar. See docs/network-access.md.
        packages: sp.GetRequiredService<Packet.Node.Core.Applications.Packages.IAppPackageCatalog>()));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Packet.Node.Core.Tailscale.TailscaleSidecarHostedService>());

// LAN discovery: advertise _pdn._tcp over mDNS (management.mdns, default-off) so the pdn
// mobile app finds the node on the local network. Infra, not an app: it supervises an
// avahi-publish child, self-gates on enabled + a non-loopback web bind, carries the node
// callsign as the `cs` TXT + service instance name (so multiple nodes on one LAN stay
// distinguishable), and degrades to dormant where avahi-publish is absent. See
// docs/plan.md (mDNS) + Packet.Node.Core.Discovery.
builder.Services.AddSingleton(sp =>
    new Packet.Node.Core.Discovery.MdnsAdvertiserHostedService(
        sp.GetRequiredService<IConfigProvider>(),
        sp.GetRequiredService<TimeProvider>(),
        NodeCommandService.Version,
        sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<Packet.Node.Core.Discovery.MdnsAdvertiserHostedService>());

// Phase 8: the in-process MCP tool surface + live backend. The HTTP transport is
// mounted below (MapPdnMcp) only when mcp.sse.enabled; stdio is the `pdn mcp`
// subcommand. Registering the services is harmless when unused. See docs/mcp-design.md.
builder.Services.AddPdnMcp();

// Bind Kestrel from the node config's management.http section, plus an optional TLS
// listener (management.https). HTTPS is opt-in; the plain HTTP listener is unchanged.
var management = configProvider.Current.Management;
var http = management.Http;
var https = management.Https;

// Resolve the HTTPS cert ONCE, before binding. A null result (TLS off, or a cert that
// couldn't be loaded/generated — logged by the provider) just means we skip the HTTPS
// endpoint; HTTP still serves, so a TLS misconfig never takes the node down.
System.Security.Cryptography.X509Certificates.X509Certificate2? httpsCert = null;
if (https.Enabled)
{
    // Persist a generated self-signed cert beside the db (the writable StateDirectory
    // on the packaged node), e.g. /var/lib/packetnet/certs/server.pfx.
    var certDir = Path.GetDirectoryName(dbPath) is { Length: > 0 } d ? d : ".";
    var selfSignedPath = Path.Combine(certDir, "certs", "server.pfx");
    var bindIp = IPAddress.TryParse(https.Bind, out var hb) ? hb : IPAddress.Loopback;
    var sanIps = new List<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
    if (!Equals(bindIp, IPAddress.Any) && !Equals(bindIp, IPAddress.IPv6Any) && !sanIps.Contains(bindIp))
    {
        sanIps.Add(bindIp);
    }
    var commonName = string.IsNullOrWhiteSpace(configProvider.Current.Identity.Callsign)
        ? "packetnet"
        : configProvider.Current.Identity.Callsign;
    httpsCert = Packet.Node.Core.TlsCertificateProvider.Resolve(
        https, selfSignedPath, commonName,
        sanDnsNames: ["localhost", Environment.MachineName],
        sanIpAddresses: sanIps,
        clock: TimeProvider.System,
        logger: bootstrapLoggers.CreateLogger("Packet.Node.Tls"));
}

builder.WebHost.ConfigureKestrel(options =>
{
    var address = IPAddress.TryParse(http.Bind, out var ip) ? ip : IPAddress.Loopback;
    options.Listen(address, http.Port);

    if (httpsCert is not null)
    {
        var httpsAddress = IPAddress.TryParse(https.Bind, out var hip) ? hip : IPAddress.Loopback;
        options.Listen(httpsAddress, https.Port, listenOptions => listenOptions.UseHttps(httpsCert));
    }
});

// Forwarded headers — for the loopback TLS edge (the embedded Tailscale tsnet sidecar,
// network-access.md). The sidecar terminates HTTPS and reverse-proxies to pdn's loopback
// HTTP; honouring X-Forwarded-Proto/Host/For makes Request.IsHttps/Scheme/Host reflect the
// PUBLIC https origin, so PdnAppGateway's pdn_at cookie Secure flag and any request-derived
// WebAuthn origin are correct. Trust the headers ONLY from a loopback proxy: clear the
// default known proxies/networks then add the two loopback addresses, so an arbitrary remote
// client cannot spoof its scheme/host (anti-spoof). With no proxy in front there are no such
// headers to read → this is a no-op, safe to always enable.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Add(IPAddress.Loopback);
    o.KnownProxies.Add(IPAddress.IPv6Loopback);
});

var app = builder.Build();

// UseForwardedHeaders runs FIRST — before auth, static files, and every endpoint — so the
// rewritten scheme/host is in place for the whole pipeline. Trusted from loopback only
// (configured above); a no-op when no proxy sends the headers.
app.UseForwardedHeaders();

// Authentication + authorization run for every request. With auth disabled they are
// effectively inert (no [Authorize] challenges, and the scope policies pass through via
// ScopeRequirementHandler), so the default-off pipeline behaves exactly as before.
app.UseAuthentication();
app.UseAuthorization();

// Unauthenticated liveness probe. ALWAYS open — never gated.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Auth foundation endpoints. /setup/state, /auth/login, /setup are ALWAYS open (the
// bootstrap path — you can't present a token before you have an account); the /users
// group is returned so we can gate it `admin` (the gate is a no-op when auth is off).
var usersGroup = app.MapPdnAuthApi();
usersGroup.RequireAuthorization(PdnAuthPolicies.Admin);

// WebAuthn / passkeys (auth part 3). The assert (passwordless-login) endpoints are
// ALWAYS open — a login can't carry a bearer token. The register + credential-management
// group is gated `read`: a logged-in user enrols/manages a passkey for THEMSELVES (the
// username comes from the authenticated principal, never the body), and read is the floor
// a self-service "manage my own login" action sits at — any authenticated user may add a
// passkey. The gate is a no-op when auth is off (and register/complete then 409s, since
// there is no authenticated "self" to enrol for). Mapped before the catch-all so the
// specific /api/v1/* routes win.
var webAuthnGroup = app.MapPdnWebAuthnApi();
webAuthnGroup.RequireAuthorization(PdnAuthPolicies.Read);

// Over-RF sysop-code (TOTP) enrolment (auth part 4). Self-service: a logged-in user enrols
// / inspects / removes the rolling code for THEMSELVES (the username comes from the
// authenticated principal, never the body), so the group is gated `read` — the same floor
// as the passkey register group. The gate is a no-op when auth is off (and enroll then 409s,
// since there is no authenticated "self" to enrol for). Mapped before the catch-all so the
// specific /api/v1/* routes win.
var totpGroup = app.MapPdnTotpApi();
totpGroup.RequireAuthorization(PdnAuthPolicies.Read);

// Slice 3 control API (read endpoints). Mapped BEFORE the SPA fallback so /api/*
// and /healthz win; everything else falls through to index.html for the React
// client router. (Auth is a later step — these are read-only and the node binds
// 127.0.0.1 by default. The live SSE feed for the monitor is step 1b.)
app.MapPdnReadApi();

// Prometheus exporter (GET /metrics, #457): the same listener, the same Read scope
// gate as the REST read surface (so unauthenticated when management.auth.enabled is
// off — localhost-scrape posture — and read-scoped once on). Hand-rolled exposition
// text derived from the SAME live counters /api/v1/links projects; bounded label
// cardinality (per-port only). See docs/observability.md.
app.MapPdnMetrics();

// Slice 3 step 1b: the live SSE frame feed the web monitor's EventSource
// consumes (GET /api/v1/events). Mapped after the read API and before the
// catch-all; the specific route wins over /api/{**rest} regardless of order.
app.MapPdnEvents();

// Slice 3 step 2: the write-side config API (PUT /config + /config/raw + the
// raw-YAML GET) the web editor persists edits through. Mapped after the read API
// and before the catch-all; the specific routes win over /api/{**rest} regardless
// of order. (Auth is a later step — unauthenticated, node binds 127.0.0.1.)
app.MapPdnConfigApi();

// Slice 3 step 3: the port-management API (POST/PUT/DELETE /ports + the
// /ports/{id}/lifecycle up/down) the web Ports screen mutates ports through. Every
// change is a config edit persisted through the same write seam as PUT /config.
// Mapped after the config API and before the catch-all; the specific routes win over
// /api/{**rest} regardless of order. (Auth is a later step — unauthenticated, node
// binds 127.0.0.1.)
app.MapPdnPortsApi();

// Radio-control read surface: per-port radio status/health (GET /api/v1/radios,
// GET /api/v1/ports/{id}/radio) + a bus discovery scan (GET /api/v1/radios/scan). Read-scoped,
// projecting the live supervisor; the scan opens serial ports transiently but is bounded. Mapped
// before the catch-all; specific routes win. See PdnRadiosApi.
app.MapPdnRadiosApi();

// Slice 3 step 4: the direct-supervisor session actions + ping — connect-out
// (POST /sessions), disconnect (DELETE /sessions/{id}), send-line
// (POST /sessions/{id}/send), and the connectionless TEST /ping (deferred 501).
// These run under the host's exclusive gate so a web action never races a config
// reconcile. Mapped after the port API and before the catch-all; the specific routes
// win over /api/{**rest} regardless of order. (Auth is a later step — unauthenticated,
// node binds 127.0.0.1.)
app.MapPdnSessionsApi();

// The per-peer AX.25 capability-cache action API (operate-gated): DELETE /api/v1/capabilities/{id}
// forgets one learned (port, peer) record so the next dial re-probes it. The READ side
// (GET /api/v1/capabilities) is part of MapPdnReadApi's read group above. Mapped beside the
// session actions and before the catch-all; the specific route wins over /api/{**rest}. The
// forget is audited (clear_capability). See PdnCapabilitiesApi.
app.MapPdnCapabilitiesApi();

// The browser node command console (admin-gated): POST /api/v1/console opens a new node
// command shell over an in-process loopback bridge (the same NodeCommandService the telnet
// console runs), adopted into the SysopConsoleManager so the existing SSE fan-out + input
// plumbing is reused; GET /console/{id}/stream is the SSE output, POST /console/{id}/input
// feeds keystrokes, DELETE /console/{id} closes it. Mapped before the catch-all; specific
// routes win. The open is audited. See PdnConsoleApi.
app.MapPdnConsoleApi();

// App-gateway (the human plane, app platform Slice 3): the launcher feed
// (GET /api/v1/apps) + the reverse proxy (/apps/{id}/* → a registered app's loopback web
// server, with the authenticated identity injected). Mapped before the SPA fallback so the
// /apps/* proxy route wins over index.html. See PdnAppGateway + docs/app-gateway.md.
app.MapPdnAppGateway();

// App-packages management (app platform Slice 5): the admin inventory
// (GET /api/v1/apps/packages), the enable/disable trust toggle (a config write of the apps:
// override list), and the managed-service restart action. Mapped beside the gateway, before
// the catch-all. See PdnAppPackagesApi + docs/app-packages.md.
app.MapPdnAppPackagesApi();

// "Available apps" (app catalog Slice 6b): the vetted catalog left-joined with installed state
// (GET /api/v1/apps/available) + one-click install (POST /api/v1/apps/available/{id}/install,
// admin, audited). The sibling of the package manager above — the menu you install FROM, while
// uninstall/upload live on the packages group. Mapped before the catch-all. See
// PdnAvailableAppsApi + docs/app-catalog.md.
app.MapPdnAvailableAppsApi();

// Phase 7 self-update: GET /api/v1/system/info (read — version + install channel) and
// POST /api/v1/system/update (admin, audited — channel-aware: a targeted apt upgrade via
// the privileged packetnet-update.service on the apt channel). See PdnSystemApi +
// docs/node-self-update-design.md. Mapped before the catch-all; specific routes win.
app.MapPdnSystemApi();

// The audit-log read API (GET /api/v1/audit, admin-gated): recent privileged-action
// records from the node-wide audit log (pdn.db). Mapped before the catch-all. See
// PdnAuditApi + §6.
app.MapPdnAuditApi();

// MCP management (Phase 8): POST /api/v1/mcp/token mints the long-lived MCP bearer
// token an operator pastes into a Claude Code config to reach /mcp over LAN/Tailscale.
// Admin-gated + audited. See PdnMcpApi + docs/mcp-design.md.
app.MapPdnMcpApi();

// MCP OAuth 2.1 authorization server (the hosted claude.ai connector path). Every route
// is inert (404) unless mcp.oauth.enabled — default-off. Discovery + DCR + authorize/consent
// + token + revoke. Security-critical; review before enabling. See PdnOauthApi + docs/mcp-oauth-design.md.
app.MapPdnOauthApi();

// Phase 8: the in-process MCP server's Streamable-HTTP transport, mounted at the
// configured path (default /mcp) on the web listener when mcp.sse.enabled, gated
// `read`. A no-op when MCP/SSE is off (the default). See McpRegistration + docs/mcp-design.md.
app.MapPdnMcp();

// An unknown /api/* path returns 404 — it must NOT fall through to the SPA
// index.html below (the catch-all is less specific than the real /api/v1/*
// routes, so those still win).
app.Map("/api/{**rest}", () => Results.NotFound());

// Serve the built web UI (web/packetnet-ui → wwwroot) + SPA client-side routing:
// any other unmatched, non-file route returns index.html so the React router
// can handle it (deep links like /monitor, /ports).
app.UseDefaultFiles();
// SPA caching: index.html must always be revalidated so a deploy's new asset hashes are picked
// up immediately (the recurring "updated but UI unchanged until a hard-refresh" trap — a stale
// cached index.html keeps pointing at the previous hashed bundle). The hashed /assets/* are
// content-addressed, so they're safe to cache immutably forever.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }
    },
});
// Deep-link fallback (e.g. /monitor, /apps) also returns index.html — keep it no-cache too.
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate",
});

app.Run();
return 0;   // top-level program returns an int (the `pdn config` subcommand returns a code)

static string ResolveConfigPath(string[] args)
{
    // --config <path> wins, then PACKETNET_CONFIG, then a sensible default in the
    // working directory.
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--config" or "-c")
        {
            return args[i + 1];
        }
    }
    var env = Environment.GetEnvironmentVariable("PACKETNET_CONFIG");
    return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Directory.GetCurrentDirectory(), "packetnet.yaml");
}

static string ResolveDbPath(string[] args)
{
    // --db <path> wins, then PACKETNET_DB, then pdn.db in the working directory —
    // which on the packaged node is the writable StateDirectory (/var/lib/packetnet).
    for (int i = 0; i < args.Length - 1; i++)
    {
        if (args[i] is "--db")
        {
            return args[i + 1];
        }
    }
    var env = Environment.GetEnvironmentVariable("PACKETNET_DB");
    return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Directory.GetCurrentDirectory(), "pdn.db");
}

static string? ResolveSeedPath()
{
    // PACKETNET_CONFIG_SEED: an explicit seed-file path consulted ONLY on first boot
    // when neither a DB row nor the --config YAML exists — so a headless image can seed
    // a full config without touching /etc. Null when unset (the common case).
    var env = Environment.GetEnvironmentVariable("PACKETNET_CONFIG_SEED");
    return string.IsNullOrWhiteSpace(env) ? null : env;
}

static string ResolveBootstrapTemplatePath()
{
    // The packaged pristine template the first-boot seed reads when there is no DB row,
    // no --config YAML, and no PACKETNET_CONFIG_SEED. build-deb.sh stages it here; the
    // in-code NodeConfigTemplate is the ultimate fallback if even this is missing.
    var env = Environment.GetEnvironmentVariable("PACKETNET_CONFIG_TEMPLATE");
    return string.IsNullOrWhiteSpace(env)
        ? "/usr/share/packetnet/packetnet.yaml.example"
        : env;
}

static string ResolveTrafficDbPath(Packet.Node.Core.Configuration.TrafficConfig traffic, string dbPath)
{
    // traffic.path wins when set (resolved against the CWD like every other path);
    // default = traffic.db beside pdn.db — the same writable StateDirectory
    // (/var/lib/packetnet) on the packaged node, which packaging/postinst already
    // creates. Never pdn.db itself: the log is a separate database by design.
    if (!string.IsNullOrWhiteSpace(traffic.Path))
    {
        return traffic.Path;
    }
    var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
    return string.IsNullOrEmpty(dir) ? "traffic.db" : Path.Combine(dir, "traffic.db");
}

/// <summary>Exposed so the WebApplicationFactory-based host test can boot this
/// exact composition root.</summary>
public partial class Program;
