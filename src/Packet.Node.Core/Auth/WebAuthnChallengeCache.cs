using System.Collections.Concurrent;

namespace Packet.Node.Core.Auth;

/// <summary>
/// The short-lived, server-side store of pending WebAuthn ceremonies — the
/// security-critical piece of the passkey flow. When the node hands a browser a
/// registration (<c>CredentialCreateOptions</c>) or assertion (<c>AssertionOptions</c>)
/// it has minted a fresh random challenge inside those options; that exact options
/// object is stashed here and re-read on the matching <c>complete</c> call. The
/// verifier must compare the authenticator's signed challenge against the ONE the
/// server issued — never against a challenge the client echoes back — or the whole
/// ceremony is replayable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three properties this type enforces, none of which may be skipped:</b>
/// </para>
/// <list type="number">
/// <item><b>Server-generated.</b> The stored value is the options object the node built
/// (challenge included). The client never supplies a challenge; it only returns the
/// authenticator's <em>signature over</em> the server's challenge. This type is just the
/// place the server keeps its own copy until the round trip completes.</item>
/// <item><b>Single-use.</b> <see cref="Take"/> removes-and-returns atomically (a
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey, out TValue)"/>), so a
/// replayed <c>complete</c> for the same key finds nothing. A challenge is consumed the
/// first time it is presented, win or lose.</item>
/// <item><b>Expiring.</b> Each entry carries an absolute expiry off the injected
/// <see cref="TimeProvider"/> (repo rule §2.7 — no wall-clock). <see cref="Take"/>
/// treats an expired entry as absent (and removes it), so a stale ceremony cannot be
/// completed even if the key is still known.</item>
/// </list>
/// <para>
/// <b>Key binding.</b> A registration is bound to the enrolling <em>user</em> (the key
/// is <c>reg:&lt;username&gt;</c>), so a pending enrolment for one user can never be
/// completed as another. An assertion is bound to a per-attempt <em>session</em> id (a
/// random opaque handle the begin call returns and the complete call echoes), because a
/// passwordless / discoverable-credential assertion has no username yet — the identity
/// only emerges from the signed credential at <c>complete</c>. The session id is just a
/// correlation handle, not an authenticator; the challenge inside the stashed options is
/// the actual anti-replay secret.
/// </para>
/// <para>
/// <b>In-memory, single-process.</b> Like <see cref="LoginThrottle"/>, pending
/// ceremonies live only in this process's memory: a node restart simply invalidates any
/// half-finished ceremony (the browser retries), which is the safe failure mode. There
/// is nothing here worth persisting.
/// </para>
/// </remarks>
public sealed class WebAuthnChallengeCache
{
    /// <summary>How long a pending ceremony stays valid. A passkey ceremony is a few
    /// seconds of user interaction; five minutes is generous head-room while still
    /// bounding the replay window tightly.</summary>
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider clock;
    private readonly TimeSpan ttl;

    /// <summary>Construct over the injected clock and (optional) entry lifetime.</summary>
    /// <param name="clock">The clock all expiry rides (no wall-clock — testable on
    /// <c>FakeTimeProvider</c>).</param>
    /// <param name="ttl">How long a pending ceremony lives. Null = <see cref="DefaultTtl"/>.
    /// Must be positive.</param>
    public WebAuthnChallengeCache(TimeProvider clock, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var span = ttl ?? DefaultTtl;
        if (span <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Challenge TTL must be positive.");
        }
        this.clock = clock;
        this.ttl = span;
    }

    /// <summary>The dictionary key for a pending <em>registration</em> — bound to the
    /// enrolling user, so a registration can only be completed by/for that user.</summary>
    public static string RegistrationKey(string username)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        return "reg:" + username;
    }

    /// <summary>The dictionary key for a pending <em>assertion</em> — bound to a
    /// per-attempt session handle (the begin call mints it; the complete call echoes
    /// it). A username-less / discoverable assertion has no user to key on yet.</summary>
    public static string AssertionKey(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return "assert:" + sessionId;
    }

    /// <summary>
    /// Stash a pending ceremony's options under <paramref name="key"/>, replacing any
    /// prior pending ceremony for the same key (a fresh begin supersedes an abandoned
    /// one). The value is the server-built options object whose challenge the matching
    /// complete will verify against.
    /// </summary>
    public void Put(string key, object options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(options);
        pending[key] = new Entry(options, clock.GetUtcNow() + ttl);
    }

    /// <summary>
    /// Atomically remove-and-return the pending options for <paramref name="key"/>,
    /// typed as <typeparamref name="T"/>. Returns null if the key is unknown, already
    /// consumed (single-use), expired (and removes it), or stashed as a different type.
    /// This is the only read path — there is deliberately no non-consuming peek.
    /// </summary>
    public T? Take<T>(string key) where T : class
    {
        if (string.IsNullOrWhiteSpace(key) || !pending.TryRemove(key, out var entry))
        {
            return null;
        }
        if (clock.GetUtcNow() >= entry.ExpiresUtc)
        {
            // Expired: it was removed by the TryRemove above; treat as absent.
            return null;
        }
        return entry.Options as T;
    }

    /// <summary>Best-effort sweep of entries already expired as of now, so an
    /// abandoned-ceremony backlog can't grow without bound on a busy node. Safe to call
    /// opportunistically (e.g. on each begin).</summary>
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

    private readonly record struct Entry(object Options, DateTimeOffset ExpiresUtc);
}
