using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Packet.Core;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The over-RF sysop-code (TOTP) ENROLMENT side of the pdn node control API: a signed-in
/// user enrols, inspects, or removes the rolling one-time code they will present to elevate
/// a session <em>over the air</em> (AX.25 has no authentication — see
/// <see cref="TotpService"/> for why a single-use time-based code, not a static password, is
/// the right primitive there).
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope of this surface.</b> This is the enrolment / identity half only — it provisions
/// and manages a user's TOTP credential. The console <c>SYSOP</c> command, the elevation
/// state, and the privileged-command gate that <em>verify</em> a presented code over a
/// packet session are a separate, security-critical piece owned elsewhere; they consume
/// <see cref="IUserStore.FindByCallsign"/> + <see cref="TotpService.TryVerify"/> +
/// <see cref="IUserStore.UpdateTotpCounter"/>, which this work makes ready.
/// </para>
/// <para>
/// <b>Self-service, read-gated.</b> All four endpoints are gated <c>read</c> (the floor for
/// an authenticated user managing their OWN credential — the same gate as the passkey
/// register group). The username always comes from the authenticated principal
/// (<see cref="PrincipalUsername"/>), never the request body, so a user can only enrol /
/// inspect / clear for themselves.
/// </para>
/// <para>
/// <b>The pending-enrolment cache is the secret-handling core</b>
/// (<see cref="TotpEnrollmentCache"/>): <c>begin</c> mints a fresh secret and stashes it
/// server-side keyed to the user (single-use, expiring off the injected clock) and returns
/// it ONCE for display; nothing is persisted. <c>complete</c> consumes that pending secret
/// and only persists it (via <see cref="IUserStore.SetTotpSecret"/>) once the typed code
/// verifies — and immediately advances the replay counter past the confirming code so it
/// can't itself be replayed over RF. A never-confirmed secret is never written to the db.
/// </para>
/// <para>
/// <b>Default-off contract.</b> These endpoints are ALWAYS mapped but inert until used: a
/// node with no TOTP enrolled behaves exactly as before. When auth is off there is no
/// authenticated "self" to enrol for, so the endpoints 409 (mirroring the passkey
/// register group) rather than wrongly attribute a credential.
/// </para>
/// <para>
/// No wall-clock (repo rule §2.7): the injected <see cref="TimeProvider"/> drives both the
/// enrolment-cache expiry and <see cref="TotpService"/>'s code window.
/// </para>
/// </remarks>
public static class PdnTotpApi
{
    private const string AuditCategory = "Packet.Node.Auth";

    /// <summary>
    /// Map the TOTP enrolment endpoints under <c>/api/v1/auth/totp</c> and RETURN the gated
    /// group so the caller can require <c>read</c> the same way every other gated group is
    /// wired (self-service: a user manages their own over-RF credential).
    /// </summary>
    /// <returns>The gated <c>/auth/totp</c> route group, for the caller to require
    /// <c>read</c>.</returns>
    public static RouteGroupBuilder MapPdnTotpApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/api/v1/auth/totp");

        // Begin enrolment FOR THE SIGNED-IN USER: mint a fresh secret + the otpauth URI,
        // stash the secret server-side (single-use, expiring), and return it ONCE for the
        // user to scan / type into an authenticator app. Nothing is persisted yet.
        group.MapPost("/enroll/begin", (
            HttpContext http,
            IUserStore users,
            TotpEnrollmentCache? pending,
            IConfigProvider config) =>
        {
            if (pending is null)
            {
                return TotpUnavailable();
            }
            var username = PrincipalUsername(http);
            if (username is null)
            {
                // Auth off (no principal): no "self" to enrol for. Mirrors the passkey
                // register group — mapped (not 404), but 409 since the credential would be
                // unattributable.
                return Results.Problem("TOTP enrolment requires an authenticated session.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            var user = users.FindByUsername(username);
            if (user is null)
            {
                return Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound);
            }

            pending.PruneExpired();   // opportunistic cleanup

            var secret = TotpService.GenerateSecret();
            // The label account is the user's already-bound callsign if they have one
            // (a re-enrol), else the username — the issuer is the node's own callsign so a
            // scanned credential is namespaced to this node in the authenticator app.
            var account = string.IsNullOrWhiteSpace(user.Callsign) ? user.Username : user.Callsign;
            var issuer = NodeIssuer(config);
            var otpauthUri = TotpService.BuildOtpAuthUri(secret, account, issuer);

            // Stash keyed to the USER (an enrolment can only be completed by/for them).
            pending.Put(username, secret);
            return Results.Ok(new EnrollBeginResponse(secret, otpauthUri));
        });

        // Complete enrolment: consume the pending secret, verify the typed code against it,
        // and only then persist the secret + bind the callsign + burn the confirming code's
        // counter. Generic 400 on a verify failure; 409 if the callsign is taken.
        group.MapPost("/enroll/complete", (
            EnrollCompleteRequest? body,
            HttpContext http,
            IUserStore users,
            TotpEnrollmentCache? pending,
            ILoggerFactory logs,
            TimeProvider clock) =>
        {
            var audit = logs.CreateLogger(AuditCategory);
            var ip = ClientIp(http);

            if (pending is null)
            {
                return TotpUnavailable();
            }
            var username = PrincipalUsername(http);
            if (username is null)
            {
                return Results.Problem("TOTP enrolment requires an authenticated session.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            var auditUser = Redact(username);

            // Validate the callsign shape FIRST (user-typed input → strict TryParse), but
            // don't consume the pending secret on a malformed callsign — the user can retry
            // the same begin. (A bad CODE does consume it; see below.)
            if (body is null || string.IsNullOrWhiteSpace(body.Callsign)
                || !Callsign.TryParse(body.Callsign, out var parsed))
            {
                AuthLog.TotpEnrollFailed(audit, auditUser, ip, "bad-callsign");
                return Results.BadRequest(new { error = "A valid callsign is required." });
            }
            var callsign = parsed.ToString();

            // CONSUME the pending secret for THIS user (single-use). Absent → no begin / it
            // expired / it was already used → 400. A bad code below still burns it (the Take
            // already removed it), so a brute-force needs a fresh begin per guess.
            var secret = pending.Take(username);
            if (secret is null)
            {
                AuthLog.TotpEnrollFailed(audit, auditUser, ip, "no-pending-enrolment");
                return Results.BadRequest(new { error = "No pending TOTP enrolment (it may have expired)." });
            }

            // Verify the confirming code against the pending secret. lastAcceptedCounter: -1
            // ⇒ first ever acceptance (no prior high-water mark). The accepted counter is
            // then persisted as the high-water mark so this very code can't be replayed.
            var totp = new TotpService(clock);
            if (!totp.TryVerify(secret, body.Code, lastAcceptedCounter: -1, out var counter))
            {
                AuthLog.TotpEnrollFailed(audit, auditUser, ip, "code-did-not-verify");
                return Results.BadRequest(new { error = "The code did not verify. Check the time on your device and try again." });
            }

            // Persist the secret + bind the callsign (resets the counter to NULL). A false
            // here is the callsign-already-bound-to-another-user case → 409.
            if (!users.SetTotpSecret(username, secret, callsign))
            {
                AuthLog.TotpEnrollFailed(audit, auditUser, ip, "callsign-in-use");
                return Results.Conflict(new { error = $"Callsign '{callsign}' is already enrolled to another user." });
            }

            // Burn the confirming code: advance the replay high-water mark past it so the
            // SAME code presented over RF moments later is rejected. A false here means the
            // counter could not be persisted — the secret is stored but the confirming code
            // is briefly replayable until the next accepted code advances it; surface a 500
            // so the user re-confirms rather than silently leaving that window open.
            if (!users.UpdateTotpCounter(username, counter))
            {
                AuthLog.TotpEnrollFailed(audit, auditUser, ip, "counter-not-persisted");
                return Results.Problem("Enrolment was recorded but the replay guard could not be persisted; please re-enrol.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            AuthLog.TotpEnrolled(audit, username, ip, callsign);
            return Results.Ok(new EnrollCompleteResponse(true, callsign));
        });

        // Current over-RF enrolment state for the signed-in user (never the secret).
        group.MapGet("/enroll", (HttpContext http, IUserStore users) =>
        {
            var username = PrincipalUsername(http);
            if (username is null)
            {
                // Auth off: there is no "self" to report on. Not enrolled, no callsign.
                return Results.Ok(new EnrollStateResponse(false, null));
            }
            var user = users.FindByUsername(username);
            bool enrolled = user is not null && !string.IsNullOrEmpty(user.TotpSecret);
            return Results.Ok(new EnrollStateResponse(enrolled, enrolled ? user!.Callsign : null));
        });

        // Remove the signed-in user's over-RF credential. Idempotent (204 even if there was
        // nothing to clear — there is nothing to leak).
        group.MapDelete("/enroll", (HttpContext http, IUserStore users, ILoggerFactory logs) =>
        {
            var username = PrincipalUsername(http);
            if (username is null)
            {
                return Results.Problem("TOTP management requires an authenticated session.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            var audit = logs.CreateLogger(AuditCategory);
            var ip = ClientIp(http);
            users.ClearTotp(username);   // best-effort; a no-op clear is still a 204
            AuthLog.TotpCleared(audit, username, ip);
            return Results.NoContent();
        });

        return group;
    }

    // The node's own callsign, used as the otpauth issuer so a scanned credential is
    // namespaced to this node. Falls back to a stable label if the identity is unset.
    private static string NodeIssuer(IConfigProvider config)
    {
        var callsign = config.Current.Identity.Callsign;
        return string.IsNullOrWhiteSpace(callsign) ? "packetnet" : callsign;
    }

    // 503 when the enrolment machinery couldn't initialise. Mirrors the WebAuthn 503 — the
    // node still boots, TOTP enrolment just can't be used this run.
    private static IResult TotpUnavailable() =>
        Results.Problem("TOTP enrolment is not available.", statusCode: StatusCodes.Status503ServiceUnavailable);

    // The authenticated principal's username, or null when there is no authenticated user
    // (auth off, or no token). The endpoints take the enrolling/managing username from HERE
    // — never from the request body — so a user can only act on themselves. (Same resolution
    // as PdnWebAuthnApi: the JWT carries the username in `sub`, which can surface as
    // Identity.Name, the raw `sub` claim, or the mapped NameIdentifier.)
    private static string? PrincipalUsername(HttpContext http)
    {
        var user = http.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }
        var name = user.Identity!.Name
            ?? user.FindFirst(Microsoft.IdentityModel.JsonWebTokens.JwtRegisteredClaimNames.Sub)?.Value
            ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string Redact(string? username) =>
        string.IsNullOrEmpty(username) ? "(unknown)" : username;

    // --- DTOs (camelCased by STJ web defaults) ------------------------------------

    /// <summary>The <c>/enroll/begin</c> response: the freshly-minted base32 secret (shown
    /// ONCE for manual entry) and the <c>otpauth://</c> URI to render as a QR code. Neither
    /// is persisted until <c>/enroll/complete</c> succeeds.</summary>
    public sealed record EnrollBeginResponse(string Secret, string OtpauthUri);

    /// <summary>The <c>/enroll/complete</c> request: the current code from the authenticator
    /// app + the callsign to bind the credential to. (The username comes from the principal,
    /// not here.)</summary>
    public sealed record EnrollCompleteRequest(string? Code, string? Callsign);

    /// <summary>The <c>/enroll/complete</c> success body.</summary>
    public sealed record EnrollCompleteResponse(bool Enrolled, string Callsign);

    /// <summary>The <c>GET /enroll</c> body: whether the signed-in user has an over-RF code
    /// enrolled, and the bound callsign (null when not enrolled). Never the secret.</summary>
    public sealed record EnrollStateResponse(bool Enrolled, string? Callsign);
}
