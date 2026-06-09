namespace Packet.Node.Core.Auth;

/// <summary>
/// The persistence seam for enrolled WebAuthn / passkey credentials, kept web-free so
/// it lives in <c>Packet.Node.Core</c> (the host wires the <c>IFido2</c> ceremonies +
/// endpoints around it).
/// </summary>
/// <remarks>
/// <para>
/// Resilient like <see cref="IUserStore"/> / <see cref="IRefreshTokenStore"/> and the
/// NET/ROM routing store: a backing-store fault logs and degrades (a lookup returns
/// null/empty, a write returns false) — it never throws out to crash the node.
/// Implementations open a fresh pooled connection per call.
/// </para>
/// <para>
/// <b>Credential ids are raw bytes</b> (the authenticator's handle). They are the
/// primary key, and they carry on the wire base64url-encoded; the store deals only in
/// the raw bytes — the endpoints do the base64url at the HTTP boundary.
/// </para>
/// </remarks>
public interface IWebAuthnCredentialStore
{
    /// <summary>Persist a freshly-enrolled credential. Returns <c>false</c> on a store
    /// fault or a duplicate credential id (the caller then fails the enrolment safely).</summary>
    bool Add(WebAuthnCredentialRecord credential);

    /// <summary>All credentials enrolled by <paramref name="username"/> (empty if none
    /// / on fault). Used to build <c>excludeCredentials</c> at registration and
    /// <c>allowCredentials</c> for a username-scoped assertion.</summary>
    IReadOnlyList<WebAuthnCredentialRecord> GetByUser(string username);

    /// <summary>Look up a single credential by its raw id, or null if absent / on
    /// fault. The assert path uses this to find the public key + counter to verify
    /// against.</summary>
    WebAuthnCredentialRecord? GetByCredentialId(byte[] credentialId);

    /// <summary>Every enrolled credential id across all users (empty on fault). Used to
    /// build the allow-list for a <em>username-less</em> (discoverable-credential)
    /// assertion, and to enforce global credential-id uniqueness at registration.</summary>
    IReadOnlyList<byte[]> GetAllCredentialIds();

    /// <summary>Advance a credential's signature counter + stamp its last-used time
    /// after a successful assertion. Best-effort: a fault is swallowed (a failed stamp
    /// must never fail an otherwise-good assertion — but see the clone-detection note
    /// in <see cref="WebAuthnCredentialRecord"/>; the counter check happens BEFORE this
    /// write, in the verify path).</summary>
    void UpdateSignCount(byte[] credentialId, uint newCount, DateTimeOffset whenUtc);

    /// <summary>Delete a credential, but only if it belongs to <paramref name="username"/>
    /// (so a caller can only remove their OWN passkeys). Returns <c>true</c> if a row
    /// was removed, <c>false</c> if absent / not theirs / on fault.</summary>
    bool Delete(byte[] credentialId, string username);
}
