namespace Packet.Node.Core.Auth;

/// <summary>
/// One refresh-token row as persisted in <c>pdn.db</c>. The opaque token the client
/// holds is <em>never</em> stored - only its SHA-256 hash
/// (<see cref="TokenHash"/>), exactly the way a password is never stored in clear.
/// </summary>
/// <param name="TokenHash">Base64url SHA-256 of the opaque token (the PRIMARY KEY).</param>
/// <param name="Username">The user this token authenticates as.</param>
/// <param name="Family">The rotation family id - every token minted from one login
/// shares it. Reuse of a revoked token revokes the whole family (theft response).</param>
/// <param name="IssuedUtc">When the token was minted.</param>
/// <param name="ExpiresUtc">Absolute expiry (login instant + RefreshTokenMinutes).</param>
/// <param name="Revoked">Whether this token has been consumed (rotated) or revoked
/// (logout / family revocation). A revoked token is rejected; a <em>presented</em>
/// revoked token is the theft signal.</param>
/// <param name="RevokedUtc">When this token was consumed by an ordinary rotation, or
/// null. Only the one-time-use rotation consume stamps it - a hard revoke (logout /
/// family burn / expiry) leaves it null. It exists for the reuse-leeway window: a
/// just-rotated token replayed within the leeway by the legitimate client racing
/// itself (two tabs, a retried refresh) is benign, not theft. A null here means "not
/// leeway-eligible" - replaying it is always reuse.</param>
public sealed record RefreshTokenRecord(
    string TokenHash,
    string Username,
    string Family,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    bool Revoked,
    DateTimeOffset? RevokedUtc = null);
