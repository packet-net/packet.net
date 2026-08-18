using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Packet.Node.Api;

/// <summary>
/// The app-gateway human-plane 401 recovery (see <c>Program.cs</c>'s <c>OnChallenge</c>).
/// </summary>
/// <remarks>
/// <para>
/// A browser navigation or slot iframe to a gated <c>/apps/{id}/*</c> path authenticates via
/// the <c>pdn_at</c> cookie (it can't set an <c>Authorization</c> header). When that cookie's
/// token is expired/absent the JwtBearer pipeline issues a bare <c>401 + WWW-Authenticate:
/// Bearer</c> with an empty body - which a browser frame can't render (iOS Safari saves the
/// empty body to disk as a download). For a real browser navigation we instead swap the bare
/// 401 for a renderable login redirect: a top-level navigation gets a <c>302</c> to the SPA
/// login; a sub-frame can't redirect its own parent, so it gets a tiny <c>text/html</c> 200
/// that breaks the SLOT out to the top-level login.
/// </para>
/// <para>
/// <b>This does not weaken auth.</b> The request is still rejected and re-login is still
/// required; only the SHAPE of the rejection changes, and only for navigations a human made
/// (detected from <c>Sec-Fetch-*</c> / <c>Accept</c>). XHR / API 401s (the SPA's own fetches)
/// are untouched - they still get the bare 401 the SPA's <c>on401</c> chokepoint expects.
/// </para>
/// <para>
/// <b>Open-redirect safety.</b> The <c>next</c> the login URL carries is built from the
/// server-side <c>Request.Path + Request.QueryString</c> - never any client-supplied value -
/// and is a single-leading-slash, same-site RELATIVE path. The SPA side additionally rejects
/// any <c>next</c> that isn't a single-leading-slash relative path (no <c>//</c>, no scheme),
/// so a crafted absolute/protocol-relative URL can never be honoured as a redirect target.
/// </para>
/// </remarks>
internal static class AppGatewayChallenge
{
    /// <summary>The SPA login route the redirect/break-out targets.</summary>
    internal const string LoginPath = "/login";

    /// <summary>
    /// True when the request is a human-plane browser navigation (a top-level navigation or a
    /// sub-frame document load) rather than an XHR / API fetch. Decided from the Fetch Metadata
    /// request headers (<c>Sec-Fetch-Mode</c> / <c>Sec-Fetch-Dest</c>) with an <c>Accept:
    /// text/html</c> fallback for clients (or contexts) that don't send them. A fetch/XHR sends
    /// <c>Sec-Fetch-Mode: cors|no-cors|same-origin</c> + <c>Sec-Fetch-Dest: empty</c> and an
    /// <c>Accept</c> of <c>application/json</c>, so it is NOT classified as a navigation and
    /// keeps the bare 401.
    /// </summary>
    internal static bool IsBrowserNavigation(HttpRequest request)
    {
        var dest = request.Headers["Sec-Fetch-Dest"].ToString();
        // A document/iframe/frame destination is unambiguously a navigation/frame load.
        if (IsFrameDest(dest) || string.Equals(dest, "document", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An explicit navigate mode (top-level navigation) - belt-and-braces alongside Dest.
        var mode = request.Headers["Sec-Fetch-Mode"].ToString();
        if (string.Equals(mode, "navigate", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // If the client sent Fetch-Metadata headers AND they say this is not a navigation
        // (e.g. Sec-Fetch-Dest: empty for an XHR), trust them - do NOT fall through to Accept.
        if (!string.IsNullOrEmpty(dest) || !string.IsNullOrEmpty(mode))
        {
            return false;
        }

        // No Fetch-Metadata at all → fall back to Accept. A browser document load sends an
        // Accept that includes text/html; an API fetch asks for application/json.
        return AcceptsHtml(request.Headers["Accept"].ToString());
    }

    /// <summary>
    /// Whether the request is a sub-frame (iframe/frame) document load - these can't 302 their
    /// own parent, so they get the break-out HTML instead of a redirect.
    /// </summary>
    internal static bool IsFrameDest(string secFetchDest) =>
        string.Equals(secFetchDest, "iframe", StringComparison.OrdinalIgnoreCase)
        || string.Equals(secFetchDest, "frame", StringComparison.OrdinalIgnoreCase);

    private static bool AcceptsHtml(string accept) =>
        accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Write the human-plane re-auth response onto <paramref name="http"/>: a 302 to the login
    /// for a top-level navigation, or a tiny break-out HTML 200 for a sub-frame. The caller is
    /// responsible for <c>context.HandleResponse()</c> so the bare-401 challenge is suppressed.
    /// </summary>
    internal static Task WriteLoginRedirect(HttpContext http)
    {
        var loginUrl = BuildLoginUrl(http.Request);
        var dest = http.Request.Headers["Sec-Fetch-Dest"].ToString();

        if (IsFrameDest(dest))
        {
            // A sub-frame can't redirect its TOP window, and a 302 would just reload the frame
            // with the same unauthenticated 401. Return a renderable 200 whose script breaks the
            // slot out to the top-level login. window.top falls back to window.location for the
            // (cross-origin-isolated / no-top) edge so the navigation always lands somewhere sane.
            var json = JsonSerializer.Serialize(loginUrl);
            var body =
                "<!doctype html><meta charset=utf-8><title>Sign in</title>"
                + "<script>(function(){var u=" + json + ";try{window.top.location.href=u;}catch(e){window.location.href=u;}})();</script>";
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/html; charset=utf-8";
            // Never let an intermediary cache this transient re-auth page.
            http.Response.Headers.CacheControl = "no-store";
            return http.Response.WriteAsync(body);
        }

        // Top-level navigation → a plain 302 to the login. (Status + Location only; no body.)
        http.Response.StatusCode = StatusCodes.Status302Found;
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Location = loginUrl;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The login URL: the SPA login route plus a <c>next</c> query carrying the originally
    /// requested same-site relative path (server-derived <c>Request.Path + QueryString</c>),
    /// URL-encoded. The result is itself a single-leading-slash relative URL.
    /// </summary>
    internal static string BuildLoginUrl(HttpRequest request)
    {
        // Return to the SPA app route (/apps/{id}) - NOT the raw gateway path
        // (/apps/{id}/...?pdn_embed=1), which would load the headless app full-page outside the
        // panel chrome. Both a top-level nav (to /apps/{id}) and a slot iframe (to /apps/{id}/...)
        // resolve to the same SPA route. Server-derived only - never a client-supplied next.
        var segments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var next = segments.Length >= 2 && string.Equals(segments[0], "apps", StringComparison.Ordinal)
            ? "/apps/" + segments[1]
            : "/";
        return LoginPath + "?next=" + Uri.EscapeDataString(next);
    }
}
