using System.Security.Cryptography;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Packet.Node.Core.Auth;
using Packet.Node.Core.Configuration;

namespace Packet.Node.Api;

/// <summary>
/// The WebAuthn / passkey side of the pdn node control API: enrol a passkey for the
/// signed-in user, sign in passwordlessly with a passkey, and manage (list / delete)
/// the caller's own passkeys.
/// </summary>
/// <remarks>
/// <para>
/// <b>Localhost-first (docs/passkeys-lan-trust-pattern.md §2).</b> The RP id defaults
/// to <c>localhost</c> and the expected origin is the actual serving origin the browser
/// used (<see cref="WebAuthnFido2Builder"/>), so same-machine passkeys work with zero
/// config over plain HTTP on loopback (a secure context). A real-domain operator sets
/// <c>management.auth.webAuthn.relyingPartyId</c> + <c>allowedOrigins</c>; the
/// distribution tiers (mDNS/ACME/redirects) are parked per the doc's §8 decision gate.
/// </para>
/// <para>
/// <b>The challenge cache is the security-critical bit</b> (<see cref="WebAuthnChallengeCache"/>):
/// every <c>begin</c> stashes the server-built options (challenge included) keyed to the
/// user (register) or a per-attempt session (assert); the matching <c>complete</c>
/// <em>consumes</em> that exact stashed options and verifies the authenticator's signed
/// challenge against the server's — never a client-supplied one. Single-use (a replay
/// finds nothing), expiring (off the injected clock), and key-bound.
/// </para>
/// <para>
/// <b>Sign-count clone detection.</b> The verify path rejects an assertion whose new
/// signature counter has not advanced past the stored one (when the authenticator uses
/// counters &gt; 0) — the signature of a cloned credential. Fido2NetLib enforces this
/// against the <see cref="MakeAssertionParams.StoredSignatureCounter"/> we pass; we then
/// persist the advanced counter on success.
/// </para>
/// <para>
/// <b>Passwordless assertion mints the SAME session as a password login.</b> A
/// successful assert issues the identical <c>{token,expiresAt,scopes,refreshToken}</c>
/// shape <c>/auth/login</c> returns (<see cref="JwtTokenService"/> +
/// <see cref="RefreshTokenService"/>), so the client treats it exactly like a password
/// login.
/// </para>
/// <para>
/// <b>Default-off contract.</b> These endpoints are ALWAYS mapped, but a node only
/// becomes usable via passkeys when <c>management.auth.enabled</c> is on AND a user has
/// enrolled one. The begin/complete-register endpoints are <c>operate</c>-gated (a
/// logged-in user enrols a passkey for THEMSELVES — the username comes from the
/// authenticated principal, never the body); the assert endpoints are always open (a
/// passwordless login can't carry a bearer token); the credential list/delete are gated.
/// </para>
/// </remarks>
public static class PdnWebAuthnApi
{
    /// <summary>
    /// Map the WebAuthn endpoints. The always-open assert endpoints are mapped directly;
    /// the gated group (register + credential management) is RETURNED to the caller so it
    /// can apply the conditional auth gate the same way every other gated group is wired.
    /// </summary>
    /// <returns>The gated <c>/auth/webauthn</c> route group (register + credentials),
    /// for the caller to require <c>operate</c>.</returns>
    public static RouteGroupBuilder MapPdnWebAuthnApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var v1 = app.MapGroup("/api/v1");

        // ===== ALWAYS-OPEN: passwordless assertion (login) ======================

        // Begin a passwordless assertion. Always open — a login has no bearer token.
        // Supports a username-less / discoverable-credential assertion (empty allow-list
        // ⇒ the authenticator offers any resident credential it holds for this RP), and a
        // username-scoped one (allow-list = that user's enrolled credentials).
        v1.MapPost("/auth/webauthn/assert/begin", (
            AssertBeginRequest? body,
            HttpContext http,
            IWebAuthnCredentialStore? credentials,
            WebAuthnChallengeCache? challenges,
            IConfigProvider config) =>
        {
            if (credentials is null || challenges is null)
            {
                return WebAuthnUnavailable();
            }

            challenges.PruneExpired();   // opportunistic cleanup

            // Build the allow-list. With a username, scope it to that user's credentials;
            // without one, leave it empty so a discoverable credential can be used (the
            // user is identified by the signed credential at /complete). We do NOT leak
            // whether a username exists: an unknown user simply yields an empty list,
            // which is indistinguishable from "use a discoverable credential".
            List<PublicKeyCredentialDescriptor> allow = [];
            if (!string.IsNullOrWhiteSpace(body?.Username))
            {
                foreach (var c in credentials.GetByUser(body.Username))
                {
                    allow.Add(Descriptor(c));
                }
            }

            var fido2 = WebAuthnFido2Builder.ForRequest(config.Current.Management.Auth.WebAuthn, http.Request);
            var options = fido2.GetAssertionOptions(new GetAssertionOptionsParams
            {
                AllowedCredentials = allow,
                UserVerification = UserVerificationRequirement.Preferred,
            });

            // Bind the pending assertion to a fresh per-attempt session id (a random
            // opaque handle), since a passwordless assertion has no username to key on.
            var sessionId = NewSessionId();
            challenges.Put(WebAuthnChallengeCache.AssertionKey(sessionId), options);

            return Results.Ok(new AssertBeginResponse(sessionId, System.Text.Json.JsonDocument.Parse(options.ToJson()).RootElement));
        });

        // Complete a passwordless assertion → verify + issue the SAME token pair as a
        // password login. Always open.
        v1.MapPost("/auth/webauthn/assert/complete", async (
            AssertCompleteRequest body,
            HttpContext http,
            IUserStore users,
            IWebAuthnCredentialStore? credentials,
            WebAuthnChallengeCache? challenges,
            [Microsoft.AspNetCore.Mvc.FromServices] JwtTokenService? tokens,
            [Microsoft.AspNetCore.Mvc.FromServices] RefreshTokenService? refresh,
            IConfigProvider config,
            ILoggerFactory logs,
            TimeProvider clock) =>
        {
            var audit = logs.CreateLogger("Packet.Node.Auth");
            var ip = ClientIp(http);

            if (credentials is null || challenges is null || tokens is null || refresh is null)
            {
                return WebAuthnUnavailable();
            }
            if (body is null || string.IsNullOrWhiteSpace(body.SessionId) || body.Response is null)
            {
                AuthLog.PasskeyAssertionFailed(audit, ip, "malformed-request");
                return AssertionRejected();
            }

            // CONSUME the server-stashed options for this session (single-use). A replay /
            // unknown / expired session finds nothing → reject. The challenge to verify
            // against is the one INSIDE these options — never a client value.
            var options = challenges.Take<AssertionOptions>(WebAuthnChallengeCache.AssertionKey(body.SessionId));
            if (options is null)
            {
                AuthLog.PasskeyAssertionFailed(audit, ip, "no-pending-challenge");
                return AssertionRejected();
            }

            AuthenticatorAssertionRawResponse raw;
            try
            {
                raw = ToAssertionResponse(body.Response.Value);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
            {
                AuthLog.PasskeyAssertionFailed(audit, ip, "unparseable-response");
                return AssertionRejected();
            }

            // A response with no credential id (e.g. an empty/garbage body that still
            // deserialised) can't identify a credential — reject generically rather than
            // fault the store lookup.
            if (raw.RawId is not { Length: > 0 })
            {
                AuthLog.PasskeyAssertionFailed(audit, ip, "no-credential-id");
                return AssertionRejected();
            }

            // Find the credential the assertion is for, by its raw id, and the user it
            // belongs to. (For a discoverable credential the browser still returns the
            // credential id, so this resolves the identity.)
            var stored = credentials.GetByCredentialId(raw.RawId);
            if (stored is null)
            {
                AuthLog.PasskeyAssertionFailed(audit, ip, "unknown-credential");
                return AssertionRejected();
            }
            var user = users.FindByUsername(stored.Username);
            if (user is null)
            {
                // The owning user was deleted out from under the credential.
                AuthLog.PasskeyAssertionFailed(audit, ip, "owner-gone");
                return AssertionRejected();
            }

            VerifyAssertionResult result;
            try
            {
                result = await fido2VerifyAssertion(config, http, stored, raw, options).ConfigureAwait(false);
            }
            catch (Fido2VerificationException ex)
            {
                // Fido2NetLib raises this for a counter regression (clone), an origin/RP
                // mismatch, a bad signature, etc. The counter-regression case is the
                // clone-detection rejection; log it specifically when we can tell.
                if (CounterRegressed(stored.SignCount, ex))
                {
                    AuthLog.PasskeyCloneDetected(audit, user.Username, ip);
                }
                else
                {
                    AuthLog.PasskeyAssertionFailed(audit, ip, "verification-failed");
                }
                return AssertionRejected();
            }

            // Belt-and-braces clone check: even if the library accepted it, enforce the
            // monotonic-counter rule ourselves for authenticators that use counters (> 0).
            if (stored.SignCount > 0 && result.SignCount <= stored.SignCount)
            {
                AuthLog.PasskeyCloneDetected(audit, user.Username, ip);
                return AssertionRejected();
            }

            // Success → advance the counter + stamp last-used, then mint the SAME token
            // pair a password login does.
            credentials.UpdateSignCount(stored.CredentialId, result.SignCount, clock.GetUtcNow());
            refresh.PruneExpired();
            var (token, expiresAt) = tokens.Issue(user.Username, user.Scope);
            var refreshToken = refresh.Issue(user.Username);
            users.UpdateLastLogin(user.Username, clock.GetUtcNow());
            AuthLog.PasskeyAssertionSucceeded(audit, user.Username, ip, user.Scope);
            return Results.Ok(new PdnAuthApi.LoginResponse(token, expiresAt, user.Scope, refreshToken));
        });

        // ===== GATED: registration + credential management =====================

        var group = v1.MapGroup("/auth/webauthn");

        // Begin enrolling a passkey FOR THE SIGNED-IN USER. The username comes from the
        // authenticated principal — never the body — so a user can only enrol for self.
        group.MapPost("/register/begin", (
            HttpContext http,
            IUserStore users,
            IWebAuthnCredentialStore? credentials,
            WebAuthnChallengeCache? challenges,
            IConfigProvider config) =>
        {
            if (credentials is null || challenges is null)
            {
                return WebAuthnUnavailable();
            }

            var username = PrincipalUsername(http);
            if (username is null)
            {
                // Auth is off (no principal): there is no "self" to enrol for. Enrolment
                // is a logged-in action; without auth there is nobody to bind the passkey
                // to, so it is unavailable rather than wrongly attributed.
                return Results.Problem("Passkey enrolment requires an authenticated session.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            var user = users.FindByUsername(username);
            if (user is null)
            {
                return Results.Problem("Unknown user.", statusCode: StatusCodes.Status404NotFound);
            }

            challenges.PruneExpired();

            // The Fido2 user id is a stable per-user handle. We derive it deterministically
            // from the username (UTF-8 bytes) so a re-enrol resolves to the same user
            // handle without persisting a separate id column.
            var fido2User = new Fido2User
            {
                Id = System.Text.Encoding.UTF8.GetBytes(user.Username),
                Name = user.Username,
                DisplayName = user.Username,
            };

            // Exclude the user's already-enrolled credentials so the authenticator won't
            // double-enrol the same key.
            List<PublicKeyCredentialDescriptor> exclude = [];
            foreach (var c in credentials.GetByUser(user.Username))
            {
                exclude.Add(Descriptor(c));
            }

            var fido2 = WebAuthnFido2Builder.ForRequest(config.Current.Management.Auth.WebAuthn, http.Request);
            var options = fido2.RequestNewCredential(new RequestNewCredentialParams
            {
                User = fido2User,
                ExcludeCredentials = exclude,
                AuthenticatorSelection = new AuthenticatorSelection
                {
                    ResidentKey = ResidentKeyRequirement.Required,        // discoverable ⇒ username-less login
                    UserVerification = UserVerificationRequirement.Preferred,
                },
                AttestationPreference = AttestationConveyancePreference.None,
            });

            // Stash keyed to the USER (a registration can only be completed for them).
            challenges.Put(WebAuthnChallengeCache.RegistrationKey(user.Username), options);
            return Results.Ok(System.Text.Json.JsonDocument.Parse(options.ToJson()).RootElement);
        });

        // Complete the enrolment → verify the attestation + store the credential.
        group.MapPost("/register/complete", async (
            RegisterCompleteRequest body,
            HttpContext http,
            IUserStore users,
            IWebAuthnCredentialStore? credentials,
            WebAuthnChallengeCache? challenges,
            IConfigProvider config,
            ILoggerFactory logs,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var audit = logs.CreateLogger("Packet.Node.Auth");
            var ip = ClientIp(http);

            if (credentials is null || challenges is null)
            {
                return WebAuthnUnavailable();
            }
            var username = PrincipalUsername(http);
            if (username is null)
            {
                return Results.Problem("Passkey enrolment requires an authenticated session.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            var auditUser = Redact(username);
            if (body is null || body.Response is null)
            {
                AuthLog.PasskeyRegistrationFailed(audit, auditUser, ip, "malformed-request");
                return Results.BadRequest(new { error = "An attestation response is required." });
            }

            // CONSUME the pending registration for THIS user (single-use). Bound to the
            // principal's username, so a stash for one user can't be completed by another.
            var options = challenges.Take<CredentialCreateOptions>(WebAuthnChallengeCache.RegistrationKey(username));
            if (options is null)
            {
                AuthLog.PasskeyRegistrationFailed(audit, auditUser, ip, "no-pending-challenge");
                return Results.BadRequest(new { error = "No pending passkey enrolment (it may have expired)." });
            }

            AuthenticatorAttestationRawResponse raw;
            try
            {
                raw = ToAttestationResponse(body.Response.Value);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
            {
                AuthLog.PasskeyRegistrationFailed(audit, auditUser, ip, "unparseable-response");
                return Results.BadRequest(new { error = "Malformed attestation response." });
            }

            var fido2 = WebAuthnFido2Builder.ForRequest(config.Current.Management.Auth.WebAuthn, http.Request);
            RegisteredPublicKeyCredential made;
            try
            {
                made = await fido2.MakeNewCredentialAsync(new MakeNewCredentialParams
                {
                    AttestationResponse = raw,
                    OriginalOptions = options,
                    // Global uniqueness: the credential id must not already be enrolled
                    // (by anyone) — a fresh key always is, but enforce it explicitly.
                    IsCredentialIdUniqueToUserCallback = (p, _) =>
                        Task.FromResult(credentials.GetByCredentialId(p.CredentialId) is null),
                }, ct).ConfigureAwait(false);
            }
            catch (Fido2VerificationException)
            {
                AuthLog.PasskeyRegistrationFailed(audit, auditUser, ip, "verification-failed");
                return Results.BadRequest(new { error = "Passkey verification failed." });
            }

            var record = new WebAuthnCredentialRecord(
                made.Id,
                username,
                made.PublicKey,
                made.SignCount,
                made.Type.ToString(),
                EncodeTransports(made.Transports),
                made.AaGuid.ToByteArray(),
                clock.GetUtcNow(),
                LastUsedUtc: null);
            if (!credentials.Add(record))
            {
                AuthLog.PasskeyRegistrationFailed(audit, auditUser, ip, "store-rejected");
                return Results.Conflict(new { error = "Could not store the passkey (it may already be enrolled)." });
            }

            AuthLog.PasskeyRegistered(audit, username, ip);
            return Results.Ok(new RegisterCompleteResponse(true, Base64Url.Encode(made.Id)));
        });

        // List the caller's OWN passkeys (never the key material).
        group.MapGet("/credentials", (HttpContext http, IWebAuthnCredentialStore? credentials) =>
        {
            if (credentials is null)
            {
                return WebAuthnUnavailable();
            }
            var username = PrincipalUsername(http);
            if (username is null)
            {
                // Auth off: there is no "caller" to scope to — return an empty list rather
                // than every user's credentials.
                return Results.Ok(Array.Empty<WebAuthnCredentialSummary>());
            }
            var list = credentials.GetByUser(username)
                .Select(c => new WebAuthnCredentialSummary(
                    Base64Url.Encode(c.CredentialId), c.Transports, c.CreatedUtc, c.LastUsedUtc))
                .ToArray();
            return Results.Ok(list);
        });

        // Delete one of the caller's OWN passkeys (the store enforces ownership too).
        group.MapDelete("/credentials/{id}", (
            string id,
            HttpContext http,
            IWebAuthnCredentialStore? credentials,
            ILoggerFactory logs) =>
        {
            if (credentials is null)
            {
                return WebAuthnUnavailable();
            }
            var username = PrincipalUsername(http);
            if (username is null)
            {
                return Results.Problem("Passkey management requires an authenticated session.",
                    statusCode: StatusCodes.Status409Conflict);
            }
            if (!Base64Url.TryDecode(id, out var credentialId))
            {
                return Results.NotFound();
            }
            if (!credentials.Delete(credentialId, username))
            {
                return Results.NotFound();
            }
            var audit = logs.CreateLogger("Packet.Node.Auth");
            var ip = ClientIp(http);   // precompute (a method call inline in a log arg trips CA1873)
            AuthLog.PasskeyDeleted(audit, username, ip);
            return Results.NoContent();
        });

        return group;
    }

    // Verify the assertion against the stored public key + counter, with the expected
    // origin = the actual serving origin (built per request). Factored out so the try/catch
    // around the verify is readable.
    private static Task<VerifyAssertionResult> fido2VerifyAssertion(
        IConfigProvider config, HttpContext http, WebAuthnCredentialRecord stored,
        AuthenticatorAssertionRawResponse raw, AssertionOptions options)
    {
        var fido2 = WebAuthnFido2Builder.ForRequest(config.Current.Management.Auth.WebAuthn, http.Request);
        return fido2.MakeAssertionAsync(new MakeAssertionParams
        {
            AssertionResponse = raw,
            OriginalOptions = options,
            StoredPublicKey = stored.PublicKey,
            StoredSignatureCounter = stored.SignCount,
            // The credential's user-handle must match the one we stored (derived from the
            // username), proving the assertion is for the credential we think it is.
            IsUserHandleOwnerOfCredentialIdCallback = (p, _) =>
            {
                var expected = System.Text.Encoding.UTF8.GetBytes(stored.Username);
                return Task.FromResult(
                    p.UserHandle is { } uh && CryptographicOperations.FixedTimeEquals(uh, expected));
            },
        }, CancellationToken.None);
    }

    // A counter regression is the clone signature when the authenticator uses counters.
    private static bool CounterRegressed(uint storedCount, Fido2VerificationException ex) =>
        storedCount > 0 && ex.Message.Contains("counter", StringComparison.OrdinalIgnoreCase);

    // --- response/request marshalling ---------------------------------------------

    private static PublicKeyCredentialDescriptor Descriptor(WebAuthnCredentialRecord c) =>
        new(PublicKeyCredentialType.PublicKey, c.CredentialId, DecodeTransports(c.Transports));

    private static AuthenticatorAttestationRawResponse ToAttestationResponse(System.Text.Json.JsonElement el) =>
        System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(el.GetRawText())
        ?? throw new System.Text.Json.JsonException("null attestation response");

    private static AuthenticatorAssertionRawResponse ToAssertionResponse(System.Text.Json.JsonElement el) =>
        System.Text.Json.JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(el.GetRawText())
        ?? throw new System.Text.Json.JsonException("null assertion response");

    private static string? EncodeTransports(AuthenticatorTransport[]? transports) =>
        transports is { Length: > 0 } ? string.Join(',', transports.Select(t => t.ToString().ToLowerInvariant())) : null;

    private static AuthenticatorTransport[]? DecodeTransports(string? transports)
    {
        if (string.IsNullOrWhiteSpace(transports))
        {
            return null;
        }
        var list = new List<AuthenticatorTransport>();
        foreach (var part in transports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<AuthenticatorTransport>(part, ignoreCase: true, out var t))
            {
                list.Add(t);
            }
        }
        return list.Count > 0 ? list.ToArray() : null;
    }

    // --- helpers ------------------------------------------------------------------

    // The authenticated principal's username, or null when there is no authenticated
    // user (auth off, or no token). The endpoints take the enrolling/managing username
    // from HERE — never from the request body — so a user can only act on themselves.
    // The JWT carries the username in the `sub` claim (JwtTokenService.Issue); depending
    // on inbound-claim mapping that can surface as Identity.Name, the raw `sub` claim, or
    // the mapped NameIdentifier — read whichever is present.
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

    private static string NewSessionId() =>
        Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    private static string ClientIp(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static string Redact(string? username) =>
        string.IsNullOrEmpty(username) ? "(unknown)" : username;

    // 503 when the passkey machinery couldn't initialise (e.g. pdn.db unwritable). Mirrors
    // the login 503 — the node still boots, passkeys just can't be used this run.
    private static IResult WebAuthnUnavailable() =>
        Results.Problem("Passkeys are not available.", statusCode: StatusCodes.Status503ServiceUnavailable);

    // A generic rejection for every assert failure — no detail on which step failed (no
    // oracle for credential existence / clone / bad signature). Mirrors the login 401.
    private static IResult AssertionRejected() =>
        Results.Json(new { error = "Passkey sign-in failed." }, statusCode: StatusCodes.Status401Unauthorized);

    // --- DTOs (camelCased by STJ web defaults) ------------------------------------

    /// <summary>The <c>/auth/webauthn/assert/begin</c> request — an optional username to
    /// scope the allow-list (omit for a discoverable / username-less login).</summary>
    public sealed record AssertBeginRequest(string? Username);

    /// <summary>The <c>/auth/webauthn/assert/begin</c> response: the per-attempt session
    /// id (echoed back at complete) + the assertion options (passed to
    /// <c>navigator.credentials.get</c>).</summary>
    public sealed record AssertBeginResponse(string SessionId, System.Text.Json.JsonElement Options);

    /// <summary>The <c>/auth/webauthn/assert/complete</c> request: the session id from
    /// begin + the authenticator's assertion response.</summary>
    public sealed record AssertCompleteRequest(string SessionId, System.Text.Json.JsonElement? Response);

    /// <summary>The <c>/auth/webauthn/register/complete</c> request: the authenticator's
    /// attestation response (the username comes from the principal, not here).</summary>
    public sealed record RegisterCompleteRequest(System.Text.Json.JsonElement? Response);

    /// <summary>The <c>/auth/webauthn/register/complete</c> response.</summary>
    public sealed record RegisterCompleteResponse(bool Registered, string CredentialId);
}
