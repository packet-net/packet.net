namespace Packet.Node.Core.Auth;

/// <summary>
/// The denylist that makes an individual JWT revocable (#428). Every token the node issues
/// carries a <c>jti</c>; a revoked one's id is kept here until the token would have expired
/// anyway, and the bearer authentication path refuses a token whose id is listed. The
/// alternative, rotating the signing key, invalidates every token at once.
/// </summary>
public interface IRevokedTokenStore
{
    /// <summary>True if <paramref name="jti"/> has been revoked and has not yet been pruned.</summary>
    bool IsRevoked(string jti);

    /// <summary>List <paramref name="jti"/> until <paramref name="expiresUtc"/>, after which the
    /// token is dead on its own and the row is pruned. Idempotent. Returns false when the id
    /// could not be persisted; it is still refused for the life of this process.</summary>
    bool Revoke(string jti, DateTimeOffset expiresUtc, DateTimeOffset now);

    /// <summary>Drop ids whose token has expired. Returns the number removed.</summary>
    int PruneExpired(DateTimeOffset now);
}
