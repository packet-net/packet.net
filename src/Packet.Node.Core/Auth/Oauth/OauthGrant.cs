namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// What an OAuth authorization was for, keyed by the refresh-token family the token endpoint
/// minted for it (#428): the client that holds it and the scope the owner consented to, so a
/// refresh re-issues exactly that grant and nothing wider. The family's tokens themselves live
/// in the refresh-token store; when the family is burned or logged out, the grant goes too.
/// </summary>
/// <param name="Family">The refresh-token family (<see cref="RefreshTokenService"/>).</param>
/// <param name="ClientId">The registered client the grant was issued to; a refresh must name it.</param>
/// <param name="Username">The owner who consented.</param>
/// <param name="Scope">The <see cref="AuthScopes"/> value consented to (<c>read</c> or <c>operate</c>).</param>
/// <param name="CreatedUtc">When the authorization code was exchanged.</param>
public sealed record OauthGrant(string Family, string ClientId, string Username, string Scope, DateTimeOffset CreatedUtc);

/// <summary>Persistence for <see cref="OauthGrant"/>; resilient on fault like the sibling stores.</summary>
public interface IOauthGrantStore
{
    /// <summary>Record a grant. False if it could not be persisted (the caller then issues no refresh token).</summary>
    bool Add(OauthGrant grant);

    /// <summary>The grant a refresh-token family was issued for, or null.</summary>
    OauthGrant? FindByFamily(string family);

    /// <summary>Forget a family's grant (it was burned, logged out or revoked). Idempotent.</summary>
    bool Delete(string family);

    /// <summary>Every live grant an owner has consented to, newest first (for a connected-apps view).</summary>
    IReadOnlyList<OauthGrant> ListByUser(string username);
}
