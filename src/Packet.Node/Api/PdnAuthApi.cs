using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Audit;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The authentication side of the pdn node control API: login (with refresh-token
/// issue + lockout hardening), refresh-token rotation + logout, the first-run setup
/// probe + bootstrap, and user management.
/// </summary>
/// <remarks>
/// <para>
/// <b>Always-open (never gated):</b> <c>GET /setup/state</c>, <c>POST /auth/login</c>,
/// <c>POST /auth/refresh</c>, <c>POST /auth/logout</c> and <c>POST /setup</c> are
/// reachable without a token — they are the bootstrap / session-lifecycle path (you
/// cannot present a bearer access token to refresh or log out a session that has, by
/// definition, an expired/absent one). They are mapped here without any
/// <c>.RequireAuthorization</c>. <c>/users</c> is gated <c>admin</c> (via the
/// conditional gate in <c>Program.cs</c>, so it too is open when auth is disabled).
/// </para>
/// <para>
/// <b>Login timing-safety.</b> A login for an unknown username and a login with a
/// bad password take the same code path and the same time: an unknown user is
/// verified against a fixed decoy Argon2 hash so the (expensive, dominant)
/// Argon2 derivation runs either way, and both failures return the identical
/// generic 401 — no oracle for "does this user exist?".
/// </para>
/// <para>
/// <b>Login hardening (lockout).</b> A <see cref="LoginThrottle"/> counts failures in
/// a sliding window under BOTH the username AND the source IP; once either crosses the
/// threshold the login is refused with 429 before the password verify even runs (so a
/// locked account/IP also stops burning Argon2 CPU). A successful login resets both
/// counters. Lockout is checked before the user lookup, so it adds no existence oracle.
/// </para>
/// <para>
/// <b>Refresh-token rotation + reuse detection.</b> Login mints an opaque refresh
/// token (stored only as a SHA-256 hash) in a fresh family; <c>/auth/refresh</c>
/// one-time-exchanges it for a new access JWT + a new refresh token in the same
/// family; replaying an already-consumed token revokes the whole family (theft
/// response). All the rotation logic lives in <see cref="RefreshTokenService"/>; this
/// class only maps it to HTTP + the audit log. See that type for the algorithm.
/// </para>
/// <para>
/// <b>Audit log (no secrets).</b> Login success/failure, lockout, refresh success,
/// refresh rejection, reuse-detection and logout each emit one structured log line
/// via <see cref="LoggerMessage"/> — carrying the username / source IP / outcome but
/// never a password, token, or token hash.
/// </para>
/// <para>
/// <b>Setup is one-shot:</b> <c>POST /setup</c> only succeeds while zero users
/// exist; once an admin exists it returns 409. It creates the admin
/// (<c>admin</c> scope) and applies the station identity (+ optional first port)
/// through the existing <see cref="IWritableConfigProvider.TryApply"/> seam — the
/// same validate→persist→reconcile path the config editor uses — rather than
/// reinventing a config write.
/// </para>
/// <para>
/// No wall-clock (repo rule §2.7): the injected <see cref="TimeProvider"/> stamps
/// <c>created</c>/<c>last login</c> and drives the token expiry through
/// <see cref="JwtTokenService"/>.
/// </para>
/// </remarks>
public static class PdnAuthApi
{
    // Minimum admin/user password length enforced on setup + user-create. A floor,
    // not a policy engine — keep the bar simple but non-trivial.
    private const int MinPasswordLength = 8;

    // A fixed, well-formed decoy Argon2id hash verified against when the username is
    // unknown, so an unknown-user login still pays the full Argon2 cost (constant-time
    // w.r.t. user existence). Generated once at module load from a random password the
    // caller can never know — its only purpose is to burn the same CPU as a real verify.
    private static readonly string DecoyHash = PasswordHasher.Hash(Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Map the auth endpoints under <c>/api/v1</c>. Called from the node composition
    /// root. The login / setup endpoints are always open; the <c>/users</c> group is
    /// returned to the caller so it can apply the admin gate conditionally on the
    /// auth flag (the same way every other gated group is wired).
    /// </summary>
    /// <returns>The <c>/users</c> route group, for the caller to gate <c>admin</c>.</returns>
    public static RouteGroupBuilder MapPdnAuthApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1");

        // --- Always-open bootstrap endpoints --------------------------------------

        // Whether first-run setup is still required (zero users). Unauthenticated —
        // it is the probe the setup wizard hits before any account exists.
        v1.MapGet("/setup/state", (IUserStore users) =>
            Results.Ok(new SetupStateResponse(NeedsSetup: users.Count() == 0)));

        // Password login → access JWT + opaque refresh token. Generic 401 on any
        // failure; same timing for unknown-user vs bad-password (see the type remarks).
        // 429 when the username OR the source IP is locked out (too many recent fails).
        // [FromServices] on the nullable services: when the signing key is unavailable
        // they are unregistered, and an explicit optional-service binding resolves them
        // to null (→ 503 below) instead of failing minimal-API parameter inference at
        // startup (which would abort the whole host).
        v1.MapPost("/auth/login", (
            LoginRequest body,
            HttpContext http,
            IUserStore users,
            [Microsoft.AspNetCore.Mvc.FromServices] JwtTokenService? tokens,
            [Microsoft.AspNetCore.Mvc.FromServices] RefreshTokenService? refresh,
            [Microsoft.AspNetCore.Mvc.FromServices] LoginThrottle? throttle,
            ILoggerFactory logs,
            TimeProvider clock) =>
        {
            var audit = logs.CreateLogger("Packet.Node.Auth");
            var ip = ClientIp(http);

            if (tokens is null || refresh is null)
            {
                // Auth couldn't be initialised (e.g. the signing key is unreadable).
                return Results.Problem("Authentication is not available.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var username = body?.Username ?? string.Empty;
            // Precompute the redacted username (a method call inline in a log argument
            // trips CA1873 — the logging rule wants non-trivial args precomputed).
            var auditUser = Redact(username);

            // Lockout check FIRST — before the user lookup / password verify — so a
            // locked account or IP can't keep guessing (and stops burning Argon2 CPU).
            // Checked per-username AND per-IP; the username key is empty-safe.
            string userKey = UserKey(username);
            string ipKey = IpKey(ip);
            if (throttle is not null && (throttle.IsLocked(userKey) || throttle.IsLocked(ipKey)))
            {
                AuthLog.LoginLockedOut(audit, auditUser, ip);
                return TooManyRequests();
            }

            if (body is null || string.IsNullOrEmpty(body.Username) || string.IsNullOrEmpty(body.Password))
            {
                RecordFailure(throttle, userKey, ipKey);
                AuthLog.LoginFailed(audit, auditUser, ip);
                return Unauthorized();
            }

            var user = users.FindByUsername(body.Username);
            // Constant-time w.r.t. user existence: verify against the decoy when the
            // user is absent so the Argon2 derivation runs either way, then fail
            // generically. (FixedTimeEquals inside Verify guards the digest compare.)
            bool ok = user is not null
                ? PasswordHasher.Verify(body.Password, user.PasswordHash)
                : PasswordHasher.Verify(body.Password, DecoyHash) && false;

            if (!ok || user is null)
            {
                RecordFailure(throttle, userKey, ipKey);
                AuthLog.LoginFailed(audit, auditUser, ip);
                return Unauthorized();
            }

            // Success → reset both lockout counters, mint the token pair.
            throttle?.Reset(userKey);
            throttle?.Reset(ipKey);
            refresh.PruneExpired();   // opportunistic cleanup, fault-swallowed

            var (token, expiresAt) = tokens.Issue(user.Username, user.Scope);
            var refreshToken = refresh.Issue(user.Username);
            users.UpdateLastLogin(user.Username, clock.GetUtcNow());
            // Set the HttpOnly gateway cookie so a browser navigation to a proxied app UI
            // (/apps/{id}/*) authenticates without an Authorization header (the panel's fetch
            // API still uses the bearer token from the body). See PdnAppGateway.
            PdnAppGateway.SetGatewayCookie(http, token, expiresAt);
            AuthLog.LoginSucceeded(audit, user.Username, ip, user.Scope);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Scope, refreshToken, user.Username));
        });

        // Rotate a refresh token → a fresh access JWT + a fresh refresh token (same
        // family). Always open (no bearer token — the access token may have expired,
        // which is the whole reason to refresh). 401 on any invalid / expired / reused
        // token; a reused (already-consumed) token additionally burns its whole family.
        v1.MapPost("/auth/refresh", (
            RefreshRequest body,
            HttpContext http,
            IUserStore users,
            [Microsoft.AspNetCore.Mvc.FromServices] JwtTokenService? tokens,
            [Microsoft.AspNetCore.Mvc.FromServices] RefreshTokenService? refresh,
            ILoggerFactory logs) =>
        {
            var audit = logs.CreateLogger("Packet.Node.Auth");
            var ip = ClientIp(http);

            if (tokens is null || refresh is null)
            {
                return Results.Problem("Authentication is not available.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            if (body is null || string.IsNullOrEmpty(body.RefreshToken))
            {
                var reason = nameof(RefreshOutcome.Invalid);
                AuthLog.RefreshRejected(audit, reason, ip);
                return Unauthorized();
            }

            var result = refresh.Rotate(body.RefreshToken);
            if (!result.IsSuccess)
            {
                if (result.Outcome == RefreshOutcome.ReuseDetected)
                {
                    var auditUser = Redact(result.Username);
                    AuthLog.RefreshReuseDetected(audit, auditUser, ip);
                }
                else
                {
                    var reason = result.Outcome.ToString();
                    AuthLog.RefreshRejected(audit, reason, ip);
                }
                return Unauthorized();
            }

            // The refresh token is the session's source of truth for identity; the
            // user's CURRENT scope is read fresh from the store (so a scope change /
            // a deleted user takes effect on the next refresh, not only on re-login).
            var user = users.FindByUsername(result.Username!);
            if (user is null)
            {
                // The user was deleted out from under a live session — revoke the
                // family and reject (don't mint a token for a non-existent user).
                if (result.Family is { } fam)
                {
                    refresh.LogoutFamily(fam);
                }
                AuthLog.RefreshRejected(audit, "UserGone", ip);
                return Unauthorized();
            }

            var (token, expiresAt) = tokens.Issue(user.Username, user.Scope);
            // Refresh the gateway cookie alongside the access token so a long-lived panel
            // session keeps proxied app UIs authenticated.
            PdnAppGateway.SetGatewayCookie(http, token, expiresAt);
            AuthLog.RefreshSucceeded(audit, user.Username, ip);
            return Results.Ok(new LoginResponse(token, expiresAt, user.Scope, result.NewToken, user.Username));
        });

        // Log out → revoke the presented token's whole family (every descendant). Always
        // open; idempotent (an unknown/blank token is still a 204 — nothing to leak).
        v1.MapPost("/auth/logout", (
            RefreshRequest body,
            HttpContext http,
            [Microsoft.AspNetCore.Mvc.FromServices] RefreshTokenService? refresh,
            ILoggerFactory logs) =>
        {
            var audit = logs.CreateLogger("Packet.Node.Auth");
            if (refresh is not null && body is { RefreshToken: { Length: > 0 } presented })
            {
                var (user, _) = refresh.Logout(presented);
                var redacted = Redact(user);
                var ip = ClientIp(http);
                AuthLog.Logout(audit, redacted, ip);
            }
            // Clear the gateway cookie so proxied app UIs are no longer reachable post-logout.
            PdnAppGateway.ClearGatewayCookie(http);
            return Results.NoContent();
        });

        // First-run bootstrap: create the admin + apply identity/firstPort. One-shot.
        v1.MapPost("/setup", (SetupRequest body, IUserStore users, IWritableConfigProvider cfg, TimeProvider clock) =>
        {
            // One-shot: refuse once any user exists (403 — the bootstrap is over).
            if (users.Count() > 0)
            {
                return Results.Problem("Setup has already been completed.", statusCode: StatusCodes.Status403Forbidden);
            }
            if (body is null || body.Identity is null || body.Admin is null)
            {
                return Results.BadRequest(new { error = "identity and admin are required." });
            }
            if (string.IsNullOrWhiteSpace(body.Identity.Callsign))
            {
                return Results.BadRequest(new { error = "identity.callsign is required." });
            }
            if (string.IsNullOrWhiteSpace(body.Admin.Username))
            {
                return Results.BadRequest(new { error = "admin.username is required." });
            }
            if (body.Admin.Password is null || body.Admin.Password.Length < MinPasswordLength)
            {
                return Results.BadRequest(new { error = $"admin.password must be at least {MinPasswordLength} characters." });
            }

            // Build the candidate config from the live one + the setup identity (+ first
            // port if given), then push it through the SAME write seam the editor uses —
            // it validates the callsign/port and reconciles. A rejected config (e.g. a
            // malformed callsign) is a 422, and NO user is created (config first).
            var current = cfg.Current;
            var candidate = current with
            {
                Identity = new Identity
                {
                    Callsign = body.Identity.Callsign,
                    Alias = string.IsNullOrWhiteSpace(body.Identity.Alias) ? null : body.Identity.Alias,
                    Grid = string.IsNullOrWhiteSpace(body.Identity.Grid) ? null : body.Identity.Grid,
                },
            };
            if (body.FirstPort is { } port)
            {
                candidate = candidate with { Ports = [.. current.Ports, port] };
            }

            if (!cfg.TryApply(candidate, out var errors))
            {
                return Results.UnprocessableEntity(new Packet.Node.Core.Api.ValidationProblem(errors));
            }

            // Config applied → create the admin user. Guarded by Create's UNIQUE +
            // the zero-users check above; a Create-false here means a concurrent setup
            // raced us in (still one-shot overall) → 409.
            var now = clock.GetUtcNow();
            var admin = new UserRecord(
                body.Admin.Username.Trim(),
                PasswordHasher.Hash(body.Admin.Password),
                AuthScopes.Admin,
                now,
                LastLoginUtc: null);
            if (!users.Create(admin))
            {
                return Results.Conflict(new { error = "An administrator already exists." });
            }

            return Results.Ok(new SetupResponse(admin.Username, admin.Scope));
        });

        // --- Admin-gated user management (gated by the caller) --------------------

        var usersGroup = v1.MapGroup("/users");

        usersGroup.MapGet("", (IUserStore users) =>
            Results.Ok(users.List().Select(UserSummary.From).ToArray()));

        usersGroup.MapPost("", (CreateUserRequest body, HttpContext ctx, IUserStore users, IAuditLog audit, TimeProvider clock) =>
        {
            if (body is null || string.IsNullOrWhiteSpace(body.Username))
            {
                return Results.BadRequest(new { error = "username is required." });
            }
            if (body.Password is null || body.Password.Length < MinPasswordLength)
            {
                return Results.BadRequest(new { error = $"password must be at least {MinPasswordLength} characters." });
            }
            if (!AuthScopes.IsKnown(body.Scope))
            {
                return Results.BadRequest(new { error = $"scope must be one of: {AuthScopes.Read}, {AuthScopes.Operate}, {AuthScopes.Admin}." });
            }

            var user = new UserRecord(
                body.Username.Trim(),
                PasswordHasher.Hash(body.Password),
                body.Scope!,
                clock.GetUtcNow(),
                LastLoginUtc: null);
            if (!users.Create(user))
            {
                return Results.Conflict(new { error = $"User '{user.Username}' already exists." });
            }
            // Creating an account is a privileged grant — record it (scope, not the password).
            audit.RecordRest(ctx, clock, "create_user", user.Username, "ok", $"scope={user.Scope}");
            return Results.Created($"/api/v1/users/{user.Username}", UserSummary.From(user));
        });

        usersGroup.MapDelete("/{username}", (string username, HttpContext ctx, IUserStore users, IAuditLog audit, TimeProvider clock) =>
        {
            // Don't let the last admin delete themselves into a locked-out node.
            var all = users.List();
            var target = all.FirstOrDefault(u => u.Username == username);
            if (target is null)
            {
                return Results.NotFound();
            }
            if (target.Scope == AuthScopes.Admin && all.Count(u => u.Scope == AuthScopes.Admin) <= 1)
            {
                return Results.Conflict(new { error = "Cannot delete the last administrator." });
            }
            if (!users.Delete(username))
            {
                return Results.NotFound();
            }
            audit.RecordRest(ctx, clock, "delete_user", username, "ok", "");
            return Results.NoContent();
        });

        return usersGroup;
    }

    // The identical generic 401 every login failure returns — no detail on which of
    // username/password was wrong. Reused by /auth/refresh so an invalid refresh and a
    // bad login are indistinguishable to a probe.
    private static IResult Unauthorized() =>
        Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);

    // Returned when a username/IP is locked out (sliding-window throttle).
    private static IResult TooManyRequests() =>
        Results.Json(new { error = "Too many login attempts. Try again later." }, statusCode: StatusCodes.Status429TooManyRequests);

    // Record a failed attempt under both the username and the source-IP keys.
    private static void RecordFailure(LoginThrottle? throttle, string userKey, string ipKey)
    {
        if (throttle is null)
        {
            return;
        }
        throttle.RecordFailure(userKey);
        throttle.RecordFailure(ipKey);
    }

    // The source IP for throttling + audit. Falls back to a sentinel so a missing
    // RemoteIpAddress (e.g. under the test server) still keys + logs consistently.
    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Namespaced throttle keys so a username can never collide with an IP. The username
    // key is empty-safe (a blank-username attempt still keys to "user:").
    private static string UserKey(string username) => "user:" + username;
    private static string IpKey(string ip) => "ip:" + ip;

    // Audit-log redaction: usernames are not secret, but an empty/unknown one logs as a
    // placeholder rather than a blank field. Passwords/tokens are NEVER passed here.
    private static string Redact(string? username) =>
        string.IsNullOrEmpty(username) ? "(unknown)" : username;

    // --- Request / response DTOs (camelCased by STJ web defaults) ----------------

    /// <summary>The <c>/auth/login</c> request body.</summary>
    public sealed record LoginRequest(string Username, string Password);

    /// <summary>The <c>/auth/login</c> + <c>/auth/refresh</c> (and passkey-assert) success
    /// body. <c>Scopes</c> is the single granted scope string; <c>RefreshToken</c> is the
    /// opaque token the client stores + presents to <c>/auth/refresh</c> (null only if the
    /// refresh-token store could not persist it — the access token still works until it
    /// expires); <c>Username</c> is the authenticated account, so the client need not
    /// derive it (a passwordless passkey sign-in has no typed username to fall back on).</summary>
    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt, string Scopes, string? RefreshToken, string Username);

    /// <summary>The <c>/auth/refresh</c> + <c>/auth/logout</c> request body.</summary>
    public sealed record RefreshRequest(string RefreshToken);

    /// <summary>The <c>/setup/state</c> body.</summary>
    public sealed record SetupStateResponse(bool NeedsSetup);

    /// <summary>The <c>/setup</c> request body.</summary>
    public sealed record SetupRequest(SetupIdentity Identity, SetupAdmin Admin, PortConfig? FirstPort = null);

    /// <summary>The station identity supplied at setup.</summary>
    public sealed record SetupIdentity(string Callsign, string? Alias = null, string? Grid = null);

    /// <summary>The first admin account supplied at setup.</summary>
    public sealed record SetupAdmin(string Username, string Password);

    /// <summary>The <c>/setup</c> success body.</summary>
    public sealed record SetupResponse(string Username, string Scope);

    /// <summary>The <c>POST /users</c> request body.</summary>
    public sealed record CreateUserRequest(string Username, string Password, string Scope);
}
