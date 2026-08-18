namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// A dynamically-registered OAuth client (RFC 7591) - e.g. the claude.ai connector.
/// A <b>public</b> client: no secret (PKCE is the proof), so the only credentials are
/// the <see cref="ClientId"/> and the pre-registered <see cref="RedirectUris"/> that the
/// authorize flow matches exactly. Persisted in <c>pdn.db</c>.
/// </summary>
/// <param name="ClientId">The issued identifier (an opaque random token).</param>
/// <param name="ClientName">The client's self-declared display name (shown on the consent screen).</param>
/// <param name="RedirectUris">The exact redirect URIs the client may use - no wildcards.</param>
/// <param name="CreatedUtc">When the client registered.</param>
public sealed record OauthClient(
    string ClientId,
    string ClientName,
    IReadOnlyList<string> RedirectUris,
    DateTimeOffset CreatedUtc);

/// <summary>
/// The store of registered OAuth clients. Resilient (mirrors the other <c>pdn.db</c>
/// stores): a store fault logs and degrades - <see cref="Register"/> returns null,
/// reads return null/empty - rather than faulting the request path.
/// </summary>
public interface IOauthClientStore
{
    /// <summary>Register a new public client; returns it with its issued id, or null on a
    /// store fault.</summary>
    OauthClient? Register(string clientName, IReadOnlyList<string> redirectUris, DateTimeOffset now);

    /// <summary>Look up a client by id, or null if unknown / on fault.</summary>
    OauthClient? Find(string clientId);

    /// <summary>All registered clients, newest first (for the panel's connected-apps view).</summary>
    IReadOnlyList<OauthClient> List();

    /// <summary>Remove a client. Returns true if a row was deleted.</summary>
    bool Delete(string clientId);
}
