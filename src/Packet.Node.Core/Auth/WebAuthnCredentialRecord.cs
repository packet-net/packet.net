namespace Packet.Node.Core.Auth;

/// <summary>
/// One enrolled WebAuthn / passkey credential as persisted in <c>pdn.db</c>. This is
/// the public-key side of a passkey: the authenticator keeps the private key (in
/// secure hardware / the platform keystore), and the node stores only what it needs
/// to <em>verify</em> a future assertion — the credential id, the COSE public key,
/// the running signature counter, and a little metadata.
/// </summary>
/// <remarks>
/// <para>
/// <b>No secret here.</b> Unlike a password hash or a refresh-token hash, a stored
/// public key is not sensitive on its own — it can only verify, never produce, a
/// signature. It is still scoped to a single <see cref="Username"/> so an assertion
/// can only authenticate the user who enrolled it.
/// </para>
/// <para>
/// <b><see cref="SignCount"/> is the clone-detection counter.</b> A conformant
/// authenticator increments a per-credential counter on every assertion; a counter
/// that fails to advance (a new value ≤ the stored one, when the authenticator uses
/// counters at all) is the signature of a cloned credential, and the assert path
/// rejects it. Some authenticators (notably platform/passkey ones) always report
/// <c>0</c> — in that case the check is skipped (0 → 0 is not a regression).
/// </para>
/// </remarks>
/// <param name="CredentialId">The raw credential id bytes (the authenticator's
/// handle for this key) — the primary key, and the value <c>excludeCredentials</c> /
/// <c>allowCredentials</c> carry.</param>
/// <param name="Username">The user who enrolled this passkey; an assertion against it
/// authenticates exactly this user.</param>
/// <param name="PublicKey">The COSE-encoded public key used to verify assertions.</param>
/// <param name="SignCount">The last-seen signature counter (clone detection — see remarks).</param>
/// <param name="CredType">The credential type string (always <c>public-key</c> today;
/// stored verbatim for forward-compatibility).</param>
/// <param name="Transports">A comma-joined list of the authenticator transports the
/// browser reported (e.g. <c>internal</c>, <c>usb</c>, <c>hybrid</c>), or null. Fed
/// back into <c>allowCredentials</c> so the browser surfaces the right UI; never
/// trusted for security.</param>
/// <param name="AaGuid">The authenticator's AAGUID (model identifier), or null.</param>
/// <param name="CreatedUtc">When the passkey was enrolled.</param>
/// <param name="LastUsedUtc">When the passkey was last used for a successful assertion,
/// or null if never.</param>
public sealed record WebAuthnCredentialRecord(
    byte[] CredentialId,
    string Username,
    byte[] PublicKey,
    uint SignCount,
    string? CredType,
    string? Transports,
    byte[]? AaGuid,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc);

/// <summary>
/// A passkey credential projected for the API — the public key is deliberately
/// absent (the client never needs it, and it keeps the surface minimal). This is the
/// only shape <c>GET /auth/webauthn/credentials</c> returns.
/// </summary>
/// <param name="Id">The credential id, base64url-encoded (the stable handle the client
/// passes to <c>DELETE /auth/webauthn/credentials/{id}</c>).</param>
/// <param name="Transports">The comma-joined transports, or null.</param>
/// <param name="CreatedUtc">When the passkey was enrolled.</param>
/// <param name="LastUsedUtc">When it was last used, or null.</param>
public sealed record WebAuthnCredentialSummary(
    string Id,
    string? Transports,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastUsedUtc);
