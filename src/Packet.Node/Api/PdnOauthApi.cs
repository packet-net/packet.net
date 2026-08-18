using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Auth.Oauth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The MCP OAuth 2.1 authorization server (the hosted claude.ai connector path -
/// <c>docs/mcp-oauth-design.md</c>). The node is its own AS + RS: it owns the identities
/// (<see cref="IUserStore"/>), mints the JWTs (<see cref="JwtTokenService"/>), and hosts the
/// consent surface - so the whole flow reuses what the panel auth already ships.
/// </summary>
/// <remarks>
/// <para><b>Default-off + security-critical.</b> Every route here is mapped unconditionally
/// but short-circuits to 404 unless <c>mcp.oauth.enabled</c> - so nothing is exposed until an
/// operator opts in. Review before enabling in production (cf. the WebAuthn review).</para>
/// <para><b>Flow:</b> discovery (RFC 9728 + 8414) → dynamic client registration (RFC 7591) →
/// authorize (code + PKCE S256, owner login + explicit consent) → token (code→JWT). The MCP
/// access token is a node JWT minted on the dedicated <see cref="JwtTokenService.McpAudience"/>
/// (so it reaches <c>/mcp</c> only - never the wider control API), validated through the
/// existing JwtBearer middleware unchanged. <b>No refresh token in this cut</b>
/// (the connector re-runs authorize on expiry); refresh is a documented follow-up.</para>
/// <para><b>Hardening:</b> PKCE S256 mandatory; exact redirect-URI match (no wildcards);
/// single-use, short-TTL codes bound to client+redirect+challenge+user; explicit consent by a
/// logged-in owner; the consent POST must echo a single-use anti-forgery token and the
/// consent page ships <c>frame-ancestors 'none'</c> (no login-CSRF, no clickjacked consent);
/// login throttled on the same credential-guess budget as /auth/login; everything audited
/// (source <c>oauth</c>).</para>
/// </remarks>
public static class PdnOauthApi
{
    /// <summary>OAuth scope strings advertised + accepted (mapped to the node's read/operate).</summary>
    public const string ScopeRead = "mcp:read";
    public const string ScopeOperate = "mcp:operate";

    /// <summary>Authorization codes live briefly - long enough for the redirect round-trip.</summary>
    private static readonly TimeSpan CodeTtl = TimeSpan.FromSeconds(60);

    // Static metadata arrays (CA1861: hoisted out of the per-request dictionaries).
    private static readonly string[] ScopesSupported = [ScopeRead, ScopeOperate];
    private static readonly string[] BearerMethods = ["header"];
    private static readonly string[] ResponseTypes = ["code"];
    private static readonly string[] GrantTypes = ["authorization_code"];
    private static readonly string[] ChallengeMethods = [OauthPkce.MethodS256];
    private static readonly string[] AuthMethods = ["none"];

    public static void MapPdnOauthApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ---- Discovery (public) -------------------------------------------------

        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx, IConfigProvider config) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }

            var b = BaseUrl(ctx);
            return Results.Json(new Dictionary<string, object?>
            {
                ["resource"] = $"{b}/mcp",
                ["authorization_servers"] = new[] { b },
                ["scopes_supported"] = ScopesSupported,
                ["bearer_methods_supported"] = BearerMethods,
            });
        });

        app.MapGet("/.well-known/oauth-authorization-server", (HttpContext ctx, IConfigProvider config) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }

            var b = BaseUrl(ctx);
            return Results.Json(new Dictionary<string, object?>
            {
                ["issuer"] = b,
                ["authorization_endpoint"] = $"{b}/oauth/authorize",
                ["token_endpoint"] = $"{b}/oauth/token",
                ["registration_endpoint"] = $"{b}/oauth/register",
                ["revocation_endpoint"] = $"{b}/oauth/revoke",
                ["scopes_supported"] = ScopesSupported,
                ["response_types_supported"] = ResponseTypes,
                ["grant_types_supported"] = GrantTypes,
                ["code_challenge_methods_supported"] = ChallengeMethods,
                ["token_endpoint_auth_methods_supported"] = AuthMethods,
            });
        });

        // ---- Dynamic client registration (RFC 7591, public) ---------------------

        app.MapPost("/oauth/register", async (HttpContext ctx, IConfigProvider config, IOauthClientStore clients, IAuditLog audit, TimeProvider clock) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }

            DcrRequest? body;
            try { body = await ctx.Request.ReadFromJsonAsync<DcrRequest>(); }
            catch { return OauthError(StatusCodes.Status400BadRequest, "invalid_client_metadata", "Body is not valid JSON."); }

            var uris = body?.RedirectUris?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
            if (uris.Count == 0)
            {
                return OauthError(StatusCodes.Status400BadRequest, "invalid_redirect_uri", "At least one redirect_uri is required.");
            }
            // Each redirect_uri must be an absolute URI (https, or http only for loopback).
            foreach (var u in uris)
            {
                if (!Uri.TryCreate(u, UriKind.Absolute, out var parsed) || !IsAllowedRedirect(parsed))
                {
                    return OauthError(StatusCodes.Status400BadRequest, "invalid_redirect_uri", $"'{u}' is not an allowed redirect URI.");
                }
            }

            string name = string.IsNullOrWhiteSpace(body?.ClientName) ? "(unnamed client)" : body!.ClientName!.Trim();
            var client = clients.Register(name, uris, clock.GetUtcNow());
            if (client is null)
            {
                return Results.Problem("Client registration store is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            audit.RecordRest(ctx, clock, "oauth_register", client.ClientId, "ok", $"name={name}");
            return Results.Json(new Dictionary<string, object?>
            {
                ["client_id"] = client.ClientId,
                ["client_id_issued_at"] = client.CreatedUtc.ToUnixTimeSeconds(),
                ["client_name"] = client.ClientName,
                ["redirect_uris"] = client.RedirectUris,
                ["grant_types"] = GrantTypes,
                ["response_types"] = ResponseTypes,
                ["token_endpoint_auth_method"] = "none",
            }, statusCode: StatusCodes.Status201Created);
        });

        // ---- Authorize (GET: consent screen) ------------------------------------

        app.MapGet("/oauth/authorize", (HttpContext ctx, IConfigProvider config, IOauthClientStore clients, [FromServices] OauthCsrfCache? csrf) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }

            var q = ctx.Request.Query;
            var req = AuthorizeRequest.FromQuery(q);

            // Client + redirect_uri must validate BEFORE we trust redirect_uri enough to
            // redirect errors to it (else a 400 page).
            var client = clients.Find(req.ClientId);
            if (client is null || !client.RedirectUris.Contains(req.RedirectUri, StringComparer.Ordinal))
            {
                return ConsentHtml(ctx, ErrorPage("Unknown client or redirect URI."), StatusCodes.Status400BadRequest);
            }

            // From here, parameter errors redirect back to the (validated) redirect_uri.
            var paramError = req.Validate();
            if (paramError is not null)
            {
                return Results.Redirect(RedirectWithError(req.RedirectUri, paramError, req.State));
            }

            if (csrf is null)
            {
                // Fail closed: a consent page without an anti-forgery token must not render.
                return ConsentHtml(ctx, ErrorPage("Consent is not available."), StatusCodes.Status503ServiceUnavailable);
            }

            return ConsentHtml(ctx, ConsentPage(csrf.Mint(), client.ClientName, req));
        });

        // ---- Authorize (POST: owner login + consent decision) -------------------

        app.MapPost("/oauth/authorize", async (HttpContext ctx, IConfigProvider config, IOauthClientStore clients, IOauthCodeStore codes, IUserStore users, IAuditLog audit, [FromServices] LoginThrottle? throttle, [FromServices] OauthCsrfCache? csrf, TimeProvider clock) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }

            // Opportunistic cleanup, fault-swallowed (the same discipline as the refresh-token
            // prune on /auth/login). Consume only deletes codes that come back to /token, so an
            // abandoned authorize would otherwise leave its row behind for ever.
            codes.PruneExpired(clock.GetUtcNow());

            var form = await ctx.Request.ReadFormAsync();

            // CSRF: the POST must echo the single-use token minted into the rendered consent
            // page. Without it a third-party site could drive the victim's browser into posting
            // a login + approve (login CSRF) against the one-click operate-scope consent.
            // Reject before touching anything else; fail closed when the cache is absent.
            if (csrf is null || !csrf.Consume(form["csrf"].ToString()))
            {
                return ConsentHtml(ctx, ErrorPage(
                    "The consent form is missing its verification token. Go back, reload the authorization page, and try again."),
                    StatusCodes.Status400BadRequest);
            }

            var req = AuthorizeRequest.FromForm(form);

            var client = clients.Find(req.ClientId);
            if (client is null || !client.RedirectUris.Contains(req.RedirectUri, StringComparer.Ordinal))
            {
                return ConsentHtml(ctx, ErrorPage("Unknown client or redirect URI."), StatusCodes.Status400BadRequest);
            }
            var paramError = req.Validate();
            if (paramError is not null)
            {
                return Results.Redirect(RedirectWithError(req.RedirectUri, paramError, req.State));
            }

            // Explicit deny → access_denied back to the client.
            if (!string.Equals(form["action"], "approve", StringComparison.Ordinal))
            {
                audit.RecordRest(ctx, clock, "oauth_authorize", req.ClientId, "denied", "user declined");
                return Results.Redirect(RedirectWithError(req.RedirectUri, "access_denied", req.State));
            }

            string username = form["username"].ToString();
            string password = form["password"].ToString();
            // SAME credential-guess key namespace as /auth/login (LoginThrottle.IpKey /
            // UserKey): splitting guesses across the two endpoints must not double the
            // per-window password budget. The "unknown" fallback matches PdnAuthApi's
            // ClientIp so a missing RemoteIpAddress keys identically on both routes.
            string ipKey = LoginThrottle.IpKey(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            string userKey = LoginThrottle.UserKey(username);

            if (throttle is not null && (throttle.IsLocked(userKey) || throttle.IsLocked(ipKey)))
            {
                return ConsentHtml(ctx, ConsentPage(csrf.Mint(), client.ClientName, req, "Too many attempts - try again later."), StatusCodes.Status429TooManyRequests);
            }

            var user = users.FindByUsername(username);
            // Spend an equivalent Argon2 derivation when the user is unknown, so a probe
            // for a non-existent username takes the same wall-clock as one for a real
            // user - no username enumeration via the response timing.
            bool ok = user is not null
                ? PasswordHasher.Verify(password, user.PasswordHash)
                : PasswordHasher.VerifyDummy(password);
            if (!ok || user is null)
            {
                throttle?.RecordFailure(userKey);
                throttle?.RecordFailure(ipKey);
                audit.RecordRest(ctx, clock, "oauth_authorize", req.ClientId, "denied", $"bad credentials user={username}");
                return ConsentHtml(ctx, ConsentPage(csrf.Mint(), client.ClientName, req, "Incorrect username or password."), StatusCodes.Status401Unauthorized);
            }

            // Map the requested OAuth scope to a node scope, and enforce the user actually holds it.
            string nodeScope = req.WantsOperate ? AuthScopes.Operate : AuthScopes.Read;
            if (!AuthScopes.Satisfies(user.Scope, nodeScope))
            {
                audit.RecordRest(ctx, clock, "oauth_authorize", req.ClientId, "denied", $"user={user.Username} lacks {nodeScope}");
                return Results.Redirect(RedirectWithError(req.RedirectUri, "access_denied", req.State));
            }

            throttle?.Reset(userKey);
            throttle?.Reset(ipKey);

            // Mint the single-use code bound to client + redirect + challenge + user + scope.
            string code = Base64Url(RandomNumberGenerator.GetBytes(32));
            codes.Issue(new OauthCode(code, req.ClientId, req.RedirectUri, req.CodeChallenge, nodeScope, req.Resource ?? string.Empty, user.Username, clock.GetUtcNow() + CodeTtl));
            audit.RecordRest(ctx, clock, "oauth_authorize", req.ClientId, "ok", $"user={user.Username} scope={nodeScope}");

            var sep = req.RedirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var sb = new StringBuilder(req.RedirectUri).Append(sep).Append("code=").Append(Uri.EscapeDataString(code));
            if (!string.IsNullOrEmpty(req.State))
            {
                sb.Append("&state=").Append(Uri.EscapeDataString(req.State));
            }
            // RFC 9207: identify the issuer in the authorization response so the client can
            // detect a mix-up attack (a code minted by a different AS than it expected).
            sb.Append("&iss=").Append(Uri.EscapeDataString(BaseUrl(ctx)));
            return Results.Redirect(sb.ToString());
        });

        // ---- Token (code → access token) ----------------------------------------

        app.MapPost("/oauth/token", async (HttpContext ctx, IConfigProvider config, IOauthClientStore clients, IOauthCodeStore codes, IAuditLog audit, [FromServices] JwtTokenService? tokens, TimeProvider clock) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }

            if (tokens is null)
            {
                return OauthError(StatusCodes.Status503ServiceUnavailable, "temporarily_unavailable", "Token signing is not configured.");
            }

            var form = await ctx.Request.ReadFormAsync();
            if (!string.Equals(form["grant_type"], "authorization_code", StringComparison.Ordinal))
            {
                return OauthError(StatusCodes.Status400BadRequest, "unsupported_grant_type", "Only authorization_code is supported.");
            }

            string code = form["code"].ToString();
            string clientId = form["client_id"].ToString();
            string redirectUri = form["redirect_uri"].ToString();
            string verifier = form["code_verifier"].ToString();

            var stored = codes.Consume(code, clock.GetUtcNow());
            if (stored is null
                || !string.Equals(stored.ClientId, clientId, StringComparison.Ordinal)
                || !string.Equals(stored.RedirectUri, redirectUri, StringComparison.Ordinal)
                || !OauthPkce.Verify(verifier, stored.CodeChallenge))
            {
                audit.RecordRest(ctx, clock, "oauth_token", clientId, "denied", "invalid_grant");
                return OauthError(StatusCodes.Status400BadRequest, "invalid_grant", "The authorization code is invalid, expired, already used, or the PKCE verifier does not match.");
            }

            var lifetime = TimeSpan.FromMinutes(Math.Clamp(config.Current.Mcp.Oauth.AccessTokenLifetimeMinutes, 1, 1440));
            // MCP audience: the connector token reaches /mcp only, never the wider control API.
            var (token, expiresAt) = tokens.Issue(stored.Username, stored.Scope, lifetime, JwtTokenService.McpAudience);
            audit.RecordRest(ctx, clock, "oauth_token", clientId, "ok", $"user={stored.Username} scope={stored.Scope}");

            return Results.Json(new Dictionary<string, object?>
            {
                ["access_token"] = token,
                ["token_type"] = "Bearer",
                ["expires_in"] = (int)(expiresAt - clock.GetUtcNow()).TotalSeconds,
                ["scope"] = stored.Scope == AuthScopes.Operate ? ScopeOperate : ScopeRead,
            });
        });

        // ---- Revoke (RFC 7009) --------------------------------------------------

        app.MapPost("/oauth/revoke", async (HttpContext ctx, IConfigProvider config, IAuditLog audit, TimeProvider clock) =>
        {
            if (!Enabled(config))
            {
                return Results.NotFound();
            }
            // The MCP access token is a stateless JWT - it cannot be individually revoked (it
            // expires on its own; `pdn auth rotate-signing-key` + a restart invalidates ALL
            // tokens, which is the only revocation a stateless token has - see PdnAuthCli).
            // Per RFC 7009 we still answer 200 for any token. Per-token revocation rides the
            // refresh-token follow-up. The request is audited for transparency.
            await ctx.Request.ReadFormAsync();
            audit.RecordRest(ctx, clock, "oauth_revoke", "", "ok", "");
            return Results.Ok();
        });
    }

    // ---- helpers -------------------------------------------------------------

    private static bool Enabled(IConfigProvider config) => config.Current.Mcp.Oauth.Enabled;

    private static string BaseUrl(HttpContext ctx) => $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";

    // https anywhere; http only for loopback (local dev / Claude Code on the same box).
    private static bool IsAllowedRedirect(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static IResult OauthError(int status, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: status);

    private static string RedirectWithError(string redirectUri, string error, string? state)
    {
        var sep = redirectUri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        var sb = new StringBuilder(redirectUri).Append(sep).Append("error=").Append(Uri.EscapeDataString(error));
        if (!string.IsNullOrEmpty(state))
        {
            sb.Append("&state=").Append(Uri.EscapeDataString(state));
        }

        return sb.ToString();
    }

    private static string ErrorPage(string message) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Authorization error</title></head>" +
        $"<body style=\"font-family:system-ui;max-width:32rem;margin:4rem auto\"><h1>Authorization error</h1>" +
        $"<p>{WebUtility.HtmlEncode(message)}</p></body></html>";

    // Serve a consent/authorize HTML page with frame protection: the one-click
    // operate-scope consent (and the node login it embeds) must never be embeddable in
    // a foreign iframe, or an attacker page could clickjack the Approve click.
    private static IResult ConsentHtml(HttpContext ctx, string html, int status = StatusCodes.Status200OK)
    {
        ctx.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
        ctx.Response.Headers.XFrameOptions = "DENY";
        return Results.Content(html, "text/html", Encoding.UTF8, status);
    }

    // A minimal, self-contained consent + login page. All authorize params ride as hidden
    // fields so the POST reconstructs the request; everything user-facing is HTML-encoded.
    // The single-use anti-forgery token (csrfToken) rides as a hidden field too, and the
    // POST rejects unless it echoes back (login-CSRF protection).
    private static string ConsentPage(string csrfToken, string clientName, AuthorizeRequest req, string? error = null)
    {
        string Enc(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        string hidden(string n, string? v) => $"<input type=\"hidden\" name=\"{n}\" value=\"{Enc(v)}\">";
        string scopeText = req.WantsOperate
            ? "read and operate (observe and control the node)"
            : "read (observe the node)";
        string errBlock = error is null ? "" : $"<p style=\"color:#b00\">{Enc(error)}</p>";

        return $"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Connect {Enc(clientName)}</title></head>
            <body style="font-family:system-ui;max-width:28rem;margin:3rem auto;line-height:1.5">
              <h1>Authorize MCP access</h1>
              <p><strong>{Enc(clientName)}</strong> wants to connect to this packet node's MCP endpoint with scope:
                 <strong>{scopeText}</strong>.</p>
              {errBlock}
              <form method="post" action="/oauth/authorize">
                {hidden("csrf", csrfToken)}
                {hidden("response_type", req.ResponseType)}{hidden("client_id", req.ClientId)}
                {hidden("redirect_uri", req.RedirectUri)}{hidden("scope", req.Scope)}
                {hidden("state", req.State)}{hidden("code_challenge", req.CodeChallenge)}
                {hidden("code_challenge_method", req.CodeChallengeMethod)}{hidden("resource", req.Resource)}
                <p><label>Username<br><input name="username" autocomplete="username" required style="width:100%"></label></p>
                <p><label>Password<br><input name="password" type="password" autocomplete="current-password" required style="width:100%"></label></p>
                <p><button type="submit" name="action" value="approve">Approve</button>
                   <button type="submit" name="action" value="deny" formnovalidate>Deny</button></p>
              </form>
              <p style="color:#666;font-size:.85rem">Log in as a node user to approve. Only approve clients you trust.</p>
            </body></html>
            """;
    }

    /// <summary>DCR request body (RFC 7591 - the subset we honour).</summary>
    private sealed record DcrRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("client_name")] string? ClientName,
        [property: System.Text.Json.Serialization.JsonPropertyName("redirect_uris")] List<string>? RedirectUris);

    /// <summary>The authorize request parameters, from query (GET) or form (POST).</summary>
    private sealed record AuthorizeRequest(
        string ResponseType, string ClientId, string RedirectUri, string Scope,
        string? State, string CodeChallenge, string CodeChallengeMethod, string? Resource)
    {
        public bool WantsOperate => Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ScopeOperate);

        public static AuthorizeRequest FromQuery(IQueryCollection q) => new(
            q["response_type"].ToString(), q["client_id"].ToString(), q["redirect_uri"].ToString(),
            string.IsNullOrWhiteSpace(q["scope"]) ? ScopeRead : q["scope"].ToString(),
            q["state"], q["code_challenge"].ToString(), q["code_challenge_method"].ToString(), q["resource"]);

        public static AuthorizeRequest FromForm(IFormCollection f) => new(
            f["response_type"].ToString(), f["client_id"].ToString(), f["redirect_uri"].ToString(),
            string.IsNullOrWhiteSpace(f["scope"]) ? ScopeRead : f["scope"].ToString(),
            f["state"], f["code_challenge"].ToString(), f["code_challenge_method"].ToString(), f["resource"]);

        /// <summary>Returns an OAuth error code if a parameter is invalid, else null.</summary>
        public string? Validate()
        {
            if (!string.Equals(ResponseType, "code", StringComparison.Ordinal))
            {
                return "unsupported_response_type";
            }

            if (!string.Equals(CodeChallengeMethod, OauthPkce.MethodS256, StringComparison.Ordinal))
            {
                return "invalid_request";
            }

            if (string.IsNullOrWhiteSpace(CodeChallenge))
            {
                return "invalid_request";
            }

            foreach (var s in Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (s is not (ScopeRead or ScopeOperate))
                {
                    return "invalid_scope";
                }
            }
            return null;
        }
    }
}
