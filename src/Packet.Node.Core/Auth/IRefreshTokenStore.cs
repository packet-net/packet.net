namespace Packet.Node.Core.Auth;

/// <summary>
/// The persistence seam for web control-API refresh tokens, kept web-free so it
/// lives in <c>Packet.Node.Core</c> (the host wires the rotation endpoints around
/// the <see cref="RefreshTokenService"/> that drives it).
/// </summary>
/// <remarks>
/// <para>
/// Resilient like <see cref="IUserStore"/> and the NET/ROM routing store: a
/// backing-store fault logs and degrades (a lookup returns null, a write returns
/// false) — it never throws out to crash the node. Implementations open a fresh
/// pooled connection per call.
/// </para>
/// <para>
/// <b>Only hashes are stored.</b> Callers pass the SHA-256 hash of the opaque
/// token (see <see cref="RefreshTokenRecord.TokenHash"/>); the plaintext token
/// never reaches the store. Lookup is by hash — exactly like a password is never
/// stored in clear and is verified, never read back.
/// </para>
/// </remarks>
public interface IRefreshTokenStore
{
    /// <summary>Insert a freshly-minted refresh token. Returns <c>false</c> on a
    /// store fault (the caller then fails the issue safely — no token returned).</summary>
    bool Insert(RefreshTokenRecord token);

    /// <summary>Look up a token by its SHA-256 hash, or null if absent / on fault.</summary>
    RefreshTokenRecord? FindByHash(string tokenHash);

    /// <summary>Mark a single token revoked. <paramref name="consumedAtUtc"/> stamps
    /// the revoke time when this is an ordinary one-time-use rotation consume (which
    /// makes the token leeway-eligible — a brief replay by the racing legitimate
    /// client is benign, not theft); pass <c>null</c> for a hard revoke (expiry) that
    /// must never be leeway-eligible. Returns <c>true</c> if a row changed,
    /// <c>false</c> if absent or on fault.</summary>
    bool Revoke(string tokenHash, DateTimeOffset? consumedAtUtc);

    /// <summary>Revoke every token in a family — the theft response (a replayed,
    /// already-revoked token) and the logout path. A family burn is a hard kill: it
    /// does not stamp the leeway window, so a burned/logged-out family can't be
    /// resurrected through the reuse leeway. Returns the number of rows revoked (0 on
    /// fault / empty family).</summary>
    int RevokeFamily(string family);

    /// <summary>Revoke every token belonging to <paramref name="username"/>, across every
    /// family - the account-lifecycle kill (delete, and any future password reset). Like
    /// <see cref="RevokeFamily"/> this is a hard kill with no leeway stamp. Without it a
    /// deleted account's refresh families survived in the table and a recreated same-name
    /// account revived them (review item C055). Returns the number of rows revoked (0 on
    /// fault / nothing live).</summary>
    int RevokeAllForUser(string username);

    /// <summary>Whether <paramref name="family"/> still has at least one non-revoked
    /// token — i.e. the session is alive. Guards the reuse-leeway grace path so it can
    /// only extend a live session, never resurrect a logged-out / theft-burned family.
    /// Returns <c>false</c> on fault (fail safe — deny grace).</summary>
    bool HasLiveToken(string family);

    /// <summary>Best-effort prune of tokens that expired before
    /// <paramref name="olderThanUtc"/>, so the table doesn't grow without bound. A
    /// fault is swallowed (pruning never fails an operation). Returns rows removed.</summary>
    int PruneExpired(DateTimeOffset olderThanUtc);
}
