using System.Collections.Concurrent;

namespace Packet.Node.Core.Auth;

/// <summary>
/// The short-lived, server-side store of pending TOTP enrolments — the secret-handling
/// piece of the over-RF sysop-code flow. <c>enroll/begin</c> mints a fresh random secret
/// (<see cref="TotpService.GenerateSecret"/>) and shows it to the user ONCE (as a QR /
/// manual key) without persisting it; that secret is stashed here keyed to the enrolling
/// user, and <c>enroll/complete</c> consumes it to verify the confirming code before the
/// store ever sees it. The secret is therefore never written to <c>pdn.db</c> until the
/// user has proved they hold a working authenticator.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="WebAuthnChallengeCache"/> exactly — the same three properties, none
/// of which may be skipped:
/// </para>
/// <list type="number">
/// <item><b>Server-generated.</b> The stashed value is the secret the node minted; the
/// client never supplies it. The complete call types only a code derived from that secret
/// by the authenticator app.</item>
/// <item><b>Single-use.</b> <see cref="Take"/> removes-and-returns atomically (a
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>), so a
/// pending enrolment is consumed the first time it is completed (win or lose) — a replayed
/// or double-submitted complete finds nothing.</item>
/// <item><b>Expiring.</b> Each entry carries an absolute expiry off the injected
/// <see cref="TimeProvider"/> (repo rule §2.7 — no wall-clock). <see cref="Take"/> treats
/// an expired entry as absent (and removes it), so an abandoned begin can't be completed
/// later.</item>
/// </list>
/// <para>
/// <b>Key binding.</b> A pending enrolment is bound to the enrolling <em>user</em> (the
/// key is the username), so one user's pending secret can never be completed as another.
/// The username comes from the authenticated principal at both begin and complete — never
/// the request body.
/// </para>
/// <para>
/// <b>In-memory, single-process.</b> Like <see cref="WebAuthnChallengeCache"/> and
/// <see cref="LoginThrottle"/>, pending enrolments live only in this process's memory: a
/// node restart simply invalidates any half-finished enrolment (the user re-begins), which
/// is the safe failure mode. There is nothing here worth persisting — and a never-confirmed
/// secret is one we explicitly DON'T want to keep.
/// </para>
/// </remarks>
public sealed class TotpEnrollmentCache
{
    /// <summary>How long a pending enrolment stays valid. Generous head-room for a user to
    /// scan a QR and read back a code, while still bounding how long an unconfirmed secret
    /// lingers in memory.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly TimeSpan ttl;

    /// <summary>Construct over the injected clock and (optional) entry lifetime.</summary>
    /// <param name="clock">The clock all expiry rides (no wall-clock — testable on
    /// <c>FakeTimeProvider</c>).</param>
    /// <param name="ttl">How long a pending enrolment lives. Null = <see cref="DefaultTtl"/>.
    /// Must be positive.</param>
    public TotpEnrollmentCache(TimeProvider clock, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var span = ttl ?? DefaultTtl;
        if (span <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Enrolment TTL must be positive.");
        }
        this.clock = clock;
        this.ttl = span;
    }

    /// <summary>
    /// Stash a pending enrolment's <paramref name="secret"/> for <paramref name="username"/>,
    /// replacing any prior pending enrolment for the same user (a fresh begin supersedes an
    /// abandoned one). The value is the server-minted base32 secret the matching complete
    /// will verify the typed code against.
    /// </summary>
    public void Put(string username, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        pending[username] = new Entry(secret, clock.GetUtcNow() + ttl);
    }

    /// <summary>
    /// Atomically remove-and-return the pending secret for <paramref name="username"/>.
    /// Returns null if the user has no pending enrolment, it was already consumed
    /// (single-use), or it expired (and removes it). This is the only read path — there is
    /// deliberately no non-consuming peek, so a completed-or-failed attempt always burns the
    /// pending secret.
    /// </summary>
    public string? Take(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || !pending.TryRemove(username, out var entry))
        {
            return null;
        }
        if (clock.GetUtcNow() >= entry.ExpiresUtc)
        {
            // Expired: it was removed by the TryRemove above; treat as absent.
            return null;
        }
        return entry.Secret;
    }

    /// <summary>Best-effort sweep of entries already expired as of now, so an
    /// abandoned-enrolment backlog can't grow without bound. Safe to call opportunistically
    /// (e.g. on each begin).</summary>
    public void PruneExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var kvp in pending)
        {
            if (now >= kvp.Value.ExpiresUtc)
            {
                pending.TryRemove(kvp.Key, out _);
            }
        }
    }

    private readonly record struct Entry(string Secret, DateTimeOffset ExpiresUtc);
}
