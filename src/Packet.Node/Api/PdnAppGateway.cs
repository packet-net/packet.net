using Microsoft.AspNetCore.Http;
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
        // Launcher feed: the registered apps that expose a UI (read-gated, like the other reads).
        app.MapGet("/api/v1/apps", (IConfigProvider config) =>
        {
            var tiles = config.Current.Applications
                .Where(a => a.Enabled && a.Ui is not null)
                .Select(a => new AppTile(a.Id, a.Ui!.Name ?? a.Id, a.Ui.Icon, $"/apps/{a.Id}/"))
                .ToList();
            return Results.Ok(tiles);
        }).RequireAuthorization(PdnAuthPolicies.Read);

        // Apps are reached at /apps/{id}/ (trailing slash — the launcher always emits it, and the
        // app's relative URLs resolve against it). The catch-all below matches that (rest = "").
        // We deliberately do NOT add a bare `/apps/{id}` → `/apps/{id}/` redirect: routing also
        // matches `/apps/{id}` against the WITH-slash form, so such a route shadows the proxy for
        // `/apps/{id}/` and 302-loops. (A no-trailing-slash request falls through to the SPA.)

        // The reverse proxy. Read-gated; the token comes from the bearer header or the pdn_at
        // cookie (Program.cs OnMessageReceived). Resolve the app by id, forward to its upstream.
        // All methods (MapMethods, not the ambiguous all-methods app.Map overload).
        app.MapMethods("/apps/{id}/{**rest}", ProxyMethods, async (HttpContext context, IConfigProvider config, IHttpForwarder forwarder) =>
        {
            var id = context.Request.RouteValues["id"] as string;
            var appCfg = config.Current.Applications
                .FirstOrDefault(a => a.Enabled && a.Ui is not null && string.Equals(a.Id, id, StringComparison.Ordinal));
            if (appCfg is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await forwarder.SendAsync(context, appCfg.Ui!.Upstream, ProxyClient, ForwardConfig, Transformer)
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

    /// <summary>A launcher tile (the <c>/api/v1/apps</c> shape).</summary>
    public sealed record AppTile(string Id, string Name, string? Icon, string Url);

    // Rebases the path (strips /apps/{id}), strips any client-supplied identity headers, and
    // injects the authenticated identity. A singleton — all per-request state is on the context.
    private sealed class AppGatewayTransformer : HttpTransformer
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

            // Anti-spoof: never let a client-supplied X-Pdn-* through — we set them ourselves.
            proxyRequest.Headers.Remove(UserHeader);
            proxyRequest.Headers.Remove(ScopeHeader);
            proxyRequest.Headers.Remove(GatewayHeader);

            var user = context.User?.Identity?.IsAuthenticated == true
                ? context.User.Identity!.Name ?? string.Empty
                : string.Empty;
            var scope = context.User?.FindFirst(AuthScopes.ScopeClaim)?.Value ?? string.Empty;

            proxyRequest.Headers.TryAddWithoutValidation(UserHeader, user);
            proxyRequest.Headers.TryAddWithoutValidation(ScopeHeader, scope);
            proxyRequest.Headers.TryAddWithoutValidation(GatewayHeader, "1");
        }
    }
}
