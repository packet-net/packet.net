using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

namespace Packet.Node.Core.Auth.Oauth;

/// <summary>
/// The short-lived, server-side store of anti-forgery (CSRF) tokens for the OAuth
/// consent page. The consent POST is unauthenticated and performs a real login plus a
/// scope grant, so without a token of its own a third-party site could drive a
/// victim's browser into posting it (login CSRF / clickjacked consent). The GET that
/// renders the consent page mints a fresh token here and embeds it as a hidden form
/// field; the POST must echo it back, and is rejected otherwise.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three properties, the same discipline as <see cref="WebAuthnChallengeCache"/>:</b>
/// server-generated (128 CSPRNG bits; the client only echoes), single-use (a
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/> on
/// consume, so a replayed submission finds nothing), and expiring (each token carries
/// an absolute expiry off the injected <see cref="TimeProvider"/> - repo rule §2.7,
/// no wall-clock).
/// </para>
/// <para>
/// <b>In-memory, single-process.</b> Like <see cref="LoginThrottle"/>, pending tokens
/// live only in this process's memory: a node restart simply invalidates any rendered
/// consent page (the user reloads), which is the safe failure mode.
/// </para>
/// </remarks>
public sealed class OauthCsrfCache
{
    /// <summary>How long a minted token stays valid. A consent page is filled in
    /// within a minute or two; fifteen minutes is generous head-room while keeping the
    /// window a stolen or leaked token stays usable tightly bounded.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    /// <summary>Default hard ceiling on live tokens. GET /oauth/authorize is
    /// unauthenticated and unthrottled, so without a cap a flood would grow this map
    /// without bound for a whole TTL window (the entries only expire, they are never
    /// evicted); the cap keeps worst-case memory bounded (~a few MB).</summary>
    public const int DefaultMaxEntries = 16384;

    private readonly ConcurrentDictionary<string, DateTimeOffset> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly TimeSpan ttl;
    private readonly int maxEntries;
    // Only sweep once the map is non-trivially large, so the common (small) case pays
    // nothing per Mint - avoids the O(n^2) of pruning the whole map on every mint.
    private readonly int pruneThreshold;

    /// <summary>Construct over the injected clock and (optional) token lifetime.</summary>
    /// <param name="clock">The clock all expiry rides (no wall-clock - testable on
    /// <c>FakeTimeProvider</c>).</param>
    /// <param name="ttl">How long a minted token lives. Null = <see cref="DefaultTtl"/>.
    /// Must be positive.</param>
    /// <param name="maxEntries">Hard ceiling on live tokens (default
    /// <see cref="DefaultMaxEntries"/>); a flood past it evicts the nearest-expiry
    /// tokens. Must be positive. (Overridable for tests.)</param>
    public OauthCsrfCache(TimeProvider clock, TimeSpan? ttl = null, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var span = ttl ?? DefaultTtl;
        if (span <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "CSRF token TTL must be positive.");
        }
        if (maxEntries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries), "maxEntries must be positive.");
        }
        this.clock = clock;
        this.ttl = span;
        this.maxEntries = maxEntries;
        this.pruneThreshold = Math.Max(1, maxEntries / 4);
    }

    /// <summary>The number of currently-pending tokens (test/diagnostic hook).</summary>
    public int Count => pending.Count;

    /// <summary>Mint a fresh token, stash its expiry, and return it for embedding in
    /// the consent form.</summary>
    public string Mint()
    {
        MaybeSweep();
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        pending[token] = clock.GetUtcNow() + ttl;
        return token;
    }

    /// <summary>Keep the map bounded without paying on every mint. Below the prune
    /// threshold: do nothing. Above it: drop expired tokens; if a flood has still left
    /// the map at or over the hard cap (nothing expired yet), evict the nearest-expiry
    /// entries (the oldest mints - most likely already submitted or abandoned) down to
    /// half the cap. Bounds worst-case memory under an unauthenticated GET flood.</summary>
    private void MaybeSweep()
    {
        if (pending.Count <= pruneThreshold)
        {
            return;
        }
        PruneExpired();
        if (pending.Count < maxEntries)
        {
            return;
        }
        int evict = pending.Count - (maxEntries / 2);
        foreach (var kvp in pending.OrderBy(e => e.Value).Take(evict))
        {
            pending.TryRemove(kvp.Key, out _);
        }
    }

    /// <summary>
    /// Atomically consume a presented token. Returns <c>true</c> only if it was known,
    /// not already consumed (single-use), and not expired. Anything else (absent,
    /// replayed, expired, blank) is a rejection.
    /// </summary>
    public bool Consume(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || !pending.TryRemove(token, out var expiresUtc))
        {
            return false;
        }
        return clock.GetUtcNow() < expiresUtc;
    }

    /// <summary>Best-effort sweep of tokens already expired as of now, so an
    /// abandoned-consent-page backlog can't grow without bound. Safe to call
    /// opportunistically (Mint does).</summary>
    public void PruneExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var kvp in pending)
        {
            if (now >= kvp.Value)
            {
                pending.TryRemove(kvp.Key, out _);
            }
        }
    }
}
