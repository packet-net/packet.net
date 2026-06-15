using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Packet.Node.Core.Applications.Packages;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Packet.Node.Api;

/// <summary>
/// The app-gateway — the app platform's <b>human plane</b> (<c>docs/app-gateway.md</c>). pdn is
/// a broker: it renders a launcher from each app's <c>ui</c> manifest (<c>GET /api/v1/apps</c>)
/// and reverse-proxies <c>/apps/{id}/*</c> to the app's own loopback web server, injecting the
/// authenticated identity. It never imports an app's code.
/// </summary>
/// <remarks>
/// <para><b>Auth.</b> The proxy route requires the <c>read</c> scope (so only a panel user
/// reaches an app's UI; passes through when auth is off). Browser navigations can't set an
/// <c>Authorization</c> header, so the JWT bearer pipeline also reads the token from the
/// <see cref="CookieName"/> cookie for <c>/apps/*</c> paths (set at login/refresh) — see
/// <c>Program.cs</c>'s <c>OnMessageReceived</c>.</para>
/// <para><b>Identity injection.</b> The transformer strips any client-supplied <c>X-Pdn-*</c>
/// header, then sets <c>X-Pdn-User</c>/<c>X-Pdn-Scope</c>/<c>X-Pdn-Gateway</c> from the validated
/// principal. The upstream trusts these because it binds loopback and only pdn reaches it (v1
/// trust model — unsigned; see <c>docs/app-gateway.md</c> §Trust).</para>
/// </remarks>
public static class PdnAppGateway
{
    /// <summary>The HttpOnly cookie carrying the access token for browser navigations to
    /// <c>/apps/*</c> (the panel's fetch API still uses the bearer header).</summary>
    public const string CookieName = "pdn_at";

    private const string UserHeader = "X-Pdn-User";
    private const string ScopeHeader = "X-Pdn-Scope";
    private const string GatewayHeader = "X-Pdn-Gateway";

    /// <summary>The standard reverse-proxy mount-point header (<c>/apps/{id}</c>): a
    /// server-rendered app prefixes its absolute URLs with this so links, form actions and
    /// redirect Locations stay inside the proxied prefix. Anti-spoofed like the X-Pdn-* set.</summary>
    private const string ForwardedPrefixHeader = "X-Forwarded-Prefix";

    // A proxy-tuned client: no auto-redirect (the app's redirects are its own), no cookie
    // container (we manage identity via headers), no decompression (pass bytes through).
    private static readonly HttpMessageInvoker ProxyClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ActivityHeadersPropagator = null,
    });

    private static readonly ForwarderRequestConfig ForwardConfig = new() { ActivityTimeout = TimeSpan.FromSeconds(100) };
    private static readonly AppGatewayTransformer Transformer = new();
    private static readonly string[] ProxyMethods = ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];

    /// <summary>Map the launcher feed + the reverse-proxy route. Call before the SPA fallback.</summary>
    public static void MapPdnAppGateway(this WebApplication app)
    {
        // Launcher feed: the UNION of inline applications and enabled, error-free discovered
        // packages that expose a UI (read-gated, like the other reads). Inline first — on an
        // id collision inline wins (the catalog errors that case, so the package side is
        // never enabled; the explicit skip below is belt-and-braces).
        app.MapGet("/api/v1/apps", (IConfigProvider config, IAppPackageCatalog catalog, IServiceProvider services) =>
        {
            // The supervisor (optional, like the packages API) supplies the live service state so
            // the panel's nav can render a not-running warning badge from this ONE fetch. An
            // inline app has no service block, so its state is null (nothing to warn about).
            var supervisor = services.GetService<IAppServiceSupervisor>();

            var tiles = config.Current.Applications
                .Where(a => a.Enabled && a.Ui is not null)
                .Select(a => new AppTile(a.Id, a.Ui!.Name ?? a.Id, a.Ui.Icon, $"/apps/{a.Id}/", UiModeName(a.Ui.Mode), State: null))
                .ToList();

            var inlineIds = new HashSet<string>(
                config.Current.Applications.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            tiles.AddRange(catalog.Discover(config.Current)
                .Where(p => p.Enabled && p.Error is null && p.Manifest?.Ui is not null
                    && !inlineIds.Contains(p.Id))
                .Select(p => new AppTile(
                    p.Id,
                    p.Manifest!.Ui!.Name ?? p.Manifest.Name ?? p.Id,
                    p.Manifest.Ui.Icon,
                    $"/apps/{p.Id}/",
                    UiModeName(p.Manifest.Ui.Mode),
                    ServiceState(p, supervisor))));
            return Results.Ok(tiles);
        }).RequireAuthorization(PdnAuthPolicies.Read);

        // Apps are reached at /apps/{id}/ (trailing slash — the launcher always emits it, and the
        // app's relative URLs resolve against it). The catch-all below matches that (rest = "").
        // The catch-all ALSO matches the bare `/apps/{id}` (no trailing slash): the `/` before a
        // `{**rest}` catch-all is optional, so `/apps/{id}` binds rest = "". We do NOT proxy that
        // form — it's the SPA's in-panel route for a slot/embedded app (the left-nav links there
        // with a react-router <Link>, and <AppFrame> renders the app inside pdn chrome via an
        // iframe of `/apps/{id}/`). A hard reload of `/apps/{id}` must therefore boot the SPA
        // shell (so the embedded experience is restored), exactly like any other deep link — NOT
        // proxy the raw app and lose pdn's chrome. We serve index.html for the no-slash form
        // inside the handler below (rather than a separate route) so it can't shadow the proxy for
        // the with-slash forms `/apps/{id}/` and `/apps/{id}/...`, which still forward as before.

        // The reverse proxy. Read-gated; the token comes from the bearer header or the pdn_at
        // cookie (Program.cs OnMessageReceived). Resolve the app by id — inline entries first
        // (inline wins on a collision), then enabled error-free packages with a ui: block —
        // and forward to its upstream. The same transformer serves both sources, so the
        // identity-injection/anti-spoof behaviour is identical for package-backed upstreams.
        // All methods (MapMethods, not the ambiguous all-methods app.Map overload).
        app.MapMethods("/apps/{id}/{**rest}", ProxyMethods, async (HttpContext context, IConfigProvider config, IAppPackageCatalog catalog, IHttpForwarder forwarder, IWebHostEnvironment env) =>
        {
            var id = context.Request.RouteValues["id"] as string;

            // The bare no-trailing-slash form `/apps/{id}` (rest empty, path has no trailing
            // slash) is the SPA's embedded route, not a proxy target — serve the SPA shell so a
            // reload restores the in-chrome experience instead of stranding the user on the raw
            // proxied app. The with-slash forms (rest non-empty, or a trailing slash) proxy.
            var rest = context.Request.RouteValues["rest"] as string;
            if (string.IsNullOrEmpty(rest) && context.Request.Path.Value?.EndsWith('/') == false)
            {
                var index = env.WebRootFileProvider.GetFileInfo("index.html");
                if (index.Exists)
                {
                    // Match the SPA fallback's no-cache policy so a deploy's new asset hashes are
                    // picked up immediately (Program.cs MapFallbackToFile).
                    context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.SendFileAsync(index, context.RequestAborted).ConfigureAwait(false);
                    return;
                }
                // No built SPA present (shouldn't happen in a real deploy) — fall through and let
                // the proxy resolution below answer (it 404s an unknown id).
            }

            var upstream = config.Current.Applications
                .FirstOrDefault(a => a.Enabled && a.Ui is not null && string.Equals(a.Id, id, StringComparison.Ordinal))
                ?.Ui!.Upstream;
            upstream ??= catalog.Discover(config.Current)
                .FirstOrDefault(p => p.Enabled && p.Error is null && p.Manifest?.Ui is not null
                    && string.Equals(p.Id, id, StringComparison.Ordinal))
                ?.Manifest!.Ui!.Upstream;
            if (upstream is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await forwarder.SendAsync(context, upstream, ProxyClient, ForwardConfig, Transformer)
                .ConfigureAwait(false);
        }).RequireAuthorization(PdnAuthPolicies.Read);
    }

    /// <summary>Set the gateway cookie (the access token) so a browser navigation to
    /// <c>/apps/*</c> authenticates. HttpOnly + SameSite=Strict + Secure (on HTTPS), scoped to
    /// <c>/apps</c> so it isn't sent on every API/SPA request, expiring with the token.</summary>
    public static void SetGatewayCookie(HttpContext context, string token, DateTimeOffset expiresAt)
    {
        context.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/apps",
            Expires = expiresAt,
        });
    }

    /// <summary>Clear the gateway cookie (logout).</summary>
    public static void ClearGatewayCookie(HttpContext context)
    {
        context.Response.Cookies.Append(CookieName, string.Empty, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/apps",
            Expires = DateTimeOffset.UnixEpoch,
        });
    }

    /// <summary>A launcher tile (the <c>/api/v1/apps</c> shape — camelCase on the wire). The
    /// feed lists only enabled, web-capable apps. <see cref="UiMode"/> tells the panel how to
    /// open the app from its nav entry — <c>standalone</c> (a full navigation to <see cref="Url"/>,
    /// the default), or <c>embedded</c>/<c>slot</c> (an in-panel iframe; <c>slot</c> appends
    /// <c>?pdn_embed=1</c> so the app renders chrome-less). <see cref="State"/> is the live
    /// supervisor state — an <see cref="AppServiceState"/> name (<c>Stopped</c>|<c>Starting</c>|
    /// <c>Running</c>|<c>Backoff</c>|<c>Faulted</c>|<c>External</c>) — or null when the app has
    /// no pdn-managed service to watch (an inline app, or a package with no <c>service:</c>
    /// block). The panel's nav warns when an enabled tile's state is one the supervisor should
    /// have driven to Running but hasn't (Stopped/Backoff/Faulted), sourcing the badge from this
    /// single fetch rather than a second call to the packages inventory.</summary>
    public sealed record AppTile(string Id, string Name, string? Icon, string Url, string UiMode, string? State);

    /// <summary>The wire spelling of an <see cref="AppUiMode"/> on the launcher feed — the
    /// lowercase contract name (<c>standalone</c> | <c>embedded</c> | <c>slot</c>) the panel nav
    /// branches on.</summary>
    private static string UiModeName(AppUiMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>The live service state for a launcher tile, derived exactly as
    /// <see cref="PdnAppPackagesApi"/>'s inventory does: null when the package declares no
    /// <c>service:</c> block (nothing to run); <c>External</c> for an owner-managed daemon pdn
    /// never tracks; otherwise the supervisor's reported <see cref="AppServiceState"/> name,
    /// falling back to <c>Stopped</c> when the supervisor has no status yet (or isn't wired).</summary>
    private static string? ServiceState(DiscoveredAppPackage package, IAppServiceSupervisor? supervisor)
    {
        var managed = package.Manifest?.Service;
        if (managed is null)
        {
            return null;   // no service → nothing to run, nothing to warn about
        }
        if (managed.Managed == AppServiceManaged.External)
        {
            return nameof(AppServiceState.External);
        }

        var status = supervisor?.Statuses.FirstOrDefault(s =>
            string.Equals(s.Id, package.Id, StringComparison.OrdinalIgnoreCase));
        return status is not null
            ? status.State.ToString()
            : nameof(AppServiceState.Stopped);   // supervisor not wired / no status yet
    }

    /// <summary>
    /// The authenticated username for the identity header, robust to inbound-claim mapping that
    /// can surface the subject as <see cref="System.Security.Claims.ClaimsIdentity.Name"/>, the
    /// raw <c>sub</c> claim, or the mapped <c>NameIdentifier</c> (the same fallback the auth APIs
    /// use). Empty when the principal is unauthenticated (anonymous / auth-off).
    /// </summary>
    internal static string AuthenticatedUsername(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return string.Empty;
        }
        return principal.Identity!.Name
            ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? string.Empty;
    }

    // Rebases the path (strips /apps/{id}), strips any client-supplied identity headers, and
    // injects the authenticated identity. A singleton — all per-request state is on the context.
    internal sealed class AppGatewayTransformer : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(
            HttpContext context, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            // Default copy (method, headers minus hop-by-hop, removes Host).
            await base.TransformRequestAsync(context, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);

            // Rebase: the app sees the path with the /apps/{id} prefix stripped (the catch-all
            // 'rest' route value), so it is mounted at its own root.
            var rest = context.Request.RouteValues["rest"] as string ?? string.Empty;
            var prefix = destinationPrefix.TrimEnd('/');
            proxyRequest.RequestUri = new Uri($"{prefix}/{rest}{context.Request.QueryString}");
            // Derive the Host from the (rebased) destination URI rather than forwarding pdn's
            // own Host — the upstream is the app's loopback server, not pdn. (Clearing it makes
            // HttpClient set Host from RequestUri.)
            proxyRequest.Headers.Host = null;

            // Anti-spoof: never let a client-supplied X-Pdn-* (or forwarded-prefix) through —
            // we set them ourselves.
            proxyRequest.Headers.Remove(UserHeader);
            proxyRequest.Headers.Remove(ScopeHeader);
            proxyRequest.Headers.Remove(GatewayHeader);
            proxyRequest.Headers.Remove(ForwardedPrefixHeader);

            var user = AuthenticatedUsername(context.User);
            var scope = context.User?.FindFirst(AuthScopes.ScopeClaim)?.Value ?? string.Empty;

            proxyRequest.Headers.TryAddWithoutValidation(UserHeader, user);
            proxyRequest.Headers.TryAddWithoutValidation(ScopeHeader, scope);
            proxyRequest.Headers.TryAddWithoutValidation(GatewayHeader, "1");
            // The public mount point (the standard reverse-proxy convention): a server-rendered
            // app prefixes its absolute links/form actions/redirect Locations with this, so they
            // stay inside /apps/{id}/ instead of escaping to pdn's root (the BBS-claim 405).
            if (context.Request.RouteValues["id"] is string appId && appId.Length > 0)
            {
                proxyRequest.Headers.TryAddWithoutValidation(ForwardedPrefixHeader, $"/apps/{appId}");
            }
        }
    }
}
