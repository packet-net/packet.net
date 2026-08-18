using System.Security.Cryptography;
using System.Text;

namespace Packet.Node.Core.Auth;

/// <summary>
/// The refresh-token lifecycle on top of <see cref="IRefreshTokenStore"/>: issue
/// (on login), rotate (one-time-use exchange), reuse detection (the theft
/// response), and logout. Web-free (it depends only on the store + a
/// <see cref="TimeProvider"/>), so it lives in <c>Packet.Node.Core</c> and is unit
/// testable on <c>FakeTimeProvider</c>; the host wires the
/// <c>/auth/refresh</c> + <c>/auth/logout</c> endpoints around it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opaque token, hash at rest.</b> The token handed to the client is a 256-bit
/// CSPRNG value (<see cref="RandomNumberGenerator.GetBytes(int)"/>, 32 bytes,
/// base64url) - unguessable and never derived from anything. Only its SHA-256 hash
/// is stored, so a database read never yields a usable token, exactly like a
/// password is never stored in clear.
/// </para>
/// <para>
/// <b>One-time use + rotation.</b> Each refresh token is good for exactly one
/// rotation: <see cref="Rotate"/> marks the presented token revoked and mints a new
/// one in the <em>same family</em>. A family is the chain of tokens descending from
/// one login.
/// </para>
/// <para>
/// <b>Reuse detection (theft response) + leeway.</b> A token that is found but
/// <em>already revoked</em> means someone replayed a consumed token - either the
/// legitimate client racing itself (two browser tabs, a retried silent refresh, a
/// backgrounded tab waking - all present the same just-rotated token within a
/// moment), or an attacker who stole a token the client has since rotated past.
/// Burning the family unconditionally turns the routine self-race into a logout on
/// every access-token expiry. So a replay <em>within the reuse-leeway window</em> of
/// the token's rotation, <em>while its family is still alive</em>, is taken as the
/// benign self-race: a fresh successor is minted, no burn. Outside that window - or
/// against a logged-out / already-burned family (<see cref="IRefreshTokenStore.HasLiveToken"/>
/// is false) - a replay is the theft response: <see cref="RevokeFamily"/> the entire
/// family and reject. This keeps the stolen-token window bounded (leeway + a single
/// rotation) without logging the real user out for racing themselves.
/// </para>
/// <para>
/// <b>No wall-clock (repo rule §2.7):</b> issue/expiry stamps and the
/// expiry comparison ride the injected <see cref="TimeProvider"/>.
/// </para>
/// </remarks>
public sealed class RefreshTokenService
{
    private const int TokenBytes = 32;   // 256-bit opaque token

    /// <summary>Default reuse-leeway window (see the <paramref name="reuseLeeway"/>
    /// constructor parameter). Long enough to absorb a browser's concurrent
    /// tab/refresh burst, short enough that a genuinely stolen token can't be replayed
    /// much later under its cover.</summary>
    public static readonly TimeSpan DefaultReuseLeeway = TimeSpan.FromSeconds(10);

    private readonly IRefreshTokenStore store;
    private readonly TimeProvider clock;
    private readonly TimeSpan lifetime;
    private readonly TimeSpan reuseLeeway;

    /// <summary>
    /// Construct over the backing store, the refresh-token lifetime, and the clock.
    /// </summary>
    /// <param name="store">The hash-only persistence seam.</param>
    /// <param name="lifetime">How long a fresh refresh token lives (login instant +
    /// this). Must be positive.</param>
    /// <param name="clock">The injected clock (issue/expiry + the expiry check ride
    /// this - no wall-clock).</param>
    /// <param name="reuseLeeway">The window after a token is rotation-consumed within
    /// which replaying it is treated as the legitimate client racing itself (two tabs,
    /// a retried refresh) rather than theft: the family is NOT burned and a fresh
    /// successor is minted. Outside the window a replay is still the theft response.
    /// Null uses <see cref="DefaultReuseLeeway"/>; <see cref="TimeSpan.Zero"/> restores
    /// the strict zero-tolerance behaviour. Must not be negative.</param>
    public RefreshTokenService(IRefreshTokenStore store, TimeSpan lifetime, TimeProvider clock, TimeSpan? reuseLeeway = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Refresh-token lifetime must be positive.");
        }
        var leeway = reuseLeeway ?? DefaultReuseLeeway;
        if (leeway < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reuseLeeway), "Reuse leeway must not be negative.");
        }
        this.store = store;
        this.lifetime = lifetime;
        this.clock = clock;
        this.reuseLeeway = leeway;
    }

    /// <summary>
    /// Issue a brand-new refresh token for <paramref name="username"/> in a fresh
    /// random family (the start of a rotation chain - call this on every login).
    /// Returns the opaque token the client must keep, or null if the store could not
    /// persist it (the caller then declines to hand out a refresh token, but the
    /// login itself can still succeed with just the access token).
    /// </summary>
    public string? Issue(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        var family = NewOpaque();   // a random family id (also a 256-bit value)
        return MintInFamily(username, family);
    }

    /// <summary>
    /// Rotate a presented refresh token. Looks it up by hash and applies the
    /// one-time-use + reuse-detection rules:
    /// <list type="bullet">
    /// <item><b>Not found</b> → <see cref="RefreshOutcome.Invalid"/> (unknown/forged token).</item>
    /// <item><b>Already revoked</b> → revoke the whole family and return
    /// <see cref="RefreshOutcome.ReuseDetected"/> (theft response).</item>
    /// <item><b>Expired</b> → <see cref="RefreshOutcome.Expired"/>.</item>
    /// <item><b>Valid</b> → revoke it, mint a successor in the same family, return
    /// <see cref="RefreshOutcome.Rotated"/> with the new opaque token + the username.</item>
    /// </list>
    /// All four outcomes are 401 to the client; the distinction drives the audit log.
    /// </summary>
    public RefreshResult Rotate(string presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return RefreshResult.Failure(RefreshOutcome.Invalid);
        }

        var hash = HashToken(presentedToken);
        var record = store.FindByHash(hash);
        if (record is null)
        {
            // Unknown token: forged, already-pruned, or never ours. No family to act on.
            return RefreshResult.Failure(RefreshOutcome.Invalid);
        }

        var now = clock.GetUtcNow();

        if (record.Revoked)
        {
            return HandleReplay(record, now);
        }

        if (now >= record.ExpiresUtc)
        {
            // Expired but still valid-looking: hard-revoke it (null = not leeway-eligible)
            // so it can't later be replayed as a "reuse" false-positive, and reject.
            // (Pruning eventually removes it.)
            store.Revoke(hash, consumedAtUtc: null);
            return RefreshResult.Failure(RefreshOutcome.Expired, record.Username, record.Family);
        }

        // Valid: consume it atomically (one-time use, stamping the leeway window). The
        // store's consume is conditional (only a live token is revoked), so of N
        // concurrent rotations of this SAME token exactly one wins; the losers see
        // false here instead of all minting their own successor.
        if (!store.Revoke(hash, consumedAtUtc: now))
        {
            // Consumed 0 rows: a concurrent rotation of this SAME live token won the
            // consume race and is minting the family's successor right now. Resolve the
            // loser WITHOUT re-deriving family liveness (deliberately NOT HandleReplay,
            // which reads HasLiveToken): that read races the winner's not-yet-committed
            // insert and can transiently observe zero live tokens, which would wrongly
            // steer a benign self-race into a family burn (spurious logout) or a burn
            // that races the winner's fresh successor. We already established at the top
            // of Rotate that the token was live, so within the leeway this is the same
            // benign near-simultaneous use a sequential double-submit is: mint a grace
            // successor. Outside the leeway (strict one-time-use) a concurrent double-use
            // is treated as reuse and burns the family.
            var reread = store.FindByHash(hash);
            if (reread is null)
            {
                return RefreshResult.Failure(RefreshOutcome.Invalid);   // pruned mid-flight
            }
            bool withinLeeway = reuseLeeway > TimeSpan.Zero
                && reread.RevokedUtc is { } revokedAt
                && now - revokedAt <= reuseLeeway;
            if (withinLeeway)
            {
                var graceNext = MintInFamily(reread.Username, reread.Family);
                return graceNext is null
                    ? RefreshResult.Failure(RefreshOutcome.Invalid, reread.Username, reread.Family)
                    : RefreshResult.Success(graceNext, reread.Username, reread.Family);
            }
            store.RevokeFamily(reread.Family);
            return RefreshResult.Failure(RefreshOutcome.ReuseDetected, reread.Username, reread.Family);
        }

        var next = MintInFamily(record.Username, record.Family);
        if (next is null)
        {
            // The successor couldn't be persisted; we've already revoked the presented
            // one, so the session can't silently continue on a stale token. Surface a
            // store fault as Invalid (401) rather than hand back an untracked token.
            return RefreshResult.Failure(RefreshOutcome.Invalid, record.Username, record.Family);
        }
        return RefreshResult.Success(next, record.Username, record.Family);
    }

    // Replay semantics for a token that is already revoked (or was just found to be,
    // after losing the consume race). A consumed/revoked token being presented means
    // someone replayed it - but the legitimate client races itself routinely (two
    // tabs, a retried silent refresh, a backgrounded tab waking) and presents the SAME
    // just-rotated token twice within a moment. Burning the family for that logs the
    // real user out every time the access token expires. So: if this token was
    // rotation-consumed within the leeway window AND its family is still alive (not
    // logged out / not already burned), treat the replay as benign and mint a fresh
    // successor instead of burning. Outside the window, or against a dead family, it
    // is the theft response: burn the family.
    private RefreshResult HandleReplay(RefreshTokenRecord record, DateTimeOffset now)
    {
        bool withinLeeway = reuseLeeway > TimeSpan.Zero
            && record.RevokedUtc is { } revokedAt
            && now - revokedAt <= reuseLeeway;
        if (withinLeeway && store.HasLiveToken(record.Family))
        {
            var graceNext = MintInFamily(record.Username, record.Family);
            return graceNext is null
                ? RefreshResult.Failure(RefreshOutcome.Invalid, record.Username, record.Family)
                : RefreshResult.Success(graceNext, record.Username, record.Family);
        }

        // Old replay, or a dead/logged-out family → genuine reuse: burn the family.
        store.RevokeFamily(record.Family);
        return RefreshResult.Failure(RefreshOutcome.ReuseDetected, record.Username, record.Family);
    }

    /// <summary>
    /// Revoke the family of a presented token (logout). Best-effort: an unknown
    /// token is a no-op success (logout is idempotent - there is nothing to leak by
    /// confirming it). Returns the username + family acted on when known, for the
    /// audit log.
    /// </summary>
    public (string? Username, string? Family) Logout(string presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken))
        {
            return (null, null);
        }
        var record = store.FindByHash(HashToken(presentedToken));
        if (record is null)
        {
            return (null, null);
        }
        store.RevokeFamily(record.Family);
        return (record.Username, record.Family);
    }

    /// <summary>Revoke a family directly (used when a refresh finds the family's user
    /// has been deleted - we burn the family rather than mint for a non-existent
    /// user). Best-effort.</summary>
    public void LogoutFamily(string family)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        store.RevokeFamily(family);
    }

    /// <summary>Revoke every family belonging to <paramref name="username"/> - the
    /// account-lifecycle kill the user-delete handler runs, so a deleted account leaves no
    /// live refresh chain behind for a recreated same-name account to inherit (C055).
    /// Returns the number of tokens revoked. Best-effort (0 on a store fault).</summary>
    public int RevokeAllForUser(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return store.RevokeAllForUser(username);
    }

    /// <summary>Best-effort prune of tokens already expired as of now. Safe to call
    /// opportunistically (e.g. on each login); a fault is swallowed by the store.</summary>
    public void PruneExpired() => store.PruneExpired(clock.GetUtcNow());

    // Mint + persist a token in the given family; null if the store rejected the write.
    private string? MintInFamily(string username, string family)
    {
        var token = NewOpaque();
        var now = clock.GetUtcNow();
        var record = new RefreshTokenRecord(
            HashToken(token), username, family, now, now + lifetime, Revoked: false);
        return store.Insert(record) ? token : null;
    }

    // A fresh 256-bit CSPRNG value, base64url-encoded (URL-safe, unpadded) so it can
    // travel in a JSON body / header without escaping.
    private static string NewOpaque() =>
        Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// SHA-256 of the opaque token, base64url-encoded - the value stored + looked up.
    /// A plain hash (no salt) is correct here: the input is already 256 bits of
    /// CSPRNG entropy, so there is nothing to brute-force and a per-token salt would
    /// only break the by-hash lookup. (Contrast passwords, which are low-entropy and
    /// need Argon2 + per-user salt.)
    /// </summary>
    public static string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Base64Url(digest);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The outcome of a <see cref="RefreshTokenService.Rotate"/> call.</summary>
public enum RefreshOutcome
{
    /// <summary>The presented token was valid; a successor was minted (see
    /// <see cref="RefreshResult.NewToken"/>).</summary>
    Rotated,

    /// <summary>The token is unknown / forged / never ours (or a store fault on the
    /// successor write). 401.</summary>
    Invalid,

    /// <summary>The token had expired. 401.</summary>
    Expired,

    /// <summary>The token was found already-revoked - a replay of a consumed token.
    /// The whole family was revoked as the theft response. 401.</summary>
    ReuseDetected,
}

/// <summary>
/// The result of a rotation. On <see cref="RefreshOutcome.Rotated"/>,
/// <see cref="NewToken"/> + <see cref="Username"/> are set; otherwise it carries the
/// failure outcome (and, when the token was found, the username/family for the audit
/// log).
/// </summary>
public sealed record RefreshResult(
    RefreshOutcome Outcome,
    string? NewToken,
    string? Username,
    string? Family)
{
    /// <summary>Whether the rotation produced a new usable token.</summary>
    public bool IsSuccess => Outcome == RefreshOutcome.Rotated;

    internal static RefreshResult Success(string newToken, string username, string family) =>
        new(RefreshOutcome.Rotated, newToken, username, family);

    internal static RefreshResult Failure(RefreshOutcome outcome, string? username = null, string? family = null) =>
        new(outcome, null, username, family);
}
